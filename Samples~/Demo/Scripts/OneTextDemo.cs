using System.Collections.Generic;
using System.Text;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// The whole demo: effects, markup, six writing systems, and the numbers
    /// underneath them.
    ///
    /// Put this on one empty object in an otherwise empty scene and press play.
    /// It builds its own canvas, so there is no prefab to fall out of date with
    /// the package and no scene file anybody has to read a diff of.
    ///
    /// The claim the right-hand column is making, and the reason the stress
    /// buttons exist: every label here samples one shared atlas through one
    /// shared material, so the batch count does not move with the number of
    /// labels. Not one batch for the screen — the panels, buttons and clip
    /// rects cost their own — but constant. Measured at 1600x900, walking the
    /// count up in fifties:
    ///
    ///     86 labels → 10 batches, 6 set-pass
    ///    486 labels → 10 batches, 6 set-pass
    ///
    /// Unchanged at every step in between. If pressing the stress button ever
    /// moves that number the way the label count moves, the demo has found a
    /// bug, which is the other thing a demo is for. It found one during its own
    /// construction — see <see cref="FitStressGrid"/>.
    ///
    /// What is deliberately not here yet: the input field. Watching the atlas
    /// rasterise a glyph the instant you type it is the best thing this demo
    /// could do, and it needs the hidden-HTML-input work that the roadmap still
    /// lists as outstanding for the web. Until then the "rasterise" button
    /// below fakes the interesting half of it.
    /// </summary>
    [AddComponentMenu("OneText/Samples/OneText Demo")]
    public sealed class OneTextDemo : MonoBehaviour
    {
        [Tooltip("The fonts every label draws with. Empty runs on system fonts, " +
                 "which exist in the editor and in no build.")]
        [SerializeField] private OneTextDemoFonts _fonts = new OneTextDemoFonts();

        [Tooltip("Labels each press of the stress button adds.")]
        [SerializeField] private int _stressStep = 50;

        [Tooltip("Previously unseen codepoints each press of the rasterise button demands.")]
        [SerializeField] private int _rasteriseStep = 200;

        [Tooltip("Shrink the stress cells so every label stays inside the panel. " +
                 "Turn off to reproduce the overflow that costs a batch — see FitStressGrid.")]
        [SerializeField] private bool _autoFitStressGrid = true;

        /// <summary>
        /// The font stack, for a caller that wants to configure it from code
        /// rather than the inspector — see
        /// <see cref="OneTextDemoFonts.UseBytes"/>. Only useful before the demo
        /// builds itself, which happens in <c>Awake</c>: create the object
        /// deactivated, set this, then activate it.
        /// </summary>
        public OneTextDemoFonts Fonts => _fonts;

        // ---------------------------------------------------------- content

        /// <summary>
        /// The continuous effects, in registration order, each with the tag a
        /// user would actually type. Parameters are left off on purpose: an
        /// unparameterised tag runs on the defaults the effect declares, and
        /// the defaults are what a newcomer will see first.
        /// </summary>
        private static readonly (string Tag, string Note)[] LoopingEffects =
        {
            ("wave", "vertical sine along the run"),
            ("shake", "per-glyph jitter"),
            ("wobble", "rotation about each glyph"),
            ("bounce", "offset, eased, one glyph behind the last"),
            ("rainbow", "hue swept across the run"),
            ("pulse", "scale about the glyph centre"),
            ("glitch", "displacement in bursts"),
            ("stretch", "non-uniform scale"),
            ("flash", "alpha, squared off"),
        };

        /// <summary>
        /// The entrance effects. These settle and stop, which is the point of
        /// them, so the demo needs a replay button to show them at all.
        /// </summary>
        private static readonly (string Tag, string Note)[] EntranceEffects =
        {
            ("fade", "alpha in"),
            ("rise", "up into place"),
            ("swell", "scale into place"),
            ("pop", "overshoot and settle"),
            ("drop", "down into place"),
        };

        private static readonly (string Caption, string Markup)[] Markup =
        {
            ("outline", "<outline w=0.25>Outlined text</outline>"),
            ("shadow", "<shadow x=2 y=-2 soft=0.3>Text with a shadow</shadow>"),
            ("glow", "<glow r=0.45>Glowing text</glow>"),
            ("colour and mark", "<color=#58A6FF>coloured</color> and <mark=#D2992240>marked</mark>"),
            ("weight and slant", "<b>bold</b> · <i>italic</i> · <u>underline</u> · <s>strike</s>"),
            ("size and offset", "H<sub>2</sub>O · E=mc<sup>2</sup> · <size=140%>larger</size>"),
            // Ems, both of them, which is the trap: <mspace=14> is not a
            // fourteen-pixel cell, it is fourteen of them, and the run wraps to
            // one glyph a line.
            ("letter spacing", "<cspace=0.25>spaced out</cspace> · <mspace=1>monospaced</mspace>"),
            ("alpha", "<alpha=#FF>full <alpha=#99>three quarters <alpha=#44>a quarter"),
            ("links", "a <link=onetext><color=#58A6FF>clickable span</color></link> in a paragraph"),
        };

        /// <summary>
        /// One line per script, and each is here because it needs something the
        /// naive answer does not have. Arabic joins; Devanagari reorders;
        /// Thai has no spaces to break at; Hebrew runs the other way and the
        /// digits inside it do not; the ZWJ emoji is four people in one glyph.
        /// </summary>
        private static readonly (string Caption, string Text)[] Scripts =
        {
            ("arabic · contextual joining", "العربية مرحبا بالعالم"),
            ("devanagari · reordering", "देवनागरी नमस्ते"),
            ("thai · no spaces to break at", "ภาษาไทยเขียนติดกันโดยไม่มีช่องว่าง"),
            ("hebrew · bidi with digits", "עברית 2026 שלום"),
            ("bidi · mixed runs", "English مرحبا English again"),
            ("korean", "한국어 조판 테스트"),
            ("japanese", "日本語の組版テスト"),
            ("chinese", "中文排版测试"),
            ("emoji · zwj sequence", "👩‍👩‍👧‍👦 👨🏽‍🚀 🇰🇷"),
        };

        // ------------------------------------------------------------ state

        private readonly List<OneTextLabel> _all = new List<OneTextLabel>();
        private readonly List<OneTextLabel> _entrance = new List<OneTextLabel>();
        private readonly List<OneTextLabel> _stress = new List<OneTextLabel>();
        private readonly StringBuilder _scratch = new StringBuilder(2048);

        private DemoStatsPanel _stats;
        private DemoAtlasViewer _atlas;
        private RectTransform _stressField;
        private GridLayoutGroup _stressGrid;
        private int _stressOverflow;
        private OneTextLabel _stressCaption;
        private OneTextLabel _rasteriseLabel;
        private int _rasterisedSoFar;
        private bool _precise;

        /// <summary>
        /// Set by <see cref="DemoShell"/> before this component is enabled, to
        /// borrow the tour as one tab of the principles demo rather than let it
        /// stand up a second canvas over the first.
        /// </summary>
        [System.NonSerialized] private RectTransform _hostedIn;

        internal void HostIn(RectTransform host, OneTextDemoFonts fonts)
        {
            _hostedIn = host;
            if (fonts != null) _fonts = fonts;
        }

        private void Awake()
        {
            // Hosted: the shell owns the canvas, the chrome and the fonts, and
            // this builds its three columns into the rect it was given. Alone:
            // the original behaviour, so the tour still runs as its own scene.
            if (_hostedIn != null)
            {
                BuildEffectsColumn(_hostedIn);
                BuildMarkupColumn(_hostedIn);
                BuildRightColumn(_hostedIn);
                return;
            }

            _fonts.Prepare();
            var canvas = BuildCanvas();
            var content = BuildChrome(canvas);
            BuildEffectsColumn(content);
            BuildMarkupColumn(content);
            BuildRightColumn(content);
        }

        // ----------------------------------------------------------- canvas

        private RectTransform BuildCanvas()
        {
            var go = new GameObject("Demo Canvas", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            // Match width rather than splitting the difference: the three
            // columns are the layout, and a narrow window should shorten them
            // rather than shrink the type.
            scaler.matchWidthOrHeight = 1f;

            var background = DemoUi.Box("background", go.transform, new Color32(0x01, 0x04, 0x09, 0xFF));
            DemoUi.Fill((RectTransform)background.transform);
            return (RectTransform)go.transform;
        }

        /// <summary>Header and footer; returns the rect the columns live in.</summary>
        private RectTransform BuildChrome(RectTransform canvas)
        {
            var header = DemoUi.Rect("header", canvas);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.sizeDelta = new Vector2(0f, 46f);

            var title = Track(DemoUi.Label("title", header,
                "OneText <color=" + DemoUi.DimHex + ">· every language, shaped correctly</color>",
                20f, DemoUi.Ink, _fonts));
            var titleRect = DemoUi.Fill((RectTransform)title.transform);
            titleRect.offsetMin = new Vector2(16f, 0f);
            titleRect.offsetMax = new Vector2(-16f, 0f);
            title.VerticalAlignment = VerticalAlignment.Middle;
            title.Wrap = TextWrap.NoWrap;

            var content = DemoUi.Rect("content", canvas);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = new Vector2(8f, 8f);
            content.offsetMax = new Vector2(-8f, -46f);
            return content;
        }

        private static RectTransform Column(RectTransform parent, string name, float x0, float x1,
            float y0 = 0f, float y1 = 1f)
        {
            var column = DemoUi.Rect(name, parent);
            column.anchorMin = new Vector2(x0, y0);
            column.anchorMax = new Vector2(x1, y1);
            column.offsetMin = new Vector2(4f, 4f);
            column.offsetMax = new Vector2(-4f, -4f);
            return column;
        }

        // ---------------------------------------------------------- effects

        private void BuildEffectsColumn(RectTransform content)
        {
            var panel = Column(content, "effects", 0f, 0.38f);
            var body = DemoUi.PanelWithTitle("panel", panel,
                "effects · " + (LoopingEffects.Length + EntranceEffects.Length) +
                " built in, all markup", _fonts);
            DemoUi.Fill((RectTransform)body.parent, 0f);
            var list = DemoUi.ScrollColumn(body, 2f, 10f);

            foreach (var (tag, note) in LoopingEffects)
                EffectRow(list, tag, note, entrance: false);

            DemoUi.Rule(list);
            Track(DemoUi.Label("entrance-head", list,
                "<color=" + DemoUi.DimHex + ">these settle and stop — press Replay</color>", 12f,
                DemoUi.Dim, _fonts));

            foreach (var (tag, note) in EntranceEffects)
                EffectRow(list, tag, note, entrance: true);
        }

        private void EffectRow(RectTransform list, string tag, string note, bool entrance)
        {
            string markup = "<" + tag + ">" + tag + " effect</" + tag + ">";
            var label = DemoUi.Row(list, "<" + tag + ">  " + note, markup, 24f, _fonts,
                out var caption);
            Track(caption);
            Track(label);
            if (entrance) _entrance.Add(label);
        }

        // ----------------------------------------------- markup and scripts

        private void BuildMarkupColumn(RectTransform content)
        {
            var top = Column(content, "markup", 0.38f, 0.66f, 0.36f, 1f);
            var body = DemoUi.PanelWithTitle("panel", top,
                "markup and writing systems", _fonts);
            DemoUi.Fill((RectTransform)body.parent, 0f);
            var list = DemoUi.ScrollColumn(body, 2f, 10f);

            foreach (var (caption, markup) in Markup)
            {
                Track(DemoUi.Row(list, caption, markup, 20f, _fonts, out var captionLabel));
                Track(captionLabel);
            }

            DemoUi.Rule(list);

            foreach (var (caption, text) in Scripts)
            {
                Track(DemoUi.Row(list, caption, text, 22f, _fonts, out var captionLabel));
                Track(captionLabel);
            }

            DemoUi.Rule(list);

            // Ruby and vertical each need a property rather than a tag, so they
            // get built rather than listed.
            var ruby = DemoUi.Row(list, "ruby · placed by the W3C rules",
                "<ruby=かんじ>漢字</ruby>を<ruby=よ>読</ruby>む", 24f, _fonts, out var rubyCaption);
            Track(rubyCaption);
            Track(ruby);

            var vertical = DemoUi.Row(list, "vertical · UAX #50, same line breaker",
                "縦書きのテスト", 22f, _fonts, out var verticalCaption);
            verticalCaption.Text = "vertical · UAX #50, same line breaker";
            vertical.WritingMode = TextWritingMode.VerticalRightToLeft;
            var verticalElement = vertical.gameObject.AddComponent<LayoutElement>();
            verticalElement.minHeight = 150f;
            verticalElement.preferredHeight = 150f;
            Track(verticalCaption);
            Track(vertical);

            BuildStressField(content);
        }

        // ----------------------------------------------------- stress field

        private void BuildStressField(RectTransform content)
        {
            var panel = Column(content, "stress", 0.38f, 0.66f, 0f, 0.36f);
            var body = DemoUi.PanelWithTitle("panel", panel,
                "stress field · watch the batch count not move", _fonts);
            DemoUi.Fill((RectTransform)body.parent, 0f);

            _stressCaption = Track(DemoUi.Label("caption", body,
                "empty — press + " + _stressStep, DemoUi.Caption, DemoUi.Dim, _fonts));
            var captionRect = (RectTransform)_stressCaption.transform;
            captionRect.anchorMin = new Vector2(0f, 1f);
            captionRect.anchorMax = new Vector2(1f, 1f);
            captionRect.pivot = new Vector2(0.5f, 1f);
            captionRect.sizeDelta = new Vector2(-20f, 22f);
            captionRect.anchoredPosition = new Vector2(0f, -4f);

            _stressField = DemoUi.Rect("field", body);
            _stressField.anchorMin = Vector2.zero;
            _stressField.anchorMax = Vector2.one;
            _stressField.offsetMin = new Vector2(10f, 10f);
            _stressField.offsetMax = new Vector2(-10f, -26f);

            _stressGrid = _stressField.gameObject.AddComponent<GridLayoutGroup>();
            _stressGrid.cellSize = BaseCell;
            _stressGrid.spacing = new Vector2(BaseSpacing, BaseSpacing);
            _stressGrid.childAlignment = TextAnchor.UpperLeft;
        }

        private static readonly Vector2 BaseCell = new Vector2(52f, 18f);
        private const float BaseSpacing = 3f;
        private const float BaseStressFontSize = 11f;
        private const float MinimumScale = 0.34f;

        /// <summary>
        /// Shrinks the cells until every stress label fits inside the field.
        ///
        /// Not cosmetic. A grid that spills past its panel was the one thing in
        /// this demo that made the batch count move, and it moved for a reason
        /// that has nothing to do with text: spilled geometry stops merging
        /// with what is drawn after it. Measured, the step is one batch and one
        /// set-pass call, it lands exactly when the first label crosses the
        /// edge, and it does not grow as the spill does.
        ///
        /// Which means an overflowing grid here would be the demo slandering
        /// the thing it exists to demonstrate — a cost the reader would read as
        /// "labels are getting expensive" when it is "the sample laid its own
        /// panel out badly". So the cells shrink instead, everything stays
        /// inside, everything is still drawn, and the batch count says what is
        /// actually true.
        /// </summary>
        private void FitStressGrid()
        {
            if (_stressGrid == null || _stressField == null) return;

            var size = _stressField.rect.size;
            if (size.x <= 1f || size.y <= 1f) return;

            int wanted = Mathf.Max(1, _stress.Count);

            // The escape hatch, so the finding above stays reproducible rather
            // than being a paragraph you have to take on trust: turn this off,
            // press the stress button past a hundred, and watch the batch and
            // set-pass rows each tick up by one as the grid leaves the panel.
            if (!_autoFitStressGrid)
            {
                _stressGrid.cellSize = BaseCell;
                _stressGrid.spacing = new Vector2(BaseSpacing, BaseSpacing);
                for (int i = 0; i < _stress.Count; i++)
                    if (_stress[i] != null) _stress[i].FontSize = BaseStressFontSize;
                _stressOverflow = Mathf.Max(0, wanted - Capacity(size, 1f));
                return;
            }
            float scale = MinimumScale;
            for (float k = 1f; k >= MinimumScale; k -= 0.02f)
            {
                if (Capacity(size, k) < wanted) continue;
                scale = k;
                break;
            }

            _stressGrid.cellSize = BaseCell * scale;
            _stressGrid.spacing = new Vector2(Mathf.Max(1f, BaseSpacing * scale),
                                              Mathf.Max(1f, BaseSpacing * scale));

            float fontSize = Mathf.Max(4f, BaseStressFontSize * scale);
            for (int i = 0; i < _stress.Count; i++)
                if (_stress[i] != null) _stress[i].FontSize = fontSize;

            _stressOverflow = Mathf.Max(0, wanted - Capacity(size, scale));
        }

        private static int Capacity(Vector2 size, float scale)
        {
            var cell = BaseCell * scale;
            float spacing = Mathf.Max(1f, BaseSpacing * scale);
            int columns = Mathf.Max(1, Mathf.FloorToInt((size.x + spacing) / (cell.x + spacing)));
            int rows = Mathf.Max(1, Mathf.FloorToInt((size.y + spacing) / (cell.y + spacing)));
            return columns * rows;
        }

        private void AddStress(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int index = _stress.Count;
                var label = DemoUi.Label("s" + index, _stressField,
                    "lbl " + index.ToString("000"), 11f, DemoUi.Dim, _fonts);
                label.Wrap = TextWrap.NoWrap;
                label.Precise = _precise;
                _stress.Add(label);
                Track(label);
            }
            FitStressGrid();
            UpdateStressCaption();
        }

        private void ClearStress()
        {
            foreach (var label in _stress)
            {
                _all.Remove(label);
                if (_stats != null) _stats.Counted.Remove(label);
                if (label != null) Destroy(label.gameObject);
            }
            _stress.Clear();
            FitStressGrid();
            UpdateStressCaption();
        }

        private void UpdateStressCaption()
        {
            if (_stressCaption == null) return;
            if (_stress.Count == 0)
            {
                _stressCaption.Text = "empty — press + " + _stressStep;
                return;
            }
            _stressCaption.Text = _stressOverflow > 0
                // Said out loud rather than hidden, because from here on the
                // batch count is measuring the spill and not the text.
                ? _stress.Count + " extra labels · <color=#D29922>" + _stressOverflow +
                  " past the edge — the batch count is no longer only about text</color>"
                : _stress.Count + " extra labels, same atlas, same material";
        }

        // ------------------------------------------------------ right column

        private void BuildRightColumn(RectTransform content)
        {
            BuildStatsPanel(Column(content, "stats", 0.66f, 1f, 0.42f, 1f));
            BuildAtlasPanel(Column(content, "atlas", 0.66f, 1f, 0.12f, 0.42f));
            BuildControls(Column(content, "controls", 0.66f, 1f, 0f, 0.12f));
        }

        private void BuildStatsPanel(RectTransform panel)
        {
            var body = DemoUi.PanelWithTitle("panel", panel,
                "measured · unity's counters, then ours", _fonts);
            DemoUi.Fill((RectTransform)body.parent, 0f);

            var text = Track(DemoUi.Label("text", body, "measuring…", DemoUi.Caption, DemoUi.Dim, _fonts));
            var rect = DemoUi.Fill((RectTransform)text.transform, 10f);
            rect.offsetMax = new Vector2(-10f, -6f);

            _stats = gameObject.AddComponent<DemoStatsPanel>();
            _stats.Bind(text, _fonts, transform);
            foreach (var label in _all) _stats.Counted.Add(label);
        }

        private void BuildAtlasPanel(RectTransform panel)
        {
            var body = DemoUi.PanelWithTitle("panel", panel, "the atlas, as it is", _fonts);
            DemoUi.Fill((RectTransform)body.parent, 0f);

            var caption = Track(DemoUi.Label("caption", body, "no atlas yet", 12f,
                DemoUi.Dim, _fonts));
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 0f);
            captionRect.pivot = new Vector2(0.5f, 0f);
            captionRect.sizeDelta = new Vector2(-20f, 22f);
            captionRect.anchoredPosition = new Vector2(0f, 6f);

            // Square, and centred: an atlas is square and stretching it to a
            // panel's aspect would misrepresent how full it is.
            var sheet = DemoUi.GraphicRect("sheet", body);
            sheet.anchorMin = new Vector2(0.5f, 0.5f);
            sheet.anchorMax = new Vector2(0.5f, 0.5f);
            sheet.pivot = new Vector2(0.5f, 0.5f);
            sheet.sizeDelta = new Vector2(168f, 168f);
            sheet.anchoredPosition = new Vector2(0f, 12f);
            var border = sheet.gameObject.AddComponent<Image>();
            border.color = DemoUi.Line;
            border.raycastTarget = false;

            var view = DemoUi.GraphicRect("view", sheet);
            DemoUi.Fill(view, 1f);
            var raw = view.gameObject.AddComponent<RawImage>();
            raw.raycastTarget = false;

            _atlas = gameObject.AddComponent<DemoAtlasViewer>();
            _atlas.Bind(raw, caption);
        }

        private void BuildControls(RectTransform panel)
        {
            var row = DemoUi.Rect("row", panel);
            DemoUi.Fill(row, 0f);
            var group = row.gameObject.AddComponent<GridLayoutGroup>();
            group.cellSize = new Vector2(102f, 24f);
            group.spacing = new Vector2(4f, 4f);
            group.childAlignment = TextAnchor.UpperLeft;

            DemoUi.Button("replay", row, _fonts, Replay);
            DemoUi.Button("precise ↔", row, _fonts, TogglePrecise);
            DemoUi.Button("+ " + _stressStep, row, _fonts, () => AddStress(_stressStep));
            DemoUi.Button("clear", row, _fonts, ClearStress);
            DemoUi.Button("atlas ↔", row, _fonts, () => _atlas.NextAtlas());
            DemoUi.Button("layer ▶", row, _fonts, () => _atlas.NextLayer(1));

            var rasterise = DemoUi.Button("+" + _rasteriseStep + " glyphs", row, _fonts, Rasterise);
            rasterise.gameObject.name = "rasterise";

            // The scratch label is where the demanded codepoints actually go.
            // It is off the bottom of the panel rather than disabled: a
            // disabled label lays nothing out, and laying them out is the whole
            // request.
            var scratch = DemoUi.Rect("scratch", panel);
            scratch.anchorMin = new Vector2(0f, 0f);
            scratch.anchorMax = new Vector2(1f, 0f);
            scratch.pivot = new Vector2(0.5f, 1f);
            scratch.sizeDelta = new Vector2(0f, 120f);
            scratch.anchoredPosition = new Vector2(0f, -8f);
            _rasteriseLabel = Track(DemoUi.Label("text", scratch, "", 16f,
                new Color(1f, 1f, 1f, 0.12f), _fonts));
            DemoUi.Fill((RectTransform)_rasteriseLabel.transform);
        }

        // --------------------------------------------------------- controls

        private void Replay()
        {
            // The clock, not the component: re-enabling would rebuild the mesh
            // and re-resolve the font chain, and neither is what "replay" means.
            foreach (var label in _entrance)
                if (label != null) label.AnimationTime = 0f;
        }

        private void TogglePrecise()
        {
            _precise = !_precise;
            foreach (var label in _all)
                if (label != null) label.Precise = _precise;
        }

        /// <summary>
        /// Demands a block of codepoints nothing has drawn yet, so the atlas
        /// has to rasterise them while you watch. Hangul because it is dense,
        /// contiguous, and eleven thousand syllables deep — enough to fill any
        /// budget this demo would ship with, which is the interesting failure
        /// to be able to see.
        /// </summary>
        private void Rasterise()
        {
            _scratch.Clear();
            _rasterisedSoFar += _rasteriseStep;
            for (int i = 0; i < _rasteriseStep; i++)
            {
                int syllable = 0xAC00 + (_rasterisedSoFar - _rasteriseStep + i) % 11172;
                _scratch.Append((char)syllable);
            }
            if (_rasteriseLabel != null) _rasteriseLabel.Text = _scratch.ToString();
        }

        private OneTextLabel Track(OneTextLabel label)
        {
            _all.Add(label);
            if (_stats != null) _stats.Counted.Add(label);
            return label;
        }
    }
}
