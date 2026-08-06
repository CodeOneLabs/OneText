# Changelog

## [Unreleased]

### Added

- **`<u>`, `<s>` and `<mark>` now draw something.** All three parsed, set
  their flag on the style, and were then read by nothing: the parser tests
  went green while a reader saw no line and no highlight. They are geometry
  now — a bar under the run for `<u>`, one through it for `<s>`, and the line
  box filled behind it for `<mark=#rrggbbaa>`.

  A bar is a tile in the same mesh as the letters, not a second graphic: it
  takes the fourth value of the atlas discriminator the colour and precise
  atlases already ride in (`3` means "no atlas, return the vertex colour"), so
  an underlined word still batches with the sentence around it and costs no
  extra vertex data. It is cut one glyph at a time rather than drawn as one
  rectangle over the run, so the typewriter reveals it with the text and a
  per-character effect moves it with the letter it belongs to. Thickness and
  offset come from the face's own `post` and `OS/2` metrics through HarfBuzz,
  with HarfBuzz's fallback for a face that omits them.

  Down a column the face's numbers do not apply — they are measured from a
  horizontal baseline and an upright glyph has none — so the bars are written
  against the em box instead: the wash is the column, one em across and the
  same em for every run in it; the line runs down beside it; the strikethrough
  runs down the middle. A rotated run keeps the face's numbers, because its
  frame turned and its baseline turned with it, and the two land close enough
  together that a column of kana with Latin in it wears one unbroken line.

- **No tofu, where the machine can help it.** A character that no font in the
  label's chain and no font in the project's chain covers is now drawn from a
  font the operating system has, instead of as a box. It is on by default
  (`System font fallback` in Project Settings > OneText) because a box is the
  worst thing a reader can be handed.

  The tier is last in the literal sense: nothing happens until every font the
  project ships has said no. When one finally does, the first miss lists the
  platform's font directories (`/System/Library/Fonts` and its Supplemental
  folder on macOS, `C:\Windows\Fonts` and the per-user folder on Windows,
  `/system/fonts` on Android, the fontconfig folders on Linux, the system
  folders on iOS) and reads the `cmap` table out of the candidates (a few
  kilobytes seeked out of each file, not a font parse), so the face that wins
  is the only one HarfBuzz is ever handed. A short preference list per script
  runs before the brute-force sweep, which is why 한 comes out of Apple SD
  Gothic Neo rather than out of whichever file happens to sort first. Every
  answer is remembered for the process, including "nothing on this machine has
  it", so the second occurrence of a character costs a dictionary lookup and
  allocates nothing. The face then joins the run as an ordinary fallback (the
  itemizer already splits runs per font), so shaping, vertical writing, the
  precise atlas and the decorations needed no changes at all.

  And Doctor still reports every character that needed one, as a
  `system-fallback` **warning** naming the face that caught it. Both halves of
  that are the point: the build renders, and it renders because of a font on
  the machine that built it, which a player's device may have in a different
  version or not at all. Warnings do not fail a merge, so this does not break
  CI; a character nothing on the machine has is still the `tofu` error it
  always was, and so is every missing character when the option is off.

  Two limits, both recorded in `Docs/NATIVES.md`. Web has no font directory to
  walk, so the tier finds nothing there and the box stays. And Apple's colour
  emoji are sbix payloads, which the colour path deliberately does not read;
  an emoji resolved from a macOS or iOS system font draws as an outline, so
  emoji still want a bundled colour font.

- **The Web native.** HarfBuzz 14.2.1 compiled to wasm with Emscripten
  3.1.38 (the tag Unity 6 bundles, because LLVM promises no object
  compatibility across versions) ships as
  `Runtime/Plugins/WebGL/libHarfBuzzSharp.a`, rebuilt reproducibly by
  `Tools/build_webgl_natives.sh`. Unity statically links its own HarfBuzz
  8.0.1 into every Web player, so every identifier in ours is compiled
  behind an `onetext_` prefix and the C# switches `EntryPoint` on Web only;
  a built player answers `hb_version_string` with 14.2.1 on both WebGL2 and
  WebGPU, shapes Arabic, Devanagari, kerned Latin and emoji ZWJ sequences
  identically to the desktop editor, and runs the subsetter, which Unity's
  bundled copy does not contain. A standalone browser harness with a WebGL2
  and a WebGPU renderer lives in `Web~/`. The toolchain match, the
  symbol-collision guard and the harness results are documented in
  `Docs/NATIVES.md`.

- **Vertical writing (縦書き).** `Writing mode` on the label (or
  `WritingMode` in code) sets text down columns that progress right to left.
  Horizontal is the default and is unchanged.

  Which characters stand upright and which turn is UAX #50's
  Vertical_Orientation property, generated from the UCD by
  `Tools/gen_vertical_tables.py` the way the line-break and bidi tables are:
  Han, kana, Hangul, full-width forms and emoji stand up; Latin, Cyrillic,
  Greek and the rest turn ninety degrees clockwise. The property's two
  "transformed typographically" classes are resolved the way the property
  asks: 。、！ and the small kana stand up and take the font's vertical form,
  and 「」ー are asked of the font first and rotated only if it has nothing to
  offer.

  An upright run is shaped top-to-bottom, which is the whole of the shaping
  side: HarfBuzz applies the font's `vert` feature (so the vertical forms of
  the brackets, the small kana and the punctuation come from the face rather
  than from a transform) and answers with `vmtx`/`VORG` metrics, so the
  advances and the centring on the column are the font's own. A rotated run is
  shaped horizontally, with its kerning and its ligatures intact, and turned
  only when it is drawn.

  A column is a line in a frame turned ninety degrees, and everything that
  measures one follows without a second implementation: the UAX #14 pipeline
  and its kinsoku tailoring wrap a column at the box's height, punctuation
  compression and the line-edge rules apply at column heads and feet,
  alignment turns with the text (the horizontal alignment places text along
  its column, the vertical one places the stack of columns across the box),
  and hit-testing, carets and selection rectangles turn with it too. Ruby sits
  to the right of its column at the same half size, by the same arithmetic
  JLREQ's horizontal rule uses: the annotation goes on the far side of the
  base's own ascent, which is above it across the page and beside it down a
  column. Reveal, effects, decorations and links count exactly what they
  counted before, because a column changes where a character is drawn and not
  what a reader reads.

  Not implemented, and deliberately: tate-chū-yoko (縦中横), bidi inside a
  column, a vertical caret or input field (rendering is read-only; the field
  stays horizontal), and `vrt2` beyond what `vert` gives.

- **Ruby (furigana) as a layout-engine feature.**
  `<ruby=ふりがな>漢字</ruby>` annotates a range of text with a second string
  (shaped text in any script the engine shapes, not a kana decoration), laid
  out by the rules in the W3C note *Rules for Simple Placement of Japanese
  Ruby*.

  The annotation is shaped at half the size of the text it annotates
  (`Ruby size` on the label, or `RubyScale` in code), centred over the base's
  advance with the slack distributed the spec's way (double gaps between the
  ruby characters, half at each end, capped at half a base character), and a
  Latin or Cyrillic reading centred rather than letter-spaced, because the
  distribution rule is written about ruby set on the em grid. A reading too
  wide for its base hangs over a neighbour's blank where JLREQ allows it
  (closing marks, full stops, commas, the ideographic space and middle dots,
  each on the side that faces the base, and only over what punctuation
  compression left), and pads the base with whatever no neighbour would give.

  The line grows to hold it (a ruby line is taller by exactly the annotation,
  above the baseline, so nothing reaches into the line above), and a base and
  its reading are one unbreakable group in the UAX #14 pipeline, tailored
  through the opportunity table like kinsoku and `<nobr>`.

  The annotation is not in the text and takes no indices: reveal steps, caret
  positions, effect spans and link ranges still count only what a reader reads.
  Ruby glyphs carry the clusters of the base characters they sit over, spread
  across the base, so a typewriter reveals ふり with 漢 and がな with 字, a
  per-cluster effect moves a reading with the character under it, and a
  decorated or coloured span decorates and colours its reading, with no
  special case anywhere in the mesh builder. Ruby markup inside an input field
  is not supported: the field turns rich text off, so the tag shows as literal
  text.

- **MSDF rendering, as the per-label `precise` option.** A multi-channel
  distance field keeps corners sharp where a single channel stores a cone the
  sampler rounds off. Worth it for display text, logotypes and anything large
  enough to show the difference. Off by default and off for every existing
  label: the ordinary single-channel SDF is still what body text renders
  through, and a project that never turns `precise` on never allocates the
  second atlas.

  Precise tiles live in their own RGBA `Texture2DArray` (four bytes a texel
  instead of one, same size and layer count as the ordinary atlas, same LRU
  eviction, compaction and partial uploads), and are cached apart from the
  single-channel tiles of the same glyph. Rendering still goes through one
  material and one draw call: which of the three atlases a tile came from rides
  in the vertex channel that already told the colour atlas from the SDF one, so
  a precise heading batches with the paragraph under it, and decorations
  (`<outline> <shadow> <glow>`) and colour emoji work in both modes.

  The tooling knows about the second atlas: the Hub's Atlas tab shows it its
  own occupancy pie, eviction history and demand-based budget advice, the
  on-device diagnostics overlay reports its memory alongside the first one's,
  and the settings page counts its four-bytes-a-texel cost next to the budget
  it shares. All of it only when the atlas exists; none of these readouts
  create it.

### Changed

- **The floor is now Unity 2022.3.** The Hub rebuild draws its charts with
  UI Toolkit's `painter2D`, which 2021.3 has never heard of, and the first
  CI run against a 2021.3 editor said so in three compile errors. Guarding
  an editor window's every stroke behind version defines would buy 2021.3
  users a worse Hub and this package a permanent tax, so the minimum moves
  to the LTS that can draw it. The runtime itself asked for nothing newer;
  this is the price of the front door, paid once.

- **The Hub is a different window.** Same tools, rebuilt on UI Toolkit (UXML
  and USS assets under `Editor/Hub/UI/`, one controller per section) and
  skinned to the project's own site rather than to the editor: near-black
  ground, green hairlines, cards, a left sidebar instead of a row of toolbar
  tabs. That is a decision about who the window is for. The old one assumed
  you already knew which tab answered your question; this one opens on an
  **Overview** that says what the project has, ticks off the five things every
  project does once, and links each number to the screen that owns it.

  Usability drove the rest. Every section carries a sentence saying what it is
  for. Every card has one obvious action instead of a row of equal-weight
  buttons. Empty states name the first step and offer the button that takes it
  ("No string folders yet" with a folder picker, "No charset chosen" with a
  create button) rather than showing a blank panel. Advanced knobs (the box
  the gallery measures in, a dictionary's licence notice) start collapsed.
  Actions answer where the eye already is: importing a font, promoting
  recorded characters or rescanning a charset says what changed in the window
  instead of only in the console. Clearing a recording asks first. The
  sidebar carries live status (the atlas's occupancy, Doctor's error count,
  whether the recorder is running), so the window says where to look before it
  is asked.

  One thing it does that it could not before: **the full ICU word lists
  install in one click.** They have always been in the package (4.2 MB under
  `Samples~`), and reaching them meant knowing to open the Package Manager,
  find the Samples tab and press Import, which is two windows away from the
  screen that says Thai is 40% segmented. The Dictionaries section now has an
  "Install word lists (4.2 MB)" button that copies them out of the package
  (nothing is downloaded), stores them compressed as assets under
  `Assets/Samples/OneText/<version>/`, registers them in project settings so a
  build ships them, and re-measures coverage on the spot: 40.3% → 100% on the
  same strings, in one press. The Package Manager route still works and is
  named in the card. The thin-coverage warning points at the button.

  Card titles are field labels, not sentences ("Segmentation coverage", not
  "How much of your text is segmented"), with the explanation demoted to the
  line under the title. The section ledes stay sentences: a headline says what
  a thing is, a lede says what a screen is for.

  The Fonts screen shows the whole chain, ending where it really ends: a
  "System fonts" tier saying whether OS fallback is on and what that costs, so
  the last resort is visible in the same list as the fonts the project ships
  rather than only in Doctor's findings.

  Nothing the Hub computed was changed. `TextDoctor`, `StringGallery`,
  `CharsetFolderScan`, `GlyphForensics`, `TextSourceScanner` and
  `TextPreviewRenderer` keep their APIs; the atlas pie is now a ring drawn
  with the vector API, with the eviction history beside it and both atlases,
  standard and precise, reported side by side. The gallery still renders
  every cell with the real engine, and forensics still maps a click back to
  the glyph box that was drawn.

  Two consequences worth naming. The window is built entirely from runtime
  UI Toolkit elements (no `ObjectField`, no `Foldout`) because editor
  controls drag the editor's chrome into a window whose point is not to look
  like one, and because a tree made only of those can be built and rendered
  without an editor GUI skin. Which is why the suite's one skipped test is
  gone: `HubWindowTests` could not run in batch mode while the Hub was IMGUI,
  and now every section builds, rebuilds, shows and ticks in CI.

- **The asset types have icons.** A font asset, a charset, a style, a word
  list, the settings and a sprite sheet all wore the same default script icon,
  so a project folder holding six of them read as six of nothing. Each now has
  its own, in the palette the site uses (green for what text is made of,
  amber for the data it is measured against, violet for what decorates it),
  drawn by `Tools/gen_asset_icons.py` into `Editor/Icons/` and attached with
  `[Icon]`. Drawn as distance fields with the standard library alone, and
  drawn fat, because the size that matters is sixteen pixels in a Project
  window rather than the sixty-four they are stored at.

### Fixed

- **iOS could not call HarfBuzz at all.** Every non-Web platform loads the
  native library by name, and on iOS that lookup has nowhere to land: an
  embedded framework is resolved through its install name, not through a
  search path holding the file, so the first shaping call threw
  DllNotFoundException on the simulator and would have thrown identically
  on every device. iOS now binds through `__Internal` instead, which works
  because UnityFramework already links the framework and every hb_* symbol
  is in the process before managed code runs. Found by the mobile smoke
  tier on its first ever iOS run, which is the kind of thing it is for.

- **Player builds no longer need a project setting to draw anything.** The
  SDF shader was resolved by name at runtime and referenced by nothing else,
  so Unity stripped it from every player: labels measured, wrapped and
  shaped exactly as they do in the editor, and drew zero glyphs. It looked
  like a font failure and was one line in the player log. The workaround
  (add **OneText/SDF** to Always Included Shaders) was documented, which
  meant every project met the bug once before reading about it.

  The shader now ships from `Runtime/Shaders/Resources/` and is loaded with
  `Resources.Load`, so the build includes it because of where it sits rather
  than because someone remembered a setting. Nothing to migrate: no scene,
  prefab or component stores a shader reference, existing labels keep
  sharing the same single material, and the `precise` MSDF path, colour
  emoji and the decorations still batch through it unchanged. `Shader.Find`
  remains as a fallback for projects that already added the line, and
  Doctor's new `sdf-shader` rule fails a build whose shader would reach the
  player through neither route. Proved by a macOS player, not by inspection:
  the built game logs the shader it drew through.

## [0.1.0] - 2026-08-05

First public release.

OpenType shaping (HarfBuzz + FreeType), the Unicode algorithms validated
against the full UCD conformance suites, SDF rendering on a shared dynamic
atlas, uGUI label and input field, rich text with named styles, colour emoji,
tag-driven animation, Asian typography, text decorations, the Hub tooling
window, and an input field that survives an IME. Windows, macOS, Linux,
Android and iOS natives included; WebGL not yet.

See [Docs/ROADMAP.md](Docs/ROADMAP.md) for what is done, what is next, and
the honest list of what has not been verified yet.
