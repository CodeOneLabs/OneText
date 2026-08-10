using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// The inspector for a font asset.
    ///
    /// Without one, Unity drew the three serialized fields it can see — family
    /// name, source path and language — as three text boxes, and two of those
    /// invited exactly the wrong thing. The source path is provenance: the file
    /// was read at import and its bytes live inside the asset, so typing a
    /// different path there changed a label and nothing else, and the font
    /// stayed what it was. The language is a BCP 47 tag whose only readers are
    /// Han, kana and Hangul, so an empty box asking for a string had people
    /// filling it in for Latin faces and expecting something to happen.
    ///
    /// Both are now the thing people were reaching for: a file picker that
    /// actually replaces the font, and a menu of the tags that do something.
    /// </summary>
    [CustomEditor(typeof(OneFontAsset))]
    public sealed class OneFontAssetEditor : UnityEditor.Editor
    {
        private SerializedProperty _familyName;
        private SerializedProperty _sourcePath;
        private SerializedProperty _language;

        // Whether the face has any of the characters its language tag would
        // decide between. Worked out once per selection: it parses the font,
        // which is not something to do every repaint.
        private bool _probed;
        private bool _hasIdeographs;

        // Remembered, because "Other" is a state the tag itself cannot hold:
        // choosing it on an untagged font leaves the tag empty, and working the
        // menu selection back out of the value on the next repaint would close
        // the box under the cursor of the person about to type in it.
        private bool _typingCustomTag;

        private void OnEnable()
        {
            _familyName = serializedObject.FindProperty("_familyName");
            _sourcePath = serializedObject.FindProperty("_sourcePath");
            _language = serializedObject.FindProperty("_language");
            _probed = false;
            _typingCustomTag = false;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var font = (OneFontAsset)target;
            bool single = targets.Length == 1;

            EditorGUILayout.PropertyField(_familyName, new GUIContent("Family name",
                "Read from the font's name table at import. What the Hub and the " +
                "diagnostics call this face."));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Font file", EditorStyles.boldLabel);
            DrawFontFile(font, single);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Language", EditorStyles.boldLabel);
            DrawLanguage(font, single);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawFontFile(OneFontAsset font, bool single)
        {
            if (font.IsPlaceholder)
            {
                string expected = font.Recovery.ExpectedFileName;
                EditorGUILayout.HelpBox(
                    "This asset is a placeholder the TextMesh Pro migration left behind. It is " +
                    "waiting for " +
                    (string.IsNullOrEmpty(expected) ? "its source .ttf/.otf" : expected) +
                    ", and every label pointing at it draws in the project default until the " +
                    "file arrives. Nothing else needs redoing: the file is the only thing missing.",
                    MessageType.Warning);
            }
            else
            {
                // The path the bytes came from, shown as the record it is. It is
                // not an input: the font is inside the asset, and editing this
                // used to look like a way to point it somewhere else.
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(new GUIContent("Imported from",
                        "Where the file was read at import time. The font itself is stored " +
                        "inside this asset; this is a record of where it came from."),
                        _sourcePath.hasMultipleDifferentValues ? "(several)"
                            : string.IsNullOrEmpty(_sourcePath.stringValue) ? "(unknown)"
                            : _sourcePath.stringValue);

                if (single) DrawSizes(font);
            }

            if (!single)
            {
                EditorGUILayout.HelpBox(
                    "Choosing a file replaces the font in every selected asset with the same " +
                    "one, which is almost never what a multiple selection wants. Select one.",
                    MessageType.None);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(font.IsPlaceholder ? "Choose the font file…" : "Replace…"))
                    PickFontFile(font);

                using (new EditorGUI.DisabledScope(!CanPing(font)))
                    if (GUILayout.Button("Show in project", GUILayout.Width(120f)))
                        Ping(font);
            }
        }

        private static void DrawSizes(OneFontAsset font)
        {
            int file = font.FontFileSize;
            int stored = font.StoredSize;
            string packed = stored > 0 && file > 0
                ? $"{Kilobytes(file)} font, {Kilobytes(stored)} stored ({stored / (float)file:P0})"
                : $"{Kilobytes(file)} font";
            EditorGUILayout.LabelField(" ", $"{packed}, packed for " +
                (font.Packing == OneFontAsset.FontPacking.Smallest ? "size" : "import speed"),
                EditorStyles.miniLabel);
        }

        private static string Kilobytes(int bytes) =>
            bytes >= 1024 * 1024
                ? $"{bytes / (1024f * 1024f):0.#} MB"
                : $"{bytes / 1024f:n0} KB";

        private void DrawLanguage(OneFontAsset font, bool single)
        {
            string current = _language.stringValue ?? "";
            int at = FontLanguages.IndexOf(current);
            bool custom = _typingCustomTag || at < 0;

            var labels = new string[FontLanguages.Choices.Length + 1];
            for (int i = 0; i < FontLanguages.Choices.Length; i++)
                labels[i] = FontLanguages.Choices[i].Label;
            int otherIndex = labels.Length - 1;
            labels[otherIndex] = custom ? $"Other — {current}" : "Other…";

            var content = new GUIContent("Designed for",
                "Which reader this face is cut for. 直 is one character with a different " +
                "correct shape in Japanese and Chinese, and this is what decides which one a " +
                "reader gets when several fonts in the chain cover it. Leave it at Any unless " +
                "the face is a CJK face.");

            EditorGUI.showMixedValue = _language.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.Popup(content, custom ? otherIndex : at, labels);
            if (EditorGUI.EndChangeCheck())
            {
                // Picking Other leaves the tag alone and opens the box: the
                // choice is "I will type one", not "clear it".
                _typingCustomTag = picked == otherIndex;
                if (!_typingCustomTag) _language.stringValue = FontLanguages.Choices[picked].Tag;
                custom = _typingCustomTag;
            }
            EditorGUI.showMixedValue = false;

            if (custom)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_language, new GUIContent("Tag",
                    "A BCP 47 tag. Matching is by prefix, so a font tagged zh serves a label " +
                    "asking for zh-Hans."));
                EditorGUI.indentLevel--;
            }

            if (single) DrawLanguageNote(font, _language.stringValue);
        }

        private void DrawLanguageNote(OneFontAsset font, string tag)
        {
            if (!Probe(font)) return;

            if (_hasIdeographs)
            {
                if (string.IsNullOrEmpty(tag))
                {
                    EditorGUILayout.HelpBox(
                        "This face has Han, kana or Hangul in it and is not tagged. With more " +
                        "than one such font in the chain, which of them draws a shared " +
                        "character is decided by the order they are listed in, which is how a " +
                        "Japanese player ends up reading Chinese glyph shapes.",
                        MessageType.Info);
                }
                return;
            }

            if (!string.IsNullOrEmpty(tag))
            {
                EditorGUILayout.HelpBox(
                    "This face has no Han, kana or Hangul in it, and the tag is read for " +
                    "nothing else — it changes nothing here. That is not a mistake worth " +
                    "undoing, just a setting that will not do anything.",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Whether the face covers any of the characters the tag arbitrates,
        /// or false when there is no face to ask. Sampled rather than scanned:
        /// one Han ideograph, one kana and one Hangul syllable settle it, and
        /// walking a CJK cmap in OnInspectorGUI would not.
        /// </summary>
        private bool Probe(OneFontAsset font)
        {
            if (_probed) return true;
            // Never on a placeholder: asking it for a face is what makes it
            // borrow the project default and warn about it, and an inspector
            // being drawn is not a reason to say that.
            if (font.IsPlaceholder || font.FontFileSize == 0) return false;

            var face = font.Font;
            if (face == null || !face.IsValid) return false;

            _hasIdeographs = face.HasGlyph('一') || face.HasGlyph('あ') || face.HasGlyph('가');
            _probed = true;
            return true;
        }

        private void PickFontFile(OneFontAsset font)
        {
            string start = StartingFolder(font);
            string picked = EditorUtility.OpenFilePanel(
                font.IsPlaceholder ? $"The font file for {font.FamilyName}" : "A .ttf, .otf or .ttc",
                start, "ttf,otf,ttc");
            if (string.IsNullOrEmpty(picked)) return;

            Undo.RecordObject(font, font.IsPlaceholder ? "Fill font asset" : "Replace font");
            if (!OneFontAssetCreator.FillFromFontFile(font, TextSourceScanner.ToProjectPath(picked)))
                return;

            AssetDatabase.SaveAssets();
            _probed = false;
            serializedObject.Update();
        }

        /// <summary>
        /// Where the file panel opens: beside the asset, which is where a font
        /// usually sits, falling back to the project.
        /// </summary>
        private static string StartingFolder(OneFontAsset font)
        {
            string assetPath = AssetDatabase.GetAssetPath(font);
            string folder = string.IsNullOrEmpty(assetPath)
                ? "Assets"
                : Path.GetDirectoryName(assetPath);
            return string.IsNullOrEmpty(folder) ? "Assets" : folder;
        }

        private static bool CanPing(OneFontAsset font) =>
            !string.IsNullOrEmpty(font.SourcePath) &&
            AssetDatabase.LoadMainAssetAtPath(font.SourcePath) != null;

        private static void Ping(OneFontAsset font)
        {
            var file = AssetDatabase.LoadMainAssetAtPath(font.SourcePath);
            if (file == null) return;
            EditorGUIUtility.PingObject(file);
            Selection.activeObject = file;
        }
    }
}
