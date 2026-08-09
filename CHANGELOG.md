# Changelog

## [Unreleased]

### Added

- **The Hub is now Project Settings > OneText, and that page finally holds the
  defaults a TextMesh Pro project goes looking for.** The window and the
  settings page were two places holding one subject: the default font was on
  the page, the fonts themselves were in the window, and the numbers a person
  actually wanted to change — the size a new label starts at, whether it takes
  clicks, whether it parses markup — were in neither, because they were C#
  field initializers, which is to say they were not the project's to decide.

  The page mounts the whole Hub, sidebar and all, with a new **Global
  Settings** section first in the list. It writes the settings asset through a
  SerializedObject, so every edit is undoable and marks the asset dirty the way
  the inspector did, and it says what each field is for instead of showing what
  it is called.

  New in the asset, and new as project decisions: **font size**, **auto-size
  bounds**, **wrapping**, **rich text**, **escape parsing**, **raycast target**,
  and the **container size** a label (320 × 80) and a world text (20 × 5, TMP's)
  are created with. A component reads them in `Reset`, which is what Unity runs
  when it is added from the GameObject menu, from Add Component, or from its own
  context menu; `OneTextLabel.ApplyProjectDefaults()` and the `OneTextMesh` one
  are public, so a component that has drifted can be put back. Nothing walks the
  project rewriting objects that already exist — a default decides the next one.

  `Window > OneText > Hub` opens the settings page, and every "project settings"
  button inside the Hub now goes to the Global Settings section rather than
  opening the window it is already inside.

- **A question, once, when the package is first installed.** A project that
  already draws text with TextMesh Pro gets asked whether it wants to see what
  moving off it involves, and is taken to Onboarding if it says yes. Asked once
  per project and remembered either way; never in batch mode, so a test run
  cannot stop on a modal dialog.

- **The three parity names that were deliberately absent — `alignment`,
  `lineSpacing`, `textWrappingMode` — plus `enableWordWrapping`, the pre-3.2
  spelling.** They were absent because a straight alias for any of them would
  lie: TMP's line spacing is an offset and OneText's a multiplier, and TMP's
  alignment and wrapping name enums this package did not have. What ships now
  is not a straight alias for any of them — each converts, through the same
  `TmpCompat` arithmetic the Onboarding migration has always used, so a value
  assigned through the alias and the same value carried by the migration
  cannot disagree.

  `alignment` needed its enum, so the package now declares
  `OneText.UGUI.TextAlignmentOptions` — TMP's names, TMP's values, verified
  member for member against the real enum by a test in the TMP-guarded
  assembly. Assigning splits it across `Alignment`/`VerticalAlignment`; the
  five distinctions OneText does not draw (Flush, GeoAligned, Baseline,
  Midline, Capline) resolve to the nearest one it does, exactly as the
  Onboarding report says they will. Reading reassembles the pair, with `Start`
  and `End` answering the edge they resolve to, since TMP never had a start
  edge. `lineSpacing` does the offset↔multiplier arithmetic both ways — `10`
  through the alias is `1.1` on the property, same intent if not the same
  pixels, and the readback is a float round trip, so compare it with an
  epsilon rather than `==`. `textWrappingMode` maps TMP's four modes onto
  OneText's two, and the whitespace-preservation half, which OneText does not
  hold, is dropped on assignment and absent on read.

  Because a converting alias never promises `get` returns the bits `set` was
  given (an approximated alignment reads back as what it resolved to, a line
  spacing as its float round trip), both setters and `LineSpacing`'s swallow
  writes that change nothing — TMP projects run `if (label.x != v) label.x = v`
  in Update, and that idiom must land as a no-op, not a nightly re-layout.

  Like the rest of the parity surface these are hidden from completion,
  forward-only in spirit, and tested in both directions. The script rewriter
  learned the two qualified enum names (`TMPro.TextAlignmentOptions`,
  `TMPro.TextWrappingModes`); unqualified uses were already covered by the
  `using` rewrite, because the new enums live under the names TMP used.

### Changed

- **A new label says `New Text` at size 36, not `مرحبا بالعالم` at 64.** The
  Arabic was there to prove the shaper ran before anybody typed anything, which
  it did; it is still the wrong thing to hand somebody who just added a
  component, and 64 was never the size the settings asset claimed new labels
  got. That claim is now true: `Default Font Size` had no reader at all before
  this, and 36 is both its value and TMP's.

- **Buttons, pills and badges are rounded rectangles rather than lozenges.** A
  980px radius makes the curve a function of the control's height, so a tall
  button and a short one disagreed about what the same corner looks like. They
  are 6px now (4px on badges), which is one shape at every size.

- **Onboarding says what to do before it says what it found.** The screen opens
  with the four buttons in the order they are meant to be pressed and one
  sentence each on what they do to the project, the two steps are labelled Step
  1 and Step 2, and "What this cannot finish" is now "What you have to fix by
  hand". The toggles say what turning them on does instead of naming the state
  they are in.

- **The system-font tier says where it runs.** It reads the fonts of the device
  the game is running on — `/system/fonts` on an Android phone, the system font
  folders elsewhere — in the build, not only in the editor. Every description
  of it said "this machine", which read as "the editor" and made the feature
  sound useless on a player's device.

- **The atlas budget's readouts follow its controls.** Changing the texture size
  or the layer count rewrites the memory and capacity figures under them
  instead of leaving the old numbers on screen. The layer count is a typed
  number rather than a sixteen-stop slider.

- **A missing shader is said once, not eight thousand times.** `SharedGlyphAtlas.
  Material` logged an error every time it was asked and the shader was not
  there, and every label asks on enable. On CI that turned one missing file into
  ~8,000 identical lines, and into 28 EditMode failures whose whole content was
  the words "Unhandled log message" — failures about the logging, sitting on top
  of the ones about the shader. It is reported once per domain now, and it names
  the graphics device and — in the editor — whether the asset is in the database
  at all, which is the difference between an import problem and a compilation
  one.

  That distinction was not decoration. The first run carrying this message
  answered the question the same day: `shaders named OneText-SDF in the
  database: 0` with `Shader.Find: null` on a working OpenGLCore device, which is
  an import problem and nothing else, and is what led to the CI layout fix
  below.

  A process with **no graphics device** loads no shaders by design, so there the
  report is a warning rather than an error: layout, measurement and the atlas
  all still work, and only drawing is unavailable. That is a description of a
  headless container, not of a fault.

- **The allocation tests stopped disagreeing with themselves.** Five of them
  asserted through `Is.Not.AllocatingGCMemory()`, which reads a single
  invocation — sound only if nothing else can allocate on that thread inside the
  window, which in an editor running a test suite is not true. The symptom was
  a suite where the same case passed and failed on consecutive runs with nothing
  changed, and two runs an hour apart failed disjoint sets: a failure there
  carried no information. They now measure many times and keep the smallest
  count. Noise can only add allocations, so one clean reading proves the steady
  state is clean, and a path that really allocates cannot produce one however
  many attempts it gets. Five consecutive full runs of the class: 6/6 passing,
  every time.

- **The Unicode coverage sweep gets a timeout that measures the right thing.**
  It takes ~124 s on an idle machine against Unity's default 180 s budget, so
  it failed at 224 s whenever the rest of the suite was competing for the
  machine. Ten minutes now — the distance between "slow" and "hung".

- **A project that installs this package no longer compiles the tools that
  build it.** The golden-image harness, the benchmark suite, the proof-image
  generators, the native-plugin importer batch and the Cluster debug dump were
  all in `Editor/`, with no constraint on them, which meant every consumer
  compiled them and got a `Tools > OneText > Golden Images` menu item for a
  baseline set they do not have. They live in `Editor/Dev/` now, behind
  `OneText.Editor.Dev` — an assembly constrained to `UNITY_INCLUDE_TESTS`, so it
  builds in a project that lists this package in `testables` (which is how the
  golden run, the benchmarks and the proof generators are driven) and nowhere
  else. `TmpMigrationProofGenerator` gets the same treatment, one folder deeper,
  since it also needs TextMesh Pro.

  Verified rather than assumed: in a project with no `testables` entry, Unity
  builds `OneText`, `OneText.UGUI`, `OneText.Mesh`, `OneText.Editor` and
  `OneText.Editor.Onboarding.Tmp`, and `GoldenRegen` and `BenchSuite` are not
  types that exist. In a project that does list it, all 843 tests still pass,
  the 28 golden-image ones included.

- **A published package carries the package.** `.npmignore` keeps `Tests/`,
  `Editor/Dev/`, `Docs/`, `Tools/`, `page~/`, `Web~/` and the repository's own
  furniture out of a registry tarball: 360 files, 32.6 MB unpacked, of which
  27.6 MB is the native plugins for every platform and 4.2 MB is the dictionary
  sample the Package Manager offers to import. Installing from the git URL still
  clones the repository — nothing can change that — which is why the assembly
  constraint above, not this file, is what actually keeps the dev tooling out of
  a consumer's compile.

- **Importing a font no longer freezes the editor for a minute.** Creating a
  font asset packed the file with brotli at quality 11, and quality 11 is the
  setting that costs a hundred times what the one below it costs. Measured
  here: Noto Sans CJK KR (15.7 MB) took **64 seconds** and stored 10.9 MB;
  Pretendard (6.4 MB) took **8.0 seconds** and stored 2.04 MB. At quality 6 the
  same two take **0.6 s → 12.6 MB** and **0.14 s → 2.30 MB**. So the editor
  froze for a minute to save the last 15 % of a font, on every drag-and-drop.

  Imports pack fast now — the whole `Create Font Asset` on that 6.4 MB face went
  from **10.1 s to 0.21 s** — and the Fonts section grew a **Pack smaller**
  button per font, which spends the minute deliberately on the build that ships
  it. Font assets made before this keep their existing packing and read back
  exactly as they did; the new field's zero value means "as small as it goes",
  which is what they are. Unpacking is unchanged, and it is the only half a
  player ever pays for.

- **Forensics opens in a tenth of the time.** Measured on a real project, that
  section took **433 ms** to appear: composing it shaped the sample string and
  rasterized every glyph in it, all before the panel existed. The panel is on
  screen in **9 ms** now and the layout happens on the next frame, which is also
  what makes typing in its text field feel like typing. Nothing else in the
  window is over 20 ms, and the sidebar refresh is 3 ms.

- **The sidebar stopped opening every font asset to count them.** The Fonts and
  Styles badges asked for the assets themselves — each font carrying a
  compressed copy of its .ttf — twice per refresh, and a refresh happens on
  every click in the window. They ask the search index for a count now.

### Fixed

- **CI has never once been able to load an asset out of this package, and now
  can.** The throwaway project the suite runs from was created *inside* the
  package it tests: this repository's root is the package, and the project sat
  in it. That arrangement import-loops, a trailing tilde on the folder name
  stopped the loop, and because it stopped the loop it looked settled — the code
  compiled, every test that read a file through `File.ReadAllBytes` passed, and
  316 of the package's assets were in the `AssetDatabase` with paths and GUIDs.

  None of them had been imported. `FindAssets("t:Shader")` under the package
  returned 0 while the shader's own GUID resolved from its path;
  `LoadMainAssetAtPath` returned NULL; so did `Resources.Load`. Every test that
  loaded the SDF shader, the Hub's UXML or its USS failed, and every test that
  did not, passed — about a hundred of them, on Linux **and on Windows**. That
  it was both is the part that had been hiding in plain sight: this was filed as
  a Linux bug for as long as it was, and Linux was only where anyone looked.

  The project is created beside the package now rather than within it, which is
  what the dev project this package is written against has always done and why
  it never showed any of this. The `Library` cache keys carry a `v2`, because
  every cache written before this holds an artifact database in which these
  assets never imported and `restore-keys` matches on prefix — it would have
  restored the broken state into the fixed tree.

- **The 2022.3 job builds again, and has run a test for the first time since the
  scene-and-prefab migration landed.** It died at compile on four `CS1503`s that
  named neither the cause nor the version. `placeholder.SetText` written as a
  method group resolves against the TextMesh Pro inside Unity 6 and not at all
  against TMP 3.0.7, which is what 2022.3 pulls in: 3.0.7 has only
  `SetText(string, bool syncTextInputBox = true)`, and a method group whose only
  candidate has an optional parameter does not convert to
  `UnityAction<string>` — a method group conversion does not fill optional
  parameters in. The generic `AddPersistentListener` stopped being applicable,
  resolution fell back to the non-generic pair, and the error came out naming
  `UnityEvent`, which the line never mentions. The signature is named through
  reflection instead of left to overload resolution.

- **A test that cannot run where it is now reports Skipped, which is what it
  always meant.** Fifteen guards — no DOTween, no coverage fonts, no colour
  emoji font, no git, a renderer that does not match the stamp the goldens were
  taken on — said `Assert.Inconclusive`. Unity's command-line runner exits 2
  when a suite does not come back Passed, and Inconclusive is not Passed, so a
  run with nothing wrong in it reported failure: the 2022.3 job went red holding
  737 passed and 0 failed. `Assert.Ignore` is the API that means what these say,
  and the suite was already using it in eleven other places.

- **`DomainReloadTests` reads its font inside the play session rather than
  before it.** The `[SetUp]` turns Domain Reload off, but a project that had it
  on when the run started still reloads on the way into the first play session:
  the setting lands, and the entry already under way does not get it. The reload
  discards the iterator's locals, so bytes read before the loop came back empty
  and `FontData.Load` threw on an empty array — in the first test of the class
  only. The dev project already has Domain Reload off in its `EditorSettings`,
  so its first entry never reloads and the failure could not happen there.

- **The Windows smoke build stops asking git a question the workspace root
  cannot answer.** `unity-builder` derives a build number from git at the
  workspace root, and the checkout moved into a subdirectory, so
  `git rev-parse --is-shallow-repository` exited 128 before the build started.
  Nothing reads that version; the smoke player exists to print one marker into a
  log and is then discarded.

## [0.2.0] - 2026-08-08

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

- **Component migration: the scenes and prefabs, scanned, judged and swapped
  in place.** The other half of leaving TextMesh Pro, in the same Onboarding
  tab and above the script rewrite, because it is the half a project decides
  on. Scan opens every scene in Build Settings (or every scene under `Assets`)
  and every prefab, reads every component on every object including the
  inactive ones, and closes them again having saved nothing. Convert then does
  the whole scan a second time from scratch and only then destroys anything:
  the first pass's component references stopped being valid the moment its
  scene closed, and a migration that acted on a stale one would act on the
  wrong object.

  `TextMeshProUGUI` becomes `OneTextLabel`, `TextMeshPro` becomes
  `OneTextMesh`, `TMP_InputField` becomes `OneTextInputField`, and
  `UnityEngine.UI.Text` and `TextMesh` come along too — a project that left
  uGUI text for TMP years ago and never finished has both. `TMP_Dropdown` is
  found, counted and left exactly where it is, because there is no OneText
  dropdown and saying so is better than a swap that loses a caption.

  The two failures worth naming are the two nothing else catches. The first is
  the reference: every serialized `ObjectReference` on every component in the
  container is walked, and any that pointed at a component about to be
  destroyed is re-pointed at the one replacing it and then *read back* — a
  field still declared as a TMP type silently refuses a `OneTextLabel`, and
  from the writing side that refusal looks exactly like success. When it does
  not stick, the finding says which field, on which object, and to run the
  script rewrite first. The second is the listener: the buttons a designer
  wired in the inspector are the part of a migration nobody can reconstruct
  from a screenshot, and a component swap destroys them silently. `On Value
  Changed` and `On Submit` are both `UnityEvent<string>` on both sides, so
  their persistent calls are carried across as serialized data — with the
  target itself run through the map of what became what, since the object a
  listener names is quite often the very label being replaced — and counted
  again afterwards. `On End Edit` has no counterpart and is reported rather
  than moved.

  Fonts are followed back to the file. OneText rasterises from the `.ttf`, so
  a TMP font asset is useful here for the source it names; one `OneFontAsset`
  is made per source file however many labels share it, and a font asset baked
  to a static atlas — which is every font asset TMP ships, including the
  LiberationSans in a first project — is followed through the GUID it keeps
  rather than declared missing. TMP's project-wide default and global
  fallbacks are offered to OneText's own settings, and only ever fill a blank.

  Prefabs convert before scenes, and a base prefab before anything built out
  of it. That ordering is the difference between a migration and a mess: a
  variant converted first records the swap as an override on a base that still
  holds the old component, and the object ends up carrying both. Converting
  the base first means the variant, when it opens, has simply nothing left to
  convert — which is also why converting twice is a no-op, asserted rather
  than hoped for.

  Everything that will not survive is named before the button, not after:
  unsupported markup that will print literally, a TMP margin OneText has no
  concept of (the rect is the text box), alignment and overflow modes with no
  equivalent, a line spacing that is an offset there and a multiplier here, a
  sprite or animation tag on world text that has neither, a font asset with no
  file behind it. Errors, warnings and notes, drawn the way Doctor draws them,
  and available on the command line — `-executeMethod
  OneText.Editor.ComponentMigration.RunFromCommandLine` scans, reports and
  exits 1, so a team mid-migration can put "no TMP components left" in a
  pipeline instead of in somebody's memory.

  The TMP-reading half lives in its own assembly, gated on TMP being installed,
  and it only reads: it fills a struct of primitives and OneText enums, and
  every component that is destroyed and every serialized field that is written
  is done by the ungated engine. That is deliberate. It means the dangerous
  code is ordinary editor code that CI can test on a machine which has never
  had TMP installed, and it means the tab still migrates `UnityEngine.UI.Text`
  and `TextMesh` — and says plainly that TMP was not found — in a project that
  already removed the package.

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

  The other nine plugins had the same `.meta` problem with quieter symptoms:
  folder-guessing covers the player platforms, so what 2022.3 lost was only
  what no folder name can imply — the macOS dylib's editor OS, and the iOS
  framework's `AddToEmbeddedBinaries`, the flag without which the framework is
  in the Xcode project and absent from the shipped app. All ten `.meta`s are
  the older form now, GUIDs untouched, with the Android `Is16KbAligned` values
  and the iOS embed flag carried over intact. Fixing the first assertion in
  line let the tagging test walk further than it ever had, and it found one
  more: 2022.3 has no CPU choice for Linux and reads `x86_64` back as
  `AnyCPU`, which on a one-ABI platform is the same claim, and is now the one
  substitution the test accepts.

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
