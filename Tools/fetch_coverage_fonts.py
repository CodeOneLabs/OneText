#!/usr/bin/env python3
"""Fetches the fonts the every-codepoint coverage test needs.

The test asks a question no unit test can: does every assigned codepoint in
Unicode survive the pipeline — shaped, rasterized, placed in the atlas — without
throwing, hanging, or coming out as tofu where the font had a glyph. Answering it
needs fonts covering all of Unicode, which is about 200 MB. That does not belong
in a repository somebody clones to fix a typo, so it lives here as a fetch
instead, and the test skips when the fonts are absent.

Output goes to Tests/CoverageFonts~/. The trailing tilde is what keeps Unity from
importing 200 MB of fonts as assets on every domain reload.

Everything fetched is SIL Open Font License. Re-running skips what is already
present, so an interrupted fetch resumes.

    python3 Tools/fetch_coverage_fonts.py [--list] [--skip-cjk]
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

REPO_API = "https://api.github.com/repos/notofonts/notofonts.github.io/contents/fonts"
RAW = "https://raw.githubusercontent.com/notofonts/notofonts.github.io/main/fonts"

# Noto's CJK and emoji fonts are not in the per-script repository above: they are
# too big for it and are released separately. CJK matters more than all the rest
# combined — about 120,000 of Unicode's ~155,000 assigned codepoints are unified
# Han — so a run without it is not a coverage run at all.
EXTRAS = [
    ("NotoSansCJKjp-Regular.otf",
     "https://github.com/notofonts/noto-cjk/raw/main/Sans/OTF/Japanese/NotoSansCJKjp-Regular.otf"),
    ("NotoSansCJKsc-Regular.otf",
     "https://github.com/notofonts/noto-cjk/raw/main/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf"),
    ("NotoSansCJKtc-Regular.otf",
     "https://github.com/notofonts/noto-cjk/raw/main/Sans/OTF/TraditionalChinese/NotoSansCJKtc-Regular.otf"),
    ("NotoSansCJKkr-Regular.otf",
     "https://github.com/notofonts/noto-cjk/raw/main/Sans/OTF/Korean/NotoSansCJKkr-Regular.otf"),
    ("NotoColorEmoji.ttf",
     "https://github.com/googlefonts/noto-emoji/raw/main/fonts/NotoColorEmoji.ttf"),
]

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), "Tests", "CoverageFonts~")


def get(url, binary=True):
    request = urllib.request.Request(url, headers={"User-Agent": "onetext-fetch"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read() if binary else response.read().decode("utf-8")


def script_dirs():
    listing = json.loads(get(REPO_API, binary=False))
    return sorted(entry["name"] for entry in listing if entry["type"] == "dir")


def fetch(name, url, out_dir):
    path = os.path.join(out_dir, name)
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return os.path.getsize(path), True
    try:
        data = get(url)
    except (urllib.error.HTTPError, urllib.error.URLError):
        return 0, False
    with open(path, "wb") as handle:
        handle.write(data)
    return len(data), False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--list", action="store_true",
                        help="print the script families and exit, fetching nothing")
    parser.add_argument("--skip-cjk", action="store_true",
                        help="skip the CJK and emoji fonts (about 130 MB of the total)")
    args = parser.parse_args()

    families = script_dirs()
    if args.list:
        print("\n".join(families))
        print(f"\n{len(families)} families", file=sys.stderr)
        return 0

    os.makedirs(OUT, exist_ok=True)
    total = fetched = cached = missing = 0

    for i, family in enumerate(families, 1):
        # One weight is enough: coverage is a property of the cmap, and a second
        # weight would double the download to test the same codepoints again.
        name = f"{family}-Regular.ttf"
        url = f"{RAW}/{family}/hinted/ttf/{name}"
        size, was_cached = fetch(name, url, OUT)
        if size:
            total += size
            cached += was_cached
            fetched += not was_cached
        else:
            # Several families ship only as variable or unhinted; they are
            # reported rather than retried, because a family absent from the
            # fetch is a hole in coverage the test must not silently inherit.
            missing += 1
            print(f"  no hinted Regular: {family}", file=sys.stderr)
        if i % 25 == 0:
            print(f"  {i}/{len(families)} families, {total / 1e6:.0f} MB", file=sys.stderr)

    if not args.skip_cjk:
        for name, url in EXTRAS:
            size, was_cached = fetch(name, url, OUT)
            if size:
                total += size
                cached += was_cached
                fetched += not was_cached
            else:
                missing += 1
                print(f"  FAILED: {name}", file=sys.stderr)

    print(f"\n{fetched} fetched, {cached} already present, {missing} unavailable")
    print(f"{total / 1e6:.0f} MB in {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
