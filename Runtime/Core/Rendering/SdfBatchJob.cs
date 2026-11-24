using Unity.Burst;
using Unity.Burst.Intrinsics;
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
    /// the few that could cross it; and of those within reach, each texel
    /// looks only at the ones whose box it can reach in x (a sweep along the
    /// row keeps that set). Each texel then runs the texel job's own tests
    /// and arithmetic on those, unchanged.
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
    [BurstCompile(FloatMode = FloatMode.Default, DisableSafetyChecks = true)]
    internal unsafe struct SdfRowJob : IJobParallelFor
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
            int width = tile.Width;

            float reach = SpreadPixels * tile.UnitsPerPixel;
            // The row's y for choosing candidates only; texels recompute p as
            // the texel job does. The slack covers any rounding between the two.
            float rowY = tile.OriginUnits.y + (py + 0.5f) * tile.UnitsPerPixel;
            float slack = tile.UnitsPerPixel;

            int first = tile.ContourStart;
            int last = tile.ContourStart + tile.ContourCount;
            int segments = 0;
            for (int c = first; c < last; c++) segments += math.max(0, ContourRanges[c].y - 1);
            int segs = math.max(1, segments), contours = math.max(1, tile.ContourCount);

            // Scratch on the stack for every glyph a UI draws; the heap only
            // for the rare tile with thousands of segments.
            const int StackInts = 4096;
            int scratchInts = 11 * segs + 8 * contours + width + 6;
            int* onStack = stackalloc int[StackInts];
            int* scratch = onStack;
            bool heap = scratchInts > StackInts;
            if (heap)
                scratch = (int*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(
                    scratchInts * sizeof(int), 16, Allocator.Temp);
            float4* box = (float4*)scratch;      // per near candidate: segment box
            int* seg = (int*)(box + segs);       // its first point
            int* runOf = seg + segs;             // its run
            int* fromX = runOf + segs;           // texels whose x it can reach
            int* toX = fromX + segs;
            int* order = toX + segs;             // candidates by fromX
            int* active = order + segs;
            int* cross = active + segs;          // segments that could cross the row
            int4* runs = (int4*)(cross + segs);  // per run: -, -, cross range
            float4* minSq4 = (float4*)(runs + contours); // per run, four lanes
            int* starts = (int*)(minSq4 + contours); // width + 2 counters

            int nearCount = 0, crossCount = 0, runCount = 0;
            int currentGroup = tile.ContourCount > 0 ? ContourGroups[first] : 0;
            int runCross = 0;
            float invUpp = 1f / tile.UnitsPerPixel;

            for (int c = first; c < last; c++)
            {
                if (ContourGroups[c] != currentGroup)
                {
                    runs[runCount++] = new int4(0, 0, runCross, crossCount);
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
                    float2 a = Points[start + i], b = Points[start + i + 1];
                    float loY = math.min(a.y, b.y), hiY = math.max(a.y, b.y);
                    if (wantDistance && rowY >= loY - reach - slack && rowY <= hiY + reach + slack)
                    {
                        float loX = math.min(a.x, b.x), hiX = math.max(a.x, b.x);
                        // Texels whose centre could be within reach of the box
                        // in x, a texel of slack either side.
                        int lo = (int)math.floor((loX - reach - tile.OriginUnits.x) * invUpp - 0.5f) - 1;
                        int hi = (int)math.ceil((hiX + reach - tile.OriginUnits.x) * invUpp - 0.5f) + 1;
                        lo = math.max(lo, 0);
                        hi = math.min(hi, width - 1);
                        if (lo <= hi)
                        {
                            box[nearCount] = new float4(loX, loY, hiX, hiY);
                            seg[nearCount] = start + i;
                            runOf[nearCount] = runCount;
                            fromX[nearCount] = lo;
                            toX[nearCount] = hi;
                            nearCount++;
                        }
                    }
                    if (wantWinding && rowY >= loY - slack && rowY <= hiY + slack)
                        cross[crossCount++] = start + i;
                }
            }
            runs[runCount++] = new int4(0, 0, runCross, crossCount);

            // Counting sort by the first texel each candidate reaches.
            for (int x = 0; x <= width + 1; x++) starts[x] = 0;
            for (int k = 0; k < nearCount; k++) starts[fromX[k] + 1]++;
            for (int x = 1; x <= width + 1; x++) starts[x] += starts[x - 1];
            for (int k = 0; k < nearCount; k++) order[starts[fromX[k]]++] = k;
            // starts[x] is now the end of x's block; walk them in order.

            // What the texel job's group with nothing in reach folds to.
            float empty = FoldGroup(float.MaxValue, 0);
            int outputRow = tile.OutputStart + py * width;
            if (Arm.Neon.IsNeonSupported) TexelsNeon(tile, py, width, reach, empty, outputRow, nearCount, runCount,
                    box, seg, runOf, fromX, toX, order, active, cross, runs, minSq4);
            else TexelsScalar(tile, py, width, reach, empty, outputRow, nearCount, runCount,
                    box, seg, runOf, fromX, toX, order, active, cross, runs, minSq4);

            if (heap) Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(scratch, Allocator.Temp);
        }

        /// <summary>
        /// The texels of one row, one at a time, in the texel job's own
        /// expressions: what runs where there is no Neon (managed code with
        /// Burst off, where both jobs are Mono's arithmetic).
        /// </summary>
        private void TexelsScalar(in SdfTileDesc tile, int py, int width, float reach, float empty, int outputRow,
            int nearCount, int runCount, float4* box, int* seg, int* runOf, int* fromX, int* toX, int* order,
            int* active, int* cross, int4* runs, float4* minSq4)
        {
            float* minSq = (float*)minSq4;
            int next = 0, activeCount = 0;
            for (int px = 0; px < width; px++)
            {
                float2 p = tile.OriginUnits + new float2(px + 0.5f, py + 0.5f) * tile.UnitsPerPixel;
                while (next < nearCount && fromX[order[next]] <= px) active[activeCount++] = order[next++];
                for (int r = 0; r < runCount; r++) minSq[r] = float.MaxValue;

                for (int j = 0; j < activeCount; j++)
                {
                    int k = active[j];
                    if (toX[k] < px)
                    {
                        active[j--] = active[--activeCount];
                        continue;
                    }
                    float4 bb = box[k];
                    // The texel job's test, on the same min and max.
                    bool close = p.x >= bb.x - reach && p.x <= bb.z + reach &&
                                 p.y >= bb.y - reach && p.y <= bb.w + reach;
                    if (!close) continue;
                    int run = runOf[k];
                    minSq[run] = math.min(minSq[run], DistanceSq(p, seg[k]));
                }

                float best = empty;
                for (int r = 0; r < runCount; r++)
                {
                    var range = runs[r];
                    int wind = 0;
                    for (int k = range.z; k < range.w; k++)
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
                                if (ab.x * ap.y - ab.y * ap.x > 0f) wind++;
                            }
                        }
                        else if (b.y <= p.y)
                        {
                            float2 ab = b - a;
                            float2 ap = p - a;
                            if (ab.x * ap.y - ab.y * ap.x < 0f) wind--;
                        }
                    }
                    best = math.min(best, FoldGroup(minSq[r], wind));
                }

                float sdPixels = best / tile.UnitsPerPixel;
                float v = math.saturate(0.5f - sdPixels / (2f * SpreadPixels));
                Output[outputRow + px] = (byte)math.round(v * 255f);
            }

        }

        /// <summary>
        /// The texels of one row, four at a time, with every floating-point
        /// operation the texel job's compiled code performs, in its order and
        /// with its fusion: this job is compiled without fast-math, so nothing
        /// is contracted except where an intrinsic says so, and the texel job's
        /// two fused operations (the texel centre, <c>ap - ab * t</c>) are
        /// FMLA/FMLS here too. Its dot products are a multiply and an add
        /// (FMUL + FADDP there), its final scaling divides by
        /// <c>UnitsPerPixel * 2 * Spread</c> (fast-math reassociated the two
        /// divisions), and its rounding is to nearest even.
        /// </summary>
        private void TexelsNeon(in SdfTileDesc tile, int py, int width, float reach, float empty, int outputRow,
            int nearCount, int runCount, float4* box, int* seg, int* runOf, int* fromX, int* toX, int* order,
            int* active, int* cross, int4* runs, float4* minSq4)
        {
            if (!Arm.Neon.IsNeonSupported) return;
            float upp = tile.UnitsPerPixel;
            var upp4 = new v128(upp);
            // p = origin + (px + 0.5, py + 0.5) * upp, fused.
            float pyc = Arm.Neon.vfmaq_f32(new v128(tile.OriginUnits.y), new v128(py + 0.5f), upp4).Float0;
            var originX = new v128(tile.OriginUnits.x);
            float scale = upp * (SpreadPixels + SpreadPixels);
            int next = 0, activeCount = 0;
            for (int px0 = 0; px0 < width; px0 += 4)
            {
                var centres = new v128(px0 + 0.5f, px0 + 1.5f, px0 + 2.5f, px0 + 3.5f);
                v128 pxv = Arm.Neon.vfmaq_f32(originX, centres, upp4);
                float4 pxs = new float4(pxv.Float0, pxv.Float1, pxv.Float2, pxv.Float3);
                int lastLane = math.min(px0 + 3, width - 1);
                while (next < nearCount && fromX[order[next]] <= lastLane) active[activeCount++] = order[next++];
                for (int r = 0; r < runCount; r++) minSq4[r] = new float4(float.MaxValue);

                for (int j = 0; j < activeCount; j++)
                {
                    int k = active[j];
                    if (toX[k] < px0)
                    {
                        active[j--] = active[--activeCount];
                        continue;
                    }
                    float4 bb = box[k];
                    if (!(pyc >= bb.y - reach && pyc <= bb.w + reach)) continue;
                    bool4 close = pxs >= bb.x - reach & pxs <= bb.z + reach;
                    if (!math.any(close)) continue;
                    int s = seg[k];
                    float2 a = Points[s];
                    float2 b = Points[s + 1];
                    float abx = b.x - a.x, aby = b.y - a.y;
                    float abab = abx * abx + aby * aby;
                    float den = math.max(abab, 1e-12f);
                    float4 apx = pxs - a.x;
                    float apy = pyc - a.y;
                    float4 apab = apx * abx + new float4(apy * aby);
                    float4 t = math.saturate(apab / den);
                    v128 tv = new v128(t.x, t.y, t.z, t.w);
                    v128 dxv = Arm.Neon.vfmsq_f32(new v128(apx.x, apx.y, apx.z, apx.w), new v128(abx), tv);
                    v128 dyv = Arm.Neon.vfmsq_f32(new v128(apy), new v128(aby), tv);
                    float4 dx = new float4(dxv.Float0, dxv.Float1, dxv.Float2, dxv.Float3);
                    float4 dy = new float4(dyv.Float0, dyv.Float1, dyv.Float2, dyv.Float3);
                    float4 dSq = dx * dx + dy * dy;
                    int run = runOf[k];
                    minSq4[run] = math.select(minSq4[run], math.min(minSq4[run], dSq), close);
                }

                // Winding, four lanes at once: whether a segment crosses the
                // row depends on y alone, and the texel job's sign test is a
                // comparison of two products (it compiled x - y > 0 to x > y).
                float4 best4 = new float4(empty);
                for (int r = 0; r < runCount; r++)
                {
                    var range = runs[r];
                    int4 wind = int4.zero;
                    for (int k = range.z; k < range.w; k++)
                    {
                        int s = cross[k];
                        float2 a = Points[s];
                        float2 b = Points[s + 1];
                        float abx = b.x - a.x, aby = b.y - a.y;
                        float up = abx * (pyc - a.y);
                        float4 right = aby * (pxs - a.x);
                        if (a.y <= pyc)
                        {
                            if (b.y > pyc) wind += math.select(int4.zero, new int4(1), up > right);
                        }
                        else if (b.y <= pyc)
                        {
                            wind -= math.select(int4.zero, new int4(1), up < right);
                        }
                    }
                    float4 sd = math.sqrt(minSq4[r]);
                    best4 = math.min(best4, math.select(sd, -sd, wind != 0));
                }

                for (int lane = 0; lane < 4 && px0 + lane < width; lane++)
                {
                    int px = px0 + lane;
                    float best = best4[lane];
                    float v = math.saturate(0.5f - best / scale);
                    Output[outputRow + px] = (byte)math.round(v * 255f);
                }
            }
        }

        /// <summary>The texel job's texel centre, by its expression.</summary>
        private static float2 TexelCenter(in SdfTileDesc tile, int px, int py) =>
            tile.OriginUnits + new float2(px + 0.5f, py + 0.5f) * tile.UnitsPerPixel;

        /// <summary>The texel job's squared distance from p to segment s, s + 1.</summary>
        private float DistanceSq(float2 p, int s)
        {
            float2 a = Points[s];
            float2 b = Points[s + 1];
            float2 ab = b - a;
            float2 ap = p - a;
            float t = math.saturate(math.dot(ap, ab) / math.max(math.dot(ab, ab), 1e-12f));
            float2 d = ap - ab * t;
            return math.dot(d, d);
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
