#!/usr/bin/env python3
"""Draws the icons Unity's Project window gives OneText's asset types.

A ScriptableObject with no icon gets the default script sheet, which is the
same picture for a font, a charset and a word list, so a project folder with
six of them reads as six of nothing. These are drawn instead, in the palette
the package's own site uses: green for the things text is made of, amber for
the data it is measured against, violet for the things that decorate it.

Everything is a signed distance field, evaluated per pixel and clamped to a one
pixel edge, so there is no supersampling and no dependency: the standard
library writes the PNG. Shapes are deliberately fat and few, because the size
that matters is 16 pixels in a Project window, not the 64 they are stored at.

Run:  python3 Tools/gen_asset_icons.py
Out:  Editor/Icons/*.png  (checked in; a contributor never needs to run this)
"""

import math
import os
import struct
import zlib

SIZE = 64

GREEN = (126, 231, 180)
AMBER = (255, 204, 102)
VIOLET = (179, 157, 255)

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "Editor", "Icons")


# --------------------------------------------------------------- distances

def sd_circle(x, y, cx, cy, r):
    return math.hypot(x - cx, y - cy) - r


def sd_round_rect(x, y, cx, cy, hw, hh, r):
    dx = abs(x - cx) - (hw - r)
    dy = abs(y - cy) - (hh - r)
    return (math.hypot(max(dx, 0.0), max(dy, 0.0)) + min(max(dx, dy), 0.0) - r)


def sd_segment(x, y, ax, ay, bx, by, r):
    """A capsule: the line from a to b, thickened by r."""
    pax, pay = x - ax, y - ay
    bax, bay = bx - ax, by - ay
    denominator = bax * bax + bay * bay
    h = 0.0 if denominator == 0 else max(0.0, min(1.0, (pax * bax + pay * bay) / denominator))
    return math.hypot(pax - bax * h, pay - bay * h) - r


def outline(distance, half_width):
    """Turns a filled shape into a stroked one."""
    return abs(distance) - half_width


# ------------------------------------------------------------- compositing

class Canvas:
    def __init__(self, size):
        self.size = size
        self.pixels = [[0.0, 0.0, 0.0, 0.0] for _ in range(size * size)]

    def draw(self, shape, colour, alpha=1.0):
        """Paints one distance function over what is already there."""
        red, green, blue = (c / 255.0 for c in colour)
        for py in range(self.size):
            y = py + 0.5
            row = py * self.size
            for px in range(self.size):
                coverage = 0.5 - shape(px + 0.5, y)
                if coverage <= 0.0:
                    continue
                coverage = min(1.0, coverage) * alpha
                pixel = self.pixels[row + px]
                inverse = 1.0 - coverage
                pixel[0] = red * coverage + pixel[0] * inverse
                pixel[1] = green * coverage + pixel[1] * inverse
                pixel[2] = blue * coverage + pixel[2] * inverse
                pixel[3] = coverage + pixel[3] * inverse

    def to_bytes(self):
        out = bytearray()
        for pixel in self.pixels:
            # Straight alpha, unpremultiplied: Unity's importer expects it.
            a = pixel[3]
            if a <= 0.0:
                out += b"\x00\x00\x00\x00"
                continue
            out += bytes(int(round(max(0.0, min(1.0, channel / a)) * 255))
                         for channel in pixel[:3])
            out += bytes([int(round(a * 255))])
        return bytes(out)


def write_png(path, canvas):
    size = canvas.size
    data = canvas.to_bytes()
    raw = b"".join(b"\x00" + data[y * size * 4:(y + 1) * size * 4] for y in range(size))

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload +
                struct.pack(">I", zlib.crc32(tag + payload) & 0xffffffff))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) +
           chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    with open(path, "wb") as handle:
        handle.write(png)
    print(f"  {os.path.basename(path)}  {len(png):,} bytes")


# ------------------------------------------------------------------ icons

def font_asset(canvas):
    """An A, and the line it sits on."""
    stroke = 4.0
    apex = (32.0, 12.0)
    left = (14.0, 46.0)
    right = (50.0, 46.0)
    canvas.draw(lambda x, y: sd_segment(x, y, *apex, *left, stroke), GREEN)
    canvas.draw(lambda x, y: sd_segment(x, y, *apex, *right, stroke), GREEN)
    canvas.draw(lambda x, y: sd_segment(x, y, 21.0, 35.0, 43.0, 35.0, 3.2), GREEN)
    canvas.draw(lambda x, y: sd_round_rect(x, y, 32.0, 55.0, 22.0, 1.6, 1.6), GREEN, 0.42)


def text_style(canvas):
    """Two cards, one behind the other: a look, worn by more than one thing."""
    canvas.draw(lambda x, y: outline(
        sd_round_rect(x, y, 26.0, 24.0, 16.0, 13.0, 4.0), 1.8), VIOLET, 0.55)
    canvas.draw(lambda x, y: sd_round_rect(x, y, 38.0, 38.0, 16.0, 13.0, 4.0), VIOLET)
    canvas.draw(lambda x, y: sd_round_rect(x, y, 33.0, 34.0, 8.0, 1.6, 1.6),
                (10, 14, 18))
    canvas.draw(lambda x, y: sd_round_rect(x, y, 36.0, 42.0, 11.0, 1.6, 1.6),
                (10, 14, 18))


def charset(canvas):
    """A grid, part of it filled: some characters chosen out of all of them."""
    filled = {(0, 0), (1, 0), (0, 1), (2, 1), (1, 2), (2, 2)}
    for column in range(3):
        for row in range(3):
            cx = 16.0 + column * 16.0
            cy = 16.0 + row * 16.0
            if (column, row) in filled:
                canvas.draw(lambda x, y, cx=cx, cy=cy:
                            sd_round_rect(x, y, cx, cy, 6.0, 6.0, 2.0), GREEN)
            else:
                canvas.draw(lambda x, y, cx=cx, cy=cy: outline(
                    sd_round_rect(x, y, cx, cy, 6.0, 6.0, 2.0), 1.4), GREEN, 0.42)


def dictionary(canvas):
    """Three lines of text, broken into words: what a word list is for."""
    rows = [
        [(10.0, 26.0), (30.0, 46.0), (50.0, 56.0)],
        [(10.0, 22.0), (26.0, 54.0)],
        [(10.0, 34.0), (38.0, 54.0)],
    ]
    for index, row in enumerate(rows):
        y = 18.0 + index * 14.0
        for start, end in row:
            canvas.draw(lambda x, py, s=start, e=end, cy=y:
                        sd_round_rect(x, py, (s + e) / 2.0, cy,
                                      (e - s) / 2.0, 3.6, 3.0), AMBER)


def settings(canvas):
    """Three knobs, set differently: the project's defaults."""
    for index, position in enumerate((44.0, 24.0, 36.0)):
        y = 18.0 + index * 14.0
        canvas.draw(lambda x, py, cy=y:
                    sd_round_rect(x, py, 32.0, cy, 22.0, 1.8, 1.8), GREEN, 0.45)
        canvas.draw(lambda x, py, cx=position, cy=y:
                    sd_circle(x, py, cx, cy, 5.2), GREEN)


def sprite_sheet(canvas):
    """A picture in a frame: the emoji and the icons a label draws inline."""
    canvas.draw(lambda x, y: outline(
        sd_round_rect(x, y, 32.0, 32.0, 23.0, 19.0, 5.0), 2.0), VIOLET)
    canvas.draw(lambda x, y: sd_circle(x, y, 22.0, 24.0, 4.2), VIOLET)
    canvas.draw(lambda x, y: sd_segment(x, y, 22.0, 45.0, 34.0, 30.0, 2.6), VIOLET)
    canvas.draw(lambda x, y: sd_segment(x, y, 34.0, 30.0, 46.0, 45.0, 2.6), VIOLET)
    canvas.draw(lambda x, y: sd_round_rect(x, y, 34.0, 46.0, 15.0, 2.6, 2.0), VIOLET)


ICONS = {
    "OneFontAsset": font_asset,
    "OneTextStyle": text_style,
    "OneTextCharset": charset,
    "OneTextDictionary": dictionary,
    "OneTextSettings": settings,
    "OneTextSpriteSheet": sprite_sheet,
}


def main():
    os.makedirs(OUT, exist_ok=True)
    print(f"Drawing {len(ICONS)} icons into {OUT}")
    for name, draw in ICONS.items():
        canvas = Canvas(SIZE)
        draw(canvas)
        write_png(os.path.join(OUT, f"{name}.png"), canvas)


if __name__ == "__main__":
    main()
