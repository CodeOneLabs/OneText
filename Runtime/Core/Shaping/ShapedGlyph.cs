namespace OneText
{
    /// <summary>
    /// One positioned glyph produced by shaping. All metrics are in font
    /// design units; divide by <see cref="FontData.UnitsPerEm"/> and multiply
    /// by point size to get render units.
    /// </summary>
    public readonly struct ShapedGlyph
    {
        /// <summary>Glyph id in the font (not a Unicode codepoint).</summary>
        public readonly uint GlyphId;

        /// <summary>UTF-16 code-unit index of the source character(s).</summary>
        public readonly int Cluster;

        public readonly int XAdvance;
        public readonly int YAdvance;
        public readonly int XOffset;
        public readonly int YOffset;

        public ShapedGlyph(uint glyphId, int cluster, int xAdvance, int yAdvance, int xOffset, int yOffset)
        {
            GlyphId = glyphId;
            Cluster = cluster;
            XAdvance = xAdvance;
            YAdvance = yAdvance;
            XOffset = xOffset;
            YOffset = yOffset;
        }

        public override string ToString() =>
            $"gid={GlyphId} cluster={Cluster} adv=({XAdvance},{YAdvance}) off=({XOffset},{YOffset})";
    }
}
