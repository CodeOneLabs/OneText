using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Unicode;

namespace OneText.Tests
{
    /// <summary>
    /// <see cref="LayoutFastPathTests"/> for text with style spans: the same
    /// text laid out with the styled shortcuts off and on must agree in every
    /// glyph, run and line, exactly.
    ///
    /// The corpus is built to reach the corners of the styled item loop:
    /// every tag the parser knows over Latin, Korean, Japanese, Arabic and
    /// mixed text; nested and adjacent tags; spans that begin or end inside a
    /// grapheme cluster, empty spans, gaps between spans and lists that are
    /// out of order (which the shortcut must hand to the general loop); named
    /// styles and font overrides, including an override that names a disposed
    /// face; sprites and ruby under styles.
    /// </summary>
    public class StyledLayoutFastPathTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string CoveragePath = "Packages/com.onetext.core/Tests/CoverageFonts~/";

        private FontData _latin, _arabic, _cjk, _hebrew, _disposed;
        private readonly List<FontData> _owned = new List<FontData>();

        [OneTimeSetUp]
        public void LoadFonts()
        {
            // NotoSans is variable, so <b> over it is an instanced face; the
            // CJK face is not, so <b> over it is synthetic bold. Both halves of
            // the bold question are in the corpus.
            _latin = Own(FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath))));
            _arabic = Own(FontData.Load(File.ReadAllBytes(Path.GetFullPath(ArabicFontPath))));
            _cjk = TryLoad("NotoSansCJKjp-Regular.otf");
            _hebrew = TryLoad("NotoSansHebrew-Regular.ttf");
            _disposed = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            _disposed.Dispose();
        }

        private FontData Own(FontData font)
        {
            _owned.Add(font);
            return font;
        }

        private FontData TryLoad(string name)
        {
            string path = Path.GetFullPath(CoveragePath + name);
            return File.Exists(path) ? Own(FontData.Load(File.ReadAllBytes(path))) : null;
        }

        [OneTimeTearDown]
        public void DisposeFonts()
        {
            foreach (var font in _owned) font.Dispose();
            _owned.Clear();
        }

        [TearDown]
        public void RestoreSwitches() => SetShortcuts(true);

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
            TextLayoutEngine.StyledItemization = on;
            TextLayoutEngine.SimpleTextShortcut = on;
        }

        private static readonly string[] Scripts =
        {
            "Score 12", "427", "AV To", "가나다 한글", "日本語かな、「括弧」。", "مرحبا 123", "abc 가나 مرحبا 日本",
            "x́ÿz", "👍🏽 ok", "שלום abc",
        };

        // {0} is one of the scripts above.
        private static readonly string[] Templates =
        {
            "<b>{0}</b>", "<i>{0}</i>", "<u>{0}</u>", "<s>{0}</s>", "<nobr>{0}</nobr> tail", "<color=#ff8800>{0}</color>",
            "<mark=#ffff0080>{0}</mark>", "<size=150%>{0}</size>", "<size=12>{0}</size>", "<voffset=0.3em>{0}</voffset>",
            "<cspace=0.1>{0}</cspace>", "<cspace=0>{0}</cspace>", "<mspace=0.6>{0}</mspace>", "<sup>{0}</sup>", "<sub>{0}</sub>",
            "<alpha=#80>{0}</alpha>", "<align=right>{0}</align>", "<style=title>{0}</style>", "<style=spaced>{0}</style>",
            "<font=cjk>{0}</font>", "<font=arabic>{0}</font>", "<font=gone>{0}</font>", "<font=none>{0}</font>",
            "<outline>{0}</outline>", "<link=a>{0}</link>", "<wave>{0}</wave>", "<noparse><b></noparse>{0}",
            "a<br>{0}", "<ruby=よみ>{0}</ruby>", "{0}<sprite=0><sprite=1>", "<b><sprite=0></b>{0}",
            "<b>{0}</b> <color=#ff8800>dmg</color>", "<b><color=red>{0}</color> <i>dmg</i></b>",
            "<color=#f00>{0}</color><color=#f00>{0}</color>", "<color=#f00>{0}</color><color=#0f0>{0}</color>",
            "<b>{0}<b>{0}</b>{0}</b>", "<size=120%><b>{0}</b></size> {0}", "<b>{0}", "{0}<i>", "<b></b>{0}<i></i>",
            "<u><s><i>{0}</i></s></u> <style=title><b>x</b></style>", "<b>{0}\n{0}</b> line two {0}",
        };

        private IEnumerable<string> Corpus()
        {
            foreach (string template in Templates)
            foreach (string script in Scripts)
                yield return string.Format(template, script);
        }

        private static int StyleIndex(string name) =>
            name == "title" ? 0 : name == "spaced" ? 1 : -1;

        private static int FontIndex(string name) =>
            name == "cjk" ? 0 : name == "arabic" ? 1 : name == "gone" ? 2 : name == "none" ? 3 : -1;

        private FontData FontOverride(int index) =>
            index == 0 ? _cjk : index == 1 ? _arabic : index == 2 ? _disposed : null;

        private static TextStyle NamedStyle(int index, TextStyle style)
        {
            if (index == 0)
            {
                style.Flags |= TextStyle.Flag.Bold | TextStyle.Flag.HasColor;
                style.Color = new UnityEngine.Color32(10, 20, 30, 255);
                style.SizeScale *= 1.25f;
            }
            else if (index == 1)
            {
                style.Flags |= TextStyle.Flag.HasLetterSpacing | TextStyle.Flag.Italic;
                style.LetterSpacingEm = 0.07f;
            }
            return style;
        }

        private FontStack Stack()
        {
            var fonts = new FontStack();
            fonts.Add(_latin);
            fonts.Add(_arabic);
            if (_cjk != null) fonts.Add(_cjk);
            if (_hebrew != null) fonts.Add(_hebrew);
            return fonts;
        }

        private IEnumerable<TextLayoutSettings> Variations(FontStack fonts)
        {
            foreach (var wrap in new[] { TextWrap.NoWrap, TextWrap.Wrap })
            foreach (var overflow in new[] { TextOverflow.Overflow, TextOverflow.Truncate, TextOverflow.Ellipsis })
            foreach (float width in new[] { 0f, 35f, 300f })
            foreach (var alignment in new[] { TextAlignment.Start, TextAlignment.Center, TextAlignment.Justified })
            foreach (byte direction in new[] { BidiAlgorithm.AutoDirection, (byte)1 })
            {
                var s = TextLayoutSettings.Default(fonts, 22f);
                s.Wrap = wrap;
                s.Overflow = overflow;
                s.MaxWidth = width;
                s.MaxHeight = overflow == TextOverflow.Overflow ? 0f : 40f;
                s.Alignment = alignment;
                s.BaseDirection = direction;
                yield return s;
            }

            // Letter spacing from the label and the face, the East Asian
            // rules, a language, and a column.
            foreach (bool hasSpacing in new[] { false, true })
            foreach (string language in new[] { "", "ja", "ko" })
            foreach (var mode in new[] { TextWritingMode.Horizontal, TextWritingMode.VerticalRightToLeft })
            {
                var s = TextLayoutSettings.Default(fonts, 18f);
                s.Wrap = TextWrap.NoWrap;
                s.MaxWidth = 200f;
                s.MaxHeight = 200f;
                s.HasLetterSpacing = hasSpacing;
                s.LetterSpacingEm = 0.05f;
                s.Language = language;
                s.KoreanWordWrap = language == "ko";
                s.Kinsoku = language == "ja" ? AsianTypography.Kinsoku.Strict : AsianTypography.Kinsoku.Off;
                s.CjkLatinSpacing = language == "ja";
                s.PunctuationCompression = language == "ja";
                s.WritingMode = mode;
                yield return s;
            }
        }

        private void Configure(ref TextLayoutSettings s, RichTextResult markup)
        {
            s.Spans = markup.Spans.Count > 0 ? markup.Spans : null;
            s.Rubies = markup.Rubies.Count > 0 ? markup.Rubies : null;
            s.Alignments = markup.Alignments.Count > 0 ? markup.Alignments : null;
            s.ResolveNamedStyle = NamedStyle;
            s.ResolveFontOverride = FontOverride;
            s.ResolveSpriteAspect = index => index == 0 ? 1.5f : 0.8f;
        }

        [Test]
        public void Styled_Shortcuts_Equal_The_General_Loop_In_Every_Field()
        {
            using var fonts = Stack();
            using var slow = new TextLayoutEngine();
            using var fast = new TextLayoutEngine();
            var expected = new TextLayoutResult();
            var actual = new TextLayoutResult();
            var markup = new RichTextResult();
            int compared = 0, faults = 0;

            foreach (string source in Corpus())
            {
                RichTextParser.Parse(source, markup, StyleIndex, FontIndex);
                string text = markup.Text;
                foreach (var variation in Variations(fonts))
                {
                    var s = variation;
                    Configure(ref s, markup);
                    string context = $"\"{source}\" {s.Wrap} {s.Overflow} width {s.MaxWidth} {s.Alignment} " +
                                     $"dir {s.BaseDirection} lang {s.Language} {s.WritingMode} spacing {s.HasLetterSpacing}";
                    Compare(slow, fast, text, s, expected, actual, context, ref compared, ref faults);
                }
            }
            Assert.Greater(compared, 30000);
            Assert.Less(faults, compared / 20);
        }

        /// <summary>
        /// Span lists the parser never produces: boundaries inside a grapheme
        /// cluster, empty spans, gaps, a list that does not reach the end, and
        /// lists out of order or overlapping (which must take the general loop
        /// and still agree, because the shortcut has to recognise them).
        /// </summary>
        [Test]
        public void Hand_Built_Span_Lists_Equal_The_General_Loop()
        {
            using var fonts = Stack();
            using var slow = new TextLayoutEngine();
            using var fast = new TextLayoutEngine();
            var expected = new TextLayoutResult();
            var actual = new TextLayoutResult();
            int compared = 0, faults = 0;

            var bold = TextStyle.Default;
            bold.Flags |= TextStyle.Flag.Bold;
            var red = TextStyle.Default;
            red.Flags |= TextStyle.Flag.HasColor;
            red.Color = new UnityEngine.Color32(255, 0, 0, 255);
            var big = TextStyle.Default;
            big.SizeScale = 1.5f;
            var named = TextStyle.Default;
            named.NamedStyle = 0;
            var overridden = TextStyle.Default;
            overridden.FontOverride = 0;
            var italicBold = bold;
            italicBold.Flags |= TextStyle.Flag.Italic;
            var nan = TextStyle.Default;
            nan.SizeScale = float.NaN;

            string[] texts =
            {
                "ééé abc", "가각갂 abc 123", "👍🏽👍🏽 x", "Score 427 dmg", "مرحبا abc", "漢字かなカナ",
                "fi fl ffi AV", "ä́b",
            };
            var styles = new[] { bold, red, big, named, overridden, italicBold, nan, TextStyle.Default };

            var lists = new List<List<TextStyleSpan>>();
            foreach (string text in texts)
            {
                int n = text.Length;
                for (int cut = 0; cut <= n; cut++)
                for (int st = 0; st < styles.Length; st++)
                {
                    // Two spans meeting at every index, a cluster interior included.
                    lists.Add(new List<TextStyleSpan>
                    {
                        new TextStyleSpan(0, cut, styles[st]),
                        new TextStyleSpan(cut, n - cut, styles[(st + 1) % styles.Length]),
                    });
                }
                lists.Add(new List<TextStyleSpan>
                {
                    new TextStyleSpan(0, 0, bold), new TextStyleSpan(0, 1, red), new TextStyleSpan(1, 0, big),
                    new TextStyleSpan(1, n - 1, red),
                });
                // A gap, then a list that stops short of the end.
                lists.Add(new List<TextStyleSpan> { new TextStyleSpan(1, 1, bold), new TextStyleSpan(3, 1, red) });
                lists.Add(new List<TextStyleSpan> { new TextStyleSpan(0, Math.Min(2, n), big) });
                // Out of order, and overlapping.
                lists.Add(new List<TextStyleSpan> { new TextStyleSpan(2, n - 2, red), new TextStyleSpan(0, 2, bold) });
                lists.Add(new List<TextStyleSpan> { new TextStyleSpan(0, n, red), new TextStyleSpan(1, 2, bold) });
                // One span per character.
                var each = new List<TextStyleSpan>();
                for (int i = 0; i < n; i++) each.Add(new TextStyleSpan(i, 1, styles[i % styles.Length]));
                lists.Add(each);

                foreach (var spans in lists)
                foreach (var wrap in new[] { TextWrap.NoWrap, TextWrap.Wrap })
                foreach (float width in new[] { 0f, 30f })
                foreach (var overflow in new[] { TextOverflow.Overflow, TextOverflow.Ellipsis })
                {
                    var s = TextLayoutSettings.Default(fonts, 20f);
                    s.Wrap = wrap;
                    s.MaxWidth = width;
                    s.Overflow = overflow;
                    s.Spans = spans;
                    s.ResolveNamedStyle = NamedStyle;
                    s.ResolveFontOverride = FontOverride;
                    Compare(slow, fast, text, s, expected, actual,
                        $"\"{text}\" spans [{string.Join(", ", spans)}] {wrap} {width} {overflow}",
                        ref compared, ref faults);
                }
                lists.Clear();
            }
            Assert.Greater(compared, 3000);
        }

        [Test]
        public void Styled_Itemization_Is_Actually_Taken()
        {
            // The general loop finds each character's style through a cursor
            // that it leaves where it stopped; the styled loop never moves it.
            using var fonts = Stack();
            using var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();
            var markup = new RichTextResult();
            RichTextParser.Parse("<b>427</b> <color=#ff8800>dmg</color>", markup);
            var settings = TextLayoutSettings.Default(fonts, 28f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Spans = markup.Spans;
            var cursor = typeof(TextLayoutEngine).GetField("_spanCursor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            TextLayoutEngine.StyledItemization = false;
            engine.Layout(markup.TextSpan, settings, result);
            Assert.Greater((int)cursor.GetValue(engine), 0, "the general loop walks the cursor");
            TextLayoutEngine.StyledItemization = true;
            engine.Layout(markup.TextSpan, settings, result);
            Assert.AreEqual(0, (int)cursor.GetValue(engine), "the styled loop does not");
            Assert.AreEqual(3, result.Runs.Count);
        }

        private static void Compare(TextLayoutEngine slow, TextLayoutEngine fast, string text,
            in TextLayoutSettings settings, TextLayoutResult expected, TextLayoutResult actual, string context,
            ref int compared, ref int faults)
        {
            SetShortcuts(false);
            var slowFault = Run(slow, text, settings, expected);
            SetShortcuts(true);
            var fastFault = Run(fast, text, settings, actual);
            Assert.AreEqual(slowFault?.GetType(), fastFault?.GetType(), context);
            if (slowFault != null)
            {
                faults++;
                return;
            }
            LayoutComparison.AssertEqual(expected, actual, context);

            // And each styled switch alone against everything else on, so a
            // fault in one cannot hide behind the other.
            TextLayoutEngine.StyledItemization = false;
            Run(slow, text, settings, expected);
            TextLayoutEngine.StyledItemization = true;
            LayoutComparison.AssertEqual(expected, actual, context + " (StyledItemization alone)");
            TextLayoutEngine.SimpleTextShortcut = false;
            Run(slow, text, settings, expected);
            TextLayoutEngine.SimpleTextShortcut = true;
            LayoutComparison.AssertEqual(expected, actual, context + " (SimpleTextShortcut alone)");
            compared++;
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
    }
}
