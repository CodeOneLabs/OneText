using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace OneText.Tests
{
    /// <summary>
    /// Throughput budgets for the hot paths, plus the sharing guarantees that
    /// make many labels cheap. The thresholds are deliberately loose — they are
    /// there to catch an order-of-magnitude regression in CI, not to measure a
    /// machine. Every test logs its real number so trends are visible in the
    /// run output.
    /// </summary>
    public class PerformanceTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static string Paragraph(int repeats)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < repeats; i++)
                builder.Append("The quick brown fox jumps over the lazy dog. ");
            return builder.ToString();
        }

        private static double Measure(int iterations, System.Action action)
        {
            action(); // warm up JIT, caches and native handles
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / iterations;
        }

        [Test]
        public void Layout_Throughput()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            string text = Paragraph(100); // ~4.5k characters
            var settings = TextLayoutSettings.Default(fonts, 24f);
            settings.MaxWidth = 800f;
            var result = new TextLayoutResult();

            double ms = Measure(10, () => engine.Layout(text, settings, result));
            Debug.Log($"[perf] layout: {text.Length} chars in {ms:F2} ms " +
                      $"({text.Length / ms:F0} chars/ms, {result.Lines.Count} lines)");

            Assert.Less(ms, 250.0, "full-paragraph layout regressed by an order of magnitude");
        }

        [Test]
        public void Shaping_Throughput()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();

            string latinText = Paragraph(20);
            var arabicBuilder = new StringBuilder();
            for (int i = 0; i < 60; i++) arabicBuilder.Append("السلام عليكم ورحمة الله وبركاته ");
            string arabicText = arabicBuilder.ToString();

            double latinMs = Measure(20, () =>
            {
                glyphs.Clear();
                shaper.Shape(latin, latinText, glyphs);
            });
            double arabicMs = Measure(20, () =>
            {
                glyphs.Clear();
                shaper.Shape(arabic, arabicText, 0, arabicText.Length,
                    Shaper.Direction.RightToLeft, glyphs);
            });

            Debug.Log($"[perf] shaping latin: {latinText.Length / latinMs:F0} chars/ms, " +
                      $"arabic: {arabicText.Length / arabicMs:F0} chars/ms");

            Assert.Less(latinMs, 100.0);
            Assert.Less(arabicMs, 100.0);
        }

        [Test]
        public void Atlas_Cached_Lookup_Is_Far_Cheaper_Than_Rasterizing()
        {
            using var font = LoadFont(LatinFontPath);
            using var shaper = new Shaper();
            using var atlas = new GlyphAtlas();

            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "The quick brown fox jumps over the lazy dog 0123456789", glyphs);

            var watch = Stopwatch.StartNew();
            foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, 48f);
            atlas.Flush();
            double coldMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            for (int i = 0; i < 100; i++)
                foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, 48f);
            double warmMs = watch.Elapsed.TotalMilliseconds / 100.0;

            Debug.Log($"[perf] atlas: {glyphs.Count} glyphs rasterized in {coldMs:F2} ms, " +
                      $"cached lookup {warmMs:F3} ms ({coldMs / System.Math.Max(warmMs, 1e-4):F0}x)");

            Assert.Less(warmMs, coldMs, "a cache hit must beat rasterizing");
            Assert.Less(coldMs, 3000.0, "cold rasterization regressed badly");
        }

        [Test]
        public void Atlas_Upload_Partial_Versus_Full()
        {
            // Uploading a 1024x1024x4 array costs the whole 4 MB however small
            // the change was. Copying just the changed tiles is the fix; this
            // logs both so the difference stays visible.
            using var font = LoadFont(LatinFontPath);
            using var shaper = new Shaper();
            using var atlas = new GlyphAtlas();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "abcdefghij", glyphs);

            foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, 48f);
            atlas.Flush();

            double fullMs = Measure(20, () =>
                atlas.Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false));

            // Rasterize into a fresh size bucket (untimed), then time only the
            // upload of those new tiles — that is the per-frame cost a scene
            // pays when a few glyphs appear.
            var watch = new Stopwatch();
            int rounds = 0;
            var perRound = new List<double>();
            foreach (int ppem in new[] { 24, 32, 40, 56, 64, 80, 96, 112, 128, 160 })
            {
                foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, ppem);
                watch.Restart();
                atlas.Flush();
                watch.Stop();
                perRound.Add(watch.Elapsed.TotalMilliseconds * 1000);
                rounds++;
            }
            double tilesMs = 0;
            foreach (double round in perRound) tilesMs += round;
            tilesMs /= rounds * 1000.0;

            var uploadStats = atlas.GetStats();
            Debug.Log($"[perf] atlas upload: full Apply {fullMs * 1000:F0} us for " +
                      $"{atlas.Settings.MemoryBytes / (1024 * 1024)} MB, " +
                      $"{glyphs.Count} new tiles {tilesMs * 1000:F0} us " +
                      $"({uploadStats.PartialUploads} partial / {uploadStats.FullUploads} full uploads, " +
                      $"supported: {GlyphAtlas.SupportsPartialUpload})");
            Assert.Less(fullMs, 50.0, "a full atlas upload got absurdly slow");
            if (GlyphAtlas.SupportsPartialUpload)
            {
                Assert.AreEqual(0, uploadStats.FullUploads,
                    "a handful of new tiles must not fall back to uploading the whole array");
            }
        }

        [Test]
        public void Atlas_Capacity_Diagnostic()
        {
            // How many tiles a budget actually holds is the number that decides
            // whether a script is usable. Measured with Latin at 96ppem, whose
            // tiles are close in size to CJK at 48ppem.
            using var font = LoadFont(LatinFontPath);
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font,
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzÁÀÂÄÃÅÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜÝ",
                glyphs);

            foreach (int size in new[] { 1024, 2048 })
            {
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings
                {
                    TextureSize = size,
                    LayerCount = 4,
                });

                // Every glyph at every bucket, until the atlas starts recycling.
                int placed = 0;
                foreach (int ppem in new[] { 96, 112, 128, 160, 192, 224, 256 })
                {
                    foreach (var glyph in glyphs)
                    {
                        atlas.GetOrAdd(font, glyph.GlyphId, ppem);
                        atlas.Flush();
                        if (atlas.GetStats().Evictions > 0) break;
                        placed++;
                    }
                    if (atlas.GetStats().Evictions > 0) break;
                }

                var stats = atlas.GetStats();
                Debug.Log($"[perf] atlas capacity: {atlas.Settings} held {placed} large tiles " +
                          $"({stats.UsedFraction:P0} occupancy, {stats.ShelfCount} shelves) " +
                          "before the first eviction");
                Assert.Greater(placed, 0);
            }
        }


        [Test]
        public void Cluster_Granularity_Diagnostic()
        {
            // How much of a line ends up merged into one atlas tile decides how
            // well the cache survives changing text: a tile that covers a whole
            // word has to be re-rasterized whenever any letter in it changes.
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var shaper = new Shaper();

            // Latin letters stand apart, so they must stay one tile each — that
            // is what lets the cache survive an edit. Arabic letters join, so
            // they must still merge or the joint seams.
            int latinLargest = Tiles(shaper, latin, "The quick brown fox", Shaper.Direction.LeftToRight);
            int arabicLargest = Tiles(shaper, arabic, "ورحمة الله", Shaper.Direction.RightToLeft);

            Assert.AreEqual(1, latinLargest, "separate letters must not share a tile");
            Assert.Greater(arabicLargest, 1, "joined letters must share a tile");
        }

        private static int Tiles(Shaper shaper, FontData font, string text, Shaper.Direction direction)
        {
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, text, 0, text.Length, direction, glyphs);
            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();

            int largest = 0;
            foreach (float size in new[] { 16f, 24f, 48f, 96f })
            {
                int ppem = GlyphAtlas.QuantizePixelsPerEm(size);
                float unitsPerTilePixel = font.UnitsPerEm / (float)ppem;
                GlyphClusters.Split(font, glyphs, clusters, positioned,
                    1000f * unitsPerTilePixel, GlyphClusters.DefaultMergeGapUnits(font));

                int biggest = 0;
                foreach (var cluster in clusters) biggest = System.Math.Max(biggest, cluster.Count);
                largest = System.Math.Max(largest, biggest);
                Debug.Log($"[perf] clusters '{text}' at {size}px: {clusters.Count} tiles for " +
                          $"{glyphs.Count} glyphs (largest tile holds {biggest})");
            }
            return largest;
        }

        [Test]
        public void Many_Labels_Share_One_Atlas_And_One_Material()
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var labels = new List<OneTextLabel>();
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

            try
            {
                for (int i = 0; i < 8; i++)
                {
                    var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
                    go.transform.SetParent(canvasGo.transform, false);
                    go.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 60f);
                    var label = go.AddComponent<OneTextLabel>();
                    label.SetFont(fontBytes);
                    label.FontSize = 28f;
                    label.Text = $"shared atlas {i}";
                    label.EnsureLayout();
                    labels.Add(label);
                }

                var material = labels[0].material;
                foreach (var label in labels)
                {
                    Assert.AreSame(material, label.material,
                        "labels must share one material or uGUI cannot batch them");
                }
                Assert.AreSame(SharedGlyphAtlas.Material, material);
                Assert.AreSame(SharedGlyphAtlas.Atlas.Texture, material.GetTexture("_GlyphTex"),
                    "the shared material must sample the shared atlas");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void Mesh_Rebuild_Cost_For_A_Screenful_Of_Labels()
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            var labels = new List<OneTextLabel>();
            const int count = 50;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
                    go.transform.SetParent(canvasGo.transform, false);
                    go.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 40f);
                    var label = go.AddComponent<OneTextLabel>();
                    label.SetFont(fontBytes);
                    label.FontSize = 24f;
                    label.Text = "Status line " + i;
                    labels.Add(label);
                }

                var populate = typeof(Graphic).GetMethod("OnPopulateMesh",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(VertexHelper) }, null);
                var helper = new VertexHelper();

                // First pass rasterizes; steady state is what a running game pays.
                foreach (var label in labels) populate.Invoke(label, new object[] { helper });

                var watch = Stopwatch.StartNew();
                foreach (var label in labels)
                {
                    helper.Clear();
                    populate.Invoke(label, new object[] { helper });
                }
                double ms = watch.Elapsed.TotalMilliseconds;

                Debug.Log($"[perf] mesh rebuild: {count} labels in {ms:F2} ms ({ms / count:F3} ms each)");
                Assert.Less(ms / count, 5.0, "per-label mesh rebuild regressed");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        // ------------------------------------------------- the idle animation

        private static OneTextLabel NewLabel(GameObject canvas, byte[] fontBytes, string text)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvas.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 60f);
            var label = go.AddComponent<OneTextLabel>();
            label.SetFont(fontBytes);
            label.FontSize = 28f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            // Drawn once so the effect spans exist before the first tick: a
            // label that has never been emitted has no spans yet, and would look
            // idle here for a reason that has nothing to do with being finished.
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            return label;
        }

        /// <summary>
        /// Runs the label's own per-frame decision the way Update does — asking
        /// every frame whether there is anything left to animate, and paying the
        /// clock advance (which dirties the vertices) only when there is.
        /// Returns the frames that cost a mesh re-emit.
        ///
        /// Every frame is offered, not stopped at the first idle one, because
        /// idle is not a terminal state: text that changes or a clock scrubbed
        /// backwards has to start the animation again.
        /// </summary>
        private static int TickWhileAnimating(OneTextLabel label, int frames, float delta)
        {
            int ticked = 0;
            for (int i = 0; i < frames; i++)
            {
                if (!label.IsAnimating) continue;
                label.AnimationTime += delta;
                ticked++;
            }
            return ticked;
        }

        [Test]
        public void A_Finished_Effect_Stops_Costing_A_Mesh_Rebuild_Every_Frame()
        {
            // A <pop for=0.3> damage number is 18 frames of work at 60 Hz. The
            // regression this guards is that it went on re-emitting its whole
            // mesh every frame afterwards, for the rest of its life, to write
            // back identical vertices — the span outlives its envelope, so
            // "are there spans?" is always yes and is not the question.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

            try
            {
                var timed = NewLabel(canvasGo, fontBytes, "<pop for=0.3>-40</pop>");
                var forever = NewLabel(canvasGo, fontBytes, "<wave>-40</wave>");
                var plain = NewLabel(canvasGo, fontBytes, "-40");

                const int frames = 600; // ten seconds at 60 Hz
                const float delta = 1f / 60f;
                int timedTicks = TickWhileAnimating(timed, frames, delta);
                int foreverTicks = TickWhileAnimating(forever, frames, delta);

                Debug.Log($"[perf] idle animation: <pop for=0.3> re-emitted on {timedTicks}/{frames} " +
                          $"frames, <wave> on {foreverTicks}/{frames}");

                Assert.GreaterOrEqual(timedTicks, 15,
                    "the pop never got to play — the clock stopped before its envelope did");
                Assert.Less(timedTicks, 30,
                    "a finished for= effect is still dirtying its vertices every frame");
                Assert.IsFalse(timed.IsAnimating,
                    "nothing about this label can change again until its text does");

                // The envelope is not a hard stop, so a label that has gone idle
                // must be resting exactly where an unanimated one draws — an
                // idle test that passes on a frozen mid-swing is worse than none.
                timed.SetAllDirty();
                timed.Rebuild(CanvasUpdate.PreRender);
                plain.SetAllDirty();
                plain.Rebuild(CanvasUpdate.PreRender);
                var resting = new List<TextQuad>(timed.DrawnQuads);
                var home = new List<TextQuad>(plain.DrawnQuads);
                Assert.AreEqual(home.Count, resting.Count);
                for (int i = 0; i < home.Count; i++)
                {
                    Assert.AreEqual(home[i].Position.x, resting[i].Position.x, 0.001f);
                    Assert.AreEqual(home[i].Position.y, resting[i].Position.y, 0.001f);
                    Assert.AreEqual(home[i].Size, resting[i].Size);
                }

                Assert.AreEqual(frames, foreverTicks,
                    "an effect with no for= has no end; stopping its clock freezes it mid-swing");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void An_Idle_Label_Animates_Again_When_There_Is_Work()
        {
            // The three ways work comes back. Each of them is a way this fix
            // turns into an effect that silently never plays again if "finished"
            // is ever cached instead of recomputed.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

            try
            {
                var label = NewLabel(canvasGo, fontBytes, "<pop for=0.3>-40</pop>");
                TickWhileAnimating(label, 600, 1f / 60f);
                Assert.IsFalse(label.IsAnimating, "the pop should be over");

                // Scrubbing backwards makes finished work unfinished again.
                label.AnimationTime = 0.1f;
                Assert.IsTrue(label.IsAnimating, "a rewound clock has the effect still to run");

                // New markup is a new envelope.
                label.AnimationTime = 9f;
                label.Text = "<wave>-40</wave>";
                Assert.IsTrue(label.IsAnimating, "replacing the spans has to restart the tick");

                // A typewriter mid-reveal is work whatever the envelopes say:
                // appearance effects key off each cluster's own reveal stamp,
                // and clusters whose turn has not come have not played.
                var typing = NewLabel(canvasGo, fontBytes, "<fade for=0.2>typewriter</fade>");
                typing.MaxVisibleGraphemes = 3;
                typing.AnimationTime = 5f;
                Assert.IsTrue(typing.IsAnimating,
                    "a reveal still in progress is work the clock has to keep serving");
                typing.MaxVisibleGraphemes = -1;
                Assert.IsFalse(typing.IsAnimating,
                    "with the reveal done and the envelope elapsed there is nothing left");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        // --------------------------------------- the idle appearance effect
        //
        // The dialogue-box case: a long string, an appearance effect, no for=.
        // Nothing bounds it but the effect's own settle, so a label that reads
        // only the envelope ticks and re-emits for the rest of its life.

        /// <summary>
        /// A label whose clock counts as running. Outside play mode that is not
        /// automatic and it is not incidental: a label nobody has moved
        /// AnimationTime on is drawn with its appearance effects frozen
        /// finished, which is the editor-preview rule, and such a label is idle
        /// for a reason that has nothing to do with anything measured here.
        /// </summary>
        private static OneTextLabel NewRunningLabel(GameObject canvas, byte[] fontBytes,
            string text, float delta)
        {
            var label = NewLabel(canvas, fontBytes, text);
            label.AnimationTime = delta;
            label.Rebuild(CanvasUpdate.PreRender);
            return label;
        }

        /// <summary>
        /// Ticks like <see cref="TickWhileAnimating"/> and re-emits the mesh
        /// each time, the way a canvas does. Appearance effects need the emit:
        /// their finish is measured from the reveal stamps, and a stamp is only
        /// taken when the mesh is written.
        /// </summary>
        private static int TickAndDraw(OneTextLabel label, int frames, float delta)
        {
            int ticked = 0;
            for (int i = 0; i < frames; i++)
            {
                if (!label.IsAnimating) continue;
                label.AnimationTime += delta;
                label.Rebuild(CanvasUpdate.PreRender);
                ticked++;
            }
            return ticked;
        }

        [Test]
        public void An_Appearance_Effect_With_No_Duration_Stops_When_It_Has_Settled()
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            const float delta = 1f / 60f;
            const int frames = 600; // ten seconds at 60 Hz

            try
            {
                // fade's default is a quarter second per cluster, and every
                // cluster of a fully revealed label is stamped at once, so this
                // is fifteen frames of work followed by nothing.
                var fade = NewRunningLabel(canvasGo, fontBytes, "<fade>hello dialogue</fade>", delta);
                var plain = NewLabel(canvasGo, fontBytes, "hello dialogue");
                int ticks = TickAndDraw(fade, frames, delta);

                Debug.Log($"[perf] idle appearance: <fade> re-emitted on {ticks}/{frames} frames");

                Assert.GreaterOrEqual(ticks, 10, "the fade never got to play");
                Assert.LessOrEqual(ticks, 30,
                    "a settled appearance effect is still dirtying its vertices every frame");
                Assert.IsFalse(fade.IsAnimating,
                    "nothing about this label can change again until its text or reveal does");

                // Idle has to mean finished, not frozen part-way: an alpha the
                // fade stopped at 0.9 is a dialogue box that never quite arrives.
                Assert.AreEqual(plain.DrawnQuads.Count, fade.DrawnQuads.Count,
                    "the settled fade is not drawing every tile");
                for (int i = 0; i < fade.DrawnQuads.Count; i++)
                    Assert.AreEqual(255, (int)fade.DrawnQuads[i].Color.a,
                        "the fade came to rest short of full alpha");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void An_Ambient_Effect_With_No_Duration_Never_Stops()
        {
            // The other half of the same predicate, and the one that breaks
            // visibly when it is got wrong: a wave whose clock is stopped is
            // frozen mid-swing, on screen, for ever.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            const float delta = 1f / 60f;
            const int frames = 600;

            try
            {
                foreach (string markup in new[] { "<wave>{0}</wave>", "<rainbow>{0}</rainbow>",
                                                  "<pulse>{0}</pulse>" })
                {
                    var label = NewRunningLabel(canvasGo, fontBytes,
                        string.Format(markup, "hello dialogue"), delta);
                    Assert.AreEqual(frames, TickAndDraw(label, frames, delta),
                        $"{markup} has no end; stopping its clock freezes it mid-swing");
                }

                // Stacked with an appearance effect, the endless one wins: the
                // fade settling cannot be allowed to silence the wave under it.
                var both = NewRunningLabel(canvasGo, fontBytes,
                    "<wave><fade>hello dialogue</fade></wave>", delta);
                Assert.AreEqual(frames, TickAndDraw(both, frames, delta),
                    "a cluster under both effects has to follow the one that never stops");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void A_Reveal_Still_Running_Keeps_An_Appearance_Effect_Working()
        {
            // The finish is each CLUSTER's reveal stamp plus the settle, never
            // the label clock. Measured against the clock instead, this label
            // goes idle a quarter second in with seven of its ten clusters still
            // to arrive, and they arrive already faded — or never.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            const float delta = 1f / 60f;

            try
            {
                var typing = NewRunningLabel(canvasGo, fontBytes, "<fade>typewriter</fade>", delta);
                typing.MaxVisibleGraphemes = 3;
                typing.Rebuild(CanvasUpdate.PreRender);

                Assert.AreEqual(300, TickAndDraw(typing, 300, delta),
                    "a typewriter mid-reveal has work left however long it has been paused");

                // Five seconds after the first three clusters were stamped, the
                // rest arrive. A stamp taken later has to push the finish out;
                // anything that only ever remembered the earliest reveal calls
                // this label finished the moment the reveal completes.
                typing.MaxVisibleGraphemes = -1;
                typing.Rebuild(CanvasUpdate.PreRender);
                Assert.IsTrue(typing.IsAnimating,
                    "the clusters that just arrived have their whole fade still to play");

                int ticks = TickAndDraw(typing, 600, delta);
                Assert.GreaterOrEqual(ticks, 10, "the late clusters never got to fade in");
                Assert.LessOrEqual(ticks, 30, "and then it has to stop again");
                Assert.IsFalse(typing.IsAnimating);
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void An_Editor_Preview_Draws_An_Appearance_Effect_Finished()
        {
            // A designer typing <fade> into a label in the Scene view has no
            // clock advancing anything. Frozen at t=0 the effect is alpha 0,
            // which is text that has vanished; the label shows appearance
            // effects finished instead. Deciding it is idle must not undo that —
            // idle is about what the next frame would change, not about what is
            // already on screen.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            byte[] fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

            try
            {
                var fade = NewLabel(canvasGo, fontBytes, "<fade>hello dialogue</fade>");
                var rise = NewLabel(canvasGo, fontBytes, "<rise>hello dialogue</rise>");
                var plain = NewLabel(canvasGo, fontBytes, "hello dialogue");

                Assert.AreNotEqual(0, plain.DrawnQuads.Count, "the plain label drew nothing");
                Assert.AreEqual(plain.DrawnQuads.Count, fade.DrawnQuads.Count,
                    "a previewed fade drew fewer tiles than the text has — it is invisible");
                Assert.AreEqual(plain.DrawnQuads.Count, rise.DrawnQuads.Count,
                    "a previewed rise drew fewer tiles than the text has — it is invisible");

                for (int i = 0; i < plain.DrawnQuads.Count; i++)
                {
                    Assert.AreEqual(255, (int)fade.DrawnQuads[i].Color.a, "the previewed fade is dim");
                    Assert.AreEqual(plain.DrawnQuads[i].Position.y, rise.DrawnQuads[i].Position.y,
                        0.001f, "the previewed rise is still on its way up");
                }

                Assert.IsFalse(fade.IsAnimating,
                    "a preview nobody is animating has no frames to pay for");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }
    }
}
