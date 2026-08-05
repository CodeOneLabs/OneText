using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Every font in the project, what it costs, and the one field that cannot
    /// be inferred: the language it is designed for.
    ///
    /// The language tag is here rather than buried in the asset inspector
    /// because it is the fix for the failure nobody sees — Han is unified in
    /// Unicode and not in type, so an untagged chain gives Japanese and Chinese
    /// readers whichever face was listed first. Doctor reports it; this is
    /// where it gets set.
    /// </summary>
    public sealed class HubFontsTab
    {
        private static readonly string[] LanguageSuggestions =
            { "", "ja", "zh-Hans", "zh-Hant", "ko", "th", "ar", "hi" };

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Fonts",
                "Fonts the project has imported. The chain a label gets by default is the project " +
                "font followed by its fallbacks, set in Project Settings > OneText.");

            var settings = OneTextSettings.Instance;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Project default",
                    settings == null ? "no settings asset"
                    : settings.DefaultFont == null ? "none"
                    : settings.DefaultFont.FamilyName);
                if (GUILayout.Button("Project Settings", GUILayout.Width(130f)))
                    SettingsService.OpenProjectSettings("Project/OneText");
            }

            EditorGUILayout.Space();
            var fonts = OneTextHub.AllFonts();
            if (fonts.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No font assets yet. Right-click a .ttf or .otf in the Project window and choose " +
                    "OneText > Create Font Asset.", MessageType.Info);
                return;
            }

            foreach (var font in fonts) DrawFont(font);
        }

        private static void DrawFont(OneFontAsset font)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(font.FamilyName, EditorStyles.boldLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(60f)))
                        Selection.activeObject = font;
                }

                EditorGUILayout.LabelField(
                    $"{font.FontFileSize / 1024f:n0} KB font file, {font.StoredSize / 1024f:n0} KB stored" +
                    (font.FontFileSize > 0
                        ? $" ({font.StoredSize / (float)font.FontFileSize:P0})"
                        : ""),
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    string language = EditorGUILayout.TextField(
                        new GUIContent("Language",
                            "ja, zh-Hans, zh-Hant, ko — only meaningful for the unified scripts."),
                        font.Language ?? "");
                    if (language != (font.Language ?? ""))
                    {
                        Undo.RecordObject(font, "Set font language");
                        font.Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
                        EditorUtility.SetDirty(font);
                    }

                    if (GUILayout.Button("v", GUILayout.Width(22f)))
                        ShowLanguageMenu(font);
                }
            }
        }

        private static void ShowLanguageMenu(OneFontAsset font)
        {
            var menu = new GenericMenu();
            foreach (string suggestion in LanguageSuggestions)
            {
                string captured = suggestion;
                menu.AddItem(new GUIContent(suggestion.Length == 0 ? "(none)" : suggestion),
                    (font.Language ?? "") == suggestion, () =>
                    {
                        Undo.RecordObject(font, "Set font language");
                        font.Language = captured.Length == 0 ? null : captured;
                        EditorUtility.SetDirty(font);
                    });
            }
            menu.ShowAsContext();
        }
    }

    /// <summary>
    /// Named styles, with a line of real text in each so choosing one is a
    /// matter of looking rather than reading a font name.
    /// </summary>
    public sealed class HubStylesTab
    {
        private string _sample = "The quick brown fox 다람쥐 헌 쳇바퀴 1234";

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Styles",
                "Named styles a label or a <style> tag can refer to. A style that sets a font " +
                "swaps the typeface everywhere it is used, which is how a localised build changes " +
                "type without touching a prefab.");

            _sample = EditorGUILayout.TextField("Sample", _sample);
            EditorGUILayout.Space();

            var styles = OneTextHub.AllStyles();
            if (styles.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No styles yet. Assets > Create > OneText > Style.", MessageType.Info);
                return;
            }

            foreach (var style in styles)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(style.name, EditorStyles.boldLabel);
                        if (GUILayout.Button("Select", GUILayout.Width(60f)))
                            Selection.activeObject = style;
                        if (GUILayout.Button("Preview in gallery", GUILayout.Width(130f)))
                            OneTextHub.Open(OneTextHub.Tab.Gallery);
                    }
                    EditorGUILayout.LabelField(Describe(style), EditorStyles.miniLabel);
                }
            }
        }

        private static string Describe(OneTextStyle style)
        {
            var parts = new List<string>();
            if (style.Sets(OneTextStyle.Fields.Font) && style.Font != null)
                parts.Add(style.Font.FamilyName);
            if (style.Sets(OneTextStyle.Fields.Size)) parts.Add($"{style.FontSize:0.#} px");
            if (style.Sets(OneTextStyle.Fields.Weight) && style.Bold) parts.Add("bold");
            if (style.Sets(OneTextStyle.Fields.Slant) && style.Italic) parts.Add("italic");
            if (style.Sets(OneTextStyle.Fields.LineSpacing)) parts.Add($"line {style.LineSpacing:0.##}");
            if (style.Extends != null) parts.Add($"extends {style.Extends.name}");
            return parts.Count == 0 ? "sets nothing" : string.Join("   ", parts);
        }
    }
}
