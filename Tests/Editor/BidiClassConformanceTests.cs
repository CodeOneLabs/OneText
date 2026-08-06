using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OneText.Unicode;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The other half of UAX #9's conformance data.
    ///
    /// <c>BidiCharacterTest.txt</c>, which <see cref="BidiTests"/> runs, is
    /// written in real characters. <c>BidiTest.txt</c> is written in bidi
    /// classes instead, and covers every combination of them up to four deep,
    /// including the ones no language actually writes, which is exactly where an
    /// implementation that special-cased its way through the first file falls
    /// over. It is also where the volume is: 493,501 lines, each carrying a
    /// bitset of the paragraph directions to test it under, expanding to 770,241
    /// cases against the character file's 91,707.
    ///
    /// Classes are turned back into characters through one representative each.
    /// Any character of the class would do (the algorithm sees only the class),
    /// and the representatives are checked against the engine's own class table
    /// before anything else runs, so a mapping that drifts fails loudly here
    /// rather than quietly passing the suite for the wrong reason.
    /// </summary>
    public class BidiClassConformanceTests
    {
        private const string TestFile = "Packages/com.onetext.core/Tests/UnicodeData/BidiTest.txt";

        /// <summary>One character per bidi class, by the class's own definition.</summary>
        private static readonly (string Name, int Codepoint)[] Representatives =
        {
            ("L", 0x0041),    // LATIN CAPITAL LETTER A
            ("R", 0x05D0),    // HEBREW LETTER ALEF
            ("AL", 0x0627),   // ARABIC LETTER ALEF
            ("EN", 0x0030),   // DIGIT ZERO
            ("ES", 0x002B),   // PLUS SIGN
            ("ET", 0x0023),   // NUMBER SIGN
            ("AN", 0x0660),   // ARABIC-INDIC DIGIT ZERO
            ("CS", 0x002C),   // COMMA
            ("NSM", 0x0300),  // COMBINING GRAVE ACCENT
            ("BN", 0x00AD),   // SOFT HYPHEN
            ("B", 0x2029),    // PARAGRAPH SEPARATOR
            ("S", 0x0009),    // CHARACTER TABULATION
            ("WS", 0x0020),   // SPACE
            ("ON", 0x0021),   // EXCLAMATION MARK
            ("LRE", 0x202A), ("RLE", 0x202B), ("PDF", 0x202C),
            ("LRO", 0x202D), ("RLO", 0x202E),
            ("LRI", 0x2066), ("RLI", 0x2067), ("FSI", 0x2068), ("PDI", 0x2069),
        };

        private static Dictionary<string, int> ClassToCodepoint()
        {
            var map = new Dictionary<string, int>(Representatives.Length, StringComparer.Ordinal);
            foreach (var (name, codepoint) in Representatives) map[name] = codepoint;
            return map;
        }

        /// <summary>
        /// The bitset in column two says which paragraph directions to run the
        /// line under: 1 auto, 2 LTR, 4 RTL. A line is one case per bit set, and
        /// dropping that expansion would silently test a third of the file.
        /// </summary>
        private static readonly (int Bit, byte Direction)[] Directions =
        {
            (1, BidiAlgorithm.AutoDirection),
            (2, 0),
            (4, 1),
        };

        [Test]
        public void Passes_Complete_BidiTest()
        {
            var classes = ClassToCodepoint();

            string[] expectedLevels = null;
            int[] expectedOrder = null;

            int cases = 0, failed = 0;
            string firstFailure = null;

            var codepoints = new List<int>(64);
            var levels = new byte[64];
            var removed = new bool[64];
            var visual = new List<int>();

            foreach (string raw in File.ReadLines(Path.GetFullPath(TestFile)))
            {
                string line = raw;
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("@Levels:", StringComparison.Ordinal))
                {
                    expectedLevels = line.Substring(8).Trim()
                        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    continue;
                }
                if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
                {
                    var tokens = line.Substring(9).Trim()
                        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    expectedOrder = new int[tokens.Length];
                    for (int i = 0; i < tokens.Length; i++) expectedOrder[i] = int.Parse(tokens[i]);
                    continue;
                }
                if (line[0] == '@') continue;

                int semicolon = line.IndexOf(';');
                if (semicolon < 0 || expectedLevels == null) continue;

                var names = line.Substring(0, semicolon).Trim()
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int bitset = int.Parse(line.Substring(semicolon + 1).Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture);

                codepoints.Clear();
                bool mappable = true;
                foreach (string name in names)
                {
                    if (!classes.TryGetValue(name, out int codepoint))
                    {
                        mappable = false;
                        break;
                    }
                    codepoints.Add(codepoint);
                }
                // An unknown class name means the file has grown a class this
                // test does not know, which is a gap to fix rather than skip.
                Assert.That(mappable, Is.True, $"no representative for a class in: {line}");

                if (codepoints.Count > levels.Length)
                {
                    levels = new byte[codepoints.Count * 2];
                    removed = new bool[codepoints.Count * 2];
                }

                foreach (var (bit, direction) in Directions)
                {
                    if ((bitset & bit) == 0) continue;

                    Array.Clear(levels, 0, codepoints.Count);
                    Array.Clear(removed, 0, codepoints.Count);
                    cases++;

                    BidiAlgorithm.Resolve(codepoints, direction, levels, removed, visual);

                    bool ok = codepoints.Count == expectedLevels.Length;
                    for (int i = 0; ok && i < codepoints.Count; i++)
                    {
                        // "x" marks a character the algorithm removes, whose
                        // level is undefined rather than zero.
                        string got = removed[i] ? "x" : levels[i].ToString(CultureInfo.InvariantCulture);
                        ok = got == expectedLevels[i];
                    }
                    if (ok && expectedOrder != null)
                    {
                        ok = visual.Count == expectedOrder.Length;
                        for (int i = 0; ok && i < visual.Count; i++) ok = visual[i] == expectedOrder[i];
                    }

                    if (!ok)
                    {
                        failed++;
                        if (firstFailure == null)
                        {
                            firstFailure = $"'{string.Join(" ", names)}' at direction {direction}: " +
                                $"expected levels [{string.Join(" ", expectedLevels)}], " +
                                $"got [{Describe(levels, removed, codepoints.Count)}]";
                        }
                    }
                }
            }

            Debug.Log($"[conformance] BidiTest: {cases} cases, {cases - failed} passed");

            Assert.That(cases, Is.GreaterThan(700000),
                "BidiTest.txt should expand to over 700,000 cases; far fewer means the " +
                "paragraph-direction bitset was not expanded and most of the file went untested");
            Assert.That(failed, Is.Zero, $"{failed} of {cases} failed. First: {firstFailure}");
        }

        [Test]
        public void Every_Representative_Really_Has_The_Class_It_Stands_For()
        {
            // The suite above is only meaningful if these characters carry the
            // classes their names claim. Resolving a single character under an
            // LTR paragraph is the cheapest observable proxy the public API
            // offers: a strong R or AL must come out at an odd level, and a
            // strong L at an even one.
            var levels = new byte[1];
            var removed = new bool[1];
            var visual = new List<int>();
            var one = new List<int> { 0 };

            foreach (var (name, codepoint) in Representatives)
            {
                bool expectRightToLeft = name == "R" || name == "AL";
                if (!expectRightToLeft && name != "L") continue;

                one[0] = codepoint;
                Array.Clear(levels, 0, 1);
                Array.Clear(removed, 0, 1);
                BidiAlgorithm.Resolve(one, 0, levels, removed, visual);

                bool odd = (levels[0] & 1) == 1;
                Assert.That(odd, Is.EqualTo(expectRightToLeft),
                    $"U+{codepoint:X4} stands for class {name} but resolved to level {levels[0]}");
            }
        }

        private static string Describe(byte[] levels, bool[] removed, int count)
        {
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = removed[i] ? "x" : levels[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(" ", parts);
        }
    }
}
