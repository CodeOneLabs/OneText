using UnityEngine;

namespace OneText
{
    /// <summary>
    /// One <c>&lt;ruby=…&gt;</c> annotation: a string of its own, attached to a
    /// range of the laid-out text.
    ///
    /// The annotation is not <em>in</em> the text, which is the decision the
    /// rest of ruby follows from. Putting it there would give furigana its own
    /// character indices, and then every index the engine publishes (reveal
    /// steps, caret positions, effect spans, link ranges) would count text no
    /// reader is reading. It is a separate string the layout engine shapes,
    /// sizes and places against the base's advance.
    /// </summary>
    public readonly struct TextRubySpan
    {
        /// <summary>The annotation itself: shaped text, in any script.</summary>
        public readonly string Text;

        /// <summary>Base range in the display text, in UTF-16 code units.</summary>
        public readonly int Start, Length;

        public TextRubySpan(string text, int start, int length)
        {
            Text = text;
            Start = start;
            Length = length;
        }

        public int End => Start + Length;

        public override string ToString() => $"[{Start}..{End}) ruby=\"{Text}\"";
    }

    /// <summary>
    /// The arithmetic of simple ruby placement, from the W3C note "Rules for
    /// Simple Placement of Japanese Ruby" (and JLREQ, which it condenses).
    ///
    /// Kept out of the layout engine and free of every layout type on purpose:
    /// these are the two decisions the spec actually makes (what to do with
    /// the slack under a narrow ruby, and how far a wide one may hang over its
    /// neighbours), and they are worth being able to test as numbers rather
    /// than by measuring a rendered line.
    /// </summary>
    public static class RubyPlacement
    {
        /// <summary>
        /// "The size of the ruby is by default set to half of the size of the
        /// base characters."
        /// </summary>
        public const float DefaultScale = 0.5f;

        /// <summary>Sanitises a configured ratio; 0 or less means the default.</summary>
        public static float ResolveScale(float scale) =>
            scale > 0f ? Mathf.Min(scale, 1f) : DefaultScale;

        /// <summary>
        /// Spreads the slack under a ruby narrower than its base.
        ///
        /// The spec's group-ruby rule: the gap between two ruby characters is
        /// twice the gap at each end (so the annotation reads as one word,
        /// evenly set, rather than as characters pinned to the base's corners),
        /// and no end gap may exceed half a base character, which is what stops
        /// a one-kana reading of a four-kanji compound from being flung out to
        /// the edges. Whatever the cap refuses is centred, so the degenerate
        /// case (one ruby character, or a cap that bites) is simple centring.
        /// </summary>
        /// <param name="slack">Base advance minus ruby advance; never negative.</param>
        /// <param name="count">Ruby characters that carry an advance.</param>
        /// <param name="baseCharacterWidth">The base's advance over its own character count.</param>
        /// <param name="lead">Offset from the base's left edge to the first ruby glyph.</param>
        /// <param name="gap">Extra advance given to every ruby glyph but the last.</param>
        public static void Distribute(float slack, int count, float baseCharacterWidth,
            out float lead, out float gap)
        {
            lead = 0f;
            gap = 0f;
            if (slack <= 0f || count <= 0) return;

            float end = Mathf.Min(slack / (2f * count), Mathf.Max(0f, baseCharacterWidth) * 0.5f);
            gap = end * 2f;
            lead = end + (slack - 2f * end * count) * 0.5f;
        }

        /// <summary>
        /// Splits the excess of a ruby wider than its base into overhang and
        /// padding.
        ///
        /// Overhang comes first and is taken symmetrically as far as both sides
        /// allow, because a reading centred over its kanji is the point; what
        /// neither neighbour will give up becomes padding, which widens the
        /// base itself. Padding is the fallback rather than the rule precisely
        /// because it moves the text: a paragraph whose every furigana pushed
        /// its sentence apart is what the spec's overhang exists to avoid.
        /// </summary>
        public static void Overhang(float needed, float allowedBefore, float allowedAfter,
            out float before, out float after, out float pad)
        {
            if (needed <= 0f)
            {
                before = after = pad = 0f;
                return;
            }
            before = Mathf.Min(allowedBefore, needed * 0.5f);
            after = Mathf.Min(allowedAfter, needed - before);
            // Asked again now that the other side has taken its share: a
            // neighbour that could give nothing must not cost the side that
            // could give everything.
            before = Mathf.Min(allowedBefore, needed - after);
            pad = Mathf.Max(0f, needed - before - after);
        }

        /// <summary>
        /// The blank a neighbouring character has left to give, in render
        /// units.
        ///
        /// <paramref name="advance"/> is the advance the neighbour actually
        /// occupies, which may already be less than its em because punctuation
        /// compression took the same blank first, and the spec is explicit
        /// that ruby may then only hang over what compression left.
        /// </summary>
        public static float BlankOf(float blankFraction, float advance, float em)
        {
            if (blankFraction <= 0f) return 0f;
            return Mathf.Max(0f, Mathf.Min(blankFraction * em, advance - (1f - blankFraction) * em));
        }
    }
}
