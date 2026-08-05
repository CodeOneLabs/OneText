using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M8: markup. Two questions run through all of it — does a well-formed tag
    /// change exactly what it says it changes, and does a malformed one leave
    /// the text alone? The second matters more. Text that silently disappears
    /// because someone typed "5 &lt; 6" is the worst failure a text engine has,
    /// and it is the one every markup parser is tempted into.
    /// </summary>
    public class RichTextTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansVariable.ttf";

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
            // font metric moves — which is exactly how this test failed to
            // discriminate before.
            float box = -1f;
            for (float candidate = 120f; candidate <= 400f; candidate += 5f)
            {
                if (!SplitsThePhrase(fonts, plain, phrase, candidate)) continue;
                if (Layout(fonts, Parse(phrase), 24f, candidate).Lines.Count != 1) continue;
                box = candidate;
                break;
            }
            Assert.Greater(box, 0f, "no width both splits the phrase and fits it — check the font");

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
            using var font = LoadFont("Packages/com.onetext.core/Tests/Fonts/CffShapes.otf");
            if (font.IsVariable) Assert.Ignore("test font is variable");
            using var fonts = FontStack.Single(font);

            Assert.IsFalse(fonts.TryGetStyled(font, FontStack.Face.Bold, out var styled),
                "a static font with no bold face must report that it cannot do bold, " +
                "rather than quietly drawing regular and leaving the author guessing");
            Assert.AreSame(font, styled, "the fallback still has to be a usable face");
            Assert.AreSame(font, fonts.Resolve('A', bold: true, italic: false));
        }
    }
}
