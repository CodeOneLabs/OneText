# Third-party notices

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
