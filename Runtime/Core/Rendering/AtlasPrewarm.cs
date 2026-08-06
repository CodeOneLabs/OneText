using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>What a prewarm pass managed to do.</summary>
    public struct PrewarmReport
    {
        /// <summary>(character, size) pairs asked for.</summary>
        public int Requested;

        /// <summary>Tiles rasterized by this pass.</summary>
        public int Baked;

        /// <summary>Pairs already in the atlas: nothing to do.</summary>
        public int AlreadyResident;

        /// <summary>Characters no font in the stack covers.</summary>
        public int Uncovered;

        /// <summary>Pairs not warmed because the atlas budget ran out.</summary>
        public int Skipped;

        /// <summary>Fraction of the atlas occupied when the pass ended.</summary>
        public float Fill;

        /// <summary>True when the pass stopped early because the atlas filled up.</summary>
        public bool StoppedAtBudget;

        public override string ToString() =>
            $"prewarm: {Baked} baked, {AlreadyResident} cached, {Skipped} skipped, " +
            $"{Uncovered} uncovered, atlas {Fill * 100f:0.#}% full" +
            (StoppedAtBudget ? " (STOPPED AT BUDGET)" : "");
    }

    /// <summary>
    /// Rasterizes a charset ahead of time so the first frame that shows a
    /// character does not pay for it.
    ///
    /// Each character is shaped on its own and clustered exactly the way a
    /// label clusters it, so the tiles land under the keys labels will look up.
    /// Characters whose tile depends on their neighbours, the joined forms of
    /// Arabic and other cursive scripts, cannot be prewarmed this way and are
    /// left to the live path; isolated glyphs (Latin, CJK, Hangul, Kana), which
    /// is where the atlas pressure actually is, all warm correctly.
    ///
    /// A prewarm pass stops when the atlas reaches <c>fillLimit</c> and reports
    /// what did not fit. Filling the atlas past that point would evict the
    /// glyphs prewarmed a moment earlier, the very thrash prewarming exists to
    /// prevent.
    /// </summary>
    public static class AtlasPrewarm
    {
        /// <summary>Default occupancy ceiling: leave room for glyphs nobody predicted.</summary>
        public const float DefaultFillLimit = 0.85f;

        public static PrewarmReport Warm(GlyphAtlas atlas, FontStack fonts,
            IEnumerable<int> codepoints, IReadOnlyList<float> sizes,
            float fillLimit = DefaultFillLimit)
        {
            var report = new PrewarmReport();
            if (atlas == null || fonts == null || fonts.Primary == null ||
                codepoints == null || sizes == null || sizes.Count == 0)
                return report;

            // Evicting during a prewarm means the pass has started overwriting
            // its own work, the exact thrash prewarming exists to prevent, and
            // a truer "full" signal than occupancy, which a small atlas never
            // reaches because it recycles instead.
            int evictionsAtStart = atlas.GetStats().Evictions;

            // Everything baked from here is a tile the project predicted, which
            // is what the Hub's occupancy split is measuring. Restored rather
            // than cleared: a prewarm invoked from inside another one (a charset
            // that warms a second charset) must not un-mark the outer pass.
            bool wasMarking = atlas.MarkingPrewarmed;
            atlas.MarkingPrewarmed = true;
            try
            {
                return WarmMarked(atlas, fonts, codepoints, sizes, fillLimit,
                    evictionsAtStart, report);
            }
            finally
            {
                atlas.MarkingPrewarmed = wasMarking;
            }
        }

        private static PrewarmReport WarmMarked(GlyphAtlas atlas, FontStack fonts,
            IEnumerable<int> codepoints, IReadOnlyList<float> sizes, float fillLimit,
            int evictionsAtStart, PrewarmReport report)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();

            foreach (int codepoint in codepoints)
            {
                string text = char.ConvertFromUtf32(codepoint);
                var font = fonts.Resolve(codepoint);
                bool covered = font != null && font.HasGlyph(codepoint);
                if (!covered) report.Uncovered++;
                if (font == null) continue;

                foreach (float size in sizes)
                {
                    report.Requested++;
                    if (report.StoppedAtBudget)
                    {
                        report.Skipped++;
                        continue;
                    }

                    shaped.Clear();
                    shaper.Shape(font, text, shaped);
                    if (shaped.Count == 0) continue;

                    int ppem = GlyphAtlas.QuantizePixelsPerEm(size);
                    float unitsPerTilePixel = font.UnitsPerEm / (float)ppem;
                    GlyphClusters.Split(font, shaped, clusters, positioned,
                        1000f * unitsPerTilePixel, GlyphClusters.DefaultMergeGapUnits(font));

                    foreach (var cluster in clusters)
                    {
                        if (atlas.Contains(font, cluster.Hash, size))
                        {
                            report.AlreadyResident++;
                            continue;
                        }
                        atlas.GetOrAddCluster(font, size, positioned,
                            cluster.Start, cluster.Count, cluster.Hash);
                        report.Baked++;
                    }

                    var stats = atlas.GetStats();
                    if (stats.UsedFraction >= fillLimit || stats.Evictions > evictionsAtStart)
                        report.StoppedAtBudget = true;
                }
            }

            report.Fill = atlas.GetStats().UsedFraction;
            if (report.StoppedAtBudget)
            {
                Debug.LogWarning($"OneText {report}. Raise the atlas budget in " +
                    "Project Settings > OneText, or prewarm fewer characters/sizes.");
            }
            return report;
        }
    }
}
