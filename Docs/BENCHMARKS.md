# Benchmarks

Everything here is produced by the harness in `Editor/Dev/Benchmarks`, on this
machine, from a seeded corpus. To reproduce:

```
Unity -batchmode -quit -nographics -projectPath <dev project> \
      -executeMethod CompoundBench.RunAll -oneOut <dir>
```

`CompoundBench` lives in the dev project because it references TextMeshPro;
the scenarios themselves are in the package, so both systems run the identical
scene, strings and frame loop. `OneText.Benchmarks.BenchSuite.RunAll` is the
same thing without the TMP side, `.WorkloadMatrix` varies rebuild count and
glyph novelty independently, and `.AsianLayout` prices the M10 tailorings on
their own. `AllocDiff.Run`, also in the dev project, is where the allocation
figures come from.

**Everything below was re-measured at v0.3.2 on 2026-08-20.** The raw reports
that run produced are kept beside this file:
[v0.3.2-compound-report.md](benchmarks/v0.3.2-compound-report.md) and
[v0.3.2-alloc-diff.md](benchmarks/v0.3.2-alloc-diff.md). The tables it
replaces were v0.1.0's, and three of them had gone stale in ways worth stating
rather than quietly overwriting: what got faster, what got slower, and one
headline multiple that shrank. Those are collected under "What moved since
v0.1.0".

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
engine's own batch statistics. The editor's null graphics device means absolute
microseconds are not a device measurement; the ratios are symmetric across
systems.

**Coverage is in every table, because a frame time is only comparable to
another frame time when both frames drew the same text.** A system that has no
glyph for a fifth of the characters posts a better number for doing less work,
and TMP draws a replacement glyph for what it cannot find, so the rendered
frame looks complete either way. Where a row says "no system fonts", that is
OneText run with its last-resort tier switched off so that it draws the same
set TMP draws: **that row, not the arithmetic in the us/char column, is the
like-for-like comparison.**

### The allocation instrument

Allocation is a managed heap delta over a whole frame, with frames that
collected excluded rather than clamped to zero — clamping makes a frame that
allocated look like one that did not.

The gauge is coarse and the harness now states its own resolution rather than
implying precision it does not have. Measured against known allocations it read
20.8 KB of real work as 28-32 KB, 5.2 KB as 4-8 KB, and 1 KB as 0 more often
than not, while never reporting bytes for a frame that allocated none. **A
per-frame figure below a page means nothing; the mean over hundreds of frames
is the number to read, and it is an over-estimate.** Nothing here can prove
zero from a single call, which is why every allocation figure below comes from
50 to 200 labels over 600 frames.

**The profiler's `GC.Alloc` recorder — the instrument that could prove zero —
does not work in this environment, and the tables do not use it.** It counts
nothing under `-executeMethod` and nothing under the test runner either,
because neither submits the player-loop frames it records against. This is not
inferred: the harness carries a control case that allocates one `byte[64]` per
call and must therefore read 1.000 allocations per call. It reads 0.000, with
and without `-nographics`. Any table that shows a zero in that column, here or
in a report file, is showing a broken instrument.

## Scenarios

- **C1 global UI**: a live-service HUD of 40 static and 20 changing labels,
  three faces, three sizes, and a language switch a third of the way in.
- **C2 chat stream**: 30 lines of mixed Korean/Japanese/English, one replaced
  every frame for 2000 frames. Glyph churn that no realistic charset covers.
- **C3 world-space labels**: 200 nameplates and damage numbers on a moving
  camera.

## Results

Unity 6000.0.77f1, Apple M4 Pro, null graphics device, v0.3.2. Median of 3
repetitions per cell, chosen by p99.

| Scenario | System | Median ms | p99 ms | Max ms | Draw groups | Alloc/frame | Texture | Coverage |
|---|---|---|---|---|---|---|---|---|
| C2 | OneText 4 MB | 0.501 | **1.290** | **1.611** | **1** | **3.4 KB** | 4 MB | **100 %** |
| C2 | OneText 4 MB, no system fonts | **0.246** | 0.531 | 0.692 | 1 | 2.2 KB | 4 MB | 78 % |
| C2 | OneText 16 MB | 0.424 | 1.186 | 1.643 | 1 | 3.9 KB | 16 MB | 100 % |
| C2 | TMP dynamic 1024² | 0.637 | 10.315 | 12.467 | 6 | 43.1 KB | 9 MB → grows | 78 % |
| C2 | TMP dynamic 1024² +prewarm | 0.573 | 10.260 | 11.067 | 6 | 41.1 KB | 11 MB → grows | 78 % |
| C2 | TMP dynamic 2048² +prewarm | 3.496 | 15.551 | 23.510 | 2 | 126.0 KB | 20 MB → grows | 78 % |
| C2 | TMP static 1024² | **0.097** | **0.164** | **0.245** | 2 | 7.0 KB | 3 MB | **60 %** |
| C1 | OneText 4 MB | 0.568 | 3.184 | 14.682 | **1** | **2.9 KB** | 4 MB | 100 % |
| C1 | OneText 4 MB, no system fonts | 0.576 | **1.625** | **6.786** | 1 | 1.5 KB | 4 MB | 100 % |
| C1 | TMP dynamic 1024² | **0.471** | 14.357 | 340.889 | 5 | 25.8 KB | 4 MB → grows | 100 % |
| C1 | TMP static 1024² | 0.544 | 0.917 | 2.559 | 8 | 12.6 KB | 3 MB | 100 % |
| C3 | OneText 4 MB | 0.846 | 1.126 | 1.159 | **1** | **894 B** | 4 MB | 100 % |
| C3 | OneText 4 MB, no system fonts | 0.845 | 1.122 | 1.265 | 1 | 894 B | 4 MB | 100 % |
| C3 | TMP dynamic 1024² | **0.663** | **0.726** | **0.756** | 3 | 1.6 KB | 2 MB → grows | 100 % |
| C3 | TMP static 1024² | 0.678 | 0.909 | 0.951 | 5 | 1.6 KB | 3 MB | 100 % |

**C2 is the scenario this engine exists for, and it now draws more than TMP
does rather than the same amount.** OneText draws all 659 characters of the
last frame; TMP draws 517 of them and substitutes a replacement for the rest.
Doing that, it is still ahead at the median (0.501 against 0.637) and ahead at
p99 by 8x. Switch the system-font tier off so both draw the same 78 % and the
median is 0.246 — 2.6x — with a p99 of 0.531 against 10.3.

**C3 is a loss and used to be a tie.** 0.846 ms against TMP dynamic's 0.663 and
TMP static's 0.678. The parity row is the same, so the system-font tier is not
the cause: short ASCII over a warm atlas is layout and nothing else, and
OneText's layout has got slower since v0.1.0. See "What moved since v0.1.0".

**In C1 the system-font tier costs a worst frame and buys nothing.** Both C1
rows draw 357 of 357 characters — the project's own fonts already cover that
text — but the tier still probes 3,089 files, and the language-switch frame
goes from 6.8 ms to 14.7 ms for it. That is the clearest actionable finding in
this table: the tier should not be paying to look for glyphs the stack can
already draw.

TMP static's C2 row is the fair version of "TMP is 2.5x faster than you": it
draws 60 % of the text. It is a good answer for a charset fixed at build time
and this table says so.

## What allocation costs

`AllocDiff.Run` changes one thing at a time on a 200-label scene over 600
frames, so the difference between two rows names its cause. The instrument and
its resolution are described above.

| Case | OneText | TMP |
|---|---|---|
| idle: nothing changes | 0.00 KB | 0.00 KB |
| 50 labels retexted, strings pre-built | **0.00 KB** | 0.00 KB |
| 200 labels retexted, strings pre-built | **0.00 KB** | 0.00 KB |
| 50 labels retexted, rich-text markup | **0.00 KB** | 0.00 KB |
| 50 labels retexted, `ToString()` each (= C3) | 1.53 KB | 1.57 KB |
| a number every frame, no string anywhere | **0.00 KB** | 2.62 KB |

The last row is the one v0.3.2 was written for. `SetText(int)` writes the digits
into a buffer the label owns; TMP's own non-allocating answer to the same case,
`SetText("{0}", n)`, reads 2.62 KB a frame on this gauge. The row above it is
the same fifty labels with `int.ToString()` left in, and the 1.53 KB there is
the caller's fifty strings, not either engine — which is why the two systems
agree to within 40 bytes on it.

Markup allocating nothing is new as well: the rich-text parser writes into the
label's own buffer and interns tag names, and the layout engine indexes style
spans rather than enumerating an `IReadOnlyList<T>`, which was boxing an
enumerator once per layout.

Times from the same run, for the frame these rows measure: 50 labels retexted
costs OneText 793 µs against TMP's 568, and with markup 1,605 against 1,011.
Allocation is where this release moved; that gap is not.

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

| Rebuilds/frame | Glyphs | System | Median ms | p99 ms | Max ms | Draw groups | Coverage |
|---|---|---|---|---|---|---|---|
| 5 | warm | OneText | 0.226 | **0.308** | **0.431** | 1 | **100 %** |
| 5 | warm | TMP dynamic | **0.208** | 0.340 | 0.495 | 1 | 79 % |
| 50 | warm | OneText | 1.946 | 2.256 | 2.494 | 1 | **100 %** |
| 50 | warm | TMP dynamic | **1.639** | **1.879** | **2.034** | 1 | 78 % |
| 5 | new | **OneText** | **2.506** | **3.896** | **36.014** | **1** | **100 %** |
| 5 | new | TMP dynamic | 12.457 | 36.585 | 44.173 | 8 | 78 % |
| 50 | new | OneText | 26.206 | **31.838** | **55.610** | **1** | **100 %** |
| 50 | new | TMP dynamic | **4.421** | 189.850 | 225.708 | 18 | 79 % |

The warm rows are layout-bound and TMP is now slightly ahead in both, while
drawing about a fifth less. The novel rows are where the architectures diverge:
at five rebuilds a frame OneText is 5x faster at the median and 9.4x at p99, in
one draw call against eight and 4 MB against 14.

**The last row needs reading with its footnotes.** TMP's median there is lower
than OneText's, and it gets there by opening 32 atlas pages, spending 20 MB
against a fixed 4 MB, and still leaving one character in five undrawn. The
frames where it grows the atlas cost 190 ms at p99 and 226 ms at worst. OneText
holds 4 MB and one draw call, evicting 137,377 tiles over the run to do it, and
its worst frame is 55.6 ms. A median is not a comparison when the two runs drew
different amounts of text.

### The same matrix with the system-font tier off

The novel cells look far worse than v0.1.0's until the tier is switched off,
and then they do not, which is the whole finding:

| Cell | v0.1.0 | v0.3.2, tier off | v0.3.2, tier on |
|---|---|---|---|
| 5 rebuilds, warm | 0.146 | 0.213 | 0.238 |
| 50 rebuilds, warm | 1.320 | 1.689 | 1.945 |
| 5 rebuilds, new glyphs | 1.032 | **1.127** | 2.521 |
| 50 rebuilds, new glyphs | 11.419 | **11.498** | 26.633 |

The two novel cells return to their old numbers within noise. What the extra
time buys is in the coverage column of the same run: 1,851 of 1,851 characters
with the tier on, 1,448 of 1,851 without it. **Baking a glyph nobody predicted
costs what it always cost; the tier's cost is finding the file it lives in.**

The warm cells do not come back, and no tier explains them. They are the same
regression C3 shows.

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
> measured", predate v0.3.2, and have not been re-run.** Every OneText figure
> in this section is v0.1.0's; the tables above supersede them. OneText's column was measured with its
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
this table at 10 to 38 KB a frame; it was 0.9 to 1.2 KB when this comparison
was run, against UniText's 1.3 to 1.7 KB, and at v0.3.2 the rebuild path
allocates nothing measurable — see "What allocation costs". Both hold one material+texture pair. UniText's atlas grows with the
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
| none | 755 | 1,324 | baseline |
| kinsoku (normal) | 718 | 1,392 | +5 % |
| + punctuation compression | 639 | 1,566 | +18 % |
| + CJK-Latin spacing | 655 | 1,528 | +15 % |

The rules are cheaper relative to the baseline than they were at v0.1.0
(+33 % then, +15 % now) and that is not good news: the baseline is what got
slower. In absolute terms every row here costs more µs per 1,000 characters
than the same row did at v0.1.0.

What has not changed is that a project which never ships Japanese does not pay
for them. Costing line edges is behind a flag set once per layout, and the last
two rows differ by less than the run-to-run noise on this bench.

## What moved since v0.1.0

Three tables here were measured at v0.1.0 and re-measured at v0.3.2 on the same
machine and the same corpus. Two of the differences are the price of features
that draw more text. One is not.

**Layout got slower, by roughly a third to a half.** Three independent
measurements agree, and none of them touches the atlas, the system-font tier or
the mesh:

| Measurement | v0.1.0 | v0.3.2 |
|---|---|---|
| C3 median (short ASCII, warm atlas) | 0.630 ms | 0.846 ms |
| Matrix, 5 rebuilds warm, tier off | 0.146 ms | 0.213 ms |
| Matrix, 50 rebuilds warm, tier off | 1.320 ms | 1.689 ms |
| `AsianLayout`, every rule off | 1,357 chars/ms | 755 chars/ms |

A bisect over 106 commits found no single cause: it is a staircase. An earlier
version of this paragraph named the per-frame ppem measurement as the largest
step at +84 µs; **that was an over-attribution and the number is wrong.**
Switching `OneTextLabel.DynamicPpem` off inside one session, alternating twice,
moves C3's median by **29 µs** — under a fifth of the regression. The +84 µs
was the height of one bisect step, and that step also tripled the atlas tile
count.

Where C3's 833 µs actually goes, from `BenchSuite.BreakdownWorldSpace`:

| | µs/frame |
|---|---|
| rebuilds (50 labels) | 561 |
| — layout and shaping | 203 |
| — — HarfBuzz shaping itself | 41 |
| — — itemize / wrap / everything else | 162 |
| — quad building (split, lookup, emit) | 226 |
| — per-rebuild scaffolding | 132 |
| outside rebuilds (canvas, ppem) | 272 |

Two things that table says and the paragraph above it did not. **More than half
the cost is outside the layout engine**: quad building alone (226 µs) beats
layout and shaping (203 µs), and `emit` at 112 µs is the largest single stage.
And **this is a per-call cost, not a per-character one** — C3's damage labels
are one to four ASCII digits, and they cost 4.0 µs to lay out and 4.5 µs to
turn into quads. Nothing here scales with the text; it scales with the number
of labels.

`emit` has been taken apart since. Driving uGUI's `VertexHelper` at C3's rate
and nothing else — 200 quads a frame, 800 `AddVert` and 400 `AddTriangle`,
none of this package's code in the window — costs **48 µs a frame**, because
one `AddVert` appends to eight parallel lists and a frame of C3 is 6,400 of
those appends. So a little under half of `emit` is uGUI's vertex plumbing and
the rest, about 64 µs, is this package's own loop at 320 ns a quad. Recovering
the first half means not using `VertexHelper` — writing a persistent mesh and
handing it to `canvasRenderer.SetMesh` — which also takes the label out of the
`IMeshModifier` chain that Outline, Shadow and every third-party mesh modifier
attach to. That is a trade, not a cleanup, and it has not been made.

That is consistent with what the bisect saw. `TextLayoutEngine.cs` went from
1,214 lines to 2,162 over the same range, `TextRun` gained three fields and
`TextQuad` went from 16 to 18, while the HarfBuzz shaping call underneath is
unchanged to the microsecond: a label that uses none of the new features still
pays for the branches and the state that serve them. The fix is therefore not a
single revert, and it is not the ppem work either. It is per-label fixed cost,
in `EmitQuads` and the atlas cluster lookup before it.

One attempt is already recorded as a failure. The build path re-measures
density through its own `ScreenPpem.Context`, once per rebuild, where the
watcher long ago learned to read the camera once per canvas; memoising it per
poll measured 11 µs faster at the median and 35 µs at p99, and then failed the
test written for it — a poll's context stays valid until the *next* poll, not
until the end of the canvas pass, and uGUI offers no signal for the end of a
pass. `DynamicPpemTests.APollThenACameraMove_IsNotServedTheStaleMeasurement`
and `TwoCapturesWithNoCanvasPassBetweenThem_EachBakeAtTheirOwn` keep that door
shut.

**The p99 multiple against TMP shrank because we draw more.** At five rebuilds
a frame of novel glyphs, v0.1.0 published 12x at the median and 23x at p99;
v0.3.2 reads 5x and 9.4x. The tier-off column above shows why: the engine did
not get slower at that workload, it started drawing the 22 % of characters it
used to skip.

**Allocation moved the other way, and by more than the tables above show.** The
v0.1.0 rows read 0.9 to 1.2 KB a frame; the rebuild path now allocates nothing
measurable at any label count, and what remains in a compound row is the
scenario's own strings.

## What the numbers say

**Where OneText wins.** A charset nobody can enumerate ahead of time. In C2,
TMP's dynamic atlas costs a 10 to 16 ms hitch at p99 against 1.3 ms, its
texture memory grows to between 9 and 20 MB and keeps going while OneText holds
the budget it was configured with, and OneText draws every character in the
frame where TMP draws 78 % of them. In C1 the language switch produces a
340.9 ms frame in TMP against 6.8 ms here with the tier off — **50x**, and
twenty frames a player watches disappear. The workload matrix isolates why:
with unseen glyphs arriving at five labels a frame, OneText is 5x faster at the
median and 9.4x at p99, in one draw call against eight. Everything draws in one
material+texture pair however many faces and sizes are on screen, and a label
that changes its text allocates nothing.

**Where TMP wins.** A charset known ahead of time and fully baked — TMP static
is 5x faster than OneText at the C2 median while drawing 60 % of the text — and,
now, layout on a warm atlas. C3 and both warm cells of the matrix are losses of
15 to 28 %, and they are losses against a system drawing about a fifth less. At
fifty rebuilds a frame of entirely new glyphs TMP's dynamic atlas posts a lower
median by giving up: 32 atlas pages, 20 MB, and 21 % of characters undrawn.

**The claim.** At the same rasterization density, **OneText's worst frames are
8 to 50x cheaper in dynamic-charset workloads, in a fixed memory budget that
does not grow and one draw call that does not split, while drawing characters
TMP has no glyph for.** It is not "faster than TMP": on a warm atlas with a
known charset, TMP is faster than this, and by more than it was at v0.1.0.
