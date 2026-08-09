using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Every font in the project, what it costs, and the one field that cannot
    /// be inferred: the language it is designed for.
    ///
    /// The language tag is here rather than buried in the asset inspector
    /// because it is the fix for the failure nobody sees: Han is unified in
    /// Unicode and not in type, so an untagged chain gives Japanese and Chinese
    /// readers whichever face was listed first. Doctor reports it; this is
    /// where it gets set.
    /// </summary>
    public sealed class HubFontsTab : HubSection
    {
        private static readonly string[] LanguageSuggestions =
            { "", "ja", "zh-Hans", "zh-Hant", "ko", "th", "ar", "hi" };

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Fonts;

        public override string Title => "Fonts";

        public override string Eyebrow => "Type";

        public override string Lede =>
            "The faces this project can draw with, what they cost, and which language each one " +
            "is cut for.";

        public override string NavHint => "faces and language tags";

        public override string NavGroup => "Type";

        public override string BadgeText
        {
            get
            {
                int count = FontCount();
                return count == 0 ? "0" : count.ToString("n0");
            }
        }

        public override HubTone BadgeTone =>
            FontCount() == 0 ? HubTone.Warn : HubTone.Neutral;

        protected override void Compose(VisualElement content)
        {
            content.Add(DefaultCard());

            var fonts = AllFonts();
            if (fonts.Count == 0)
            {
                var card = HubUI.MakeCard("No fonts yet",
                    "Nothing in this project can draw a character until one is imported.");
                card.Add(HubUI.Empty("Import your first font",
                    "Drop a .ttf, .otf or .ttc under Assets, then pick it here. OneText stores a " +
                    "compressed copy on the asset, so the font travels with the project.",
                    "Choose a font file…", ImportFont, "A"));
                content.Add(card.Root);
                return;
            }

            foreach (var font in fonts) content.Add(FontCard(font));

            var more = HubUI.MakeCard("Another font",
                "Fallbacks are ordinary font assets too; the chain is set in Global Settings.");
            more.Add(HubUI.Ghost("Choose a font file…", ImportFont));
            content.Add(more.Root);
        }

        private VisualElement DefaultCard()
        {
            var settings = OneTextSettings.Instance;
            var card = HubUI.MakeCard("Project default",
                "What a label draws with when it has no font of its own, followed by its fallbacks.");
            card.Act(HubUI.Ghost("Global settings",
                () => OneTextHub.Open(OneTextHub.Tab.Settings)));

            if (settings == null)
            {
                card.Add(HubUI.Notice(
                    "This project has no OneText settings asset, so there is no default font and " +
                    "no fallback chain. Open Global Settings to make one.", HubTone.Warn));
                return card.Root;
            }

            if (settings.DefaultFont == null)
            {
                card.Add(HubUI.Notice(
                    "No default font. Labels with no font of their own will draw nothing.",
                    HubTone.Warn));
                return card.Root;
            }

            card.Add(HubUI.KeyValue("Default", settings.DefaultFont.FamilyName, HubTone.Good));
            var chain = new List<string>();
            foreach (var fallback in settings.FallbackFonts)
                if (fallback != null) chain.Add(fallback.FamilyName);
            card.Add(HubUI.KeyValue("Fallbacks",
                chain.Count == 0 ? "none" : string.Join("  →  ", chain),
                chain.Count == 0 ? HubTone.Warn : HubTone.Neutral));
            card.Add(SystemTier(settings));
            return card.Root;
        }

        /// <summary>
        /// The last tier of the chain, which is not a font asset at all.
        ///
        /// When nothing the project ships covers a character, the fonts of the
        /// device running the game are asked — in the build, on the player's
        /// phone or PC, not just in this editor. That is a better outcome than a
        /// box and a worse one than shipping the face: the glyph comes from
        /// whatever that particular device happens to have, so two devices can
        /// draw the same string differently. Putting it at the end of the chain
        /// is where a reader would look for it, and Doctor's `system-fallback`
        /// rule says which characters are relying on it.
        /// </summary>
        private static VisualElement SystemTier(OneTextSettings settings)
        {
            bool on = settings.SystemFontFallback;
            var row = HubUI.KeyValue("System fonts",
                on
                    ? "Characters no font above covers are drawn from the player's own device " +
                      "fonts at runtime, which another device may not have. Doctor lists them."
                    : "A character no font above covers draws a box.",
                on ? HubTone.Neutral : HubTone.Warn);
            var badge = HubUI.Badge(on ? "OS FALLBACK ON" : "OFF",
                on ? HubTone.Info : HubTone.Warn);
            badge.style.marginRight = 10f;
            row.Insert(1, badge);

            var change = HubUI.Quiet("Change",
                () => OneTextHub.Open(OneTextHub.Tab.Settings));
            change.style.marginLeft = 10f;
            row.Add(change);
            return row;
        }

        private VisualElement FontCard(OneFontAsset font)
        {
            string cost = $"{font.FontFileSize / 1024f:n0} KB font file · " +
                $"{font.StoredSize / 1024f:n0} KB stored" +
                (font.FontFileSize > 0
                    ? $" ({HubUI.Percent(font.StoredSize / (float)font.FontFileSize)})"
                    : "");

            var card = HubUI.MakeCard(font.FamilyName, cost);
            card.Act(HubUI.Ghost("Select", () =>
            {
                Selection.activeObject = font;
                EditorGUIUtility.PingObject(font);
            }));

            var field = HubUI.Input(font.Language ?? "");
            field.isDelayed = true;
            field.RegisterValueChangedCallback(evt =>
            {
                SetLanguage(font, evt.newValue);
                Say($"{font.FamilyName} is now tagged " +
                    (string.IsNullOrWhiteSpace(evt.newValue) ? "with no language." : evt.newValue));
            });

            var control = HubUI.Box("field__control");
            control.Add(field);
            var suggest = HubUI.Quiet("Suggest…", () => ShowLanguageMenu(font, field));
            suggest.style.marginLeft = 8f;
            control.Add(suggest);

            var row = HubUI.Box("field");
            row.Add(HubUI.Text("Language", "field__label"));
            row.Add(control);
            card.Add(row);

            card.Add(HubUI.Text(
                "Only meaningful for the scripts Unicode unified: ja, zh-Hans, zh-Hant, ko. An " +
                "untagged Han font gives Japanese readers Chinese shapes and nobody files a bug.",
                "hint"));

            card.Add(PackingRow(font));
            return card.Root;
        }

        /// <summary>
        /// How hard this font is packed, and the button that spends a minute to
        /// pack it harder.
        ///
        /// Importing packs fast, because packing a 16 MB Korean face as small
        /// as brotli goes froze the editor for over a minute and 15 % of a font
        /// is not worth that on every drag-and-drop. It is worth it once, on
        /// the build that ships, and this is where that once happens.
        /// </summary>
        private VisualElement PackingRow(OneFontAsset font)
        {
            bool smallest = font.Packing == OneFontAsset.FontPacking.Smallest;
            var row = HubUI.KeyValue("Packing", smallest
                    ? "as small as it goes"
                    : "packed for a fast import",
                smallest ? HubTone.Good : HubTone.Neutral);
            if (smallest) return row;

            var button = HubUI.Quiet("Pack smaller", () => PackSmaller(font));
            button.style.marginLeft = 10f;
            row.Add(button);
            return row;
        }

        private void PackSmaller(OneFontAsset font)
        {
            long before = font.StoredSize;
            bool done;
            try
            {
                EditorUtility.DisplayProgressBar("OneText",
                    $"Packing {font.FamilyName} as small as it goes — this takes a while…", 0.5f);
                done = font.Repack(OneFontAsset.FontPacking.Smallest);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!done)
            {
                SayBadly($"{font.FamilyName} has no font file to pack.");
                return;
            }

            EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            Refresh();
            Say($"{font.FamilyName}: {before / 1024f:n0} KB → {font.StoredSize / 1024f:n0} KB.");
        }

        private void ShowLanguageMenu(OneFontAsset font, TextField field)
        {
            var menu = new GenericMenu();
            foreach (string suggestion in LanguageSuggestions)
            {
                string captured = suggestion;
                menu.AddItem(new GUIContent(suggestion.Length == 0 ? "(none)" : suggestion),
                    (font.Language ?? "") == suggestion, () =>
                    {
                        SetLanguage(font, captured);
                        field.SetValueWithoutNotify(captured);
                        Say($"{font.FamilyName} is now tagged " +
                            (captured.Length == 0 ? "with no language." : captured));
                    });
            }
            menu.ShowAsContext();
        }

        private static void SetLanguage(OneFontAsset font, string language)
        {
            Undo.RecordObject(font, "Set font language");
            font.Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
            EditorUtility.SetDirty(font);
        }

        private void ImportFont()
        {
            string picked = EditorUtility.OpenFilePanel(
                "A .ttf, .otf or .ttc inside this project", "Assets", "ttf,otf,ttc");
            if (string.IsNullOrEmpty(picked)) return;

            string relative = TextSourceScanner.ToProjectPath(picked);
            if (!relative.StartsWith("Assets", System.StringComparison.Ordinal))
            {
                SayBadly($"{Path.GetFileName(picked)} is outside this project. Copy it under " +
                    "Assets first; the font asset is stored beside the file it was made from.");
                return;
            }

            var asset = OneFontAssetCreator.CreateFromFontFile(relative);
            if (asset == null)
            {
                SayBadly($"Could not read {Path.GetFileName(picked)}.");
                return;
            }

            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            Refresh();
            Say($"Imported {asset.FamilyName}.");
        }
    }

    /// <summary>
    /// Named styles, with what each one actually sets spelled out, so choosing
    /// between them is a matter of reading one line rather than opening five
    /// inspectors.
    /// </summary>
    public sealed class HubStylesTab : HubSection
    {
        private string _sample = "The quick brown fox 다람쥐 헌 쳇바퀴 1234";

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Styles;

        public override string Title => "Styles";

        public override string Eyebrow => "Type";

        public override string Lede =>
            "Named looks a label or a <style> tag can refer to. A style that sets a font swaps " +
            "the typeface everywhere it is used, which is how a localised build changes type " +
            "without touching a prefab.";

        public override string NavHint => "reusable looks";

        public override string NavGroup => "Type";

        public override string BadgeText
        {
            get
            {
                int count = StyleCount();
                return count == 0 ? null : count.ToString("n0");
            }
        }

        protected override void Compose(VisualElement content)
        {
            var styles = AllStyles();
            if (styles.Count == 0)
            {
                var empty = HubUI.MakeCard("No styles yet",
                    "A project works without them; a localised one usually stops wanting to.");
                empty.Add(HubUI.Empty("Make your first style",
                    "A style is a font, a size, a weight, named once and referred to everywhere. " +
                    "Change the style and every label wearing it changes with it.",
                    "Create a style…", CreateStyle, "S"));
                content.Add(empty.Root);
                return;
            }

            var card = HubUI.MakeCard("Styles in this project",
                $"{styles.Count:n0} style(s). Click one to select it in the Project window.").Flush();
            card.Act(HubUI.Ghost("New style…", CreateStyle));

            foreach (var style in styles)
            {
                var captured = style;
                var row = HubUI.Box("style-row");
                row.Add(HubUI.Text(style.name, "style-row__name"));
                var describe = HubUI.Text(Describe(style), "kv__value");
                HubUI.Mono(describe);
                row.Add(describe);
                row.Add(HubUI.Quiet("Select", () =>
                {
                    Selection.activeObject = captured;
                    EditorGUIUtility.PingObject(captured);
                }));
                card.Add(row);
            }
            content.Add(card.Root);

            var sample = HubUI.MakeCard("Preview",
                "See them drawn: the gallery lays one string out in every style and every font " +
                "at once.");
            sample.Act(HubUI.Primary("Open the gallery", () => Hub.Go(OneTextHub.Tab.Gallery)));
            var field = HubUI.Input(_sample, null, value => _sample = value);
            sample.Add(HubUI.Field("Sample text", field));
            content.Add(sample.Root);
        }

        private void CreateStyle()
        {
            var style = CreateAsset<OneTextStyle>("New Text Style", "Where should the style go?");
            if (style == null) return;
            Refresh();
            Say($"Created {style.name}. Its fields are in the Inspector.");
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
            return parts.Count == 0 ? "sets nothing" : string.Join("   ·   ", parts);
        }
    }
}
