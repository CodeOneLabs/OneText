using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UI;
// The namespace carries the extension that puts AllocatingGCMemory on Is.Not;
// the alias resolves Is itself, which NUnit also defines.
using Is = UnityEngine.TestTools.Constraints.Is;

namespace OneText.Tests
{
    /// <summary>
    /// Which stage of a rebuild allocates, and how much.
    ///
    /// The compound benchmark can say a scenario costs 19 KB a frame but not
    /// where it comes from: the only instrument the editor offers it is a heap
    /// delta that resolves a 4 KB page, and attributing bytes to a call with one
    /// is not merely imprecise but unsound: <c>GC.GetTotalMemory</c> reports the
    /// whole process heap, so anything the editor does on another thread lands
    /// in the window. Attempts to fix that by repetition produced a reading
    /// where an unchanged-text populate allocated twice what a full rebuild did,
    /// which is impossible and is how the approach was abandoned.
    ///
    /// So attribution is done here instead, with the profiler's own GC.Alloc
    /// recorder: it counts allocation calls the runtime actually made rather
    /// than inferring them from heap movement. The count is the useful number:
    /// zero means a path allocates nothing, and any other value names a path
    /// worth opening. Bytes stay with the benchmark, which measures a whole
    /// frame and can afford a coarse gauge.
    /// </summary>
    public class AllocationTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string Sample = "The quick brown fox jumps over the lazy dog";

        private GameObject _canvas;
        private OneTextLabel _label;
        private VertexHelper _helper;
        private MethodInfo _populate;
        private object[] _populateArgs;

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_canvas.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(600f, 40f);

            _label = go.AddComponent<OneTextLabel>();
            _label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            _label.FontSize = 24f;
            _label.Wrap = TextWrap.NoWrap;

            _helper = new VertexHelper();
            _populate = typeof(Graphic).GetMethod("OnPopulateMesh",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(VertexHelper) }, null);
            _populateArgs = new object[] { _helper };

            // Bake every glyph these tests use, so rasterizing a new tile never
            // lands inside a measured window and gets blamed on the rebuild.
            foreach (var text in new[] { Sample, Sample.ToUpperInvariant() })
            {
                _label.Text = text;
                Populate();
            }
        }

        [TearDown]
        public void TearDown()
        {
            _helper?.Dispose();
            if (_canvas != null) UnityEngine.Object.DestroyImmediate(_canvas);
        }

        private void Populate()
        {
            _helper.Clear();
            _populate.Invoke(_label, _populateArgs);
        }

        /// <summary>
        /// Allocation calls per iteration, from the profiler's own counter.
        ///
        /// Reflection allocates on every <c>Invoke</c> whatever the label does,
        /// so a populate stage cannot be read on its own; every figure here is
        /// net of a baseline running the same scaffolding without the work.
        /// </summary>
        private static double AllocationsPerCall(int iterations, Action work)
        {
            for (int i = 0; i < 200; i++) work(); // warm every lazy path first

            var recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;

            for (int i = 0; i < iterations; i++) work();

            recorder.enabled = false;
            double perCall = recorder.sampleBlockCount / (double)iterations;
            recorder.CollectFromAllThreads();
            return perCall;
        }

        private const int Iterations = 2000;

        [Test]
        public void Rebuild_Stages_Report_Where_Allocation_Happens()
        {
            string a = Sample, b = Sample.ToUpperInvariant();
            bool flip = false;

            // The scaffolding on its own: one reflection Invoke and a Clear, no
            // label work at all. Everything below is quoted net of this.
            double baseline = AllocationsPerCall(Iterations, () =>
            {
                _helper.Clear();
            });

            double setter = AllocationsPerCall(Iterations, () =>
            {
                flip = !flip;
                _label.Text = flip ? a : b;
            });

            _label.Text = a;
            _label.EnsureLayout();
            double layoutCached = AllocationsPerCall(Iterations, () => _label.EnsureLayout());

            double layoutMiss = AllocationsPerCall(Iterations, () =>
            {
                flip = !flip;
                _label.Text = flip ? a : b;
                _label.EnsureLayout();
            });

            _label.Text = a;
            Populate();
            double populateCached = AllocationsPerCall(Iterations, Populate);

            double fullRebuild = AllocationsPerCall(Iterations, () =>
            {
                flip = !flip;
                _label.Text = flip ? a : b;
                Populate();
            });

            Debug.Log(
                $"[alloc] baseline (Clear only)      : {baseline:F2} allocs/call\n" +
                $"[alloc] Text setter                : {setter:F2}\n" +
                $"[alloc] EnsureLayout, cache hit    : {layoutCached:F2}\n" +
                $"[alloc] EnsureLayout, cache miss   : {layoutMiss:F2}\n" +
                $"[alloc] populate, quads cached     : {populateCached:F2}\n" +
                $"[alloc] full rebuild (new text)    : {fullRebuild:F2}\n" +
                $"[alloc] => layout costs {layoutMiss - layoutCached:F2}, " +
                $"quad rebuild+emit costs {fullRebuild - layoutMiss:F2}");

            // Ordering the numbers have to obey, whatever they turn out to be. A
            // full rebuild does everything a cached populate does and more, and
            // the reading that broke the previous approach violated exactly this.
            Assert.GreaterOrEqual(fullRebuild, populateCached - 0.01,
                "a full rebuild cannot allocate less than a populate that reuses its quads");
            Assert.GreaterOrEqual(layoutMiss, layoutCached - 0.01,
                "laying out fresh text cannot allocate less than reusing the cached layout");
        }

        [Test]
        public void Steady_State_Redraw_Does_Not_Allocate()
        {
            _label.Text = Sample;
            Populate();

            // The case a static label hits every frame: nothing changed, the
            // layout and the quads are both cached, and only the emit runs. If
            // anything here allocates, every label in a scene pays it forever.
            Assert.That(() =>
            {
                _label.EnsureLayout();
            }, Is.Not.AllocatingGCMemory(), "re-reading a cached layout must not allocate");
        }

        [Test]
        public void Laying_Out_Unchanged_Text_Does_Not_Allocate()
        {
            _label.Text = Sample;
            _label.EnsureLayout();

            // Braces, not an expression body: an expression lambda here returns
            // the layout and binds as a Func, which the constraint rejects.
            Assert.That(() => { _label.EnsureLayout(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Shaping_A_Run_Does_Not_Allocate()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();

            // Warm the buffer growth and the language lookup, which are one-time
            // by design; what matters is whether a steady run allocates.
            for (int i = 0; i < 50; i++)
            {
                glyphs.Clear();
                shaper.Shape(font, Sample, 0, Sample.Length, Shaper.Direction.LeftToRight, glyphs, null);
            }

            // The measured delegate is part of what has to be warm: its first
            // invocation JIT-compiles the lambda body, and the recorder blames
            // that one-time allocation on shaping. Bisected per-step, the run
            // itself is clean from the second invocation on, forever.
            TestDelegate warmedRun = () =>
            {
                glyphs.Clear();
                shaper.Shape(font, Sample, 0, Sample.Length, Shaper.Direction.LeftToRight, glyphs, null);
            };
            warmedRun();

            Assert.That(warmedRun, Is.Not.AllocatingGCMemory(), "shaping a warmed run must not allocate");
        }

        [Test]
        public void Laying_Out_Fresh_Text_Does_Not_Allocate()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var stack = new FontStack();
            stack.Add(font, null);
            using var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();

            var settings = new TextLayoutSettings
            {
                Fonts = stack,
                FontSize = 24f,
                MaxWidth = 600f,
                MaxHeight = 0f,
                Alignment = TextAlignment.Left,
                Wrap = TextWrap.Wrap,
                Overflow = TextOverflow.Overflow,
                LineSpacing = 1f,
                BaseDirection = Unicode.BidiAlgorithm.AutoDirection,
            };

            string a = Sample, b = Sample.ToUpperInvariant();
            for (int i = 0; i < 50; i++) engine.Layout(i % 2 == 0 ? a : b, settings, result);

            bool flip = false;
            Assert.That(() =>
            {
                flip = !flip;
                engine.Layout(flip ? a : b, settings, result);
            }, Is.Not.AllocatingGCMemory(), "laying out text of a size already seen must not allocate");
        }

        [Test]
        public void Asking_Whether_A_Label_Still_Animates_Does_Not_Allocate()
        {
            _label.Text = $"<fade>{Sample}</fade>";
            Populate();
            for (int i = 0; i < 200; i++) if (_label.IsAnimating) { }

            // Every animated label asks this once a frame whether or not it
            // then ticks, so anything allocating here is paid per label per
            // frame for the life of the scene. The part worth watching is the
            // test for an effect's declared settle time: it runs per span, and
            // written as a cast of a boxed struct rather than a type test it
            // would box one back on every frame of every label in the scene.
            Assert.That(() => { if (_label.IsAnimating) { } },
                Is.Not.AllocatingGCMemory(),
                "the per-frame idle decision must not allocate");
        }
    }
}
