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

        /// <summary>
        /// How far from the outline, in texels, a texel has to be before the
        /// median is allowed to be overruled by the true distance. Zero or less
        /// turns the correction off; see <see cref="Correct"/> for what it is
        /// and why the guard band exists.
        /// </summary>
        public float ErrorCorrectionTexels;

        [WriteOnly] [NativeDisableParallelForRestriction] public NativeArray<byte> Output;

        /// <summary>
        /// Which source glyph won this texel, one byte each. Two glyphs of a
        /// cluster are coloured independently, so R means one edge inside one
        /// of them and an unrelated edge inside the other: across the seam
        /// where the winner changes, the three channels are not comparable and
        /// interpolating between them produces a value belonging to neither.
        /// The interpolation pass reads this to find those seams.
        /// </summary>
        [WriteOnly] [NativeDisableParallelForRestriction] public NativeArray<byte> WinningGroup;

        public void Execute(int index)
        {
            int tileIndex = MsdfTiles.Find(TileEnds, TileCount, index);
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
            int winner = currentGroup;

            for (int c = first; c < last; c++)
            {
                if (ContourGroups[c] != currentGroup)
                {
                    Fold(ref union, ref unionTrue, ref winner, currentGroup,
                        value, beyond, minSq, winding);
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
            Fold(ref union, ref unionTrue, ref winner, currentGroup,
                value, beyond, minSq, winding);

            float scale = 1f / tile.UnitsPerPixel;
            float3 field = union * scale;
            float trueField = unionTrue * scale;
            field = Correct(field, trueField);

            int at = (tile.OutputStart + local) * 4;
            Output[at + 0] = Encode(field.x);
            Output[at + 1] = Encode(field.y);
            Output[at + 2] = Encode(field.z);
            Output[at + 3] = Encode(trueField);
            WinningGroup[tile.OutputStart + local] = (byte)(winner & 0xFF);
        }

        /// <summary>
        /// Error correction: the median and the true distance have to agree on
        /// which side of the outline this texel is, and where they do not, the
        /// true distance wins.
        ///
        /// The classic multi-channel artifact is two unrelated parts of a glyph
        /// — the bowl of an 'a' passing near its stem, a crossbar meeting a
        /// diagonal — whose channels happen to cross here, so the median is
        /// decided by an edge that has nothing to do with this texel. It comes
        /// out as a dark dart bitten out of the ink or a spur hanging off it,
        /// and magnification makes one bad texel into a visible one.
        ///
        /// The test is a sign check because it can afford to be. Near a corner
        /// the median is the intersection of the two half-planes and the true
        /// distance is the distance to the corner point: different numbers, but
        /// the same zero set, because the intersection's boundary <em>is</em>
        /// the outline there. Corner sharpening happens between texel centres,
        /// in what bilinear reconstruction does to a linear field against a
        /// cone, not at the centres themselves. So at a texel centre the two
        /// agree in sign for every well-formed corner, and a disagreement is
        /// the clash and nothing else.
        ///
        /// Which is why this is a per-texel test and not the neighbourhood
        /// search msdfgen has to run: msdfgen approximates the true distance
        /// from the same three channels it is trying to check, and here alpha
        /// already holds it exactly, for free.
        ///
        /// The guard band is the one concession. A run boundary the colouring
        /// invented rather than found — the three-way split of a single-corner
        /// contour, or a flattening bend that cleared the corner threshold —
        /// extends a line past a join that was nearly smooth, and the extension
        /// diverges from the true distance by about the bend angle times the
        /// distance travelled. That is a magnitude error, so it can only flip a
        /// sign within a fraction of a texel of the outline, and half a texel
        /// of slack is well clear of it. The artifacts worth catching are texels
        /// deep inside the ink.
        ///
        /// A corrected texel gets the true distance in all three channels, so it
        /// reads exactly as the single-channel field would. The corner it sits
        /// on loses its sharpening. That is the trade and it is the right way
        /// round: the alternative at that texel was not a sharp corner, it was
        /// a hole.
        /// </summary>
        private float3 Correct(float3 field, float trueField)
        {
            if (ErrorCorrectionTexels <= 0f) return field;
            float median = math.max(math.min(field.x, field.y),
                math.min(math.max(field.x, field.y), field.z));

            // Sign disagreement, anywhere past the guard band: the median has
            // been handed an edge belonging to something else entirely. This is
            // the detached block of ink the CFF S-curve grows.
            bool wrongSide = median * trueField < 0f &&
                             math.abs(trueField) > ErrorCorrectionTexels;

            // The sag: solidly inside the ink, the median has drifted back to
            // the outline, because every channel is measuring against a line
            // extended past a run end that stopped bounding the shape. A
            // crossbar meeting a diagonal is the standard case. The median never
            // crosses over, so nothing about the sign catches it; what it does
            // instead is land inside the antialiasing band, and a pixel whose
            // coverage is decided from there comes out grey in the middle of a
            // stroke.
            //
            // The test is where the median ENDS UP, not how far it moved. How
            // far it moved is the wrong question, and being precise about why
            // matters, because this rule fires only inside the ink and the
            // divergence that lives inside the ink is not the convex corner
            // everyone pictures. It is the REFLEX one: in the interior wedge of
            // a crossbar meeting a stem, the true distance is the cone to the
            // corner point while the median is min(dA, dB), the union of the two
            // half-planes — a perfectly good unit-gradient field with the right
            // zero set, sitting 0.414 * depth below the cone at a right angle.
            // So this rule does overrule a legitimate reconstruction, routinely,
            // at every such junction deeper than the guard.
            //
            // It is harmless because of an invariant the shader holds and this
            // job depends on: NOTHING READS THE MEDIAN DEEPER THAN THE 0.5
            // ISOLINE. The face thresholds at 0.5, the outline threshold is
            // 0.5 - REACH_FIELD * width and so only ever moves outward, the glow
            // is a falloff on (0.5 - d), and the shadow reads alpha, which is
            // never rewritten here. A texel past the guard therefore feeds, by
            // bilinear reconstruction, only samples that are themselves well
            // inside, and one unit-gradient inside field cannot be told from
            // another there. Add an inward threshold — a faux-bold dilate, an
            // inner outline — and that stops being true: reflex corners would
            // round off at the new isoline, and this rule would have to learn
            // the difference it is currently allowed to ignore.
            //
            // Which is also why this holds up where a deviation test could not.
            // A stem four texels thick at 64 ppem has its centre two texels in,
            // so a guard band wide enough to protect corner reconstruction from
            // a deviation test would swallow the whole stem — and 64 ppem
            // magnified is exactly where the artifact is worst.
            bool sagging = trueField < -(ErrorCorrectionTexels + GuardTexels) &&
                           median > -ErrorCorrectionTexels;

            return wrongSide | sagging ? new float3(trueField) : field;
        }

        /// <summary>
        /// How much deeper than the floor a texel has to be before the sag test
        /// looks at it at all, so that a texel merely sitting at the floor is
        /// never rewritten on rounding alone.
        /// </summary>
        private const float GuardTexels = 0.5f;

        private byte Encode(float signedPixels) =>
            (byte)math.round(math.saturate(0.5f - signedPixels / (2f * SpreadPixels)) * 255f);

        /// <summary>
        /// Signs one source glyph's channels and folds them into the union.
        /// Pseudo-distances taken past a run end already carry their sign;
        /// everything else is a magnitude the winding turns negative inside.
        /// </summary>
        private static void Fold(ref float3 union, ref float unionTrue,
            ref int winner, int group,
            float3 value, int3 beyond, float minSq, int winding)
        {
            float trueDistance = math.sqrt(minSq);
            if (winding != 0) trueDistance = -trueDistance;

            float3 wound = winding != 0 ? -value : value;
            float3 signed = math.select(wound, value, beyond != 0);

            // The nearest group wins, and it wins with all three of its channels
            // at once. Taking a per-channel minimum instead was wrong twice
            // over: the median of three minima is not the minimum of three
            // medians, and a pseudo-distance means something only near the
            // corner it was extended from — carried into another glyph's
            // territory it is a half-plane that stopped bounding anything, and
            // a minimum lets it win there. Measured on four 'A's baked as one
            // cluster at 48 ppem, that put ink up to 2.5 texels clear of the
            // outline where one glyph alone put 1.5; on screen, the specks and
            // bitten edges a magnified world mesh shows.
            //
            // Nearest by the true distance, which is the same quantity the union
            // of the shapes is defined by, so this cannot pick a group whose
            // edges are buried inside another's ink: buried means the other
            // group is further inside, and further inside is smaller here.
            if (trueDistance < unionTrue)
            {
                unionTrue = trueDistance;
                union = signed;
                winner = group;
            }
        }
    }
}
