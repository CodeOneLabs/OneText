using System.Collections.Generic;

namespace OneText.Unicode
{
    /// <summary>
    /// The line-breaking and spacing rules East Asian typography specs call
    /// for and UAX #14 leaves to tailoring: kinsoku (禁則処理 / 금칙 / 避头尾),
    /// punctuation compression (約物詰め / 标점挤压), and the quarter-em gap
    /// between Han and Latin.
    ///
    /// These are the loudest unserved complaints from the Korean, Japanese and
    /// Chinese communities. In TextMesh Pro kinsoku is two editable text files
    /// and buggy at the edges — a Japanese exclamation mark can break wrapping
    /// entirely — punctuation compression exists nowhere in the ecosystem, and
    /// the CJK–Latin gap is something people insert by hand.
    ///
    /// Everything here is spec-driven (JLREQ, KLREQ, CLREQ, and the UAX #14
    /// tailoring section), which is the ground this project is strongest on:
    /// these are not judgement calls, they are published rules that nobody
    /// implemented.
    /// </summary>
    public static class AsianTypography
    {
        /// <summary>
        /// How strictly to apply kinsoku. The specs themselves define more than
        /// one severity, and the difference is real: strict keeps small kana
        /// off a line start, normal does not, and Japanese publishers disagree
        /// about which is correct for body text.
        /// </summary>
        public enum Kinsoku
        {
            /// <summary>No tailoring; plain UAX #14.</summary>
            Off,

            /// <summary>Punctuation only — the rule nobody disputes.</summary>
            Loose,

            /// <summary>Punctuation plus small kana and the prolonged sound mark.</summary>
            Normal,

            /// <summary>Normal plus the characters only strict typesetting keeps back.</summary>
            Strict,
        }

        // ------------------------------------------------------------- classes
        //
        // Sorted, searched by binary search. Data rather than code: the point of
        // this milestone is that these are published lists, and a list is the
        // honest way to hold a published list.

        /// <summary>
        /// Characters forbidden at the start of a line — 行頭禁則. Closing
        /// brackets, the punctuation that follows a word, and the marks that
        /// belong to whatever came before them.
        /// </summary>
        private static readonly char[] NeverStartsLine =
        {
            '!', ')', ',', '.', ':', ';', '?', ']', '}', '¢', '°', '’', '”', '‰',
            '′', '″', '‱', '、', '。', '〉', '》', '」', '』', '】', '〕', '〗', '〙',
            '〛', '！', '％', '）', '，', '．', '：', '；', '？', '］', '｝', '｣',
            '｡', '､', '·', '・', '〜', 'ー', '々', '〻', '‐', '–', '—',
        };

        /// <summary>
        /// Characters forbidden at the end of a line — 行末禁則. Opening
        /// brackets and anything that introduces what follows it.
        /// </summary>
        private static readonly char[] NeverEndsLine =
        {
            '(', '[', '{', '£', '¥', '‘', '“', '〈', '《', '「', '『', '【', '〔',
            '〖', '〘', '〚', '（', '［', '｛', '｢', '＄', '￥', '￡', '＃', '＠',
        };

        /// <summary>Small kana: kept off a line start by normal and strict kinsoku.</summary>
        private static readonly char[] SmallKana =
        {
            'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ', 'っ', 'ゃ', 'ゅ', 'ょ', 'ゎ', 'ゕ', 'ゖ',
            'ァ', 'ィ', 'ゥ', 'ェ', 'ォ', 'ッ', 'ャ', 'ュ', 'ョ', 'ヮ', 'ヵ', 'ヶ',
        };

        /// <summary>Kept off a line start only under strict typesetting.</summary>
        private static readonly char[] StrictOnly = { '～', '〟', '〞', '‥', '…' };

        /// <summary>Full-width opening punctuation: blank on its left half.</summary>
        private static readonly char[] OpeningPunctuation =
        {
            '（', '［', '｛', '〈', '《', '「', '『', '【', '〔', '〖', '〘', '〚', '“', '‘',
        };

        /// <summary>Full-width closing punctuation: blank on its right half.</summary>
        private static readonly char[] ClosingPunctuation =
        {
            '）', '］', '｝', '〉', '》', '」', '』', '】', '〕', '〗', '〙', '〛', '”', '’',
            '、', '。', '，', '．',
        };

        static AsianTypography()
        {
            // Sorted once so every lookup below can binary search. Writing the
            // literals in code-point order by hand is the kind of thing that is
            // right until someone adds one in the wrong place.
            System.Array.Sort(NeverStartsLine);
            System.Array.Sort(NeverEndsLine);
            System.Array.Sort(SmallKana);
            System.Array.Sort(StrictOnly);
            System.Array.Sort(OpeningPunctuation);
            System.Array.Sort(ClosingPunctuation);
        }

        private static bool In(char[] sorted, char c) => System.Array.BinarySearch(sorted, c) >= 0;

        /// <summary>True if a line may not begin with this character.</summary>
        public static bool ForbiddenAtLineStart(char c, Kinsoku severity)
        {
            if (severity == Kinsoku.Off) return false;
            if (In(NeverStartsLine, c)) return true;
            if (severity == Kinsoku.Loose) return false;
            if (In(SmallKana, c)) return true;
            return severity == Kinsoku.Strict && In(StrictOnly, c);
        }

        /// <summary>True if a line may not end with this character.</summary>
        public static bool ForbiddenAtLineEnd(char c, Kinsoku severity) =>
            severity != Kinsoku.Off && In(NeverEndsLine, c);

        /// <summary>
        /// Applies kinsoku to a break-opportunity table, in place.
        ///
        /// Editing the table is the right shape for this: UAX #14 defines
        /// tailoring as exactly that, and it keeps the wrapper — which has
        /// enough to think about — from needing to know what a small kana is.
        ///
        /// This only ever <em>removes</em> opportunities, which is what makes
        /// the degenerate case safe. A run of "！！！！" in a chat message — the
        /// one from the bug tracker — has no legal break anywhere inside it, so
        /// every opportunity goes; the wrapper then falls through to its
        /// emergency grapheme-cluster break and the line is overfull rather
        /// than lost. That is the right trade — bad typography beats missing
        /// text. The emergency path does consult kinsoku, but only as a
        /// preference: given two cluster boundaries that both fit it takes the
        /// legal one, and given none it breaks at a forbidden character anyway,
        /// because the alternative is a line that never ends.
        /// </summary>
        public static void ApplyKinsoku(string text, LineBreaker.Opportunity[] opportunities,
            Kinsoku severity)
        {
            if (severity == Kinsoku.Off || string.IsNullOrEmpty(text)) return;

            int n = text.Length;
            for (int i = 1; i < n && i < opportunities.Length; i++)
            {
                if (opportunities[i] != LineBreaker.Opportunity.Allowed) continue;

                // A break at i puts text[i] at the start of the next line and
                // text[i-1] at the end of this one.
                bool bad = ForbiddenAtLineStart(text[i], severity) ||
                           ForbiddenAtLineEnd(text[i - 1], severity);
                if (bad) opportunities[i] = LineBreaker.Opportunity.None;
            }
        }

        // ---------------------------------------------------- Korean word wrap

        /// <summary>
        /// The UAX #14 Korean tailoring: spaces break, syllables do not.
        ///
        /// Korean is written with spaces between words, and Korean readers
        /// expect wrapping at those spaces — but UAX #14 gives Hangul syllables
        /// class ID, which breaks between any two of them. That is correct for
        /// Japanese and wrong for Korean, and it is a per-locale decision, not
        /// a global toggle: a Korean line in a Japanese UI still wants Korean
        /// wrapping.
        /// </summary>
        public static void ApplyKoreanWordWrap(string text, LineBreaker.Opportunity[] opportunities)
        {
            if (string.IsNullOrEmpty(text)) return;

            int n = text.Length;
            for (int i = 1; i < n && i < opportunities.Length; i++)
            {
                if (opportunities[i] != LineBreaker.Opportunity.Allowed) continue;
                // Only a break with Hangul on both sides is suppressed; a break
                // after a space, or between Hangul and anything else, stays.
                if (IsHangul(text[i]) && IsHangul(text[i - 1]))
                    opportunities[i] = LineBreaker.Opportunity.None;
            }
        }

        /// <summary>Hangul syllables, jamo, and the compatibility jamo block.</summary>
        public static bool IsHangul(char c) =>
            (c >= '가' && c <= '힣') ||   // syllables
            (c >= 'ᄀ' && c <= 'ᇿ') ||   // jamo
            (c >= '㄰' && c <= '㆏') ||   // compatibility jamo
            (c >= 'ꥠ' && c <= '꥿') ||   // jamo extended-A
            (c >= 'ힰ' && c <= '퟿');     // jamo extended-B

        // ------------------------------------------------ punctuation and gaps

        /// <summary>
        /// How much of its width a full-width punctuation mark gives up.
        ///
        /// This is 約物詰め, and nothing in the Unity ecosystem has it. A
        /// full-width opening bracket is a glyph in the left half of a square
        /// box; a closing bracket or a comma is a glyph in the right half. Set
        /// solid, two of them in a row leave a full em of white space in the
        /// middle of a sentence — which is what makes CJK text look merely
        /// rendered rather than typeset.
        ///
        /// This is the adjacency half of the rule, which is all that can be
        /// decided before the text is broken into lines. The line-edge half is
        /// <see cref="CompressionAtLineStart"/> and
        /// <see cref="CompressionAtLineEnd"/>; a mark subject to both gives up
        /// half an em once, not twice, because half an em is all the blank it
        /// has.
        /// </summary>
        public static float CompressionFor(char previous, char current, char next)
        {
            // A pair of adjacent marks has one em of blank between them, not
            // two — so exactly one of the pair gives up its half. The rule is
            // that the *second* one does, which keeps the decision local: a
            // mark looks only at what came before it, and no two marks can both
            // decide to shrink the same gap.
            if (In(OpeningPunctuation, current) &&
                (In(OpeningPunctuation, previous) || In(ClosingPunctuation, previous)))
                return 0.5f;

            // Two closing marks in a row: 」』 leaves an em between the glyphs.
            if (In(ClosingPunctuation, current) && In(ClosingPunctuation, previous))
                return 0.5f;

            return 0f;
        }

        /// <summary>
        /// How much a mark gives up because it is the first character of a
        /// line — 行頭の約物.
        ///
        /// An opening bracket at a line start puts its blank left half against
        /// the margin, where it reads as an indent nobody asked for: the line
        /// begins half an em short of every other line in the paragraph. The
        /// spec's answer is that the mark surrenders that half and the line
        /// starts flush.
        /// </summary>
        public static float CompressionAtLineStart(char c) => In(OpeningPunctuation, c) ? 0.5f : 0f;

        /// <summary>
        /// How much a mark gives up because it is the last character of a line
        /// — 行末の約物. A 。 or a closing bracket has its blank on the right,
        /// which at a line end is invisible white space between the text and
        /// the margin — and, worse, half an em of width the wrapper counted, so
        /// it can push the next word to the following line for nothing.
        /// </summary>
        public static float CompressionAtLineEnd(char c) => In(ClosingPunctuation, c) ? 0.5f : 0f;

        /// <summary>
        /// A cheap reject for the three blocks every compressible mark lives
        /// in: the curly quotes, CJK symbols and punctuation, and the
        /// full-width forms.
        ///
        /// Three comparisons instead of two binary searches, and it is the
        /// difference between the compression rules costing something and
        /// costing nothing on the characters they have no opinion about —
        /// which, in Japanese body text, is nineteen characters in twenty. Kana
        /// and Han sit in the gaps between these ranges and are rejected by the
        /// first test they meet. A test asserts this never rejects a character
        /// <see cref="IsCompressible"/> accepts, because the failure mode of a
        /// hand-written prefilter is that someone adds a mark to the lists and
        /// the fast path silently stops seeing it.
        /// </summary>
        public static bool MayCompress(char c) =>
            (c >= '‘' && c <= '”') || (c >= '、' && c <= '〟') || (c >= '！' && c <= '｠');

        /// <summary>True for full-width marks whose ink sits in the right half.</summary>
        public static bool IsOpeningPunctuation(char c) => In(OpeningPunctuation, c);

        /// <summary>True if the character is full-width punctuation this can compress.</summary>
        public static bool IsCompressible(char c) =>
            MayCompress(c) && (In(OpeningPunctuation, c) || In(ClosingPunctuation, c));

        /// <summary>
        /// The quarter-em gap every East Asian layout spec calls for between a
        /// Han or Kana run and a Latin one — 和欧文間のアキ.
        ///
        /// Without it, "日本語とEnglishの混在" reads as if the Latin word were
        /// jammed against its neighbours, because CJK glyphs fill their box
        /// edge to edge and Latin ones do not.
        /// </summary>
        public static bool WantsLatinGap(char left, char right) =>
            (IsIdeographic(left) && IsLatinish(right)) ||
            (IsLatinish(left) && IsIdeographic(right));

        /// <summary>Han, Kana and Hangul — everything set on a full-width em grid.</summary>
        public static bool IsIdeographic(char c) =>
            (c >= '぀' && c <= 'ヿ') ||   // kana
            (c >= '㐀' && c <= '䶿') ||   // CJK extension A
            (c >= '一' && c <= '鿿') ||   // CJK unified
            (c >= '豈' && c <= '﫿') ||   // compatibility ideographs
            IsHangul(c);

        /// <summary>Letters and digits that are not set on the em grid.</summary>
        public static bool IsLatinish(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= 'À' && c <= 'ɏ');     // Latin-1 supplement and extended

        /// <summary>Every character this module treats specially, for tooling.</summary>
        public static IEnumerable<char> TailoredCharacters()
        {
            foreach (char c in NeverStartsLine) yield return c;
            foreach (char c in NeverEndsLine) yield return c;
            foreach (char c in SmallKana) yield return c;
            foreach (char c in StrictOnly) yield return c;
        }
    }
}
