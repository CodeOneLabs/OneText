using System.Text;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// Where the glyphs actually live.
    ///
    /// Every text library says it caches glyphs. The thing worth showing is not
    /// that a cache exists but its <em>shape</em>, because the shape is what
    /// decides the draw call count. TextMesh Pro gives each font asset its own
    /// atlas and its own material, so a screen with Latin, Korean, Arabic and
    /// Hebrew on it holds four textures and issues four draws. OneText puts
    /// every face into slices of one <c>Texture2DArray</c> behind one material,
    /// so the same screen is one texture and one draw.
    ///
    /// That is a claim about storage, so this page shows the storage. The sheet
    /// on the left is the array the shader is sampling this frame — not a
    /// rendering of it, the thing itself, handed to a RawImage. Adding a script
    /// with the buttons rasterises its glyphs into the same sheet while you
    /// watch: the tile count climbs, the shelves fill, and the number of
    /// textures stays at one.
    ///
    /// The counters are the atlas's own <see cref="GlyphAtlasStats"/>, not
    /// anything recomputed here, so the packing you see and the numbers beside
    /// it cannot disagree.
    /// </summary>
    internal sealed class AtlasPage : DemoPage
    {
        private readonly struct Script
        {
            internal readonly string Name;
            internal readonly string Text;

            internal Script(string name, string text)
            {
                Name = name;
                Text = text;
            }
        }

        private static readonly Script[] Scripts =
        {
            new Script("Latin", "The quick brown fox jumps over the lazy dog"),
            new Script("한국어", "다람쥐 헌 쳇바퀴에 타고파"),
            new Script("日本語", "いろはにほへと ちりぬるを 色は匂へど"),
            new Script("العربية", "نص حكيم له سر قاطع وذو شأن"),
            new Script("ไทย", "เป็นมนุษย์สุดประเสริฐเลิศคุณค่า"),
            new Script("Ελληνικά", "Ταχίστη αλώπηξ βαφής ψημένη γη"),
            new Script("हिन्दी", "ऋषियों को सताने वाले दुष्ट"),
            new Script("עברית", "דג סקרן שט בים מאוכזב"),
        };

        private readonly StringBuilder _scratch = new StringBuilder(768);
        private readonly bool[] _on = new bool[Scripts.Length];

        private DemoAtlasViewer _viewer;
        private OneTextLabel _specimen;
        private OneTextLabel _stats;
        private OneTextLabel _note;
        private OneTextLabel _caption;
        private RectTransform _sheetRect;
        private RectTransform _sheetHolder;
        private int _framesSinceRefresh;

        internal override string Title => "Atlas";

        internal override string Claim =>
            "Every face lands in slices of one texture array behind one material — " +
            "which is why adding a script does not add a draw call.";

        protected override void Build(RectTransform host)
        {
            // ------------------------------------------------------- the sheet
            var sheetColumn = DemoUi.Rect("sheet", host);
            sheetColumn.anchorMin = new Vector2(0f, 0f);
            sheetColumn.anchorMax = new Vector2(0.46f, 1f);
            sheetColumn.offsetMin = new Vector2(4f, 4f);
            sheetColumn.offsetMax = new Vector2(-4f, -4f);
            var sheetBody = DemoUi.PanelWithTitle("panel", sheetColumn,
                "the texture array, this frame", Fonts);

            // The sheet is square and is kept square by hand in Tick rather
            // than by an AspectRatioFitter, which quietly does nothing to a
            // rect that is already anchored to stretch. A sheet drawn into a
            // tall panel without this is stretched vertically, which misreports
            // the one thing the panel exists to show: how much of a shelf a
            // glyph actually occupies.
            var sheetHolder = DemoUi.Rect("holder", sheetBody);
            sheetHolder.anchorMin = new Vector2(0f, 0f);
            sheetHolder.anchorMax = new Vector2(1f, 1f);
            sheetHolder.offsetMin = new Vector2(10f, 62f);
            sheetHolder.offsetMax = new Vector2(-10f, -10f);

            var imageRect = DemoUi.GraphicRect("sheet", sheetHolder);
            imageRect.anchorMin = imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            imageRect.pivot = new Vector2(0.5f, 0.5f);
            imageRect.sizeDelta = new Vector2(256f, 256f);
            _sheetHolder = sheetHolder;
            var raw = imageRect.gameObject.AddComponent<RawImage>();
            raw.raycastTarget = false;
            _sheetRect = imageRect;

            _caption = DemoUi.Label("caption", sheetBody, string.Empty, 12f, DemoUi.Dim, Fonts);
            var captionRect = (RectTransform)_caption.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 0f);
            captionRect.pivot = new Vector2(0f, 0f);
            captionRect.anchoredPosition = new Vector2(10f, 34f);
            captionRect.sizeDelta = new Vector2(-20f, 20f);
            _caption.Wrap = TextWrap.NoWrap;

            var sheetButtons = DemoUi.Rect("buttons", sheetBody);
            sheetButtons.anchorMin = new Vector2(0f, 0f);
            sheetButtons.anchorMax = new Vector2(1f, 0f);
            sheetButtons.pivot = new Vector2(0f, 0f);
            sheetButtons.anchoredPosition = new Vector2(10f, 6f);
            sheetButtons.sizeDelta = new Vector2(-20f, 26f);
            var buttonRow = sheetButtons.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttonRow.childControlWidth = false;
            buttonRow.childControlHeight = true;
            buttonRow.childForceExpandWidth = false;
            buttonRow.spacing = 4f;

            _viewer = Host.gameObject.AddComponent<DemoAtlasViewer>();
            _viewer.Bind(raw, _caption);
            DemoUi.Button("next sheet", sheetButtons, Fonts, () => _viewer.NextAtlas(), 96f);
            DemoUi.Button("slice −", sheetButtons, Fonts, () => _viewer.NextLayer(-1), 70f);
            DemoUi.Button("slice +", sheetButtons, Fonts, () => _viewer.NextLayer(1), 70f);

            // ------------------------------------------------ scripts and text
            var rightColumn = DemoUi.Rect("right", host);
            rightColumn.anchorMin = new Vector2(0.46f, 0f);
            rightColumn.anchorMax = new Vector2(1f, 1f);
            rightColumn.offsetMin = new Vector2(4f, 4f);
            rightColumn.offsetMax = new Vector2(-4f, -4f);

            var addBody = DemoUi.PanelWithTitle("add", rightColumn,
                "add a script and watch the same sheet fill", Fonts);
            var addRect = (RectTransform)addBody.parent;
            addRect.anchorMin = new Vector2(0f, 0.62f);
            addRect.anchorMax = new Vector2(1f, 1f);
            addRect.offsetMin = Vector2.zero;
            addRect.offsetMax = Vector2.zero;

            var toggles = DemoUi.Rect("toggles", addBody);
            toggles.anchorMin = new Vector2(0f, 1f);
            toggles.anchorMax = new Vector2(1f, 1f);
            toggles.pivot = new Vector2(0f, 1f);
            toggles.anchoredPosition = new Vector2(10f, -8f);
            toggles.sizeDelta = new Vector2(-20f, 56f);
            var grid = toggles.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(96f, 24f);
            grid.spacing = new Vector2(4f, 4f);
            for (int i = 0; i < Scripts.Length; i++)
            {
                int captured = i;
                DemoUi.Button(Scripts[i].Name, toggles, Fonts, () => Toggle(captured));
            }

            _specimen = DemoUi.Label("specimen", addBody, string.Empty, 22f, DemoUi.Ink, Fonts);
            var specimenRect = (RectTransform)_specimen.transform;
            specimenRect.anchorMin = new Vector2(0f, 0f);
            specimenRect.anchorMax = new Vector2(1f, 1f);
            specimenRect.offsetMin = new Vector2(10f, 10f);
            specimenRect.offsetMax = new Vector2(-10f, -70f);

            // ------------------------------------------------------ the numbers
            var statsBody = DemoUi.PanelWithTitle("stats", rightColumn,
                "what the atlas reports about itself", Fonts);
            var statsRect = (RectTransform)statsBody.parent;
            statsRect.anchorMin = new Vector2(0f, 0f);
            statsRect.anchorMax = new Vector2(1f, 0.6f);
            statsRect.offsetMin = Vector2.zero;
            statsRect.offsetMax = Vector2.zero;

            // Two labels, not one: the table is monospaced and must not wrap,
            // and the sentence under it must. One label cannot be both, and the
            // version that tried had its last words cut off at the panel edge.
            _stats = DemoUi.Label("rows", statsBody, string.Empty, 13f, DemoUi.Ink, Fonts);
            var statsTextRect = (RectTransform)_stats.transform;
            statsTextRect.anchorMin = new Vector2(0f, 0f);
            statsTextRect.anchorMax = new Vector2(1f, 1f);
            statsTextRect.offsetMin = new Vector2(10f, 62f);
            statsTextRect.offsetMax = new Vector2(-10f, -10f);
            _stats.Wrap = TextWrap.NoWrap;

            _note = DemoUi.Label("note", statsBody,
                "Turn on every script above. The tile count climbs and the shelves fill, " +
                "and the two rows that decide the draw call — texture arrays and materials — " +
                "do not move.", 13f, DemoUi.Dim, Fonts);
            var noteRect = (RectTransform)_note.transform;
            noteRect.anchorMin = new Vector2(0f, 0f);
            noteRect.anchorMax = new Vector2(1f, 0f);
            noteRect.pivot = new Vector2(0f, 0f);
            noteRect.anchoredPosition = new Vector2(10f, 8f);
            noteRect.sizeDelta = new Vector2(-20f, 48f);

            _on[0] = true;
            Rebuild();
        }

        private void Toggle(int index)
        {
            _on[index] = !_on[index];
            Rebuild();
        }

        private void Rebuild()
        {
            _scratch.Clear();
            for (int i = 0; i < Scripts.Length; i++)
            {
                if (!_on[i]) continue;
                if (_scratch.Length > 0) _scratch.Append('\n');
                _scratch.Append(Scripts[i].Text);
            }
            if (_scratch.Length == 0)
                _scratch.Append("<color=#8B949E>nothing selected — the sheet keeps what it " +
                                "already rasterised until something evicts it</color>");
            _specimen.Text = _scratch.ToString();
        }

        internal override void Tick()
        {
            // Square the sheet against whatever the panel currently is, every
            // frame, so a resized window keeps the packing honest.
            if (_sheetRect != null && _sheetHolder != null)
            {
                float side = Mathf.Min(_sheetHolder.rect.width, _sheetHolder.rect.height);
                if (side > 1f) _sheetRect.sizeDelta = new Vector2(side, side);
            }

            // Frames rather than seconds, matching the rest of the demo: a
            // clock is a thing that can stop, and a paused editor should not
            // make the panel look frozen for a different reason than it is.
            if (++_framesSinceRefresh < 15) return;
            _framesSinceRefresh = 0;

            _scratch.Clear();
            if (!SharedGlyphAtlas.Exists)
            {
                _stats.Text = "no atlas yet — nothing has been drawn";
                return;
            }

            var atlas = SharedGlyphAtlas.Atlas;
            var stats = atlas.GetStats();
            var settings = OneTextSettings.Instance != null
                ? OneTextSettings.Instance.AtlasSettings
                : GlyphAtlasSettings.Default;

            int sheets = (SharedGlyphAtlas.Exists ? 1 : 0)
                + (SharedGlyphAtlas.PreciseAtlasExists ? 1 : 0)
                + (SharedGlyphAtlas.ColorAtlasExists ? 1 : 0);

            _scratch.Append("<mspace=0.62em>");
            Row("texture arrays", sheets + "  <color=#8B949E>(sdf" +
                (SharedGlyphAtlas.PreciseAtlasExists ? ", msdf" : "") +
                (SharedGlyphAtlas.ColorAtlasExists ? ", colour" : "") + ")</color>");
            Row("materials", "1  <color=#8B949E>shared by every label</color>");
            Row("slice size", settings.TextureSize + " × " + settings.TextureSize);
            Row("slices", settings.LayerCount.ToString());
            Row("glyph tiles", stats.TileCount.ToString());
            Row("shelves", stats.ShelfCount.ToString());
            Row("occupancy", stats.CapacityPixels > 0
                ? (100f * stats.UsedPixels / stats.CapacityPixels).ToString("0.0") + " %"
                : "—");
            Row("memory", DemoUi.Megabytes(stats.MemoryBytes));
            if (stats.PrewarmedTiles > 0)
                Row("prewarmed", stats.PrewarmedTiles + " of " + stats.TileCount);
            if (stats.Evictions > 0) Row("evictions", stats.Evictions.ToString());
            if (stats.Compactions > 0) Row("compactions", stats.Compactions.ToString());
            _scratch.Append("</mspace>");
            _stats.Text = _scratch.ToString();
        }

        private void Row(string name, string value)
        {
            _scratch.Append(name.PadRight(16)).Append(value).Append('\n');
        }
    }
}
