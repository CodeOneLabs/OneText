using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Reads the references a container holds out of its serialized text, for
    /// the ones the loaded hierarchy cannot be asked about.
    ///
    /// The census normally reads a live <c>SerializedObject</c>, which is right
    /// and cheap and knows about nesting. It has one blind spot, and the blind
    /// spot is the common case in a real migration: scripts are rewritten before
    /// components are converted, so by the time anything is converted a field
    /// that used to say <c>TMP_Text</c> says <c>OneTextLabel</c> while the file
    /// still names a <c>TextMeshProUGUI</c>. Unity does not bind that — and it
    /// does not merely leave the field empty, it discards the pointer, so
    /// <c>objectReferenceValue</c> and <c>objectReferenceInstanceIDValue</c> are
    /// both nothing. Measured, not assumed: on a real project every such field
    /// read 0 while a sibling field of unchanged type on the same component
    /// resolved normally.
    ///
    /// A component whose script is missing from the project is the same story
    /// told differently — it comes back null from
    /// <c>GetComponentsInChildren</c>, so nothing it holds is ever read either.
    ///
    /// In both cases the file still says exactly what was meant. A reference is
    /// a fileID; a fileID in another container resolves to a <c>stripped</c>
    /// stub; and the stub carries the source file's guid, the object's id inside
    /// it, and the script guid that says what the target was. That is the whole
    /// of what a <see cref="ContainerReferences.CrossReference"/> needs, and it
    /// is sitting on disk in every project this has ever gone wrong in.
    ///
    /// This is deliberately not a YAML parser. Unity writes one dialect, this
    /// reads that dialect, and anything it does not recognise it skips rather
    /// than guesses at.
    /// </summary>
    public static class ContainerFile
    {
        /// <summary>One reference, as the file spells it.</summary>
        public sealed class StoredReference
        {
            /// <summary>Object id of the component that holds the field.</summary>
            public long Referrer;

            /// <summary>Script guid of that component; empty when it has none.</summary>
            public string ReferrerScript;

            /// <summary>The field, in the form a SerializedProperty would name it.</summary>
            public string PropertyPath;

            /// <summary>Asset guid of the container the target is written in.</summary>
            public string TargetGuid;

            /// <summary>Object id of the target inside that container.</summary>
            public long TargetFileId;

            /// <summary>Script guid of the target component.</summary>
            public string TargetScript;

            public override string ToString() =>
                $"{Referrer}.{PropertyPath} -> {TargetGuid}:{TargetFileId}";
        }

        /// <summary>
        /// Every reference in this file that leaves it, whatever the loaded
        /// hierarchy would say about it.
        ///
        /// Returns an empty list rather than throwing for a file that cannot be
        /// read: a container this cannot parse is one the ordinary census still
        /// covers, and a migration that stops because a file surprised it is
        /// worse than one that quietly covers less of it.
        /// </summary>
        public static List<StoredReference> Read(string containerPath)
        {
            string text;
            try { text = File.ReadAllText(containerPath); }
            catch (Exception) { return new List<StoredReference>(); }
            return Parse(text);
        }

        /// <summary>
        /// <see cref="Read"/> without the file, so the dialect this understands
        /// can be tested by writing it down rather than by building a prefab and
        /// hoping Unity serializes it the way the real case was serialized.
        ///
        /// Every mistake this reader has made so far — a document's type name
        /// leaking into property paths, a sibling key that failed to close the
        /// one before it, a file id that was never parsed because the space after
        /// the colon was stepped over too late — was invisible from the outside:
        /// the integration test could only say "nothing found".
        /// </summary>
        public static List<StoredReference> Parse(string text)
        {
            var found = new List<StoredReference>();
            if (string.IsNullOrEmpty(text) || !text.StartsWith("%YAML", StringComparison.Ordinal))
                return found; // a binary-serialized project; nothing here to read

            var documents = Split(text);

            // Where a reference can land, which is two different places. A
            // stripped stub stands for an object in another file and carries
            // that file's guid; an ordinary document is an object in this one
            // and carries nothing but itself.
            //
            // Both are read, because both are severed by the same thing. The
            // cross-file case was the one that could be seen — the field went
            // empty and a prefab in another folder was visibly wrong — and for a
            // long time it was the only one this returned. The same-file case is
            // commoner and quieter: a controller and the label it drives usually
            // live in one prefab, so the field empties, nothing outside that file
            // changes, and the migration's own census cannot see it either
            // because the loaded object no longer holds the pointer.
            var stubs = new Dictionary<long, Stub>();
            var inside = new Dictionary<long, string>();
            foreach (var document in documents)
            {
                if (document.Stripped)
                {
                    var stub = new Stub
                    {
                        Script = Guid(document, "m_Script"),
                        SourceGuid = Guid(document, "m_CorrespondingSourceObject"),
                        SourceFileId = FileId(document, "m_CorrespondingSourceObject"),
                    };
                    if (!string.IsNullOrEmpty(stub.SourceGuid)) stubs[document.Id] = stub;
                    continue;
                }

                // Only the ones with a script: a reference to a Transform or a
                // RectTransform is not something this migration ever breaks.
                string script = Guid(document, "m_Script");
                if (!string.IsNullOrEmpty(script)) inside[document.Id] = script;
            }
            if (stubs.Count == 0 && inside.Count == 0) return found;

            foreach (var document in documents)
            {
                if (document.Stripped) continue;
                string referrerScript = Guid(document, "m_Script");

                foreach (var reference in References(document))
                {
                    if (stubs.TryGetValue(reference.Value, out var stub))
                    {
                        found.Add(new StoredReference
                        {
                            Referrer = document.Id,
                            ReferrerScript = referrerScript,
                            PropertyPath = reference.Path,
                            TargetGuid = stub.SourceGuid,
                            TargetFileId = stub.SourceFileId,
                            TargetScript = stub.Script,
                        });
                        continue;
                    }

                    // A component naming itself is a back-pointer, not a
                    // reference something else has to mend.
                    if (reference.Value == document.Id) continue;

                    if (!inside.TryGetValue(reference.Value, out string targetScript)) continue;
                    found.Add(new StoredReference
                    {
                        Referrer = document.Id,
                        ReferrerScript = referrerScript,
                        PropertyPath = reference.Path,
                        TargetGuid = null, // in this file; there is no other file to name
                        TargetFileId = reference.Value,
                        TargetScript = targetScript,
                    });
                }
            }
            return found;
        }

        /// <summary>
        /// Whether this reference names something in the same file as the field
        /// that holds it. The two cases are mended by different code in different
        /// passes, and the difference is exactly whether there is another
        /// container to go and open.
        /// </summary>
        public static bool IsInside(StoredReference reference) =>
            reference != null && string.IsNullOrEmpty(reference.TargetGuid);

        /// <summary>
        /// What a script guid says the component is, asked of the providers so
        /// the answer cannot drift from what the conversion actually does.
        /// </summary>
        public static MigrationKind KindOfScript(string scriptGuid)
        {
            if (string.IsNullOrEmpty(scriptGuid)) return MigrationKind.None;

            string path = AssetDatabase.GUIDToAssetPath(scriptGuid);
            if (string.IsNullOrEmpty(path)) return MigrationKind.None;

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            var type = script == null ? null : script.GetClass();
            if (type == null) return MigrationKind.None;

            foreach (var provider in MigrationProviders.All)
            {
                MigrationKind kind;
                try { kind = provider.KindOf(type); }
                catch (Exception) { continue; }
                if (kind != MigrationKind.None) return kind;
            }
            return MigrationKind.None;
        }

        // ----------------------------------------------------------- internals

        private struct Stub
        {
            public string Script;
            public string SourceGuid;
            public long SourceFileId;
        }

        private sealed class Document
        {
            public long Id;
            public bool Stripped;
            public List<string> Lines = new List<string>();
        }

        private struct Reference
        {
            public string Path;
            public long Value;
        }

        /// <summary>
        /// Cuts the file at its document headers: <c>--- !u!114 &amp;12345</c>,
        /// with an optional <c>stripped</c> after the id.
        /// </summary>
        private static List<Document> Split(string text)
        {
            var documents = new List<Document>();
            Document current = null;

            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    current = new Document { Stripped = line.EndsWith(" stripped", StringComparison.Ordinal) };
                    int amp = line.IndexOf('&');
                    if (amp >= 0)
                    {
                        // The minus first, and then the digits. An anchor can be
                        // negative — Unity writes them in imported prefabs, and
                        // this project has them — and eating only the digits
                        // parsed '&-123' as nothing, so a stub with a negative
                        // anchor could never be matched and the reference that
                        // landed on it was dropped without a word.
                        int end = amp + 1;
                        if (end < line.Length && line[end] == '-') end++;
                        while (end < line.Length && char.IsDigit(line[end])) end++;
                        long.TryParse(line.Substring(amp + 1, end - amp - 1),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out current.Id);
                    }
                    documents.Add(current);
                    continue;
                }
                current?.Lines.Add(line.TrimEnd('\r'));
            }
            return documents;
        }

        /// <summary>
        /// Every <c>{fileID: n}</c> in this document that names an object in the
        /// same file, with the property path a SerializedProperty would use.
        ///
        /// The path is rebuilt from indentation, which is what Unity's writer
        /// uses and the only structure the text has. A sequence becomes
        /// <c>name.Array.data[i]</c> because that is what
        /// <c>SerializedObject.FindProperty</c> answers to, and a field inside a
        /// list of a serializable class — which is where a chart, a table or a
        /// tab bar keeps its labels — is reachable no other way.
        /// </summary>
        private static IEnumerable<Reference> References(Document document)
        {
            var frames = new List<Frame>();
            bool rootSeen = false;

            foreach (string raw in document.Lines)
            {
                if (raw.Length == 0) continue;

                int indent = 0;
                while (indent < raw.Length && raw[indent] == ' ') indent++;
                string line = raw.Substring(indent);
                if (line.Length == 0 || line[0] == '#') continue;

                bool item = line.StartsWith("- ", StringComparison.Ordinal);
                if (item) line = line.Substring(2);

                // A key at the same indent as an open frame is that frame's
                // sibling and closes it — which is also how a key that only
                // looked like a block is undone. Unity writes an empty string as
                // "m_Name:" with nothing after it, indistinguishable at that
                // line from a map about to be indented, and the sibling on the
                // next line is what settles which it was.
                //
                // A sequence entry is the exception, because Unity writes it at
                // the same indent as the key that owns it: it closes a previous
                // entry at its own indent and never the key above it. That
                // ambiguity is why the frames have to remember which they are.
                while (frames.Count > 0)
                {
                    var top = frames[frames.Count - 1];
                    bool closes = item
                        ? top.Indent > indent || (top.Indent == indent && top.IsItem)
                        : top.Indent >= indent;
                    if (!closes) break;
                    frames.RemoveAt(frames.Count - 1);
                }

                if (item)
                {
                    int index = 0;
                    if (frames.Count > 0)
                    {
                        var owner = frames[frames.Count - 1];
                        index = owner.NextItem;
                        owner.NextItem = index + 1;
                    }
                    frames.Add(new Frame { Indent = indent, IsItem = true, Name = $"Array.data[{index}]" });
                }

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon);
                string value = line.Substring(colon + 1).Trim();

                if (value.Length == 0)
                {
                    // The first block key of a document is the type — the
                    // "MonoBehaviour:" a component's body hangs from — and it is
                    // not part of any property path, so it opens nothing.
                    if (!rootSeen && indent == 0 && !item) { rootSeen = true; continue; }
                    frames.Add(new Frame { Indent = indent, IsItem = false, Name = key });
                    continue;
                }

                if (!value.StartsWith("{fileID:", StringComparison.Ordinal)) continue;
                // Only references inside this file matter here. One carrying a
                // guid names an asset directly and was never at risk: assets are
                // not renumbered by a conversion.
                if (value.IndexOf("guid:", StringComparison.Ordinal) >= 0) continue;

                long id = ParseFileId(value);
                if (id == 0) continue;

                yield return new Reference { Path = PathTo(frames, key), Value = id };
            }
        }

        private sealed class Frame
        {
            public int Indent;
            public bool IsItem;
            public string Name;
            public int NextItem;
        }

        private static string PathTo(List<Frame> frames, string key)
        {
            var parts = new List<string>(frames.Count + 1);
            foreach (var frame in frames) parts.Add(frame.Name);
            parts.Add(key);
            return string.Join(".", parts);
        }

        /// <summary>
        /// The number in <c>{fileID: 12345, ...}</c>. The space after the colon
        /// is always there and has to be stepped over before the digits are
        /// counted, not after.
        /// </summary>
        private static long ParseFileId(string value)
        {
            int start = value.IndexOf(':') + 1;
            if (start <= 0) return 0;
            while (start < value.Length && value[start] == ' ') start++;

            int end = start;
            if (end < value.Length && value[end] == '-') end++;
            while (end < value.Length && char.IsDigit(value[end])) end++;
            if (end == start) return 0;

            long.TryParse(value.Substring(start, end - start),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long id);
            return id;
        }

        private static string Guid(Document document, string key)
        {
            string line = Line(document, key);
            if (line == null) return null;
            int at = line.IndexOf("guid:", StringComparison.Ordinal);
            if (at < 0) return null;
            int start = at + 5;
            while (start < line.Length && line[start] == ' ') start++;
            int end = start;
            while (end < line.Length && Uri.IsHexDigit(line[end])) end++;
            return end > start ? line.Substring(start, end - start) : null;
        }

        private static long FileId(Document document, string key)
        {
            string line = Line(document, key);
            if (line == null) return 0;
            int brace = line.IndexOf('{');
            return brace < 0 ? 0 : ParseFileId(line.Substring(brace));
        }

        /// <summary>The one line of this document whose key is <paramref name="key"/>, at any depth.</summary>
        private static string Line(Document document, string key)
        {
            string wanted = key + ":";
            foreach (string line in document.Lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith(wanted, StringComparison.Ordinal)) return trimmed;
            }
            return null;
        }
    }
}
