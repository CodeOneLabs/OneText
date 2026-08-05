using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// Inspector for a charset, plus the three ways of filling one that do not
    /// involve typing characters by hand: take what a play session recorded,
    /// scan the project's labels, or add a Unicode range.
    /// </summary>
    [CustomEditor(typeof(OneTextCharset))]
    public sealed class OneTextCharsetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var charset = (OneTextCharset)target;

            EditorGUILayout.Space();
            var codepoints = charset.Codepoints();
            int pairs = codepoints.Count * Mathf.Max(1, charset.Sizes.Count);
            EditorGUILayout.LabelField("Characters", $"{codepoints.Count:n0} " +
                $"({pairs:n0} tiles across {Mathf.Max(1, charset.Sizes.Count)} size(s))");

            var budget = OneTextSettings.Instance != null
                ? OneTextSettings.Instance.AtlasSettings
                : GlyphAtlasSettings.Default;
            // 56px square is about what a CJK glyph takes at 48px with padding;
            // it is the useful order of magnitude for "will this fit?".
            long estimated = (long)pairs * 56 * 56;
            if (estimated > budget.MemoryBytes * charset.FillLimit)
            {
                EditorGUILayout.HelpBox(
                    $"This charset is roughly {estimated / (1024f * 1024f):0.#} MB of tiles at CJK sizes, " +
                    $"more than the {budget.MemoryBytes / (1024f * 1024f):0.#} MB atlas can hold. " +
                    "Prewarm will stop at the budget and report what did not fit.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add recorded characters"))
                    AddRecorded(charset);
                if (GUILayout.Button("Scan project labels"))
                    AddFromProject(charset);
            }

            if (GUILayout.Button("Add range..."))
                ShowRangeMenu(charset);

            using (new EditorGUI.DisabledScope(!Application.isPlaying && charset.BuildFontStack().Primary == null))
            {
                if (GUILayout.Button("Prewarm now"))
                {
                    var report = charset.Prewarm();
                    Debug.Log($"OneText {report}", charset);
                }
            }
        }

        private static void AddRecorded(OneTextCharset charset)
        {
            if (CharsetRecorder.CodepointCount == 0)
            {
                EditorUtility.DisplayDialog("Nothing recorded",
                    "No characters have been recorded. Turn on 'Record Charset In Play Mode' in " +
                    "Project Settings > OneText, play the game, then try again.", "OK");
                return;
            }

            Undo.RecordObject(charset, "Add recorded characters");
            charset.Characters = Merge(charset.Characters, CharsetRecorder.CharactersAsString());
            foreach (float size in CharsetRecorder.SizesSorted())
                if (!charset.Sizes.Contains(size)) charset.Sizes.Add(size);
            EditorUtility.SetDirty(charset);
        }

        private static void ShowRangeMenu(OneTextCharset charset)
        {
            var menu = new GenericMenu();
            foreach (var preset in OneTextCharset.Presets)
            {
                var captured = preset;
                menu.AddItem(new GUIContent($"{preset.Name} ({preset.Count:n0})"), false, () =>
                {
                    Undo.RecordObject(charset, "Add range");
                    charset.Ranges.Add(captured);
                    EditorUtility.SetDirty(charset);
                });
            }
            menu.ShowAsContext();
        }

        /// <summary>
        /// Collects the text of every label in the project's prefabs and open
        /// scenes. Catches static UI; anything assembled at runtime only a play
        /// session can report.
        /// </summary>
        private static void AddFromProject(OneTextCharset charset)
        {
            var found = new StringBuilder();
            var sizes = new HashSet<float>();
            int labels = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab == null) continue;
                foreach (var label in prefab.GetComponentsInChildren<OneTextLabel>(true))
                {
                    found.Append(label.Text);
                    sizes.Add(label.FontSize);
                    labels++;
                }
            }

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.loadedSceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var label in root.GetComponentsInChildren<OneTextLabel>(true))
                    {
                        found.Append(label.Text);
                        sizes.Add(label.FontSize);
                        labels++;
                    }
                }
            }

            Undo.RecordObject(charset, "Scan project labels");
            charset.Characters = Merge(charset.Characters, found.ToString());
            foreach (float size in sizes)
                if (!charset.Sizes.Contains(size)) charset.Sizes.Add(size);
            EditorUtility.SetDirty(charset);
            Debug.Log($"OneText: scanned {labels} label(s); charset now holds " +
                $"{charset.Codepoints().Count:n0} characters.", charset);
        }

        /// <summary>Union of two character sets, sorted, without duplicates or whitespace.</summary>
        private static string Merge(string a, string b)
        {
            var seen = new SortedSet<int>();
            foreach (string source in new[] { a, b })
            {
                if (string.IsNullOrEmpty(source)) continue;
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                    if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                    {
                        seen.Add(char.ConvertToUtf32(c, source[i + 1]));
                        i++;
                    }
                    else seen.Add(c);
                }
            }

            var builder = new StringBuilder(seen.Count);
            foreach (int cp in seen) builder.Append(char.ConvertFromUtf32(cp));
            return builder.ToString();
        }
    }

    /// <summary>Menu entries for turning a play session into a charset asset.</summary>
    public static class CharsetRecorderMenu
    {
        [MenuItem("Assets/OneText/Save Recorded Charset", false, 1202)]
        private static void SaveRecorded()
        {
            if (CharsetRecorder.CodepointCount == 0)
            {
                EditorUtility.DisplayDialog("Nothing recorded",
                    "No characters have been recorded yet. Turn on 'Record Charset In Play Mode' in " +
                    "Project Settings > OneText, play the game, then save.", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save recorded charset",
                "RecordedCharset", "asset", "Where should the charset asset go?");
            if (string.IsNullOrEmpty(path)) return;

            var charset = ScriptableObject.CreateInstance<OneTextCharset>();
            charset.Characters = CharsetRecorder.CharactersAsString();
            charset.Sizes.Clear();
            charset.Sizes.AddRange(CharsetRecorder.SizesSorted());
            AssetDatabase.CreateAsset(charset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = charset;
            Debug.Log($"OneText: saved {CharsetRecorder.CodepointCount:n0} recorded characters " +
                $"at {charset.Sizes.Count} size(s) to {path}.", charset);
        }
    }
}
