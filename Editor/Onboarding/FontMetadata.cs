using System.Collections.Generic;

namespace OneText.Editor
{
    /// <summary>
    /// The part of a Google Fonts <c>METADATA.pb</c> that decides what may be
    /// fetched: what the family is called, which licence it is published under,
    /// and the real names of the files in its directory.
    ///
    /// This is here because the file name cannot be derived from the family
    /// name. Google Fonts writes the axis list into it — <c>Cairo[slnt,wght].ttf</c>,
    /// <c>Sen[wght].ttf</c>, <c>Alata-Regular.ttf</c> — so a catalogue that only
    /// knows "Cairo" knows the directory and not the file, and the difference
    /// between those two is a 404. Every family directory carries a
    /// <c>METADATA.pb</c> that lists its files, which turns a hand-written list
    /// of eleven families into the whole of the repository.
    ///
    /// The parse is deliberately shallow. <c>METADATA.pb</c> is text protobuf
    /// and this reads four fields out of it by line, tracking brace depth so
    /// that the <c>name:</c> inside a <c>fonts { }</c> block is not mistaken for
    /// the family's own. A real protobuf parser would be a dependency, and this
    /// module is not allowed one; a field this cannot read comes back empty and
    /// the caller reports that it could not establish something, which is the
    /// answer it should give anyway.
    /// </summary>
    public struct FontMetadata
    {
        /// <summary>The family as the repository spells it: "Noto Sans KR".</summary>
        public string FamilyName;

        /// <summary>
        /// The licence the file declares: <c>OFL</c>, <c>APACHE2</c>, <c>UFL</c>.
        /// Compared against the directory the file was found in rather than
        /// trusted on its own.
        /// </summary>
        public string LicenceId;

        /// <summary>Every <c>filename:</c> in the family's <c>fonts</c> blocks.</summary>
        public List<string> FileNames;

        /// <summary>True when the family declares variation axes.</summary>
        public bool HasAxes;

        /// <summary>Whether this says enough to act on.</summary>
        public bool Found => !string.IsNullOrEmpty(FamilyName) &&
                             FileNames != null && FileNames.Count > 0;

        /// <summary>
        /// The one file worth fetching for a family, or null when the directory
        /// does not obviously contain one.
        ///
        /// A variable file wins outright: it is the whole family in one download
        /// and it is the only file that can answer four placeholders at once.
        /// Failing that it is the Regular, which is the face everything else is
        /// described relative to. A directory of static faces with no Regular in
        /// it is not something to pick from — the choice would be arbitrary, and
        /// an arbitrary weight installed under the family's name is exactly the
        /// failure this whole module is arranged to avoid.
        /// </summary>
        public string PreferredFile
        {
            get
            {
                if (FileNames == null) return null;

                foreach (string file in FileNames)
                    if (file.IndexOf('[') >= 0 && !IsItalic(file)) return file;

                foreach (string file in FileNames)
                    if (Regular(file)) return file;

                // One file and no way to misread which one it is.
                return FileNames.Count == 1 ? FileNames[0] : null;
            }
        }

        /// <summary>True when the file this family is fetched from is variable.</summary>
        public bool Variable
        {
            get
            {
                string file = PreferredFile;
                return file != null && file.IndexOf('[') >= 0;
            }
        }

        private static bool IsItalic(string file) =>
            file.IndexOf("Italic", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool Regular(string file)
        {
            int dot = file.LastIndexOf('.');
            string stem = dot > 0 ? file.Substring(0, dot) : file;
            return stem.EndsWith("-Regular", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Reads one <c>METADATA.pb</c>.
        ///
        /// Never throws and never returns null: a file that is not what it was
        /// expected to be — a 404 page, a redirect, a format that changed —
        /// comes back with nothing in it, and <see cref="Found"/> false is a
        /// thing the caller can put in a sentence.
        /// </summary>
        public static FontMetadata Parse(string text)
        {
            var facts = new FontMetadata { FileNames = new List<string>() };
            if (string.IsNullOrEmpty(text)) return facts;

            int depth = 0;
            string block = null;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.EndsWith("{", System.StringComparison.Ordinal))
                {
                    string opened = line.Substring(0, line.Length - 1).Trim();
                    if (depth == 0)
                    {
                        block = opened;
                        if (opened == "axes") facts.HasAxes = true;
                    }
                    depth++;
                    continue;
                }

                if (line == "}")
                {
                    depth--;
                    if (depth <= 0)
                    {
                        depth = 0;
                        block = null;
                    }
                    continue;
                }

                if (!Field(line, out string key, out string value)) continue;

                if (depth == 0)
                {
                    // The family's own name is the first one, before any block
                    // opens; the ones inside `fonts { }` name faces.
                    if (key == "name" && string.IsNullOrEmpty(facts.FamilyName)) facts.FamilyName = value;
                    else if (key == "license") facts.LicenceId = value;
                }
                else if (block == "fonts" && key == "filename" && value.Length > 0)
                {
                    if (!facts.FileNames.Contains(value)) facts.FileNames.Add(value);
                }
            }

            return facts;
        }

        /// <summary>One <c>key: value</c> line, with the quotes taken off.</summary>
        private static bool Field(string line, out string key, out string value)
        {
            key = null;
            value = null;

            int colon = line.IndexOf(':');
            if (colon <= 0) return false;

            key = line.Substring(0, colon).Trim();
            value = line.Substring(colon + 1).Trim();
            if (key.Length == 0) return false;

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return true;
        }
    }
}
