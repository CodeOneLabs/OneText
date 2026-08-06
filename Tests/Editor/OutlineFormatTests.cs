using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The PostScript/CFF outline path, and the shapes that break rasterizers.
    ///
    /// Both other test fonts are TrueType, so hb-draw's cubic callback and the
    /// flattening that hangs off it had never been rendered by a test: the
    /// half of the outline code with the harder error bound. `CffShapes.otf` is
    /// authored for this (see generate_cff_test_font.py) and holds a counter, a
    /// pair of overlapping contours and a long shallow S-curve.
    /// </summary>
    public class OutlineFormatTests
    {
        private const string CffFontPath = "Packages/com.onetext.core/Tests/Fonts/CffShapes.otf";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static uint GlyphOf(FontData font, Shaper shaper, char c)
        {
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, c.ToString(), shaped);
            Assert.AreEqual(1, shaped.Count, $"expected one glyph for '{c}'");
            Assert.AreNotEqual(0u, shaped[0].GlyphId, $"'{c}' is not in the font");
            return shaped[0].GlyphId;
        }

        /// <summary>Reads a texel of a rasterized tile at a glyph-space point.</summary>
        private static byte Sample(RasterizedGlyph tile, byte[] pixels, Vector2 pointUnits)
        {
            int px = Mathf.RoundToInt((pointUnits.x - tile.OriginUnits.x) / tile.UnitsPerPixel - 0.5f);
            int py = Mathf.RoundToInt((pointUnits.y - tile.OriginUnits.y) / tile.UnitsPerPixel - 0.5f);
            Assert.IsTrue(px >= 0 && px < tile.Width && py >= 0 && py < tile.Height,
                $"sample point {pointUnits} falls outside the tile");
            return pixels[py * tile.Width + px];
        }

        // Above 128 the texel is inside the ink; below it is outside.
        private const byte Inside = 128;

        [Test]
        public void CffOutlines_FlattenIntoContours()
        {
            using var font = LoadFont(CffFontPath);
            using var shaper = new Shaper();
            Assert.AreEqual(1000u, font.UnitsPerEm);

            var outline = new GlyphOutline();
            foreach (char c in "OQSI")
            {
                outline.Clear();
                OutlineExtractor.Extract(font, GlyphOf(font, shaper, c), outline, 32f / 1000f);

                int points = 0;
                foreach (var contour in outline.Contours) points += contour.Count;
                Assert.Greater(outline.Contours.Count, 0, $"'{c}' produced no contours");
                Assert.Greater(points, outline.Contours.Count * 3,
                    $"'{c}' produced degenerate contours");
                Debug.Log($"[outline] CFF '{c}': {outline.Contours.Count} contours, {points} points");
            }
        }

        [Test]
        public void Counter_ComesOutOutsideTheGlyph()
        {
            // 'O' is a ring inside a ring, wound the other way. If the sign
            // rule is wrong the middle fills in, which is the "glyphs with
            // holes" failure, and it is invisible in a font whose counters
            // happen to be small.
            using var font = LoadFont(CffFontPath);
            using var shaper = new Shaper();
            var tile = GlyphRasterizer.Rasterize(font, GlyphOf(font, shaper, 'O'), 64f / 1000f);
            var pixels = tile.CopyPixels();

            byte middle = Sample(tile, pixels, new Vector2(350, 350));
            byte ring = Sample(tile, pixels, new Vector2(350, 350 + 245));
            byte beyond = Sample(tile, pixels, new Vector2(350 + 318, 350));

            Debug.Log($"[outline] counter: middle {middle}, ring {ring}, outer edge {beyond}");
            Assert.Less(middle, Inside, "the counter filled in: the hole is being treated as ink");
            Assert.Greater(ring, Inside, "the ring itself is not ink");
        }

        [Test]
        public void OverlappingContours_Union_WithoutCarving()
        {
            // Two rectangles crossing, both wound the same way. The overlap is
            // covered twice; a rasterizer that cancels there punches a hole,
            // and the edges buried inside the other rectangle must not cut
            // into the union either.
            using var font = LoadFont(CffFontPath);
            using var shaper = new Shaper();
            var tile = GlyphRasterizer.Rasterize(font, GlyphOf(font, shaper, 'Q'), 64f / 1000f);
            var pixels = tile.CopyPixels();

            byte overlap = Sample(tile, pixels, new Vector2(300, 300));  // in both boxes
            byte armH = Sample(tile, pixels, new Vector2(100, 300));     // horizontal bar only
            byte armV = Sample(tile, pixels, new Vector2(300, 500));     // vertical bar only
            byte corner = Sample(tile, pixels, new Vector2(80, 500));    // in neither

            Debug.Log($"[outline] overlap: shared {overlap}, horizontal {armH}, " +
                $"vertical {armV}, outside {corner}");
            Assert.Greater(overlap, Inside, "the shared area of two overlapping contours dropped out");
            Assert.Greater(armH, Inside, "the horizontal bar is not ink");
            Assert.Greater(armV, Inside, "the vertical bar is not ink");
            Assert.Less(corner, Inside, "an area covered by neither contour came out as ink");
        }

        [Test]
        public void AdaptiveFlattening_StaysCloseToDenseSubdivision()
        {
            // The tolerance decides how many segments a curve becomes, and it
            // was tuned against the *old fixed* subdivision, which is itself an
            // approximation. This compares against as dense a flattening as the
            // extractor will produce, for quadratics and cubics both.
            using var latin = LoadFont(LatinFontPath);
            using var cff = LoadFont(CffFontPath);
            using var shaper = new Shaper();

            float previous = OutlineExtractor.FlatnessPixels;
            try
            {
                foreach (var (font, text, label) in new[]
                {
                    (latin, "OSBGQ8ao", "TrueType"),
                    (cff, "OQS", "CFF"),
                })
                {
                    foreach (int ppem in new[] { 24, 48 })
                    {
                        int worstSeen = 0;
                        double meanSeen = 0;
                        float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
                        foreach (char c in text)
                        {
                            uint glyph = GlyphOf(font, shaper, c);

                            OutlineExtractor.FlatnessPixels = 0.0005f; // clamps to the segment cap
                            var dense = GlyphRasterizer.Rasterize(font, glyph, pixelsPerUnit);
                            var densePixels = dense.CopyPixels();

                            OutlineExtractor.FlatnessPixels = previous;
                            var shipped = GlyphRasterizer.Rasterize(font, glyph, pixelsPerUnit);
                            var shippedPixels = shipped.CopyPixels();

                            Assert.AreEqual(dense.Width, shipped.Width,
                                $"{label} '{c}' at {ppem}ppem: tile width changed with the tolerance");
                            Assert.AreEqual(dense.Height, shipped.Height,
                                $"{label} '{c}' at {ppem}ppem: tile height changed with the tolerance");

                            int worst = 0;
                            long total = 0;
                            for (int i = 0; i < densePixels.Length; i++)
                            {
                                int delta = Mathf.Abs(densePixels[i] - shippedPixels[i]);
                                if (delta > worst) worst = delta;
                                total += delta;
                            }
                            double mean = total / (double)densePixels.Length;
                            worstSeen = Mathf.Max(worstSeen, worst);
                            meanSeen = System.Math.Max(meanSeen, mean);

                            // One level of the stored field is 8/255 of a pixel
                            // of distance, so the 0.05 px tolerance is about
                            // 1.6 of them. Measured: worst 3, mean ~1.0, for
                            // quadratics and cubics alike. The bounds are set
                            // with headroom for another machine's rounding and
                            // still well under what a fixed subdivision costs.
                            Assert.LessOrEqual(worst, 8,
                                $"{label} '{c}' at {ppem}ppem differs from dense flattening by " +
                                $"{worst} levels (mean {mean:F2})");
                            Assert.Less(mean, 1.5,
                                $"{label} '{c}' at {ppem}ppem differs on average by {mean:F2} levels");
                        }

                        Debug.Log($"[outline] {label} at {ppem}ppem vs dense flattening: " +
                            $"worst {worstSeen} levels, worst mean {meanSeen:F2}");
                    }
                }
            }
            finally
            {
                OutlineExtractor.FlatnessPixels = previous;
            }
        }

        [Test]
        public void CulledRasterizer_MatchesNaive_ForCffOutlines()
        {
            using var font = LoadFont(CffFontPath);
            using var shaper = new Shaper();
            try
            {
                foreach (int ppem in new[] { 24, 64 })
                {
                    float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
                    foreach (char c in "OQSI")
                    {
                        uint glyph = GlyphOf(font, shaper, c);

                        GlyphRasterizer.Cull = false;
                        var naive = GlyphRasterizer.Rasterize(font, glyph, pixelsPerUnit);
                        var naivePixels = naive.CopyPixels();
                        GlyphRasterizer.Cull = true;
                        var culled = GlyphRasterizer.Rasterize(font, glyph, pixelsPerUnit);

                        Assert.AreEqual(naive.Width, culled.Width);
                        Assert.AreEqual(naive.Height, culled.Height);
                        CollectionAssert.AreEqual(naivePixels, culled.CopyPixels(),
                            $"CFF '{c}' at {ppem}ppem differs with culling on");
                    }
                }
            }
            finally
            {
                GlyphRasterizer.Cull = true;
            }
        }
    }
}
