using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Clustering decides which glyphs share a tile by their ink boxes, and it
    /// asks for one per glyph per line. Those boxes now come from the font's
    /// own tables instead of a flattened outline — cheap, but only correct if
    /// the box still contains the ink it is standing in for.
    /// </summary>
    public class InkBoundsTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        [Test]
        public void InkBounds_ContainTheOutline()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();

            foreach (var font in new[] { latin, arabic })
            {
                shaped.Clear();
                shaper.Shape(font, "AaBbGgQqMmWwjy@#%&0123456789,.()", shaped);
                shaper.Shape(font, "السلام عليكم ورحمة الله وبركاته", shaped);

                var outline = new GlyphOutline();
                var seen = new HashSet<uint>();
                int compared = 0;
                foreach (var glyph in shaped)
                {
                    if (!seen.Add(glyph.GlyphId)) continue;

                    outline.Clear();
                    OutlineExtractor.Extract(font, glyph.GlyphId, outline);
                    var lo = new Vector2(float.MaxValue, float.MaxValue);
                    var hi = new Vector2(float.MinValue, float.MinValue);
                    bool hasInk = false;
                    foreach (var contour in outline.Contours)
                    {
                        if (contour.Count < 2) continue;
                        hasInk = true;
                        foreach (var p in contour)
                        {
                            lo = Vector2.Min(lo, p);
                            hi = Vector2.Max(hi, p);
                        }
                    }

                    bool reported = font.TryGetInkBounds(glyph.GlyphId, out var min, out var max);
                    Assert.AreEqual(hasInk, reported,
                        $"glyph {glyph.GlyphId}: disagreement about whether it has ink");
                    if (!hasInk) continue;

                    // The stored box may be looser than the ink — a cluster that
                    // merges slightly too eagerly costs cache reuse, never a
                    // seam. Tighter than the ink is the failure that matters.
                    // One unit of slack: the flattening is itself approximate.
                    Assert.LessOrEqual(min.x, lo.x + 1f, $"glyph {glyph.GlyphId}: box cuts ink on the left");
                    Assert.LessOrEqual(min.y, lo.y + 1f, $"glyph {glyph.GlyphId}: box cuts ink at the bottom");
                    Assert.GreaterOrEqual(max.x, hi.x - 1f, $"glyph {glyph.GlyphId}: box cuts ink on the right");
                    Assert.GreaterOrEqual(max.y, hi.y - 1f, $"glyph {glyph.GlyphId}: box cuts ink at the top");
                    compared++;
                }

                Assert.Greater(compared, 20, "the test needs glyphs to compare");
            }
        }
    }
}
