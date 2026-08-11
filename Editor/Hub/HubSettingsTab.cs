using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// The project's own answers: the font every label falls back to, what a
    /// new label is created as, and the atlas budget they all share.
    ///
    /// This is the section that used to be the whole of Project Settings &gt;
    /// OneText, drawn by the default inspector for the settings asset. Two
    /// things were wrong with that. A serialized-fields sheet says what the
    /// fields are called and never what they are for, and half of what a
    /// TextMesh Pro project expects to find on that page — the size a new label
    /// starts at, whether it takes clicks, whether it parses markup — was not
    /// on it at all: those numbers lived as C# field initializers, which is to
    /// say they were not the project's to decide.
    ///
    /// Every control here writes through a SerializedObject, so an edit is
    /// undoable and the asset is marked dirty the way the inspector would have.
    /// </summary>
    public sealed class HubSettingsTab : HubSection
    {
        private static readonly int[] TextureSizes = { 512, 1024, 2048, 4096 };

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Settings;

        public override string Title => "Global Settings";

        public override string Eyebrow => "Project";

        public override string Lede =>
            "What text in this project starts as: the font a label with none of its own draws " +
            "with, the size and behaviour a new one is created at, and the atlas they share.";

        public override string NavHint => "font, new text, atlas";

        public override string BadgeText
        {
            get
            {
                var settings = OneTextSettingsProvider.Find();
                if (settings == null) return "none";
                return settings.DefaultFont == null ? "no font" : null;
            }
        }

        public override HubTone BadgeTone => HubTone.Warn;

        private OneTextSettings _settings;

        protected override void Compose(VisualElement content)
        {
            _settings = OneTextSettingsProvider.Find();
            if (_settings == null)
            {
                content.Add(MissingCard());
                return;
            }

            content.Add(FontCard());
            content.Add(NewTextCard());
            content.Add(AtlasCard());
            content.Add(PrewarmCard());
            content.Add(WordListsCard());
            content.Add(AssetCard());
        }

        // ------------------------------------------------------------- writing

        /// <summary>
        /// One edit, applied and made visible everywhere at once.
        ///
        /// A fresh SerializedObject per edit rather than one held across the
        /// panel's lifetime: the asset can be changed by an import, a migration
        /// or another inspector between two clicks in here, and a stale
        /// SerializedObject would write the values it was built with back over
        /// all of it.
        /// </summary>
        private void Edit(string field, Action<SerializedProperty> assign,
            bool rebuildAtlas = false)
        {
            if (_settings == null) return;
            var serialized = new SerializedObject(_settings);
            assign(serialized.FindProperty(field));
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(_settings);
            OneTextSettings.Invalidate();
            // A different budget means a different texture; rebuild it now so
            // the editor shows what the game will get.
            if (rebuildAtlas) SharedGlyphAtlas.Reconfigure();
        }

        private VisualElement MissingCard()
        {
            var card = HubUI.MakeCard("No settings asset yet",
                "Until one exists, every default on this page is the package's own.");
            card.Add(HubUI.Empty("Create the settings asset",
                "One ScriptableObject at Assets/Resources/OneTextSettings.asset. It holds the " +
                "default font, the fallback chain, what a new label starts as and the atlas " +
                "budget, and it loads at runtime without any editor code.",
                "Create settings asset", () =>
                {
                    var made = OneTextSettingsProvider.GetOrCreate();
                    Selection.activeObject = made;
                    Refresh();
                    Say("Created Assets/Resources/OneTextSettings.asset. Set a default font next.");
                }, "◈"));
            return card.Root;
        }

        // --------------------------------------------------------------- fonts

        private VisualElement FontCard()
        {
            var card = HubUI.MakeCard("Default font and fallbacks",
                "The font a label with none of its own draws with, then the chain consulted for " +
                "characters it does not cover.");
            card.Act(HubUI.Ghost("All fonts", () => Hub.Go(OneTextHub.Tab.Fonts)));

            card.Add(HubUI.Field("Default font",
                HubUI.AssetPicker<OneFontAsset>(
                    () => _settings.DefaultFont,
                    value => Edit("_defaultFont", p => p.objectReferenceValue = value),
                    "none — labels draw nothing"),
                "Every label with no font of its own, and every measurement made before one is " +
                "assigned, uses this."));

            var fallbacks = new SerializedObject(_settings).FindProperty("_fallbackFonts");
            for (int i = 0; i < fallbacks.arraySize; i++)
            {
                int index = i;
                var row = HubUI.Box("row");
                row.Add(HubUI.AssetPicker<OneFontAsset>(
                    () =>
                    {
                        var list = new SerializedObject(_settings).FindProperty("_fallbackFonts");
                        return index < list.arraySize
                            ? list.GetArrayElementAtIndex(index).objectReferenceValue as OneFontAsset
                            : null;
                    },
                    value => Edit("_fallbackFonts",
                        p => p.GetArrayElementAtIndex(index).objectReferenceValue = value),
                    "empty slot"));
                row.Add(HubUI.Quiet("Remove", () =>
                {
                    Edit("_fallbackFonts", p =>
                    {
                        // The first delete on an object-reference element only
                        // clears it; the second is the one that shortens the
                        // list. Doing both is what "Remove" means here.
                        p.GetArrayElementAtIndex(index).objectReferenceValue = null;
                        p.DeleteArrayElementAtIndex(index);
                    });
                    Refresh();
                }));
                card.Add(HubUI.Field(index == 0 ? "Fallbacks" : " ", row));
            }

            card.Add(HubUI.Ghost("Add a fallback", () =>
            {
                Edit("_fallbackFonts", p => p.InsertArrayElementAtIndex(p.arraySize));
                Refresh();
            }));

            card.Add(HubUI.Field("System fonts",
                // The pill says what it is, not what it is set to: the state is
                // the pill's own lit/unlit, and a label that has to be rewritten
                // on every click is a label that will one day disagree with it.
                HubUI.Pill("Use the device's own fonts", _settings.SystemFontFallback,
                    on => Edit("_systemFontFallback", p => p.boolValue = on)),
                "The last tier of the chain, and it runs on the device, in the build: when no " +
                "font above covers a character, OneText looks through the fonts the player's " +
                "own machine ships with (/system/fonts on Android, the system font folders on " +
                "Windows, macOS, Linux and iOS) and draws it from there instead of a box.\n\n" +
                "The catch is that it is that device's font, not yours. A Galaxy and a Pixel and " +
                "a PC can each answer with a different face, and a device that has nothing for " +
                "the character still draws a box — so the same screen is not the same everywhere. " +
                "Doctor lists every character that ended up here for exactly that reason. Web has " +
                "no font folder to look in, so there it finds nothing."));
            return card.Root;
        }

        // ------------------------------------------------------------ new text

        /// <summary>
        /// What a label or a world text is created as.
        ///
        /// These are applied by the component's Reset, which Unity runs when it
        /// is added — from the GameObject menu, from Add Component, or from the
        /// context menu's own Reset. Changing a number here does not walk the
        /// project rewriting objects that already exist; it decides the next
        /// one, which is the only thing a default can honestly mean.
        /// </summary>
        private VisualElement NewTextCard()
        {
            var defaults = _settings.Defaults;
            var card = HubUI.MakeCard("New text",
                "What a label or world text is created with. Existing objects keep what they " +
                "have; this decides the next one.");

            card.Add(HubUI.Field("Font size",
                Number(defaults.FontSize, "0.##",
                    value => Edit("_defaultFontSize", p => p.floatValue = Mathf.Max(1f, value)))));

            var autoSize = HubUI.Box("row");
            autoSize.Add(Number(defaults.AutoSizeMin, "0.##",
                value => Edit("_defaultAutoSizeMin", p => p.floatValue = Mathf.Max(1f, value))));
            autoSize.Add(Gap());
            autoSize.Add(Number(defaults.AutoSizeMax, "0.##",
                value => Edit("_defaultAutoSizeMax", p => p.floatValue = Mathf.Max(1f, value))));
            card.Add(HubUI.Field("Auto size", autoSize,
                "The bounds auto-size may pick between, when a label turns it on."));

            card.Add(HubUI.Field("Wrapping",
                HubUI.Segments(new[] { "No wrap", "Wrap" }, defaults.Wrap == TextWrap.Wrap ? 1 : 0,
                    index => Edit("_defaultWrap", p => p.enumValueIndex = index))));

            var flags = HubUI.Box("row");
            flags.Add(HubUI.Pill("Raycast target", defaults.RaycastTarget,
                on => Edit("_defaultRaycastTarget", p => p.boolValue = on)));
            flags.Add(Gap());
            flags.Add(HubUI.Pill("Rich text", defaults.RichText,
                on => Edit("_defaultRichText", p => p.boolValue = on)));
            flags.Add(Gap());
            flags.Add(HubUI.Pill("Parse escapes", defaults.ParseEscapes,
                on => Edit("_defaultParseEscapes", p => p.boolValue = on)));
            card.Add(HubUI.Field("Behaviour", flags,
                "Raycast target decides whether a new label sits between the pointer and whatever " +
                "is behind it — a screen of captions usually does not want to. Rich text parses " +
                "<b> and the rest; escapes turn the two characters \\n into a real newline."));

            card.Add(HubUI.Field("Canvas size",
                Size(defaults.CanvasSize,
                    value => Edit("_defaultCanvasSize", p => p.vector2Value = value)),
                "The rect a label made from GameObject > UI > OneText > Label starts with."));

            card.Add(HubUI.Field("World size",
                Size(defaults.WorldSize,
                    value => Edit("_defaultWorldSize", p => p.vector2Value = value)),
                "The rect a world text starts with, in metres. TextMesh Pro's is 20 × 5, which is " +
                "why this is."));
            return card.Root;
        }

        // --------------------------------------------------------------- atlas

        private VisualElement AtlasCard()
        {
            var budget = _settings.AtlasSettings;
            var card = HubUI.MakeCard("Glyph atlas",
                "One texture array every label in the project draws from. Bigger holds more " +
                "glyphs at once; a CJK project at several sizes needs more than a Latin one.");
            card.Act(HubUI.Ghost("Live atlas", () => Hub.Go(OneTextHub.Tab.Atlas)));

            // The two readouts under the controls are the whole reason anybody
            // touches those controls, so they are built first and rewritten in
            // place on every change. Rebuilding the card instead would work for
            // the segments and break the slider: the element being dragged
            // would be destroyed mid-drag.
            Label memory = null;
            Label capacity = null;

            void Sync()
            {
                var current = _settings.AtlasSettings;
                memory.text = $"{current.MemoryBytes / (1024f * 1024f):0.#} MB " +
                    $"({current.TextureSize}x{current.TextureSize} x {current.LayerCount})";
                // A CJK glyph at 48px rasterizes to roughly a 56px square once
                // the SDF padding is added; that ratio is what makes CJK the
                // budget case.
                long texels = (long)current.TextureSize * current.TextureSize * current.LayerCount;
                capacity.text = $"~{texels / (56 * 56):n0} CJK tiles @48px, " +
                    $"~{texels / (26 * 26):n0} Latin tiles @36px";
            }

            // Above the budget controls because it is what spends them: a rung
            // multiplies the texels every label at Project asks for, and the
            // square of that is the atlas area the two knobs below have to
            // find. The three rungs mean different multiples on the two sides
            // (1/1.5/2 on a canvas, 1/2/4 in the world) and the hint says so
            // rather than the labels, which would otherwise have to name four
            // numbers in three buttons.
            var rungs = new[] { TextQuality.Performance, TextQuality.Medium, TextQuality.High };
            int rung = Array.IndexOf(rungs, _settings.DefaultQuality);
            card.Add(HubUI.Field("Quality",
                HubUI.Segments(new[] { "Performance", "Medium", "High" }, rung < 0 ? 0 : rung,
                    index => Edit("_defaultQuality", p => p.intValue = (int)rungs[index],
                        rebuildAtlas: true)),
                "Atlas texels per em, for every label and world text whose own Quality says " +
                "Project — which is all of them until somebody says otherwise. Performance " +
                "bakes at the size the text asks for.\n\n" +
                "Raise it when text is magnified after it is baked. On a canvas that is the " +
                "scale factor: a CanvasScaler set to Scale With Screen Size draws a 36-point " +
                "label at 108 screen pixels off a tile baked for 36, and nothing here can read " +
                "that factor at bake time. In the world it is the camera getting close. The " +
                "rungs are 1x, 1.5x and 2x on a canvas and 1x, 2x and 4x in the world, because " +
                "a scale factor has a ceiling and a camera does not.\n\n" +
                "Each rung costs the square of itself in atlas area, so a project that raises " +
                "this usually raises the layers below with it."));

            int selected = Array.IndexOf(TextureSizes, budget.TextureSize);
            var labels = new string[TextureSizes.Length];
            for (int i = 0; i < TextureSizes.Length; i++) labels[i] = TextureSizes[i].ToString();
            card.Add(HubUI.Field("Texture size",
                HubUI.Segments(labels, selected < 0 ? 1 : selected,
                    index =>
                    {
                        Edit("_atlasTextureSize", p => p.intValue = TextureSizes[index],
                            rebuildAtlas: true);
                        Sync();
                    })));

            // A count from 1 to 16 typed, not dragged. The slider that was here
            // is the one the forensics screen uses for a continuous size, and
            // on a whole number with sixteen stops it was both fiddly and, at
            // this width, unreadable.
            card.Add(HubUI.Field("Layers",
                Number(budget.LayerCount, "0", value =>
                {
                    Edit("_atlasLayerCount",
                        p => p.intValue = Mathf.Clamp(Mathf.RoundToInt(value), 1, 16),
                        rebuildAtlas: true);
                    Sync();
                }),
                "Layers in the texture array. Memory is size × size × layers bytes, so this is " +
                "the cheaper of the two knobs to raise: a layer is added, not a bigger texture " +
                "allocated."));

            memory = Readout(card, "Texture memory");
            capacity = Readout(card, "Rough capacity");
            Sync();

            // The memory figure is R8; the precise atlas holds the same texels
            // in four channels from the same setting, so a project with precise
            // labels pays it again times four. Only said when that atlas exists;
            // the existence check never creates one.
            if (SharedGlyphAtlas.PreciseAtlasExists)
            {
                var precise = SharedGlyphAtlas.PreciseAtlas.GetStats();
                card.Add(HubUI.KeyValue("Precise atlas",
                    $"+{precise.MemoryBytes / (1024f * 1024f):0.#} MB (RGBA32, for labels with " +
                    "Precise on)"));
            }

            if (SharedGlyphAtlas.Exists)
            {
                var stats = SharedGlyphAtlas.Atlas.GetStats();
                card.Add(HubUI.KeyValue("Live atlas",
                    $"{stats.TileCount:n0} tiles, {HubUI.Percent(stats.UsedFraction, 1)} full, " +
                    $"{stats.Evictions:n0} evictions",
                    stats.Drops > 0 ? HubTone.Bad : HubTone.Good));
            }
            else if (!SharedGlyphAtlas.PreciseAtlasExists)
            {
                card.Add(HubUI.KeyValue("Live atlas", "not created yet"));
            }

            card.Add(HubUI.KeyValue("Partial upload",
                GlyphAtlas.SupportsPartialUpload
                    ? "supported"
                    : "not supported (full upload per flush)"));
            return card.Root;
        }

        // ------------------------------------------------------------- prewarm

        private VisualElement PrewarmCard()
        {
            var card = HubUI.MakeCard("Prewarm and recording",
                "Characters rasterized when the game starts, so the first frame showing one does " +
                "not hitch — and a recorder that says which characters those turned out to be.");
            card.Act(HubUI.Ghost("Charsets", () => Hub.Go(OneTextHub.Tab.Charsets)));

            card.Add(HubUI.Field("Prewarm charset",
                HubUI.AssetPicker<OneTextCharset>(
                    () => _settings.PrewarmCharset,
                    value => Edit("_prewarmCharset", p => p.objectReferenceValue = value),
                    "nothing prewarmed")));

            card.Add(HubUI.Field("In play mode",
                HubUI.Pill("Record every character drawn", _settings.RecordCharsetInPlayMode,
                    on => Edit("_recordCharsetInPlayMode", p => p.boolValue = on)),
                "Records every character drawn during play, to save as a charset afterwards. " +
                "The characters a project cannot predict — a player's name, a number, a line " +
                "built at runtime — are only ever found this way."));
            return card.Root;
        }

        private VisualElement WordListsCard()
        {
            var card = HubUI.MakeCard("Line breaking",
                "Word lists installed at startup for the scripts that write no spaces: Thai, Lao, " +
                "Khmer, Burmese. Each replaces the built-in starter list for its script.");
            card.Act(HubUI.Ghost("Dictionaries", () => Hub.Go(OneTextHub.Tab.Dictionaries)));

            int count = 0;
            foreach (var dictionary in _settings.Dictionaries)
                if (dictionary != null) count++;
            card.Add(HubUI.KeyValue("Installed", count == 0
                    ? "none — those scripts wrap on the starter lists"
                    : $"{count:n0} list(s)",
                count == 0 ? HubTone.Neutral : HubTone.Good));
            return card.Root;
        }

        private VisualElement AssetCard()
        {
            var card = HubUI.MakeCard("The asset",
                "Everything on this page lives in one ScriptableObject, under Resources so the " +
                "runtime loads it with no editor code involved.");
            card.Act(HubUI.Quiet("Select",
                () => Selection.activeObject = _settings));
            card.Add(HubUI.KeyValue("Path", AssetDatabase.GetAssetPath(_settings)));
            return card.Root;
        }

        // --------------------------------------------------------------- parts

        /// <summary>
        /// A number, typed rather than dragged.
        ///
        /// Delayed on purpose: every keystroke in an immediate field is a write
        /// to the asset, and "36" passes through 3 on its way in.
        /// </summary>
        private static TextField Number(float value, string format, Action<float> onChange)
        {
            var field = HubUI.Input(value.ToString(format, CultureInfo.InvariantCulture));
            field.isDelayed = true;
            field.style.width = 84f;
            field.style.flexGrow = 0f;
            HubUI.Mono(field);
            field.RegisterValueChangedCallback(evt =>
            {
                if (float.TryParse(evt.newValue, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float parsed))
                    onChange(parsed);
            });
            return field;
        }

        private static VisualElement Size(Vector2 value, Action<Vector2> onChange)
        {
            var row = HubUI.Box("row");
            row.Add(Number(value.x, "0.##", x => onChange(new Vector2(x, value.y))));
            row.Add(HubUI.Text("×", "hint"));
            row.Add(Number(value.y, "0.##", y => onChange(new Vector2(value.x, y))));
            return row;
        }

        /// <summary>A key/value row whose value this panel keeps rewriting.</summary>
        private static Label Readout(HubUI.Card card, string key)
        {
            var row = HubUI.Box("kv");
            row.Add(HubUI.Text(key, "kv__key"));
            var value = HubUI.Mono(HubUI.Text(string.Empty, "kv__value"));
            row.Add(value);
            card.Add(row);
            return value;
        }

        private static VisualElement Gap()
        {
            var gap = new VisualElement();
            gap.style.width = 8f;
            return gap;
        }
    }
}
