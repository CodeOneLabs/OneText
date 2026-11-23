using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText.Unicode;

namespace OneText.Tests
{
    /// <summary>
    /// The layout engine's shortcuts are only shortcuts if nothing can tell.
    /// Each test here lays the same text out with a switch off and on and
    /// compares the two results field by field, exactly: every glyph, run and
    /// line, every float. "Close" is not the claim; a fast path that is off by
    /// an ulp somewhere is a fast path that draws a different label.
    /// </summary>
    public class LayoutFastPathTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private FontData _latin, _arabic;

        [OneTimeSetUp]
        public void LoadFonts()
        {
            _latin = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            _arabic = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ArabicFontPath)));
        }

        [OneTimeTearDown]
        public void DisposeFonts()
        {
            _latin?.Dispose();
            _arabic?.Dispose();
        }

        [TearDown]
        public void RestoreSwitches()
        {
            SetShortcuts(true);
        }

        private static readonly string[] AsciiCorpus =
        {
            "", "7", "42", "427", "9000", "-12", "Player 3", "Score 12345", "trailing   ", "   leading",
            " ", "   ", "a  b", "fi fl ffi office affluent", "AV To Wa Ty", "(x+y)*z=[1]{2}<3>",
            "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~",
            "The quick brown fox jumps over the lazy dog, again and again and again.",
            "W", "i", ".", "~", "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW",
        };

        /// <summary>Every setting that reaches the simple path, crossed with the corpus.</summary>
        private static IEnumerable<TextLayoutSettings> Variations(FontStack fonts)
        {
            var alignments = (TextAlignment[])Enum.GetValues(typeof(TextAlignment));
            var overflows = (TextOverflow[])Enum.GetValues(typeof(TextOverflow));
            foreach (float size in new[] { 28f, 13.37f })
            foreach (float width in new[] { 0f, 30f, 200f, 1e5f })
            foreach (var overflow in overflows)
            foreach (var alignment in alignments)
            {
                var s = TextLayoutSettings.Default(fonts, size);
                s.Wrap = TextWrap.NoWrap;
                s.MaxWidth = width;
                s.Overflow = overflow;
                s.Alignment = alignment;
                yield return s;
            }

            foreach (float spacing in new[] { 0f, 1f, 1.5f, 0.7f })
            foreach (float tracking in new[] { 0f, 0.05f, -0.1f, 0.333f })
            foreach (bool hasTracking in new[] { false, true })
            foreach (float height in new[] { 0f, 5f, 100f })
            {
                var s = TextLayoutSettings.Default(fonts, 24f);
                s.Wrap = TextWrap.NoWrap;
                s.MaxWidth = 240f;
                s.MaxHeight = height;
                s.Overflow = height > 0f ? TextOverflow.Ellipsis : TextOverflow.Overflow;
                s.LineSpacing = spacing;
                s.LetterSpacingEm = tracking;
                s.HasLetterSpacing = hasTracking;
                yield return s;
            }

            // The East Asian rules and tailorings are all on; none of them has
            // anything to say about ASCII, which is the claim being tested.
            foreach (byte direction in new[] { (byte)0, BidiAlgorithm.AutoDirection })
            foreach (string language in new[] { null, "", "ja", "ko", "zh-Hans" })
            {
                var s = TextLayoutSettings.Default(fonts, 20f);
                s.Wrap = TextWrap.NoWrap;
                s.MaxWidth = 150f;
                s.BaseDirection = direction;
                s.Language = language;
                s.KoreanWordWrap = language == "ko";
                s.Kinsoku = AsianTypography.Kinsoku.Strict;
                s.CjkLatinSpacing = true;
                s.PunctuationCompression = true;
                s.Alignment = TextAlignment.End;
                yield return s;
            }

            // <align> overrides still reach the simple path.
            var aligned = TextLayoutSettings.Default(fonts, 20f);
            aligned.Wrap = TextWrap.NoWrap;
            aligned.MaxWidth = 300f;
            aligned.Alignments = new List<(int, TextAlignment)> { (0, TextAlignment.Right) };
            yield return aligned;
        }

        [Test]
        public void SimpleLine_Equals_The_General_Path_In_Every_Field()
        {
            int compared = 0;
            foreach (var spacingOfFont in new[] { 0f, 0.08f })
            {
                using var fonts = new FontStack();
                fonts.Add(_latin, null, spacingOfFont);
                fonts.Add(_arabic);
                using var slow = new TextLayoutEngine();
                using var fast = new TextLayoutEngine();
                var expected = new TextLayoutResult();
                var actual = new TextLayoutResult();

                foreach (var settings in Variations(fonts))
                foreach (string text in AsciiCorpus)
                {
                    TextLayoutEngine.SimpleLineFastPath = false;
                    TextLayoutEngine.AsciiCoverageCache = false;
                    slow.Layout(text, settings, expected);
                    TextLayoutEngine.SimpleLineFastPath = true;
                    TextLayoutEngine.AsciiCoverageCache = true;
                    fast.Layout(text, settings, actual);
                    LayoutComparison.AssertEqual(expected, actual,
                        $"\"{text}\" size {settings.FontSize} width {settings.MaxWidth} " +
                        $"overflow {settings.Overflow} align {settings.Alignment} " +
                        $"spacing {settings.LineSpacing} tracking {settings.LetterSpacingEm}/{settings.HasLetterSpacing} " +
                        $"height {settings.MaxHeight} lang {settings.Language} font spacing {spacingOfFont}");
                    compared++;
                }
            }
            Assert.Greater(compared, 1000);
        }

        [Test]
        public void SimpleLine_Is_Actually_Taken()
        {
            // The equality above proves nothing if the fast path never runs.
            // Its fingerprint: the general path leaves the shaped glyphs in the
            // measuring buffer, the fast path never fills it.
            using var fonts = FontStack.Single(_latin);
            using var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();
            var settings = TextLayoutSettings.Default(fonts, 28f);
            settings.Wrap = TextWrap.NoWrap;
            settings.MaxWidth = 200f;

            var measured = (List<ShapedGlyph>)typeof(TextLayoutEngine)
                .GetField("_measured", System.Reflection.BindingFlags.NonPublic |
                                       System.Reflection.BindingFlags.Instance)
                .GetValue(engine);

            TextLayoutEngine.SimpleLineFastPath = false;
            engine.Layout("427", settings, result);
            Assert.AreEqual(3, measured.Count, "the general path measures");
            measured.Clear();
            TextLayoutEngine.SimpleLineFastPath = true;
            engine.Layout("427", settings, result);
            Assert.AreEqual(0, measured.Count, "the fast path does not");
            Assert.AreEqual(3, result.Glyphs.Count);

            // And it hands a line that has to be clipped back.
            settings.Overflow = TextOverflow.Ellipsis;
            settings.MaxWidth = 10f;
            engine.Layout("Score 12345", settings, result);
            Assert.Greater(measured.Count, 0, "a clipped line goes the long way");
            Assert.IsTrue(result.Truncated);
        }

        private static readonly string[] GeneralCorpus =
        {
            "", "가나다 漢字 abc", "사과를 먹었다. 수박도!", "日本語とEnglishの混在、「括弧」。", "ありがとう！！！！",
            "สว\u0E31สด\u0E35คร\u0E31บ ภาษาไทย", "two\nlines", "crlf\r\nand\rcr", "end\n", "\n\n", "a\u2028b\u2029c\u0085d",
            "مرحبا بالعالم 123", "abc עברית def", "x\u0301y\u0308", "👍🏽 and 🇰🇷 and 👨\u200D👩\u200D👧", "\u202Bembedded\u202C ltr",
            "Score: 12,345 (x2)", "  spaces  ", "漢字\nかな\nカナ", "ㄱㄴㄷ 한글 자모 각",
            "<nobr>keep this together</nobr> but not this", "<ruby=かんじ>漢字</ruby>を<ruby=よ>読</ruby>む",
            "<b>bold</b> <size=150%>big</size> <color=#f00>red</color>\n<cspace=0.1>spaced</cspace>",
            "<mspace=0.6>12:34</mspace> 가나", "mixed 가나 <i>it</i> مرحبا\nnext 漢字",
            "icon <sprite=0> and <sprite=1><sprite=1> x", "<align=right>right</align>\n<align=center>c</align>",
            "the quick brown fox jumps over the lazy dog and keeps on running far away",
        };

        [Test]
        public void General_Path_Shortcuts_Equal_The_Long_Way()
        {
            using var fonts = new FontStack();
            fonts.Add(_latin);
            fonts.Add(_arabic);
            var extra = new List<FontData>();
            foreach (string name in new[] { "NotoSansCJKjp-Regular.otf", "NotoSansThai-Regular.ttf", "NotoSansHebrew-Regular.ttf" })
            {
                string path = Path.GetFullPath("Packages/com.onetext.core/Tests/CoverageFonts~/" + name);
                if (!File.Exists(path)) continue;
                var font = FontData.Load(File.ReadAllBytes(path));
                extra.Add(font);
                fonts.Add(font);
            }

            using var slow = new TextLayoutEngine();
            using var fast = new TextLayoutEngine();
            var expected = new TextLayoutResult();
            var actual = new TextLayoutResult();
            var markup = new RichTextResult();
            int compared = 0, faults = 0;
            try
            {
                foreach (string source in GeneralCorpus)
                {
                    RichTextParser.Parse(source, markup);
                    string text = markup.Text;
                    foreach (var wrap in new[] { TextWrap.NoWrap, TextWrap.Wrap })
                    foreach (var overflow in new[] { TextOverflow.Overflow, TextOverflow.Truncate, TextOverflow.Ellipsis })
                    foreach (float width in new[] { 0f, 40f, 240f })
                    foreach (float height in new[] { 0f, 30f })
                    foreach (string language in new[] { "", "ja", "ko" })
                    foreach (byte direction in new[] { BidiAlgorithm.AutoDirection, (byte)0, (byte)1 })
                    foreach (var alignment in new[] { TextAlignment.Center, TextAlignment.Justified })
                    {
                        var s = TextLayoutSettings.Default(fonts, 20f);
                        s.Wrap = wrap;
                        s.Overflow = overflow;
                        s.MaxWidth = width;
                        s.MaxHeight = height;
                        s.Language = language;
                        s.KoreanWordWrap = language == "ko";
                        s.Kinsoku = language == "ja" ? AsianTypography.Kinsoku.Strict : AsianTypography.Kinsoku.Off;
                        s.CjkLatinSpacing = language == "ja";
                        s.PunctuationCompression = language == "ja";
                        s.BaseDirection = direction;
                        s.Spans = markup.Spans.Count > 0 ? markup.Spans : null;
                        s.Rubies = markup.Rubies.Count > 0 ? markup.Rubies : null;
                        s.Alignment = alignment;
                        s.Alignments = markup.Alignments.Count > 0 ? markup.Alignments : null;

                        string context =
                            $"\"{source}\" {wrap} {overflow} width {width} height {height} lang {language} dir {direction} {alignment}";
                        // ApplyEllipsis indexes the runs of a line that has
                        // none when the line it would shorten is a blank one
                        // (a pre-existing fault, on both paths). Equal here
                        // means failing the same way.
                        SetShortcuts(false);
                        var slowFault = Run(slow, text, s, expected);
                        SetShortcuts(true);
                        var fastFault = Run(fast, text, s, actual);
                        Assert.AreEqual(slowFault?.GetType(), fastFault?.GetType(), context);
                        if (slowFault != null) { faults++; continue; }
                        LayoutComparison.AssertEqual(expected, actual, context);
                        compared++;
                    }
                }
            }
            finally
            {
                SetShortcuts(true);
                foreach (var font in extra) font.Dispose();
            }
            Assert.Greater(compared, 16000);
            Assert.Less(faults, 400);
        }

        private static Exception Run(TextLayoutEngine engine, string text, in TextLayoutSettings settings,
            TextLayoutResult result)
        {
            try
            {
                engine.Layout(text, settings, result);
                return null;
            }
            catch (ArgumentOutOfRangeException e)
            {
                return e;
            }
        }

        private static void SetShortcuts(bool on)
        {
            TextLayoutEngine.SimpleLineFastPath = on;
            TextLayoutEngine.AsciiCoverageCache = on;
            TextLayoutEngine.NoWrapMandatoryBreaksOnly = on;
            BidiRuns.LeftToRightShortcut = on;
            TextSegmenter.StandaloneShortcut = on;
            TextLayoutEngine.PlainItemization = on;
            TextLayoutEngine.HoistedSpacingChecks = on;
            TextLayoutEngine.LeanEmit = on;
            TextLayoutEngine.RunsShapedInPlace = on;
        }

        [Test]
        public void Ascii_Coverage_Cache_Follows_The_Font()
        {
            // A face with no Latin at all: every character fails the guard, so
            // the label must go the general path and fall back, cache or not.
            using var arabicOnly = FontStack.Single(_arabic);
            using var latin = FontStack.Single(_latin);
            using var engine = new TextLayoutEngine();
            using var reference = new TextLayoutEngine();
            var expected = new TextLayoutResult();
            var actual = new TextLayoutResult();
            foreach (var fonts in new[] { latin, arabicOnly, latin, arabicOnly })
            {
                var settings = TextLayoutSettings.Default(fonts, 20f);
                settings.Wrap = TextWrap.NoWrap;
                foreach (string text in AsciiCorpus)
                {
                    TextLayoutEngine.AsciiCoverageCache = false;
                    TextLayoutEngine.SimpleLineFastPath = false;
                    reference.Layout(text, settings, expected);
                    TextLayoutEngine.AsciiCoverageCache = true;
                    TextLayoutEngine.SimpleLineFastPath = true;
                    engine.Layout(text, settings, actual);
                    LayoutComparison.AssertEqual(expected, actual, $"\"{text}\" primary {fonts.Primary}");
                }
            }
        }
    }

    /// <summary>Field-by-field comparison of two layout results.</summary>
    internal static class LayoutComparison
    {
        public static void AssertEqual(TextLayoutResult expected, TextLayoutResult actual, string context)
        {
            string difference = Difference(expected, actual);
            if (difference != null) Assert.Fail(context + ": " + difference);
        }

        // Exact, bit for bit: NaN never reaches here and -0 against +0 is a
        // difference worth hearing about.
        private static bool Same(float a, float b) =>
            BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        public static string Difference(TextLayoutResult e, TextLayoutResult a)
        {
            if (!Same(e.Width, a.Width)) return $"Width {e.Width:R} vs {a.Width:R}";
            if (!Same(e.Height, a.Height)) return $"Height {e.Height:R} vs {a.Height:R}";
            if (!Same(e.FontSize, a.FontSize)) return "FontSize";
            if (e.WritingMode != a.WritingMode) return "WritingMode";
            if (e.Truncated != a.Truncated) return $"Truncated {e.Truncated} vs {a.Truncated}";

            if (e.GraphemeStarts.Count != a.GraphemeStarts.Count)
                return $"GraphemeStarts.Count {e.GraphemeStarts.Count} vs {a.GraphemeStarts.Count}";
            for (int i = 0; i < e.GraphemeStarts.Count; i++)
                if (e.GraphemeStarts[i] != a.GraphemeStarts[i]) return $"GraphemeStarts[{i}]";

            if (e.Glyphs.Count != a.Glyphs.Count) return $"Glyphs.Count {e.Glyphs.Count} vs {a.Glyphs.Count}";
            for (int i = 0; i < e.Glyphs.Count; i++)
            {
                var x = e.Glyphs[i];
                var y = a.Glyphs[i];
                if (x.GlyphId != y.GlyphId || x.Cluster != y.Cluster || x.XAdvance != y.XAdvance ||
                    x.YAdvance != y.YAdvance || x.XOffset != y.XOffset || x.YOffset != y.YOffset)
                    return $"Glyphs[{i}] {Describe(x)} vs {Describe(y)}";
            }

            if (e.Runs.Count != a.Runs.Count) return $"Runs.Count {e.Runs.Count} vs {a.Runs.Count}";
            for (int i = 0; i < e.Runs.Count; i++)
            {
                var x = e.Runs[i];
                var y = a.Runs[i];
                string field =
                    !ReferenceEquals(x.Font, y.Font) ? "Font" :
                    x.GlyphStart != y.GlyphStart ? "GlyphStart" :
                    x.GlyphCount != y.GlyphCount ? "GlyphCount" :
                    x.TextStart != y.TextStart ? "TextStart" :
                    x.TextLength != y.TextLength ? "TextLength" :
                    !Same(x.X, y.X) ? $"X {x.X:R} vs {y.X:R}" :
                    !Same(x.Baseline, y.Baseline) ? $"Baseline {x.Baseline:R} vs {y.Baseline:R}" :
                    !Same(x.Width, y.Width) ? $"Width {x.Width:R} vs {y.Width:R}" :
                    x.Level != y.Level ? "Level" :
                    !Same(x.FontSize, y.FontSize) ? "FontSize" :
                    !x.Style.Equals(y.Style) ? "Style" :
                    !Same(x.BaselineShift, y.BaselineShift) ? "BaselineShift" :
                    x.SyntheticBold != y.SyntheticBold ? "SyntheticBold" :
                    x.IsRuby != y.IsRuby ? "IsRuby" :
                    x.Rotated != y.Rotated ? "Rotated" : null;
                if (field != null) return $"Runs[{i}].{field}";
            }

            if (e.Lines.Count != a.Lines.Count) return $"Lines.Count {e.Lines.Count} vs {a.Lines.Count}";
            for (int i = 0; i < e.Lines.Count; i++)
            {
                var x = e.Lines[i];
                var y = a.Lines[i];
                string field =
                    x.RunStart != y.RunStart ? "RunStart" :
                    x.RunCount != y.RunCount ? "RunCount" :
                    x.TextStart != y.TextStart ? "TextStart" :
                    x.TextLength != y.TextLength ? "TextLength" :
                    !Same(x.Width, y.Width) ? $"Width {x.Width:R} vs {y.Width:R}" :
                    !Same(x.Baseline, y.Baseline) ? "Baseline" :
                    !Same(x.Ascent, y.Ascent) ? "Ascent" :
                    !Same(x.Descent, y.Descent) ? "Descent" :
                    !Same(x.Height, y.Height) ? "Height" :
                    !Same(x.InlineOffset, y.InlineOffset) ? $"InlineOffset {x.InlineOffset:R} vs {y.InlineOffset:R}" :
                    x.ParagraphLevel != y.ParagraphLevel ? "ParagraphLevel" :
                    x.IsParagraphEnd != y.IsParagraphEnd ? "IsParagraphEnd" : null;
                if (field != null) return $"Lines[{i}].{field}";
            }
            return null;
        }

        private static string Describe(ShapedGlyph g) =>
            $"(id {g.GlyphId} cl {g.Cluster} adv {g.XAdvance},{g.YAdvance} off {g.XOffset},{g.YOffset})";
    }
}
