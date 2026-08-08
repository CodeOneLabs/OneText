using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace OneText.Tests
{
    public class SdfTests
    {
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static uint FirstGlyph(FontData font, string text)
        {
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, text, glyphs);
            return glyphs[0].GlyphId;
        }

        [Test]
        public void Rasterizer_ProducesFullDistanceRange()
        {
            using var font = LoadFont(ArabicFontPath);
            float ppu = 64f / font.UnitsPerEm;
            var raster = GlyphRasterizer.Rasterize(font, FirstGlyph(font, "ب"), ppu);

            Assert.IsFalse(raster.IsEmpty);
            var pixels = raster.CopyPixels();
            Assert.AreEqual(raster.Width * raster.Height, pixels.Length);
            // A healthy SDF must reach well inside (bright) and well outside (dark).
            Assert.GreaterOrEqual(pixels.Max(), 200, "no deep-inside texels: sign/winding broken?");
            Assert.LessOrEqual(pixels.Min(), 55, "no outside texels: bbox mapping broken?");
        }

        [Test]
        public void Rasterizer_UniformDensity_MatchesRequestedScale()
        {
            using var font = LoadFont(LatinFontPath);
            float ppu = 96f / font.UnitsPerEm;
            var raster = GlyphRasterizer.Rasterize(font, FirstGlyph(font, "H"), ppu);
            Assert.IsFalse(raster.IsEmpty);
            Assert.AreEqual(1f / ppu, raster.UnitsPerPixel, 1e-3f,
                "density must be exactly what the caller requested, uniform across glyphs");
        }

        [Test]
        public void Rasterizer_EmptyGlyph_ReportsEmpty()
        {
            using var font = LoadFont(LatinFontPath);
            var raster = GlyphRasterizer.Rasterize(font, FirstGlyph(font, " "), 64f / font.UnitsPerEm);
            Assert.IsTrue(raster.IsEmpty);
        }

        [Test]
        public void Atlas_CachesGlyphs_AndReportsLocations()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas();
            uint gid = FirstGlyph(font, "A");

            var first = atlas.GetOrAdd(font, gid, 64f);
            var second = atlas.GetOrAdd(font, gid, 64f);

            Assert.IsTrue(first.HasPixels);
            Assert.AreEqual(first.UvRect, second.UvRect, "cache miss on identical glyph");
            Assert.AreEqual(first.Layer, second.Layer);
            Assert.Greater(first.SizeUnits.x, 0f);
        }

        [Test]
        public void Atlas_DistinctGlyphs_GetDistinctSlots()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas();

            var a = atlas.GetOrAdd(font, FirstGlyph(font, "A"), 64f);
            var b = atlas.GetOrAdd(font, FirstGlyph(font, "B"), 64f);
            Assert.AreNotEqual(a.UvRect.position, b.UvRect.position);
        }

        [Test]
        public void Atlas_SizeBuckets_AreSeparateEntries()
        {
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas();
            uint gid = FirstGlyph(font, "A");

            var small = atlas.GetOrAdd(font, gid, 32f);
            var large = atlas.GetOrAdd(font, gid, 200f);
            Assert.Greater(large.UvRect.width, small.UvRect.width,
                "higher ppem bucket must produce a larger tile");
        }
    }
}
