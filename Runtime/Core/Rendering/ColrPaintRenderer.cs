using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Draws a COLR version 1 glyph: walks its paint graph and composites the
    /// result into a tile of straight RGBA, the shape every other colour
    /// glyph arrives in.
    ///
    /// A version 0 glyph is a stack of outlines, each one flat colour. A
    /// version 1 glyph is a small program. Its leaves are fills (a solid, or a
    /// linear, radial or sweep gradient along a colour line); above them are
    /// the nodes that say where a fill lands (an outline to clip to, another
    /// colour glyph to reuse, an affine transform in eight spellings) and how
    /// two sub-pictures combine (twenty-eight compositing and blend modes).
    /// So this is an interpreter with a canvas, and the one walker serves
    /// three questions — how big the picture is, whether it asks for the text
    /// colour, and what it looks like — so that the three cannot disagree
    /// about what a paint table means.
    ///
    /// Everything is evaluated per texel on the CPU. A colour glyph is baked
    /// once per size and kept in the colour atlas, and an emoji is a few dozen
    /// nodes, so there is nothing here worth a job.
    ///
    /// Colour arithmetic is in sRGB-encoded values, not linear light, and a
    /// colour line is interpolated unpremultiplied: that is what the format
    /// asks for and what the browsers that draw these fonts do.
    ///
    /// A font is untrusted input. The graph may be cyclic, enormous or cut
    /// short, so the walk has a depth limit and a node budget, and a glyph
    /// that exceeds either — or reads past the end of the table — is
    /// abandoned whole rather than drawn in part.
    /// </summary>
    internal sealed class ColrPaintRenderer
    {
        private const int MaxDepth = 64;
        private const int MaxNodes = 16384;
        private const int MaxStops = 256;
        // A node that draws touches every texel of the tile, so on a large
        // tile the node budget alone would still allow minutes of work. This
        // is texels times nodes: thousands of nodes at a UI size, hundreds at
        // the largest tile, and in either case far more than a real glyph has.
        private const long MaxTexelWork = 1L << 29;
        private const uint ForegroundColor = 0xFFFF;

        private enum Pass { Probe, Measure, Render }

        // ------------------------------------------------------------ transform

        /// <summary>x' = xx·x + xy·y + dx, y' = yx·x + yy·y + dy: the format's own letters.</summary>
        internal struct Affine
        {
            public float XX, YX, XY, YY, DX, DY;

            public static readonly Affine Identity = new Affine { XX = 1f, YY = 1f };

            public Vector2 Apply(Vector2 p) =>
                new Vector2(XX * p.x + XY * p.y + DX, YX * p.x + YY * p.y + DY);

            /// <summary><paramref name="inner"/> first, then this.</summary>
            public Affine Then(in Affine inner) => new Affine
            {
                XX = XX * inner.XX + XY * inner.YX,
                YX = YX * inner.XX + YY * inner.YX,
                XY = XX * inner.XY + XY * inner.YY,
                YY = YX * inner.XY + YY * inner.YY,
                DX = XX * inner.DX + XY * inner.DY + DX,
                DY = YX * inner.DX + YY * inner.DY + DY,
            };

            public bool TryInvert(out Affine inverse)
            {
                inverse = default;
                float det = XX * YY - XY * YX;
                if (Mathf.Abs(det) < 1e-12f || float.IsNaN(det) || float.IsInfinity(det)) return false;
                float inv = 1f / det;
                inverse.XX = YY * inv;
                inverse.YX = -YX * inv;
                inverse.XY = -XY * inv;
                inverse.YY = XX * inv;
                inverse.DX = -(inverse.XX * DX + inverse.XY * DY);
                inverse.DY = -(inverse.YX * DX + inverse.YY * DY);
                return true;
            }

            /// <summary>How much this can stretch a length: what a curve must be flattened for.</summary>
            public float MaxScale => Mathf.Max(
                Mathf.Sqrt(XX * XX + YX * YX), Mathf.Sqrt(XY * XY + YY * YY));

            public static Affine Translate(float dx, float dy) =>
                new Affine { XX = 1f, YY = 1f, DX = dx, DY = dy };

            public static Affine Scale(float sx, float sy) => new Affine { XX = sx, YY = sy };

            /// <summary>Counter-clockwise, in half turns: the unit the table stores.</summary>
            public static Affine Rotate(float halfTurns)
            {
                float radians = halfTurns * Mathf.PI;
                float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
                return new Affine { XX = c, YX = s, XY = -s, YY = c };
            }

            public static Affine Skew(float xHalfTurns, float yHalfTurns) => new Affine
            {
                XX = 1f,
                YX = Mathf.Tan(yHalfTurns * Mathf.PI),
                XY = -Mathf.Tan(xHalfTurns * Mathf.PI),
                YY = 1f,
            };

            public Affine Around(float cx, float cy) =>
                Translate(cx, cy).Then(this).Then(Translate(-cx, -cy));
        }

        // ---------------------------------------------------------------- state

        private ColrTable _colr;
        private FontData _font;
        private List<Color32> _palette;
        private Color _textColor;
        private Pass _pass;
        private int _budget;
        private long _work;
        private bool _abandoned;

        // Probe
        private bool _followsText;

        // Measure
        private Vector2 _min, _max;

        // Render: the tile, in font units and texels.
        private float _pixelsPerUnit;
        private Vector2 _origin;
        private int _width, _height, _texels;

        // Scratch, kept between glyphs. Planes are premultiplied RGBA in
        // floats; masks are coverage. Both are handed out as a stack, because
        // the walk that needs them is a recursion.
        private readonly List<float[]> _planes = new List<float[]>();
        private readonly List<float[]> _masks = new List<float[]>();
        private int _planesInUse, _masksInUse;
        private readonly GlyphOutline _outline = new GlyphOutline();

        private struct Stop
        {
            public float Offset;
            public Color Color; // straight, text colour and alpha already resolved
        }

        private readonly List<Stop> _stops = new List<Stop>(8);
        private int _extend;

        private static readonly Comparison<Stop> s_byOffset = (a, b) => a.Offset.CompareTo(b.Offset);

        // ------------------------------------------------------------ questions

        /// <summary>Whether any fill in this glyph's graph takes the text colour.</summary>
        public bool FollowsText(ColrTable colr, uint paint)
        {
            Begin(colr, null, null, default, Pass.Probe);
            Walk(paint, Affine.Identity, null, null, 0);
            return End() && _followsText;
        }

        /// <summary>
        /// Draws one glyph. False when there is nothing to show for it — no
        /// ink, a tile too large, or a graph that had to be abandoned — and
        /// the glyph should go down the next path.
        /// </summary>
        public bool TryRender(ColrTable colr, FontData font, uint glyphId, uint paint,
            float pixelsPerUnit, Color32 textColor, List<Color32> palette, out ColorGlyph result)
        {
            result = default;
            if (pixelsPerUnit <= 0f) return false;

            // The font's clip box is the picture's size when it gives one.
            // Without one the graph is walked once for the outlines it clips
            // to, which is everything a fill can reach.
            bool boxed = colr.TryClipBox(glyphId, out var min, out var max);
            if (!boxed)
            {
                Begin(colr, font, palette, textColor, Pass.Measure);
                _min = new Vector2(float.MaxValue, float.MaxValue);
                _max = new Vector2(float.MinValue, float.MinValue);
                Walk(paint, Affine.Identity, null, null, 0);
                if (!End() || _min.x >= _max.x || _min.y >= _max.y) return false;
                min = _min;
                max = _max;
            }

            const int pad = 1;
            int width = Mathf.CeilToInt((max.x - min.x) * pixelsPerUnit) + pad * 2;
            int height = Mathf.CeilToInt((max.y - min.y) * pixelsPerUnit) + pad * 2;
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024) return false;

            Begin(colr, font, palette, textColor, Pass.Render);
            _pixelsPerUnit = pixelsPerUnit;
            _origin = new Vector2(min.x - pad / pixelsPerUnit, min.y - pad / pixelsPerUnit);
            _width = width;
            _height = height;
            _texels = width * height;

            var target = RentPlane();
            float[] clip = null;
            if (boxed)
            {
                // The box is exact; the padding around it is not part of the
                // picture, and a fill with no outline of its own would
                // otherwise run to the edge of the tile.
                clip = RentMask();
                BoxMask(min, max, Affine.Identity, clip);
            }
            Walk(paint, Affine.Identity, clip, target, 0);
            if (clip != null) ReturnMask();

            bool drawn = End();
            Color32[] pixels = null;
            if (drawn)
            {
                pixels = new Color32[_texels];
                bool any = false;
                for (int i = 0, c = 0; i < _texels; i++, c += 4)
                {
                    float a = target[c + 3];
                    if (a <= 0f) continue;
                    any = true;
                    float inv = 1f / a;
                    pixels[i] = new Color32(
                        ToByte(target[c] * inv), ToByte(target[c + 1] * inv),
                        ToByte(target[c + 2] * inv), ToByte(a));
                }
                drawn = any;
            }
            ReturnPlane();
            if (!drawn) return false;

            result = new ColorGlyph
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                OriginUnits = _origin,
                UnitsPerPixel = 1f / pixelsPerUnit,
            };
            return true;
        }

        private static byte ToByte(float value) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

        private void Begin(ColrTable colr, FontData font, List<Color32> palette, Color32 textColor, Pass pass)
        {
            _colr = colr;
            _font = font;
            _palette = palette;
            _textColor = textColor;
            _pass = pass;
            _budget = MaxNodes;
            _work = MaxTexelWork;
            _texels = 0;
            _abandoned = false;
            _followsText = false;
            _planesInUse = 0;
            _masksInUse = 0;
            colr.Truncated = false;
        }

        private bool End()
        {
            bool sound = !_abandoned && !_colr.Truncated;
            _colr.Truncated = false;
            _colr = null;
            _font = null;
            _palette = null;
            return sound;
        }

        // ----------------------------------------------------------------- walk

        private void Walk(uint paint, in Affine transform, float[] clip, float[] target, int depth)
        {
            if (_abandoned) return;
            if (depth > MaxDepth || --_budget < 0 || (_work -= _texels) < 0)
            {
                _abandoned = true;
                return;
            }

            var colr = _colr;
            uint format = colr.U8(paint);
            if (colr.Truncated) return;

            switch (format)
            {
                case 1: // PaintColrLayers
                {
                    uint count = colr.U8(paint + 1);
                    uint first = colr.U32(paint + 2);
                    for (uint i = 0; i < count && !_abandoned; i++)
                        if (colr.TryLayer(first + i, out uint layer))
                            Walk(layer, transform, clip, target, depth + 1);
                    break;
                }

                case 2: // PaintSolid
                case 3: // PaintVarSolid
                {
                    uint index = colr.U16(paint + 1);
                    float alpha = colr.F2Dot14(paint + 3);
                    if (index == ForegroundColor) _followsText = true;
                    if (_pass == Pass.Render) FillSolid(target, clip, Resolve(index, alpha));
                    break;
                }

                case 4: case 5: // PaintLinearGradient, and its variable form
                case 6: case 7: // PaintRadialGradient
                case 8: case 9: // PaintSweepGradient
                {
                    bool variable = (format & 1) != 0;
                    if (!ReadColorLine(paint + colr.U24(paint + 1), variable)) break;
                    if (_pass == Pass.Render) FillGradient((int)format & ~1, paint, transform, clip, target);
                    break;
                }

                case 10: // PaintGlyph: the child, wherever this outline is
                {
                    uint child = colr.U24(paint + 1);
                    uint glyph = colr.U16(paint + 4);
                    if (child == 0 || colr.Truncated) break;

                    if (_pass == Pass.Probe)
                    {
                        Walk(paint + child, transform, null, null, depth + 1);
                    }
                    else if (_pass == Pass.Measure)
                    {
                        // What is inside the outline cannot reach outside it,
                        // so the outline's own extent is all there is to add.
                        if (Flatten(glyph, transform, 0f)) GrowToOutline();
                    }
                    else
                    {
                        var mask = RentMask();
                        if (Flatten(glyph, transform, _pixelsPerUnit))
                        {
                            ColorGlyphs.FillOutline(_outline, _pixelsPerUnit, _origin, _width, _height, mask);
                            if (clip != null)
                                for (int i = 0; i < _texels; i++) mask[i] *= clip[i];
                            Walk(paint + child, transform, mask, target, depth + 1);
                        }
                        ReturnMask();
                    }
                    break;
                }

                case 11: // PaintColrGlyph: another glyph's whole graph, here
                {
                    uint glyph = colr.U16(paint + 1);
                    if (colr.Truncated || !colr.TryFindPaint(glyph, out uint root)) break;

                    // The reused glyph keeps its own clip box, carried through
                    // whatever transform it is being reused under.
                    if (_pass == Pass.Render && colr.TryClipBox(glyph, out var lo, out var hi))
                    {
                        var mask = RentMask();
                        BoxMask(lo, hi, transform, mask);
                        if (clip != null)
                            for (int i = 0; i < _texels; i++) mask[i] *= clip[i];
                        Walk(root, transform, mask, target, depth + 1);
                        ReturnMask();
                    }
                    else
                    {
                        Walk(root, transform, clip, target, depth + 1);
                    }
                    break;
                }

                case 12: case 13: // PaintTransform
                {
                    uint child = colr.U24(paint + 1);
                    uint matrix = paint + colr.U24(paint + 4);
                    var local = new Affine
                    {
                        // Affine2x3 is six numbers and no format byte.
                        XX = colr.Fixed(matrix), YX = colr.Fixed(matrix + 4),
                        XY = colr.Fixed(matrix + 8), YY = colr.Fixed(matrix + 12),
                        DX = colr.Fixed(matrix + 16), DY = colr.Fixed(matrix + 20),
                    };
                    Descend(paint, child, transform, local, clip, target, depth);
                    break;
                }

                case 14: case 15: // PaintTranslate
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Translate(colr.I16(paint + 4), colr.I16(paint + 6)), clip, target, depth);
                    break;

                case 16: case 17: // PaintScale
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Scale(colr.F2Dot14(paint + 4), colr.F2Dot14(paint + 6)), clip, target, depth);
                    break;

                case 18: case 19: // PaintScaleAroundCenter
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Scale(colr.F2Dot14(paint + 4), colr.F2Dot14(paint + 6))
                            .Around(colr.I16(paint + 8), colr.I16(paint + 10)), clip, target, depth);
                    break;

                case 20: case 21: // PaintScaleUniform
                {
                    float scale = colr.F2Dot14(paint + 4);
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Scale(scale, scale), clip, target, depth);
                    break;
                }

                case 22: case 23: // PaintScaleUniformAroundCenter
                {
                    float scale = colr.F2Dot14(paint + 4);
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Scale(scale, scale).Around(colr.I16(paint + 6), colr.I16(paint + 8)),
                        clip, target, depth);
                    break;
                }

                case 24: case 25: // PaintRotate
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Rotate(colr.F2Dot14(paint + 4)), clip, target, depth);
                    break;

                case 26: case 27: // PaintRotateAroundCenter
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Rotate(colr.F2Dot14(paint + 4))
                            .Around(colr.I16(paint + 6), colr.I16(paint + 8)), clip, target, depth);
                    break;

                case 28: case 29: // PaintSkew
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Skew(colr.F2Dot14(paint + 4), colr.F2Dot14(paint + 6)), clip, target, depth);
                    break;

                case 30: case 31: // PaintSkewAroundCenter
                    Descend(paint, colr.U24(paint + 1), transform,
                        Affine.Skew(colr.F2Dot14(paint + 4), colr.F2Dot14(paint + 6))
                            .Around(colr.I16(paint + 8), colr.I16(paint + 10)), clip, target, depth);
                    break;

                case 32: // PaintComposite
                {
                    uint source = colr.U24(paint + 1);
                    uint mode = colr.U8(paint + 4);
                    uint backdrop = colr.U24(paint + 5);
                    if (colr.Truncated) break;

                    if (_pass != Pass.Render)
                    {
                        if (backdrop != 0) Walk(paint + backdrop, transform, clip, target, depth + 1);
                        if (source != 0) Walk(paint + source, transform, clip, target, depth + 1);
                        break;
                    }

                    // Each side is drawn alone, on nothing, so the mode sees
                    // the two pictures and not whatever was already under
                    // them; the pair then goes onto the canvas as one.
                    var under = RentPlane();
                    var over = RentPlane();
                    if (backdrop != 0) Walk(paint + backdrop, transform, clip, under, depth + 1);
                    if (source != 0) Walk(paint + source, transform, clip, over, depth + 1);
                    ColrBlend.Composite(over, under, _texels, (int)mode);
                    for (int c = 0, end = _texels * 4; c < end; c += 4)
                    {
                        float a = under[c + 3];
                        if (a <= 0f) continue;
                        float keep = 1f - a;
                        target[c] = under[c] + target[c] * keep;
                        target[c + 1] = under[c + 1] + target[c + 1] * keep;
                        target[c + 2] = under[c + 2] + target[c + 2] * keep;
                        target[c + 3] = a + target[c + 3] * keep;
                    }
                    ReturnPlane();
                    ReturnPlane();
                    break;
                }

                // A format this does not know is one a later version of the
                // table added. It draws nothing, and its siblings still draw.
            }
        }

        private void Descend(uint paint, uint child, in Affine transform, in Affine local,
            float[] clip, float[] target, int depth)
        {
            if (child == 0 || _colr.Truncated) return;
            Walk(paint + child, transform.Then(local), clip, target, depth + 1);
        }

        // -------------------------------------------------------------- outline

        /// <summary>
        /// Puts a glyph's outline, carried through <paramref name="transform"/>,
        /// into the scratch outline. False when the glyph has no contours.
        /// </summary>
        private bool Flatten(uint glyph, in Affine transform, float pixelsPerUnit)
        {
            _outline.Clear();
            // Curves are split for the density they will be seen at, and a
            // transform that enlarges the outline enlarges its error with it.
            OutlineExtractor.Extract(_font, glyph, _outline, pixelsPerUnit * transform.MaxScale);
            if (_outline.Contours.Count == 0) return false;

            foreach (var contour in _outline.Contours)
                for (int i = 0; i < contour.Count; i++)
                    contour[i] = transform.Apply(contour[i]);
            return true;
        }

        private void GrowToOutline()
        {
            foreach (var contour in _outline.Contours)
            {
                if (contour.Count < 2) continue;
                foreach (var p in contour)
                {
                    if (float.IsNaN(p.x) || float.IsNaN(p.y)) continue;
                    _min = Vector2.Min(_min, p);
                    _max = Vector2.Max(_max, p);
                }
            }
        }

        /// <summary>Coverage of a font-unit box under a transform: a quadrilateral, filled like an outline.</summary>
        private void BoxMask(Vector2 min, Vector2 max, in Affine transform, float[] mask)
        {
            _outline.Clear();
            var contour = _outline.BeginContour();
            contour.Add(transform.Apply(new Vector2(min.x, min.y)));
            contour.Add(transform.Apply(new Vector2(max.x, min.y)));
            contour.Add(transform.Apply(new Vector2(max.x, max.y)));
            contour.Add(transform.Apply(new Vector2(min.x, max.y)));
            ColorGlyphs.FillOutline(_outline, _pixelsPerUnit, _origin, _width, _height, mask);
        }

        // ---------------------------------------------------------------- fills

        private Color Resolve(uint paletteIndex, float alpha)
        {
            Color color = paletteIndex != ForegroundColor && _palette != null &&
                          paletteIndex < (uint)_palette.Count
                ? (Color)_palette[(int)paletteIndex]
                // The sentinel, or an index the palette does not have: a broken
                // font is not a reason to draw nothing.
                : _textColor;
            color.a *= Mathf.Clamp01(alpha);
            return color;
        }

        private void FillSolid(float[] target, float[] clip, Color color)
        {
            if (color.a <= 0f) return;
            for (int i = 0, c = 0; i < _texels; i++, c += 4)
            {
                float a = clip != null ? clip[i] * color.a : color.a;
                if (a <= 0f) continue;
                float keep = 1f - a;
                target[c] = color.r * a + target[c] * keep;
                target[c + 1] = color.g * a + target[c + 1] * keep;
                target[c + 2] = color.b * a + target[c + 2] * keep;
                target[c + 3] = a + target[c + 3] * keep;
            }
        }

        /// <summary>
        /// Reads a colour line into <see cref="_stops"/>, sorted. False when
        /// there is nothing to draw with it.
        /// </summary>
        private bool ReadColorLine(uint line, bool variable)
        {
            var colr = _colr;
            _stops.Clear();
            _extend = (int)colr.U8(line);
            uint count = colr.U16(line + 1);
            if (colr.Truncated || count == 0) return false;
            if (count > MaxStops) count = MaxStops;

            // A variable stop carries a variation index after the same three
            // fields, and that is the whole difference.
            uint size = variable ? 10u : 6u;
            for (uint i = 0; i < count; i++)
            {
                uint stop = line + 3 + i * size;
                uint index = colr.U16(stop + 2);
                if (index == ForegroundColor) _followsText = true;
                if (_pass != Pass.Render) continue;
                _stops.Add(new Stop
                {
                    Offset = colr.F2Dot14(stop),
                    Color = Resolve(index, colr.F2Dot14(stop + 4)),
                });
            }
            if (colr.Truncated) return false;
            if (_pass == Pass.Render) _stops.Sort(s_byOffset);
            return true;
        }

        /// <summary>
        /// The colour at <paramref name="t"/> along the line, premultiplied.
        ///
        /// The line is defined between its first and last stops, not between
        /// 0 and 1: outside that span the extend mode decides, and repeat and
        /// reflect tile the span itself.
        /// </summary>
        private void ColorAt(float t, out float r, out float g, out float b, out float a)
        {
            int last = _stops.Count - 1;
            float first = _stops[0].Offset, span = _stops[last].Offset - first;

            if (span <= 0f)
            {
                // Every stop in one place: a step, whatever the mode says.
                var flat = t < first ? _stops[0].Color : _stops[last].Color;
                r = flat.r * flat.a; g = flat.g * flat.a; b = flat.b * flat.a; a = flat.a;
                return;
            }

            float u = t - first;
            if (_extend == 1) // repeat
            {
                u -= Mathf.Floor(u / span) * span;
            }
            else if (_extend == 2) // reflect
            {
                float period = span * 2f;
                u -= Mathf.Floor(u / period) * period;
                if (u > span) u = period - u;
            }
            else // pad
            {
                u = Mathf.Clamp(u, 0f, span);
            }
            t = first + u;

            int next = 1;
            while (next < last && _stops[next].Offset < t) next++;
            var from = _stops[next - 1];
            var to = _stops[next];
            float width = to.Offset - from.Offset;
            float k = width > 0f ? Mathf.Clamp01((t - from.Offset) / width) : 1f;

            // Interpolated straight, premultiplied afterwards.
            a = from.Color.a + (to.Color.a - from.Color.a) * k;
            r = (from.Color.r + (to.Color.r - from.Color.r) * k) * a;
            g = (from.Color.g + (to.Color.g - from.Color.g) * k) * a;
            b = (from.Color.b + (to.Color.b - from.Color.b) * k) * a;
        }

        private void FillGradient(int format, uint paint, in Affine transform, float[] clip, float[] target)
        {
            var colr = _colr;
            if (_stops.Count == 0) return;
            // A texel is found in the gradient's own space by undoing every
            // transform above it. One that cannot be undone has squashed the
            // gradient flat, and there is nothing to see.
            if (!transform.TryInvert(out var inverse)) return;

            // Linear: p0, p1 and the rotation point p2.
            // Radial: two circles. Sweep: a centre and two angles.
            float x0 = colr.I16(paint + 4), y0 = colr.I16(paint + 6);
            float ax = 0f, ay = 0f, scale = 0f;         // linear: direction / its length squared
            float cdx = 0f, cdy = 0f, r0 = 0f, dr = 0f; // radial
            float qa = 0f;
            float start = 0f, sweep = 0f;               // sweep, in degrees

            if (format == 4)
            {
                float x1 = colr.I16(paint + 8), y1 = colr.I16(paint + 10);
                float x2 = colr.I16(paint + 12), y2 = colr.I16(paint + 14);
                // The line the colours run along is p0→p1 with whatever part
                // of it lies along p0→p2 taken away: p2 turns the gradient
                // without moving its ends. With p2 on p0 there is no turn.
                float q1x = x1 - x0, q1y = y1 - y0, q2x = x2 - x0, q2y = y2 - y0;
                float s = q2x * q2x + q2y * q2y;
                if (s > 1e-6f)
                {
                    float k = (q2x * q1x + q2y * q1y) / s;
                    q1x -= k * q2x;
                    q1y -= k * q2y;
                }
                float length = q1x * q1x + q1y * q1y;
                // p0 on p1, or p2 on the line through them: no gradient at all.
                if (length < 1e-6f) return;
                ax = q1x;
                ay = q1y;
                scale = 1f / length;
            }
            else if (format == 6)
            {
                r0 = colr.U16(paint + 8);
                float x1 = colr.I16(paint + 10), y1 = colr.I16(paint + 12), r1 = colr.U16(paint + 14);
                cdx = x1 - x0;
                cdy = y1 - y0;
                dr = r1 - r0;
                qa = cdx * cdx + cdy * cdy - dr * dr;
            }
            else
            {
                // Stored as half turns less one, so that a full circle fits
                // the field: 0° is -1.0 and 360° is +1.0.
                start = (colr.F2Dot14(paint + 8) + 1f) * 180f;
                sweep = (colr.F2Dot14(paint + 10) + 1f) * 180f - start;
            }
            if (colr.Truncated) return;

            float step = 1f / _pixelsPerUnit;
            for (int y = 0; y < _height; y++)
            {
                float unitY = _origin.y + (y + 0.5f) * step;
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    float coverage = clip != null ? clip[i] : 1f;
                    if (coverage <= 0f) continue;

                    float unitX = _origin.x + (x + 0.5f) * step;
                    float px = inverse.XX * unitX + inverse.XY * unitY + inverse.DX - x0;
                    float py = inverse.YX * unitX + inverse.YY * unitY + inverse.DY - y0;

                    float t;
                    if (format == 4)
                    {
                        t = (px * ax + py * ay) * scale;
                    }
                    else if (format == 6)
                    {
                        // The largest t whose circle passes through this point
                        // and has a radius that is not negative.
                        float qb = px * cdx + py * cdy + r0 * dr;
                        float qc = px * px + py * py - r0 * r0;
                        if (Mathf.Abs(qa) < 1e-6f)
                        {
                            if (Mathf.Abs(qb) < 1e-6f) continue;
                            t = qc / (2f * qb);
                            if (r0 + t * dr < 0f) continue;
                        }
                        else
                        {
                            float discriminant = qb * qb - qa * qc;
                            if (discriminant < 0f) continue;
                            float root = Mathf.Sqrt(discriminant);
                            float high = (qb + root) / qa, low = (qb - root) / qa;
                            if (high < low) { float swap = high; high = low; low = swap; }
                            if (r0 + high * dr >= 0f) t = high;
                            else if (r0 + low * dr >= 0f) t = low;
                            else continue;
                        }
                    }
                    else
                    {
                        float angle = Mathf.Atan2(py, px) * Mathf.Rad2Deg;
                        if (angle < 0f) angle += 360f;
                        if (Mathf.Abs(sweep) < 1e-6f) t = angle < start ? -1e9f : 1e9f;
                        else t = (angle - start) / sweep;
                    }

                    ColorAt(t, out float r, out float g, out float b, out float a);
                    if (a <= 0f) continue;
                    r *= coverage; g *= coverage; b *= coverage; a *= coverage;
                    float keep = 1f - a;
                    int c = i * 4;
                    target[c] = r + target[c] * keep;
                    target[c + 1] = g + target[c + 1] * keep;
                    target[c + 2] = b + target[c + 2] * keep;
                    target[c + 3] = a + target[c + 3] * keep;
                }
            }
        }

        // -------------------------------------------------------------- scratch

        private float[] RentPlane()
        {
            int size = _texels * 4;
            if (_planesInUse == _planes.Count) _planes.Add(new float[size]);
            var plane = _planes[_planesInUse];
            if (plane.Length < size) _planes[_planesInUse] = plane = new float[size];
            _planesInUse++;
            Array.Clear(plane, 0, size);
            return plane;
        }

        private void ReturnPlane() => _planesInUse--;

        private float[] RentMask()
        {
            if (_masksInUse == _masks.Count) _masks.Add(new float[_texels]);
            var mask = _masks[_masksInUse];
            if (mask.Length < _texels) _masks[_masksInUse] = mask = new float[_texels];
            _masksInUse++;
            return mask;
        }

        private void ReturnMask() => _masksInUse--;
    }
}
