using System.Collections.Generic;
using OneText.Unicode;

namespace OneText
{
    /// <summary>
    /// What "one step" of a typewriter means.
    ///
    /// The reveal has always counted grapheme clusters, which is already the
    /// only defensible answer for "one character" in shaped text. It is still
    /// the wrong step size for most of the world: a Thai leading vowel is its
    /// own grapheme and stands to the LEFT of the consonant it belongs to, so
    /// a grapheme-stepped typewriter spends a tick drawing nothing and then
    /// pops two letters at once; a Khmer subscript consonant is a separate
    /// grapheme from the coeng that introduces it; and a Japanese sentence
    /// gives 。 and っ ticks of their own, which no reader would call
    /// characters.
    ///
    /// These are three different questions with three different published
    /// answers, so they are three modes rather than a heuristic.
    /// </summary>
    public enum RevealGranularity
    {
        /// <summary>
        /// One UAX #29 extended grapheme cluster per step, the historical
        /// behaviour and the default, so no existing label changes.
        /// </summary>
        Grapheme,

        /// <summary>
        /// One orthographic cluster per step: a base together with everything
        /// that is written as part of it, including the pieces that sit before
        /// it. Thai and Lao leading vowels join their consonant, a Khmer coeng
        /// keeps its subscript, and anything the shaper fused into one glyph
        /// (a ligature, an Arabic lam-alef) is one step because it is one tile
        /// and cannot be drawn in halves.
        /// </summary>
        Cluster,

        /// <summary>
        /// One written syllable per step: the cluster, plus whatever cannot
        /// begin one. A Hangul block is already a syllable and is untouched; a
        /// Japanese っ, ゃ, ー, 。 or 、 arrives with the character it belongs
        /// to instead of getting a tick of its own.
        /// </summary>
        Syllable,
    }

    /// <summary>
    /// Extra time held after a revealed unit that contains one of these
    /// characters. A table rather than a bool because the pause a writer wants
    /// after 。 is not the one they want after 、, and neither is the one they
    /// want after ….
    /// </summary>
    [System.Serializable]
    public struct PunctuationDelay
    {
        /// <summary>
        /// The characters this delay applies to, as a set: "、。！？…". Not a
        /// string to match: a set, so one row covers a whole class of marks.
        /// </summary>
        public string Characters;

        /// <summary>Seconds to hold after the unit, on top of the normal step.</summary>
        public float Seconds;

        public PunctuationDelay(string characters, float seconds)
        {
            Characters = characters;
            Seconds = seconds;
        }
    }

    /// <summary>
    /// The starter table, offered in the inspector rather than shipped as a
    /// default: a delay table is content, and a package that silently retimed
    /// everyone's dialogue would be a package that broke it.
    ///
    /// Thai is the row people are surprised by. Thai writes no sentence
    /// punctuation (its phrase separator is the space), so a Thai-only label
    /// wants a row for " " that no other language should have, and gets ฯ and
    /// ๆ here instead of nothing.
    /// </summary>
    public static class PunctuationDelays
    {
        public static void Recommended(IList<PunctuationDelay> into)
        {
            if (into == null) return;
            into.Clear();
            // Full stops, in the widths people actually type them: full-width
            // for CJK, half-width for CJK text with an ASCII keyboard, and
            // halfwidth-forms 。 for Japanese chat.
            into.Add(new PunctuationDelay("。．！？!?｡", 0.35f));
            into.Add(new PunctuationDelay("、，；：,;:､", 0.15f));
            // The beat. Longer than a full stop on purpose: an ellipsis is a
            // pause written down, which is the one case where the punctuation
            // IS the timing.
            into.Add(new PunctuationDelay("…‥⋯—―", 0.45f));
            // Devanagari danda and double danda, Khmer khan and bariyoosan,
            // Arabic comma, semicolon and question mark. Every one of these is
            // a sentence end that an ASCII-only table silently ignores.
            into.Add(new PunctuationDelay("।॥។៕៚؟،؛", 0.35f));
            into.Add(new PunctuationDelay("ฯๆ๚๛", 0.25f));
        }
    }

    /// <summary>
    /// Turns a finished layout into the list of units a typewriter steps
    /// through, as indices into the layout's grapheme table.
    ///
    /// Every mode is a COARSENING of the grapheme table, never a re-cut of it:
    /// a unit boundary is always a grapheme boundary. That is what keeps the
    /// reveal compatible with everything already built on
    /// <c>MaxVisibleGraphemes</c> (carets, effect spans, the merged-tile rule),
    /// and it is why none of this invents a segmentation. The boundaries
    /// come from the shaper's own cluster values, from the two dozen code
    /// points whose script defines them as attached, and from the kinsoku
    /// tables, all of which are already here and already tested.
    /// </summary>
    public static class RevealUnits
    {
        [System.ThreadStatic] private static bool[] t_clusterStarts;

        /// <summary>
        /// Fills <paramref name="unitStarts"/> with the grapheme index each
        /// reveal unit begins at, terminated with the grapheme count, the same
        /// shape as <see cref="TextLayoutResult.GraphemeStarts"/>, so the unit
        /// count is <c>unitStarts.Count - 1</c> and unit <c>u</c> covers
        /// graphemes <c>[unitStarts[u], unitStarts[u + 1])</c>.
        ///
        /// <paramref name="text"/> must be the text that was laid out (the
        /// display text, after markup removal), because every index here is one
        /// of its.
        /// </summary>
        public static void Build(TextLayoutResult layout, string text,
            RevealGranularity granularity, List<int> unitStarts)
        {
            if (unitStarts == null) return;
            unitStarts.Clear();
            unitStarts.Add(0);

            int graphemes = layout?.GraphemeCount ?? 0;
            if (graphemes <= 0) return;

            // A layout paired with text it was not built from indexes past the
            // end of it. That should not happen and is one cheap compare to be
            // sure of, because the failure is an exception thrown out of a
            // mesh rebuild rather than a wrong step size.
            bool matches = text != null && layout.GraphemeStarts[graphemes] == text.Length;
            if (granularity == RevealGranularity.Grapheme || !matches)
            {
                // Materialised rather than special-cased away, so callers have
                // one table to read whatever the mode is. It is rebuilt only
                // when the layout is, never per frame.
                for (int g = 1; g <= graphemes; g++) unitStarts.Add(g);
                return;
            }

            BuildClusters(layout, text, graphemes, unitStarts);
            if (granularity == RevealGranularity.Syllable) MergeIntoSyllables(layout, text, unitStarts);
        }

        /// <summary>
        /// Orthographic clusters, from two sources that answer different halves
        /// of the question.
        ///
        /// The shaper answers "did these characters become one piece of ink":
        /// a glyph carries the text index its cluster starts at, so a grapheme
        /// whose start no glyph claims was absorbed by the one before it:
        /// a ligature, a reordered mark, an Indic conjunct the font composed.
        /// Splitting one of those is not merely ugly, it is impossible: the
        /// tile is baked whole, and the label already refuses to draw it until
        /// every grapheme under it is revealed, so a grapheme-stepped reveal
        /// spends those steps drawing nothing.
        ///
        /// The script answers "is this character written as part of its
        /// neighbour" for the cases where the two stay separate glyphs, which
        /// is the Thai case and the one that matters most here: เ is a whole
        /// glyph, a whole grapheme and half a syllable, and it is stored
        /// BEFORE the consonant it is pronounced after.
        /// </summary>
        private static void BuildClusters(TextLayoutResult layout, string text,
            int graphemes, List<int> unitStarts)
        {
            int length = text?.Length ?? 0;
            var starts = t_clusterStarts;
            if (starts == null || starts.Length < length + 1)
                t_clusterStarts = starts = new bool[System.Math.Max(length + 1, 64)];
            System.Array.Clear(starts, 0, length + 1);

            var glyphs = layout.Glyphs;
            for (int i = 0; i < glyphs.Count; i++)
            {
                int cluster = glyphs[i].Cluster;
                if ((uint)cluster <= (uint)length) starts[cluster] = true;
            }

            var table = layout.GraphemeStarts;
            for (int g = 1; g < graphemes; g++)
            {
                int at = table[g];
                bool boundary = starts[at];

                // Nothing in the shaper's output claims this index. That is
                // usually absorption, but it is also what a grapheme that
                // produced no glyph at all looks like (a truncated line, a
                // control character), and treating THAT as absorption swallows
                // the whole tail of the label into one step. So the previous
                // grapheme has to have put something in the output for this one
                // to be read as part of it.
                if (!boundary)
                {
                    bool previousShaped = false;
                    for (int i = table[g - 1]; i < at && !previousShaped; i++)
                        previousShaped = starts[i];
                    boundary = !previousShaped;
                }
                if (!boundary) continue;

                // Same shape as 行末禁則 and 行頭禁則, one script layer down: a
                // character that cannot end a unit keeps the next one, and a
                // character that cannot start one joins the previous.
                if (AttachesToNext(text[at - 1])) continue;
                if (AttachesToPrevious(text[at])) continue;
                unitStarts.Add(g);
            }
            unitStarts.Add(graphemes);
        }

        /// <summary>
        /// Characters that are written before what they belong to, so a unit
        /// may not end on one.
        ///
        /// The Thai and Lao leading vowels are the whole reason this method
        /// exists. They are ordinary letters by every Unicode property (not
        /// marks, not Prepend, their own grapheme cluster), and they are
        /// pronounced after the consonant they are stored before. A typewriter
        /// that steps over one shows a floating เ, then nothing for a tick
        /// while the pair's tile waits for its consonant.
        ///
        /// The two invisible stackers are the same shape of problem from the
        /// other direction: a mark that belongs to the consonant BEFORE it and
        /// introduces the one AFTER it, so the boundary the grapheme rules find
        /// falls in the middle of a stack. UAX #29's conjunct rule (GB9c) joins
        /// the ones whose script it names, and these two are here for what it
        /// does not: the rule is written around the Indic linkers, and the
        /// cost of listing a stacker it already covers is nothing.
        /// </summary>
        public static bool AttachesToNext(char c) =>
            (c >= 'เ' && c <= 'ไ') ||   // Thai เ แ โ ใ ไ
            (c >= 'ເ' && c <= 'ໄ') ||   // Lao ເ ແ ໂ ໃ ໄ
            c == '្' ||                      // Khmer coeng
            c == '္';                        // Myanmar virama

        /// <summary>
        /// Characters written as part of what precedes them that UAX #29 still
        /// gives a cluster of their own.
        ///
        /// SARA AM is a spacing letter, not a mark, because it is two things
        /// at once (a nasal above the consonant and a vowel beside it), and
        /// Unicode encodes it as one letter to keep Thai keyboards honest. It
        /// is never a step of its own to a reader.
        /// </summary>
        public static bool AttachesToPrevious(char c) =>
            c == 'ำ' ||                      // Thai sara am
            c == 'ຳ';                        // Lao sign am

        /// <summary>
        /// Drops the cluster boundaries that fall in front of a character which
        /// may not begin a line.
        ///
        /// 行頭禁則 is already the published answer to "may a written unit start
        /// here", so this asks the existing tables rather than growing a second
        /// list that would disagree with them. It reads them at
        /// <see cref="AsianTypography.Kinsoku.Strict"/> and NOT at the label's
        /// own severity, because those are different questions: the label's
        /// setting is house style for where lines may break, and a project
        /// that leaves it Off has not thereby declared that 。 is a syllable.
        ///
        /// Compacted in place; this runs on a layout change, but it is not a
        /// reason to allocate a second list.
        /// </summary>
        private static void MergeIntoSyllables(TextLayoutResult layout, string text,
            List<int> unitStarts)
        {
            var table = layout.GraphemeStarts;
            int keep = 1;
            for (int u = 1; u < unitStarts.Count - 1; u++)
            {
                int at = table[unitStarts[u]];
                if (at < text.Length &&
                    AsianTypography.ForbiddenAtLineStart(text[at], AsianTypography.Kinsoku.Strict))
                    continue;
                unitStarts[keep++] = unitStarts[u];
            }
            unitStarts[keep++] = unitStarts[unitStarts.Count - 1];
            unitStarts.RemoveRange(keep, unitStarts.Count - keep);
        }

        /// <summary>
        /// How many units are FULLY revealed once <paramref name="grapheme"/>
        /// clusters are showing.
        ///
        /// Fully, because a unit that is half-drawn has not happened: the
        /// per-unit callback is a typing sound, and firing it for a Thai
        /// syllable whose consonant is still to come is the bug this whole
        /// table exists to stop. Binary search; the table is sorted by
        /// construction, and this is called once per reveal step.
        /// </summary>
        public static int RevealedBy(List<int> unitStarts, int grapheme)
        {
            if (unitStarts == null || unitStarts.Count < 2 || grapheme <= 0) return 0;
            int lo = 0, hi = unitStarts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (unitStarts[mid] <= grapheme) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>
        /// The first unit that starts at or after <paramref name="grapheme"/>,
        /// or the unit count when there is none.
        ///
        /// Where a positional pause lands. A <c>&lt;wait&gt;</c> written inside
        /// a cluster (between a Thai leading vowel and its consonant, which a
        /// writer can absolutely do by accident) has to fall on the next unit
        /// boundary rather than split the cluster it was written into.
        /// </summary>
        public static int FirstUnitAtOrAfter(List<int> unitStarts, int grapheme)
        {
            if (unitStarts == null || unitStarts.Count < 2) return 0;
            int lo = 0, hi = unitStarts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (unitStarts[mid] < grapheme) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
