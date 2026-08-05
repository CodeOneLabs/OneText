#!/usr/bin/env python3
"""Builds CffShapes.otf, the test face for the PostScript/CFF outline path.

Both other test fonts are TrueType, so hb-draw's cubic callback — and every
line of flattening that depends on it — was never rendered by a test. Rather
than vendor someone else's .otf and inherit its licence and provenance, this
authors a small face containing exactly the shapes that go wrong:

  O  an outer ring and an inner ring wound the other way (a counter: the
     middle must come out *outside* the glyph, not inside)
  Q  two overlapping rectangles in the same contour direction (a union: the
     shared middle must stay inside, and the buried edges must not carve it)
  S  a long shallow cubic S-curve (flattening error is largest here)
  I  a plain rectangle, straight lines only, as the control

Requires fonttools. Run from this directory:  python3 generate_cff_test_font.py
"""

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen

UPEM = 1000


def ring(pen, cx, cy, r, clockwise):
    """A circle out of four cubics, wound in the requested direction."""
    k = r * 0.5523  # circle-to-cubic constant
    if clockwise:
        pen.moveTo((cx, cy + r))
        pen.curveTo((cx + k, cy + r), (cx + r, cy + k), (cx + r, cy))
        pen.curveTo((cx + r, cy - k), (cx + k, cy - r), (cx, cy - r))
        pen.curveTo((cx - k, cy - r), (cx - r, cy - k), (cx - r, cy))
        pen.curveTo((cx - r, cy + k), (cx - k, cy + r), (cx, cy + r))
    else:
        pen.moveTo((cx, cy + r))
        pen.curveTo((cx - k, cy + r), (cx - r, cy + k), (cx - r, cy))
        pen.curveTo((cx - r, cy - k), (cx - k, cy - r), (cx, cy - r))
        pen.curveTo((cx + k, cy - r), (cx + r, cy - k), (cx + r, cy))
        pen.curveTo((cx + r, cy + k), (cx + k, cy + r), (cx, cy + r))
    pen.closePath()


def box(pen, x0, y0, x1, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x0, y1))
    pen.lineTo((x1, y1))
    pen.lineTo((x1, y0))
    pen.closePath()


def draw_counter(pen):
    ring(pen, 350, 350, 320, clockwise=True)
    ring(pen, 350, 350, 170, clockwise=False)


def draw_overlap(pen):
    box(pen, 40, 200, 460, 400)
    box(pen, 200, 40, 400, 560)


def draw_s_curve(pen):
    # A shallow S: the flattest cubics are where a fixed subdivision wastes
    # the most segments and an adaptive one risks too few.
    pen.moveTo((40, 80))
    pen.curveTo((40, 420), (460, 180), (460, 520))
    pen.curveTo((460, 560), (300, 600), (40, 600))
    pen.lineTo((40, 540))
    pen.curveTo((280, 540), (400, 540), (400, 520))
    pen.curveTo((400, 240), (100, 460), (100, 80))
    pen.closePath()


def draw_bar(pen):
    box(pen, 150, 0, 350, 700)


GLYPHS = {
    ".notdef": (None, 500),
    "space": (None, 500),
    "O": (draw_counter, 700),
    "Q": (draw_overlap, 500),
    "S": (draw_s_curve, 500),
    "I": (draw_bar, 500),
}


def main():
    order = list(GLYPHS)
    builder = FontBuilder(UPEM, isTTF=False)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap({ord(name): name for name in "OQSI"} | {0x20: "space"})

    charstrings = {}
    metrics = {}
    for name, (draw, advance) in GLYPHS.items():
        pen = T2CharStringPen(advance, None)
        if draw is not None:
            draw(pen)
        charstrings[name] = pen.getCharString()
        metrics[name] = (advance, 0)

    builder.setupCFF("CffShapes", {"FullName": "CffShapes"}, charstrings, {})
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({
        "familyName": "CffShapes",
        "styleName": "Regular",
        "psName": "CffShapes-Regular",
        "licenseDescription":
            "Authored for the OneText test suite. Same licence as OneText (MIT).",
    })
    builder.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    builder.setupPost()
    builder.save("CffShapes.otf")
    print("wrote CffShapes.otf")


if __name__ == "__main__":
    main()
