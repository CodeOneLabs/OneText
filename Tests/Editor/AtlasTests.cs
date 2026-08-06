using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// The atlas under pressure: what happens when more glyphs are asked for
    /// than fit. Before M6 the answer was "throw away a whole layer"; these
    /// tests pin down the per-tile behaviour that replaced it.
    /// </summary>
    public class AtlasTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static readonly string Sample =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "áàâäãåçéèêëíìîïñóòôöõúùûüýÿÁÀÂÄÃÅÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜÝ" +
            "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~¡¢£¤¥¦§¨©ª«¬®¯°±²³´µ¶·¸¹º»¼½¾¿";

        private static List<uint> GlyphsOf(FontData font, string text)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            var ids = new List<uint>();
            foreach (char c in text)
            {
                shaped.Clear();
                shaper.Shape(font, c.ToString(), shaped);
                foreach (var glyph in shaped) ids.Add(glyph.GlyphId);
            }
            return ids;
        }

        [Test]
        public void ChangingFlatness_RebakesInsteadOfMixingTolerances()
        {
            // A tile flattened at one tolerance and a tile flattened at another
            // are different pictures of the same glyph. Serving one where the
            // other is expected is the inconsistency the ppem buckets exist to
            // prevent, so the tolerance has to be part of the cache key.
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(
                new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var ids = GlyphsOf(font, "ABC");

            float previous = OutlineExtractor.FlatnessPixels;
            try
            {
                foreach (uint id in ids) atlas.GetOrAdd(font, id, 32f);
                int baked = atlas.GetStats().TileCount;
                Assert.AreEqual(ids.Count, baked);

                foreach (uint id in ids) atlas.GetOrAdd(font, id, 32f);
                Assert.AreEqual(baked, atlas.GetStats().TileCount, "same tolerance must hit the cache");

                OutlineExtractor.FlatnessPixels = 0.4f;
                foreach (uint id in ids) atlas.GetOrAdd(font, id, 32f);
                Assert.AreEqual(baked * 2, atlas.GetStats().TileCount,
                    "a coarser tolerance must bake new tiles, not reuse the old ones");
            }
            finally
            {
                OutlineExtractor.FlatnessPixels = previous;
            }
        }

        [Test]
        public void Settings_AreHonoured_AndValidated()
        {
            var wanted = new GlyphAtlasSettings { TextureSize = 300, LayerCount = 2 }.Validated();
            Assert.AreEqual(512, wanted.TextureSize, "sizes round up to a power of two");
            Assert.AreEqual(2, wanted.LayerCount);
            Assert.AreEqual(512L * 512 * 2, wanted.MemoryBytes);

            using var atlas = new GlyphAtlas(wanted);
            Assert.AreEqual(512, atlas.Texture.width);
            Assert.AreEqual(2, atlas.Texture.depth);
            Assert.AreEqual(wanted, atlas.Settings);

            var clamped = new GlyphAtlasSettings { TextureSize = 99999, LayerCount = 99 }.Validated();
            Assert.AreEqual(4096, clamped.TextureSize);
            Assert.AreEqual(16, clamped.LayerCount);
        }

        [Test]
        public void Overflow_EvictsTiles_NotLayers()
        {
            using var font = LoadFont(LatinFontPath);
            // One layer: 512x512 holds a few dozen 96ppem tiles, and this
            // sample asks for several times that.
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });

            // Letters only: one shape class, so the tile count is about
            // eviction behaviour and not about the mix of glyph sizes.
            var glyphs = GlyphsOf(font, Sample.Substring(0, 52));
            int peak = 0;
            foreach (uint gid in glyphs)
            {
                atlas.GetOrAdd(font, gid, 96f);
                atlas.Flush(); // ends the "in use this frame" window, as a canvas pass would
                peak = Mathf.Max(peak, atlas.GetStats().TileCount);
            }

            var stats = atlas.GetStats();
            Debug.Log($"[atlas] overflow: {stats.TileCount} tiles (peak {peak}), {stats.UsedFraction:P0} full, " +
                $"{stats.Evictions} evictions, {stats.Compactions} compactions, {stats.ShelfCount} shelves");
            Assert.Greater(stats.Evictions, 0, "the atlas should have run out of room");
            // Whole-layer recycling would leave the single layer nearly empty
            // after each overflow; per-tile eviction keeps it full.
            Assert.Greater(stats.TileCount, peak * 0.8f,
                $"eviction dropped too much at once: {stats.TileCount} of a peak {peak}");
            Assert.Greater(stats.UsedFraction, 0.5f,
                $"atlas only {stats.UsedFraction:P0} full after eviction: space is being wasted");
        }

        [Test]
        public void Eviction_KeepsRecentlyUsedGlyphs()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });

            var glyphs = GlyphsOf(font, Sample);
            uint hot = glyphs[0];
            atlas.GetOrAdd(font, hot, 96f);

            foreach (uint gid in glyphs)
            {
                atlas.GetOrAdd(font, gid, 96f);
                atlas.GetOrAdd(font, hot, 96f); // keep touching the hot glyph
                atlas.Flush();
            }

            Assert.Greater(atlas.GetStats().Evictions, 0, "test needs the atlas to overflow");
            Assert.IsTrue(atlas.Contains(font, hot, 96f),
                "the most recently used glyph was evicted: LRU order is wrong");
        }

        [Test]
        public void FreedSpans_AreReused_WithoutGrowingTheAtlas()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var glyphs = GlyphsOf(font, Sample);

            foreach (uint gid in glyphs)
            {
                atlas.GetOrAdd(font, gid, 96f);
                atlas.Flush();
            }
            int shelvesAfterFirstPass = atlas.GetStats().ShelfCount;

            // A second pass over the same glyphs at the same size: every tile is
            // either resident or lands in a span an evicted tile gave back.
            foreach (uint gid in glyphs)
            {
                atlas.GetOrAdd(font, gid, 96f);
                atlas.Flush();
            }

            Assert.AreEqual(shelvesAfterFirstPass, atlas.GetStats().ShelfCount,
                "recycling tiles should not need new shelves");
        }

        [Test]
        public void Compaction_MovesTiles_AndKeepsTheirPixels()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var glyphs = GlyphsOf(font, Sample.Substring(0, 20));
            foreach (uint gid in glyphs) atlas.GetOrAdd(font, gid, 48f);

            var before = atlas.GetOrAdd(font, glyphs[0], 48f);
            var pixelsBefore = ReadTile(atlas, before);
            int version = atlas.Version;
            long usedBefore = atlas.GetStats().UsedPixels;

            atlas.Compact();

            var after = atlas.GetOrAdd(font, glyphs[0], 48f);
            Assert.Greater(atlas.Version, version, "a compaction must be visible to anything holding UVs");
            Assert.AreEqual(usedBefore, atlas.GetStats().UsedPixels, "compaction must not lose tiles");
            Assert.AreEqual(before.SizeUnits, after.SizeUnits);
            CollectionAssert.AreEqual(pixelsBefore, ReadTile(atlas, after),
                "the tile's pixels moved but did not survive the move");
        }

        [Test]
        public void Compaction_ReclaimsFragmentedSpace()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var glyphs = GlyphsOf(font, Sample);

            // Mixing sizes fragments the layer: shelves of one bucket cannot
            // take tiles of another, which is the case whole-layer recycling
            // could not handle at all.
            foreach (uint gid in glyphs)
            {
                atlas.GetOrAdd(font, gid, 24f);
                atlas.GetOrAdd(font, gid, 64f);
                atlas.Flush();
            }

            var stats = atlas.GetStats();
            Debug.Log($"[atlas] mixed sizes: {stats.TileCount} tiles, {stats.UsedFraction:P0} full, " +
                $"{stats.Evictions} evictions, {stats.Compactions} compactions, {stats.ShelfCount} shelves");
            Assert.Greater(stats.TileCount, 20, "mixed sizes should still leave a full atlas");
            Assert.Greater(stats.UsedFraction, 0.4f,
                $"fragmentation left the atlas at {stats.UsedFraction:P0} occupancy");
        }

        [Test]
        public void Prewarm_BakesTilesLabelsThenFind()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 1 });
            var stack = FontStack.Single(font);

            var codepoints = new List<int>();
            foreach (char c in "Hamburgefonstiv") codepoints.Add(c);
            var report = AtlasPrewarm.Warm(atlas, stack, codepoints, new[] { 36f });

            Assert.Greater(report.Baked, 0);
            Assert.IsFalse(report.StoppedAtBudget);
            int tilesAfterPrewarm = atlas.GetStats().TileCount;

            // Now draw the same text the way a label does, clustering and all.
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, "Hamburgefonstiv", shaped);
            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();
            int ppem = GlyphAtlas.QuantizePixelsPerEm(36f);
            GlyphClusters.Split(font, shaped, clusters, positioned,
                1000f * font.UnitsPerEm / ppem, GlyphClusters.DefaultMergeGapUnits(font));
            foreach (var cluster in clusters)
                atlas.GetOrAddCluster(font, 36f, positioned, cluster.Start, cluster.Count, cluster.Hash);

            Assert.AreEqual(tilesAfterPrewarm, atlas.GetStats().TileCount,
                "prewarmed tiles must land under the keys labels look up, or they are wasted");

            var second = AtlasPrewarm.Warm(atlas, stack, codepoints, new[] { 36f });
            Assert.AreEqual(0, second.Baked, "a second prewarm should find everything resident");
            Assert.Greater(second.AlreadyResident, 0);
        }

        [Test]
        public void Prewarm_StopsAtBudget_AndReportsWhatDidNotFit()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var stack = FontStack.Single(font);

            var codepoints = new List<int>();
            for (int cp = 0x21; cp <= 0x24F; cp++) codepoints.Add(cp);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("prewarm"));
            var report = AtlasPrewarm.Warm(atlas, stack, codepoints, new[] { 48f, 96f });

            Assert.IsTrue(report.StoppedAtBudget, "a 64 KB atlas cannot hold Latin at two large sizes");
            Assert.Greater(report.Skipped, 0, "what did not fit has to be reported, not silently dropped");
            Assert.Greater(report.Baked, 0);
            Assert.LessOrEqual(report.Fill, 1f);
        }

        [Test]
        public void Recorder_CapturesCharactersAndBuckets()
        {
            CharsetRecorder.Clear();
            CharsetRecorder.Enabled = false;
            CharsetRecorder.Record("ignored", 36f);
            Assert.AreEqual(0, CharsetRecorder.CodepointCount, "recording must be off until asked for");

            CharsetRecorder.Enabled = true;
            CharsetRecorder.Record("héllo wörld", 36f);
            CharsetRecorder.Record("héllo", 72f);
            CharsetRecorder.Enabled = false;

            Assert.AreEqual("dhlorwéö", CharsetRecorder.CharactersAsString(),
                "characters are deduplicated, sorted, and whitespace dropped");
            CollectionAssert.AreEqual(new List<float> { 32f, 64f }, CharsetRecorder.SizesSorted(),
                "sizes are recorded as density buckets, which is what the atlas keys on");
            CharsetRecorder.Clear();
        }

        [Test]
        public void Charset_ExpandsRangesAndCharacters()
        {
            var charset = ScriptableObject.CreateInstance<OneTextCharset>();
            charset.Characters = "AAB";
            charset.Ranges.Add(new CodepointRange("digits", '0', '9'));
            var codepoints = charset.Codepoints();

            Assert.AreEqual(12, codepoints.Count, "duplicates collapse; ranges expand");
            CollectionAssert.Contains(codepoints, (int)'A');
            CollectionAssert.Contains(codepoints, (int)'7');
            Object.DestroyImmediate(charset);
        }

        private static byte[] ReadTile(GlyphAtlas atlas, GlyphLocation location)
        {
            int size = atlas.Settings.TextureSize;
            int x = Mathf.RoundToInt(location.UvRect.x * size);
            int y = Mathf.RoundToInt(location.UvRect.y * size);
            int w = Mathf.RoundToInt(location.UvRect.width * size);
            int h = Mathf.RoundToInt(location.UvRect.height * size);
            var data = atlas.Texture.GetPixelData<byte>(0, location.Layer);
            var tile = new byte[w * h];
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                    tile[row * w + col] = data[(y + row) * size + x + col];
            return tile;
        }
    }
}
