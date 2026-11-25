using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText.Unicode;

namespace OneText.Tests
{
    /// <summary>
    /// The Unicode stages' shortcuts, held to the long way round: each has a
    /// prefilter that is checked against the property data for every code
    /// point it accepts, and an answer that is checked against the full
    /// algorithm over the official conformance files and a fuzzed corpus.
    /// </summary>
    public class UnicodeShortcutTests
    {
        private const string DataDir = "Packages/com.onetext.core/Tests/UnicodeData~/";

        [TearDown]
        public void RestoreSwitches()
        {
            BidiRuns.LeftToRightShortcut = true;
            TextSegmenter.StandaloneShortcut = true;
        }

        // ------------------------------------------------------------ prefilters

        [Test]
        public void HardBreak_Prefilter_Skips_Only_Characters_That_Cannot_Be_Hard_Breaks()
        {
            for (int c = 0; c <= 0xFFFF; c++)
            {
                if (LineBreaker.MayBeHardBreak((char)c)) continue;
                var cls = BreakData.GetLineClass(c);
                Assert.IsFalse(cls == LineBreakClass.BK || cls == LineBreakClass.CR ||
                               cls == LineBreakClass.LF || cls == LineBreakClass.NL,
                    $"U+{c:X4} is {cls} and the prefilter skips it");
            }
            // A high surrogate is never asked: the code point it starts is
            // skipped whole, so none of them may be a hard break either.
            for (int cp = 0x10000; cp <= 0x10FFFF; cp++)
            {
                var cls = BreakData.GetLineClass(cp);
                Assert.IsFalse(cls == LineBreakClass.BK || cls == LineBreakClass.CR ||
                               cls == LineBreakClass.LF || cls == LineBreakClass.NL, $"U+{cp:X} is {cls}");
            }
        }

        [Test]
        public void Bidi_Prefilter_Accepts_Only_Characters_With_No_Hard_Class()
        {
            int accepted = 0;
            for (int c = 0; c <= 0xFFFF; c++)
            {
                if (!BidiRuns.CannotBeHard((char)c)) continue;
                accepted++;
                Assert.IsFalse(char.IsSurrogate((char)c), $"U+{c:X4} is a surrogate");
                var cls = BidiData.GetClass(c);
                Assert.IsFalse(BidiAlgorithm.IsDirectionallyHard(cls), $"U+{c:X4} is {cls}");
            }
            Assert.Greater(accepted, 30000);
        }

        [Test]
        public void Grapheme_Prefilter_Accepts_Only_Characters_No_Rule_Can_Join()
        {
            int accepted = 0;
            for (int c = 0; c <= 0xFFFF; c++)
            {
                if (!TextSegmenter.IsStandalone((char)c)) continue;
                accepted++;
                Assert.IsFalse(char.IsSurrogate((char)c), $"U+{c:X4} is a surrogate");
                var cls = BreakData.GetGraphemeClass(c);
                Assert.IsTrue(cls == GraphemeBreakClass.Other || cls == GraphemeBreakClass.LV ||
                              cls == GraphemeBreakClass.LVT, $"U+{c:X4} is {cls}");
                var incb = BreakData.GetIncb(c);
                Assert.IsTrue(incb == IncbClass.None || incb == IncbClass.Consonant, $"U+{c:X4} is InCB {incb}");
            }
            Assert.Greater(accepted, 30000);
        }

        // --------------------------------------------------------------- corpora

        private static IEnumerable<string> ConformanceStrings(string file)
        {
            foreach (var raw in File.ReadLines(Path.GetFullPath(DataDir + file)))
            {
                var body = raw.Split('#')[0].Trim();
                if (body.Length == 0) continue;
                var sb = new StringBuilder();
                foreach (var token in body.Split(' '))
                {
                    if (token.Length == 0 || token == "÷" || token == "×") continue;
                    sb.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber)));
                }
                yield return sb.ToString();
            }
        }

        private static IEnumerable<string> BidiCharacterStrings()
        {
            foreach (var raw in File.ReadLines(Path.GetFullPath(DataDir + "BidiCharacterTest.txt")))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var sb = new StringBuilder();
                foreach (var token in line.Split(';')[0].Split(' '))
                    sb.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber)));
                yield return sb.ToString();
            }
        }

        // The characters the shortcuts are about and the ones that must defeat
        // them, mixed: newlines of every kind, marks, jamo, surrogate pairs and
        // lone halves, RTL letters and numbers, controls, emoji sequences.
        private static readonly string[] Alphabet =
        {
            "a", "Z", "7", " ", "-", ".", "가", "힣", "漢", "字", "あ", "ア", "ー", "、", "。", "「", "」", "！",
            "\n", "\r", "\r\n", "\u0085", "\u2028", "\u2029", "\u000B", "\u000C", "\t",
            "\u0301", "\u200D", "\u200B", "\u00AD", "\u3099", "\u302A", "ᄀ", "ᅡ", "ᆨ",
            "א", "ا", "١", "\u200F", "\u202B", "\u202C", "\u2067", "\u2069", "\u061C",
            "😀", "👍🏽", "🇰🇷", "\uD83D", "\uDE00", "ก", "\u0E31", "क", "\u094D", "Ａ", "ﾞ",
        };

        private static IEnumerable<string> Fuzz(int count, int seed)
        {
            var random = new Random(seed);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Clear();
                int length = random.Next(0, 12);
                // Half the corpus stays inside the shortcut's territory, so the
                // shortcut is exercised, not only its refusal.
                bool plain = random.Next(2) == 0;
                for (int k = 0; k < length; k++)
                    sb.Append(Alphabet[random.Next(plain ? 18 : Alphabet.Length)]);
                yield return sb.ToString();
            }
        }

        private static IEnumerable<string> Corpus(string conformanceFile)
        {
            foreach (var s in ConformanceStrings(conformanceFile)) yield return s;
            foreach (var s in Fuzz(20000, 17)) yield return s;
        }

        // ------------------------------------------------------------- answers

        [Test]
        public void AnalyzeMandatory_Finds_Exactly_The_Mandatory_Breaks_Of_Analyze()
        {
            var full = new LineBreaker.Opportunity[64];
            var mandatory = new LineBreaker.Opportunity[64];
            int total = 0;
            foreach (string text in Corpus("LineBreakTest.txt"))
            {
                if (full.Length < text.Length + 1)
                {
                    full = new LineBreaker.Opportunity[text.Length * 2 + 2];
                    mandatory = new LineBreaker.Opportunity[text.Length * 2 + 2];
                }
                LineBreaker.Analyze(text, full);
                LineBreaker.AnalyzeMandatory(text, mandatory);
                for (int i = 0; i <= text.Length; i++)
                {
                    bool expected = full[i] == LineBreaker.Opportunity.Mandatory;
                    if (expected != (mandatory[i] == LineBreaker.Opportunity.Mandatory) ||
                        (!expected && mandatory[i] != LineBreaker.Opportunity.None))
                        Assert.Fail($"{Escape(text)} at {i}: {full[i]} vs {mandatory[i]}");
                }
                total++;
            }
            Assert.Greater(total, 39000);
        }

        [Test]
        public void Bidi_Shortcut_Gives_The_Algorithms_Runs()
        {
            var slow = new List<BidiRuns.Run>();
            var fast = new List<BidiRuns.Run>();
            int total = 0, shortcut = 0;
            var strings = new List<string>(BidiCharacterStrings());
            strings.AddRange(Fuzz(20000, 23));
            foreach (string text in strings)
            foreach (byte direction in new byte[] { 0, 1, BidiAlgorithm.AutoDirection })
            {
                // Sub-ranges too: the engine asks per paragraph.
                for (int start = 0; start <= Math.Min(2, text.Length); start++)
                {
                    int length = text.Length - start;
                    BidiRuns.LeftToRightShortcut = false;
                    byte expected = BidiRuns.GetLogicalRuns(text, start, length, direction, slow);
                    BidiRuns.LeftToRightShortcut = true;
                    byte actual = BidiRuns.GetLogicalRuns(text, start, length, direction, fast);
                    Assert.AreEqual(expected, actual, $"paragraph level of {Escape(text)} from {start} dir {direction}");
                    Assert.AreEqual(slow.Count, fast.Count, $"run count of {Escape(text)} from {start} dir {direction}");
                    for (int r = 0; r < slow.Count; r++)
                        Assert.IsTrue(slow[r].Start == fast[r].Start && slow[r].Length == fast[r].Length &&
                                      slow[r].Level == fast[r].Level, $"run {r} of {Escape(text)} dir {direction}");
                    total++;
                    if (direction != 1 && length > 0 && BidiRuns.HasNothingHard(text, start, text.Length)) shortcut++;
                }
            }
            Assert.Greater(total, 100000);
            Assert.Greater(shortcut, 10000, "the shortcut has to be taken to be tested");
        }

        [Test]
        public void Grapheme_Shortcut_Gives_The_Algorithms_Boundaries()
        {
            var slow = new List<int>();
            var fast = new List<int>();
            int total = 0;
            foreach (string text in Corpus("GraphemeBreakTest.txt"))
            {
                TextSegmenter.StandaloneShortcut = false;
                TextSegmenter.GraphemeBoundaries(text, slow);
                TextSegmenter.StandaloneShortcut = true;
                TextSegmenter.GraphemeBoundaries(text, fast);
                CollectionAssert.AreEqual(slow, fast, Escape(text));
                total++;
            }
            Assert.Greater(total, 20000);
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text) sb.Append($"\\u{(int)c:X4}");
            return sb.ToString();
        }
    }
}
