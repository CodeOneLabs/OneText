using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M8: markup. Two questions run through all of it: does a well-formed tag
    /// change exactly what it says it changes, and does a malformed one leave
    /// the text alone? The second matters more. Text that silently disappears
    /// because someone typed "5 &lt; 6" is the worst failure a text engine has,
    /// and it is the one every markup parser is tempted into.
    /// </summary>
    public class RichTextTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static RichTextResult Parse(string source)
        {
            var result = new RichTextResult();
            RichTextParser.Parse(source, result);
            return result;
        }

        // ----------------------------------------------------------- the text

        [Test]
        public void PlainText_PassesThroughUntouched()
        {
            var result = Parse("Hello, world");
            Assert.AreEqual("Hello, world", result.Text);
            Assert.IsFalse(result.HasMarkup);
            Assert.AreEqual(1, result.Spans.Count);
            Assert.AreEqual(TextStyle.Default, result.Spans[0].Style);
        }

        [TestCase("5 < 6", "5 < 6")]
        [TestCase("a<b and c<d", "a<b and c<d")]
        [TestCase("<notatag>x", "<notatag>x")]
        [TestCase("<b", "<b")]
        [TestCase("<color=notacolour>x", "<color=notacolour>x")]
        [TestCase("<size=>x", "<size=>x")]
        [TestCase("<size=0>x", "<size=0>x")]
        [TestCase("</b>x", "</b>x")]
        [TestCase("<b>x", "x")]
        [TestCase("<color=#GGG>x", "<color=#GGG>x")]
        [TestCase("i <3 markup", "i <3 markup")]
        public void MalformedTags_StayLiteral(string source, string expected)
        {
            Assert.AreEqual(expected, Parse(source).Text,
                "a tag that does not parse must be left in the text, not swallowed");
        }

        [Test]
        public void UnterminatedTag_DoesNotSwallowTheRestOfTheLine()
        {
            // The failure this guards is a '<' near the end of a paragraph
            // eating everything after it.
            var result = Parse("before <b\nafter");
            StringAssert.Contains("after", result.Text);
            StringAssert.Contains("before", result.Text);
        }

        // ---------------------------------------------------------- the spans

        [Test]
        public void Spans_CoverTheWholeTextContiguously()
        {
            var result = Parse("plain <b>bold <i>both</i> bold</b> plain");
            Assert.AreEqual("plain bold both bold plain", result.Text);

            int cursor = 0;
            foreach (var span in result.Spans)
            {
                Assert.AreEqual(cursor, span.Start, "spans must be contiguous");
                Assert.Greater(span.Length, 0, "empty spans are noise");
                cursor = span.End;
            }
            Assert.AreEqual(result.Text.Length, cursor, "spans must cover the whole text");
        }

        [Test]
        public void Nesting_RestoresTheOuterStyle()
        {
            var result = Parse("<b>a<i>b</i>c</b>d");
            Assert.AreEqual("abcd", result.Text);
            Assert.IsTrue(result.StyleAt(0).Bold, "a: bold");
            Assert.IsFalse(result.StyleAt(0).Italic);
            Assert.IsTrue(result.StyleAt(1).Bold, "b: bold");
            Assert.IsTrue(result.StyleAt(1).Italic, "b: italic too");
            Assert.IsTrue(result.StyleAt(2).Bold, "c: back to bold only");
            Assert.IsFalse(result.StyleAt(2).Italic);
            Assert.IsFalse(result.StyleAt(3).Bold, "d: outside everything");
        }

        [Test]
        public void CloseAll_ClosesTheInnermostOpenTag()
        {
            var result = Parse("<b><i>x</>y</>z");
            Assert.AreEqual("xyz", result.Text);
            Assert.IsTrue(result.StyleAt(0).Italic);
            Assert.IsTrue(result.StyleAt(1).Bold);
            Assert.IsFalse(result.StyleAt(1).Italic, "</> closed the italic");
            Assert.IsFalse(result.StyleAt(2).Bold);
        }

        [Test]
        public void OutOfOrderClose_ClosesTheTagItNames()
        {
            // Sloppy markup, not broken markup: dropping the text would be the
            // worse answer, so the named tag is closed wherever it sits.
            var result = Parse("<b><i>x</b>y");
            Assert.AreEqual("xy", result.Text);
            Assert.IsFalse(result.StyleAt(1).Bold, "</b> closed the bold");
        }

        [Test]
        public void OutOfOrderClose_StillReportsALinkItClosesImplicitly()
        {
            // </b> closes the link too, because the link was opened inside it.
            // The link is off the stack before the end-of-input flush, so if
            // the close does not report its range, nothing ever will and the
            // clickable text is silently not clickable.
            var result = Parse("<b>see <link=docs>the manual</b> now");
            Assert.AreEqual("see the manual now", result.Text);
            Assert.AreEqual(1, result.Links.Count, "the link was dropped when </b> closed over it");
            Assert.AreEqual("docs", result.Links[0].Id);
            Assert.AreEqual("the manual",
                result.Text.Substring(result.Links[0].Start, result.Links[0].Length));
        }

        [Test]
        public void CloseAlign_RevertsToTheLabelsOwnAlignment()
        {
            // A one-way <align> is worse than none: the author wrote </align>
            // and the rest of the document keeps the alignment anyway.
            var result = Parse("<align=center>title</align>\nbody");
            Assert.AreEqual("title\nbody", result.Text);

            Assert.IsTrue(result.TryGetAlignment(0, out var title));
            Assert.AreEqual(TextAlignment.Center, title);
            Assert.IsFalse(result.TryGetAlignment(result.Text.Length - 1, out _),
                "</align> did not give the alignment back");
        }

        [Test]
        public void CloseAlign_RevertsToTheEnclosingOverride_NotToNothing()
        {
            var result = Parse("<align=right>a<align=center>b</align>c");
            Assert.AreEqual("abc", result.Text);
            Assert.IsTrue(result.TryGetAlignment(2, out var afterClose));
            Assert.AreEqual(TextAlignment.Right, afterClose,
                "</align> should restore the alignment it replaced, not clear everything");
        }

        [TestCase("<b=7>x", "<b=7>x")]
        [TestCase("<i=1>x", "<i=1>x")]
        [TestCase("<nobr=yes>x", "<nobr=yes>x")]
        public void ArgumentOnAnArgumentlessTag_IsMalformed(string source, string expected)
        {
            // The all-or-nothing rule applies to arguments too: <b=7> is far
            // more likely a typo than a request for bold.
            Assert.AreEqual(expected, Parse(source).Text);
        }

        [Test]
        public void UnclosedTag_StylesTheRestOfTheText()
        {
            var result = Parse("normal <b>bold to the end");
            Assert.IsTrue(result.StyleAt(result.Text.Length - 1).Bold);
        }

        // --------------------------------------------------------- attributes

        [Test]
        public void Color_ParsesHexAndNames()
        {
            var hex = Parse("<color=#FF8000>x").StyleAt(0);
            Assert.IsTrue(hex.HasColor);
            Assert.AreEqual(255, hex.Color.r);
            Assert.AreEqual(128, hex.Color.g);
            Assert.AreEqual(0, hex.Color.b);
            Assert.AreEqual(255, hex.Color.a, "a colour with no alpha is opaque");

            var named = Parse("<color=red>x").StyleAt(0);
            Assert.IsTrue(named.HasColor);
            Assert.AreEqual(255, named.Color.r);
            Assert.AreEqual(0, named.Color.g);

            var alpha = Parse("<color=#00FF0080>x").StyleAt(0);
            Assert.AreEqual(128, alpha.Color.a);
        }

        [Test]
        public void Size_HandlesAbsoluteAndPercentage()
        {
            Assert.AreEqual(48f, Parse("<size=48>x").StyleAt(0).ResolveSize(20f), 0.001f);
            Assert.AreEqual(30f, Parse("<size=150%>x").StyleAt(0).ResolveSize(20f), 0.001f);
            // Percentages compose, because they are multipliers on what they inherit.
            Assert.AreEqual(40f, Parse("<size=200%><size=100%>x").StyleAt(0).ResolveSize(20f), 0.001f);
            Assert.AreEqual(20f, Parse("plain").StyleAt(0).ResolveSize(20f), 0.001f);
        }

        [Test]
        public void QuotedArguments_AreAccepted()
        {
            var result = Parse("<link=\"a b\">x</link>");
            Assert.AreEqual("x", result.Text);
            Assert.AreEqual(1, result.Links.Count);
            Assert.AreEqual("a b", result.Links[0].Id);
        }

        [Test]
        public void Links_SurviveAlongsideOtherMarkup()
        {
            var result = Parse("see <b><link=docs>the <i>manual</i></link></b> now");
            Assert.AreEqual("see the manual now", result.Text);
            Assert.AreEqual(1, result.Links.Count);
            Assert.AreEqual("docs", result.Links[0].Id);
            Assert.AreEqual("the manual",
                result.Text.Substring(result.Links[0].Start, result.Links[0].Length));
        }

        [Test]
        public void Align_IsRecordedByPosition_NotOnSpans()
        {
            var result = Parse("left\n<align=center>middle\n<align=right>right");
            Assert.AreEqual("left\nmiddle\nright", result.Text);
            Assert.AreEqual(2, result.Alignments.Count);

            Assert.IsFalse(result.TryGetAlignment(0, out _), "nothing overrides the first line");
            Assert.IsTrue(result.TryGetAlignment(6, out var middle));
            Assert.AreEqual(TextAlignment.Center, middle);
            Assert.IsTrue(result.TryGetAlignment(result.Text.Length - 1, out var last));
            Assert.AreEqual(TextAlignment.Right, last);

            // Alignment must not split runs: it is a line property.
            foreach (var span in result.Spans)
                Assert.AreEqual(TextStyle.Default, span.Style,
                    "<align> changed a span's style, which would split a run for nothing");
        }

        [Test]
        public void Sprite_BecomesOneCharacterInASpanOfItsOwn()
        {
            var result = Parse("a<sprite=3>b");
            Assert.AreEqual(3, result.Text.Length, "a sprite occupies exactly one index");
            Assert.AreEqual(RichTextParser.SpritePlaceholder, result.Text[1]);

            var style = result.StyleAt(1);
            Assert.IsTrue(style.IsSprite);
            Assert.AreEqual(3, style.Sprite);
            Assert.IsFalse(result.StyleAt(0).IsSprite, "the sprite style must not leak backwards");
            Assert.IsFalse(result.StyleAt(2).IsSprite, "the sprite style must not leak forwards");
        }

        [Test]
        public void NamedStyleAndFont_StayLiteralWithoutAResolver()
        {
            // A style the label cannot resolve is not a style; leaving the tag
            // visible is how the author finds out.
            Assert.AreEqual("<style=title>x", Parse("<style=title>x").Text);
            Assert.AreEqual("<font=mono>x", Parse("<font=mono>x").Text);

            var result = new RichTextResult();
            RichTextParser.Parse("<style=title>x", result, name => name == "title" ? 7 : -1, null);
            Assert.AreEqual("x", result.Text);
            Assert.AreEqual(7, result.StyleAt(0).NamedStyle);
        }

        // ------------------------------------------------------------ layout

        private static TextLayoutResult Layout(FontStack fonts, RichTextResult markup,
            float size = 32f, float maxWidth = 0f)
        {
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, size);
            settings.Spans = markup.HasMarkup ? markup.Spans : null;
            settings.Alignments = markup.Alignments.Count > 0 ? markup.Alignments : null;
            settings.MaxWidth = maxWidth;
            settings.Wrap = maxWidth > 0f ? TextWrap.Wrap : TextWrap.NoWrap;
            var result = new TextLayoutResult();
            engine.Layout(markup.Text, settings, result);
            return result;
        }

        [Test]
        public void SizeTag_MakesTheRunWiderAndTheLineTaller()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("AAAA"));
            var big = Layout(fonts, Parse("AA<size=200%>AA</size>"));

            Assert.Greater(big.Width, plain.Width, "<size=200%> did not widen the text");
            Assert.Greater(big.Height, plain.Height, "a bigger run must make its line taller");

            // And the runs really did split at the tag.
            Assert.GreaterOrEqual(big.Runs.Count, 2, "<size> must end the run it interrupts");
            float small = 0f, large = 0f;
            foreach (var run in big.Runs)
            {
                if (run.FontSize > 40f) large = run.FontSize;
                else small = run.FontSize;
            }
            Assert.AreEqual(32f, small, 0.001f);
            Assert.AreEqual(64f, large, 0.001f);
        }

        [Test]
        public void ColorTag_SplitsRunsAndIsCarriedOnThem()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var result = Layout(fonts, Parse("plain <color=red>red</color> plain"));
            bool sawColored = false, sawPlain = false;
            foreach (var run in result.Runs)
            {
                if (run.Style.HasColor)
                {
                    sawColored = true;
                    Assert.AreEqual(255, run.Style.Color.r);
                }
                else sawPlain = true;
            }
            Assert.IsTrue(sawColored, "no run carried the colour");
            Assert.IsTrue(sawPlain, "the colour leaked outside its tag");
        }

        [Test]
        public void MarkupDoesNotChangeLayoutWhenItChangesNothing()
        {
            // A tag that has no visual effect must not move a single glyph.
            // This is what makes <link> free, and it is the regression that
            // would tell us run splitting had become over-eager.
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("see the manual now"));
            var linked = Layout(fonts, Parse("see <link=docs>the manual</link> now"));

            Assert.AreEqual(plain.Glyphs.Count, linked.Glyphs.Count);
            Assert.AreEqual(plain.Width, linked.Width, 0.001f);
            Assert.AreEqual(plain.Height, linked.Height, 0.001f);
        }

        [Test]
        public void NoBr_KeepsAPhraseOnOneLine()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            const string plain = "word keep this together word";
            const string tagged = "word <nobr>keep this together</nobr> word";
            const string phrase = "keep this together";

            // The box width is searched for rather than guessed: it has to be
            // narrow enough that greedy wrapping breaks *inside* the phrase and
            // wide enough that the phrase fits on a line of its own. Pick it by
            // hand and the test quietly stops proving anything the first time a
            // font metric moves, which is exactly how this test failed to
            // discriminate before.
            float box = -1f;
            for (float candidate = 120f; candidate <= 400f; candidate += 5f)
            {
                if (!SplitsThePhrase(fonts, plain, phrase, candidate)) continue;
                if (Layout(fonts, Parse(phrase), 24f, candidate).Lines.Count != 1) continue;
                box = candidate;
                break;
            }
            Assert.Greater(box, 0f, "no width both splits the phrase and fits it; check the font");

            Assert.IsTrue(SplitsThePhrase(fonts, plain, phrase, box),
                "without <nobr> the phrase must actually break, or this test proves nothing");
            Assert.IsFalse(SplitsThePhrase(fonts, Parse(tagged).Text, phrase, box, tagged),
                $"<nobr> let the phrase break across lines at {box}px");
        }

        /// <summary>True if any line contains part of the phrase but not all of it.</summary>
        private static bool SplitsThePhrase(FontStack fonts, string text, string phrase,
            float box, string source = null)
        {
            var result = Layout(fonts, Parse(source ?? text), 24f, box);
            string[] words = phrase.Split(' ');
            foreach (var line in result.Lines)
            {
                string lineText = text.Substring(line.TextStart, line.TextLength);
                bool any = false, all = true;
                foreach (var word in words)
                {
                    if (lineText.Contains(word)) any = true;
                    else all = false;
                }
                if (any && !all) return true;
            }
            return false;
        }

        [Test]
        public void AlignTag_MovesOnlyItsOwnParagraph()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var markup = Parse("left\n<align=right>right");
            var result = Layout(fonts, markup, 32f, 400f);
            Assert.AreEqual(2, result.Lines.Count);

            float firstX = result.Runs[result.Lines[0].RunStart].X;
            float secondX = result.Runs[result.Lines[1].RunStart].X;
            Assert.AreEqual(0f, firstX, 0.001f, "the first line was not overridden");
            Assert.Greater(secondX, 1f, "<align=right> did not move its line");
        }

        [Test]
        public void LetterSpacing_MovesTheGlyphs_NotJustTheWidth()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("AAAA"));
            var spaced = Layout(fonts, Parse("<cspace=0.25>AAAA</cspace>"));

            Assert.Greater(spaced.Width, plain.Width, "<cspace> did not widen the run");

            // The width has to come from the glyphs, not be asserted over the
            // top of them: a run that claims a width its glyphs do not occupy
            // draws with a trailing gap when left-aligned and lands in the
            // wrong place when right-aligned.
            float fromGlyphs = 0f;
            foreach (var run in spaced.Runs)
            {
                float scale = run.FontSize / run.Font.UnitsPerEm;
                for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
                    fromGlyphs += spaced.Glyphs[i].XAdvance * scale;
            }
            Assert.AreEqual(spaced.Width, fromGlyphs, 0.01f,
                "the run's width and its glyphs' advances disagree");
        }

        [Test]
        public void LetterSpacing_MeasuresTheSameWayItWraps()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            // If the wrapper measured tracking differently from the shaper, a
            // line would be accepted as fitting and then come out wider than
            // the box it was accepted into.
            const float box = 260f;
            var result = Layout(fonts, Parse("<cspace=0.3>the quick brown fox jumps</cspace>"), 24f, box);
            foreach (var line in result.Lines)
                Assert.LessOrEqual(line.Width, box + 0.5f,
                    "a line was wrapped as fitting and then measured wider than the box");
        }

        /// <summary>
        /// Lays out with a whole-label letter spacing, the way a label whose
        /// base style sets one asks for it: through the settings, not through
        /// a span, because plain text has no spans to put it in.
        /// </summary>
        private static TextLayoutResult LayoutSpaced(FontStack fonts, RichTextResult markup, float ems)
        {
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.Spans = markup.HasMarkup ? markup.Spans : null;
            settings.LetterSpacingEm = ems;
            settings.HasLetterSpacing = true;
            var result = new TextLayoutResult();
            engine.Layout(markup.Text, settings, result);
            return result;
        }

        [Test]
        public void LetterSpacing_FromTheLabel_ReachesTextWithNoMarkupInIt()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            // The path this used to miss entirely. Plain text carries no
            // spans, so a spacing folded into a parsed style reached nothing:
            // the label with a style asset set to widen it drew at the face's
            // own spacing and said nothing about why.
            var plain = Layout(fonts, Parse("Hamburgefonstiv"));
            var spaced = LayoutSpaced(fonts, Parse("Hamburgefonstiv"), 0.1f);

            Assert.Greater(spaced.Width, plain.Width + 1f,
                "a whole-label letter spacing never reached text with no markup in it");

            // And again through itemization rather than the plain-text fast
            // path, which is a different place the item is built.
            var styledPlain = Layout(fonts, Parse("Hamburge<b>fonstiv</b>"));
            var styledSpaced = LayoutSpaced(fonts, Parse("Hamburge<b>fonstiv</b>"), 0.1f);
            Assert.Greater(styledSpaced.Width, styledPlain.Width + 1f,
                "a whole-label letter spacing did not reach a run that markup had split");
        }

        [Test]
        public void LetterSpacing_FromMarkup_WinsEvenWhenItAsksForZero()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            float plain = Layout(fonts, Parse("AAAA")).Width;
            float spaced = LayoutSpaced(fonts, Parse("AAAA"), 0.2f).Width;
            float pulledBack = LayoutSpaced(fonts, Parse("<cspace=0>AAAA</cspace>"), 0.2f).Width;

            Assert.Greater(spaced, plain + 1f, "the label's spacing did not apply");
            Assert.AreEqual(plain, pulledBack, 0.5f,
                "<cspace=0> must be an instruction to draw at the face's own spacing, not an " +
                "absence of one: read as 'said nothing', the label's spacing is handed straight " +
                "back and there is no way to ask for zero");
        }

        [Test]
        public void LetterSpacing_FromTheFace_DoesNotSpreadToTheFallback()
        {
            using var latin = LoadFont(LatinFontPath);
            using var arabic = LoadFont(ArabicFontPath);

            // Three behs: the Latin face at the head of the stack has no glyph
            // for them, so every one of them is drawn by the fallback.
            const string text = "ببب";

            using var plain = new FontStack();
            plain.Add(latin, null);
            plain.Add(arabic, null);

            using var latinWidened = new FontStack();
            latinWidened.Add(latin, null, 0.2f);
            latinWidened.Add(arabic, null);

            using var arabicWidened = new FontStack();
            arabicWidened.Add(latin, null);
            arabicWidened.Add(arabic, null, 0.2f);

            float baseline = Layout(plain, Parse(text)).Width;
            Assert.Greater(baseline, 0f);

            // This is the whole reason the correction lives on the face rather
            // than on the label: a label-wide value is applied to every face on
            // the line, so a Latin font's correction lands on the CJK or Arabic
            // fallback that drew the middle of the sentence.
            Assert.AreEqual(baseline, Layout(latinWidened, Parse(text)).Width, 0.5f,
                "a correction meant for one face widened text a different face drew");
            Assert.Greater(Layout(arabicWidened, Parse(text)).Width, baseline + 1f,
                "the face that actually drew the text did not get its own correction");
        }

        [Test]
        public void LetterSpacing_FromTheFace_CoversTheFamilysStyledFaces()
        {
            using var variable = LoadFont(VariableFontPath);
            using var fonts = new FontStack();
            fonts.Add(variable, null, 0.05f);

            Assert.IsTrue(fonts.TryGetStyled(variable, FontStack.Face.Bold, out var bold));
            Assert.AreEqual(0.05f, fonts.LetterSpacingOf(bold), 1e-6f,
                "an instanced bold is the same design with the same metrics, so <b> must not " +
                "quietly drop the correction the family was given");
        }

        [Test]
        public void BaselineShift_RaisesTheRunAndTheLine()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("xx"));
            var raised = Layout(fonts, Parse("x<voffset=0.5>x</voffset>"));

            Assert.Greater(raised.Height, plain.Height, "a raised run must make its line taller");
            bool sawShift = false;
            foreach (var run in raised.Runs)
                if (run.BaselineShift > 0f) sawShift = true;
            Assert.IsTrue(sawShift, "<voffset> did not reach the run");
        }

        [Test]
        public void BoldOnAVariableFont_InstancesTheWeightAxis()
        {
            using var font = LoadFont(VariableFontPath);
            if (!font.IsVariable) Assert.Ignore("test font is not variable");
            using var fonts = FontStack.Single(font);

            var regular = fonts.Resolve('A', bold: false, italic: false);
            var bold = fonts.Resolve('A', bold: true, italic: false);
            Assert.AreNotSame(regular, bold, "bold did not produce a different face");

            // And it is heavier, not merely different: a bold 'A' advances wider.
            using var shaper = new Shaper();
            var a = new List<ShapedGlyph>();
            var b = new List<ShapedGlyph>();
            shaper.Shape(regular, "Hamburgefonstiv", a);
            shaper.Shape(bold, "Hamburgefonstiv", b);
            int widthA = 0, widthB = 0;
            foreach (var g in a) widthA += g.XAdvance;
            foreach (var g in b) widthB += g.XAdvance;
            Assert.Greater(widthB, widthA, "the instanced bold is not actually bolder");
        }

        [Test]
        public void BoldOnAStaticFont_FallsBackToRegular_AndSaysSo()
        {
            // CffShapes.otf is authored for the test suite and has no axes,
            // which is exactly the case this covers.
            using var font = LoadFont("Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf");
            if (font.IsVariable) Assert.Ignore("test font is variable");
            using var fonts = FontStack.Single(font);

            Assert.IsFalse(fonts.TryGetStyled(font, FontStack.Face.Bold, out var styled),
                "a static font with no bold face must report that it cannot do bold, " +
                "rather than quietly drawing regular and leaving the author guessing");
            Assert.AreSame(font, styled, "the fallback still has to be a usable face");
            // 'O' rather than 'A': this font's cmap holds only "OQSI" and a
            // space, and the question here is what bold does to a character the
            // font has; a character it does not have is the system-fallback
            // tier's question, and it now answers it.
            Assert.AreSame(font, fonts.Resolve('O', bold: true, italic: false));
        }

        // ------------------------------------------------- where bold comes from

        // Three tiers, in the order the engine tries them: a designed bold from
        // a second file, an instance off a variable font's wght axis, and — only
        // when neither exists — a faked weight. The last one is a drawing trick
        // and the tests are here to hold the line at which it starts: a project
        // that has a real bold must never get the trick instead.

        [Test]
        public void ADesignedBold_IsUsedForBoldRuns_AndIsNotSynthetic()
        {
            using var regular = LoadFont(LatinFontPath);
            using var bold = LoadFont("Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf");
            var fonts = new FontStack();
            fonts.Add(regular, bold, null, null);
            using (fonts)
            {
                Assert.AreSame(bold, fonts.Resolve('O', bold: true, italic: false),
                    "a family with a designed bold drew its bold run with the regular");
                Assert.IsTrue(fonts.HasBold(regular),
                    "the family has a bold and says it has none, so every bold run in it " +
                    "would be faked over the top of a font that was right there");

                // Asked through a styled face rather than the regular, which is
                // what a bold-italic run that found only the italic hands over.
                Assert.IsTrue(fonts.HasBold(bold),
                    "asking a family's own bold face whether the family has a bold said no");
            }
        }

        [Test]
        public void AStaticFontWithNoBold_SaysSo()
        {
            using var font = LoadFont("Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf");
            if (font.IsVariable) Assert.Ignore("test font is variable");
            using var fonts = FontStack.Single(font);

            Assert.IsFalse(fonts.HasBold(font),
                "a static font with no bold face reported one, so nothing will fake the weight " +
                "and <b> stays silently invisible — which is the bug this is here for");
        }

        [Test]
        public void AVariableFont_HasABoldWithoutASecondFile()
        {
            using var font = LoadFont(VariableFontPath);
            if (!font.IsVariable) Assert.Ignore("test font is not variable");
            using var fonts = FontStack.Single(font);

            Assert.IsTrue(fonts.HasBold(font),
                "a variable font's wght axis is a real bold and must be preferred to a faked one");
        }

        [Test]
        public void BoldOnAFontWithNoBold_MarksTheRunForFaking()
        {
            using var font = LoadFont("Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf");
            if (font.IsVariable) Assert.Ignore("test font is variable");
            using var fonts = FontStack.Single(font);

            // 'O' rather than 'A': this font's cmap holds only "OQSI".
            var result = Layout(fonts, Parse("<b>O</b>"));
            Assert.Greater(result.Runs.Count, 0, "nothing was laid out");
            foreach (var run in result.Runs)
                Assert.IsTrue(run.SyntheticBold,
                    "a bold run on a font with no bold was not marked for faking, so it draws " +
                    "at the regular weight and the author cannot see why");
        }

        [Test]
        public void BoldOnAVariableFont_IsNotFaked()
        {
            using var font = LoadFont(VariableFontPath);
            if (!font.IsVariable) Assert.Ignore("test font is not variable");
            using var fonts = FontStack.Single(font);

            var result = Layout(fonts, Parse("<b>A</b>"));
            foreach (var run in result.Runs)
                Assert.IsFalse(run.SyntheticBold,
                    "a real interpolated bold was thickened on top of being bold");
        }

        [Test]
        public void TextThatNeverAskedForBold_IsNeverFaked()
        {
            using var font = LoadFont("Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf");
            using var fonts = FontStack.Single(font);

            var result = Layout(fonts, Parse("O"));
            foreach (var run in result.Runs)
                Assert.IsFalse(run.SyntheticBold, "plain text was thickened");
        }

        [Test]
        public void SyntheticBold_ThickensTheFace_AndKeepsWhatWasAlreadyThere()
        {
            var plain = TextDecoration.None.WithSyntheticBold();
            Assert.IsTrue(plain.HasFace);
            Assert.AreEqual(TextDecoration.SyntheticBoldDilate, plain.FaceDilate, 0.0001f);
            Assert.IsFalse(plain.IsNone, "a faked bold that resolves to 'no decoration' draws thin");

            // A label already styled with a thicker face and a span inside it
            // asking for bold have both said something, and neither is wrong.
            var thickened = new TextDecoration { Set = TextDecoration.Parts.Face, FaceDilate = 0.1f };
            Assert.AreEqual(0.1f + TextDecoration.SyntheticBoldDilate,
                thickened.WithSyntheticBold().FaceDilate, 0.0001f);
        }

        // ------------------------------------------- the tags TMP text brings

        // Five tags that were printed rather than obeyed until now, chosen
        // because they are the ones a real TextMesh Pro project actually has in
        // it. Each is asserted twice: that a well-formed use changes exactly
        // what it says, and that a malformed one leaves the text alone — the
        // second being the rule this whole parser is built around.

        [Test]
        public void Superscript_IsHalfSizeAndLifted_AndClosesBackToWhereItWas()
        {
            var result = Parse("x<sup>2</sup>y");
            Assert.AreEqual("x2y", result.Text, "the tags are still in the text");

            var plain = result.StyleAt(0);
            var lifted = result.StyleAt(1);
            var after = result.StyleAt(2);

            Assert.AreEqual(0.5f, lifted.SizeScale, 0.0001f, "superscript is not half size");
            Assert.Greater(lifted.BaselineShiftEm, 0f, "superscript did not go up");
            Assert.AreEqual(plain, after, "</sup> did not put the style back");
        }

        [Test]
        public void Superscript_IsLiftedByTheSizeItHadBeforeItShrank()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            // The bug this catches, and it is invisible in the parser: a
            // baseline shift is resolved against the size of the run holding it,
            // and a superscript run is half size, so an offset written in
            // unchanged buys half the lift. It reads as a superscript sitting
            // too low, which is exactly what it is.
            const float size = 32f;
            var result = Layout(fonts, Parse("x<sup>2</sup>"), size);

            float lifted = 0f;
            foreach (var run in result.Runs)
                if (run.BaselineShift > lifted) lifted = run.BaselineShift;

            Assert.AreEqual(0.35f * size, lifted, 0.01f,
                "the superscript is not raised by a third of the size it was written at");
        }

        [Test]
        public void Superscript_InsideAVoffset_KeepsTheRaiseItInherited()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            const float size = 32f;
            var plain = Layout(fonts, Parse("<voffset=0.5>x</voffset>"), size);
            var nested = Layout(fonts, Parse("<voffset=0.5>x<sup>2</sup></voffset>"), size);

            float outer = plain.Runs[0].BaselineShift;
            float inner = 0f;
            foreach (var run in nested.Runs)
                if (run.BaselineShift > inner) inner = run.BaselineShift;

            Assert.AreEqual(outer + 0.35f * size, inner, 0.01f,
                "the superscript halved the raise it was nested in instead of adding to it");
        }

        [Test]
        public void Subscript_IsHalfSizeAndDropped()
        {
            var style = Parse("H<sub>2</sub>O").StyleAt(1);
            Assert.AreEqual(0.5f, style.SizeScale, 0.0001f);
            Assert.Less(style.BaselineShiftEm, 0f, "subscript did not go down");
        }

        [Test]
        public void SuperscriptInsideASize_MultipliesRatherThanReplaces()
        {
            // The composition that makes this worth having as two existing
            // fields rather than a flag: a superscript inside a <size=200%> is
            // half of that, not half of the label.
            var style = Parse("<size=200%>x<sup>2</sup></size>").StyleAt(1);
            Assert.AreEqual(1f, style.SizeScale, 0.0001f,
                "the superscript threw away the size it was nested in");
        }

        [Test]
        public void SuperscriptWithAnArgument_IsNotATag()
        {
            Assert.AreEqual("<sup=3>x", Parse("<sup=3>x").Text,
                "a tag that takes no argument was given one and obeyed it anyway");
        }

        [Test]
        public void Alpha_SetsTheAlphaAndLeavesTheHueAlone()
        {
            var style = Parse("<alpha=#80>faded").StyleAt(0);
            Assert.IsTrue(style.HasAlpha);
            Assert.AreEqual(128, style.AlphaOverride);
            Assert.IsFalse(style.HasColor, "<alpha> invented a colour nobody wrote");

            // The whole point of the separate field: resolving has to give white
            // at that alpha, not black. Black is what an unset Color happens to
            // be, and folding alpha into it is how a fade turns into a blackout.
            var resolved = style.ResolveColor();
            Assert.AreEqual(255, resolved.r);
            Assert.AreEqual(255, resolved.g);
            Assert.AreEqual(255, resolved.b);
            Assert.AreEqual(128, resolved.a);
        }

        [Test]
        public void Alpha_OverAColour_KeepsTheColour()
        {
            var resolved = Parse("<color=#FF0000><alpha=#40>x").StyleAt(0).ResolveColor();
            Assert.AreEqual(255, resolved.r);
            Assert.AreEqual(0, resolved.g);
            Assert.AreEqual(64, resolved.a);
        }

        [Test]
        public void Alpha_WithoutHexDigits_IsNotATag()
        {
            // TMP writes <alpha=#80> and nothing else, and a parser that also
            // took 0.5 would read a migrated tag one way and a hand-written one
            // another.
            Assert.AreEqual("<alpha=0.5>x", Parse("<alpha=0.5>x").Text);
            Assert.AreEqual("<alpha=80>x", Parse("<alpha=80>x").Text);
            Assert.AreEqual("<alpha=#8>x", Parse("<alpha=#8>x").Text);
        }

        [Test]
        public void Mspace_SetsTheCellWidth_AndZeroIsNotACell()
        {
            var style = Parse("<mspace=0.6em>123").StyleAt(0);
            Assert.IsTrue(style.HasMonoAdvance);
            Assert.AreEqual(0.6f, style.MonoAdvanceEm, 0.0001f);

            Assert.AreEqual("<mspace=0>123", Parse("<mspace=0>123").Text,
                "a cell of zero would draw every glyph on top of the last");
            Assert.AreEqual("<mspace=-1em>x", Parse("<mspace=-1em>x").Text);
        }

        [Test]
        public void Noparse_ShowsItsContentsExactly()
        {
            var result = Parse("a<noparse><b>not bold</b></noparse>z");
            Assert.AreEqual("a<b>not bold</b>z", result.Text);
            Assert.IsFalse(result.StyleAt(1).Bold, "the tag inside noparse was obeyed");
            Assert.IsTrue(result.HasMarkup);
        }

        [Test]
        public void Noparse_Unterminated_SwallowsTheRest()
        {
            // The same house rule as every other unclosed tag: it runs to the
            // end. Which is also the safe direction — the alternative is markup
            // in a player's name taking effect after all.
            Assert.AreEqual("<size=500>huge", Parse("<noparse><size=500>huge").Text);
        }

        [Test]
        public void Br_IsALineBreak()
        {
            var result = Parse("one<br>two");
            Assert.AreEqual("one\ntwo", result.Text);
            Assert.IsTrue(result.HasMarkup);
            Assert.AreEqual(1, result.Spans.Count, "a line break split the run it was inside");
        }

        [Test]
        public void Br_WithAnArgument_IsNotATag()
        {
            Assert.AreEqual("<br=2>x", Parse("<br=2>x").Text);
        }

        [Test]
        public void Mspace_GivesEveryGlyphTheSameCell()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            // 'i' and 'W' are the widest disagreement a Latin face has to offer,
            // and the reason anybody reaches for this tag: a score that goes
            // from 11 to 18 must not move everything beside it.
            var result = Layout(fonts, Parse("<mspace=0.6em>iWiW</mspace>"), 32f);

            float first = -1f;
            foreach (var run in result.Runs)
            {
                float scale = run.FontSize / run.Font.UnitsPerEm;
                for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
                {
                    float advance = result.Glyphs[i].XAdvance * scale;
                    if (first < 0f) first = advance;
                    Assert.AreEqual(first, advance, 0.02f,
                        "a monospaced run drew cells of different widths");
                }
            }

            Assert.Greater(first, 0f, "the run had no glyphs to measure");
            Assert.AreEqual(0.6f * 32f, first, 0.05f,
                "the cell is not the width the tag asked for");
        }

        [Test]
        public void Mspace_CentresTheGlyphInItsCell()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            var plain = Layout(fonts, Parse("i"), 32f);
            var mono = Layout(fonts, Parse("<mspace=0.6em>i</mspace>"), 32f);

            // A narrow glyph left where it was sits hard against the left of a
            // wide cell, which is exactly the ragged look the tag is reached for
            // to fix.
            Assert.Greater(mono.Glyphs[0].XOffset, plain.Glyphs[0].XOffset,
                "the glyph was given a wider cell and left at the left edge of it");
        }

        [Test]
        public void Mspace_MeasuresTheSameWayItWraps()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            // The failure tracking taught this engine once already, arriving by
            // a second door: the measuring pass and the shaping pass have to
            // agree about the cell, or a line is accepted as fitting and then
            // drawn wider than the box that accepted it.
            const float box = 260f;
            var result = Layout(fonts,
                Parse("<mspace=0.55em>the quick brown fox jumps over it</mspace>"), 24f, box);
            foreach (var line in result.Lines)
                Assert.LessOrEqual(line.Width, box + 0.5f,
                    "a monospaced line was wrapped as fitting and then measured wider");
        }
    }
}
