using System.Collections.Generic;
using System.IO;
using OneText.Editor;
using OneText.Unicode;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M11: the parts of the Hub that are not a window.
    ///
    /// Every tab is a view over something answerable without a window — what
    /// strings a project ships, which of them no font can draw, how much of the
    /// atlas a session actually wanted. Those are the parts tested here, and
    /// the window is then a way of looking at them.
    /// </summary>
    public class HubTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "OneTextHubTests", Path.GetRandomFileName());
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
            DictionaryLineBreaker.ResetToDefaults();
        }

        private void Write(string name, string contents) =>
            File.WriteAllText(Path.Combine(_folder, name), contents, System.Text.Encoding.UTF8);

        private static FontData Load(string path) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(path)));

        // ------------------------------------------------------------ scanning

        [Test]
        public void Csv_ReadsOneColumnPerLocale()
        {
            Write("ui.csv", "key,en,ko\ngreeting,Hello,안녕하세요\nfarewell,Bye,안녕히\n");

            var scan = TextSourceScanner.Scan(new[] { _folder });

            Assert.AreEqual(4, scan.Entries.Count);
            var locales = scan.Locales();
            CollectionAssert.AreEquivalent(new[] { "en", "ko" }, locales);
            Assert.AreEqual("greeting", scan.Entries[0].Key);
        }

        [Test]
        public void Csv_KeepsQuotedCommasAndNewlines()
        {
            // The row a naive split loses half of — and losing half a row means
            // a charset missing exactly the characters of the longest string.
            Write("ui.csv", "key,en\nline,\"one, two\"\nwrapped,\"first\nsecond\"\n");

            var scan = TextSourceScanner.Scan(new[] { _folder });

            Assert.AreEqual(2, scan.Entries.Count);
            Assert.AreEqual("one, two", scan.Entries[0].Value);
            Assert.AreEqual("first\nsecond", scan.Entries[1].Value);
        }

        [Test]
        public void Json_TakesLocaleFromTopLevelKeyOrFileName()
        {
            Write("strings.json", "{\"en\":{\"a\":\"Hello\"},\"th\":{\"a\":\"สวัสดี\"}}");
            Write("dialogue.ko.json", "{\"line\":\"안녕\"}");

            var scan = TextSourceScanner.Scan(new[] { _folder });

            var byLocale = new Dictionary<string, string>();
            foreach (var entry in scan.Entries) byLocale[entry.Locale] = entry.Value;
            Assert.AreEqual("Hello", byLocale["en"]);
            Assert.AreEqual("สวัสดี", byLocale["th"]);
            Assert.AreEqual("안녕", byLocale["ko"]);
        }

        [Test]
        public void Json_EscapesAndUnicodeSurviveTheReader()
        {
            Write("escapes.json", "{\"a\":\"line\\nbreak \\u0041 \\\"quoted\\\"\"}");

            var scan = TextSourceScanner.Scan(new[] { _folder });

            Assert.AreEqual(1, scan.Entries.Count);
            Assert.AreEqual("line\nbreak A \"quoted\"", scan.Entries[0].Value);
        }

        [Test]
        public void MalformedJson_IsReportedRatherThanGuessedAt()
        {
            Write("broken.json", "{\"a\": \"unterminated");

            var scan = TextSourceScanner.Scan(new[] { _folder });

            Assert.AreEqual(1, scan.Skipped.Count, "a file that does not parse must be reported");
        }

        [Test]
        public void Characters_AreDeduplicatedAndWhitespaceFree()
        {
            Write("ui.csv", "key,en\na,aab b\n");

            string characters = TextSourceScanner.Scan(new[] { _folder }).CharactersAsString();

            Assert.AreEqual("ab", characters);
        }

        [Test]
        public void LocaleCodes_AreNotConfusedWithKeys()
        {
            Assert.IsTrue(TextSourceScanner.IsLocaleCode("en"));
            Assert.IsTrue(TextSourceScanner.IsLocaleCode("ko-KR"));
            Assert.IsTrue(TextSourceScanner.IsLocaleCode("zh_Hans"));
            Assert.IsFalse(TextSourceScanner.IsLocaleCode("greeting"));
            Assert.IsFalse(TextSourceScanner.IsLocaleCode("description"));
        }

        // -------------------------------------------------------------- doctor

        [Test]
        public void Doctor_FindsCharactersNoFontCanDraw()
        {
            Write("ui.csv", "key,en\na,Hello\nb,안녕하세요\n");
            using var latin = Load(LatinFontPath);
            var stack = FontStack.Single(latin);

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }), stack);

            Assert.IsFalse(report.Passed, "Hangul in a Latin-only chain is tofu, not a warning");
            bool tofu = false;
            foreach (var finding in report.Findings)
                if (finding.Rule == "tofu") tofu = true;
            Assert.IsTrue(tofu);
        }

        [Test]
        public void Doctor_PassesWhenEveryCharacterIsCovered()
        {
            Write("ui.csv", "key,en\na,Hello world\n");
            using var latin = Load(LatinFontPath);

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }),
                FontStack.Single(latin));

            Assert.IsTrue(report.Passed, report.Summary());
        }

        [Test]
        public void Doctor_ReportsOneFindingPerMissingCharacter()
        {
            // Not one per string: a missing character is missing everywhere, and
            // a hundred findings that say the same thing bury the rest.
            Write("ui.csv", "key,en\na,안녕\nb,안녕\nc,안녕\n");
            using var latin = Load(LatinFontPath);

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }),
                FontStack.Single(latin));

            int tofu = 0;
            foreach (var finding in report.Findings) if (finding.Rule == "tofu") tofu++;
            Assert.AreEqual(2, tofu, "two distinct characters, three strings");
        }

        [Test]
        public void Doctor_WarnsWhenTwoHanLocalesShareAnUntaggedChain()
        {
            Write("ui.csv", "key,ja,zh-Hans\na,直火,直火\n");
            using var latin = Load(LatinFontPath);
            using var arabic = Load(ArabicFontPath);
            var stack = new FontStack();
            stack.Add(latin);
            stack.Add(arabic);

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }), stack);

            bool warned = false;
            foreach (var finding in report.Findings)
                if (finding.Rule == "han-unification") warned = true;
            Assert.IsTrue(warned,
                "ja and zh in one untagged chain is Han unification going wrong quietly");
        }

        [Test]
        public void Doctor_IsQuietAboutHanWhenTheChainIsTagged()
        {
            Write("ui.csv", "key,ja\na,直火\n");
            using var latin = Load(LatinFontPath);
            var stack = new FontStack();
            stack.Add(latin, "ja");

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }), stack);

            foreach (var finding in report.Findings)
                Assert.AreNotEqual("han-unification", finding.Rule);
        }

        [Test]
        public void Doctor_FlagsThaiWithNoDictionaryInstalled()
        {
            Write("ui.csv", "key,th\na,ค่าที่ตั้งไว้ในเกม\n");
            using var latin = Load(LatinFontPath);
            DictionaryLineBreaker.ClearAll();

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }),
                FontStack.Single(latin));

            bool missing = false;
            foreach (var finding in report.Findings)
                if (finding.Rule == "missing-dictionary") missing = true;
            Assert.IsTrue(missing);
            Assert.IsFalse(report.Passed, "Thai with no word list wraps at arbitrary characters");
        }

        [Test]
        public void Doctor_ReportsCoverageRatherThanPresence()
        {
            // The built-in starter list is present in every project and answers
            // a fraction of real text; presence alone would call that fine.
            Write("ui.csv", "key,th\na,ค่าที่ตั้งไว้ในเกมนี้ไม่เหมือนของคนอื่น\n");
            using var latin = Load(LatinFontPath);
            DictionaryLineBreaker.ResetToDefaults();

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { _folder }),
                FontStack.Single(latin));

            bool coverage = false;
            foreach (var finding in report.Findings)
                if (finding.Rule == "dictionary-coverage") coverage = true;
            Assert.IsTrue(coverage, "a dictionary that is installed still has to be measured");
        }

        // ------------------------------------------------------------- gallery

        [Test]
        public void Gallery_FlagsAStringTooWideForItsBox()
        {
            using var latin = Load(LatinFontPath);

            var entries = new List<TextEntry>
            {
                new TextEntry { Key = "short", Value = "Hi", Locale = "en" },
                new TextEntry { Key = "long", Value = "A considerably longer label than the box", Locale = "en" },
            };
            var options = GalleryOptions.Default;
            options.BoxWidth = 90f;
            // Tall enough that this is a test about width: one line of 28px
            // text is about 34px, so a 30px box would call everything an
            // overflow and the assertion would pass for the wrong reason.
            options.BoxHeight = 100f;
            options.Wrap = TextWrap.NoWrap;

            var cells = MeasureWith(latin, entries, options);

            Assert.IsFalse(cells[0].Overflow, "'Hi' fits a 90px box");
            Assert.IsTrue(cells[1].Overflow, "a long label in a 90px box overflows");
        }

        [Test]
        public void Gallery_CountsCharactersNoFontCanDraw()
        {
            using var latin = Load(LatinFontPath);
            var entries = new List<TextEntry>
            {
                new TextEntry { Key = "ko", Value = "안녕", Locale = "ko" },
            };

            var cells = MeasureWith(latin, entries, GalleryOptions.Default);

            Assert.AreEqual(2, cells[0].MissingGlyphs);
            Assert.IsFalse(cells[0].Ok);
        }

        /// <summary>
        /// The gallery resolves its own stack from the project settings, which a
        /// test project does not have; this measures with an explicit font by
        /// laying out the same way the gallery does.
        /// </summary>
        private static List<GalleryCell> MeasureWith(FontData font, List<TextEntry> entries,
            in GalleryOptions options)
        {
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            OneTextSettings.Instance = settings;
            try
            {
                var cells = new List<GalleryCell>();
                var engine = new TextLayoutEngine();
                var result = new TextLayoutResult();
                var stack = FontStack.Single(font);

                foreach (var entry in entries)
                {
                    var layoutSettings = TextLayoutSettings.Default(stack, options.FontSize);
                    layoutSettings.MaxWidth = options.BoxWidth;
                    layoutSettings.Wrap = options.Wrap;
                    engine.Layout(entry.Value, layoutSettings, result);

                    int missing = 0;
                    foreach (int codepoint in TextDoctor.Codepoints(entry.Value))
                        if (!stack.Covers(codepoint)) missing++;

                    cells.Add(new GalleryCell
                    {
                        Entry = entry,
                        Width = result.Width,
                        Height = result.Height,
                        LineCount = result.Lines.Count,
                        Overflow = result.Width > options.BoxWidth + 0.5f ||
                                   result.Height > options.BoxHeight + 0.5f,
                        MissingGlyphs = missing,
                    });
                }
                return cells;
            }
            finally
            {
                OneTextSettings.Instance = null;
                Object.DestroyImmediate(settings);
            }
        }

        // ----------------------------------------------------------- forensics

        [Test]
        public void LineBreaker_NamesTheRuleThatDecided()
        {
            // A space before the boundary is LB18, and it is the answer to
            // nineteen questions in twenty about why a line broke.
            Assert.AreEqual("LB18", LineBreaker.RuleAt("hello world", 6));

            // Between two ideographs nothing forbids a break: the default rule.
            Assert.AreEqual("LB31", LineBreaker.RuleAt("日本語", 1));

            // An exclamation mark cannot be separated from what it follows.
            Assert.AreEqual("LB13", LineBreaker.RuleAt("hi!", 2));

            // A full stop is an infix separator, not a closing mark: LB15d,
            // which is the sort of thing this feature exists to settle.
            Assert.AreEqual("LB15d", LineBreaker.RuleAt("end.", 3));
        }

        [Test]
        public void LineBreaker_RuleNamesDoNotChangeTheDecisions()
        {
            // The refactor that added rule names touched every return in the
            // rule cascade; this is the cheap guard beside the UCD suite.
            const string text = "The quick (brown) fox 日本語です。 x-ray 1,234.5";
            var opportunities = LineBreaker.Analyze(text);

            for (int i = 1; i < text.Length; i++)
            {
                string rule = LineBreaker.RuleAt(text, i);
                Assert.IsNotNull(rule, $"no rule reported for the boundary at {i}");
                Assert.IsTrue(rule.StartsWith("LB"), rule);
            }
            Assert.AreEqual(LineBreaker.Opportunity.Allowed, opportunities[4]);
        }

        [Test]
        public void Forensics_ReportsTheFontAndClusterOfEveryGlyph()
        {
            using var latin = Load(LatinFontPath);
            var stack = FontStack.Single(latin);
            var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();
            var settings = TextLayoutSettings.Default(stack, 32f);
            settings.MaxWidth = 400f;
            const string text = "fine wine";
            engine.Layout(text, settings, result);

            var reports = GlyphForensics.Inspect(text, result, stack);

            Assert.IsNotEmpty(reports);
            foreach (var report in reports)
            {
                Assert.IsNotEmpty(report.Characters);
                Assert.GreaterOrEqual(report.TextStart, 0);
                Assert.LessOrEqual(report.TextStart + report.TextLength, text.Length);
            }
        }

        [Test]
        public void Forensics_KnowsWhichFeaturesAFaceOffers()
        {
            using var latin = Load(LatinFontPath);

            var features = latin.LayoutFeatures(true);

            Assert.IsNotEmpty(features, "Noto Sans registers GSUB features");
            foreach (string feature in features)
                Assert.AreEqual(4, feature.Length, $"'{feature}' is not an OpenType tag");
        }

        // -------------------------------------------------------------- atlas

        [Test]
        public void Atlas_SeparatesPrewarmedTilesFromRuntimeOnes()
        {
            using var latin = Load(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings
            {
                TextureSize = 512,
                LayerCount = 2,
            });

            var stack = FontStack.Single(latin);
            AtlasPrewarm.Warm(atlas, stack, new[] { 'A', 'B', 'C' }.ToCodepoints(), new[] { 32f });
            var afterPrewarm = atlas.GetStats();

            atlas.GetOrAdd(latin, latin.NominalGlyph('Z'), 32f);
            var afterDrawing = atlas.GetStats();

            Assert.Greater(afterPrewarm.PrewarmedTiles, 0, "the prewarm baked nothing");
            Assert.AreEqual(0, afterPrewarm.RuntimeTiles);
            Assert.AreEqual(afterPrewarm.PrewarmedTiles, afterDrawing.PrewarmedTiles,
                "drawing must not relabel prewarmed tiles");
            Assert.AreEqual(1, afterDrawing.RuntimeTiles);
            Assert.AreEqual(afterDrawing.UsedPixels,
                afterDrawing.PrewarmedPixels + afterDrawing.RuntimePixels,
                "the pie has to sum to the occupancy");
        }

        [Test]
        public void Atlas_CountsDemandOncePerTileHoweverOftenItIsRebaked()
        {
            using var latin = Load(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings
            {
                TextureSize = 256,
                LayerCount = 1,
            });

            uint glyph = latin.NominalGlyph('A');
            atlas.GetOrAdd(latin, glyph, 32f);
            var first = atlas.GetStats();
            atlas.GetOrAdd(latin, glyph, 32f);
            var second = atlas.GetStats();

            Assert.AreEqual(1, first.DemandTiles);
            Assert.AreEqual(first.DemandTiles, second.DemandTiles,
                "a cache hit is not new demand");
            Assert.AreEqual(first.DemandPixels, second.DemandPixels);
        }

        // --------------------------------------------------------- dictionary

        [Test]
        public void DictionaryAsset_RoundTripsAndInstalls()
        {
            var asset = ScriptableObject.CreateInstance<OneTextDictionary>();
            try
            {
                asset.Initialize("กิน\nนอน\n# a comment\nเล่น\t1000\n", "Thai", "thaidict.txt", "ICU");

                Assert.AreEqual(3, asset.WordCount, "comments and weights are not words");
                StringAssert.Contains("กิน", asset.GetText());
                Assert.Less(asset.StoredSize, asset.SourceSize + 1);

                DictionaryLineBreaker.ClearAll();
                asset.Install();
                var words = DictionaryLineBreaker.GetWordList("Thai");

                Assert.IsNotNull(words);
                Assert.AreEqual(3, words.WordCount);
                Assert.AreEqual(3, words.LongestMatch("กิน", 0, 3));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DictionaryCoverage_RisesWhenTheFullListArrives()
        {
            const string sample = "ค่าที่ตั้งไว้";
            var starter = new WordList();
            starter.AddAll("ค่า");
            var full = new WordList();
            full.AddAll("ค่า\nที่\nตั้ง\nไว้");

            float before = starter.Coverage(sample);
            float after = full.Coverage(sample);

            Assert.Less(before, after);
            Assert.AreEqual(1f, after, 0.001f, "every word in the sample is in the full list");
        }
    }

    internal static class CodepointExtensions
    {
        /// <summary>Characters as code points, for the prewarm calls.</summary>
        public static List<int> ToCodepoints(this char[] characters)
        {
            var result = new List<int>(characters.Length);
            foreach (char c in characters) result.Add(c);
            return result;
        }
    }
}
