namespace OneText.Editor
{
    /// <summary>
    /// The language tags worth offering for a font, and what each one is called
    /// in a menu.
    ///
    /// Short on purpose. <see cref="FontStack"/> consults a font's tag only for
    /// ideographic characters — Han, kana and Hangul, see
    /// <c>AsianTypography.IsIdeographic</c> — because a CJK face covers Latin
    /// too, and letting the tag decide every codepoint would move a Japanese
    /// label's digits and punctuation into the CJK face as well. So these four
    /// are not a starting set somebody should extend by analogy: they are the
    /// complete list of tags that change which glyph a reader sees. A font
    /// tagged <c>th</c> or <c>ar</c> renders exactly as an untagged one does,
    /// and offering those alongside the four that work is most of why people
    /// cannot tell what this field is for.
    ///
    /// Anything else is still accepted — the field is a BCP 47 string and
    /// matching is by prefix, so <c>zh</c> serves <c>zh-Hans</c> — it just has
    /// to be typed rather than picked.
    /// </summary>
    public static class FontLanguages
    {
        /// <summary>A tag and the name a person would look for in a menu.</summary>
        public readonly struct Choice
        {
            public readonly string Tag;
            public readonly string Label;

            public Choice(string tag, string label)
            {
                Tag = tag;
                Label = label;
            }
        }

        /// <summary>What the menus offer, in the order they offer it.</summary>
        public static readonly Choice[] Choices =
        {
            new Choice("", "Any language"),
            new Choice("ja", "Japanese (ja)"),
            new Choice("zh-Hans", "Chinese, Simplified (zh-Hans)"),
            new Choice("zh-Hant", "Chinese, Traditional (zh-Hant)"),
            new Choice("ko", "Korean (ko)"),
        };

        /// <summary>
        /// Where <paramref name="tag"/> sits in <see cref="Choices"/>, or -1 for
        /// a tag somebody typed themselves. Null and empty are the same answer:
        /// an untagged font.
        /// </summary>
        public static int IndexOf(string tag)
        {
            string wanted = tag ?? "";
            for (int i = 0; i < Choices.Length; i++)
                if (Choices[i].Tag == wanted) return i;
            return -1;
        }

        /// <summary>
        /// The label for a tag, including one that is not on the list, so a
        /// project that tagged a font <c>zh</c> years ago still reads as
        /// something rather than as a blank menu.
        /// </summary>
        public static string LabelOf(string tag)
        {
            int at = IndexOf(tag);
            return at >= 0 ? Choices[at].Label : tag;
        }
    }
}
