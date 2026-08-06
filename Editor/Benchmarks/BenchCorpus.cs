using System;
using System.Collections.Generic;
using System.Text;

namespace OneText.Benchmarks
{
    /// <summary>
    /// Deterministic text for the scenarios. Two properties matter and both are
    /// deliberate:
    ///
    /// - **reuse ratio.** Real chat and real UI repeat most of their vocabulary
    ///   and introduce a trickle of new characters. A corpus of pure novelty
    ///   measures rasterization; a corpus of pure repetition measures nothing.
    ///   The default is 60% from a fixed pool, 40% newly drawn.
    /// - **fixed seed.** Both systems under test see the identical strings in
    ///   the identical order, and a rerun reproduces the numbers.
    /// </summary>
    public sealed class BenchCorpus
    {
        private readonly Random _random;
        private readonly List<string> _common = new List<string>();
        private readonly float _reuse;

        public BenchCorpus(int seed, Language language, float reuse = 0.6f, int commonWords = 220)
        {
            _random = new Random(seed);
            _reuse = reuse;
            Script = language;
            for (int i = 0; i < commonWords; i++) _common.Add(NewWord());
        }

        public enum Language { Korean, Japanese, English, Mixed }

        public Language Script { get; }

        /// <summary>A line of <paramref name="words"/> words.</summary>
        public string Line(int words = 7)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < words; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(_random.NextDouble() < _reuse
                    ? _common[_random.Next(_common.Count)]
                    : NewWord());
            }
            return builder.ToString();
        }

        /// <summary>A short label: a word and a number, the shape most HUD text has.</summary>
        public string Short(int number) =>
            $"{_common[_random.Next(_common.Count)]} {number}";

        private string NewWord()
        {
            int length = 2 + _random.Next(3);
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++) builder.Append(NewChar());
            return builder.ToString();
        }

        private char NewChar()
        {
            switch (Script)
            {
                case Language.Korean:
                    return (char)(0xAC00 + _random.Next(11172));
                case Language.Japanese:
                    // Kana mostly, with the Han a real sentence would carry.
                    return _random.NextDouble() < 0.7
                        ? (char)(0x3041 + _random.Next(0x30FF - 0x3041))
                        : (char)(0x4E00 + _random.Next(6000));
                case Language.English:
                    return (char)('a' + _random.Next(26));
                default:
                    double roll = _random.NextDouble();
                    if (roll < 0.4) return (char)(0xAC00 + _random.Next(11172));
                    if (roll < 0.7) return (char)(0x4E00 + _random.Next(6000));
                    return (char)('a' + _random.Next(26));
            }
        }

        /// <summary>Every character this corpus can produce from its fixed pool: a prewarm charset.</summary>
        public List<int> CommonCodepoints()
        {
            var seen = new HashSet<int>();
            var result = new List<int>();
            foreach (string word in _common)
            {
                foreach (char c in word)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    if (seen.Add(c)) result.Add(c);
                }
            }
            for (char c = '0'; c <= '9'; c++)
                if (seen.Add(c)) result.Add(c);
            return result;
        }
    }
}
