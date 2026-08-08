using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Unicode;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M15: vertical writing (縦書き).
    ///
    /// Three claims, and one test group each. That the engine knows which
    /// characters stand upright and which turn, which is UAX #50 and nothing
    /// else. That an upright run is shaped top-to-bottom, which is what makes
    /// the font hand over its vertical forms (。 in the top right of its
    /// square, small っ moved off the baseline) and its <c>vmtx</c> advances
    /// instead of its <c>hmtx</c> ones. And that a column is the engine's own
    /// line in a frame turned ninety degrees, so wrapping, kinsoku and ruby
    /// arrive already correct rather than re-implemented.
    ///
    /// The fourth group is the one that matters most and asserts nothing new:
    /// horizontal text is byte-for-byte what it was.
    /// </summary>
    public class VerticalTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string JapaneseFontPath =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKjp-Regular.otf";

        private static FontData LoadFont(string packagePath)
        {
            // The Japanese face is one of the coverage fonts, which are a
            // download rather than repository content. Vertical writing has
            // nothing to say without it, and a test that cannot run is not a
            // test that failed: say so, the way EmojiSequenceTests does.
            string full = Path.GetFullPath(packagePath);
            if (!File.Exists(full))
                Assert.Inconclusive($"No {Path.GetFileName(packagePath)}. " +
                                    "Run: python3 Tools/fetch_coverage_fonts.py");
            return FontData.Load(File.ReadAllBytes(full));
        }

        private static TextLayoutSettings Vertical(FontStack fonts, float size = 40f,
            float maxHeight = 0f)
        {
            var settings = TextLayoutSettings.Default(fonts, size);
            settings.WritingMode = TextWritingMode.VerticalRightToLeft;
            settings.Language = "ja";
            settings.MaxHeight = maxHeight;
            settings.Wrap = maxHeight > 0f ? TextWrap.Wrap : TextWrap.NoWrap;
            return settings;
        }

        private static TextLayoutResult Layout(TextLayoutEngine engine, string text,
            in TextLayoutSettings settings)
        {
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);
            return result;
        }

        private static List<TextRun> TextRuns(TextLayoutResult layout)
        {
            var runs = new List<TextRun>();
            foreach (var run in layout.Runs)
                if (!run.IsRuby) runs.Add(run);
            return runs;
        }

        private static uint FirstGlyph(Shaper shaper, FontData font, string text,
            Shaper.Direction direction)
        {
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, text, 0, text.Length, direction, glyphs, "ja");
            Assert.Greater(glyphs.Count, 0, $"nothing shaped for \"{text}\"");
            return glyphs[0].GlyphId;
        }

        // ------------------------------------------------------------- UAX #50

        [TestCase('A', VerticalOrientation.Rotated, TestName = "Latin capital is R")]
        [TestCase('z', VerticalOrientation.Rotated, TestName = "Latin small is R")]
        [TestCase('1', VerticalOrientation.Rotated, TestName = "ASCII digit is R")]
        [TestCase('Ж', VerticalOrientation.Rotated, TestName = "Cyrillic is R")]
        [TestCase('α', VerticalOrientation.Rotated, TestName = "Greek is R")]
        [TestCase('漢', VerticalOrientation.Upright, TestName = "Han is U")]
        [TestCase('あ', VerticalOrientation.Upright, TestName = "Hiragana is U")]
        [TestCase('カ', VerticalOrientation.Upright, TestName = "Katakana is U")]
        [TestCase('가', VerticalOrientation.Upright, TestName = "Hangul syllable is U")]
        [TestCase('０', VerticalOrientation.Upright, TestName = "Fullwidth digit is U")]
        [TestCase('　', VerticalOrientation.Upright, TestName = "Ideographic space is U")]
        [TestCase('。', VerticalOrientation.TransformedUpright, TestName = "Ideographic full stop is Tu")]
        [TestCase('、', VerticalOrientation.TransformedUpright, TestName = "Ideographic comma is Tu")]
        [TestCase('っ', VerticalOrientation.TransformedUpright, TestName = "Small tu is Tu")]
        [TestCase('！', VerticalOrientation.TransformedUpright, TestName = "Fullwidth bang is Tu")]
        [TestCase('「', VerticalOrientation.TransformedRotated, TestName = "Corner bracket is Tr")]
        [TestCase('ー', VerticalOrientation.TransformedRotated, TestName = "Prolonged sound mark is Tr")]
        [TestCase('“', VerticalOrientation.TransformedRotated, TestName = "Double quote is Tr")]
        public void VerticalOrientation_MatchesTheProperty(char c, VerticalOrientation expected)
        {
            Assert.AreEqual(expected, VerticalOrientationLookup.Get(c));
        }

        [Test]
        public void Emoji_IsUpright()
        {
            // A face in a column stands up like a kanji. It is also a surrogate
            // pair, and looking at one code unit would answer for a lone
            // surrogate, which the property calls rotated.
            Assert.AreEqual(VerticalOrientation.Upright,
                VerticalOrientationLookup.Get(0x1F600));
            Assert.AreEqual(VerticalOrientation.Upright,
                VerticalOrientationLookup.Get("\U0001F600", 0));
        }

        [Test]
        public void OnlyTheTransformedRotatedClass_HasToAskTheFont()
        {
            // The one per-character cost vertical itemization carries is two
            // shaping calls to find out whether the face has a vertical form.
            // It must be paid for 「 and for nothing else.
            Assert.IsTrue(VerticalOrientationLookup.NeedsFont(VerticalOrientation.TransformedRotated));
            Assert.IsFalse(VerticalOrientationLookup.NeedsFont(VerticalOrientation.Upright));
            Assert.IsFalse(VerticalOrientationLookup.NeedsFont(VerticalOrientation.TransformedUpright));
            Assert.IsFalse(VerticalOrientationLookup.NeedsFont(VerticalOrientation.Rotated));

            // And the fallbacks are the property's own: Tr rotates when the
            // font offers nothing, Tu stands up regardless.
            Assert.IsFalse(VerticalOrientationLookup.Resolve(
                VerticalOrientation.TransformedRotated, hasVerticalForm: false));
            Assert.IsTrue(VerticalOrientationLookup.Resolve(
                VerticalOrientation.TransformedRotated, hasVerticalForm: true));
            Assert.IsTrue(VerticalOrientationLookup.Resolve(
                VerticalOrientation.TransformedUpright, hasVerticalForm: false));
        }

        // ------------------------------------------------------------- shaping

        [Test]
        public void TopToBottomShaping_SelectsTheVerticalForms()
        {
            // This is the whole reason a vertical run is shaped top-to-bottom
            // rather than laid out from horizontal glyphs: 。 sits in the
            // bottom left of its square across the page and in the top right of
            // it down a column, and only the font knows what that glyph is.
            using var font = LoadFont(JapaneseFontPath);
            using var shaper = new Shaper();

            foreach (string mark in new[] { "。", "、", "「", "」", "ー", "っ", "ゃ" })
            {
                uint horizontal = FirstGlyph(shaper, font, mark, Shaper.Direction.LeftToRight);
                uint vertical = FirstGlyph(shaper, font, mark, Shaper.Direction.TopToBottom);
                Assert.AreNotEqual(horizontal, vertical,
                    $"\"{mark}\" should have a vertical form in this face");
            }
        }

        [Test]
        public void AnOrdinaryKanji_KeepsItsGlyphInAColumn()
        {
            // The other half of the same claim: `vert` is a substitution over
            // the marks that need one, not a second alphabet.
            using var font = LoadFont(JapaneseFontPath);
            using var shaper = new Shaper();

            Assert.AreEqual(FirstGlyph(shaper, font, "漢", Shaper.Direction.LeftToRight),
                FirstGlyph(shaper, font, "漢", Shaper.Direction.TopToBottom));
        }

        [Test]
        public void VerticalGlyphs_ComeBackInFlowSpace()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "日本語", 0, 3, Shaper.Direction.TopToBottom, glyphs, "ja");

            Assert.AreEqual(3, glyphs.Count);
            foreach (var glyph in glyphs)
            {
                // The advance is along the column and positive, which is what
                // lets every measurement in the engine stay one walk over
                // XAdvance. vmtx gives a full em for Han.
                Assert.AreEqual(font.UnitsPerEm, glyph.XAdvance, font.UnitsPerEm * 0.02f,
                    "an upright Han glyph advances one em down the column");
                Assert.AreEqual(0, glyph.YAdvance, "the y axis is spent");
                // And the cross-axis offset centres the glyph on the column,
                // which is VORG/vmtx doing its job: half an em to the left.
                Assert.Less(glyph.YOffset, 0, "the glyph is pulled back onto the column's centre");
            }
        }

        // -------------------------------------------------------------- layout

        [Test]
        public void ColumnsProgressRightToLeft()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "一二三\n四五六", Vertical(fonts));

            Assert.AreEqual(2, layout.Lines.Count);
            Assert.Greater(layout.Lines[1].Baseline, layout.Lines[0].Baseline,
                "the block axis grows away from the right edge, so column two is further along it");
            Assert.AreEqual(TextWritingMode.VerticalRightToLeft, layout.WritingMode);
            Assert.Greater(layout.Height, layout.Width,
                "three characters down and two columns across is taller than it is wide");
            Assert.AreEqual(layout.Height, layout.InlineExtent, 0.01f);
            Assert.AreEqual(layout.Width, layout.BlockExtent, 0.01f);
        }

        [Test]
        public void ACharacterAdvancesDownTheColumn_ByItsEm()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "日本語", Vertical(fonts, 40f));

            Assert.AreEqual(120f, layout.Lines[0].Width, 2f, "three ems down the column");
            Assert.AreEqual(40f, layout.Width, 2f, "one column, one em across: JLREQ's grid");
        }

        [Test]
        public void AColumnEndsAtTheBoxHeight()
        {
            // The wrap limit is the box's height down a column, and its width
            // is what overflow spends. That is the only thing the two settings
            // swap, and getting it wrong would wrap at the wrong number
            // silently.
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var settings = Vertical(fonts, 40f, maxHeight: 130f);
            var layout = Layout(engine, "一二三四五六七八", settings);

            Assert.Greater(layout.Lines.Count, 1, "eight ems do not fit in 130 units");
            foreach (var line in layout.Lines)
                Assert.LessOrEqual(line.Width, 130f + 0.5f, "no column runs past the box");
        }

        [Test]
        public void KinsokuHoldsAtAColumnEnd()
        {
            // The line-break pipeline is shared whole: kinsoku is a tailoring
            // of the UAX #14 opportunity table, and a column is a line.
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            const string text = "あいうえお。かきくけこ";
            var settings = Vertical(fonts, 40f, maxHeight: 220f);
            settings.Kinsoku = AsianTypography.Kinsoku.Normal;
            var layout = Layout(engine, text, settings);

            Assert.Greater(layout.Lines.Count, 1);
            foreach (var line in layout.Lines)
                Assert.AreNotEqual('。', text[line.TextStart],
                    "行頭禁則: a column never begins with a full stop");
        }

        [Test]
        public void LatinInsideACjkColumn_IsARotatedRunOfItsOwn()
        {
            using var latin = LoadFont(LatinFontPath);
            using var japanese = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(japanese);
            fonts.Add(latin);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "図OKです", Vertical(fonts, 40f));
            var runs = TextRuns(layout);

            Assert.AreEqual(3, runs.Count, "upright, rotated, upright");
            Assert.IsFalse(runs[0].Rotated, "図 stands up");
            Assert.IsTrue(runs[1].Rotated, "OK lies on its side");
            Assert.IsFalse(runs[2].Rotated, "です stands up");

            // A rotated run is a horizontal run: same glyphs, same advances,
            // same kerning; only the drawing turns. So its extent down the
            // column is the width the same text would have across the page.
            var flat = TextLayoutSettings.Default(fonts, 40f);
            flat.Wrap = TextWrap.NoWrap;
            var horizontal = Layout(engine, "OK", flat);
            Assert.AreEqual(horizontal.Width, runs[1].Width, 0.01f);
        }

        [Test]
        public void ARotatedRunSitsCentredInItsColumn()
        {
            // A horizontal baseline has unequal ink above and below it. Laying
            // it on the column's centre line would push a stretch of Latin
            // visibly to one side of the column it is set in.
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "Ay", Vertical(fonts, 40f));
            var run = TextRuns(layout)[0];

            Assert.IsTrue(run.Rotated);
            Assert.Greater(run.CrossAxisBaselineOffset, 0f,
                "the baseline moves back by half the line box");

            float scale = run.FontSize / run.Font.UnitsPerEm;
            float half = (run.Font.Ascender - run.Font.Descender) * scale * 0.5f;
            Assert.AreEqual(half, layout.Lines[0].Ascent, 0.01f);
            Assert.AreEqual(half, layout.Lines[0].Descent, 0.01f,
                "a rotated column is symmetric about its centre line");
        }

        [Test]
        public void ASpaceDoesNotSplitAColumnIntoThreeRuns()
        {
            // A space rotates by the property and by nothing else: it has no
            // ink to turn, and letting it end a run would cost a shaping call
            // and a line-metric vote for nothing.
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "日 本", Vertical(fonts, 40f));

            Assert.AreEqual(1, TextRuns(layout).Count);
            Assert.IsFalse(TextRuns(layout)[0].Rotated);
        }

        [Test]
        public void AnEmptyVerticalLabel_StillOccupiesOneColumn()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var layout = Layout(engine, "", Vertical(fonts, 40f));

            Assert.AreEqual(40f, layout.Width, 0.01f, "one em of column, waiting");
            Assert.AreEqual(0f, layout.Height, 0.01f);
        }

        // ---------------------------------------------------------------- ruby

        [Test]
        public void RubySitsToTheRightOfItsColumn()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var markup = new RichTextResult();
            RichTextParser.Parse("<ruby=かんじ>漢字</ruby>", markup);
            var settings = Vertical(fonts, 40f);
            settings.Spans = markup.Spans;
            settings.Rubies = markup.Rubies;
            var layout = Layout(engine, markup.Text, settings);

            TextRun ruby = default;
            foreach (var run in layout.Runs)
                if (run.IsRuby) ruby = run;
            Assert.Greater(ruby.GlyphCount, 0, "the annotation was laid out");

            // The block axis grows leftward, so "less" is "further right",
            // which is where JLREQ puts a reading beside a vertical column, and
            // it is the same subtraction the horizontal case makes.
            Assert.Less(ruby.Baseline, layout.Lines[0].Baseline,
                "the annotation sits to the right of the text it annotates");
            Assert.AreEqual(20f, ruby.FontSize, 0.01f, "half the size of its base");

            // The column is wider by exactly the annotation, on the ruby's side
            // only; nothing reaches into the column to its right.
            Assert.AreEqual(20f + 20f, layout.Lines[0].Ascent, 0.5f);
            Assert.AreEqual(20f, layout.Lines[0].Descent, 0.5f);
        }

        [Test]
        public void RubyRunsDownTheColumnWithItsBase()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var markup = new RichTextResult();
            RichTextParser.Parse("<ruby=かんじ>漢字</ruby>", markup);
            var settings = Vertical(fonts, 40f);
            settings.Spans = markup.Spans;
            settings.Rubies = markup.Rubies;
            var layout = Layout(engine, markup.Text, settings);

            TextRun ruby = default, text = default;
            foreach (var run in layout.Runs)
                if (run.IsRuby) ruby = run;
                else text = run;

            // Centred on the base along the column, exactly as it is centred on
            // it along a line: three half-size kana over two full-size kanji.
            Assert.AreEqual(text.X + text.Width * 0.5f, ruby.X + ruby.Width * 0.5f, 1f);

            // And the annotation still carries the base's clusters, which is
            // what keeps reveal and effects working without knowing about it.
            Assert.AreEqual(0, layout.Glyphs[ruby.GlyphStart].Cluster);
            Assert.AreEqual(1, layout.Glyphs[ruby.GlyphStart + ruby.GlyphCount - 1].Cluster);
        }

        // ----------------------------------------------------- reveal / indices

        [Test]
        public void GraphemeIndices_AreTheSameTextEitherWay()
        {
            // Reveal, carets, effect spans and links all count graphemes, and
            // turning the page ninety degrees does not change what a reader
            // reads. The clusters on a column's glyphs ascend as they do on a
            // line's.
            using var latin = LoadFont(LatinFontPath);
            using var japanese = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(japanese);
            fonts.Add(latin);
            using var engine = new TextLayoutEngine();

            const string text = "図OKです。";
            var flat = TextLayoutSettings.Default(fonts, 40f);
            flat.Wrap = TextWrap.NoWrap;
            var horizontal = Layout(engine, text, flat);
            var vertical = Layout(engine, text, Vertical(fonts, 40f));

            CollectionAssert.AreEqual(horizontal.GraphemeStarts, vertical.GraphemeStarts);
            Assert.AreEqual(horizontal.GraphemeCount, vertical.GraphemeCount);

            foreach (var run in TextRuns(vertical))
            {
                int previous = -1;
                for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
                {
                    Assert.GreaterOrEqual(vertical.Glyphs[g].Cluster, previous,
                        "clusters run forward down a column");
                    previous = vertical.Glyphs[g].Cluster;
                }
            }
        }

        // -------------------------------------------- horizontal is untouched

        [Test]
        public void HorizontalIsTheDefault()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var settings = TextLayoutSettings.Default(fonts, 32f);
            Assert.AreEqual(TextWritingMode.Horizontal, settings.WritingMode);
            Assert.AreEqual(TextWritingMode.Horizontal, new TextLayoutResult().WritingMode);
        }

        [Test]
        public void AVerticalLayout_LeavesNoTraceOnTheNextHorizontalOne()
        {
            // One engine, reused (which is what a label does), so the vertical
            // path has to leave the item list, the bidi run list, the
            // measured-glyph buffer and the vertical-form cache exactly as it
            // found them. This is the test that would catch a state leak, and a
            // state leak here is horizontal text that renders differently
            // depending on what was laid out before it.
            using var latin = LoadFont(LatinFontPath);
            using var japanese = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(japanese);
            fonts.Add(latin);
            using var engine = new TextLayoutEngine();

            const string text = "図OKです。あいうえお";
            var flat = TextLayoutSettings.Default(fonts, 36f);
            flat.MaxWidth = 200f;
            flat.Kinsoku = AsianTypography.Kinsoku.Normal;

            var before = Layout(engine, text, flat);
            var beforeCopy = Snapshot(before);
            Layout(engine, text, Vertical(fonts, 36f, maxHeight: 200f));
            var after = Layout(engine, text, flat);

            CollectionAssert.AreEqual(beforeCopy, Snapshot(after),
                "horizontal geometry is what it was");
        }

        [Test]
        public void HorizontalGeometry_IsUnchangedByTheVerticalFields()
        {
            // The fields vertical writing added are inert horizontally: no run
            // is rotated, no run's baseline is offset from its own, and width
            // and height still mean what they meant.
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.MaxWidth = 300f;
            var layout = Layout(engine, "The quick brown fox jumps over the lazy dog.", settings);

            Assert.Greater(layout.Lines.Count, 1);
            Assert.AreEqual(layout.Width, layout.InlineExtent, 0.01f);
            Assert.AreEqual(layout.Height, layout.BlockExtent, 0.01f);
            foreach (var run in layout.Runs)
            {
                Assert.IsFalse(run.Rotated);
                Assert.AreEqual(0f, run.CrossAxisBaselineOffset);
            }
        }

        /// <summary>Every number a laid-out block puts on screen, in one list.</summary>
        private static List<float> Snapshot(TextLayoutResult layout)
        {
            var values = new List<float> { layout.Width, layout.Height, layout.Lines.Count };
            foreach (var line in layout.Lines)
            {
                values.Add(line.Width);
                values.Add(line.Baseline);
                values.Add(line.Ascent);
                values.Add(line.Descent);
                values.Add(line.Height);
            }
            foreach (var run in layout.Runs)
            {
                values.Add(run.X);
                values.Add(run.Baseline);
                values.Add(run.Width);
                values.Add(run.GlyphCount);
            }
            foreach (var glyph in layout.Glyphs)
            {
                values.Add(glyph.GlyphId);
                values.Add(glyph.Cluster);
                values.Add(glyph.XAdvance);
                values.Add(glyph.XOffset);
                values.Add(glyph.YOffset);
            }
            return values;
        }
    }
}
