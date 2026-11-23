using System;
using System.Collections.Generic;

namespace OneText.Unicode
{
    /// <summary>
    /// Bridges the BiDi algorithm to the shaping pipeline: turns a UTF-16
    /// string into directional runs listed in visual (left-to-right) order,
    /// each shapeable as one HarfBuzz buffer with a forced direction.
    /// </summary>
    public static class BidiRuns
    {
        public struct Run
        {
            /// <summary>UTF-16 offset of the run's first code unit.</summary>
            public int Start;

            /// <summary>Run length in UTF-16 code units.</summary>
            public int Length;

            /// <summary>Resolved embedding level; odd = right-to-left.</summary>
            public byte Level;

            public bool IsRightToLeft => (Level & 1) != 0;
        }

        /// <summary>
        /// Computes runs in <em>logical</em> order, covering the range without
        /// gaps; line breaking works in logical order, and each line reorders
        /// its own runs visually afterwards.
        /// </summary>
        /// <param name="text">Text containing the paragraph.</param>
        /// <param name="start">Paragraph start, in UTF-16 code units.</param>
        /// <param name="length">Paragraph length, in UTF-16 code units.</param>
        /// <param name="paragraphDirection">0 = LTR, 1 = RTL, BidiAlgorithm.AutoDirection = auto.</param>
        /// <param name="output">Cleared and filled with runs in logical order.</param>
        /// <returns>The resolved paragraph level.</returns>
        public static byte GetLogicalRuns(string text, int start, int length,
            byte paragraphDirection, List<Run> output) =>
            GetLogicalRuns(text.AsSpan(), start, length, paragraphDirection, output);

        /// <inheritdoc cref="GetLogicalRuns(string, int, int, byte, List{Run})"/>
        public static byte GetLogicalRuns(ReadOnlySpan<char> text, int start, int length,
            byte paragraphDirection, List<Run> output)
        {
            output.Clear();
            if (text.IsEmpty || length <= 0) return 0;

            if (LeftToRightShortcut &&
                (paragraphDirection == 0 || paragraphDirection == BidiAlgorithm.AutoDirection) &&
                HasNothingHard(text, start, start + length))
            {
                // What BidiAlgorithm.Resolve's own plain path concludes for
                // this text, without building its inputs: level 0 everywhere,
                // nothing removed, one run.
                output.Add(new Run { Start = start, Length = length, Level = 0 });
                return 0;
            }

            var cps = t_cps ??= new List<int>();
            var cpStarts = t_cpStarts ??= new List<int>();
            cps.Clear();
            cpStarts.Clear();
            int end = start + length;
            for (int i = start; i < end;)
            {
                cps.Add(Codepoint(text, i));
                cpStarts.Add(i);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }
            cpStarts.Add(end); // sentinel: end offset of the last codepoint

            var levels = Scratch(ref t_levels, cps.Count);
            var removed = Scratch(ref t_removed, cps.Count);
            var visual = t_visual ??= new List<int>();
            byte para = BidiAlgorithm.Resolve(cps, paragraphDirection, levels, removed, visual);

            // Characters removed by X9 (embedding controls) produce no glyphs but
            // must not punch holes in the coverage: they inherit the run they sit in.
            for (int i = 0; i < cps.Count; i++)
                if (removed[i]) levels[i] = i > 0 ? levels[i - 1] : para;

            int runStart = 0;
            for (int i = 1; i <= cps.Count; i++)
            {
                if (i < cps.Count && levels[i] == levels[runStart]) continue;
                output.Add(new Run
                {
                    Start = cpStarts[runStart],
                    Length = cpStarts[i] - cpStarts[runStart],
                    Level = levels[runStart],
                });
                runStart = i;
            }

            return para;
        }

        /// <summary>
        /// Whether <see cref="GetLogicalRuns(ReadOnlySpan{char}, int, int, byte, List{Run})"/>
        /// answers left-to-right text without running the algorithm. Same
        /// answer either way; the switch exists to measure that and to prove it.
        /// </summary>
        public static bool LeftToRightShortcut = true;

        /// <summary>
        /// True when no code point in [start, end) has a class that
        /// <see cref="BidiAlgorithm"/>'s plain path rejects: R, AL, AN, BN or
        /// an explicit embedding, override or isolate. Text like that, in a
        /// paragraph that is not right-to-left, resolves to level 0 throughout
        /// (W7 turns every European number into L, and nothing else can raise
        /// a level), which is the conclusion Resolve reaches the long way
        /// after decoding the text into lists and clearing three scratch
        /// buffers. Decoded here exactly as there, code point by code point.
        /// </summary>
        internal static bool HasNothingHard(ReadOnlySpan<char> text, int start, int end)
        {
            for (int i = start; i < end;)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (BidiAlgorithm.IsDirectionallyHard(BidiData.GetClass(Codepoint(text, i)))) return false;
                    i += 2;
                    continue;
                }
                if (!CannotBeHard(c) && BidiAlgorithm.IsDirectionallyHard(BidiData.GetClass(Codepoint(text, i)))) return false;
                i++;
            }
            return true;
        }

        /// <summary>
        /// A cheap reject for the blocks the text this engine mostly sees lives
        /// in: printable ASCII, Latin-1 to Armenian, Indic to Mongolian, and
        /// everything from the CJK symbols through Hangul. None of their code
        /// points has a hard class; a test walks every one of them against
        /// the data so this cannot drift from it.
        /// </summary>
        internal static bool CannotBeHard(char c) =>
            (c >= ' ' && c < '\u007F') ||
            (c >= '\u00A0' && c < '\u0590' && c != '\u00AD') ||
            (c >= '\u3000' && c < '\uD800');

        [System.ThreadStatic] private static byte[] t_levels;
        [System.ThreadStatic] private static bool[] t_removed;

        // Reused across calls: one layout pass asks for runs once per label.
        private static T[] Scratch<T>(ref T[] array, int length)
        {
            if (array == null || array.Length < length)
                array = new T[UnityEngine.Mathf.NextPowerOfTwo(UnityEngine.Mathf.Max(length, 32))];
            else System.Array.Clear(array, 0, length);
            return array;
        }

        [System.ThreadStatic] private static List<int> t_cps;
        [System.ThreadStatic] private static List<int> t_cpStarts;
        [System.ThreadStatic] private static List<int> t_visual;

        /// <summary>
        /// Computes visual-order runs for one paragraph of text.
        /// </summary>
        /// <param name="text">Paragraph text (no embedded paragraph separators expected).</param>
        /// <param name="paragraphDirection">0 = LTR, 1 = RTL, BidiAlgorithm.AutoDirection = auto.</param>
        /// <param name="output">Cleared and filled with runs in visual order.</param>
        /// <returns>The resolved paragraph level (0 = LTR base, 1 = RTL base).</returns>
        public static byte GetVisualRuns(string text, byte paragraphDirection, List<Run> output) =>
            GetVisualRuns(text.AsSpan(), paragraphDirection, output);

        /// <inheritdoc cref="GetVisualRuns(string, byte, List{Run})"/>
        public static byte GetVisualRuns(ReadOnlySpan<char> text, byte paragraphDirection, List<Run> output)
        {
            output.Clear();
            if (text.IsEmpty) return 0;

            var cps = t_cps ??= new List<int>();
            var cpStarts = t_cpStarts ??= new List<int>();
            cps.Clear();
            cpStarts.Clear();
            for (int i = 0; i < text.Length;)
            {
                int cp = Codepoint(text, i);
                cps.Add(cp);
                cpStarts.Add(i);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }
            cpStarts.Add(text.Length); // sentinel: end offset of last codepoint

            var levels = Scratch(ref t_levels, cps.Count);
            var removed = Scratch(ref t_removed, cps.Count);
            var visual = t_visual ??= new List<int>();
            byte para = BidiAlgorithm.Resolve(cps, paragraphDirection, levels, removed, visual);

            // Group visually-consecutive codepoints that are also logically
            // consecutive at the same level: +1 steps for LTR runs, -1 for RTL.
            int k = 0;
            while (k < visual.Count)
            {
                int startCp = visual[k];
                byte level = levels[startCp];
                int step = (level & 1) == 0 ? 1 : -1;
                int j = k;
                while (j + 1 < visual.Count &&
                       levels[visual[j + 1]] == level &&
                       visual[j + 1] == visual[j] + step)
                {
                    j++;
                }

                int firstCp = step > 0 ? visual[k] : visual[j];
                int lastCp = step > 0 ? visual[j] : visual[k];
                output.Add(new Run
                {
                    Start = cpStarts[firstCp],
                    Length = cpStarts[lastCp + 1] - cpStarts[firstCp],
                    Level = level,
                });
                k = j + 1;
            }

            return para;
        }

        /// <summary>The codepoint at <paramref name="index"/>; see Unicode.Utf16.</summary>
        private static int Codepoint(ReadOnlySpan<char> text, int index) =>
            Utf16.Codepoint(text, index);

    }
}
