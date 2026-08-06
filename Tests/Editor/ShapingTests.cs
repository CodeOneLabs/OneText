using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace OneText.Tests
{
    public class ShapingTests
    {
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static FontData LoadFont(string packagePath)
        {
            var bytes = File.ReadAllBytes(Path.GetFullPath(packagePath));
            return FontData.Load(bytes);
        }

        private static List<ShapedGlyph> Shape(FontData font, string text)
        {
            using var shaper = new Shaper();
            var result = new List<ShapedGlyph>();
            shaper.Shape(font, text, result);
            return result;
        }

        [Test]
        public void NativeLibrary_Loads_AndReportsVersion()
        {
            var version = Shaper.HarfBuzzVersion;
            Assert.IsNotEmpty(version);
            var major = int.Parse(version.Split('.')[0]);
            Assert.GreaterOrEqual(major, 8, $"Unexpectedly old HarfBuzz: {version}");
        }

        [Test]
        public void Latin_Shapes_OneGlyphPerLetter_WithPositiveAdvances()
        {
            using var font = LoadFont(LatinFontPath);
            var glyphs = Shape(font, "AV");
            Assert.AreEqual(2, glyphs.Count);
            Assert.That(glyphs.All(g => g.XAdvance > 0));
            Assert.That(glyphs.All(g => g.GlyphId != 0), "'.notdef' produced: cmap lookup failed");
        }

        [Test]
        public void Arabic_AppliesContextualForms()
        {
            using var font = LoadFont(ArabicFontPath);
            // The same letter (beh) must resolve to different glyphs in
            // isolated vs initial/medial/final position.
            var isolated = Shape(font, "ب");
            var joined = Shape(font, "ببب");
            var uniqueIds = isolated.Concat(joined).Select(g => g.GlyphId).Distinct().Count();
            Assert.GreaterOrEqual(uniqueIds, 4,
                "Expected distinct isolated/initial/medial/final forms: GSUB not applied?");
        }

        [Test]
        public void Arabic_OutputsVisualOrder_RightToLeft()
        {
            using var font = LoadFont(ArabicFontPath);
            var glyphs = Shape(font, "السلام"); // السلام
            // For an RTL run HarfBuzz emits glyphs in visual (left-to-right)
            // order, so source clusters must be non-increasing.
            for (int i = 1; i < glyphs.Count; i++)
                Assert.LessOrEqual(glyphs[i].Cluster, glyphs[i - 1].Cluster);
        }

        [Test]
        public void Arabic_PositionsMarks_WithZeroAdvance()
        {
            using var font = LoadFont(ArabicFontPath);
            var glyphs = Shape(font, "السلام عليكم");
            Assert.That(glyphs.Any(g => g.XAdvance == 0 && (g.XOffset != 0 || g.YOffset != 0)),
                "Expected at least one zero-advance mark positioned via GPOS");
        }

        [Test]
        public void Outline_Extracts_NonEmptyContours()
        {
            using var font = LoadFont(ArabicFontPath);
            var glyphs = Shape(font, "ب");
            var outline = new GlyphOutline();
            OutlineExtractor.Extract(font, glyphs[0].GlyphId, outline);
            Assert.IsNotEmpty(outline.Contours);
            Assert.That(outline.Contours.All(c => c.Count >= 3));
        }
    }
}
