using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// The uGUI plumbing the demo would otherwise repeat forty times.
    ///
    /// Everything here builds its objects in code rather than coming out of a
    /// prefab, for one reason: a scene file is a diff nobody can read. What
    /// this demo claims — that fourteen animated effects, six scripts and a
    /// hundred labels come out of one draw call — is a claim about the code,
    /// and the code that makes the claim should be the code you can read.
    ///
    /// Every piece of chrome is a <see cref="OneTextLabel"/>, including the
    /// numbers in the stats panel. Drawing a demo of a text engine with some
    /// other text engine would be a strange thing to do.
    /// </summary>
    internal static class DemoUi
    {
        internal static readonly Color Ink = new Color32(0xE6, 0xED, 0xF3, 0xFF);
        internal static readonly Color Dim = new Color32(0x8B, 0x94, 0x9E, 0xFF);
        internal static readonly Color Accent = new Color32(0x58, 0xA6, 0xFF, 0xFF);
        internal static readonly Color Warn = new Color32(0xD2, 0x99, 0x22, 0xFF);
        internal static readonly Color Bad = new Color32(0xF8, 0x51, 0x49, 0xFF);
        internal static readonly Color Panel = new Color32(0x0D, 0x11, 0x17, 0xE8);
        internal static readonly Color PanelHead = new Color32(0x16, 0x1B, 0x22, 0xFF);
        internal static readonly Color Line = new Color32(0x30, 0x36, 0x3D, 0xFF);

        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>
        /// A rect that is about to carry a <see cref="Graphic"/>, with the
        /// CanvasRenderer named up front.
        ///
        /// Not paranoia and not redundant: adding a Graphic to a bare
        /// GameObject does not reliably bring its required CanvasRenderer
        /// along, and the failure is a <c>MissingComponentException</c> from
        /// inside uGUI's rebuild rather than anything pointing at the line that
        /// caused it. The package's own golden-image harness names the
        /// component explicitly for the same reason; so does this.
        /// </summary>
        internal static RectTransform GraphicRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>A filled box. Colour alpha of zero still costs a draw, so
        /// callers that want nothing drawn should not make one.</summary>
        internal static Image Box(string name, Transform parent, Color color)
        {
            var image = GraphicRect(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// Anchors a rect to fill its parent, inset by <paramref name="pad"/>.
        /// </summary>
        internal static RectTransform Fill(RectTransform rect, float pad = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(pad, pad);
            rect.offsetMax = new Vector2(-pad, -pad);
            return rect;
        }

        internal static OneTextLabel Label(string name, Transform parent, string text,
            float size, Color color, OneTextDemoFonts fonts)
        {
            var label = GraphicRect(name, parent).gameObject.AddComponent<OneTextLabel>();
            fonts.Apply(label);
            label.Text = text;
            label.FontSize = size;
            label.color = color;
            label.Alignment = TextAlignment.Start;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Wrap = TextWrap.Wrap;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>
        /// A row in one of the scrolling columns: a dim caption on the left and
        /// the specimen under it, sized by the layout group above.
        /// </summary>
        internal static OneTextLabel Row(Transform parent, string caption, string text,
            float size, OneTextDemoFonts fonts, out OneTextLabel captionLabel)
        {
            var row = Rect(caption, parent);
            var group = row.gameObject.AddComponent<VerticalLayoutGroup>();
            group.childControlHeight = true;
            group.childControlWidth = true;
            group.childForceExpandHeight = false;
            group.childForceExpandWidth = true;
            group.spacing = 1f;
            group.padding = new RectOffset(0, 0, 4, 8);
            row.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            captionLabel = Label("caption", row, caption, 13f, Dim, fonts);
            return Label("specimen", row, text, size, Ink, fonts);
        }

        /// <summary>
        /// A panel with a title bar, filling its parent. Returns the body rect,
        /// which is everything below the title.
        ///
        /// It fills by default because every caller wanted that and every
        /// caller had to say so: a programmatic RectTransform comes out as a
        /// small box in the middle of its parent, so the line after this one
        /// was always <c>Fill(body.parent)</c>, and the two places that forgot
        /// it drew a hundred-pixel square with the panel's contents spilling
        /// across the screen around it. A caller subdividing a column into
        /// several panels still sets its own anchors afterwards and wins.
        /// </summary>
        internal static RectTransform PanelWithTitle(string name, Transform parent,
            string title, OneTextDemoFonts fonts, float titleHeight = 26f)
        {
            var panel = Fill(GraphicRect(name, parent));
            var bg = panel.gameObject.AddComponent<Image>();
            bg.color = Panel;
            bg.raycastTarget = false;

            var head = GraphicRect("title", panel);
            head.anchorMin = new Vector2(0f, 1f);
            head.anchorMax = new Vector2(1f, 1f);
            head.pivot = new Vector2(0.5f, 1f);
            head.sizeDelta = new Vector2(0f, titleHeight);
            head.anchoredPosition = Vector2.zero;
            var headBg = head.gameObject.AddComponent<Image>();
            headBg.color = PanelHead;
            headBg.raycastTarget = false;

            var titleLabel = Label("text", head, title, 13f, Ink, fonts);
            Fill((RectTransform)titleLabel.transform, 0f);
            ((RectTransform)titleLabel.transform).offsetMin = new Vector2(10f, 0f);
            ((RectTransform)titleLabel.transform).offsetMax = new Vector2(-10f, 0f);
            titleLabel.VerticalAlignment = VerticalAlignment.Middle;

            var body = Rect("body", panel);
            body.anchorMin = Vector2.zero;
            body.anchorMax = Vector2.one;
            body.offsetMin = new Vector2(0f, 0f);
            body.offsetMax = new Vector2(0f, -titleHeight);
            return body;
        }

        /// <summary>
        /// A scroll view whose content is a vertical stack. Returns the content
        /// rect to parent rows onto.
        /// </summary>
        internal static RectTransform ScrollColumn(RectTransform parent, float spacing, float pad)
        {
            var viewport = Fill(GraphicRect("viewport", parent), 0f);

            // RectMask2D rather than Mask, and the difference is not taste.
            // Mask clips with the stencil buffer, and it writes that stencil by
            // rendering its own graphic through an alpha-clipped shader — so a
            // fully transparent viewport image, which is what a scroll view
            // wants, writes no stencil at all and clips away every child.
            // RectMask2D needs no graphic, costs no stencil, and drives
            // _ClipRect, which the SDF shader already honours.
            viewport.gameObject.AddComponent<RectMask2D>();

            // Transparent, but present and raycastable: without a graphic under
            // the pointer the ScrollRect never sees a drag.
            var catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            catcher.raycastTarget = true;

            var content = Rect("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            // Top-left pivot, which is what ScrollRect assumes when it writes
            // anchoredPosition. A centred pivot on a stretched anchor leaves
            // the column sitting half a viewport to the left.
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;

            var group = content.gameObject.AddComponent<VerticalLayoutGroup>();
            group.childControlHeight = true;
            group.childControlWidth = true;
            group.childForceExpandHeight = false;
            group.childForceExpandWidth = true;
            group.spacing = spacing;
            group.padding = new RectOffset((int)pad, (int)pad, (int)pad, (int)pad);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = parent.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 28f;
            return content;
        }

        /// <summary>
        /// A button. uGUI's <see cref="Button"/> wants a Graphic to raycast
        /// against, so the background is the target and the label rides on top
        /// with raycasting off.
        /// </summary>
        internal static Button Button(string label, Transform parent, OneTextDemoFonts fonts,
            System.Action onClick, float width = 0f)
        {
            var rect = GraphicRect(label, parent);
            var bg = rect.gameObject.AddComponent<Image>();
            bg.color = PanelHead;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            var colors = button.colors;
            colors.highlightedColor = new Color32(0x21, 0x26, 0x2D, 0xFF);
            colors.pressedColor = Accent;
            button.colors = colors;
            button.onClick.AddListener(() => onClick());

            var text = Label("text", rect, label, 13f, Ink, fonts);
            Fill((RectTransform)text.transform, 0f);
            text.Alignment = TextAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Middle;
            text.Wrap = TextWrap.NoWrap;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 24f;
            element.preferredHeight = 24f;
            if (width > 0f) element.preferredWidth = width;
            return button;
        }

        /// <summary>
        /// A horizontal slider, built from parts rather than a prefab like
        /// everything else here.
        ///
        /// uGUI's <see cref="UnityEngine.UI.Slider"/> needs its fill and handle
        /// wired by hand when it is made in code: without a fillRect the value
        /// is invisible, and without a handleRect there is nothing to drag. The
        /// background is the raycast target, so a click anywhere on the track
        /// jumps the value there.
        /// </summary>
        internal static Slider Slider(Transform parent, float min, float max, float value,
            System.Action<float> onChanged)
        {
            var root = GraphicRect("slider", parent);
            var track = root.gameObject.AddComponent<Image>();
            track.color = PanelHead;
            track.raycastTarget = true;

            var fillArea = Fill(Rect("fill area", root), 0f);
            var fill = Box("fill", fillArea, Accent);
            var fillRect = Fill((RectTransform)fill.transform, 0f);

            var handleArea = Fill(Rect("handle area", root), 0f);
            var handle = Box("handle", handleArea, Ink);
            var handleRect = (RectTransform)handle.transform;
            handleRect.sizeDelta = new Vector2(10f, 0f);
            handleRect.anchorMin = new Vector2(0f, 0f);
            handleRect.anchorMax = new Vector2(0f, 1f);

            var slider = root.gameObject.AddComponent<Slider>();
            slider.targetGraphic = track;
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.value = value;
            slider.onValueChanged.AddListener(v => onChanged(v));
            return slider;
        }

        /// <summary>A one-pixel rule, for separating stanzas in a panel.</summary>
        internal static void Rule(Transform parent)
        {
            var image = Box("rule", parent, Line);
            var element = image.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 1f;
            element.preferredHeight = 1f;
        }

        internal static string Megabytes(long bytes) =>
            (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
    }
}
