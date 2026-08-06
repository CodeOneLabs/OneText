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

        /// <summary>Extra letter spacing in ems (<c>&lt;cspace&gt;</c>).</summary>
        public float LetterSpacingEm;

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

        public bool Equals(TextStyle other) =>
            SizeAbsolute.Equals(other.SizeAbsolute) &&
            SizeScale.Equals(other.SizeScale) &&
            ColorEquals(Color, other.Color) &&
            ColorEquals(MarkColor, other.MarkColor) &&
            BaselineShiftEm.Equals(other.BaselineShiftEm) &&
            LetterSpacingEm.Equals(other.LetterSpacingEm) &&
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
