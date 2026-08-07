using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace OneText
{
    /// <summary>One tile to rasterize: positioned contours at a chosen density.</summary>
    public struct RasterizeRequest
    {
        public List<List<Vector2>> Contours;
        public List<int> Groups;      // source glyph per contour (null = one group)
        public float PixelsPerUnit;
    }

    public static partial class GlyphRasterizer
    {
        /// <summary>
        /// Rasterizes several tiles in one dispatch, appending one
        /// <see cref="RasterizedGlyph"/> per request.
        ///
        /// This is the path that matters for cost. A single glyph pays the job
        /// scheduling and temporary allocations in full: measured at 18 ppem, a
        /// Hangul tile spent about 132 ns per texel against 34 ns for a tile
        /// five times larger, and the difference is all fixed overhead. Every
        /// tile a frame needs therefore goes up together.
        ///
        /// Every buffer here is reused between calls, including the one the
        /// results point into: a text system that allocates per frame hands the
        /// player a collection pause eventually, and the tile pixels are read
        /// by the atlas immediately and never held.
        /// </summary>
        /// <param name="precise">
        /// Bake multi-channel fields (4 bytes a texel) instead of single-channel
        /// ones. One mode for the whole batch: the two fields live in different
        /// atlases, so a dispatch never mixes them.
        /// </param>
        public static void RasterizeBatch(IReadOnlyList<RasterizeRequest> requests,
            List<RasterizedGlyph> results, bool precise = false)
        {
            results.Clear();
            if (requests == null || requests.Count == 0) return;

            long startedAt = AtlasDiagnostics.Now;

            int totalPoints = 0, totalContours = 0, totalTexels = 0;
            s_descs.Clear();

            // First pass: size everything and lay out the tiles.
            foreach (var request in requests)
            {
                int points = 0, contours = 0;
                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                foreach (var contour in request.Contours)
                {
                    if (contour.Count < 2) continue;
                    points += contour.Count;
                    contours++;
                    foreach (var p in contour)
                    {
                        min = Vector2.Min(min, p);
                        max = Vector2.Max(max, p);
                    }
                }

                if (points == 0)
                {
                    results.Add(new RasterizedGlyph { IsEmpty = true });
                    s_descs.Add(default);
                    continue;
                }

                float unitsPerPixel = 1f / request.PixelsPerUnit;
                int width = Mathf.Min(MaxTileEdge,
                    Mathf.CeilToInt((max.x - min.x) * request.PixelsPerUnit) + 2 * Padding);
                int height = Mathf.Min(MaxTileEdge,
                    Mathf.CeilToInt((max.y - min.y) * request.PixelsPerUnit) + 2 * Padding);
                var origin = min - new Vector2(Padding, Padding) * unitsPerPixel;

                s_descs.Add(new SdfTileDesc
                {
                    ContourStart = totalContours,
                    ContourCount = contours,
                    OutputStart = totalTexels,
                    Width = width,
                    Height = height,
                    OriginUnits = new float2(origin.x, origin.y),
                    UnitsPerPixel = unitsPerPixel,
                });
                results.Add(new RasterizedGlyph
                {
                    Width = width,
                    Height = height,
                    OriginUnits = origin,
                    UnitsPerPixel = unitsPerPixel,
                    Channels = precise ? 4 : 1,
                });

                totalPoints += points;
                totalContours += contours;
                totalTexels += width * height;
            }

            if (totalTexels == 0)
            {
                AtlasDiagnostics.Add(ref AtlasDiagnostics.RasterizeTicks, startedAt);
                return;
            }

            int bytesPerTexel = precise ? 4 : 1;
            Grow(ref s_points, totalPoints);
            Grow(ref s_ranges, totalContours);
            Grow(ref s_groups, totalContours);
            Grow(ref s_bounds, totalContours);
            Grow(ref s_tiles, s_descs.Count);
            Grow(ref s_tileEnds, s_descs.Count);
            Grow(ref s_output, totalTexels * bytesPerTexel);
            if (precise)
            {
                Grow(ref s_flags, totalPoints);
                Grow(ref s_orientation, totalContours);
                Grow(ref s_winners, totalTexels);
                if (s_flagBytes.Length < totalPoints)
                    s_flagBytes = new byte[Mathf.NextPowerOfTwo(totalPoints)];
            }

            int pi = 0, ci = 0, texelEnd = 0;
            for (int r = 0; r < requests.Count; r++)
            {
                var request = requests[r];
                var desc = s_descs[r];
                // Contour winding decides the sign of a pseudo-distance, and it
                // is a property of the source glyph, not of one of its contours:
                // a counter is wound against its outline on purpose. Summed over
                // the group, where the outline dominates.
                int groupStart = ci, currentGroup = int.MinValue;
                float groupArea = 0f;

                for (int k = 0; k < request.Contours.Count; k++)
                {
                    var contour = request.Contours[k];
                    if (contour.Count < 2) continue;
                    int group = request.Groups?[k] ?? 0;
                    if (precise && ci > groupStart && group != currentGroup)
                    {
                        WriteOrientation(groupStart, ci, groupArea);
                        groupStart = ci;
                        groupArea = 0f;
                    }
                    currentGroup = group;
                    s_groups[ci] = group;
                    s_ranges[ci] = new int2(pi, contour.Count);
                    if (precise) MsdfEdgeColoring.Assign(contour, s_flagBytes, pi);

                    var lo = new Vector2(float.MaxValue, float.MaxValue);
                    var hi = new Vector2(float.MinValue, float.MinValue);
                    var previous = contour[contour.Count - 1];
                    foreach (var p in contour)
                    {
                        lo = Vector2.Min(lo, p);
                        hi = Vector2.Max(hi, p);
                        groupArea += previous.x * p.y - p.x * previous.y;
                        previous = p;
                        s_points[pi++] = new float2(p.x, p.y);
                    }
                    s_bounds[ci++] = new float4(lo.x, lo.y, hi.x, hi.y);
                }
                if (precise && ci > groupStart) WriteOrientation(groupStart, ci, groupArea);

                s_tiles[r] = desc;
                texelEnd += desc.Width * desc.Height;
                s_tileEnds[r] = texelEnd;
            }

            long jobStart = AtlasDiagnostics.Now;
            // Scheduled, not Run(), even for the two or three tiles a single
            // label contributes. Running small batches inline was tried and
            // measured twice as slow on the workload it was aimed at: a scene
            // retexting fifty labels a frame with unseen glyphs went from 11.1
            // to 23.0 ms a frame, and five labels a frame from 1.0 to 2.3.
            //
            // The mistake was reading JobWaitTicks as scheduling overhead. It
            // is not: Complete() waits for the distance fields themselves, so
            // nearly all of that time is the work, spread across cores. Taking
            // the scheduler away does not remove a cost, it removes the
            // parallelism that was paying for it.
            if (precise)
            {
                NativeArray<byte>.Copy(s_flagBytes, 0, s_flags, 0, totalPoints);
                new MsdfBatchJob
                {
                    Points = s_points,
                    SegmentFlags = s_flags,
                    ContourRanges = s_ranges,
                    ContourGroups = s_groups,
                    ContourBounds = s_bounds,
                    ContourOrientation = s_orientation,
                    Tiles = s_tiles,
                    TileEnds = s_tileEnds,
                    TileCount = s_descs.Count,
                    SpreadPixels = SpreadPixels,
                    Cull = Cull,
                    ErrorCorrectionTexels = MsdfErrorCorrectionTexels,
                    Output = s_output,
                    WinningGroup = s_winners,
                }.Schedule(totalTexels, 128).Complete();

                // The half that needs neighbours, and so cannot ride inside the
                // job above: a median that is right at every texel it is stored
                // in and dives between two of them. Two passes, because a
                // texel's verdict is read off neighbours that must still hold
                // their original values while it is being decided.
                if (MsdfErrorCorrectionTexels > 0f)
                {
                    Grow(ref s_marks, totalTexels);
                    // One sweep, not iterated. A second was tried and moved
                    // nothing measurable: flattening a texel to its own median
                    // does not shift its value, so it cannot open a crossing
                    // that was not already there, and there is no second round
                    // of damage to chase.
                    new MsdfMarkJob
                    {
                        Field = s_output,
                        WinningGroup = s_winners,
                        Tiles = s_tiles,
                        TileEnds = s_tileEnds,
                        TileCount = s_descs.Count,
                        SpreadPixels = SpreadPixels,
                        FloorTexels = MsdfErrorCorrectionTexels,
                        Marks = s_marks,
                    }.Schedule(totalTexels, 128).Complete();

                    new MsdfFlattenJob
                    {
                        Marks = s_marks,
                        Field = s_output,
                    }.Schedule(totalTexels, 128).Complete();
                }
            }
            else
            {
                new SdfBatchJob
                {
                    Points = s_points,
                    ContourRanges = s_ranges,
                    ContourGroups = s_groups,
                    ContourBounds = s_bounds,
                    Tiles = s_tiles,
                    TileEnds = s_tileEnds,
                    TileCount = s_descs.Count,
                    SpreadPixels = SpreadPixels,
                    Cull = Cull,
                    Output = s_output,
                }.Schedule(totalTexels, 128).Complete();
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.JobWaitTicks, jobStart);

            // One copy for the whole batch into one reused buffer; the results
            // index into it rather than owning an array each.
            int totalBytes = totalTexels * bytesPerTexel;
            if (s_pixels.Length < totalBytes)
                s_pixels = new byte[Mathf.NextPowerOfTwo(totalBytes)];
            NativeArray<byte>.Copy(s_output, 0, s_pixels, 0, totalBytes);

            for (int r = 0; r < results.Count; r++)
            {
                var result = results[r];
                if (result.IsEmpty) continue;
                result.Pixels = s_pixels;
                result.PixelStart = s_descs[r].OutputStart * bytesPerTexel;
                results[r] = result;
            }

            AtlasDiagnostics.Add(ref AtlasDiagnostics.RasterizeTicks, startedAt);
            if (AtlasDiagnostics.Enabled)
            {
                AtlasDiagnostics.RasterizeCount += requests.Count;
                AtlasDiagnostics.DispatchCount++;
                AtlasDiagnostics.RasterizedPixels += totalTexels;
            }
        }

        // ------------------------------------------------------------ buffers

        private static readonly List<SdfTileDesc> s_descs = new List<SdfTileDesc>();
        private static byte[] s_pixels = System.Array.Empty<byte>();

        // Edge colours are decided in managed code (the corner test wants the
        // contour as a list) and go over to the job in one copy per batch.
        private static byte[] s_flagBytes = System.Array.Empty<byte>();

        private static NativeArray<float2> s_points;
        private static NativeArray<int2> s_ranges;
        private static NativeArray<int> s_groups;
        private static NativeArray<float4> s_bounds;
        private static NativeArray<SdfTileDesc> s_tiles;
        private static NativeArray<int> s_tileEnds;
        private static NativeArray<byte> s_output;
        private static NativeArray<byte> s_flags;
        private static NativeArray<float> s_orientation;

        // One byte a texel, the interpolation pass's verdict between its two
        // halves. Persistent like every other buffer here: a text system that
        // allocates per frame hands the player a collection pause eventually.
        private static NativeArray<byte> s_marks;

        // The source glyph that won each texel, so the interpolation pass can
        // find the seams between them.
        private static NativeArray<byte> s_winners;

        private static void WriteOrientation(int first, int last, float area)
        {
            float orientation = area >= 0f ? 1f : -1f;
            for (int c = first; c < last; c++) s_orientation[c] = orientation;
        }

        /// <summary>
        /// Grows a persistent buffer to at least <paramref name="needed"/>,
        /// rounded up so a slowly growing frame stops reallocating. Buffers are
        /// never shrunk: the high-water mark of one scene is a fair guess at
        /// the next one's.
        /// </summary>
        private static void Grow<T>(ref NativeArray<T> array, int needed) where T : struct
        {
            if (array.IsCreated && array.Length >= needed) return;
            if (array.IsCreated) array.Dispose();
            array = new NativeArray<T>(Mathf.NextPowerOfTwo(Mathf.Max(needed, 64)),
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Frees the persistent buffers. Called on domain reload and on quit;
        /// public because a host that tears the engine down itself may want it.
        /// </summary>
        public static void ReleaseBuffers()
        {
            Release(ref s_points);
            Release(ref s_ranges);
            Release(ref s_groups);
            Release(ref s_bounds);
            Release(ref s_tiles);
            Release(ref s_tileEnds);
            Release(ref s_output);
            Release(ref s_flags);
            Release(ref s_orientation);
            Release(ref s_marks);
            Release(ref s_winners);
            s_pixels = System.Array.Empty<byte>();
            s_flagBytes = System.Array.Empty<byte>();
        }

        private static void Release<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ReleaseOnAssemblyReload() =>
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseBuffers;
#endif

        // With Domain Reload disabled this method runs once per play session
        // while the statics survive between them, so the subscription would
        // stack up, and so would anything else done here unconditionally.
        // Releasing first is not merely tidy: buffers left over from the last
        // session are sized for its high-water mark and belong to no one.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReleaseOnQuit()
        {
            ReleaseBuffers();
            Application.quitting -= ReleaseBuffers;
            Application.quitting += ReleaseBuffers;
        }
    }
}
