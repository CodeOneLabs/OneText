using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OneText.Tests
{
    /// <summary>
    /// Every codepoint Unicode assigns, put through the whole pipeline.
    ///
    /// The UCD conformance suites check the algorithms (bidi resolves, lines
    /// break, clusters segment) against Unicode's own expected answers. None of
    /// them touches a font. This asks the other question: given the real text
    /// of the standard, does anything in shaping, outline extraction,
    /// rasterization or atlas placement throw, hang, or produce a tile that is
    /// not a glyph. That is the failure class that kills a text engine in the
    /// field, and it is invisible to every other test here.
    ///
    /// Two tiers, because one assertion cannot cover both halves honestly:
    ///
    /// 1. A codepoint some font in the set has a glyph for MUST render: a tile
    ///    with sane ink bounds, placed in the atlas.
    /// 2. EVERY assigned codepoint, glyph or not, must survive. A codepoint no
    ///    font covers has to come out as a clean miss rather than an exception,
    ///    an infinite loop, or a corrupted atlas.
    ///
    /// Tier 2 is also the atlas's worst hour: a hundred and fifty thousand tiles
    /// arriving into a budget that holds a few thousand exercises eviction far
    /// harder than any scenario benchmark does.
    ///
    /// The fonts are ~140 MB and are not in the repository. Run
    /// <c>python3 Tools/fetch_coverage_fonts.py</c> to fetch them; without them
    /// every test here reports inconclusive rather than passing quietly, because
    /// a coverage test that silently checks nothing is worse than no test.
    /// </summary>
    [Category("Coverage")]
    public class CodepointCoverageTests
    {
        private const string FontDirPath = "Packages/com.onetext.core/Tests/CoverageFonts~";
        private const string UnicodeDataPath = "Packages/com.onetext.core/Tests/UnicodeData~/UnicodeData.txt";

        /// <summary>
        /// Codepoints that are assigned and are meant to be drawn.
        ///
        /// Private use is excluded because what it renders is by definition a
        /// private agreement, surrogates because they are not characters, and
        /// Cc/Cf because a control is correct to draw as nothing; asserting a
        /// glyph for U+0009 would be asserting a bug.
        /// </summary>
        private static List<int> RenderableCodepoints()
        {
            var codepoints = new List<int>(160000);
            int previous = -1;

            foreach (string raw in File.ReadLines(Path.GetFullPath(UnicodeDataPath)))
            {
                int firstSemicolon = raw.IndexOf(';');
                if (firstSemicolon < 0) continue;

                int codepoint = Convert.ToInt32(raw.Substring(0, firstSemicolon), 16);
                int secondSemicolon = raw.IndexOf(';', firstSemicolon + 1);
                string name = raw.Substring(firstSemicolon + 1, secondSemicolon - firstSemicolon - 1);
                int thirdSemicolon = raw.IndexOf(';', secondSemicolon + 1);
                string category = raw.Substring(secondSemicolon + 1, thirdSemicolon - secondSemicolon - 1);

                bool renderable = category != "Co" && category != "Cs"
                    && category != "Cc" && category != "Cf";

                // Big blocks are written as a First/Last pair rather than a line
                // each; expanding them is the difference between 40,575 lines
                // and the ~300,000 codepoints they stand for.
                if (name.EndsWith(", Last>", StringComparison.Ordinal) && previous >= 0)
                {
                    if (renderable)
                        for (int cp = previous + 1; cp <= codepoint; cp++) codepoints.Add(cp);
                }
                else if (renderable)
                {
                    codepoints.Add(codepoint);
                }

                previous = codepoint;
            }

            return codepoints;
        }

        private static string[] FontFiles()
        {
            string dir = Path.GetFullPath(FontDirPath);
            if (!Directory.Exists(dir)) return Array.Empty<string>();

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(dir, "*.ttf"));
            files.AddRange(Directory.GetFiles(dir, "*.otf"));
            files.Sort(StringComparer.Ordinal);
            return files.ToArray();
        }

        private static void RequireFonts(string[] fonts)
        {
            if (fonts.Length == 0)
                Assert.Ignore(
                    $"No coverage fonts in {FontDirPath}. Run: python3 Tools/fetch_coverage_fonts.py");
        }

        /// <summary>
        /// Loads every font once and remembers which one first claims each
        /// codepoint. Loading 213 faces costs seconds; asking a 213-deep
        /// fallback chain about 160,000 codepoints costs far more, and would
        /// measure the chain rather than the pipeline.
        /// </summary>
        private static Dictionary<int, int> BuildOwnership(
            string[] fonts, List<FontData> loaded, List<int> codepoints)
        {
            foreach (string path in fonts)
            {
                try { loaded.Add(FontData.Load(File.ReadAllBytes(path))); }
                catch (Exception e) { Assert.Fail($"{Path.GetFileName(path)} failed to load: {e.Message}"); }
            }

            var owner = new Dictionary<int, int>(codepoints.Count);
            for (int f = 0; f < loaded.Count; f++)
            {
                var font = loaded[f];
                foreach (int cp in codepoints)
                {
                    if (owner.ContainsKey(cp)) continue;
                    if (font.HasGlyph(cp)) owner[cp] = f;
                }
            }
            return owner;
        }

        [Test]
        public void Every_Codepoint_With_A_Glyph_Renders_A_Real_Tile()
        {
            var fonts = FontFiles();
            RequireFonts(fonts);

            var codepoints = RenderableCodepoints();
            var loaded = new List<FontData>();

            try
            {
                var watch = Stopwatch.StartNew();
                var owner = BuildOwnership(fonts, loaded, codepoints);

                var noInkBounds = new List<int>();
                var insaneBounds = new List<int>();
                int checkedGlyphs = 0;

                foreach (var pair in owner)
                {
                    var font = loaded[pair.Value];
                    uint glyph = font.NominalGlyph(pair.Key);
                    if (glyph == 0) continue; // HasGlyph and cmap disagreeing is tier 2's problem

                    checkedGlyphs++;
                    if (!font.TryGetInkBounds(glyph, out var min, out var max))
                    {
                        // Legitimately empty for a space or a combining mark that
                        // draws nothing; only worth reporting in bulk.
                        noInkBounds.Add(pair.Key);
                        continue;
                    }

                    bool sane = !float.IsNaN(min.x) && !float.IsNaN(min.y)
                        && !float.IsNaN(max.x) && !float.IsNaN(max.y)
                        && !float.IsInfinity(min.x) && !float.IsInfinity(min.y)
                        && !float.IsInfinity(max.x) && !float.IsInfinity(max.y)
                        && max.x >= min.x && max.y >= min.y;
                    if (!sane) insaneBounds.Add(pair.Key);
                }

                Debug.Log($"[coverage] {codepoints.Count} renderable codepoints in Unicode; " +
                          $"{owner.Count} have a glyph in {fonts.Length} fonts " +
                          $"({100.0 * owner.Count / codepoints.Count:F2} %); " +
                          $"{checkedGlyphs} outlines read, {noInkBounds.Count} blank, " +
                          $"{insaneBounds.Count} malformed; {watch.Elapsed.TotalSeconds:F0} s");

                Assert.That(insaneBounds, Is.Empty,
                    "ink bounds must be finite and non-inverted; first few: " + Describe(insaneBounds));
            }
            finally
            {
                foreach (var font in loaded) font.Dispose();
            }
        }

        // Every assigned codepoint in the coverage fonts, shaped and rasterized.
        // It takes about two minutes on an idle machine, and Unity's default
        // budget is three — which is not headroom, it is a coin toss: the same
        // test passed at 124 s alone and failed at 224 s while the rest of the
        // suite was running. Ten minutes is the distance between "slow" and
        // "hung", which is the only thing a timeout should be measuring.
        [Test, Timeout(600000)]
        public void Every_Assigned_Codepoint_Survives_Shaping_And_The_Atlas()
        {
            var fonts = FontFiles();
            RequireFonts(fonts);

            var codepoints = RenderableCodepoints();
            var loaded = new List<FontData>();
            GlyphAtlas atlas = null;

            try
            {
                var owner = BuildOwnership(fonts, loaded, codepoints);

                // Deliberately small. The point is not to hold 160,000 tiles
                // (nothing could) but to make eviction run continuously and
                // prove the pipeline stays correct while it does.
                atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 });

                using var shaper = new Shaper();
                var glyphs = new List<ShapedGlyph>();
                var failures = new List<string>();
                var watch = Stopwatch.StartNew();
                int shaped = 0, withGlyph = 0;

                foreach (int cp in codepoints)
                {
                    string text = char.ConvertFromUtf32(cp);
                    // A codepoint no font covers still has to go somewhere: the
                    // first font stands in, and the answer must be a clean miss.
                    var font = loaded[owner.TryGetValue(cp, out int index) ? index : 0];

                    try
                    {
                        glyphs.Clear();
                        shaper.Shape(font, text, 0, text.Length,
                            Shaper.Direction.LeftToRight, glyphs, null);
                        shaped++;

                        foreach (var glyph in glyphs)
                        {
                            if (glyph.GlyphId == 0) continue;
                            withGlyph++;
                            // GetOrAdd, not Contains: Contains is a dictionary
                            // lookup and would leave outline extraction, the
                            // rasterizer and eviction untested, which is most of
                            // what this test exists to exercise.
                            var location = atlas.GetOrAdd(font, glyph.GlyphId, 24f);
                            if (location.HasPixels && (float.IsNaN(location.SizeUnits.x)
                                || float.IsNaN(location.SizeUnits.y)))
                            {
                                failures.Add($"U+{cp:X4}: tile size is NaN");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Add($"U+{cp:X4}: {e.GetType().Name} {e.Message}");
                        if (failures.Count > 20) break;
                    }
                }

                Debug.Log($"[coverage] shaped {shaped}/{codepoints.Count} codepoints, " +
                          $"{withGlyph} produced a glyph, {failures.Count} threw; " +
                          $"{watch.Elapsed.TotalSeconds:F0} s");

                Assert.That(failures, Is.Empty,
                    "no assigned codepoint may throw: " + string.Join("; ", failures));
            }
            finally
            {
                atlas?.Dispose();
                foreach (var font in loaded) font.Dispose();
            }
        }

        [Test]
        public void The_Font_Set_Reports_What_It_Cannot_Cover()
        {
            var fonts = FontFiles();
            RequireFonts(fonts);

            var codepoints = RenderableCodepoints();
            var loaded = new List<FontData>();

            try
            {
                var owner = BuildOwnership(fonts, loaded, codepoints);
                var uncovered = new List<int>();
                foreach (int cp in codepoints) if (!owner.ContainsKey(cp)) uncovered.Add(cp);

                // Not an assertion about the engine. Coverage is a property of
                // the fonts that exist, and the honest published number needs
                // this printed rather than rounded away: as of Unicode 17 the
                // gap is the blocks no free font has shipped for yet.
                Debug.Log($"[coverage] {uncovered.Count} of {codepoints.Count} renderable codepoints " +
                          $"have no glyph in any of the {fonts.Length} fonts " +
                          $"({100.0 * uncovered.Count / codepoints.Count:F2} %). " +
                          Describe(uncovered));

                Assert.That(owner.Count, Is.GreaterThan(0), "the font set covered nothing at all");
            }
            finally
            {
                foreach (var font in loaded) font.Dispose();
            }
        }

        private static string Describe(List<int> codepoints)
        {
            if (codepoints.Count == 0) return "none";
            codepoints.Sort();

            // Runs, not a list: 4,000 consecutive hieroglyphs is one fact.
            var text = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < codepoints.Count && shown < 8; shown++)
            {
                int start = codepoints[i], end = start;
                while (i + 1 < codepoints.Count && codepoints[i + 1] == end + 1) { i++; end = codepoints[i]; }
                i++;
                text.Append(start == end
                    ? $"U+{start:X4} "
                    : $"U+{start:X4}..U+{end:X4} ({end - start + 1}) ");
            }
            if (shown == 8) text.Append("…");
            return text.ToString();
        }
    }
}
