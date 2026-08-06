using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Editor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Tier 1: every golden case renders the picture it rendered last time.
    ///
    /// The rest of this suite asserts numbers — cluster counts, run
    /// directions, break opportunities, caret rectangles — and numbers cannot
    /// see the failure this file exists for: the day the layout is still right
    /// and the picture stops being. A shader that drops its outline channel, an
    /// atlas that bleeds a neighbour's tile into a glyph's margin, a vertical
    /// column that drifts half an em, an emoji that comes back as four people
    /// in a row: each is invisible to a layout assertion and unmistakable in a
    /// diff of two PNGs.
    ///
    /// One test per case, so a failure names the picture that broke rather than
    /// stopping at the first one. Each writes its actual render and a diff
    /// heatmap next to the assert message when it fails.
    ///
    /// Approving a change is deliberately a separate, manual act:
    ///   Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///         OneText.Editor.GoldenRegen.RegenerateAll
    /// </summary>
    [Category("Golden")]
    public class GoldenImageTests
    {
        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (var golden in GoldenCases.All)
                yield return new TestCaseData(golden.Name)
                    .SetName($"Renders_{golden.Name}")
                    .SetDescription(golden.Description);
        }

        [Test]
        [TestCaseSource(nameof(Cases))]
        public void Renders_Its_Baseline(string name)
        {
            var golden = GoldenCases.Find(name);
            RequireTheRendererTheBaselinesCameFrom();

            string missing = golden.MissingFont();
            if (missing != null)
                Assert.Inconclusive(
                    $"'{name}' needs {missing}, which is fetched rather than committed. " +
                    "Run: python3 Tools/fetch_coverage_fonts.py");

            if (!File.Exists(golden.BaselinePath))
                Assert.Ignore(
                    $"No baseline for '{name}' at {golden.BaselinePath}. " +
                    "Create one with: Unity -batchmode -quit -executeMethod " +
                    "OneText.Editor.GoldenRegen.RegenerateAll");

            Texture2D actual = null;
            try
            {
                actual = golden.Render();
                var result = GoldenComparer.Compare(name, actual, golden.BaselinePath);
                // Recorded on the way past even when it passes: "green" and
                // "green with two pixels of headroom" are different facts about
                // a tolerance, and only one of them says whether the budget is
                // set anywhere near right.
                TestContext.WriteLine($"{name}: {result.Message}");
                Assert.IsTrue(result.Passed, result.Message);
            }
            finally
            {
                if (actual != null) Object.DestroyImmediate(actual);
            }
        }

        /// <summary>
        /// Skips unless this machine draws the way the committed baselines
        /// were drawn.
        ///
        /// This is the honest limit of a pixel-exact golden suite, and saying
        /// it out loud is better than the two alternatives. A tolerance wide
        /// enough to cover Metal against OpenGL is wide enough to cover a glyph
        /// moving, which makes the suite decorative; failing on every machine
        /// that is not the one the baselines came from makes it noise nobody
        /// reads. So: a matching renderer runs the comparison, and anything
        /// else says why it did not.
        /// </summary>
        private static void RequireTheRendererTheBaselinesCameFrom()
        {
            string committed = GoldenCases.CommittedRendererStamp();
            if (committed == null)
                Assert.Ignore(
                    $"No renderer stamp at {GoldenCases.RendererStampPath}: the baselines do not " +
                    "say which rasterizer drew them. Regenerate with " +
                    "OneText.Editor.GoldenRegen.RegenerateAll.");

            if (committed != GoldenCases.RendererStamp)
                Assert.Inconclusive(
                    "The baselines were drawn by a different renderer, so a pixel comparison " +
                    $"would be measuring the driver rather than the text.\n  baselines: {committed}" +
                    $"\n  this machine: {GoldenCases.RendererStamp}");
        }

        /// <summary>
        /// The registry itself: names are unique and usable as file names, and
        /// every case has a baseline checked in. Without this a case that is
        /// silently never rendered looks exactly like a case that passes.
        /// </summary>
        [Test]
        public void Every_Case_Has_A_Unique_Name_And_A_Baseline()
        {
            var seen = new HashSet<string>();
            var missingBaselines = new List<string>();

            foreach (var golden in GoldenCases.All)
            {
                Assert.IsTrue(seen.Add(golden.Name), $"duplicate golden case name '{golden.Name}'");
                Assert.AreEqual(-1, golden.Name.IndexOfAny(Path.GetInvalidFileNameChars()),
                    $"'{golden.Name}' is not usable as a file name");
                Assert.Greater(golden.Width, 0);
                Assert.Greater(golden.Height, 0);

                // A case whose fonts are not on disk cannot have a baseline
                // regenerated here, so its absence is not a finding.
                if (golden.MissingFont() == null && !File.Exists(golden.BaselinePath))
                    missingBaselines.Add(golden.Name);
            }

            Assert.Greater(GoldenCases.All.Count, 0, "the golden registry is empty");
            CollectionAssert.IsEmpty(missingBaselines,
                "golden cases with no committed baseline; run OneText.Editor.GoldenRegen.RegenerateAll");
        }
    }
}
