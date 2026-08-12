# Third-party notices

## Pretendard

Typeface by Kil Hyung-jin, licensed under the SIL Open Font License 1.1 with
Reserved Font Name "Pretendard". https://github.com/orioncactus/pretendard

`Samples~/Demo/Fonts/PretendardVariable.ttf.bytes` is Pretendard Variable
1.3.9, unmodified except for the `.bytes` suffix Unity needs to import a file
as a `TextAsset`. It ships with the demo sample so that the sample draws
something the first time somebody opens it; nothing in `Runtime/` or `Editor/`
references it, and a project that imports the sample can delete it and assign
its own face. The licence travels beside it as
`Samples~/Demo/Fonts/Pretendard-OFL.txt`, which the OFL requires and which must
not be separated from the font.

The reserved name means a *modified* copy may not be called Pretendard. This
copy is not modified, so it keeps the name.

## Noto Sans (Arabic, Thai, Devanagari, Hebrew)

Typefaces by the Noto Project Authors, licensed under the SIL Open Font
License 1.1. https://github.com/notofonts

Four faces ship in `Samples~/Demo/Fonts/` as the demo's fallback chain, so the
page that argues about shaping has Arabic, Thai and Devanagari to argue with
rather than boxes. Unmodified except for the `.bytes` suffix. Their licence is
`Samples~/Demo/Fonts/Noto-OFL.txt` and must not be separated from them.

The same faces are also used, uncommitted, as test fixtures; see
`Tests/Fonts~/OFL.txt`.

## Noto Sans CJK JP and Noto Color Emoji (subsets)

`Samples~/Demo/Fonts/NotoSansJP-DemoSubset.otf.bytes` and
`NotoColorEmoji-DemoSubset.ttf.bytes` are **modified** copies: subsets cut to
the seventeen ideographs and six emoji the demo's own specimen strings use, at
twelve and eighty kilobytes rather than sixteen and ten megabytes. Nothing else
is changed — outlines, bitmaps, metrics and layout features are the originals'.
`Tools/make_demo_font_subsets.py` reproduces both from the full faces.

Noto Sans CJK JP is © 2014-2021 Adobe; Noto Color Emoji is © 2022 Google Inc.
Both are under the SIL Open Font License 1.1, neither declares a Reserved Font
Name, and the OFL therefore permits a modified copy to keep the family name —
which these do, so that what they were cut from stays legible. Their licence is
the same `Samples~/Demo/Fonts/Noto-OFL.txt` and must not be separated from them.

## HarfBuzz

OpenType text shaping engine, including `harfbuzz-subset`. Licensed under the
"Old MIT" license. https://github.com/harfbuzz/harfbuzz

The binaries in `Runtime/Plugins/` are HarfBuzz 14.2.1 as built and packaged by
the `HarfBuzzSharp.NativeAssets.*` NuGet packages (14.2.1.1). Two notices are
vendored at `Runtime/Plugins/`, and both cover every platform's binary; they
come from one build tree:

- `HarfBuzz-COPYING.txt`, HarfBuzz's own "Old MIT" licence and copyright
  holders. The binaries are HarfBuzz, so this is the notice that has to travel
  with them.
- `HarfBuzzSharp-LICENSE.txt`, the MIT licence covering Microsoft's packaging.

`Docs/NATIVES.md` records the exact package and path each file came from, what
was verified before committing it, and the three modifications made on the way
in for iOS: thinned to arm64, its `Info.plist` corrected from the simulator one
Microsoft ships on the device framework, and device plus simulator repacked
into a single `.xcframework`.

## FreeType

Font parsing and outline extraction. Used under the FreeType License (FTL),
which requires this attribution:

> Portions of this software are copyright © The FreeType Project
> (www.freetype.org). All rights reserved.

https://freetype.org

## Unicode Character Database

Break property tables in `Runtime/Core/Unicode/*.g.cs` are generated from the
UCD (version 17.0.0) by the scripts in `Tools/`, and the conformance test files
in `Tests/UnicodeData~/` are shipped verbatim. Both are © Unicode, Inc., under
the Unicode License v3. https://www.unicode.org/license.txt

## Noto fonts (test data)

`Tests/Fonts~/` contains Noto Sans, Noto Sans Arabic and the Noto Sans variable
font, used only by the test suite. They are licensed under the SIL Open Font
License 1.1; the license text is in `Tests/Fonts~/OFL.txt`.
https://github.com/notofonts
