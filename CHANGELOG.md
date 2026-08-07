# Changelog

## [Unreleased]

### Added

- **`OneTextMesh.Quality`: how dense a tile world text asks for.** Performance,
  Medium and High, whose values are the multiplier itself — 1, 2 and 4 times
  the density the point size implies — defaulting to Medium.

  A UI label needs nothing like this: its font size is in screen pixels, so
  the density it asks for is the density it gets. World text has no such
  number. A sign at thirty points is thirty points whether the camera is two
  metres away or twenty, and the component cannot see which; left to the point
  size alone, a nameplate the player walks up to is a tile magnified ten
  times. Measured on the reported scene — an auto-sized mesh that fitted to
  thirty points, so a 28 ppem tile filling the view — the crossbar of an 'A'
  was under two texels thick, and no amount of correctness in the field can
  put a junction into two texels. Medium clears it.

  Medium rather than Performance by default because world text is usually
  approached, and the cost is atlas area for those tiles only.

- **Lowercase TMP names on `OneTextLabel`, so a migrating project still
  compiles.** `text`, `fontSize`, `richText`, `enableAutoSizing`,
  `fontSizeMin`, `fontSizeMax`, `maxVisibleCharacters` and `SetText(string)`
  are aliases and nothing but aliases: each forwards to the PascalCase
  property that is the real API, none holds state, and every one is hidden
  from completion, so new code written against this class still reads
  OneText's own names.

  The reason is arithmetic. A project leaving TextMesh Pro has `label.text =`
  written in four hundred places, and whether those lines still compile on the
  afternoon somebody tries the swap decides whether the swap gets tried at
  all. `maxVisibleCharacters` is the one that is close rather than exact: it
  forwards to `MaxVisibleGraphemes`, and OneText counts grapheme clusters
  where TMP counted UTF-16 characters, so a flag emoji is one step here and
  four there. That is the right behaviour, and it is documented on the alias.

  `lineSpacing` and `alignment` are deliberately absent. TMP's line spacing is
  an offset and OneText's is a multiplier, so an alias would compile, run, and
  silently re-lay every paragraph in the project — a compile error is the
  better outcome, and it is the one you get. TMP's alignment names an enum
  that does not exist here. There are no no-op stubs either: a
  `ForceMeshUpdate` that does nothing is a bug report about a stale mesh,
  filed six months later.

- **A TMPro script rewriter, in the Hub's new Onboarding tab.** Scans every
  `.cs` file under `Assets` — packages are left alone — and offers the four
  renames that are mechanical: `TextMeshProUGUI` and `TMP_Text` to
  `OneTextLabel`, `TMP_InputField` to `OneTextInputField`, `TextMeshPro` to
  `OneTextMesh`, and `using TMPro;` to the namespaces those live in. The diff
  is on screen before anything is written, files are ticked one at a time, and
  Apply asks git whether the targets hold uncommitted work first — saying so
  plainly when git cannot be asked at all, because "I do not know" and "clean"
  are different answers.

  It is text processing and holds no reference to a TMPro type, which is what
  makes it work in the project that needs it most: the one where the package
  was removed an hour ago and nothing compiles, so no rename refactor will
  run.

  The scanner is a lexer rather than a regular expression, and that is the
  whole safety argument. A project's own text is full of the words `TMP_Text`
  and `TextMeshPro` — in dialogue, in log messages, in a verbatim Windows
  path, in a block commented out last year — and a pattern with word
  boundaries in it cannot tell any of those from code. Strings of all four
  kinds, character literals and both comment forms come out byte for byte
  identical, and a member access (`x.text`) is never touched.

  What it cannot finish, it names. A file mentioning `TMP_Dropdown`,
  `TMP_FontAsset` or a `using TMP = TMPro;` alias is reported with the
  identifier and the line before the button is pressed, rather than through a
  wall of compile errors after.

### Fixed

- **The whisker under a sharp vertex: the antialiasing width came off the
  median.** A distance field has unit gradient, so one screen pixel is a fixed
  step in field value and `fwidth` recovers it. The median does not: outside a
  corner it is the larger of two half-planes, so along the bisector of a
  twenty-degree vertex it falls at about a sixth of that rate, and it has a
  kink exactly on the bisector where the two swap. `fwidth` is a two-by-two
  quad difference, so on the kink it reports several times the real gradient;
  the band widens by that much, and because the field is also falling slowly
  there, the widened band reaches far down the bisector. That is the thin
  tapering spike below the point of an 'A' or a 'W' — not ink the field ever
  claimed (every texel of it measures within half a texel of the outline) but
  antialiasing spread along a direction the median is flat in.

  The width now comes off alpha, the ordinary single-channel field, which is
  smooth where the median kinks, shares the encoding, and arrived in the same
  fetch. The single-channel path is unchanged: there the two are one number.

- **A cluster's seam between two glyphs interpolated channels that meant
  different things.** Glyphs are coloured independently, so red is one edge
  inside one of them and an unrelated edge inside the other. The union picks a
  winner per texel, so across the texel where the winner changes the two
  triples are not comparable, and what bilinear puts between them belongs to
  neither — a spike hanging in the gap between the feet of two adjacent
  letters. The rasterizer now records which glyph won each texel and the
  interpolation pass flattens the seam, which costs nothing: a gap between two
  glyphs has no corner to keep sharp. World text meets this on every run,
  since it clusters everything.

- **World text baked every tile at the smallest density there is.**
  `OneTextMesh` passes a run's size to the atlas to choose a density, and that
  size is in local units — a tenth of the point size, by the TextMesh Pro
  convention the component ports from — while every one of those calls wants a
  pixels-per-em. A 55-point mesh therefore asked for five and a half pixels an
  em and got the smallest bucket on the ladder, 24 ppem, and so did every
  other world text in every project regardless of its size. Converted back to
  points at the one place the density is derived.

- **A cluster of glyphs took the wrong multi-channel field between them.**
  Rasterizing several glyphs into one tile resolves each as its own group and
  unions them, and the union was a per-channel minimum. That is wrong twice:
  the median of three minima is not the minimum of three medians, and a
  pseudo-distance means something only near the corner it was extended from —
  carried into another glyph's territory it is a half-plane that stopped
  bounding anything, and a minimum lets it win there. The nearest group now
  wins with all three of its channels at once, chosen by the same true
  distance the union of the shapes is defined by, so an edge buried inside
  another glyph's ink still cannot carve into it. Measured on four 'A's baked
  as one cluster at 48 ppem, ink up to 2.5 texels clear of the outline went
  back to the 1.5 a single glyph produces; on screen, the bitten stroke edges
  a magnified world mesh showed. World text hits this on every run, since it
  clusters everything; labels only where glyphs join.

- **MSDF error correction: the median can no longer contradict the field it
  is a reconstruction of.** The classic multi-channel artifact — two parts of
  a glyph whose channels cross, so the median is decided by an edge that has
  nothing to do with this texel — was deferred in M14 as "not yet a problem
  at the sizes `precise` is for". It is a problem: the CFF specimen's long
  shallow S-curve grows a detached block of ink four texels clear of the
  outline, in the padding ring the shader is entitled to read as empty.

  Every texel now checks the median against the true distance alpha already
  carries, under two rules. Where the two disagree about which *side* of the
  outline the texel is on, the true distance wins: that is the detached
  block. And where the truth places a texel solidly inside the ink but the
  median has drifted back to within a texel of the outline, the true distance
  wins again: that is the sag a crossbar junction leaves, which never crosses
  over and so shows up as a grey mark rather than a hole. Both are per-texel
  tests and not the neighbourhood search msdfgen runs, because msdfgen has to
  approximate the true distance from the same three channels it is checking
  and here it is already exact and free.

  The second rule does overrule a legitimate reconstruction — inside a reflex
  corner the median is the union of two half-planes and the truth is the cone
  to the corner point, and they differ by design. It is allowed to because
  nothing reads the median deeper than the 0.5 isoline: the face thresholds
  there, the outline threshold only moves outward, the glow is a falloff on
  (0.5 - d), and the shadow reads alpha. An inward threshold — a faux-bold
  dilate, an inner outline — would end that, and the rule would have to learn
  the difference; the reasoning is written out beside the code.

  `GlyphRasterizer.MsdfErrorCorrectionTexels` sets the allowance and zero
  turns it off; changing it bumps the new `GlyphRasterizer.Generation`, which
  the atlas keys tiles by, and `MsdfEdgeColoring.CornerAngleDegrees` now bumps
  it too — it decides the bytes of every multi-channel tile and was
  invalidating nothing.

- **MSDF error correction, the half that lives between texels.** The rules
  above are per-texel, and the artifact a magnified tile actually shows is
  not. Across an 'A' crossbar junction at 64 ppem, green falls and blue rises
  and the two swap rank partway between one texel and the next; the median
  follows the swap down to the outline while the true distance says a texel of
  solid ink — and both endpoint texels are inside tolerance the whole time.
  The median of three linear functions is piecewise linear with a kink at
  every crossing, and the kink is free to point the wrong way.

  So a second pass tests the crossings themselves: for every neighbouring
  pair, each channel pair is solved for where it swaps, and the median there
  is compared against the true distance interpolated to the same place. A
  texel whose crossing dives is flattened to its own median — not replaced by
  the true distance, which was tried and measured worse, because replacing it
  puts a step between the corrected texel and its neighbours and the
  interpolation across the step is a new artifact in place of the old one.
  Flattening moves the value nowhere; it removes only the disagreement that
  let the rank swap happen.

  Measured on the reported case — a 64 ppem 'A' drawn at 3x, which is an
  ordinary thing to ask of a distance field and which the single-channel path
  does cleanly — the darkest pixel inside solid ink goes from 0.937 to 0.988,
  and the count of dimmed pixels from 5 to 1. At 1x and at 6x it is 1.000.

  Known limit, measured rather than assumed: neither rule reaches anything
  within its own allowance of the outline, so a sub-texel residue survives at
  the sharpest junctions.

### Added

- **Auto-size: the label can now pick its own font size.** `AutoSize` on a
  `OneTextLabel` chooses the largest size in `[AutoSizeMin, AutoSizeMax]` at
  which the whole block fits the rect, by bisection over real layouts —
  fitting is monotonic, so ten measures bracket the answer to half a point,
  and the result snaps down to the half-point grid so the search cannot churn
  one atlas ppem bucket per fractional answer. The fit measures with overflow
  disabled (truncation makes every size "fit", which would leave the search
  nothing to compare) and judges both axes, so an unbreakable word that
  overflows the wrap side shrinks the text exactly as a stack of lines
  overflowing the block side does. Vertical labels fit against their own
  axes. The fit is part of the layout key: it re-runs when the text, the rect
  or the bounds change and never otherwise, and `FittedFontSize` reports the
  chosen size. `<size>` runs keep their absolute size — auto-size drives the
  base size only, and a tagged run that must not shrink is what an absolute
  size in markup means.

- **World-space text: `OneTextMesh`, no Canvas required.** A new
  `OneText.Mesh` assembly (depending only on Core — a project can ship world
  text without uGUI) with one component: the same shaping, layout, atlas and
  shader pipeline as the label, rendered through a MeshFilter/MeshRenderer
  pair for nameplates, signs and diegetic UI. The rect still comes from a
  RectTransform, so wrap, overflow, both alignments and auto-size mean what
  they mean on a label. Font sizes are points on TextMesh Pro's world-text
  scale — ten points to one local unit — so a TMP nameplate's numbers (size
  36, rect 20×5) port verbatim and land the same size on screen; `<size>`
  tags convert on the same scale, and em-relative values pass through
  unchanged. It draws through
  a clone of the shared SDF material with `unity_GUIZTestMode` pinned to
  LEqual — the canvas system drives that global per canvas, nothing drives it
  for a MeshRenderer, and world text should be occluded like world geometry.
  Atlas uploads batch to one per frame via `Application.onBeforeRender`
  (there is no canvas pass to ride), and each instance watches the atlas
  versions its mesh baked, so an eviction or compaction under a built mesh
  rebuilds it the way `AtlasInvalidation` rebuilds a label. Deliberately not
  in this first cut, and documented on the component: reveal/animation,
  decorations, inline sprites, style assets, interaction.

- **Backslash escapes resolve, so a localized `\n` is a newline.** A CSV cell
  or a JSON-ish string table has no way to hold a newline except as the two
  characters `\n`, and a label handed one straight from a table printed them,
  backslash and all — TextMesh Pro resolves these, so migrated strings arrived
  expecting the same. `OneTextLabel` now runs an escape pass before the markup
  parser (so every span and link index refers to the text the engine sees):
  `\n`, `\t`, `\v`, `\r`, `\\`, `\uXXXX` and `\UXXXXXXXX`, with everything
  else left exactly as written — a Windows path must not lose its separators
  because `\U` started a folder name, which is also why the hex forms only
  apply with every digit present. On by default (`Parse escapes` in the
  inspector, `ParseEscapes` in code); an input field turns it off alongside
  rich text, because a typed backslash is a backslash.

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

- **The Linux editor crashed on the first shaping call, because two HarfBuzzes
  were in the room.** Unity's editor links a HarfBuzz of its own for TextCore,
  ELF resolves an exported function through the process's global symbol scope
  with the first definition winning, and a library's calls to its *own*
  exported functions go through that scope like anything else. So our
  `hb_font_create` allocated a font and passed it to Unity's
  `hb_font_set_var_coords_normalized`, which walked a struct of a different
  shape and freed something that was never a pointer. The stack trace has both
  addresses in it, six hundred megabytes apart, in two different modules.

  macOS could not have caught this and never will: Mach-O's two-level namespace
  records which library each undefined symbol came from, so a dylib's calls to
  itself are bound to itself no matter what else is loaded. Same HarfBuzz, same
  version, same tests, green on one loader and a SIGSEGV on the other. The
  package has now made the two-HarfBuzzes mistake on Web and on Linux, in
  opposite directions: there it was a link error nobody could ignore, here it
  was a segfault forty-five frames deep.

  The Linux binary is therefore no longer vendored from the HarfBuzzSharp NuGet
  packages. It is built from HarfBuzz 14.2.1 by `Tools/build_linux_natives.sh`,
  in an old-glibc container, with `-Wl,-Bsymbolic`, which binds those calls at
  link time so they never reach the PLT. A version script cuts the dynamic
  symbol table to the 59 entry points `HarfBuzzApi.cs` names plus `hb_subset_*`
  (84 symbols, down from thousands), and libstdc++ is linked statically, so
  there is less of it able to collide with anything at all. The build workflow
  loads a stub HarfBuzz first and shapes through the real one afterwards, and
  refuses to ship a binary that answered the stub: the binary being replaced
  answers it once, and this one never does.

- **The 2022.3 editor could not find the library at all**, which looked like a
  missing dependency and was a `.meta` written by a newer editor. Unity 6
  writes `PluginImporter` metas as `serializedVersion: 3`; 2022.3 reads
  `serializedVersion: 2` and, handed the newer shape, guesses each plugin's
  platform from the folder it sits in. That covers `Linux64` and `Android` and
  cannot cover the editor's own OS, so 2022.3 logged `found 0 plugins`, never
  gave Mono a path, and threw `DllNotFoundException: libHarfBuzzSharp` from
  every test that shapes. The Linux plugin's import settings are written in the
  older form now, which Unity 6 reads back unchanged.

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
