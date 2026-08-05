using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// M1 proof-of-life component: shapes a string with HarfBuzz and renders
    /// the glyph outlines as line strips inside a uGUI canvas. This is a
    /// debug view — the real SDF-based renderer arrives in M2. It exists so
    /// that "correct Arabic inside a Canvas" is demonstrable from day one.
    /// </summary>
    [AddComponentMenu("OneText/Shaped Text Debug View")]
    public sealed class ShapedTextDebugView : MaskableGraphic
    {
        [Tooltip("Font file bytes (rename .ttf to .ttf.bytes to import as TextAsset).")]
        [SerializeField] private TextAsset _fontAsset;

        [TextArea]
        [SerializeField] private string _text = "مرحبا بالعالم";

        [SerializeField] private float _fontSize = 64f;
        [SerializeField] private float _outlineThickness = 1.5f;

        private FontData _font;
        private Shaper _shaper;
        private readonly List<ShapedGlyph> _glyphs = new List<ShapedGlyph>();
        private readonly GlyphOutline _outline = new GlyphOutline();

        public string Text
        {
            get => _text;
            set { _text = value; SetVerticesDirty(); }
        }

        protected override void OnDestroy()
        {
            _font?.Dispose();
            _font = null;
            _shaper?.Dispose();
            _shaper = null;
            base.OnDestroy();
        }

        private bool EnsureNativeState()
        {
            if (_fontAsset == null) return false;
            if (_font == null || !_font.IsValid)
                _font = FontData.Load(_fontAsset.bytes);
            _shaper ??= new Shaper();
            return _font.IsValid;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (string.IsNullOrEmpty(_text) || !EnsureNativeState()) return;

            _glyphs.Clear();
            _shaper.Shape(_font, _text, _glyphs);

            float scale = _fontSize / _font.UnitsPerEm;
            var rect = GetPixelAdjustedRect();
            float penX = rect.xMin;
            float baseline = rect.center.y;

            foreach (var glyph in _glyphs)
            {
                _outline.Clear();
                OutlineExtractor.Extract(_font, glyph.GlyphId, _outline);

                var origin = new Vector2(penX + glyph.XOffset * scale, baseline + glyph.YOffset * scale);
                foreach (var contour in _outline.Contours)
                {
                    for (int i = 1; i < contour.Count; i++)
                    {
                        AddLineQuad(vh,
                            origin + contour[i - 1] * scale,
                            origin + contour[i] * scale);
                    }
                }

                penX += glyph.XAdvance * scale;
            }
        }

        private void AddLineQuad(VertexHelper vh, Vector2 a, Vector2 b)
        {
            Vector2 dir = (b - a).normalized;
            Vector2 normal = new Vector2(-dir.y, dir.x) * (_outlineThickness * 0.5f);
            int start = vh.currentVertCount;
            var c = color;
            vh.AddVert(a - normal, c, Vector2.zero);
            vh.AddVert(a + normal, c, Vector2.zero);
            vh.AddVert(b + normal, c, Vector2.zero);
            vh.AddVert(b - normal, c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
