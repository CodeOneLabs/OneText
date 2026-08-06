#!/usr/bin/env python3
"""Builds ColorGlyphs.ttf, the test face for the colour-glyph paths.

Colour emoji is the differentiating feature of M8 (TextMesh Pro renders a ZWJ
sequence as separate glyphs and the standard workaround is a hand-maintained
sprite sheet), so the paths that decode colour glyphs need a test face. Noto
Color Emoji would do it, and the roadmap originally planned to vendor it, but
it is 10.7 MB of someone else's font to carry forever in order to exercise two
code paths. This authors a face containing exactly what those paths read, the
same way CffShapes.otf does for the CFF outline path: no third-party licence in
the test data, and a few kilobytes instead of ten megabytes.

What is in it:

  A  a COLRv0 glyph: two layers, a red square behind a blue triangle, drawn
     from a CPAL palette. Layer order and palette lookup are the two things
     that go wrong.
  B  a COLRv0 glyph whose second layer uses palette index 0xFFFF, the "use the
     text colour" sentinel: a real font's way of saying a layer should follow
     the label rather than the palette, and the case a naive palette lookup
     reads out of bounds on.
  C  a CBDT/CBLC glyph: a 32x32 PNG with a transparent border, so a decoder
     that ignores alpha is visible immediately.
  D  a plain monochrome outline glyph, as the control: the colour paths must
     leave it alone.

  Plus a GSUB ligature mapping A + ZWJ + B to a single glyph, so the
  "multi-codepoint sequences shape as one glyph" claim has something to shape.

Requires fonttools and Pillow.  Run from this directory:
    python3 generate_color_test_font.py
"""

import struct
from io import BytesIO

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import newTable
from fontTools.ttLib.tables.BitmapGlyphMetrics import SmallGlyphMetrics
from fontTools.ttLib.tables.C_B_D_T_ import cbdt_bitmap_format_17
from fontTools.ttLib.tables.E_B_L_C_ import (
    SbitLineMetrics, Strike, eblc_index_sub_table_1)
from PIL import Image

UPEM = 1000
PPEM = 32

# 直 (U+76F4) is here for the Han-unification test: one codepoint whose correct
# shape differs between Japanese and Chinese readers, so a test can check that
# the locale, not the fallback order, decides which font draws it.
GLYPHS = [".notdef", "A", "B", "C", "D", "AB", "space", "han"]
CMAP = {0x41: "A", 0x42: "B", 0x43: "C", 0x44: "D", 0x20: "space", 0x76F4: "han"}


def square(pen, x0, y0, x1, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x1, y0))
    pen.lineTo((x1, y1))
    pen.lineTo((x0, y1))
    pen.closePath()


def triangle(pen, x0, y0, x1, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x1, y0))
    pen.lineTo(((x0 + x1) / 2, y1))
    pen.closePath()


def build_outlines():
    """Every glyph needs a glyf entry; COLR layers reference these by name."""
    glyphs = {}

    pen = TTGlyphPen(None)
    square(pen, 100, 0, 900, 800)
    glyphs["A"] = pen.glyph()          # COLR layer 0 shape

    pen = TTGlyphPen(None)
    triangle(pen, 200, 100, 800, 700)
    glyphs["B"] = pen.glyph()          # COLR layer 1 shape

    pen = TTGlyphPen(None)
    glyphs["C"] = pen.glyph()          # CBDT glyph: no outline at all

    pen = TTGlyphPen(None)
    square(pen, 150, 0, 850, 700)
    glyphs["D"] = pen.glyph()          # the monochrome control

    pen = TTGlyphPen(None)
    square(pen, 50, 0, 950, 800)
    glyphs["AB"] = pen.glyph()         # the ligature's own shape

    pen = TTGlyphPen(None)
    square(pen, 100, 0, 900, 900)
    glyphs["han"] = pen.glyph()

    glyphs[".notdef"] = TTGlyphPen(None).glyph()
    glyphs["space"] = TTGlyphPen(None).glyph()
    return glyphs


def png_bytes():
    """A 32x32 PNG: opaque green centre, fully transparent border."""
    image = Image.new("RGBA", (PPEM, PPEM), (0, 0, 0, 0))
    for y in range(4, PPEM - 4):
        for x in range(4, PPEM - 4):
            image.putpixel((x, y), (0, 200, 60, 255))
    buffer = BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def add_cbdt(font, glyph_name):
    """A single-strike CBDT/CBLC pair holding one PNG glyph (format 17)."""
    glyph_id = font.getGlyphID(glyph_name)

    bitmap = cbdt_bitmap_format_17(b"", None)
    # Format 17 carries SmallGlyphMetrics, one direction only.
    bitmap.metrics = SmallGlyphMetrics()
    bitmap.metrics.height = PPEM
    bitmap.metrics.width = PPEM
    bitmap.metrics.BearingX = 0
    bitmap.metrics.BearingY = PPEM
    bitmap.metrics.Advance = PPEM
    bitmap.imageData = png_bytes()
    bitmap.name = glyph_name

    cbdt = newTable("CBDT")
    cbdt.version = 3.0
    cbdt.strikeData = [{glyph_name: bitmap}]
    font["CBDT"] = cbdt

    strike = Strike()
    strike.bitmapSizeTable.colorRef = 0
    strike.bitmapSizeTable.startGlyphIndex = glyph_id
    strike.bitmapSizeTable.endGlyphIndex = glyph_id
    strike.bitmapSizeTable.ppemX = PPEM
    strike.bitmapSizeTable.ppemY = PPEM
    strike.bitmapSizeTable.bitDepth = 32
    strike.bitmapSizeTable.flags = 1
    for direction in ("hori", "vert"):
        line = SbitLineMetrics()
        setattr(strike.bitmapSizeTable, direction, line)
        line.ascender = PPEM
        line.descender = 0
        line.widthMax = PPEM
        line.caretSlopeNumerator = 0
        line.caretSlopeDenominator = 1
        line.caretOffset = 0
        line.minOriginSB = 0
        line.minAdvanceSB = 0
        line.maxBeforeBL = 0
        line.minAfterBL = 0
        line.pad1 = 0
        line.pad2 = 0

    index = eblc_index_sub_table_1(b"", None)
    index.indexFormat = 1
    index.imageFormat = 17
    index.imageSize = len(bitmap.imageData)
    index.names = [glyph_name]
    index.firstGlyphIndex = glyph_id
    index.lastGlyphIndex = glyph_id
    strike.indexSubTables = [index]

    cblc = newTable("CBLC")
    cblc.version = 3.0
    cblc.strikes = [strike]
    font["CBLC"] = cblc


def main():
    builder = FontBuilder(UPEM, isTTF=True)
    builder.setupGlyphOrder(GLYPHS)
    builder.setupCharacterMap(CMAP)
    builder.setupGlyf(build_outlines())

    # Left side bearings from the real outlines, not zero: fontTools derives a
    # glyph's bounding box from lsb, and a wrong box means HarfBuzz reports
    # ink extents that are the right size in the wrong place, which is a tile
    # positioned wrongly, not an obviously broken font.
    glyf = builder.font["glyf"]
    advances = {}
    for name in GLYPHS:
        glyph = glyf[name]
        glyph.recalcBounds(glyf)
        advances[name] = (UPEM, glyph.xMin if glyph.numberOfContours else 0)
    builder.setupHorizontalMetrics(advances)
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({
        "familyName": "OneText Color Glyphs",
        "styleName": "Regular",
        "psName": "OneTextColorGlyphs-Regular",
    })
    builder.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    builder.setupPost()

    # COLRv0: A is two layers, B has a layer using the text colour.
    builder.setupCOLR({
        "A": [("A", 0), ("B", 1)],
        "B": [("A", 2), ("B", 0xFFFF)],
    })
    builder.setupCPAL([[
        (1.0, 0.0, 0.0, 1.0),   # 0: red
        (0.0, 0.0, 1.0, 1.0),   # 1: blue
        (1.0, 1.0, 0.0, 0.5),   # 2: half-transparent yellow
    ]])

    # A ZWJ B -> AB, so a multi-codepoint sequence has something to shape into
    # one glyph. Emoji sequences are exactly this mechanism.
    builder.setupGlyphOrder(GLYPHS)
    builder.addOpenTypeFeatures(
        "feature liga {"
        "  sub A B by AB;"
        "} liga;"
    )

    font = builder.font
    add_cbdt(font, "C")
    font.save("ColorGlyphs.ttf")
    print("wrote ColorGlyphs.ttf")


if __name__ == "__main__":
    main()
