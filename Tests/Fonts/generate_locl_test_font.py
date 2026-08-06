#!/usr/bin/env python3
"""Builds LoclRegional.ttf, the test face for locale-driven glyph selection.

M10 passes a BCP 47 tag through to HarfBuzz so OpenType `locl` can pick the
regional form of a shared codepoint: the Han unification fix, from the
shaping side rather than the fallback side. But every other test face is a
real-world font with no `locl` table, so what the suite could check was only
that a language tag does not corrupt ordinary text. That is the weaker half of
the claim: it says a tag is harmless, not that it works.

This face makes the strong half testable. U+76F4 (直), the codepoint the
Japanese and Chinese communities argue about, and the one the fallback test
already uses, is drawn three ways, and `locl` selects between them:

  han       the default form, for a label with no locale and for every
            language the feature says nothing about
  han.jp    selected under the OpenType language system JAN (BCP 47 "ja")
  han.zh    selected under ZHS (BCP 47 "zh-Hans")

The shapes are three bars at different heights, which is enough to tell the
glyphs apart by ID and enough to see in a render. U+4E00 (一) is here as the
control: a Han codepoint in the same script with no `locl` rule, so a test can
show the feature reaches the characters it claims and no further.

Requires fonttools. Run from this directory:  python3 generate_locl_test_font.py
"""

from fontTools.fontBuilder import FontBuilder
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPEM = 1000

# Three bars, one per regional form. Different heights so the glyphs differ as
# outlines and not only as IDs; a substitution that fired but drew the same
# picture would be indistinguishable from one that never fired.
BARS = {
    "han": (100, 900, 200, 700),
    "han.jp": (100, 900, 200, 500),
    "han.zh": (100, 900, 500, 700),
    "one": (100, 900, 400, 500),
}

FEATURES = """
languagesystem DFLT dflt;
languagesystem hani dflt;
languagesystem hani JAN;
languagesystem hani ZHS;

feature locl {
    script hani;

    language JAN exclude_dflt;
    sub han by han.jp;

    language ZHS exclude_dflt;
    sub han by han.zh;
} locl;
"""


def bar(pen, x0, x1, y0, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x0, y1))
    pen.lineTo((x1, y1))
    pen.lineTo((x1, y0))
    pen.closePath()


def main():
    order = [".notdef", "space"] + list(BARS)
    builder = FontBuilder(UPEM, isTTF=True)
    builder.setupGlyphOrder(order)
    # Only the default forms are in the cmap. The regional ones are reachable
    # through `locl` and nothing else, which is what makes the test honest:
    # if the feature does not run, no tag can produce them.
    builder.setupCharacterMap({0x20: "space", 0x76F4: "han", 0x4E00: "one"})

    glyphs = {".notdef": TTGlyphPen(None).glyph(), "space": TTGlyphPen(None).glyph()}
    metrics = {".notdef": (1000, 0), "space": (1000, 0)}
    for name, box in BARS.items():
        pen = TTGlyphPen(None)
        bar(pen, *box)
        glyphs[name] = pen.glyph()
        metrics[name] = (1000, 100)

    builder.setupGlyf(glyphs)
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=880, descent=-120)
    builder.setupNameTable({
        "familyName": "LoclRegional",
        "styleName": "Regular",
        "psName": "LoclRegional-Regular",
        "licenseDescription":
            "Authored for the OneText test suite. Same licence as OneText (MIT).",
    })
    builder.setupOS2(sTypoAscender=880, sTypoDescender=-120,
                     usWinAscent=880, usWinDescent=120)
    builder.setupPost()
    addOpenTypeFeaturesFromString(builder.font, FEATURES)
    builder.save("LoclRegional.ttf")
    print("wrote LoclRegional.ttf")


if __name__ == "__main__":
    main()
