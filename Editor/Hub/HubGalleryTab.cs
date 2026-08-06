using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Every string the project ships, laid out with its real font, style and
    /// locale, in one list, with the overflows in red.
    ///
    /// The pass this replaces is a person opening every screen in every
    /// language looking for the three buttons whose German label does not fit.
    /// The layout engine needs no scene to answer that, so it is a list you
    /// scroll rather than a build you play.
    ///
    /// The same view flipped, one string across every style, is how a
    /// typeface gets chosen, with your own sentence rather than the pangram the
    /// foundry picked.
    /// </summary>
    public sealed class HubGalleryTab : HubSection
    {
        private enum Mode
        {
            EveryString,
            OneStringEveryStyle,
        }

        private static readonly string[] ModeNames = { "Every string", "One string, every style" };

        private Mode _mode;
        private GalleryOptions _options = GalleryOptions.Default;
        private TextScanResult _scan;
        private List<GalleryCell> _cells;
        private string _localeFilter = "(all)";
        private bool _problemsOnly;
        private string _sample = "다람쥐 헌 쳇바퀴에 타고파";
        private bool _preview = true;
        private int _shown;

        // Composited over the window's own panel colour rather than the
        // renderer's default grey, so a preview is part of the card it sits in.
        private readonly TextPreviewRenderer _renderer = new TextPreviewRenderer
        {
            Background = new Color(0.078f, 0.094f, 0.11f, 1f),
        };
        private readonly Dictionary<string, Texture2D> _previews = new Dictionary<string, Texture2D>();

        /// <summary>Cells rendered per pass, so a table of ten thousand strings still scrolls.</summary>
        private const int PreviewBudget = 60;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Gallery;

        public override string Title => "Gallery";

        public override string Eyebrow => "Every string, drawn";

        public override string Lede =>
            "Your strings laid out at the size and in the box they will really have, without " +
            "building anything. Red means the text does not fit, or that no font in the chain " +
            "can draw it.";

        public override string NavHint => "overflow and previews";

        public override string NavGroup => "Checks";

        public override string BadgeText
        {
            get
            {
                if (_cells == null) return null;
                int problems = 0;
                foreach (var cell in _cells) if (!cell.Ok) problems++;
                return problems == 0 ? "OK" : problems.ToString("n0");
            }
        }

        public override HubTone BadgeTone => BadgeText == "OK" ? HubTone.Good : HubTone.Bad;

        protected override void Compose(VisualElement content)
        {
            content.Add(ModeCard());
            if (_mode == Mode.OneStringEveryStyle) ComposeOneString(content);
            else ComposeEveryString(content);
        }

        // --------------------------------------------------------------- setup

        private VisualElement ModeCard()
        {
            var card = HubUI.MakeCard("View",
                "One project's strings, or one sentence in everything you could set it in.");
            card.Add(HubUI.Segments(ModeNames, (int)_mode, index =>
            {
                _mode = (Mode)index;
                Refresh();
            }));

            var advanced = new VisualElement();

            var box = HubUI.Box("row");
            var width = HubUI.Input(_options.BoxWidth.ToString("0.#"));
            width.isDelayed = true;
            width.RegisterValueChangedCallback(evt =>
            {
                if (float.TryParse(evt.newValue, out float value)) _options.BoxWidth = value;
                Refresh();
            });
            width.style.width = 90f;
            var height = HubUI.Input(_options.BoxHeight.ToString("0.#"));
            height.isDelayed = true;
            height.RegisterValueChangedCallback(evt =>
            {
                if (float.TryParse(evt.newValue, out float value)) _options.BoxHeight = value;
                Refresh();
            });
            height.style.width = 90f;
            height.style.marginLeft = 8f;
            box.Add(width);
            box.Add(HubUI.Text("×", "muted"));
            box.Add(height);
            advanced.Add(HubUI.Field("Box, in pixels", box));

            var size = HubUI.Input(_options.FontSize.ToString("0.#"));
            size.isDelayed = true;
            size.RegisterValueChangedCallback(evt =>
            {
                if (float.TryParse(evt.newValue, out float value)) _options.FontSize = value;
                Refresh();
            });
            size.style.width = 90f;
            advanced.Add(HubUI.Field("Font size", size));

            advanced.Add(HubUI.Field("Wrapping", HubUI.Segments(
                new[] { "No wrap", "Wrap" }, _options.Wrap == TextWrap.Wrap ? 1 : 0, index =>
                {
                    _options.Wrap = index == 1 ? TextWrap.Wrap : TextWrap.NoWrap;
                    Refresh();
                })));

            advanced.Add(HubUI.Field("Pictures", HubUI.Pill(
                "Render each cell with the engine", _preview, on =>
                {
                    _preview = on;
                    Refresh();
                })));

            card.Add(HubUI.Disclose("The box these are measured in", advanced));
            return card.Root;
        }

        // ------------------------------------------------- one string, all styles

        private void ComposeOneString(VisualElement content)
        {
            var fonts = AllFonts();
            var card = HubUI.MakeCard("Sample string",
                "Type something you actually ship; a foundry's pangram tells you about the " +
                "foundry.");
            var field = HubUI.Input(_sample);
            field.isDelayed = true;
            field.RegisterValueChangedCallback(evt =>
            {
                _sample = evt.newValue;
                ClearPreviews();
                Refresh();
            });
            card.Add(HubUI.Field("String", field));
            content.Add(card.Root);

            if (fonts.Count == 0)
            {
                var empty = HubUI.MakeCard("Nothing to draw with",
                    "There are no font assets in this project yet.");
                empty.Add(HubUI.Empty("No fonts imported",
                    "Import a font first; the gallery draws with the real engine and the real " +
                    "font chain, so it needs one.",
                    "Open fonts", () => Hub.Go(OneTextHub.Tab.Fonts), "A"));
                content.Add(empty.Root);
                return;
            }

            var rows = HubUI.MakeCard("Styles and fonts",
                $"{AllStyles().Count:n0} style(s) and {fonts.Count:n0} font(s).").Flush();

            foreach (var style in AllStyles())
            {
                var font = style.Sets(OneTextStyle.Fields.Font) ? style.Font : null;
                rows.Add(Row(style.name, _sample, font,
                    style.Sets(OneTextStyle.Fields.Size) ? style.FontSize : _options.FontSize, null));
            }

            // Styles are how a project should choose type, and font assets are
            // what it has before anybody has made one.
            foreach (var font in fonts)
                rows.Add(Row(font.FamilyName, _sample, font, _options.FontSize, font.Language));

            content.Add(rows.Root);
        }

        private VisualElement Row(string label, string text, OneFontAsset font, float size,
            string language)
        {
            var row = HubUI.Box("style-row");
            row.Add(HubUI.Text(label, "style-row__name"));

            int width = Mathf.Max(8, Mathf.RoundToInt(_options.BoxWidth));
            int height = Mathf.Max(24, Mathf.RoundToInt(size * 1.6f));

            if (!_preview)
            {
                var plain = HubUI.Text(text, "kv__value");
                row.Add(plain);
                return row;
            }

            var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            picture.AddToClassList("preview");
            picture.style.width = width;
            picture.style.height = height;
            picture.image = Preview($"{label}|{size}|{text}", text, font, language, size,
                width, height);
            if (picture.image == null)
            {
                row.Add(HubUI.Text("(no font)", "hint"));
                return row;
            }
            row.Add(picture);
            return row;
        }

        // ---------------------------------------------------- every string

        private void ComposeEveryString(VisualElement content)
        {
            content.Add(StringSources(
                "The gallery lays out every string it finds under these paths."));

            var card = HubUI.MakeCard("Scan results",
                _cells == null
                    ? "Nothing scanned yet."
                    : $"{_cells.Count:n0} cell(s) from {_scan.FilesScanned} file(s).");
            card.Act(HubUI.Primary("Scan and lay out", () =>
            {
                Scan();
                Refresh();
            }));

            if (_cells == null)
            {
                card.Add(HubUI.Empty("Nothing scanned yet",
                    Hub.StringFolders.Count == 0
                        ? "Add a folder of strings above, then scan. Every string in it is laid " +
                          "out with its real font at its real size."
                        : "One scan lays out every string under those folders and flags the ones " +
                          "that do not fit their box.",
                    Hub.StringFolders.Count == 0 ? null : "Scan and lay out",
                    Hub.StringFolders.Count == 0 ? (Action)null : () =>
                    {
                        Scan();
                        Refresh();
                    }, "▤"));
                content.Add(card.Root);
                return;
            }

            int problems = 0;
            foreach (var cell in _cells) if (!cell.Ok) problems++;
            card.Add(problems == 0
                ? HubUI.Notice($"{_cells.Count:n0} string(s) all fit their box and every " +
                    "character has a font.", HubTone.Good)
                : HubUI.Notice($"{problems:n0} of {_cells.Count:n0} string(s) overflow or " +
                    "contain a character no font in the chain can draw.", HubTone.Bad));

            var filters = HubUI.Box("row");
            var locales = new List<string> { "(all)" };
            foreach (string locale in _scan.Locales()) locales.Add(locale ?? "(unnamed)");
            int index = Mathf.Max(0, locales.IndexOf(_localeFilter));
            filters.Add(HubUI.Text("Locale", "field__label"));
            filters.Add(LocaleMenu(locales, index));
            filters.Add(HubUI.Pill("Problems only", _problemsOnly, on =>
            {
                _problemsOnly = on;
                Refresh();
            }));
            card.Add(filters);
            content.Add(card.Root);

            _shown = 0;
            var list = new VisualElement();
            foreach (var cell in _cells)
            {
                if (_problemsOnly && cell.Ok) continue;
                if (_localeFilter != "(all)" &&
                    (cell.Entry.Locale ?? "(unnamed)") != _localeFilter) continue;
                list.Add(Cell(cell));
            }

            if (_shown == 0)
                list.Add(HubUI.Notice("Nothing to show with these filters.", HubTone.Neutral));
            else if (_preview && _shown > PreviewBudget)
                list.Add(HubUI.Notice(
                    $"The first {PreviewBudget} cells are drawn; the rest show their text. " +
                    "Filter to fewer strings to draw them all.", HubTone.Neutral));
            content.Add(list);
        }

        private Button LocaleMenu(List<string> locales, int index)
        {
            Button button = null;
            button = new Button(() =>
            {
                var menu = new GenericMenu();
                foreach (string locale in locales)
                {
                    string captured = locale;
                    menu.AddItem(new GUIContent(locale), locale == _localeFilter, () =>
                    {
                        _localeFilter = captured;
                        Refresh();
                    });
                }
                menu.DropDown(button.worldBound);
            })
            { text = string.Empty };
            button.AddToClassList("picker");
            button.style.flexGrow = 0f;
            button.style.width = 180f;
            button.style.marginRight = 10f;
            button.Add(HubUI.Text(locales[index], "picker__name"));
            button.Add(HubUI.Text("▾", "picker__caret"));
            return button;
        }

        private VisualElement Cell(in GalleryCell cell)
        {
            _shown++;
            var root = HubUI.Box("cell");
            if (!cell.Ok) root.AddToClassList("cell--bad");

            var head = HubUI.Box("cell__head");
            head.Add(HubUI.Text((cell.Entry.Locale ?? "-").ToUpperInvariant(), "cell__locale"));
            head.Add(HubUI.Text(cell.Entry.Key, "cell__key"));
            var status = HubUI.Text(Status(cell), "cell__status");
            if (!cell.Ok) status.AddToClassList("cell__status--bad");
            HubUI.Mono(status);
            head.Add(status);
            root.Add(head);

            var body = HubUI.Box("cell__body");
            int width = Mathf.Max(8, Mathf.RoundToInt(_options.BoxWidth));
            int height = Mathf.Max(24, Mathf.RoundToInt(_options.BoxHeight));

            if (_preview && _shown <= PreviewBudget)
            {
                var style = cell.Style;
                var font = style != null && style.Sets(OneTextStyle.Fields.Font)
                    ? style.Font
                    : OneTextSettings.Instance != null
                        ? OneTextSettings.Instance.DefaultFont
                        : null;
                var texture = Preview(
                    $"{cell.Entry.Source}|{cell.Entry.Key}|{cell.StyleName}|{_options.FontSize}",
                    cell.Entry.Value, font, cell.Entry.Locale,
                    style != null && style.Sets(OneTextStyle.Fields.Size)
                        ? style.FontSize : _options.FontSize,
                    width, height);

                if (texture != null)
                {
                    // The box is drawn as a frame around the render, so overflow
                    // is something you see rather than a number you compare.
                    var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop, image = texture };
                    picture.AddToClassList("preview");
                    if (!cell.Ok) picture.AddToClassList("preview--bad");
                    picture.style.width = width;
                    picture.style.height = height;
                    body.Add(picture);
                }
                else
                {
                    body.Add(HubUI.Text(cell.Entry.Value, "kv__value"));
                }
            }
            else
            {
                body.Add(HubUI.Text(cell.Entry.Value, "kv__value"));
            }

            root.Add(body);
            return root;
        }

        private static string Status(in GalleryCell cell)
        {
            if (cell.MissingGlyphs > 0)
                return $"{cell.MissingGlyphs} character(s) no font draws";
            if (cell.Overflow)
                return $"overflows: needs {cell.Width:0}×{cell.Height:0}" +
                       (cell.WouldTruncate ? " (will be cut)" : "");
            return $"{cell.Width:0}×{cell.Height:0} in {cell.LineCount} line(s)";
        }

        private void Scan()
        {
            ClearPreviews();
            _scan = TextSourceScanner.Scan(Hub.StringFolders);
            var styles = AllStyles();
            _cells = StringGallery.Measure(_scan.Entries, styles, _options);

            int problems = 0;
            foreach (var cell in _cells) if (!cell.Ok) problems++;
            Say($"{_cells.Count:n0} string(s) from {_scan.FilesScanned} file(s); " +
                (problems == 0 ? "nothing overflows." : $"{problems:n0} need attention."));
        }

        // ------------------------------------------------------------ previews

        private Texture2D Preview(string key, string text, OneFontAsset font, string language,
            float size, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            string cacheKey = $"{key}|{width}x{height}";
            if (_previews.TryGetValue(cacheKey, out var cached) && cached != null) return cached;

            var texture = _renderer.Render(text, font, language, size, width, height,
                _options.Wrap, TextAlignment.Left);
            _previews[cacheKey] = texture;
            return texture;
        }

        private void ClearPreviews()
        {
            foreach (var texture in _previews.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            _previews.Clear();
        }

        public override void Dispose()
        {
            ClearPreviews();
            _renderer.Dispose();
        }
    }
}
