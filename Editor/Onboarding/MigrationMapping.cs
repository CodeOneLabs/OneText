using System.Collections.Generic;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// Every value conversion the migration performs, as arithmetic on
    /// primitives.
    ///
    /// This file is the part of leaving TextMesh Pro that is actually decidable,
    /// pulled out where it can be read and tested on a machine with no TMP
    /// installed. Nothing here touches a component, an asset or a scene: an int
    /// goes in and an enum comes out, plus a flag saying whether the answer is
    /// exact or the nearest honest thing.
    ///
    /// "Approximated" is not a hedge. TMP has six vertical alignments and
    /// OneText has three, and the three missing ones (baseline, midline,
    /// capline) are all real distinctions on a line of text; the point of the
    /// flag is that the report can name them rather than letting a project
    /// discover the difference in a screenshot.
    /// </summary>
    public static class MigrationMapping
    {
        // ------------------------------------------------------ TMP alignment

        // The bitfield itself lives in the runtime assembly, next to the
        // TextAlignmentOptions the parity aliases needed declaring anyway, and
        // these are the names the migration and its tests already use. One set
        // of numbers, because a migration that disagreed with the runtime alias
        // about what 0x1000 means would be two migrations.
        public const int TmpLeft = TmpCompat.Left;
        public const int TmpCenter = TmpCompat.Center;
        public const int TmpRight = TmpCompat.Right;
        public const int TmpJustified = TmpCompat.Justified;
        public const int TmpFlush = TmpCompat.Flush;
        public const int TmpGeometryCenter = TmpCompat.GeometryCenter;

        public const int TmpTop = TmpCompat.Top;
        public const int TmpMiddle = TmpCompat.Middle;
        public const int TmpBottom = TmpCompat.Bottom;
        public const int TmpBaseline = TmpCompat.Baseline;
        public const int TmpMidline = TmpCompat.Midline;
        public const int TmpCapline = TmpCompat.Capline;

        /// <summary>
        /// Splits a <c>TextAlignmentOptions</c> value into the pair OneText
        /// keeps it in. <paramref name="approximated"/> says a distinction was
        /// lost, and <paramref name="what"/> names it for the report — which is
        /// the whole difference between this and assigning the runtime alias,
        /// where there is nobody to tell.
        /// </summary>
        public static void FromTmpAlignment(int alignment,
            out TextAlignment horizontal, out VerticalAlignment vertical,
            out bool approximated, out string what) =>
            TmpCompat.SplitAlignment(alignment, out horizontal, out vertical,
                out approximated, out what);

        // --------------------------------------------------- TMP line spacing

        /// <summary>
        /// TMP's line spacing is an offset in font-size-relative units where
        /// zero means "the font's own line height"; OneText's is a multiplier
        /// of that same height where one means the same thing. Ten percent is
        /// therefore <c>10</c> there and <c>1.1</c> here.
        ///
        /// Approximate on purpose: TMP applies the offset after its own
        /// ascender/descender arithmetic, so identical numbers do not
        /// guarantee identical pixels, only identical intent.
        /// </summary>
        public static float LineSpacingFromTmp(float offset) =>
            TmpCompat.LineSpacingFromTmp(offset);

        // ------------------------------------------------------ TMP overflow

        public const int TmpOverflowOverflow = 0;
        public const int TmpOverflowEllipsis = 1;
        public const int TmpOverflowMasking = 2;
        public const int TmpOverflowTruncate = 3;
        public const int TmpOverflowScrollRect = 4;
        public const int TmpOverflowPage = 5;
        public const int TmpOverflowLinked = 6;

        /// <summary>
        /// Maps <c>TextOverflowModes</c>. <paramref name="unsupported"/> is the
        /// TMP mode's name when OneText has nothing like it and the nearest
        /// behaviour was chosen instead.
        /// </summary>
        public static TextOverflow FromTmpOverflow(int mode, out string unsupported)
        {
            unsupported = null;
            switch (mode)
            {
                case TmpOverflowEllipsis: return TextOverflow.Ellipsis;
                case TmpOverflowTruncate: return TextOverflow.Truncate;
                case TmpOverflowMasking:
                    unsupported = "Masking";
                    return TextOverflow.Truncate;
                case TmpOverflowScrollRect:
                    unsupported = "ScrollRect";
                    return TextOverflow.Overflow;
                case TmpOverflowPage:
                    unsupported = "Page";
                    return TextOverflow.Truncate;
                case TmpOverflowLinked:
                    unsupported = "Linked";
                    return TextOverflow.Overflow;
                default: return TextOverflow.Overflow;
            }
        }

        public const int TmpWrapNoWrap = (int)TextWrappingModes.NoWrap;
        public const int TmpWrapNormal = (int)TextWrappingModes.Normal;
        public const int TmpWrapPreserveWhitespace = (int)TextWrappingModes.PreserveWhitespace;
        public const int TmpWrapPreserveWhitespaceNoWrap =
            (int)TextWrappingModes.PreserveWhitespaceNoWrap;

        /// <summary>Maps <c>TextWrappingModes</c>; whitespace preservation is not a wrap mode here.</summary>
        public static TextWrap FromTmpWrappingMode(int mode) =>
            TmpCompat.WrapFromTmp((TextWrappingModes)mode);

        public static TextWrap FromTmpWordWrapping(bool enabled) =>
            TmpCompat.WrapFromWordWrapping(enabled);

        // ------------------------------------------------------- Unity UI Text

        /// <summary>
        /// <c>TextAnchor</c>'s nine values, which are a horizontal and a
        /// vertical alignment written as one enum. Values are the documented
        /// serialized order: upper row 0-2, middle 3-5, lower 6-8.
        /// </summary>
        public static void FromTextAnchor(int anchor,
            out TextAlignment horizontal, out VerticalAlignment vertical)
        {
            int column = ((anchor % 3) + 3) % 3;
            int row = anchor < 0 ? 0 : anchor / 3;

            horizontal = column == 0 ? TextAlignment.Left
                : column == 1 ? TextAlignment.Center
                : TextAlignment.Right;

            vertical = row == 0 ? VerticalAlignment.Top
                : row == 1 ? VerticalAlignment.Middle
                : VerticalAlignment.Bottom;
        }

        /// <summary>UnityEngine.TextAlignment: Left 0, Center 1, Right 2. What legacy TextMesh uses.</summary>
        public static TextAlignment FromLegacyAlignment(int alignment) =>
            alignment == 1 ? TextAlignment.Center
            : alignment == 2 ? TextAlignment.Right
            : TextAlignment.Left;

        /// <summary><c>HorizontalWrapMode</c>: Wrap 0, Overflow 1.</summary>
        public static TextWrap FromHorizontalWrapMode(int mode) =>
            mode == 0 ? TextWrap.Wrap : TextWrap.NoWrap;

        /// <summary><c>VerticalWrapMode</c>: Truncate 0, Overflow 1.</summary>
        public static TextOverflow FromVerticalWrapMode(int mode) =>
            mode == 0 ? TextOverflow.Truncate : TextOverflow.Overflow;

        /// <summary>
        /// Legacy <c>TextMesh</c> sizing, into OneText's world units.
        ///
        /// A TextMesh draws a point at <c>characterSize / 10</c> world units,
        /// and OneTextMesh draws ten points to the unit, so the two scales meet
        /// at the product. A TextMesh with <c>fontSize 0</c> is asking for the
        /// font's own size, which is not knowable from the component, so the
        /// caller passes what the Font asset says.
        /// </summary>
        public static float FromTextMeshSize(int fontSize, float characterSize, int fontDefaultSize)
        {
            float points = fontSize > 0 ? fontSize : (fontDefaultSize > 0 ? fontDefaultSize : 16);
            return points * (characterSize <= 0f ? 1f : characterSize);
        }

        // ---------------------------------------------------------- tag lint

        /// <summary>
        /// TMP markup OneText has no tag for. A string holding one of these
        /// does not fail: the tag is printed, literally, in the middle of the
        /// sentence, which is why this is a warning and not a note.
        /// </summary>
        public static readonly string[] UnsupportedTags =
        {
            "indent", "line-height", "line-indent",
            "margin", "margin-left", "margin-right", "pos", "space", "rotate", "width",
            "gradient", "uppercase", "lowercase", "smallcaps", "allcaps", "page",
            "material", "quad",
        };

        /// <summary>
        /// Every TMP tag in <paramref name="text"/> that OneText will print
        /// rather than obey, distinct and in first-seen order.
        ///
        /// The reader is deliberately the same shape as the one the runtime
        /// parser uses — a name of letters, digits, dash and underscore, then
        /// either <c>=argument</c> or a space-separated attribute list, then
        /// <c>&gt;</c> — because a lint that disagrees with the parser about
        /// what a tag is reports tags nobody wrote.
        /// </summary>
        public static List<string> LintTags(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(text)) return found;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '<') continue;
                if (!ReadTag(text, i, out string name, out string argument, out int after)) continue;
                i = after - 1;
                if (name == null) continue;

                string report = Classify(name, argument);
                if (report != null && !found.Contains(report)) found.Add(report);
            }
            return found;
        }

        /// <summary>
        /// What to call this tag in a finding, or null when OneText handles it.
        ///
        /// <c>sprite</c> is the awkward one: <c>&lt;sprite=3&gt;</c> and
        /// <c>&lt;sprite name="x"&gt;</c> both work, and
        /// <c>&lt;sprite index=3&gt;</c> and <c>&lt;sprite anim=…&gt;</c> both
        /// parse as an attribute list OneText does not read, so they print.
        /// </summary>
        private static string Classify(string name, string argument)
        {
            if (name == "sprite")
            {
                if (string.IsNullOrEmpty(argument)) return null;
                string lower = argument.ToLowerInvariant();
                if (lower.StartsWith("index")) return "sprite index=";
                if (lower.StartsWith("anim")) return "sprite anim=";
                return null;
            }

            foreach (string unsupported in UnsupportedTags)
                if (name == unsupported) return name;
            return null;
        }

        /// <summary>
        /// Reads one tag. Returns false when what follows the '&lt;' is not a
        /// tag at all, in which case the scan simply carries on from the next
        /// character — the same thing the runtime does with a stray angle
        /// bracket.
        /// </summary>
        private static bool ReadTag(string s, int at, out string name, out string argument,
            out int after)
        {
            name = null;
            argument = null;
            after = at + 1;

            int i = at + 1;
            if (i >= s.Length) return false;
            if (s[i] == '/') i++;

            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
            if (i == start) return false;
            string read = s.Substring(start, i - start).ToLowerInvariant();

            if (i < s.Length && s[i] == ' ')
            {
                int listStart = ++i;
                while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                if (i >= s.Length || s[i] != '>') return false;
                argument = s.Substring(listStart, i - listStart).Trim();
                name = read;
                after = i + 1;
                return true;
            }

            if (i < s.Length && s[i] == '=')
            {
                i++;
                int argStart = i;
                if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                {
                    char quote = s[i++];
                    argStart = i;
                    while (i < s.Length && s[i] != quote && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != quote) return false;
                    argument = s.Substring(argStart, i - argStart);
                    i++;
                }
                else
                {
                    while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != '>') return false;
                    argument = s.Substring(argStart, i - argStart);
                }
            }

            if (i >= s.Length || s[i] != '>') return false;
            name = read;
            after = i + 1;
            return true;
        }

        /// <summary>
        /// Whether a string leans on anything OneTextMesh does not have.
        /// World text ships without sprites and without the animation tags on
        /// purpose, so a 3D label carrying either loses it silently unless
        /// somebody says so first.
        /// </summary>
        public static List<string> LintMeshOnlyLosses(string text)
        {
            var lost = new List<string>();
            if (string.IsNullOrEmpty(text)) return lost;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '<') continue;
                if (!ReadTag(text, i, out string name, out _, out int after)) continue;
                i = after - 1;
                if (name == null) continue;
                if (name == "sprite" || IsEffect(name))
                    if (!lost.Contains(name)) lost.Add(name);
            }
            return lost;
        }

        /// <summary>The built-in animation tag names, spelled out rather than
        /// asked of the registry: a lint must say the same thing whether or not
        /// a project registered effects of its own.</summary>
        private static readonly string[] Effects =
        {
            "wave", "shake", "wobble", "bounce", "rainbow", "pulse", "fade",
            "rise", "swell", "glitch", "stretch", "flash", "pop", "drop",
        };

        private static bool IsEffect(string name)
        {
            foreach (string effect in Effects) if (name == effect) return true;
            return false;
        }
    }
}
