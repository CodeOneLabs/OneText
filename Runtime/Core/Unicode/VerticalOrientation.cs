namespace OneText.Unicode
{
    /// <summary>
    /// Vertical_Orientation (UAX #50): how one codepoint stands in a vertical
    /// column.
    ///
    /// The four values are the property's own, in its own order, so the
    /// generated table can store the index and cast.
    /// </summary>
    public enum VerticalOrientation : byte
    {
        /// <summary>Turned ninety degrees clockwise from the code charts. The default for Unicode.</summary>
        Rotated = 0,

        /// <summary>Upright, exactly as in the code charts: Han, kana, Hangul, emoji.</summary>
        Upright = 1,

        /// <summary>Transformed typographically, falling back to upright: small kana, 、。</summary>
        TransformedUpright = 2,

        /// <summary>Transformed typographically, falling back to rotated: 「」（）ー</summary>
        TransformedRotated = 3,
    }

    /// <summary>
    /// The UAX #50 half of vertical writing: for every character in a column,
    /// upright or rotated.
    ///
    /// The property has four values and only two of them are answers. U is
    /// upright and R is rotated ninety degrees clockwise; Tu and Tr say
    /// "apply the font's typographic transform, and if there is none fall
    /// back to upright / to rotated". That transform is the OpenType
    /// <c>vert</c> feature, which the engine gets for free by shaping the run
    /// top-to-bottom, so a Tu character is shaped upright and comes out as
    /// its vertical form (small っ moves to the top right of its square, 。
    /// to the top right of its), and a Tr character is asked of the font
    /// first and rotated only if the font has nothing to offer.
    ///
    /// That last question is the only one this class cannot answer alone: 「
    /// is Tr, every Japanese face has a vertical form for it, and a Latin
    /// face has none. So <see cref="Resolve"/> takes the answer as a
    /// parameter and the layout engine, which has a shaper, supplies it.
    /// </summary>
    public static class VerticalOrientationLookup
    {
        /// <summary>The Vertical_Orientation value of a codepoint.</summary>
        public static VerticalOrientation Get(int codepoint) =>
            VerticalData.GetOrientation(codepoint);

        /// <summary>The value of the UTF-16 code unit at <paramref name="index"/>.</summary>
        public static VerticalOrientation Get(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
                return VerticalOrientation.Rotated;
            int cp = char.IsHighSurrogate(text[index]) && index + 1 < text.Length &&
                     char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(text, index)
                : text[index];
            return VerticalData.GetOrientation(cp);
        }

        /// <summary>
        /// Whether a codepoint stands upright in a column.
        ///
        /// <paramref name="hasVerticalForm"/> answers the typographic half:
        /// does the font in force substitute something for this character
        /// under <c>vert</c>? It is only consulted for Tr; U and Tu are
        /// upright whatever the font says, R is rotated whatever the font
        /// says, and a font with no <c>vert</c> at all still draws a Tu
        /// character upright, just without its vertical form.
        /// </summary>
        public static bool IsUpright(int codepoint, bool hasVerticalForm) =>
            Resolve(Get(codepoint), hasVerticalForm);

        /// <summary>The same decision, from an already-looked-up value.</summary>
        public static bool Resolve(VerticalOrientation orientation, bool hasVerticalForm) =>
            orientation switch
            {
                VerticalOrientation.Upright => true,
                VerticalOrientation.TransformedUpright => true,
                VerticalOrientation.TransformedRotated => hasVerticalForm,
                _ => false,
            };

        /// <summary>
        /// True if the font has to be asked before this value can be resolved,
        /// which is Tr and nothing else. Worth a test of its own because
        /// the shaping it triggers is the one per-character cost vertical
        /// itemization has, and it must not be paid for the other three.
        /// </summary>
        public static bool NeedsFont(VerticalOrientation orientation) =>
            orientation == VerticalOrientation.TransformedRotated;
    }
}
