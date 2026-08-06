#!/usr/bin/env python3
"""Dumps a handful of glyph outlines as SVG paths, for the landing page's globe.

The page extrudes these into real geometry, the same way the sibling project's
page extrudes its logo, so the glyphs need outlines, not a font. Baking them
here rather than loading a font at runtime keeps the page to one request and
means a script the visitor has no font for still renders: by then it is
geometry, not text.

Outlines are quadratic in a TrueType file and cubic in SVG; fontTools converts
on the way out. The coordinates stay in font space, which is y-up; only the
path *syntax* is SVG's. The page must not flip y: these are already the way a
y-up scene wants them, and an earlier "correction" turned every glyph on the
ball upside down.

Run: python3 Tools/extract_glyph_paths.py > page/glyphs.js
"""

import json
import os
import sys

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.ttLib import TTFont

COVERAGE = "Tests/CoverageFonts~"

# One glyph per writing system, chosen for a shape that reads at thumbnail size
# and survives being turned edge-on: closed counters and a strong stem beat
# something wispy.
WANTED = [
    ("Arabic",     "\u0639", f"{COVERAGE}/NotoSansArabic-Regular.ttf", "Tests/Fonts/NotoSansArabic.ttf"),
    ("Arabic",     "\u062c", f"{COVERAGE}/NotoSansArabic-Regular.ttf", "Tests/Fonts/NotoSansArabic.ttf"),
    ("Arabic",     "\u0635", f"{COVERAGE}/NotoSansArabic-Regular.ttf", "Tests/Fonts/NotoSansArabic.ttf"),
    ("Greek",      "\u03a9", "Tests/Fonts/NotoSans.ttf", None),
    ("Greek",      "\u03bb", "Tests/Fonts/NotoSans.ttf", None),
    ("Greek",      "\u03be", "Tests/Fonts/NotoSans.ttf", None),
    ("Latin",      "R",       "Tests/Fonts/NotoSans.ttf", None),
    ("Latin",      "&",       "Tests/Fonts/NotoSans.ttf", None),
    ("Latin",      "g",       "Tests/Fonts/NotoSans.ttf", None),
    ("Cyrillic",   "\u0416", "Tests/Fonts/NotoSans.ttf", None),
    ("Cyrillic",   "\u0414", "Tests/Fonts/NotoSans.ttf", None),
    ("Hangul",     "\ud55c", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Hangul",     "\uae00", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Hangul",     "\uc6d0", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Han",        "\u6c38", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Han",        "\u9f8d", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Han",        "\u611b", f"{COVERAGE}/NotoSansCJKkr-Regular.otf", None),
    ("Kana",       "\u3042", f"{COVERAGE}/NotoSansCJKjp-Regular.otf", None),
    ("Kana",       "\u30e6", f"{COVERAGE}/NotoSansCJKjp-Regular.otf", None),
    ("Devanagari", "\u0915", f"{COVERAGE}/NotoSansDevanagari-Regular.ttf", None),
    ("Devanagari", "\u092e", f"{COVERAGE}/NotoSansDevanagari-Regular.ttf", None),
    ("Thai",       "\u0e01", f"{COVERAGE}/NotoSansThai-Regular.ttf", None),
    ("Thai",       "\u0e2a", f"{COVERAGE}/NotoSansThai-Regular.ttf", None),
    ("Hebrew",     "\u05d0", f"{COVERAGE}/NotoSansHebrew-Regular.ttf", None),
    ("Hebrew",     "\u05e9", f"{COVERAGE}/NotoSansHebrew-Regular.ttf", None),
    ("Georgian",   "\u10d0", f"{COVERAGE}/NotoSansGeorgian-Regular.ttf", None),
    ("Armenian",   "\u0562", f"{COVERAGE}/NotoSansArmenian-Regular.ttf", None),
    ("Ethiopic",   "\u1200", f"{COVERAGE}/NotoSansEthiopic-Regular.ttf", None),
    ("Malayalam",  "\u0d15", f"{COVERAGE}/NotoSansMalayalam-Regular.ttf", None),
    ("Tamil",      "\u0b95", f"{COVERAGE}/NotoSansTamil-Regular.ttf", None),
    ("Bengali",    "\u0995", f"{COVERAGE}/NotoSansBengali-Regular.ttf", None),
    ("Telugu",     "\u0c15", f"{COVERAGE}/NotoSansTelugu-Regular.ttf", None),
    ("Kannada",    "\u0c95", f"{COVERAGE}/NotoSansKannada-Regular.ttf", None),
    ("Gujarati",   "\u0a95", f"{COVERAGE}/NotoSansGujarati-Regular.ttf", None),
    ("Gurmukhi",   "\u0a15", f"{COVERAGE}/NotoSansGurmukhi-Regular.ttf", None),
    ("Oriya",      "\u0b15", f"{COVERAGE}/NotoSansOriya-Regular.ttf", None),
    ("Sinhala",    "\u0d9a", f"{COVERAGE}/NotoSansSinhala-Regular.ttf", None),
    ("Khmer",      "\u1780", f"{COVERAGE}/NotoSansKhmer-Regular.ttf", None),
    ("Lao",        "\u0e81", f"{COVERAGE}/NotoSansLao-Regular.ttf", None),
    ("Myanmar",    "\u1000", f"{COVERAGE}/NotoSansMyanmar-Regular.ttf", None),
    ("Tibetan",    "\u0f40", f"{COVERAGE}/NotoSansTibetan-Regular.ttf", None),
    ("Thaana",     "\u0780", f"{COVERAGE}/NotoSansThaana-Regular.ttf", None),
    ("Syriac",     "\u0710", f"{COVERAGE}/NotoSansSyriac-Regular.ttf", None),
    ("N'Ko",       "\u07ca", f"{COVERAGE}/NotoSansNKo-Regular.ttf", None),
    ("Adlam",      "\U0001e900", f"{COVERAGE}/NotoSansAdlam-Regular.ttf", None),
    ("Cherokee",   "\u13a0", f"{COVERAGE}/NotoSansCherokee-Regular.ttf", None),
    ("Runic",      "\u16a0", f"{COVERAGE}/NotoSansRunic-Regular.ttf", None),
    ("Coptic",     "\u2c80", f"{COVERAGE}/NotoSansCoptic-Regular.ttf", None),
    ("Glagolitic", "\U0001e000", f"{COVERAGE}/NotoSansGlagolitic-Regular.ttf", None),
    ("Mongolian",  "\u1820", f"{COVERAGE}/NotoSansMongolian-Regular.ttf", None),
    ("Osage",      "\U000104b0", f"{COVERAGE}/NotoSansOsage-Regular.ttf", None),
    ("Vai",        "\ua500", f"{COVERAGE}/NotoSansVai-Regular.ttf", None),
]

_cache = {}


def load(path):
    if path not in _cache:
        font = TTFont(path, fontNumber=0, lazy=True)
        _cache[path] = (font, font.getGlyphSet(), font["head"].unitsPerEm)
    return _cache[path]


def outline(codepoint, *paths):
    """The glyph's contours as an SVG path, scaled into a 1-unit em box."""
    for path in paths:
        if not path or not os.path.exists(path):
            continue
        font, glyphs, upem = load(path)
        name = font.getBestCmap().get(ord(codepoint))
        if not name or name not in glyphs:
            continue
        pen = SVGPathPen(glyphs)
        glyphs[name].draw(pen)
        d = pen.getCommands()
        if d.strip():
            return d, upem
    return None, None


def main():
    out = []
    for script, char, primary, fallback in WANTED:
        d, upem = outline(char, primary, fallback)
        if not d:
            print(f"missing {script} U+{ord(char):04X}", file=sys.stderr)
            continue
        out.append({"script": script, "d": d, "upem": upem})

    print("// Generated by Tools/extract_glyph_paths.py. Do not edit by hand.")
    print("export const GLYPHS = " + json.dumps(out, separators=(",", ":")) + ";")
    print(f"extracted {len(out)} glyphs", file=sys.stderr)


if __name__ == "__main__":
    main()
