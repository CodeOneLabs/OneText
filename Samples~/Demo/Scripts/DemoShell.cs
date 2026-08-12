using System.Collections.Generic;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// The demo's frame: a canvas, a row of tabs, and one <see cref="DemoPage"/>
    /// visible at a time.
    ///
    /// The order of the tabs is the order of the argument, not the order the
    /// features were written. Shaping comes first because it is the claim with
    /// the most damning wrong answer, and because everything after it —
    /// the atlas holds shaped glyphs, the fields render shaped glyphs, vertical
    /// text re-shapes them — depends on the reader having accepted it.
    ///
    /// Hidden pages are built once and then left alone: they keep their objects
    /// but stop being ticked, so the page reading atlas statistics every frame
    /// costs nothing while somebody is looking at variable fonts.
    /// </summary>
    [AddComponentMenu("OneText/Demo/OneText Principles")]
    public sealed class DemoShell : MonoBehaviour
    {
        [Tooltip("Faces the demo draws with. Every page reads this one stack.")]
        [SerializeField] private OneTextDemoFonts _fonts = new OneTextDemoFonts();

        [Tooltip("Include the original feature tour as a final tab.")]
        [SerializeField] private bool _includeOverview = true;

        private readonly List<DemoPage> _pages = new List<DemoPage>();
        private readonly List<RectTransform> _hosts = new List<RectTransform>();
        private readonly List<Image> _tabs = new List<Image>();
        private OneTextLabel _claim;
        private int _current = -1;

        private void Awake()
        {
            _fonts.Prepare();

            var canvas = BuildCanvas();
            var header = BuildHeader(canvas);
            var body = DemoUi.Rect("body", canvas);
            body.anchorMin = Vector2.zero;
            body.anchorMax = Vector2.one;
            body.offsetMin = new Vector2(8f, 8f);
            body.offsetMax = new Vector2(-8f, -104f);

            Add(new ShapingPage(), body, header);
            Add(new AtlasPage(), body, header);
            Add(new VariablePage(), body, header);
            Add(new MsdfPage(), body, header);
            Add(new VerticalPage(), body, header);
            if (_includeOverview) Add(new OverviewPage(this), body, header);

            Show(0);
        }

        private void Update()
        {
            if (_current >= 0 && _current < _pages.Count) _pages[_current].Tick();
        }

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
            scaler.matchWidthOrHeight = 1f;

            var background = DemoUi.Box("background", go.transform,
                new Color32(0x01, 0x04, 0x09, 0xFF));
            DemoUi.Fill((RectTransform)background.transform);
            return (RectTransform)go.transform;
        }

        /// <summary>Title, tab row, and the claim line the pages write into.</summary>
        private RectTransform BuildHeader(RectTransform canvas)
        {
            var header = DemoUi.Rect("header", canvas);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.sizeDelta = new Vector2(0f, 104f);

            var title = DemoUi.Label("title", header,
                "OneText <color=" + DemoUi.DimHex +
                ">· what a text engine actually has to do</color>", 22f, DemoUi.Ink, _fonts);
            var titleRect = (RectTransform)title.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(16f, -8f);
            titleRect.sizeDelta = new Vector2(-32f, 28f);
            title.Wrap = TextWrap.NoWrap;

            var tabs = DemoUi.Rect("tabs", header);
            tabs.anchorMin = new Vector2(0f, 1f);
            tabs.anchorMax = new Vector2(1f, 1f);
            tabs.pivot = new Vector2(0f, 1f);
            tabs.anchoredPosition = new Vector2(16f, -40f);
            tabs.sizeDelta = new Vector2(-32f, 28f);
            var row = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = false;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.spacing = 4f;

            _claim = DemoUi.Label("claim", header, string.Empty, DemoUi.Claim, DemoUi.Dim, _fonts);
            var claimRect = (RectTransform)_claim.transform;
            claimRect.anchorMin = new Vector2(0f, 1f);
            claimRect.anchorMax = new Vector2(1f, 1f);
            claimRect.pivot = new Vector2(0f, 1f);
            claimRect.anchoredPosition = new Vector2(16f, -72f);
            claimRect.sizeDelta = new Vector2(-32f, 26f);
            _claim.Wrap = TextWrap.NoWrap;

            return tabs;
        }

        private void Add(DemoPage page, RectTransform body, RectTransform tabRow)
        {
            int index = _pages.Count;
            _pages.Add(page);

            var host = DemoUi.Fill(DemoUi.Rect(page.Title, body));
            host.gameObject.SetActive(false);
            _hosts.Add(host);

            var button = DemoUi.Button(page.Title, tabRow, _fonts, () => Show(index), 118f);
            _tabs.Add(button.targetGraphic as Image);

            page.Attach(host, _fonts);
        }

        private void Show(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].gameObject.SetActive(i == index);
                if (_tabs[i] != null)
                    _tabs[i].color = i == index ? DemoUi.Accent : DemoUi.PanelHead;
            }
            _current = index;
            if (_claim != null) _claim.Text = _pages[index].Claim;
        }

        /// <summary>
        /// The font stack every page draws through, for a caller configuring it
        /// from code rather than the inspector — see
        /// <see cref="OneTextDemoFonts.UseBytes"/>. Only useful before the
        /// shell builds itself, which happens in <c>Awake</c>: create the
        /// object deactivated, set this, then activate it.
        /// </summary>
        public OneTextDemoFonts Fonts => _fonts;

        internal OneTextDemoFonts SharedFonts => _fonts;

        /// <summary>
        /// Switches tabs from code, for the capture harness. Public because
        /// photographing every page is the only way to know they all still
        /// build, and a screenshot of one tab proves one tab.
        /// </summary>
        public void ShowPage(int index) => Show(index);

        /// <summary>Number of tabs, so a caller can walk them all.</summary>
        public int PageCount => _pages.Count;

        /// <summary>Tab label at <paramref name="index"/>, for naming a capture.</summary>
        public string PageTitle(int index) =>
            index >= 0 && index < _pages.Count ? _pages[index].Title : string.Empty;
    }
}
