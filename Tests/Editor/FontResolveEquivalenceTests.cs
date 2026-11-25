using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// The stack's lookups were made cheaper — an open-addressed coverage
    /// table instead of a dictionary, one look at the settings per miss instead
    /// of three — and that is only worth having if no character changes font.
    /// This runs the switches off and on over the same characters, styles,
    /// presentations and languages and compares the faces.
    ///
    /// <para>The system tier sits under the chain, and system fonts differ from
    /// machine to machine, so nothing here names a file: the assertion is "the
    /// same answer both ways", whatever the answer is on this machine.</para>
    /// </summary>
    public class FontResolveEquivalenceTests
    {
        private const string Coverage = "Packages/com.onetext.core/Tests/CoverageFonts~/";
        private const string Fonts = "Packages/com.onetext.core/Tests/Fonts~/";
        private const string ArabicKey = "Fonts/Arabic";

        private sealed class Source : IFontSource
        {
            public readonly Dictionary<string, OneFontAsset> Assets = new Dictionary<string, OneFontAsset>();
            public void Load(string key, Action<OneFontAsset> done) => done(LoadNow(key));
            public OneFontAsset LoadNow(string key) => Assets.TryGetValue(key, out var a) ? a : null;
            public void Release(string key, OneFontAsset font) { }
        }

        private readonly List<FontData> _loaded = new List<FontData>();
        private readonly List<Object> _created = new List<Object>();
        private OneTextSettings _previousSettings;

        [SetUp]
        public void SetUp()
        {
            SystemFonts.Enabled = true;
            SystemFonts.Forget();
            _previousSettings = OneTextSettings.Instance;
        }

        [TearDown]
        public void TearDown()
        {
            FontStack.FastLookup = true;
            FontResidency.ReadSettingsOnce = true;
            OneTextSettings.CheckSearchedFirst = true;
            SystemFonts.IndexInBackground = true;
            SystemFonts.ShareCollectionCmaps = true;
            SystemFontIndex.FaceCoverage.MergeRanges = true;
            FontResidency.ResetForTests();
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
            foreach (var font in _loaded) font.Dispose();
            _loaded.Clear();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
        }

        private FontData Load(string path)
        {
            var font = FontData.LoadFile(Path.GetFullPath(path));
            _loaded.Add(font);
            return font;
        }

        /// <summary>
        /// A spread of Unicode: every 29th codepoint through the supplementary
        /// planes, which lands in every script block the coverage fonts have,
        /// plus the cases that take their own paths — unassigned kana (the
        /// character that once cost a walk of the disk), emoji, keycap digits,
        /// regional indicators, CJK punctuation, Hangul, a private-use point.
        /// </summary>
        private static List<int> Sample()
        {
            var sample = new List<int>();
            for (int cp = 0x20; cp < 0x32000; cp += 29) sample.Add(cp);
            sample.AddRange(new[]
            {
                'A', 'z', '1', '#', 0x00E9, 0x0644, 0x05D0, 0x0E01, 0x0915, 0x3097, 0x3098, 0x3041, 0x30A2,
                0x30FB, 0x3001, 0x3002, 0xFF0C, 0x300C, 0x76F4, 0x9AA8, 0x6F22, 0xD55C, 0xAE00, 0x1F600,
                0x1F44D, 0x2764, 0x2714, 0x263A, 0x1F1F0, 0x1F1F7, 0x00A9, 0x2122, 0xE000, 0x20BB7, 0xFFFD,
            });
            return sample;
        }

        // ------------------------------------------------------- the stack

        [Test]
        public void The_Stack_Picks_The_Same_Face_For_Every_Character_With_Its_Fast_Paths_On_Or_Off()
        {
            // A project chain the way a global game has one: Latin, Arabic,
            // colour emoji, and CJK faces tagged by language, plus a variable
            // face so bold and italic are instanced; an on-demand Arabic face
            // in the settings; and the system tier under all of it.
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            var arabic = ScriptableObject.CreateInstance<OneFontAsset>();
            _created.Add(arabic);
            arabic.Initialize(File.ReadAllBytes(Path.GetFullPath(Fonts + "NotoSansArabic.ttf")), "Arabic",
                Fonts + "NotoSansArabic.ttf");
            arabic.Language = "ar";
            var coverage = FontCoverage.Of(arabic.Font);
            arabic.Unload();
            settings.SetOnDemandFonts(new[] { new OneFontReference(ArabicKey, "ar", coverage) });
            OneTextSettings.Instance = settings;
            FontResidency.ResetForTests();
            var source = new Source();
            source.Assets[ArabicKey] = arabic;
            FontResidency.Source = source;
            FontResidency.LoadOnDemand = true;

            var latin = Load(Fonts + "NotoSansVariable.ttf");
            var emoji = Load(Coverage + "NotoColorEmoji.ttf");
            var jp = Load(Coverage + "NotoSansCJKjp-Regular.otf");
            var kr = Load(Coverage + "NotoSansCJKkr-Regular.otf");
            var sc = Load(Coverage + "NotoSansCJKsc-Regular.otf");

            FontStack Build()
            {
                var stack = new FontStack { UseOnDemandFonts = true };
                stack.Add(latin);
                stack.Add(emoji);
                stack.Add(kr, "ko");
                stack.Add(jp, "ja");
                stack.Add(sc, "zh-Hans");
                return stack;
            }

            using var slow = Build();
            using var fast = Build();
            var sample = Sample();
            string[] languages = { null, "ja", "zh-Hans", "ko", "ar" };
            var presentations = new[]
                { FontStack.Presentation.Any, FontStack.Presentation.Text, FontStack.Presentation.Emoji };

            int compared = 0;
            // Twice over: the first pass fills every cache, the second answers
            // from them, and both have to agree.
            for (int pass = 0; pass < 2; pass++)
            foreach (int cp in sample)
            foreach (string language in languages)
            foreach (var presentation in presentations)
            for (int face = 0; face < 4; face++)
            {
                bool bold = (face & 1) != 0, italic = (face & 2) != 0;
                SetFast(false);
                var expected = slow.Resolve(cp, bold, italic, presentation, language);
                SetFast(true);
                var actual = fast.Resolve(cp, bold, italic, presentation, language);
                compared++;
                if (ReferenceEquals(expected, actual)) continue;
                // The two stacks instance their own bold and italic faces, so
                // a styled answer is compared by what it was made from.
                Assert.AreEqual(Describe(slow, expected), Describe(fast, actual),
                    $"U+{cp:X4} bold={bold} italic={italic} {presentation} lang={language ?? "none"}");
            }
            Assert.Greater(compared, 100000);
        }

        private static void SetFast(bool on)
        {
            FontStack.FastLookup = on;
            FontResidency.ReadSettingsOnce = on;
            OneTextSettings.CheckSearchedFirst = on;
        }

        // A face's identity across two stacks that share their regular fonts:
        // the regular itself, or which family's which instance it is.
        private static string Describe(FontStack stack, FontData font)
        {
            if (font == null) return "null";
            for (int i = 0; i < stack.Count; i++)
            {
                var regular = stack.Fonts[i];
                if (regular == font) return "regular " + i;
                foreach (var face in new[] { FontStack.Face.Bold, FontStack.Face.Italic, FontStack.Face.BoldItalic })
                    if (stack.TryGetStyled(regular, face, out var styled) && styled == font)
                        return face + " of " + i;
            }
            return "other " + font.CacheId;
        }
    }
}
