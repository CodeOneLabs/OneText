using System.Collections.Generic;

namespace OneText.Unicode
{
    /// <summary>
    /// UAX #29 text segmentation: extended grapheme cluster boundaries (GB1-GB999,
    /// including the Indic conjunct rule GB9c), word boundaries (WB1-WB999) and
    /// sentence boundaries (SB1-SB998).
    /// Grapheme clusters are what "one character" means for caret movement,
    /// selection and backspace; word boundaries drive double-click selection and
    /// word-granularity operations; sentence boundaries drive triple-click,
    /// sentence-at-a-time reveal in dialogue, and read-aloud chunking.
    /// Validated against the complete GraphemeBreakTest.txt / WordBreakTest.txt /
    /// SentenceBreakTest.txt.
    /// </summary>
    public static class TextSegmenter
    {
        [System.ThreadStatic] private static List<int> t_cps;
        [System.ThreadStatic] private static List<int> t_offsets;

        /// <summary>
        /// Fills <paramref name="boundaries"/> with every UTF-16 offset at which
        /// an extended grapheme cluster starts, plus <c>text.Length</c>.
        /// </summary>
        public static void GraphemeBoundaries(string text, List<int> boundaries)
        {
            boundaries.Clear();
            int n = text?.Length ?? 0;
            boundaries.Add(0);                 // GB1
            if (n == 0) return;

            Decode(text, out var cps, out var offsets);
            int m = cps.Count;
            for (int k = 1; k < m; k++)
                if (GraphemeBreaks(cps, k)) boundaries.Add(offsets[k]);
            boundaries.Add(n);                 // GB2
        }

        /// <summary>
        /// Fills <paramref name="boundaries"/> with every UTF-16 offset at which
        /// a word boundary falls, including 0 and <c>text.Length</c>.
        /// </summary>
        public static void WordBoundaries(string text, List<int> boundaries)
        {
            boundaries.Clear();
            int n = text?.Length ?? 0;
            boundaries.Add(0);                 // WB1
            if (n == 0) return;

            Decode(text, out var cps, out var offsets);
            int m = cps.Count;
            for (int k = 1; k < m; k++)
                if (WordBreaks(cps, k)) boundaries.Add(offsets[k]);
            boundaries.Add(n);                 // WB2
        }

        /// <summary>
        /// Fills <paramref name="boundaries"/> with every UTF-16 offset at which
        /// a sentence boundary falls, including 0 and <c>text.Length</c>.
        /// </summary>
        public static void SentenceBoundaries(string text, List<int> boundaries)
        {
            boundaries.Clear();
            int n = text?.Length ?? 0;
            boundaries.Add(0);                 // SB1
            if (n == 0) return;

            Decode(text, out var cps, out var offsets);
            int m = cps.Count;
            for (int k = 1; k < m; k++)
                if (SentenceBreaks(cps, k)) boundaries.Add(offsets[k]);
            boundaries.Add(n);                 // SB2
        }

        /// <summary>Number of extended grapheme clusters in the text.</summary>
        public static int CountGraphemes(string text)
        {
            var list = new List<int>();
            GraphemeBoundaries(text, list);
            return System.Math.Max(0, list.Count - 1);
        }

        private static void Decode(string text, out List<int> cps, out List<int> offsets)
        {
            cps = t_cps ??= new List<int>();
            offsets = t_offsets ??= new List<int>();
            cps.Clear();
            offsets.Clear();
            for (int i = 0; i < text.Length;)
            {
                cps.Add(char.ConvertToUtf32(text, i));
                offsets.Add(i);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }
        }

        // ---------------------------------------------------------------- graphemes

        private static bool GraphemeBreaks(List<int> cps, int k)
        {
            var b = BreakData.GetGraphemeClass(cps[k - 1]);
            var a = BreakData.GetGraphemeClass(cps[k]);

            // GB3, GB4, GB5
            if (b == GraphemeBreakClass.CR && a == GraphemeBreakClass.LF) return false;
            if (b == GraphemeBreakClass.Control || b == GraphemeBreakClass.CR ||
                b == GraphemeBreakClass.LF) return true;
            if (a == GraphemeBreakClass.Control || a == GraphemeBreakClass.CR ||
                a == GraphemeBreakClass.LF) return true;

            // GB6, GB7, GB8: Hangul syllables
            if (b == GraphemeBreakClass.L &&
                (a == GraphemeBreakClass.L || a == GraphemeBreakClass.V ||
                 a == GraphemeBreakClass.LV || a == GraphemeBreakClass.LVT)) return false;
            if ((b == GraphemeBreakClass.LV || b == GraphemeBreakClass.V) &&
                (a == GraphemeBreakClass.V || a == GraphemeBreakClass.T)) return false;
            if ((b == GraphemeBreakClass.LVT || b == GraphemeBreakClass.T) &&
                a == GraphemeBreakClass.T) return false;

            // GB9, GB9a, GB9b
            if (a == GraphemeBreakClass.Extend || a == GraphemeBreakClass.ZWJ) return false;
            if (a == GraphemeBreakClass.SpacingMark) return false;
            if (b == GraphemeBreakClass.Prepend) return false;

            // GB9c: Consonant [Extend Linker]* Linker [Extend Linker]* × Consonant
            if (BreakData.GetIncb(cps[k]) == IncbClass.Consonant && HasIncbLinkerRun(cps, k))
                return false;

            // GB11: pictographic Extend* ZWJ × pictographic
            if (b == GraphemeBreakClass.ZWJ && IsPictographic(cps[k]))
            {
                int j = k - 2;
                while (j >= 0 && BreakData.GetGraphemeClass(cps[j]) == GraphemeBreakClass.Extend) j--;
                if (j >= 0 && IsPictographic(cps[j])) return false;
            }

            // GB12, GB13: regional indicator pairs
            if (b == GraphemeBreakClass.RegionalIndicator && a == GraphemeBreakClass.RegionalIndicator)
            {
                int count = 0;
                for (int j = k - 1;
                     j >= 0 && BreakData.GetGraphemeClass(cps[j]) == GraphemeBreakClass.RegionalIndicator;
                     j--) count++;
                if ((count & 1) != 0) return false;
            }

            return true; // GB999
        }

        /// <summary>
        /// GB9c left context: a Consonant followed only by InCB Extend/Linker
        /// characters, at least one of which is a Linker.
        /// </summary>
        private static bool HasIncbLinkerRun(List<int> cps, int k)
        {
            bool sawLinker = false;
            for (int j = k - 1; j >= 0; j--)
            {
                var incb = BreakData.GetIncb(cps[j]);
                if (incb == IncbClass.Linker) { sawLinker = true; continue; }
                if (incb == IncbClass.Extend) continue;
                return incb == IncbClass.Consonant && sawLinker;
            }
            return false;
        }

        private static bool IsPictographic(int cp) =>
            (BreakData.GetFlags(cp) & CharFlags.Pictographic) != 0;

        // -------------------------------------------------------------------- words

        private static bool WordBreaks(List<int> cps, int k)
        {
            var rawBefore = BreakData.GetWordClass(cps[k - 1]);
            var rawAfter = BreakData.GetWordClass(cps[k]);

            // WB3, WB3a, WB3b
            if (rawBefore == WordBreakClass.CR && rawAfter == WordBreakClass.LF) return false;
            if (IsNewline(rawBefore) || IsNewline(rawAfter)) return true;

            // WB3c
            if (rawBefore == WordBreakClass.ZWJ && IsPictographic(cps[k])) return false;

            // WB3d
            if (rawBefore == WordBreakClass.WSegSpace && rawAfter == WordBreakClass.WSegSpace)
                return false;

            // WB4: Format/Extend/ZWJ are invisible to the remaining rules, and
            // never break from what precedes them.
            if (IsIgnorable(rawAfter)) return false;

            // Positions of the significant characters around the boundary.
            int b1 = PreviousSignificant(cps, k - 1);
            if (b1 < 0) return true;                       // only ignorables before: sot
            var b = BreakData.GetWordClass(cps[b1]);
            var a = rawAfter;
            int b2 = PreviousSignificant(cps, b1 - 1);
            var bb = b2 >= 0 ? BreakData.GetWordClass(cps[b2]) : WordBreakClass.Other;
            int a1 = NextSignificant(cps, k + 1);
            var aa = a1 >= 0 ? BreakData.GetWordClass(cps[a1]) : WordBreakClass.Other;

            // WB5
            if (IsLetter(b) && IsLetter(a)) return false;

            // WB6, WB7
            if (IsLetter(b) && (a == WordBreakClass.MidLetter || IsMidNumLetQ(a)) && IsLetter(aa))
                return false;
            if (IsLetter(a) && (b == WordBreakClass.MidLetter || IsMidNumLetQ(b)) && IsLetter(bb))
                return false;

            // WB7a, WB7b, WB7c
            if (b == WordBreakClass.HebrewLetter && a == WordBreakClass.SingleQuote) return false;
            if (b == WordBreakClass.HebrewLetter && a == WordBreakClass.DoubleQuote &&
                aa == WordBreakClass.HebrewLetter) return false;
            if (bb == WordBreakClass.HebrewLetter && b == WordBreakClass.DoubleQuote &&
                a == WordBreakClass.HebrewLetter) return false;

            // WB8, WB9, WB10
            if (b == WordBreakClass.Numeric && a == WordBreakClass.Numeric) return false;
            if (IsLetter(b) && a == WordBreakClass.Numeric) return false;
            if (b == WordBreakClass.Numeric && IsLetter(a)) return false;

            // WB11, WB12
            if (a == WordBreakClass.Numeric && (b == WordBreakClass.MidNum || IsMidNumLetQ(b)) &&
                bb == WordBreakClass.Numeric) return false;
            if (b == WordBreakClass.Numeric && (a == WordBreakClass.MidNum || IsMidNumLetQ(a)) &&
                aa == WordBreakClass.Numeric) return false;

            // WB13, WB13a, WB13b
            if (b == WordBreakClass.Katakana && a == WordBreakClass.Katakana) return false;
            if ((IsLetter(b) || b == WordBreakClass.Numeric || b == WordBreakClass.Katakana ||
                 b == WordBreakClass.ExtendNumLet) && a == WordBreakClass.ExtendNumLet) return false;
            if (b == WordBreakClass.ExtendNumLet &&
                (IsLetter(a) || a == WordBreakClass.Numeric || a == WordBreakClass.Katakana))
                return false;

            // WB15, WB16: regional indicator pairs, counting only significant chars
            if (b == WordBreakClass.RegionalIndicator && a == WordBreakClass.RegionalIndicator)
            {
                int count = 0;
                for (int j = b1; j >= 0; j = PreviousSignificant(cps, j - 1))
                {
                    if (BreakData.GetWordClass(cps[j]) != WordBreakClass.RegionalIndicator) break;
                    count++;
                }
                if ((count & 1) != 0) return false;
            }

            return true; // WB999
        }

        /// <summary>WB4: characters the word rules look through.</summary>
        private static bool IsIgnorable(WordBreakClass c) =>
            c == WordBreakClass.Extend || c == WordBreakClass.Format || c == WordBreakClass.ZWJ;

        private static bool IsNewline(WordBreakClass c) =>
            c == WordBreakClass.Newline || c == WordBreakClass.CR || c == WordBreakClass.LF;

        /// <summary>The AHLetter macro (ALetter | Hebrew_Letter).</summary>
        private static bool IsLetter(WordBreakClass c) =>
            c == WordBreakClass.ALetter || c == WordBreakClass.HebrewLetter;

        /// <summary>The MidNumLetQ macro (MidNumLet | Single_Quote).</summary>
        private static bool IsMidNumLetQ(WordBreakClass c) =>
            c == WordBreakClass.MidNumLet || c == WordBreakClass.SingleQuote;

        /// <summary>
        /// Index of the last codepoint at or before <paramref name="from"/> that
        /// WB4 does not skip; -1 if there is none. Newlines stop the skip, since
        /// WB4 does not apply after sot, CR, LF or Newline.
        /// </summary>
        private static int PreviousSignificant(List<int> cps, int from)
        {
            for (int j = from; j >= 0; j--)
            {
                var c = BreakData.GetWordClass(cps[j]);
                if (!IsIgnorable(c)) return j;
                if (j == 0) return 0;
                var previous = BreakData.GetWordClass(cps[j - 1]);
                if (IsNewline(previous)) return j;   // the ignorable starts a new word
            }
            return -1;
        }

        private static int NextSignificant(List<int> cps, int from)
        {
            for (int j = from; j < cps.Count; j++)
                if (!IsIgnorable(BreakData.GetWordClass(cps[j]))) return j;
            return -1;
        }

        // ---------------------------------------------------------------- sentences

        private static bool SentenceBreaks(List<int> cps, int k)
        {
            var rawBefore = BreakData.GetSentenceClass(cps[k - 1]);
            var rawAfter = BreakData.GetSentenceClass(cps[k]);

            // SB3, SB4. SB4 outranks everything below, which is why the optional
            // ParaSep at the end of SB11 never has to be matched here: a boundary
            // whose left neighbour is Sep/CR/LF has already been decided.
            if (rawBefore == SentenceBreakClass.CR && rawAfter == SentenceBreakClass.LF)
                return false;
            if (IsParagraphSeparator(rawBefore)) return true;

            // SB5: Extend/Format attach to the preceding character and are
            // invisible to every rule after this one.
            if (IsIgnorable(rawAfter)) return false;

            int b1 = PreviousSignificantSentence(cps, k - 1);
            // Only ignorables to the left (sot, or just past a ParaSep): they
            // stand for themselves, match no rule, and fall through to SB998.
            if (b1 < 0) return false;
            var b = BreakData.GetSentenceClass(cps[b1]);
            var a = rawAfter;
            int b2 = PreviousSignificantSentence(cps, b1 - 1);
            var bb = b2 >= 0 ? BreakData.GetSentenceClass(cps[b2]) : SentenceBreakClass.Other;

            // SB6: a period before a digit is a decimal point, not a full stop.
            if (b == SentenceBreakClass.ATerm && a == SentenceBreakClass.Numeric) return false;

            // SB7: U.S. (an initial's period followed by another capital).
            if (b == SentenceBreakClass.ATerm && a == SentenceBreakClass.Upper &&
                (bb == SentenceBreakClass.Upper || bb == SentenceBreakClass.Lower)) return false;

            // The terminator that opened the Close* Sp* run to the left, if any;
            // SB8 through SB11 all hang off it.
            var term = TerminatorBefore(cps, b1, skipSpaces: true);

            // SB8: ATerm Close* Sp* × (¬(OLetter|Upper|Lower|Sep|CR|LF|STerm|ATerm))* Lower
            if (term == SentenceBreakClass.ATerm && LowerAheadOfSB8(cps, k)) return false;

            // SB8a: quotes and continuations still inside the same sentence.
            if (term != SentenceBreakClass.Other &&
                (a == SentenceBreakClass.SContinue || a == SentenceBreakClass.STerm ||
                 a == SentenceBreakClass.ATerm)) return false;

            // SB9: the terminator's own trailing punctuation and space.
            if (TerminatorBefore(cps, b1, skipSpaces: false) != SentenceBreakClass.Other &&
                (a == SentenceBreakClass.Close || a == SentenceBreakClass.Sp ||
                 IsParagraphSeparator(a))) return false;

            // SB10, SB11: the sentence ends after the terminator's spaces, not
            // between them, so a run of spaces stays with the sentence it closes.
            if (term != SentenceBreakClass.Other)
            {
                if (a == SentenceBreakClass.Sp || IsParagraphSeparator(a)) return false;
                return true;
            }

            return false; // SB998
        }

        /// <summary>SB5: characters the sentence rules look through.</summary>
        private static bool IsIgnorable(SentenceBreakClass c) =>
            c == SentenceBreakClass.Extend || c == SentenceBreakClass.Format;

        /// <summary>The ParaSep macro (Sep | CR | LF).</summary>
        private static bool IsParagraphSeparator(SentenceBreakClass c) =>
            c == SentenceBreakClass.Sep || c == SentenceBreakClass.CR ||
            c == SentenceBreakClass.LF;

        /// <summary>
        /// Left context shared by SB8-SB11: walks back over <c>Sp*</c> (SB9 alone
        /// does not allow the spaces) and then <c>Close*</c>, and reports the
        /// ATerm or STerm that opened the run, or Other if there is none.
        /// </summary>
        private static SentenceBreakClass TerminatorBefore(List<int> cps, int from, bool skipSpaces)
        {
            int j = from;
            if (skipSpaces)
                while (j >= 0 && BreakData.GetSentenceClass(cps[j]) == SentenceBreakClass.Sp)
                    j = PreviousSignificantSentence(cps, j - 1);
            while (j >= 0 && BreakData.GetSentenceClass(cps[j]) == SentenceBreakClass.Close)
                j = PreviousSignificantSentence(cps, j - 1);
            if (j < 0) return SentenceBreakClass.Other;

            var c = BreakData.GetSentenceClass(cps[j]);
            return c == SentenceBreakClass.ATerm || c == SentenceBreakClass.STerm
                ? c : SentenceBreakClass.Other;
        }

        /// <summary>
        /// SB8's right context: <c>(¬(OLetter|Upper|Lower|Sep|CR|LF|STerm|ATerm))* Lower</c>,
        /// the rule that keeps "etc. and so on" one sentence while "Wait. Then
        /// go." is two. The star is unbounded, so this is the one sentence rule
        /// that cannot be decided from a fixed window: an arbitrary run of
        /// digits, spaces and punctuation may sit between the period and the
        /// lowercase letter that settles it, and a windowed approximation breaks
        /// exactly there. The scan is cheap in practice because any letter or
        /// terminator ends it, which in real prose is the very next word.
        /// </summary>
        private static bool LowerAheadOfSB8(List<int> cps, int from)
        {
            for (int j = from; j < cps.Count; j++)
            {
                var c = BreakData.GetSentenceClass(cps[j]);
                if (c == SentenceBreakClass.Lower) return true;
                if (c == SentenceBreakClass.OLetter || c == SentenceBreakClass.Upper ||
                    c == SentenceBreakClass.STerm || c == SentenceBreakClass.ATerm ||
                    IsParagraphSeparator(c)) return false;
            }
            return false;
        }

        /// <summary>
        /// Index of the last codepoint at or before <paramref name="from"/> that
        /// SB5 does not skip; -1 if there is none. A ParaSep stops the skip: SB4
        /// has already broken there, so the ignorables after it open a new
        /// sentence rather than attaching to the separator.
        /// </summary>
        private static int PreviousSignificantSentence(List<int> cps, int from)
        {
            for (int j = from; j >= 0; j--)
            {
                var c = BreakData.GetSentenceClass(cps[j]);
                if (!IsIgnorable(c)) return j;
                if (j == 0) return 0;
                if (IsParagraphSeparator(BreakData.GetSentenceClass(cps[j - 1]))) return j;
            }
            return -1;
        }
    }
}
