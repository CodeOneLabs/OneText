using System.Collections;
using System.Text;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// The atlas under pressure, over time, with labels still drawing out of
    /// it.
    ///
    /// The EditMode fixture fills an atlas and reads its statistics; that
    /// proves the packer evicts. It cannot prove the half that matters to a
    /// running game: that a label whose tile was evicted three frames ago
    /// notices, rebuilds against the new uv rectangles and keeps drawing. An
    /// atlas that evicts correctly and a mesh that never hears about it is
    /// text that turns into somebody else's glyphs partway through a
    /// conversation, and it only happens after enough distinct characters have
    /// gone through to wrap the atlas round — which is to say, after a few
    /// minutes of play, and never in a single-frame test.
    /// </summary>
    public class RuntimeAtlasPressureTests
    {
        private readonly PlayHarness _harness = new PlayHarness();

        [SetUp]
        public void Setup() => _harness.Setup();

        [TearDown]
        public void Teardown() => _harness.Teardown();

        /// <summary>
        /// The face the churn is drawn through. Han is the pressure test that
        /// matters — tens of thousands of glyphs, each a large tile, and no
        /// project ever prewarms all of them — but the CJK face is fetched
        /// rather than committed. Without it the sweep runs through Latin
        /// Extended, Greek and Cyrillic, which the committed face does cover:
        /// fewer distinct tiles, same shape of test.
        /// </summary>
        private static string ChurnFont =>
            PlayHarness.HasFont(PlayHarness.JapaneseFont)
                ? PlayHarness.JapaneseFont
                : PlayHarness.LatinFont;

        private static bool UsingHan => ChurnFont == PlayHarness.JapaneseFont;

        /// <summary>Sixteen characters no earlier frame asked for.</summary>
        private static string ChurnLine(int seed)
        {
            var builder = new StringBuilder(16);
            for (int i = 0; i < 16; i++)
            {
                int index = seed * 16 + i;
                builder.Append(UsingHan
                    ? (char)(0x4E00 + index % 0x4000)
                    // U+0100..U+04FF: Latin Extended-A and -B, Greek and
                    // Cyrillic, minus the unassigned hole at U+0378.
                    : (char)(0x0100 + index % 0x0400));
            }
            return builder.ToString();
        }

        [UnityTest]
        public IEnumerator Hundreds_Of_Distinct_Glyphs_Across_Frames_Keep_Drawing()
        {
            var label = _harness.Label(ChurnLine(0), 28f, ChurnFont, new Vector2(700f, 120f));
            yield return PlayHarness.Frames(2);

            var atlas = SharedGlyphAtlas.Atlas;
            int startTiles = atlas.GetStats().TileCount;

            // 150 frames x 16 characters: a couple of thousand distinct tiles
            // against a default atlas that holds a few thousand, so the packer
            // is working near its limit for most of the run.
            for (int frame = 1; frame <= 150; frame++)
            {
                label.Text = ChurnLine(frame);
                yield return PlayHarness.Frame();

                Assert.Greater(PlayHarness.DrawnQuads(label), 0,
                    $"the label stopped drawing on frame {frame}");
            }

            var stats = SharedGlyphAtlas.Atlas.GetStats();
            Assert.Greater(stats.TileCount, startTiles,
                "nothing was ever added to the atlas");
            Assert.LessOrEqual(stats.UsedPixels, stats.CapacityPixels,
                "the atlas reported using more than it has");

            // And it is still a working atlas afterwards, not a wedged one.
            label.Text = "Back to Latin.";
            yield return PlayHarness.Frames(2);
            Assert.Greater(PlayHarness.DrawnQuads(label), 0,
                "the label could not draw plain Latin after the pressure test");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Many_Labels_Sharing_One_Atlas_All_Keep_Their_Geometry()
        {
            // The eviction hazard is specifically about labels that are NOT
            // being touched: the one that changes every frame rebuilds anyway,
            // and would hide the bug. These eight are set once and then left
            // alone while the atlas churns underneath them.
            var quiet = new OneTextLabel[8];
            var quads = new int[quiet.Length];
            for (int i = 0; i < quiet.Length; i++)
            {
                quiet[i] = _harness.Label($"Quiet label number {i}", 22f,
                    PlayHarness.LatinFont, new Vector2(700f, 40f));
                quiet[i].rectTransform.anchoredPosition = new Vector2(0f, -40f - i * 30f);
            }

            var churner = _harness.Label(ChurnLine(0), 26f, ChurnFont, new Vector2(700f, 120f));
            yield return PlayHarness.Frames(3);

            for (int i = 0; i < quiet.Length; i++)
            {
                quads[i] = PlayHarness.DrawnQuads(quiet[i]);
                Assert.Greater(quads[i], 0, $"quiet label {i} never drew");
            }

            for (int frame = 1; frame <= 120; frame++)
            {
                churner.Text = ChurnLine(frame);
                yield return PlayHarness.Frame();
            }

            for (int i = 0; i < quiet.Length; i++)
                Assert.AreEqual(quads[i], PlayHarness.DrawnQuads(quiet[i]),
                    $"quiet label {i} lost geometry while the atlas churned around it");

            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Font_Sizes_Sweeping_Across_Frames_Do_Not_Wedge_The_Atlas()
        {
            // Each size bucket is a separate set of tiles for the same glyphs,
            // which is the other way a project fills an atlas without meaning
            // to: one animated "score up" tween scaling text through forty
            // sizes.
            var label = _harness.Label("Size sweep 0123456789", 12f, PlayHarness.LatinFont,
                new Vector2(760f, 300f));
            yield return PlayHarness.Frames(2);

            for (int frame = 0; frame < 80; frame++)
            {
                label.FontSize = 10f + frame * 1.5f;
                yield return PlayHarness.Frame();
                Assert.Greater(PlayHarness.DrawnQuads(label), 0,
                    $"no geometry at size {label.FontSize}");
            }

            PlayHarness.ExpectNoErrors();
        }
    }
}
