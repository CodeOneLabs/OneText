using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Charsets, and the folders they are built from.
    ///
    /// The recorder answers "what did this play session draw" and the project
    /// scan answers "what is typed into a prefab". Neither answers "what will
    /// this game ever draw", because that lives in the string tables — so a
    /// charset can name folders, and the folders are rescanned when they
    /// change. One output feeds prewarm and subsetting alike.
    /// </summary>
    public sealed class HubCharsetsTab
    {
        private OneTextCharset _selected;
        private CharsetFolderScan.Report _lastReport;
        private bool _scanned;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Charsets",
                "Characters to rasterize before they are needed. A charset that scans folders " +
                "follows the string tables as translators change them, instead of being correct " +
                "on the day it was made.");

            _selected = (OneTextCharset)EditorGUILayout.ObjectField("Charset", _selected,
                typeof(OneTextCharset), false);

            if (_selected == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick a charset, or create one with Assets > Create > OneText > Charset.",
                    MessageType.Info);
                DrawRecorder();
                return;
            }

            var codepoints = _selected.Codepoints();
            EditorGUILayout.LabelField("Characters",
                $"{codepoints.Count:n0} across {Mathf.Max(1, _selected.Sizes.Count)} size(s)");

            EditorGUILayout.Space();
            DrawSourceFolders();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rescan now"))
                {
                    _lastReport = CharsetFolderScan.Rescan(_selected);
                    _scanned = true;
                }
                using (new EditorGUI.DisabledScope(!Application.isPlaying &&
                    _selected.BuildFontStack().Primary == null))
                {
                    if (GUILayout.Button("Prewarm now"))
                        Debug.Log($"OneText {_selected.Prewarm()}", _selected);
                }
            }

            if (_scanned)
            {
                EditorGUILayout.HelpBox(_lastReport.ToString(),
                    _lastReport.Skipped != null && _lastReport.Skipped.Count > 0
                        ? MessageType.Warning
                        : MessageType.Info);
                if (_lastReport.Skipped != null)
                    foreach (string skipped in _lastReport.Skipped)
                        EditorGUILayout.LabelField("  skipped", skipped, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
            DrawRecorder();
        }

        private void DrawSourceFolders()
        {
            EditorGUILayout.LabelField("Source folders", EditorStyles.boldLabel);
            var folders = _selected.SourceFolders;
            for (int i = 0; i < folders.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string edited = EditorGUILayout.TextField(folders[i]);
                    if (edited != folders[i])
                    {
                        Undo.RecordObject(_selected, "Edit charset source folder");
                        folders[i] = edited;
                        EditorUtility.SetDirty(_selected);
                    }
                    if (GUILayout.Button("-", GUILayout.Width(24f)))
                    {
                        Undo.RecordObject(_selected, "Remove charset source folder");
                        folders.RemoveAt(i);
                        EditorUtility.SetDirty(_selected);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add folder"))
                {
                    string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        Undo.RecordObject(_selected, "Add charset source folder");
                        folders.Add(TextSourceScanner.ToProjectPath(picked));
                        EditorUtility.SetDirty(_selected);
                    }
                }

                bool auto = EditorGUILayout.ToggleLeft(
                    new GUIContent("Rescan on import",
                        "Refresh this charset whenever a file under those folders changes."),
                    _selected.AutoRescan, GUILayout.Width(160f));
                if (auto != _selected.AutoRescan)
                {
                    Undo.RecordObject(_selected, "Toggle charset auto-rescan");
                    _selected.AutoRescan = auto;
                    EditorUtility.SetDirty(_selected);
                }
            }
        }

        /// <summary>
        /// The recorder, which is the other half: what a play session drew is
        /// what no static scan can predict.
        /// </summary>
        private void DrawRecorder()
        {
            EditorGUILayout.LabelField("Recorder", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Recorded this session",
                CharsetRecorder.Enabled
                    ? $"{CharsetRecorder.CodepointCount:n0} character(s)"
                    : "off — turn on 'Record Charset In Play Mode' in Project Settings > OneText");

            using (new EditorGUI.DisabledScope(CharsetRecorder.CodepointCount == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_selected != null && GUILayout.Button("Add recorded to this charset"))
                    {
                        Undo.RecordObject(_selected, "Add recorded characters");
                        _selected.Characters = Merge(_selected.Characters,
                            CharsetRecorder.CharactersAsString());
                        foreach (float size in CharsetRecorder.SizesSorted())
                            if (!_selected.Sizes.Contains(size)) _selected.Sizes.Add(size);
                        EditorUtility.SetDirty(_selected);
                    }
                    if (GUILayout.Button("Clear"))
                        CharsetRecorder.Clear();
                }
            }
        }

        /// <summary>Union of two character sets, sorted, without duplicates or whitespace.</summary>
        internal static string Merge(string a, string b)
        {
            var seen = new System.Collections.Generic.SortedSet<int>();
            foreach (string source in new[] { a, b })
            {
                if (string.IsNullOrEmpty(source)) continue;
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                    if (char.IsHighSurrogate(c) && i + 1 < source.Length &&
                        char.IsLowSurrogate(source[i + 1]))
                    {
                        seen.Add(char.ConvertToUtf32(c, source[i + 1]));
                        i++;
                    }
                    else seen.Add(c);
                }
            }

            var builder = new System.Text.StringBuilder(seen.Count);
            foreach (int cp in seen) builder.Append(char.ConvertFromUtf32(cp));
            return builder.ToString();
        }
    }
}
