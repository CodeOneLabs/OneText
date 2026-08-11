using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>Horizontal placement of each line inside the layout box.</summary>
    public enum TextAlignment
    {
        /// <summary>Aligned to the paragraph's own start edge (left for LTR, right for RTL).</summary>
        Start,
        Left,
        Center,
        Right,
        /// <summary>Aligned to the paragraph's own end edge.</summary>
        End,
        /// <summary>Stretched to the full width, except on a paragraph's last line.</summary>
        Justified,
    }

    /// <summary>Vertical placement of the text block inside its box.</summary>
    public enum VerticalAlignment
    {
        Top,
        Middle,
        Bottom,
    }

    /// <summary>
    /// Which way the text flows, and which way its lines stack.
    ///
    /// The two axes are named after the flow rather than after the screen,
    /// because that is the only naming that survives being rotated: the
    /// <em>inline</em> axis is the one characters advance along, and the
    /// <em>block</em> axis is the one lines stack along. Horizontally they are
    /// x and y; vertically they are y and −x, and every rule in the engine
    /// (wrapping, kinsoku, alignment, ruby placement) is written about the
    /// axes and not about the screen.
    /// </summary>
    public enum TextWritingMode
    {
        /// <summary>Left to right, lines top to bottom. The default, and unchanged by any of this.</summary>
        Horizontal,

        /// <summary>
        /// 縦書き: characters top to bottom, columns right to left. Han, kana
        /// and Hangul stand upright; Latin and everything else Unicode calls
        /// rotated turns ninety degrees clockwise (UAX #50).
        /// </summary>
        VerticalRightToLeft,
    }

    /// <summary>Whether lines wrap at the layout width.</summary>
    public enum TextWrap
    {
        /// <summary>Only mandatory breaks (newlines) start a new line.</summary>
        NoWrap,
        Wrap,
    }

    /// <summary>What happens to lines that do not fit the layout height.</summary>
    public enum TextOverflow
    {
        /// <summary>Keep every line; the block may exceed the box.</summary>
        Overflow,
        /// <summary>Drop lines past the bottom of the box.</summary>
        Truncate,
        /// <summary>Drop them, and mark the last visible line with an ellipsis.</summary>
        Ellipsis,
    }

    /// <summary>Everything the layout engine needs besides the text itself.</summary>
    public struct TextLayoutSettings
    {
        /// <summary>Fonts to use, in fallback order.</summary>
        public FontStack Fonts;

        /// <summary>Em size in render units.</summary>
        public float FontSize;

        /// <summary>Layout width; 0 or less means unbounded (no wrapping).</summary>
        public float MaxWidth;

        /// <summary>Layout height; 0 or less means unbounded.</summary>
        public float MaxHeight;

        public TextAlignment Alignment;
        public TextWrap Wrap;
        public TextOverflow Overflow;

        /// <summary>
        /// Horizontal (the default) or 縦書き.
        ///
        /// <see cref="MaxWidth"/> and <see cref="MaxHeight"/> keep meaning
        /// width and height of the box; the caller has a rectangle, not a
        /// pair of abstract axes. What changes is which one wraps: vertically
        /// a column ends at the box's <em>height</em>, and the box's width is
        /// the budget <see cref="TextOverflow"/> spends.
        /// </summary>
        public TextWritingMode WritingMode;

        /// <summary>Multiplier on the font's natural line height; 0 means 1.</summary>
        public float LineSpacing;

        /// <summary>
        /// Letter spacing in ems for text that has no opinion of its own, and
        /// whether the caller is expressing one at all.
        ///
        /// This is the whole-label knob, and it is a pair rather than a single
        /// number for the same reason <see cref="TextStyle.LetterSpacingEm"/>
        /// has a flag: 0 is a value an author asks for, not an absence. A
        /// label whose style asset sets spacing to 0 over a font that ships
        /// wide means 0, and without the flag the font would win.
        ///
        /// Precedence, tightest scope first: <c>&lt;cspace&gt;</c> markup, a
        /// named style, this, then the face's own
        /// <c>LetterSpacingEm</c> from the font stack.
        /// </summary>
        public float LetterSpacingEm;

        /// <inheritdoc cref="LetterSpacingEm"/>
        public bool HasLetterSpacing;

        /// <summary>
        /// Paragraph direction: 0 = LTR, 1 = RTL,
        /// <see cref="Unicode.BidiAlgorithm.AutoDirection"/> = from content (default).
        /// </summary>
        public byte BaseDirection;

        /// <summary>
        /// Style spans over the text, from <see cref="RichTextParser"/>. Null
        /// means one default style throughout, which is the plain-text path and
        /// costs nothing extra.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<TextStyleSpan> Spans;

        /// <summary>
        /// Per-paragraph alignment overrides from <c>&lt;align&gt;</c>, or null.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<(int Start, TextAlignment Alignment)> Alignments;

        /// <summary>
        /// Ruby annotations over ranges of the text, from
        /// <c>&lt;ruby=…&gt;</c>. Null or empty is the ordinary path and costs
        /// nothing.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<TextRubySpan> Rubies;

        /// <summary>
        /// Ruby size as a fraction of the size of the text it annotates; 0 or
        /// less means <see cref="RubyPlacement.DefaultScale"/>, the half the
        /// spec asks for.
        /// </summary>
        public float RubyScale;

        /// <summary>
        /// Resolves a <c>&lt;font=…&gt;</c> index to a font, or null. Markup
        /// names a font; only the frontend knows what that name means.
        /// </summary>
        public System.Func<int, FontData> ResolveFontOverride;

        /// <summary>
        /// Applies a named style (<c>&lt;style=…&gt;</c>) on top of the style
        /// markup built, before the run is measured. Null leaves them inert.
        /// </summary>
        public System.Func<int, TextStyle, TextStyle> ResolveNamedStyle;

        /// <summary>
        /// Width over height of an inline sprite, by index. A sprite occupies a
        /// character's worth of line and how wide that is depends on its shape,
        /// so layout has to ask before anything is drawn. Null means sprites
        /// take a square em.
        /// </summary>
        public System.Func<int, float> ResolveSpriteAspect;

        /// <summary>
        /// BCP 47 language tag for this text: "ja", "ko", "zh-Hans".
        ///
        /// It does three things, and each of them is a complaint from a
        /// community that has been asking. It reaches HarfBuzz so `locl`
        /// selects regional forms; it keys the fallback stack, so Han
        /// unification stops being a lottery and a Japanese label resolves 直
        /// from the Japanese font even when a Chinese font sits higher in the
        /// chain; and it picks the line-breaking tailorings below, because
        /// Korean word wrap is a per-locale rule and not a global toggle.
        /// </summary>
        public string Language;

        /// <summary>Kinsoku severity for this text. Off by default.</summary>
        public Unicode.AsianTypography.Kinsoku Kinsoku;

        /// <summary>
        /// Apply the UAX #14 Korean tailoring: break at spaces, not between
        /// syllables. Set from the locale rather than globally; a Korean line
        /// in a Japanese UI still wants Korean wrapping.
        /// </summary>
        public bool KoreanWordWrap;

        /// <summary>
        /// Insert the quarter-em gap East Asian specs call for between Han/Kana
        /// and Latin runs.
        /// </summary>
        public bool CjkLatinSpacing;

        /// <summary>Compress full-width punctuation (約物詰め).</summary>
        public bool PunctuationCompression;

        public static TextLayoutSettings Default(FontStack fonts, float fontSize) =>
            new TextLayoutSettings
            {
                Fonts = fonts,
                FontSize = fontSize,
                LineSpacing = 1f,
                Wrap = TextWrap.Wrap,
                BaseDirection = Unicode.BidiAlgorithm.AutoDirection,
            };
    }

    /// <summary>
    /// A maximal stretch of glyphs sharing one font, one bidi level and one
    /// style, already placed on its line.
    ///
    /// Style joined font and level as a reason to end a run when markup
    /// arrived: a run is the unit that shapes together, measures together and
    /// draws with one colour, and none of those survive a size change halfway
    /// through.
    /// </summary>
    public struct TextRun
    {
        public FontData Font;

        /// <summary>Range into <see cref="TextLayoutResult.Glyphs"/>.</summary>
        public int GlyphStart, GlyphCount;

        /// <summary>Source text range, in UTF-16 code units.</summary>
        public int TextStart, TextLength;

        /// <summary>
        /// Start of the run along the inline axis, in render units from the
        /// block's start edge: its left edge horizontally, the top of its
        /// column vertically.
        /// </summary>
        public float X;

        /// <summary>
        /// The run's baseline along the block axis, in render units from the
        /// block's start edge. Horizontally that is downward from the top;
        /// vertically it is leftward from the right, and the "baseline" is the
        /// centre line of the column.
        /// </summary>
        public float Baseline;

        /// <summary>Advance along the inline axis in render units.</summary>
        public float Width;

        /// <summary>Resolved bidi embedding level; odd means right-to-left.</summary>
        public byte Level;

        /// <summary>
        /// Em size this run was shaped and measured at: the label's size with
        /// the run's style applied, not the label's size. Everything that
        /// converts font units to render units for this run uses it.
        /// </summary>
        public float FontSize;

        /// <summary>The style in force over the run, as markup left it.</summary>
        public TextStyle Style;

        /// <summary>Baseline offset in render units, positive up.</summary>
        public float BaselineShift;

        /// <summary>
        /// Markup asked this run for bold and the family had none to give, so
        /// its weight is being faked by thickening the face rather than by a
        /// different set of outlines.
        ///
        /// Recorded on the run rather than worked out at emit because only the
        /// layout pass has the font stack to ask, and because it is constant
        /// over a run by construction — a change of font or of style ends an
        /// item, and this can only change with one of those.
        ///
        /// It is a last resort and it is meant to look like one. A dilated
        /// regular is not a designed bold: it fattens every stroke by the same
        /// amount where a type designer would have thickened the stems and left
        /// the counters alone, which is why the counters are the first thing to
        /// close up, and why this is worst on the scripts with the least room
        /// in them. Hangul and Han lose legibility here before Latin does.
        /// </summary>
        public bool SyntheticBold;

        /// <summary>
        /// True if this run is a ruby annotation rather than text on the line.
        ///
        /// A ruby run is an ordinary run everywhere that draws: same glyphs,
        /// same atlas, same style, and clusters pointing at the base it
        /// annotates, so reveal, effects and decorations follow the base with
        /// nothing added. It is not ordinary anywhere that <em>measures</em>:
        /// it contributes no advance to the line, and a caret must never land
        /// in it.
        /// </summary>
        public bool IsRuby;

        /// <summary>
        /// True if this run's glyphs are turned ninety degrees clockwise:
        /// a Latin, Cyrillic or Greek stretch inside a vertical column, per
        /// UAX #50.
        ///
        /// A rotated run is shaped horizontally, because that is what it is:
        /// a line of Latin, with its kerning and its ligatures, laid down the
        /// column instead of across the page. Only the drawing is rotated,
        /// which is why every measurement above still reads
        /// <see cref="Width"/> and every glyph still carries an
        /// <see cref="ShapedGlyph.XAdvance"/>.
        /// </summary>
        public bool Rotated;

        /// <summary>
        /// How far a rotated run's own baseline sits from the centre line of
        /// its column, as an offset to add to <see cref="Baseline"/> along the
        /// block axis. Zero for everything else.
        ///
        /// Upright text needs no such offset: its baseline <em>is</em> the
        /// column's centre line, which is where HarfBuzz's vertical origin puts
        /// it. A rotated run keeps its horizontal baseline, and a horizontal
        /// baseline has unequal ink above and below it; laying that baseline on
        /// the centre line would push a stretch of Latin visibly to one side of
        /// the column it is set in. Half of the line box, moved back.
        /// </summary>
        public float CrossAxisBaselineOffset =>
            Rotated && Font != null && Font.UnitsPerEm > 0
                ? (Font.Ascender + Font.Descender) * (FontSize / Font.UnitsPerEm) * 0.5f
                : 0f;

        public bool IsRightToLeft => (Level & 1) != 0;
    }

    /// <summary>One laid-out line: a range of runs in visual (left-to-right) order.</summary>
    public struct TextLine
    {
        /// <summary>Range into <see cref="TextLayoutResult.Runs"/>, visually ordered.</summary>
        public int RunStart, RunCount;

        /// <summary>Source text range, in UTF-16 code units.</summary>
        public int TextStart, TextLength;

        /// <summary>
        /// Extent along the inline axis excluding trailing whitespace, in
        /// render units: the line's width, or the column's length.
        /// </summary>
        public float Width;

        /// <summary>
        /// The line's baseline along the block axis, in render units from the
        /// block's start edge. Vertically, the centre line of the column.
        /// </summary>
        public float Baseline;

        /// <summary>
        /// Line box metrics in render units. <see cref="Ascent"/> is the
        /// extent on the far side of the baseline from the block direction
        /// (above it horizontally, to its right in a right-to-left column), and
        /// <see cref="Height"/> is what the block cursor advances by.
        /// </summary>
        public float Ascent, Descent, Height;

        /// <summary>Embedding level of the paragraph this line belongs to.</summary>
        public byte ParagraphLevel;

        /// <summary>True if this is the last line of its paragraph (justification skips it).</summary>
        public bool IsParagraphEnd;

        public bool IsRightToLeft => (ParagraphLevel & 1) != 0;
    }

    /// <summary>
    /// The output of <see cref="TextLayoutEngine"/>: positioned glyphs grouped
    /// into runs and lines. Frontends turn this into meshes; the core never
    /// touches a UI framework.
    /// </summary>
    public sealed class TextLayoutResult
    {
        /// <summary>Shaped glyphs, in run order. Metrics are in font design units.</summary>
        public readonly List<ShapedGlyph> Glyphs = new List<ShapedGlyph>();

        public readonly List<TextRun> Runs = new List<TextRun>();

        public readonly List<TextLine> Lines = new List<TextLine>();

        /// <summary>
        /// Width of the laid-out block on screen, in render units: the
        /// widest line horizontally, the stack of columns vertically.
        /// </summary>
        public float Width;

        /// <summary>
        /// Height of the laid-out block on screen, in render units: the
        /// stack of lines horizontally, the longest column vertically.
        /// </summary>
        public float Height;

        /// <summary>
        /// Which way this result flows. Everything in <see cref="Runs"/> and
        /// <see cref="Lines"/> is in the inline/block axes it names, so
        /// nothing that reads a position can be right without it.
        /// </summary>
        public TextWritingMode WritingMode;

        /// <summary>True if this result was laid out in a vertical writing mode.</summary>
        public bool IsVertical => WritingMode == TextWritingMode.VerticalRightToLeft;

        /// <summary>Extent along the inline axis: the longest line or column.</summary>
        public float InlineExtent => IsVertical ? Height : Width;

        /// <summary>Extent along the block axis: what the lines or columns stack to.</summary>
        public float BlockExtent => IsVertical ? Width : Height;

        /// <summary>Em size this result was laid out at, in render units.</summary>
        public float FontSize;

        /// <summary>True if <see cref="TextOverflow"/> dropped any line.</summary>
        public bool Truncated;

        /// <summary>
        /// Start index of every grapheme cluster in the laid-out text, in
        /// logical order, plus a terminator at the text's length.
        ///
        /// The engine publishes this because only the engine knows it, and
        /// everything above needs it: "one character at a time" is not a thing
        /// in shaped text. An Arabic ligature is two characters in one glyph, a
        /// Hangul syllable is three, and a flag emoji is four. Revealing half
        /// of any of them is nonsense. A typewriter counts these, not chars.
        /// </summary>
        public readonly List<int> GraphemeStarts = new List<int>();

        /// <summary>Number of grapheme clusters in the laid-out text.</summary>
        public int GraphemeCount => Mathf.Max(0, GraphemeStarts.Count - 1);

        /// <summary>
        /// The grapheme cluster a text index falls in, clamped into range.
        /// Binary search: the table is sorted by construction.
        /// </summary>
        public int GraphemeAt(int textIndex)
        {
            if (GraphemeStarts.Count == 0) return 0;
            int lo = 0, hi = GraphemeStarts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (GraphemeStarts[mid] <= textIndex) lo = mid;
                else hi = mid - 1;
            }
            return Mathf.Clamp(lo, 0, Mathf.Max(0, GraphemeStarts.Count - 2));
        }

        public void Clear()
        {
            Glyphs.Clear();
            Runs.Clear();
            Lines.Clear();
            GraphemeStarts.Clear();
            Width = 0f;
            Height = 0f;
            WritingMode = TextWritingMode.Horizontal;
            FontSize = 0f;
            Truncated = false;
        }
    }
}
