using System;
using System.Collections.Generic;
using System.Threading;
using OneText.Native;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// What a flattened glyph comes to, before it is moved into a cluster: the
    /// bounds of the contours the bake keeps (two points or more), and how many
    /// points and contours those are. Enough to size a tile, place it, and lay
    /// out the rasterizer's buffers for it, without the outline.
    /// </summary>
    internal struct GlyphFlatStats
    {
        public float4 Bounds; // min x, min y, max x, max y
        public int Points, Contours;
    }

    /// <summary>
    /// One glyph a job draws, flattens and moves into a batch's buffers: what
    /// <see cref="OutlineExtractor.AppendFlat(FontData, uint, float, Vector2, int, FlatContours)"/>
    /// does on the main thread, with the place it writes to decided in advance.
    /// </summary>
    internal struct OutlineItem
    {
        /// <summary>The font's per-thread handles (<see cref="OutlineJobs.HandlesFor"/>), indexed by job thread.</summary>
        public IntPtr Fonts;
        public uint Glyph;
        public float PixelsPerUnit, Flatness;
        public float2 Offset;
        public int Group, PointStart, ContourStart, Points, Contours;

        /// <summary>The glyph's <see cref="GlyphFlatStats.Bounds"/>, which its tile was sized from.</summary>
        public float4 RawBounds;
    }

    /// <summary>
    /// Glyphs of a frame's misses that are placed but not yet drawn: what the
    /// atlas builds a request from when it knows every glyph's
    /// <see cref="GlyphFlatStats"/> already (see <see cref="GlyphAtlas.WorkerOutlines"/>).
    /// One per atlas, like <see cref="FlatContours"/>, and cleared with it.
    /// </summary>
    internal sealed class LazyGlyphs
    {
        public FontData[] Fonts = new FontData[256];
        public int[] Generations = new int[256];
        public uint[] Glyphs = new uint[256];
        public float[] PixelsPerUnit = new float[256];
        public float[] Flatness = new float[256];
        public float2[] Offsets = new float2[256];
        public int[] Groups = new int[256];
        public GlyphFlatStats[] Stats = new GlyphFlatStats[256];

        /// <summary>Per glyph: its kept contours' bounds, moved by its offset.</summary>
        public float4[] Bounds = new float4[256];
        public int Count;

        // The fonts these glyphs come from, so a font about to change its
        // outlines (or go away) can tell whether anything here still needs it.
        private readonly List<int> _fontIds = new List<int>();

        public void Add(FontData font, uint glyph, float pixelsPerUnit, float flatness, Vector2 offset, int group,
            in GlyphFlatStats stats)
        {
            if (Count == Glyphs.Length)
            {
                int size = Count * 2;
                Array.Resize(ref Fonts, size);
                Array.Resize(ref Generations, size);
                Array.Resize(ref Glyphs, size);
                Array.Resize(ref PixelsPerUnit, size);
                Array.Resize(ref Flatness, size);
                Array.Resize(ref Offsets, size);
                Array.Resize(ref Groups, size);
                Array.Resize(ref Stats, size);
                Array.Resize(ref Bounds, size);
            }
            int i = Count++;
            Fonts[i] = font;
            Generations[i] = font.Generation;
            Glyphs[i] = glyph;
            PixelsPerUnit[i] = pixelsPerUnit;
            Flatness[i] = flatness;
            Offsets[i] = new float2(offset.x, offset.y);
            Groups[i] = group;
            Stats[i] = stats;
            if (stats.Contours > 0)
            {
                NativeOutline.OffsetBounds(stats.Bounds, offset.x, offset.y, out var moved);
                Bounds[i] = moved;
            }
            int id = font.CacheId;
            if (_fontIds.Count == 0 || _fontIds[_fontIds.Count - 1] != id)
                if (!_fontIds.Contains(id)) _fontIds.Add(id);
        }

        /// <summary>Drops glyphs from <paramref name="count"/> on (a cluster that turned out not to qualify).</summary>
        public void Truncate(int count)
        {
            for (int i = count; i < Count; i++) Fonts[i] = null;
            Count = count;
        }

        public bool Uses(int fontId) => Count > 0 && _fontIds.Contains(fontId);

        public void Clear()
        {
            // Released, so a font nobody else holds can be collected.
            Array.Clear(Fonts, 0, Count);
            Count = 0;
            _fontIds.Clear();
        }
    }

    /// <summary>
    /// Outline extraction on the job workers (<see cref="GlyphAtlas.WorkerOutlines"/>):
    /// the per-glyph statistics that let a miss be placed before its outline
    /// exists, the per-thread HarfBuzz handles the jobs draw with, and the job.
    ///
    /// <para><b>What is shared between threads and what is not.</b> Shared,
    /// and only read: the <c>hb_face_t</c> (made immutable when the font is
    /// loaded; HarfBuzz allows any number of readers of an immutable face and
    /// loads its tables lazily with atomics), the blob under it, the
    /// <c>hb_draw_funcs_t</c> (built once, published with a volatile write,
    /// never modified), and the Burst-compiled recorders it points at, which
    /// keep no state of their own: each call writes into the buffer passed as
    /// its draw data. Per thread: one <c>hb_font_t</c> per font per job
    /// thread index (an <c>hb_font_t</c> carries variation coordinates and
    /// caches, and is not ours to share), and the scratch the recording and
    /// flattening use. Main thread only: everything in this class but the
    /// job, the atlases, and the rasterizer's buffer sets.</para>
    ///
    /// <para><b>Why a font cannot change or go away under a job.</b>
    /// <see cref="FontData.SetVariations"/> and <see cref="FontData.Dispose"/>
    /// call <see cref="OnOutlinesChanging"/> before they touch a native
    /// object. On the main thread that finishes every bake still waiting on
    /// the font — every batch in flight and every placed glyph not yet
    /// dispatched, so no tile is drawn with axes it was not placed with — and
    /// only then destroys the font's per-thread handles. A batch holds the
    /// <see cref="FontData"/> itself (through <see cref="LazyGlyphs"/>) until
    /// it is collected, so the finalizer cannot run while one is waiting
    /// either. Disposing a font from another thread while the main thread
    /// still draws with it is not supported (nor was it before: the main
    /// thread may be shaping with it).</para>
    /// </summary>
    internal static unsafe class OutlineJobs
    {
        // ------------------------------------------------------------ stats

        private readonly struct StatsKey : IEquatable<StatsKey>
        {
            private readonly int _font, _ppem, _generation, _outline;
            private readonly uint _glyph;

            public StatsKey(FontData font, uint glyph, int ppem)
            {
                _font = font.CacheId;
                _glyph = glyph;
                _ppem = ppem;
                _generation = font.Generation;
                _outline = OutlineExtractor.Generation;
            }

            public bool Equals(StatsKey o) => _font == o._font && _glyph == o._glyph && _ppem == o._ppem &&
                _generation == o._generation && _outline == o._outline;

            public override bool Equals(object obj) => obj is StatsKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    ulong h = _glyph * 0x9E3779B97F4A7C15UL;
                    h ^= (ulong)(uint)_font * 0xC2B2AE3D27D4EB4FUL;
                    h ^= (ulong)(uint)(_ppem ^ (_generation << 9) ^ (_outline << 20)) * 0x165667B19E3779F9UL;
                    return (int)(h >> 32) ^ (int)h;
                }
            }
        }

        // Every glyph flattened so far, at the density it was flattened at.
        // About 60 bytes an entry with the dictionary's own, so 2 MB at most;
        // bounded, and simply emptied when full: a glyph it forgot is drawn on
        // the main thread once more and remembered again, which is what every
        // glyph's first bake does anyway. A CJK game at two sizes is 16,000.
        private static readonly Dictionary<StatsKey, GlyphFlatStats> s_stats =
            new Dictionary<StatsKey, GlyphFlatStats>(4096);

        internal const int MaxStats = 1 << 15;

        internal static bool TryGetStats(FontData font, uint glyph, int ppem, out GlyphFlatStats stats) =>
            s_stats.TryGetValue(new StatsKey(font, glyph, ppem), out stats);

        internal static void RecordStats(FontData font, uint glyph, int ppem, in GlyphFlatStats stats)
        {
            if (s_stats.Count >= MaxStats) s_stats.Clear();
            s_stats[new StatsKey(font, glyph, ppem)] = stats;
        }

        /// <summary>Forgets every glyph's statistics. Tests, and a batch whose glyphs did not match them.</summary>
        internal static void ForgetStats() => s_stats.Clear();

        /// <summary>For tests: makes one glyph's statistics wrong, to exercise the recovery.</summary>
        internal static bool CorruptStatsForTests(FontData font, uint glyph, int ppem)
        {
            var key = new StatsKey(font, glyph, ppem);
            if (!s_stats.TryGetValue(key, out var stats)) return false;
            stats.Points += 1;
            s_stats[key] = stats;
            return true;
        }

        /// <summary>
        /// Whether a job can draw outlines here: the Burst recorders compiled
        /// (they are what HarfBuzz calls back on a worker), and flattening in
        /// Burst (the job's flattening is that code; where the managed
        /// flattening is the reference, under IL2CPP, the job would not match).
        /// </summary>
        internal static bool Available =>
            OutlineExtractor.NativeCallbacks && OutlineExtractor.NativeFlatten &&
            NativeOutline.DrawFuncs() != IntPtr.Zero;

        // ------------------------------------------------------------ handles

        private sealed class FontHandles
        {
            public IntPtr* Handles;
            public int Count;
            public int Generation;
        }

        private static readonly Dictionary<int, FontHandles> s_handles = new Dictionary<int, FontHandles>();
        private static readonly object s_handleSync = new object();
        private static int s_mainThread = -1;

        // Atlases that may hold glyphs placed with this machinery and not yet
        // drawn, so a font about to change can have them finished first.
        private static readonly List<GlyphAtlas> s_owners = new List<GlyphAtlas>();

        internal static void Register(GlyphAtlas atlas)
        {
            if (s_mainThread < 0) s_mainThread = Thread.CurrentThread.ManagedThreadId;
            if (!s_owners.Contains(atlas)) s_owners.Add(atlas);
            // Once, a static method: no delegate per call.
            if (FontData.OutlinesChanging == null) FontData.OutlinesChanging = OnOutlinesChanging;
        }

        internal static void Unregister(GlyphAtlas atlas) => s_owners.Remove(atlas);

        private static int ThreadSlots => JobsUtility.ThreadIndexCount;

        /// <summary>
        /// The font's per-thread handles, one for every job thread index,
        /// created on first use with the axes the font has now. Main thread.
        /// </summary>
        internal static IntPtr HandlesFor(FontData font)
        {
            lock (s_handleSync)
            {
                if (s_handles.TryGetValue(font.CacheId, out var existing))
                {
                    if (existing.Generation == font.Generation) return (IntPtr)existing.Handles;
                    // Cannot happen: the axes changing destroys them first
                    // (OnOutlinesChanging). Kept honest rather than trusted.
                    s_handles.Remove(font.CacheId);
                    Destroy(existing);
                }
                int slots = ThreadSlots;
                var handles = new FontHandles
                {
                    Count = slots,
                    Generation = font.Generation,
                    Handles = (IntPtr*)UnsafeUtility.Malloc(slots * sizeof(IntPtr), UnsafeUtility.AlignOf<IntPtr>(),
                        Allocator.Persistent),
                };
                for (int i = 0; i < slots; i++) handles.Handles[i] = font.CreateWorkerFont();
                s_handles[font.CacheId] = handles;
                return (IntPtr)handles.Handles;
            }
        }

        private static void Destroy(FontHandles handles)
        {
            for (int i = 0; i < handles.Count; i++)
                if (handles.Handles[i] != IntPtr.Zero) HarfBuzzApi.hb_font_destroy(handles.Handles[i]);
            UnsafeUtility.Free(handles.Handles, Allocator.Persistent);
            handles.Handles = null;
            handles.Count = 0;
        }

        /// <summary>
        /// Called by a font before its axes change or its native objects are
        /// freed. See the class notes for why this is enough.
        /// </summary>
        internal static void OnOutlinesChanging(FontData font)
        {
            int id = font.CacheId;
            if (Thread.CurrentThread.ManagedThreadId == s_mainThread)
            {
                // Glyphs placed with this font's current outlines are drawn with
                // them, now, before anything changes; batches already baking
                // with its handles are collected.
                for (int i = 0; i < s_owners.Count; i++)
                    if (s_owners[i].HoldsWorkerGlyphsOf(id)) s_owners[i].CompletePendingBakes();
                if (GlyphRasterizer.HasFlight && GlyphRasterizer.FlightsUseWorkerOutlines) GlyphRasterizer.FinishFlight();
            }
            FontHandles handles;
            lock (s_handleSync)
            {
                if (!s_handles.TryGetValue(id, out handles)) return;
                s_handles.Remove(id);
            }
            Destroy(handles);
        }

        /// <summary>
        /// Tests: draws <paramref name="glyphs"/> on this thread into
        /// <paramref name="main"/> (what the atlas does for a glyph it has not
        /// seen) and with the job into each of <paramref name="workers"/>, all
        /// of those jobs scheduled before any is waited for, so they run on
        /// several threads at once. Returns how many glyphs a job refused.
        /// </summary>
        internal static int DrawForTests(FontData font, IReadOnlyList<uint> glyphs, IReadOnlyList<Vector2> offsets,
            float pixelsPerUnit, FlatContours main, IReadOnlyList<FlatContours> workers)
        {
            if (s_mainThread < 0) s_mainThread = Thread.CurrentThread.ManagedThreadId;
            if (FontData.OutlinesChanging == null) FontData.OutlinesChanging = OnOutlinesChanging;
            main.Clear();
            int n = glyphs.Count;
            var stats = new GlyphFlatStats[n];
            for (int i = 0; i < n; i++)
                if (!OutlineExtractor.AppendFlat(font, glyphs[i], pixelsPerUnit, offsets[i], i, main, out stats[i]))
                    throw new InvalidOperationException("the native outline path is unavailable");

            var items = new NativeArray<OutlineItem>(Math.Max(n, 1), Allocator.TempJob);
            int pi = 0, ci = 0, count = 0;
            var table = HandlesFor(font);
            for (int i = 0; i < n; i++)
            {
                if (stats[i].Contours == 0) continue;
                items[count++] = new OutlineItem
                {
                    Fonts = table, Glyph = glyphs[i], PixelsPerUnit = pixelsPerUnit,
                    Flatness = OutlineExtractor.CurrentFlatness, Offset = new float2(offsets[i].x, offsets[i].y),
                    Group = i, PointStart = pi, ContourStart = ci, Points = stats[i].Points,
                    Contours = stats[i].Contours, RawBounds = stats[i].Bounds,
                };
                pi += stats[i].Points;
                ci += stats[i].Contours;
            }

            int copies = workers.Count;
            var points = new NativeArray<float2>[copies];
            var ranges = new NativeArray<int2>[copies];
            var groupIds = new NativeArray<int>[copies];
            var bounds = new NativeArray<float4>[copies];
            var kinds = new NativeArray<byte>[copies];
            var failed = new NativeArray<int>[copies];
            var handles = new NativeArray<JobHandle>(copies, Allocator.Temp);
            int refused = 0;
            try
            {
                for (int k = 0; k < copies; k++)
                {
                    points[k] = new NativeArray<float2>(Math.Max(pi, 1), Allocator.TempJob);
                    ranges[k] = new NativeArray<int2>(Math.Max(ci, 1), Allocator.TempJob);
                    groupIds[k] = new NativeArray<int>(Math.Max(ci, 1), Allocator.TempJob);
                    bounds[k] = new NativeArray<float4>(Math.Max(ci, 1), Allocator.TempJob);
                    kinds[k] = new NativeArray<byte>(Math.Max(ci, 1), Allocator.TempJob);
                    failed[k] = new NativeArray<int>(Math.Max(count, 1), Allocator.TempJob);
                    handles[k] = count > 0
                        ? Schedule(items, count, points[k], ranges[k], groupIds[k], bounds[k], kinds[k], failed[k])
                        : default;
                }
                JobHandle.ScheduleBatchedJobs();
                JobHandle.CompleteAll(handles);
                for (int k = 0; k < copies; k++)
                {
                    var into = workers[k];
                    into.Clear();
                    into.Reserve(pi, ci);
                    for (int i = 0; i < count; i++) refused += failed[k][i];
                    for (int i = 0; i < pi; i++) into.Points[i] = points[k][i];
                    for (int c = 0; c < ci; c++)
                    {
                        into.Ranges[c] = ranges[k][c];
                        into.Groups[c] = groupIds[k][c];
                        into.Bounds[c] = bounds[k][c];
                        if (kinds[k][c] != ContourKind.Both) refused++;
                    }
                    into.PointCount = pi;
                    into.ContourCount = ci;
                }
            }
            finally
            {
                for (int k = 0; k < copies; k++)
                {
                    if (points[k].IsCreated) points[k].Dispose();
                    if (ranges[k].IsCreated) ranges[k].Dispose();
                    if (groupIds[k].IsCreated) groupIds[k].Dispose();
                    if (bounds[k].IsCreated) bounds[k].Dispose();
                    if (kinds[k].IsCreated) kinds[k].Dispose();
                    if (failed[k].IsCreated) failed[k].Dispose();
                }
                items.Dispose();
                handles.Dispose();
            }
            return refused;
        }

        /// <summary>Frees every handle and scratch buffer. With the rasterizer's buffers, nothing in flight.</summary>
        internal static void ReleaseAll()
        {
            lock (s_handleSync)
            {
                foreach (var handles in s_handles.Values) Destroy(handles);
                s_handles.Clear();
            }
            if (s_ops.IsCreated) s_ops.Dispose();
            if (s_points.IsCreated) s_points.Dispose();
            if (s_starts.IsCreated) s_starts.Dispose();
            s_ops = default;
            s_points = default;
            s_starts = default;
        }

        // ------------------------------------------------------------ the job

        // Per job thread: room for one glyph's recording, its flattened points
        // and its contour starts (about 25 KB a thread; a CJK glyph at UI sizes
        // records ~1,000 floats and flattens to a few hundred points). A glyph
        // that needs more takes temporary memory for itself inside the job.
        internal const int OpsPerThread = 2048, PointsPerThread = 2048, StartsPerThread = 256;

        private static NativeArray<float> s_ops;
        private static NativeArray<float2> s_points;
        private static NativeArray<int> s_starts;

        /// <summary>
        /// Schedules the drawing of <paramref name="count"/> items into a
        /// batch's buffers. Main thread; the buffers are the batch's own.
        /// </summary>
        internal static JobHandle Schedule(NativeArray<OutlineItem> items, int count,
            NativeArray<float2> points, NativeArray<int2> ranges, NativeArray<int> groups,
            NativeArray<float4> bounds, NativeArray<byte> kinds, NativeArray<int> failed)
        {
            int slots = ThreadSlots;
            if (!s_ops.IsCreated)
            {
                s_ops = new NativeArray<float>(slots * OpsPerThread, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                s_points = new NativeArray<float2>(slots * PointsPerThread, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                s_starts = new NativeArray<int>(slots * StartsPerThread, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            return new OutlineJob
            {
                Items = items,
                DrawFuncs = NativeOutline.DrawFuncs(),
                Ops = (float*)s_ops.GetUnsafePtr(),
                Scratch = (float2*)s_points.GetUnsafePtr(),
                Starts = (int*)s_starts.GetUnsafePtr(),
                Points = (float2*)points.GetUnsafePtr(),
                Ranges = (int2*)ranges.GetUnsafePtr(),
                Groups = (int*)groups.GetUnsafePtr(),
                Bounds = (float4*)bounds.GetUnsafePtr(),
                Kinds = (byte*)kinds.GetUnsafePtr(),
                Failed = failed,
            }.Schedule(count, 1);
        }
    }

    /// <summary>
    /// Draws, flattens and moves one glyph per index, exactly as the main
    /// thread's <see cref="OutlineExtractor.AppendFlat(FontData, uint, float, Vector2, int, FlatContours)"/>
    /// does: the same recorders, the same Burst flattening
    /// (<see cref="NativeOutline.Flatten"/>), the same move
    /// (<see cref="NativeOutline.Offset"/>), compiled with the same float mode.
    /// Each item writes only the points and contours it was given, after
    /// checking it came to exactly as many as its statistics said; one that
    /// did not writes nothing and says so in <see cref="Failed"/>, and the
    /// batch is baked again from the main thread (<c>GlyphRasterizer.EndBatch</c>).
    ///
    /// The buffers are raw pointers rather than arrays: every item writes a
    /// disjoint range of them, which the safety system cannot be told, and the
    /// rasterizer's jobs that read them depend on this one.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    internal unsafe struct OutlineJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<OutlineItem> Items;
        [NativeDisableUnsafePtrRestriction] public IntPtr DrawFuncs;
        [NativeSetThreadIndex] public int ThreadIndex;

        [NativeDisableUnsafePtrRestriction] public float* Ops;
        [NativeDisableUnsafePtrRestriction] public float2* Scratch;
        [NativeDisableUnsafePtrRestriction] public int* Starts;

        [NativeDisableUnsafePtrRestriction] public float2* Points;
        [NativeDisableUnsafePtrRestriction] public int2* Ranges;
        [NativeDisableUnsafePtrRestriction] public int* Groups;
        [NativeDisableUnsafePtrRestriction] public float4* Bounds;
        [NativeDisableUnsafePtrRestriction] public byte* Kinds;

        [WriteOnly] public NativeArray<int> Failed;

        public void Execute(int index)
        {
            var item = Items[index];
            IntPtr font = ((IntPtr*)item.Fonts)[ThreadIndex];

            float* ops = Ops + (long)ThreadIndex * OutlineJobs.OpsPerThread;
            int opsCapacity = OutlineJobs.OpsPerThread;
            float* ownOps = null;
            int count;
            while (true)
            {
                var state = new NativeOutline.State { Ops = ops, Capacity = opsCapacity };
                HarfBuzzApi.hb_font_draw_glyph(font, item.Glyph, DrawFuncs, (IntPtr)(&state));
                if (state.Overflow == 0)
                {
                    count = state.Count;
                    break;
                }
                // Drawn again into room enough, from the start, as Record does.
                if (ownOps != null) UnsafeUtility.Free(ownOps, Allocator.Temp);
                opsCapacity *= 2;
                ownOps = (float*)UnsafeUtility.Malloc(opsCapacity * sizeof(float), 16, Allocator.Temp);
                ops = ownOps;
            }

            float2* points = Scratch + (long)ThreadIndex * OutlineJobs.PointsPerThread;
            int* starts = Starts + (long)ThreadIndex * OutlineJobs.StartsPerThread;
            int pointCapacity = OutlineJobs.PointsPerThread, startCapacity = OutlineJobs.StartsPerThread;
            float2* ownPoints = null;
            int* ownStarts = null;
            int pointCount, contourCount;
            while (true)
            {
                NativeOutline.Flatten(ops, count, item.PixelsPerUnit, item.Flatness, points, pointCapacity,
                    starts, startCapacity, out pointCount, out contourCount);
                if (pointCount < 0)
                {
                    if (ownPoints != null) UnsafeUtility.Free(ownPoints, Allocator.Temp);
                    pointCapacity *= 2;
                    ownPoints = (float2*)UnsafeUtility.Malloc(pointCapacity * sizeof(float2), 16, Allocator.Temp);
                    points = ownPoints;
                    continue;
                }
                if (contourCount < 0)
                {
                    if (ownStarts != null) UnsafeUtility.Free(ownStarts, Allocator.Temp);
                    startCapacity *= 2;
                    ownStarts = (int*)UnsafeUtility.Malloc(startCapacity * sizeof(int), 16, Allocator.Temp);
                    starts = ownStarts;
                    continue;
                }
                break;
            }

            NativeOutline.RawStats(points, pointCount, starts, contourCount, out var bounds, out int kept, out int keptContours);
            // The tile was sized and placed from the statistics; an outline
            // that does not come to exactly them is not the one it was placed for.
            bool matches = kept == item.Points && keptContours == item.Contours &&
                (keptContours == 0 || math.all(bounds == item.RawBounds));
            if (matches)
            {
                NativeOutline.Offset(points, pointCount, starts, contourCount, item.Offset.x, item.Offset.y, item.Group,
                    Points, item.PointStart, Ranges, Groups, Bounds, item.ContourStart, out _, out _);
                for (int c = item.ContourStart; c < item.ContourStart + item.Contours; c++) Kinds[c] = ContourKind.Both;
            }
            Failed[index] = matches ? 0 : 1;

            if (ownOps != null) UnsafeUtility.Free(ownOps, Allocator.Temp);
            if (ownPoints != null) UnsafeUtility.Free(ownPoints, Allocator.Temp);
            if (ownStarts != null) UnsafeUtility.Free(ownStarts, Allocator.Temp);
        }
    }
}
