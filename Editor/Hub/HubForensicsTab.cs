using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>
    /// Type a string, click a glyph, get the answer: which font drew it, which
    /// characters it came from, whether the shaper substituted it, and, at the
    /// end of a line, the UAX #14 rule that put the break there, by name.
    ///
    /// The layout is real: the same engine, the same font chain, the same
    /// language. Clicking maps back through the laid-out glyph boxes, so what
    /// is inspected is what is drawn.
    /// </summary>
    public sealed class HubForensicsTab : HubSection
    {
        private string _text = "The quick brown fox, 「こんにちは」と言った。 ค่าที่ตั้งไว้";
        private OneFontAsset _font;
        private string _language = "";
        private float _fontSize = 34f;
        private float _boxWidth = 420f;
        private int _selected = -1;

        private readonly TextLayoutEngine _engine = new TextLayoutEngine();
        private readonly TextLayoutResult _layout = new TextLayoutResult();
        private readonly TextPreviewRenderer _renderer = new TextPreviewRenderer
        {
            Background = new Color(0.078f, 0.094f, 0.11f, 1f),
        };
        private List<GlyphReport> _reports;
        private FontStack _fonts;
        private Texture2D _preview;
        private string _previewKey;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Forensics;

        public override string Title => "Forensics";

        public override string Eyebrow => "Why does it look like that?";

        public override string Lede =>
            "Why does this glyph look like that, and why did the line break there? Both answers " +
            "are inside the engine at the moment it draws; this reads them back out.";

        public override string NavHint => "one glyph at a time";

        public override string NavGroup => "Checks";

        protected override void Compose(VisualElement content)
        {
            content.Add(InputCard());

            var font = _font != null ? _font
                : OneTextSettings.Instance != null ? OneTextSettings.Instance.DefaultFont
                : null;
            if (font == null || font.Font == null)
            {
                var card = HubUI.MakeCard("Nothing to draw with",
                    "There is no font chosen and no project default to borrow.");
                card.Add(HubUI.Empty("Pick a font",
                    "Choose one above, or set a project default so every screen in this window " +
                    "has something to draw with.",
                    "Open fonts", () => Hub.Go(OneTextHub.Tab.Fonts), "A"));
                content.Add(card.Root);
                return;
            }

            BuildLayout(font);
            content.Add(StageCard(font));
            content.Add(SelectionCard(font));
            content.Add(GlyphListCard());
        }

        // ---------------------------------------------------------------- input

        private VisualElement InputCard()
        {
            var card = HubUI.MakeCard("Input",
                "What to inspect. Real layout, real shaping, real fallback: the same engine a " +
                "label uses.");

            var text = HubUI.Input(_text);
            text.isDelayed = true;
            text.RegisterValueChangedCallback(evt =>
            {
                _text = evt.newValue;
                Invalidate();
                Refresh();
            });
            card.Add(HubUI.Field("Text", text));

            card.Add(HubUI.Field("Font", HubUI.AssetPicker<OneFontAsset>(
                () => _font,
                value => { _font = value; Invalidate(); Refresh(); },
                "Project default")));

            var language = HubUI.Input(_language);
            language.isDelayed = true;
            language.RegisterValueChangedCallback(evt =>
            {
                _language = evt.newValue;
                Invalidate();
                Refresh();
            });
            card.Add(HubUI.Field("Language", language,
                "Passed to the shaper, so the font's locl rules run: ja, zh-Hans, ko, th."));

            card.Add(HubUI.Field("Font size", HubUI.Knob(_fontSize, 8f, 96f, "0", value =>
            {
                _fontSize = value;
                Invalidate();
                Refresh();
            })));

            card.Add(HubUI.Field("Box width", HubUI.Knob(_boxWidth, 60f, 900f, "0", value =>
            {
                _boxWidth = value;
                Invalidate();
                Refresh();
            })));
            return card.Root;
        }

        private void BuildLayout(OneFontAsset font)
        {
            if (_reports != null) return;

            _fonts = new FontStack();
            _fonts.Add(font.Font, font.Language);
            var settings = OneTextSettings.Instance;
            if (settings != null)
                foreach (var fallback in settings.FallbackFonts)
                    if (fallback != null) _fonts.Add(fallback.Font, fallback.Language);

            var layoutSettings = TextLayoutSettings.Default(_fonts, _fontSize);
            layoutSettings.MaxWidth = _boxWidth;
            layoutSettings.Language = string.IsNullOrWhiteSpace(_language) ? null : _language.Trim();
            layoutSettings.Kinsoku = AsianTypography.Kinsoku.Normal;
            _engine.Layout(_text ?? string.Empty, layoutSettings, _layout);
            _reports = GlyphForensics.Inspect(_text, _layout, _fonts);
        }

        // ---------------------------------------------------------------- stage

        /// <summary>
        /// The rendered text, with a click mapped back to a glyph.
        ///
        /// Hit-testing against the glyph boxes rather than the caret positions
        /// on purpose: a caret answers "where would an edit go", and the
        /// question here is "what is this thing I am looking at".
        /// </summary>
        private VisualElement StageCard(OneFontAsset font)
        {
            var card = HubUI.MakeCard("The line, as drawn",
                "Click a glyph and the box moves to what you picked.");

            int width = Mathf.CeilToInt(_boxWidth);
            int height = Mathf.CeilToInt(Mathf.Max(_layout.Height + _fontSize, _fontSize * 2f));

            string key = $"{_text}|{font.GetInstanceID()}|{_language}|{_fontSize}|{_boxWidth}";
            if (_preview == null || _previewKey != key)
            {
                if (_preview != null) Object.DestroyImmediate(_preview);
                _preview = _renderer.Render(_text, font, _language, _fontSize, width, height,
                    TextWrap.Wrap, TextAlignment.Left);
                _previewKey = key;
            }

            var stage = new VisualElement();
            stage.AddToClassList("preview");
            stage.style.width = width;
            stage.style.height = height;
            stage.style.flexShrink = 0f;

            if (_preview != null)
            {
                var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop, image = _preview };
                picture.style.position = Position.Absolute;
                picture.style.left = 0f;
                picture.style.top = 0f;
                picture.style.width = width;
                picture.style.height = height;
                stage.Add(picture);
            }

            // The selected glyph, boxed, in the same coordinates the engine laid
            // it out in: y down from the top of the block.
            if (_reports != null && _selected >= 0 && _selected < _reports.Count)
            {
                var box = BoxOf(_reports[_selected]);
                if (box.width > 0f)
                {
                    var outline = HubUI.Box("selection-box");
                    outline.style.left = box.x;
                    outline.style.top = box.y;
                    outline.style.width = box.width;
                    outline.style.height = box.height;
                    outline.pickingMode = PickingMode.Ignore;
                    stage.Add(outline);
                }
            }

            stage.RegisterCallback<MouseDownEvent>(evt =>
            {
                _selected = GlyphAt(evt.localMousePosition);
                Refresh();
                evt.StopPropagation();
            });

            card.Add(stage);
            return card.Root;
        }

        /// <summary>The glyph's box in layout coordinates, or an empty rect.</summary>
        private Rect BoxOf(in GlyphReport report)
        {
            if (report.GlyphIndex < 0 || report.GlyphIndex >= _layout.Glyphs.Count) return default;
            var run = _layout.Runs[report.RunIndex];
            float scale = run.FontSize / Mathf.Max(1f, run.Font?.UnitsPerEm ?? 1000f);

            float x = run.X;
            for (int g = run.GlyphStart; g < report.GlyphIndex; g++)
                x += _layout.Glyphs[g].XAdvance * scale;

            float advance = _layout.Glyphs[report.GlyphIndex].XAdvance * scale;
            var line = _layout.Lines[report.LineIndex];
            return new Rect(x, line.Baseline - line.Ascent, Mathf.Max(2f, advance), line.Height);
        }

        private int GlyphAt(Vector2 point)
        {
            if (_reports == null) return -1;
            for (int i = 0; i < _reports.Count; i++)
            {
                var box = BoxOf(_reports[i]);
                if (box.width > 0f && box.Contains(point)) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------ selection

        private VisualElement SelectionCard(OneFontAsset font)
        {
            var card = HubUI.MakeCard("This glyph",
                "Everything the engine knew about it at the moment it drew it.");

            if (_reports == null || _selected < 0 || _selected >= _reports.Count)
            {
                card.Add(HubUI.Empty("Nothing selected",
                    "Click a glyph in the line above (or a row in the list below) and its font, " +
                    "its cluster, its substitution and the rule that broke the line after it show " +
                    "up here.", null, null, "◎"));
                return card.Root;
            }

            card.Flush();
            var report = _reports[_selected];
            card.Add(HubUI.KeyValue("Characters",
                $"'{report.Characters}'   {CodepointList(report.Characters)}   " +
                $"at {report.TextStart}..{report.TextStart + report.TextLength}"));
            card.Add(HubUI.KeyValue("Glyph",
                report.Substituted
                    ? $"{report.GlyphId}, substituted; the cmap maps this character to " +
                      $"{report.NominalGlyphId}"
                    : $"{report.GlyphId}",
                report.Substituted ? HubTone.Good : HubTone.Neutral));
            card.Add(HubUI.KeyValue("Font",
                report.FontFamily + (string.IsNullOrEmpty(report.FontLanguage)
                    ? "   (no language tag)"
                    : $"   [{report.FontLanguage}]"),
                string.IsNullOrEmpty(report.FontLanguage) ? HubTone.Warn : HubTone.Neutral));
            card.Add(HubUI.KeyValue("Run",
                $"line {report.LineIndex}, run {report.RunIndex}, " +
                (report.RightToLeft ? "right-to-left" : "left-to-right") +
                (report.Positioned ? ", moved by GPOS" : "")));

            if (report.EndsLine)
            {
                card.Add(HubUI.KeyValue("Line break",
                    report.BreakRule + (report.BreakNote != null ? $", {report.BreakNote}" : ""),
                    HubTone.Good));
            }

            card.Add(HubUI.KeyValue("Features",
                GlyphForensics.FeatureSummary(FontOf(report, font))));
            return card.Root;
        }

        private FontData FontOf(in GlyphReport report, OneFontAsset fallback)
        {
            if (report.RunIndex >= 0 && report.RunIndex < _layout.Runs.Count)
            {
                var font = _layout.Runs[report.RunIndex].Font;
                if (font != null) return font;
            }
            return fallback != null ? fallback.Font : null;
        }

        private VisualElement GlyphListCard()
        {
            var card = HubUI.MakeCard($"All {_reports.Count:n0} glyph(s)",
                "In the order the shaper produced them, which is not always the order they read " +
                "in.").Flush();

            for (int i = 0; i < _reports.Count; i++)
            {
                int index = i;
                var row = new Button(() =>
                {
                    _selected = index;
                    Refresh();
                })
                { text = string.Empty };
                row.AddToClassList("glyph-row");
                row.EnableInClassList("glyph-row--on", i == _selected);
                row.Add(HubUI.Text(index.ToString(), "glyph-row__index"));
                row.Add(HubUI.Mono(HubUI.Text(_reports[i].ToString(), "glyph-row__text")));
                card.Add(row);
            }
            return card.Root;
        }

        private static string CodepointList(string text)
        {
            var parts = new List<string>();
            foreach (int codepoint in TextDoctor.Codepoints(text)) parts.Add($"U+{codepoint:X4}");
            return string.Join(" ", parts);
        }

        private void Invalidate()
        {
            _reports = null;
            _selected = -1;
        }

        public override void Dispose()
        {
            if (_preview != null) Object.DestroyImmediate(_preview);
            _preview = null;
            _renderer.Dispose();
        }
    }
}
