using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Editor;

namespace OneText.Tests
{
    /// <summary>
    /// The last tier of fallback: a character no font in the project covers,
    /// drawn from a font the operating system has.
    ///
    /// These tests are about a machine's own fonts, which is an awkward thing
    /// to assert against, so they assert the shape of the behaviour rather
    /// than the name of a face. Hangul is the probe because the repository's
    /// test fonts genuinely lack it (NotoSans.ttf covers Latin, Greek,
    /// Cyrillic and Devanagari, and stops) while every desktop and mobile
    /// platform ships something that has it. Where the machine has nothing,
    /// each test says so and is inconclusive rather than green: a fallback
    /// test that silently checks nothing is worse than no test.
    /// </summary>
    public class SystemFontTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        /// <summary>U+D55C HANGUL SYLLABLE HAN: absent from every bundled test font.</summary>
        private const int Hangul = 0xD55C;

        /// <summary>U+AE00 HANGUL SYLLABLE GEUL: the same family, for the sharing test.</summary>
        private const int Hangul2 = 0xAE00;

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        [SetUp]
        public void EnableTier()
        {
            // Explicit rather than inherited: the tier follows the project
            // setting by default, and a test that reads the project's opinion
            // is a test whose result depends on the project.
            SystemFonts.Enabled = true;
            SystemFonts.Forget();
        }

        [TearDown]
        public void RestoreTier()
        {
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
        }

        private static void RequireASystemFont()
        {
            if (SystemFonts.Resolve(Hangul) == null)
                Assert.Ignore("no font on this machine has U+D55C; nothing to test the tier against");
        }

        // ------------------------------------------------------------ resolution

        [Test]
        public void A_Character_The_Chain_Misses_Comes_Back_From_The_System()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);

            Assert.IsFalse(fonts.Covers(Hangul), "the test font must not cover Hangul");

            var resolved = fonts.Resolve(Hangul);

            Assert.IsNotNull(resolved);
            Assert.AreNotSame(latin, resolved, "the chain's only font cannot draw it");
            Assert.IsTrue(resolved.HasGlyph(Hangul), "the face returned must actually have the glyph");
            Assert.IsTrue(SystemFonts.IsSystemFont(resolved));
            Assert.IsNotEmpty(SystemFonts.NameOf(resolved), "a finding has to be able to name the face");
        }

        [Test]
        public void Covers_Still_Means_The_Project_Chain_Alone()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);

            // The whole reporting story rests on this: the renderer may reach
            // past the chain, and the question "does this project ship a font
            // for it" must keep its old answer.
            Assert.IsFalse(fonts.Covers(Hangul));
            Assert.IsNotNull(fonts.Resolve(Hangul));
        }

        [Test]
        public void Switched_Off_The_Character_Is_A_Box_Again()
        {
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);

            SystemFonts.Enabled = false;
            var resolved = fonts.Resolve(Hangul);

            Assert.AreSame(latin, resolved, "with the tier off the primary font draws the notdef");
            Assert.IsFalse(resolved.HasGlyph(Hangul));

            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(resolved, "한", glyphs);
            Assert.AreEqual(1, glyphs.Count);
            Assert.AreEqual(0u, glyphs[0].GlyphId, "the old behaviour is .notdef, and must survive");
        }

        [Test]
        public void The_System_Face_Shapes_A_Real_Glyph()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);
            var font = fonts.Resolve(Hangul);

            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "한", glyphs);

            Assert.Greater(glyphs.Count, 0);
            Assert.AreNotEqual(0u, glyphs[0].GlyphId, "a system face that shapes to .notdef is no better than tofu");
            Assert.Greater(glyphs[0].XAdvance, 0);
        }

        // ---------------------------------------------------------------- caching

        [Test]
        public void The_Answer_Is_Remembered()
        {
            RequireASystemFont();
            var first = SystemFonts.Resolve(Hangul);
            int loaded = SystemFonts.LoadedFaceCount;
            var second = SystemFonts.Resolve(Hangul);

            Assert.AreSame(first, second, "the second ask must not reparse a font");
            Assert.AreEqual(loaded, SystemFonts.LoadedFaceCount);
        }

        [Test]
        public void Two_Characters_Of_One_Script_Share_One_Face()
        {
            RequireASystemFont();
            var first = SystemFonts.Resolve(Hangul);
            var second = SystemFonts.Resolve(Hangul2);

            Assert.IsNotNull(second);
            Assert.AreSame(first, second,
                "two Hangul syllables must come from one parse and one set of atlas tiles");
        }

        [Test]
        public void A_Stack_Answers_Without_Asking_Twice()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);

            var first = fonts.ResolveFromSystem(Hangul);
            var second = fonts.ResolveFromSystem(Hangul);

            Assert.AreSame(first, second);
        }

        [Test]
        public void A_Font_Added_Later_Wins_Over_The_System()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = new FontStack();
            fonts.Add(latin);
            var system = fonts.Resolve(Hangul);
            Assert.IsTrue(SystemFonts.IsSystemFont(system));

            // A project that fixes the warning by adding a font must see the
            // fix immediately, not after a domain reload.
            var hangul = system;
            fonts.Add(hangul);

            Assert.IsTrue(fonts.Covers(Hangul), "the added font is now part of the chain");
            Assert.AreSame(hangul, fonts.Resolve(Hangul));
        }

        // ----------------------------------------------------------------- layout

        [Test]
        public void A_Mixed_String_Splits_Into_Two_Runs_And_Draws_Both()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();

            engine.Layout("A한", TextLayoutSettings.Default(fonts, 32f), result);

            Assert.AreEqual(2, result.Glyphs.Count);
            foreach (var glyph in result.Glyphs)
                Assert.AreNotEqual(0u, glyph.GlyphId, "no tofu anywhere in the line");
            Assert.AreEqual(2, result.Runs.Count,
                "the itemizer splits per font, and the system face is a font like any other");
        }

        [Test]
        public void A_System_Face_Stands_Upright_In_A_Column()
        {
            RequireASystemFont();
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.WritingMode = TextWritingMode.VerticalRightToLeft;
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();

            // Vertical layout asks the font questions (vertical forms,
            // vmtx metrics, UAX #50 orientation), and none of them may assume
            // the font came from a OneFontAsset.
            engine.Layout("한글", settings, result);

            Assert.AreEqual(2, result.Glyphs.Count);
            foreach (var glyph in result.Glyphs) Assert.AreNotEqual(0u, glyph.GlyphId);
            Assert.Greater(result.Height, 0f, "a column of Hangul has height");
        }

        // ----------------------------------------------------------------- doctor

        private string _folder;

        private string Strings(string contents)
        {
            _folder ??= Path.Combine(Path.GetTempPath(), "OneTextSystemFontTests",
                Path.GetRandomFileName());
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "ui.csv"), contents, System.Text.Encoding.UTF8);
            return _folder;
        }

        [TearDown]
        public void RemoveStrings()
        {
            if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
            _folder = null;
        }

        [Test]
        public void Doctor_Warns_And_Names_The_Face_That_Caught_It()
        {
            RequireASystemFont();
            string folder = Strings("key,ko\na,한\n");
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { folder }), fonts);

            DoctorFinding found = default;
            bool any = false;
            foreach (var finding in report.Findings)
                if (finding.Rule == "system-fallback") { found = finding; any = true; }

            Assert.IsTrue(any, "a character only the OS draws must be reported");
            Assert.AreEqual(DoctorSeverity.Warning, found.Severity,
                "the build renders, so this is advice and not a failed merge");
            Assert.AreEqual("한", found.Sample);
            StringAssert.Contains(SystemFonts.NameFor(0xD55C), found.Message,
                "the finding has to name the font this machine supplied");
            Assert.IsTrue(report.Passed, "warnings do not fail CI; only tofu does");
        }

        [Test]
        public void Doctor_Falls_Back_To_The_Tofu_Error_When_The_Tier_Is_Off()
        {
            string folder = Strings("key,ko\na,한\n");
            using var latin = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(latin);
            SystemFonts.Enabled = false;

            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { folder }), fonts);

            bool tofu = false;
            foreach (var finding in report.Findings)
            {
                if (finding.Rule == "system-fallback") Assert.Fail("the tier is off; nothing resolves");
                if (finding.Rule == "tofu") tofu = true;
            }
            Assert.IsTrue(tofu);
            Assert.IsFalse(report.Passed, "an unrenderable character still fails the merge");
        }

        // ------------------------------------------------------------------ index

        [Test]
        public void The_Platform_Has_Somewhere_To_Look()
        {
            // Not an assertion about a particular machine: on a platform with
            // no font directory (Web), the tier is a documented no-op, and
            // this records which of the two this machine is.
            var directories = new List<string>(SystemFonts.Directories());
            UnityEngine.Debug.Log($"[system fonts] {directories.Count} director(y/ies), " +
                                  $"{SystemFonts.FontFileCount} font file(s)");
            Assert.Pass();
        }
    }
}
