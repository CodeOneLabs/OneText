
namespace OneText
{
    /// <summary>A clickable range of the displayed text.</summary>
    public readonly struct TextLink
    {
        /// <summary>The id from <c>&lt;link=id&gt;</c>, passed to click handlers.</summary>
        public readonly string Id;

        /// <summary>Range in the <em>displayed</em> text, in UTF-16 code units.</summary>
        public readonly int Start, Length;

        public TextLink(string id, int start, int length)
        {
            Id = id;
            Start = start;
            Length = length;
        }

        public int End => Start + Length;

        public bool Contains(int index) => index >= Start && index < End;

        public override string ToString() => $"link={Id} [{Start}..{End})";
    }
}
