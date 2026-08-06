using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Keeps a charset in step with the folders it was built from.
    ///
    /// A charset filled once from a string table is correct until the day the
    /// translators add a character, and nothing about that day announces
    /// itself: the atlas simply bakes a glyph at runtime that the prewarm was
    /// supposed to have covered, on a device, in a language nobody on the team
    /// reads. So the scan is recorded on the asset and repeated whenever a file
    /// under those folders is imported.
    /// </summary>
    public static class CharsetFolderScan
    {
        /// <summary>What a rescan changed.</summary>
        public struct Report
        {
            public int FilesScanned;
            public int StringsFound;
            public int CharactersBefore;
            public int CharactersAfter;
            public List<string> Skipped;

            public int Added => Mathf.Max(0, CharactersAfter - CharactersBefore);

            public override string ToString() =>
                $"scanned {FilesScanned} file(s), {StringsFound:n0} string(s): " +
                $"{CharactersBefore:n0} -> {CharactersAfter:n0} characters";
        }

        /// <summary>Rescans a charset's source folders and replaces what the last scan contributed.</summary>
        public static Report Rescan(OneTextCharset charset)
        {
            var report = new Report();
            if (charset == null) return report;

            report.CharactersBefore = charset.Codepoints().Count;
            var scan = TextSourceScanner.Scan(charset.SourceFolders);
            report.FilesScanned = scan.FilesScanned;
            report.StringsFound = scan.Entries.Count;
            report.Skipped = scan.Skipped;

            string scanned = scan.CharactersAsString();
            if (scanned != charset.ScannedCharacters)
            {
                Undo.RecordObject(charset, "Rescan charset sources");
                charset.ScannedCharacters = scanned;
                EditorUtility.SetDirty(charset);
            }
            report.CharactersAfter = charset.Codepoints().Count;
            return report;
        }

        /// <summary>Every charset in the project that scans folders and asked to be kept current.</summary>
        public static IEnumerable<OneTextCharset> AutoRescanning()
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(OneTextCharset)}"))
            {
                var charset = AssetDatabase.LoadAssetAtPath<OneTextCharset>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (charset == null || !charset.AutoRescan || charset.SourceFolders.Count == 0) continue;
                yield return charset;
            }
        }

        /// <summary>
        /// Rescans on import, for the charsets whose folders the import touched.
        ///
        /// Scoped to the folders that actually changed because the alternative,
        /// rescanning every charset on every import, is a cost paid on every
        /// asset in the project for a folder nobody edited.
        /// </summary>
        private sealed class ImportHook : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                string[] movedTo, string[] movedFrom)
            {
                if (imported.Length == 0 && deleted.Length == 0 && movedTo.Length == 0) return;

                var touched = new List<string>();
                touched.AddRange(imported);
                touched.AddRange(deleted);
                touched.AddRange(movedTo);
                touched.AddRange(movedFrom);

                foreach (var charset in AutoRescanning())
                {
                    // A charset asset saved by a rescan comes back through this
                    // hook; rescanning it again would be an import loop.
                    if (!TouchesAny(touched, charset)) continue;
                    var report = Rescan(charset);
                    if (report.Added > 0)
                    {
                        Debug.Log($"OneText: {charset.name} rescanned: {report}.", charset);
                    }
                }
            }

            private static bool TouchesAny(List<string> paths, OneTextCharset charset)
            {
                string charsetPath = AssetDatabase.GetAssetPath(charset);
                foreach (string path in paths)
                {
                    if (path == charsetPath) continue;
                    foreach (string folder in charset.SourceFolders)
                    {
                        if (string.IsNullOrEmpty(folder)) continue;
                        string prefix = folder.TrimEnd('/') + "/";
                        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                return false;
            }
        }
    }
}
