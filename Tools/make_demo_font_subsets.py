#!/usr/bin/env python3
"""Cuts the two subset faces the demo's fallback chain ends with.

The demo needs ideographs and emoji — the shaping page argues in Japanese and
Chinese, the ruby and vertical pages are Japanese by construction, and the
emoji row is there because a ZWJ family is the case that proves segmentation.
The faces that carry them are sixteen and ten megabytes, which is more than
the rest of the demo put together and far more than a browser demo should ask
a stranger to download.

So they are subset. Not to a guessed charset: the set is read out of the demo's
own sources, so adding a specimen and re-running this script is the whole
procedure, and a specimen whose characters nobody cut cannot silently ship as
boxes. Everything the existing chain already covers is dropped from the cut,
which is why the result is seventeen ideographs rather than a CJK font.

Sources are the uncommitted coverage corpus (`Tools/fetch_coverage_fonts.py`
puts it in `Tests/CoverageFonts~/`), because that is where a full Noto already
lives and a second copy of a sixteen-megabyte OTF in the repository would
defeat the point.

    python3 Tools/make_demo_font_subsets.py

Layout features are kept whole (`--layout-features=*`): the vertical page runs
`vert`/`vrt2` over these glyphs and the emoji row needs the ZWJ ligatures, and
a subsetter's default feature set keeps neither.
"""

import re
import subprocess
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEMO = ROOT / "Samples~/Demo"
FONTS = DEMO / "Fonts"
CORPUS = ROOT / "Tests/CoverageFonts~"

# What the chain already carries. Anything these cover is not cut again.
COVERED = [
    "PretendardVariable.ttf.bytes",
    "NotoSansArabic-Regular.ttf.bytes",
    "NotoSansThai-Regular.ttf.bytes",
    "NotoSansDevanagari-Regular.ttf.bytes",
    "NotoSansHebrew-Regular.ttf.bytes",
]

CJK_SOURCE = CORPUS / "NotoSansCJKjp-Regular.otf"
EMOJI_SOURCE = CORPUS / "NotoColorEmoji.ttf"
CJK_OUT = FONTS / "NotoSansJP-DemoSubset.otf.bytes"
EMOJI_OUT = FONTS / "NotoColorEmoji-DemoSubset.ttf.bytes"

# Emoji are shaped, not just mapped: the family is four people and three ZWJs
# that a ligature turns into one glyph, and the astronaut carries a skin-tone
# modifier. Subsetting by codepoint alone keeps the parts and loses the join,
# so the sequences are passed whole and the subsetter's closure keeps the
# ligature that spans them.
EMOJI_SEQUENCES = [
    "\U0001F469‍\U0001F469‍\U0001F467\U0000200D\U0001F466",  # family
    "\U0001F468\U0001F3FD‍\U0001F680",                            # astronaut
    "\U0001F1F0\U0001F1F7",                                            # flag
]


def demo_characters():
    """Every non-ASCII character the demo's own C# sources contain."""
    chars = set()
    for path in sorted(DEMO.rglob("*.cs")):
        for ch in path.read_text(encoding="utf-8"):
            if ord(ch) > 0x7F:
                chars.add(ch)
    return chars


def covered_codepoints():
    from fontTools.ttLib import TTFont

    out = set()
    for name in COVERED:
        font = TTFont(FONTS / name, lazy=True, fontNumber=0)
        out.update(font.getBestCmap().keys())
        font.close()
    return out


def cut(source, text, output):
    if not source.exists():
        sys.exit(f"{source} is missing. Run Tools/fetch_coverage_fonts.py first.")
    subprocess.run(
        [
            sys.executable, "-m", "fontTools.subset", str(source),
            "--text=" + text,
            "--layout-features=*",
            "--name-IDs=*",
            "--output-file=" + str(output),
        ],
        check=True,
    )
    print(f"{output.name}  {output.stat().st_size / 1024:.0f} KB  ({len(set(text))} characters)")


def main():
    used = demo_characters()
    covered = covered_codepoints()
    missing = sorted(c for c in used if ord(c) not in covered)

    ideographs = [c for c in missing if unicodedata.category(c) == "Lo" and ord(c) >= 0x2E80]
    emoji = [c for c in missing if ord(c) >= 0x1F000]
    other = [c for c in missing if c not in ideographs and c not in emoji]

    if other:
        # Not fatal, and not silent either: a symbol nobody has a face for is
        # exactly the box the demo is not allowed to draw, and the fix is
        # usually to pick a character Pretendard already has.
        print("uncovered and not cut here:",
              " ".join(f"U+{ord(c):04X} {c}" for c in other))

    if not ideographs:
        sys.exit("no uncovered ideographs found — nothing to cut.")

    cut(CJK_SOURCE, "".join(ideographs), CJK_OUT)
    cut(EMOJI_SOURCE, "".join(EMOJI_SEQUENCES), EMOJI_OUT)


if __name__ == "__main__":
    main()
