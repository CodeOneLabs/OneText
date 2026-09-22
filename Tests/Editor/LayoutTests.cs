using System.IO;
using OneText.Unicode;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>M4: layout, wrapping, alignment, font stacks and variable fonts.</summary>
    public class LayoutTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

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

        // ------------------------------------------- the inline axis, unwrapped

        private const string LongLine = "the quick brown fox jumps over the lazy dog";

        /// <summary>
        /// The ellipsis run on a line, if it has one.
        ///
        /// Recognised by carrying no text: the ellipsis is the one run in a
        /// result whose glyphs stand for nothing in the source string, which is
        /// exactly how a caret is kept out of it, so it is also the honest way
        /// to ask whether a line ends in one.
        /// </summary>
        private static int EllipsisRun(TextLayoutResult layout, in TextLine line)
        {
            for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
                if (layout.Runs[r].TextLength == 0 && layout.Runs[r].GlyphCount > 0) return r;
            return -1;
        }

        [Test]
        public void NoWrap_Ellipsis_Trims_To_The_Width()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Ellipsis;
            settings.MaxWidth = 120f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(LongLine, settings, result);

            Assert.AreEqual(1, result.Lines.Count, "NoWrap still makes exactly one line");
            Assert.IsTrue(result.Truncated, "the cut must be reported");
            Assert.Less(result.Lines[0].TextLength, LongLine.Length, "text was cut");
            Assert.LessOrEqual(result.Width, settings.MaxWidth + 0.01f, "the line fits the box");

            int ellipsis = EllipsisRun(result, result.Lines[0]);
            Assert.GreaterOrEqual(ellipsis, 0, "the line ends in an ellipsis run");
            Assert.AreEqual(font.NominalGlyph(0x2026),
                result.Glyphs[result.Runs[ellipsis].GlyphStart].GlyphId,
                "and that run draws U+2026");
            Assert.AreEqual(result.Lines[0].RunStart + result.Lines[0].RunCount - 1, ellipsis,
                "on the visual right of a left-to-right line");
        }

        [Test]
        public void NoWrap_Truncate_Trims_Without_An_Ellipsis()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Truncate;
            settings.MaxWidth = 120f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(LongLine, settings, result);

            Assert.AreEqual(1, result.Lines.Count);
            Assert.IsTrue(result.Truncated);
            Assert.Less(result.Lines[0].TextLength, LongLine.Length);
            Assert.LessOrEqual(result.Width, settings.MaxWidth + 0.01f);
            Assert.AreEqual(-1, EllipsisRun(result, result.Lines[0]),
                "Truncate marks nothing; that is what Ellipsis is for");
        }

        [Test]
        public void NoWrap_Overflow_Still_Runs_Past_The_Box()
        {
            // The guard on the old behaviour: Overflow is the setting that
            // means "let it stick out", and the inline clip must not reach it.
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Overflow;
            settings.MaxWidth = 120f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(LongLine, settings, result);

            Assert.AreEqual(1, result.Lines.Count);
            Assert.IsFalse(result.Truncated);
            Assert.AreEqual(LongLine.Length, result.Lines[0].TextLength, "nothing was cut");
            Assert.Greater(result.Width, settings.MaxWidth, "and it overflows, as asked");
            Assert.AreEqual(-1, EllipsisRun(result, result.Lines[0]));
        }

        [Test]
        public void NoWrap_Ellipsis_Leaves_Text_That_Fits_Alone()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Ellipsis;
            settings.MaxWidth = 400f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout("short", settings, result);

            Assert.AreEqual(1, result.Lines.Count);
            Assert.IsFalse(result.Truncated);
            Assert.AreEqual(5, result.Lines[0].TextLength);
            Assert.AreEqual(-1, EllipsisRun(result, result.Lines[0]),
                "text inside the box is never charged an ellipsis");
            Assert.LessOrEqual(result.Width, settings.MaxWidth);
        }

        [Test]
        public void NoWrap_Ellipsis_Trims_Every_Paragraph()
        {
            // A hard newline still starts a line under NoWrap, and each of
            // those lines runs out of the box on its own account.
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            const string text = "the quick brown fox jumps\nover the lazy dog again";
            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Ellipsis;
            settings.MaxWidth = 120f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(text, settings, result);

            Assert.AreEqual(2, result.Lines.Count);
            Assert.IsTrue(result.Truncated);
            foreach (var line in result.Lines)
            {
                Assert.LessOrEqual(line.Width, settings.MaxWidth + 0.01f, "each line fits");
                Assert.GreaterOrEqual(EllipsisRun(result, line), 0, "each line is marked");
            }
            Assert.AreEqual(0, result.Lines[0].TextStart);
            Assert.AreEqual(26, result.Lines[1].TextStart, "the second paragraph starts after \\n");
            Assert.Less(result.Lines[1].TextLength, 22, "and is cut on its own");
        }

        [Test]
        public void NoWrap_Ellipsis_Cuts_An_RTL_Line_At_Its_Visual_Left()
        {
            // The trim walks logical indices, so a right-to-left line keeps its
            // logical head — which is its visual *right* — and the ellipsis,
            // carrying the paragraph's odd level, reorders onto the left edge.
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);
            using var fonts = new FontStack();
            fonts.Add(latin);
            fonts.Add(arabic);

            const string text = "مرحبا بالعالم هذا نص عربي طويل جدا للاختبار";
            var settings = TextLayoutSettings.Default(fonts, 20f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Overflow = TextOverflow.Ellipsis;
            settings.MaxWidth = 120f;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(text, settings, result);

            Assert.AreEqual(1, result.Lines.Count);
            var line = result.Lines[0];
            Assert.IsTrue(line.IsRightToLeft, "an Arabic paragraph resolves right-to-left");
            Assert.IsTrue(result.Truncated);
            Assert.AreEqual(0, line.TextStart, "the logical head is what survives");
            Assert.Less(line.TextLength, text.Length);
            Assert.LessOrEqual(result.Width, settings.MaxWidth + 0.01f);

            int ellipsis = EllipsisRun(result, line);
            Assert.GreaterOrEqual(ellipsis, 0);
            Assert.AreEqual(line.RunStart, ellipsis,
                "runs are stored visually, so the left edge is the first of them");
            for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
                Assert.LessOrEqual(result.Runs[ellipsis].X, result.Runs[r].X,
                    "the ellipsis sits at the visual left");
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
