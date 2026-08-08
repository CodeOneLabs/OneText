using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Shaping and measuring the same font from several threads at once.
    ///
    /// Nothing in the engine shapes in parallel yet, which is exactly why this
    /// is worth writing now. The shared state is already shaped like the trap:
    /// one parsed face behind many font handles, a per-font ink-bounds cache,
    /// and an outline extractor whose hb-draw callbacks have nowhere to put
    /// their state but a static. The field's version of this bug is not a crash
    /// but a word rendered at another label's weight, or a glyph assembled from
    /// two glyphs, the kind that reaches a build. Written now, it fails loudly;
    /// written after something goes parallel, it fails in a bug report.
    /// </summary>
    public class ThreadSafetyTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static readonly string[] LatinWords =
        {
            "Hamburgefonstiv", "quick brown fox", "Ærøskøbing", "flï¬ ligature",
            "typography", "Wavelength", "jumped over", "1234567890",
        };

        private static readonly string[] ArabicWords =
        {
            "مرحبا", "بالعالم", "العربية", "كتابة", "الخط", "تشكيل",
        };

        /// <summary>Shapes one string and returns glyph ids and advances, flattened.</summary>
        private static long[] ShapeSignature(FontData font, string text)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, text, shaped);
            var signature = new long[shaped.Count * 4];
            for (int i = 0; i < shaped.Count; i++)
            {
                signature[i * 4 + 0] = shaped[i].GlyphId;
                signature[i * 4 + 1] = shaped[i].XAdvance;
                signature[i * 4 + 2] = shaped[i].XOffset;
                signature[i * 4 + 3] = shaped[i].YOffset;
            }
            return signature;
        }

        /// <summary>
        /// Runs <paramref name="body"/> on N threads and rethrows the first
        /// failure. NUnit assertions on a worker thread are exceptions there and
        /// silence here, so they have to be carried back by hand.
        /// </summary>
        private static void OnThreads(int threads, int iterations, Action<int, int> body)
        {
            var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var started = new CountdownEvent(threads);
            var go = new ManualResetEventSlim(false);
            var workers = new Thread[threads];

            for (int t = 0; t < threads; t++)
            {
                int thread = t;
                workers[t] = new Thread(() =>
                {
                    started.Signal();
                    go.Wait();
                    try
                    {
                        for (int i = 0; i < iterations; i++) body(thread, i);
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue(e);
                    }
                }) { IsBackground = true, Name = $"OneText test worker {t}" };
                workers[t].Start();
            }

            started.Wait();
            go.Set(); // release them together, so they overlap rather than queue
            foreach (var worker in workers)
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(60)),
                    "a worker thread did not finish; the shared font state deadlocked");

            if (failures.TryDequeue(out var first)) throw first;
        }

        [Test]
        public void ConcurrentShaping_MatchesSingleThreadedShaping()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);

            // Baseline first, on this thread alone.
            var expectedLatin = new Dictionary<string, long[]>();
            foreach (var word in LatinWords) expectedLatin[word] = ShapeSignature(latin, word);
            var expectedArabic = new Dictionary<string, long[]>();
            foreach (var word in ArabicWords) expectedArabic[word] = ShapeSignature(arabic, word);

            OnThreads(threads: 8, iterations: 40, body: (thread, iteration) =>
            {
                // Each thread takes its own handle onto the same parsed face;
                // the sharing that matters (the face) stays shared.
                var latinHandle = latin.ForCurrentThread();
                var arabicHandle = arabic.ForCurrentThread();

                string latinWord = LatinWords[(thread + iteration) % LatinWords.Length];
                var got = ShapeSignature(latinHandle, latinWord);
                CollectionAssert.AreEqual(expectedLatin[latinWord], got,
                    $"'{latinWord}' shaped differently on a worker thread");

                string arabicWord = ArabicWords[(thread + iteration) % ArabicWords.Length];
                var gotArabic = ShapeSignature(arabicHandle, arabicWord);
                CollectionAssert.AreEqual(expectedArabic[arabicWord], gotArabic,
                    $"'{arabicWord}' shaped differently on a worker thread; joining forms are " +
                    "contextual, so a corrupted shape here is a corrupted word");
            });
        }

        [Test]
        public void ConcurrentVariations_DoNotLeakBetweenThreads()
        {
            using var variable = LoadFont(VariableFontPath);
            if (!variable.IsVariable) Assert.Ignore("test font is not variable");

            // One instance per weight, exactly as the font-asset variant cache
            // hands them out. The bug this pins is a thread setting wght 700 and
            // another thread's already-shaped text coming out bold.
            var weights = new[] { 100f, 400f, 700f, 900f };
            var instances = new FontData[weights.Length];
            var expected = new long[weights.Length][];
            try
            {
                for (int i = 0; i < weights.Length; i++)
                {
                    instances[i] = variable.CreateVariant(new FontVariation("wght", weights[i]));
                    expected[i] = ShapeSignature(instances[i], "Hamburgefonstiv");
                }

                // Different weights must actually differ, or the test proves nothing.
                CollectionAssert.AreNotEqual(expected[0], expected[3],
                    "wght 100 and wght 900 shape identically; the axis is not being applied");

                OnThreads(threads: 8, iterations: 40, body: (thread, iteration) =>
                {
                    int which = (thread + iteration) % weights.Length;
                    var got = ShapeSignature(instances[which].ForCurrentThread(), "Hamburgefonstiv");
                    CollectionAssert.AreEqual(expected[which], got,
                        $"wght {weights[which]} picked up another thread's axis settings");
                });
            }
            finally
            {
                foreach (var instance in instances) instance?.Dispose();
            }
        }

        [Test]
        public void ConcurrentInkBounds_AgreeWithSingleThreadedBounds()
        {
            // Two instances on purpose. The baseline is measured on one, so the
            // other reaches the workers with an empty cache and all eight
            // threads race to fill it: a shared Dictionary being written, not
            // merely read, which is the only version of this race that bites.
            using var reference = LoadFont(LatinFontPath);
            using var font = LoadFont(LatinFontPath);

            // Ink bounds sit behind a per-font cache and, for faces whose extents
            // HarfBuzz cannot report, behind the outline extractor's callback
            // state. Layout calls this for every glyph of every line, so it is
            // the first thing a parallel layout would hit.
            var glyphs = new List<uint>();
            using (var shaper = new Shaper())
            {
                var shaped = new List<ShapedGlyph>();
                shaper.Shape(reference, "Hamburgefonstiv ABCDEFGHIJKLMNOPQRSTUVWXYZ", shaped);
                foreach (var glyph in shaped) glyphs.Add(glyph.GlyphId);
            }

            var expected = new Dictionary<uint, (Vector2 min, Vector2 max, bool ink)>();
            foreach (uint gid in glyphs)
            {
                bool ink = reference.TryGetInkBounds(gid, out var min, out var max);
                expected[gid] = (min, max, ink);
            }

            // Deliberately the shared instance, NOT ForCurrentThread: the cache
            // is per FontData, so per-thread handles would each get a cache of
            // their own and never contend. This is the only way to exercise the
            // lock the cache actually has, and layout would use the shared
            // instance too, since ink bounds are a property of the face.
            OnThreads(threads: 8, iterations: 20, body: (thread, iteration) =>
            {
                foreach (uint gid in glyphs)
                {
                    bool ink = font.TryGetInkBounds(gid, out var min, out var max);
                    var want = expected[gid];
                    Assert.AreEqual(want.ink, ink, $"glyph {gid}: ink flag disagrees across threads");
                    Assert.AreEqual(want.min, min, $"glyph {gid}: min bound disagrees across threads");
                    Assert.AreEqual(want.max, max, $"glyph {gid}: max bound disagrees across threads");
                }
            });
        }

        [Test]
        public void ConcurrentOutlineExtraction_ProducesWholeGlyphs()
        {
            using var font = LoadFont(LatinFontPath);

            var glyphs = new List<uint>();
            using (var shaper = new Shaper())
            {
                var shaped = new List<ShapedGlyph>();
                shaper.Shape(font, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg", shaped);
                foreach (var glyph in shaped) glyphs.Add(glyph.GlyphId);
            }

            // Point count per contour is a fingerprint: a glyph assembled from
            // two threads' callbacks has the wrong number of contours, or the
            // right number with the wrong points in them.
            var expected = new Dictionary<uint, int[]>();
            var scratch = new GlyphOutline();
            foreach (uint gid in glyphs)
            {
                scratch.Clear();
                OutlineExtractor.Extract(font, gid, scratch, 0.05f);
                var counts = new int[scratch.Contours.Count];
                for (int i = 0; i < counts.Length; i++) counts[i] = scratch.Contours[i].Count;
                expected[gid] = counts;
            }

            OnThreads(threads: 8, iterations: 20, body: (thread, iteration) =>
            {
                var handle = font.ForCurrentThread();
                var outline = new GlyphOutline();
                foreach (uint gid in glyphs)
                {
                    outline.Clear();
                    OutlineExtractor.Extract(handle, gid, outline, 0.05f);
                    var want = expected[gid];
                    Assert.AreEqual(want.Length, outline.Contours.Count,
                        $"glyph {gid}: wrong contour count; the extractor's callback state was shared");
                    for (int i = 0; i < want.Length; i++)
                        Assert.AreEqual(want[i], outline.Contours[i].Count,
                            $"glyph {gid}: contour {i} came back with the wrong number of points");
                }
            });
        }

        [Test]
        public void ThreadHandles_ShareTheFace_AndAreReleasedWithTheFont()
        {
            var font = LoadFont(LatinFontPath);
            FontData workerHandle = null;

            var worker = new Thread(() => workerHandle = font.ForCurrentThread());
            worker.Start();
            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(10)));

            Assert.AreNotSame(font, workerHandle, "a worker thread must get its own font handle");
            Assert.IsTrue(workerHandle.IsValid);
            Assert.AreEqual(font.UnitsPerEm, workerHandle.UnitsPerEm,
                "the handle must be onto the same parsed face, not a second parse of the bytes");
            Assert.AreSame(font, font.ForCurrentThread(),
                "the owning thread must not pay for a handle it does not need");

            font.Dispose();
            Assert.IsFalse(workerHandle.IsValid,
                "worker handles are owned by the font; disposing it must not leave them dangling " +
                "onto a destroyed face");
        }

        [Test]
        public void Variants_HandOutThreadHandlesToo()
        {
            using var font = LoadFont(VariableFontPath);
            if (!font.IsVariable) Assert.Ignore("test font is not variable");

            // A variant is where the axes live, so it is the instance most
            // likely to be shaped from several threads at once. Treating it as a
            // leaf that returns itself would leave exactly the case this
            // mechanism exists for sharing one mutable hb_font.
            var bold = font.CreateVariant(new FontVariation("wght", 700f));
            try
            {
                FontData first = null, second = null;
                var a = new Thread(() => first = bold.ForCurrentThread());
                var b = new Thread(() => second = bold.ForCurrentThread());
                a.Start();
                Assert.IsTrue(a.Join(TimeSpan.FromSeconds(10)));
                b.Start();
                Assert.IsTrue(b.Join(TimeSpan.FromSeconds(10)));

                Assert.AreNotSame(bold, first, "a variant handed a worker thread its own handle back");
                Assert.AreNotSame(first, second, "two threads were handed the same variant handle");

                // And the handle really is at the variant's weight, not the
                // face default; copying the axes is the whole point.
                CollectionAssert.AreEqual(
                    ShapeSignature(bold, "Hamburgefonstiv"),
                    ShapeSignature(first, "Hamburgefonstiv"),
                    "the worker's handle shaped at different axis values than the variant it came from");
            }
            finally
            {
                bold.Dispose();
            }
        }

        [Test]
        public void ChangingAxes_DoesNotDestroyHandlesInUse()
        {
            using var font = LoadFont(VariableFontPath);
            if (!font.IsVariable) Assert.Ignore("test font is not variable");

            var variant = font.CreateVariant(new FontVariation("wght", 400f));
            try
            {
                FontData handle = null;
                var worker = new Thread(() => handle = variant.ForCurrentThread());
                worker.Start();
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(10)));
                Assert.IsTrue(handle.IsValid);

                // A label changing weight while a worker holds a handle must not
                // pull the native object out from under it: hb_font_destroy
                // mid-shape is a crash with no managed stack.
                variant.SetVariations(new FontVariation("wght", 900f));
                Assert.IsTrue(handle.IsValid,
                    "changing the axes destroyed a handle a worker thread may be shaping with");

                // The next handle that thread asks for is at the new weight.
                FontData refreshed = null;
                var again = new Thread(() => refreshed = variant.ForCurrentThread());
                again.Start();
                Assert.IsTrue(again.Join(TimeSpan.FromSeconds(10)));
                Assert.AreNotSame(handle, refreshed, "a retired handle was handed out again");
                CollectionAssert.AreEqual(
                    ShapeSignature(variant, "Hamburgefonstiv"),
                    ShapeSignature(refreshed, "Hamburgefonstiv"),
                    "the replacement handle did not pick up the new axis values");
            }
            finally
            {
                variant.Dispose();
            }
        }
    }
}
