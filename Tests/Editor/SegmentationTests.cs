using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OneText.Unicode;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>
    /// Runs the official UCD conformance files for line breaking (UAX #14) and
    /// segmentation (UAX #29) exactly as shipped by Unicode.
    /// </summary>
    public class SegmentationTests
    {
        private const string DataDir = "Packages/com.onetext.core/Tests/UnicodeData/";

        /// <summary>
        /// Parses a test line such as "÷ 0020 × 0308 ÷ 0041 ÷" into the sample
        /// string plus the expected break flag at every UTF-16 index.
        /// </summary>
        private static (string text, bool[] breaks) ParseCase(string spec)
        {
            var sb = new StringBuilder();
            var breaks = new List<bool>();
            foreach (var token in spec.Split(' '))
            {
                if (token.Length == 0) continue;
                if (token == "÷" || token == "×")
                {
                    while (breaks.Count < sb.Length) breaks.Add(false);
                    breaks.Add(token == "÷");
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(
                        int.Parse(token, NumberStyles.HexNumber)));
                }
            }
            while (breaks.Count <= sb.Length) breaks.Add(false);
            return (sb.ToString(), breaks.ToArray());
        }

        private static IEnumerable<string> Cases(string file)
        {
            foreach (var raw in File.ReadLines(Path.GetFullPath(DataDir + file)))
            {
                var body = raw.Split('#')[0].Trim();
                if (body.Length > 0) yield return body;
            }
        }

        [Test]
        public void Passes_Complete_LineBreakTest()
        {
            int total = 0, failed = 0;
            string first = null;
            var opportunities = new LineBreaker.Opportunity[64];

            foreach (var spec in Cases("LineBreakTest.txt"))
            {
                var (text, expected) = ParseCase(spec);
                if (opportunities.Length < text.Length + 1)
                    opportunities = new LineBreaker.Opportunity[text.Length * 2 + 2];
                LineBreaker.Analyze(text, opportunities);

                total++;
                bool ok = true;
                // LineBreakTest never marks a break at the start of text (LB2).
                for (int i = 1; i <= text.Length && ok; i++)
                    ok = expected[i] == (opportunities[i] != LineBreaker.Opportunity.None);
                if (!ok) { failed++; first ??= spec; }
            }

            Assert.Greater(total, 19000, "test file did not load fully");
            Assert.AreEqual(0, failed, $"{failed}/{total} LineBreakTest lines failed. First: {first}");
        }

        [Test]
        public void Passes_Complete_GraphemeBreakTest()
        {
            AssertSegmentation("GraphemeBreakTest.txt", 700, TextSegmenter.GraphemeBoundaries);
        }

        [Test]
        public void Passes_Complete_WordBreakTest()
        {
            AssertSegmentation("WordBreakTest.txt", 1800, TextSegmenter.WordBoundaries);
        }

        [Test]
        public void Passes_Complete_SentenceBreakTest()
        {
            AssertSegmentation("SentenceBreakTest.txt", 500, TextSegmenter.SentenceBoundaries);
        }

        private static void AssertSegmentation(string file, int minimumCases,
            System.Action<string, List<int>> segment)
        {
            int total = 0, failed = 0;
            string first = null;
            var boundaries = new List<int>();

            foreach (var spec in Cases(file))
            {
                var (text, expected) = ParseCase(spec);
                segment(text, boundaries);

                total++;
                bool ok = true;
                for (int i = 0; i <= text.Length && ok; i++)
                    ok = expected[i] == boundaries.Contains(i);
                if (!ok) { failed++; first ??= spec; }
            }

            Assert.Greater(total, minimumCases, "test file did not load fully");
            Assert.AreEqual(0, failed, $"{failed}/{total} {file} lines failed. First: {first}");
        }

        [Test]
        public void Wrapping_Basics()
        {
            var opportunities = LineBreaker.Analyze("Hello world\nagain");
            Assert.AreEqual(LineBreaker.Opportunity.None, opportunities[0], "LB2: never break at sot");
            Assert.AreEqual(LineBreaker.Opportunity.Allowed, opportunities[6], "break after the space");
            Assert.AreEqual(LineBreaker.Opportunity.None, opportunities[3], "no break inside a word");
            Assert.AreEqual(LineBreaker.Opportunity.Mandatory, opportunities[12], "newline is mandatory");
            Assert.AreEqual(LineBreaker.Opportunity.Mandatory, opportunities[^1], "LB3: break at eot");
        }

        [Test]
        public void Graphemes_Keep_Emoji_And_Marks_Together()
        {
            // Family emoji (ZWJ sequence), flag (RI pair), and a combining mark.
            Assert.AreEqual(1, TextSegmenter.CountGraphemes(
                "\U0001F468\u200D\U0001F469\u200D\U0001F467"), "family emoji zwj sequence");
            Assert.AreEqual(1, TextSegmenter.CountGraphemes("\U0001F1F0\U0001F1F7"), "flag");
            Assert.AreEqual(1, TextSegmenter.CountGraphemes("e\u0301"), "e + combining acute");
            Assert.AreEqual(3, TextSegmenter.CountGraphemes("\uD55C\uAE00a"), "two syllables + a");
        }
    }
}
