namespace OneText.UGUI
{
    /// <summary>
    /// TextMesh Pro's <c>TextAlignmentOptions</c>, name for name and value for
    /// value.
    ///
    /// It is here because the alternative was worse. OneText keeps alignment on
    /// two axes — <see cref="TextAlignment"/> across the line and
    /// <see cref="VerticalAlignment"/> down the box — which is the honest shape
    /// for it, and it is also not the shape four hundred call sites are written
    /// in. Declaring the enum this package does not otherwise want costs one
    /// file and buys <c>label.alignment = TextAlignmentOptions.MidlineLeft</c>
    /// still compiling on the afternoon somebody swaps packages, which is the
    /// whole of what the parity aliases are for.
    ///
    /// The values are TMP's, not ours: a horizontal bit in the low byte and a
    /// vertical bit in the high one. That matters because projects serialize
    /// this as an int and because <c>(TextAlignmentOptions)someInt</c> appears
    /// in real code; a renumbering would compile everywhere and mean something
    /// else. <c>TmpEnumParityTests</c> compares every member against the real
    /// TMP enum, so a typo here is a failing test rather than a moved label.
    ///
    /// Five of the names are distinctions OneText does not draw — Flush and
    /// GeoAligned across the line, Baseline, Midline and Capline down it — and
    /// a combination carrying any of them resolves to the nearest thing on
    /// assignment, the same nearest thing the Onboarding migration picks and
    /// reports. A file that still has
    /// <c>using TMPro;</c> in it will find this name ambiguous with that one:
    /// qualify whichever of the two it means, or let the Onboarding tab's
    /// rewriter drop the TMPro using, which is what it is for.
    /// </summary>
    public enum TextAlignmentOptions
    {
        TopLeft = TmpCompat.Left | TmpCompat.Top,
        Top = TmpCompat.Center | TmpCompat.Top,
        TopRight = TmpCompat.Right | TmpCompat.Top,
        TopJustified = TmpCompat.Justified | TmpCompat.Top,
        TopFlush = TmpCompat.Flush | TmpCompat.Top,
        TopGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Top,

        Left = TmpCompat.Left | TmpCompat.Middle,
        Center = TmpCompat.Center | TmpCompat.Middle,
        Right = TmpCompat.Right | TmpCompat.Middle,
        Justified = TmpCompat.Justified | TmpCompat.Middle,
        Flush = TmpCompat.Flush | TmpCompat.Middle,
        CenterGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Middle,

        BottomLeft = TmpCompat.Left | TmpCompat.Bottom,
        Bottom = TmpCompat.Center | TmpCompat.Bottom,
        BottomRight = TmpCompat.Right | TmpCompat.Bottom,
        BottomJustified = TmpCompat.Justified | TmpCompat.Bottom,
        BottomFlush = TmpCompat.Flush | TmpCompat.Bottom,
        BottomGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Bottom,

        BaselineLeft = TmpCompat.Left | TmpCompat.Baseline,
        Baseline = TmpCompat.Center | TmpCompat.Baseline,
        BaselineRight = TmpCompat.Right | TmpCompat.Baseline,
        BaselineJustified = TmpCompat.Justified | TmpCompat.Baseline,
        BaselineFlush = TmpCompat.Flush | TmpCompat.Baseline,
        BaselineGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Baseline,

        MidlineLeft = TmpCompat.Left | TmpCompat.Midline,
        Midline = TmpCompat.Center | TmpCompat.Midline,
        MidlineRight = TmpCompat.Right | TmpCompat.Midline,
        MidlineJustified = TmpCompat.Justified | TmpCompat.Midline,
        MidlineFlush = TmpCompat.Flush | TmpCompat.Midline,
        MidlineGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Midline,

        CaplineLeft = TmpCompat.Left | TmpCompat.Capline,
        Capline = TmpCompat.Center | TmpCompat.Capline,
        CaplineRight = TmpCompat.Right | TmpCompat.Capline,
        CaplineJustified = TmpCompat.Justified | TmpCompat.Capline,
        CaplineFlush = TmpCompat.Flush | TmpCompat.Capline,
        CaplineGeoAligned = TmpCompat.GeometryCenter | TmpCompat.Capline,

        /// <summary>TMP's marker for a value carried over from the legacy component.</summary>
        Converted = 0xFFFF,
    }

    /// <summary>
    /// TextMesh Pro's <c>TextWrappingModes</c>, same names, same values.
    ///
    /// OneText has two states where TMP has four, because the other two are
    /// about whitespace and not about wrapping: <c>PreserveWhitespace</c> keeps
    /// the run of spaces that a break would otherwise swallow, which is a
    /// shaping decision here and not a layout one. Both preserving modes
    /// therefore land on the wrap setting they share, and the whitespace half
    /// of what they asked for is dropped — see <see cref="TmpCompat.PreservesWhitespace"/>
    /// for asking whether a value carried one.
    /// </summary>
    public enum TextWrappingModes
    {
        NoWrap = 0,
        Normal = 1,
        PreserveWhitespace = 2,
        PreserveWhitespaceNoWrap = 3,
    }

    /// <summary>
    /// The arithmetic between TextMesh Pro's units and OneText's, in one place.
    ///
    /// Both the runtime parity aliases on <see cref="OneTextLabel"/> and the
    /// editor's Onboarding migration convert the same four things — alignment,
    /// line spacing, wrapping and back again — and two implementations of that
    /// is two answers to "what does <c>lineSpacing = 10</c> mean", one of which
    /// would eventually be wrong. So the migration mapper forwards here, and
    /// what a project gets by assigning the alias is what it would have got by
    /// running the Onboarding tab.
    /// </summary>
    public static class TmpCompat
    {
        // ---------------------------------------------------------- alignment

        // The bitfield behind TextAlignmentOptions: a horizontal bit in the low
        // byte and a vertical bit in the high one, which is why the combined
        // names (TopLeft = 0x101) decompose by masking rather than by table.
        public const int Left = 0x1;
        public const int Center = 0x2;
        public const int Right = 0x4;
        public const int Justified = 0x8;
        public const int Flush = 0x10;
        public const int GeometryCenter = 0x20;

        public const int Top = 0x100;
        public const int Middle = 0x200;
        public const int Bottom = 0x400;
        public const int Baseline = 0x800;
        public const int Midline = 0x1000;
        public const int Capline = 0x2000;

        /// <summary>
        /// Splits a <c>TextAlignmentOptions</c> value into the pair OneText
        /// keeps it in. <paramref name="approximated"/> says a distinction was
        /// lost, and <paramref name="what"/> names it — the Onboarding report
        /// prints that name, and the alias throws it away, because a property
        /// setter has nobody to tell.
        /// </summary>
        public static void SplitAlignment(int alignment,
            out TextAlignment horizontal, out VerticalAlignment vertical,
            out bool approximated, out string what)
        {
            approximated = false;
            what = null;

            if ((alignment & Flush) != 0)
            {
                // Flush justifies every line, including the last one, which
                // OneText's Justified deliberately does not.
                horizontal = TextAlignment.Justified;
                approximated = true;
                what = "Flush";
            }
            else if ((alignment & Justified) != 0) horizontal = TextAlignment.Justified;
            else if ((alignment & GeometryCenter) != 0)
            {
                // Centres on the rendered glyph bounds rather than the box.
                horizontal = TextAlignment.Center;
                approximated = true;
                what = "GeometryCenter";
            }
            else if ((alignment & Right) != 0) horizontal = TextAlignment.Right;
            else if ((alignment & Center) != 0) horizontal = TextAlignment.Center;
            else horizontal = TextAlignment.Left;

            if ((alignment & Bottom) != 0) vertical = VerticalAlignment.Bottom;
            else if ((alignment & Middle) != 0) vertical = VerticalAlignment.Middle;
            else if ((alignment & Baseline) != 0)
            {
                vertical = VerticalAlignment.Middle;
                approximated = true;
                what = Join(what, "Baseline");
            }
            else if ((alignment & Midline) != 0)
            {
                vertical = VerticalAlignment.Middle;
                approximated = true;
                what = Join(what, "Midline");
            }
            else if ((alignment & Capline) != 0)
            {
                vertical = VerticalAlignment.Top;
                approximated = true;
                what = Join(what, "Capline");
            }
            else vertical = VerticalAlignment.Top;
        }

        /// <summary>
        /// The two axes written back as one TMP value, for the getter side of
        /// the alias.
        ///
        /// Lossy in exactly one direction, and worth being plain about: TMP has
        /// no notion of a start edge, so <see cref="TextAlignment.Start"/> and
        /// <see cref="TextAlignment.End"/> answer Left and Right — the sides
        /// they resolve to in a left-to-right paragraph. Assigning that answer
        /// straight back therefore pins a direction-relative label to a side,
        /// and an Arabic line that used to sit against the right edge would
        /// move. Nothing the setter produces is ever Start or End, so this only
        /// reaches a label already configured in OneText's own names.
        /// </summary>
        public static TextAlignmentOptions CombineAlignment(
            TextAlignment horizontal, VerticalAlignment vertical)
        {
            int bits = horizontal switch
            {
                TextAlignment.Center => Center,
                TextAlignment.Right => Right,
                TextAlignment.End => Right,
                TextAlignment.Justified => Justified,
                _ => Left, // Left, and Start reading as the edge it resolves to
            };

            bits |= vertical switch
            {
                VerticalAlignment.Middle => Middle,
                VerticalAlignment.Bottom => Bottom,
                _ => Top,
            };

            return (TextAlignmentOptions)bits;
        }

        private static string Join(string a, string b) =>
            string.IsNullOrEmpty(a) ? b : a + " and " + b;

        // -------------------------------------------------------- line spacing

        /// <summary>
        /// TMP's line spacing is an offset in font-size-relative units where
        /// zero means "the font's own line height"; OneText's is a multiplier
        /// of that same height where one means the same thing. Ten percent is
        /// therefore <c>10</c> there and <c>1.1</c> here.
        ///
        /// Approximate on purpose: TMP applies the offset after its own
        /// ascender/descender arithmetic, so identical numbers do not
        /// guarantee identical pixels, only identical intent. That is also why
        /// this conversion is a named function and not a magic 100 written at
        /// each of the three call sites that need it.
        /// </summary>
        public static float LineSpacingFromTmp(float offset) => 1f + offset / 100f;

        /// <summary>The same arithmetic backwards, for the getter side.</summary>
        public static float LineSpacingToTmp(float multiplier) => (multiplier - 1f) * 100f;

        // ------------------------------------------------------------ wrapping

        /// <summary>Maps <c>TextWrappingModes</c>; whitespace preservation is not a wrap mode here.</summary>
        public static TextWrap WrapFromTmp(TextWrappingModes mode) =>
            mode == TextWrappingModes.NoWrap || mode == TextWrappingModes.PreserveWhitespaceNoWrap
                ? TextWrap.NoWrap
                : TextWrap.Wrap;

        /// <summary>
        /// And back. Never answers either preserving mode, because OneText does
        /// not hold that bit: a label set to <c>PreserveWhitespace</c> reads
        /// back as <c>Normal</c>, which is what it is actually doing.
        /// </summary>
        public static TextWrappingModes WrapToTmp(TextWrap wrap) =>
            wrap == TextWrap.NoWrap ? TextWrappingModes.NoWrap : TextWrappingModes.Normal;

        /// <summary>
        /// Whether a TMP wrapping mode asked for whitespace preservation, which
        /// is the half of it OneText drops. The Onboarding report asks so it can
        /// say so.
        /// </summary>
        public static bool PreservesWhitespace(TextWrappingModes mode) =>
            mode == TextWrappingModes.PreserveWhitespace ||
            mode == TextWrappingModes.PreserveWhitespaceNoWrap;

        public static TextWrap WrapFromWordWrapping(bool enabled) =>
            enabled ? TextWrap.Wrap : TextWrap.NoWrap;
    }
}
