using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Burst;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OneText.Benchmarks
{
    /// <summary>One frame's cost.</summary>
    public struct FrameSample
    {
        public double Milliseconds;   // rebuilding text, plus whatever the system does per frame
        public long AllocatedBytes;   // managed allocation during that work
        public int DrawGroups;        // distinct material+texture pairs the canvas will draw
        public int Graphics;          // graphics that will be drawn (submeshes included)

        /// <summary>
        /// A collection ran inside the measured window, so the heap delta is
        /// not this frame's allocation and nothing can recover it. The frame's
        /// time still counts; its bytes are excluded rather than reported as a
        /// number the reader would take literally.
        /// </summary>
        public bool AllocationUnreadable;
    }

    /// <summary>
    /// What one scenario measured for one text system. Every scenario reports
    /// the same card, so results stay comparable across scenarios: median and
    /// p99 rather than a mean (a text system is judged by its worst frames),
    /// the batch count, allocation per frame, and texture memory.
    /// </summary>
    public sealed class BenchRun
    {
        public string Scenario;
        public string Subject;
        public string Notes;

        /// <summary>
        /// What the scenario cost before its first sampled frame, captured at
        /// the moment the counters were zeroed for the timed loop. Null unless
        /// diagnostics were on. Kept rather than discarded because prewarm cost
        /// is a real number someone will want; it is only a lie when it is
        /// divided by the frame count and called per-frame.
        /// </summary>
        public string SetupNotes;

        public readonly List<FrameSample> Samples = new List<FrameSample>();
        public long TextureBytes;

        public double Median => Percentile(0.5);
        public double P99 => Percentile(0.99);

        public double Max
        {
            get
            {
                double max = 0;
                foreach (var sample in Samples) max = Math.Max(max, sample.Milliseconds);
                return max;
            }
        }

        public int MaxFrame
        {
            get
            {
                double max = -1;
                int index = -1;
                for (int i = 0; i < Samples.Count; i++)
                {
                    if (Samples[i].Milliseconds <= max) continue;
                    max = Samples[i].Milliseconds;
                    index = i;
                }
                return index;
            }
        }

        /// <summary>
        /// Bytes per frame, over the frames whose delta was readable. The gauge
        /// resolves about a 4 KB page, so one frame's figure means little and
        /// this mean is the number worth quoting: the page noise averages out
        /// across hundreds of frames, and it rounds up rather than down.
        /// </summary>
        public double MeanBytes
        {
            get
            {
                double total = 0;
                int counted = 0;
                foreach (var sample in Samples)
                {
                    if (sample.AllocationUnreadable) continue;
                    total += sample.AllocatedBytes;
                    counted++;
                }
                return counted == 0 ? 0 : total / counted;
            }
        }

        /// <summary>Frames a collection landed in, whose bytes are not counted.</summary>
        public int UnreadableFrames
        {
            get
            {
                int count = 0;
                foreach (var sample in Samples) if (sample.AllocationUnreadable) count++;
                return count;
            }
        }

        public int MedianDrawGroups => MedianOf(s => s.DrawGroups);

        public int MedianGraphics => MedianOf(s => s.Graphics);

        private int MedianOf(Func<FrameSample, int> select)
        {
            if (Samples.Count == 0) return 0;
            var values = new List<int>(Samples.Count);
            foreach (var sample in Samples) values.Add(select(sample));
            values.Sort();
            return values[values.Count / 2];
        }

        private double Percentile(double fraction)
        {
            if (Samples.Count == 0) return 0;
            var values = new List<double>(Samples.Count);
            foreach (var sample in Samples) values.Add(sample.Milliseconds);
            values.Sort();
            int index = Mathf.Clamp((int)Math.Ceiling(fraction * values.Count) - 1, 0, values.Count - 1);
            return values[index];
        }
    }

    /// <summary>
    /// The canvas, camera and per-frame measurement every scenario runs on.
    ///
    /// Frames are rendered for real rather than only rebuilding meshes: batch
    /// and SetPass counts are half the question, and they only exist once
    /// something has actually drawn.
    /// </summary>
    public sealed class BenchScene : IDisposable
    {
        public readonly Canvas Canvas;
        private readonly Camera _camera;
        private readonly RenderTexture _target;
        private readonly GameObject _cameraGo;
        private readonly GameObject _canvasGo;
        private readonly Stopwatch _watch = new Stopwatch();
        private int _frameIndex;

        /// <summary>
        /// How often the camera actually draws. The metrics do not need it
        /// (draw groups are counted structurally), and tens of thousands of
        /// hand-driven renders exhaust the editor's graphics device, so this
        /// draws often enough to prove the scene still renders and no more.
        /// </summary>
        public int RenderEvery { get; set; } = 50;

        public BenchScene(int width = 1280, int height = 720, bool worldSpace = false)
        {
            _cameraGo = new GameObject("BenchCamera");
            _camera = _cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            _camera.orthographic = !worldSpace;
            _camera.orthographicSize = height / 2f;
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            _camera.targetTexture = _target;

            _canvasGo = new GameObject("BenchCanvas", typeof(RectTransform));
            Canvas = _canvasGo.AddComponent<Canvas>();
            if (worldSpace)
            {
                Canvas.renderMode = RenderMode.WorldSpace;
                var rect = (RectTransform)_canvasGo.transform;
                rect.sizeDelta = new Vector2(width, height);
                _cameraGo.transform.position = new Vector3(0f, 0f, -600f);
            }
            else
            {
                Canvas.renderMode = RenderMode.ScreenSpaceCamera;
                Canvas.worldCamera = _camera;
            }

            // The SDF shader reads this global for its ZTest; a hand-driven
            // canvas render never sets it.
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);
        }

        /// <summary>
        /// Runs one frame: <paramref name="mutate"/> changes text, then the
        /// canvas rebuilds and the system uploads whatever it batched. Only
        /// that work is timed; the draw itself is measured in batches, not
        /// milliseconds, because a hand-driven render is not a frame budget.
        /// </summary>
        public FrameSample Frame(ITextSubject subject, Action mutate)
        {
            int collectionsBefore = GC.CollectionCount(0);
            long before = AllocatedBytes();
            _watch.Restart();
            mutate?.Invoke();
            Canvas.ForceUpdateCanvases();
            subject.EndFrame();
            _watch.Stop();
            long after = AllocatedBytes();
            bool collected = GC.CollectionCount(0) != collectionsBefore;

            if (_frameIndex++ % Mathf.Max(1, RenderEvery) == 0) _camera.Render();

            CountDrawGroups(out int groups, out int graphics);
            return new FrameSample
            {
                Milliseconds = _watch.Elapsed.TotalMilliseconds,
                // Only a heap gauge is available here, so a collection inside
                // the window makes the delta meaningless; it can even go
                // negative. Clamping that to zero is what turned the old figures
                // into a floor and hid it: a frame that collected looked like a
                // frame that allocated nothing. Say unreadable instead.
                AllocatedBytes = collected ? 0 : Math.Max(0, after - before),
                AllocationUnreadable = collected,
                DrawGroups = groups,
                Graphics = graphics,
            };
        }

        private readonly HashSet<(int material, int texture)> _groups = new HashSet<(int, int)>();
        private readonly List<UnityEngine.UI.Graphic> _graphicsScratch = new List<UnityEngine.UI.Graphic>();

        /// <summary>
        /// How many distinct material+texture pairs the canvas is about to
        /// draw, and how many graphics contribute.
        ///
        /// This is a structural count, not a GPU counter: a hand-driven render
        /// in batch mode never ticks the engine's own batch statistics. It is
        /// the number that decides batching all the same: uGUI can only merge
        /// draws that share a material and a texture, so a text system with a
        /// material per font asset, or one that splits fallback runs into
        /// submeshes, reports more groups here and issues more draws in a real
        /// frame.
        /// </summary>
        private void CountDrawGroups(out int groups, out int graphics)
        {
            _groups.Clear();
            _graphicsScratch.Clear();
            _canvasGo.GetComponentsInChildren(includeInactive: false, _graphicsScratch);

            graphics = 0;
            foreach (var graphic in _graphicsScratch)
            {
                if (!graphic.isActiveAndEnabled) continue;
                var renderer = graphic.canvasRenderer;
                if (renderer == null || renderer.cull) continue;

                // uGUI batches by the material it renders with and the texture
                // that material samples; a submesh (TMP splits fallback runs
                // into child graphics) shows up here as its own entry.
                var texture = graphic.mainTexture;
                int count = Mathf.Max(1, renderer.materialCount);
                for (int i = 0; i < count; i++)
                {
                    var material = renderer.GetMaterial(i);
                    if (material == null) continue;
                    _groups.Add((material.GetInstanceID(),
                        texture != null ? texture.GetInstanceID() : 0));
                    graphics++;
                }
            }
            groups = _groups.Count;
        }

        private static int s_allocationMode; // 0 = untested, 1 = precise, 2 = heap delta

        private static long AllocatedBytes()
        {
            if (s_allocationMode == 0) s_allocationMode = ProbeAllocationCounter();
            if (s_allocationMode == 1)
            {
                try { return GC.GetAllocatedBytesForCurrentThread(); }
                catch (Exception) { s_allocationMode = 2; }
            }
            // Heap size instead: undercounts whenever a collection lands inside
            // the measured window, so it is a floor, not a total.
            return GC.GetTotalMemory(false);
        }

        /// <summary>
        /// Which instrument this runtime actually has. The exact per-thread
        /// counter is preferred and, in the editor, absent: Unity's Boehm
        /// collector returns 0 from it rather than throwing, so this probes with
        /// a known allocation instead of trusting the API to exist. Pinning the
        /// collector to make a heap delta exact is not available either (the
        /// editor refuses <c>GarbageCollector.GCMode</c> outright), and
        /// ProfilerRecorder's "GC Allocated In Frame" stays at zero samples
        /// without a player loop to tick it. A heap delta is what is left.
        /// </summary>
        private static int ProbeAllocationCounter()
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[1 << 20];
                probe[0] = 1;
                if (GC.GetAllocatedBytesForCurrentThread() - before >= (1 << 19)) return 1;
            }
            catch (Exception)
            {
                // falls through to the heap-delta mode
            }
            return 2;
        }

        /// <summary>
        /// Makes the Burst jobs actually be Burst jobs for the duration of a run.
        ///
        /// The editor's default is compilation on, synchronous compilation off:
        /// a job runs as managed IL until a background thread finishes compiling
        /// it. A benchmark started from the command line lives for a few seconds
        /// and never sees that finish, so it measures the interpreter and calls
        /// it the engine. On this machine that read the SDF rasterizer as 12x
        /// slower than it is: 131 ms against 11 ms for the same 368k texels.
        ///
        /// A player has no such mode: Burst is compiled ahead of time and always
        /// on. Forcing synchronous compilation costs one stall in the warm-up
        /// and makes the editor report the build.
        ///
        /// Returns what to restore, so a benchmark cannot leave the editor in a
        /// mode the user did not choose.
        /// </summary>
        public static (bool Enabled, bool Synchronous) ForceBurst()
        {
            var previous = (BurstCompiler.Options.EnableBurstCompilation,
                            BurstCompiler.Options.EnableBurstCompileSynchronously);
            BurstCompiler.Options.EnableBurstCompilation = true;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            s_forcedBurst = true;
            return previous;
        }

        public static void RestoreBurst((bool Enabled, bool Synchronous) previous)
        {
            BurstCompiler.Options.EnableBurstCompilation = previous.Enabled;
            BurstCompiler.Options.EnableBurstCompileSynchronously = previous.Synchronous;
        }

        // Latched, not read live: the report is written after the run has
        // already restored the editor's own setting, so asking Burst what it is
        // doing now would describe the editor rather than the measurement.
        private static bool s_forcedBurst;

        /// <summary>Whether the numbers came from compiled jobs, for the report to disclose.</summary>
        public static string BurstMethod
        {
            get
            {
                if (s_forcedBurst)
                    return "Burst, compiled synchronously before the run: what a player build gets";
                return BurstCompiler.Options.EnableBurstCompilation
                    ? "Burst compilation is on but asynchronous, so a short run measures the managed " +
                      "fallback rather than the compiled job; on this machine that read the SDF " +
                      "rasterizer as 12x slower than it is. These numbers are NOT what a player " +
                      "build gets; call BenchScene.ForceBurst() first"
                    : "Burst is DISABLED. The SDF rasterizer ran as managed IL. These numbers are not " +
                      "comparable to a player build, where Burst is compiled ahead of time";
            }
        }

        /// <summary>How allocation is being measured, for the report to disclose.</summary>
        public static string AllocationMethod
        {
            get
            {
                if (s_allocationMode == 0) s_allocationMode = ProbeAllocationCounter();
                return s_allocationMode == 1
                    ? "per-thread allocation counter (exact)"
                    : "managed heap delta, which resolves about a 4 KB page and rounds up: " +
                      "measured against known allocations it read 20.8 KB of real work as 28-32 KB, " +
                      "5.2 KB as 4-8 KB, and 1 KB as 0 more often than not, while never reporting " +
                      "bytes for a frame that allocated none. Per-frame figures below a page mean " +
                      "nothing; the per-frame mean over hundreds of frames is the number to read, " +
                      "and it is an over-estimate. Frames that collected are excluded, not clamped";
            }
        }

        public void Dispose()
        {
            _camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(_target);
            UnityEngine.Object.DestroyImmediate(_canvasGo);
            UnityEngine.Object.DestroyImmediate(_cameraGo);
        }
    }

    /// <summary>Writes the report card as markdown, plus per-frame CSVs for the curves.</summary>
    public static class BenchReport
    {
        public static void Write(string outDir, string title, List<BenchRun> runs)
        {
            Directory.CreateDirectory(outDir);
            var markdown = new StringBuilder();
            markdown.AppendLine($"# {title}");
            markdown.AppendLine();
            markdown.AppendLine($"Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceType}, " +
                $"{SystemInfo.processorType}. Median of the frames in each run; p99 and max are what a " +
                "player feels as a hitch. Draw groups are distinct material+texture pairs (the thing " +
                "that decides whether uGUI can batch), counted structurally, since a hand-driven render " +
                "does not tick the engine's own batch statistics.");
            markdown.AppendLine();
            markdown.AppendLine($"Jobs: {BenchScene.BurstMethod}.");
            markdown.AppendLine();
            markdown.AppendLine($"Allocation measured by: {BenchScene.AllocationMethod}. " +
                "Each cell is the median of 3 repetitions, chosen by p99, so a stray GC pause in one " +
                "repetition cannot decide the number.");
            markdown.AppendLine();
            markdown.AppendLine("| Scenario | System | Frames | Median ms | p99 ms | Max ms (frame) | Draw groups | Graphics | Alloc/frame | GC frames | Texture |");
            markdown.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

            foreach (var run in runs)
            {
                markdown.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3:F3} | {4:F3} | {5:F3} ({6}) | {7} | {8} | {9} | {10} | {11} |",
                    run.Scenario, run.Subject, run.Samples.Count, run.Median, run.P99,
                    run.Max, run.MaxFrame, run.MedianDrawGroups, run.MedianGraphics,
                    Bytes(run.MeanBytes), run.UnreadableFrames, Bytes(run.TextureBytes)));
            }

            markdown.AppendLine();
            foreach (var run in runs)
            {
                if (string.IsNullOrEmpty(run.Notes)) continue;
                markdown.AppendLine($"- **{run.Scenario} / {run.Subject}**: {run.Notes}");
            }

            string path = Path.Combine(outDir, "bench-report.md");
            File.WriteAllText(path, markdown.ToString());

            foreach (var run in runs)
            {
                var csv = new StringBuilder("frame,ms,alloc_bytes,alloc_readable,draw_groups,graphics\n");
                for (int i = 0; i < run.Samples.Count; i++)
                {
                    var sample = run.Samples[i];
                    csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1:F4},{2},{3},{4},{5}", i, sample.Milliseconds,
                        sample.AllocatedBytes, sample.AllocationUnreadable ? 0 : 1,
                        sample.DrawGroups, sample.Graphics));
                }
                string name = $"{Slug(run.Scenario)}-{Slug(run.Subject)}.csv";
                File.WriteAllText(Path.Combine(outDir, name), csv.ToString());
            }

            Debug.Log($"[bench] report written to {path}\n{markdown}");
        }

        private static string Bytes(double value)
        {
            if (value >= 1024 * 1024) return $"{value / (1024 * 1024):F1} MB";
            if (value >= 1024) return $"{value / 1024:F1} KB";
            return $"{value:F0} B";
        }

        private static string Slug(string value) =>
            value.ToLowerInvariant().Replace(' ', '-').Replace('/', '-').Replace(",", "");
    }
}
