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

                bool wantDistance = true, wantWinding = true;
                if (Cull)
                {
                    float4 bounds = ContourBounds[c];
                    wantDistance = p.x >= bounds.x - reach && p.x <= bounds.z + reach &&
                                   p.y >= bounds.y - reach && p.y <= bounds.w + reach;
                    wantWinding = p.y >= bounds.y && p.y <= bounds.w;
                    if (!wantDistance && !wantWinding) continue;
                }

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
