using System.Collections.Generic;

namespace OneText
{
    /// <summary>A glyph positioned within a cluster, font units relative to the cluster pen.</summary>
    public readonly struct PositionedGlyph
    {
        public readonly uint GlyphId;
        public readonly int X, Y;

        public PositionedGlyph(uint glyphId, int x, int y)
        {
            GlyphId = glyphId;
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Splits a shaped run into clusters of ink-overlapping glyphs. Each
    /// cluster is rasterized as ONE merged SDF, so joints between connected
    /// glyphs (Arabic joins, positioned marks) live in the field's interior
    /// — there is no per-glyph boundary left to seam.
    ///
    /// Membership is decided by interval union over the glyphs' ink extents,
    /// NOT by list adjacency: zero-advance marks positioned over a later
    /// base make ink ranges non-monotonic in list order, and adjacency-based
    /// grouping would cut a connected sequence exactly at those marks.
    /// </summary>
    public static class GlyphClusters
    {
        public struct Cluster
        {
            /// <summary>Range into the positioned-glyph list.</summary>
            public int Start, Count;

            /// <summary>Pen position the cluster's offsets are relative to, font units from run start.</summary>
            public int PenX;

            /// <summary>Content hash for atlas caching (glyph ids + offsets).</summary>
            public long Hash;

            /// <summary>
            /// Source text this tile came from, as UTF-16 indices [Start, End).
            /// A merged tile can cover several characters — that is the point of
            /// merging — so anything counting units of text (reveal, effects,
            /// carets) needs the range rather than a single index.
            /// </summary>
            public int TextStart, TextEnd;
        }

        private struct InkSpan
        {
            public int GlyphIndex;
            public int Pen;
            public float Min, Max;
        }

        [System.ThreadStatic] private static List<InkSpan> t_spans;

        private static readonly System.Comparison<InkSpan> s_byInkStart =
            (a, b) => a.Min.CompareTo(b.Min);

        /// <summary>
        /// Interaction distance for clustering, in font units: how close two
        /// glyphs' ink has to be before they must be baked as one tile.
        ///
        /// This is a cache-vs-seam trade. Connected scripts join with overlaps
        /// or gaps of a few font units, and those must merge or the joint
        /// shows a seam. Merging anything further apart than that is pure cost:
        /// the tile then covers several glyphs, so its cache key changes
        /// whenever any of them changes, and a word-sized tile has to be
        /// re-rasterized when one letter is edited. Keep it tight — 1% of an em
        /// is well above the real join gaps and far below the spacing between
        /// separate letters at any size.
        /// </summary>
        public static float DefaultMergeGapUnits(FontData font) => font.UnitsPerEm * 0.01f;

        /// <summary>
        /// Groups a run's glyphs into ink-overlap clusters. Fills
        /// <paramref name="positioned"/> with cluster-relative glyph
        /// placements and <paramref name="clusters"/> with ranges into it.
        /// <paramref name="maxWidthUnits"/> bounds a cluster's ink width so
        /// its tile fits the atlas. <paramref name="mergeGapUnits"/> is the
        /// interaction distance: some fonts leave a tiny real gap (a few
        /// units) at visually-continuous joins, and any two tiles closer
        /// than their padding reach can also blend visibly — both must
        /// cluster together, so pass roughly twice the tile padding
        /// converted to font units.
        /// </summary>
        public static void Split(FontData font, List<ShapedGlyph> glyphs,
            List<Cluster> clusters, List<PositionedGlyph> positioned,
            float maxWidthUnits, float mergeGapUnits) =>
            Split(font, glyphs, 0, glyphs.Count, clusters, positioned, maxWidthUnits, mergeGapUnits);

        /// <summary>
        /// Same, for one slice of a shared glyph buffer — the layout engine
        /// packs every run of a text block into a single list.
        /// </summary>
        public static void Split(FontData font, List<ShapedGlyph> glyphs, int start, int count,
            List<Cluster> clusters, List<PositionedGlyph> positioned,
            float maxWidthUnits, float mergeGapUnits)
        {
            clusters.Clear();
            positioned.Clear();

            var spans = t_spans ??= new List<InkSpan>();
            spans.Clear();
            int pen = 0;
            for (int i = start; i < start + count; i++)
            {
                var g = glyphs[i];
                if (font.TryGetInkBounds(g.GlyphId, out var lo, out var hi))
                {
                    spans.Add(new InkSpan
                    {
                        GlyphIndex = i,
                        Pen = pen,
                        Min = pen + g.XOffset + lo.x,
                        Max = pen + g.XOffset + hi.x,
                    });
                }
                pen += g.XAdvance;
            }
            if (spans.Count == 0) return;

            // Interval union: sort by ink start, then merge while overlapping
            // (or nearly touching) and under the width cap.
            spans.Sort(s_byInkStart);

            int groupStart = 0;
            float groupMin = spans[0].Min, groupMax = spans[0].Max;
            for (int s = 1; s <= spans.Count; s++)
            {
                bool close = s == spans.Count
                    || spans[s].Min > groupMax + mergeGapUnits
                    || spans[s].Max - groupMin > maxWidthUnits;
                if (close)
                {
                    clusters.Add(Emit(glyphs, spans, groupStart, s, positioned));
                    if (s == spans.Count) break;
                    groupStart = s;
                    groupMin = spans[s].Min;
                    groupMax = spans[s].Max;
                }
                else if (spans[s].Max > groupMax)
                {
                    groupMax = spans[s].Max;
                }
            }
        }

        private static Cluster Emit(List<ShapedGlyph> glyphs, List<InkSpan> spans,
            int start, int end, List<PositionedGlyph> positioned)
        {
            // Anchor offsets to the group's smallest glyph index so identical
            // content re-hashes identically wherever it appears in the run.
            int anchor = start;
            for (int s = start + 1; s < end; s++)
                if (spans[s].GlyphIndex < spans[anchor].GlyphIndex)
                    anchor = s;
            int penBase = spans[anchor].Pen;

            int outStart = positioned.Count;
            int textStart = int.MaxValue, textEnd = int.MinValue;
            for (int s = start; s < end; s++)
            {
                var g = glyphs[spans[s].GlyphIndex];
                int x = spans[s].Pen - penBase + g.XOffset;
                positioned.Add(new PositionedGlyph(g.GlyphId, x, g.YOffset));
                if (g.Cluster < textStart) textStart = g.Cluster;
                if (g.Cluster > textEnd) textEnd = g.Cluster;
            }

            return new Cluster
            {
                Start = outStart,
                Count = end - start,
                PenX = penBase,
                Hash = HashOf(positioned, outStart, end - start),
                TextStart = textStart,
                // Exclusive, and the shaper reports a cluster per character
                // start, so the last one still spans at least itself.
                TextEnd = textEnd + 1,
            };
        }

        /// <summary>
        /// Atlas cache key for a cluster: its glyph ids and their offsets.
        /// Prewarming has to produce the same key a label will look up, so the
        /// hash lives here rather than inside the splitter.
        /// </summary>
        public static long HashOf(List<PositionedGlyph> positioned, int start, int count)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = start; i < start + count; i++)
            {
                var g = positioned[i];
                hash = (hash ^ g.GlyphId) * 1099511628211UL;
                hash = (hash ^ (uint)g.X) * 1099511628211UL;
                hash = (hash ^ (uint)g.Y) * 1099511628211UL;
            }
            // Top bit marks a cluster key, so it can never collide with a plain glyph id.
            return (long)(hash | 0x8000000000000000UL);
        }
    }
}
