using System.Collections.Generic;
using System.IO;
using OneText.Unicode;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M10: the rules East Asian typography specs define and UAX #14 leaves to
    /// tailoring.
    ///
    /// These are the loudest complaints from the Korean, Japanese and Chinese
    /// communities, and they are all spec-driven (JLREQ, KLREQ, CLREQ, the
    /// UAX #14 tailoring section), which means the tests can be about what the
    /// specs say rather than about what looks right to whoever wrote the code.
    /// The degenerate cases come from the bug trackers.
    /// </summary>
    public class AsianTypographyTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ColorFontPath = "Packages/com.onetext.core/Tests/Fonts~/ColorGlyphs.ttf";
        private const string LoclFontPath = "Packages/com.onetext.core/Tests/Fonts~/LoclRegional.ttf";

        private static LineBreaker.Opportunity[] Analyze(string text,
            AsianTypography.Kinsoku kinsoku = AsianTypography.Kinsoku.Off, bool korean = false)
        {
            var opportunities = new LineBreaker.Opportunity[text.Length + 1];
            LineBreaker.Analyze(text, opportunities);
            if (korean) AsianTypography.ApplyKoreanWordWrap(text, opportunities);
            AsianTypography.ApplyKinsoku(text, opportunities, kinsoku);
            return opportunities;
        }

        // ------------------------------------------------------------- kinsoku

        [Test]
        public void ClosingPunctuation_NeverStartsALine()
        {
            // 行頭禁則. A line beginning with 。 or ）is the single most
            // recognisable typesetting mistake in Japanese text.
            const string text = "これは（テスト）です。";
            var plain = Analyze(text);
            var tailored = Analyze(text, AsianTypography.Kinsoku.Normal);

            for (int i = 1; i < text.Length; i++)
            {
                if (!AsianTypography.ForbiddenAtLineStart(text[i], AsianTypography.Kinsoku.Normal))
                    continue;
                Assert.AreNotEqual(LineBreaker.Opportunity.Allowed, tailored[i],
                    $"a line may not start with '{text[i]}'");
            }

            // And the tailoring only ever removes opportunities; inventing one
            // would break text somewhere nobody was looking.
            for (int i = 0; i < plain.Length; i++)
                if (plain[i] != LineBreaker.Opportunity.Allowed)
                    Assert.AreNotEqual(LineBreaker.Opportunity.Allowed, tailored[i],
                        "kinsoku added a break opportunity that UAX #14 did not allow");
        }

        [Test]
        public void OpeningPunctuation_NeverEndsALine()
        {
            const string text = "見出し「引用」の例";
            var tailored = Analyze(text, AsianTypography.Kinsoku.Normal);

            for (int i = 1; i < text.Length; i++)
            {
                if (!AsianTypography.ForbiddenAtLineEnd(text[i - 1], AsianTypography.Kinsoku.Normal))
                    continue;
                Assert.AreNotEqual(LineBreaker.Opportunity.Allowed, tailored[i],
                    $"a line may not end with '{text[i - 1]}'");
            }
        }

        [Test]
        public void Severity_Matters()
        {
            // Small kana are a normal-and-stricter rule; publishers genuinely
            // disagree, which is why severity is a setting rather than a
            // constant.
            Assert.IsFalse(AsianTypography.ForbiddenAtLineStart('っ', AsianTypography.Kinsoku.Loose));
            Assert.IsTrue(AsianTypography.ForbiddenAtLineStart('っ', AsianTypography.Kinsoku.Normal));

            Assert.IsFalse(AsianTypography.ForbiddenAtLineStart('…', AsianTypography.Kinsoku.Normal));
            Assert.IsTrue(AsianTypography.ForbiddenAtLineStart('…', AsianTypography.Kinsoku.Strict));

            // Off is off, for everything.
            foreach (char c in "。）っ…")
                Assert.IsFalse(AsianTypography.ForbiddenAtLineStart(c, AsianTypography.Kinsoku.Off));
        }

        [Test]
        public void RepeatedExclamation_DoesNotBreakWrappingEntirely()
        {
            // Straight from the issue tracker: a run of Japanese exclamation
            // marks in a chat message. Every one is forbidden at a line start,
            // so a naive rule can walk a break backwards through all of them
            // and lose the line. Wrapping has to survive it.
            const string text = "すごい！！！！！！！！ほんとうに";
            var tailored = Analyze(text, AsianTypography.Kinsoku.Strict);

            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var settings = TextLayoutSettings.Default(fonts, 24f);
            settings.MaxWidth = 120f;
            settings.Wrap = TextWrap.Wrap;
            settings.Kinsoku = AsianTypography.Kinsoku.Strict;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);

            Assert.Greater(result.Lines.Count, 0, "wrapping produced no lines at all");

            int covered = 0;
            foreach (var line in result.Lines) covered += line.TextLength;
            Assert.AreEqual(text.Length, covered,
                "wrapping lost characters: the kinsoku walk consumed text it should have kept");

            foreach (var line in result.Lines)
                Assert.Greater(line.TextLength, 0,
                    "a zero-length line: the walk backwards made no progress");
        }

        [Test]
        public void EmergencyBreak_DoesNotPushOutWhenPushingOutCannotHelp()
        {
            // The bound on 追い出し, and the reason the ！！！！ bug was a bug: a
            // walk backwards that resolves nothing is not a smaller fault than
            // the one it was avoiding, it is the same fault one line later plus
            // a short line. Here the run of forbidden marks is wider than the
            // box, so wherever this line ends the next one starts on a 。, and
            // the right answer is to stop paying for it.
            const string text = "aaa。。。。。。a";

            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var probe = TextLayoutSettings.Default(fonts, 24f);
            probe.Wrap = TextWrap.NoWrap;
            var measured = new TextLayoutResult();
            engine.Layout("aaa", probe, measured);

            var settings = TextLayoutSettings.Default(fonts, 24f);
            settings.Wrap = TextWrap.Wrap;
            settings.MaxWidth = measured.Width + 0.1f;
            settings.Kinsoku = AsianTypography.Kinsoku.Normal;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);

            Assert.AreEqual(3, result.Lines[0].TextLength,
                "the line was shortened for a push-out that resolves nothing");

            int covered = 0;
            foreach (var line in result.Lines) covered += line.TextLength;
            Assert.AreEqual(text.Length, covered, "wrapping lost characters");
        }

        [Test]
        public void EmergencyBreak_PrefersALegalBoundary()
        {
            // The emergency break used to ignore kinsoku outright, so a 。 that
            // happened to fall one character past the box start became a line
            // start: the exact fault the tailoring exists to prevent, arriving
            // through the one path that never asked. Walking back one character
            // is 追い出し, and it costs one character.
            //
            // The text has no break opportunity anywhere UAX #14 would take
            // (no spaces, and nothing may break before 。), so every line here
            // comes from the emergency path.
            const string text = "aaa。a";

            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            // A box that holds exactly three of the letters, so the last
            // boundary that fits is the one that strands the 。.
            var probe = TextLayoutSettings.Default(fonts, 24f);
            probe.Wrap = TextWrap.NoWrap;
            var measured = new TextLayoutResult();
            engine.Layout("aaa", probe, measured);
            float box = measured.Width + 0.1f;

            int FirstLine(AsianTypography.Kinsoku kinsoku)
            {
                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.Wrap = TextWrap.Wrap;
                settings.MaxWidth = box;
                settings.Kinsoku = kinsoku;
                var result = new TextLayoutResult();
                engine.Layout(text, settings, result);

                int covered = 0;
                foreach (var line in result.Lines) covered += line.TextLength;
                Assert.AreEqual(text.Length, covered, $"{kinsoku}: wrapping lost characters");
                return result.Lines[0].TextLength;
            }

            Assert.AreEqual(3, FirstLine(AsianTypography.Kinsoku.Off),
                "with the tailoring off the greedy break should take everything that fits");
            Assert.AreEqual(2, FirstLine(AsianTypography.Kinsoku.Normal),
                "the emergency break stranded 。 at a line start");
        }

        // ------------------------------------------------------- Korean wrap

        [Test]
        public void KoreanWordWrap_BreaksAtSpaces_NotBetweenSyllables()
        {
            // UAX #14 gives Hangul syllables class ID, which breaks anywhere.
            // That is right for Japanese and wrong for Korean, and Korean
            // readers notice immediately.
            const string text = "안녕하세요 반갑습니다";
            var plain = Analyze(text);
            var korean = Analyze(text, korean: true);

            int space = text.IndexOf(' ');
            Assert.AreEqual(LineBreaker.Opportunity.Allowed, korean[space + 1],
                "Korean must still break after a space");

            bool plainBrokeInsideAWord = false;
            for (int i = 1; i < space; i++)
                if (plain[i] == LineBreaker.Opportunity.Allowed) plainBrokeInsideAWord = true;
            Assert.IsTrue(plainBrokeInsideAWord,
                "the test needs untailored Korean to break between syllables");

            for (int i = 1; i < space; i++)
                Assert.AreNotEqual(LineBreaker.Opportunity.Allowed, korean[i],
                    "Korean word wrap must not break between syllables of one word");
        }

        [Test]
        public void KoreanWordWrap_IsPerLocale_NotGlobal()
        {
            // A Korean line in a Japanese UI still wants Korean wrapping, so
            // the rule follows the text's locale rather than a project toggle.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            TextLayoutResult Layout(string language)
            {
                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.MaxWidth = 100f;
                settings.Wrap = TextWrap.Wrap;
                settings.Language = language;
                settings.KoreanWordWrap = language == "ko";
                var result = new TextLayoutResult();
                engine.Layout("안녕하세요 반갑습니다", settings, result);
                return result;
            }

            var japanese = Layout("ja");
            var korean = Layout("ko");
            Assert.GreaterOrEqual(korean.Lines.Count, japanese.Lines.Count,
                "keeping syllables together cannot make the text take fewer lines");
        }

        // -------------------------------------------------- spacing and gaps

        [Test]
        public void CjkLatinGap_IsWantedInBothDirections()
        {
            Assert.IsTrue(AsianTypography.WantsLatinGap('語', 'E'));
            Assert.IsTrue(AsianTypography.WantsLatinGap('h', '日'));
            Assert.IsTrue(AsianTypography.WantsLatinGap('あ', 'A'));
            Assert.IsTrue(AsianTypography.WantsLatinGap('한', 'X'));

            Assert.IsFalse(AsianTypography.WantsLatinGap('語', '日'), "two Han need no gap");
            Assert.IsFalse(AsianTypography.WantsLatinGap('a', 'b'), "two Latin need no gap");
            Assert.IsFalse(AsianTypography.WantsLatinGap('語', '。'), "punctuation is not Latin");
        }

        [Test]
        public void CjkLatinSpacing_WidensMixedText()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            float Measure(bool spacing)
            {
                var settings = TextLayoutSettings.Default(fonts, 32f);
                settings.Wrap = TextWrap.NoWrap;
                settings.CjkLatinSpacing = spacing;
                var result = new TextLayoutResult();
                engine.Layout("日本語とEnglishの混在", settings, result);
                return result.Width;
            }

            Assert.Greater(Measure(true), Measure(false),
                "the quarter-em gap did not reach the line's width");
        }

        [Test]
        public void PunctuationCompression_NarrowsAdjacentFullWidthMarks()
        {
            // 約物詰め. Two full-width marks in a row leave an em of white
            // space set solid, which is what makes CJK text look rendered
            // rather than typeset.
            // Exactly one of an adjacent pair gives up its half: two marks
            // have one em of blank between them, not two. The second one
            // decides, so no two marks can both shrink the same gap.
            Assert.AreEqual(0.5f,
                AsianTypography.CompressionFor('）', '（', 'あ'), 0.001f,
                "an opening mark after a closing one gives up its leading blank");
            Assert.AreEqual(0f,
                AsianTypography.CompressionFor('あ', '）', '（'), 0.001f,
                "the first of the pair keeps its width; otherwise the gap closes twice");
            Assert.AreEqual(0f,
                AsianTypography.CompressionFor('あ', '）', 'い'), 0.001f,
                "a closing mark between letters keeps its width");
            Assert.AreEqual(0.5f,
                AsianTypography.CompressionFor('」', '』', 'あ'), 0.001f,
                "two closing marks in a row leave an em between their glyphs");

            Assert.IsTrue(AsianTypography.IsCompressible('、'));
            Assert.IsFalse(AsianTypography.IsCompressible('あ'));
        }

        [Test]
        public void CompressionPrefilter_NeverRejectsAMarkTheRulesAccept()
        {
            // MayCompress is three comparisons standing in for two binary
            // searches on the engine's innermost loop, and the way a
            // hand-written prefilter fails is silently: somebody adds a mark to
            // one of the lists, the range no longer covers it, and compression
            // quietly stops applying to that character alone. Checked over
            // every char there is, because there are only 65,536 of them.
            for (int c = 0; c <= char.MaxValue; c++)
            {
                char ch = (char)c;
                if (AsianTypography.CompressionAtLineStart(ch) > 0f ||
                    AsianTypography.CompressionAtLineEnd(ch) > 0f)
                    Assert.IsTrue(AsianTypography.MayCompress(ch),
                        $"U+{c:X4} compresses but the fast path rejects it");
            }
        }

        [Test]
        public void AsianSpacing_MeasuresTheSameWayItWraps()
        {
            // The bug tracking already caught once for <cspace>: the wrapper
            // and the renderer must not disagree about a width, or a line is
            // accepted as fitting and then drawn wider than the box it went in.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            const float box = 220f;
            foreach (var (spacing, compress) in new[] { (true, false), (false, true), (true, true) })
            {
                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.MaxWidth = box;
                settings.Wrap = TextWrap.Wrap;
                settings.CjkLatinSpacing = spacing;
                settings.PunctuationCompression = compress;
                var result = new TextLayoutResult();
                engine.Layout("日本語とEnglishの混在「テスト」（例）です", settings, result);

                foreach (var line in result.Lines)
                    Assert.LessOrEqual(line.Width, box + 0.5f,
                        $"spacing={spacing} compress={compress}: a line was wrapped as fitting " +
                        "and then measured wider than the box");
            }
        }

        [Test]
        public void PunctuationCompression_NarrowsTheLine()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            float Measure(bool compress)
            {
                var settings = TextLayoutSettings.Default(fonts, 32f);
                settings.Wrap = TextWrap.NoWrap;
                settings.PunctuationCompression = compress;
                var result = new TextLayoutResult();
                engine.Layout("「あ」、（い）。", settings, result);
                return result.Width;
            }

            Assert.Less(Measure(true), Measure(false),
                "compression did not narrow a line full of full-width punctuation");
        }

        /// <summary>Width of one NoWrap line, with compression on or off.</summary>
        private static float LineWidth(TextLayoutEngine engine, FontStack fonts, string text,
            bool compress)
        {
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.Wrap = TextWrap.NoWrap;
            settings.PunctuationCompression = compress;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);
            return result.Width;
        }

        [Test]
        public void LineEdgePunctuation_GivesUpItsBlankHalf()
        {
            // 行頭・行末の約物, the half of the rule that needs to know where the
            // lines are. A 。 last on a line has its blank against the right
            // margin: invisible, and half an em of width the wrapper counted.
            // An opening bracket first on a line has its blank against the left
            // margin, where it reads as an indent nobody asked for.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            Assert.Less(LineWidth(engine, fonts, "あい。", true),
                LineWidth(engine, fonts, "あい。", false),
                "a closing mark at a line end kept its blank right half");
            Assert.Less(LineWidth(engine, fonts, "「あい", true),
                LineWidth(engine, fonts, "「あい", false),
                "an opening mark at a line start kept its blank left half");

            // And the rule reaches only the edges: the same mark in the middle
            // of a line, with letters either side, keeps its full width.
            Assert.AreEqual(LineWidth(engine, fonts, "あ。い", false),
                LineWidth(engine, fonts, "あ。い", true), 0.001f,
                "a mark in the middle of a line was compressed by the line-edge rule");
        }

        [Test]
        public void LineEdgePunctuation_DoesNotStackWithTheAdjacencyRule()
        {
            // A mark has one blank half to surrender. A 」 that is both
            // preceded by another closing mark *and* last on its line gives up
            // half an em, not a whole one; the two halves of the rule are a
            // maximum, not a sum, and adding them would pull the last glyph
            // into the one before it.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            float edgeOnly = LineWidth(engine, fonts, "あ」", false)
                           - LineWidth(engine, fonts, "あ」", true);
            float both = LineWidth(engine, fonts, "あ」」", false)
                       - LineWidth(engine, fonts, "あ」」", true);

            Assert.Greater(edgeOnly, 0f, "the line-end rule gave up nothing");
            Assert.AreEqual(edgeOnly, both, 0.001f,
                "a mark at a line end that was already compressed for adjacency " +
                "gave up its half twice");
        }

        [Test]
        public void LineEdgeCompression_KeepsAWordOnTheLineItBelongsOn()
        {
            // The payoff, and the reason this belongs in the wrapper rather
            // than in a cosmetic pass afterwards: the half em a 。 does not
            // need at a line end is half an em the next character can have.
            // Measured against the box the text actually needs, so the test
            // does not depend on the metrics of any particular face.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            float uncompressed = LineWidth(engine, fonts, "あ。", false);
            float mark = uncompressed - LineWidth(engine, fonts, "あ", false);

            int Lines(bool compress)
            {
                var settings = TextLayoutSettings.Default(fonts, 32f);
                settings.Wrap = TextWrap.Wrap;
                settings.PunctuationCompression = compress;
                // Room for everything but the mark's blank half.
                settings.MaxWidth = uncompressed - mark * 0.5f + 0.1f;
                var result = new TextLayoutResult();
                engine.Layout("あ。", settings, result);
                return result.Lines.Count;
            }

            Assert.AreEqual(2, Lines(false), "the box was not the one this test needs");
            Assert.AreEqual(1, Lines(true),
                "the wrapper still counted a blank half nothing would be drawn in");
        }

        // ----------------------------------------------------- Korean particles

        [TestCase("사과", "을", "를")]   // ends in a vowel
        [TestCase("수박", "을", "을")]   // ends in a consonant
        [TestCase("사과", "를", "를")]   // the other spelling of the same pair
        [TestCase("수박", "를", "을")]
        [TestCase("사과", "은", "는")]
        [TestCase("수박", "는", "은")]
        [TestCase("사과", "이", "가")]
        [TestCase("수박", "가", "이")]
        [TestCase("사과", "과", "와")]
        [TestCase("수박", "와", "과")]
        public void Josa_ResolvesEitherSpelling(string word, string written, string expected)
        {
            // Either member of a pair is accepted and both resolve to the
            // correct one, so nobody has to remember which spelling the engine
            // wants, which is the difference between a feature and a trap.
            Assert.AreEqual(expected, KoreanJosa.Resolve(word, written));
        }

        [TestCase("서울", "으로", "로")]   // ㄹ takes the after-vowel form
        [TestCase("부산", "으로", "으로")] // any other consonant does not
        [TestCase("학교", "으로", "로")]   // a vowel does not
        [TestCase("서울", "로", "로")]
        [TestCase("부산", "로", "으로")]
        public void Josa_KnowsTheRieulException(string word, string written, string expected)
        {
            // (으)로 after ㄹ is the exception every Korean speaker knows and
            // every naive implementation gets wrong: 서울로, not 서울으로.
            Assert.AreEqual(expected, KoreanJosa.Resolve(word, written));
        }

        [TestCase("1", "을", "을")]   // 일 ends in ㄹ
        [TestCase("2", "을", "를")]   // 이 ends in a vowel
        [TestCase("3", "을", "을")]   // 삼 ends in ㅁ
        [TestCase("6", "을", "을")]   // 육 ends in ㄱ
        [TestCase("5", "을", "를")]   // 오 ends in a vowel
        [TestCase("1", "으로", "로")] // 일 ends in ㄹ, so the exception applies
        public void Josa_ReadsNumbersAloud(string number, string written, string expected)
        {
            // "아이템 3을 선택": the particle follows how the number is read,
            // not how it is written, which is the case interpolated strings hit
            // constantly.
            Assert.AreEqual(expected, KoreanJosa.Resolve(number, written));
        }

        [Test]
        public void Josa_LeavesNonKoreanAlone()
        {
            // A Latin name before a Korean particle is a real case. Guessing
            // produces something worse than the author's own choice.
            Assert.AreEqual("을", KoreanJosa.Resolve("Sword", "을"));
            Assert.AreEqual("를", KoreanJosa.Resolve("", "를"));
            Assert.AreEqual("xyz", KoreanJosa.Resolve("사과", "xyz"), "not a particle we know");
        }

        [Test]
        public void Josa_SkipsTrailingPunctuationAndSpace()
        {
            Assert.AreEqual("를", KoreanJosa.Resolve("사과 ", "을"));
            Assert.AreEqual("을", KoreanJosa.Resolve("수박)", "을"));
        }

        [Test]
        public void JosaTag_ResolvesAtParseTime()
        {
            // The point of the tag over a formatter: it works on a string that
            // was interpolated at runtime, with no C# call anywhere, which is
            // the case a localisation table produces.
            var result = new RichTextResult();
            RichTextParser.Parse("사과<josa=을> 먹었다", result);
            Assert.AreEqual("사과를 먹었다", result.Text);

            result = new RichTextResult();
            RichTextParser.Parse("수박<josa=를> 먹었다", result);
            Assert.AreEqual("수박을 먹었다", result.Text);

            // And it leaves no mark on the styles: a particle is part of the
            // sentence, not a tag that splits a run.
            foreach (var span in result.Spans)
                Assert.AreEqual(TextStyle.Default, span.Style);
        }

        [Test]
        public void JosaTag_StaysLiteralWhenItIsNotAParticle()
        {
            Assert.AreEqual("<josa=xyz>", ParseText("<josa=xyz>"));
            Assert.AreEqual("<josa=>", ParseText("<josa=>"));
        }

        private static string ParseText(string source)
        {
            var result = new RichTextResult();
            RichTextParser.Parse(source, result);
            return result.Text;
        }

        // ------------------------------------------------ dictionary breaking

        [Test]
        public void ThaiBreaksOutOfTheBox()
        {
            // "Thai works, and works better when you load ICU's list" is only
            // true if the built-in list is actually installed. It was not, in
            // the first version of this: s_lists started empty and Apply was a
            // no-op, so Thai wrapped exactly as badly as before.
            DictionaryLineBreaker.ResetToDefaults();
            const string text = "สวัสดีครับ";
            var opportunities = new LineBreaker.Opportunity[text.Length + 1];
            LineBreaker.Analyze(text, opportunities);
            DictionaryLineBreaker.Apply(text, opportunities);

            Assert.AreEqual(LineBreaker.Opportunity.Allowed, opportunities["สวัสดี".Length],
                "Thai did not break at a word boundary with no setup at all");
        }

        [Test]
        public void ThaiWithoutADictionary_IsLeftAlone()
        {
            // UAX #14 gives these scripts class SA and defers them. With the
            // dictionary explicitly cleared there is nothing to defer to, and
            // inventing a break would be no better than the wrong break this
            // exists to fix.
            DictionaryLineBreaker.ClearAll();
            const string text = "สวัสดีครับ";
            var opportunities = new LineBreaker.Opportunity[text.Length + 1];
            LineBreaker.Analyze(text, opportunities);
            var before = (LineBreaker.Opportunity[])opportunities.Clone();

            DictionaryLineBreaker.Apply(text, opportunities);
            CollectionAssert.AreEqual(before, opportunities,
                "no dictionary must mean no change at all");
        }

        [Test]
        public void ThaiWithADictionary_BreaksBetweenWords()
        {
            var words = DictionaryLineBreaker.BuiltInThai();
            DictionaryLineBreaker.SetWordList("Thai", words);
            try
            {
                const string text = "สวัสดีครับ";  // สวัสดี + ครับ
                var opportunities = new LineBreaker.Opportunity[text.Length + 1];
                LineBreaker.Analyze(text, opportunities);
                DictionaryLineBreaker.Apply(text, opportunities);

                int boundary = "สวัสดี".Length;
                Assert.AreEqual(LineBreaker.Opportunity.Allowed, opportunities[boundary],
                    "the dictionary did not find the word boundary");

                // And it must not invent boundaries inside a word.
                for (int i = 1; i < boundary; i++)
                    Assert.AreNotEqual(LineBreaker.Opportunity.Allowed, opportunities[i],
                        $"a break appeared inside a word, at {i}");
            }
            finally
            {
                DictionaryLineBreaker.ResetToDefaults();
            }
        }

        [Test]
        public void UnknownWords_DoNotStopTheSegmenter()
        {
            // A real sentence has names, numbers and loanwords no dictionary
            // holds. A segmenter that gives up at the first unknown word
            // produces one unbreakable line: the failure being fixed.
            var words = DictionaryLineBreaker.BuiltInThai();
            DictionaryLineBreaker.SetWordList("Thai", words);
            try
            {
                // known + nonsense + known + known: the trailing word matters,
                // because a break at the very end of the run is not a break.
                const string text = "สวัสดีฬฬฬครับขอบคุณ";
                var opportunities = new LineBreaker.Opportunity[text.Length + 1];
                LineBreaker.Analyze(text, opportunities);
                DictionaryLineBreaker.Apply(text, opportunities);

                bool foundLater = false;
                for (int i = "สวัสดี".Length + 1; i < text.Length; i++)
                    if (opportunities[i] == LineBreaker.Opportunity.Allowed) foundLater = true;
                Assert.IsTrue(foundLater,
                    "the segmenter stopped at an unknown word instead of skipping it");
            }
            finally
            {
                DictionaryLineBreaker.ResetToDefaults();
            }
        }

        [Test]
        public void WordList_LoadsIcuFormat_AndReportsCoverage()
        {
            // ICU's dictionaries are newline-separated with comments and
            // optional tab-separated weights, so they drop in unmodified.
            var words = new WordList();
            words.AddAll("# a comment\nสวัสดี\nครับ\t100\n\n");
            Assert.AreEqual(2, words.WordCount);
            Assert.AreEqual("สวัสดี".Length, words.LongestWord);

            Assert.AreEqual(1f, words.Coverage("สวัสดีครับ"), 0.001f,
                "a fully known sentence should report full coverage");
            Assert.Less(words.Coverage("สวัสดีฬฬฬ"), 1f,
                "coverage must fall when the dictionary does not know the words; that number " +
                "is how a team checks Thai wrapping without reading Thai");
        }

        [Test]
        public void OnlyTheDeferredScripts_AreTouched()
        {
            Assert.AreEqual("Thai", DictionaryLineBreaker.ScriptOf('ส'));
            Assert.AreEqual("Lao", DictionaryLineBreaker.ScriptOf('ສ'));
            Assert.AreEqual("Khmer", DictionaryLineBreaker.ScriptOf('ក'));
            Assert.AreEqual("Myanmar", DictionaryLineBreaker.ScriptOf('က'));
            Assert.IsNull(DictionaryLineBreaker.ScriptOf('a'));
            Assert.IsNull(DictionaryLineBreaker.ScriptOf('日'));
            Assert.IsNull(DictionaryLineBreaker.ScriptOf('한'));
        }

        [Test]
        public void ThaiWraps_ThroughTheLayoutEngine()
        {
            var words = DictionaryLineBreaker.BuiltInThai();
            DictionaryLineBreaker.SetWordList("Thai", words);
            try
            {
                using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
                using var fonts = FontStack.Single(font);
                using var engine = new TextLayoutEngine();

                var settings = TextLayoutSettings.Default(fonts, 24f);
                settings.MaxWidth = 60f;
                settings.Wrap = TextWrap.Wrap;
                var result = new TextLayoutResult();
                engine.Layout("สวัสดีครับขอบคุณครับ", settings, result);

                Assert.Greater(result.Lines.Count, 1, "narrow Thai text did not wrap at all");

                int covered = 0;
                foreach (var line in result.Lines) covered += line.TextLength;
                Assert.AreEqual("สวัสดีครับขอบคุณครับ".Length, covered,
                    "wrapping lost characters");
            }
            finally
            {
                DictionaryLineBreaker.ResetToDefaults();
            }
        }

        // ------------------------------------------------------------- locale

        [Test]
        public void Locale_DecidesWhichFontDrawsAUnifiedCodepoint()
        {
            // The Han unification fix. 直 is one codepoint whose correct shape
            // differs between Japanese and Chinese readers, and without this
            // the fallback *order* decides which they get, so a Japanese
            // player sees Chinese glyph shapes because somebody listed the
            // Chinese font first. The test face carries 直 for exactly this;
            // two instances of it stand in for two regional fonts.
            const char han = '\u76F4';
            using var first = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ColorFontPath)));
            using var second = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ColorFontPath)));

            using var stack = new FontStack();
            stack.Add(first, "zh-Hans");
            stack.Add(second, "ja");

            Assert.AreSame(second, stack.Resolve(han, false, false,
                FontStack.Presentation.Any, "ja"),
                "a Japanese label must get the Japanese font even though the Chinese one is first");
            Assert.AreSame(first, stack.Resolve(han, false, false,
                FontStack.Presentation.Any, "zh-Hans"),
                "a Chinese label must get the Chinese font");

            // A region subtag must not stop the language from matching.
            Assert.AreSame(first, stack.Resolve(han, false, false,
                FontStack.Presentation.Any, "zh-Hans-CN"));

            // With no locale, and for a locale no font claims, order decides.
            Assert.AreSame(first, stack.Resolve(han, false, false,
                FontStack.Presentation.Any, null));
            Assert.AreSame(first, stack.Resolve(han, false, false,
                FontStack.Presentation.Any, "ko"));

            // And the locale must not reach past the characters it has an
            // opinion about. A CJK font covers ASCII too, so letting the
            // language decide every codepoint would silently move a Japanese
            // label's Latin text, digits and punctuation into the CJK face:
            // a whole-label font swap dressed up as a Han-unification fix.
            Assert.AreSame(first, stack.Resolve('A', false, false,
                FontStack.Presentation.Any, "ja"),
                "the locale hijacked Latin text");
        }

        [Test]
        public void Locale_SelectsTheRegionalForm()
        {
            // The strong half of the claim, and the one no real-world test face
            // could carry: 直 is one codepoint whose correct shape differs
            // between Japanese and Chinese readers, and OpenType locl is how a
            // font offers both. LoclRegional.ttf is authored for this: the
            // regional forms are not in its cmap at all, so the only way to
            // reach them is for the language tag to have driven the feature.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LoclFontPath)));
            using var shaper = new Shaper();

            uint Shape(string language)
            {
                var glyphs = new List<ShapedGlyph>();
                shaper.Shape(font, "直", 0, 1, Shaper.Direction.LeftToRight, glyphs, language);
                Assert.AreEqual(1, glyphs.Count);
                return glyphs[0].GlyphId;
            }

            uint neutral = Shape(null);
            uint japanese = Shape("ja");
            uint chinese = Shape("zh-Hans");

            Assert.AreNotEqual(neutral, japanese,
                "a Japanese label did not get the Japanese form");
            Assert.AreNotEqual(neutral, chinese,
                "a Chinese label did not get the Chinese form");
            Assert.AreNotEqual(japanese, chinese,
                "both locales got the same glyph: the feature ran, but not per language");

            // A locale the feature says nothing about falls back to the default
            // form rather than to whichever regional one happens to be first.
            Assert.AreEqual(neutral, Shape("ko"));
            Assert.AreEqual(neutral, Shape("zh-Hant"));

            // And the feature reaches only the character it names. 一 is Han in
            // the same script with no locl rule; a tag that moved it would mean
            // the language was being applied to the run rather than to the
            // substitution the font asked for.
            var plain = new List<ShapedGlyph>();
            var tagged = new List<ShapedGlyph>();
            shaper.Shape(font, "一", 0, 1, Shaper.Direction.LeftToRight, plain, null);
            shaper.Shape(font, "一", 0, 1, Shaper.Direction.LeftToRight, tagged, "ja");
            Assert.AreEqual(plain[0].GlyphId, tagged[0].GlyphId,
                "the locale changed a character the font has no regional form for");
        }

        [Test]
        public void Locale_ReachesShaping()
        {
            // The other half: a tag must not corrupt text it has no opinion
            // about. Ordinary Latin in a face with no locl table at all is the
            // case where a language tag can only do harm.
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var shaper = new Shaper();

            var plain = new List<ShapedGlyph>();
            var tagged = new List<ShapedGlyph>();
            shaper.Shape(font, "Hamburgefonstiv", 0, 15, Shaper.Direction.LeftToRight, plain, null);
            shaper.Shape(font, "Hamburgefonstiv", 0, 15, Shaper.Direction.LeftToRight, tagged, "ja");

            Assert.AreEqual(plain.Count, tagged.Count);
            for (int i = 0; i < plain.Count; i++)
            {
                Assert.AreEqual(plain[i].GlyphId, tagged[i].GlyphId);
                Assert.AreEqual(plain[i].XAdvance, tagged[i].XAdvance);
            }
        }
    }
}
