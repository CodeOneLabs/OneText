using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>
    /// The system tier was made cheaper to find a font with: every file's cmap
    /// is read on a worker thread after the first probe, a collection's faces
    /// share one parse of a shared cmap, and touching ranges are joined. None
    /// of that may move a character to another face. These compare each switch
    /// off and on; system fonts differ per machine, so every assertion is "the
    /// same answer both ways", whatever the answer is here.
    /// </summary>
    public class SystemFontIndexEquivalenceTests
    {
        private const string Coverage = "Packages/com.onetext.core/Tests/CoverageFonts~/";

        [SetUp]
        public void SetUp()
        {
            SystemFonts.Enabled = true;
            SystemFonts.Forget();
        }

        [TearDown]
        public void TearDown()
        {
            SystemFonts.IndexInBackground = true;
            SystemFonts.ShareCollectionCmaps = true;
            SystemFontIndex.FaceCoverage.MergeRanges = true;
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
        }

        /// <summary>
        /// Every 97th codepoint through the supplementary planes, plus the
        /// cases with paths of their own: unassigned kana (the character that
        /// cost a walk of the disk on a language-switch frame), emoji, CJK
        /// punctuation, Han that only some locales' faces carry.
        /// </summary>
        private static List<int> Sample()
        {
            var sample = new List<int>();
            for (int cp = 0x20; cp < 0x32000; cp += 97) sample.Add(cp);
            sample.AddRange(new[]
            {
                'A', 0x0644, 0x05D0, 0x0E01, 0x0915, 0x3097, 0x3098, 0x3041, 0x30A2, 0x30FB, 0x3001,
                0xFF0C, 0x300C, 0x76F4, 0x9AA8, 0x6F22, 0x5A42, 0xD55C, 0x1F600, 0x2764, 0x1F1F0, 0x20BB7,
            });
            return sample;
        }

        // ------------------------------------------------- the system tier

        [Test]
        public void The_System_Tier_Answers_The_Same_With_Or_Without_The_Background_Index()
        {
            if (SystemFontIndex.Files().Length == 0) Assert.Ignore("this machine lists no system fonts");
            var sample = Sample();

            SystemFonts.IndexInBackground = false;
            SystemFonts.Forget();
            var expected = Answers(sample);

            SystemFonts.IndexInBackground = true;
            SystemFonts.Forget();
            // Asked while the indexer is still reading: the race a real frame
            // runs, where some files are indexed and some are read in place.
            var racing = Answers(sample);
            CollectionAssert.AreEqual(expected, racing);

            // And once the indexer has read everything, from a fresh set of
            // answers over the coverage it left behind.
            int files = SystemFontIndex.Files().Length;
            for (int i = 0; i < 400 && SystemFontIndex.BackgroundIndexed + 1 < files; i++) Thread.Sleep(25);
            SystemFonts.ForgetAnswersForTests();
            var indexed = Answers(sample);
            CollectionAssert.AreEqual(expected, indexed);
        }

        private static List<string> Answers(List<int> sample)
        {
            var answers = new List<string>(sample.Count * 5);
            string[] languages = { "ja", "zh-Hans", "zh-Hant", "ko" };
            foreach (int cp in sample)
            {
                answers.Add($"U+{cp:X4} {Name(SystemFonts.Resolve(cp))}");
                foreach (string language in languages)
                    answers.Add($"U+{cp:X4} {language} {Name(SystemFonts.ResolveForLanguage(cp, language))}");
            }
            return answers;
        }

        private static string Name(FontData font) =>
            font == null ? "none" : font.SourcePath + " | " + SystemFonts.NameOf(font);

        [Test]
        public void Shared_And_Joined_Coverage_Claims_Exactly_What_The_Raw_Cmap_Claims()
        {
            var files = SystemFontIndex.Files();
            var paths = new List<string>(files);
            foreach (string file in Directory.GetFiles(Path.GetFullPath(Coverage))) paths.Add(file);

            SystemFonts.ShareCollectionCmaps = false;
            SystemFontIndex.FaceCoverage.MergeRanges = false;
            SystemFontIndex.Forget();
            var raw = new List<SystemFontIndex.FaceCoverage[]>();
            foreach (string path in paths) raw.Add(SystemFontIndex.Coverage(path));

            SystemFonts.ShareCollectionCmaps = true;
            SystemFontIndex.FaceCoverage.MergeRanges = true;
            SystemFontIndex.Forget();

            long probes = 0, joined = 0, rawRanges = 0;
            for (int f = 0; f < paths.Count; f++)
            {
                var now = SystemFontIndex.Coverage(paths[f]);
                Assert.AreEqual(raw[f].Length, now.Length, paths[f]);
                for (int i = 0; i < now.Length; i++)
                {
                    var before = raw[f][i];
                    var after = now[i];
                    Assert.AreEqual(before.FaceIndex, after.FaceIndex, paths[f]);
                    Assert.AreEqual(before.TableDirectory, after.TableDirectory, paths[f]);
                    rawRanges += before.RangeCount;
                    joined += after.RangeCount;
                    // Every edge of every raw range, both sides of it, and its
                    // middle: wherever a join or a share could have moved an
                    // answer, it would show here.
                    for (int r = 0; r < before.RangeCount; r++)
                    {
                        int start = before.Starts[r], end = before.Ends[r];
                        foreach (int cp in new[] { start - 1, start, start + (end - start) / 2, end, end + 1 })
                        {
                            probes++;
                            if (before.Covers(cp) != after.Covers(cp))
                                Assert.Fail($"{paths[f]} face {after.FaceIndex}: U+{cp:X4} " +
                                            $"raw {before.Covers(cp)}, now {after.Covers(cp)}");
                        }
                    }
                }
            }
            Assert.Greater(probes, 0);
            TestContext.WriteLine($"{paths.Count} files, {rawRanges} raw ranges, {joined} after joining, " +
                                  $"{probes} probes");
        }
    }
}
