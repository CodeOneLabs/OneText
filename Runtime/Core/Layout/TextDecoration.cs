using System;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// What is drawn around a glyph rather than inside it: an outline, a drop
    /// shadow, a glow. Three independent parts in one value, because a span
    /// almost always wants more than one of them and carrying three optionals
    /// separately would mean three lookups per tile.
    ///
    /// This is data, exactly like <see cref="OneTextStyle"/> is data, and for
    /// the same reason: in TextMesh Pro every one of these lives on a material,
    /// so a heading with an outline and a body without it are two draw calls and
    /// a fallback glyph silently loses the effect. Here the parameters ride in
    /// vertex channels the mesh already carries (see the channel budget in
    /// <c>OneTextLabel.AddVert</c>), so a decorated and an undecorated label
    /// batch together and an emoji in the middle of an outlined word does not
    /// break the run.
    ///
    /// Every distance here is measured in <em>reaches</em>, where one reach is
    /// how far outside the ink the distance field still knows anything:
    /// <see cref="GlyphRasterizer.SpreadPixels"/> texels, which is also the
    /// atlas padding, which is why the numbers are capped at 1. Past that the
    /// field reads a flat "far outside" and an outline would simply stop, so
    /// the cap is the honest limit rather than an arbitrary one. Reaches are
    /// texels at the tile's own density, and tiles are rasterized at the size
    /// they are drawn at, so a decoration keeps its proportions when the font
    /// size changes, which a measurement in pixels would not.
    /// </summary>
    [Serializable]
    public struct TextDecoration : IEquatable<TextDecoration>
    {
        /// <summary>
        /// Which parts this value actually says something about. A part that is
        /// not set inherits, exactly as an unset <see cref="OneTextStyle"/>
        /// field does, and for the same reason: "black outline of width 0" and
        /// "no opinion about outlines" have to be tellable apart.
        /// </summary>
        [Flags]
        public enum Parts : byte
        {
            None = 0,
            Outline = 1 << 0,
            Shadow = 1 << 1,
            Glow = 1 << 2,

            /// <summary>
            /// The face itself, which is not drawn around the glyph but is the
            /// glyph. It is a part like the others so that it inherits like the
            /// others: a style that thickens the face and says nothing about
            /// outlines has to be layerable over one that does.
            /// </summary>
            Face = 1 << 3,
        }

        public Parts Set;

        /// <summary>
        /// The outline's colour. Its alpha is not carried: the channel budget
        /// below spends that byte on the outline's width instead, and a
        /// half-transparent outline over half-transparent text is a composite
        /// nobody has ever asked for. The label's own alpha still fades it, so
        /// a fading label fades its outlines with it.
        /// </summary>
        public Color32 OutlineColor;

        /// <summary>Outline thickness in reaches, 0..1.</summary>
        public float OutlineWidth;

        /// <summary>
        /// How far the outline's own edge is smeared, in reaches, 0..1.
        ///
        /// Zero is the hard edge the outline had before this existed, so a
        /// decoration written by anything that predates it draws exactly as it
        /// did. TextMesh Pro calls this Outline Softness and keeps it on the
        /// material.
        /// </summary>
        public float OutlineSoftness;

        /// <summary>TextMesh Pro calls this the underlay.</summary>
        public Color32 ShadowColor;

        /// <summary>Offset in reaches, x right and y up, each -1..1.</summary>
        public Vector2 ShadowOffset;

        /// <summary>
        /// How far the shadow's edge is smeared, in reaches, 0..1. This is the
        /// difference between a drop shadow and an offset photocopy, and it is
        /// the first thing anyone reaches for.
        /// </summary>
        public float ShadowSoftness;

        public Color32 GlowColor;

        /// <summary>
        /// How far the glow reaches past the ink, in reaches, 0..1. Kept under
        /// its original name so that every decoration serialized before the glow
        /// grew an inside still means what it meant.
        /// </summary>
        public float GlowRadius;

        /// <summary>
        /// How far the glow reaches <em>into</em> the ink from its edge, in
        /// reaches, 0..1. Zero draws the glow entirely outside, which is what it
        /// did before this existed.
        /// </summary>
        public float GlowInner;

        /// <summary>
        /// How much thicker the face is drawn than the font draws it, -1..1.
        ///
        /// The one parameter here that is not something added around the glyph:
        /// it moves the threshold the face itself is cut at. TextMesh Pro calls
        /// it Face Dilate and keeps it on the material, where the migration
        /// found it on both of a real project's two main fonts.
        /// </summary>
        public float FaceDilate;

        /// <summary>Nothing drawn around the glyph: what a plain label carries.</summary>
        public static TextDecoration None => default;

        /// <summary>True if no part is set, or every set part is invisible.</summary>
        public bool IsNone =>
            (!HasOutline || OutlineWidth <= 0f) &&
            (!HasShadow || ShadowColor.a == 0) &&
            (!HasGlow || GlowColor.a == 0 || (GlowRadius <= 0f && GlowInner <= 0f)) &&
            (!HasFace || FaceDilate == 0f);

        public bool HasOutline => (Set & Parts.Outline) != 0;
        public bool HasShadow => (Set & Parts.Shadow) != 0;
        public bool HasGlow => (Set & Parts.Glow) != 0;
        public bool HasFace => (Set & Parts.Face) != 0;

        /// <summary>
        /// What each tag draws when it is written bare. Chosen to look like
        /// something on the first try: a tag that needs three arguments before
        /// it does anything visible is a tag nobody uses twice.
        /// </summary>
        public static TextDecoration DefaultOutline => new TextDecoration
        {
            Set = Parts.Outline,
            OutlineColor = new Color32(0, 0, 0, 255),
            OutlineWidth = 0.4f,
        };

        public static TextDecoration DefaultShadow => new TextDecoration
        {
            Set = Parts.Shadow,
            ShadowColor = new Color32(0, 0, 0, 190),
            ShadowOffset = new Vector2(0.5f, -0.5f),
            ShadowSoftness = 0.35f,
        };

        public static TextDecoration DefaultGlow => new TextDecoration
        {
            Set = Parts.Glow,
            GlowColor = new Color32(255, 255, 255, 255),
            GlowRadius = 0.75f,
        };

        /// <summary>
        /// This decoration laid over <paramref name="under"/>: parts this one
        /// sets win whole, parts it does not set come through untouched.
        ///
        /// Whole parts rather than field by field, because half-merging is how
        /// you get a glow with one style's colour and another's radius, a
        /// result neither author asked for and neither can find in the markup.
        /// </summary>
        public TextDecoration Over(in TextDecoration under)
        {
            var result = under;
            if (HasOutline)
            {
                result.Set |= Parts.Outline;
                result.OutlineColor = OutlineColor;
                result.OutlineWidth = OutlineWidth;
                result.OutlineSoftness = OutlineSoftness;
            }
            if (HasShadow)
            {
                result.Set |= Parts.Shadow;
                result.ShadowColor = ShadowColor;
                result.ShadowOffset = ShadowOffset;
                result.ShadowSoftness = ShadowSoftness;
            }
            if (HasGlow)
            {
                result.Set |= Parts.Glow;
                result.GlowColor = GlowColor;
                result.GlowRadius = GlowRadius;
                result.GlowInner = GlowInner;
            }
            if (HasFace)
            {
                result.Set |= Parts.Face;
                result.FaceDilate = FaceDilate;
            }
            return result;
        }

        /// <summary>Clamps every parameter into the range the channels can carry.</summary>
        public TextDecoration Clamped()
        {
            var result = this;
            result.OutlineWidth = Mathf.Clamp01(OutlineWidth);
            result.ShadowOffset = new Vector2(
                Mathf.Clamp(ShadowOffset.x, -1f, 1f), Mathf.Clamp(ShadowOffset.y, -1f, 1f));
            result.ShadowSoftness = Mathf.Clamp01(ShadowSoftness);
            result.GlowRadius = Mathf.Clamp01(GlowRadius);
            result.GlowInner = Mathf.Clamp01(GlowInner);
            result.OutlineSoftness = Mathf.Clamp01(OutlineSoftness);
            result.FaceDilate = Mathf.Clamp(FaceDilate, -1f, 1f);
            return result;
        }

        /// <summary>
        /// Two 0..1 parameters in one byte, four bits each.
        ///
        /// The only place here that puts two things in one byte rather than two,
        /// and it is deliberately the two that can least hurt each other: the
        /// glow's inside and outside reach, same effect, same units, both soft
        /// by nature. An interpolator error that borrows across their boundary
        /// changes the glow's shape by a sixteenth, where the same error across
        /// a colour boundary would change a channel by 255. Sixteen steps of a
        /// blur is not a thing an eye finds.
        /// </summary>
        public static byte PackNibbles(float hi, float lo)
        {
            int h = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(hi) * 15f), 0, 15);
            int l = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(lo) * 15f), 0, 15);
            return (byte)((h << 4) | l);
        }

        // ------------------------------------------------------------- packing

        /// <summary>
        /// Two bytes into one float, as an exact integer below 65536.
        ///
        /// Two and not three. Three fits the mantissa, but an interpolator that
        /// returns the constant it was handed off by one unit in the last place
        /// (which at that magnitude is a whole unit) makes the low field
        /// borrow from the middle one, and a colour channel jumps by 255. At two
        /// bytes the same error is 1/256 of a unit and the shader's round()
        /// swallows it whole.
        /// </summary>
        public static float Pack(byte hi, byte lo) => hi * 256f + lo;

        /// <summary>A 0..1 parameter as a byte.</summary>
        public static byte Quantize(float value) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);

        /// <summary>
        /// A -<paramref name="range"/>..<paramref name="range"/> parameter as a
        /// byte, biased so that zero lands on 128 exactly: a shadow written
        /// with no vertical offset must have none, not half a texel of one.
        /// </summary>
        public static byte QuantizeSigned(float value, float range)
        {
            float unit = Mathf.Clamp(value / range, -1f, 1f);
            return (byte)Mathf.Clamp(128 + Mathf.RoundToInt(unit * 127f), 0, 255);
        }

        public bool Equals(TextDecoration other) =>
            Set == other.Set &&
            ColorEquals(OutlineColor, other.OutlineColor) &&
            OutlineWidth.Equals(other.OutlineWidth) &&
            OutlineSoftness.Equals(other.OutlineSoftness) &&
            ColorEquals(ShadowColor, other.ShadowColor) &&
            ShadowOffset.Equals(other.ShadowOffset) &&
            ShadowSoftness.Equals(other.ShadowSoftness) &&
            ColorEquals(GlowColor, other.GlowColor) &&
            GlowRadius.Equals(other.GlowRadius) &&
            GlowInner.Equals(other.GlowInner) &&
            FaceDilate.Equals(other.FaceDilate);

        private static bool ColorEquals(Color32 a, Color32 b) =>
            a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        public override bool Equals(object obj) => obj is TextDecoration other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Set;
                hash = hash * 397 ^ (OutlineColor.r << 24 | OutlineColor.g << 16 |
                                     OutlineColor.b << 8 | OutlineColor.a);
                hash = hash * 397 ^ OutlineWidth.GetHashCode();
                hash = hash * 397 ^ (ShadowColor.r << 24 | ShadowColor.g << 16 |
                                     ShadowColor.b << 8 | ShadowColor.a);
                hash = hash * 397 ^ ShadowOffset.GetHashCode();
                hash = hash * 397 ^ ShadowSoftness.GetHashCode();
                hash = hash * 397 ^ (GlowColor.r << 24 | GlowColor.g << 16 |
                                     GlowColor.b << 8 | GlowColor.a);
                hash = hash * 397 ^ GlowRadius.GetHashCode();
                hash = hash * 397 ^ GlowInner.GetHashCode();
                hash = hash * 397 ^ OutlineSoftness.GetHashCode();
                hash = hash * 397 ^ FaceDilate.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            if (Set == Parts.None) return "none";
            var parts = new System.Text.StringBuilder();
            if (HasOutline) parts.Append($"outline #{Hex(OutlineColor)} w={OutlineWidth:0.##} ");
            if (HasShadow)
                parts.Append($"shadow #{Hex(ShadowColor)} {ShadowOffset.x:0.##},{ShadowOffset.y:0.##} ");
            if (HasGlow) parts.Append($"glow #{Hex(GlowColor)} r={GlowRadius:0.##} ");
            return parts.ToString().TrimEnd();
        }

        private static string Hex(Color32 c) => $"{c.r:X2}{c.g:X2}{c.b:X2}{c.a:X2}";
    }

    /// <summary>A stretch of text sharing one <see cref="TextDecoration"/>.</summary>
    public readonly struct TextDecorationSpan
    {
        public readonly int Start, End;
        public readonly TextDecoration Decoration;

        public TextDecorationSpan(TextDecoration decoration, int start, int end)
        {
            Decoration = decoration;
            Start = start;
            End = end;
        }

        public bool Covers(int index) => index >= Start && index < End;

        public override string ToString() => $"[{Start}..{End}) {Decoration}";
    }
}
