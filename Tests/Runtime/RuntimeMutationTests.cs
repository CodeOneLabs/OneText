using System;
using System.Collections;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// Text that changes every frame, and a font that changes underneath it.
    ///
    /// This is the shape of the workload a real game gives a text engine — a
    /// score, a timer, a subtitle, a damage number — and it is the workload no
    /// EditMode test produces, because in the editor nothing runs a hundred
    /// frames in a row. The failures live in the caching: a layout cache keyed
    /// on something that does not actually identify the layout, a font swap
    /// that leaves the old face's glyph ids in a rebuilt mesh, a per-frame
    /// allocation that is invisible at one frame and is the frame budget at
    /// sixty.
    /// </summary>
    public class RuntimeMutationTests
    {
        private readonly PlayHarness _harness = new PlayHarness();

        /// <summary>
        /// Deliberately across scripts. A cache that is right for ASCII and
        /// wrong for a shaped script fails here and passes everywhere else.
        /// </summary>
        private static readonly string[] Phrases =
        {
            "Score: {0}",
            "الوقت {0}",
            "スコア {0}",
            "점수 {0}",
            "अंक {0}",
            "Score {0} — combo x{0}",
        };

        [SetUp]
        public void Setup() => _harness.Setup();

        [TearDown]
        public void Teardown() => _harness.Teardown();

        [UnityTest]
        public IEnumerator Text_Changed_Every_Frame_Keeps_Producing_Geometry()
        {
            var label = _harness.Label("start", 30f, PlayHarness.LatinFont);
            yield return PlayHarness.Frame();

            int previousLayouts = label.LayoutRuns;
            for (int frame = 0; frame < 100; frame++)
            {
                label.Text = string.Format(Phrases[frame % Phrases.Length], frame);
                yield return PlayHarness.Frame();

                Assert.Greater(PlayHarness.DrawnQuads(label), 0,
                    $"frame {frame} ('{label.Text}') produced no geometry");
                Assert.Greater(label.LayoutRuns, previousLayouts,
                    $"frame {frame} changed the text without re-laying it out");
                previousLayouts = label.LayoutRuns;
            }

            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator An_Untouched_Label_Costs_Nothing_Per_Frame()
        {
            // The other half of the per-frame story, and the one no EditMode
            // test can ask: most labels on screen are not changing, and a
            // label that re-lays itself out anyway is a per-frame cost
            // multiplied by every line of UI in the game. Sixty idle frames
            // must do no layout and build no quads.
            var label = _harness.Label("Score: 1200", 30f);
            yield return PlayHarness.Frames(3);

            int layouts = label.LayoutRuns;
            int quadBuilds = label.QuadBuilds;
            Assert.Greater(layouts, 0, "the label never laid out at all");

            yield return PlayHarness.Frames(60);

            Assert.AreEqual(layouts, label.LayoutRuns,
                "an untouched label re-laid its paragraph out while nothing changed");
            Assert.AreEqual(quadBuilds, label.QuadBuilds,
                "an untouched label rebuilt its quads while nothing changed");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Hundred_Mutating_Frames_Do_Not_Run_Managed_Memory_Away()
        {
            // Not an allocation assertion — that one lives in the EditMode
            // suite, where a single call can be measured exactly. This is the
            // coarser question a profiler answers: after a hundred frames of
            // real text churn, is the managed heap roughly where it started, or
            // is something accumulating per frame? The bound is deliberately
            // generous; anything that fails it is a leak and not noise.
            var label = _harness.Label("warmup", 30f);
            yield return PlayHarness.Frames(5);

            // Churn once before measuring, so the strings, the shaper's buffers
            // and the atlas tiles this test needs are all already paid for.
            for (int frame = 0; frame < 20; frame++)
            {
                label.Text = string.Format(Phrases[frame % Phrases.Length], frame);
                yield return PlayHarness.Frame();
            }

            GC.Collect();
            yield return PlayHarness.Frames(2);
            long before = GC.GetTotalMemory(true);

            for (int frame = 0; frame < 100; frame++)
            {
                label.Text = string.Format(Phrases[frame % Phrases.Length], frame + 1000);
                yield return PlayHarness.Frame();
            }

            GC.Collect();
            yield return PlayHarness.Frames(2);
            long after = GC.GetTotalMemory(true);
            long growth = after - before;

            Assert.Less(growth, 4L * 1024 * 1024,
                $"managed heap grew {growth / 1024} KB over 100 mutating frames, which is a leak " +
                "rather than the churn this test tolerates");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Swapping_The_Font_On_A_Live_Label_Rebuilds_It()
        {
            // Arabic through a Latin face is a row of .notdef boxes; the same
            // string through the Arabic face is joined script. If the swap did
            // not take, the geometry does not change, which is the assertion.
            const string arabic = "مرحبا بالعالم";
            var label = _harness.Label(arabic, 36f, PlayHarness.LatinFont);
            yield return PlayHarness.Frames(2);

            int latinQuads = PlayHarness.DrawnQuads(label);
            float latinWidth = label.EnsureLayout().Width;
            Assert.Greater(latinQuads, 0);

            label.SetFont(PlayHarness.Font(PlayHarness.ArabicFont));
            yield return PlayHarness.Frames(2);

            Assert.Greater(PlayHarness.DrawnQuads(label), 0, "the label went blank after the swap");
            Assert.AreNotEqual(latinWidth, label.EnsureLayout().Width,
                "the same string measured identically through a different face: the swap did nothing");

            // And back, several times, on a live label: the swap has to be
            // repeatable, not a one-off that works because the first face was
            // still the only one ever loaded.
            for (int i = 0; i < 5; i++)
            {
                label.SetFont(PlayHarness.Font(
                    i % 2 == 0 ? PlayHarness.LatinFont : PlayHarness.ArabicFont));
                yield return PlayHarness.Frame();
                Assert.Greater(PlayHarness.DrawnQuads(label), 0, $"blank after swap {i}");
            }

            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Variable_Axes_Set_On_A_Live_Label_Change_The_Measure()
        {
            var label = _harness.Label("Weight", 40f, PlayHarness.VariableFont);
            yield return PlayHarness.Frames(2);

            label.SetVariations(new FontVariation("wght", 200f));
            yield return PlayHarness.Frames(2);
            float light = label.EnsureLayout().Width;

            label.SetVariations(new FontVariation("wght", 900f));
            yield return PlayHarness.Frames(2);
            float heavy = label.EnsureLayout().Width;

            Assert.Greater(PlayHarness.DrawnQuads(label), 0);
            Assert.Greater(heavy, light, "the heavy instance measured no wider than the light one");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Resizing_The_Rect_Every_Frame_Rewraps_Without_Complaint()
        {
            var label = _harness.Label(
                "A paragraph long enough that the number of lines depends on how wide the box is.",
                26f, PlayHarness.LatinFont, new Vector2(500f, 300f));
            yield return PlayHarness.Frames(2);

            int narrowest = 0, widest = int.MaxValue;
            for (int frame = 0; frame < 60; frame++)
            {
                float width = 160f + frame * 6f;
                label.rectTransform.sizeDelta = new Vector2(width, 300f);
                yield return PlayHarness.Frame();

                int lines = label.EnsureLayout().Lines.Count;
                Assert.Greater(lines, 0, $"no lines at width {width}");
                Assert.Greater(PlayHarness.DrawnQuads(label), 0, $"no geometry at width {width}");
                narrowest = Mathf.Max(narrowest, lines);
                widest = Mathf.Min(widest, lines);
            }

            Assert.Greater(narrowest, widest, "the line count never moved as the box grew");
            PlayHarness.ExpectNoErrors();
        }
    }
}
