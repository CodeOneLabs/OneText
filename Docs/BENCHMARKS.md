# Benchmarks

Everything here is produced by the harness in `Editor/Benchmarks`, on this
machine, from a seeded corpus. To reproduce:

```
Unity -batchmode -quit -nographics -projectPath <dev project> \
      -executeMethod CompoundBench.RunAll -oneOut <dir>
```

`CompoundBench` lives in the dev project because it references TextMeshPro;
the scenarios themselves are in the package, so both systems run the identical
scene, strings and frame loop. `OneText.Benchmarks.BenchSuite.RunAll` is the
same thing without the TMP side, and `.AsianLayout` prices the M10 tailorings
on their own.

## What is measured

The window covers a text change plus `Canvas.ForceUpdateCanvases()`: layout,
shaping, glyph baking, atlas upload and mesh building. The median is the steady
state; **p99 and max are the numbers a player feels**, and they are where these
two systems differ most.

Both systems rasterize at the same density: OneText's largest bucket in these
scenarios is 32 px/em with 4 px of spread, and the TMP font assets are created
at 32 pt sampling with 4 px padding rather than TMP's default of 90/9. At 90 pt
TMP does several times the rasterization work per glyph and holds several times
the atlas, which would flatter these numbers rather than explain them.

**Burst is forced on and compiled before the run.** The editor's default is
compilation on, synchronous compilation off, so a job runs as managed IL until a
background thread finishes compiling it, and a benchmark started from the
command line exits before that happens. Every number this project published
before 2026-08-04 was measured that way, which read the SDF rasterizer as 12x
slower than it is (368k texels: 131 ms against 11 ms). `BenchScene.ForceBurst`
now forces it at every entry point and `BenchReport` discloses the mode, so a
report that was not measured this way says so in its header.

Draw groups are distinct material+texture pairs (what decides whether uGUI can
batch), counted structurally, because a hand-driven render does not tick the
engine's own batch statistics. Allocation is a managed heap delta, so it is a
floor. The editor's null graphics device means absolute microseconds are not a
device measurement; the ratios are symmetric across systems.

## Scenarios

- **C1 global UI**: a live-service HUD of 40 static and 20 changing labels,
  three faces, three sizes, and a language switch a third of the way in.
- **C2 chat stream**: 30 lines of mixed Korean/Japanese/English, one replaced
  every frame for 2000 frames. Glyph churn that no realistic charset covers.
- **C3 world-space labels**: 200 nameplates and damage numbers on a moving
  camera.

## Results

Unity 6000.0.77f1, Apple M4 Pro, null graphics device. Median of 3 repetitions
per cell, chosen by p99.

| Scenario | System | Median ms | p99 ms | Max ms | Draw groups | Texture | Coverage |
|---|---|---|---|---|---|---|---|
| C2 | OneText 4 MB | **0.216** | **0.480** | **0.599** | **1** | 4 MB | full |
| C2 | OneText 16 MB | 0.210 | 0.476 | 0.619 | 1 | 16 MB | full |
| C2 | TMP dynamic 1024² | 0.658 | 10.003 | 12.325 | 6 | 9 MB → grows | full |
| C2 | TMP dynamic 1024² +prewarm | 0.569 | 10.102 | 10.975 | 6 | 11 MB → grows | full |
| C2 | TMP dynamic 2048² +prewarm | 3.548 | 15.670 | 24.554 | 2 | 20 MB → grows | full |
| C2 | TMP static 1024² | 0.091 | 0.126 | 0.182 | 2 | 3 MB | **60 %** |
| C1 | OneText 4 MB | **0.446** | **1.385** | **6.875** | **1** | 4 MB | full |
| C1 | TMP dynamic 1024² | 0.454 | 14.438 | **351.800** | 5 | 4 MB → grows | full |
| C1 | TMP static 1024² | 0.506 | 0.752 | 2.651 | 8 | 3 MB | full |
| C3 | OneText 4 MB | 0.630 | 0.814 | 0.933 | **1** | 4 MB | full |
| C3 | TMP dynamic 1024² | 0.632 | 0.759 | 0.850 | 3 | 2 MB → grows | full |
| C3 | TMP static 1024² | 0.621 | 0.677 | 0.733 | 5 | 3 MB | full |

C3 is a tie and was not always one. It lost by 16 % until three changes landed
on 2026-08-04: the label colour stopped being multiplied into every quad when it
is opaque white, `ShapeRun` began reusing the glyphs `BuildItems` already
shaped when a line did not cut the item, and a fast path skips break analysis,
grapheme segmentation and bidi for printable-ASCII `NoWrap` text with no style
spans that one font covers, where all four have a forced answer. Anything else
takes the general path unchanged.

Coverage is the share of the last frame's characters the font asset can
actually draw, asked of the asset directly; TMP substitutes a replacement for
a character it cannot find, so a generated mesh looks complete either way. The
78 % ceiling the dynamic runs hit in C2 is the system CJK font's own coverage
of the mixed corpus and applies to both systems equally; the static column is
scaled against that same ceiling.

## The workload matrix

C1, C2 and C3 each move two variables at once (C2 has few long labels *and* a
stream of unseen glyphs, C3 has many short labels *and* a warm atlas), so none
of them can say which of the two a change acted on. This varies rebuild count
and glyph novelty independently, at a fixed string length, 200 labels, 600
frames. Both novelty settings prewarm the same vocabulary, so the only
difference is whether the text stays inside it.

```
Unity -batchmode -quit -nographics -projectPath <dev project> \
      -executeMethod CompoundBench.WorkloadMatrix -oneOut <dir>
```

| Rebuilds/frame | Glyphs | System | Median ms | p99 ms | Max ms | Draw groups |
|---|---|---|---|---|---|---|
| 5 | warm | **OneText** | **0.146** | **0.235** | **0.306** | 1 |
| 5 | warm | TMP dynamic | 0.174 | 0.308 | 0.496 | 1 |
| 50 | warm | **OneText** | **1.320** | **1.464** | **1.547** | 1 |
| 50 | warm | TMP dynamic | 1.520 | 1.721 | 1.922 | 1 |
| 5 | new | **OneText** | **1.032** | **1.598** | **4.036** | **1** |
| 5 | new | TMP dynamic | 12.380 | 36.146 | 43.836 | 8 |
| 50 | new | OneText | 11.419 | **15.142** | **16.736** | **1** |
| 50 | new | TMP dynamic | **4.384** | 191.701 | 254.569 | 18 |

The two warm rows are layout-bound and close. The novel rows are where the
architectures diverge: at five rebuilds a frame OneText is 12x faster at the
median and 23x at p99.

**The last row needs reading with its footnotes.** TMP's median there is 62 %
lower than OneText's, and it gets there by opening 32 atlas pages, spending
20 MB against a fixed 4 MB, and still leaving one character in five undrawn
(1,437 of 1,828 on the final frame). The frames where it grows the atlas cost
191 ms at p99 and 254 ms at worst. OneText holds 4 MB and one draw call,
evicting 79,589 tiles over the run to do it, and its worst frame is 16.7 ms.
A median is not a comparison when the two runs drew different amounts of text.

## UniText

[UniText](https://github.com/LightSideKittens/UniText) **1.0.0** (MIT) is the other
HarfBuzz-based text engine for Unity, and the one whose performance claims
prompted this harness. It was measured the same way, through the same
`ITextSubject` interface, at the same 32 pt sampling and 4 px spread. The
adapter is kept at `Docs/benchmarks/UniTextSubject.cs.txt`; the full three-way
report is `Docs/benchmarks/three-way-report.md`.

**The version matters and has to be quoted with any of these numbers.** 1.0.0
is what the open repository ships; the product's changelog is at 2.12. Its
2.0.0 added a shared `Texture2DArray` glyph atlas and Burst `IJobParallelFor`
SDF/MSDF generation, and 2.10.0 moved rasterization to compute shaders. The
structural difference these numbers rest on (1.0 grows a list of separate
atlas textures, and a label ends up with several canvas renderers) is
therefore a property of the version measured, not of the engine today.

Its implementation was deliberately not read: only the package's public
signatures and its Getting Started guide. Two things a black-box adapter has to
know: a font asset created through `CreateFontAsset` needs `LoadFontFace` and a
first glyph before it has an atlas, and the first assignment to `Appearance` on
a component built entirely from script throws (the second one takes; the guide
notes that Project Settings defaults apply only when the component is added
through the Inspector).

> **These UniText numbers predate the Burst fix described under "What is
> measured" and have not been re-run.** OneText's column was measured with its
> SDF job running as managed IL; UniText 1.0.0 uses no jobs, so its column is
> unaffected. The comparison therefore understates OneText and must not be
> quoted until the three-way run is repeated.

| Scenario | System | Median ms | p99 ms | Max ms | Draw groups | Alloc/frame | Texture |
|---|---|---|---|---|---|---|---|
| C2 | OneText 4 MB | **0.206** | **0.476** | 0.63 | 1 | **1.2 KB** | 4 MB fixed |
| C2 | UniText 1024² | 0.256 | 0.684 | 1.15 | 1 | 1.3 KB | 9 MB → grows |
| C2 | TMP dynamic 1024² | 0.587 | 10.101 | 11.09 | 6 | 40.0 KB | 11 MB → grows |
| C1 | OneText 4 MB | **0.381** | 1.332 | 6.42 | 1 | **1.1 KB** | 4 MB fixed |
| C1 | UniText 1024² | 0.506 | 1.237 | **3.05** | 1 | 1.7 KB | 4 MB → grows |
| C1 | TMP dynamic 1024² | 0.455 | 14.892 | 347.31 | 5 | 18.0 KB | 4 MB → grows |
| C3 | OneText 4 MB | **0.602** | **0.725** | 0.90 | 1 | **0.9 KB** | 4 MB fixed |
| C3 | UniText 1024² | 0.997 | 1.080 | 1.11 | 1 | 1.6 KB | 2 MB → grows |
| C3 | TMP dynamic 1024² | 0.627 | 0.729 | 0.79 | 3 | 1.6 KB | 3 MB → grows |

All three draw the same characters: 78 % in C2 is the system CJK font's own
coverage of the mixed corpus, identical for every system.

The OneText rows are a later run on the same machine, harness and scenarios:
this comparison is what prompted the allocation work below, and the numbers
that provoked it (10.6 / 20.2 / 38.1 KB a frame) would be misleading to leave
standing as current.

This is a close race, and the "3-21×" figure is not mysterious once the
comparison is stated: **we reproduce that whole range ourselves against TMP
dynamic**, from 2.6× at the C2 median to 21× at C2 p99, purely by choosing
which TMP configuration to stand next to. It is a fact about TMP's dynamic
atlas, not a fact about any particular engine.

Against UniText specifically: OneText is ahead at the median in all three
scenarios (1.2×, 1.3×, 1.7×) and at p99 in two of three. UniText is still ahead
on the worst single frame everywhere; its glyph baking is native and threaded,
so it has no equivalent of our 6.4 ms language-switch frame in C1, and that is
the next thing to fix. Allocation was our clearest loss in the first version of
this table at 10 to 38 KB a frame; it is now 0.9 to 1.2 KB, slightly under
UniText's 1.3 to 1.7 KB. Both hold one material+texture pair. UniText's atlas grows with the
charset (9 MB in C2 and climbing); OneText's stays at the budget it was
given.

## What the Asian typography rules cost

The compound scenarios leave every M10 setting off, which is how most projects
run and therefore the right default, but it means nothing above prices the
rules themselves. `BenchSuite.AsianLayout` does: layout only, no atlas and no
mesh, over 4,000 characters of Japanese wrapped into a 480 px box, because
every one of these rules lives in layout and a compound scenario would bury a
per-character cost under an atlas upload.

| Rules on | chars/ms | µs per 1,000 chars | vs. off |
|---|---|---|---|
| none | 1,357 | 737 | baseline |
| kinsoku (normal) | 1,193 | 838 | +14 % |
| + punctuation compression | 1,045 | 957 | +30 % |
| + CJK-Latin spacing | 1,021 | 979 | +33 % |

Two things worth saying plainly. The first row is the one that matters to a
project that never ships Japanese: costing line edges is behind a flag set once
per layout, so text laid out with compression off does not touch the code at
all: 1,351 chars/ms before the line-edge rule existed, 1,357 after, which is
noise.

The second is that adding the line-edge half of 約物詰め made compression
*faster*, not slower: 959 chars/ms before, 1,045 now, for a rule that does
strictly more work. The naive version of the new code was 841 (a 12 %
regression) because it asked two binary searches per glyph whether a character
was a full-width mark, on text where nineteen characters in twenty are kana and
Han. A three-comparison range test in front of the lists paid for the new rule
and part of the old one. The prefilter is a hand-written duplicate of data that
lives elsewhere, so a test walks all 65,536 characters asserting it never
rejects a mark the lists accept.

## What the numbers say

**Where OneText wins.** A charset nobody can enumerate ahead of time. In C2,
TMP's dynamic atlas costs a 10 to 16 ms hitch at p99 against 0.5 ms, and its
texture memory grows to between 9 and 20 MB and keeps going, while OneText
holds the budget it was configured with. In C1 the language switch produces a
351.8 ms frame in TMP and a 6.9 ms one here: **51x**, and twenty-one frames a
player watches disappear. The workload matrix isolates why: with unseen glyphs arriving at
five labels a frame, OneText is 12x faster at the median and 23x at p99, in one
draw call against eight. Everything draws in one material+texture pair however
many faces and sizes are on screen.

**Where TMP wins.** A charset known ahead of time and fully baked. TMP static is
2.4x faster than OneText at the median in C2, while drawing 60 % of the text.
At fifty rebuilds a frame of entirely new glyphs its dynamic atlas posts a lower
median than OneText by giving up: 32 atlas pages, 20 MB, and 21 % of characters
undrawn. C3 (short ASCII over a warm atlas, where there is no shaping to do and
no glyph to bake) is a tie, and it is the shape where doing the full Unicode
pipeline cannot pay for itself. If a project's text is a fixed Latin charset
known at build time, prebaked TMP is a good answer and this table says so.

The claim is therefore not "faster than TMP". It is: **at the same rasterization
density and with the same text actually drawn, OneText's worst frames are
3 to 51x cheaper in dynamic-charset workloads, in a fixed memory budget that does
not grow and one draw call that does not split.**
