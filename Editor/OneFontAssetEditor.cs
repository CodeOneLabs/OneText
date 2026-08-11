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
        private SerializedProperty _letterSpacing;
        private SerializedProperty _bold;

        // Whether the face has any of the characters its language tag would
        // decide between. Worked out once per selection: it parses the font,
        // which is not something to do every repaint.
        private bool _probed;
        private bool _hasIdeographs;
        private bool _hasWeightAxis;

        // Which family, if any, names this asset as its bold. Worked out once
        // per selection: it walks the project's font assets.
        private bool _boldOfProbed;
        private string _boldOf;

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
            _letterSpacing = serializedObject.FindProperty("_letterSpacingEm");
            _bold = serializedObject.FindProperty("_bold");
            _probed = false;
            _boldOfProbed = false;
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
            EditorGUILayout.LabelField("Weight", EditorStyles.boldLabel);
            DrawBold(font, single);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Language", EditorStyles.boldLabel);
            DrawLanguage(font, single);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Spacing", EditorStyles.boldLabel);
            DrawLetterSpacing();

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

        /// <summary>
        /// The correction for a face whose own tracking reads wrong.
        ///
        /// It belongs here rather than on the label because the defect belongs
        /// to the face: spacing set on a label or a style is applied to every
        /// face on the line, so a correction for a Latin font also spreads the
        /// CJK fallback that drew the middle of the sentence. Setting it here
        /// also cannot be forgotten on the next label somebody makes.
        /// </summary>
        private void DrawLetterSpacing()
        {
            EditorGUILayout.PropertyField(_letterSpacing, new GUIContent("Letter spacing",
                "Ems added between characters of this face wherever nothing more specific has " +
                "an opinion. For a face that ships too tight or too loose; 0.02 is a small " +
                "opening, -0.01 a small tightening. <cspace> markup, a style asset and a " +
                "label's base style all override it, including when they ask for 0."));

            if (Mathf.Abs(_letterSpacing.floatValue) > 0.25f && !_letterSpacing.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "That is a quarter of an em per character, which is a design decision " +
                    "rather than a correction. Nothing stops it, but a value this size belongs " +
                    "on a style asset, where it applies to the text that wants it instead of to " +
                    "everything this font ever draws.",
                    MessageType.Info);
            }
        }

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
        /// Where <c>&lt;b&gt;</c> gets its bold from, and what happens when
        /// nothing here can give it one.
        ///
        /// Three outcomes and each needs a different sentence, because the
        /// reader's next action differs: a variable font needs nothing done, a
        /// static font with a bold assigned is finished, and a static font
        /// without one is about to have its weight faked and the person looking
        /// at this screen is the only one who can go and find the file.
        /// </summary>
        private void DrawBold(OneFontAsset font, bool single)
        {
            if (!single)
            {
                EditorGUILayout.PropertyField(_bold, new GUIContent("Bold face"));
                return;
            }

            // The other side of the relationship, and the reason this is asked
            // at all: an asset that IS somebody's bold has no bold of its own,
            // has no use for a slot, and must not be told it is about to have
            // its weight faked. Without this the pair reads as an infinite
            // regress — a bold whose bold is empty, warning about it.
            string owner = BoldOf(font);
            if (owner != null)
            {
                EditorGUILayout.HelpBox(
                    $"This is the bold face of {owner}. <b> on a label using that font finds this " +
                    "one, and a face used as a bold needs no bold of its own.",
                    MessageType.Info);
                return;
            }

            if (Probe(font) && _hasWeightAxis)
            {
                EditorGUILayout.HelpBox(
                    "This font is variable and has a weight axis, so <b> interpolates a real bold " +
                    "out of this one file. Nothing to assign — and a bold assigned here would be " +
                    "preferred over the interpolated one, which is the worse of the two answers.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.PropertyField(_bold, new GUIContent("Bold face",
                "The designed bold of this family, as its own OneText font asset. A static font " +
                "has no weight axis to move, so this is the only way <b> can reach a real bold."));

            // A placeholder has no face to have a weight axis or not have one,
            // and it already says the thing worth saying: it is waiting for its
            // file. Stacking a second warning on it would be answering a
            // question nobody can act on yet.
            if (_bold.objectReferenceValue != null || font.IsPlaceholder) return;

            // Nearly always sitting next to the regular, because that is how
            // font families are shipped and how they were imported. Offering it
            // by name turns "go and make an asset, then come back and find this
            // field" into one button, which is the difference between a project
            // that has real bold and one that meant to.
            string sibling = SiblingBold(font);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (sibling != null &&
                    GUILayout.Button($"Use {Path.GetFileName(sibling)}"))
                {
                    AssignBold(font, sibling);
                    return;
                }
                if (GUILayout.Button(sibling != null ? "Another file…" : "Create from font file…"))
                {
                    string picked = EditorUtility.OpenFilePanel(
                        $"The bold of {font.FamilyName}", StartingFolder(font), "ttf,otf,ttc");
                    string inside = IntoProject(picked, font);
                    if (inside != null)
                    {
                        AssignBold(font, inside);
                        return;
                    }
                }
            }

            // The state this whole field exists for, and it used to be silent:
            // <b> resolved to the regular face and the label simply came out
            // unbold, with nothing anywhere saying why.
            EditorGUILayout.HelpBox(
                "No bold face, and this font has no weight axis to interpolate one from — so <b> " +
                "is drawn by thickening this face. That reads as bold on Latin and closes up " +
                "counters on Hangul and Han, which is what a faked weight does. Point this at the " +
                "family's real bold and it will be used instead.",
                MessageType.Warning);
        }

        /// <summary>
        /// Makes a font asset out of a bold font file — or finds the one already
        /// beside it — and assigns it.
        ///
        /// The same call the right-click menu makes, so a bold created this way
        /// is indistinguishable from one created that way, and running this
        /// twice on the same file assigns the same asset rather than making a
        /// second one.
        /// </summary>
        private void AssignBold(OneFontAsset font, string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath)) return;

            var bold = OneFontAssetCreator.CreateFromFontFile(fontPath);
            if (bold == null)
            {
                Debug.LogError($"OneText: {fontPath} could not be read as a font.");
                return;
            }
            if (bold == font)
            {
                Debug.LogError("OneText: that is this font's own file, so it is not its bold.");
                return;
            }

            AssetDatabase.SaveAssets();
            _bold.objectReferenceValue = bold;
            serializedObject.ApplyModifiedProperties();
            _boldOfProbed = false;
        }

        /// <summary>
        /// The bold of this family sitting beside it on disk, or null.
        ///
        /// Matched by name and strictly: the stem with its punctuation stripped
        /// has to be this font's stem with "bold" on the end. Loose matching is
        /// how a family gets handed its SemiBold and nobody notices until the
        /// text looks slightly wrong everywhere, so SemiBold, ExtraBold and
        /// BoldItalic are all left for the file panel to choose deliberately.
        /// </summary>
        private static string SiblingBold(OneFontAsset font)
        {
            string source = font.SourcePath;
            if (string.IsNullOrEmpty(source)) return null;

            string folder = Path.GetDirectoryName(source);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            string wanted = Normalize(Path.GetFileNameWithoutExtension(source)) + "bold";
            foreach (string path in Directory.GetFiles(folder))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".ttf" && extension != ".otf" && extension != ".ttc") continue;
                if (Normalize(Path.GetFileNameWithoutExtension(path)) != wanted) continue;
                return path.Replace('\\', '/');
            }
            return null;
        }

        /// <summary>Lower-cased letters and digits only, so separators stop mattering.</summary>
        private static string Normalize(string name)
        {
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            return builder.ToString();
        }

        /// <summary>
        /// The name of the font asset that names this one as its bold, or null.
        ///
        /// Asked of the asset graph rather than stored on the asset, because a
        /// stored role is a second copy of a fact the references already carry:
        /// unassign the bold and the stamp is stale, assign it twice and both
        /// stamps claim it. The graph cannot be wrong about this.
        ///
        /// Cheap enough to do per selection. A OneFontAsset referencing another
        /// OneFontAsset happens in exactly one field, so a plain dependency
        /// answers the question without loading anybody's font bytes, and the
        /// candidate set is the font assets in a project — tens, not thousands.
        /// </summary>
        private string BoldOf(OneFontAsset font)
        {
            if (_boldOfProbed) return _boldOf;
            _boldOfProbed = true;
            _boldOf = null;

            string path = AssetDatabase.GetAssetPath(font);
            if (string.IsNullOrEmpty(path)) return null;

            foreach (string guid in AssetDatabase.FindAssets("t:OneFontAsset"))
            {
                string other = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(other) || other == path) continue;

                bool names = false;
                foreach (string dependency in AssetDatabase.GetDependencies(other, false))
                    if (dependency == path) { names = true; break; }
                if (!names) continue;

                // Loaded only for the one asset that turned out to point here,
                // and only to be sure it is the bold slot doing the pointing
                // rather than something added later.
                var owner = AssetDatabase.LoadAssetAtPath<OneFontAsset>(other);
                if (owner == null || owner.Bold != font) continue;

                _boldOf = string.IsNullOrEmpty(owner.FamilyName)
                    ? System.IO.Path.GetFileNameWithoutExtension(other)
                    : owner.FamilyName;
                break;
            }
            return _boldOf;
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

            // Variable is not enough on its own: a font may vary its width or
            // its optical size and have no weight to move, and telling somebody
            // with one of those that <b> is taken care of would be wrong in the
            // direction that ends in an unbold label.
            _hasWeightAxis = false;
            if (face.IsVariable)
            {
                foreach (var axis in face.GetVariationAxes())
                    if (axis.Tag == "wght") { _hasWeightAxis = true; break; }
            }

            _probed = true;
            return true;
        }

        private void PickFontFile(OneFontAsset font)
        {
            string start = StartingFolder(font);
            string picked = EditorUtility.OpenFilePanel(
                font.IsPlaceholder ? $"The font file for {font.FamilyName}" : "A .ttf, .otf or .ttc",
                start, "ttf,otf,ttc");
            string inside = IntoProject(picked, font);
            if (inside == null) return;

            Undo.RecordObject(font, font.IsPlaceholder ? "Fill font asset" : "Replace font");
            if (!OneFontAssetCreator.FillFromFontFile(font, inside)) return;

            AssetDatabase.SaveAssets();
            _probed = false;
            serializedObject.Update();
        }

        /// <summary>
        /// Where the file panel opens: beside the asset, which is where a font
        /// usually sits, falling back to the project.
        /// </summary>
        /// <summary>
        /// A picked file as a path under Assets, copying it in when it is not
        /// one already. Null when the person changed their mind or it could not
        /// be brought in.
        ///
        /// The file panel opens on the whole disk, and a font sitting in
        /// Downloads or in ~/Library/Fonts is the ordinary case, not the odd
        /// one. Both callers used to take whatever came back and both were
        /// wrong about it in different ways: creating an asset beside a file
        /// outside the project fails at <c>AssetDatabase.CreateAsset</c>, and
        /// filling an existing asset from one <em>succeeds</em> — the bytes are
        /// read, packed and stored, and the asset is left recording a path like
        /// <c>/Users/someone/Downloads/Foo.ttf</c> as its provenance, which is
        /// nothing on anybody else's machine and breaks "Show in project" for
        /// no reason a person could work out.
        ///
        /// So it is asked and answered here, once, before either of them acts.
        /// </summary>
        private static string IntoProject(string picked, OneFontAsset beside)
        {
            if (string.IsNullOrEmpty(picked)) return null;
            if (!File.Exists(picked))
            {
                EditorUtility.DisplayDialog("OneText",
                    $"{picked} is not there any more.", "OK");
                return null;
            }

            string relative = TextSourceScanner.ToProjectPath(picked);
            if (relative.StartsWith("Assets/", System.StringComparison.Ordinal)) return relative;

            string folder = StartingFolder(beside);
            string destination = $"{folder}/{Path.GetFileName(picked)}";

            // Already here under that name: use it rather than asking to
            // overwrite. The alternative is a dialog about a file the person
            // has not been told they already have.
            if (File.Exists(destination))
            {
                AssetDatabase.ImportAsset(destination);
                return destination;
            }

            if (!EditorUtility.DisplayDialog("Copy the font into the project?",
                    $"{Path.GetFileName(picked)} is outside this project, and Unity can only " +
                    "build from files inside it.\n\nCopy it to " + folder + "?",
                    "Copy it in", "Cancel"))
                return null;

            try
            {
                File.Copy(picked, destination);
            }
            catch (System.Exception error)
            {
                Debug.LogError($"OneText: {Path.GetFileName(picked)} could not be copied to " +
                               $"{folder}: {error.Message}");
                return null;
            }

            AssetDatabase.ImportAsset(destination);
            return destination;
        }

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
