# OneText demo

Fourteen animated effects, three decorations, nine writing systems and a
stress field, next to the numbers underneath them: batches, set-pass calls,
triangles, reserved and texture memory, and every atlas sheet as an image you
can look at.

## Running it

1. Import this sample (Package Manager → OneText → Samples → Demo → Import).
2. **Tools → OneText → Samples → Build Demo Scene.** This writes
   `Assets/OneTextDemo/OneTextDemo.unity`: a camera, an EventSystem, and one
   object carrying `OneTextDemo`.
3. Press play.

There is no prefab and nothing to lay out by hand. `OneTextDemo` builds its
canvas in `Awake`, so what the demo claims lives in code you can diff rather
than in a scene file nobody can read.

**Tools → OneText → Samples → Build Web Demo** is the same thing as a WebGL
player, with the settings the package's own `page~/demo/` was built with —
Brotli *and* its decompression fallback, because the page is served from
GitHub Pages and a static host cannot set `Content-Encoding`. Copy the
resulting `Build/` over `page~/demo/Build/`; the `index.html` beside it is
hand-written and stays — except for the `build` tag near the top, which every
`Build/` URL is versioned by and which has to move with the files. A returning
visitor otherwise runs the new wasm against the data file the loader cached
under the old URL, which does not degrade, it blows the stack before the first
frame.

## Fonts

Leave the fonts empty and the demo runs on `SystemFonts`, which finds
something for most of these scripts in a desktop editor **and nothing at all
in a player**. WebGL in particular has no font directory to search.

Two ways to give it fonts, on the `OneTextDemo` component:

- **Primary** — a `OneFontAsset`. Per-codepoint fallbacks then come from
  Project Settings → OneText. This is the convenient route in the editor.
- **Font Files** — TTF/OTF files imported as `TextAsset`, which Unity does for
  any file renamed `.bytes`. The first is the primary and the rest are the
  fallback chain, in order. This is the route a build wants: it is the only one
  that carries a chain without a project setting behind it. Bytes win when both
  are set.

For the nine script rows to all draw, the chain needs coverage for Arabic,
Devanagari, Thai, Hebrew, Hangul, kana, Han and colour emoji. The Noto families
cover all of it and are OFL-licensed. Rows with no coverage draw tofu, which is
honest — and the stats panel says which of the three font routes it took, so
"the demo drew boxes" and "the demo had no fonts" do not look the same.

Seven faces ship with the sample, and the last two are subsets: the Han row and
the emoji row need a sixteen-megabyte CJK face and a ten-megabyte colour emoji
face to cover them, and they are here as twelve and eighty kilobytes cut to the
characters the specimens actually contain.
`Tools/make_demo_font_subsets.py` reads that set out of these sources rather
than a list, so a new specimen string is re-run rather than remembered — and a
specimen nobody cut for shows up as the tofu it is instead of quietly resolving
through a system font that no build will have.

## Reading the numbers

The panel keeps two kinds of number apart on purpose.

**Unity's counters** — fps, batches, set-pass calls, draw calls, triangles,
vertices, and the three memory rows — come from `ProfilerRecorder`. Nothing in
this package can inflate them. They exist in the editor and in a development
build; in a release build they read as `—` rather than as a confident zero.

**OneText's own counters** — tiles, fill, uploads, evictions — come from
`GlyphAtlasStats` and are labelled as this package marking its own homework.

The claim worth testing is the batch count. Press `+ 50` a few times: the label
count climbs and the batch count does not, because every label samples one
shared atlas through one shared material. Measured in a play-mode capture on an
M4 Pro at 1600×900, walking the count up in fifties:

| labels | batches | set-pass |
|---|---|---|
| 86 | 10 | 6 |
| 186 | 10 | 6 |
| 286 | 10 | 6 |
| 386 | 10 | 6 |
| 486 | 10 | 6 |

Four hundred extra labels, all of them drawn, for no extra batch. Note what
this is *not* claiming: the whole screen is not one batch, because the panels,
buttons and clip rects cost their own. What is constant is the cost of more
text.

### Why the text does not all merge into one draw

Open the Frame Debugger and you will see text that does not batch with other
text. The conclusion that suggests itself — that the text engine is what costs
you draws — is the wrong one, and it is worth being precise about why.

Taken apart by disabling one piece at a time, at 136 labels:

| what is drawing | batches | set-pass |
|---|---|---|
| everything | 10 | 5 |
| one `RectMask2D` disabled | 7 | 5 |
| both disabled | 5 | 5 |
| …and the atlas viewer too | 4 | 4 |
| **text alone, no Images at all** | **1** | **1** |

**The text is one batch.** Nine scripts, colour emoji, fourteen animated
effects, 136 labels, one draw call — because every one of them samples one
`Texture2DArray` through one material. This is the engine's actual contribution,
and it is what a second TextMeshPro font asset cannot do: a second font asset is
a second material and therefore a second batch. Here a second script is a few
more atlas tiles and no draws.

**The other nine are uGUI's**, and five of them are the two scroll masks. Two
`RectMask2D`s set different `_ClipRect` values, the CanvasRenderer writes that
uniform per draw, and draws with different uniform values cannot merge. (Two
masks with *identical* canvas-space rects would merge. Side-by-side columns
never have identical rects.)

Note what the numbers do **not** show. uGUI clipping is a shader keyword —
`#pragma multi_compile_local _ UNITY_UI_CLIP_RECT` in `OneText-SDF.shader`, and
`multi_compile` rather than `shader_feature` because the material is built at
runtime and stripping the variant would make masking work in the editor and
silently fail in a build. It would be reasonable to expect that variant switch
to cost a set-pass call. Measured, it does not: the set-pass count is unmoved by
the masks. The clip rects cost draws, not passes.

Five batches for two scroll views is a real cost and it is not fixed here. See
"Making the masks free" below.

**The SRP Batcher does not apply.** It requires an SRP; the compatibility table
lists Built-in as No. And it would not help under URP either: it batches
meshes drawn through renderers with `UnityPerDraw`/`UnityPerMaterial` constant
buffers, and uGUI geometry goes through Canvas's own native batcher. A Screen
Space Overlay canvas renders outside the render pipeline loop entirely.

### Could the masks be made free? Measured, and no

Five batches for two scroll masks is not a law of nature. Clipping costs draws
because the shader does it, per draw, from a uniform the CanvasRenderer sets —
so every distinct clip rect is a draw boundary. OneText generates its own mesh
and so does not have to clip that way. `MaskableGraphic.SetClipRect(Rect, bool)`
is `virtual` and its default body is one line; a label that overrode it to
record the rect and clip its own glyph quads would emit no `_ClipRect` at all,
and every scroll view's text in a project would merge into one batch. TMP cannot
do this — it does not own the geometry.

Draw calls are not the goal, though; frame time is. Both sides were measured on
the demo before writing any of it, by disabling the masks (the win) and marking
the masked labels' vertices dirty every frame (the cost — `SetVerticesDirty`,
not `ForceMeshUpdate`, because clipping is a mesh-stage operation and would not
re-run layout). Three interleaved repetitions, 400 frames each after 60 warmup,
M4 Pro, 136 labels of which 69 are inside a mask:

| | median ms | p99 | max | batches |
|---|---|---|---|---|
| **L0** shader clipping, idle — *today* | 0.823 | 2.21 | 2.25 | 10 |
| **L0** shader clipping, scrolling | 0.803 | 2.22 | 2.27 | 15 |
| **L1** mesh clipping, idle | **0.710** | 2.09 | 2.22 | 5 |
| **L1** mesh clipping, scrolling | 1.423 | 2.88 | 3.11 | 7 |

Repetitions agreed to within 1% on the medians.

Mesh clipping wins 0.113 ms while nothing moves and loses 0.620 ms while
something does — 9.0 µs per masked label per frame. A hybrid that mesh-clips
only when the clip rect is still would take the good column of each: 0.710 idle,
0.803 scrolling.

On this screen the prize is therefore 0.113 ms — 0.7% of a 60 Hz frame. Which
would settle it, except that this screen is the worst case for the idea. The
two sides scale on different axes: the win is one draw boundary per distinct
clip rect and so grows with the number of **masks**, while the cost is a rebuild
per moving label and so grows with the amount of masked **text**. Two tall
scroll columns is the least favourable shape there is.

So the same measurement again, holding the text at 64 labels and sweeping the
mask count:

| | labels | L0 ms | L0 batches | L1 idle ms | L1 idle batches | L1 all-moving ms | win | cost |
|---|---|---|---|---|---|---|---|---|
| 2 masks × 32 | 64 | 0.224 | 2 | 0.121 | 1 | 0.774 | +0.103 | +0.550 |
| 8 masks × 8 | 64 | 0.245 | 8 | 0.107 | 1 | 0.763 | +0.138 | +0.519 |
| 32 masks × 2 | 64 | 0.349 | 32 | 0.110 | 1 | 0.784 | +0.238 | +0.435 |
| 64 masks × 1 | 64 | 0.487 | 64 | 0.116 | 1 | 0.777 | +0.371 | +0.290 |
| 32 masks × 8 | 256 | 0.803 | 32 | 0.256 | 1 | 2.910 | +0.547 | +2.107 |

Same text, 32× the masks: the win triples and the cost falls. Sixty-four clipped
widgets draw in **one batch** instead of sixty-four. And the "all-moving" column
is a ceiling nobody hits — a real UI scrolls one view at a time, which at 32×8
is eight labels rebuilding, about 0.065 ms, against a win of 0.547 ms.

That is the shape of a real game UI: inventory slots, list rows that each clip
their own name, a wall of gauges. Many small clipped widgets, not two tall
columns.

It was built, on the strength of that table, and then reverted. Both halves of
the table turned out to be measuring the wrong thing, and the design had a hole
in the case it existed for. Written down here because the idea is a good one and
somebody — possibly the same person — will have it again.

**The design was wrong about what moves.** A group watched its `RectMask2D`'s
canvas-space rect to decide whether the view was scrolling. In a `ScrollRect` the
Viewport carries the mask and does not move; the Content moves under it. So the
rect never changed, the group never fell back, and — worse — `RectMask2D` only
pushes `SetClipRect` to its clippables when that canvas-space rect changes
(`RectMask2D.PerformClipping`, the `if (clipRect != m_LastClipRectCanvasSpace)`),
so nothing ever told the labels to re-cut. Each label kept a cut frozen in its
own local space and carried it along as it scrolled: glyphs amputated in the
middle of the view, dropped glyphs never coming back, and text drawn outside the
mask with no shader clip left to catch it, because the whole point had been to
turn that off. Both test layers were static, so neither could see any of it.

**The measurement was of the wrong thread.** The harness stopwatched
`Canvas.ForceUpdateCanvases()` plus `cam.Render()`, which is main-thread work:
canvas rebuild, culling, command generation. Draw-call submission happens on the
render thread and the GPU is never waited for, so a table showing 64 batches
collapse to 1 for 0.074 ms was reporting main-thread bookkeeping, not the cost of
the draws. On Apple Silicon and Metal, besides — the platform where a draw call
is cheapest, while the whole case for the feature is the platforms where it is
not. The number is a floor, and too low a floor to conclude anything from.

**What is actually known.** The cut itself works: rendered both ways, the same
scene came out identical to the pixel (12,911 lit pixels, none differing), with
glyphs correctly severed at all four edges. Batches collapse to 1 in every
configuration tried.

### And then the draw call was priced properly

The feature is gone, but the question underneath it — what a uGUI draw call is
worth — was worth answering once, correctly. In a standalone development player
with its own loop and vsync off, reading `FrameTimingManager`, on a scene where
the *only* difference between the two stages is the value in `_ClipRect`: 64
masks all clipping to their own near-full-screen rect (so every one is its own
draw) against 64 masks all clipping to exactly the canvas (so the uniform is
identical and uGUI merges them). Same labels in the same places, nothing
actually cut in either stage, same components doing the same work.

| stage | batches | set-pass | main ms | render ms | GPU ms |
|---|---|---|---|---|---|
| distinct clip rects | 68 | 4 | 0.499 | 0.082 | 0.377 |
| identical clip rects | 4 | 3 | 0.499 | 0.045 | 0.276 |
| distinct, again | 134 | 6 | 0.499 | 0.083 | 0.290 |
| identical, again | 4 | 3 | 0.500 | 0.048 | 0.276 |

Three things fall out.

**The main thread does not care.** Identical to three decimal places across all
four stages. Every earlier measurement in this file was a stopwatch around
main-thread work, which is why they were all wrong: draw submission is not on
that thread.

**The render thread cares by 0.037 ms** — about 0.6 µs a batch, for sixty-odd
of them. That is the real price of the draw calls, and it is the number the
whole feature was chasing.

**The frame does not change,** because the render thread was never the
bottleneck: 0.5 ms of main thread against 0.08 ms of render thread. Saving
37 µs on a thread with 0.42 ms of slack buys nothing.

And a fourth thing, unlooked for: those 64 masks cost **0.5 ms of main thread**
while their draw calls cost 0.037 ms of render thread. `RectMask2D`'s own
per-frame work is thirteen times the thing everyone tries to optimise.

### On the platform it was supposed to help

Apple Silicon and Metal is where a draw call is cheapest, so the same probe was
built for Android and run on a real device — a Galaxy Z Flip7, Samsung Xclipse
950, Vulkan, 2520×1080, same 64 masks and 256 labels.

| stage | batches | main ms | render ms | GPU ms |
|---|---|---|---|---|
| distinct clip rects | 65 | 4.214 | 0.398 | 2.625 |
| identical clip rects | 2 | 7.174 | 0.280 | 2.485 |
| distinct, again | 65 | 7.166 | 0.520 | 3.291 |
| identical, again | 2 | 7.122 | 0.279 | 2.522 |

**A draw call is about five times dearer here.** 63 extra batches cost 0.12 to
0.24 ms of render thread, against 0.037 ms for the same count on the desktop —
roughly 2–4 µs each rather than 0.6. GPU rises too, by 0.14 to 0.77 ms, though
that column is noisy. So the intuition about mobile was right.

**And it still does not matter,** because of the column beside it. The main
thread sits at ~7.15 ms and does not move with the batch count — 86% of a 120 Hz
frame, spent before a single draw is submitted. Saving a fifth of a millisecond
on a thread that finishes in half a millisecond changes nothing about when the
frame ends.

(The 4.214 in the first row is not a batching effect — it is the device still
boosted from launch, settling by the second stage. Mobile CPU scaling is why
that row disagrees with the other three and why the main-thread figures here
are worth less than the render-thread ones, which repeated to within a
thousandth across reps.)

**What that 7.15 ms is made of is not known**, and it is worth being careful
here rather than guessing, because the guess is tempting and this experiment
cannot support it. Both stages ran the same 64 `RectMask2D`s doing the same
per-frame walk over the same 256 clippables — the mask machinery was the
*control*, not the variable. So the table proves that batch count does not move
the main thread, and proves nothing at all about what the masks themselves cost.
Attributing the 7.15 ms to `ClipperRegistry` would be exactly the kind of
unmeasured claim that produced everything else in this file that had to be
retracted.

Settling it needs one more stage: the same scene with the masks deleted. If the
main thread drops to a couple of milliseconds, the mask walk is the headline and
the draw calls are a rounding error. If it barely moves, the 7 ms is canvas
overhead that would be paid anyway. Not yet run.

So: measured on the platform the feature was for, with instruments that see the
thread that pays, on real hardware. The answer is no.

The demo keeps shader clipping.

### The step that used to be here

An earlier version of this sample did show a step — one batch and one set-pass
somewhere past 150 labels — and it was the sample's fault, not the engine's.
The stress grid outgrew the panel it was laid out in, and geometry that spills
past its container stops merging with what is drawn after it. The step landed
exactly when the first label crossed the edge and never grew as the spill did.
The grid now shrinks its cells to fit; if it ever runs out of room to shrink,
the caption says so, because from that point the batch count is measuring the
spill rather than the text.

## The buttons

| | |
|---|---|
| `replay` | Resets `AnimationTime` on the entrance effects, which settle and stop. |
| `precise ⇄` | Flips every label to the MSDF atlas and back. Watch the memory row: the precise sheet is four bytes a texel where the ordinary one is one. |
| `+ 50` / `clear` | Adds or drops stress labels. |
| `atlas ⇄` / `layer ▸` | Cycles the sheets that exist, and the layers within one. |
| `+200 glyphs` | Demands a block of Hangul nothing has drawn yet, so the atlas has to rasterise it while you watch. Keep pressing and it will eventually evict, which is a thing worth having seen before a player sees it. |

## Not here yet

The input field. Watching the atlas rasterise a glyph the instant you type it
is the best thing this demo could do, and on the web it needs the hidden-HTML-
input work the roadmap still lists as outstanding. The `+200 glyphs` button
fakes the interesting half.
