using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OneText.Benchmarks
{
    /// <summary>
    /// Runs the compound scenarios and writes one report.
    ///
    /// The OneText side lives here, in the package. The TextMeshPro side
    /// implements <see cref="ITextSubject"/> in the dev project and calls the
    /// same scenarios, so the package never depends on TMP, and both systems
    /// still run the identical scene, strings and frame loop.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Benchmarks.BenchSuite.RunAll -oneOut &lt;dir&gt;
    /// </summary>
    public static class BenchSuite
    {
        // Every entry point below is a command-line target that produces a
        // number someone will quote. They all run under Burst, forced on and
        // compiled before the work starts; see BenchScene.ForceBurst for why
        // the editor's default would otherwise report the interpreter.
        public static void RunAll() => UnderBurst(RunAllCore);
        public static void Breakdown() => UnderBurst(BreakdownCore);
        public static void BreakdownWorldSpace() => UnderBurst(BreakdownWorldSpaceCore);
        public static void RasterizeModes() => UnderBurst(RasterizeModesCore);
        public static void AsianLayout() => UnderBurst(AsianLayoutCore);
        public static void WorkloadMatrix() => UnderBurst(WorkloadMatrixCore);
        public static void SystemFallback() => UnderBurst(SystemFallbackCore);

        private static void UnderBurst(Action body)
        {
            var previous = BenchScene.ForceBurst();
            try { body(); }
            finally { BenchScene.RestoreBurst(previous); }
        }

        /// <summary>
        /// What the system-font tier costs, and what remembering saves.
        ///
        /// Every other scenario registers a CJK face as a project fallback, so
        /// the tier answers a handful of times and its cost never shows. This
        /// ships one Latin font and lets the device answer for the Korean,
        /// which is the setup the tier exists for — and runs it twice, once
        /// with the per-script memory and once without, so the difference is
        /// the memory's and nothing else's.
        /// </summary>
        private static void SystemFallbackCore()
        {
            string outDir = GetArg("-oneOut") ??
                Path.Combine(Path.GetTempPath(), "onetext-bench");
            var runs = new List<BenchRun>();
            var previousFilter = UnityEngine.Debug.unityLogger.filterLogType;

            foreach (bool remember in new[] { true, false, true, false })
            {
                SystemFonts.RememberAnswers = remember;
                UnityEngine.Debug.unityLogger.filterLogType = LogType.Assert;
                var run = BenchScenarios.ChatStream(new OneTextSubject(
                    new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 },
                    systemFallbackOnly: true), 600);
                UnityEngine.Debug.unityLogger.filterLogType = previousFilter;
                runs.Add(run);
                Debug.Log($"[fallback] memory {(remember ? "on " : "off")}: " +
                    $"median {run.Median * 1000:F0} us, p99 {run.P99 * 1000:F0} us, " +
                    $"max {run.Max * 1000:F0} us | {run.Notes}");
            }

            SystemFonts.RememberAnswers = true;
            BenchReport.Write(outDir, "The system-font tier, with and without its memory", runs);
        }

        private static void RunAllCore()
        {
            string outDir = GetArg("-oneOut") ??
                Path.Combine(Path.GetTempPath(), "onetext-bench");
            var runs = new List<BenchRun>();
            runs.AddRange(Run(() => Subjects()));
            BenchReport.Write(outDir, "OneText compound benchmarks", runs);
        }

        /// <summary>
        /// Runs the churn scenario with the cost breakdown on, which is the only
        /// way to tell "the mesh path is slow" from "we are paying to bake
        /// glyphs nobody has seen yet": different numbers, different fixes.
        /// </summary>
        private static void BreakdownCore()
        {
            const int frames = 2000;
            AtlasDiagnostics.Reset();
            AtlasDiagnostics.Enabled = true;
            var previousFilter = UnityEngine.Debug.unityLogger.filterLogType;
            UnityEngine.Debug.unityLogger.filterLogType = LogType.Assert;

            BenchRun run;
            try
            {
                run = BenchScenarios.ChatStream(new OneTextSubject(
                    new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 }, prewarm: true), frames);
            }
            finally
            {
                UnityEngine.Debug.unityLogger.filterLogType = previousFilter;
                AtlasDiagnostics.Enabled = false;
            }

            double totalMs = 0;
            foreach (var sample in run.Samples) totalMs += sample.Milliseconds;
            double glyphMs = AtlasDiagnostics.Ms(AtlasDiagnostics.OutlineTicks +
                AtlasDiagnostics.RasterizeTicks + AtlasDiagnostics.CopyTicks);
            double layoutMs = AtlasDiagnostics.Ms(AtlasDiagnostics.LayoutTicks);
            double rebuildMs = AtlasDiagnostics.Ms(AtlasDiagnostics.RebuildTicks);
            double uploadMs = AtlasDiagnostics.Ms(AtlasDiagnostics.UploadTicks);

            Debug.Log($"[breakdown] C2 over {frames} frames: {totalMs:F0} ms total " +
                $"({totalMs / frames * 1000:F0} us/frame, median {run.Median * 1000:F0} us, " +
                $"p99 {run.P99 * 1000:F0} us)\n" +
                $"[breakdown] {run.SetupNotes}\n" +
                $"[breakdown] steady state: {AtlasDiagnostics.Report(frames)}\n" +
                $"[breakdown] inside the rebuild ({rebuildMs:F0} ms): layout+shape {layoutMs:F0} ms, " +
                $"glyph baking {glyphMs:F0} ms, mesh {rebuildMs - layoutMs - glyphMs:F0} ms; " +
                $"outside it: upload {uploadMs:F0} ms, " +
                $"canvas and scene {totalMs - rebuildMs - uploadMs:F0} ms of {totalMs:F0} ms");
        }

        /// <summary>
        /// The same breakdown for the scenario that allocates most: 200 labels
        /// rebuilding every frame, where any per-label cost is multiplied.
        /// </summary>
        private static void BreakdownWorldSpaceCore()
        {
            const int frames = 600;
            AtlasDiagnostics.Reset();
            AtlasDiagnostics.Enabled = true;
            var previousFilter = UnityEngine.Debug.unityLogger.filterLogType;
            UnityEngine.Debug.unityLogger.filterLogType = LogType.Assert;

            BenchRun run;
            try
            {
                run = BenchScenarios.WorldSpaceLabels(new OneTextSubject(
                    new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 }, prewarm: true), frames);
            }
            finally
            {
                UnityEngine.Debug.unityLogger.filterLogType = previousFilter;
                AtlasDiagnostics.Enabled = false;
            }

            Debug.Log($"[breakdown] C3 over {frames} frames: median {run.Median * 1000:F0} us, " +
                $"p99 {run.P99 * 1000:F0} us\n" +
                $"[breakdown] {run.SetupNotes}\n" +
                $"[breakdown] steady state: {AtlasDiagnostics.Report(frames)}");
        }

        /// <summary>
        /// The workload matrix: rebuild count against glyph novelty, at a fixed
        /// string length, with the per-stage breakdown printed for each cell.
        ///
        /// Three different bottlenecks have been measured in this engine and
        /// each wants a different fix: a layout fast path for many short
        /// rebuilds, inline rasterization for a trickle of unseen glyphs, a
        /// parallel layout pre-pass for rebuild counts in general. A fix is only
        /// worth landing if it moves the cell it was aimed at, and only safe to
        /// land if it leaves the others where they were. One scenario cannot
        /// show either, because C2 and C3 each vary both axes at once.
        ///
        /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
        ///      OneText.Benchmarks.BenchSuite.WorkloadMatrix
        /// </summary>
        private static void WorkloadMatrixCore()
        {
            const int frames = 600, labels = 200;
            var previousFilter = UnityEngine.Debug.unityLogger.filterLogType;

            foreach (var cell in new[]
            {
                (Name: "few rebuilds, warm atlas",  Rebuilds:  5, Reuse: 1f),
                (Name: "many rebuilds, warm atlas", Rebuilds: 50, Reuse: 1f),
                (Name: "few rebuilds, new glyphs",  Rebuilds:  5, Reuse: 0f),
                (Name: "many rebuilds, new glyphs", Rebuilds: 50, Reuse: 0f),
            })
            {
                AtlasDiagnostics.Reset();
                AtlasDiagnostics.Enabled = true;
                UnityEngine.Debug.unityLogger.filterLogType = LogType.Assert;
                BenchRun run;
                try
                {
                    run = BenchScenarios.Churn(new OneTextSubject(
                            new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 }, prewarm: true),
                        cell.Name, frames, labels, cell.Rebuilds, cell.Reuse);
                }
                finally
                {
                    UnityEngine.Debug.unityLogger.filterLogType = previousFilter;
                    AtlasDiagnostics.Enabled = false;
                }

                Debug.Log($"[matrix] {cell.Name}: median {run.Median * 1000:F0} us, " +
                    $"p99 {run.P99 * 1000:F0} us, {cell.Rebuilds} rebuilds/frame, reuse {cell.Reuse:F0}\n" +
                    $"[matrix]   {run.SetupNotes}\n" +
                    $"[matrix]   steady: {AtlasDiagnostics.Report(frames)}");
            }
        }

        /// <summary>
        /// Times the same glyphs rasterized inline and through the job
        /// scheduler, so the fixed cost of scheduling can be separated from the
        /// distance-field work itself.
        /// </summary>
        private static void RasterizeModesCore()
        {
            string path = BenchFonts.CjkPath;
            if (path == null)
            {
                Debug.Log("[raster] no system CJK font on this machine");
                return;
            }

            using var font = FontData.Load(System.IO.File.ReadAllBytes(path));
            var glyphs = new List<uint>();
            using (var shaper = new Shaper())
            {
                var shaped = new List<ShapedGlyph>();
                for (int cp = 0xAC00; cp < 0xAC00 + 400; cp++)
                {
                    shaped.Clear();
                    shaper.Shape(font, char.ConvertFromUtf32(cp), shaped);
                    foreach (var glyph in shaped) glyphs.Add(glyph.GlyphId);
                }
            }

            // Burst compiles asynchronously in the editor: the first hundreds of
            // calls run the managed fallback and are worthless as measurements.
            // Warm every code path before timing anything, then alternate the
            // modes so any remaining drift hits both equally.
            foreach (int warmPpem in new[] { 24, 48 })
            {
                foreach (bool cull in new[] { true, false })
                {
                    GlyphRasterizer.Cull = cull;
                    foreach (uint gid in glyphs)
                        GlyphRasterizer.Rasterize(font, gid, warmPpem / (float)font.UnitsPerEm);
                }
            }

            foreach (int ppem in new[] { 18, 24, 36, 48, 64 })
            {
                float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
                double scheduled = 0, inline = 0;
                long pixels = 0;
                const int passes = 2;
                for (int pass = 0; pass < passes; pass++)
                {
                    scheduled += Time(() =>
                    {
                        GlyphRasterizer.Cull = true;
                        foreach (uint gid in glyphs) GlyphRasterizer.Rasterize(font, gid, pixelsPerUnit);
                    });
                    inline += Time(() =>
                    {
                        GlyphRasterizer.Cull = false;
                        long total = 0;
                        foreach (uint gid in glyphs)
                        {
                            var raster = GlyphRasterizer.Rasterize(font, gid, pixelsPerUnit);
                            total += raster.Width * raster.Height;
                        }
                        pixels = total;
                    });
                }

                Debug.Log($"[raster] {ppem}ppem ({pixels / glyphs.Count} px avg): " +
                    $"culled {scheduled * 1000 / (passes * glyphs.Count):F0} us/glyph, " +
                    $"naive {inline * 1000 / (passes * glyphs.Count):F0} us/glyph " +
                    $"({inline / System.Math.Max(scheduled, 1e-6):F1}x)");
            }
            GlyphRasterizer.Cull = true;
        }

        /// <summary>
        /// What the M10 tailorings cost, one rule at a time, on the text they
        /// exist for.
        ///
        /// Layout only (no shaping cache to warm, no atlas, no mesh), because
        /// that is where every one of these rules lives and a compound scenario
        /// would bury a per-character cost under an atlas upload. The first row
        /// is the number that matters to projects that never ship Japanese: the
        /// rules are settings, and a setting nobody turned on should be free.
        ///
        /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
        ///      OneText.Benchmarks.BenchSuite.AsianLayout
        /// </summary>
        private static void AsianLayoutCore()
        {
            string path = BenchFonts.CjkPath;
            if (path == null)
            {
                Debug.Log("[asian] no system CJK font on this machine");
                return;
            }

            const string sentence =
                "彼は「こんにちは」と言った。それから（しばらく）黙っていた。" +
                "窓の外では雨が降っていて、誰も何も言わなかった。" +
                "受付番号ORDER8827でご確認ください。";
            var builder = new System.Text.StringBuilder();
            while (builder.Length < 4000) builder.Append(sentence);
            string corpus = builder.ToString();

            using var font = FontData.Load(File.ReadAllBytes(path));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();

            var cases = new (string Name, Unicode.AsianTypography.Kinsoku Kinsoku,
                bool Compress, bool Gap)[]
            {
                ("every rule off", Unicode.AsianTypography.Kinsoku.Off, false, false),
                ("kinsoku normal", Unicode.AsianTypography.Kinsoku.Normal, false, false),
                ("+ punctuation compression", Unicode.AsianTypography.Kinsoku.Normal, true, false),
                ("+ CJK-Latin spacing", Unicode.AsianTypography.Kinsoku.Normal, true, true),
            };

            double baseline = 0;
            foreach (var (name, kinsoku, compress, gap) in cases)
            {
                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.Wrap = TextWrap.Wrap;
                settings.MaxWidth = 480f;
                settings.Kinsoku = kinsoku;
                settings.PunctuationCompression = compress;
                settings.CjkLatinSpacing = gap;

                // The shaping cache and the ink-bounds cache both fill on the
                // first pass; timing that would measure the font, not the rule.
                engine.Layout(corpus, settings, result);

                const int passes = 8;
                var timed = settings;
                double elapsed = Time(() =>
                {
                    for (int i = 0; i < passes; i++) engine.Layout(corpus, timed, result);
                });
                double perThousand = elapsed / passes / corpus.Length * 1000.0;
                if (baseline == 0) baseline = perThousand;

                Debug.Log($"[asian] {name}: {corpus.Length / (elapsed / passes):F0} chars/ms, " +
                    $"{perThousand * 1000:F0} us/1000 chars " +
                    $"({(perThousand / baseline - 1) * 100:+0.0;-0.0;0.0}% vs baseline), " +
                    $"{result.Lines.Count} lines");
            }
        }

        private static double Time(Action action)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            action();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>The OneText variants under test: two budgets, prewarm on and off.</summary>
        public static IEnumerable<Func<ITextSubject>> Subjects()
        {
            yield return () => new OneTextSubject(
                new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 });
            yield return () => new OneTextSubject(
                new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 }, prewarm: true);
            yield return () => new OneTextSubject(
                new GlyphAtlasSettings { TextureSize = 2048, LayerCount = 4 }, prewarm: true);
        }

        /// <summary>
        /// Every scenario for every subject. Subjects are built fresh per run so
        /// <summary>
        /// Pays the synchronous Burst compile once, before anything is timed.
        ///
        /// Forcing synchronous compilation moves the stall from "somewhere in
        /// the background" to "the first dispatch", and the first dispatch would
        /// otherwise be inside a measured frame, where it lands in the max
        /// column, which is the column these benchmarks exist to report. The
        /// atlas here is thrown away so it cannot warm anything else either.
        /// </summary>
        private static void WarmBurst()
        {
            string path = BenchFonts.CjkPath;
            if (path == null) return;
            using var font = FontData.Load(System.IO.File.ReadAllBytes(path));
            using var atlas = new GlyphAtlas();
            for (int cp = 0xAC00; cp < 0xAC00 + 32; cp++)
            {
                uint glyph = font.NominalGlyph(cp);
                if (glyph != 0) atlas.GetOrAdd(font, glyph, 24f);
            }
        }

        /// one scenario cannot warm the atlas for the next.
        /// </summary>
        public static List<BenchRun> Run(Func<IEnumerable<Func<ITextSubject>>> subjects, int repeats = 3)
        {
            var runs = new List<BenchRun>();
            // A text system that logs once per missing glyph writes millions of
            // lines over a few thousand frames: enough to sink the run, and
            // enough to distort what is being timed.
            var previousFilter = UnityEngine.Debug.unityLogger.filterLogType;
            UnityEngine.Debug.unityLogger.filterLogType = LogType.Assert;
            // Here rather than only at the entry points: the dev project's
            // OneText-vs-TMP suite calls this method directly, and it is the
            // one whose numbers get quoted.
            var previousBurst = BenchScene.ForceBurst();
            WarmBurst();
            try
            {
                foreach (var make in subjects())
                {
                    runs.Add(Repeat(repeats, () => BenchScenarios.ChatStream(make())));
                    runs.Add(Repeat(repeats, () => BenchScenarios.GlobalUi(make())));
                    runs.Add(Repeat(repeats, () => BenchScenarios.WorldSpaceLabels(make())));
                }
            }
            finally
            {
                UnityEngine.Debug.unityLogger.filterLogType = previousFilter;
                BenchScene.RestoreBurst(previousBurst);
            }
            return runs;
        }

        /// <summary>
        /// Runs a scenario several times and keeps the middle one by p99. A
        /// single repetition catches whatever the machine happened to be doing
        /// (an editor GC pause showed up as one 76 ms frame in an otherwise
        /// 0.6 ms run), and a mean would carry that noise into the headline.
        /// </summary>
        private static BenchRun Repeat(int times, Func<BenchRun> once)
        {
            var results = new List<BenchRun>();
            for (int i = 0; i < Math.Max(1, times); i++) results.Add(once());
            results.Sort((a, b) => a.P99.CompareTo(b.P99));
            var chosen = results[results.Count / 2];
            chosen.Notes = $"{chosen.Notes} (median of {results.Count} runs by p99)";
            return chosen;
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
