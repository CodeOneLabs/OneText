using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OneText.Tests
{
    /// <summary>
    /// What a first paint actually spends inside the glyph pipeline, split into
    /// the part that could move off the frame and the part that cannot.
    ///
    /// Rasterization is the largest single item in a cold rebuild, and the
    /// obvious fix is to stop blocking on the SDF job. But only the block is
    /// recoverable: sizing the tiles, flattening the contours into the job's
    /// buffers and copying the field back out all run on the calling thread
    /// whether or not the job is awaited, and outline extraction runs before the
    /// job exists at all. This test prints the ratio so the deferral work is
    /// budgeted against a measurement rather than against the total.
    ///
    /// It asserts almost nothing; thresholds here would only measure the build
    /// agent. The number in the log is the point.
    /// </summary>
    public class RasterizerCostTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        // CJK is where tiles are largest and the job has the most to do, so it is
        // the case that decides whether deferral is worth its hazards. Not
        // vendored: the coverage fetch downloads it, and macOS has a fallback.
        private static readonly string[] CjkFontPaths =
        {
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKkr-Regular.otf",
            "/System/Library/Fonts/AppleSDGothicNeo.ttc",
        };

        [Test]
        public void Rasterize_Cost_Splits_Into_Recoverable_And_Not()
        {
            // In the editor Burst compiles in the background and the job runs
            // managed until it lands, so an unforced measurement reports the
            // interpreter on a fast machine and Burst on a slow one. Neither is
            // the shipping build. Forcing synchronous compilation costs one
            // stall in the warm-up and makes every number below reproducible.
            bool wasEnabled = Unity.Burst.BurstCompiler.Options.EnableBurstCompilation;
            bool wasSync = Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously;
            Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            try
            {
                Measure();
            }
            finally
            {
                Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = wasEnabled;
                Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = wasSync;
            }
        }

        private static void Measure()
        {
            Debug.Log($"[raster] burst enabled={Unity.Burst.BurstCompiler.Options.EnableBurstCompilation}, " +
                      $"synchronous={Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously}");
            Report("Latin", LatinFontPath, Latin());

            string cjkFont = FirstExisting(CjkFontPaths);
            if (cjkFont == null)
                Debug.Log("[raster] no CJK face on this machine; run Tools/fetch_coverage_fonts.py " +
                          "for the case that matters most");
            else
                Report("CJK", cjkFont, Korean());
        }

        /// <summary>
        /// Bakes every cluster of the text into a cold atlas exactly the way
        /// <c>OneTextLabel</c> does (shape, split, then one
        /// <c>PrepareClusters</c> dispatch per run), and reports where the time
        /// went. The atlas is new for each call so nothing is a cache hit.
        /// </summary>
        private static void Report(string label, string fontPath, string text)
        {
            using var font = FontData.Load(File.ReadAllBytes(FullPath(fontPath)));
            using var shaper = new Shaper();

            var shaped = new List<ShapedGlyph>();
            var positioned = new List<PositionedGlyph>();
            var clusters = new List<GlyphClusters.Cluster>();

            const float size = 32f;
            shaper.Shape(font, text, shaped);
            int ppem = GlyphAtlas.QuantizePixelsPerEm(size);
            GlyphClusters.Split(font, shaped, clusters, positioned,
                1000f * (font.UnitsPerEm / (float)ppem), GlyphClusters.DefaultMergeGapUnits(font));

            // Burst compiles the SDF job on its first dispatch, in the editor,
            // on the thread that asked for it. Measuring that would report the
            // compiler, not the rasterizer, and would inflate the job wait,
            // which is the one number this test exists to produce. Bake the
            // whole workload once into an atlas that is then thrown away.
            using (var warmup = new GlyphAtlas())
                warmup.PrepareClusters(font, size, positioned, clusters);

            using var atlas = new GlyphAtlas();
            bool wasEnabled = AtlasDiagnostics.Enabled;
            AtlasDiagnostics.Reset();
            AtlasDiagnostics.Enabled = true;
            try
            {
                atlas.PrepareClusters(font, size, positioned, clusters);
            }
            finally
            {
                AtlasDiagnostics.Enabled = wasEnabled;
            }

            double outline = AtlasDiagnostics.Ms(AtlasDiagnostics.OutlineTicks);
            double raster = AtlasDiagnostics.Ms(AtlasDiagnostics.RasterizeTicks);
            double jobWait = AtlasDiagnostics.Ms(AtlasDiagnostics.JobWaitTicks);
            double copy = AtlasDiagnostics.Ms(AtlasDiagnostics.CopyTicks);
            double total = outline + raster + copy;

            Debug.Log($"[raster] {label}: {clusters.Count} clusters, " +
                      $"{AtlasDiagnostics.RasterizedPixels:n0} texels, " +
                      $"{AtlasDiagnostics.DispatchCount} dispatch(es)\n" +
                      $"  outline    {outline:F3} ms\n" +
                      $"  rasterize  {raster:F3} ms  of which job wait {jobWait:F3} ms\n" +
                      $"  tile copy  {copy:F3} ms\n" +
                      $"  total      {total:F3} ms; deferral can reach " +
                      $"{(total > 0 ? jobWait / total * 100 : 0):F0}% of it");

            Assert.Greater(AtlasDiagnostics.DispatchCount, 0, "nothing was rasterized");
            Assert.LessOrEqual(jobWait, raster + 1e-6,
                "job wait is counted inside rasterize and cannot exceed it");
        }

        private static string Latin()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 12; i++)
                builder.Append("The quick brown fox jumps over the lazy dog. ");
            return builder.ToString();
        }

        // Distinct syllables: repeating one would bake a single tile and measure
        // the cache instead of the rasterizer.
        private static string Korean()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 300; i++) builder.Append((char)(0xAC00 + i * 7));
            return builder.ToString();
        }

        private static string FirstExisting(string[] paths)
        {
            foreach (string path in paths)
                if (File.Exists(FullPath(path))) return path;
            return null;
        }

        private static string FullPath(string path) =>
            File.Exists(path) ? path : Path.GetFullPath(path);
    }
}
