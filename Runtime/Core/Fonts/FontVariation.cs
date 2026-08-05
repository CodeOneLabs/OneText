namespace OneText
{
    /// <summary>
    /// One OpenType variation axis setting, e.g. <c>("wght", 700)</c>.
    /// Tags are the four-character axis tags from the font's fvar table.
    /// </summary>
    // Serializable, and therefore not readonly: Unity does not serialize
    // readonly fields, and a style asset has to be able to store axis settings.
    [System.Serializable]
    public struct FontVariation
    {
        public string Tag;
        public float Value;

        public FontVariation(string tag, float value)
        {
            Tag = tag;
            Value = value;
        }

        public override string ToString() => $"{Tag}={Value}";
    }

    /// <summary>A variation axis a font exposes, with its permitted range.</summary>
    public readonly struct FontAxis
    {
        public readonly string Tag;
        public readonly float Minimum;
        public readonly float Default;
        public readonly float Maximum;

        public FontAxis(string tag, float minimum, float defaultValue, float maximum)
        {
            Tag = tag;
            Minimum = minimum;
            Default = defaultValue;
            Maximum = maximum;
        }

        /// <summary>Clamps a requested value into the axis range.</summary>
        public float Clamp(float value) =>
            value < Minimum ? Minimum : value > Maximum ? Maximum : value;

        public override string ToString() => $"{Tag} [{Minimum}..{Maximum}] default {Default}";
    }
}
