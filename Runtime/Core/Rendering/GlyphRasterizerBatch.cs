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
            s_resolvedOf.Clear();
            for (int r = 0; r < requests.Count; r++) s_resolvedOf.Add(-1);
            int groupCount = Pack(requests, null, precise, out int packedPoints);

            long jobStart = AtlasDiagnostics.Now;
            bool scanning = ResolveOverlaps && groupCount > 0;
            var scan = new OverlapScanJob
            {
                Points = s_points,
                ContourRanges = s_ranges,
                ContourBounds = s_bounds,
                Groups = s_groupSpans,
                Overlapping = s_overlapping,
            };

            // Two ways to pay for overlapping contours, chosen by what the last
            // batch found. Most fonts ship with their overlaps removed, and for
            // those the scan costs no wait at all: the fields are scheduled
            // first, and the calling thread, which has nothing to do until they
            // finish, scans while the workers bake. No second schedule, no
            // second wait, and in the rare batch it does flag something the
            // flagged tiles are baked a second time. A font that keeps its
            // overlaps would pay that second bake on nearly every tile with a
            // join, so once a batch has found any, the next one scans first,
            // across the workers, and bakes each tile once, resolved.
            int flagged = 0, redo = 0;
            if (scanning && s_expectOverlaps)
            {
                scan.Schedule(groupCount, 4).Complete();
                redo = ResolveFlagged(requests, groupCount, precise, out flagged);
                if (redo > 0) Pack(requests, null, precise, out packedPoints);
                Dispatch(packedPoints, totalTexels, s_descs.Count, precise).Complete();
            }
            else
            {
                var fields = Dispatch(packedPoints, totalTexels, s_descs.Count, precise);
                if (scanning) scan.Run(groupCount);
                fields.Complete();
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.JobWaitTicks, jobStart);

            // One copy for the whole batch into one reused buffer; the results
            // index into it rather than owning an array each.
            int totalBytes = totalTexels * bytesPerTexel;
            if (s_pixels.Length < totalBytes)
                s_pixels = new byte[Mathf.NextPowerOfTwo(totalBytes)];
            NativeArray<byte>.Copy(s_output, 0, s_pixels, 0, totalBytes);

            if (scanning && !s_expectOverlaps)
            {
                redo = ResolveFlagged(requests, groupCount, precise, out flagged);
                if (redo > 0) Rebake(requests, precise);
            }
            if (scanning) s_expectOverlaps = flagged > 0;
            for (int k = 0; k < redo; k++) s_resolved[k].Clear();

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

        /// <summary>
        /// Whether the last batch found overlapping contours, and so whether
        /// this one waits for the scan before baking. See the note where it is
        /// read.
        /// </summary>
        private static bool s_expectOverlaps;

        /// <summary>
        /// Packs requests into the buffers the jobs read: every request in
        /// place when <paramref name="subset"/> is null, otherwise just those,
        /// one after another, as a batch of their own. A request with an entry
        /// in <see cref="s_resolvedOf"/> is packed from its resolved contours,
        /// any other from the contours it came with.
        ///
        /// Returns the number of source-glyph spans written for the overlap
        /// scan, which only a full pack of original contours records.
        /// </summary>
        private static int Pack(IReadOnlyList<RasterizeRequest> requests, List<int> subset, bool precise,
            out int totalPoints)
        {
            int tiles = subset?.Count ?? requests.Count;
            int totalContours = 0, totalTexels = 0;
            totalPoints = 0;
            bool spans = subset == null;
            for (int k = 0; k < tiles; k++)
            {
                int r = subset?[k] ?? k;
                int resolved = s_resolvedOf[r];
                if (resolved >= 0)
                {
                    spans = false;
                    var contours = s_resolved[resolved].Contours;
                    foreach (var contour in contours) totalPoints += contour.Count;
                    totalContours += contours.Count;
                }
                else
                {
                    foreach (var contour in requests[r].Contours)
                    {
                        if (contour.Count < 2) continue;
                        totalPoints += contour.Count;
                        totalContours++;
                    }
                }
                totalTexels += s_descs[r].Width * s_descs[r].Height;
            }
            GrowGeometry(totalPoints, totalContours, tiles, totalTexels, precise);
            if (spans)
            {
                Grow(ref s_groupSpans, totalContours);
                Grow(ref s_overlapping, totalContours);
                s_groupRequest.Clear();
                s_groupIds.Clear();
            }

            int pi = 0, ci = 0, texelEnd = 0, groupCount = 0;
            for (int k = 0; k < tiles; k++)
            {
                int r = subset?[k] ?? k;
                var request = requests[r];
                var resolved = s_resolvedOf[r] >= 0 ? s_resolved[s_resolvedOf[r]] : null;
                var contours = resolved?.Contours ?? request.Contours;
                var groups = resolved?.Groups ?? request.Groups;
                int tileStart = ci, flag = 0;
                // Contour winding decides the sign of a pseudo-distance, and it
                // is a property of the source glyph, not of one of its contours:
                // a counter is wound against its outline on purpose. Summed over
                // the group, where the outline dominates.
                int groupStart = ci, currentGroup = int.MinValue;
                float groupArea = 0f;

                for (int c = 0; c < contours.Count; c++)
                {
                    var contour = contours[c];
                    if (contour.Count < 2) continue;
                    int group = groups?[c] ?? 0;
                    byte kind = resolved?.Kinds[c] ?? ContourKind.Both;
                    if (precise && ci > groupStart && group != currentGroup)
                    {
                        WriteOrientation(groupStart, ci, groupArea);
                        groupStart = ci;
                        groupArea = 0f;
                    }
                    // One span per source glyph, which is what the overlap
                    // scan looks at: a glyph's own contours, nobody else's.
                    if (spans)
                    {
                        if (ci == tileStart || group != currentGroup)
                        {
                            s_groupSpans[groupCount++] = new int2(ci, 1);
                            s_groupRequest.Add(r);
                            s_groupIds.Add(group);
                        }
                        else
                        {
                            var span = s_groupSpans[groupCount - 1];
                            s_groupSpans[groupCount - 1] = new int2(span.x, span.y + 1);
                        }
                    }
                    currentGroup = group;
                    s_groups[ci] = group;
                    s_kinds[ci] = kind;
                    s_ranges[ci] = new int2(pi, contour.Count);
                    if (precise)
                    {
                        if (resolved == null)
                        {
                            MsdfEdgeColoring.Assign(contour, s_flagBytes, pi);
                        }
                        else
                        {
                            for (int i = 0; i < contour.Count; i++) s_flagBytes[pi + i] = resolved.Flags[flag + i];
                            flag += contour.Count;
                        }
                    }

                    // The orientation is the glyph's, so only the contours it
                    // was drawn with count towards it: a visible piece of a
                    // resolved overlap is a copy of part of one of them, and
                    // open besides.
                    bool wound = (kind & ContourKind.Winding) != 0;
                    var lo = new Vector2(float.MaxValue, float.MaxValue);
                    var hi = new Vector2(float.MinValue, float.MinValue);
                    var previous = contour[contour.Count - 1];
                    foreach (var p in contour)
                    {
                        lo = Vector2.Min(lo, p);
                        hi = Vector2.Max(hi, p);
                        if (wound) groupArea += previous.x * p.y - p.x * previous.y;
                        previous = p;
                        s_points[pi++] = new float2(p.x, p.y);
                    }
                    s_bounds[ci++] = new float4(lo.x, lo.y, hi.x, hi.y);
                }
                if (precise && ci > groupStart) WriteOrientation(groupStart, ci, groupArea);

                var desc = s_descs[r];
                desc.ContourStart = tileStart;
                desc.ContourCount = ci - tileStart;
                // A subset is a batch of its own and its texels start at zero;
                // a full pack writes every tile where the results expect it.
                if (subset != null) desc.OutputStart = texelEnd;
                s_tiles[k] = desc;
                texelEnd += desc.Width * desc.Height;
                s_tileEnds[k] = texelEnd;
            }
            return groupCount;
        }

        /// <summary>
        /// Schedules the field jobs over whatever is packed in the buffers: the
        /// first <paramref name="tileCount"/> tiles, their contours and points.
        /// The caller completes the handle.
        /// </summary>
        private static JobHandle Dispatch(int totalPoints, int totalTexels, int tileCount, bool precise)
        {
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
            if (!precise)
            {
                return new SdfBatchJob
                {
                    Points = s_points,
                    ContourRanges = s_ranges,
                    ContourGroups = s_groups,
                    ContourBounds = s_bounds,
                    ContourKinds = s_kinds,
                    Tiles = s_tiles,
                    TileEnds = s_tileEnds,
                    TileCount = tileCount,
                    SpreadPixels = SpreadPixels,
                    Cull = Cull,
                    Output = s_output,
                }.Schedule(totalTexels, 128);
            }

            NativeArray<byte>.Copy(s_flagBytes, 0, s_flags, 0, totalPoints);
            var fields = new MsdfBatchJob
            {
                Points = s_points,
                SegmentFlags = s_flags,
                ContourRanges = s_ranges,
                ContourGroups = s_groups,
                ContourBounds = s_bounds,
                ContourKinds = s_kinds,
                ContourOrientation = s_orientation,
                Tiles = s_tiles,
                TileEnds = s_tileEnds,
                TileCount = tileCount,
                SpreadPixels = SpreadPixels,
                Cull = Cull,
                ErrorCorrectionTexels = MsdfErrorCorrectionTexels,
                Output = s_output,
                WinningGroup = s_winners,
            }.Schedule(totalTexels, 128);

            // The half that needs neighbours, and so cannot ride inside the
            // job above: a median that is right at every texel it is stored
            // in and dives between two of them. Two passes, because a
            // texel's verdict is read off neighbours that must still hold
            // their original values while it is being decided; each waits for
            // the one before it.
            if (MsdfErrorCorrectionTexels <= 0f) return fields;
            Grow(ref s_marks, totalTexels);
            // One sweep, not iterated. A second was tried and moved
            // nothing measurable: flattening a texel to its own median
            // does not shift its value, so it cannot open a crossing
            // that was not already there, and there is no second round
            // of damage to chase.
            var marked = new MsdfMarkJob
            {
                Field = s_output,
                WinningGroup = s_winners,
                Tiles = s_tiles,
                TileEnds = s_tileEnds,
                TileCount = tileCount,
                SpreadPixels = SpreadPixels,
                FloorTexels = MsdfErrorCorrectionTexels,
                Marks = s_marks,
            }.Schedule(totalTexels, 128, fields);

            return new MsdfFlattenJob
            {
                Marks = s_marks,
                Field = s_output,
            }.Schedule(totalTexels, 128, marked);
        }

        /// <summary>
        /// Resolves the overlaps of every request the scan flagged a glyph of,
        /// into <see cref="s_resolved"/>, and points <see cref="s_resolvedOf"/>
        /// at the result. Returns how many requests that was;
        /// <paramref name="flagged"/> is how many glyphs.
        /// </summary>
        private static int ResolveFlagged(IReadOnlyList<RasterizeRequest> requests, int groupCount,
            bool precise, out int flagged)
        {
            flagged = 0;
            s_redoRequests.Clear();
            for (int g = 0; g < groupCount; g++)
            {
                if (s_overlapping[g] == 0) continue;
                flagged++;
                int r = s_groupRequest[g];
                int redo = s_redoRequests.Count;
                if (redo == 0 || s_redoRequests[redo - 1] != r)
                {
                    s_redoRequests.Add(r);
                    if (s_redoGroups.Count == redo) s_redoGroups.Add(new List<int>());
                    s_redoGroups[redo].Clear();
                    redo++;
                }
                s_redoGroups[redo - 1].Add(s_groupIds[g]);
            }

            for (int k = 0; k < s_redoRequests.Count; k++)
            {
                if (s_resolved.Count == k) s_resolved.Add(new ResolvedContours());
                int r = s_redoRequests[k];
                ContourOverlaps.Resolve(requests[r], s_redoGroups[k], precise, s_resolved[k]);
                s_resolvedOf[r] = k;
            }
            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OverlapRebakes += s_redoRequests.Count;
            return s_redoRequests.Count;
        }

        /// <summary>
        /// Bakes the resolved requests again as a batch of their own and writes
        /// them over their slots in the batch's pixels. Their tiles keep the
        /// size the first bake gave them: the ink is the same, only the
        /// distance inside it changes.
        /// </summary>
        private static void Rebake(IReadOnlyList<RasterizeRequest> requests, bool precise)
        {
            Pack(requests, s_redoRequests, precise, out int points);
            int texels = s_tileEnds[s_redoRequests.Count - 1];
            Dispatch(points, texels, s_redoRequests.Count, precise).Complete();

            int bytesPerTexel = precise ? 4 : 1;
            for (int k = 0; k < s_redoRequests.Count; k++)
            {
                var tile = s_tiles[k];
                NativeArray<byte>.Copy(s_output, tile.OutputStart * bytesPerTexel,
                    s_pixels, s_descs[s_redoRequests[k]].OutputStart * bytesPerTexel,
                    tile.Width * tile.Height * bytesPerTexel);
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
        private static NativeArray<byte> s_kinds;

        // One span of contours per source glyph, the request and group id it
        // came from, and the overlap scan's verdict on it.
        private static NativeArray<int2> s_groupSpans;
        private static NativeArray<byte> s_overlapping;
        private static readonly List<int> s_groupRequest = new List<int>();
        private static readonly List<int> s_groupIds = new List<int>();

        // The requests baked again with their overlaps resolved, the groups of
        // each that were flagged, what the resolution made of them, and for
        // every request of the batch which of those it is (-1: none).
        private static readonly List<int> s_redoRequests = new List<int>();
        private static readonly List<int> s_resolvedOf = new List<int>();
        private static readonly List<List<int>> s_redoGroups = new List<List<int>>();
        private static readonly List<ResolvedContours> s_resolved = new List<ResolvedContours>();
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

        /// <summary>Grows every buffer a dispatch reads or writes to fit what is about to be packed.</summary>
        private static void GrowGeometry(int points, int contours, int tiles, int texels, bool precise)
        {
            Grow(ref s_points, points);
            Grow(ref s_ranges, contours);
            Grow(ref s_groups, contours);
            Grow(ref s_bounds, contours);
            Grow(ref s_kinds, contours);
            Grow(ref s_tiles, tiles);
            Grow(ref s_tileEnds, tiles);
            Grow(ref s_output, texels * (precise ? 4 : 1));
            if (!precise) return;
            Grow(ref s_flags, points);
            Grow(ref s_orientation, contours);
            Grow(ref s_winners, texels);
            if (s_flagBytes.Length < points)
                s_flagBytes = new byte[Mathf.NextPowerOfTwo(points)];
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
            Release(ref s_kinds);
            Release(ref s_groupSpans);
            Release(ref s_overlapping);
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
