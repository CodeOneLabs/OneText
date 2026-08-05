# Roadmap

Where the project stands and what comes next, with the measurements and the
field research behind each decision. Milestones M0–M12 are done — each of the
sections below says so in its heading; the rest are the plan from here.

## Where we are (2026-08-04)

Done: package scaffolding (M0), HarfBuzz shaping (M1), Burst SDF atlas (M2),
the Unicode algorithms (M3 — UAX #9/#14/#29, all validated against the full
UCD conformance suites), layout (M4 — wrapping, alignment, fallback, variable
fonts), the uGUI components (M5 — label, input field, links, inspectors), and
the atlas at scale (M6 — per-tile eviction, defragmentation, prewarm,
configurable budget, partial upload) and the platform natives (M7 — Windows,
Linux, Android and iOS alongside macOS). Then rich text and sprites (M8),
tag-driven animation (M9), Asian typography (M10), the Hub (M11) and an input
field that composes (M12).

Measured on an M-series Mac, 50 labels, Noto Sans 24px:

| | OneText | TextMeshPro |
|---|---|---|
| rebuild, same text | 51 µs/label | 62 µs/label |
| rebuild, changed text, tiles cached | 56 µs/label | — |
| rebuild, changed text, new tiles | 116 µs/label | 68 µs/label |
| atlas upload | 175 µs for 10 new tiles (637 µs if the whole array goes up) | pre-baked |
| atlas memory | 4 MB shared by every font, configurable | per font asset |

Layout runs at ~1500 chars/ms, shaping at ~13.5k (Latin) and ~11.6k (Arabic)
chars/ms. We are ahead everywhere except the first appearance of a glyph,
where TMP pays the cost offline and we pay it live.

Hardening (below) is done: the five failure classes now have tests, and the
two live bugs among them — permanently-blanked glyphs after an atlas
overflow, and an outline extractor whose callback state was shared between
threads — are fixed.

Known gaps, honestly: natives now ship for every platform but WebGL, though
only macOS has actually been run; single-channel SDF rounds sharp junction
corners (MSDF is the cure); Thai and its neighbours break by dictionary
against the ~90-word starter list we ship, and correct Thai wrapping means
loading ICU's ~200 KB dictionary — which the Hub now imports in one drop, and
Doctor now fails a build over, but which is still the project's decision to
make rather than something the package carries; and the input field has never
been tested against a real IME.

## What the field research says (2026-08-02)

Before ordering the milestones we surveyed what developers actually complain
about — Unity Discussions and the issue tracker, and the Korean, Japanese and
Chinese dev communities. Ranked by how loud and how universal the pain is:

1. **Shaping in uGUI is unserved.** TextMesh Pro is effectively frozen — its
   maintainer left the text team in 2022 and the package was folded into
   `com.unity.ugui` for bugfix-level maintenance
   ([thread](https://discussions.unity.com/t/2023-2-latest-development-on-textmesh-pro/917387)).
   Unity's real fix, the Advanced Text Generator, is **UI-Toolkit-only**, and
   the top replies to its
   [announcement](https://discussions.unity.com/t/announcing-full-rtl-language-support/1544214)
   are uGUI and world-space users pointing out they get nothing. The
   community workaround for Arabic (RTLTMPro) is a presentation-forms
   substitution that breaks on rich text and input fields, and its
   maintenance has stalled. Devanagari, Thai and Khmer have no workaround at
   all — people fork the TMP package source or bake text to images.

2. **The font-asset/atlas workflow.** Static atlases mean predicting every
   glyph (Korean is 11,172 syllables; the common 2,350-syllable subset breaks
   on any chat or name-entry text), dynamic atlases mean first-use hitches,
   glyphs lost when the atlas fills, and a serialized asset that mutates
   every play session. Fallback fonts render with the wrong material preset;
   runtime font swaps lose the preset entirely. This is the pain our dynamic
   per-tile atlas already answers — it is the story to lead with.

3. **IME input.** The Korean composition bugs in `TMP_InputField` are
   community-famous — last character lost or duplicated on focus change,
   backspace corrupting composition, WebGL decomposing "한글" into
   "ㅎㅏㄴㄱㅡㄹ"
   ([tracker](https://issuetracker.unity3d.com/issues/inputfield-fails-to-get-the-last-character-of-a-hangul-ime-korean-character-text-when-focus-is-shifted-with-the-tab-key),
   [thread](https://discussions.unity.com/t/inputfield-bugs-korean-language/754691)).
   Chinese input can crash `TMP_InputField` outright. Nobody offers a
   correct, free input field — editing is where the commercial alternative
   draws its paid line.

4. **Asian typography beyond shaping.** Kinsoku (禁則処理/금칙/避头尾) in TMP
   is two editable text files, and buggy at the edges — a Japanese
   exclamation mark can break wrapping entirely
   ([tracker](https://issuetracker.unity3d.com/issues/textmesh-pro-wrapping-does-not-work-correctly-when-japanese-exclamation-marks-are-used)).
   Punctuation compression (約物詰め/标点挤压) exists nowhere. Han
   unification means the fallback chain, not the locale, decides whether a
   Chinese player sees Japanese glyph shapes
   ([thread](https://discussions.unity.com/t/textmeshpro-and-best-way-to-handle-cjk-unified-ideograph-in-fallbacks/824503)).
   Ruby (furigana) and vertical text survive on community tag hacks with
   documented breakage.

5. **Emoji.** TMP renders multi-codepoint sequences — ZWJ families, flags,
   skin tones — as separate glyphs
   ([thread](https://discussions.unity.com/t/textmesh-pro-cannot-display-multi-codepoint-emoji/1543082));
   the standard workaround is a hand-maintained sprite sheet.

White space nobody serves at all: a UI Toolkit backend, shaped text for ECS,
vertical writing, accessibility. Our core is UI-framework-free by design, so
these stay open to us.

The [UniText changelog](https://unity.lightside.media/en/unitext/changelog/)
(public docs only, per the clean-room policy in `CONTRIBUTING.md`) doubles as
a map of where the dragons live: the bug categories it fixes repeatedly —
atlas lifecycle, CFF and overlapping-contour outlines, emoji sequences,
domain-reload ghosts, parallel-shaping crashes, Unity version churn — are the
failure classes our hardening list and tests are built from.

## Hardening — before M7

**Done.** Five failure classes, each taken from a bug the field has already
shipped fixes for — several of them more than once, which is the useful
signal. They were all in code we already had, and all the kind that surface as
"text is blank" or "a character silently disappeared" long after the change
that caused them. Two were live bugs, two were latent traps, one was already
closed. The tests are the lasting part, and the ones that matter fail on the
old code — which is the standard they were held to, individually, by
reintroducing each bug and watching them go red.

1. ~~**Statics across a play session with Domain Reload off.**~~ **Done.**
   UniText fixed this three times (2.8.3, 2.12.1, 2.12.10: blank text, ghost
   quads). `DomainReloadTests` enters and exits play mode twice with reload
   disabled — holding the shared atlas open across the boundary, which is what
   makes the previous session's engine objects reachable at all — and draws
   text in both. Three latent traps closed behind it: the atlas watcher was
   guarded by a "did we already create it" flag, so the scene object that
   drives mesh invalidation was never re-created for a second session; the
   per-session statics in `AtlasInvalidation` (registry, backoff, warning
   latch) now reset at `SubsystemRegistration`, which fires per session with or
   without a reload; and the rasterizer's `Application.quitting` subscription
   stacked up once per session. The atlas texture is now `DontSave`, so a
   managed reference that outlives a session cannot be left pointing at a
   destroyed `Texture2DArray`.

2. ~~**Glyph loss under atlas pressure.**~~ **Done.** UniText 2.8.3:
   "rasterizing many glyphs in one frame could permanently drop some of them."
   Ours did too, by a different route: a tile that found no room was cached as
   a blank entry, so a frame that briefly overflowed turned into a glyph that
   was blank for the rest of the session — including after the budget was
   raised. Failed allocations are no longer cached; they are counted in
   `GlyphAtlasStats.Drops` and retried later, and the "does not fit" error is
   logged once per atlas rather than once per glyph. The eviction loop itself
   was sound: `AtlasPressureTests` runs two frames that each ask for 244 glyphs
   into a 256×256 layer holding three — no flush *within* a frame, so every
   tile is pinned as recently used — and afterwards a working set that fits
   comes back with pixels. Mixed 24/96 ppem thrashing over a 512×512 layer
   converges with zero drops and still serves every glyph at both sizes. The
   discriminating case is a cluster too wide for any layer, which is the one
   allocation failure eviction cannot solve.

3. ~~**CFF/PostScript outlines, overlapping subpaths, counters.**~~ **Done.**
   UniText fixed distorted `.otf` curves (2.2.7), artifacts on overlapping or
   self-intersecting contours (2.2.11) and on glyphs with holes (2.0.12), and
   both our test fonts were TrueType — so the cubic half of the flattening had
   never been rendered by a test, right after the tolerance changed.
   `Tests/Fonts/CffShapes.otf` is authored for it rather than vendored, so
   there is no third-party licence in the test data: a counter, two
   overlapping contours and a long shallow S-curve, built by the script beside
   it. `OutlineFormatTests` covers all three failures plus culling parity on
   CFF, and answers the open question about the tolerance — against the
   densest flattening the extractor will produce, glyphs land within **3 of
   255 levels at worst and about 1 on average**, quadratics and cubics alike.
   Where that disagreed with the old fixed 8-segment subdivision by up to 24
   levels, the fixed subdivision was the one that was wrong.

4. ~~**uGUI 2.6+ `ILayoutElement`.**~~ **Done.** UniText 2.8.1: the interface
   gained max-size members on newer Unity 6 editors and the package stopped
   compiling. uGUI 2.6 adds `maxWidth` and `maxHeight`; the label implements
   them behind a `versionDefines` entry in `OneText.UGUI.asmdef`, returning
   -1 ("not set", the same convention flexible size uses). The expression is a
   bare `2.6.0`, which Unity reads as "2.6.0 or newer" — verified against the
   editor rather than assumed, because the obvious defensive rewrite
   (`[2.6.0,)`, a half-open range) is a syntax error Unity reports at compile
   time. Nothing breaks on the uGUI 2.0.0 this project resolves.

5. ~~**Thread safety of shared font state.**~~ **Done.** UniText shipped at
   least three fixes for parallel-shaping crashes and cross-contamination
   (2.3.1, 2.3.2, 2.5.0 — native heap corruption, variable-font axes leaking
   between simultaneous shapes). We still do not shape in parallel, which is
   why this was worth writing now: `ThreadSafetyTests` fails on the old code
   with a null reference from inside an hb-draw callback, and would have failed
   far more quietly later. What it found: the outline extractor kept the
   half-built contour in plain statics, because hb-draw callbacks are static
   functions with no user-data pointer — two threads extracting two glyphs
   produced one glyph made of both. That state is per thread now, and the
   draw-funcs object is built under a lock and published once.

   The rest is the API that makes concurrency legal rather than lucky. The
   face is `hb_face_make_immutable`'d at load, which is HarfBuzz's own
   condition for sharing; `FontData.ForCurrentThread()` hands each thread its
   own `hb_font_t` over that shared face — including on a *variant*, which is
   where the axes actually live and therefore the instance most likely to be
   shaped from several threads at once. A font handle carries the variation
   coordinates and a lazily-populated shaping cache, and the failure there is a
   word at another label's weight rather than a crash. Handles are owned by the
   font and disposed with it; when the axes change they are set aside rather
   than destroyed, because a worker may be inside `hb_shape` with one and
   `hb_font_destroy` under it is a native use-after-free with no managed stack.
   The ink-bounds cache is locked, with its outline fallback measuring outside
   the lock. Rasterization stays main-thread, because the atlas is: the tests
   shape, measure and extract concurrently and assert every result matches the
   single-threaded answer exactly — including Arabic, where a corrupted shape
   is a corrupted word rather than a wrong advance.

   Two caveats worth stating rather than hiding. The uGUI 2.6 branch in item 4
   is guarded by a `versionDefines` range, so it has never been compiled by a
   green run — this project resolves uGUI 2.0.0, and there is no 2.6 editor to
   test against yet. And the ink-bounds contention test races eight threads to
   fill one cold cache, which is a probabilistic check rather than a proof: it
   is a smoke test for the lock, not the reason to trust it.

## M7 — Platform natives (everything but WebGL) — **done**

Prebuilt HarfBuzz natives for Windows (x64, x86, ARM64), Linux (x64), Android
(arm64-v8a, armeabi-v7a, x86_64) and iOS (one `.xcframework` carrying device
and simulator), from the same MIT-licensed SkiaSharp NuGet family the macOS
dylib came from — HarfBuzz 14.2.1 everywhere, one build tree, so the platforms
cannot drift apart in ways that only show up in one language. 26 MB committed.

This was scheduled after rich text; it moved ahead of it because it is the
adoption gate: a macOS-only package has an audience of approximately one,
and every milestone after this one benefits from people actually trying the
package and filing issues. WebGL stays out — it is the only platform that
needs real toolchain work (Emscripten, matched to the editor) and it gets its
own milestone with the demo site it enables.

Three things the field warned about, and what each turned into:

- **Android 16 KB page size.** Google requires it of 64-bit libraries and
  rejects at submission, not at build. Both 64-bit ABIs are aligned `0x4000`;
  Unity records what it found as `Is16KbAligned` in the plugin's `.meta`, and
  a test asserts it rather than trusting the vendor.
- **`harfbuzz-subset` in every build.** It is a separate library in HarfBuzz's
  build, so a binary can be a fine shaper with no subsetting at all — which
  would make the milestone below a feature that exists depending on which
  platform loaded. All ten binaries export the same 31 `hb_subset_*` symbols,
  checked at vendor time; `HarfBuzzSubset.IsAvailable` asks the same question
  at runtime.
- **CI on more than one host.** The matrix gains a Windows editor, marked
  `continue-on-error` — game-ci documents package testing as Linux-only, so a
  job nobody has watched go green must not block a pull request. The static
  half is what actually guards the matrix: `NativesTests` asserts every binary
  is present, tagged for its own platform and CPU and editor OS, and **not**
  enabled for any other platform. That last direction is the one that matters.
  Every one of these files is called `libHarfBuzzSharp`, and Unity refuses a
  build when two of the same name are enabled for one platform, so a wrong
  mask is not a subtle failure — it is a broken build for whoever installs the
  package.

Two mistakes were made in one line of P/Invoke, and both are worth recording
because both compile perfectly and fail on exactly one platform.

The first was `__Internal` for iOS, which names a library linked into the
executable — but this NuGet family ships iOS as a **dynamic framework** and no
static build at all, so the name has to be the framework's. The second was the
fix for it: `HarfBuzzSharp`, on the assumption that every loader tries
`lib` + name + extension. POSIX does, which is why macOS, Linux and Android
were fine and the macOS suite stayed green. Windows does not — il2cpp's
`ProbeForLibrary` opens the name it is given and nothing else, so
`HarfBuzzSharp` looks for `HarfBuzzSharp.dll` and never finds
`libHarfBuzzSharp.dll`. The answer is to spell the `lib` out everywhere, which
is what SkiaSharp does, for this reason.

iOS brought two more problems in the box. Microsoft's *device* framework
carries a *simulator* `Info.plist` (`CFBundleSupportedPlatforms:
[iPhoneSimulator]`), which fails device install and App Store validation, and
its binary carries a legacy x86_64 slice the modern simulator framework beside
it makes redundant. And shipping device and simulator as two plugins is the
same-name collision above, because the plugin importer has no device/simulator
switch — so they are repacked into one `.xcframework`, which is the format
Apple made for that question. `Docs/NATIVES.md` records every modification: a
vendored binary that differs from upstream has to say so.

**Not verified, and worth saying plainly:** only macOS has actually run. The
other binaries are checked for what can be checked without the platform —
presence, architecture, exported symbols, alignment, import settings — and no
further. Untested in particular: every non-macOS build; the Windows ARM64 rows
against 2021.3, where `CPU: ARM64` is a newer concept than the editor; and the
iOS xcframework's Xcode integration, which has the most moving parts. CI has
never run at all, because the repository has no remote yet. Reports welcome;
that is the point of shipping it.

## M8 — Rich text, sprites, emoji — **done**

Markup, named styles, the cluster mapping, cluster-aware reveal with
per-cluster events, the per-glyph vertex hook, colour glyphs (CBDT bitmaps,
COLRv0 layers), inline sprites, and VS15/VS16 — all in one RGBA array sampled
by the same shader and the same material, so a line of dialogue with emoji and
icons in it is still one draw call.

One thing is narrower than the prose above: **`<style=…>` on an inline run**
applies size, colour, spacing and weight but not font or axes. A run's font is
chosen during itemization from the label's own tables, so a style's font takes
effect as a label's *base* style and not mid-line; `<font=…>` covers the
mid-line case. The M10 localisation payoff wants the base-style path anyway.

Two things the roadmap said that turned out differently. The test face is
authored rather than vendored: `Tests/Fonts/ColorGlyphs.ttf` is 1.3 KB built
by the script beside it, holding a two-layer COLR glyph, one using the
text-colour sentinel, a CBDT PNG with a transparent border, a monochrome
control and a ligature. Noto Color Emoji would have worked at 10.7 MB of
someone else's font carried forever to exercise two code paths, and
`CffShapes.otf` had already set the better precedent. And COLRv1 is explicitly
out: it is a paint graph rather than a layer list, and it deserves a milestone
rather than a half-implementation that renders some fonts wrong.

The original plan follows.


Markup (`<b> <i> <color> <size> <font> <align> <mark> <nobr> <sprite>`) rides
on the run splitting the layout engine already does: a style range becomes
another reason to start a new run. `<b>` maps to a variable font's `wght`
axis when there is one, and to the fallback stack's bold face otherwise.
Malformed tags stay literal, as `<link>` does today.

Emoji is a first-class goal of this milestone, not a garnish, because it is
pure differentiation: TMP cannot render a ZWJ sequence, and the workaround is
a hand-built sprite sheet. The segmentation and shaping halves are already in
place — emoji ZWJ sequences are single grapheme clusters under UAX #29, and
HarfBuzz merges them into single glyphs when the emoji font provides the
ligatures. The work is the color pipeline:

- **Color glyph formats.** CBDT/CBLC and COLRv0 first (that covers Noto
  Color Emoji, which we can vendor for tests under OFL), sbix after. Apple's
  newer sbix payloads are JPEG-compressed in ways FreeType cannot decode —
  so the system-emoji story on iOS is explicitly deferred, documented, and
  answered by shipping a bundled emoji font in the fallback stack instead.
- **Rendering path.** A second RGBA `Texture2DArray` sampled by the same
  shader and material — not a submesh. A `Texture2DArray` has one format for
  all slices, so color cannot live in the R8 SDF array, and the SDF coverage
  math would binarize a color image. The path flag fits in the unused `w` of
  the existing `vmax` vertex channel: no new vertex data, no extra draw
  call. A submesh becomes necessary only if sprites ever need a different
  blend or stencil state, which is per-material and cannot be switched per
  fragment.
- **Variation selectors.** VS15/VS16 (text vs emoji presentation) decide
  which font in the stack gets the cluster; flags and skin tones are just
  ligatures once the right font is chosen.

Inline sprites (`<sprite>` from a sprite-sheet asset) share the RGBA array
and the same quad path — for the game icons in dialogue that are the other
half of what people use TMP sprite assets for.

**Named styles** are the style system's backbone, and the answer to TMP's
material-preset hell — presets welded to a specific atlas texture, fallback
glyphs ignoring your outline, runtime font swaps silently dropping the
preset. Ours can be pure data because nothing about rendering is welded to
an atlas. A style is an asset — font, size, variable axes, color, spacing,
and later decoration and animation defaults — that a label references as its
base and markup references by name (`<style=title>`). Labels store the
reference, not baked values, so editing a style updates every label that
uses it, in the editor and at runtime, through the same registry pattern the
atlas invalidation already uses. Inheritance is a single `extends` level
with explicit overrides — the full CSS cascade is a debugging trap and stays
out. Styles are created and added as assets (extension *is* authoring), and
a runtime mutation API makes theming — dark mode, colorblind palettes — a
one-line style swap. The quiet payoff arrives with M10: locale-keyed style
variants mean switching language swaps the font without losing the outline,
which is today a manual per-locale ritual that breaks in builds.

Two pieces of infrastructure land in this milestone because animation (M9)
needs them and only the engine can provide them. First, **cluster-aware
reveal**: the typewriter primitive is `maxVisibleGraphemes`, not
`maxVisibleCharacters`, because in shaped text "one character at a time" is
not a thing — an Arabic ligature is two characters in one glyph, a Hangul
syllable is three, and revealing half of either is nonsense. The engine owns
the logical-index ↔ grapheme-cluster ↔ glyph-quad mapping, so the engine
must expose it. Second, a **per-glyph vertex modifier hook** — a post-layout
pass that may translate, rotate, scale and tint each cluster's quads without
triggering re-layout, plus per-cluster reveal events. That mapping and that
hook are exactly what animation layers on TMP always had to reconstruct from
the outside, and could not once ligatures entered the picture.

## M9 — Tag-driven text animation — **done**

Effect tags ride the same markup pipeline as style tags, and they are kept out
of `TextStyle` deliberately: an effect changes no glyph and no advance, so
letting it split a run would re-shape the text for something only the vertex
pass reads.

Nine effects in the box — wave, shake, wobble, bounce, rainbow, pulse, and the
three appearance ones (fade, rise, swell) that key off reveal progress rather
than wall time. Every name is ours; the clean-room rule covers animation
vocabulary too. `BuiltInEffects.Register` adds a tenth from user code in one
call, and markup finds it — a name nothing recognises stays literal, like every
other unknown tag.

Effects are pure functions of (time, cluster, reveal). Not a stylistic
preference: a paused game must not twitch, two labels showing the same text
must animate identically, and rewinding the clock must rewind the animation —
none of which survives an effect that reaches for `Random` or accumulates
state.

`<wave><rainbow>` means both. Translations and rotations add, scales and tints
multiply, so composing effects is defined rather than last-one-wins.

The original plan follows.


The all-in-one bet, made explicit: juicy text ships in the box. In the TMP
world a dialogue box that types, shakes and fades is the engine plus a paid
animation asset stacked on top, each maintaining its own model of "which
character is where"; the commercial alternative sells editing as a separate
module. OneText's position is that rich text, emoji, animation and input
are one product — every feature, free, in one package.

This is a milestone rather than a product because M8 already built the hard
parts — the tag parser, run splitting, the cluster mapping, the vertex hook.
What lands here:

- **Effect tags** riding the same markup pipeline as style tags: wave,
  shake, bounce, fade, wobble, rainbow and friends, with parameters
  (`<wave amp=2 freq=1.5>`), plus appearance/disappearance effects that key
  off reveal progress rather than wall time. Tag names and API are our own —
  the clean-room naming rule applies to animation assets too.
- **Cluster-aware by construction.** Effects animate grapheme clusters, so a
  shaking Arabic word shakes as joined letters, a wave through Hangul moves
  syllables, and RTL reveal can choose logical or visual order. This is the
  part no TMP-based animator can do, and it demos in one line.
- **A Burst post-layout pass.** Animation writes vertex positions and colors
  in place every frame; it never re-runs shaping or layout and never
  allocates. Text that is merely animating must cost vertex-write time, not
  rebuild time — the benchmark harness gets a scenario for exactly this.
- **Typewriter, complete**: per-cluster reveal with easing, per-reveal
  events (the sound-effect/portrait hooks dialogue systems need), pause and
  speed tags, and reveal that composes correctly with ruby (annotations
  appear with their base, and are excluded from indices — same rule as
  M15).
- **Custom effects as a first-class API**: an effect is a small struct
  evaluated per cluster per frame (time, index, reveal progress in, transform
  and color out), registerable from user code and shareable as assets — the
  extension point for the community and for dialogue-system integrations
  (Naninovel, Pixel Crushers-style adapters).

## Font subsetting — **done**

A P/Invoke binding and a report, as predicted: `CharsetRecorder` already
collected the input and the bundled HarfBuzz already contained the subsetter.
Measured on the test faces — Noto Sans 2001 KB to 10 KB for fifteen
codepoints, Noto Sans Arabic 824 KB to 27 KB for thirteen.

The layout tables are the whole test. `SubsetTests` shapes a sentence against
the full face and against the subset and requires identical glyph counts,
advances and mark offsets — Latin ligatures, Arabic joining forms and Hangul —
because a subsetter that renumbers glyphs without rewriting GSUB and GPOS
produces text that is present and completely wrong to a reader, in the one
language nobody on the team reads. It also checks a variable font keeps its
axes, since flattening one would make `<b>` silently stop working.

Subsetting to nothing is refused rather than producing a font that draws
nothing, and the API is a static call with a report — the import-time asset
option that would put it in front of users is not built, which is the honest
place to leave it until someone has a project to try it on.

The original plan follows.


Compression got a 55 MB Korean face down to 12 MB in the build (brotli, 21.6 %
of the original). Subsetting is the bigger lever on the same number: a game
that draws 2,350 Hangul syllables and some Latin is shipping tens of thousands
of glyphs it will never rasterize, and those glyphs are the file.

**Most of this is already here.** `CharsetRecorder` collects what a play
session actually drew and `OneTextCharset` stores it — that is the input a
subsetter needs, and it was built for prewarming. And the subsetter itself
ships in the binary we already load: the macOS HarfBuzz we bundle exports 31
`hb_subset_*` symbols. This is a P/Invoke binding and an import-time asset
option, not a font-format project. `hb_subset_input_create_or_fail`, add the
codepoints to the unicode set, `hb_subset_or_fail`, write the returned blob
into the asset instead of the original bytes.

Two things to get right.

**Layout tables.** UniText shipped a fix for exactly this (2.0.7, "Font
Subsetter dropping OpenType layout tables"). GSUB/GPOS entries reference glyph
ids, so a subsetter that renumbers glyphs without rewriting them silently
breaks ligatures, marks and kerning — and Arabic stops joining, which is the
kind of failure that only shows up in the one language nobody on the team
reads. hb-subset does this correctly by default; the test is the check that
proves it: shape a sentence against the full face and against the subset, and
require the same glyphs at the same positions after id remapping. Latin
ligatures, Arabic joining forms and Hangul all go in that test.

**It cuts against what this engine is for.** A subset face cannot draw what
nobody predicted, and "the charset you cannot enumerate" is the workload we
win. So subsetting stays opt-in and off by default, the full face remains a
one-click revert, and a project that subsets should be told plainly what it
gave up. It is the right answer for a fixed-charset game and the wrong one for
a chat window — which is the same line the benchmarks already draw between us
and a prebaked atlas.

## M10 — Asian typography — **done**

The milestone that answers the loudest complaints, and the one where the
ground is firmest: every rule here is published (JLREQ, KLREQ, CLREQ, the
UAX #14 tailoring section), so the tests are about what the specs say rather
than about what looked right to whoever wrote the code. The degenerate cases
come from the bug trackers — the ！！！！ chat message that broke TMP's wrapping
is a regression test.

All six items are in: locale-aware rendering (BCP 47 through to HarfBuzz for
`locl`, and keying the fallback stack so Han unification stops being decided by
the order somebody listed fonts in), kinsoku with four severities, Korean word
wrap as a per-locale rule rather than a global toggle, punctuation compression,
the CJK–Latin quarter-em gap, dictionary line breaking, and the josa tag.

Three notes on where the lines were drawn.

**Dictionary data is separable.** `WordList` takes ICU's newline-separated
format unmodified, so their ~200 KB Thai dictionary drops straight in, and
`Coverage()` reports what fraction of a sample a list can actually segment —
because the honest failure of a dictionary breaker is not a crash, it is
wrapping that is subtly wrong in a language nobody on the team reads. What
ships built in is a starter list of about ninety common Thai words: enough that
Thai wraps better than not at all out of the box, and not a substitute for the
real thing. A project shipping Thai should load ICU's — and M11's
Dictionaries tab is now where that happens: one drop, with the coverage
number before and after beside it, rather than a paragraph in this file.

**The three that shipped late.** The milestone closed with three gaps, and
they are closed now. All three were the same shape — a rule implemented where
it was convenient rather than where it was decidable.

*Punctuation compression at a line edge.* The rule was applied at shaping
time, before lines exist, so only the adjacent-pair half of 約物詰め was in:
a 。 last on a line kept the blank right half nobody would see, and — worse —
the wrapper counted that half, so an invisible gap could push the next
character to the next line. The line-edge half needs the wrapper to know the
give *before* it picks the edge, so both edges are now costed per character
during measurement and subtracted from each candidate line; the same function
applies it to the glyphs, because a width measured one way and drawn another
is the bug this engine keeps not having. The two halves are a maximum, not a
sum: a mark has one blank half to surrender. Right-to-left runs are left
alone, where logically-first is visually-last and the specs are not speaking.

*A test face with a `locl` table.* `locl` was passed to HarfBuzz, but every
test font is a real-world face without one, so the suite could only show that
a language tag does no harm. `Tests/Fonts/LoclRegional.ttf` is authored for
it, like the CFF face before it: 直 has a Japanese and a Chinese form, neither
reachable through the cmap, so the only way a test can produce one is for the
tag to have driven the feature. 一 sits beside it as the control — Han, same
script, no rule — because the failure worth catching is a language applied to
the whole run instead of to the substitution the font asked for.

*Kinsoku on the emergency break.* The emergency path never asked, so a 。 one
character past the box start became a line start through the one route the
tailoring did not cover. It now walks the break back to a legal boundary,
which is 追い出し. The interesting part is the bound: TMP's famous ！！！！ bug is
an unbounded walk losing the line, and a fixed cap would be a magic number in
a milestone whose whole claim is that its rules are published ones. The bound
is push-out's own purpose — give up width only when the line you push onto can
end legally inside the box. A run of forbidden marks wider than the box buys
nothing by moving, so it does not move, and the tracker's case behaves exactly
as it did.

**The josa tag resolves at parse time**, which is the whole reason it is a tag
and not just a formatter. A formatter is a C# call, and a C# call cannot help a
string that arrived from a localisation table already assembled — which is
exactly where "{item}을" comes from. Either spelling of a pair is accepted,
numbers are read aloud (일 ends in ㄹ, 삼 in ㅁ), and the (으)로 exception after
ㄹ is handled: 서울로, not 서울으로.

The original plan follows.


The milestone that answers the loudest complaints from the Korean, Japanese
and Chinese communities — and the one where we pass both TMP and the
commercial alternatives, none of which do these correctly. Everything here is
spec-driven (JLREQ, KLREQ, CLREQ, UAX #14 tailorings), which is the ground we
are strongest on.

1. **Locale-aware rendering.** A BCP 47 language tag per label, defaulting
   from a project-wide setting: passed to HarfBuzz so `locl` features select
   the right regional forms, and used as a key into the font fallback stack
   so Han unification stops being a lottery — a Japanese label resolves 直
   from the Japanese font even when the Chinese font sits higher in the
   chain.
2. **Line-breaking tailorings.** Kinsoku severity as data (per-locale
   classes for characters forbidden at line start/end), Korean word wrap as
   the UAX #14 Korean tailoring (spaces break, syllables do not — set per
   locale, not by a global toggle), and the degenerate cases from the bug
   trackers (！！！！ in chat) as regression tests.
3. **Punctuation compression** (約物詰め/标点挤压). Full-width punctuation
   gives up half its width at line edges and when adjacent to other
   punctuation, instead of forcing an early wrap. Nothing in the Unity
   ecosystem has this; it is what makes CJK text look typeset instead of
   merely rendered.
4. **CJK–Latin spacing.** The automatic quarter-em gap between Han/Kana and
   Latin runs that every East Asian layout spec calls for.
5. **Dictionary line breaking for Thai, Lao, Khmer, Burmese.** These scripts
   shape correctly today but have no spaces between words, and UAX #14
   explicitly defers their class (SA) to dictionary analysis — which we do
   not have, so Thai currently wraps in the wrong places. A trie over the
   ICU dictionary data (permissively licensed) with longest-match plus the
   standard heuristics, behind the same LineBreaker interface. This closes
   the gap between "ships Thai shaping" and "ships Thai", which almost
   everyone else leaves quietly open.
6. **Korean particle selection (조사).** The postposition depends on
   whether the preceding syllable ends in a consonant, so an interpolated
   "{item}을" is wrong half the time, and Korean teams write custom
   formatters for it in every project. Ships as a formatting utility *and*
   a markup tag: `사과<josa=을>` resolves to 를 at parse time by reading
   the grapheme before it — which means it works on runtime-interpolated
   strings with no C# call, inside the same pipeline as every other tag.
   Either member of a pair is accepted and both resolve to the correct
   one — `<josa=을>` and `<josa=를>` are the same tag, likewise 은/는,
   이/가, 과/와, 아/야 — so nobody has to remember which spelling the
   engine wants, and it knows the ㄹ exception for (으)로. A day of work
   that tells Korean developers the engine ships in their language, not
   just their script.

## M11 — The Hub — **done**

One editor window — Fonts, Styles, Charsets, Dictionaries, Atlas, Gallery,
Doctor, Forensics (`Window > OneText > Hub`) — because TMP's
lesson is that features scattered across Project Settings, asset inspectors
and context menus effectively do not exist. Every tab pairs a view with the
one action that view makes obvious.

- **Charsets from anywhere.** The recorder already captures play sessions
  and scans prefabs and scenes; it gains a folder scan — localization
  tables (CSV, JSON, Unity Localization string tables), dialogue scripts,
  any text under a path — with an import hook so the charset follows the
  tables as they change. A game's real characters live in its string
  tables, not its scenes, and this one output feeds prewarm and subsetting
  alike.
- **Dictionaries, as an option you can see.** M10 left Thai correct in
  mechanism and thin in data: the trie, the segmenter and the ICU file
  format all work, but what ships built in is ~90 words. The Hub gains a
  **Dictionaries** tab that closes that with one drop: point it at ICU's
  `thaidict.txt` (~200 KB, permissively licensed — likewise Lao, Khmer and
  Burmese), and it imports the file as a package asset, registers it for
  the script at load, and reports `WordList.Coverage()` against a sample of
  the project's own strings before and after. The number is the point — a
  dictionary breaker fails by wrapping subtly wrong in a language nobody on
  the team reads, so "94% → 99.2%" is the only honest way to tell a Korean
  or Western team that their Thai build is fine. It stays an option, not a
  default: we are not vendoring 200 KB into every project that never ships
  Thai, and the licence notice belongs to the project that opts in. Doctor
  flags the other half — a locale whose strings need a dictionary that was
  never installed fails the check rather than shipping quietly wrong.
  Nothing at runtime changes; `DictionaryLineBreaker.SetWordList` already
  takes the file today, and this is the tab that makes people find it.
- **The atlas as a pie, with a button.** Live occupancy in play mode,
  split three ways: prewarmed, baked at runtime, free — plus the eviction
  heatmap that answers "what budget does my game need" from ten minutes of
  playing it. Next to the pie, the button it begs for: **promote** —
  append everything the session baked at runtime to a charset asset, so
  the next run prewarms what this one paid to discover. The recorder
  existed; the loop now closes in one click.
- **The string gallery.** Every string, laid out headlessly with its real
  style, font and locale, in one browsable grid — overflow and truncation
  flagged per locale. The layout engine runs without a scene, so this is
  a table you scroll, not a build you play through; localization QA's most
  expensive pass (screenshot every screen in every language) becomes a
  filter for red rows. Flipped around, the same view previews one string
  across every style and font — the way Korean font sites sell fonts with
  your own sentence — which is how styles get chosen in the first place.
- **Doctor.** Static renderability analysis over string tables × the font
  stack: strings no font can draw (tofu, predicted before runtime),
  Japanese strings that will resolve to Chinese glyph shapes through the
  fallback chain, locales missing line-breaking data. Headless, with an
  exit code — an unrenderable string fails the merge, not the release.
  Nothing in or around Unity does this.
- **Glyph forensics.** Click a rendered glyph: which font in the stack
  provided it, which cluster it belongs to, which shaping features
  applied, and why the line broke where it did — the rule, by name. Half
  of all text-rendering forum questions are "why does this glyph look
  wrong", and the current answer is to ask the forum.
- **In the build, too.** The classic text failure appears only in builds
  — wrong font resolution, missing natives, an atlas budget the device
  cannot afford — and the state of the art for debugging it is a
  screenshot of nothing. A development-build overlay ships the same
  diagnostics to the device: per-label font resolution, missing glyphs,
  live atlas pressure.

The Hub grows a tab per milestone rather than landing at once — the atlas
tab has no dependency beyond M6, while Doctor leans on M10's locale
awareness — but it is one window from the first tab, so discoverability
never regresses.

**What shipped, and the four decisions worth writing down.**

*Occupancy was the wrong number, so the atlas learned two new ones.* A pie
needs provenance, so tiles now record whether a prewarm baked them or a frame
discovered them, and `AtlasPrewarm` marks its own work. That gives the split.
But the question people actually bring to an atlas screen is "what budget does
my game need", and occupancy cannot answer it in the case that matters: an
atlas that is far too small never *reads* full, because it evicts and re-bakes
instead of filling — 30% forever, while the frame budget burns. So the atlas
also counts demand: every distinct tile it has ever baked, each counted once
however many times it was evicted and re-baked. Ten minutes of play then names
a budget. Demand tracking is on in the editor and development builds and off in
a release build, where nothing reads it and the bookkeeping would grow with the
session.

*Promotion reads the recorder, not the atlas.* "Append everything this session
baked at runtime to a charset" sounds like a walk over the atlas, and it cannot
be: tiles are keyed by cluster hash, and a cluster is not a character — a
ligature or a Hangul syllable is one tile from several code points, and there is
no way back from the hash to them. A charset needs characters. The recorder has
characters. So the button promotes what was recorded, and the pie is what
explains why you would want to.

*Doctor needed a field that did not exist.* The Han-unification check asks
whether a Japanese string will resolve through a Chinese face, and M10 built the
machinery for that — `FontStack` takes a language per family — but nothing in
the asset layer could set one, so every real project's chain was untagged and
the check would have had nothing to read. `OneFontAsset` now carries a
language, the label and the charset pass it when they build their stacks, and
Doctor reports both failures: a locale resolving through a face tagged for
another language, and the quieter one — two Han locales sharing a chain where
nobody tagged anything, which is not wrong for any one string and undefined for
all of them.

*"Why did it break there" had to come from the rule, not from a paraphrase.*
Forensics promises the line-break rule by name, and the only honest way to
report a rule is for the rule to report itself: `LineBreaker.Decide` now tags
every one of its ~50 returns with its UAX #14 number ("LB13", "LB30a") and
`RuleAt` re-runs the cascade to read the tag back. A second implementation that
explained the first would drift from it in exactly the cases anyone would look
one up for. The refactor touched every return in the rule cascade, which is why
it was worth doing against a suite that already ran all 19,338 `LineBreakTest`
cases — they still pass, and that is the whole argument for having built the
conformance harness in M3.

**What is thinner than the tab makes it look.** Shaping features are reported
per face, not per glyph — "this font registers `locl`, `liga`, `kern`", plus
whether *this* glyph differs from what the cmap maps its character to. HarfBuzz
does not report which feature substituted a given glyph, and inventing an answer
would be worse than the honest two-part one. The gallery renders its previews at
editor speed with a real offscreen canvas, capped per repaint, so a table of ten
thousand strings scrolls; measurement, which is what the red flags come from, is
headless and unbudgeted. And Doctor's font chain is the project chain: a label
that overrides its font is checked against what it would get by default, which
is the common case and not every case.

## M12 — Text editing that survives an IME — **done**

The input field exists (M5); this milestone makes it correct where every
Unity input field is famously wrong: composition. The Korean bug reports
against `TMP_InputField` become our test list — the last character must not
be lost or duplicated when focus shifts mid-composition, backspace during
composition must edit the composition rather than the committed text,
composition must render inline at the caret, and Chinese input must not
crash. Then the same discipline for Japanese conversion windows, mobile
soft-keyboard paths on Android and iOS, and read-only fields that actually
refuse composition.

Free and correct text editing is unserved: the commercial alternative sells
editing as a separate paid module, and the community RTL plugins never solved
caret logic at all. Bidi carets (we already hit-test in visual order) plus a
working IME make the input field the flagship of the free tier — which is to
say, of the whole project.

**What shipped, and the four decisions worth writing down.**

*A composition ends in two incompatible ways, so the field waits one frame.*
This is the whole milestone in one sentence. When an IME finishes, some
platforms deliver the composed text a second time as ordinary character
events and some deliver nothing at all — and which you get depends on the
platform, the IME, and whether the field still had focus. A field that assumes
the first loses the last syllable; a field that assumes the second doubles it;
both bugs are filed against `TMP_InputField` and both are real. `ImeCommitArbiter`
refuses to guess. When a composition clears on its own it holds the text for a
two-update grace window: if characters arrive, they were the commit and the
held copy is dropped; if none do, the field inserts it itself. When the field
commits on its own — focus is leaving and nobody else will — it runs the same
window in reverse, swallowing the platform's echo one matching character at a
time. The window is counted in updates rather than milliseconds, because a
wall-clock timeout would make correctness depend on the frame rate.

*The editing state left the MonoBehaviour, and that is why there are tests.*
These bugs survive a decade of reports because nobody can write a regression
test for them: reproducing one needs a Korean IME attached to the machine
running CI. So the state is `TextEditingModel` — committed text, caret,
selection, composition, no scene and no canvas — and the platform is
`IImeInput`, an interface with four methods. The Hangul assembly sequence, the
Japanese clause, the astral-plane candidate and the focus change are 24 EditMode
tests that run in the same second as everything else. The field draws what the
model reports.

*While an IME composes, the keyboard belongs to it.* Backspace shortens the
composition, the arrows walk the candidate list, Enter accepts a candidate —
and Unity delivers all of them to the field as well. Acting on them edits the
committed text behind the composition, or submits a form the user was only
confirming a syllable in. The field applies no key while composing except
Escape, which cancels. That single rule is two of the four bug reports.

*Three backends, one of them in an assembly that usually does not exist.*
`UnityEngine.Input` throws outright in a project that moved to the Input
System package, so the legacy backend is compiled behind
`ENABLE_LEGACY_INPUT_MANAGER` and the Input System backend lives in its own
assembly definition, constrained on the package being installed — an asmdef
that references a package which is not there does not fail to find it, it
fails to compile. It registers itself, so nothing in the UGUI assembly ever
names a type it cannot reference. On Android and iOS there is no composition
to observe at all: the OS owns the buffer, and the field mirrors it through
`SetExternalText`, which reports whether the value actually moved so the
change event fires once per change rather than once per poll.

**What is thinner than the feature list makes it look.** Unity reports no
conversion clause on either desktop backend — the machinery for the Japanese
highlight is there and tested, and the platform gives it one clause covering
the whole composition, so what a Japanese user sees today is the underline
without the block. The mobile path is written against `TouchScreenKeyboard`
and compiles for both platforms, but has not been run on a device; only macOS
composition has been exercised by hand. And bidi carets are M5's, not this
milestone's: composition inside an RTL run underlines correctly because it
reuses the selection-rect machinery, which was already visual-order aware.

## M13 — WebGL, and the demo that markets the project

HarfBuzz compiled to wasm with Emscripten, matched to the editor's toolchain
per Unity version, trimmed with the tiny-build options since we only use
hb-shape and hb-draw.

WebGL earns its own milestone because of what it unlocks: a playable browser
demo — Arabic and Hindi shaping correctly, a Korean input field composing
correctly, emoji sequences rendering as single glyphs, next to a TMP
comparison — is worth more than any README. WebGL text input needs the
hidden-HTML-input technique (Unity's WebGL IME support is absent; the
community plugin approach is well understood), which completes the M12 story
in the one place it is hardest. And the demo is where the animation module
earns its keep: a shaking, typing Arabic dialogue line next to TMP's version
is the whole pitch in one screenshot.

## M14 — MSDF, and text decorations

Multi-channel distance fields preserve sharp corners, which is the one place
our rendering is honestly behind. It changes the atlas format (RGB instead of
R8) and the shader's coverage math, so it lands after the color-emoji work
has already taught the atlas and shader to host a second format.

**Decorations land in the same milestone: outline, glow, underlay (shadow).**
They are the other half of what people actually use TMP material presets
for, and the half we do not have — the style system already covers the
first half as data. On a distance field each is cheap mathematics: an
outline is a second threshold pair, an underlay is one offset sample, a
glow is a distance falloff. The design constraint is not the math but the
delivery: parameters must ride per-vertex (or per-style through a small
number of shader variants), never per-material — a preset that forks the
material forks the batch, and "fonts, fallbacks and emoji never split a
draw call" is a promise this package does not trade away for a drop
shadow. That is why decorations wait for M14: the shader rework for MSDF
and the vertex-channel budget for decoration parameters are one redesign,
and doing them separately means doing one of them twice. Authoring surface
when it comes: fields on named styles (where "later decoration defaults"
has been parked since M8) plus tags for spans, with the same
defaults-visible treatment the effect table gives animation.

**Decorations landed first, and the delivery constraint held.** Outline,
shadow and glow ride in sixteen bytes of vertex channels the mesh was
already carrying: TEXCOORD1, TEXCOORD3 and TEXCOORD2.yz were the second
and third sweep-line samples, dead since joints moved inside the field
with cluster-union rasterization, and written as an unused-slot sentinel
ever since. So the cost is zero extra bytes per vertex, no extra canvas
shader channel — Normal and Tangent stay off, where turning them on would
bill every Image in the canvas seven floats for our drop shadow — and one
material for decorated and undecorated labels alike. `Tests/Editor/
DecorationTests.cs` holds that: same material, same vertex layout, same
canvas channels, plain label beside decorated one.

*What is smaller than the feature list makes it look.* Every decoration
is capped at one **reach** — `GlyphRasterizer.SpreadPixels`, four texels,
which is also the atlas padding. Past that the field is a flat "far
outside" and there is nothing left to threshold, so an outline stops
thickening and a shadow stops travelling. Four texels at the rasterized
ppem is a quarter of the em at 16 px and four per cent of it at 100 px:
decorations are proportionally bolder on small text than on large. Making
them bigger means a wider spread, which costs atlas area on every glyph
in every project whether it decorates or not — a trade nobody has asked
for yet. Colour glyphs are skipped rather than approximated: a picture has
no distance to threshold, and the colour atlas packs without the padding
ring an offset sample would need.

MSDF still slots in where it always did, and needs nothing from this: it
changes the atlas format and the coverage maths, and one more value of the
TEXCOORD2.w discriminator that already tells the colour atlas from the SDF
one.

## M15 — Ruby, then vertical text

**Ruby (furigana)** as a layout-engine feature, not a tag hack: an
annotation run attached to a base run, sized and centered by the engine,
kept unsplittable across line breaks, and excluded from reveal/caret
indices. The Japanese community currently fakes this with
`<voffset>`/`<size>` expansion and documents its breakage — wrap, auto-size,
typewriter indices — and all three failure modes are layout problems, which
is why ruby belongs in the layout engine.

**Vertical writing (縦書き)** afterwards, because ruby placement depends on
it (ruby sits to the right of a vertical column): `vert`/`vmtx` metrics,
upright CJK with rotated Latin runs, and the vertical forms of small kana
and punctuation. No Unity text solution — first-party or paid — has it; it
is a small market (Japanese visual novels and traditionally-styled games)
that is completely, permanently underserved.

## Beyond — the open squares

Kept visible because the core was built UI-framework-free for exactly these:

- **UI Toolkit frontend** — a thin layer over the same core; unclaimed by
  anyone.
- **World-space text** — lit, sorted, non-Canvas; the layout engine does not
  care.
- **ECS/DOTS** — shaped text for entities exists nowhere; the community
  package that tries is built on TextCore and cannot shape.
- **Accessibility** — exposing text to screen readers; no text asset even
  advertises it.

## Trust — the non-feature roadmap

The recurring fear in every community thread about third-party text is
single-maintainer risk: can I bet my project on this? Features do not answer
that; practice does. Standing commitments:

- **Benchmarks name their versions.** Any published figure states the exact
  version of TMP or UniText measured and the scenario, and the comparison
  harness stays reproducible. The interesting UniText rerun is against 2.x,
  not the 1.0 the open repository ships (its 2.0.0 moved to a shared
  `Texture2DArray` and Burst; 2.10.0 to compute shaders). The "3–21× faster
  than TMP" range, for the record, reproduces against TMP's dynamic atlas
  purely by choosing scenarios — we can generate the same headline, and
  decline to.
- **Every Unicode algorithm ships with its full UCD conformance run** in CI,
  on 2021.3 and Unity 6, and `Tests/Editor/PerformanceTests.cs` logs real
  numbers as `[perf]` lines on every run.
- **A TMP migration guide and OpenUPM listing** land with M7 (the first
  release outsiders can run), and the WebGL demo with M13.
- **The clean-room policy** (`CONTRIBUTING.md`) stays absolute: competitors
  are measured as built packages and read as public documentation, never as
  source.

## Benchmarking

The head-to-head against TextMeshPro and UniText lives in the dev project,
not in the package, so shipping never depends on either. Results and method
are in `Docs/BENCHMARKS.md`; both comparisons were made by measuring the
built package, never by reading its source, which `CONTRIBUTING.md` rules
out.
