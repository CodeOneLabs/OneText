# Benchmarks

Everything here is produced by the harness in `Editor/Dev/Benchmarks`, on this
machine, from a seeded corpus. To reproduce:

```
Unity -releaseCodeOptimization -batchmode -quit -nographics -projectPath <dev project> \
      -executeMethod CompoundBench.RunAll -oneOut <dir>
```

`CompoundBench` lives in the dev project because it references TextMeshPro;
the scenarios themselves are in the package, so both systems run the identical
scene, strings and frame loop. `OneText.Benchmarks.BenchSuite.RunAll` is the
same thing without the TMP side, `.WorkloadMatrix` varies rebuild count and
glyph novelty independently, `.BreakdownWorldSpace` splits a C3 frame into
stages, and `.AsianLayout` prices the M10 tailorings on their own.
`AllocDiff.Run`, also in the dev project, is where the allocation figures come
from.

**Everything below except the UniText section was re-measured on 2026-11-24,
on the commit after 0.4.2 that carries the hot-path work.** The raw reports
are kept beside this file:
[compound](benchmarks/hot-path-compound-report.md),
[workload matrix](benchmarks/hot-path-workload-matrix.md),
[allocation](benchmarks/hot-path-alloc-diff.md),
[before/after scoreboard](benchmarks/hot-path-scoreboard.md) and
[phone](benchmarks/hot-path-device-board.md). v0.3.2's are still there too
([compound](benchmarks/v0.3.2-compound-report.md),
[allocation](benchmarks/v0.3.2-alloc-diff.md)), and the page that went with
them is in this file's history. Three things that page said have turned out to
be wrong, and they are corrected where they come up rather than quietly
dropped: that the system-font tier costs C1 a worst frame "and buys nothing",
that the tier's cost is "finding the file", and that a TMP static atlas draws
all of C1.

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

**The editor runs Release code optimization.** Unity's editor defaults to
Debug, which leaves managed code about 40 % slower than a player's and does so
unevenly: it costs a system built on struct copies more than one built on
dictionary lookups. Every table on this page before this revision was taken in
Debug. These are taken with `-releaseCodeOptimization`, so **do not subtract
this page's numbers from v0.3.2's**: TextMeshPro's C3 median reads 0.663 ms
there and 0.380 ms here, and TextMeshPro did not change. The before and after
of the same code, in one process and one mode, is the scoreboard below. Even
Release in the editor is Mono, not IL2CPP, and that difference turned out to
matter enough to need a phone; see "On a phone".

## Scenarios

- **C1 global UI**: a live-service HUD of 40 static and 20 changing labels,
  three faces, three sizes, and a language switch a third of the way in.
- **C2 chat stream**: 30 lines of mixed Korean/Japanese/English, one replaced
  every frame for 2000 frames. Glyph churn that no realistic charset covers.
- **C3 world-space labels**: 200 nameplates and damage numbers on a moving
  camera.


## Results

Unity 6000.0.77f1, Apple M4 Pro, null graphics device, Release code
optimization. Median of 3 repetitions per cell, chosen by p99.

| Scenario | System | Median ms | p99 ms | Max ms | Draw groups | Alloc/frame | Texture | Coverage |
|---|---|---|---|---|---|---|---|---|
| C2 | OneText 4 MB | 0.110 | 0.246 | 0.502 | **1** | **4.3 KB** | 4 MB | **100 %** |
| C2 | OneText 4 MB, no system fonts | 0.081 | 0.176 | 0.302 | 1 | 2.5 KB | 4 MB | 78 % |
| C2 | OneText 16 MB | 0.103 | 0.231 | 0.425 | 1 | 4.3 KB | 16 MB | 100 % |
| C2 | TMP dynamic 1024² | 0.558 | 9.470 | 11.953 | 6 | 41.9 KB | 9 MB → grows | 78 % |
| C2 | TMP dynamic 1024² +prewarm | 0.518 | 9.762 | 10.780 | 6 | 39.5 KB | 11 MB → grows | 78 % |
| C2 | TMP dynamic 2048² +prewarm | 3.434 | 15.329 | 23.626 | 2 | 125.8 KB | 20 MB → grows | 78 % |
| C2 | TMP static 1024² | **0.060** | **0.087** | **0.119** | 2 | 7.0 KB | 3 MB | **60 %** |
| C1 | OneText 4 MB | **0.132** | 0.421 | 1.640 | **1** | **3.8 KB** | 4 MB | 100 % |
| C1 | OneText 4 MB, no system fonts | 0.134 | **0.319** | **1.509** | 1 | 2.8 KB | 4 MB | 100 % |
| C1 | TMP dynamic 1024² | 0.311 | 14.345 | 349.921 | 5 | 27.6 KB | 4 MB → grows | 100 % |
| C1 | TMP static 1024² | 0.358 | 0.512 | 1.754 | 8 | 12.6 KB | 3 MB | 100 % |
| C3 | OneText 4 MB | **0.173** | **0.228** | **0.249** | **1** | **1.0 KB** | 4 MB | 100 % |
| C3 | OneText 4 MB, no system fonts | 0.176 | 0.227 | 0.303 | 1 | 1.0 KB | 4 MB | 100 % |
| C3 | TMP dynamic 1024² | 0.380 | 0.444 | 0.458 | 3 | 1.6 KB | 2 MB → grows | 100 % |
| C3 | TMP static 1024² | 0.383 | 0.440 | 0.457 | 5 | 1.6 KB | 3 MB | 100 % |

**C2 is the scenario this engine exists for.** OneText draws all 659
characters of the last frame; TMP dynamic draws 517 of them and substitutes a
replacement for the rest. Doing that, OneText is 5x ahead at the median (0.110
against 0.558) and 38x at p99 (0.246 against 9.5). Switch the system-font tier
off so both draw the same 78 % and it is 0.081, 6.9x.

**C3 was a loss at v0.3.2 and is now a 2.2x lead.** 0.173 ms against TMP
dynamic's 0.380 and TMP static's 0.383. Short ASCII over a warm atlas is
layout and mesh building and nothing else; "What moved since v0.3.2" says where
the time went.

**C1: 2.4x at the median, and the language switch is a 1.6 ms frame.** TMP
dynamic's worst frame there is 350 ms and TMP static's 1.75 ms.

**Correction: the system-font tier does not cost C1 "a worst frame and buy
nothing".** v0.3.2's page said so because both C1 rows read 100 % coverage.
Coverage is counted on the last frame only. Over the run the tier-on row baked
2,154 tiles and the tier-off row 1,795: the tier drew 359 glyphs the project's
fonts had no face for, in frames the coverage column never looks at. What it
costs now is 0.42 against 0.32 ms at p99 and 1.64 against 1.51 ms on the worst
frame.

**Correction: the tier's cost is not "finding the file".** In C2 the tier-on
row costs 0.110 ms against 0.081 and draws 100 % against 78 %. The difference
is the work of the extra glyphs themselves: that run evicted 5,484 tiles
against 1,669. Baking and evicting a glyph nobody predicted costs what it
costs, whichever font it came from.

TMP static's C2 row is the fair version of "TMP is faster than you": 0.060 ms
against 0.110, drawing 60 % of the text. It is a good answer for a charset
fixed at build time and this table says so. Held to that charset OneText
measures 0.021 ms; see "Against a baked atlas".

## Before and after, in one process

The table above cannot say what the hot-path work bought, because the page it
replaces was measured in another mode. This one can. Every fast path added
since 0.4.2 sits behind an internal switch whose off position runs the code it
replaced (`[HotPathSwitch]`; see `Documentation~/README.md`), so one Unity
process can run the package both ways, interleaved with TextMeshPro, three
times. "0.4.2" is every switch off. The probe is kept at
[benchmarks/Scoreboard.cs.txt](benchmarks/Scoreboard.cs.txt); like
`CompoundBench` it needs the dev project's TMP subject.

Median µs (p99 in brackets where it is the story). OneText draws 100 % in
every row.

| Scenario | 0.4.2 | now | TMP dynamic | TMP static |
|---|---|---|---|---|
| C1 | 308 [2,511] | **135** [395] | 283 [13,360] | 331 [482] |
| C2 | 393 | 107 [239] | 683 [9,834], draws 82 % | **58** [81], draws 61 % |
| C3 | 467 | **180** | 380 | 385 |
| 5 rebuilds, warm | 118 | **67** | 96, draws 79 % | 121, draws 79 % |
| 50 rebuilds, warm | 1,018 | **479** | 892, draws 78 % | 1,152, draws 78 % |
| 5 rebuilds, new glyphs | 2,036 | 332 [568] | 10,429 [31,026], draws 78 % | **176** [218], draws 32 % |
| 50 rebuilds, new glyphs | 21,298 | 1,695 [2,521] | 2,441 [170,708], draws 79 % | **1,684** [2,157], draws 31 % |

Two readings. Against its own earlier code the package is 1.7 to 2.6x faster
where the atlas is warm and 3.7 to 12.6x where glyphs are new. Against TMP
dynamic it leads every row at the median, and the p99 column is where the
architectures differ: TMP dynamic's new-glyph frames are 31 and 170 ms. The
last row's lead depends on the font the scene is run with; "On a phone" has
the same row with a font that covers the whole corpus, where it is a narrow
loss.

**Nothing draws differently, and that was checked on pixels, not inferred.**
Each fast path has a test that runs it and the code it replaced and compares
layouts, quads, atlas bytes or meshes. The 27 golden scenes were rendered on a
real graphics device with every switch off and every switch on and compared
texel for texel (27 of 27 identical), with an on-against-on control to show
the comparison itself is quiet. That last step is not ceremony: the first fast
path on this list measured 17 % faster on the null device this page uses while
drawing nothing at all on a real one.

### Against a baked atlas

TMP static is ahead in three of those rows, and in each of them it draws a
fraction of the text: 61 %, 32 % and 31 % (the third is a tie, 1,684 against
1,695). To see what that is worth, OneText
was held to the same characters, by giving it fonts subset to TMP static's
baked charset so that every frame of every scenario drew the identical string
in both systems (checked per frame, not on the last one).

| Scenario | OneText, same characters | TMP static |
|---|---|---|
| C1 | 150 | 353 |
| C2 | 21 | 59 |
| C3 | 206 | 384 |
| 5 rebuilds, warm | 62 | 122 |
| 50 rebuilds, warm | 437 | 1,148 |
| 5 rebuilds, new glyphs | 66 | 176 |
| 50 rebuilds, new glyphs | 485 | 1,667 |

This table is older than the others by a day's commits (it predates the
IL2CPP repairs under "On a phone", which also made the editor 7 to 16 %
faster) and its probe is not in the repository, so read it as the shape of the
answer: **drawing the same text, OneText is about 1.9 to 3.4x faster than a
baked TMP atlas.** What TMP static buys in the scoreboard is not speed. It is
the option of not drawing.

## On a phone

A Galaxy Z Flip7 (SM-F766N, Exynos 2500, Android 17), IL2CPP release build,
ARM64, Vulkan, the package's defaults. Same scenarios, same fonts for both
systems (Noto Sans, Noto Sans Arabic, Noto Sans CJK KR), arms rotated per
repetition, median of 3. The phone was charging and at thermal status 1
throughout, so the absolute numbers are high and **the ratios are what to
read**; within a repetition they are steady to two digits.

| Scenario | 0.4.2 | now | TMP dynamic | now ÷ TMP dynamic (per rep) | TMP static |
|---|---|---|---|---|---|
| C1 | 616 | **206** [1,594] | 406 [125,849] | 0.49, 0.51, 0.55 | 510 (87 % drawn) |
| C2 | 854 | **461** | 1,423 | 0.32, 0.33, 0.32 | 98 (73 %) |
| C3 | 958 | **244** | 523 | 0.47, 0.47, 0.46 | 555 |
| 5 rebuilds, warm | 205 | **106** | 114 | 0.93, 0.95, 0.97 | 146 |
| 50 rebuilds, warm | 1,813 | **761** | 956 | 0.80, 0.82, 0.80 | 1,299 |
| 5 rebuilds, new glyphs | 8,688 | **2,904** [3,848] | 5,322 [12,950] | 0.53, 0.56, 0.55 | 357 (32 %) |
| 50 rebuilds, new glyphs | 43,928 | 8,197 [10,881] | **5,610** [86,063] | 1.46, 1.47, 1.44 | 3,459 (32 %) |

Median µs, p99 in brackets. Coverage here is sampled every 50 frames rather
than on the last one, which is how the third correction surfaced: **TMP static
draws 87 % of C1, not all of it.** The last frame happens to be covered; the
frames around the language switch are not.

**The last row is a loss.** At fifty rebuilds a frame of entirely new glyphs
the phone's median is 8.2 ms against TMP dynamic's 5.6. Its p99 is 10.9 ms
against 86, so the player sees fewer hitches, but the median is the median.
The frame is bound by the job workers (the distance field and the overlap
scan) on a phone with fewer fast cores than this page's desktop, and on the
main thread by HarfBuzz, because the shaping shortcut declines about three
quarters of that scene's CJK runs.

**The same harness on the desktop says part of that row is the font, not the
phone.** Run in the editor with the phone's fonts, now ÷ TMP dynamic reads
0.49, 0.23, 0.49, 0.76, 0.57, 0.36 and **1.09**: the last row is a narrow loss
there too, 3.5 ms against 3.2 at the median with a p99 of 4.7 against 62. The
desktop tables earlier on this page read 0.69 for the same row, and the
difference between the two is the CJK font. The dev project's system CJK font
lacks a fifth of the corpus, so there TMP dynamic draws 78 % and OneText draws
the rest from other system fonts; Noto Sans CJK covers all of it, so both
systems draw everything and this is the like-for-like row. **With a font that
covers the corpus, fifty rebuilds a frame of new glyphs is the one median this
package loses to TMP dynamic, on both machines.** Why that font costs OneText
more has not been isolated (it is CFF, and its cubic outlines are the first
suspect).

**Why a phone at all: the editor was wrong about both systems, in opposite
directions.** The first run on the device lost three of these rows, two of
which the same code led on the desktop in the same harness. Micro-benchmarks of code shape
say why (ns per operation, Mono editor against IL2CPP on the phone): a
`List<60-byte struct>.Add` is 28 against 2.1, a struct array write 20 against
1.0, a `Dictionary<long,int>` lookup 5 against 20, `string[i]` 0.37 against
2.0. TextMeshPro's hot path is mostly the first kind and OneText's mostly the
second. Beyond that, OneText was paying for four things IL2CPP charges and
Mono does not, found with the phone's own `simpleperf`:

| Cost | Share | Repair |
|---|---|---|
| A type-object lookup (a hash map behind a mutex) inside generic span `IndexOf` / `SequenceEqual`, the array overloads of `Mesh.SetVertexBufferData` / `SetIndexBufferData`, and `FieldInfo.GetValue` | 9 to 19 % of the main thread, warm scenes | plain loops, `NativeArray` overloads, a direct field read |
| A garbage-collector write barrier on every reference stored while moving a tile in the atlas's LRU lists (about 20 per hit) | 11 to 14 % | the lists are integer links into one array |
| Burst outline flattening switched off, because it reproduced Mono's arithmetic and IL2CPP's differs in the last bit | outline extraction 8x the editor's | under IL2CPP it does IL2CPP's float arithmetic; 0 differences on 6,750 glyph outlines on the device |
| The distance job zero-filling 16 KB of stack scratch per texel row | 16 % of all CPU time, new-glyph scene | `[SkipLocalsInit]`; every element was written before it was read |

Those took C3 from 0.80 of TMP dynamic to 0.47, the two warm rows from losses
(1.19 to 1.29 and 1.08) to 0.93 and 0.80, and the last row from 1.98 to 1.46.
IL2CPP's "Master" configuration was also tried and changed neither system
measurably.

What this section is not: one phone, one run of the final board, a harness
that is a throwaway project rather than part of the repository, and no
texture-upload cost that a null device would hide, since this one is a real
GPU but was not instrumented for it. iOS, a low-end Android device and WebGL
have not been measured at all.

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
| 50 labels retexted, `ToString()` each (= C3) | 1.36 KB | 1.56 KB |
| a number every frame, no string anywhere | **0.00 KB** | 2.75 KB |

Unchanged from v0.3.2, which is the point: the hot-path work was held to
allocating nothing on the rebuild path, and it does not. The `ToString()` row
is the caller's fifty strings, not either engine.

Times from the same run, for the frame these rows measure: 50 labels retexted
costs OneText 167 µs against TMP's 362, 200 labels 634 against 1,424, and 50
labels with markup 364 against 617. At v0.3.2 each of those was a loss.

The compound tables' Alloc/frame column is a different thing: it includes the
scenario building its strings, and the page-sized gauge rounds up. Its OneText
figures are a few hundred bytes to a kilobyte above v0.3.2's (1.0 KB against
0.9 in C3), which is inside what that gauge can resolve and not evidence
either way; the table above, which isolates the rebuild, is the one that
answers the question.

## The workload matrix

C1, C2 and C3 each move two variables at once (C2 has few long labels *and* a
stream of unseen glyphs, C3 has many short labels *and* a warm atlas), so none
of them can say which of the two a change acted on. This varies rebuild count
and glyph novelty independently, at a fixed string length, 200 labels, 600
frames. Both novelty settings prewarm the same vocabulary, so the only
difference is whether the text stays inside it.

```
Unity -releaseCodeOptimization -batchmode -quit -nographics -projectPath <dev project> \
      -executeMethod CompoundBench.WorkloadMatrix -oneOut <dir>
```

| Rebuilds/frame | Glyphs | System | Median ms | p99 ms | Max ms | Draw groups | Coverage |
|---|---|---|---|---|---|---|---|
| 5 | warm | **OneText** | **0.068** | **0.094** | **0.118** | 1 | **100 %** |
| 5 | warm | TMP dynamic | 0.099 | 0.138 | 0.200 | 1 | 79 % |
| 50 | warm | **OneText** | **0.489** | **0.597** | **0.672** | 1 | **100 %** |
| 50 | warm | TMP dynamic | 0.924 | 1.053 | 1.130 | 1 | 78 % |
| 5 | new | **OneText** | **0.363** | **0.622** | **21.861** | **1** | **100 %** |
| 5 | new | TMP dynamic | 11.367 | 33.402 | 41.015 | 8 | 78 % |
| 50 | new | **OneText** | **1.753** | **2.528** | **23.038** | **1** | **100 %** |
| 50 | new | TMP dynamic | 2.698 | 173.701 | 204.980 | 18 | 79 % |

The warm rows are layout-bound; at v0.3.2 TMP was slightly ahead in both, and
it is now behind by 1.5x and 1.9x while drawing a fifth less. The novel rows
are where the architectures diverge: at five rebuilds a frame OneText is 31x
faster at the median and 54x at p99, in one draw call against eight and 4 MB
against 14.

**The last row no longer needs footnotes to read.** At v0.3.2 TMP's median
there was lower than OneText's, and it got there by opening 32 atlas pages,
spending 20 MB against a fixed 4 MB, and leaving one character in five
undrawn. It still does all three, and its median is now the higher one: 2.7 ms
against 1.75, with a p99 of 174 ms against 2.5. OneText holds 4 MB and one
draw call, evicting 137,376 tiles over the run to do it.

OneText's worst frame in the two novel rows is 22 and 23 ms, once per run.
Each of those runs compacted the atlas exactly once, which is the likeliest
owner of that frame; it has not been isolated, and it is the largest number
on this page that belongs to OneText.

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
> measured", predate v0.3.2 and the hot-path work, are Debug where the rest of
> this page is Release, and have not been re-run.** Every OneText figure
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
| none | 1,762 | 568 | baseline |
| kinsoku (normal) | 1,659 | 603 | +6 % |
| + punctuation compression | 1,520 | 658 | +16 % |
| + CJK-Latin spacing | 1,486 | 673 | +19 % |

The proportions are v0.3.2's (+5, +18, +15 %) within the noise of this bench.
The absolute figures are not comparable with that page's 755 chars/ms, which
was Debug; how much of the difference is the mode and how much is the
hot-path work was not separated for this table, and most of that work is
aimed at short single-line labels rather than a 4,000-character paragraph.

What has not changed is that a project which never ships Japanese does not pay
for them. Costing line edges is behind a flag set once per layout.

## What moved since v0.3.2

**The layout regression v0.3.2 recorded is repaid, and then some.** That page
found C3, both warm cells of the matrix and the Asian layout baseline a third
to a half slower than v0.1.0, traced it to per-label fixed cost rather than
any one commit, and said where it was: `EmitQuads`, the atlas cluster lookup
before it, and uGUI's `VertexHelper`. That is where it was taken out. C3 with
every switch off and on, from the scoreboard: 467 µs and 180.

Where a C3 frame goes now, from `BenchSuite.BreakdownWorldSpace` (diagnostics
on, which is why its median reads 185 rather than 173):

| | µs/frame | v0.3.2 (Debug) |
|---|---|---|
| rebuilds (50 labels) | 134 | 561 |
| — layout and shaping | 53 | 203 |
| — — shaping | 11 | 41 |
| — — itemize / wrap / align | 20 / 6 / 3 | |
| — quad building: split / lookup / emit | 9 / 14 / 23 | 36 / 75 / 88 |
| outside rebuilds (canvas, ppem) | 51 | 272 |

The right-hand column is another mode and is there for shape, not for
subtraction. What the shape says is what v0.3.2's said: **this is a per-call
cost, not a per-character one.** The repairs are therefore short paths for the
case nearly every label is, and none of them is clever:

- **A single-line, single-style, left-to-right string skips the analyses that
  conclude nothing for it**: line-break opportunities, bidi resolution,
  segmentation and itemization. Styled text has the same path. A run no
  OpenType lookup can touch is shaped from the font's advance table without
  calling HarfBuzz; `hb_shape` itself was never slow, but setting up and
  tearing down a call for three digits was most of shaping.
- **The label writes its mesh.** v0.3.2 priced uGUI's `VertexHelper` at 48 µs
  a frame of C3 with none of this package's code in the window, and called
  leaving it "a trade, not a cleanup": it takes the label out of the
  `IMeshModifier` chain. The trade was made with the cost removed. A label
  writes one interleaved vertex buffer and hands the mesh to its
  `CanvasRenderer`; a label that has an `IMeshModifier` on it, or more than
  65,000 vertices, takes the `VertexHelper` path as before.
- **The atlas answers a hit with less.** A cheaper key hash, a small table of
  recent hits, LRU lists that move by integer links.
- **A miss is placed now and baked later.** The label gets its tile and
  builds its quads; the field is baked in background batches on the job
  workers, with outlines recorded by Burst-compiled HarfBuzz callbacks and
  the distance field written by a Neon row job. That is the whole of the 12x
  in the new-glyph rows.

One thing from v0.3.2 still stands. Memoising `ScreenPpem.Context` per poll
measured faster and failed the test written for it (a poll's context stays
valid until the next poll, not until the end of the canvas pass), and
`DynamicPpemTests.APollThenACameraMove_IsNotServedTheStaleMeasurement` and
`TwoCapturesWithNoCanvasPassBetweenThem_EachBakeAtTheirOwn` keep that door
shut. What was done instead is narrower: the labels rebuilt inside one uGUI
graphic rebuild loop share the canvas half of the reading, and anything
outside that loop reads fresh.

Two ideas were measured and thrown away, and are recorded so that nobody pays
for them twice. A cache of whole shaped strings lost 12 µs on C3, because a
damage number is new every frame and the cache only ever missed. A pool of
meshes keyed by quad count changed nothing.

**Linux has not caught up.** The shaping shortcut calls seven HarfBuzz entry
points the committed Linux binary predates. Until the natives are rebuilt the
shortcut stands down there and every run goes to HarfBuzz: correct, and
without that part of the gain. [NATIVES.md](NATIVES.md) lists them.

## What the numbers say

**Where OneText wins.** At the same rasterization density, every scenario
here at the median against TMP dynamic with the dev project's fonts, where it
draws 100 % of the text and TMP about 78 %, and six of seven with a font that
lets both draw everything, on the desktop and on the phone. The tails
are where it is not close: the C1 language switch is a 350 ms frame in TMP
dynamic and 1.6 ms here, and new glyphs arriving at fifty labels a frame cost
TMP a 174 ms p99 against 2.5. Everything draws in one material+texture pair
however many faces and sizes are on screen, in a fixed 4 MB that does not
grow, and a label that changes its text allocates nothing.

**Where TMP wins.** A charset known ahead of time and fully baked, when not
drawing the rest is acceptable: TMP static posts 0.060 ms against 0.110 in C2
and 0.176 against 0.332 at five rebuilds of new glyphs, drawing 60 % and 32 %
of the text. And TMP dynamic at the median of the heaviest new-glyph row when
its font covers the corpus: 3.2 ms against 3.5 on the desktop and 5.6 against
8.2 on the phone, each with a p99 an order of magnitude worse than OneText's.

**What is not known.** One desktop and one phone. No iOS, no low-end Android,
no WebGL, no Linux with the rebuilt native. A null graphics device on the
desktop, so no upload cost there. The UniText comparison above is stale in
both columns.

**The claim.** v0.3.2 ended on "it is not faster than TMP". At the same
density, against TMP dynamic, it now is, in every row of every table here but
one: 1.5 to 2.4x at the median outside the new-glyph rows on the desktop and
1.1 to 2.1x on the phone, several times that when glyphs arrive that nobody
predicted, with worst frames one to two orders of magnitude smaller. The
exception is fifty labels a frame of unseen glyphs from a font that covers
them all, where TMP dynamic's median is lower and its p99 is 62 to 86 ms. A
baked atlas that is allowed to skip what it lacks is still quicker at the
median than drawing everything.
