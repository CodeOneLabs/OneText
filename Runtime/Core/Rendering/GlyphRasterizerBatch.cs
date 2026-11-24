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

        /// <summary>
        /// The source glyphs (values of <see cref="Groups"/>; 0 when that is
        /// null) already known to have contours that overlap, or null when
        /// nothing is known. A request that says what it knows, even that it
        /// knows of none, is not scanned for overlaps; the glyphs it names are
        /// resolved before the bake. Fonts made into a <see cref="OneFontAsset"/>
        /// carry this knowledge (<see cref="OneFontAsset.HasOverlapIndex"/>),
        /// and the atlas passes it on.
        /// </summary>
        public List<int> KnownOverlaps;

        /// <summary>
        /// Instead of <see cref="Contours"/> and <see cref="Groups"/>: contours
        /// <see cref="FlatFirst"/> .. + <see cref="FlatCount"/> of a shared
        /// flat buffer, already offset, bounded and free of contours under two
        /// points. Only single-channel requests are built this way.
        /// </summary>
        internal FlatContours Flat;
        internal int FlatFirst, FlatCount;

        /// <summary>
        /// Instead of contours of any kind: glyphs
        /// <see cref="LazyFirst"/> .. + <see cref="LazyCount"/> of a
        /// <see cref="LazyGlyphs"/>, whose outlines the batch draws on the
        /// workers (<see cref="GlyphAtlas.WorkerOutlines"/>). Sized from their
        /// statistics; single-channel only, and only for glyphs known to have
        /// no overlapping contours or not known either way.
        /// </summary>
        internal LazyGlyphs Lazy;
        internal int LazyFirst, LazyCount;
    }

    /// <summary>
    /// Contours of many requests in flat arrays: what <see cref="GlyphAtlas"/>
    /// builds a frame's misses into when it can (see
    /// <see cref="GlyphAtlas.FlatContours"/>), so sizing and packing a batch is
    /// a copy per request rather than a walk over lists of lists.
    /// </summary>
    internal sealed class FlatContours
    {
        public float2[] Points = new float2[4096];
        public int PointCount;

        /// <summary>Per contour: first point and point count (always two or more).</summary>
        public int2[] Ranges = new int2[256];

        /// <summary>Per contour: the source glyph it belongs to, within its request.</summary>
        public int[] Groups = new int[256];

        /// <summary>Per contour: (min x, min y, max x, max y) of its points.</summary>
        public float4[] Bounds = new float4[256];
        public int ContourCount;

        public void Clear()
        {
            PointCount = 0;
            ContourCount = 0;
        }

        public void Reserve(int points, int contours)
        {
            if (PointCount + points > Points.Length)
                System.Array.Resize(ref Points, Mathf.NextPowerOfTwo(PointCount + points));
            if (ContourCount + contours > Ranges.Length)
            {
                int size = Mathf.NextPowerOfTwo(ContourCount + contours);
                System.Array.Resize(ref Ranges, size);
                System.Array.Resize(ref Groups, size);
                System.Array.Resize(ref Bounds, size);
            }
        }

        /// <summary>Tests: every point, range, group and bound, as bits.</summary>
        internal string DescribeForTests()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(PointCount).Append('/').Append(ContourCount).Append(':');
            for (int c = 0; c < ContourCount; c++)
            {
                var b = Bounds[c];
                sb.Append('[').Append(Ranges[c].x).Append(',').Append(Ranges[c].y).Append(" g").Append(Groups[c])
                    .Append(' ').Append(math.asint(b.x)).Append(',').Append(math.asint(b.y)).Append(',')
                    .Append(math.asint(b.z)).Append(',').Append(math.asint(b.w)).Append(']');
            }
            for (int i = 0; i < PointCount; i++)
                sb.Append(math.asint(Points[i].x)).Append(',').Append(math.asint(Points[i].y)).Append(' ');
            return sb.ToString();
        }

        /// <summary>
        /// The contours of a flat request as lists, for the managed code that
        /// only reads lists (overlap resolution); rare, so not pooled hard.
        /// </summary>
        public void ToLists(int first, int count, List<List<Vector2>> contours, List<int> groups)
        {
            contours.Clear();
            groups.Clear();
            for (int c = first; c < first + count; c++)
            {
                var range = Ranges[c];
                var list = new List<Vector2>(range.y);
                for (int i = range.x; i < range.x + range.y; i++) list.Add(new Vector2(Points[i].x, Points[i].y));
                contours.Add(list);
                groups.Add(Groups[c]);
            }
        }
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
            // Everything still baking is finished (and handed to its owner)
            // first: those tiles were placed before whatever this writes.
            FinishFlight();
            Use(s_syncSet);
            var flight = s_syncFlight;
            if (BeginBatch(requests, results, precise, background: false, flight))
            {
                s_flights.Add(flight);
                EndBatch(flight);
            }
        }


        /// <summary>
        /// Who started the batch now baking in the background, and is told to
        /// take its tiles (<see cref="IBatchOwner.FinishBatch"/>) when anything
        /// else needs the rasterizer. Null when nothing is in flight.
        /// </summary>
        internal interface IBatchOwner
        {
            /// <summary>Calls <see cref="EndBatch"/> and writes the tiles out.</summary>
            void FinishBatch();
        }

        /// <summary>
        /// Off, one background batch at a time owns the rasterizer's buffers:
        /// starting the next one first collects (waits for) the last, so a
        /// frame that bakes two hundred tiles in a dozen batches makes the
        /// main thread wait for the workers a dozen times, each time for a
        /// batch it could have left baking. On, every background batch gets a
        /// set of buffers of its own (<see cref="BufferSet"/>) and several bake
        /// at once; the owner collects them in the order it started them, so
        /// tiles are still written in placement order, and the only wait left
        /// is for whatever is still baking when the frame needs its texels.
        /// Same requests, same fields, same bytes.
        /// </summary>
        public static bool PipelinedBatches = true;

        /// <summary>
        /// One batch in flight: the buffers it was packed into and baked from,
        /// the job baking it, and what <see cref="EndBatch"/> needs to finish it.
        /// </summary>
        internal sealed class Flight
        {
            internal BufferSet Set;
            internal JobHandle Handle;
            internal IBatchOwner Owner;
            internal IReadOnlyList<RasterizeRequest> Requests;
            internal List<RasterizedGlyph> Results;
            internal bool Precise, Scanning, ScanFirst;
            internal int Texels, Groups, Flagged;

            /// <summary>Glyphs the batch draws on the workers (<see cref="RasterizeRequest.Lazy"/>).</summary>
            internal int Outlines;

            // OverlapsOnWorkers: the overlaps the scan found, resolved by
            // ResolveJob before the batch ends, and what they came to.
            internal bool ResolvedOnWorker;
            internal int WorkerFlagged, WorkerResolvedCount;
            internal readonly List<int> WorkerRequests = new List<int>();
            internal readonly List<List<int>> WorkerGroups = new List<List<int>>();
            internal readonly List<ResolvedContours> WorkerResolved = new List<ResolvedContours>();

            // The scan's group-to-request tables as the batch packed them: the
            // live lists, not the set's fields, which are written back only
            // when the set stops being current (and the synchronous set may
            // never have been).
            internal List<int> GroupRequest, GroupIds;
            internal System.Runtime.InteropServices.GCHandle Self;

            internal Flight() => Self = System.Runtime.InteropServices.GCHandle.Alloc(this);
            internal long BeginTicks;

            /// <summary>True once the workers are done with it (main thread).</summary>
            internal bool IsDone => Handle.IsCompleted;
        }

        // Every batch in flight, oldest first, and the retired ones kept for reuse.
        private static readonly List<Flight> s_flights = new List<Flight>();
        private static readonly Stack<Flight> s_spareFlights = new Stack<Flight>();

        /// <summary>True while a background batch is baking.</summary>
        internal static bool HasFlight => s_flights.Count > 0;

        /// <summary>True when a batch in flight draws outlines on the workers.</summary>
        internal static bool FlightsUseWorkerOutlines
        {
            get
            {
                for (int i = 0; i < s_flights.Count; i++)
                    if (s_flights[i].Outlines > 0) return true;
                return false;
            }
        }

        /// <summary>
        /// Whether the last batch scanned found overlapping contours, so the
        /// next one will scan before it bakes. A batch that draws its outlines
        /// on the workers cannot (the outlines do not exist until it runs), so
        /// the atlas keeps glyphs of unknown overlap on the main thread while
        /// this holds.
        /// </summary>
        internal static bool ExpectingOverlaps => ResolveOverlaps && s_expectOverlaps;

        /// <summary>
        /// Has the owners of every batch in flight take them, oldest first:
        /// for whoever is about to rasterize synchronously, which writes now.
        /// </summary>
        internal static void FinishFlight()
        {
            while (s_flights.Count > 0)
            {
                var flight = s_flights[0];
                if (flight.Owner != null) flight.Owner.FinishBatch();
                // An owner that left its batch unfinished (or none) cannot hold
                // the others up; this one is ended here, with nobody to write it.
                if (s_flights.Count > 0 && s_flights[0] == flight) EndBatch(flight);
            }
        }

        /// <summary>
        /// Starts a batch in the background and returns without waiting for
        /// it: the workers bake while the caller goes on. <paramref name="requests"/>
        /// and <paramref name="results"/> belong to the batch until the owner
        /// calls <see cref="EndBatch"/> with what this returns, which it must
        /// do before it writes anything later (and before anything else
        /// rasterizes, which <see cref="FinishFlight"/> asks it to). Null when
        /// there was nothing to bake.
        ///
        /// With <see cref="PipelinedBatches"/> off this first ends every batch
        /// still in flight, which is how a single set of buffers is shared.
        /// </summary>
        internal static Flight BeginBackground(IReadOnlyList<RasterizeRequest> requests,
            List<RasterizedGlyph> results, bool precise, IBatchOwner owner)
        {
            if (!PipelinedBatches) FinishFlight();
            var flight = s_spareFlights.Count > 0 ? s_spareFlights.Pop() : new Flight();
            if (!PipelinedBatches)
            {
                // A set of its own from a pipelined session is idle; let it go.
                // If it is the one the statics hold, they hold its newest
                // arrays (its fields are written back only when it stops being
                // current), so it is put back first.
                if (flight.Set != null && flight.Set != s_syncSet)
                {
                    if (s_current == flight.Set) Use(s_syncSet);
                    flight.Set.Release();
                }
                flight.Set = s_syncSet;
            }
            else if (flight.Set == null || flight.Set == s_syncSet)
            {
                flight.Set = new BufferSet();
            }
            Use(flight.Set);
            if (!BeginBatch(requests, results, precise, background: true, flight))
            {
                s_spareFlights.Push(flight);
                return null;
            }
            flight.Owner = owner;
            s_flights.Add(flight);
            return flight;
        }

        /// <summary>
        /// Sizes, packs and schedules a batch. With <paramref name="background"/>
        /// everything the workers can do is scheduled and nothing is waited
        /// on; otherwise this is the first half of the synchronous bake, and
        /// the calling thread scans for overlaps while the fields bake, as it
        /// always did. False when the batch has no texels (then it is already
        /// finished: every result is sized and empty ones are marked).
        /// </summary>
        private static bool BeginBatch(IReadOnlyList<RasterizeRequest> requests,
            List<RasterizedGlyph> results, bool precise, bool background, Flight flight)
        {
            results.Clear();
            if (requests == null || requests.Count == 0) return false;

            long startedAt = AtlasDiagnostics.Now;

            int totalPoints = 0, totalContours = 0, totalTexels = 0;
            s_descs.Clear();

            // First pass: size everything and lay out the tiles.
            foreach (var request in requests)
            {
                var shape = Measure(request, precise, out int points, out int contours);
                results.Add(shape);
                if (shape.IsEmpty)
                {
                    s_descs.Add(default);
                    continue;
                }

                s_descs.Add(new SdfTileDesc
                {
                    ContourStart = totalContours,
                    ContourCount = contours,
                    OutputStart = totalTexels,
                    Width = shape.Width,
                    Height = shape.Height,
                    OriginUnits = new float2(shape.OriginUnits.x, shape.OriginUnits.y),
                    UnitsPerPixel = shape.UnitsPerPixel,
                });

                totalPoints += points;
                totalContours += contours;
                totalTexels += shape.Width * shape.Height;
            }

            if (totalTexels == 0)
            {
                AtlasDiagnostics.Add(ref AtlasDiagnostics.RasterizeTicks, startedAt);
                return false;
            }

            s_resolvedOf.Clear();
            for (int r = 0; r < requests.Count; r++) s_resolvedOf.Add(-1);
            s_resolvedUsed = 0;

            // What a font asset already knows needs no scan: those glyphs are
            // resolved now and baked once, and a request that knows of none is
            // packed as it came. Only requests that know nothing are scanned.
            if (ResolveOverlaps)
            {
                for (int r = 0; r < requests.Count; r++)
                {
                    var known = requests[r].KnownOverlaps;
                    if (known == null || known.Count == 0 || results[r].IsEmpty) continue;
                    s_resolvedOf[r] = ResolveInto(r, requests[r], known, precise);
                }
            }
            long packStart = AtlasDiagnostics.Now;
            int groupCount = Pack(requests, null, precise, true, out int packedPoints);
            AtlasDiagnostics.Add(ref AtlasDiagnostics.PackTicks, packStart);

            long jobStart = AtlasDiagnostics.Now;
            bool scanning = ResolveOverlaps && groupCount > 0;
            // Outlines drawn on the workers come first; everything reading the
            // packed contours waits for them.
            int outlines = s_packedOutlines;
            var drawn = outlines > 0
                ? OutlineJobs.Schedule(s_outlines, outlines, s_points, s_ranges, s_groups, s_bounds, s_kinds, s_outlineFailed)
                : default;
            var scan = new OverlapScanJob
            {
                Points = s_points,
                ContourRanges = s_ranges,
                ContourBounds = s_bounds,
                Groups = s_groupSpans,
                Overlapping = s_overlapping,
            };

            flight.Requests = requests;
            flight.Results = results;
            flight.Precise = precise;
            flight.Scanning = scanning;
            // Scanning first means resolving on this thread, from outlines a
            // batch with worker outlines does not have yet; it scans beside
            // the fields and re-bakes what the scan flags, as any batch can.
            flight.ScanFirst = scanning && s_expectOverlaps && outlines == 0;
            flight.Outlines = outlines;
            flight.Texels = totalTexels;
            flight.Groups = groupCount;
            flight.Flagged = 0;

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
            if (flight.ScanFirst)
            {
                scan.Schedule(groupCount, 4).Complete();
                long resolveStart = AtlasDiagnostics.Now;
                int redo = ResolveFlagged(requests, groupCount, precise, out flight.Flagged);
                if (redo > 0) Pack(requests, null, precise, false, out packedPoints);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.ResolveTicks, resolveStart);
                flight.Handle = Dispatch(packedPoints, totalTexels, s_descs.Count, precise);
            }
            else if (background)
            {
                // Nobody waits here to scan by hand, so the scan is a job too,
                // beside the fields; both only read the packed contours.
                var fields = Dispatch(packedPoints, totalTexels, s_descs.Count, precise, drawn);
                flight.ResolvedOnWorker = false;
                if (!scanning)
                {
                    flight.Handle = fields;
                }
                else
                {
                    var scanned = scan.Schedule(groupCount, 4, drawn);
                    if (OverlapsOnWorkers && !precise)
                    {
                        // And what it finds is resolved there too, behind it.
                        flight.ResolvedOnWorker = true;
                        flight.GroupRequest = s_groupRequest;
                        flight.GroupIds = s_groupIds;
                        scanned = new ResolveJob
                        {
                            Points = s_points,
                            Ranges = s_ranges,
                            Groups = s_groups,
                            Tiles = s_tiles,
                            Overlapping = s_overlapping,
                            GroupCount = groupCount,
                            Flight = System.Runtime.InteropServices.GCHandle.ToIntPtr(flight.Self),
                        }.Schedule(scanned);
                    }
                    flight.Handle = JobHandle.CombineDependencies(fields, scanned);
                }
            }
            else
            {
                flight.Handle = Dispatch(packedPoints, totalTexels, s_descs.Count, precise, drawn);
                long scanStart = AtlasDiagnostics.Now;
                if (scanning)
                {
                    drawn.Complete();
                    scan.Run(groupCount);
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.ResolveTicks, scanStart);
            }
            if (background) JobHandle.ScheduleBatchedJobs();
            AtlasDiagnostics.Add(ref AtlasDiagnostics.JobWaitTicks, jobStart);
            // Charged when the batch ends, without the time it spent in the
            // background while the caller did other work.
            flight.BeginTicks = AtlasDiagnostics.Enabled ? AtlasDiagnostics.Now - startedAt : 0;
            return true;
        }

        /// <summary>
        /// Waits for the batch <see cref="BeginBackground"/> started, re-bakes
        /// what the scan flagged, and fills its results' pixels (valid until
        /// the next batch). Only the batch's owner calls this.
        /// </summary>
        internal static void EndBatch(Flight flight)
        {
            int index = s_flights.IndexOf(flight);
            if (index < 0) return;
            s_flights.RemoveAt(index);
            Use(flight.Set);

            long endStart = AtlasDiagnostics.Now;
            long jobStart = endStart;
            // First: a job behind the fields (ResolveJob) reads the flight.
            flight.Handle.Complete();
            AtlasDiagnostics.Add(ref AtlasDiagnostics.JobWaitTicks, jobStart);
            var requests = flight.Requests;
            var results = flight.Results;
            bool precise = flight.Precise;
            flight.Requests = null;
            flight.Results = null;
            flight.Owner = null;
            if (flight.Outlines > 0 && AnyOutlineFailed(flight.Outlines)) RedoFromMainThread(flight, requests, results);

            int bytesPerTexel = precise ? 4 : 1;
            int totalTexels = flight.Texels;
            int flagged = flight.Flagged;

            // One copy for the whole batch into one reused buffer; the results
            // index into it rather than owning an array each.
            int totalBytes = totalTexels * bytesPerTexel;
            if (s_pixels.Length < totalBytes)
                s_pixels = new byte[Mathf.NextPowerOfTwo(totalBytes)];
            NativeArray<byte>.Copy(s_output, 0, s_pixels, 0, totalBytes);

            if (flight.Scanning && !flight.ScanFirst)
            {
                long resolveStart = AtlasDiagnostics.Now;
                int redo = flight.ResolvedOnWorker
                    ? TakeWorkerResolutions(flight, out flagged)
                    : ResolveFlagged(requests, flight.Groups, precise, out flagged);
                if (redo > 0) Rebake(requests, precise);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.ResolveTicks, resolveStart);
            }
            if (flight.Scanning) s_expectOverlaps = flagged > 0;
            for (int k = 0; k < s_resolvedUsed; k++) s_resolved[k].Clear();

            for (int r = 0; r < results.Count; r++)
            {
                var result = results[r];
                if (result.IsEmpty) continue;
                result.Pixels = s_pixels;
                result.PixelStart = s_descs[r].OutputStart * bytesPerTexel;
                results[r] = result;
            }

            if (AtlasDiagnostics.Enabled)
                AtlasDiagnostics.RasterizeTicks += flight.BeginTicks + (AtlasDiagnostics.Now - endStart);
            if (AtlasDiagnostics.Enabled)
            {
                AtlasDiagnostics.RasterizeCount += requests.Count;
                AtlasDiagnostics.DispatchCount++;
                AtlasDiagnostics.RasterizedPixels += totalTexels;
            }
            if (flight != s_syncFlight) s_spareFlights.Push(flight);
        }

        private static bool AnyOutlineFailed(int count)
        {
            for (int i = 0; i < count; i++)
                if (s_outlineFailed[i] != 0) return true;
            return false;
        }

        internal static bool ReportedOutlineMismatch;
        private static readonly FlatContours s_redoFlat = new FlatContours();

        /// <summary>
        /// A glyph a worker drew did not come to the statistics its tile was
        /// placed from. That is a bug (a statistic recorded under the wrong
        /// key, or a font whose outlines changed without its generation), so
        /// it is reported once; and the batch is drawn again on this thread,
        /// the way it would have been without worker outlines, and baked again.
        /// A tile whose size came out different is not written by the atlas
        /// (it checks), exactly as with any request that changed under it.
        /// </summary>
        private static void RedoFromMainThread(Flight flight, IReadOnlyList<RasterizeRequest> requests,
            List<RasterizedGlyph> results)
        {
            if (!ReportedOutlineMismatch)
            {
                ReportedOutlineMismatch = true;
                Debug.LogError("OneText: a glyph drawn on a worker thread did not match the outline its tile " +
                    "was placed for; the batch was redrawn on the main thread. Please report this.");
            }
            OutlineJobs.ForgetStats();
            if (!(requests is List<RasterizeRequest> list)) return;
            s_redoFlat.Clear();
            for (int r = 0; r < list.Count; r++)
            {
                var request = list[r];
                if (request.Lazy == null) continue;
                var lazy = request.Lazy;
                int first = s_redoFlat.ContourCount;
                for (int g = request.LazyFirst; g < request.LazyFirst + request.LazyCount; g++)
                {
                    var offset = new Vector2(lazy.Offsets[g].x, lazy.Offsets[g].y);
                    OutlineExtractor.AppendFlat(lazy.Fonts[g], lazy.Glyphs[g], lazy.PixelsPerUnit[g], offset,
                        lazy.Groups[g], s_redoFlat);
                }
                request.Lazy = null;
                request.Flat = s_redoFlat;
                request.FlatFirst = first;
                request.FlatCount = s_redoFlat.ContourCount - first;
                list[r] = request;
            }
            if (BeginBatch(requests, results, flight.Precise, background: false, flight))
                flight.Handle.Complete();
        }

        /// <summary>
        /// The tile a request will bake into, without baking it: its size, its
        /// origin and its density, or <see cref="RasterizedGlyph.IsEmpty"/>
        /// when it has no points. <see cref="RasterizeBatch"/> sizes every
        /// request through this, so a caller that places a tile before the
        /// field is computed (<see cref="GlyphAtlas"/>'s deferred bake) places
        /// exactly the tile the batch will fill. No pixels.
        /// </summary>
        internal static RasterizedGlyph Measure(in RasterizeRequest request, bool precise)
            => Measure(request, precise, out _, out _);

        private static RasterizedGlyph Measure(in RasterizeRequest request, bool precise,
            out int points, out int contours)
        {
            points = 0;
            contours = 0;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            if (request.Lazy != null)
            {
                // Each glyph's kept contours' bounds, moved: the minima and
                // maxima of those contours' own bounds, without the contours.
                var lazy = request.Lazy;
                for (int g = request.LazyFirst; g < request.LazyFirst + request.LazyCount; g++)
                {
                    var stats = lazy.Stats[g];
                    if (stats.Contours == 0) continue;
                    points += stats.Points;
                    contours += stats.Contours;
                    var b = lazy.Bounds[g];
                    min = Vector2.Min(min, new Vector2(b.x, b.y));
                    max = Vector2.Max(max, new Vector2(b.z, b.w));
                }
            }
            else if (request.Flat != null)
            {
                // The contours' own bounds, which are the same minima and
                // maxima over the same points.
                var flat = request.Flat;
                for (int c = request.FlatFirst; c < request.FlatFirst + request.FlatCount; c++)
                {
                    points += flat.Ranges[c].y;
                    contours++;
                    var b = flat.Bounds[c];
                    min = Vector2.Min(min, new Vector2(b.x, b.y));
                    max = Vector2.Max(max, new Vector2(b.z, b.w));
                }
            }
            else
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

            if (points == 0) return new RasterizedGlyph { IsEmpty = true };

            float unitsPerPixel = 1f / request.PixelsPerUnit;
            int width = Mathf.Min(MaxTileEdge,
                Mathf.CeilToInt((max.x - min.x) * request.PixelsPerUnit) + 2 * Padding);
            int height = Mathf.Min(MaxTileEdge,
                Mathf.CeilToInt((max.y - min.y) * request.PixelsPerUnit) + 2 * Padding);
            var origin = min - new Vector2(Padding, Padding) * unitsPerPixel;
            return new RasterizedGlyph
            {
                Width = width,
                Height = height,
                OriginUnits = origin,
                UnitsPerPixel = unitsPerPixel,
                Channels = precise ? 4 : 1,
            };
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
        /// With <paramref name="recordSpans"/>, also records one span per
        /// source glyph for the overlap scan, for every request that is neither
        /// resolved nor carrying <see cref="RasterizeRequest.KnownOverlaps"/>,
        /// and returns how many.
        /// </summary>
        private static int Pack(IReadOnlyList<RasterizeRequest> requests, List<int> subset, bool precise,
            bool recordSpans, out int totalPoints)
        {
            int tiles = subset?.Count ?? requests.Count;
            int totalContours = 0, totalTexels = 0, totalOutlines = 0;
            totalPoints = 0;
            for (int k = 0; k < tiles; k++)
            {
                int r = subset?[k] ?? k;
                int resolved = s_resolvedOf[r];
                if (resolved >= 0)
                {
                    var contours = s_resolved[resolved].Contours;
                    foreach (var contour in contours) totalPoints += contour.Count;
                    totalContours += contours.Count;
                }
                else if (requests[r].Lazy != null)
                {
                    var lazy = requests[r].Lazy;
                    for (int g = requests[r].LazyFirst; g < requests[r].LazyFirst + requests[r].LazyCount; g++)
                    {
                        if (lazy.Stats[g].Contours == 0) continue;
                        totalPoints += lazy.Stats[g].Points;
                        totalContours += lazy.Stats[g].Contours;
                        totalOutlines++;
                    }
                }
                else if (requests[r].Flat != null)
                {
                    totalPoints += FlatPoints(requests[r], out _);
                    totalContours += requests[r].FlatCount;
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
            s_packedOutlines = 0;
            if (totalOutlines > 0)
            {
                Grow(ref s_outlines, totalOutlines);
                Grow(ref s_outlineFailed, totalOutlines);
            }
            if (recordSpans)
            {
                Grow(ref s_groupSpans, totalContours);
                Grow(ref s_overlapping, totalContours);
                s_groupRequest.Clear();
                s_groupIds.Clear();
            }

            int pi = 0, ci = 0, texelEnd = 0, rowEnd = 0, groupCount = 0;
            for (int k = 0; k < tiles; k++)
            {
                int r = subset?[k] ?? k;
                var request = requests[r];
                var resolved = s_resolvedOf[r] >= 0 ? s_resolved[s_resolvedOf[r]] : null;
                bool spans = recordSpans && resolved == null && request.KnownOverlaps == null;
                int tileStarts = ci;
                if (resolved == null && request.Lazy != null)
                {
                    PackLazy(request, spans, r, ref pi, ref ci, ref groupCount);
                }
                else if (resolved == null && request.Flat != null)
                {
                    PackFlat(request, spans, r, ref pi, ref ci, ref groupCount);
                }
                else
                {
                    PackLists(request, resolved, precise, spans, r, ref pi, ref ci, ref groupCount);
                }

                var desc = s_descs[r];
                desc.ContourStart = tileStarts;
                desc.ContourCount = ci - tileStarts;
                // A subset is a batch of its own and its texels start at zero;
                // a full pack writes every tile where the results expect it.
                if (subset != null) desc.OutputStart = texelEnd;
                s_tiles[k] = desc;
                texelEnd += desc.Width * desc.Height;
                s_tileEnds[k] = texelEnd;
                rowEnd += desc.Height;
                s_rowEnds[k] = rowEnd;
            }
            return groupCount;
        }

        /// <summary>
        /// <see cref="PackFlat"/> for a request whose outlines the workers
        /// draw: the same ranges of the buffers, one span per glyph, laid out
        /// from the glyphs' statistics, and one <see cref="OutlineItem"/> per
        /// glyph telling the job where its points and contours go. The job
        /// writes the points, ranges, groups, bounds and kinds PackFlat would
        /// have copied.
        /// </summary>
        private static void PackLazy(in RasterizeRequest request, bool spans, int r,
            ref int pi, ref int ci, ref int groupCount)
        {
            var lazy = request.Lazy;
            FontData lastFont = null;
            System.IntPtr handles = System.IntPtr.Zero;
            for (int g = request.LazyFirst; g < request.LazyFirst + request.LazyCount; g++)
            {
                var stats = lazy.Stats[g];
                if (stats.Contours == 0) continue;
                var font = lazy.Fonts[g];
                if (font != lastFont)
                {
                    handles = OutlineJobs.HandlesFor(font);
                    lastFont = font;
                }
                if (spans)
                {
                    s_groupSpans[groupCount++] = new int2(ci, stats.Contours);
                    s_groupRequest.Add(r);
                    s_groupIds.Add(lazy.Groups[g]);
                }
                s_outlines[s_packedOutlines++] = new OutlineItem
                {
                    Fonts = handles,
                    Glyph = lazy.Glyphs[g],
                    PixelsPerUnit = lazy.PixelsPerUnit[g],
                    Flatness = lazy.Flatness[g],
                    Offset = lazy.Offsets[g],
                    Group = lazy.Groups[g],
                    PointStart = pi,
                    ContourStart = ci,
                    Points = stats.Points,
                    Contours = stats.Contours,
                    RawBounds = stats.Bounds,
                };
                // A font whose axes moved since the glyph was placed would draw
                // another outline; the job would notice, but cannot be asked to.
                if (font.Generation != lazy.Generations[g])
                {
                    var stale = s_outlines[s_packedOutlines - 1];
                    stale.Points = -1;
                    s_outlines[s_packedOutlines - 1] = stale;
                }
                pi += stats.Points;
                ci += stats.Contours;
            }
        }

        /// <summary>Points of a flat request, and the index of its first.</summary>
        private static int FlatPoints(in RasterizeRequest request, out int firstPoint)
        {
            firstPoint = 0;
            if (request.FlatCount == 0) return 0;
            var flat = request.Flat;
            firstPoint = flat.Ranges[request.FlatFirst].x;
            var last = flat.Ranges[request.FlatFirst + request.FlatCount - 1];
            return last.x + last.y - firstPoint;
        }

        /// <summary>
        /// <see cref="PackLists"/> for a flat request: the points are one copy,
        /// the bounds were taken when they were made. Single-channel only.
        /// </summary>
        private static void PackFlat(in RasterizeRequest request, bool spans, int r,
            ref int pi, ref int ci, ref int groupCount)
        {
            var flat = request.Flat;
            int points = FlatPoints(request, out int firstPoint);
            if (points > 0) NativeArray<float2>.Copy(flat.Points, firstPoint, s_points, pi, points);
            int tileStart = ci, currentGroup = int.MinValue;
            for (int c = request.FlatFirst; c < request.FlatFirst + request.FlatCount; c++)
            {
                int group = flat.Groups[c];
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
                s_kinds[ci] = ContourKind.Both;
                s_ranges[ci] = new int2(pi + (flat.Ranges[c].x - firstPoint), flat.Ranges[c].y);
                s_bounds[ci++] = flat.Bounds[c];
            }
            pi += points;
        }

        private static void PackLists(in RasterizeRequest request, ResolvedContours resolved, bool precise,
            bool spans, int r, ref int pi, ref int ci, ref int groupCount)
        {
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
        }

        /// <summary>
        /// Schedules the field jobs over whatever is packed in the buffers: the
        /// first <paramref name="tileCount"/> tiles, their contours and points.
        /// The caller completes the handle.
        /// </summary>
        /// <summary>
        /// Off bakes the single-channel field a texel per job index
        /// (<see cref="SdfBatchJob"/>); on, a tile row per index, choosing
        /// each row's candidate segments once (<see cref="SdfRowJob"/>). Same
        /// bytes. Only with <see cref="Cull"/>: the unculled field is the
        /// reference the culled one is tested against and stays the texel job.
        /// </summary>
        public static bool RowJob = true;

        /// <summary>
        /// The row job reproduces the texel job's compiled arithmetic as ARM64
        /// executes it (its fused operations included), and has been held to
        /// it there. Elsewhere the texel job's own code generation differs and
        /// has not been measured, so it keeps baking.
        /// </summary>
        private static readonly bool s_rowJobMatches =
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64;

        private static JobHandle Dispatch(int totalPoints, int totalTexels, int tileCount, bool precise,
            JobHandle dependsOn = default)
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
            if (!precise && RowJob && Cull && s_rowJobMatches)
            {
                return new SdfRowJob
                {
                    Points = s_points,
                    ContourRanges = s_ranges,
                    ContourGroups = s_groups,
                    ContourBounds = s_bounds,
                    ContourKinds = s_kinds,
                    Tiles = s_tiles,
                    RowEnds = s_rowEnds,
                    TileCount = tileCount,
                    SpreadPixels = SpreadPixels,
                    Output = s_output,
                }.Schedule(s_rowEnds[tileCount - 1], 2, dependsOn);
            }

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
                }.Schedule(totalTexels, 128, dependsOn);
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
            }.Schedule(totalTexels, 128, dependsOn);

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
        /// Off resolves the overlaps a background batch's scan found on the
        /// main thread when the batch is collected: ~85 us of managed geometry
        /// per glyph, in the frame's wait for its tiles. On, a job behind the
        /// scan resolves them on a worker (<see cref="ResolveJob"/>, managed
        /// code like the main thread's, so the same arithmetic), and the
        /// collection only re-bakes them. Same pieces, same tiles.
        /// </summary>
        public static bool OverlapsOnWorkers = true;

        /// <summary>
        /// <see cref="ResolveFlagged"/> on a worker, for a batch in flight:
        /// reads only the batch's own buffers and requests, and writes only
        /// into the flight's own lists.
        /// </summary>
        internal static void ResolveOnWorker(Flight flight, in ResolveJob job)
        {
            var requests = flight.Requests;
            flight.WorkerFlagged = 0;
            flight.WorkerRequests.Clear();
            int used = 0;
            for (int g = 0; g < job.GroupCount; g++)
            {
                if (job.Overlapping[g] == 0) continue;
                flight.WorkerFlagged++;
                int r = flight.GroupRequest[g];
                if (used == 0 || flight.WorkerRequests[used - 1] != r)
                {
                    flight.WorkerRequests.Add(r);
                    if (flight.WorkerGroups.Count == used) flight.WorkerGroups.Add(new List<int>());
                    flight.WorkerGroups[used].Clear();
                    used++;
                }
                flight.WorkerGroups[used - 1].Add(flight.GroupIds[g]);
            }
            for (int k = 0; k < used; k++)
            {
                if (flight.WorkerResolved.Count == k) flight.WorkerResolved.Add(new ResolvedContours());
                int r = flight.WorkerRequests[k];
                var request = requests[r];
                if (request.Lazy != null || request.Flat != null)
                {
                    // The contours where the batch packed them, which are the
                    // flat buffer's (or the job's) points, as lists.
                    var listed = request;
                    var tile = job.Tiles[r];
                    listed.Contours = new List<List<Vector2>>(tile.ContourCount);
                    listed.Groups = new List<int>(tile.ContourCount);
                    for (int c = tile.ContourStart; c < tile.ContourStart + tile.ContourCount; c++)
                    {
                        var range = job.Ranges[c];
                        var line = new List<Vector2>(range.y);
                        for (int i = range.x; i < range.x + range.y; i++)
                            line.Add(new Vector2(job.Points[i].x, job.Points[i].y));
                        listed.Contours.Add(line);
                        listed.Groups.Add(job.Groups[c]);
                    }
                    listed.Lazy = null;
                    listed.Flat = null;
                    ContourOverlaps.Resolve(listed, flight.WorkerGroups[k], false, flight.WorkerResolved[k]);
                }
                else
                {
                    ContourOverlaps.Resolve(request, flight.WorkerGroups[k], false, flight.WorkerResolved[k]);
                }
            }
            flight.WorkerResolvedCount = used;
        }

        /// <summary>
        /// What <see cref="ResolveOnWorker"/> resolved, moved into this batch's
        /// resolution table as <see cref="ResolveFlagged"/> would have filled it.
        /// </summary>
        private static int TakeWorkerResolutions(Flight flight, out int flagged)
        {
            flagged = flight.WorkerFlagged;
            s_redoRequests.Clear();
            for (int j = 0; j < flight.WorkerResolvedCount; j++)
            {
                int r = flight.WorkerRequests[j];
                int k = s_resolvedUsed++;
                if (s_resolved.Count == k) s_resolved.Add(new ResolvedContours());
                (s_resolved[k], flight.WorkerResolved[j]) = (flight.WorkerResolved[j], s_resolved[k]);
                s_resolvedOf[r] = k;
                s_redoRequests.Add(r);
                if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OverlapRebakes++;
            }
            flight.WorkerResolvedCount = 0;
            return s_redoRequests.Count;
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
                int r = s_redoRequests[k];
                s_resolvedOf[r] = ResolveInto(r, requests[r], s_redoGroups[k], precise);
            }
            return s_redoRequests.Count;
        }

        /// <summary>
        /// Resolves the named groups of one request into the next free entry of
        /// <see cref="s_resolved"/> and returns its index.
        /// </summary>
        private static int ResolveInto(int r, in RasterizeRequest request, List<int> groups, bool precise)
        {
            int k = s_resolvedUsed++;
            if (s_resolved.Count == k) s_resolved.Add(new ResolvedContours());
            if (request.Lazy != null)
            {
                // Drawn on the workers, so the contours exist only where the
                // job put them: the request's range of the packed buffers,
                // which is what a flat request's lists would have held.
                var listed = request;
                var tile = s_tiles[r];
                listed.Contours = new List<List<Vector2>>(tile.ContourCount);
                listed.Groups = new List<int>(tile.ContourCount);
                for (int c = tile.ContourStart; c < tile.ContourStart + tile.ContourCount; c++)
                {
                    var range = s_ranges[c];
                    var line = new List<Vector2>(range.y);
                    for (int i = range.x; i < range.x + range.y; i++) line.Add(new Vector2(s_points[i].x, s_points[i].y));
                    listed.Contours.Add(line);
                    listed.Groups.Add(s_groups[c]);
                }
                listed.Lazy = null;
                ContourOverlaps.Resolve(listed, groups, precise, s_resolved[k]);
            }
            else if (request.Flat != null)
            {
                // The resolver reads lists. A handful of glyphs a frame, at
                // most, come this way.
                var listed = request;
                listed.Contours = new List<List<Vector2>>(request.FlatCount);
                listed.Groups = new List<int>(request.FlatCount);
                request.Flat.ToLists(request.FlatFirst, request.FlatCount, listed.Contours, listed.Groups);
                listed.Flat = null;
                ContourOverlaps.Resolve(listed, groups, precise, s_resolved[k]);
            }
            else
            {
                ContourOverlaps.Resolve(request, groups, precise, s_resolved[k]);
            }
            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OverlapRebakes++;
            return k;
        }

        /// <summary>Entries of <see cref="s_resolved"/> this batch has filled.</summary>
        private static int s_resolvedUsed;

        /// <summary>
        /// Bakes the resolved requests again as a batch of their own and writes
        /// them over their slots in the batch's pixels. Their tiles keep the
        /// size the first bake gave them: the ink is the same, only the
        /// distance inside it changes.
        /// </summary>
        private static void Rebake(IReadOnlyList<RasterizeRequest> requests, bool precise)
        {
            Pack(requests, s_redoRequests, precise, false, out int points);
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

        private static List<SdfTileDesc> s_descs = new List<SdfTileDesc>();
        private static byte[] s_pixels = System.Array.Empty<byte>();

        // Edge colours are decided in managed code (the corner test wants the
        // contour as a list) and go over to the job in one copy per batch.
        private static byte[] s_flagBytes = System.Array.Empty<byte>();

        private static NativeArray<float2> s_points;
        private static NativeArray<int2> s_ranges;

        // The glyphs a batch draws on the workers, and each one's verdict.
        private static NativeArray<OutlineItem> s_outlines;
        private static NativeArray<int> s_outlineFailed;
        private static int s_packedOutlines;
        private static NativeArray<int> s_groups;
        private static NativeArray<float4> s_bounds;
        private static NativeArray<byte> s_kinds;

        // One span of contours per source glyph, the request and group id it
        // came from, and the overlap scan's verdict on it.
        private static NativeArray<int2> s_groupSpans;
        private static NativeArray<byte> s_overlapping;
        private static List<int> s_groupRequest = new List<int>();
        private static List<int> s_groupIds = new List<int>();

        // The requests baked again with their overlaps resolved, the groups of
        // each that were flagged, what the resolution made of them, and for
        // every request of the batch which of those it is (-1: none).
        private static readonly List<int> s_redoRequests = new List<int>();
        private static List<int> s_resolvedOf = new List<int>();
        private static readonly List<List<int>> s_redoGroups = new List<List<int>>();
        private static List<ResolvedContours> s_resolved = new List<ResolvedContours>();
        private static NativeArray<SdfTileDesc> s_tiles;
        private static NativeArray<int> s_tileEnds;
        private static NativeArray<int> s_rowEnds;
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

        /// <summary>
        /// Everything one batch packs into and bakes from. The statics above
        /// are the set in use (<see cref="Use"/>): the synchronous path and the
        /// unpipelined background batch use <see cref="s_syncSet"/>, every
        /// pipelined background batch a set of its own, so that one can bake
        /// while the next is packed. Swapping is a copy of struct handles; a
        /// job that was scheduled with a set's arrays keeps reading them.
        /// </summary>
        internal sealed class BufferSet
        {
            internal NativeArray<float2> Points;
            internal NativeArray<int2> Ranges, GroupSpans;
            internal NativeArray<int> Groups, TileEnds, RowEnds;
            internal NativeArray<float4> Bounds;
            internal NativeArray<byte> Kinds, Overlapping, Output, Flags, Marks, Winners;
            internal NativeArray<SdfTileDesc> Tiles;
            internal NativeArray<float> Orientation;
            internal NativeArray<OutlineItem> Outlines;
            internal NativeArray<int> OutlineFailed;
            internal List<SdfTileDesc> Descs = new List<SdfTileDesc>();
            internal List<int> GroupRequest = new List<int>(), GroupIds = new List<int>(), ResolvedOf = new List<int>();
            internal List<ResolvedContours> Resolved = new List<ResolvedContours>();
            internal int ResolvedUsed;

            internal void Release()
            {
                GlyphRasterizer.Release(ref Points);
                GlyphRasterizer.Release(ref Ranges);
                GlyphRasterizer.Release(ref GroupSpans);
                GlyphRasterizer.Release(ref Groups);
                GlyphRasterizer.Release(ref TileEnds);
                GlyphRasterizer.Release(ref RowEnds);
                GlyphRasterizer.Release(ref Bounds);
                GlyphRasterizer.Release(ref Kinds);
                GlyphRasterizer.Release(ref Overlapping);
                GlyphRasterizer.Release(ref Output);
                GlyphRasterizer.Release(ref Flags);
                GlyphRasterizer.Release(ref Marks);
                GlyphRasterizer.Release(ref Winners);
                GlyphRasterizer.Release(ref Tiles);
                GlyphRasterizer.Release(ref Orientation);
                GlyphRasterizer.Release(ref Outlines);
                GlyphRasterizer.Release(ref OutlineFailed);
            }
        }

        // Holds the statics' own lists from the start, so its fields describe
        // it even before it has ever stopped being current.
        private static readonly BufferSet s_syncSet = StoredSet();

        private static BufferSet StoredSet()
        {
            var set = new BufferSet();
            Store(set);
            return set;
        }
        private static BufferSet s_current = s_syncSet;

        // The synchronous path's flight record, never pooled.
        private static readonly Flight s_syncFlight = new Flight { Set = s_syncSet };

        /// <summary>Makes <paramref name="set"/> the one the statics hold, keeping the previous one's state.</summary>
        private static void Use(BufferSet set)
        {
            if (s_current == set) return;
            Store(s_current);
            s_current = set;
            s_points = set.Points; s_ranges = set.Ranges; s_groupSpans = set.GroupSpans; s_groups = set.Groups;
            s_tileEnds = set.TileEnds; s_rowEnds = set.RowEnds; s_bounds = set.Bounds; s_kinds = set.Kinds;
            s_overlapping = set.Overlapping; s_output = set.Output; s_flags = set.Flags; s_marks = set.Marks;
            s_winners = set.Winners; s_tiles = set.Tiles; s_orientation = set.Orientation;
            s_outlines = set.Outlines; s_outlineFailed = set.OutlineFailed;
            s_descs = set.Descs; s_groupRequest = set.GroupRequest; s_groupIds = set.GroupIds;
            s_resolvedOf = set.ResolvedOf; s_resolved = set.Resolved; s_resolvedUsed = set.ResolvedUsed;
        }

        private static void Store(BufferSet set)
        {
            set.Points = s_points; set.Ranges = s_ranges; set.GroupSpans = s_groupSpans; set.Groups = s_groups;
            set.TileEnds = s_tileEnds; set.RowEnds = s_rowEnds; set.Bounds = s_bounds; set.Kinds = s_kinds;
            set.Overlapping = s_overlapping; set.Output = s_output; set.Flags = s_flags; set.Marks = s_marks;
            set.Winners = s_winners; set.Tiles = s_tiles; set.Orientation = s_orientation;
            set.Outlines = s_outlines; set.OutlineFailed = s_outlineFailed;
            set.Descs = s_descs; set.GroupRequest = s_groupRequest; set.GroupIds = s_groupIds;
            set.ResolvedOf = s_resolvedOf; set.Resolved = s_resolved; set.ResolvedUsed = s_resolvedUsed;
        }

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
            Grow(ref s_rowEnds, tiles);
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
            // Nothing may still be reading or writing them.
            foreach (var flight in s_flights)
            {
                flight.Handle.Complete();
                flight.Requests = null;
                flight.Results = null;
                flight.Owner = null;
                if (flight != s_syncFlight) s_spareFlights.Push(flight);
            }
            s_flights.Clear();
            // The statics are one set's arrays; put them back where they
            // belong, then release every set.
            Use(s_syncSet);
            foreach (var spare in s_spareFlights)
                if (spare.Set != null && spare.Set != s_syncSet) spare.Set.Release();
            Release(ref s_points);
            Release(ref s_ranges);
            Release(ref s_groups);
            Release(ref s_bounds);
            Release(ref s_kinds);
            Release(ref s_groupSpans);
            Release(ref s_overlapping);
            Release(ref s_tiles);
            Release(ref s_tileEnds);
            Release(ref s_rowEnds);
            Release(ref s_output);
            Release(ref s_flags);
            Release(ref s_orientation);
            Release(ref s_marks);
            Release(ref s_winners);
            Release(ref s_outlines);
            Release(ref s_outlineFailed);
            OutlineJobs.ReleaseAll();
            s_pixels = System.Array.Empty<byte>();
            s_flagBytes = System.Array.Empty<byte>();
            // The sync set holds copies of the arrays just disposed; a copy
            // still answers IsCreated, so it is overwritten with the defaults.
            Store(s_syncSet);
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

    /// <summary>
    /// Resolves the overlaps a batch's scan found, on a worker, behind the
    /// scan (<see cref="GlyphRasterizer.OverlapsOnWorkers"/>). Managed on
    /// purpose, never Burst: <see cref="ContourOverlaps.Resolve"/> is managed
    /// code, and running the same code here is what makes the pieces the main
    /// thread's to the bit. It reads the batch's packed buffers (declared here,
    /// so the safety system knows) and, through the flight, the batch's own
    /// requests and lists, none of which anything else touches until the batch
    /// is collected.
    /// </summary>
    internal struct ResolveJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Points;
        [ReadOnly] public NativeArray<int2> Ranges;
        [ReadOnly] public NativeArray<int> Groups;
        [ReadOnly] public NativeArray<SdfTileDesc> Tiles;
        [ReadOnly] public NativeArray<byte> Overlapping;
        public int GroupCount;
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public System.IntPtr Flight;

        public void Execute()
        {
            var flight = (GlyphRasterizer.Flight)System.Runtime.InteropServices.GCHandle.FromIntPtr(Flight).Target;
            GlyphRasterizer.ResolveOnWorker(flight, this);
        }
    }
}
