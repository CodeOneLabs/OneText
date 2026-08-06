namespace OneText
{
    /// <summary>
    /// Text an input method is still composing: the Hangul syllable being
    /// assembled, the kana waiting to be converted, the pinyin before a
    /// candidate is picked. It is <em>not</em> part of the field's value: it
    /// sits inside the displayed text so the user can see it at the caret, and
    /// becomes real only when the platform commits it.
    /// </summary>
    public struct ImeComposition
    {
        /// <summary>True while an input method owns the text at <see cref="Start"/>.</summary>
        public bool Active;

        /// <summary>Where the composition sits, as an index into the committed text.</summary>
        public int Start;

        /// <summary>The composing text. Never null while <see cref="Active"/>.</summary>
        public string Text;

        /// <summary>Caret offset inside <see cref="Text"/>, in UTF-16 code units.</summary>
        public int Caret;

        /// <summary>
        /// The clause being converted, as an offset into <see cref="Text"/>.
        /// Japanese conversion splits a composition into clauses and highlights
        /// one of them; for every other input method this is the whole string.
        /// </summary>
        public int ClauseStart;

        /// <summary>Length of the converting clause; zero when there is none.</summary>
        public int ClauseLength;

        public int Length => Text != null ? Text.Length : 0;

        /// <summary>End of the composition in committed-text indices.</summary>
        public int End => Start + Length;

        public bool HasClause => ClauseLength > 0;

        public static ImeComposition None => new ImeComposition { Text = string.Empty };
    }
}
