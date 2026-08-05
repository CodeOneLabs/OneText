using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>
    /// The rasterizer skips segments whose contribution is provably discarded
    /// (farther than the spread, or unable to cross the texel's row). "Provably"
    /// has to be checked rather than argued: these tests rasterize the same
    /// glyphs with and without the culls and compare every byte.
    /// </summary>
    public class SdfCullingTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        [TearDown]
        public void RestoreCulling() => GlyphRasterizer.Cull = true;

        [Test]
        public void CulledRasterizer_MatchesNaive_ForSingleGlyphs()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var shaper = new Shaper();

            foreach (var font in new[] { latin, arabic })
            {
                var glyphs = new List<ShapedGlyph>();
                shaper.Shape(font, "AaBbGgQqMmWw@#%&Ωπ§¶ﬁﬂ0123456789", glyphs);
                shaper.Shape(font, "السلام عليكم ورحمة الله وبركاته", glyphs);

                foreach (int ppem in new[] { 24, 48, 96 })
                {
                    float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
                    foreach (var glyph in glyphs)
                    {
                        // Copied out on the spot: the rasterizer's buffer is
                        // reused by the very next call.
                        GlyphRasterizer.Cull = false;
                        var naive = GlyphRasterizer.Rasterize(font, glyph.GlyphId, pixelsPerUnit);
                        var naivePixels = naive.IsEmpty ? null : naive.CopyPixels();
                        GlyphRasterizer.Cull = true;
                        var culled = GlyphRasterizer.Rasterize(font, glyph.GlyphId, pixelsPerUnit);

                        Assert.AreEqual(naive.IsEmpty, culled.IsEmpty);
                        if (naive.IsEmpty) continue;
                        Assert.AreEqual(naive.Width, culled.Width);
                        Assert.AreEqual(naive.Height, culled.Height);
                        CollectionAssert.AreEqual(naivePixels, culled.CopyPixels(),
                            $"glyph {glyph.GlyphId} at {ppem}ppem differs with culling on");
                    }
                }
            }
        }

        [Test]
        public void CulledRasterizer_MatchesNaive_ForMergedClusters()
        {
            // Clusters are the case the culls could plausibly break: several
            // glyphs in one field, distance resolved per group and unioned.
            using var font = LoadFont(ArabicFontPath);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, "ورحمة الله وبركاته", 0, 18, Shaper.Direction.RightToLeft, shaped);

            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();
            foreach (int ppem in new[] { 32, 64 })
            {
                float unitsPerTilePixel = font.UnitsPerEm / (float)ppem;
                GlyphClusters.Split(font, shaped, clusters, positioned,
                    1000f * unitsPerTilePixel, GlyphClusters.DefaultMergeGapUnits(font));

                foreach (var cluster in clusters)
                {
                    GlyphRasterizer.Cull = false;
                    using var naiveAtlas = new GlyphAtlas();
                    var naive = naiveAtlas.GetOrAddCluster(font, ppem, positioned,
                        cluster.Start, cluster.Count, cluster.Hash);

                    GlyphRasterizer.Cull = true;
                    using var culledAtlas = new GlyphAtlas();
                    var culled = culledAtlas.GetOrAddCluster(font, ppem, positioned,
                        cluster.Start, cluster.Count, cluster.Hash);

                    Assert.AreEqual(naive.UvRect, culled.UvRect, "tile size changed");
                    var a = naiveAtlas.Texture.GetPixelData<byte>(0, naive.Layer);
                    var b = culledAtlas.Texture.GetPixelData<byte>(0, culled.Layer);
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] == b[i]) continue;
                        Assert.Fail($"cluster tile differs at texel {i} ({a[i]} vs {b[i]}) at {ppem}ppem");
                    }
                }
            }
        }
    }
}
