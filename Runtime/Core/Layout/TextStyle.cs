using System;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The style in force over a stretch of text: everything markup can change
    /// that the layout engine or the mesh builder has to know about.
    ///
    /// This is a value, not a reference, and it is compared by value; that is
    /// what makes "where does a run end" a cheap question. Two adjacent
    /// characters with equal styles stay in one run, and a run is the unit
    /// shaping, line metrics and the mesh all work in.
    ///
    /// Sizes are stored as a multiplier and an absolute, rather than resolved,
    /// because the label's own font size is not known to the parser and may
    /// change afterwards without the text changing.
    /// </summary>
    [Serializable]
    public struct TextStyle : IEquatable<TextStyle>
    {
        /// <summary>Absolute size in the label's units; 0 means "inherit".</summary>
        public float SizeAbsolute;

        /// <summary>Multiplier applied to the inherited size (1 = unchanged).</summary>
        public float SizeScale;

        /// <summary>Text colour; only used when <see cref="HasColor"/>.</summary>
        public Color32 Color;

        /// <summary>Highlight behind the text; only used when <see cref="HasMark"/>.</summary>
        public Color32 MarkColor;

        /// <summary>Baseline offset in ems, positive up (<c>&lt;voffset&gt;</c>).</summary>
        public float BaselineShiftEm;

        /// <summary>
        /// Extra letter spacing in ems (<c>&lt;cspace&gt;</c>); only used when
        /// <see cref="HasLetterSpacing"/>.
        ///
        /// Unlike <see cref="SizeAbsolute"/> this cannot carry its own absence
        /// in the value, which is why it has a flag and size does not. Size 0
        /// is nonsense and can safely mean "inherit"; spacing 0 is what an
        /// author asks for to pull a run back to the face's own metrics, and
        /// they have to be able to ask for it over a label, a style or a font
        /// that says otherwise.
        /// </summary>
        public float LetterSpacingEm;

        /// <summary>
        /// The advance every glyph in this run is given, in ems
        /// (<c>&lt;mspace&gt;</c>); only used when <see cref="HasMonoAdvance"/>.
        ///
        /// Not a correction like <see cref="LetterSpacingEm"/> but a
        /// replacement: a monospaced run is one where the author has decided
        /// what a cell is worth and every character gets that, which is what
        /// makes a score or a timer stop jittering as its digits change. The
        /// glyph is centred in the cell it was given, because a 1 sitting hard
        /// against the left of a digit-wide box is the thing people notice.
        /// </summary>
        public float MonoAdvanceEm;

        /// <summary>
        /// Alpha to draw this run at, 0–255; only used when
        /// <see cref="HasAlpha"/>.
        ///
        /// Its own field rather than the alpha channel of <see cref="Color"/>,
        /// because <c>&lt;alpha&gt;</c> is the tag that says nothing about hue.
        /// Folding it into the colour would mean either setting
        /// <see cref="HasColor"/> — which paints the run black, the colour an
        /// unset <see cref="Color"/> happens to be — or reading an alpha out of
        /// a colour nobody wrote. It is resolved over whatever the colour turns
        /// out to be, by <see cref="ResolveColor"/>.
        /// </summary>
        public byte AlphaOverride;

        /// <summary>Index into the label's named-style table, or -1.</summary>
        public int NamedStyle;

        /// <summary>Index into the label's font table (<c>&lt;font&gt;</c>), or -1.</summary>
        public int FontOverride;

        /// <summary>Flags, packed so the whole struct stays small and comparable.</summary>
        [Flags]
        public enum Flag : ushort
        {
            None = 0,
            Bold = 1 << 0,
            Italic = 1 << 1,
            Underline = 1 << 2,
            Strikethrough = 1 << 3,
            NoBreak = 1 << 4,
            HasColor = 1 << 5,
            HasMark = 1 << 6,
            /// <summary>Set on the placeholder character standing in for a sprite.</summary>
            Sprite = 1 << 7,
            HasLetterSpacing = 1 << 8,
            HasMonoAdvance = 1 << 9,
            HasAlpha = 1 << 10,
        }

        public Flag Flags;

        /// <summary>Sprite index for a <c>&lt;sprite&gt;</c> placeholder, or -1.</summary>
        public int Sprite;

        public bool Bold => (Flags & Flag.Bold) != 0;
        public bool Italic => (Flags & Flag.Italic) != 0;
        public bool Underline => (Flags & Flag.Underline) != 0;
        public bool Strikethrough => (Flags & Flag.Strikethrough) != 0;
        public bool NoBreak => (Flags & Flag.NoBreak) != 0;
        public bool HasColor => (Flags & Flag.HasColor) != 0;
        public bool HasMark => (Flags & Flag.HasMark) != 0;
        public bool IsSprite => (Flags & Flag.Sprite) != 0;
        public bool HasLetterSpacing => (Flags & Flag.HasLetterSpacing) != 0;
        public bool HasMonoAdvance => (Flags & Flag.HasMonoAdvance) != 0;
        public bool HasAlpha => (Flags & Flag.HasAlpha) != 0;

        /// <summary>Neutral style: inherit everything from the label.</summary>
        public static TextStyle Default => new TextStyle
        {
            SizeScale = 1f,
            NamedStyle = -1,
            FontOverride = -1,
            Sprite = -1,
        };

        /// <summary>This style's size, given the label's.</summary>
        public float ResolveSize(float inherited)
        {
            float size = SizeAbsolute > 0f ? SizeAbsolute : inherited;
            return SizeScale > 0f ? size * SizeScale : size;
        }

        /// <summary>
        /// The colour this run's quads are baked with: what markup said, or
        /// white when it said nothing, with <c>&lt;alpha&gt;</c> laid over
        /// either.
        ///
        /// White rather than the label's colour on purpose — the label's is
        /// multiplied in at emit, so tinting or fading a label never
        /// invalidates a baked quad. <c>&lt;alpha&gt;</c> rides along in the
        /// same channel and gets the same benefit, which is what lets a
        /// typewriter reveal built out of <c>&lt;alpha=#00&gt;</c> cost nothing
        /// but a re-parse.
        /// </summary>
        public Color32 ResolveColor()
        {
            var color = HasColor ? Color : new Color32(255, 255, 255, 255);
            if (HasAlpha) color.a = AlphaOverride;
            return color;
        }

        public bool Equals(TextStyle other) =>
            SizeAbsolute.Equals(other.SizeAbsolute) &&
            SizeScale.Equals(other.SizeScale) &&
            ColorEquals(Color, other.Color) &&
            ColorEquals(MarkColor, other.MarkColor) &&
            BaselineShiftEm.Equals(other.BaselineShiftEm) &&
            LetterSpacingEm.Equals(other.LetterSpacingEm) &&
            MonoAdvanceEm.Equals(other.MonoAdvanceEm) &&
            AlphaOverride == other.AlphaOverride &&
            NamedStyle == other.NamedStyle &&
            FontOverride == other.FontOverride &&
            Flags == other.Flags &&
            Sprite == other.Sprite;

        private static bool ColorEquals(Color32 a, Color32 b) =>
            a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        public override bool Equals(object obj) => obj is TextStyle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SizeAbsolute.GetHashCode();
                hash = hash * 397 ^ SizeScale.GetHashCode();
                hash = hash * 397 ^ (Color.r << 24 | Color.g << 16 | Color.b << 8 | Color.a);
                hash = hash * 397 ^ (int)Flags;
                hash = hash * 397 ^ AlphaOverride;
                hash = hash * 397 ^ MonoAdvanceEm.GetHashCode();
                hash = hash * 397 ^ NamedStyle;
                hash = hash * 397 ^ FontOverride;
                hash = hash * 397 ^ Sprite;
                return hash;
            }
        }

        public override string ToString()
        {
            var parts = new System.Text.StringBuilder();
            if (Bold) parts.Append("b ");
            if (Italic) parts.Append("i ");
            if (Underline) parts.Append("u ");
            if (Strikethrough) parts.Append("s ");
            if (NoBreak) parts.Append("nobr ");
            if (HasColor) parts.Append($"#{Color.r:X2}{Color.g:X2}{Color.b:X2}{Color.a:X2} ");
            if (SizeAbsolute > 0f) parts.Append($"size={SizeAbsolute} ");
            if (SizeScale != 1f) parts.Append($"x{SizeScale} ");
            if (HasLetterSpacing) parts.Append($"cspace={LetterSpacingEm} ");
            if (HasMonoAdvance) parts.Append($"mspace={MonoAdvanceEm} ");
            if (HasAlpha) parts.Append($"alpha=#{AlphaOverride:X2} ");
            if (NamedStyle >= 0) parts.Append($"style#{NamedStyle} ");
            if (Sprite >= 0) parts.Append($"sprite#{Sprite} ");
            return parts.Length == 0 ? "default" : parts.ToString().TrimEnd();
        }
    }

    /// <summary>A run of display text sharing one <see cref="TextStyle"/>.</summary>
    public readonly struct TextStyleSpan
    {
        public readonly int Start, Length;
        public readonly TextStyle Style;

        public TextStyleSpan(int start, int length, TextStyle style)
        {
            Start = start;
            Length = length;
            Style = style;
        }

        public int End => Start + Length;

        public override string ToString() => $"[{Start}..{End}) {Style}";
    }
}
