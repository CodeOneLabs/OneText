# Roadmap

## At a glance

| | |
|---|---|
| **Done** | M0 to M15, shipped as v0.1.0 (2026-08-05): shaping, the Unicode algorithms, layout, the shared atlas, uGUI components, natives for every platform including wasm, rich text, colour emoji, animation, Asian typography, the Hub, an input field that survives an IME, decorations, MSDF, ruby, vertical writing. Then v0.2.0 (2026-08-08): world-space text, auto-size, MSDF error correction, and the Hub's Onboarding tab for leaving TMP. Then v0.3.0 (2026-08-11): a migration that runs on part of a project and mends the rest, the rich-text tags a real TMP project actually contains, and a `<b>` that can reach a designed bold |
| **Now: M16** | Ship it: a remote and CI's first green run, the browser demo, the platform and IME matrix on real hardware, OpenUPM listing and the TMP migration guide (the tooling shipped in v0.2.0 and grew up in v0.3.0; the prose has not) |
| **Next: M17** | The known gaps: COLRv1, tate-chū-yoko, vertical editing, Thai proven against real data |
| **Later** | The open squares: UI Toolkit frontend, ECS/DOTS, accessibility |
| **1.0** | A trust claim, not a feature list: every platform verified on real hardware, CI green as a standing condition, the IME matrix passed, external projects shipping on the package |

The premise behind the ordering: with v0.1.0 out, the bottleneck is no longer
features. The feature set is already past TMP and the commercial alternatives
on the ground this project chose; what it has instead is an audience of
approximately one, a test suite whose trust is confined to a single Mac, and a
CI that has never run because the repository has no remote. The risk from here
is not a missing milestone; it is the bug nobody has met because nobody has
run the package. So the next milestones front-load distribution and
verification over new capability, deliberately.

## Done: M0 to M15, briefly

One line each. The measurements, the field research, the design decisions and
what each milestone honestly deferred are preserved in this file's git history
and in [CHANGELOG.md](../CHANGELOG.md); the deferred items that still matter
are all in M16 and M17 below.

- **M0 to M4**: package scaffolding; HarfBuzz shaping; the Burst SDF atlas; the
  Unicode algorithms (UAX #9/#14/#29, validated against the full UCD
  conformance suites); layout with wrapping, alignment, font fallback and
  variable fonts.
- **M5 to M7**: the uGUI label and input field with links and inspectors; the
  atlas at scale (per-tile LRU eviction, defragmentation, prewarm,
  configurable budget, partial upload); prebuilt natives for Windows, Linux,
  Android and iOS beside macOS (HarfBuzz 14.2.1 from one build tree), plus
  hardening against the five failure classes the field keeps shipping fixes
  for.
- **M8 to M9**: rich text markup and named styles; colour emoji (ZWJ sequences
  as single glyphs) and inline sprites in one draw call; tag-driven animation
  with cluster-aware reveal and a custom-effect API; font subsetting that
  provably preserves GSUB/GPOS.
- **M10 to M12**: Asian typography (locale-aware fallback and `locl`, kinsoku,
  punctuation compression, CJK-Latin spacing, dictionary line breaking, the
  josa tag); the Hub (one tooling window with charsets, dictionaries, the
  atlas pie, the string gallery, glyph forensics and Doctor, the renderability
  lint CI can fail a merge on); text editing that survives an IME, with the
  composition state in a testable model rather than a MonoBehaviour.
- **M13 to M15**: HarfBuzz on wasm, symbol-prefixed past Unity's own copy and
  verified in real WebGL2 and WebGPU players (the demo site itself moved to
  M16); decorations and MSDF as the per-label `precise` option, neither
  splitting a draw call; ruby placed by the W3C rules; vertical writing by
  UAX #50, reusing the whole line-breaking pipeline down the column.

Measured against TextMeshPro (M-series Mac, 50 labels, Noto Sans 24px):
rebuilds at 51 µs/label to TMP's 62, layout ~1500 chars/ms, shaping ~13.5k
(Latin) and ~11.6k (Arabic) chars/ms. Ahead everywhere except a glyph's first
appearance, which TMP pays offline and we pay live. Method and full figures:
`Docs/BENCHMARKS.md`.

## M16, ship it: remote, CI, demo, listing

Everything in this milestone is infrastructure or verification, and every
item is one the roadmap has already promised somewhere.

1. **A remote, and CI's first green run.** The UCD conformance suites on
   2022.3 and Unity 6, `NativesTests`, and the `[perf]` lines: actually
   running, on machines that are not this one. Until then, 454 passing tests
   are a claim, not a record.
2. **The browser demo, the open half of M13.** The wasm native shipped;
   what remains is the site: Arabic and Devanagari shaping correctly, a
   Korean input field composing correctly, ZWJ emoji as single glyphs, next
   to a TMP comparison. WebGL text input lands here too (the hidden-HTML-input
   technique), which completes M12 on the one platform where it is hardest.
   Worth more than any README, and marketing is this project's scarcest
   resource.
3. **The platform matrix, actually run.** Windows first (il2cpp's
   `ProbeForLibrary` and the `libHarfBuzzSharp.dll` name is exactly the class
   of bug that compiles perfectly and fails on one platform), then Android
   (the 16 KB alignment asserted on a real device), iOS (the xcframework
   through a real Xcode build), Linux. IME verification widens the same way:
   `ImeCommitArbiter`'s grace window is aimed at behaviour that differs *by
   platform and IME*, so macOS-only manual testing exercises half its design.
   Windows Korean and Japanese IMEs by hand; the mobile soft-keyboard path on
   at least one device per OS.
4. **OpenUPM listing and the TMP migration guide.** Promised with M7, still
   unwritten. This is the front door for adoption; the demo is the window
   display.

## M17: The known gaps

The items the earlier milestones deferred honestly, ordered by how soon a
real user would file each one:

1. **COLRv1.** Deferred in M8 as "a paint graph deserves a milestone rather
   than a half-implementation". Newer emoji and icon fonts lean on it more
   every year.
2. ~~**MSDF error correction.**~~ Done, both halves: the per-texel rules
   folded into the rasterize job, and the pass over neighbouring pairs that
   catches a median which is right at every texel and dives between two of
   them. See the changelog. What is left is a sub-texel residue at the
   sharpest junctions, inside the allowance either rule is willing to act on;
   it is bounded and tunable rather than open-ended.
3. **Tate-chū-yoko (縦中横).** Two or three digits set horizontally inside a
   vertical column: a nested layout with em-fitting rules, excluded from
   M15 by scope. The visual-novel market that wants vertical text wants this
   in the same breath.
4. **Vertical caret and editing, `vrt2`.** Behind demand; rendering-only
   vertical text may be enough for most of its audience.
5. **Thai, proven rather than plumbed.** The trie, the Hub import and the
   coverage number all exist; what does not is a real Thai sample project
   with ICU's dictionary loaded and the before/after coverage recorded in
   the docs.

## Beyond: the open squares

Kept visible because the core was built UI-framework-free for exactly these,
in priority order:

1. **UI Toolkit frontend.** A thin layer over the same core; unclaimed by
   anyone. This is also where Unity's own Advanced Text Generator lives, so
   it is the one square where first-party competition exists, and the one
   where a single shaping core serving uGUI *and* UI Toolkit would be unique.
   Start when the issue tracker asks for it.
2. ~~**World-space text.**~~ Done in v0.2.0, and it was the cheapest square
   on the board exactly as predicted: `OneTextMesh` runs the whole pipeline
   through a MeshRenderer with no Canvas anywhere, on TMP's world scale so a
   migrated nameplate keeps its numbers. What it deliberately does not carry
   is reveal, sprites, styles and interaction — the smaller component, on
   purpose.
3. **ECS/DOTS.** Shaped text for entities exists nowhere; the community
   package that tries is built on TextCore and cannot shape.
4. **Accessibility.** Exposing text to screen readers; no text asset even
   advertises it.

## What 1.0 means

A version number is a trust claim, so the criteria are trust criteria, not a
feature list: every platform in the matrix verified on real hardware; CI
green as a standing condition, not an event; the IME matrix (Windows and
macOS against Korean, Japanese and Chinese, plus the Android and iOS
soft-keyboard paths) passed and recorded; and real external projects
shipping on the package, with their issues answered. The single-maintainer
fear in every community thread about third-party text is answered by that
record and by nothing else.

## Trust: the standing commitments

- **Benchmarks name their versions.** Any published figure states the exact
  version of TMP or UniText measured and the scenario, and the comparison
  harness stays reproducible.
- **Every Unicode algorithm ships with its full UCD conformance run** in CI,
  on 2022.3 and Unity 6, with real numbers logged as `[perf]` lines on every
  run.
- **The clean-room policy** (`CONTRIBUTING.md`) stays absolute: competitors
  are measured as built packages and read as public documentation, never as
  source.

The head-to-head against TextMeshPro and UniText lives in the dev project,
not in the package, so shipping never depends on either; results and method
are in `Docs/BENCHMARKS.md`.
