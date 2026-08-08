using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M14: multi-channel distance fields, as the per-label <c>precise</c>
    /// option.
    ///
    /// Two claims are worth testing and they pull in opposite directions. The
    /// first is that MSDF does what it is for: a corner survives the bilinear
    /// sampler, where a single channel stores a cone and the sampler cuts it,
    /// which is measured here by reconstructing both fields the way the GPU
    /// does and counting how many sub-texel points each one misclassifies. The
    /// second is that it stays opt-in and stays cheap for everyone else: off by
    /// default, tiles cached apart from the ordinary ones, and (the part that
    /// would be easy to lose) still the same material, so a precise heading
    /// batches with the body text under it.
    /// </summary>
    public class MsdfTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private const string VariableFontPath =
            "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        // ------------------------------------------------------------ colouring

        private static List<Vector2> Square(float size) => new List<Vector2>
        {
            new Vector2(0f, 0f), new Vector2(size, 0f), new Vector2(size, size),
            new Vector2(0f, size), new Vector2(0f, 0f),
        };

        private static List<Vector2> Circle(float radius, int steps)
        {
            var points = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                points.Add(new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius));
            }
            points[steps] = points[0];
            return points;
        }

        private static int Bits(int mask)
        {
            int count = 0;
            while (mask != 0) { count += mask & 1; mask >>= 1; }
            return count;
        }

        [Test]
        public void EdgesMeetingAtACorner_ShareExactlyOneChannel()
        {
            // The whole mechanism in one assertion: two edges that share one
            // channel put their two half-planes into the other two, and the
            // median of the three is their intersection. Share none and the
            // corner has no channel to agree on; share two and there is nothing
            // for the median to choose between.
            var contour = Square(100f);
            var flags = new byte[contour.Count];
            MsdfEdgeColoring.Assign(contour, flags, 0);

            int segments = contour.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                int here = flags[i] & MsdfEdgeColoring.ChannelMask;
                int next = flags[(i + 1) % segments] & MsdfEdgeColoring.ChannelMask;
                Assert.AreNotEqual(here, next, $"segments {i} and {i + 1} share a colour");
                Assert.AreEqual(1, Bits(here & next),
                    $"segments {i} and {i + 1} must share exactly one channel");
            }
        }

        [Test]
        public void EveryEdgeOfACorneredContour_IsBoundedByCorners()
        {
            // The pseudo-distance extension is what sharpens a corner, and it
            // is only allowed at an edge's ends. A square's four edges are one
            // segment each, so each carries both marks.
            var contour = Square(100f);
            var flags = new byte[contour.Count];
            MsdfEdgeColoring.Assign(contour, flags, 0);

            for (int i = 0; i < contour.Count - 1; i++)
            {
                Assert.AreNotEqual(0, flags[i] & MsdfEdgeColoring.RunStart);
                Assert.AreNotEqual(0, flags[i] & MsdfEdgeColoring.RunEnd);
            }
        }

        [Test]
        public void SmoothLoop_IsOneWhiteEdgeWithNothingToExtend()
        {
            // A flattened circle bends at every vertex, and a corner threshold
            // that called those corners would stripe an 'o' in three colours
            // and crease it wherever a run ended.
            var contour = Circle(100f, 64);
            var flags = new byte[contour.Count];
            MsdfEdgeColoring.Assign(contour, flags, 0);

            for (int i = 0; i < contour.Count - 1; i++)
            {
                Assert.AreEqual(MsdfEdgeColoring.White, flags[i],
                    "a flattening bend was mistaken for a corner");
            }
        }

        // ---------------------------------------------------------- the field

        /// <summary>One channel of a texel, or the single-channel byte.</summary>
        private static float Texel(in RasterizedGlyph tile, byte[] pixels, int x, int y, int channel)
        {
            x = Mathf.Clamp(x, 0, tile.Width - 1);
            y = Mathf.Clamp(y, 0, tile.Height - 1);
            return pixels[(y * tile.Width + x) * tile.BytesPerTexel + channel];
        }

        /// <summary>The sampler's own reconstruction: bilinear, in texel space.</summary>
        private static float Sample(in RasterizedGlyph tile, byte[] pixels, Vector2 at, int channel)
        {
            float u = (at.x - tile.OriginUnits.x) / tile.UnitsPerPixel - 0.5f;
            float v = (at.y - tile.OriginUnits.y) / tile.UnitsPerPixel - 0.5f;
            int x = Mathf.FloorToInt(u), y = Mathf.FloorToInt(v);
            float fx = u - x, fy = v - y;
            float bottom = Mathf.Lerp(Texel(tile, pixels, x, y, channel),
                Texel(tile, pixels, x + 1, y, channel), fx);
            float top = Mathf.Lerp(Texel(tile, pixels, x, y + 1, channel),
                Texel(tile, pixels, x + 1, y + 1, channel), fx);
            return Mathf.Lerp(bottom, top, fy);
        }

        private static float Median(float a, float b, float c) =>
            Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));

        private static RasterizedGlyph Bake(List<Vector2> contour, bool precise, out byte[] pixels)
        {
            var contours = new List<List<Vector2>> { contour };
            var tile = GlyphRasterizer.RasterizeContours(contours, null, 1f, precise);
            pixels = tile.CopyPixels();
            return tile;
        }

        [Test]
        public void PreciseTile_IsFourChannelsThatDisagreeAtACorner()
        {
            var tile = Bake(Square(40f), precise: true, out var pixels);

            Assert.AreEqual(4, tile.BytesPerTexel);
            Assert.AreEqual(tile.Width * tile.Height * 4, pixels.Length);

            int disagreeing = 0;
            for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                float r = Texel(tile, pixels, x, y, 0);
                float g = Texel(tile, pixels, x, y, 1);
                float b = Texel(tile, pixels, x, y, 2);
                if (Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b)) > 8f) disagreeing++;
            }
            Assert.Greater(disagreeing, 0,
                "no texel where the three channels differ: the shape was coloured as one edge");
        }

        [Test]
        public void SmoothShape_HasNothingForThreeChannelsToDisagreeAbout()
        {
            // The other half of the previous test: where there is no corner the
            // multi-channel field is the single-channel one, three times over.
            var tile = Bake(Circle(30f, 96), precise: true, out var pixels);

            for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                float r = Texel(tile, pixels, x, y, 0);
                Assert.AreEqual(r, Texel(tile, pixels, x, y, 1), 1f);
                Assert.AreEqual(r, Texel(tile, pixels, x, y, 2), 1f);
            }
        }

        [Test]
        public void PreciseAlpha_IsTheOrdinarySingleChannelField()
        {
            // Alpha costs nothing to fill (every edge is in two channels, so
            // the nearest edge overall is the nearest in some channel), and the
            // shader reads it for the shadow. It has to be the same number the
            // ordinary rasterizer would have written.
            var precise = Bake(Square(40f), precise: true, out var preciseBytes);
            var plain = Bake(Square(40f), precise: false, out var plainBytes);

            Assert.AreEqual(plain.Width, precise.Width);
            Assert.AreEqual(plain.Height, precise.Height);
            for (int i = 0; i < plain.Width * plain.Height; i++)
                Assert.AreEqual(plainBytes[i], preciseBytes[i * 4 + 3], 1f, $"texel {i}");
        }

        [Test]
        public void MedianReconstruction_KeepsACornerTheSamplerWouldHaveCut()
        {
            // What MSDF is actually for. Both fields are exact at texel
            // centres; the difference is what happens between them. A single
            // channel stores the distance to the corner (a cone), and bilinear
            // interpolation of a cone bulges outward, which pulls the 0.5
            // isoline inside the shape and rounds the corner off. Each of the
            // multi-channel channels stores a half-plane near that corner,
            // which is linear, which the sampler reproduces exactly.
            const float size = 40f;
            var precise = Bake(Square(size), precise: true, out var preciseBytes);
            var plain = Bake(Square(size), precise: false, out var plainBytes);

            int preciseWrong = 0, plainWrong = 0;
            for (float y = -1.5f; y <= 1.5f; y += 0.1f)
            for (float x = -1.5f; x <= 1.5f; x += 0.1f)
            {
                var at = new Vector2(x, y);
                bool inside = x > 0f && y > 0f;

                float single = Sample(plain, plainBytes, at, 0);
                float median = Median(
                    Sample(precise, preciseBytes, at, 0),
                    Sample(precise, preciseBytes, at, 1),
                    Sample(precise, preciseBytes, at, 2));

                if (single > 127.5f != inside) plainWrong++;
                if (median > 127.5f != inside) preciseWrong++;
            }

            Assert.Greater(plainWrong, 0,
                "the single-channel field reconstructed the corner perfectly; " +
                "the test is not measuring what it thinks it is");
            Assert.Less(preciseWrong, plainWrong,
                $"median reconstruction was no sharper than one channel " +
                $"({preciseWrong} wrong against {plainWrong})");
        }

        // --------------------------------------------------- error correction

        /// <summary>
        /// Texels where the median says one side of the outline and the true
        /// distance in alpha says the other, counted past a guard band so the
        /// sub-texel disagreement either side of the boundary is not mistaken
        /// for the artifact. This is the clash, measured: nothing else can put
        /// the two fields on opposite sides of a texel centre.
        /// </summary>
        private static int WrongSideTexels(in RasterizedGlyph tile, byte[] pixels, float guardTexels)
        {
            // A byte is 2 * SpreadPixels / 255 texels of distance; the encoding
            // is 0.5 - d / (2 * spread), so 127.5 is the outline.
            float guardBytes = guardTexels * 255f / (2f * GlyphRasterizer.SpreadPixels);
            int wrong = 0;
            for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                float median = Median(
                    Texel(tile, pixels, x, y, 0),
                    Texel(tile, pixels, x, y, 1),
                    Texel(tile, pixels, x, y, 2));
                float truth = Texel(tile, pixels, x, y, 3);
                if (Mathf.Abs(truth - 127.5f) <= guardBytes) continue;
                if (median > 127.5f != truth > 127.5f) wrong++;
            }
            return wrong;
        }

        private static RasterizedGlyph BakeGlyph(FontData font, string text, float ppem,
            float correctionTexels, out byte[] pixels)
        {
            float was = GlyphRasterizer.MsdfErrorCorrectionTexels;
            GlyphRasterizer.MsdfErrorCorrectionTexels = correctionTexels;
            try
            {
                var tile = GlyphRasterizer.Rasterize(font, FirstGlyph(font, text),
                    ppem / font.UnitsPerEm, precise: true);
                pixels = tile.CopyPixels();
                return tile;
            }
            finally
            {
                GlyphRasterizer.MsdfErrorCorrectionTexels = was;
            }
        }

        /// <summary>
        /// The shape that actually clashes, found by sweeping every glyph of
        /// every test face at three sizes: the CFF specimen's long shallow
        /// S-curve, whose two ends run nearly parallel a few texels apart. It is
        /// also the only one — the whole of NotoSans and NotoSansVariable came
        /// back clean, which is what M14 meant by "not yet a problem at the
        /// sizes precise is for" and why this went unnoticed until now.
        /// </summary>
        private const string CffFontPath = "Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf";

        [Test]
        public void WithoutErrorCorrection_TheShallowCurveGrowsInkThatIsNotThere()
        {
            // The bug, before the fix, and the half of the pair that would stop
            // meaning anything if the correction were moved somewhere it always
            // ran. The median claims ink four texels clear of the outline: a
            // detached white block hanging off the tail of the S, in the padding
            // ring the shader is entitled to read as empty.
            using var font = LoadFont(CffFontPath);
            var tile = BakeGlyph(font, "S", 48f, 0f, out var pixels);

            Assert.Greater(WrongSideTexels(tile, pixels, 0.5f), 0,
                "the clash is gone from the specimen that had one: either the " +
                "colouring changed or the measurement is not measuring the median");
        }

        [Test]
        public void ErrorCorrection_LeavesNoTexelOnTheWrongSideOfTheOutline()
        {
            // And after. Zero, not fewer: the correction makes the same sign
            // check this does, so every texel it declined to fix is one inside
            // its guard band, and the count past that band has to come out empty.
            using var font = LoadFont(CffFontPath);

            foreach (var glyph in new[] { "O", "Q", "S", "I" })
            foreach (float ppem in new[] { 24f, 48f, 128f })
            {
                var tile = BakeGlyph(font, glyph, ppem, 0.5f, out var pixels);
                Assert.AreEqual(0, WrongSideTexels(tile, pixels, 0.5f),
                    $"'{glyph}' at {ppem} ppem still contradicts its own alpha");
            }
        }

        /// <summary>
        /// One byte of the encoding, in texels of distance. The field is
        /// 0.5 - d / (2 * spread) over 255 levels, so this converts back.
        /// </summary>
        private static float BytesToTexels =>
            2f * GlyphRasterizer.SpreadPixels / 255f;

        /// <summary>
        /// The worst SAG: how far shallower than the true distance the median
        /// reads, at texels the true distance says are solidly inside the ink.
        /// Not a sign test — the median never crosses the outline here. This is
        /// what dims the middle of a stroke to grey.
        /// </summary>
        private static float WorstSagTexels(in RasterizedGlyph tile, byte[] pixels,
            float depthTexels)
        {
            float deep = 127.5f + depthTexels / BytesToTexels;
            float worst = 0f;
            for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                float truth = Texel(tile, pixels, x, y, 3);
                if (truth < deep) continue;
                float median = Median(
                    Texel(tile, pixels, x, y, 0),
                    Texel(tile, pixels, x, y, 1),
                    Texel(tile, pixels, x, y, 2));
                worst = Mathf.Max(worst, (truth - median) * BytesToTexels);
            }
            return worst;
        }

        [Test]
        public void WithoutErrorCorrection_TheCrossbarAndApexSagInsideTheInk()
        {
            // The artifact the user actually reported, measured before the fix.
            // Where a crossbar meets a diagonal, every channel is measuring
            // against a line extended past a run end that stopped bounding the
            // shape, so the median reads a couple of texels shallower than the
            // truth. It never crosses the outline, which is why the sign test
            // that catches the CFF block finds nothing here; on screen it is a
            // grey mark in the middle of solid ink.
            using var font = LoadFont(LatinFontPath);

            foreach (var glyph in new[] { "A", "W", "K" })
            {
                var tile = BakeGlyph(font, glyph, 128f, 0f, out var pixels);
                Assert.Greater(WorstSagTexels(tile, pixels, 2f), 1f,
                    $"'{glyph}' no longer sags: the measurement or the colouring changed");
            }
        }

        /// <summary>
        /// The shallowest the median reads, in texels inside the outline, over
        /// the texels the true distance places at least <paramref name="depthTexels"/>
        /// in. Negative means the median put a texel of solid ink outside the
        /// shape altogether.
        /// </summary>
        private static float ShallowestMedianTexels(in RasterizedGlyph tile, byte[] pixels,
            float depthTexels)
        {
            float deep = 127.5f + depthTexels / BytesToTexels;
            float shallowest = float.MaxValue;
            for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                if (Texel(tile, pixels, x, y, 3) < deep) continue;
                float median = Median(
                    Texel(tile, pixels, x, y, 0),
                    Texel(tile, pixels, x, y, 1),
                    Texel(tile, pixels, x, y, 2));
                shallowest = Mathf.Min(shallowest, (median - 127.5f) * BytesToTexels);
            }
            return shallowest;
        }

        [Test]
        public void ErrorCorrection_KeepsTheMedianClearOfTheOutlineInsideSolidInk()
        {
            // And after. Stated the way the correction is: a texel the true
            // distance places solidly inside the ink has a median at least the
            // allowance clear of the outline, so no antialiasing band the
            // shader picks can reach it and dim it.
            //
            // 64 ppem is in the list on purpose. It is the size the report came
            // from, and a deviation-based rule could not have covered it: an 'A'
            // crossbar there is four texels thick, so its centre is two texels
            // in, and the guard band that would protect corner reconstruction
            // from such a rule reaches further than that.
            using var font = LoadFont(LatinFontPath);
            const float allowance = 1f;

            foreach (var glyph in new[] { "A", "W", "K", "a", "e", "R", "B", "M" })
            foreach (float ppem in new[] { 64f, 128f, 256f })
            {
                var tile = BakeGlyph(font, glyph, ppem, allowance, out var pixels);
                float shallowest = ShallowestMedianTexels(tile, pixels, allowance + 0.5f);
                Assert.GreaterOrEqual(shallowest, allowance - BytesToTexels,
                    $"'{glyph}' at {ppem} ppem: solid ink whose median reads " +
                    $"{shallowest:F2} texels from the outline");
            }
        }

        /// <summary>
        /// The worst the median dips BETWEEN texel centres, in texels inside the
        /// outline, over the sub-texel positions the true field places solidly
        /// inside the ink. The per-texel measurements above cannot see this: the
        /// dip lives at a channel crossing, where the median of three linear
        /// functions kinks, and both endpoints can be perfectly in tolerance.
        /// </summary>
        private static float WorstInterpolatedDipTexels(in RasterizedGlyph tile, byte[] pixels,
            float depthTexels)
        {
            const int Sub = 12;
            float worst = float.MaxValue;
            for (int iy = Sub; iy < (tile.Height - 2) * Sub; iy++)
            for (int ix = Sub; ix < (tile.Width - 2) * Sub; ix++)
            {
                float u = ix / (float)Sub, v = iy / (float)Sub;
                float truth = (SampleBilinear(tile, pixels, u, v, 3) - 127.5f) * BytesToTexels;
                if (truth < depthTexels) continue;
                float median = Median(
                    SampleBilinear(tile, pixels, u, v, 0),
                    SampleBilinear(tile, pixels, u, v, 1),
                    SampleBilinear(tile, pixels, u, v, 2));
                worst = Mathf.Min(worst, (median - 127.5f) * BytesToTexels);
            }
            return worst;
        }

        private static float SampleBilinear(in RasterizedGlyph tile, byte[] pixels,
            float u, float v, int channel)
        {
            int x = Mathf.FloorToInt(u), y = Mathf.FloorToInt(v);
            float fx = u - x, fy = v - y;
            float bottom = Mathf.Lerp(Texel(tile, pixels, x, y, channel),
                Texel(tile, pixels, x + 1, y, channel), fx);
            float top = Mathf.Lerp(Texel(tile, pixels, x, y + 1, channel),
                Texel(tile, pixels, x + 1, y + 1, channel), fx);
            return Mathf.Lerp(bottom, top, fy);
        }

        [Test]
        public void WithoutTheInterpolationPass_TheMedianDivesBetweenTexels()
        {
            // The artifact a per-texel rule cannot reach, and the one a magnified
            // tile actually shows. Across an 'A' crossbar junction two channels
            // swap rank partway between two texels; the median follows the swap
            // down to the outline while the truth says a texel of solid ink, and
            // both endpoints are inside tolerance the whole time.
            using var font = LoadFont(LatinFontPath);
            var tile = BakeGlyph(font, "A", 64f, 0f, out var pixels);

            Assert.Less(WorstInterpolatedDipTexels(tile, pixels, 1f), 0.5f,
                "the dip between texels is gone from the specimen that had one: " +
                "either the colouring changed or the measurement moved");
        }

        [Test]
        public void TheInterpolationPass_KeepsTheMedianOutOfTheNotchBetweenTexels()
        {
            // And after. Stated where it failed: not at texel centres, which the
            // per-texel rule already covers, but everywhere bilinear can put a
            // sample, which is what a magnified tile samples.
            using var font = LoadFont(LatinFontPath);
            const float allowance = 0.5f;

            // Measured a guard band inside the pass's own domain rather than at
            // its edge. The pass acts on crossings where the true distance is
            // strictly past floor + guard, so a sample sitting exactly on that
            // boundary is one it is entitled to leave alone, and asserting over
            // it would be asserting a promise nobody made.
            foreach (var glyph in new[] { "A", "W", "K", "M", "e", "R" })
            foreach (float ppem in new[] { 48f, 64f, 96f })
            {
                var tile = BakeGlyph(font, glyph, ppem, allowance, out var pixels);
                float dip = WorstInterpolatedDipTexels(tile, pixels, 2f * allowance + 0.5f);
                Assert.GreaterOrEqual(dip, allowance - 2f * BytesToTexels,
                    $"'{glyph}' at {ppem} ppem: solid ink whose reconstructed median " +
                    $"dives to {dip:F2} texels from the outline");
            }
        }

        [Test]
        public void ErrorCorrection_NeverTouchesTheBandWhereCornersAreReconstructed()
        {
            // The cost of the fix, bounded where it matters. Within about a
            // texel of the outline the median is SUPPOSED to disagree with the
            // true distance: that disagreement is what bilinear reconstruction
            // turns into a sharp corner, and overruling it there would trade the
            // whole point of `precise` against a grey mark. So every texel the
            // correction rewrites has to be one no nearby pixel's coverage is
            // decided by — deep inside the ink, or (for the sign rule) a clear
            // reach outside it.
            const float allowance = 1f;
            foreach (var path in new[] { LatinFontPath, VariableFontPath })
            {
                using var font = LoadFont(path);
                foreach (var glyph in new[] { "a", "A", "e", "g", "s", "R", "B", "W", "M" })
                foreach (float ppem in new[] { 24f, 48f, 128f })
                {
                    var tile = BakeGlyph(font, glyph, ppem, 0f, out var raw);
                    BakeGlyph(font, glyph, ppem, allowance, out var corrected);

                    for (int i = 0; i < raw.Length; i += 4)
                    {
                        if (raw[i] == corrected[i] && raw[i + 1] == corrected[i + 1] &&
                            raw[i + 2] == corrected[i + 2]) continue;

                        Assert.AreEqual(raw[i + 3], corrected[i + 3],
                            "the correction rewrote the true distance in alpha");
                        // Read back from the byte, while the rule fired on the
                        // float behind it, so one level of the encoding has to
                        // be allowed for or a texel at the exact boundary flakes.
                        float truth = (raw[i + 3] - 127.5f) * BytesToTexels;
                        Assert.Greater(Mathf.Abs(truth), allowance - BytesToTexels,
                            $"'{glyph}' at {ppem} ppem: a texel {truth:F2} texels from the " +
                            "outline was rewritten, inside the band corners are made of");
                    }
                }
            }
        }

        [Test]
        public void ErrorCorrection_DoesNotTouchAShapeWithNothingWrongWithIt()
        {
            // The cost of the fix, bounded. A square is all right angles and a
            // circle is all curve, and the correction has no business firing on
            // either; if it does, it is rounding off corners that were correct
            // and the guard band is too small.
            foreach (var shape in new[] { Square(40f), Circle(30f, 96) })
            {
                float was = GlyphRasterizer.MsdfErrorCorrectionTexels;
                byte[] corrected, raw;
                try
                {
                    GlyphRasterizer.MsdfErrorCorrectionTexels = 0.5f;
                    Bake(shape, precise: true, out corrected);
                    GlyphRasterizer.MsdfErrorCorrectionTexels = 0f;
                    Bake(shape, precise: true, out raw);
                }
                finally
                {
                    GlyphRasterizer.MsdfErrorCorrectionTexels = was;
                }

                CollectionAssert.AreEqual(raw, corrected,
                    "error correction rewrote a texel of a shape that had no clash");
            }
        }

        [Test]
        public void ChangingTheCorrection_IsANewCacheKey()
        {
            // Tiles baked with the correction and tiles baked without it are
            // different pictures of the same glyph at the same size, so an atlas
            // that kept serving the old ones would make the setting look inert.
            float was = GlyphRasterizer.MsdfErrorCorrectionTexels;
            try
            {
                int before = GlyphRasterizer.Generation;
                GlyphRasterizer.MsdfErrorCorrectionTexels = was + 0.25f;
                Assert.AreNotEqual(before, GlyphRasterizer.Generation);

                using var font = LoadFont(LatinFontPath);
                using var atlas = new GlyphAtlas(Small, precise: true);
                uint gid = FirstGlyph(font, "A");
                atlas.GetOrAdd(font, gid, 64f);
                Assert.IsTrue(atlas.Contains(font, gid, 64f));

                GlyphRasterizer.MsdfErrorCorrectionTexels = was;
                Assert.IsFalse(atlas.Contains(font, gid, 64f),
                    "the atlas answered with a tile baked under the other setting");
            }
            finally
            {
                GlyphRasterizer.MsdfErrorCorrectionTexels = was;
            }
        }

        // ------------------------------------------------------------- caching

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static uint FirstGlyph(FontData font, string text)
        {
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, text, glyphs);
            return glyphs[0].GlyphId;
        }

        private static GlyphAtlasSettings Small =>
            new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 };

        [Test]
        public void PreciseAndPlainTiles_NeverShareACacheEntry()
        {
            // The two fields are different pictures of the same glyph at the
            // same size. A cache that told them apart by glyph and size alone
            // would hand one label the other's tile, and it would look like a
            // shader bug.
            using var font = LoadFont(LatinFontPath);
            using var plain = new GlyphAtlas(Small);
            using var precise = new GlyphAtlas(Small, precise: true);
            uint gid = FirstGlyph(font, "A");

            Assert.IsFalse(plain.Precise);
            Assert.IsTrue(precise.Precise);

            var plainTile = plain.GetOrAdd(font, gid, 64f);
            Assert.IsTrue(plain.Contains(font, gid, 64f));
            Assert.IsFalse(precise.Contains(font, gid, 64f),
                "the precise cache answered for a single-channel tile");

            var preciseTile = precise.GetOrAdd(font, gid, 64f);
            Assert.IsTrue(plainTile.HasPixels);
            Assert.IsTrue(preciseTile.HasPixels);
            Assert.AreEqual(TextureFormat.R8, plain.Texture.format);
            Assert.AreEqual(TextureFormat.RGBA32, precise.Texture.format);
            Assert.AreEqual(plain.GetStats().MemoryBytes * 4, precise.GetStats().MemoryBytes);
        }

        [Test]
        public void PreciseAtlas_EvictsAndCompactsLikeTheOther()
        {
            // The precise atlas is the same machinery with wider texels, and
            // the row copies that move a tile are the part four bytes could
            // have broken silently.
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings
            {
                TextureSize = 256, LayerCount = 1,
            }, precise: true);

            for (char c = 'A'; c <= 'Z'; c++)
                atlas.GetOrAdd(font, FirstGlyph(font, c.ToString()), 48f);
            atlas.Compact();

            var stats = atlas.GetStats();
            Assert.Greater(stats.TileCount, 0);
            Assert.AreEqual(0, stats.Drops, "a 256² precise atlas could not hold the alphabet");
        }

        // -------------------------------------------------------------- label

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            return go.GetComponent<Canvas>();
        }

        private OneTextLabel NewLabel(Canvas canvas, string text)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 64f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private static Mesh Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            return label.canvasRenderer.GetMesh();
        }

        [Test]
        public void ANewLabel_IsNotPrecise()
        {
            // The default is the whole shape of this feature: MSDF costs four
            // times the atlas and buys something only large text can show, so
            // nobody gets it without asking.
            var label = NewLabel(NewCanvas(), "Hamburgefonstiv");
            Assert.IsFalse(label.Precise);

            // Counted rather than asserted absent: the shared atlas outlives a
            // test, so what can be shown is that this label added nothing to it.
            int before = SharedGlyphAtlas.PreciseAtlasExists
                ? SharedGlyphAtlas.PreciseAtlas.GetStats().TileCount
                : 0;
            Draw(label);
            int after = SharedGlyphAtlas.PreciseAtlasExists
                ? SharedGlyphAtlas.PreciseAtlas.GetStats().TileCount
                : 0;

            Assert.Greater(label.DrawnQuads.Count, 0);
            foreach (var quad in label.DrawnQuads)
                Assert.IsFalse(quad.IsPrecise);
            Assert.AreEqual(before, after,
                "a label that never asked for MSDF baked into the precise atlas anyway");
        }

        [Test]
        public void APreciseLabel_DrawsFromThePreciseAtlas()
        {
            var label = NewLabel(NewCanvas(), "AVW");
            label.Precise = true;
            var mesh = Draw(label);

            Assert.IsTrue(SharedGlyphAtlas.PreciseAtlasExists);
            Assert.Greater(SharedGlyphAtlas.PreciseAtlas.GetStats().TileCount, 0);

            var quads = label.DrawnQuads;
            Assert.Greater(quads.Count, 0);
            foreach (var quad in quads)
            {
                Assert.IsTrue(quad.IsPrecise);
                Assert.IsFalse(quad.IsColor);
            }

            // The discriminator the shader reads: 0 the single-channel atlas,
            // 1 the colour one, 2 this.
            var bounds = new List<Vector4>();
            mesh.GetUVs(2, bounds);
            Assert.AreEqual(quads.Count * 4, bounds.Count);
            foreach (var vertex in bounds) Assert.AreEqual(2f, vertex.w, 1e-6f);
        }

        [Test]
        public void PreciseAndPlainLabels_ShareOneMaterial()
        {
            // The delivery constraint decorations were held to, held to again:
            // a third atlas must not fork the material, or a precise heading
            // stops batching with the paragraph beneath it.
            var canvas = NewCanvas();
            var plain = NewLabel(canvas, "body text");
            var precise = NewLabel(canvas, "Heading");
            precise.Precise = true;

            var plainMesh = Draw(plain);
            var preciseMesh = Draw(precise);

            Assert.AreSame(SharedGlyphAtlas.Material, plain.materialForRendering);
            Assert.AreSame(SharedGlyphAtlas.Material, precise.materialForRendering);

            var plainLayout = plainMesh.GetVertexAttributes();
            var preciseLayout = preciseMesh.GetVertexAttributes();
            Assert.AreEqual(plainLayout.Length, preciseLayout.Length);
            for (int i = 0; i < plainLayout.Length; i++)
                Assert.AreEqual(plainLayout[i].attribute, preciseLayout[i].attribute);
        }

        [Test]
        public void TogglingPrecise_RebuildsTheQuadsAgainstTheOtherAtlas()
        {
            // The cached quads hold uv rects from whichever atlas baked them,
            // and the two atlases pack independently, so the switch has to
            // invalidate them even though the layout has not changed.
            var label = NewLabel(NewCanvas(), "Wave");
            Draw(label);
            int layouts = label.LayoutRuns, builds = label.QuadBuilds;

            label.Precise = true;
            Draw(label);

            Assert.AreEqual(builds + 1, label.QuadBuilds, "the stale quads were kept");
            Assert.AreEqual(layouts, label.LayoutRuns,
                "switching the field re-laid the text out; it is a rendering choice");
            foreach (var quad in label.DrawnQuads) Assert.IsTrue(quad.IsPrecise);
        }
    }
}
