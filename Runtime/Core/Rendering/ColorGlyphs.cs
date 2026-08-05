using System;
using System.Collections.Generic;
using OneText.Native;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A decoded colour glyph: straight RGBA texels plus the glyph-space box
    /// they cover, in the same shape the SDF rasterizer produces.
    /// </summary>
    public struct ColorGlyph
    {
        /// <summary>RGBA32 texels, row 0 at the bottom (y-up, matching Unity).</summary>
        public Color32[] Pixels;
        public int Width, Height;

        /// <summary>Glyph-space position of the tile's bottom-left corner.</summary>
        public Vector2 OriginUnits;

        /// <summary>Font units per texel.</summary>
        public float UnitsPerPixel;

        public bool IsEmpty => Pixels == null || Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// Reads the colour a font already has for a glyph.
    ///
    /// This is the half of emoji that is not shaping. Segmentation and shaping
    /// were solved by UAX #29 and HarfBuzz — a ZWJ family is one grapheme
    /// cluster and one glyph once the font's ligatures apply — so what is left
    /// is getting the colour out. Two formats cover the fonts that matter:
    ///
    /// <list type="bullet">
    /// <item><b>CBDT/CBLC</b> — PNG bitmaps, which is Noto Color Emoji and
    /// therefore Android and most of Linux. HarfBuzz hands the PNG straight
    /// over and Unity decodes it.</item>
    /// <item><b>COLRv0</b> — layers of ordinary outlines, each drawn in a
    /// colour from the CPAL palette. Composited here from the same coverage the
    /// SDF rasterizer produces, which is why this needed no new rasterizer.</item>
    /// </list>
    ///
    /// Deliberately absent: sbix. Apple's newer sbix payloads are JPEG in ways
    /// FreeType cannot decode, so the system-emoji story on iOS is a bundled
    /// font in the fallback stack, not the system one. Saying so is better than
    /// a path that works on some iOS versions.
    ///
    /// COLRv1 — gradients and transforms — is also absent. It is a paint graph,
    /// not a layer list, and it deserves its own milestone rather than a
    /// half-implementation that renders some fonts wrong.
    /// </summary>
    public static class ColorGlyphs
    {
        /// <summary>Palette index meaning "use the text colour", per the COLR spec.</summary>
        private const uint UseTextColor = 0xFFFF;

        /// <summary>What kind of colour a font has, if any.</summary>
        [Flags]
        public enum Support
        {
            None = 0,
            Bitmaps = 1 << 0,   // CBDT/CBLC
            Layers = 1 << 1,    // COLRv0 + CPAL
        }

        // Keyed by FontData.CacheId, never by the native face pointer. A freed
        // face's address is handed straight back to the next one, so a pointer
        // cache reports the previous font's answer for an unrelated font —
        // which showed up here as an ordinary text font claiming to have CBDT.
        private static readonly Dictionary<int, Support> s_support = new Dictionary<int, Support>();

        /// <summary>
        /// What colour formats this face carries. Cached per face: the answer
        /// cannot change, and asking costs a table lookup in the font.
        /// </summary>
        public static Support SupportedBy(FontData font)
        {
            if (font == null || !font.IsValid) return Support.None;
            if (s_support.TryGetValue(font.CacheId, out var cached)) return cached;

            var support = Support.None;
            if (HarfBuzzApi.hb_ot_color_has_png(font.Face) != 0) support |= Support.Bitmaps;
            if (HarfBuzzApi.hb_ot_color_has_layers(font.Face) != 0 &&
                HarfBuzzApi.hb_ot_color_has_palettes(font.Face) != 0) support |= Support.Layers;

            s_support[font.CacheId] = support;
            return support;
        }

        /// <summary>True if this font draws any glyph in colour.</summary>
        public static bool IsColorFont(FontData font) => SupportedBy(font) != Support.None;

        /// <summary>
        /// True if any COLR layer of this glyph asks for the text colour, so a
        /// cache of the baked tile has to be keyed by that colour. Most glyphs
        /// do not, and keying every tile by colour would cost a miss per label
        /// colour for nothing.
        /// </summary>
        public static unsafe bool UsesTextColor(FontData font, uint glyphId)
        {
            if ((SupportedBy(font) & Support.Layers) == 0) return false;

            uint count = 0;
            uint total = HarfBuzzApi.hb_ot_color_glyph_get_layers(font.Face, glyphId, 0, ref count, null);
            if (total == 0 || total > MaxLayers) return false;

            var layers = stackalloc HBOtColorLayer[(int)total];
            count = total;
            HarfBuzzApi.hb_ot_color_glyph_get_layers(font.Face, glyphId, 0, ref count, layers);
            for (uint i = 0; i < count; i++)
                if (layers[i].ColorIndex == UseTextColor) return true;
            return false;
        }

        /// <summary>
        /// Ceiling on what a font may make us put on the stack. A count comes
        /// from the font file, and a font file is untrusted input.
        /// </summary>
        private const uint MaxLayers = 256;

        /// <summary>
        /// Decodes one glyph in colour, or returns false if this glyph has none
        /// and should go down the ordinary SDF path.
        ///
        /// <paramref name="textColor"/> is used for COLR layers that ask for it
        /// — that is the mechanism by which a colour font lets part of a glyph
        /// follow the label instead of the palette.
        /// </summary>
        public static bool TryDecode(FontData font, uint glyphId, float pixelsPerUnit,
            Color32 textColor, out ColorGlyph result)
        {
            result = default;
            var support = SupportedBy(font);
            if (support == Support.None) return false;

            if ((support & Support.Bitmaps) != 0 && TryDecodeBitmap(font, glyphId, out result))
                return true;
            if ((support & Support.Layers) != 0 &&
                TryDecodeLayers(font, glyphId, pixelsPerUnit, textColor, out result))
                return true;
            return false;
        }

        // ------------------------------------------------------------- bitmaps

        private static bool TryDecodeBitmap(FontData font, uint glyphId, out ColorGlyph result)
        {
            result = default;
            var blob = HarfBuzzApi.hb_ot_color_glyph_reference_png(font.Font, glyphId);
            if (blob == IntPtr.Zero) return false;

            try
            {
                var data = HarfBuzzApi.hb_blob_get_data(blob, out uint length);
                if (data == IntPtr.Zero || length == 0) return false;

                var bytes = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, (int)length);

                // Unity's PNG decoder rather than one of our own: it is there,
                // it is native, and a decoder is not what this project is for.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false);
                bool loaded = texture.LoadImage(bytes, markNonReadable: false);
                if (!loaded || texture.width <= 0)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return false;
                }

                var pixels = texture.GetPixels32();
                int width = texture.width, height = texture.height;
                UnityEngine.Object.DestroyImmediate(texture);

                // The strike is authored against the em box, so the tile covers
                // it: ascender at the top, baseline at the bottom. Fonts whose
                // bitmaps do not fill the em are rare enough that reading the
                // per-strike metrics is not worth the binding.
                if (!font.TryGetInkBounds(glyphId, out var lo, out var hi) || hi.y <= lo.y)
                {
                    lo = new Vector2(0f, font.Descender);
                    hi = new Vector2(font.UnitsPerEm, font.Ascender);
                }

                result = new ColorGlyph
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height,
                    OriginUnits = lo,
                    UnitsPerPixel = (hi.y - lo.y) / height,
                };
                return true;
            }
            finally
            {
                HarfBuzzApi.hb_blob_destroy(blob);
            }
        }

        // -------------------------------------------------------------- layers

        /// <summary>Same reason as <c>MaxLayers</c>: the count comes from the file.</summary>
        private const uint MaxPaletteEntries = 4096;

        [ThreadStatic] private static List<Color32> t_palette;

        private static unsafe bool TryDecodeLayers(FontData font, uint glyphId, float pixelsPerUnit,
            Color32 textColor, out ColorGlyph result)
        {
            result = default;

            uint count = 0;
            uint total = HarfBuzzApi.hb_ot_color_glyph_get_layers(
                font.Face, glyphId, 0, ref count, null);
            if (total == 0 || total > MaxLayers) return false;

            var layers = stackalloc HBOtColorLayer[(int)total];
            count = total;
            HarfBuzzApi.hb_ot_color_glyph_get_layers(font.Face, glyphId, 0, ref count, layers);
            if (count == 0) return false;

            var palette = ReadPalette(font);

            // The tile is sized to hold every layer, because a layer may reach
            // outside the base glyph's own ink.
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (uint i = 0; i < count; i++)
            {
                if (!font.TryGetInkBounds(layers[i].Glyph, out var lo, out var hi)) continue;
                min = Vector2.Min(min, lo);
                max = Vector2.Max(max, hi);
            }
            if (min.x > max.x) return false;

            const int pad = 1;
            int width = Mathf.CeilToInt((max.x - min.x) * pixelsPerUnit) + pad * 2;
            int height = Mathf.CeilToInt((max.y - min.y) * pixelsPerUnit) + pad * 2;
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024) return false;

            var pixels = new Color32[width * height];
            var coverage = new float[width * height];
            var origin = new Vector2(min.x - pad / pixelsPerUnit, min.y - pad / pixelsPerUnit);

            for (uint i = 0; i < count; i++)
            {
                var color = layers[i].ColorIndex == UseTextColor
                    ? textColor
                    : layers[i].ColorIndex < palette.Count
                        ? palette[(int)layers[i].ColorIndex]
                        // A palette index the font does not have is a broken
                        // font, not a reason to draw nothing.
                        : textColor;

                Rasterize(font, layers[i].Glyph, pixelsPerUnit, origin, width, height, coverage);
                Composite(pixels, coverage, color);
            }

            result = new ColorGlyph
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                OriginUnits = origin,
                UnitsPerPixel = 1f / pixelsPerUnit,
            };
            return true;
        }

        private static List<Color32> ReadPalette(FontData font)
        {
            var palette = t_palette ??= new List<Color32>();
            palette.Clear();
            if (HarfBuzzApi.hb_ot_color_palette_get_count(font.Face) == 0) return palette;

            unsafe
            {
                uint count = 0;
                uint total = HarfBuzzApi.hb_ot_color_palette_get_colors(font.Face, 0, 0, ref count, null);
                if (total == 0) return palette;
                if (total > MaxPaletteEntries) total = MaxPaletteEntries;

                var colors = stackalloc uint[(int)total];
                count = total;
                HarfBuzzApi.hb_ot_color_palette_get_colors(font.Face, 0, 0, ref count, colors);
                for (uint i = 0; i < count; i++)
                {
                    // hb_color_t is HB_COLOR(b,g,r,a) over HB_TAG, which packs
                    // its first argument into the *high* byte: blue is bits
                    // 24-31 and alpha is bits 0-7. Guessing this the other way
                    // round is quiet — it turns one palette entry into another
                    // valid colour rather than into garbage, and a red layer
                    // comes out with zero alpha and simply is not there.
                    uint c = colors[i];
                    palette.Add(new Color32(
                        (byte)((c >> 8) & 0xFF),   // r
                        (byte)((c >> 16) & 0xFF),  // g
                        (byte)((c >> 24) & 0xFF),  // b
                        (byte)(c & 0xFF)));        // a
                }
            }
            return palette;
        }

        [ThreadStatic] private static GlyphOutline t_outline;

        /// <summary>
        /// Fills <paramref name="coverage"/> with this layer's alpha, using
        /// scanline sampling of the flattened outline.
        ///
        /// A layer is a fill, not a distance field: it wants coverage, not
        /// signed distance, and the atlas will hold the composited RGBA rather
        /// than something the SDF shader reconstructs. So this is a small
        /// even-odd scanline filler rather than a call into the SDF job.
        /// </summary>
        private static void Rasterize(FontData font, uint glyphId, float pixelsPerUnit,
            Vector2 origin, int width, int height, float[] coverage)
        {
            Array.Clear(coverage, 0, coverage.Length);

            var outline = t_outline ??= new GlyphOutline();
            outline.Clear();
            OutlineExtractor.Extract(font, glyphId, outline, pixelsPerUnit);
            if (outline.Contours.Count == 0) return;

            // 2x2 supersampling: enough to stop a fill looking cut out with a
            // knife, and cheap because a colour glyph is baked once.
            const int samples = 2;
            var crossings = t_crossings ??= new List<(float X, int Winding)>(16);

            for (int y = 0; y < height; y++)
            {
                for (int sy = 0; sy < samples; sy++)
                {
                    float unitY = origin.y + (y + (sy + 0.5f) / samples) / pixelsPerUnit;
                    crossings.Clear();
                    foreach (var contour in outline.Contours)
                    {
                        for (int i = 0; i < contour.Count; i++)
                        {
                            var a = contour[i];
                            var b = contour[(i + 1) % contour.Count];
                            if (a.y == b.y) continue;
                            if (unitY < Mathf.Min(a.y, b.y) || unitY >= Mathf.Max(a.y, b.y)) continue;
                            float t = (unitY - a.y) / (b.y - a.y);
                            crossings.Add((a.x + t * (b.x - a.x), b.y > a.y ? 1 : -1));
                        }
                    }
                    if (crossings.Count < 2) continue;
                    crossings.Sort(s_byCrossing);

                    // Nonzero winding, not even-odd. TrueType outlines are
                    // filled nonzero, and real emoji shapes are unions of
                    // overlapping same-direction contours — even-odd punches
                    // holes through exactly those.
                    int winding = 0;
                    for (int c = 0; c + 1 < crossings.Count; c++)
                    {
                        winding += crossings[c].Winding;
                        if (winding == 0) continue;

                        float x0 = (crossings[c].X - origin.x) * pixelsPerUnit;
                        float x1 = (crossings[c + 1].X - origin.x) * pixelsPerUnit;
                        int first = Mathf.Max(0, Mathf.FloorToInt(x0));
                        int last = Mathf.Min(width - 1, Mathf.CeilToInt(x1));
                        for (int x = first; x <= last; x++)
                        {
                            // Horizontal coverage of this pixel by the span.
                            float covered = Mathf.Min(x + 1f, x1) - Mathf.Max((float)x, x0);
                            if (covered <= 0f) continue;
                            coverage[y * width + x] += Mathf.Min(1f, covered) / samples;
                        }
                    }
                }
            }

            for (int i = 0; i < coverage.Length; i++) coverage[i] = Mathf.Clamp01(coverage[i]);
        }

        [ThreadStatic] private static List<(float X, int Winding)> t_crossings;

        private static readonly Comparison<(float X, int Winding)> s_byCrossing =
            (a, b) => a.X.CompareTo(b.X);

        /// <summary>Source-over, in straight alpha.</summary>
        private static void Composite(Color32[] pixels, float[] coverage, Color32 color)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                float a = Mathf.Clamp01(coverage[i]) * (color.a / 255f);
                if (a <= 0f) continue;

                var dst = pixels[i];
                float dstA = dst.a / 255f;
                float outA = a + dstA * (1f - a);
                if (outA <= 0f) continue;

                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt((color.r * a + dst.r * dstA * (1f - a)) / outA),
                    (byte)Mathf.RoundToInt((color.g * a + dst.g * dstA * (1f - a)) / outA),
                    (byte)Mathf.RoundToInt((color.b * a + dst.b * dstA * (1f - a)) / outA),
                    (byte)Mathf.RoundToInt(outA * 255f));
            }
        }

        /// <summary>Drops the per-face cache; call after unloading fonts.</summary>
        public static void ClearCache() => s_support.Clear();

        /// <summary>
        /// Forgets one font's answer. Called from <see cref="FontData.Dispose"/>
        /// — labels reload their fonts whenever a style or a font field changes,
        /// and every reload is a new CacheId, so without this the table grows
        /// for the lifetime of the editor.
        /// </summary>
        internal static void Forget(int cacheId) => s_support.Remove(cacheId);
    }
}
