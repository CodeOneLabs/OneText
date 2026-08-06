using System.Collections.Generic;
using System.IO;
using OneText.Unicode;
using NUnit.Framework;

namespace OneText.Tests
{
    public class BidiTests
    {
        private const string TestFile = "Packages/com.onetext.core/Tests/UnicodeData~/BidiCharacterTest.txt";

        [Test]
        public void Passes_Complete_BidiCharacterTest()
        {
            int total = 0, failed = 0;
            string firstFailure = null;

            var levels = new byte[256];
            var removed = new bool[256];
            var visual = new List<int>();

            foreach (var raw in File.ReadLines(Path.GetFullPath(TestFile)))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var cols = line.Split(';');
                var cpTokens = cols[0].Split(' ');
                var cps = new int[cpTokens.Length];
                for (int i = 0; i < cpTokens.Length; i++)
                    cps[i] = int.Parse(cpTokens[i], System.Globalization.NumberStyles.HexNumber);

                byte direction = byte.Parse(cols[1]);
                byte expectedPara = byte.Parse(cols[2]);
                var expectedLevels = cols[3].Split(' ');
                var expectedVisual = cols[4].Trim().Length == 0
                    ? new string[0] : cols[4].Trim().Split(' ');

                if (cps.Length > levels.Length)
                {
                    levels = new byte[cps.Length * 2];
                    removed = new bool[cps.Length * 2];
                }
                System.Array.Clear(levels, 0, cps.Length);
                System.Array.Clear(removed, 0, cps.Length);

                total++;
                byte para = BidiAlgorithm.Resolve(cps, direction, levels, removed, visual);

                bool ok = para == expectedPara &&
                          visual.Count == expectedVisual.Length &&
                          cps.Length == expectedLevels.Length;
                if (ok)
                {
                    for (int i = 0; i < cps.Length && ok; i++)
                    {
                        string got = removed[i] ? "x" : levels[i].ToString();
                        ok = got == expectedLevels[i];
                    }
                    for (int i = 0; i < visual.Count && ok; i++)
                        ok = visual[i].ToString() == expectedVisual[i];
                }

                if (!ok)
                {
                    failed++;
                    firstFailure ??= line;
                }
            }

            Assert.Greater(total, 90000, "test file did not load fully");
            Assert.AreEqual(0, failed,
                $"{failed}/{total} BidiCharacterTest lines failed. First: {firstFailure}");
        }

        [Test]
        public void MixedDirection_Sanity()
        {
            // "abc ALEF-BET-GIMEL 123": Hebrew segment reversed, digits at level 2.
            var cps = new List<int> { 'a', ' ', 0x05D0, 0x05D1, ' ', '1', '2' };
            var levels = new byte[cps.Count];
            var removed = new bool[cps.Count];
            var visual = new List<int>();

            byte para = BidiAlgorithm.Resolve(cps, BidiAlgorithm.AutoDirection, levels, removed, visual);

            Assert.AreEqual(0, para);
            Assert.AreEqual(0, levels[0]);
            Assert.AreEqual(1, levels[2]);
            Assert.AreEqual(1, levels[3]);
            Assert.AreEqual(2, levels[5], "digits after RTL should be level 2");
            // Visual: a _ [12 _ heb1 heb0]; the whole RTL segment reverses,
            // digits stay LTR inside it: 0 1 5 6 4 3 2.
            Assert.AreEqual(6, visual.IndexOf(2), "hebrew letters must swap visually");
            Assert.AreEqual(5, visual.IndexOf(3));
            Assert.Less(visual.IndexOf(5), visual.IndexOf(6), "digits must stay LTR");
        }
    }
}
