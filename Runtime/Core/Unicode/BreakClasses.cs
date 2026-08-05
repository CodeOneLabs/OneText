namespace OneText.Unicode
{
    /// <summary>
    /// Line_Break property values (UAX #14, Table 1), as resolved by rule LB1 —
    /// AI/SG/XX are already folded into AL, CJ into NS, SA into CM or AL.
    /// </summary>
    public enum LineBreakClass : byte
    {
        AL, AK, AP, AS, B2, BA, BB, BK, CB, CL, CM, CP, CR, EB, EM, EX, GL, H2, H3,
        HH, HL, HY, ID, IN, IS, JL, JT, JV, LF, NL, NS, NU, OP, PO, PR, QU, RI, SP,
        SY, VF, VI, WJ, ZW, ZWJ,
    }

    /// <summary>Grapheme_Cluster_Break property values (UAX #29, Table 2).</summary>
    public enum GraphemeBreakClass : byte
    {
        Other, CR, LF, Control, Extend, ZWJ, RegionalIndicator, Prepend,
        SpacingMark, L, V, T, LV, LVT,
    }

    /// <summary>Word_Break property values (UAX #29, Table 3).</summary>
    public enum WordBreakClass : byte
    {
        Other, CR, LF, Newline, Extend, ZWJ, RegionalIndicator, Format, Katakana,
        HebrewLetter, ALetter, SingleQuote, DoubleQuote, MidNumLet, MidLetter,
        MidNum, Numeric, ExtendNumLet, WSegSpace,
    }

    /// <summary>Sentence_Break property values (UAX #29, Table 4).</summary>
    public enum SentenceBreakClass : byte
    {
        Other, CR, LF, Extend, Sep, Format, Sp, Lower, Upper, OLetter, Numeric,
        ATerm, SContinue, STerm, Close,
    }

    /// <summary>Indic_Conjunct_Break property values, used by GB9c.</summary>
    public enum IncbClass : byte
    {
        None, Consonant, Extend, Linker,
    }

    /// <summary>
    /// Auxiliary character properties the break rules consult beyond the
    /// break classes themselves.
    /// </summary>
    [System.Flags]
    public enum CharFlags : byte
    {
        None = 0,

        /// <summary>General_Category = Pi (initial quotation punctuation).</summary>
        InitialPunctuation = 1,

        /// <summary>General_Category = Pf (final quotation punctuation).</summary>
        FinalPunctuation = 2,

        /// <summary>East_Asian_Width is Fullwidth, Wide or Halfwidth ($EastAsian).</summary>
        EastAsianWide = 4,

        /// <summary>Extended_Pictographic.</summary>
        Pictographic = 8,

        /// <summary>General_Category = Cn (unassigned).</summary>
        Unassigned = 16,
    }
}
