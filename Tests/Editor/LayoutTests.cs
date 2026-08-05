using System.IO;
using OneText.Unicode;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>M4: layout, wrapping, alignment, font stacks and variable fonts.</summary>
    public class LayoutTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansVariable.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        [Test]
        public void SingleLine_Has_One_Line_And_Positive_Metrics()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("Hello", TextLayoutSettings.Default(fonts, 32f), result);

            Assert.AreEqual(1, result.Lines.Count);
            Assert.Greater(result.Width, 0f);
            Assert.Greater(result.Height, 0f);
            Assert.Greater(result.Glyphs.Count, 0);
            Assert.Greater(result.Lines[0].Ascent, 0f, "ascent above the baseline");
            Assert.Greater(result.Lines[0].Descent, 0f, "descent below the baseline");
        }

        [Test]
        public void Newlines_Start_New_Lines()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("one\ntwo\r\nthree", TextLayoutSettings.Default(fonts, 32f), result);

            Assert.AreEqual(3, result.Lines.Count);
            Assert.Less(result.Lines[0].Baseline, result.Lines[1].Baseline, "lines advance downward");
            foreach (var line in result.Lines)
                Assert.IsTrue(line.IsParagraphEnd, "each hard line ends its paragraph");
        }

        [Test]
        public void Wrapping_Breaks_At_Spaces_And_Fits_The_Box()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 24f);
            settings.MaxWidth = 160f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("the quick brown fox jumps over the lazy dog", settings, result);

            Assert.Greater(result.Lines.Count, 2, "text must wrap into several lines");
            foreach (var line in result.Lines)
            {
                Assert.LessOrEqual(line.Width, settings.MaxWidth + 0.01f, "line overflows the box");
                var text = "the quick brown fox jumps over the lazy dog"
                    .Substring(line.TextStart, line.TextLength);
                Assert.IsFalse(text.StartsWith(" "), "wrapped lines start at a word: " + text);
            }
        }

        [Test]
        public void Long_Word_Breaks_At_Grapheme_Boundaries()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.MaxWidth = 60f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("Donaudampfschifffahrt", settings, result);

            Assert.Greater(result.Lines.Count, 1, "an unbreakable word still has to fit");
            foreach (var line in result.Lines)
                Assert.Greater(line.TextLength, 0, "emergency breaks must make progress");
        }

        [Test]
        public void Alignment_Moves_Runs_Inside_The_Box()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            float Left(TextAlignment alignment)
            {
                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.MaxWidth = 400f;
                settings.Alignment = alignment;
                var result = new TextLayoutResult();
                engine.Layout("short", settings, result);
                return result.Runs[0].X;
            }

            float left = Left(TextAlignment.Left);
            float center = Left(TextAlignment.Center);
            float right = Left(TextAlignment.Right);

            Assert.AreEqual(0f, left, 0.01f);
            Assert.Greater(center, left);
            Assert.Greater(right, center);
        }

        [Test]
        public void Justified_Lines_Fill_The_Box_Except_The_Last()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.MaxWidth = 200f;
            settings.Alignment = TextAlignment.Justified;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("the quick brown fox jumps over the lazy dog again and again", settings, result);

            Assert.Greater(result.Lines.Count, 1);
            for (int i = 0; i < result.Lines.Count - 1; i++)
                Assert.AreEqual(settings.MaxWidth, result.Lines[i].Width, 1f,
                    "justified lines must reach the box edge");
        }

        [Test]
        public void MixedDirection_Reorders_Runs_Visually()
        {
            using var font = LoadFont(ArabicFontPath);
            using var fonts = FontStack.Single(font);

            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.BaseDirection = 1; // RTL paragraph
            engine.Layout("مرحبا abc", settings, result);

            Assert.AreEqual(1, result.Lines.Count);
            Assert.GreaterOrEqual(result.Runs.Count, 2, "one run per direction");

            // In an RTL paragraph the Latin run sits to the left of the Arabic one.
            var first = result.Runs[0];
            var last = result.Runs[result.Runs.Count - 1];
            Assert.IsFalse(first.IsRightToLeft, "leftmost run should be the Latin one");
            Assert.IsTrue(last.IsRightToLeft);
            Assert.Less(first.X, last.X);
        }

        [Test]
        public void FontStack_Falls_Back_For_Uncovered_Characters()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var fonts = new FontStack();
            fonts.Add(latin);
            fonts.Add(arabic);

            Assert.AreEqual(latin, fonts.Resolve('A'));
            Assert.AreEqual(arabic, fonts.Resolve(0x0645), "Arabic meem is not in Noto Sans");

            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("A م", TextLayoutSettings.Default(fonts, 32f), result);

            bool usedLatin = false, usedArabic = false;
            foreach (var run in result.Runs)
            {
                usedLatin |= run.Font == latin;
                usedArabic |= run.Font == arabic;
            }
            Assert.IsTrue(usedLatin && usedArabic, "both fonts must appear in the layout");
        }

        [Test]
        public void Ellipsis_Truncates_To_The_Height_Budget()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.MaxWidth = 120f;
            settings.Overflow = TextOverflow.Ellipsis;
            var full = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("the quick brown fox jumps over the lazy dog", settings, full);
            Assert.Greater(full.Lines.Count, 2);

            settings.MaxHeight = full.Lines[0].Height * 2.2f;
            var clipped = new TextLayoutResult();
            engine.Layout("the quick brown fox jumps over the lazy dog", settings, clipped);

            Assert.IsTrue(clipped.Truncated);
            Assert.AreEqual(2, clipped.Lines.Count);
            Assert.LessOrEqual(clipped.Lines[1].Width, settings.MaxWidth + 0.01f);
        }

        [Test]
        public void VariableFont_Exposes_Axes_And_Changes_Advances()
        {
            using var font = LoadFont(VariableFontPath);
            Assert.IsTrue(font.IsVariable, "Noto Sans variable carries an fvar table");

            var axes = font.GetVariationAxes();
            Assert.Greater(axes.Length, 0);

            bool hasWeight = false;
            foreach (var axis in axes) hasWeight |= axis.Tag == "wght";
            Assert.IsTrue(hasWeight, "expected a wght axis");

            using var fonts = FontStack.Single(font);
            var thin = new TextLayoutResult();
            var bold = new TextLayoutResult();
            using var engine = new TextLayoutEngine();

            font.SetVariations(new FontVariation("wght", 100f));
            int generation = font.Generation;
            engine.Layout("Hamburgefonstiv", TextLayoutSettings.Default(fonts, 32f), thin);

            font.SetVariations(new FontVariation("wght", 900f));
            engine.Layout("Hamburgefonstiv", TextLayoutSettings.Default(fonts, 32f), bold);

            Assert.Greater(font.Generation, generation, "cache generation must change");
            Assert.Greater(bold.Width, thin.Width, "heavier weight is wider");
        }

        [Test]
        public void Empty_Text_Still_Reserves_A_Line()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("", TextLayoutSettings.Default(fonts, 32f), result);

            Assert.AreEqual(0, result.Runs.Count);
            Assert.Greater(result.Height, 0f);
        }
    }
}
