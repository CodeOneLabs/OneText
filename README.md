# OneText

**Free, open-source text engine for Unity. Every language, shaped correctly.**

OneText brings real OpenType text shaping to Unity — the same approach used by
Chrome, Firefox, Android, and Adobe InDesign (HarfBuzz + FreeType). Arabic
ligatures, Devanagari conjuncts, Thai clustering, bidirectional text: rendered
correctly, out of the box, for free.

> Status: **v0.1.0 — the first public release.** Shaping, SDF rendering, the
> Unicode algorithms, layout, the uGUI components, a per-tile atlas, natives
> for every platform but WebGL, rich text with named styles, colour emoji and
> inline sprites, tag-driven animation, font subsetting, Asian typography
> (kinsoku, punctuation compression, Thai dictionary breaking, the josa tag),
> text decorations (outline, shadow, glow — without splitting a single draw
> call), the Hub — one window for fonts, charsets, dictionaries, the atlas, a
> string gallery and Doctor, the renderability lint that CI can fail a merge
> on — and an input field that composes: Korean, Japanese and Chinese input
> methods draw inline at the caret and keep the last syllable when focus
> moves. Next up: WebGL and a browser demo, then MSDF. Star/watch to follow
> along.

## Why

Unity's built-in text solutions cannot correctly shape complex scripts. If your
game ships in Arabic, Hindi, Thai, or any script that needs contextual glyph
substitution, you have been living with workarounds. OneText's goal is simple:
**correct text rendering should not be a paid feature.**

## Design pillars

1. **Correctness first** — HarfBuzz for OpenType shaping (GSUB/GPOS), Unicode
   algorithms (UAX #9 BiDi, UAX #14 line breaking, UAX #29 segmentation)
   validated against the official Unicode test suites.
2. **Drop-in uGUI integration** — a `MaskableGraphic`-based component that
   behaves the way you expect: works with `RectTransform`, layout groups,
   `ContentSizeFitter`, masks, and raycasting. Migrating a `Text` or TMP label
   should feel boring.
3. **Modern rendering** — curve-based SDF/MSDF glyph rasterization (Burst),
   one shared texture-array atlas: per-tile LRU eviction, defragmentation, a
   budget you set, and uploads that carry only the tiles that changed.
4. **Engine/UI separation** — the core (shaping, layout, atlas) has no uGUI
   dependency, so additional frontends (UI Toolkit, world-space text) are
   thin layers.
5. **All-in-one** — rich text, emoji, text animation, input fields: in the
   box, not spread across paid add-ons that each keep their own broken model
   of where a character is.
6. **Free forever** — MIT licensed. All features. No Pro tier, no paid
   modules.

## Roadmap

| Milestone | Scope |
|---|---|
| M0 | Package scaffolding, repo, CI — **done** |
| M1 | HarfBuzz + FreeType bindings; first shaped glyphs on screen — **done** |
| M2 | SDF atlas pipeline (Burst rasterizer, Texture2DArray, LRU) — **done** |
| M3 | Unicode algorithms: BiDi, line breaking, segmentation (UAX #9/#14/#29) — **done**, validated against the full UCD conformance suites |
| M4 | Layout, alignment, font stacks & fallback, variable fonts — **done** |
| M5 | uGUI component polish: input field, links/click events, inspector UX — **done** |
| M6 | Atlas at scale: per-tile eviction, defragmentation, prewarm charsets, configurable budget, partial upload — **done** |
| M7 | Platform natives: Windows/Linux/Android/iOS — **done** (WebGL comes later, with the demo) |
| M8 | **done** — Rich text markup; named styles (edit one asset, every label follows); inline sprites & emoji (ZWJ sequences, flags, skin tones); cluster-aware reveal + vertex-hook API |
| M9 | **done** — Tag-driven text animation: typewriter (reveal by grapheme / orthographic cluster / syllable, speed, per-punctuation pauses incl. CJK & Thai, `<wait=0.5>`, skip, per-unit callback), per-cluster effects (wave/shake/fade…), custom-effect API — in the box, not a paid add-on |
| M10 | **done** — Asian typography: locale-aware fallback & `locl`, kinsoku/금칙 tailorings, punctuation compression, CJK–Latin spacing, dictionary line breaking (Thai/Lao/Khmer/Burmese), Korean josa tag |
| M11 | **done** — The Hub: one tooling window (Window > OneText > Hub) — charset folder scan with an import hook, ICU dictionary import with before/after coverage, atlas occupancy pie + demand-based budget advice + one-click prewarm promotion, string gallery with per-locale overflow flags, Doctor (CI renderability lint, exits 1), glyph forensics naming the UAX #14 rule, in-build diagnostic overlay |
| M12 | **done** — Text editing that survives an IME: inline composition with underline and Japanese clause highlight, no syllable lost or doubled when focus moves, composition keys left to the IME, read-only refuses composition, Input Manager + Input System + mobile soft-keyboard backends |
| M13 | WebGL (HarfBuzz to wasm) + playable browser demo |
| M14 | Decorations — `<outline> <shadow> <glow>`, on named styles or as spans, carried in vertex channels the mesh already had, so a decorated label still batches with a plain one — **done**; MSDF rendering (sharp junction corners) still to come |
| M15 | Ruby (furigana), then vertical writing |

See [Docs/ROADMAP.md](Docs/ROADMAP.md) for the reasoning and the measurements
behind each of these.

## Installing

In Unity, open **Window > Package Manager**, press **+**, choose
**Add package from git URL…**, and paste:

```
https://github.com/CodeOneLabs/OneText.git
```

That is the whole install — the HarfBuzz/FreeType natives ship inside the
package. Then use **GameObject > UI > OneText Label** (or add the
`OneTextLabel` component) and assign a font. Shipping Thai, Lao,
Khmer or Burmese? Import the **Word-break dictionaries** sample from the
package's Samples tab so those scripts wrap on real word boundaries.

## Requirements

- Unity 2021.3 LTS or newer
- Windows, macOS, Linux, Android or iOS. HarfBuzz binaries ship with the
  package — see [Docs/NATIVES.md](Docs/NATIVES.md) for where they come from
  and what was verified. WebGL is not supported yet (M13). Only macOS has been
  run end to end so far; if another platform misbehaves, that is a bug worth
  filing.

## License

[MIT](LICENSE). Native dependencies (HarfBuzz, FreeType) are permissively
licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — including the project's clean-room
policy (we build from public specifications and open-source references only).
