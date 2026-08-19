using System;
using System.Collections.Generic;

namespace OneText.Unicode
{
    /// <summary>
    /// Full UAX #14 line breaking: computes where a line is allowed to break
    /// and where it must break. Rules LB1-LB31 of Unicode 17.0, including the
    /// combining-mark folding of LB9/LB10, the number expressions of LB25 and
    /// the Brahmic orthographic syllables of LB28a.
    /// Validated against the complete LineBreakTest.txt.
    /// </summary>
    public static class LineBreaker
    {
        /// <summary>What the algorithm permits at a given position.</summary>
        public enum Opportunity : byte
        {
            /// <summary>No line break allowed here.</summary>
            None = 0,

            /// <summary>A line may break here if the text does not fit.</summary>
            Allowed = 1,

            /// <summary>A line break is required here (LB4/LB5: newlines).</summary>
            Mandatory = 2,
        }

        private struct Item
        {
            public int Offset;              // UTF-16 offset of the base codepoint
            public LineBreakClass Class;    // after LB1/LB9/LB10
            public CharFlags Flags;
            public bool DottedCircle;       // U+25CC, referenced explicitly by LB28a
            public bool ZwjBefore;          // LB8a: the preceding code point is ZWJ
        }

        [System.ThreadStatic] private static List<Item> t_items;

        /// <summary>
        /// Fills <paramref name="opportunities"/> with the break opportunity
        /// <em>before</em> each UTF-16 index of <paramref name="text"/>. The
        /// array must hold at least <c>text.Length + 1</c> entries; index 0 is
        /// always <see cref="Opportunity.None"/> (LB2) and index
        /// <c>text.Length</c> is always <see cref="Opportunity.Mandatory"/> (LB3).
        /// </summary>
        public static void Analyze(string text, Opportunity[] opportunities) =>
            Analyze(text.AsSpan(), opportunities);

        /// <inheritdoc cref="Analyze(string, Opportunity[])"/>
        public static void Analyze(ReadOnlySpan<char> text, Opportunity[] opportunities)
        {
            int n = text.Length;
            System.Array.Clear(opportunities, 0, n + 1);
            opportunities[n] = Opportunity.Mandatory; // LB3
            if (n == 0) return;

            var items = t_items ??= new List<Item>();
            items.Clear();

            // LB1 (in the tables) + LB9 folding + LB10.
            var previousRaw = (LineBreakClass)255;
            for (int i = 0; i < n;)
            {
                int cp = Codepoint(text, i);
                int width = char.IsHighSurrogate(text[i]) ? 2 : 1;
                var raw = BreakData.GetLineClass(cp);

                bool combining = raw == LineBreakClass.CM || raw == LineBreakClass.ZWJ;
                // LB9: a mark on a base takes that base's class and drops out
                // of the rule stream entirely.
                bool attaches = combining && i > 0 && !IsBreakBase(previousRaw);
                if (!attaches)
                {
                    var cls = raw;
                    var flags = BreakData.GetFlags(cp);
                    bool dottedCircle = cp == 0x25CC;
                    if (combining)
                    {
                        // LB10: a leftover mark behaves exactly like U+0041 A.
                        cls = LineBreakClass.AL;
                        flags = CharFlags.None;
                        dottedCircle = false;
                    }
                    items.Add(new Item
                    {
                        Offset = i,
                        Class = cls,
                        Flags = flags,
                        DottedCircle = dottedCircle,
                        ZwjBefore = previousRaw == LineBreakClass.ZWJ,
                    });
                }

                previousRaw = raw;
                i += width;
            }

            for (int k = 1; k < items.Count; k++)
                opportunities[items[k].Offset] = Decide(items, k, out _);
        }

        /// <summary>
        /// The rule that decided the boundary before <paramref name="index"/>,
        /// by its name in UAX #14 ("LB13", "LB30a"), or null when the index is
        /// not a boundary between two items.
        ///
        /// Diagnostics only, and deliberately a second pass rather than
        /// bookkeeping the first one carries: half of all text-rendering
        /// questions are "why did it break there", and the answer stops being
        /// an opinion once it is a rule number somebody can look up.
        /// </summary>
        public static string RuleAt(string text, int index) => RuleAt(text.AsSpan(), index);

        /// <inheritdoc cref="RuleAt(string, int)"/>
        public static string RuleAt(ReadOnlySpan<char> text, int index)
        {
            if (text.IsEmpty || index <= 0 || index > text.Length) return null;
            if (index == text.Length) return "LB3";

            var opportunities = new Opportunity[text.Length + 1];
            Analyze(text, opportunities);

            var items = t_items;
            if (items == null) return null;
            for (int k = 1; k < items.Count; k++)
            {
                if (items[k].Offset != index) continue;
                Decide(items, k, out string rule);
                return rule;
            }
            // Inside a grapheme that LB9 folded away: no rule applies, which is
            // itself the answer: the mark went with its base.
            return "LB9";
        }

        /// <summary>Convenience overload allocating the result array.</summary>
        public static Opportunity[] Analyze(string text) => Analyze(text.AsSpan());

        /// <inheritdoc cref="Analyze(string)"/>
        public static Opportunity[] Analyze(ReadOnlySpan<char> text)
        {
            var result = new Opportunity[(text.Length) + 1];
            Analyze(text, result);
            return result;
        }

        /// <summary>
        /// The rule that fired, for <see cref="RuleAt"/>. An out-parameter
        /// rather than a field: the caller that does not want it passes a
        /// discard, and nothing about the hot path changes.
        /// </summary>
        private static Opportunity Rule(string name, Opportunity result, out string rule)
        {
            rule = name;
            return result;
        }

        private static Opportunity Decide(List<Item> items, int k, out string rule)
        {
            int m = items.Count;
            var b = items[k - 1].Class;
            var a = items[k].Class;

            // LB4, LB5, LB6
            if (b == LineBreakClass.BK) return Rule("LB4", Opportunity.Mandatory, out rule);
            if (b == LineBreakClass.CR && a == LineBreakClass.LF)
                return Rule("LB5", Opportunity.None, out rule);
            if (b == LineBreakClass.CR || b == LineBreakClass.LF || b == LineBreakClass.NL)
                return Rule("LB5", Opportunity.Mandatory, out rule);
            if (a == LineBreakClass.BK || a == LineBreakClass.CR ||
                a == LineBreakClass.LF || a == LineBreakClass.NL)
                return Rule("LB6", Opportunity.None, out rule);

            // LB7
            if (a == LineBreakClass.SP || a == LineBreakClass.ZW)
                return Rule("LB7", Opportunity.None, out rule);

            // LB8: ZW SP* ÷
            int s = SkipSpacesBack(items, k - 1);
            if (s >= 0 && items[s].Class == LineBreakClass.ZW)
                return Rule("LB8", Opportunity.Allowed, out rule);

            // LB8a
            if (items[k].ZwjBefore) return Rule("LB8a", Opportunity.None, out rule);

            // LB11, LB12, LB12a
            if (a == LineBreakClass.WJ || b == LineBreakClass.WJ)
                return Rule("LB11", Opportunity.None, out rule);
            if (b == LineBreakClass.GL) return Rule("LB12", Opportunity.None, out rule);
            if (a == LineBreakClass.GL && b != LineBreakClass.SP && b != LineBreakClass.BA &&
                b != LineBreakClass.HY && b != LineBreakClass.HH)
                return Rule("LB12a", Opportunity.None, out rule);

            // LB13
            if (a == LineBreakClass.CL || a == LineBreakClass.CP ||
                a == LineBreakClass.EX || a == LineBreakClass.SY)
                return Rule("LB13", Opportunity.None, out rule);

            // LB14: OP SP* ×
            if (s >= 0 && items[s].Class == LineBreakClass.OP)
                return Rule("LB14", Opportunity.None, out rule);

            // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [Pi&QU] SP* ×
            if (s >= 0 && items[s].Class == LineBreakClass.QU && Has(items[s], CharFlags.InitialPunctuation))
            {
                if (s == 0) return Rule("LB15a", Opportunity.None, out rule);
                var before = items[s - 1].Class;
                if (before == LineBreakClass.BK || before == LineBreakClass.CR ||
                    before == LineBreakClass.LF || before == LineBreakClass.NL ||
                    before == LineBreakClass.OP || before == LineBreakClass.QU ||
                    before == LineBreakClass.GL || before == LineBreakClass.SP ||
                    before == LineBreakClass.ZW)
                    return Rule("LB15a", Opportunity.None, out rule);
            }

            // LB15b: × [Pf&QU] (SP | GL | WJ | CL | QU | CP | EX | IS | SY | BK | CR | LF | NL | ZW | eot)
            if (a == LineBreakClass.QU && Has(items[k], CharFlags.FinalPunctuation))
            {
                if (k + 1 >= m) return Rule("LB15b", Opportunity.None, out rule);
                var next = items[k + 1].Class;
                if (next == LineBreakClass.SP || next == LineBreakClass.GL ||
                    next == LineBreakClass.WJ || next == LineBreakClass.CL ||
                    next == LineBreakClass.QU || next == LineBreakClass.CP ||
                    next == LineBreakClass.EX || next == LineBreakClass.IS ||
                    next == LineBreakClass.SY || next == LineBreakClass.BK ||
                    next == LineBreakClass.CR || next == LineBreakClass.LF ||
                    next == LineBreakClass.NL || next == LineBreakClass.ZW)
                    return Rule("LB15b", Opportunity.None, out rule);
            }

            // LB15c, LB15d
            if (b == LineBreakClass.SP && a == LineBreakClass.IS &&
                k + 1 < m && items[k + 1].Class == LineBreakClass.NU)
                return Rule("LB15c", Opportunity.Allowed, out rule);
            if (a == LineBreakClass.IS) return Rule("LB15d", Opportunity.None, out rule);

            // LB16: (CL | CP) SP* × NS
            if (a == LineBreakClass.NS && s >= 0 &&
                (items[s].Class == LineBreakClass.CL || items[s].Class == LineBreakClass.CP))
                return Rule("LB16", Opportunity.None, out rule);

            // LB17: B2 SP* × B2
            if (a == LineBreakClass.B2 && s >= 0 && items[s].Class == LineBreakClass.B2)
                return Rule("LB17", Opportunity.None, out rule);

            // LB18
            if (b == LineBreakClass.SP) return Rule("LB18", Opportunity.Allowed, out rule);

            // LB19
            if (a == LineBreakClass.QU && !Has(items[k], CharFlags.InitialPunctuation))
                return Rule("LB19", Opportunity.None, out rule);
            if (b == LineBreakClass.QU && !Has(items[k - 1], CharFlags.FinalPunctuation))
                return Rule("LB19", Opportunity.None, out rule);

            // LB19a: quotation marks break only when East Asian text surrounds them
            if (a == LineBreakClass.QU)
            {
                if (!Has(items[k - 1], CharFlags.EastAsianWide))
                    return Rule("LB19a", Opportunity.None, out rule);
                if (k + 1 >= m || !Has(items[k + 1], CharFlags.EastAsianWide))
                    return Rule("LB19a", Opportunity.None, out rule);
            }
            if (b == LineBreakClass.QU)
            {
                if (!Has(items[k], CharFlags.EastAsianWide))
                    return Rule("LB19a", Opportunity.None, out rule);
                if (k - 2 < 0 || !Has(items[k - 2], CharFlags.EastAsianWide))
                    return Rule("LB19a", Opportunity.None, out rule);
            }

            // LB20
            if (a == LineBreakClass.CB || b == LineBreakClass.CB)
                return Rule("LB20", Opportunity.Allowed, out rule);

            // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | HH) × (AL | HL)
            if ((b == LineBreakClass.HY || b == LineBreakClass.HH) && IsAlphabetic(a))
            {
                if (k - 2 < 0) return Rule("LB20a", Opportunity.None, out rule);
                var before = items[k - 2].Class;
                if (before == LineBreakClass.BK || before == LineBreakClass.CR ||
                    before == LineBreakClass.LF || before == LineBreakClass.NL ||
                    before == LineBreakClass.SP || before == LineBreakClass.ZW ||
                    before == LineBreakClass.CB || before == LineBreakClass.GL)
                    return Rule("LB20a", Opportunity.None, out rule);
            }

            // LB21, LB21a, LB21b
            if (a == LineBreakClass.BA || a == LineBreakClass.HH ||
                a == LineBreakClass.HY || a == LineBreakClass.NS || b == LineBreakClass.BB)
                return Rule("LB21", Opportunity.None, out rule);
            if (k >= 2 && items[k - 2].Class == LineBreakClass.HL &&
                (b == LineBreakClass.HY || b == LineBreakClass.HH) && a != LineBreakClass.HL)
                return Rule("LB21a", Opportunity.None, out rule);
            if (b == LineBreakClass.SY && a == LineBreakClass.HL)
                return Rule("LB21b", Opportunity.None, out rule);

            // LB22
            if (a == LineBreakClass.IN) return Rule("LB22", Opportunity.None, out rule);

            // LB23, LB23a, LB24
            if (IsAlphabetic(b) && a == LineBreakClass.NU)
                return Rule("LB23", Opportunity.None, out rule);
            if (b == LineBreakClass.NU && IsAlphabetic(a))
                return Rule("LB23", Opportunity.None, out rule);
            if (b == LineBreakClass.PR && IsIdeographic(a))
                return Rule("LB23a", Opportunity.None, out rule);
            if (IsIdeographic(b) && a == LineBreakClass.PO)
                return Rule("LB23a", Opportunity.None, out rule);
            if (IsAffix(b) && IsAlphabetic(a)) return Rule("LB24", Opportunity.None, out rule);
            if (IsAlphabetic(b) && IsAffix(a)) return Rule("LB24", Opportunity.None, out rule);

            // LB25: numbers, including the surrounding prefixes and postfixes
            if (IsAffix(a))
            {
                if ((b == LineBreakClass.CL || b == LineBreakClass.CP) && EndsNumber(items, k - 2))
                    return Rule("LB25", Opportunity.None, out rule);
                if (EndsNumber(items, k - 1)) return Rule("LB25", Opportunity.None, out rule);
            }
            if (IsAffix(b))
            {
                if (a == LineBreakClass.NU) return Rule("LB25", Opportunity.None, out rule);
                if (a == LineBreakClass.OP)
                {
                    if (k + 1 < m && items[k + 1].Class == LineBreakClass.NU)
                        return Rule("LB25", Opportunity.None, out rule);
                    if (k + 2 < m && items[k + 1].Class == LineBreakClass.IS &&
                        items[k + 2].Class == LineBreakClass.NU)
                        return Rule("LB25", Opportunity.None, out rule);
                }
            }
            if ((b == LineBreakClass.HY || b == LineBreakClass.IS) && a == LineBreakClass.NU)
                return Rule("LB25", Opportunity.None, out rule);
            if (a == LineBreakClass.NU && EndsNumber(items, k - 1))
                return Rule("LB25", Opportunity.None, out rule);

            // LB26, LB27: Korean syllable blocks
            if (b == LineBreakClass.JL &&
                (a == LineBreakClass.JL || a == LineBreakClass.JV ||
                 a == LineBreakClass.H2 || a == LineBreakClass.H3))
                return Rule("LB26", Opportunity.None, out rule);
            if ((b == LineBreakClass.JV || b == LineBreakClass.H2) &&
                (a == LineBreakClass.JV || a == LineBreakClass.JT))
                return Rule("LB26", Opportunity.None, out rule);
            if ((b == LineBreakClass.JT || b == LineBreakClass.H3) && a == LineBreakClass.JT)
                return Rule("LB26", Opportunity.None, out rule);
            if (IsHangul(b) && a == LineBreakClass.PO)
                return Rule("LB27", Opportunity.None, out rule);
            if (b == LineBreakClass.PR && IsHangul(a))
                return Rule("LB27", Opportunity.None, out rule);

            // LB28
            if (IsAlphabetic(b) && IsAlphabetic(a)) return Rule("LB28", Opportunity.None, out rule);

            // LB28a: Brahmic orthographic syllables
            if (b == LineBreakClass.AP && IsAksara(items[k]))
                return Rule("LB28a", Opportunity.None, out rule);
            if (IsAksara(items[k - 1]) && (a == LineBreakClass.VF || a == LineBreakClass.VI))
                return Rule("LB28a", Opportunity.None, out rule);
            if (k >= 2 && IsAksara(items[k - 2]) && b == LineBreakClass.VI &&
                (a == LineBreakClass.AK || items[k].DottedCircle))
                return Rule("LB28a", Opportunity.None, out rule);
            if (IsAksara(items[k - 1]) && IsAksara(items[k]) &&
                k + 1 < m && items[k + 1].Class == LineBreakClass.VF)
                return Rule("LB28a", Opportunity.None, out rule);

            // LB29
            if (b == LineBreakClass.IS && IsAlphabetic(a))
                return Rule("LB29", Opportunity.None, out rule);

            // LB30: parentheses that are not East Asian
            if ((IsAlphabetic(b) || b == LineBreakClass.NU) && a == LineBreakClass.OP &&
                !Has(items[k], CharFlags.EastAsianWide))
                return Rule("LB30", Opportunity.None, out rule);
            if (b == LineBreakClass.CP && !Has(items[k - 1], CharFlags.EastAsianWide) &&
                (IsAlphabetic(a) || a == LineBreakClass.NU))
                return Rule("LB30", Opportunity.None, out rule);

            // LB30a: regional indicators pair up into flags
            if (b == LineBreakClass.RI && a == LineBreakClass.RI)
            {
                int count = 0;
                for (int j = k - 1; j >= 0 && items[j].Class == LineBreakClass.RI; j--) count++;
                if ((count & 1) != 0) return Rule("LB30a", Opportunity.None, out rule);
            }

            // LB30b
            if (b == LineBreakClass.EB && a == LineBreakClass.EM)
                return Rule("LB30b", Opportunity.None, out rule);
            if (a == LineBreakClass.EM &&
                Has(items[k - 1], CharFlags.Pictographic) && Has(items[k - 1], CharFlags.Unassigned))
                return Rule("LB30b", Opportunity.None, out rule);

            // LB31
            return Rule("LB31", Opportunity.Allowed, out rule);
        }

        /// <summary>Index of the last item at or before <paramref name="from"/> that is not SP.</summary>
        private static int SkipSpacesBack(List<Item> items, int from)
        {
            int j = from;
            while (j >= 0 && items[j].Class == LineBreakClass.SP) j--;
            return j;
        }

        /// <summary>True if items[0..end] ends with the LB25 sequence NU (SY | IS)*.</summary>
        private static bool EndsNumber(List<Item> items, int end)
        {
            int j = end;
            while (j >= 0 && (items[j].Class == LineBreakClass.SY || items[j].Class == LineBreakClass.IS))
                j--;
            return j >= 0 && items[j].Class == LineBreakClass.NU;
        }

        private static bool Has(Item item, CharFlags flag) => (item.Flags & flag) != 0;

        /// <summary>The (AK | ◌ | AS) set of LB28a.</summary>
        private static bool IsAksara(Item item) =>
            item.Class == LineBreakClass.AK || item.Class == LineBreakClass.AS || item.DottedCircle;

        private static bool IsAlphabetic(LineBreakClass c) =>
            c == LineBreakClass.AL || c == LineBreakClass.HL;

        private static bool IsAffix(LineBreakClass c) =>
            c == LineBreakClass.PR || c == LineBreakClass.PO;

        private static bool IsIdeographic(LineBreakClass c) =>
            c == LineBreakClass.ID || c == LineBreakClass.EB || c == LineBreakClass.EM;

        private static bool IsHangul(LineBreakClass c) =>
            c == LineBreakClass.JL || c == LineBreakClass.JV || c == LineBreakClass.JT ||
            c == LineBreakClass.H2 || c == LineBreakClass.H3;

        /// <summary>Classes that a combining mark cannot attach to (LB9).</summary>
        private static bool IsBreakBase(LineBreakClass c) =>
            c == LineBreakClass.BK || c == LineBreakClass.CR || c == LineBreakClass.LF ||
            c == LineBreakClass.NL || c == LineBreakClass.SP || c == LineBreakClass.ZW;

        /// <summary>The codepoint at <paramref name="index"/>; see Unicode.Utf16.</summary>
        private static int Codepoint(ReadOnlySpan<char> text, int index) =>
            Utf16.Codepoint(text, index);

    }
}
