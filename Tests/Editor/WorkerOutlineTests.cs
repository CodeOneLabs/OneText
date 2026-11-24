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
    /// The new-glyph work moved off the main thread, against the main-thread
    /// path it replaces: outlines drawn by the job workers against outlines
    /// drawn here, batches baking side by side against one at a time, misses
    /// dispatched as soon as the workers are idle, the remembered no-room walk
    /// against the full walk, and a font changing or going away while glyphs
    /// placed with it are still to be drawn. Every switch must leave every
    /// point, slot, version stamp and texel where it was.
    /// </summary>
    public class WorkerOutlineTests
    {
        private const string Fonts = "Packages/com.onetext.core/Tests/Fonts~/";

        private static byte[] Bytes(string path) => File.ReadAllBytes(Path.GetFullPath(path));

        private (bool, bool) _burst;
        private bool _workers, _pipelined, _eager, _fitMemo, _layerPointers, _background, _deferred;
        private int _texels;

        [SetUp]
        public void SetUp()
        {
            _burst = (BurstCompiler.Options.EnableBurstCompilation, BurstCompiler.Options.EnableBurstCompileSynchronously);
            BurstCompiler.Options.EnableBurstCompilation = true;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            _workers = GlyphAtlas.WorkerOutlines;
            _pipelined = GlyphRasterizer.PipelinedBatches;
            _eager = GlyphAtlas.EagerDispatch;
            _fitMemo = GlyphAtlas.FitMemo;
            _layerPointers = GlyphAtlas.LayerPointersOnce;
            _background = GlyphAtlas.BackgroundBake;
            _deferred = GlyphAtlas.DeferredBake;
            _texels = GlyphAtlas.BackgroundBakeTexels;
            OutlineJobs.ForgetStats();
        }

        [TearDown]
        public void TearDown()
        {
            BurstCompiler.Options.EnableBurstCompilation = _burst.Item1;
            BurstCompiler.Options.EnableBurstCompileSynchronously = _burst.Item2;
            GlyphAtlas.WorkerOutlines = _workers;
            GlyphRasterizer.PipelinedBatches = _pipelined;
            GlyphAtlas.EagerDispatch = _eager;
            GlyphAtlas.FitMemo = _fitMemo;
            GlyphAtlas.LayerPointersOnce = _layerPointers;
            GlyphAtlas.BackgroundBake = _background;
            GlyphAtlas.DeferredBake = _deferred;
            GlyphAtlas.BackgroundBakeTexels = _texels;
            AtlasDiagnostics.Enabled = false;
            LogAssert.ignoreFailingMessages = false;
            OutlineJobs.ForgetStats();
        }

        // ------------------------------------------------------------ outlines

        private static string Describe(FlatContours flat) => flat.DescribeForTests();

        /// <summary>
        /// Every glyph in [0, count) by <paramref name="step"/>, a batch at a
        /// time, at several densities, moved by awkward offsets: the job's
        /// points, ranges, groups and bounds against the main thread's, bit
        /// for bit, from several jobs running at once.
        /// </summary>
        private static int CompareFont(FontData font, string name, int count, int step)
        {
            var main = new FlatContours();
            var workers = new List<FlatContours> { new FlatContours(), new FlatContours(), new FlatContours() };
            int compared = 0;
            foreach (float ppem in new[] { 12f, 24f, 37f, 64f, 128f, 256f })
            {
                float ppu = ppem / font.UnitsPerEm;
                var glyphs = new List<uint>();
                var offsets = new List<Vector2>();
                for (uint g = 0; g < count; g += (uint)step)
                {
                    glyphs.Add(g);
                    offsets.Add(new Vector2(g * 37.3f - 101.7f, (g % 7) * -13.1f + 0.003f));
                    if (glyphs.Count < 64 && g + step < count) continue;
                    int refused = OutlineJobs.DrawForTests(font, glyphs, offsets, ppu, main, workers);
                    Assert.AreEqual(0, refused, $"{name} at {ppem} ppem: a job refused glyphs it was given the statistics of");
                    string expected = Describe(main);
                    for (int k = 0; k < workers.Count; k++)
                        if (Describe(workers[k]) != expected)
                            Assert.Fail($"{name} at {ppem} ppem, glyphs {glyphs[0]}..{glyphs[glyphs.Count - 1]}: " +
                                $"job {k} drew different points");
                    compared += glyphs.Count;
                    glyphs.Clear();
                    offsets.Clear();
                }
            }
            return compared;
        }

        [Test]
        public void Worker_Outlines_Are_The_Main_Threads_Bit_For_Bit()
        {
            Assert.IsTrue(NativeOutline.RunsInBurst(), "the native path fell back to managed code; nothing would be compared");
            Assert.IsTrue(OutlineJobs.Available);
            int compared = 0;
            foreach (var path in new[] { "NotoSans.ttf", "NotoSansArabic.ttf", "CffShapes.otf", "NotoSansVariable.ttf" })
            {
                using var font = FontData.Load(Bytes(Fonts + path));
                compared += CompareFont(font, path, 1200, 1);
            }
            Assert.Greater(compared, 10000);
        }

        [Test]
        public void Worker_Outlines_Follow_The_Variation_Axes()
        {
            using var font = FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf"));
            CompareFont(font, "default", 400, 3);
            font.SetVariations(new FontVariation("wght", 900f));
            CompareFont(font, "wght 900", 600, 1);
            // A variant made with axes, which is where they usually live.
            using var light = font.CreateVariant(new FontVariation("wght", 250f), new FontVariation("wdth", 80f));
            CompareFont(light, "wght 250 wdth 80", 600, 1);
            font.SetVariations();
            CompareFont(font, "back to default", 400, 3);
        }

        [Test]
        public void Worker_Outlines_Match_On_Large_System_Faces()
        {
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
                CompareFont(font, Path.GetFileName(path), 20000, 61);
            }
            if (found == 0) Assert.Ignore("no system CJK face on this machine");
        }

        // ------------------------------------------------------------ atlas

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
        /// Frames of label traffic into a small atlas: the same lines again at
        /// the same sizes (so later frames know every glyph and draw them on the
        /// workers), several faces, one with overlapping contours and no index,
        /// immediate bakes and a compaction in between, eviction throughout.
        /// Everything observable, and how many glyphs went to the workers.
        /// </summary>
        private static List<string> Script(bool deferBake, out long onWorkers, int frames = 8,
            Action<int, FontData> midFrame = null)
        {
            using var latin = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            using var arabic = FontData.Load(Bytes(Fonts + "NotoSansArabic.ttf"));
            using var variable = FontData.Load(Bytes(Fonts + "NotoSansVariable.ttf"));
            using var cff = FontData.Load(Bytes(Fonts + "CffShapes.otf"));
            var lines = new[]
            {
                (latin, "Wave fill AVATAR 12345 office"),
                (arabic, "بِسْمِ ٱللَّهِ ٱلرَّحْمَٰنِ ٱلرَّحِيمِ"),
                (cff, "ABCDEFabcdef0123"),
                (latin, "The quick brown fox jumps over the lazy dog"),
                (variable, "AaBbQq&@%#WwMm8$ fi ŒœÆæ"),
                (latin, "ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞß"),
                (arabic, "مرحبا بالعالم"),
            };
            var log = new List<string>();
            AtlasDiagnostics.Reset();
            AtlasDiagnostics.Enabled = true;
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });
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
                    float size = (frame / 2 + l) % 3 == 0 ? 64f : (frame / 2 + l) % 3 == 1 ? 40f : 28f;
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
                    if (l == 3)
                    {
                        var g = atlas.GetOrAdd(latin, shaped[0].GlyphId, 48f);
                        log.Add($"g {g.HasPixels} {g.UvRect} v{atlas.Version}");
                    }
                    if (l == 4) midFrame?.Invoke(frame, variable);
                }
                if (frame == 5) atlas.Compact();
                atlas.Flush();
                log.Add($"flush f{frame} v{atlas.Version} texels {Texels(atlas)}");
            }
            var stats = atlas.GetStats();
            log.Add($"tiles {stats.TileCount} used {stats.UsedPixels} evictions {stats.Evictions} " +
                $"compactions {stats.Compactions} drops {stats.Drops} shelves {stats.ShelfCount}");
            LogAssert.ignoreFailingMessages = false;
            onWorkers = AtlasDiagnostics.WorkerOutlineCount;
            AtlasDiagnostics.Enabled = false;
            OutlineJobs.ForgetStats();
            return log;
        }

        private static void AssertPressure(List<string> log)
        {
            Assert.Greater(log.Count, 100);
            StringAssert.DoesNotContain("evictions 0 ", log[log.Count - 1], "the script needs the atlas to evict");
            StringAssert.DoesNotContain("compactions 0 ", log[log.Count - 1], "the script needs the atlas to compact");
        }

        private static void AssertSame(List<string> expected, List<string> actual, string what)
        {
            Assert.AreEqual(expected.Count, actual.Count, what + ": a different number of lines");
            for (int i = 0; i < expected.Count; i++)
                if (expected[i] != actual[i])
                    Assert.Fail($"{what}, line {i}:\n  expected {expected[i]}\n  actual   {actual[i]}");
        }

        [TestCase(1)]
        [TestCase(700)]
        [TestCase(8192)]
        [TestCase(1 << 30)]
        public void Worker_Outlines_Bake_What_The_Main_Thread_Baked(int batchTexels)
        {
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.BackgroundBakeTexels = batchTexels;
            GlyphAtlas.WorkerOutlines = false;
            var main = Script(deferBake: true, out long none);
            GlyphAtlas.WorkerOutlines = true;
            var workers = Script(deferBake: true, out long drawn);
            AssertPressure(main);
            Assert.AreEqual(0, none);
            Assert.Greater(drawn, 200, "too few glyphs went to the workers to mean anything");
            AssertSame(main, workers, "worker outlines");

            // And against the immediate bake, the reference for all of it.
            AssertSame(Script(deferBake: false, out _), workers, "against the immediate bake");
        }

        /// <summary>
        /// Batch boundaries anywhere, several batches in flight or one at a
        /// time, dispatched eagerly or at the threshold: the same tiles.
        /// </summary>
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Batches_Side_By_Side_Write_What_One_At_A_Time_Did(bool pipelined, bool eager)
        {
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.WorkerOutlines = true;
            var reference = Script(deferBake: false, out _);
            foreach (int texels in new[] { 1, 333, 5000 })
            {
                GlyphRasterizer.PipelinedBatches = pipelined;
                GlyphAtlas.EagerDispatch = eager;
                GlyphAtlas.BackgroundBakeTexels = texels;
                AssertSame(reference, Script(deferBake: true, out _), $"pipelined {pipelined} eager {eager} texels {texels}");
            }
        }

        /// <summary>
        /// Pipelining switched off and on again between frames, after batches
        /// that grew their buffers: the buffers a pipelined batch leaves
        /// behind are let go without touching anything still in use, and the
        /// tiles are the same throughout. (The golden A/B found the first
        /// version releasing a set the rasterizer was still pointing at.)
        /// </summary>
        [Test]
        public void Pipelining_Switched_Off_And_On_Lets_Its_Buffers_Go_Cleanly()
        {
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.WorkerOutlines = true;
            var reference = Script(deferBake: false, out _);
            foreach (var (pipelined, texels) in new[] { (true, 1 << 30), (false, 1), (true, 1), (false, 1 << 30), (true, 2048) })
            {
                GlyphRasterizer.PipelinedBatches = pipelined;
                GlyphAtlas.BackgroundBakeTexels = texels;
                AssertSame(reference, Script(deferBake: true, out _), $"pipelined {pipelined} texels {texels}");
            }
            GlyphRasterizer.ReleaseBuffers();
            GlyphRasterizer.PipelinedBatches = true;
            AssertSame(reference, Script(deferBake: true, out _), "after releasing every buffer");

            // Labels only ever defer, so nothing switches the rasterizer back
            // to its own buffers between frames: a batch that grew its set is
            // still the one in use when pipelining goes off.
            string Frames(bool deferBake)
            {
                using var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
                var log = new StringBuilder();
                // Small batches, each into a set; then one batch big enough to
                // grow the set it lands in; then pipelining off.
                var texts = new[] { "Small ones", "Grow the buffers of one batch, then let them go", "Then bake again" };
                var sizes = new[] { 30f, 96f, 40f };
                for (int i = 0; i < texts.Length; i++)
                {
                    GlyphRasterizer.PipelinedBatches = i != 2;
                    GlyphAtlas.BackgroundBakeTexels = i == 1 ? 1 << 30 : 1;
                    var shaped = new List<ShapedGlyph>();
                    using (var shaper = new Shaper()) shaper.Shape(font, texts[i], shaped);
                    var clusters = new List<GlyphClusters.Cluster>();
                    var positioned = new List<PositionedGlyph>();
                    GlyphClusters.Split(font, shaped, 0, shaped.Count, clusters, positioned,
                        1000f * font.UnitsPerEm / sizes[i], GlyphClusters.DefaultMergeGapUnits(font));
                    atlas.PrepareClusters(font, sizes[i], positioned, clusters, null, deferBake);
                    atlas.Flush();
                    log.Append(Texels(atlas));
                }
                return log.ToString();
            }
            GlyphRasterizer.ReleaseBuffers();
            Assert.AreEqual(Frames(false), Frames(true));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void The_Remembered_Walk_Finds_The_Slots_The_Full_Walk_Found(bool deferBake)
        {
            GlyphAtlas.FitMemo = false;
            var walk = Script(deferBake, out _, frames: 10);
            GlyphAtlas.FitMemo = true;
            var remembered = Script(deferBake, out _, frames: 10);
            AssertPressure(walk);
            AssertSame(walk, remembered, "remembered walk");
        }

        [Test]
        public void Layer_Pointers_Once_Write_The_Same_Texels()
        {
            GlyphAtlas.LayerPointersOnce = false;
            var each = Script(deferBake: true, out _);
            GlyphAtlas.LayerPointersOnce = true;
            AssertSame(each, Script(deferBake: true, out _), "layer pointers once");
        }

        /// <summary>
        /// A font whose axes change after glyphs were placed with it and before
        /// they were drawn: they are drawn with the axes they were placed
        /// with, exactly as the main thread drew them when it placed them.
        /// </summary>
        [Test]
        public void Axes_Changing_Before_The_Flush_Do_Not_Reach_Glyphs_Already_Placed()
        {
            void Twist(int frame, FontData font) =>
                font.SetVariations(new FontVariation("wght", 200f + 90f * frame));
            GlyphAtlas.DeferredBake = true;
            GlyphAtlas.BackgroundBake = true;
            GlyphAtlas.BackgroundBakeTexels = 1 << 30; // everything waits for the flush
            GlyphAtlas.WorkerOutlines = false;
            var main = Script(deferBake: true, out _, midFrame: Twist);
            GlyphAtlas.WorkerOutlines = true;
            var workers = Script(deferBake: true, out long drawn, midFrame: Twist);
            Assert.Greater(drawn, 100);
            AssertSame(main, workers, "axes changed mid-frame");
        }

        /// <summary>
        /// A font disposed while glyphs placed with it are still to be drawn:
        /// they are drawn first (nothing reads a freed face), with what the
        /// main thread would have drawn.
        /// </summary>
        [Test]
        public void A_Font_Disposed_Before_The_Flush_Is_Drawn_From_First()
        {
            string Run(bool workers)
            {
                GlyphAtlas.WorkerOutlines = workers;
                GlyphAtlas.DeferredBake = true;
                GlyphAtlas.BackgroundBake = true;
                GlyphAtlas.BackgroundBakeTexels = 1 << 30;
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
                var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
                var shaped = new List<ShapedGlyph>();
                using (var shaper = new Shaper()) shaper.Shape(font, "Disposed fonts draw first", shaped);
                var clusters = new List<GlyphClusters.Cluster>();
                var positioned = new List<PositionedGlyph>();
                GlyphClusters.Split(font, shaped, 0, shaped.Count, clusters, positioned,
                    1000f * font.UnitsPerEm / 40f, GlyphClusters.DefaultMergeGapUnits(font));
                // The first pass teaches the statistics; the second is drawn on the workers.
                atlas.PrepareClusters(font, 40f, positioned, clusters, null, deferBake: true);
                atlas.Flush();
                atlas.Forget(font);
                atlas.Flush();
                AtlasDiagnostics.Reset();
                AtlasDiagnostics.Enabled = true;
                atlas.PrepareClusters(font, 40f, positioned, clusters, null, deferBake: true);
                long drawn = AtlasDiagnostics.WorkerOutlineCount;
                AtlasDiagnostics.Enabled = false;
                Assert.AreEqual(workers, drawn > 10, "the second pass did not go the way it was meant to");
                font.Dispose();
                atlas.Flush();
                return Texels(atlas);
            }

            Assert.AreEqual(Run(false), Run(true));
        }

        /// <summary>
        /// A glyph whose outline does not come to the statistics its tile was
        /// placed from (it cannot happen unless a statistic is wrong): the
        /// batch is drawn again on the main thread, reported once, and the
        /// tiles are what the main thread would have baked.
        /// </summary>
        [Test]
        public void A_Glyph_That_Does_Not_Match_Its_Statistics_Is_Redrawn_On_The_Main_Thread()
        {
            string Run(bool corrupt)
            {
                GlyphAtlas.WorkerOutlines = true;
                GlyphAtlas.DeferredBake = true;
                GlyphAtlas.BackgroundBake = true;
                GlyphAtlas.BackgroundBakeTexels = 1 << 30;
                using var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
                var shaped = new List<ShapedGlyph>();
                using (var shaper = new Shaper()) shaper.Shape(font, "Statistics are checked", shaped);
                var clusters = new List<GlyphClusters.Cluster>();
                var positioned = new List<PositionedGlyph>();
                GlyphClusters.Split(font, shaped, 0, shaped.Count, clusters, positioned,
                    1000f * font.UnitsPerEm / 40f, GlyphClusters.DefaultMergeGapUnits(font));
                atlas.PrepareClusters(font, 40f, positioned, clusters, null, deferBake: true);
                atlas.Flush();
                var log = new StringBuilder();
                atlas.Forget(font);
                atlas.Flush();
                if (corrupt)
                {
                    GlyphRasterizer.ReportedOutlineMismatch = false;
                    Assert.IsTrue(OutlineJobs.CorruptStatsForTests(font, shaped[1].GlyphId, 40));
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("did not match the outline"));
                }
                AtlasDiagnostics.Reset();
                AtlasDiagnostics.Enabled = true;
                atlas.PrepareClusters(font, 40f, positioned, clusters, null, deferBake: true);
                long drawn = AtlasDiagnostics.WorkerOutlineCount;
                AtlasDiagnostics.Enabled = false;
                Assert.Greater(drawn, 10);
                atlas.Flush();
                foreach (var cluster in clusters)
                {
                    var at = atlas.GetOrAddCluster(font, 40f, positioned, cluster.Start, cluster.Count, cluster.Hash);
                    log.Append(at.UvRect).Append(at.OriginUnits).Append(at.SizeUnits).Append(';');
                }
                return log + Texels(atlas);
            }

            string clean = Run(false);
            OutlineJobs.ForgetStats();
            Assert.AreEqual(clean, Run(true));
        }
    }
}
