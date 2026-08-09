using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OneText.Editor
{
    /// <summary>One assembly definition, as much of it as this file has a use for.</summary>
    public sealed class TmpAssemblyDefinition
    {
        public TmpAssemblyDefinition(string path, string name, string guid, List<string> references)
        {
            Path = path == null ? null : path.Replace('\\', '/');
            Name = name;
            Guid = guid;
            References = references ?? new List<string>();
            int cut = Path == null ? -1 : Path.LastIndexOf('/');
            Folder = cut < 0 ? string.Empty : Path.Substring(0, cut);
        }

        /// <summary>The <c>.asmdef</c> itself.</summary>
        public string Path { get; }

        /// <summary>The folder it governs, and everything under it.</summary>
        public string Folder { get; }

        /// <summary>The assembly's name, e.g. <c>LayerLab.CommonSource</c>.</summary>
        public string Name { get; }

        /// <summary>Its own GUID, from the <c>.meta</c> beside it, or empty.</summary>
        public string Guid { get; }

        /// <summary>
        /// Exactly as written, which is either a name or a <c>GUID:…</c>. Unity
        /// accepts both and real projects contain both, sometimes in the same
        /// array, so nothing here assumes one form.
        /// </summary>
        public List<string> References { get; }

        public override string ToString() => Name ?? Path;
    }

    /// <summary>
    /// Which assembly a script belongs to, and whether that assembly can see
    /// OneText.
    ///
    /// This exists because of a failure that looks nothing like the rewrite that
    /// caused it. Renaming <c>TextMeshProUGUI</c> to <c>OneTextLabel</c> inside
    /// a file governed by somebody else's <c>.asmdef</c> produces twenty-one
    /// CS0246s at a path the person migrating never opened, and the reason is
    /// not in the file at all: the assembly lists its references by hand and
    /// OneText is not among them. <c>autoReferenced</c> does not help — it only
    /// covers Assembly-CSharp, and a project large enough to have this problem
    /// is a project that stopped using Assembly-CSharp years ago.
    ///
    /// So the scan has to look one level up from the file. Everything here is
    /// text: no <c>CompilationPipeline</c>, no <c>AssetDatabase</c>, nothing
    /// that needs the editor to have finished compiling — which it has not, in
    /// the project that needs this.
    ///
    /// Reading and patching are deliberately separate calls. A scan that
    /// silently edited a vendor's <c>.asmdef</c> would be doing surgery during
    /// a diagnosis.
    /// </summary>
    public sealed class TmpAssemblyGraph
    {
        /// <summary>The assemblies a rewritten file can end up naming.</summary>
        public const string CoreAssembly = "OneText";

        public const string UGuiAssembly = "OneText.UGUI";

        public const string MeshAssembly = "OneText.Mesh";

        /// <summary>
        /// OneText's own GUIDs, so a reference written as <c>GUID:…</c> resolves
        /// even when the package is not somewhere this graph indexed — which is
        /// the normal case, since a package lives outside <c>Assets</c> and a
        /// registry copy lives somewhere nobody should be walking.
        /// </summary>
        private static readonly (string Guid, string Name)[] OwnGuids =
        {
            ("8bd298fe0fa4540968dc3f9e6ee7139d", CoreAssembly),
            ("f78bcd2f5c7814894929e319cfe7a2f9", UGuiAssembly),
            ("8f7e01308b6644748a471553cb5d4b84", MeshAssembly),
            ("c12098c24c3524547b01143c253e57b3", "OneText.Editor"),
        };

        private readonly Dictionary<string, TmpAssemblyDefinition> _byFolder =
            new Dictionary<string, TmpAssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Folders already looked in and found to hold no <c>.asmdef</c>, so a
        /// project's deep folder trees are walked once rather than once per
        /// script in them.
        /// </summary>
        private readonly HashSet<string> _empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TmpAssemblyGraph()
        {
            foreach (var own in OwnGuids) _names[own.Guid] = own.Name;
        }

        // ================================================================ api

        /// <summary>
        /// Indexes every assembly definition under the given folders, which is
        /// what makes a <c>GUID:…</c> reference readable. A graph built with no
        /// roots still answers every question below; it just has to find each
        /// <c>.asmdef</c> on the way up, and cannot name a GUID that is not
        /// OneText's own.
        /// </summary>
        public static TmpAssemblyGraph Build(params string[] roots)
        {
            var graph = new TmpAssemblyGraph();
            if (roots == null) return graph;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                graph.Index(root);
            }
            return graph;
        }

        /// <summary>
        /// The assembly a script compiles into, or <c>null</c> when no
        /// <c>.asmdef</c> stands above it — which means the predefined assembly,
        /// and the predefined assembly sees everything auto-referenced, so a
        /// <c>null</c> here is the good answer rather than a missing one.
        /// </summary>
        public TmpAssemblyDefinition Owner(string csFilePath)
        {
            if (string.IsNullOrEmpty(csFilePath)) return null;
            string folder = Directory.Exists(csFilePath)
                ? csFilePath.Replace('\\', '/')
                : DirectoryOf(csFilePath);

            while (!string.IsNullOrEmpty(folder))
            {
                if (_byFolder.TryGetValue(folder, out var found)) return found;
                if (!_empty.Contains(folder))
                {
                    var here = ReadFolder(folder);
                    if (here != null) return here;
                    _empty.Add(folder);
                }
                int cut = folder.LastIndexOf('/');
                if (cut <= 0) break;
                folder = folder.Substring(0, cut);
            }
            return null;
        }

        /// <summary>
        /// Can this assembly name a type in <paramref name="assembly"/>? An
        /// assembly definition's references are not transitive, so referencing
        /// <c>OneText.UGUI</c> is not referencing <c>OneText</c>, and a project
        /// that assumes otherwise finds out one CS0246 at a time.
        /// </summary>
        public bool Sees(TmpAssemblyDefinition asmdef, string assembly)
        {
            if (asmdef == null) return true; // the predefined assembly
            if (string.IsNullOrEmpty(assembly)) return true;
            if (string.Equals(asmdef.Name, assembly, StringComparison.Ordinal)) return true;

            foreach (string reference in asmdef.References)
                if (string.Equals(Resolve(reference), assembly, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Of the assemblies a rewritten file would name, the ones this
        /// assembly does not reference yet, in the order they were asked for.
        /// </summary>
        public List<string> Missing(TmpAssemblyDefinition asmdef, IEnumerable<string> wanted)
        {
            var missing = new List<string>();
            if (wanted == null || asmdef == null) return missing;
            foreach (string assembly in wanted)
            {
                if (string.IsNullOrEmpty(assembly) || missing.Contains(assembly)) continue;
                if (!Sees(asmdef, assembly)) missing.Add(assembly);
            }
            return missing;
        }

        /// <summary>
        /// Adds references to an assembly definition on disk, keeping its byte
        /// order mark, its line endings and its indentation, because a
        /// migration that reformats a vendor's file has put noise in a diff
        /// somebody else has to read. Returns whether anything was written.
        /// </summary>
        public static bool Patch(string asmdefPath, IEnumerable<string> assemblies)
        {
            if (string.IsNullOrEmpty(asmdefPath) || !File.Exists(asmdefPath)) return false;

            string text = Read(asmdefPath, out bool bom);
            string patched = WithReferences(text, assemblies);
            if (patched == text) return false;

            File.WriteAllText(asmdefPath, patched, new UTF8Encoding(bom));
            return true;
        }

        /// <summary>
        /// The same edit as <see cref="Patch"/>, on text, which is the half
        /// worth testing. A name already in the array is not added twice, and a
        /// file whose <c>references</c> array cannot be found comes back
        /// untouched rather than half-written.
        /// </summary>
        public static string WithReferences(string text, IEnumerable<string> assemblies)
        {
            if (string.IsNullOrEmpty(text) || assemblies == null) return text;

            var existing = ReadArray(text, "references");
            var adding = new List<string>();
            foreach (string assembly in assemblies)
            {
                if (string.IsNullOrEmpty(assembly)) continue;
                if (adding.Contains(assembly)) continue;
                bool had = false;
                foreach (string reference in existing)
                    if (string.Equals(Known(reference), assembly, StringComparison.Ordinal))
                        had = true;
                if (!had) adding.Add(assembly);
            }
            if (adding.Count == 0) return text;

            if (!FindArray(text, "references", out int open, out int close)) return text;

            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string inner = text.Substring(open + 1, close - open - 1);
            bool empty = inner.Trim().Length == 0;
            bool multiline = inner.IndexOf('\n') >= 0;
            string itemPad = ItemIndent(text, open, multiline || empty);

            var built = new StringBuilder();
            if (empty)
            {
                // An empty array is rewritten whole: there is no existing entry
                // to copy a shape from.
                built.Append('[').Append(newline);
                for (int i = 0; i < adding.Count; i++)
                {
                    built.Append(itemPad).Append('"').Append(adding[i]).Append('"');
                    if (i < adding.Count - 1) built.Append(',');
                    built.Append(newline);
                }
                built.Append(ClosingIndent(text, close)).Append(']');
                return text.Substring(0, open) + built + text.Substring(close + 1);
            }

            int last = close - 1;
            while (last > open && char.IsWhiteSpace(text[last])) last--;
            foreach (string assembly in adding)
            {
                built.Append(',');
                if (multiline) built.Append(newline).Append(itemPad);
                else built.Append(' ');
                built.Append('"').Append(assembly).Append('"');
            }
            return text.Substring(0, last + 1) + built + text.Substring(last + 1);
        }

        // ============================================================ reading

        private void Index(string folder)
        {
            var here = ReadFolder(folder);
            if (here == null) _empty.Add(folder.Replace('\\', '/'));

            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (string child in children)
            {
                string name = System.IO.Path.GetFileName(child);
                if (name.Length == 0 || name[0] == '.') continue;
                if (name == "obj" || name == "bin" || name == "Library" || name == "Temp") continue;
                Index(child);
            }
        }

        /// <summary>The assembly definition in exactly this folder, if there is one.</summary>
        private TmpAssemblyDefinition ReadFolder(string folder)
        {
            string key = folder.Replace('\\', '/');
            if (_byFolder.TryGetValue(key, out var known)) return known;

            string[] files;
            try
            {
                if (!Directory.Exists(folder)) return null;
                files = Directory.GetFiles(folder, "*.asmdef");
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            if (files.Length == 0) return null;

            Array.Sort(files, StringComparer.Ordinal);
            var asmdef = ReadAsmdef(files[0]);
            if (asmdef == null) return null;

            _byFolder[key] = asmdef;
            if (!string.IsNullOrEmpty(asmdef.Guid) && !string.IsNullOrEmpty(asmdef.Name))
                _names[asmdef.Guid] = asmdef.Name;
            return asmdef;
        }

        private static TmpAssemblyDefinition ReadAsmdef(string path)
        {
            string text = Read(path, out _);
            if (text == null) return null;
            return new TmpAssemblyDefinition(path, ReadString(text, "name"), ReadGuid(path),
                ReadArray(text, "references"));
        }

        /// <summary>
        /// The GUID Unity gave the assembly definition, which is the other half
        /// of resolving a <c>GUID:…</c> reference.
        /// </summary>
        private static string ReadGuid(string asmdefPath)
        {
            string meta = Read(asmdefPath + ".meta", out _);
            if (meta == null) return string.Empty;
            const string key = "guid:";
            int at = meta.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return string.Empty;
            at += key.Length;
            while (at < meta.Length && (meta[at] == ' ' || meta[at] == '\t')) at++;
            int end = at;
            while (end < meta.Length && Uri.IsHexDigit(meta[end])) end++;
            return meta.Substring(at, end - at);
        }

        /// <summary>
        /// A reference as a name. Several real assembly definitions are written
        /// with a byte order mark and several reference by GUID; both are
        /// Unity's own doing, and both have to read the same here.
        /// </summary>
        private string Resolve(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return reference;
            const string prefix = "GUID:";
            if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return reference;
            string guid = reference.Substring(prefix.Length).Trim();
            return _names.TryGetValue(guid, out string name) ? name : reference;
        }

        /// <summary>
        /// The same, for OneText's own GUIDs only, which is all a patch needs to
        /// know: an assembly that already references <c>OneText.UGUI</c> by GUID
        /// must not be handed a second reference to it by name.
        /// </summary>
        private static string Known(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return reference;
            const string prefix = "GUID:";
            if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return reference;
            string guid = reference.Substring(prefix.Length).Trim();
            foreach (var own in OwnGuids)
                if (string.Equals(own.Guid, guid, StringComparison.OrdinalIgnoreCase)) return own.Name;
            return reference;
        }

        /// <summary>
        /// Reads a file and says whether it carried a byte order mark, which
        /// several real assembly definitions do and which has to survive a
        /// patch — Unity does not mind either way, but a version control diff
        /// that shows every line changed does.
        /// </summary>
        private static string Read(string path, out bool bom)
        {
            bom = false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0,
                    bytes.Length - (bom ? 3 : 0));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // ======================================================= a little json

        /// <summary>
        /// Enough JSON to read an assembly definition and no more. Unity writes
        /// these files itself and their shape is fixed, so a scanner that finds
        /// a key and reads the value after it is both shorter than a parser and
        /// unbothered by the fields this does not know about.
        /// </summary>
        private static string ReadString(string json, string key)
        {
            int at = KeyAt(json, key);
            if (at < 0) return string.Empty;
            while (at < json.Length && json[at] != '"') at++;
            if (at >= json.Length) return string.Empty;
            var value = new StringBuilder();
            for (int i = at + 1; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length) { value.Append(json[++i]); continue; }
                if (json[i] == '"') break;
                value.Append(json[i]);
            }
            return value.ToString();
        }

        private static List<string> ReadArray(string json, string key)
        {
            var values = new List<string>();
            if (!FindArray(json, key, out int open, out int close)) return values;

            var value = new StringBuilder();
            bool inside = false;
            for (int i = open + 1; i < close; i++)
            {
                char c = json[i];
                if (inside && c == '\\' && i + 1 < close) { value.Append(json[++i]); continue; }
                if (c != '"') { if (inside) value.Append(c); continue; }
                if (inside) values.Add(value.ToString());
                else value.Length = 0;
                inside = !inside;
            }
            return values;
        }

        private static bool FindArray(string json, string key, out int open, out int close)
        {
            open = close = -1;
            int at = KeyAt(json, key);
            if (at < 0) return false;
            while (at < json.Length && json[at] != '[')
            {
                if (json[at] == ',' || json[at] == '}') return false;
                at++;
            }
            if (at >= json.Length) return false;
            open = at;

            bool inString = false;
            for (int i = at + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == ']') { close = i; return true; }
            }
            return false;
        }

        /// <summary>Index just past <c>"key":</c>, or −1.</summary>
        private static int KeyAt(string json, string key)
        {
            string quoted = "\"" + key + "\"";
            int at = json.IndexOf(quoted, StringComparison.Ordinal);
            if (at < 0) return -1;
            at += quoted.Length;
            while (at < json.Length && (json[at] == ' ' || json[at] == '\t')) at++;
            if (at >= json.Length || json[at] != ':') return -1;
            return at + 1;
        }

        // ============================================================ shaping

        /// <summary>The indent a new entry in the array should carry.</summary>
        private static string ItemIndent(string text, int open, bool multiline)
        {
            if (!multiline) return string.Empty;

            int line = text.LastIndexOf('\n', Math.Min(open, text.Length - 1));
            int i = line + 1;
            var pad = new StringBuilder();
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) pad.Append(text[i++]);
            string outer = pad.ToString();
            return outer + (outer.IndexOf('\t') >= 0 ? "\t" : "    ");
        }

        private static string ClosingIndent(string text, int close)
        {
            int line = text.LastIndexOf('\n', Math.Min(close, text.Length - 1));
            var pad = new StringBuilder();
            for (int i = line + 1; i < close && (text[i] == ' ' || text[i] == '\t'); i++)
                pad.Append(text[i]);
            return pad.ToString();
        }

        private static string DirectoryOf(string path)
        {
            string normal = path.Replace('\\', '/');
            int cut = normal.LastIndexOf('/');
            return cut < 0 ? string.Empty : normal.Substring(0, cut);
        }

        /// <summary>
        /// The nearest folder named <c>Assets</c> above a path, which is where a
        /// scan can start indexing without being told.
        /// </summary>
        public static string AssetsRootOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string folder = DirectoryOf(path.Replace('\\', '/'));
            while (!string.IsNullOrEmpty(folder))
            {
                string name = System.IO.Path.GetFileName(folder);
                if (string.Equals(name, "Assets", StringComparison.Ordinal)) return folder;
                int cut = folder.LastIndexOf('/');
                if (cut <= 0) return null;
                folder = folder.Substring(0, cut);
            }
            return null;
        }
    }
}
