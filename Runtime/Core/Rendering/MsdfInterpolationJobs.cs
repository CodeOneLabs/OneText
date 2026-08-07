using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace OneText
{
    /// <summary>
    /// The other half of MSDF error correction: the artifact that exists only
    /// <em>between</em> texels.
    ///
    /// <see cref="MsdfBatchJob"/> can check a texel against the true distance it
    /// already has, and that catches a median which is wrong where it is stored.
    /// It cannot catch this, and this is what a magnified tile actually shows.
    /// Measured on an 'A' at 64 ppem drawn at 3x, across the crossbar junction:
    ///
    /// <code>
    ///      u      R      G      B  median   true
    ///  23.08   1.01   0.98  -0.43    0.98   1.31
    ///  23.83   0.53   0.25   0.27    0.27   1.05   &lt;- the dip
    ///  24.08   0.51   0.01   0.51    0.51   1.04
    /// </code>
    ///
    /// Green is falling and blue is rising, and they swap rank partway between
    /// texel 23 and texel 24. The median follows the swap into the notch — a
    /// texel of solid ink reported as sitting on the outline — while both
    /// endpoints are within tolerance and no per-texel test can see anything
    /// wrong. Bilinear interpolation is linear per channel but the median of
    /// three linear functions is piecewise linear with a kink at every crossing,
    /// and the kink is free to point the wrong way.
    ///
    /// So the test has to be the crossings themselves. For each neighbouring
    /// pair, each of the three channel pairs is solved for where it swaps, and
    /// the median there is compared against the true distance interpolated to
    /// the same place — the same comparison the per-texel rule makes, moved to
    /// the point where it can fail. Where the median dives to the outline and
    /// the truth says solid ink, the texel is marked.
    ///
    /// A marked texel is then flattened to its own median rather than replaced
    /// by the true distance. Replacing it was tried and measured worse (0.937 to
    /// 0.890 on the case above): it puts a step between the corrected texel and
    /// its uncorrected neighbours, and the interpolation across the step is a new
    /// artifact in place of the old one. Flattening moves the value nowhere. It
    /// only removes the disagreement that let the rank swap happen, which is the
    /// thing that was actually wrong.
    ///
    /// Two passes, because a texel's verdict depends on neighbours that must
    /// still hold their original values while it is being decided.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    internal struct MsdfMarkJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Field;

        /// <inheritdoc cref="MsdfBatchJob.WinningGroup"/>
        [ReadOnly] public NativeArray<byte> WinningGroup;

        [ReadOnly] public NativeArray<SdfTileDesc> Tiles;
        [ReadOnly] public NativeArray<int> TileEnds;

        public int TileCount;
        public float SpreadPixels;

        /// <summary>
        /// How far clear of the outline the median has to stay where the truth
        /// says solid ink; <see cref="GlyphRasterizer.MsdfErrorCorrectionTexels"/>.
        /// Zero or less turns this pass off.
        /// </summary>
        public float FloorTexels;

        [WriteOnly] public NativeArray<byte> Marks;

        public void Execute(int index)
        {
            int tileIndex = MsdfTiles.Find(TileEnds, TileCount, index);
            var tile = Tiles[tileIndex];
            int local = index - (tileIndex == 0 ? 0 : TileEnds[tileIndex - 1]);
            int x = local % tile.Width, y = local / tile.Width;

            float4 here = Read(tile, x, y);
            bool bad = false;

            // Right, up, and both diagonals: every direction bilinear can
            // reconstruct along. The opposite four are covered when those
            // neighbours run their own iteration.
            bad |= Between(here, tile, x + 1, y);
            bad |= Between(here, tile, x, y + 1);
            bad |= Between(here, tile, x + 1, y + 1);
            bad |= Between(here, tile, x + 1, y - 1);

            // And the seam. Two glyphs of a cluster are coloured on their own,
            // so R is one edge in one of them and an unrelated edge in the
            // other; where the winning glyph changes from one texel to the
            // next, the two triples are not comparable and what bilinear puts
            // between them belongs to neither. That is the spike that hangs
            // between the feet of two adjacent letters, in a gap where the
            // field itself is right to within half a texel.
            //
            // Nothing is lost by flattening there: a seam is a gap between two
            // glyphs, and a gap has no corner to keep sharp.
            byte group = WinningGroup[index];
            bad |= Differs(group, tile, x + 1, y);
            bad |= Differs(group, tile, x - 1, y);
            bad |= Differs(group, tile, x, y + 1);
            bad |= Differs(group, tile, x, y - 1);

            Marks[index] = bad ? (byte)1 : (byte)0;
        }

        private bool Differs(byte group, in SdfTileDesc tile, int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= tile.Width || ny >= tile.Height) return false;
            return WinningGroup[tile.OutputStart + ny * tile.Width + nx] != group;
        }

        private float4 Read(in SdfTileDesc tile, int x, int y)
        {
            x = math.clamp(x, 0, tile.Width - 1);
            y = math.clamp(y, 0, tile.Height - 1);
            int at = (tile.OutputStart + y * tile.Width + x) * 4;
            // Back to signed texels, positive outside, so the thresholds below
            // read as the distances they are.
            float4 unit = new float4(Field[at], Field[at + 1], Field[at + 2], Field[at + 3])
                          * (1f / 255f);
            return (0.5f - unit) * (2f * SpreadPixels);
        }

        /// <summary>
        /// True when the segment from <paramref name="a"/> to the given
        /// neighbour has the median diving to the outline somewhere a channel
        /// pair swaps, while the true distance there says solid ink.
        /// </summary>
        private bool Between(float4 a, in SdfTileDesc tile, int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= tile.Width || ny >= tile.Height) return false;
            float4 b = Read(tile, nx, ny);

            bool bad = false;
            bad |= AtCrossing(a, b, 0, 1);
            bad |= AtCrossing(a, b, 1, 2);
            bad |= AtCrossing(a, b, 2, 0);
            return bad;
        }

        private bool AtCrossing(float4 a, float4 b, int i, int j)
        {
            // Where the two channels swap rank, if they do so strictly between.
            float d0 = a[i] - a[j];
            float d1 = b[i] - b[j];
            float denominator = d0 - d1;
            if (math.abs(denominator) < 1e-9f) return false;
            float t = d0 / denominator;
            if (t <= 0f || t >= 1f) return false;

            float3 channels = math.lerp(a.xyz, b.xyz, t);
            float median = math.max(math.min(channels.x, channels.y),
                math.min(math.max(channels.x, channels.y), channels.z));
            float trueField = math.lerp(a.w, b.w, t);

            // Inside is negative. Solid ink by the truth, on the outline by the
            // median: the same statement the per-texel rule makes, asked where
            // interpolation can break it.
            return trueField < -(FloorTexels + GuardTexels) && median > -FloorTexels;
        }

        /// <summary>
        /// The margin past the floor before a crossing counts, matching the
        /// per-texel rule's own guard so the two agree about what "solid" means.
        /// </summary>
        private const float GuardTexels = 0.5f;
    }

    /// <summary>
    /// Applies <see cref="MsdfMarkJob"/>'s verdict: a marked texel's three
    /// channels all become its median, which leaves the median exactly where it
    /// was and takes away the rank swap that made it dive between texels. Alpha
    /// is never touched — it is the true distance every other rule is measured
    /// against, and the shader reads it for the shadow.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    internal struct MsdfFlattenJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Marks;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Field;

        public void Execute(int index)
        {
            if (Marks[index] == 0) return;
            int at = index * 4;
            int r = Field[at], g = Field[at + 1], b = Field[at + 2];
            var median = (byte)math.max(math.min(r, g), math.min(math.max(r, g), b));
            Field[at] = median;
            Field[at + 1] = median;
            Field[at + 2] = median;
        }
    }

    internal static class MsdfTiles
    {
        /// <summary>Which tile a flat texel index belongs to.</summary>
        public static int Find(NativeArray<int> tileEnds, int tileCount, int texel)
        {
            int lo = 0, hi = tileCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (texel < tileEnds[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
    }
}
