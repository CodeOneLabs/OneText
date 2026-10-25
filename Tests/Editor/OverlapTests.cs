using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Glyphs whose own contours overlap: strokes drawn as separate outlines
    /// that cross, abut or loop over themselves, which is valid TrueType and
    /// what most fonts exported from variable sources ship.
    ///
    /// The nonzero rule signs them right; the distance used to be the minimum
    /// over every edge, buried ones included, so the field fell to the outline
    /// value in the middle of the ink wherever two strokes met. Every test here
    /// is a pair: the repair on, and the same bake with it off, so that a test
    /// which stopped exercising the problem would say so instead of passing.
    /// </summary>
    public class OverlapTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string CffFontPath = "Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        // 64 ppem on a 1000-unit em: a texel is 15.6 units and a reach (4
        // texels) 62.5, so a point 100 units from the nearest real outline is
        // past the reach and reads 255, and one on a buried edge read 128.
        private const float PixelsPerUnit = 64f / 1000f;

        private const byte Outline = 128;

        // A texel centre within half a texel of an edge reads within 16 levels
        // of the outline; one past the nearest real outline by at least 1.75
        // texels reads 200 or more. Every buried edge sampled below is a
        // texel or more inside the ink, so the two are well apart.
        private const byte AtTheOutline = Outline + 20;
        private const byte Deep = 200;

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        [TearDown]
        public void Restore()
        {
            GlyphRasterizer.ResolveOverlaps = true;
            GlyphRasterizer.Cull = true;
        }

        // ------------------------------------------------------------ shapes

        /// <summary>A closed polygon the way the extractor delivers one: start repeated at the end, twice.</summary>
        private static List<Vector2> Polygon(params float[] xy)
        {
            var points = new List<Vector2>();
            for (int i = 0; i < xy.Length; i += 2) points.Add(new Vector2(xy[i], xy[i + 1]));
            points.Add(points[0]);
            points.Add(points[0]);
            return points;
        }

        private static List<Vector2> Box(float x0, float y0, float x1, float y1, bool counterClockwise = true) =>
            counterClockwise
                ? Polygon(x0, y0, x1, y0, x1, y1, x0, y1)
                : Polygon(x0, y0, x0, y1, x1, y1, x1, y0);

        /// <summary>Two bars crossing, both wound the same way: four buried edges round the shared square.</summary>
        private static List<List<Vector2>> Cross() =>
            new List<List<Vector2>> { Box(40, 200, 460, 400), Box(200, 40, 400, 560) };

        /// <summary>A crossbar butted against a stem: one edge each, lying along each other.</summary>
        private static List<List<Vector2>> Abutment() =>
            new List<List<Vector2>> { Box(100, 0, 200, 600), Box(200, 250, 500, 350) };

        /// <summary>One contour crossing itself: the pentagon in the middle is wound twice.</summary>
        private static List<List<Vector2>> Pentagram() =>
            new List<List<Vector2>> { Polygon(250, 500, 397, 48, 12, 327, 488, 327, 103, 48) };

        /// <summary>A contour inside another wound the same way: a dot drawn on top of a stem.</summary>
        private static List<List<Vector2>> Nested() =>
            new List<List<Vector2>> { Box(0, 0, 400, 400), Box(100, 100, 300, 300) };

        /// <summary>The same, wound the other way: a counter, which must stay a counter.</summary>
        private static List<List<Vector2>> Counter() =>
            new List<List<Vector2>> { Box(0, 0, 400, 400), Box(100, 100, 300, 300, counterClockwise: false) };

        // ----------------------------------------------------------- helpers

        private static RasterizedGlyph Bake(List<List<Vector2>> contours, List<int> groups, bool precise,
            bool resolve, out byte[] pixels)
        {
            GlyphRasterizer.ResolveOverlaps = resolve;
            var tile = GlyphRasterizer.RasterizeContours(contours, groups, PixelsPerUnit, precise);
            pixels = tile.CopyPixels();
            GlyphRasterizer.ResolveOverlaps = true;
            return tile;
        }

        /// <summary>A channel of a tile's texel at a glyph-space point.</summary>
        private static byte Sample(in RasterizedGlyph tile, byte[] pixels, Vector2 pointUnits, int channel = 0)
        {
            int px = Mathf.RoundToInt((pointUnits.x - tile.OriginUnits.x) / tile.UnitsPerPixel - 0.5f);
            int py = Mathf.RoundToInt((pointUnits.y - tile.OriginUnits.y) / tile.UnitsPerPixel - 0.5f);
            Assert.IsTrue(px >= 0 && px < tile.Width && py >= 0 && py < tile.Height,
                $"sample point {pointUnits} falls outside the tile");
            return pixels[(py * tile.Width + px) * tile.BytesPerTexel + channel];
        }

        private static byte Median(in RasterizedGlyph tile, byte[] pixels, Vector2 pointUnits)
        {
            byte r = Sample(tile, pixels, pointUnits, 0);
            byte g = Sample(tile, pixels, pointUnits, 1);
            byte b = Sample(tile, pixels, pointUnits, 2);
            return (byte)Mathf.Max(Mathf.Min(r, g), Mathf.Min(Mathf.Max(r, g), b));
        }

        /// <summary>
        /// Asserts a buried edge no longer reaches the field at
        /// <paramref name="point"/>: at the outline with the repair off, deep
        /// with it on, in the plain field and in both halves of the precise one.
        /// </summary>
        private static void AssertBuriedEdgeGone(string shape, List<List<Vector2>> contours, Vector2 point)
        {
            var off = Bake(contours, null, false, false, out var offPixels);
            var on = Bake(contours, null, false, true, out var onPixels);
            byte before = Sample(off, offPixels, point), after = Sample(on, onPixels, point);
            Debug.Log($"[overlap] {shape} at {point}: {before} -> {after}");
            Assert.Less(before, AtTheOutline,
                $"{shape}: with the repair off the buried edge at {point} should read at the outline; " +
                "if it does not, this test no longer looks at a buried edge");
            Assert.GreaterOrEqual(after, Deep, $"{shape}: a buried edge still dents the field at {point}");

            var preciseOff = Bake(contours, null, true, false, out var preciseOffPixels);
            var preciseOn = Bake(contours, null, true, true, out var preciseOnPixels);
            Assert.Less(Sample(preciseOff, preciseOffPixels, point, 3), AtTheOutline);
            Assert.GreaterOrEqual(Sample(preciseOn, preciseOnPixels, point, 3), Deep,
                $"{shape}: a buried edge still dents the precise field's true distance at {point}");
            Assert.GreaterOrEqual(Median(preciseOn, preciseOnPixels, point), Deep,
                $"{shape}: a buried edge still dents the precise field's median at {point}");
        }

        /// <summary>
        /// What the repair may and may not touch: nothing outside the ink, and
        /// nothing inside it made shallower. Only edges are taken away, so a
        /// texel's nearest edge can only get further off, and outside the ink
        /// the nearest edge is always a real one.
        /// </summary>
        private static void AssertOnlyDeepens(string shape, List<List<Vector2>> contours, List<int> groups = null)
        {
            foreach (bool precise in new[] { false, true })
            {
                var off = Bake(contours, groups, precise, false, out var before);
                var on = Bake(contours, groups, precise, true, out var after);
                Assert.AreEqual(off.Width, on.Width, $"{shape}: the tile changed size");
                Assert.AreEqual(off.Height, on.Height, $"{shape}: the tile changed size");
                int channel = precise ? 3 : 0, stride = precise ? 4 : 1;
                for (int i = channel; i < before.Length; i += stride)
                {
                    if (before[i] < Outline)
                        Assert.AreEqual(before[i], after[i], $"{shape}: a texel outside the ink moved");
                    Assert.GreaterOrEqual(after[i], before[i], $"{shape}: a texel inside the ink got shallower");
                }
            }
        }

        // ------------------------------------------------------------- tests

        [Test]
        public void CrossingContours_BuryNoEdgeInTheInk()
        {
            // The midpoints of the four edges that run through the shared
            // square, each 100 units (1.6 reaches) from the nearest real
            // outline.
            foreach (var point in new[]
                     {
                         new Vector2(200, 300), new Vector2(400, 300),
                         new Vector2(300, 200), new Vector2(300, 400),
                     })
                AssertBuriedEdgeGone("cross", Cross(), point);
            AssertOnlyDeepens("cross", Cross());
        }

        [Test]
        public void CffOverlap_BuriesNoEdgeInTheInk()
        {
            // The same shape as it comes out of a real font: 'Q' in the CFF
            // specimen is the cross above, drawn as two overlapping contours.
            using var font = LoadFont(CffFontPath);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, "Q", shaped);
            var outline = new GlyphOutline();
            OutlineExtractor.Extract(font, shaped[0].GlyphId, outline, PixelsPerUnit);
            var contours = new List<List<Vector2>>(outline.Contours);
            Assert.AreEqual(2, contours.Count, "the specimen's 'Q' is two contours");

            AssertBuriedEdgeGone("CFF Q", contours, new Vector2(200, 300));
            AssertBuriedEdgeGone("CFF Q", contours, new Vector2(300, 400));
            AssertOnlyDeepens("CFF Q", contours);
        }

        [Test]
        public void AbuttingStrokes_LeaveNoSeam()
        {
            // Two edges lying along each other at x = 200, wound opposite ways:
            // ink on both sides of the pair, which is the faint line at a joint.
            AssertBuriedEdgeGone("abutment", Abutment(), new Vector2(200, 300));
            AssertOnlyDeepens("abutment", Abutment());
        }

        [Test]
        public void SelfIntersectingContour_BuriesNothingInsideItself()
        {
            // The pentagon in the middle of a pentagram is wound twice, so the
            // middle of each chord is buried; (250, 327) is the middle of the
            // horizontal one, about 53 units (3.4 texels) from the outline.
            AssertBuriedEdgeGone("pentagram", Pentagram(), new Vector2(250, 327));
            AssertOnlyDeepens("pentagram", Pentagram());
        }

        [Test]
        public void AContourInsideInkWoundTheSameWay_IsNotMeasured()
        {
            AssertBuriedEdgeGone("nested", Nested(), new Vector2(100, 200));
            AssertOnlyDeepens("nested", Nested());
        }

        [Test]
        public void ACounter_StaysACounter()
        {
            // Nested the other way, the inner contour is a hole: its edges are
            // the outline, and taking them away would fill it in.
            foreach (bool resolve in new[] { false, true })
            {
                var tile = Bake(Counter(), null, false, resolve, out var pixels);
                byte edge = Sample(tile, pixels, new Vector2(100, 200));
                byte middle = Sample(tile, pixels, new Vector2(200, 200));
                Assert.That((int)edge, Is.InRange(Outline - 20, Outline + 20),
                    $"the counter's edge is not at the outline (repair {(resolve ? "on" : "off")})");
                Assert.Less(middle, Outline, "the counter filled in");
            }
            AssertOnlyDeepens("counter", Counter());
        }

        [Test]
        public void GlyphsOfACluster_AreResolvedEachOnItsOwn()
        {
            // Two glyphs of one cluster, each crossing itself, and crossing each
            // other as well. Between glyphs the union already handled the
            // overlap; inside each it is this repair, and the two must compose.
            var contours = new List<List<Vector2>>
            {
                Box(40, 200, 460, 400), Box(200, 40, 400, 560),
                Box(300, 250, 700, 350), Box(500, 100, 600, 500),
            };
            var groups = new List<int> { 0, 0, 1, 1 };
            var off = Bake(contours, groups, false, false, out var before);
            var on = Bake(contours, groups, false, true, out var after);
            var insideSecond = new Vector2(500, 300); // the second glyph's own buried edge
            Assert.Less(Sample(off, before, insideSecond), AtTheOutline);
            Assert.GreaterOrEqual(Sample(on, after, insideSecond), Deep);
            AssertOnlyDeepens("cluster", contours, groups);
        }

        [Test]
        public void FontsWithoutOverlaps_BakeByteForByteTheSame()
        {
            // The scan runs for every glyph; for one that meets nothing it must
            // change nothing, which is the whole of what most fonts see of it.
            // NotoSansArabic is not such a font all through: six of the letters
            // below (glyphs 18, 36, 60, 70, 71, 77) are drawn from pieces that
            // overlap, are flagged at every size, and are meant to change. The
            // rest of it, and all of the Latin sample, must not.
            using var shaper = new Shaper();

            foreach (var path in new[] { LatinFontPath, ArabicFontPath })
            {
                using var font = LoadFont(path);
                var overlapping = new HashSet<int>(ContourOverlaps.FindOverlappingGlyphs(font,
                    ContourOverlaps.GlyphCountOf(File.ReadAllBytes(Path.GetFullPath(path)))));
                var glyphs = new List<ShapedGlyph>();
                shaper.Shape(font, "AaBbGgQqMmWw@#%&0123456789", glyphs);
                shaper.Shape(font, "السلام عليكم", glyphs);
                int skipped = glyphs.FindAll(g => overlapping.Contains((int)g.GlyphId)).Count;
                if (path == LatinFontPath) Assert.AreEqual(0, skipped, "the Latin sample has no overlaps to skip");
                else Assert.Greater(skipped, 0, "the Arabic sample no longer reaches an overlapping letter");

                foreach (bool precise in new[] { false, true })
                foreach (int ppem in new[] { 24, 48, 96 })
                foreach (var glyph in glyphs)
                {
                    if (overlapping.Contains((int)glyph.GlyphId)) continue;
                    float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
                    GlyphRasterizer.ResolveOverlaps = false;
                    var plain = GlyphRasterizer.Rasterize(font, glyph.GlyphId, pixelsPerUnit, precise);
                    var plainPixels = plain.IsEmpty ? null : plain.CopyPixels();
                    GlyphRasterizer.ResolveOverlaps = true;
                    var resolved = GlyphRasterizer.Rasterize(font, glyph.GlyphId, pixelsPerUnit, precise);
                    if (plain.IsEmpty) continue;
                    CollectionAssert.AreEqual(plainPixels, resolved.CopyPixels(),
                        $"glyph {glyph.GlyphId} at {ppem} ppem ({(precise ? "precise" : "plain")}) changed");
                }
            }
        }

        [Test]
        public void ResolvedContours_CullExactlyLikeNaive()
        {
            // The measured-only and wound-only contours a repair produces go
            // through the same culls as any other; skipping them must stay a
            // pure optimisation.
            //
            // For a precise tile that means the true distance, in alpha, which
            // is where the kinds are read. The colour channels are not compared:
            // culled MSDF channels already part from the naive ones for glyphs
            // with nothing to repair (NotoSans at 24-96 ppem: 225 median texels
            // over 78 tiles, the same before this repair existed), and that is
            // the channel cull's own behaviour, not something a repair changes.
            foreach (var (name, contours) in new[]
                     {
                         ("cross", Cross()), ("abutment", Abutment()), ("pentagram", Pentagram()),
                         ("nested", Nested()),
                     })
            foreach (bool precise in new[] { false, true })
            {
                GlyphRasterizer.Cull = false;
                var naive = GlyphRasterizer.RasterizeContours(contours, null, PixelsPerUnit, precise);
                var naivePixels = naive.CopyPixels();
                GlyphRasterizer.Cull = true;
                var culled = GlyphRasterizer.RasterizeContours(contours, null, PixelsPerUnit, precise);
                var culledPixels = culled.CopyPixels();
                if (!precise)
                {
                    CollectionAssert.AreEqual(naivePixels, culledPixels, $"{name} differs with culling on");
                    continue;
                }
                Assert.AreEqual(naivePixels.Length, culledPixels.Length);
                for (int i = 3; i < naivePixels.Length; i += 4)
                    Assert.AreEqual(naivePixels[i], culledPixels[i],
                        $"{name} (precise) true distance differs with culling on at texel {i / 4}");
            }
        }

        [Test]
        public void ABatch_GivesEveryTileWhatItWouldGetAlone()
        {
            // A batch with overlapping and clean glyphs mixed: the flagged
            // tiles are baked a second time (or, once the batcher expects
            // overlaps, resolved before the first), and either way every tile
            // has to land in its own slot. Run three times so both strategies
            // are taken: a clean batch first leaves the batcher expecting none.
            using var latin = LoadFont(LatinFontPath);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(latin, "R", shaped);
            var clean = new GlyphOutline();
            OutlineExtractor.Extract(latin, shaped[0].GlyphId, clean, PixelsPerUnit);
            var cleanContours = new List<List<Vector2>>(clean.Contours);

            var shapes = new List<List<List<Vector2>>> { Cross(), cleanContours, Pentagram(), Abutment() };
            foreach (bool precise in new[] { false, true })
            {
                var alone = new List<byte[]>();
                foreach (var contours in shapes)
                    alone.Add(GlyphRasterizer.RasterizeContours(contours, null, PixelsPerUnit, precise).CopyPixels());

                var requests = new List<RasterizeRequest>();
                foreach (var contours in shapes)
                    requests.Add(new RasterizeRequest { Contours = contours, PixelsPerUnit = PixelsPerUnit });
                var results = new List<RasterizedGlyph>();

                GlyphRasterizer.RasterizeContours(cleanContours, null, PixelsPerUnit, precise);
                for (int round = 0; round < 3; round++)
                {
                    GlyphRasterizer.RasterizeBatch(requests, results, precise);
                    for (int i = 0; i < shapes.Count; i++)
                        CollectionAssert.AreEqual(alone[i], results[i].CopyPixels(),
                            $"tile {i} of the batch differs from the same tile baked alone (round {round})");
                }
            }
        }

        // ------------------------------------------------- the font asset's index

        private static OneFontAsset IndexedAsset(string path)
        {
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.Initialize(File.ReadAllBytes(Path.GetFullPath(path)), "Overlap Test", path);
            Assert.IsFalse(asset.HasOverlapIndex, "a freshly packed asset has not been indexed yet");
            Assert.IsTrue(asset.IndexOverlaps());
            Assert.IsTrue(asset.HasOverlapIndex);
            return asset;
        }

        private static uint GlyphOf(FontData font, char c)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, c.ToString(), shaped);
            return shaped[0].GlyphId;
        }

        [Test]
        public void GlyphCount_IsReadFromTheFile()
        {
            Assert.AreEqual(6, ContourOverlaps.GlyphCountOf(File.ReadAllBytes(Path.GetFullPath(CffFontPath))),
                ".notdef, space, O, Q, S and I");
            Assert.AreEqual(4515, ContourOverlaps.GlyphCountOf(File.ReadAllBytes(Path.GetFullPath(LatinFontPath))));
            Assert.AreEqual(0, ContourOverlaps.GlyphCountOf(new byte[] { 1, 2, 3 }), "not a font");
        }

        [Test]
        public void AFontAsset_RecordsWhichGlyphsOverlap()
        {
            var asset = IndexedAsset(CffFontPath);
            try
            {
                var font = asset.Font;
                Assert.IsTrue(font.TryGetOverlap(GlyphOf(font, 'Q'), out bool q), "an indexed face answers");
                Assert.IsTrue(q, "'Q' is two contours crossing");
                font.TryGetOverlap(GlyphOf(font, 'O'), out bool o);
                Assert.IsFalse(o, "'O' is a ring and its counter: nested, wound apart, meeting nowhere");
                font.TryGetOverlap(GlyphOf(font, 'I'), out bool i);
                Assert.IsFalse(i, "'I' is one rectangle");

                using var unindexed = LoadFont(CffFontPath);
                Assert.IsFalse(unindexed.TryGetOverlap(GlyphOf(unindexed, 'Q'), out _),
                    "a face loaded without an asset knows nothing, and is scanned instead");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AnIndexedFont_BakesExactlyWhatAScannedOneBakes()
        {
            // The index is a shortcut past the scan, not a second opinion: a
            // glyph has to come out the same whichever way its overlaps were
            // found, including the one the index says is clean.
            var asset = IndexedAsset(CffFontPath);
            using var scanned = LoadFont(CffFontPath);
            try
            {
                foreach (bool precise in new[] { false, true })
                foreach (char c in "OQSI")
                {
                    var fromIndex = GlyphRasterizer.Rasterize(asset.Font, GlyphOf(asset.Font, c), PixelsPerUnit, precise);
                    var indexPixels = fromIndex.CopyPixels();
                    var fromScan = GlyphRasterizer.Rasterize(scanned, GlyphOf(scanned, c), PixelsPerUnit, precise);
                    CollectionAssert.AreEqual(fromScan.CopyPixels(), indexPixels,
                        $"'{c}' ({(precise ? "precise" : "plain")}) differs between the index and the scan");
                    if (c == 'Q')
                        Assert.GreaterOrEqual(Sample(fromIndex, indexPixels, new Vector2(200, 300), precise ? 3 : 0), Deep,
                            "the index named 'Q' and its buried edges are still in the field");
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void TheIndex_DescribesOnlyTheDefaultInstance()
        {
            // Variation moves outlines, and strokes that cross at one weight
            // may not at another. A varied face scans as it bakes.
            var asset = IndexedAsset(VariableFontPath);
            try
            {
                uint a = GlyphOf(asset.Font, 'A');
                Assert.IsTrue(asset.Font.TryGetOverlap(a, out _), "the default instance answers from the index");
                var bold = asset.GetVariant(new[] { new FontVariation("wght", 700f) });
                Assert.IsFalse(bold.TryGetOverlap(a, out _), "a varied instance must not trust it");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void TheIndex_FollowsTheFontFile()
        {
            var asset = IndexedAsset(CffFontPath);
            try
            {
                int count = asset.OverlappingGlyphCount;
                Assert.Greater(count, 0);

                // The same file packed another way keeps its glyph ids.
                asset.Repack(asset.Packing == OneFontAsset.FontPacking.Smallest
                    ? OneFontAsset.FontPacking.Fast
                    : OneFontAsset.FontPacking.Smallest);
                Assert.IsTrue(asset.HasOverlapIndex, "repacking dropped the index");
                Assert.AreEqual(count, asset.OverlappingGlyphCount);

                // A different file does not.
                asset.Initialize(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)), "Other", LatinFontPath);
                Assert.IsFalse(asset.HasOverlapIndex, "an index of another file's glyph ids survived");
                Assert.AreEqual(-1, asset.OverlappingGlyphCount);
                Assert.IsFalse(asset.Font.TryGetOverlap(0, out _));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
