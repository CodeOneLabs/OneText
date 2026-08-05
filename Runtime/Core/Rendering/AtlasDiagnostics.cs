using System.Diagnostics;

namespace OneText
{
    /// <summary>
    /// Where the time inside a text rebuild actually goes.
    ///
    /// Mesh building, outline extraction, SDF rasterization and the atlas
    /// upload are four different costs with four different fixes, and a single
    /// millisecond figure hides which one is the problem. Off by default: the
    /// counters cost a timestamp per call, which is small but not free.
    /// </summary>
    public static class AtlasDiagnostics
    {
        public static bool Enabled;

        public static long RebuildTicks, RebuildCount;
        public static long LayoutTicks, LayoutCount;
        public static long SplitTicks, LookupTicks, EmitTicks;

        /// <summary>
        /// The layout engine shapes each item twice: once in BuildItems to fill
        /// per-character advances for the wrapper, and once in ShapeRun to
        /// produce the glyphs that are drawn. Counted apart because the first
        /// pass is the one a reuse fix would remove, and sizing that fix off the
        /// combined figure would credit it with the work it cannot avoid.
        /// </summary>
        public static long MeasureShapeTicks, MeasureShapeCount;
        public static long EmitShapeTicks, EmitShapeCount;

        /// <summary>
        /// The layout front-end, stage by stage. A three-character nameplate was
        /// measured at 7.2 us to lay out, of which shaping is 1.4 — the rest is
        /// spread across break analysis, segmentation, itemization, wrapping and
        /// alignment, each of which is a fixed per-call cost that a short string
        /// cannot amortise. Which one is biggest is not guessable from reading
        /// the code; three attempts at it were wrong before these existed.
        /// </summary>
        public static long BreakTicks, SegmentTicks, ItemizeTicks, WrapTicks, AlignTicks;
        public static long LayoutBytes, MeshBytes;

        /// <summary>
        /// Managed heap right now. Only meaningful summed over many frames —
        /// a collection inside the window hides allocations — but that is
        /// enough to say which half of a rebuild is doing the allocating.
        /// </summary>
        public static long Heap => Enabled ? System.GC.GetTotalMemory(false) : 0L;
        public static long OutlineTicks, OutlineCount;
        public static long RasterizeTicks, RasterizeCount, RasterizedPixels, DispatchCount;

        /// <summary>
        /// Of <see cref="RasterizeTicks"/>, the wall time inside
        /// Schedule().Complete().
        ///
        /// This is NOT scheduling overhead, and reading it as overhead has
        /// already cost one wrong fix: it is almost entirely the distance-field
        /// work itself, done on worker threads, which Complete() is waiting for.
        /// Replacing the schedule with an inline Run() on small batches — on the
        /// theory that 85 us blocked against three tiles had to be mostly
        /// wake-up — doubled the frame time of the workload it was aimed at,
        /// because what it actually removed was the parallelism.
        ///
        /// What it is good for is the ratio against the rest of RasterizeTicks,
        /// which is real calling-thread work: tile sizing, contour flattening
        /// and the copy back out.
        /// </summary>
        public static long JobWaitTicks;
        public static long UploadTicks, UploadCount, UploadedTiles;
        public static long CopyTicks;

        public static long Now => Enabled ? Stopwatch.GetTimestamp() : 0L;

        public static void Add(ref long accumulator, long startedAt)
        {
            if (!Enabled) return;
            accumulator += Stopwatch.GetTimestamp() - startedAt;
        }

        public static void Reset()
        {
            RebuildTicks = RebuildCount = 0;
            LayoutTicks = LayoutCount = 0;
            SplitTicks = LookupTicks = EmitTicks = 0;
            MeasureShapeTicks = MeasureShapeCount = 0;
            EmitShapeTicks = EmitShapeCount = 0;
            BreakTicks = SegmentTicks = ItemizeTicks = WrapTicks = AlignTicks = 0;
            LayoutBytes = MeshBytes = 0;
            OutlineTicks = OutlineCount = 0;
            RasterizeTicks = RasterizeCount = RasterizedPixels = DispatchCount = 0;
            JobWaitTicks = 0;
            UploadTicks = UploadCount = UploadedTiles = 0;
            CopyTicks = 0;
        }

        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Summarises everything counted so far, then zeroes it, so what follows
        /// is measured on its own.
        ///
        /// A benchmark that prewarms an atlas and fills a window before its
        /// timed loop has already done most of the session's glyph work by the
        /// time the first frame is sampled. Counting that into the same totals
        /// and then dividing by the frame count reports it as a per-frame cost
        /// it never was: it read this project's C3 scenario as spending 137 us
        /// a frame on rasterization that in steady state is nearer zero, and
        /// sent a day of optimisation at the wrong subsystem. Call this at the
        /// boundary between setting a scenario up and measuring it.
        /// </summary>
        public static string TakeSetupSummary()
        {
            if (!Enabled) return null;
            string summary =
                $"setup before the timed loop: {RebuildCount:n0} rebuilds, " +
                $"layout+shape {Ms(LayoutTicks):F1} ms, " +
                $"outline {Ms(OutlineTicks):F1} ms ({OutlineCount:n0} glyphs), " +
                $"rasterize {Ms(RasterizeTicks):F1} ms ({RasterizeCount:n0} tiles in " +
                $"{DispatchCount:n0} dispatches, {Ms(JobWaitTicks):F1} ms blocked), " +
                $"tile copy {Ms(CopyTicks):F1} ms, upload {Ms(UploadTicks):F1} ms";
            Reset();
            return summary;
        }

        public static string Report(int frames)
        {
            double perFrame(long ticks) => frames > 0 ? Ms(ticks) / frames : 0;
            return
                $"rebuilds {RebuildCount:n0} ({(frames > 0 ? RebuildCount / (double)frames : 0):F1} labels/frame, " +
                $"{Ms(RebuildTicks):F1} ms, {perFrame(RebuildTicks) * 1000:F0} us/frame); " +
                $"layout+shape {Ms(LayoutTicks):F1} ms ({perFrame(LayoutTicks) * 1000:F0} us/frame, " +
                $"{(LayoutCount > 0 ? Ms(LayoutTicks) * 1000 / LayoutCount : 0):F0} us/call); " +
                $"shape measure {perFrame(MeasureShapeTicks) * 1000:F0} us/frame " +
                $"({MeasureShapeCount:n0} calls, " +
                $"{(MeasureShapeCount > 0 ? Ms(MeasureShapeTicks) * 1000 / MeasureShapeCount : 0):F2} us/call), " +
                $"shape emit {perFrame(EmitShapeTicks) * 1000:F0} us/frame " +
                $"({EmitShapeCount:n0} calls, " +
                $"{(EmitShapeCount > 0 ? Ms(EmitShapeTicks) * 1000 / EmitShapeCount : 0):F2} us/call); " +
                $"break {perFrame(BreakTicks) * 1000:F0} us/frame, " +
                $"segment {perFrame(SegmentTicks) * 1000:F0} us/frame, " +
                $"itemize {perFrame(ItemizeTicks) * 1000:F0} us/frame, " +
                $"wrap {perFrame(WrapTicks) * 1000:F0} us/frame, " +
                $"align {perFrame(AlignTicks) * 1000:F0} us/frame; " +
                $"split {perFrame(SplitTicks) * 1000:F0} us/frame, " +
                $"lookup {perFrame(LookupTicks) * 1000:F0} us/frame, " +
                $"emit {perFrame(EmitTicks) * 1000:F0} us/frame; " +
                $"alloc layout {LayoutBytes / 1024:n0} KB, mesh {MeshBytes / 1024:n0} KB; " +
                $"outline {Ms(OutlineTicks):F1} ms total ({perFrame(OutlineTicks) * 1000:F0} us/frame, " +
                $"{OutlineCount:n0} glyphs); " +
                $"rasterize {Ms(RasterizeTicks):F1} ms ({perFrame(RasterizeTicks) * 1000:F0} us/frame, " +
                $"{RasterizeCount:n0} tiles, {RasterizedPixels / 1024:n0} kpx, " +
                $"{(RasterizeCount > 0 ? Ms(RasterizeTicks) * 1000 / RasterizeCount : 0):F0} us/tile, " +
                $"{DispatchCount:n0} dispatches of " +
                $"{(DispatchCount > 0 ? RasterizeCount / (double)DispatchCount : 0):F1} tiles, " +
                $"{Ms(JobWaitTicks):F1} ms blocked on the job); " +
                $"tile copy {Ms(CopyTicks):F1} ms ({perFrame(CopyTicks) * 1000:F0} us/frame); " +
                $"upload {Ms(UploadTicks):F1} ms ({perFrame(UploadTicks) * 1000:F0} us/frame, " +
                $"{UploadCount:n0} uploads, {UploadedTiles:n0} tiles)";
        }
    }
}
