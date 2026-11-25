#!/usr/bin/env python3
"""Builds ColrV1.ttf, the test face for the COLR version 1 paint graph.

Same reasoning as generate_color_test_font.py: Noto Color Emoji's COLRv1 build
would exercise the renderer, at five megabytes of someone else's font, and it
would exercise it with whatever paints an emoji happens to use. This authors a
face with one glyph per thing the renderer has to get right, each small enough
that a test can say what colour a particular point must be.

fontTools is the encoder, which is the point: the reader in ColrTable.cs was
written from the specification, and a table written by somebody else's
implementation of the same specification is what it should be tested on.

Shapes (plain outlines, used as clips):
  sq     100..900 x 0..800      left   100..400 x 0..800
  tri    (200,100) (800,100) (500,700)
  bar    700..900 x 300..500    back   100..700 x 0..600
  front  300..900 x 200..800

Colour glyphs, by character:
  a  solid red in sq                    b  solid blue at half alpha
  c  linear red->blue, left to right    d  the same line, given a p1 that only
                                           p2 turns back to horizontal
  e  radial, centre out                 f  sweep, 0..360 degrees
  g  PaintTranslate                     h  PaintRotateAroundCenter, 90 degrees
  i  PaintTransform (x halved)          j  PaintSkewAroundCenter
  k  PaintComposite src_in              l  PaintComposite multiply
  m  PaintColrLayers                    n  PaintColrGlyph of m, scaled by half
  o  solid in the text colour           p  gradient ending in the text colour
  q  a clip box narrower than the ink   r  repeat extend      s  reflect extend
  t  the variable formats               u  radial with two different circles
  v  version 1 and version 0 for one glyph (1 is green, 0 is red and blue)
  w  a PaintColrGlyph that names itself x  version 0 only     z  no colour
  U+E000 + n: PaintComposite with CompositeMode n, for every mode, over two
  gradient-filled rectangles that overlap in part.

The ones a font should not contain, or a renderer should not trust:
  U+E100  a solid whose palette index the palette does not have
  U+E101  a hard edge: nineteen stops, two of them at the same offset
  U+E102  six composites, each the backdrop of the one above

Requires fonttools.  Run from this directory:
    python3 generate_colrv1_test_font.py
"""

import copy

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib.tables.otTables import CompositeMode, PaintFormat as F

UPEM = 1000
NO_VARIATION = 0xFFFFFFFF

SHAPES = {
    "sq": [(100, 0), (900, 0), (900, 800), (100, 800)],
    "left": [(100, 0), (400, 0), (400, 800), (100, 800)],
    "tri": [(200, 100), (800, 100), (500, 700)],
    "bar": [(700, 300), (900, 300), (900, 500), (700, 500)],
    "back": [(100, 0), (700, 0), (700, 600), (100, 600)],
    "front": [(300, 200), (900, 200), (900, 800), (300, 800)],
}

LETTERS = "abcdefghijklmnopqrstuvwxz"
EXTRAS = {0xE100: "badindex", 0xE101: "edge", 0xE102: "deep"}
NO_SUCH_COLOUR = 200
NESTED_COMPOSITES = 6
MODES = list(CompositeMode)

RED, BLUE, GREEN, YELLOW, CYAN = 0, 1, 2, 3, 4
TEXT = 0xFFFF
PALETTE = [
    (1.0, 0.0, 0.0, 1.0),
    (0.0, 0.0, 1.0, 1.0),
    (0.0, 1.0, 0.0, 1.0),
    (1.0, 1.0, 0.0, 1.0),
    (0.0, 1.0, 1.0, 1.0),
]


def solid(index, alpha=1.0):
    return {"Format": F.PaintSolid, "PaletteIndex": index, "Alpha": alpha}


def glyph(name, paint):
    return {"Format": F.PaintGlyph, "Paint": paint, "Glyph": name}


def line(stops, extend="pad"):
    return {
        "Extend": extend,
        "ColorStop": [
            {"StopOffset": offset, "PaletteIndex": index, "Alpha": alpha}
            for offset, index, alpha in stops
        ],
    }


def linear(stops, p0, p1, p2, extend="pad"):
    return {
        "Format": F.PaintLinearGradient, "ColorLine": line(stops, extend),
        "x0": p0[0], "y0": p0[1], "x1": p1[0], "y1": p1[1], "x2": p2[0], "y2": p2[1],
    }


RED_TO_BLUE = [(0.0, RED, 1.0), (1.0, BLUE, 1.0)]


def paints():
    layers_m = (F.PaintColrLayers, [glyph("sq", solid(RED)), glyph("tri", solid(BLUE))])
    table = {
        "a": glyph("sq", solid(RED)),
        "b": glyph("sq", solid(BLUE, 0.5)),
        "c": glyph("sq", linear(RED_TO_BLUE, (100, 0), (900, 0), (100, 800))),
        "d": glyph("sq", linear(RED_TO_BLUE, (100, 0), (900, 800), (100, 800))),
        "e": glyph("sq", {
            "Format": F.PaintRadialGradient, "ColorLine": line(RED_TO_BLUE),
            "x0": 500, "y0": 400, "r0": 0, "x1": 500, "y1": 400, "r1": 400,
        }),
        "f": glyph("sq", {
            "Format": F.PaintSweepGradient, "ColorLine": line(RED_TO_BLUE),
            "centerX": 500, "centerY": 400, "startAngle": 0, "endAngle": 360,
        }),
        "g": {"Format": F.PaintTranslate, "Paint": glyph("left", solid(GREEN)), "dx": 400, "dy": 0},
        "h": {
            "Format": F.PaintRotateAroundCenter, "Paint": glyph("bar", solid(RED)),
            "angle": 90, "centerX": 500, "centerY": 400,
        },
        "i": {
            "Format": F.PaintTransform, "Paint": glyph("sq", solid(BLUE)),
            "Transform": (0.5, 0, 0, 1, 0, 0),
        },
        "j": {
            "Format": F.PaintSkewAroundCenter, "Paint": glyph("left", solid(RED)),
            "xSkewAngle": -20, "ySkewAngle": 0, "centerX": 100, "centerY": 0,
        },
        "k": {
            "Format": F.PaintComposite, "CompositeMode": "src_in",
            "SourcePaint": glyph("sq", solid(RED)), "BackdropPaint": glyph("tri", solid(BLUE)),
        },
        "l": {
            "Format": F.PaintComposite, "CompositeMode": "multiply",
            "SourcePaint": glyph("sq", solid(YELLOW)), "BackdropPaint": glyph("sq", solid(CYAN)),
        },
        "m": layers_m,
        "n": {
            "Format": F.PaintScaleUniformAroundCenter,
            "Paint": {"Format": F.PaintColrGlyph, "Glyph": "m"},
            "scale": 0.5, "centerX": 500, "centerY": 400,
        },
        "o": glyph("sq", solid(TEXT)),
        "p": glyph("sq", linear([(0.0, RED, 1.0), (1.0, TEXT, 1.0)], (100, 0), (900, 0), (100, 800))),
        "q": glyph("sq", solid(RED)),
        "r": glyph("sq", linear(RED_TO_BLUE, (100, 0), (300, 0), (100, 800), "repeat")),
        "s": glyph("sq", linear(RED_TO_BLUE, (100, 0), (300, 0), (100, 800), "reflect")),
        "t": (F.PaintColrLayers, [
            glyph("left", {"Format": F.PaintVarSolid, "PaletteIndex": GREEN, "Alpha": 1.0,
                           "VarIndexBase": NO_VARIATION}),
            {
                "Format": F.PaintVarTranslate, "dx": 400, "dy": 0, "VarIndexBase": NO_VARIATION,
                "Paint": glyph("left", {
                    "Format": F.PaintVarLinearGradient,
                    "ColorLine": {
                        "Extend": "pad",
                        "ColorStop": [
                            {"StopOffset": 0.0, "PaletteIndex": RED, "Alpha": 1.0,
                             "VarIndexBase": NO_VARIATION},
                            {"StopOffset": 1.0, "PaletteIndex": BLUE, "Alpha": 1.0,
                             "VarIndexBase": NO_VARIATION},
                        ],
                    },
                    "x0": 0, "y0": 0, "x1": 0, "y1": 800, "x2": 400, "y2": 0,
                    "VarIndexBase": NO_VARIATION,
                }),
            },
        ]),
        "u": glyph("sq", {
            "Format": F.PaintRadialGradient, "ColorLine": line(RED_TO_BLUE),
            "x0": 300, "y0": 400, "r0": 0, "x1": 500, "y1": 400, "r1": 400,
        }),
        "v": glyph("sq", solid(GREEN)),
        "w": {"Format": F.PaintColrGlyph, "Glyph": "w"},
        "x": [("sq", RED), ("tri", BLUE)],
    }

    backdrop = glyph("back", linear(
        [(0.0, BLUE, 0.8), (1.0, GREEN, 0.8)], (100, 0), (700, 0), (100, 600)))
    source = glyph("front", linear(
        [(0.0, RED, 1.0), (1.0, YELLOW, 0.6)], (0, 200), (0, 800), (600, 200)))
    for mode in MODES:
        table[f"mode{mode.value:02d}"] = {
            "Format": F.PaintComposite, "CompositeMode": mode,
            "SourcePaint": source, "BackdropPaint": backdrop,
        }

    table["badindex"] = glyph("sq", solid(NO_SUCH_COLOUR))

    # Red up to the middle, blue from it. The two stops at 0.5 are the edge,
    # and there are enough stops around them that a sort which does not keep
    # equal offsets in order has every chance to swap the pair.
    reds = [(i * 0.05, RED, 1.0) for i in range(9)]
    blues = [(0.6 + i * 0.05, BLUE, 1.0) for i in range(8)]
    table["edge"] = glyph("sq", linear(
        reds + [(0.5, RED, 1.0), (0.5, BLUE, 1.0)] + blues, (100, 0), (900, 0), (100, 800)))

    # Blue at the bottom, and a red square laid over it six times. Each level
    # holds two planes while the one below it is drawn.
    deep = glyph("sq", solid(BLUE))
    for _ in range(NESTED_COMPOSITES):
        deep = {
            "Format": F.PaintComposite, "CompositeMode": "src_over",
            "SourcePaint": glyph("sq", solid(RED)), "BackdropPaint": deep,
        }
    table["deep"] = deep
    return table


def main():
    mode_names = [f"mode{mode.value:02d}" for mode in MODES]
    order = [".notdef", "space"] + list(SHAPES) + list(LETTERS) + mode_names + list(EXTRAS.values())
    cmap = {0x20: "space"}
    cmap.update({ord(c): c for c in LETTERS})
    cmap.update({0xE000 + mode.value: f"mode{mode.value:02d}" for mode in MODES})
    cmap.update(EXTRAS)

    outlines = {}
    for name in order:
        pen = TTGlyphPen(None)
        # A colour glyph's own outline is what a reader with no colour support
        # draws. The square will do for all of them.
        points = SHAPES.get(name) or (SHAPES["sq"] if name not in (".notdef", "space") else None)
        if points:
            pen.moveTo(points[0])
            for point in points[1:]:
                pen.lineTo(point)
            pen.closePath()
        outlines[name] = pen.glyph()

    builder = FontBuilder(UPEM, isTTF=True)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(outlines)

    glyf = builder.font["glyf"]
    metrics = {}
    for name in order:
        g = glyf[name]
        g.recalcBounds(glyf)
        metrics[name] = (UPEM, g.xMin if g.numberOfContours else 0)
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({
        "familyName": "OneText ColrV1",
        "styleName": "Regular",
        "psName": "OneTextColrV1-Regular",
    })
    builder.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    builder.setupPost()

    builder.setupCOLR(paints(), clipBoxes={"q": (100, 0, 500, 800)})
    builder.setupCPAL([PALETTE])

    # v: the same glyph in both halves of the table. The builder keys a glyph
    # to one or the other, so x's version 0 record is copied and renamed.
    font = builder.font
    colr = font["COLR"].table
    records = colr.BaseGlyphRecordArray.BaseGlyphRecord
    twin = copy.copy(next(r for r in records if r.BaseGlyph == "x"))
    twin.BaseGlyph = "v"
    records.append(twin)
    records.sort(key=lambda r: font.getGlyphID(r.BaseGlyph))
    colr.BaseGlyphRecordCount = len(records)

    font.save("ColrV1.ttf")
    print("wrote ColrV1.ttf")


if __name__ == "__main__":
    main()
