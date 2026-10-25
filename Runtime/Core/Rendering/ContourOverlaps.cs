using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace OneText
{
    /// <summary>What a packed contour contributes to the field it is baked into.</summary>
    internal static class ContourKind
    {
        /// <summary>Its edges are measured for the distance.</summary>
        public const byte Distance = 1;

        /// <summary>Its edges count towards the winding that signs the distance.</summary>
        public const byte Winding = 2;

        /// <summary>A contour as the font drew it: both. Everything but a resolved overlap.</summary>
        public const byte Both = Distance | Winding;
    }

    /// <summary>
    /// The geometry both halves of overlap resolution agree on: when two
    /// segments meet, and which way a segment winds past a point. Kept apart
    /// from <see cref="ContourOverlaps"/> so the Burst scan can call it without
    /// touching that class's managed pools.
    /// </summary>
    internal static class OverlapGeometry
    {
        /// <summary>
        /// How close, in font units, two edges have to come to count as
        /// meeting. Outlines are integers in font units and the flattening's
        /// points are not, so exact zero tests would catch an abutment drawn
        /// with straight lines and miss the same one drawn with a curve. A
        /// sixty-fourth of a unit is a few thousandths of a texel at the
        /// largest bucket, far below anything the field can show.
        /// </summary>
        public const float Tolerance = 1f / 64f;

        /// <summary>
        /// The winding <paramref name="a"/>..<paramref name="b"/> adds at
        /// <paramref name="p"/>: exactly the crossing rule the field jobs use,
        /// so a resolved outline is signed by the same arithmetic it was
        /// classified with.
        /// </summary>
        public static int Crossing(float2 a, float2 b, float2 p)
        {
            if (a.y <= p.y)
            {
                if (b.y > p.y && (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) > 0f) return 1;
            }
            else if (b.y <= p.y && (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) < 0f)
            {
                return -1;
            }
            return 0;
        }

        /// <summary>Signed distance of <paramref name="p"/> from the line a..b, positive to its left.</summary>
        public static float Side(float2 a, float2 b, float2 p) =>
            Side(a, b, math.rsqrt(math.max(math.lengthsq(b - a), 1e-12f)), p);

        /// <inheritdoc cref="Side(float2, float2, float2)"/>
        /// <param name="inverseLength">One over the length of a..b, when the caller already has it.</param>
        public static float Side(float2 a, float2 b, float inverseLength, float2 p) =>
            ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x)) * inverseLength;

        /// <summary>One over the length of a..b, for <see cref="Side(float2, float2, float, float2)"/>.</summary>
        public static float InverseLength(float2 a, float2 b) =>
            math.rsqrt(math.max(math.lengthsq(b - a), 1e-12f));

        /// <summary>Where along a..b, as a fraction of it, <paramref name="p"/> projects.</summary>
        public static float Along(float2 a, float2 b, float2 p) =>
            math.dot(p - a, b - a) / math.max(math.lengthsq(b - a), 1e-12f);

        /// <summary>
        /// Whether two segments cross or touch, within <see cref="Tolerance"/>.
        /// Touching counts: an abutment (a vertex on another edge, two edges
        /// lying along each other) buries edges exactly as a crossing does.
        /// </summary>
        public static bool Meet(float2 a1, float2 b1, float inverse1, float2 a2, float2 b2, float inverse2)
        {
            float d1 = Side(a2, b2, inverse2, a1), d2 = Side(a2, b2, inverse2, b1);
            float d3 = Side(a1, b1, inverse1, a2), d4 = Side(a1, b1, inverse1, b2);
            if (Straddles(d1, d2) && Straddles(d3, d4)) return true;
            float slack1 = Tolerance * inverse1;
            float slack2 = Tolerance * inverse2;
            return (math.abs(d1) <= Tolerance && Within(Along(a2, b2, a1), slack2)) ||
                   (math.abs(d2) <= Tolerance && Within(Along(a2, b2, b1), slack2)) ||
                   (math.abs(d3) <= Tolerance && Within(Along(a1, b1, a2), slack1)) ||
                   (math.abs(d4) <= Tolerance && Within(Along(a1, b1, b2), slack1));
        }

        public static bool Straddles(float d1, float d2) =>
            (d1 > Tolerance && d2 < -Tolerance) || (d1 < -Tolerance && d2 > Tolerance);

        public static bool Within(float t, float slack) => t >= -slack && t <= 1f + slack;
    }

    /// <summary>
    /// Finds the glyphs of a batch whose outlines overlap themselves, one source
    /// glyph per index, without changing anything.
    ///
    /// This is the half every glyph pays for, so it is built to cost next to
    /// nothing and to run beside the field jobs rather than ahead of them: the
    /// batch bakes as if no glyph overlapped, and only the few this flags are
    /// baked again with their buried edges taken out. Most fonts ship with
    /// overlaps removed, and for those this is the whole of the cost.
    ///
    /// A glyph overlaps when two of its segments that are not neighbours meet
    /// (two strokes crossing, a stroke abutting another, one contour looping
    /// over itself), or when a whole contour lies inside ink wound the same way
    /// (a dot drawn inside a stem). Segments are swept in order of their lowest
    /// point, so a pair is only looked at if it shares a band of height:
    /// roughly the segments times the edges a scanline crosses, rather than the
    /// segments squared.
    /// </summary>
    [BurstCompile]
    internal struct OverlapScanJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Points;
        [ReadOnly] public NativeArray<int2> ContourRanges;
        [ReadOnly] public NativeArray<float4> ContourBounds;

        /// <summary>First contour and contour count of each source glyph.</summary>
        [ReadOnly] public NativeArray<int2> Groups;

        /// <summary>One byte per group: 1 when it has edges buried in its own ink.</summary>
        [WriteOnly] public NativeArray<byte> Overlapping;

        private struct Segment
        {
            public float2 A, B;
            public float Low, High, InverseLength;
            public int Contour, Ordinal, Last;
        }

        private struct ByLow : IComparer<Segment>
        {
            public int Compare(Segment x, Segment y) => x.Low.CompareTo(y.Low);
        }

        public void Execute(int index)
        {
            int2 group = Groups[index];
            int first = group.x, last = group.x + group.y;
            Overlapping[index] = (byte)(Meets(first, last) || Nested(first, last) ? 1 : 0);
        }

        private bool Meets(int first, int last)
        {
            int total = 0;
            for (int c = first; c < last; c++) total += math.max(ContourRanges[c].y - 1, 0);
            if (total < 2) return false;

            const float tolSq = OverlapGeometry.Tolerance * OverlapGeometry.Tolerance;
            var segments = new NativeArray<Segment>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int count = 0;
            for (int c = first; c < last; c++)
            {
                int start = ContourRanges[c].x, points = ContourRanges[c].y;
                int from = count, ordinal = 0;
                for (int i = 0; i < points - 1; i++)
                {
                    float2 a = Points[start + i], b = Points[start + i + 1];
                    // A point, not an edge: the close-path's repeated start, or
                    // a curve flattened onto itself. Its neighbours meet across
                    // it, which is why ordinals skip it.
                    if (math.lengthsq(b - a) <= tolSq) continue;
                    segments[count++] = new Segment
                    {
                        A = a, B = b, Low = math.min(a.y, b.y), High = math.max(a.y, b.y),
                        InverseLength = OverlapGeometry.InverseLength(a, b),
                        Contour = c, Ordinal = ordinal++,
                    };
                }
                // A closed contour's last edge meets its first at the start point.
                bool closed = points > 2 && math.lengthsq(Points[start] - Points[start + points - 1]) <= tolSq;
                int lastOrdinal = closed && ordinal > 2 ? ordinal - 1 : -1;
                for (int k = from; k < count; k++)
                {
                    var s = segments[k];
                    s.Last = lastOrdinal;
                    segments[k] = s;
                }
            }

            var sorted = segments.GetSubArray(0, count);
            sorted.Sort(new ByLow());
            for (int i = 0; i < count; i++)
            {
                var s = sorted[i];
                float left = math.min(s.A.x, s.B.x) - OverlapGeometry.Tolerance;
                float right = math.max(s.A.x, s.B.x) + OverlapGeometry.Tolerance;
                float top = s.High + OverlapGeometry.Tolerance;
                for (int j = i + 1; j < count; j++)
                {
                    var t = sorted[j];
                    if (t.Low > top) break;
                    if (math.max(t.A.x, t.B.x) < left || math.min(t.A.x, t.B.x) > right) continue;
                    if (Adjacent(s, t)) continue;
                    if (OverlapGeometry.Meet(s.A, s.B, s.InverseLength, t.A, t.B, t.InverseLength))
                    {
                        segments.Dispose();
                        return true;
                    }
                }
            }
            segments.Dispose();
            return false;
        }

        private static bool Adjacent(in Segment s, in Segment t)
        {
            if (s.Contour != t.Contour) return false;
            int gap = math.abs(s.Ordinal - t.Ordinal);
            return gap == 1 || (s.Last > 0 && gap == s.Last);
        }

        /// <summary>
        /// A contour that meets nothing is buried whole or not at all: both its
        /// sides keep the same winding from the others all the way round, so
        /// one point on it decides. Only asked once <see cref="Meets"/> has
        /// said no, which is what makes one point enough.
        /// </summary>
        private bool Nested(int first, int last)
        {
            if (last - first < 2) return false;
            const float tolSq = OverlapGeometry.Tolerance * OverlapGeometry.Tolerance;
            for (int c = first; c < last; c++)
            {
                int start = ContourRanges[c].x, points = ContourRanges[c].y;
                float area = 0f;
                float2 probe = default;
                bool found = false;
                for (int i = 0; i < points - 1; i++)
                {
                    float2 a = Points[start + i], b = Points[start + i + 1];
                    area += a.x * b.y - b.x * a.y;
                    if (!found && math.lengthsq(b - a) > tolSq)
                    {
                        probe = (a + b) * 0.5f;
                        found = true;
                    }
                }
                if (!found || math.abs(area) < 1e-6f) continue;

                int winding = 0;
                for (int d = first; d < last; d++)
                {
                    if (d == c) continue;
                    float4 bounds = ContourBounds[d];
                    if (probe.y < bounds.y || probe.y > bounds.w || probe.x > bounds.z) continue;
                    int dStart = ContourRanges[d].x, dPoints = ContourRanges[d].y;
                    for (int i = 0; i < dPoints - 1; i++)
                        winding += OverlapGeometry.Crossing(Points[dStart + i], Points[dStart + i + 1], probe);
                }
                // Outside this contour the others wind `winding`; inside, its
                // own turn is added. Ink on both sides is a buried edge.
                int own = area > 0f ? 1 : -1;
                if (winding != 0 && winding + own != 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The contours one request is rasterized from once its overlaps are
    /// resolved: the original contours for the winding, and the visible pieces
    /// of them for the distance. Lists it rented are handed back by
    /// <see cref="Clear"/>; the request's own contours are referenced, never
    /// copied or recycled.
    /// </summary>
    internal sealed class ResolvedContours
    {
        public readonly List<List<Vector2>> Contours = new List<List<Vector2>>();
        public readonly List<int> Groups = new List<int>();
        public readonly List<byte> Kinds = new List<byte>();

        /// <summary>
        /// Multi-channel edge flags, one byte per point of every contour in
        /// order, when the request is precise; empty otherwise.
        /// </summary>
        public readonly List<byte> Flags = new List<byte>();

        private readonly List<List<Vector2>> _rented = new List<List<Vector2>>();
        private readonly List<List<Vector2>> _spare = new List<List<Vector2>>();

        public List<Vector2> Rent()
        {
            List<Vector2> list;
            if (_spare.Count > 0)
            {
                list = _spare[_spare.Count - 1];
                _spare.RemoveAt(_spare.Count - 1);
                list.Clear();
            }
            else
            {
                list = new List<Vector2>();
            }
            _rented.Add(list);
            return list;
        }

        public void Add(List<Vector2> contour, int group, byte kind)
        {
            Contours.Add(contour);
            Groups.Add(group);
            Kinds.Add(kind);
        }

        public void Clear()
        {
            Contours.Clear();
            Groups.Clear();
            Kinds.Clear();
            Flags.Clear();
            _spare.AddRange(_rented);
            _rented.Clear();
        }
    }

    /// <summary>
    /// Takes out of the distance the edges a glyph buries inside its own ink.
    ///
    /// Overlapping contours are valid TrueType, and most fonts exported from
    /// variable sources ship them: every stroke its own outline, crossing the
    /// next. The nonzero winding rule signs such a glyph correctly. The
    /// distance does not come from the winding, though; it is the minimum over
    /// every edge, and an edge buried inside another stroke is as near as any.
    /// Along it the field falls to the outline value in the middle of solid
    /// ink, and a large glyph with an outline shows the notches, specks and
    /// faint lines that follow its joints. Clusters already avoid this between
    /// glyphs (signed distance per glyph, then a union); this is the same
    /// repair inside one.
    ///
    /// Every segment is split where another meets it, each piece between two
    /// splits is classified by the winding just either side of it, and a piece
    /// with ink on both sides is buried. The original contours still sign the
    /// field, as wound-only contours, so the ink is exactly what the font drew;
    /// only the visible pieces are measured. That is a boolean union as far as
    /// the field can tell, without having to rebuild an outline from the
    /// pieces.
    ///
    /// Managed and unhurried, because it runs only for what
    /// <see cref="OverlapScanJob"/> flagged.
    /// </summary>
    internal static class ContourOverlaps
    {
        /// <summary>
        /// How far either side of a piece the winding is probed, in font units.
        /// Past the meeting tolerance, so two edges lying along each other are
        /// both stepped over, and far below any stroke a font would draw.
        /// </summary>
        private const float ProbeReach = 4f * OverlapGeometry.Tolerance;

        private struct Segment
        {
            public float2 A, B;
            public float Low, High;
            public int Contour, Index, Ordinal, Last;
        }

        private struct Split
        {
            public int Contour, Index;
            public float T;
        }

        private sealed class ByLow : IComparer<Segment>
        {
            public int Compare(Segment x, Segment y) => x.Low.CompareTo(y.Low);
        }

        private sealed class ByPlace : IComparer<Split>
        {
            public int Compare(Split x, Split y)
            {
                if (x.Contour != y.Contour) return x.Contour.CompareTo(y.Contour);
                if (x.Index != y.Index) return x.Index.CompareTo(y.Index);
                return x.T.CompareTo(y.T);
            }
        }

        private static readonly ByLow s_byLow = new ByLow();
        private static readonly ByPlace s_byPlace = new ByPlace();
        private static readonly List<Segment> s_segments = new List<Segment>();
        private static readonly List<Split> s_splits = new List<Split>();
        private static readonly List<List<Vector2>> s_group = new List<List<Vector2>>();
        private static readonly List<Vector2> s_split = new List<Vector2>();
        private static readonly List<bool> s_marks = new List<bool>();
        private static readonly List<bool> s_vertexMarks = new List<bool>();
        private static readonly List<int> s_boundaries = new List<int>();
        private static readonly List<Piece> s_pieces = new List<Piece>();

        private enum Piece : byte { Visible, Buried, Point }
        private static byte[] s_flags = new byte[256];

        /// <summary>
        /// Bumped whenever what counts as an overlap changes (the tolerance, the
        /// probe, the scan), so an index a font asset recorded under the old
        /// rule is not trusted under the new one.
        /// </summary>
        public const int IndexVersion = 1;

        /// <summary>
        /// The density an index is computed at, in pixels per em: the largest
        /// bucket the atlas bakes, whose flattening is the closest to the true
        /// curves. Whether two strokes meet does not depend on the size they
        /// are drawn at; only a near-tangent touch could come and go with the
        /// flattening, and the finest one is the best guess at the truth.
        /// </summary>
        private const float IndexPixelsPerEm = 256f;

        /// <summary>
        /// Every glyph of <paramref name="font"/>'s default instance whose own
        /// contours overlap, sorted: what a font asset records when it is made,
        /// so a face drawn from it never has to look for overlaps at runtime.
        /// The same scan the rasterizer runs, over the whole font at once, in
        /// batches across the job workers.
        /// </summary>
        /// <param name="glyphCount">Glyphs in the face (see <see cref="GlyphCountOf"/>).</param>
        /// <param name="progress">Told the fraction done after each batch, for a progress bar.</param>
        public static int[] FindOverlappingGlyphs(FontData font, int glyphCount,
            System.Action<float> progress = null)
        {
            var found = new List<int>();
            if (font == null || !font.IsValid || glyphCount <= 0) return found.ToArray();

            const int batch = 512;
            float pixelsPerUnit = IndexPixelsPerEm / font.UnitsPerEm;
            var outlines = new GlyphOutline[batch];
            for (int i = 0; i < batch; i++) outlines[i] = new GlyphOutline();

            for (int first = 0; first < glyphCount; first += batch)
            {
                int count = System.Math.Min(batch, glyphCount - first);
                int points = 0, contours = 0;
                for (int i = 0; i < count; i++)
                {
                    outlines[i].Clear();
                    OutlineExtractor.Extract(font, (uint)(first + i), outlines[i], pixelsPerUnit);
                    foreach (var contour in outlines[i].Contours)
                    {
                        if (contour.Count < 2) continue;
                        points += contour.Count;
                        contours++;
                    }
                }

                var pointArray = new NativeArray<float2>(System.Math.Max(points, 1), Allocator.TempJob);
                var ranges = new NativeArray<int2>(System.Math.Max(contours, 1), Allocator.TempJob);
                var bounds = new NativeArray<float4>(System.Math.Max(contours, 1), Allocator.TempJob);
                var groups = new NativeArray<int2>(count, Allocator.TempJob);
                var overlapping = new NativeArray<byte>(count, Allocator.TempJob);
                try
                {
                    int pi = 0, ci = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int start = ci;
                        foreach (var contour in outlines[i].Contours)
                        {
                            if (contour.Count < 2) continue;
                            ranges[ci] = new int2(pi, contour.Count);
                            var lo = new float2(float.MaxValue);
                            var hi = new float2(float.MinValue);
                            foreach (var p in contour)
                            {
                                float2 q = p;
                                lo = math.min(lo, q);
                                hi = math.max(hi, q);
                                pointArray[pi++] = q;
                            }
                            bounds[ci++] = new float4(lo, hi);
                        }
                        groups[i] = new int2(start, ci - start);
                    }

                    new OverlapScanJob
                    {
                        Points = pointArray,
                        ContourRanges = ranges,
                        ContourBounds = bounds,
                        Groups = groups,
                        Overlapping = overlapping,
                    }.Schedule(count, 8).Complete();

                    for (int i = 0; i < count; i++)
                        if (overlapping[i] != 0) found.Add(first + i);
                }
                finally
                {
                    pointArray.Dispose();
                    ranges.Dispose();
                    bounds.Dispose();
                    groups.Dispose();
                    overlapping.Dispose();
                }
                progress?.Invoke((first + count) / (float)glyphCount);
            }
            return found.ToArray();
        }

        /// <summary>
        /// The number of glyphs in face <paramref name="faceIndex"/> of a font
        /// file, from its <c>maxp</c> table; 0 when the file cannot say.
        ///
        /// Read from the bytes rather than asked of HarfBuzz because not every
        /// bundled binary exports <c>hb_face_get_glyph_count</c> (the Linux
        /// one's export list does not), and the bytes are in hand whenever an
        /// index is made.
        /// </summary>
        public static int GlyphCountOf(byte[] font, uint faceIndex = 0)
        {
            if (font == null || font.Length < 12) return 0;
            long directory = 0;
            if (ReadUInt32(font, 0) == 0x74746366) // 'ttcf'
            {
                if (faceIndex >= ReadUInt32(font, 8)) return 0;
                directory = ReadUInt32(font, 12 + 4 * (int)faceIndex);
            }
            if (directory + 12 > font.Length) return 0;
            int tables = ReadUInt16(font, directory + 4);
            for (int t = 0; t < tables; t++)
            {
                long record = directory + 12 + 16L * t;
                if (record + 16 > font.Length) return 0;
                if (ReadUInt32(font, record) != 0x6D617870) continue; // 'maxp'
                long offset = ReadUInt32(font, record + 8);
                return offset + 6 <= font.Length ? ReadUInt16(font, offset + 4) : 0;
            }
            return 0;
        }

        private static uint ReadUInt32(byte[] b, long at) =>
            (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static int ReadUInt16(byte[] b, long at) => b[at] << 8 | b[at + 1];

        /// <summary>
        /// Fills <paramref name="output"/> with the contours
        /// <paramref name="request"/> is rasterized from: the groups in
        /// <paramref name="overlapping"/> resolved, every other one as it was.
        /// </summary>
        public static void Resolve(in RasterizeRequest request, List<int> overlapping, bool precise,
            ResolvedContours output)
        {
            output.Clear();
            var contours = request.Contours;
            int k = 0;
            while (k < contours.Count)
            {
                int group = request.Groups?[k] ?? 0;
                s_group.Clear();
                for (; k < contours.Count && (request.Groups?[k] ?? 0) == group; k++)
                    if (contours[k].Count >= 2) s_group.Add(contours[k]);
                if (s_group.Count == 0) continue;

                if (overlapping.Contains(group))
                {
                    ResolveGroup(group, precise, output);
                    continue;
                }
                foreach (var contour in s_group) Emit(output, contour, group, ContourKind.Both, precise);
            }
        }

        private static void Emit(ResolvedContours output, List<Vector2> contour, int group, byte kind,
            bool precise)
        {
            output.Add(contour, group, kind);
            if (!precise) return;
            int at = output.Flags.Count;
            for (int i = 0; i < contour.Count; i++) output.Flags.Add(0);
            if ((kind & ContourKind.Distance) == 0) return;
            Grow(contour.Count);
            MsdfEdgeColoring.Assign(contour, s_flags, 0);
            for (int i = 0; i < contour.Count - 1; i++) output.Flags[at + i] = s_flags[i];
        }

        private static void Grow(int count)
        {
            if (s_flags.Length < count) s_flags = new byte[Mathf.NextPowerOfTwo(count)];
        }

        private static void ResolveGroup(int group, bool precise, ResolvedContours output)
        {
            CollectSegments();
            FindSplits();

            s_splits.Sort(s_byPlace);
            int cursor = 0;
            for (int c = 0; c < s_group.Count; c++)
            {
                var contour = s_group[c];
                int splitsEnd = cursor;
                while (splitsEnd < s_splits.Count && s_splits[splitsEnd].Contour == c) splitsEnd++;
                if (splitsEnd == cursor)
                {
                    // Nothing meets it: one piece, buried whole or not at all.
                    bool buried = IsBuried(contour, 0, contour.Count - 1);
                    Emit(output, contour, group, buried ? ContourKind.Winding : ContourKind.Both, precise);
                    continue;
                }

                BuildSplitContour(contour, cursor, splitsEnd);
                cursor = splitsEnd;
                EmitPieces(contour, group, precise, output);
            }
        }

        private static void CollectSegments()
        {
            const float tolSq = OverlapGeometry.Tolerance * OverlapGeometry.Tolerance;
            s_segments.Clear();
            for (int c = 0; c < s_group.Count; c++)
            {
                var contour = s_group[c];
                int from = s_segments.Count, ordinal = 0;
                for (int i = 0; i < contour.Count - 1; i++)
                {
                    float2 a = contour[i], b = contour[i + 1];
                    if (math.lengthsq(b - a) <= tolSq) continue;
                    s_segments.Add(new Segment
                    {
                        A = a, B = b, Low = math.min(a.y, b.y), High = math.max(a.y, b.y),
                        Contour = c, Index = i, Ordinal = ordinal++,
                    });
                }
                bool closed = contour.Count > 2 &&
                              (contour[0] - contour[contour.Count - 1]).sqrMagnitude <= tolSq;
                int last = closed && ordinal > 2 ? ordinal - 1 : -1;
                for (int s = from; s < s_segments.Count; s++)
                {
                    var segment = s_segments[s];
                    segment.Last = last;
                    s_segments[s] = segment;
                }
            }
            s_segments.Sort(s_byLow);
        }

        private static void FindSplits()
        {
            s_splits.Clear();
            for (int i = 0; i < s_segments.Count; i++)
            {
                var s = s_segments[i];
                float left = math.min(s.A.x, s.B.x) - OverlapGeometry.Tolerance;
                float right = math.max(s.A.x, s.B.x) + OverlapGeometry.Tolerance;
                float top = s.High + OverlapGeometry.Tolerance;
                for (int j = i + 1; j < s_segments.Count; j++)
                {
                    var t = s_segments[j];
                    if (t.Low > top) break;
                    if (math.max(t.A.x, t.B.x) < left || math.min(t.A.x, t.B.x) > right) continue;
                    if (s.Contour == t.Contour)
                    {
                        int gap = math.abs(s.Ordinal - t.Ordinal);
                        if (gap == 1 || (s.Last > 0 && gap == s.Last)) continue;
                    }
                    AddSplits(s, t);
                }
            }
        }

        /// <summary>Records where two segments meet, as a split on each.</summary>
        private static void AddSplits(in Segment s, in Segment t)
        {
            const float tol = OverlapGeometry.Tolerance;
            float d1 = OverlapGeometry.Side(t.A, t.B, s.A), d2 = OverlapGeometry.Side(t.A, t.B, s.B);
            float d3 = OverlapGeometry.Side(s.A, s.B, t.A), d4 = OverlapGeometry.Side(s.A, s.B, t.B);
            if (OverlapGeometry.Straddles(d1, d2) && OverlapGeometry.Straddles(d3, d4))
            {
                AddSplit(s, d1 / (d1 - d2));
                AddSplit(t, d3 / (d3 - d4));
                return;
            }
            float slackS = tol / math.max(math.length(s.B - s.A), 1e-6f);
            float slackT = tol / math.max(math.length(t.B - t.A), 1e-6f);
            // An endpoint lying on the other segment: the endpoint is a split
            // of its own segment, and its foot is a split of the other. Two
            // edges along each other come out as up to four of these.
            TouchAt(s, 0f, s.A, d1, t, slackT);
            TouchAt(s, 1f, s.B, d2, t, slackT);
            TouchAt(t, 0f, t.A, d3, s, slackS);
            TouchAt(t, 1f, t.B, d4, s, slackS);
        }

        private static void TouchAt(in Segment owner, float end, float2 point, float side, in Segment other,
            float slack)
        {
            if (math.abs(side) > OverlapGeometry.Tolerance) return;
            float along = OverlapGeometry.Along(other.A, other.B, point);
            if (!OverlapGeometry.Within(along, slack)) return;
            AddSplit(owner, end);
            AddSplit(other, math.saturate(along));
        }

        private static void AddSplit(in Segment segment, float t) =>
            s_splits.Add(new Split { Contour = segment.Contour, Index = segment.Index, T = math.saturate(t) });

        /// <summary>
        /// The contour with every split inserted as a point of its own, into
        /// <see cref="s_split"/>, and which of its points are splits, into
        /// <see cref="s_marks"/>. A split within tolerance of a vertex is that
        /// vertex.
        /// </summary>
        private static void BuildSplitContour(List<Vector2> contour, int from, int to)
        {
            s_vertexMarks.Clear();
            for (int i = 0; i < contour.Count; i++) s_vertexMarks.Add(false);
            for (int k = from; k < to; k++)
            {
                var split = s_splits[k];
                float slack = OverlapGeometry.Tolerance /
                              Mathf.Max((contour[split.Index + 1] - contour[split.Index]).magnitude, 1e-6f);
                if (split.T <= slack) s_vertexMarks[split.Index] = true;
                else if (split.T >= 1f - slack) s_vertexMarks[split.Index + 1] = true;
            }

            s_split.Clear();
            s_marks.Clear();
            int cursor = from;
            for (int i = 0; i < contour.Count - 1; i++)
            {
                var a = contour[i];
                var b = contour[i + 1];
                s_split.Add(a);
                s_marks.Add(s_vertexMarks[i]);
                float slack = OverlapGeometry.Tolerance / Mathf.Max((b - a).magnitude, 1e-6f);
                float previous = 0f;
                for (; cursor < to && s_splits[cursor].Index == i; cursor++)
                {
                    float t = s_splits[cursor].T;
                    if (t <= slack || t >= 1f - slack || t - previous <= slack) continue;
                    s_split.Add(Vector2.LerpUnclamped(a, b, t));
                    s_marks.Add(true);
                    previous = t;
                }
            }
            s_split.Add(contour[contour.Count - 1]);
            s_marks.Add(s_vertexMarks[contour.Count - 1]);
        }

        /// <summary>
        /// Classifies the pieces of <see cref="s_split"/> and emits the
        /// contour: as it was when nothing on it is buried, otherwise as a
        /// wound-only original plus a measured-only polyline per visible piece.
        /// </summary>
        private static void EmitPieces(List<Vector2> contour, int group, bool precise, ResolvedContours output)
        {
            const float tolSq = OverlapGeometry.Tolerance * OverlapGeometry.Tolerance;
            int segments = s_split.Count - 1;
            bool closed = segments >= 2 && (s_split[0] - s_split[segments]).sqrMagnitude <= tolSq;
            if (closed) s_marks[0] |= s_marks[segments];

            s_boundaries.Clear();
            for (int i = 0; i < segments; i++)
                if (s_marks[i] || (!closed && i == 0)) s_boundaries.Add(i);
            // A closed loop runs round to its first boundary again; an open one
            // stops at its last point.
            int pieces = s_boundaries.Count;
            if (pieces == 0)
            {
                bool whole = IsBuried(contour, 0, contour.Count - 1);
                Emit(output, contour, group, whole ? ContourKind.Winding : ContourKind.Both, precise);
                return;
            }

            s_pieces.Clear();
            bool anyBuried = false;
            for (int p = 0; p < pieces; p++)
            {
                int start = s_boundaries[p];
                int end = p + 1 < pieces ? s_boundaries[p + 1] : (closed ? s_boundaries[0] + segments : segments);
                var piece = Classify(start, end, segments);
                s_pieces.Add(piece);
                anyBuried |= piece == Piece.Buried;
            }

            if (!anyBuried)
            {
                Emit(output, contour, group, ContourKind.Both, precise);
                return;
            }

            Emit(output, contour, group, ContourKind.Winding, precise);
            if (precise)
            {
                // Coloured as the closed loop it is, so a piece keeps the
                // channels and run ends of the edge it was cut from: the cuts
                // are collinear points and turn no corner.
                Grow(s_split.Count);
                MsdfEdgeColoring.Assign(s_split, s_flags, 0);
            }
            for (int p = 0; p < pieces; p++)
            {
                // A point is not an edge. The close-path's repeated start comes
                // out as one whenever a split lands on it, and it can sit
                // anywhere, buried included, where measuring it would put back
                // a dent of exactly the kind this removes.
                if (s_pieces[p] != Piece.Visible) continue;
                int start = s_boundaries[p];
                int end = p + 1 < pieces ? s_boundaries[p + 1] : (closed ? s_boundaries[0] + segments : segments);
                var piece = output.Rent();
                for (int i = start; i <= end; i++) piece.Add(s_split[Wrap(i, segments, closed)]);
                output.Add(piece, group, ContourKind.Distance);
                if (!precise) continue;
                for (int i = start; i < end; i++) output.Flags.Add(s_flags[Wrap(i, segments, closed)]);
                output.Flags.Add(0);
            }
        }

        private static int Wrap(int i, int segments, bool closed) => closed && i >= segments ? i - segments : i;

        /// <summary>What the piece of <see cref="s_split"/> from point start to point end is.</summary>
        private static Piece Classify(int start, int end, int segments)
        {
            // Probed on its longest segment, the one furthest from the splits
            // at either end of it.
            int best = start;
            float longest = -1f;
            for (int i = start; i < end; i++)
            {
                int a = i >= segments ? i - segments : i;
                float length = (s_split[a + 1] - s_split[a]).sqrMagnitude;
                if (length <= longest) continue;
                longest = length;
                best = a;
            }
            if (longest <= OverlapGeometry.Tolerance * OverlapGeometry.Tolerance) return Piece.Point;
            return InkBothSides(s_split[best], s_split[best + 1]) ? Piece.Buried : Piece.Visible;
        }

        /// <summary>Whether a contour that nothing meets is buried, probed on its longest segment.</summary>
        private static bool IsBuried(List<Vector2> contour, int start, int end)
        {
            int best = start;
            float longest = -1f;
            for (int i = start; i < end; i++)
            {
                float length = (contour[i + 1] - contour[i]).sqrMagnitude;
                if (length <= longest) continue;
                longest = length;
                best = i;
            }
            return InkBothSides(contour[best], contour[best + 1]);
        }

        /// <summary>Ink on both sides of the segment a..b: the winding is nonzero either side of it.</summary>
        private static bool InkBothSides(float2 a, float2 b)
        {
            float2 along = b - a;
            float length = math.length(along);
            if (length <= 1e-6f) return false;
            float2 normal = new float2(-along.y, along.x) / length;
            float2 mid = (a + b) * 0.5f;
            float reach = math.min(ProbeReach, length * 0.25f);
            return WindingAt(mid + normal * reach) != 0 && WindingAt(mid - normal * reach) != 0;
        }

        /// <summary>The group's winding at a point, over every edge the font drew.</summary>
        private static int WindingAt(float2 p)
        {
            int winding = 0;
            foreach (var contour in s_group)
                for (int i = 0; i < contour.Count - 1; i++)
                    winding += OverlapGeometry.Crossing(contour[i], contour[i + 1], p);
            return winding;
        }
    }
}
