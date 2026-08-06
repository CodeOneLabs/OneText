#!/usr/bin/env python3
"""Generates Runtime/Core/Unicode/BreakData.g.cs from UCD data files.

Usage: python3 gen_break_tables.py <ucd-dir> <output.cs>
Needs, in <ucd-dir>: DerivedLineBreak.txt, DerivedGeneralCategory.txt,
EastAsianWidth.txt, GraphemeBreakProperty.txt, WordBreakProperty.txt,
SentenceBreakProperty.txt, DerivedCoreProperties.txt, emoji-data.txt.

Line_Break values are pre-resolved per UAX #14 rule LB1 (AI/SG/XX to AL,
SA to CM or AL by General_Category, CJ to NS), so the runtime never needs
General_Category for line breaking.
"""
import sys, os

MAX_CP = 0x110000

# Line_Break values that survive LB1 resolution. AL is index 0 (the lookup default).
LINE_CLASSES = ("AL AK AP AS B2 BA BB BK CB CL CM CP CR EB EM EX GL H2 H3 HH HL HY "
                "ID IN IS JL JT JV LF NL NS NU OP PO PR QU RI SP SY VF VI WJ ZW ZWJ").split()
LINE2IDX = {n: i for i, n in enumerate(LINE_CLASSES)}

# Grapheme_Cluster_Break; Other is index 0.
GRAPHEME_CLASSES = ("Other CR LF Control Extend ZWJ Regional_Indicator Prepend "
                    "SpacingMark L V T LV LVT").split()
GRAPHEME2IDX = {n: i for i, n in enumerate(GRAPHEME_CLASSES)}

# Word_Break; Other is index 0.
WORD_CLASSES = ("Other CR LF Newline Extend ZWJ Regional_Indicator Format Katakana "
                "Hebrew_Letter ALetter Single_Quote Double_Quote MidNumLet MidLetter "
                "MidNum Numeric ExtendNumLet WSegSpace").split()
WORD2IDX = {n: i for i, n in enumerate(WORD_CLASSES)}

# Sentence_Break; Other is index 0.
SENTENCE_CLASSES = ("Other CR LF Extend Sep Format Sp Lower Upper OLetter Numeric "
                    "ATerm SContinue STerm Close").split()
SENTENCE2IDX = {n: i for i, n in enumerate(SENTENCE_CLASSES)}

# Indic_Conjunct_Break; None is index 0.
INCB_CLASSES = "None Consonant Extend Linker".split()
INCB2IDX = {n: i for i, n in enumerate(INCB_CLASSES)}

FLAG_PI = 1        # General_Category = Pi (initial punctuation)
FLAG_PF = 2        # General_Category = Pf (final punctuation)
FLAG_EA = 4        # East_Asian_Width in {F, W, H}
FLAG_PICT = 8      # Extended_Pictographic
FLAG_CN = 16       # General_Category = Cn (unassigned)


def parse(path, wanted=None):
    """Yields (lo, hi, value) for a semicolon-delimited UCD property file."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            body = line.split("#")[0].strip()
            if not body or ";" not in body:
                continue
            fields = [x.strip() for x in body.split(";")]
            cps, value = fields[0], fields[1]
            if wanted is not None and value not in wanted:
                continue
            lo, hi = (cps.split("..") + [None])[:2]
            lo = int(lo, 16)
            yield lo, int(hi, 16) if hi else lo, value


LONG2SHORT = {"Ideographic": "ID", "Prefix_Numeric": "PR", "Alphabetic": "AL"}


def missing_defaults(path):
    """Yields (lo, hi, value) for the '# @missing:' default ranges of a UCD file."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.startswith("# @missing:"):
                continue
            cps, value = [x.strip() for x in line[len("# @missing:"):].split(";")[:2]]
            lo, hi = (cps.split("..") + [None])[:2]
            lo = int(lo, 16)
            yield lo, int(hi, 16) if hi else lo, value


def fill(path, table, mapping, wanted=None):
    for lo, hi, value in parse(path, wanted):
        idx = mapping[value]
        for cp in range(lo, hi + 1):
            table[cp] = idx


def ranges(table, default):
    """Merges a per-codepoint table into sorted ranges, dropping the default."""
    out = []
    for cp in range(MAX_CP):
        v = table[cp]
        if v == default:
            continue
        if out and out[-1][1] == cp - 1 and out[-1][2] == v:
            out[-1][1] = cp
        else:
            out.append([cp, cp, v])
    return out


def emit(name, rows, value_type):
    starts = ",".join(f"0x{lo:X}" for lo, _, _ in rows)
    ends = ",".join(f"0x{hi:X}" for _, hi, _ in rows)
    values = ",".join(str(v) for _, _, v in rows)
    return (f"        private static readonly int[] {name}Starts = {{{starts}}};\n"
            f"        private static readonly int[] {name}Ends = {{{ends}}};\n"
            f"        private static readonly {value_type}[] {name}Values = {{{values}}};\n")


def lookup(name, table, value_type, default, doc):
    return f"""
        /// <summary>{doc}</summary>
        public static {value_type} {name}(int codepoint)
        {{
            int lo = 0, hi = {table}Starts.Length - 1;
            while (lo <= hi)
            {{
                int mid = (lo + hi) >> 1;
                if (codepoint < {table}Starts[mid]) hi = mid - 1;
                else if (codepoint > {table}Ends[mid]) lo = mid + 1;
                else return ({value_type}){table}Values[mid];
            }}
            return {default};
        }}
"""


def main():
    ucd, out = sys.argv[1], sys.argv[2]
    p = lambda f: os.path.join(ucd, f)

    # General_Category first: LB1 needs Mn/Mc, and the flags need Pi/Pf/Cn.
    gc = ["Cn"] * MAX_CP
    for lo, hi, value in parse(p("DerivedGeneralCategory.txt")):
        for cp in range(lo, hi + 1):
            gc[cp] = value

    # Line_Break, resolved through LB1. The @missing lines carry the defaults
    # for unassigned code points (CJK and emoji blocks default to ID, the
    # currency block to PR), which rules LB23a and LB30b depend on.
    line = [LINE2IDX["AL"]] * MAX_CP
    for lo, hi, value in missing_defaults(p("DerivedLineBreak.txt")):
        idx = LINE2IDX[LONG2SHORT.get(value, value)] if value != "Unknown" else LINE2IDX["AL"]
        for cp in range(lo, hi + 1):
            line[cp] = idx
    for lo, hi, value in parse(p("DerivedLineBreak.txt")):
        if value in ("AI", "SG", "XX"):
            resolved = "AL"
        elif value == "CJ":
            resolved = "NS"
        elif value == "SA":
            resolved = None  # per-codepoint, depends on General_Category
        else:
            resolved = value
        for cp in range(lo, hi + 1):
            r = resolved or ("CM" if gc[cp] in ("Mn", "Mc") else "AL")
            line[cp] = LINE2IDX[r]

    grapheme = [0] * MAX_CP
    fill(p("GraphemeBreakProperty.txt"), grapheme, GRAPHEME2IDX)

    word = [0] * MAX_CP
    fill(p("WordBreakProperty.txt"), word, WORD2IDX)

    sentence = [0] * MAX_CP
    fill(p("SentenceBreakProperty.txt"), sentence, SENTENCE2IDX)

    # InCB rows carry the value in a third field: "cp ; InCB ; Linker".
    incb = [0] * MAX_CP
    with open(p("DerivedCoreProperties.txt"), encoding="utf-8") as f:
        for raw in f:
            body = raw.split("#")[0].strip()
            if not body or body.count(";") < 2:
                continue
            cps, prop, value = [x.strip() for x in body.split(";")[:3]]
            if prop != "InCB":
                continue
            lo, hi = (cps.split("..") + [None])[:2]
            lo = int(lo, 16)
            hi = int(hi, 16) if hi else lo
            for cp in range(lo, hi + 1):
                incb[cp] = INCB2IDX[value]

    flags = [0] * MAX_CP
    for cp in range(MAX_CP):
        c = gc[cp]
        if c == "Pi":
            flags[cp] |= FLAG_PI
        elif c == "Pf":
            flags[cp] |= FLAG_PF
        elif c == "Cn":
            flags[cp] |= FLAG_CN
    for lo, hi, _ in parse(p("EastAsianWidth.txt"), wanted={"F", "W", "H"}):
        for cp in range(lo, hi + 1):
            flags[cp] |= FLAG_EA
    for lo, hi, _ in parse(p("emoji-data.txt"), wanted={"Extended_Pictographic"}):
        for cp in range(lo, hi + 1):
            flags[cp] |= FLAG_PICT

    version = "unknown"
    with open(p("DerivedLineBreak.txt"), encoding="utf-8") as f:
        first = f.readline()
        if "-" in first:
            version = first.split("-", 1)[1].split(".txt")[0]

    line_rows = ranges(line, LINE2IDX["AL"])
    grapheme_rows = ranges(grapheme, 0)
    word_rows = ranges(word, 0)
    sentence_rows = ranges(sentence, 0)
    incb_rows = ranges(incb, 0)
    flag_rows = ranges(flags, 0)

    body = (emit("Line", line_rows, "byte") + "\n" +
            emit("Grapheme", grapheme_rows, "byte") + "\n" +
            emit("Word", word_rows, "byte") + "\n" +
            emit("Sentence", sentence_rows, "byte") + "\n" +
            emit("Incb", incb_rows, "byte") + "\n" +
            emit("Flag", flag_rows, "byte") +
            lookup("GetLineClass", "Line", "LineBreakClass", "LineBreakClass.AL",
                   "Line_Break, pre-resolved per LB1. Codepoints not listed are AL.") +
            lookup("GetGraphemeClass", "Grapheme", "GraphemeBreakClass", "GraphemeBreakClass.Other",
                   "Grapheme_Cluster_Break (UAX #29).") +
            lookup("GetWordClass", "Word", "WordBreakClass", "WordBreakClass.Other",
                   "Word_Break (UAX #29).") +
            lookup("GetSentenceClass", "Sentence", "SentenceBreakClass",
                   "SentenceBreakClass.Other", "Sentence_Break (UAX #29).") +
            lookup("GetIncb", "Incb", "IncbClass", "IncbClass.None",
                   "Indic_Conjunct_Break, used by GB9c.") +
            lookup("GetFlags", "Flag", "CharFlags", "CharFlags.None",
                   "Auxiliary properties used by the break rules."))

    with open(out, "w", encoding="utf-8") as f:
        f.write(f"""// <auto-generated>
// Generated by Tools/gen_break_tables.py from the Unicode Character Database
// (DerivedLineBreak / DerivedGeneralCategory / EastAsianWidth /
// GraphemeBreakProperty / WordBreakProperty / SentenceBreakProperty /
// DerivedCoreProperties / emoji-data, UCD version {version}).
// Data (c) Unicode, Inc., distributed under the Unicode License v3
// (https://www.unicode.org/license.txt). Do not edit by hand.
// </auto-generated>

namespace OneText.Unicode
{{
    internal static class BreakData
    {{
        // Sorted, non-overlapping ranges; codepoints outside them take the
        // default value returned by each lookup.
{body}    }}
}}
""")
    print(f"line={len(line_rows)} grapheme={len(grapheme_rows)} word={len(word_rows)} "
          f"sentence={len(sentence_rows)} incb={len(incb_rows)} flags={len(flag_rows)} -> {out}")


if __name__ == "__main__":
    main()
