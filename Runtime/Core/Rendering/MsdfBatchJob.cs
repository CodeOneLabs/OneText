using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace OneText
{
    /// <summary>
    /// The multi-channel counterpart of <see cref="SdfBatchJob"/>: same tiles,
    /// same contours, same batching, four bytes per texel instead of one.
    ///
    /// Each of R, G and B carries the signed distance to the subset of edges
    /// that <see cref="MsdfEdgeColoring"/> gave that channel, and the shader
    /// takes the median of the three. Two edges meeting at a corner share
    /// exactly one channel, so the median is the intersection of their two
    /// half-planes, a corner that survives bilinear reconstruction, where a
    /// single channel stores a cone the sampler rounds off.
    ///
    /// What makes the corner sharp is the <em>pseudo</em>-distance: past the
    /// end of an edge run, the channel keeps measuring against that edge's
    /// extended line rather than against its endpoint. Extension is confined to
    /// run ends (corners), because inside a run the neighbouring segment is the
    /// nearer one anyway and extending there would put creases along a curve
    /// the flattening only bent.
    ///
    /// Alpha is the ordinary single-channel field, which costs nothing extra:
    /// every edge belongs to two channels, so the smallest of the three true
    /// distances is the smallest over all edges. The shader reads it for the
    /// shadow, whose offset sample lands where the three channels disagree and
    /// where the median has no advantage to offer a blurred, displaced copy.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    internal struct MsdfBatchJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Points;

        /// <summary>Channel mask and run-end bits per segment, indexed like <see cref="Points"/>.</summary>
        [ReadOnly] public NativeArray<byte> SegmentFlags;

        [ReadOnly] public NativeArray<int2> ContourRanges;
        [ReadOnly] public NativeArray<int> ContourGroups;
        [ReadOnly] public NativeArray<float4> ContourBounds;

        /// <summary>
        /// +1 where the source glyph's contours wind counter-clockwise, -1
        /// where they wind clockwise; TrueType and CFF disagree, and the sign
        /// of a pseudo-distance is which side of the edge's line the point is
        /// on, so the convention has to be measured rather than assumed.
        /// </summary>
        [ReadOnly] public NativeArray<float> ContourOrientation;

        [ReadOnly] public NativeArray<SdfTileDesc> Tiles;
        [ReadOnly] public NativeArray<int> TileEnds;

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

            // Unioned across source glyphs exactly as the single-channel job
            // does: a group's edges buried inside another glyph's ink must not
            // carve into the union.
            float3 union = float.MaxValue;
            float unionTrue = float.MaxValue;

            float3 nearSq = float.MaxValue;   // winning true distance per channel
            float3 nearDot = float.MaxValue;  // and how obliquely it was met
            float3 value = float.MaxValue;    // that winner's pseudo-distance
            int3 beyond = 0;                  // and whether it is already signed
            float minSq = float.MaxValue;
            int winding = 0;

            int first = tile.ContourStart;
            int last = tile.ContourStart + tile.ContourCount;
            int currentGroup = tile.ContourCount > 0 ? ContourGroups[first] : 0;

            for (int c = first; c < last; c++)
            {
                if (ContourGroups[c] != currentGroup)
                {
                    Fold(ref union, ref unionTrue, value, beyond, minSq, winding);
                    nearSq = float.MaxValue;
                    nearDot = float.MaxValue;
                    value = float.MaxValue;
                    beyond = 0;
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

                float orientation = ContourOrientation[c];
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
                            byte flags = SegmentFlags[start + i];
                            float2 ab = b - a;
                            float2 ap = p - a;
                            float lengthSq = math.max(math.dot(ab, ab), 1e-12f);
                            float raw = math.dot(ap, ab) / lengthSq;
                            float t = math.saturate(raw);
                            float2 delta = ap - ab * t;
                            float distSq = math.dot(delta, delta);
                            minSq = math.min(minSq, distSq);

                            // Every edge of a corner is exactly as far from a
                            // point in the wedge beyond it (the corner itself
                            // is the nearest point on both), so the winner
                            // there is decided by which edge the point faces
                            // more squarely. Without that tie-break the first
                            // edge in the contour takes two channels and the
                            // median reports one half-plane instead of their
                            // intersection: the corner is classified right and
                            // shaded as if it were flat.
                            float oblique = 0f;
                            if (t != raw)
                            {
                                float2 fromEnd = t > 0f ? p - b : ap;
                                float endLengthSq = math.max(math.dot(fromEnd, fromEnd), 1e-12f);
                                oblique = math.abs(math.dot(ab, fromEnd)) *
                                          math.rsqrt(lengthSq * endLengthSq);
                            }

                            float3 slack = nearSq * 1e-5f + 1e-9f;
                            bool3 has = new bool3(
                                (flags & MsdfEdgeColoring.Red) != 0,
                                (flags & MsdfEdgeColoring.Green) != 0,
                                (flags & MsdfEdgeColoring.Blue) != 0);
                            bool3 closer = distSq < nearSq - slack;
                            bool3 wins = has & (closer |
                                (distSq <= nearSq + slack & oblique < nearDot));
                            if (math.any(wins))
                            {
                                // Past a run end the edge keeps measuring
                                // against its own line, and the side of that
                                // line, not the winding, is the sign. This is
                                // the whole mechanism: in the wedge outside a
                                // convex corner one channel reads negative
                                // while the others read positive, and the
                                // median lands on the half-plane intersection.
                                bool past =
                                    (raw < 0f && (flags & MsdfEdgeColoring.RunStart) != 0) ||
                                    (raw > 1f && (flags & MsdfEdgeColoring.RunEnd) != 0);
                                float pseudo;
                                if (past)
                                {
                                    float cross = ab.x * ap.y - ab.y * ap.x;
                                    float perpendicular = math.abs(cross) * math.rsqrt(lengthSq);
                                    pseudo = cross * orientation > 0f ? -perpendicular : perpendicular;
                                }
                                else
                                {
                                    pseudo = math.sqrt(distSq);
                                }

                                nearSq = math.select(nearSq, distSq, wins);
                                nearDot = math.select(nearDot, oblique, wins);
                                value = math.select(value, pseudo, wins);
                                beyond = math.select(beyond, past ? 1 : 0, wins);
                            }
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
            Fold(ref union, ref unionTrue, value, beyond, minSq, winding);

            float scale = 1f / tile.UnitsPerPixel;
            int at = (tile.OutputStart + local) * 4;
            Output[at + 0] = Encode(union.x * scale);
            Output[at + 1] = Encode(union.y * scale);
            Output[at + 2] = Encode(union.z * scale);
            Output[at + 3] = Encode(unionTrue * scale);
        }

        private byte Encode(float signedPixels) =>
            (byte)math.round(math.saturate(0.5f - signedPixels / (2f * SpreadPixels)) * 255f);

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

        /// <summary>
        /// Signs one source glyph's channels and folds them into the union.
        /// Pseudo-distances taken past a run end already carry their sign;
        /// everything else is a magnitude the winding turns negative inside.
        /// </summary>
        private static void Fold(ref float3 union, ref float unionTrue,
            float3 value, int3 beyond, float minSq, int winding)
        {
            float trueDistance = math.sqrt(minSq);
            if (winding != 0) trueDistance = -trueDistance;
            unionTrue = math.min(unionTrue, trueDistance);

            float3 wound = winding != 0 ? -value : value;
            union = math.min(union, math.select(wound, value, beyond != 0));
        }
    }
}
