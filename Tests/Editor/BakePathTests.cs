using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// The new-glyph path's shortcuts against the paths they replace: the
    /// native outline callbacks against the managed ones, the frame-wide
    /// deferred bake against a bake per run, the list-head eviction against
    /// the walk, and one large rasterizer batch against many small ones.
    /// Each switch must leave every point, every slot, every version stamp
    /// and every texel exactly where it was.
    /// </summary>
    public class BakePathTests
    {
        private const string Fonts = "Packages/com.onetext.core/Tests/Fonts~/";

        private static byte[] Bytes(string path) => File.ReadAllBytes(Path.GetFullPath(path));

        private (bool, bool) _burst;
        private bool _native, _flatten, _flat, _deferred, _fastEviction, _rawCopy, _rowJob, _background;
        private int _backgroundTexels;

        [SetUp]
        public void SetUp()
        {
            _burst = (BurstCompiler.Options.EnableBurstCompilation, BurstCompiler.Options.EnableBurstCompileSynchronously);
            BurstCompiler.Options.EnableBurstCompilation = true;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            _native = OutlineExtractor.NativeCallbacks;
            _flatten = OutlineExtractor.NativeFlatten;
            _flat = GlyphAtlas.FlatContours;
            _deferred = GlyphAtlas.DeferredBake;
            _fastEviction = GlyphAtlas.FastEviction;
            _rawCopy = GlyphAtlas.RawTileCopy;
            _rowJob = GlyphRasterizer.RowJob;
            _background = GlyphAtlas.BackgroundBake;
            _backgroundTexels = GlyphAtlas.BackgroundBakeTexels;
        }

        [TearDown]
        public void TearDown()
        {
            BurstCompiler.Options.EnableBurstCompilation = _burst.Item1;
            BurstCompiler.Options.EnableBurstCompileSynchronously = _burst.Item2;
            OutlineExtractor.NativeCallbacks = _native;
            OutlineExtractor.NativeFlatten = _flatten;
            GlyphAtlas.FlatContours = _flat;
            GlyphAtlas.DeferredBake = _deferred;
            GlyphAtlas.FastEviction = _fastEviction;
            GlyphAtlas.RawTileCopy = _rawCopy;
            GlyphRasterizer.RowJob = _rowJob;
            GlyphAtlas.BackgroundBake = _background;
            GlyphAtlas.BackgroundBakeTexels = _backgroundTexels;
            LogAssert.ignoreFailingMessages = false;
        }

        private static IEnumerable<string> OutlineFonts()
        {
            yield return Fonts + "NotoSans.ttf";
            yield return Fonts + "NotoSansArabic.ttf";
            yield return Fonts + "CffShapes.otf";
            yield return Fonts + "NotoSansVariable.ttf";
        }

        private static string Points(FontData font, uint glyph, float pixelsPerUnit, bool native, bool flatten = true)
        {
            OutlineExtractor.NativeCallbacks = native;
            OutlineExtractor.NativeFlatten = flatten;
            var outline = new GlyphOutline();
            OutlineExtractor.Extract(font, glyph, outline, pixelsPerUnit);
            var sb = new StringBuilder();
            foreach (var contour in outline.Contours)
            {
                sb.Append('[').Append(contour.Count).Append(']');
                foreach (var p in contour)
                    sb.Append(BitConverter.ToInt32(BitConverter.GetBytes(p.x), 0)).Append(',')
                        .Append(BitConverter.ToInt32(BitConverter.GetBytes(p.y), 0)).Append(' ');
            }
            return sb.ToString();
        }

        private static void CompareOutlines(FontData font, string name, int glyphs, int step)
        {
            int compared = 0, withPoints = 0;
            foreach (float ppem in new[] { 0f, 12f, 24f, 37f, 64f, 128f, 256f })
            {
                float ppu = ppem / font.UnitsPerEm;
                for (uint g = 0; g < glyphs; g += (uint)step)
                {
                    string managed = Points(font, g, ppu, native: false);
                    string native = Points(font, g, ppu, native: true);
                    if (managed != native)
                        Assert.Fail($"{name} glyph {g} at {ppem} ppem: the native flattening drew different points");
                    string replayed = Points(font, g, ppu, native: true, flatten: false);
                    if (managed != replayed)
                        Assert.Fail($"{name} glyph {g} at {ppem} ppem: the replayed recording drew different points");
                    compared++;
                    if (managed.Length > 0) withPoints++;
                }
            }
            Assert.Greater(withPoints, Math.Min(20, compared / 4), $"{name}: too few glyphs had outlines to mean anything");
        }

        [Test]
        public void Native_Outline_Callbacks_Draw_The_Same_Points_Bit_For_Bit()
        {
            Assert.IsTrue(NativeOutline.RunsInBurst(), "the native path fell back to managed code; nothing would be compared");
            foreach (var path in OutlineFonts())
            {
                using var font = FontData.Load(Bytes(path));
                CompareOutlines(font, Path.GetFileName(path), 1200, 1);
            }
        }

        [Test]
        public void Native_Outline_Callbacks_Follow_The_Variation_Axes()
        {
            using var font = FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf"));
            string before = Points(font, 40, 64f / font.UnitsPerEm, native: true);
            font.SetVariations(new FontVariation("wght", 900f));
            CompareOutlines(font, "NotoSansVariable wght 900", 600, 1);
            Assert.AreNotEqual(before, Points(font, 40, 64f / font.UnitsPerEm, native: true),
                "the bold instance drew the regular outline");
        }

        [Test]
        public void Native_Outline_Callbacks_Match_On_Large_System_Faces()
        {
            // A TrueType and a CFF CJK face, the kind the benchmarks bake.
            int found = 0;
            foreach (var path in new[]
            {
                "/System/Library/Fonts/AppleSDGothicNeo.ttc",
                "/System/Library/Fonts/Hiragino Sans GB.ttc",
                "C:/Windows/Fonts/malgun.ttf",
                "C:/Windows/Fonts/msgothic.ttc",
            })
            {
                if (!File.Exists(path)) continue;
                found++;
                using var font = FontData.Load(File.ReadAllBytes(path));
                CompareOutlines(font, Path.GetFileName(path), 20000, 37);
            }
            if (found == 0) Assert.Ignore("no system CJK face on this machine");
        }

        // ------------------------------------------------------------ atlas

        private static List<uint> GlyphsOf(FontData font, string text)
        {
            var shaped = new List<ShapedGlyph>();
            var ids = new List<uint>();
            using var shaper = new Shaper();
            foreach (char c in text)
            {
                shaped.Clear();
                shaper.Shape(font, c.ToString(), shaped);
                foreach (var glyph in shaped) ids.Add(glyph.GlyphId);
            }
            return ids;
        }

        private static string Texels(GlyphAtlas atlas)
        {
            var hash = new StringBuilder();
            for (int layer = 0; layer < atlas.Settings.LayerCount; layer++)
            {
                var pixels = atlas.Texture.GetPixelData<byte>(0, layer);
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < pixels.Length; i++) h = (h ^ pixels[i]) * 1099511628211UL;
                hash.Append(h.ToString("x")).Append(' ');
            }
            return hash.ToString();
        }

        /// <summary>
        /// Frames of label-shaped traffic into a small atlas: runs prepared the
        /// way a label prepares them (deferred when asked), lookups after, a
        /// glyph baked immediately in the middle, a compaction, and a flush per
        /// frame. Returns everything observable.
        /// </summary>
        private static List<string> Script(bool deferBake, bool precise, int frames = 6)
        {
            using var latin = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            using var arabic = FontData.Load(Bytes(Fonts + "NotoSansArabic.ttf"));
            var lines = new[]
            {
                (latin, "Wave fill AVATAR 12345 office"),
                (arabic, "بِسْمِ ٱللَّهِ ٱلرَّحْمَٰنِ ٱلرَّحِيمِ"),
                (latin, "The quick brown fox jumps over the lazy dog"),
                (latin, "ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞß àáâãäåæçèéêë"),
                (arabic, "مرحبا بالعالم"),
                (latin, "Sphinx of black quartz, judge my vow 67890"),
            };
            var singles = GlyphsOf(latin, "ĀāĂăĄąĆćĈĉĊċČčĎďĐđĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĦħĨĩĪīĬĭĮįİıĲĳĴĵ");

            var log = new List<string>();
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 }, precise);
            var shaped = new List<ShapedGlyph>();
            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();
            var locations = new List<GlyphLocation>();
            using var shaper = new Shaper();
            LogAssert.ignoreFailingMessages = true;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int l = 0; l < lines.Length; l++)
                {
                    var (font, text) = lines[(l + frame) % lines.Length];
                    float size = (frame + l) % 3 == 0 ? 64f : (frame + l) % 3 == 1 ? 40f : 28f;
                    shaped.Clear();
                    shaper.Shape(font, text, shaped);
                    GlyphClusters.Split(font, shaped, 0, shaped.Count, clusters, positioned,
                        1000f * font.UnitsPerEm / size, GlyphClusters.DefaultMergeGapUnits(font));
                    bool located = atlas.PrepareClusters(font, size, positioned, clusters, locations, deferBake);
                    log.Add($"f{frame} l{l} prepare {located} {locations.Count} v{atlas.Version}");
                    foreach (var cluster in clusters)
                    {
                        var at = atlas.GetOrAddCluster(font, size, positioned, cluster.Start, cluster.Count, cluster.Hash);
                        log.Add($"c{cluster.Hash:x} {at.HasPixels} {at.UvRect} {at.Layer} {at.OriginUnits} {at.SizeUnits} v{atlas.Version}");
                    }
                    if (l == 2)
                    {
                        // An immediate bake between deferred ones.
                        var g = atlas.GetOrAdd(latin, singles[(frame * 7) % singles.Count], 48f);
                        log.Add($"g {g.HasPixels} {g.UvRect} v{atlas.Version}");
                    }
                }
                if (frame == 3) atlas.Compact();
                atlas.Flush();
                log.Add($"flush f{frame} v{atlas.Version} texels {Texels(atlas)}");
            }
            var stats = atlas.GetStats();
            log.Add($"tiles {stats.TileCount} used {stats.UsedPixels} evictions {stats.Evictions} " +
                $"compactions {stats.Compactions} drops {stats.Drops} shelves {stats.ShelfCount}");
            LogAssert.ignoreFailingMessages = false;
            return log;
        }

        private static void AssertPressure(List<string> log)
        {
            Assert.Greater(log.Count, 100);
            StringAssert.DoesNotContain("evictions 0 ", log[log.Count - 1], "the script needs the atlas to evict");
            StringAssert.DoesNotContain("compactions 0 ", log[log.Count - 1], "the script needs the atlas to compact");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Deferred_Bake_Places_And_Writes_What_The_Immediate_Bake_Did(bool precise)
        {
            GlyphAtlas.DeferredBake = true;
            var immediate = Script(deferBake: false, precise);
            var deferred = Script(deferBake: true, precise);
            AssertPressure(immediate);
            CollectionAssert.AreEqual(immediate, deferred);
        }

        /// <summary>
        /// Batches handed to the workers mid-frame and collected by the next
        /// one, by an immediate bake, by a compaction or by the flush: every
        /// way a background batch ends.
        /// </summary>
        [TestCase(1, false)]
        [TestCase(300, false)]
        [TestCase(2000, false)]
        [TestCase(1, true)]
        public void Background_Batches_Write_What_The_Immediate_Bake_Did(int texels, bool precise)
        {
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = false;
            var immediate = Script(deferBake: false, precise);
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.BackgroundBakeTexels = texels;
            var background = Script(deferBake: true, precise);
            AssertPressure(immediate);
            CollectionAssert.AreEqual(immediate, background);
        }

        /// <summary>
        /// One atlas's batch still baking when another atlas rasterizes: the
        /// shared buffers go to the second only after the first has its tiles.
        /// </summary>
        [Test]
        public void A_Second_Atlas_Collects_The_Firsts_Background_Batch_First()
        {
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.BackgroundBakeTexels = 1;
            using var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            var shaped = new List<ShapedGlyph>();
            using (var shaper = new Shaper()) shaper.Shape(font, "Background bakes collect first", shaped);
            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();
            GlyphClusters.Split(font, shaped, 0, shaped.Count, clusters, positioned,
                1000f * font.UnitsPerEm / 40f, GlyphClusters.DefaultMergeGapUnits(font));

            string Run(bool interleave)
            {
                using var first = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });
                using var second = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });
                first.PrepareClusters(font, 40f, positioned, clusters, null, deferBake: true);
                if (interleave) second.GetOrAdd(font, shaped[0].GlyphId, 64f);
                if (!interleave) first.Flush();
                if (!interleave) second.GetOrAdd(font, shaped[0].GlyphId, 64f);
                first.Flush();
                second.Flush();
                return Texels(first) + "|" + Texels(second);
            }

            Assert.AreEqual(Run(false), Run(true));
        }

        /// <summary>
        /// Misses drawn straight into one flat buffer against misses built as
        /// lists: same sizes, same slots, same texels, both bakes, including
        /// the variable font, whose overlapping contours go through the
        /// resolver (which reads lists) and the re-bake.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void Flat_Contours_Bake_What_The_Lists_Baked(bool deferBake)
        {
            GlyphAtlas.FlatContours = false;
            var lists = Script(deferBake, false);
            GlyphAtlas.FlatContours = true;
            var flat = Script(deferBake, false);
            AssertPressure(lists);
            CollectionAssert.AreEqual(lists, flat);

            using var variable = FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf"));
            string Bake(bool on)
            {
                GlyphAtlas.FlatContours = on;
                var shaped = new List<ShapedGlyph>();
                using (var shaper = new Shaper()) shaper.Shape(variable, "AaBbQq&@%#WwMm8$ fi ŒœÆæ", shaped);
                var clusters = new List<GlyphClusters.Cluster>();
                var positioned = new List<PositionedGlyph>();
                GlyphClusters.Split(variable, shaped, 0, shaped.Count, clusters, positioned,
                    1000f * variable.UnitsPerEm / 56f, GlyphClusters.DefaultMergeGapUnits(variable));
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
                var log = new StringBuilder();
                for (int pass = 0; pass < 2; pass++)
                {
                    atlas.PrepareClusters(variable, 56f + pass * 40f, positioned, clusters, null, deferBake);
                    foreach (var cluster in clusters)
                    {
                        var at = atlas.GetOrAddCluster(variable, 56f + pass * 40f, positioned, cluster.Start, cluster.Count, cluster.Hash);
                        log.Append(at.UvRect).Append(at.OriginUnits).Append(at.SizeUnits).Append(';');
                    }
                    atlas.Flush();
                }
                return log + Texels(atlas);
            }
            Assert.AreEqual(Bake(false), Bake(true));
        }

        [Test]
        public void Deferred_Bake_Switched_Off_Is_The_Immediate_Bake()
        {
            GlyphAtlas.DeferredBake = false;
            CollectionAssert.AreEqual(Script(deferBake: false, false), Script(deferBake: true, false));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Fast_Eviction_Chooses_The_Tiles_The_Walk_Chose(bool deferBake)
        {
            GlyphAtlas.FastEviction = false;
            var walk = Script(deferBake, false, frames: 8);
            GlyphAtlas.FastEviction = true;
            var heads = Script(deferBake, false, frames: 8);
            AssertPressure(walk);
            CollectionAssert.AreEqual(walk, heads);
        }

        [Test]
        public void Raw_Tile_Copy_Writes_The_Same_Texels()
        {
            GlyphAtlas.RawTileCopy = false;
            var rows = Script(deferBake: true, false);
            GlyphAtlas.RawTileCopy = true;
            var raw = Script(deferBake: true, false);
            CollectionAssert.AreEqual(rows, raw);
            GlyphAtlas.RawTileCopy = false;
            var preciseRows = Script(deferBake: true, true, frames: 3);
            GlyphAtlas.RawTileCopy = true;
            CollectionAssert.AreEqual(preciseRows, Script(deferBake: true, true, frames: 3));
        }

        // ------------------------------------------------------- rasterizer

        /// <summary>
        /// The deferred bake hands the rasterizer one batch for a whole frame
        /// where it used to get one per run. A tile's bytes must not depend on
        /// what else is in its batch, including the overlap scan, whose path
        /// depends on what the previous batch found.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void A_Large_Batch_Bakes_Each_Tile_As_A_Batch_Of_One_Does(bool precise)
        {
            // The variable font keeps its overlapping contours and has no
            // overlap index, so the scan and the re-bake both run.
            using var variable = FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf"));
            using var latin = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            var requests = new List<RasterizeRequest>();
            foreach (var font in new[] { variable, latin })
            {
                foreach (uint g in GlyphsOf(font, "AaBbQq&@%#WwMmŒœÆæ8$ßfiøØ"))
                {
                    var outline = new GlyphOutline();
                    float ppu = 40f / font.UnitsPerEm;
                    OutlineExtractor.Extract(font, g, outline, ppu);
                    var contours = new List<List<Vector2>>();
                    foreach (var c in outline.Contours) contours.Add(new List<Vector2>(c));
                    requests.Add(new RasterizeRequest { Contours = contours, PixelsPerUnit = ppu });
                }
            }

            var one = new List<RasterizedGlyph>();
            var single = new List<RasterizeRequest>(1) { default };
            var each = new List<byte[]>();
            foreach (var request in requests)
            {
                single[0] = request;
                GlyphRasterizer.RasterizeBatch(single, one, precise);
                each.Add(one[0].IsEmpty ? Array.Empty<byte>() : one[0].CopyPixels());
            }

            // Twice: the second batch runs on whatever the first one found.
            for (int pass = 0; pass < 2; pass++)
            {
                var all = new List<RasterizedGlyph>();
                GlyphRasterizer.RasterizeBatch(requests, all, precise);
                Assert.AreEqual(requests.Count, all.Count);
                for (int i = 0; i < requests.Count; i++)
                {
                    var bytes = all[i].IsEmpty ? Array.Empty<byte>() : all[i].CopyPixels();
                    CollectionAssert.AreEqual(each[i], bytes, $"request {i}, pass {pass}");
                }
            }
        }

        /// <summary>
        /// Requests the way the atlas builds them: single glyphs and merged
        /// clusters of neighbours (one group per glyph), at several densities.
        /// </summary>
        private static List<RasterizeRequest> ClusterRequests(FontData font, string text, float[] ppems)
        {
            var requests = new List<RasterizeRequest>();
            var shaped = new List<ShapedGlyph>();
            using (var shaper = new Shaper()) shaper.Shape(font, text, shaped);
            foreach (float ppem in ppems)
            {
                float ppu = ppem / font.UnitsPerEm;
                for (int width = 1; width <= 3; width++)
                    for (int start = 0; start + width <= shaped.Count; start += width)
                    {
                        var contours = new List<List<Vector2>>();
                        var groups = new List<int>();
                        float pen = 0f;
                        for (int i = 0; i < width; i++)
                        {
                            var glyph = shaped[start + i];
                            var outline = new GlyphOutline();
                            OutlineExtractor.Extract(font, glyph.GlyphId, outline, ppu);
                            foreach (var c in outline.Contours)
                            {
                                if (c.Count < 2) continue;
                                var moved = new List<Vector2>(c.Count);
                                foreach (var point in c) moved.Add(point + new Vector2(pen + glyph.XOffset, glyph.YOffset));
                                contours.Add(moved);
                                groups.Add(i);
                            }
                            // Tight enough that neighbours overlap, as joined scripts do.
                            pen += glyph.XAdvance * 0.8f;
                        }
                        requests.Add(new RasterizeRequest { Contours = contours, Groups = groups, PixelsPerUnit = ppu });
                    }
            }
            return requests;
        }

        private static List<byte[]> Bake(List<RasterizeRequest> requests)
        {
            var results = new List<RasterizedGlyph>();
            GlyphRasterizer.RasterizeBatch(requests, results, false);
            var bytes = new List<byte[]>();
            foreach (var r in results) bytes.Add(r.IsEmpty ? Array.Empty<byte>() : r.CopyPixels());
            return bytes;
        }

        [Test]
        public void Row_Job_Bakes_The_Texel_Jobs_Bytes()
        {
            var cases = new List<(string name, FontData font, string text)>();
            try
            {
                cases.Add(("latin", FontData.Load(Bytes(Fonts + "NotoSans.ttf")),
                    "Wave fill AVATAR office ffi 12345 &@%# ÆæŒœ ßøØ gjpqy"));
                cases.Add(("arabic", FontData.Load(Bytes(Fonts + "NotoSansArabic.ttf")),
                    "بِسْمِ ٱللَّهِ ٱلرَّحْمَٰنِ ٱلرَّحِيمِ مرحبا"));
                // Overlapping contours: resolved into wound-only and measured-only pieces.
                cases.Add(("variable", FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf")),
                    "AaBbQq&@%#WwMm8$ fi"));
                cases.Add(("cff", FontData.Load(Bytes(Fonts + "CffShapes.otf")), "ABCDEFabcdef0123"));
                if (File.Exists("/System/Library/Fonts/AppleSDGothicNeo.ttc"))
                    cases.Add(("hangul", FontData.Load(File.ReadAllBytes("/System/Library/Fonts/AppleSDGothicNeo.ttc")),
                        "한글 글리프 아틀라스 굽기 똠방각하 뷁 漢字"));

                long texels = 0;
                foreach (var (name, font, text) in cases)
                {
                    var requests = ClusterRequests(font, text, new[] { 12f, 24f, 40f, 96f, 200f });
                    GlyphRasterizer.RowJob = false;
                    var texel = Bake(requests);
                    GlyphRasterizer.RowJob = true;
                    var row = Bake(requests);
                    Assert.AreEqual(texel.Count, row.Count);
                    for (int i = 0; i < texel.Count; i++)
                    {
                        texels += texel[i].Length;
                        CollectionAssert.AreEqual(texel[i], row[i], $"{name} request {i}");
                    }
                }
                Assert.Greater(texels, 1_000_000, "too few texels compared to mean anything");
            }
            finally
            {
                foreach (var c in cases) c.font.Dispose();
            }
        }
    }
}
