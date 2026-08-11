using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// Why one distance is not enough.
    ///
    /// A signed distance field stores, per texel, how far that texel is from
    /// the nearest edge of the shape. Reconstructing a straight edge from that
    /// is exact at any magnification, which is the whole appeal. But a corner
    /// is two edges meeting, and one number cannot say "this far from one edge
    /// and that far from another" — so near a corner the field describes a
    /// rounded blend of the two, and the corner comes back rounded off. Blow it
    /// up and the serifs melt.
    ///
    /// A multi-channel field stores three distances, to three subsets of the
    /// edges, chosen so that the two edges of any corner land in different
    /// channels. The median of the three reconstructs both edges independently,
    /// and the corner survives.
    ///
    /// The page shows the same glyph at the same size through both, and then
    /// the thing that makes it an explanation rather than a before-and-after:
    /// the field itself, with the three channels separated. Seeing R, G and B
    /// carry different edges is the moment the median trick stops being a claim.
    ///
    /// Nothing here reimplements the field. <see cref="OneTextLabel.Precise"/>
    /// is the label's own switch between the two atlases, and the channel views
    /// sample the multi-channel atlas the shader is already using.
    /// </summary>
    internal sealed class MsdfPage : DemoPage
    {
        // Small on purpose. The field is baked at the size the label asks for,
        // so a specimen set at 40 and drawn at 40 looks the same through both
        // atlases and proves nothing. Baking small and magnifying hard is the
        // case the encoding exists for, and the only one where the difference
        // is visible rather than argued. The letters are chosen for their
        // corners: the apex of A, the three vertices of W, the junction of K.
        private const float BakeSize = 13f;
        private const string Word = "AW4";

        private static readonly int AtlasId = Shader.PropertyToID("_AtlasTex");
        private static readonly int SliceId = Shader.PropertyToID("_Slice");
        private static readonly int BroadcastId = Shader.PropertyToID("_Broadcast");
        private static readonly int ChannelId = Shader.PropertyToID("_Channel");
        private static readonly int UvRectId = Shader.PropertyToID("_UvRect");

        private OneTextLabel _plain;
        private OneTextLabel _precise;
        private OneTextLabel _caption;
        private readonly RawImage[] _channels = new RawImage[3];
        private readonly Material[] _channelMaterials = new Material[3];
        private float _zoom = 22f;
        private int _framesSinceRefresh;

        // A corner of slice zero, where the first tiles land. Small enough that
        // individual texels of the field are legible, which is the whole reason
        // the channel views exist.
        private static readonly Vector4 FieldRegion = new Vector4(0f, 0f, 0.14f, 0.14f);

        internal override string Title => "MSDF";

        internal override string Claim =>
            "One distance per texel cannot describe two edges meeting, so corners round off. " +
            "Three distances can.";

        protected override void Build(RectTransform host)
        {
            var top = DemoUi.Rect("top", host);
            top.anchorMin = new Vector2(0f, 0.42f);
            top.anchorMax = new Vector2(1f, 1f);
            top.offsetMin = new Vector2(4f, 4f);
            top.offsetMax = new Vector2(-4f, -4f);

            _plain = Specimen(top, "single channel · one distance", 0f, 0.5f, precise: false);
            _precise = Specimen(top, "multi channel · three distances", 0.5f, 1f, precise: true);

            var controls = DemoUi.Rect("controls", host);
            controls.anchorMin = new Vector2(0f, 0.35f);
            controls.anchorMax = new Vector2(1f, 0.41f);
            controls.offsetMin = new Vector2(8f, 0f);
            controls.offsetMax = new Vector2(-8f, 0f);

            // Says where to look. The difference is real but it is local — it
            // lives at the apex of the A, the two inner vertices of the W and
            // the corner of the 4 — and a reader scanning the whole word sees
            // two words that look alike and moves on.
            var where = DemoUi.Label("where", host,
                "<color=#8B949E>Compare the apex of the <b>A</b>, the inner vertices of the " +
                "<b>W</b> and the corner of the <b>4</b>. Both are baked at " +
                BakeSize.ToString("0") + " px and magnified; only the encoding differs." +
                "</color>", 13f, DemoUi.Dim, Fonts);
            var whereRect = (RectTransform)where.transform;
            whereRect.anchorMin = new Vector2(0f, 0.42f);
            whereRect.anchorMax = new Vector2(1f, 0.42f);
            whereRect.pivot = new Vector2(0f, 0f);
            whereRect.anchoredPosition = new Vector2(12f, 2f);
            whereRect.sizeDelta = new Vector2(-24f, 20f);
            where.Wrap = TextWrap.NoWrap;

            var zoomLabel = DemoUi.Label("zoom", controls,
                "magnification", 13f, DemoUi.Dim, Fonts);
            var zoomRect = (RectTransform)zoomLabel.transform;
            zoomRect.anchorMin = new Vector2(0f, 0.5f);
            zoomRect.anchorMax = new Vector2(0f, 0.5f);
            zoomRect.pivot = new Vector2(0f, 0.5f);
            zoomRect.sizeDelta = new Vector2(110f, 20f);
            zoomLabel.Wrap = TextWrap.NoWrap;
            zoomLabel.VerticalAlignment = VerticalAlignment.Middle;

            var slider = DemoUi.Slider(controls, 2f, 26f, _zoom, OnZoom);
            var sliderRect = (RectTransform)slider.transform;
            sliderRect.anchorMin = new Vector2(0f, 0.5f);
            sliderRect.anchorMax = new Vector2(1f, 0.5f);
            sliderRect.pivot = new Vector2(0f, 0.5f);
            sliderRect.anchoredPosition = new Vector2(118f, 0f);
            sliderRect.sizeDelta = new Vector2(-126f, 18f);

            // ------------------------------------------------ the field itself
            var fieldColumn = DemoUi.Rect("field", host);
            fieldColumn.anchorMin = new Vector2(0f, 0f);
            fieldColumn.anchorMax = new Vector2(1f, 0.33f);
            fieldColumn.offsetMin = new Vector2(4f, 4f);
            fieldColumn.offsetMax = new Vector2(-4f, -4f);
            var fieldBody = DemoUi.PanelWithTitle("panel", fieldColumn,
                "a corner of the multi-channel sheet, one channel at a time — " +
                "the two edges of a corner are in different channels", Fonts);

            string[] names = { "red", "green", "blue" };
            for (int i = 0; i < 3; i++)
            {
                var cell = DemoUi.Rect(names[i], fieldBody);
                cell.anchorMin = new Vector2(i / 3f, 0f);
                cell.anchorMax = new Vector2((i + 1) / 3f, 1f);
                cell.offsetMin = new Vector2(8f, 26f);
                cell.offsetMax = new Vector2(-8f, -6f);

                var imageRect = DemoUi.GraphicRect("image", cell);
                imageRect.anchorMin = new Vector2(0f, 0f);
                imageRect.anchorMax = new Vector2(1f, 1f);
                imageRect.offsetMin = new Vector2(0f, 18f);
                imageRect.offsetMax = Vector2.zero;
                var raw = imageRect.gameObject.AddComponent<RawImage>();
                raw.raycastTarget = false;
                raw.texture = Texture2D.whiteTexture;
                _channels[i] = raw;

                var tag = DemoUi.Label("tag", cell, names[i], 12f, DemoUi.Dim, Fonts);
                var tagRect = (RectTransform)tag.transform;
                tagRect.anchorMin = new Vector2(0f, 0f);
                tagRect.anchorMax = new Vector2(1f, 0f);
                tagRect.pivot = new Vector2(0f, 0f);
                tagRect.anchoredPosition = Vector2.zero;
                tagRect.sizeDelta = new Vector2(0f, 16f);
                tag.Alignment = TextAlignment.Center;
            }

            _caption = DemoUi.Label("caption", fieldBody, string.Empty, 12f, DemoUi.Warn, Fonts);
            var captionRect = (RectTransform)_caption.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 0f);
            captionRect.pivot = new Vector2(0f, 0f);
            captionRect.anchoredPosition = new Vector2(10f, 2f);
            captionRect.sizeDelta = new Vector2(-20f, 20f);
            _caption.Wrap = TextWrap.NoWrap;

            OnZoom(_zoom);
        }

        private OneTextLabel Specimen(RectTransform parent, string title, float x0, float x1,
            bool precise)
        {
            var column = DemoUi.Rect(title, parent);
            column.anchorMin = new Vector2(x0, 0f);
            column.anchorMax = new Vector2(x1, 1f);
            column.offsetMin = new Vector2(4f, 0f);
            column.offsetMax = new Vector2(-4f, 0f);
            var body = DemoUi.PanelWithTitle("panel", column, title, Fonts);

            // The label keeps its small rect and small size; only the transform
            // grows. Anchored to the centre so the scale expands about the
            // glyphs rather than dragging them off one edge.
            var label = DemoUi.Label("glyph", body, Word, BakeSize, DemoUi.Ink, Fonts);
            var rect = (RectTransform)label.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(80f, 20f);
            label.Alignment = TextAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Middle;
            label.Wrap = TextWrap.NoWrap;
            label.Precise = precise;
            return label;
        }

        /// <summary>
        /// Magnifies by growing the rect rather than the font size.
        ///
        /// That distinction is the experiment. Raising FontSize re-rasterises
        /// at the new size, and a field re-baked at the size it is drawn at
        /// looks fine either way — which would demonstrate nothing. Scaling the
        /// transform keeps the baked field and asks it to reconstruct an edge
        /// far above the density it was baked at, which is precisely the case
        /// where one distance runs out and three do not.
        /// </summary>
        private void OnZoom(float zoom)
        {
            _zoom = zoom;
            if (_plain != null) _plain.transform.localScale = Vector3.one * zoom;
            if (_precise != null) _precise.transform.localScale = Vector3.one * zoom;
        }

        internal override void Tick()
        {
            if (++_framesSinceRefresh < 15) return;
            _framesSinceRefresh = 0;

            if (!SharedGlyphAtlas.PreciseAtlasExists)
            {
                if (_caption != null)
                    _caption.Text = "the multi-channel atlas has not been created yet — " +
                                    "it appears once something on this page has drawn through it";
                return;
            }

            var texture = SharedGlyphAtlas.PreciseAtlas.Texture;
            if (texture == null) return;

            for (int i = 0; i < 3; i++)
            {
                if (_channels[i] == null) continue;
                if (_channelMaterials[i] == null)
                {
                    var shader = Resources.Load<Shader>("OneTextDemo-AtlasSlice");
                    if (shader == null)
                    {
                        if (_caption != null)
                            _caption.Text = "atlas preview shader missing; the channel views are off";
                        return;
                    }
                    _channelMaterials[i] = new Material(shader) { hideFlags = HideFlags.DontSave };
                    _channels[i].material = _channelMaterials[i];
                    _channels[i].color = Color.white;
                }

                _channelMaterials[i].SetTexture(AtlasId, texture);
                _channelMaterials[i].SetFloat(SliceId, 0f);
                _channelMaterials[i].SetFloat(BroadcastId, 0f);
                _channelMaterials[i].SetFloat(ChannelId, i + 1);
                _channelMaterials[i].SetVector(UvRectId, FieldRegion);
            }

            if (_caption != null)
                _caption.Text = "Each channel carries a different subset of the edges. " +
                                "Where two of them disagree is a corner — and the median of " +
                                "the three recovers both edges instead of averaging them away.";
        }
    }
}
