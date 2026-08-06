using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M15: ruby (furigana). The claim is that ruby is a layout-engine feature
    /// and not a tag hack, so the tests are the three things a tag hack cannot
    /// do (the annotation is sized and centred by the engine, the line grows
    /// to hold it, and a base never splits away from its reading), plus the
    /// one thing that makes the rest of the engine not care: a ruby glyph
    /// carries the cluster of the base character it sits over, so reveal,
    /// effects and decorations find it without knowing ruby exists.
    ///
    /// Placement follows the W3C note "Rules for Simple Placement of Japanese
    /// Ruby" (and JLREQ behind it): half size by default, slack distributed
    /// with double gaps between characters and half at the ends, and a wide
    /// annotation allowed to hang only over a neighbour's blank.
    /// </summary>
    public class RubyTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string JapaneseFontPath =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKjp-Regular.otf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static RichTextResult Parse(string source)
        {
            var result = new RichTextResult();
            RichTextParser.Parse(source, result);
            return result;
        }

        private static TextLayoutResult Layout(FontStack fonts, RichTextResult markup,
            float size = 40f, float maxWidth = 0f, float rubyScale = 0f)
        {
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, size);
            settings.Spans = markup.HasMarkup ? markup.Spans : null;
            settings.Rubies = markup.Rubies.Count > 0 ? markup.Rubies : null;
            settings.RubyScale = rubyScale;
            settings.MaxWidth = maxWidth;
            settings.Wrap = maxWidth > 0f ? TextWrap.Wrap : TextWrap.NoWrap;
            var result = new TextLayoutResult();
            engine.Layout(markup.Text, settings, result);
            return result;
        }

        private static List<TextRun> RubyRuns(TextLayoutResult layout)
        {
            var runs = new List<TextRun>();
            foreach (var run in layout.Runs)
                if (run.IsRuby) runs.Add(run);
            return runs;
        }

        private static (float Min, float Max) Extent(TextLayoutResult layout, IEnumerable<TextRun> runs)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var run in runs)
            {
                min = Mathf.Min(min, run.X);
                max = Mathf.Max(max, run.X + run.Width);
            }
            return (min, max);
        }

        private static (float Min, float Max) BaseExtent(TextLayoutResult layout, int start, int end)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var run in layout.Runs)
            {
                if (run.IsRuby) continue;
                float scale = run.FontSize / run.Font.UnitsPerEm;
                float pen = run.X;
                for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
                {
                    var glyph = layout.Glyphs[g];
                    float advance = glyph.XAdvance * scale;
                    if (glyph.Cluster >= start && glyph.Cluster < end)
                    {
                        min = Mathf.Min(min, pen);
                        max = Mathf.Max(max, pen + advance);
                    }
                    pen += advance;
                }
            }
            return (min, max);
        }

        // ----------------------------------------------------------- the markup

        [Test]
        public void RubyTag_KeepsTheBaseInTheTextAndTheAnnotationOutOfIt()
        {
            var result = Parse("<ruby=ふりがな>漢字</ruby>です");

            Assert.AreEqual("漢字です", result.Text,
                "the annotation is not text; it has no indices of its own");
            Assert.AreEqual(1, result.Rubies.Count);
            Assert.AreEqual("ふりがな", result.Rubies[0].Text);
            Assert.AreEqual(0, result.Rubies[0].Start);
            Assert.AreEqual(2, result.Rubies[0].Length);
        }

        [Test]
        public void RubyAnnotation_MayBeAnyScript()
        {
            // The annotation is shaped text, not a kana decoration: a romaji
            // gloss and a Hangul reading go through the same path.
            var latin = Parse("<ruby=kanji>漢字</ruby>");
            Assert.AreEqual("kanji", latin.Rubies[0].Text);

            var hangul = Parse("<ruby=한자>漢字</ruby>");
            Assert.AreEqual("한자", hangul.Rubies[0].Text);
        }

        [TestCase("<ruby>x</ruby>", "<ruby>x</ruby>")]
        [TestCase("<ruby=>x</ruby>", "<ruby=>x</ruby>")]
        public void MalformedRuby_StaysLiteral(string source, string expected)
        {
            // A ruby with nothing to say is a typo, and the house rule for a
            // typo is that it stays visible rather than silently doing nothing.
            Assert.AreEqual(expected, Parse(source).Text);
        }

        [Test]
        public void NestedRuby_StaysLiteral()
        {
            // Two annotations over one base is double-sided ruby, which is a
            // placement problem of its own and never what a stray nested tag
            // meant.
            var result = Parse("<ruby=あ>漢<ruby=い>字</ruby></ruby>");

            StringAssert.Contains("<ruby=い>", result.Text);
            Assert.AreEqual(1, result.Rubies.Count);
        }

        [Test]
        public void UnterminatedRuby_AnnotatesTheRestOfTheText()
        {
            var result = Parse("<ruby=よ>読みかけ");

            Assert.AreEqual("読みかけ", result.Text);
            Assert.AreEqual(1, result.Rubies.Count);
            Assert.AreEqual(4, result.Rubies[0].Length);
        }

        [Test]
        public void RubyOverNothing_IsDropped()
        {
            Assert.AreEqual(0, Parse("<ruby=あ></ruby>x").Rubies.Count,
                "there is no advance to centre an annotation over");
        }

        [Test]
        public void Ruby_NestsInsideOtherTags()
        {
            var result = Parse("<b><color=red><ruby=かん>漢</ruby></color></b>");

            Assert.AreEqual("漢", result.Text);
            Assert.AreEqual(1, result.Rubies.Count);
            Assert.IsTrue(result.StyleAt(0).Bold);
            Assert.IsTrue(result.StyleAt(0).HasColor);
        }

        // ----------------------------------------------------------- the layout

        [Test]
        public void Ruby_IsLaidOutAsRunsAboveTheBaseline()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"));

            var ruby = RubyRuns(layout);
            Assert.AreEqual(1, ruby.Count, "one font, one ruby run");
            Assert.Greater(ruby[0].GlyphCount, 0);
            Assert.Less(ruby[0].Baseline, layout.Lines[0].Baseline,
                "the annotation sits above the text it annotates");
            Assert.AreEqual(1, layout.Lines[0].RunCount - ruby.Count,
                "the base is still one run of its own");
        }

        [Test]
        public void RubyGlyphs_CarryTheClustersOfTheBaseTheySitOver()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"));

            var ruby = RubyRuns(layout)[0];
            int first = layout.Glyphs[ruby.GlyphStart].Cluster;
            int last = layout.Glyphs[ruby.GlyphStart + ruby.GlyphCount - 1].Cluster;

            Assert.AreEqual(0, first, "the reading starts with the first base character");
            Assert.AreEqual(1, last, "and ends with the last");
            for (int g = ruby.GlyphStart; g < ruby.GlyphStart + ruby.GlyphCount; g++)
            {
                int cluster = layout.Glyphs[g].Cluster;
                Assert.GreaterOrEqual(cluster, 0);
                Assert.Less(cluster, 2, "a ruby glyph never points outside its base");
            }
        }

        [Test]
        public void Ruby_IsHalfTheSizeOfItsBaseByDefault()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f);

            Assert.AreEqual(20f, RubyRuns(layout)[0].FontSize, 0.001f);
        }

        [Test]
        public void RubyScale_SetsTheSize()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            var half = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f);
            var big = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f, rubyScale: 0.75f);

            Assert.AreEqual(30f, RubyRuns(big)[0].FontSize, 0.001f);
            Assert.Greater(RubyRuns(big)[0].Width, RubyRuns(half)[0].Width,
                "a bigger annotation is a wider one");
        }

        [Test]
        public void Ruby_FollowsTheSizeOfTheTextItAnnotates()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            // Ruby on a doubled heading is half of the heading, not half of
            // the label.
            var layout = Layout(fonts, Parse("<size=200%><ruby=ふり>漢</ruby></size>"), size: 40f);

            Assert.AreEqual(40f, RubyRuns(layout)[0].FontSize, 0.001f);
        }

        [Test]
        public void NarrowRuby_IsCentredOverItsBase()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            // Two kana at half size over two full-width kanji: the annotation
            // is half the width of the base, so there is slack to distribute.
            var layout = Layout(fonts, Parse("<ruby=かん>漢字</ruby>"), size: 40f);

            var (rubyMin, rubyMax) = Extent(layout, RubyRuns(layout));
            var (baseMin, baseMax) = BaseExtent(layout, 0, 2);

            Assert.Less(rubyMax - rubyMin, baseMax - baseMin, "narrower than its base");
            Assert.AreEqual((baseMin + baseMax) * 0.5f, (rubyMin + rubyMax) * 0.5f, 0.5f,
                "and centred on it");
            Assert.GreaterOrEqual(rubyMin, baseMin - 0.01f, "with no overhang either side");
            Assert.LessOrEqual(rubyMax, baseMax + 0.01f);
        }

        [Test]
        public void LatinRuby_IsCentredAndNotLetterSpaced()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            // The spec's distribution rule is about ruby set on the em grid.
            // A romaji gloss is a word with its own fitting; spreading its
            // letters to fill the base would be breaking it, not setting it.
            var latin = Layout(fonts, Parse("<ruby=kanji>漢字</ruby>"), size: 40f);
            var kana = Layout(fonts, Parse("<ruby=かんじ>漢字</ruby>"), size: 40f);

            float unspaced = 0f;
            var run = RubyRuns(latin)[0];
            float scale = run.FontSize / run.Font.UnitsPerEm;
            for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
                unspaced += latin.Glyphs[g].XAdvance * scale;
            Assert.AreEqual(unspaced, run.Width, 0.001f, "no gap was added between letters");

            var (min, max) = Extent(latin, RubyRuns(latin));
            var (baseMin, baseMax) = BaseExtent(latin, 0, 2);
            Assert.AreEqual((baseMin + baseMax) * 0.5f, (min + max) * 0.5f, 0.5f, "centred instead");

            // Kana over the same base does get spread.
            var kanaRun = RubyRuns(kana)[0];
            float kanaGlyphs = 0f;
            float kanaScale = kanaRun.FontSize / kanaRun.Font.UnitsPerEm;
            for (int g = kanaRun.GlyphStart; g < kanaRun.GlyphStart + kanaRun.GlyphCount; g++)
                kanaGlyphs += kana.Glyphs[g].XAdvance * kanaScale;
            Assert.Greater(kanaRun.Width, 0f);
            Assert.AreEqual(kanaGlyphs, kanaRun.Width, 0.001f);
            Assert.Greater(kanaRun.Width, 3f * kanaRun.FontSize * 0.99f,
                "three full-width kana, plus the distribution between them");
        }

        [Test]
        public void WideRuby_PadsABaseWhoseNeighboursWillNotYield()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("あ漢あ"), size: 40f);
            var annotated = Layout(fonts, Parse("あ<ruby=かんじだよ>漢</ruby>あ"), size: 40f);

            // Kana on both sides: JLREQ lets ruby hang only over a neighbour's
            // blank, and a kana has none, so the base has to make the room.
            Assert.Greater(annotated.Lines[0].Width, plain.Lines[0].Width + 1f,
                "the line grew to hold an annotation nothing would give way for");

            var (rubyMin, rubyMax) = Extent(annotated, RubyRuns(annotated));
            var (baseMin, baseMax) = BaseExtent(annotated, 1, 2);
            Assert.AreEqual((baseMin + baseMax) * 0.5f, (rubyMin + rubyMax) * 0.5f, 1f,
                "the padded base stays centred under its reading");
            Assert.AreEqual(rubyMax - rubyMin, baseMax - baseMin, 1.5f,
                "and the padding is exactly what the reading needed");
        }

        [Test]
        public void WideRuby_HangsOverTheBlankOfAPunctuationNeighbour()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            // A full stop before the base has blank on its right, which the
            // spec lets a wide annotation hang over; a kana does not.
            var overKana = Layout(fonts, Parse("あ<ruby=かんじだよ>漢</ruby>"), size: 40f);
            var overStop = Layout(fonts, Parse("。<ruby=かんじだよ>漢</ruby>"), size: 40f);

            Assert.Less(overStop.Lines[0].Width, overKana.Lines[0].Width,
                "blank that was already there is not paid for twice");

            var (rubyMin, _) = Extent(overStop, RubyRuns(overStop));
            var (baseMin, _) = BaseExtent(overStop, 1, 2);
            Assert.Less(rubyMin, baseMin, "the annotation reaches back into the mark's blank");
        }

        [Test]
        public void LineWithRuby_IsTallerAndTheAscentIsWhatGrows()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("漢字"), size: 40f);
            var annotated = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f);

            Assert.Greater(annotated.Lines[0].Ascent, plain.Lines[0].Ascent,
                "room is made above the baseline, where the annotation is");
            Assert.AreEqual(plain.Lines[0].Descent, annotated.Lines[0].Descent, 0.001f,
                "and not below it");
            Assert.Greater(annotated.Height, plain.Height);

            // The point of growing the line: nothing of the annotation may
            // reach into the line above.
            var ruby = RubyRuns(annotated)[0];
            float top = ruby.Baseline - ruby.Font.Ascender * (ruby.FontSize / ruby.Font.UnitsPerEm);
            var line = annotated.Lines[0];
            Assert.GreaterOrEqual(top, line.Baseline - line.Ascent - 0.01f);
        }

        [Test]
        public void SecondLine_IsNotPushedDownByRubyOnTheFirst()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>\n漢字"), size: 40f);

            Assert.AreEqual(2, layout.Lines.Count);
            Assert.Greater(layout.Lines[0].Height, layout.Lines[1].Height,
                "only the line that carries an annotation pays for it");
        }

        [Test]
        public void Ruby_AddsNothingToTheLineWidth()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("漢字"), size: 40f);
            var annotated = Layout(fonts, Parse("<ruby=かん>漢字</ruby>"), size: 40f);

            // A narrow annotation fits over its base, so the line is the same
            // line; the annotation is not in the advance.
            Assert.AreEqual(plain.Lines[0].Width, annotated.Lines[0].Width, 0.001f);
        }

        // -------------------------------------------------------- line breaking

        [Test]
        public void BaseAndItsRuby_DoNotSplitAcrossLines()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            // Japanese breaks between any two ideographs, so without the
            // tailoring this base would be cut in half by a narrow box.
            var markup = Parse("あああ<ruby=にほんご>日本語</ruby>ああ");
            var layout = Layout(fonts, markup, size: 40f, maxWidth: 170f);

            Assert.Greater(layout.Lines.Count, 1, "the box is narrow enough to wrap");
            var ruby = markup.Rubies[0];
            foreach (var line in layout.Lines)
            {
                int end = line.TextStart + line.TextLength;
                Assert.IsFalse(line.TextStart > ruby.Start && line.TextStart < ruby.End,
                    "no line may begin inside an annotated base");
                Assert.IsFalse(end > ruby.Start && end < ruby.End,
                    "and none may end inside one");
            }
        }

        [Test]
        public void WrappedRuby_TravelsWithItsBase()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);

            var markup = Parse("あああ<ruby=にほんご>日本語</ruby>ああ");
            var layout = Layout(fonts, markup, size: 40f, maxWidth: 170f);

            int rubyIndex = -1;
            for (int r = 0; r < layout.Runs.Count; r++)
                if (layout.Runs[r].IsRuby) rubyIndex = r;
            Assert.GreaterOrEqual(rubyIndex, 0, "exactly one annotation, laid out once");
            Assert.AreEqual(1, RubyRuns(layout).Count);

            // The annotation is in the run range of the line its base landed
            // on, and sits above that line's baseline, which is what makes it
            // move with the base through alignment and truncation.
            int owner = -1;
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var candidate = layout.Lines[i];
                if (candidate.TextStart <= markup.Rubies[0].Start &&
                    markup.Rubies[0].Start < candidate.TextStart + candidate.TextLength) owner = i;
            }
            Assert.GreaterOrEqual(owner, 0);

            var line = layout.Lines[owner];
            Assert.GreaterOrEqual(rubyIndex, line.RunStart);
            Assert.Less(rubyIndex, line.RunStart + line.RunCount);
            Assert.Less(layout.Runs[rubyIndex].Baseline, line.Baseline);
            Assert.Greater(layout.Runs[rubyIndex].Baseline, line.Baseline - line.Ascent);
        }

        [Test]
        public void TwoAnnotations_AreEachCentredOverTheirOwnBase()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=かん>漢</ruby>と<ruby=じ>字</ruby>"), size: 40f);

            var ruby = RubyRuns(layout);
            Assert.AreEqual(2, ruby.Count);

            foreach (var (runIndex, from, to) in new[] { (0, 0, 1), (1, 2, 3) })
            {
                var (baseMin, baseMax) = BaseExtent(layout, from, to);
                float centre = ruby[runIndex].X + ruby[runIndex].Width * 0.5f;
                Assert.AreEqual((baseMin + baseMax) * 0.5f, centre, 0.5f);
            }
        }

        [Test]
        public void Ellipsis_IsNotWidenedByARubyAtTheStartOfTheText()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            // The ellipsis is shaped over "…", whose index 0 is not the text's
            // index 0; the padding a wide ruby charged the first character
            // must not land on it.
            var markup = Parse("<ruby=むずかしいよみかた>難</ruby>しい字がここにあります");
            var settings = TextLayoutSettings.Default(fonts, 40f);
            settings.Spans = markup.Spans;
            settings.Rubies = markup.Rubies;
            settings.MaxWidth = 200f;
            settings.MaxHeight = 130f;
            settings.Overflow = TextOverflow.Ellipsis;
            var layout = new TextLayoutResult();
            engine.Layout(markup.Text, settings, layout);

            Assert.IsTrue(layout.Truncated, "the box is too short for this text");

            bool sawEllipsis = false;
            foreach (var run in layout.Runs)
            {
                if (run.IsRuby || run.TextLength != 0) continue;
                sawEllipsis = true;
                Assert.Less(run.Width, 60f, "an ellipsis is one narrow glyph");
            }
            Assert.IsTrue(sawEllipsis);
        }

        // ------------------------------------------------------- interactions

        [Test]
        public void Reveal_ShowsARubyGlyphWithTheBaseCharacterItReads()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f);

            // Four kana over two kanji: the first two belong to 漢 and the last
            // two to 字, so a typewriter that has revealed only 漢 shows ふり
            // and not がな.
            var ruby = RubyRuns(layout)[0];
            var clusters = new List<int>();
            for (int g = ruby.GlyphStart; g < ruby.GlyphStart + ruby.GlyphCount; g++)
                clusters.Add(layout.Glyphs[g].Cluster);

            CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, clusters);
            for (int i = 1; i < clusters.Count; i++)
                Assert.GreaterOrEqual(clusters[i], clusters[i - 1], "reveal order is reading order");
        }

        [Test]
        public void Caret_NeverLandsInsideAnAnnotation()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("<ruby=ふりがな>漢字</ruby>"), size: 40f);

            var ruby = RubyRuns(layout)[0];
            float y = ruby.Baseline;
            for (float x = 0f; x < layout.Width; x += 2f)
            {
                int index = TextHitTest.GetIndexAtPoint(layout, new Vector2(x, y));
                Assert.GreaterOrEqual(index, 0);
                Assert.LessOrEqual(index, 2, "hit testing answers in base indices");
            }
        }

        [Test]
        public void Selection_CoversTheBaseAndNotTheOverhang()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("あ<ruby=かんじだよ>漢</ruby>あ"), size: 40f);

            var rects = new List<Rect>();
            TextHitTest.GetSelectionRects(layout, 1, 2, rects);
            Assert.AreEqual(1, rects.Count);

            var (baseMin, baseMax) = BaseExtent(layout, 1, 2);
            Assert.AreEqual(baseMin, rects[0].xMin, 0.01f);
            Assert.AreEqual(baseMax, rects[0].xMax, 0.01f);
        }

        [Test]
        public void Ruby_KeepsTheStyleOfItsBase()
        {
            using var font = LoadFont(JapaneseFontPath);
            using var fonts = FontStack.Single(font);
            // Colour rides on the run and decorations are resolved from the
            // cluster, so a decorated span decorates its reading too; the
            // ruby run has the base's style and the base's indices.
            var layout = Layout(fonts, Parse("<color=red><ruby=かん>漢</ruby></color>"), size: 40f);

            var ruby = RubyRuns(layout)[0];
            Assert.IsTrue(ruby.Style.HasColor);
            Assert.AreEqual(255, ruby.Style.Color.r);
            Assert.IsFalse(ruby.Style.IsSprite);
        }

        [Test]
        public void PlainText_PaysNothingForRuby()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            var layout = Layout(fonts, Parse("Hello, world"), size: 32f);

            Assert.AreEqual(0, RubyRuns(layout).Count);
            Assert.AreEqual(1, layout.Lines.Count);
        }

        // ------------------------------------------------- the placement rules

        [Test]
        public void Distribute_PutsHalfAGapAtEachEnd()
        {
            // The spec: the gap between two ruby characters is twice the gap at
            // each end, so four characters over 12 units of slack take 1.5 at
            // each end and 3 between.
            RubyPlacement.Distribute(12f, 4, 100f, out float lead, out float gap);

            Assert.AreEqual(1.5f, lead, 0.001f);
            Assert.AreEqual(3f, gap, 0.001f);
            Assert.AreEqual(12f, 2f * lead + 3f * gap, 0.001f, "the slack is exactly consumed");
        }

        [Test]
        public void Distribute_CapsTheEndGapAndCentresTheRest()
        {
            // "No more than half the size of one base character": a two-kana
            // reading of a wide compound is set together and centred rather
            // than flung out to the corners.
            RubyPlacement.Distribute(100f, 2, 10f, out float lead, out float gap);

            Assert.AreEqual(10f, gap, 0.001f, "twice the capped end gap");
            Assert.AreEqual(45f, lead, 0.001f, "and the slack the cap refused is centred");
            Assert.AreEqual(100f, 2f * lead + gap, 0.001f, "the slack is still exactly consumed");

            // One character has nothing to space between, so the cap leaves
            // plain centring.
            RubyPlacement.Distribute(100f, 1, 10f, out lead, out _);
            Assert.AreEqual(50f, lead, 0.001f);
        }

        [Test]
        public void Overhang_TakesFromBothSidesThenPads()
        {
            RubyPlacement.Overhang(10f, 3f, 3f, out float before, out float after, out float pad);
            Assert.AreEqual(3f, before, 0.001f);
            Assert.AreEqual(3f, after, 0.001f);
            Assert.AreEqual(4f, pad, 0.001f);

            // One side that can give everything is not limited by one that can
            // give nothing.
            RubyPlacement.Overhang(10f, 20f, 0f, out before, out after, out pad);
            Assert.AreEqual(10f, before, 0.001f);
            Assert.AreEqual(0f, after, 0.001f);
            Assert.AreEqual(0f, pad, 0.001f);
        }

        [Test]
        public void Overhang_IsOnlyOverBlank()
        {
            // A kana or a kanji fills its box and lends nothing; a closing mark
            // has half a box of blank, a middle dot a quarter.
            Assert.AreEqual(0f, Unicode.AsianTypography.RubyOverhangBefore('あ'));
            Assert.AreEqual(0f, Unicode.AsianTypography.RubyOverhangBefore('漢'));
            Assert.AreEqual(0.5f, Unicode.AsianTypography.RubyOverhangBefore('。'));
            Assert.AreEqual(0.5f, Unicode.AsianTypography.RubyOverhangBefore('」'));
            Assert.AreEqual(0.25f, Unicode.AsianTypography.RubyOverhangBefore('・'));

            // And the sides are not the same: an opening bracket has its blank
            // on the left, so it lends to a base before it, not after.
            Assert.AreEqual(0.5f, Unicode.AsianTypography.RubyOverhangAfter('「'));
            Assert.AreEqual(0f, Unicode.AsianTypography.RubyOverhangAfter('」'));
            Assert.AreEqual(0f, Unicode.AsianTypography.RubyOverhangBefore('「'));
        }

        [Test]
        public void Blank_IsWhatCompressionLeft()
        {
            // Ruby may hang over a mark's blank; if 約物詰め already took that
            // blank, there is nothing left to hang over.
            Assert.AreEqual(0.5f, RubyPlacement.BlankOf(0.5f, 1f, 1f), 0.001f);
            Assert.AreEqual(0f, RubyPlacement.BlankOf(0.5f, 0.5f, 1f), 0.001f);
            Assert.AreEqual(0.25f, RubyPlacement.BlankOf(0.25f, 1f, 1f), 0.001f);
        }
    }
}
