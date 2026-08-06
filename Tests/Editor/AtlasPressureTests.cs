using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// One frame asking for more tiles than the atlas holds.
    ///
    /// This is the failure the field has already shipped fixes for ("rasterizing
    /// many glyphs in one frame could permanently drop some of them"), and it is
    /// nasty precisely because it is quiet: the frame that overflows recovers, but
    /// a glyph that lost the race stays blank for the rest of the session. The
    /// eviction guard that spares tiles touched since the last flush is what makes
    /// an over-full frame possible in the first place, so the interesting question
    /// is not "does the atlas survive" but "does every glyph come back afterwards".
    /// </summary>
    public class AtlasPressureTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

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

        // Every printable Latin-1 letter, plus Latin Extended-A: far more 96ppem
        // tiles than a single 256x256 layer can hold at once.
        private static string Overflowing()
        {
            var text = new System.Text.StringBuilder();
            for (int cp = 'A'; cp <= 'Z'; cp++) text.Append((char)cp);
            for (int cp = 'a'; cp <= 'z'; cp++) text.Append((char)cp);
            for (int cp = 0x00C0; cp <= 0x017F; cp++) text.Append((char)cp);
            return text.ToString();
        }

        [Test]
        public void OverflowingFrame_LosesNoGlyphPermanently()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });
            var glyphs = GlyphsOf(font, Overflowing());

            // Two frames, each asking for everything, with no Flush inside a
            // frame: within a frame every tile is "used since the last flush",
            // which is exactly the state the eviction guard has to resolve
            // without either deadlocking or blacklisting a glyph.
            LogAssert.ignoreFailingMessages = true; // the budget error is the point, not the failure
            for (int frame = 0; frame < 2; frame++)
            {
                foreach (uint gid in glyphs) atlas.GetOrAdd(font, gid, 96f);
                atlas.Flush();
            }
            LogAssert.ignoreFailingMessages = false;

            var pressured = atlas.GetStats();
            Debug.Log($"[atlas] pressure: {pressured.TileCount} tiles, {pressured.Evictions} evictions, " +
                $"{pressured.Drops} drops over {glyphs.Count} glyphs x 2 frames");
            Assert.Greater(pressured.Evictions, 0, "the test needs the atlas to actually overflow");

            // The recovery frame: a working set that comfortably fits. If the
            // overflow above cached any failure, these come back blank forever.
            var working = glyphs.GetRange(0, 8);
            foreach (uint gid in working) atlas.GetOrAdd(font, gid, 96f);
            atlas.Flush();

            foreach (uint gid in working)
            {
                var location = atlas.GetOrAdd(font, gid, 96f);
                Assert.IsTrue(location.HasPixels,
                    $"glyph {gid} never came back after the atlas was crowded: a dropped tile was " +
                    "cached as a permanent failure");
                Assert.Greater(location.SizeUnits.x, 0f);
                Assert.Greater(location.SizeUnits.y, 0f);
            }
        }

        [Test]
        public void TileTooLargeForTheAtlas_IsRetried_NotBlacklisted()
        {
            using var font = LoadFont(LatinFontPath);
            using var small = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });

            // A cluster of six glyphs laid out side by side at 256ppem is wider
            // than a 256px layer, so no amount of eviction can make room. That is
            // the one allocation failure the atlas cannot evict its way out of,
            // and the one where caching the failure is permanent by construction.
            var ids = GlyphsOf(font, "MMMMMM");
            var positioned = new List<PositionedGlyph>();
            int advance = (int)font.UnitsPerEm;
            for (int i = 0; i < ids.Count; i++)
                positioned.Add(new PositionedGlyph(ids[i], i * advance, 0));

            const long hash = 0x5EEDL;
            LogAssert.ignoreFailingMessages = true;
            var first = small.GetOrAddCluster(font, 256f, positioned, 0, positioned.Count, hash);
            LogAssert.ignoreFailingMessages = false;

            Assert.IsFalse(first.HasPixels, "test needs a cluster too wide for the layer");
            Assert.IsFalse(small.Contains(font, hash, 256f),
                "a tile that did not fit must not sit in the cache as a blank entry; it would " +
                "be served as 'blank' forever, including after the budget is raised");
            Assert.AreEqual(1, small.GetStats().Drops, "an overflowing tile has to be counted somewhere");

            // The same cluster at a size that does fit must still rasterize: the
            // failure above is about this frame's budget, not about this content.
            var smaller = small.GetOrAddCluster(font, 24f, positioned, 0, positioned.Count, hash);
            Assert.IsTrue(smaller.HasPixels, "a failed allocation poisoned the content at other sizes");
        }

        [Test]
        public void MixedSizeOverflow_Terminates_AndEveryGlyphStillResolves()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            var glyphs = GlyphsOf(font, Overflowing());

            // Mixed sizes mean shelves of several height buckets, which is the
            // case where evicting by age frees space the requesting tile cannot
            // use: a freed 40px tile does nothing for a 90px one. An eviction
            // loop that cannot tell the difference either spins or gives up, and
            // both show up here: the first as a hang, the second as a drop.
            LogAssert.ignoreFailingMessages = true;
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (uint gid in glyphs)
                {
                    atlas.GetOrAdd(font, gid, 24f);
                    atlas.GetOrAdd(font, gid, 96f);
                }
                atlas.Flush();
            }
            LogAssert.ignoreFailingMessages = false;

            var stats = atlas.GetStats();
            Debug.Log($"[atlas] mixed-size pressure: {stats.TileCount} tiles, {stats.UsedFraction:P0} full, " +
                $"{stats.Evictions} evictions, {stats.Compactions} compactions, {stats.Drops} drops");
            Assert.Greater(stats.Evictions, 0, "the test needs the atlas to actually overflow");
            Assert.AreEqual(0, stats.Drops,
                "eviction gave up on a tile it should have been able to make room for");

            // Whatever the thrashing did to the packing, the atlas still has to
            // serve. Every glyph, at both sizes, one at a time.
            foreach (uint gid in glyphs)
            {
                Assert.IsTrue(atlas.GetOrAdd(font, gid, 24f).HasPixels, $"glyph {gid} at 24ppem is blank");
                Assert.IsTrue(atlas.GetOrAdd(font, gid, 96f).HasPixels, $"glyph {gid} at 96ppem is blank");
                atlas.Flush();
            }
        }
    }
}
