# Contributing to OneText

Thanks for your interest! A few ground rules keep this project healthy.

## Clean-room policy

OneText is developed **from public specifications and permissively-licensed
open-source references only**:

- Unicode Standard Annexes (UAX #9, #14, #29) and the Unicode Character Database
- OpenType / TrueType specifications
- HarfBuzz documentation, API, and test suite
- FreeType documentation and API
- Open-source engines with compatible licenses (e.g. Chromium's text stack,
  msdfgen): for study, with attribution when code is derived

**Do not** reference the source code of proprietary or commercial text
solutions for Unity (including decompiled output, or their public repositories)
when contributing. If you have studied such code in depth, please say so in
your PR so we can review accordingly. Feature-level inspiration ("X can do
this, we should too") is fine; implementation-level reference is not.

## Finding your way around

`Documentation~/` holds one document per source folder — what the module does,
diagrams of its structure and behaviour, the invariants a change must keep, where
a new feature plugs in, and which tests cover it. Start at
[Documentation~/README.md](Documentation~/README.md). The folder mirrors the
source tree, so the doc for `Runtime/Core/Layout/` is
`Documentation~/Runtime/Core/Layout/README.md`. When you change a module, update
its doc in the same PR; diagrams are Mermaid sources with committed PNGs, and
`Documentation~/render-diagrams.sh --changed` re-renders what you edited.

## Testing

Unicode algorithm implementations must pass the official Unicode test files
(`BidiCharacterTest.txt`, `LineBreakTest.txt`, `GraphemeBreakTest.txt`,
`WordBreakTest.txt`, all vendored in `Tests/UnicodeData~/`).
Shaping-path changes should be validated against HarfBuzz's expected outputs.

Run the suite locally against a project that references the package:

```
Unity -batchmode -projectPath <dev-project> -runTests -testPlatform EditMode \
      -testResults results.xml
```

`Tests/Editor/PerformanceTests.cs` carries throughput budgets. They are loose
on purpose (they exist to catch an order-of-magnitude regression in CI, not to
benchmark a machine), and every one of them logs its real number, so `[perf]`
lines in the run output are the trend record. When a change moves those
numbers, say so in the PR.

CI (`.github/workflows/tests.yml`) runs the same suite on 2022.3.62f1 and Unity
6 (6000.0.77f1).
It needs `UNITY_LICENSE` (plus `UNITY_EMAIL` / `UNITY_PASSWORD`) as repository
secrets; a free personal license is enough. See the
[game-ci activation docs](https://game.ci/docs/github/activation).

## Code style

- C# with Unity conventions; `OneText` root namespace.
- Core (`Runtime/Core`) must not reference uGUI or any UI framework.
- No per-frame allocations in the text pipeline; pool everything.
