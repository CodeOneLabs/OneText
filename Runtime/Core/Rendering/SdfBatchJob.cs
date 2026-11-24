using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace OneText
{
    /// <summary>One tile inside a batch: where its contours, texels and geometry live.</summary>
    internal struct SdfTileDesc
    {
        public int ContourStart, ContourCount;  // into the shared contour arrays
        public int OutputStart;                 // into the shared output buffer
        public int Width, Height;
        public float2 OriginUnits;
        public float UnitsPerPixel;
    }

    /// <summary>
    /// Rasterizes many glyph tiles in a single job.
    ///
    /// One job per glyph was costing far more in fixed overhead than in
    /// distance-field work: at 18 ppem a Hangul tile spent ~132 ns per texel,
    /// while a tile five times larger spent ~34 ns, the difference being the
    /// scheduling and allocation paid once per call either way. Batching a
    /// frame's misses into one dispatch amortizes that across every tile, and
    /// the parallel loop gets enough work to fill the worker threads.
    ///
    /// Texels are indexed globally across the batch; each one finds its tile
    /// with a binary search over the tile offsets, which costs a handful of
    /// comparisons against the tens of segments it is about to evaluate.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    internal struct SdfBatchJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Points;
        [ReadOnly] public NativeArray<int2> ContourRanges;
        [ReadOnly] public NativeArray<int> ContourGroups;
        [ReadOnly] public NativeArray<float4> ContourBounds;

        /// <summary>
        /// <see cref="ContourKind"/> per contour: whether its edges are measured,
        /// counted towards the winding, or both. Both for every contour a font
        /// drew; a glyph whose contours overlap is packed as its original
        /// contours wound-only plus the visible pieces of them measured-only
        /// (see <see cref="ContourOverlaps"/>).
        /// </summary>
        [ReadOnly] public NativeArray<byte> ContourKinds;
        [ReadOnly] public NativeArray<SdfTileDesc> Tiles;
        [ReadOnly] public NativeArray<int> TileEnds;   // exclusive prefix sums of texel counts

        /// <summary>Tiles actually in this batch; the buffers are pooled and may be longer.</summary>
        public int TileCount;

        public float SpreadPixels;
        public bool Cull;

        [WriteOnly] [NativeDisableParallelForRestriction] public NativeArray<byte> Output;

        public void Execute(int index)
        {
            int tileIndex = FindTile(index);
            var tile = Tiles[tileIndex];
            int local = index - (tileIndex == 0 ? 0 : TileEnds[tileIndex - 1]);

            int px = local % tile.Width;
            int py = local / tile.Width;
            float2 p = tile.OriginUnits + new float2(px + 0.5f, py + 0.5f) * tile.UnitsPerPixel;
            float reach = SpreadPixels * tile.UnitsPerPixel;

            // Signed distance per source glyph, unioned at field level: edges
            // buried inside another glyph's ink must not carve into the union.
            float best = float.MaxValue;
            float minSq = float.MaxValue;
            int winding = 0;
            int first = tile.ContourStart;
            int last = tile.ContourStart + tile.ContourCount;
            int currentGroup = tile.ContourCount > 0 ? ContourGroups[first] : 0;

            for (int c = first; c < last; c++)
            {
                if (ContourGroups[c] != currentGroup)
                {
                    best = math.min(best, FoldGroup(minSq, winding));
                    minSq = float.MaxValue;
                    winding = 0;
                    currentGroup = ContourGroups[c];
                }

                int kind = ContourKinds[c];
                bool wantDistance = (kind & ContourKind.Distance) != 0;
                bool wantWinding = (kind & ContourKind.Winding) != 0;
                if (Cull)
                {
                    float4 bounds = ContourBounds[c];
                    wantDistance &= p.x >= bounds.x - reach && p.x <= bounds.z + reach &&
                                    p.y >= bounds.y - reach && p.y <= bounds.w + reach;
                    wantWinding &= p.y >= bounds.y && p.y <= bounds.w;
                }
                if (!wantDistance && !wantWinding) continue;

                int start = ContourRanges[c].x;
                int count = ContourRanges[c].y;
                for (int i = 0; i < count - 1; i++)
                {
                    float2 a = Points[start + i];
                    float2 b = Points[start + i + 1];

                    if (wantDistance)
                    {
                        bool near = !Cull ||
                            (p.x >= math.min(a.x, b.x) - reach && p.x <= math.max(a.x, b.x) + reach &&
                             p.y >= math.min(a.y, b.y) - reach && p.y <= math.max(a.y, b.y) + reach);
                        if (near)
                        {
                            float2 ab = b - a;
                            float2 ap = p - a;
                            float t = math.saturate(math.dot(ap, ab) / math.max(math.dot(ab, ab), 1e-12f));
                            float2 d = ap - ab * t;
                            minSq = math.min(minSq, math.dot(d, d));
                        }
                    }

                    if (!wantWinding) continue;
                    if (a.y <= p.y)
                    {
                        if (b.y > p.y)
                        {
                            float2 ab = b - a;
                            float2 ap = p - a;
                            if (ab.x * ap.y - ab.y * ap.x > 0f) winding++;
                        }
                    }
                    else if (b.y <= p.y)
                    {
                        float2 ab = b - a;
                        float2 ap = p - a;
                        if (ab.x * ap.y - ab.y * ap.x < 0f) winding--;
                    }
                }
            }
            best = math.min(best, FoldGroup(minSq, winding));

            float sdPixels = best / tile.UnitsPerPixel;
            float v = math.saturate(0.5f - sdPixels / (2f * SpreadPixels));
            Output[tile.OutputStart + local] = (byte)math.round(v * 255f);
        }

        private int FindTile(int texel)
        {
            int lo = 0, hi = TileCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (texel < TileEnds[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        private static float FoldGroup(float minSq, int winding)
        {
            float sd = math.sqrt(minSq);
            return winding != 0 ? -sd : sd;
        }
    }
}

namespace OneText
{
    /// <summary>
    /// <see cref="SdfBatchJob"/>, a tile row at a time.
    ///
    /// The texel job walks every contour of its tile, and every segment of
    /// every contour its box does not rule out, for every texel: a CJK glyph
    /// at UI sizes is a hundred-odd segments and the reach is a third of its
    /// height, so nearly all of them, two hundred thousand texels a frame
    /// when a frame bakes two hundred glyphs. A row shares its y, and most of
    /// what the texel loop rejects it rejects on y alone, so the row picks its
    /// candidates once: the segments that could be within reach of it, and
    /// the few that could cross it. Each texel then runs the texel job's own
    /// tests and arithmetic on those, unchanged.
    ///
    /// Same bytes, by construction rather than by luck: the row's filters are
    /// conservative (a texel unit of slack either way), so every segment the
    /// texel job would measure or count is still a candidate; the texel-level
    /// tests that decide whether it is measured or counted are the texel
    /// job's, on the same <c>p</c> from the same expression; a minimum and an
    /// integer sum do not depend on the order they are taken in; and the
    /// per-glyph fold happens at the same group boundaries. A group with no
    /// candidates folds to what the texel job's empty group folds to.
    /// <c>BakePathTests</c> holds the two jobs to the byte.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    internal struct SdfRowJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Points;
        [ReadOnly] public NativeArray<int2> ContourRanges;
        [ReadOnly] public NativeArray<int> ContourGroups;
        [ReadOnly] public NativeArray<float4> ContourBounds;
        [ReadOnly] public NativeArray<byte> ContourKinds;
        [ReadOnly] public NativeArray<SdfTileDesc> Tiles;

        /// <summary>Exclusive prefix sums of tile heights: row ends.</summary>
        [ReadOnly] public NativeArray<int> RowEnds;

        public int TileCount;
        public float SpreadPixels;

        [WriteOnly] [NativeDisableParallelForRestriction] public NativeArray<byte> Output;

        public void Execute(int row)
        {
            int tileIndex = FindTile(row);
            var tile = Tiles[tileIndex];
            int py = row - (tileIndex == 0 ? 0 : RowEnds[tileIndex - 1]);

            float reach = SpreadPixels * tile.UnitsPerPixel;
            // The row's y for choosing candidates only; texels recompute p as
            // the texel job does. The slack covers any rounding between the two.
            float rowY = tile.OriginUnits.y + (py + 0.5f) * tile.UnitsPerPixel;
            float slack = tile.UnitsPerPixel;

            int first = tile.ContourStart;
            int last = tile.ContourStart + tile.ContourCount;

            // Candidates as segment start indices into Points (b = a + 1),
            // grouped into runs of consecutive contours of one glyph.
            int segments = 0;
            for (int c = first; c < last; c++) segments += math.max(0, ContourRanges[c].y - 1);
            var near = new NativeArray<int>(math.max(1, segments), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var cross = new NativeArray<int>(math.max(1, segments), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var runs = new NativeArray<int4>(math.max(1, tile.ContourCount), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int nearCount = 0, crossCount = 0, runCount = 0;
            int currentGroup = tile.ContourCount > 0 ? ContourGroups[first] : 0;
            int runNear = 0, runCross = 0;

            for (int c = first; c < last; c++)
            {
                if (ContourGroups[c] != currentGroup)
                {
                    runs[runCount++] = new int4(runNear, nearCount, runCross, crossCount);
                    runNear = nearCount;
                    runCross = crossCount;
                    currentGroup = ContourGroups[c];
                }

                int kind = ContourKinds[c];
                bool wantDistance = (kind & ContourKind.Distance) != 0;
                bool wantWinding = (kind & ContourKind.Winding) != 0;
                float4 bounds = ContourBounds[c];
                wantDistance &= rowY >= bounds.y - reach - slack && rowY <= bounds.w + reach + slack;
                wantWinding &= rowY >= bounds.y - slack && rowY <= bounds.w + slack;
                if (!wantDistance && !wantWinding) continue;

                int start = ContourRanges[c].x;
                int count = ContourRanges[c].y;
                for (int i = 0; i < count - 1; i++)
                {
                    float ay = Points[start + i].y, by = Points[start + i + 1].y;
                    float lo = math.min(ay, by), hi = math.max(ay, by);
                    if (wantDistance && rowY >= lo - reach - slack && rowY <= hi + reach + slack)
                        near[nearCount++] = start + i;
                    if (wantWinding && rowY >= lo - slack && rowY <= hi + slack)
                        cross[crossCount++] = start + i;
                }
            }
            runs[runCount++] = new int4(runNear, nearCount, runCross, crossCount);

            // What the texel job's group with nothing in reach folds to.
            float empty = FoldGroup(float.MaxValue, 0);
            int outputRow = tile.OutputStart + py * tile.Width;
            for (int px = 0; px < tile.Width; px++)
            {
                float2 p = tile.OriginUnits + new float2(px + 0.5f, py + 0.5f) * tile.UnitsPerPixel;
                float best = empty;
                for (int r = 0; r < runCount; r++)
                {
                    var run = runs[r];
                    float minSq = float.MaxValue;
                    int winding = 0;
                    for (int k = run.x; k < run.y; k++)
                    {
                        int s = near[k];
                        float2 a = Points[s];
                        float2 b = Points[s + 1];
                        bool close = p.x >= math.min(a.x, b.x) - reach && p.x <= math.max(a.x, b.x) + reach &&
                                     p.y >= math.min(a.y, b.y) - reach && p.y <= math.max(a.y, b.y) + reach;
                        if (!close) continue;
                        float2 ab = b - a;
                        float2 ap = p - a;
                        float t = math.saturate(math.dot(ap, ab) / math.max(math.dot(ab, ab), 1e-12f));
                        float2 d = ap - ab * t;
                        minSq = math.min(minSq, math.dot(d, d));
                    }
                    for (int k = run.z; k < run.w; k++)
                    {
                        int s = cross[k];
                        float2 a = Points[s];
                        float2 b = Points[s + 1];
                        if (a.y <= p.y)
                        {
                            if (b.y > p.y)
                            {
                                float2 ab = b - a;
                                float2 ap = p - a;
                                if (ab.x * ap.y - ab.y * ap.x > 0f) winding++;
                            }
                        }
                        else if (b.y <= p.y)
                        {
                            float2 ab = b - a;
                            float2 ap = p - a;
                            if (ab.x * ap.y - ab.y * ap.x < 0f) winding--;
                        }
                    }
                    best = math.min(best, FoldGroup(minSq, winding));
                }

                float sdPixels = best / tile.UnitsPerPixel;
                float v = math.saturate(0.5f - sdPixels / (2f * SpreadPixels));
                Output[outputRow + px] = (byte)math.round(v * 255f);
            }

            near.Dispose();
            cross.Dispose();
            runs.Dispose();
        }

        private int FindTile(int row)
        {
            int lo = 0, hi = TileCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (row < RowEnds[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        private static float FoldGroup(float minSq, int winding)
        {
            float sd = math.sqrt(minSq);
            return winding != 0 ? -sd : sd;
        }
    }
}
