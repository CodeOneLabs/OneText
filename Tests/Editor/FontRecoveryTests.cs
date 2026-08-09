using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OneText.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// The half of the TextMesh Pro migration that runs after it has failed.
    ///
    /// A project whose font packs shipped baked atlases and no font files has
    /// nothing to convert, and the interesting behaviour is entirely about what
    /// is left behind: an asset per missing font rather than a null per label, a
    /// list that is as long as the number of files to find rather than the
    /// number of labels waiting for them, and a catalogue that would rather say
    /// "I do not know this font" than install the wrong one.
    ///
    /// Nothing here touches the network. The catalogue is tested on what it
    /// claims and on what it refuses, and the download is tested on the step
    /// that matters — deciding whether the bytes that came back are a font —
    /// which is separated from the request for exactly this reason.
    /// </summary>
    public class FontRecoveryTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string VariableFontPath =
            "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        /// <summary>A font with CFF outlines and no axes — the static case, and the OTTO one.</summary>
        private const string StaticFontPath = "Packages/com.onetext.core/Tests/Fonts~/CffShapes.otf";

        private readonly List<Object> _made = new List<Object>();
        private OneTextSettings _settings;
        private bool _settingsSwapped;

        [TearDown]
        public void TearDown()
        {
            if (_settingsSwapped)
            {
                OneTextSettings.Instance = _settings;
                _settingsSwapped = false;
            }
            foreach (var made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
        }

        private T New<T>() where T : ScriptableObject
        {
            var made = ScriptableObject.CreateInstance<T>();
            _made.Add(made);
            return made;
        }

        private static string FullPath(string packagePath) => Path.GetFullPath(packagePath);

        private static OneFontRecovery CairoBoldFacts() => new OneFontRecovery
        {
            ExpectedFileName = "Cairo-Bold.ttf",
            RecoveredFrom = "Assets/Fonts/Cairo-Bold SDF.asset",
            StyleName = "Bold",
            PointSize = 90f,
            Padding = 9,
            AtlasWidth = 1024,
            AtlasHeight = 1024,
            CharacterCount = 231,
            CharacterRanges = "0020-007E,00A0-00FF",
        };

        // ------------------------------------------------------ placeholders

        [Test]
        public void Placeholder_Carries_Everything_But_The_Font()
        {
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo", CairoBoldFacts());

            Assert.IsTrue(placeholder.IsPlaceholder, "a placeholder must know it is one");
            Assert.AreEqual("Cairo", placeholder.FamilyName);
            Assert.IsNull(placeholder.GetFontBytes(), "a placeholder must have no font in it");
            Assert.AreEqual(0, placeholder.StoredSize);

            var recovery = placeholder.Recovery;
            Assert.AreEqual("Cairo-Bold.ttf", recovery.ExpectedFileName,
                "the whole point is knowing which file to look for");
            Assert.AreEqual("Assets/Fonts/Cairo-Bold SDF.asset", recovery.RecoveredFrom);
            Assert.AreEqual(90f, recovery.PointSize, 1e-4f);
            Assert.AreEqual(9, recovery.Padding);
            Assert.AreEqual(1024, recovery.AtlasWidth);
            Assert.AreEqual(231, recovery.CharacterCount);
            Assert.AreEqual("0020-007E,00A0-00FF", recovery.CharacterRanges);
        }

        [Test]
        public void Filling_A_Placeholder_Turns_It_Into_An_Ordinary_Font_Asset()
        {
            // The round trip every label pointing at the placeholder depends on:
            // one file, dropped once, and nothing else re-assigned anywhere.
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo", CairoBoldFacts());

            Assert.IsTrue(FontRecovery.Fill(placeholder, FullPath(LatinFontPath)),
                "the font file could not be read into the placeholder");

            Assert.IsFalse(placeholder.IsPlaceholder, "a filled placeholder is a font asset");

            var original = File.ReadAllBytes(FullPath(LatinFontPath));
            Assert.AreEqual(original, placeholder.GetFontBytes(),
                "the font that came out is not the font that went in");

            Assert.AreEqual("Cairo-Bold.ttf", placeholder.Recovery.ExpectedFileName,
                "filling a placeholder should keep the record of where it came from");
        }

        [Test]
        public void An_Unfilled_Placeholder_Draws_With_The_Project_Default()
        {
            // The migration points thousands of labels at these. If a fontless
            // placeholder answered null, every one of those labels would render
            // nothing — strictly worse than the empty field it replaced, which
            // at least fell through to the project default.
            var real = New<OneFontAsset>();
            real.Initialize(File.ReadAllBytes(FullPath(LatinFontPath)), "Noto Sans", LatinFontPath);

            var settings = New<OneTextSettings>();
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("_defaultFont").objectReferenceValue = real;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.NotNull(settings.DefaultFont, "the test could not set a project default font");

            _settings = OneTextSettings.Instance;
            _settingsSwapped = true;
            OneTextSettings.Instance = settings;

            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo", CairoBoldFacts());

            LogAssert.Expect(LogType.Warning, new Regex("has no font file yet"));
            Assert.AreSame(real.Font, placeholder.Font,
                "an unfilled placeholder should stand in with the project default");
            Assert.IsTrue(placeholder.IsPlaceholder,
                "standing in is not the same as being filled, and the report says so");
        }

        [Test]
        public void An_Unfilled_Placeholder_With_No_Default_Is_Empty_Rather_Than_Angry()
        {
            var settings = New<OneTextSettings>();
            _settings = OneTextSettings.Instance;
            _settingsSwapped = true;
            OneTextSettings.Instance = settings;

            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo", CairoBoldFacts());

            LogAssert.Expect(LogType.Warning, new Regex("has no font file yet"));
            Assert.IsNull(placeholder.Font,
                "with nothing to stand in with, the answer is the same null a missing font " +
                "always was");
        }

        // ---------------------------------------------------------- manifest

        private static MigrationFinding Missing(string fontAssetName, string container) =>
            new MigrationFinding
            {
                Severity = DoctorSeverity.Error,
                Rule = FontRecovery.Rule,
                // Word for word what TmpMigrationProvider says, because that is
                // the only thing linking a finding to the font it is about.
                Message = $"the font asset '{fontAssetName}' has no usable source font file. " +
                          "OneText rasterises from the .ttf/.otf, not from a baked atlas, so " +
                          "there is nothing to convert.",
                Container = container,
                Component = "TextMeshProUGUI",
            };

        [Test]
        public void The_Manifest_Deduplicates_By_Source_Font_Not_By_Font_Asset()
        {
            // The real case this was built for: one project, one Cairo-Bold.ttf
            // nobody has, and three font assets baked from it at different sizes
            // scattered over two scenes. The user has one file to find, and a
            // list of three would be a list of the wrong thing.
            var report = new MigrationReport();
            report.Add(Missing("Cairo-Bold SDF", "Assets/Scenes/Menu.unity"));
            report.Add(Missing("Cairo-Bold SDF 48", "Assets/Scenes/Menu.unity"));
            report.Add(Missing("Cairo-Bold Outline SDF", "Assets/Prefabs/Hud.prefab"));
            report.Add(Missing("Cairo-Regular SDF", "Assets/Scenes/Menu.unity"));

            var manifest = FontRecovery.Collect(report);

            Assert.AreEqual(2, manifest.Count,
                "four font assets baked from two files are two fonts to find");

            var bold = manifest.ForFontAsset("Cairo-Bold SDF");
            Assert.NotNull(bold, "the font asset was not mapped to an entry");
            Assert.AreSame(bold, manifest.ForFontAsset("Cairo-Bold SDF 48"),
                "two bakes of one file must share an entry");
            Assert.AreSame(bold, manifest.ForFontAsset("Cairo-Bold Outline SDF"));
            Assert.AreNotSame(bold, manifest.ForFontAsset("Cairo-Regular SDF"),
                "bold and regular are different files and different work");

            Assert.AreEqual("Cairo", bold.FamilyName);
            Assert.AreEqual("Bold", bold.StyleName);
            Assert.AreEqual("Cairo-Bold.ttf", bold.ExpectedFileName);
            Assert.AreEqual(3, bold.FontAssets.Count, "every font asset should be named");
            Assert.AreEqual(3, bold.LabelCount, "three components are waiting on this one file");
            Assert.AreEqual(2, bold.Containers.Count, "in two containers");
            CollectionAssert.Contains(bold.Containers, "Assets/Prefabs/Hud.prefab");
        }

        [Test]
        public void Collecting_Twice_Reports_The_Project_Once()
        {
            // The Hub collects after the scan to show what is coming and again
            // after the apply to pick up the placeholders. Counting the labels
            // twice would make the second screen a lie.
            var report = new MigrationReport();
            report.Add(Missing("Sen-Regular SDF", "Assets/Scenes/Menu.unity"));
            report.Add(Missing("Sen-Regular SDF", "Assets/Scenes/Menu.unity"));

            FontRecovery.Collect(report);
            var manifest = FontRecovery.Collect(report);

            Assert.AreEqual(1, manifest.Count);
            Assert.AreEqual(2, manifest.Entries[0].LabelCount);
            Assert.AreEqual(1, manifest.Entries[0].Containers.Count);
        }

        [Test]
        public void A_Finding_About_A_File_Is_Not_A_Font_To_Recover()
        {
            // The same rule fires when a font file is present and unreadable.
            // That is a different problem and must not turn into a placeholder
            // for a font called "Fonts/Broken.ttf".
            Assert.IsNull(FontRecovery.NamedFont(
                "no OneText font asset could be made from 'Assets/Fonts/Broken.ttf'."));
            Assert.IsNull(FontRecovery.NamedFont("no font asset was assigned."));
            Assert.AreEqual("Cairo-Bold SDF",
                FontRecovery.NamedFont(Missing("Cairo-Bold SDF", null).Message));
        }

        [Test]
        public void Font_Asset_Names_Split_Into_A_Family_And_A_Style()
        {
            FontRecovery.ParseFace("Cairo-Bold SDF 32", out string family, out string style);
            Assert.AreEqual("Cairo", family);
            Assert.AreEqual("Bold", style);

            FontRecovery.ParseFace("JosefinSans SDF", out family, out style);
            Assert.AreEqual("Josefin Sans", family, "a run-together name is still two words");
            Assert.IsNull(style);

            FontRecovery.ParseFace("LINESeedSans", out family, out style);
            Assert.AreEqual("LINE Seed Sans", family, "an acronym is one word, not four letters");

            FontRecovery.ParseFace("어그로OTF", out family, out style);
            Assert.AreEqual("어그로", family, "a file extension in the name is noise in any script");

            // A family whose name starts with a weight word has to survive: only
            // the tail of a name is ever stripped.
            FontRecovery.ParseFace("Black Han Sans SDF", out family, out style);
            Assert.AreEqual("Black Han Sans", family);
            Assert.IsNull(style);
        }

        // --------------------------------------------------------- catalogue

        [Test]
        public void The_Catalogue_Answers_With_A_Licence_Before_Anything_Is_Fetched()
        {
            var cairo = FontSourceCatalog.Match("Cairo");

            Assert.AreEqual(FontSourceMatch.Download, cairo.Match);
            Assert.AreEqual("Cairo", cairo.FamilyName);
            Assert.IsTrue(cairo.DownloadUrl.StartsWith("https://"),
                "nothing in the catalogue may be fetched over plain http");
            Assert.AreEqual("SIL Open Font License 1.1", cairo.LicenceName);
            Assert.IsTrue(cairo.LicenceUrl.StartsWith("https://"),
                "the licence has to be readable before the download, not after");
            Assert.IsNotEmpty(cairo.LicenceFileUrl, "the licence file ships beside the font");
            Assert.IsNotEmpty(cairo.Note, "a variable font standing in for four faces is worth saying");

            // Spelling is not identity: these are the same font.
            Assert.AreEqual(FontSourceCatalog.Match("JosefinSans").DownloadUrl,
                FontSourceCatalog.Match("Josefin Sans").DownloadUrl);
        }

        [Test]
        public void A_Near_Miss_Is_A_Miss()
        {
            // Cairo Play is its own family. Installing Cairo for it would look
            // like success and change every glyph on the screen, which is the
            // one failure this catalogue exists to avoid.
            var play = FontSourceCatalog.Match("Cairo Play");
            Assert.AreEqual(FontSourceMatch.None, play.Match, "'Cairo Play' is not 'Cairo'");
            Assert.IsNull(play.DownloadUrl);
            Assert.IsNotEmpty(play.Note, "an unmatched font still has to say what to do");

            Assert.AreEqual(FontSourceMatch.None,
                FontSourceCatalog.Match("Noto Sans KR Display").Match,
                "a longer name is a different font");
            Assert.AreEqual(FontSourceMatch.None, FontSourceCatalog.Match("LayerLab GUI Pro").Match,
                "fonts from an asset pack are nobody's to redistribute");
            Assert.AreEqual(FontSourceMatch.None, FontSourceCatalog.Match("어그로").Match,
                "a font whose licence this list cannot vouch for is not offered");
            Assert.AreEqual(FontSourceMatch.None, FontSourceCatalog.Match(string.Empty).Match);
        }

        [Test]
        public void A_Font_That_Cannot_Be_Fetched_Is_Named_Rather_Than_Guessed_At()
        {
            // LINE Seed is openly licensed and published only as a zip behind a
            // page. Knowing which font it is and where it lives is worth saying;
            // inventing a file URL for it is not.
            var seed = FontSourceCatalog.Match("LINESeedSans");
            Assert.AreEqual(FontSourceMatch.Manual, seed.Match);
            Assert.IsNull(seed.DownloadUrl, "a manual match must have nothing to click");
            Assert.IsNotEmpty(seed.HomeUrl, "and must say where to go instead");
            Assert.AreEqual("SIL Open Font License 1.1", seed.LicenceName);

            var download = FontSourceCatalog.Download(seed);
            Assert.AreEqual(FontSourceOutcome.NoCandidate, download.Outcome);
            StringAssert.Contains("by hand", download.Message);
        }

        // -------------------------------------------------------- the bytes

        [Test]
        public void Only_Something_That_Begins_Like_A_Font_Is_A_Font()
        {
            Assert.IsTrue(FontSourceCatalog.LooksLikeFont(
                File.ReadAllBytes(FullPath(LatinFontPath)), out string what));
            StringAssert.Contains("TrueType", what);

            Assert.IsTrue(FontSourceCatalog.LooksLikeFont(
                File.ReadAllBytes(FullPath(StaticFontPath)), out what));
            StringAssert.Contains("CFF", what, "an OTTO font is a font");

            Assert.IsFalse(FontSourceCatalog.LooksLikeFont(
                Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>404</body></html>"),
                out what));
            StringAssert.Contains("HTML", what, "an error page should be named as one");

            Assert.IsFalse(FontSourceCatalog.LooksLikeFont(
                new byte[] { (byte)'w', (byte)'O', (byte)'F', (byte)'2', 0, 0, 0, 0 }, out what));
            StringAssert.Contains("WOFF2", what, "a web font is a font in a wrapper, and saying " +
                                                 "so is the difference between a fix and a hunt");

            Assert.IsFalse(FontSourceCatalog.LooksLikeFont(new byte[0], out what));
            Assert.IsFalse(FontSourceCatalog.LooksLikeFont(null, out what));
        }

        [Test]
        public void A_Payload_That_Is_Not_A_Font_Is_Refused_Before_Anything_Is_Written()
        {
            const string folder = "Assets/OneTextRecoveryTestFolder";
            Assert.IsFalse(AssetDatabase.IsValidFolder(folder), "the test folder already exists");

            var candidate = FontSourceCatalog.Match("Cairo");
            LogAssert.Expect(LogType.Error, new Regex("not a font file"));

            var result = FontSourceCatalog.Install(candidate,
                Encoding.ASCII.GetBytes("<html>Sign in to continue</html>"), null, folder);

            Assert.AreEqual(FontSourceOutcome.NotAFont, result.Outcome);
            Assert.IsNull(result.FontPath, "nothing may be written when the check fails");
            StringAssert.Contains("Nothing was written", result.Message);
            Assert.IsFalse(AssetDatabase.IsValidFolder(folder),
                "the check has to happen before the project is touched at all");
        }

        [Test]
        public void One_File_May_Only_Stand_For_Several_Faces_If_It_Is_Variable()
        {
            Assert.IsTrue(FontRecovery.HasWeightAxis(FullPath(VariableFontPath)),
                "a variable font's weight axis should be readable from its own tables");
            Assert.IsFalse(FontRecovery.HasWeightAxis(FullPath(StaticFontPath)),
                "a static font must not be treated as four faces");
            Assert.IsFalse(FontRecovery.HasWeightAxis("nowhere/at/all.ttf"));
        }

        [Test]
        public void The_Axes_A_File_Declares_Are_Read_With_Their_Ranges()
        {
            // The ranges are the half that matters once a weight is being set:
            // an inferred 900 on a font that stops at 700 has to become 700, and
            // that decision needs the number the font published.
            var axes = FontRecovery.VariationAxes(FullPath(VariableFontPath));
            Assert.IsNotEmpty(axes);

            FontAxis weight = default;
            foreach (var axis in axes) if (axis.Tag == "wght") weight = axis;

            Assert.AreEqual("wght", weight.Tag, "the variable test font should have a weight axis");
            Assert.AreEqual(100f, weight.Minimum, 0.01f);
            Assert.AreEqual(400f, weight.Default, 0.01f);
            Assert.AreEqual(900f, weight.Maximum, 0.01f);

            Assert.IsEmpty(FontRecovery.VariationAxes(FullPath(StaticFontPath)),
                "a static font declares no axes");
            Assert.IsEmpty(FontRecovery.VariationAxes("nowhere/at/all.ttf"),
                "an unreadable file is not an exception, it is a font with no axes");
        }

        // ----------------------------------------------------- weight in a name

        private static int WeightOf(string faceName)
        {
            var guess = FontWeightNames.Infer(faceName);
            return guess.HasWeight ? guess.Weight : 0;
        }

        [Test]
        public void The_Weight_A_Face_Is_Named_For_Is_Read_Off_Its_Name()
        {
            // Every one of these is a font asset name from the project this was
            // measured on. One variable file comes back for each family, and
            // without this every one of them renders at the file's default
            // weight — which is not the weight that shipped.
            Assert.AreEqual(800, WeightOf("Pretendard-ExtraBold SDF"));
            Assert.AreEqual(800, WeightOf("NotoSansJP-ExtraBold SDF"));
            Assert.AreEqual(900, WeightOf("NotoSansCJKsc-Black SDF"));
            Assert.AreEqual(800, WeightOf("Sen-ExtraBold SDF"));
            Assert.AreEqual(400, WeightOf("Alata-Regular.ttf"));

            // The whole standard mapping, because the vocabulary is the feature.
            Assert.AreEqual(100, WeightOf("Inter-Thin"));
            Assert.AreEqual(200, WeightOf("Inter-ExtraLight"));
            Assert.AreEqual(200, WeightOf("Inter-UltraLight"));
            Assert.AreEqual(300, WeightOf("Inter-Light"));
            Assert.AreEqual(400, WeightOf("Inter-Normal"));
            Assert.AreEqual(400, WeightOf("Inter-Book"));
            Assert.AreEqual(500, WeightOf("Inter-Medium"));
            Assert.AreEqual(600, WeightOf("Inter-SemiBold"));
            Assert.AreEqual(600, WeightOf("Inter-DemiBold"));
            Assert.AreEqual(700, WeightOf("Inter-Bold"));
            Assert.AreEqual(900, WeightOf("Inter-Heavy"));
        }

        [Test]
        public void A_Weight_Word_Is_A_Whole_Word_Or_It_Is_Nothing()
        {
            // The trap this is built around: Cairo_Line_Black is the Black of a
            // family called Cairo Line, and every separator in that name has to
            // be read as one. "Line" must not confuse it and "Black" must not be
            // found inside anything else.
            Assert.AreEqual(900, WeightOf("Cairo_Line_Black SDF"));
            Assert.AreEqual(900, WeightOf("Cairo Line Black"));
            Assert.AreEqual(900, WeightOf("CairoLineBlack"));

            // Families that contain a weight word and are not one. A substring
            // match makes both of these bold or black, silently, forever.
            Assert.AreEqual(0, WeightOf("Blackout"), "'Blackout' is a family, not a Black");
            Assert.AreEqual(0, WeightOf("Boldoni"), "'Boldoni' is a family, not a Bold");
            Assert.AreEqual(400, WeightOf("Blackout-Regular"),
                "and the Regular of Blackout is a Regular, read from its own word");
            Assert.AreEqual(0, WeightOf("Lightning"));
            Assert.AreEqual(0, WeightOf("Bookman Old Style"));
        }

        [Test]
        public void A_Two_Word_Weight_Is_One_Weight()
        {
            Assert.AreEqual(800, WeightOf("Barlow Extra Bold"), "however the name spaces it");
            Assert.AreEqual(800, WeightOf("Barlow-Extra-Bold"));
            Assert.AreEqual(800, WeightOf("BarlowExtraBold"));
            Assert.AreEqual(200, WeightOf("Barlow Ultra Light"));
            Assert.AreEqual(600, WeightOf("Barlow Semi Bold"));

            // And the pair counts once. Reading "Extra Bold" as an Extra and a
            // Bold would be two weight words, which is the same as none.
            var guess = FontWeightNames.Infer("Barlow Extra Bold");
            Assert.IsTrue(guess.HasWeight);
            Assert.AreEqual("ExtraBold", guess.WeightName);

            // A modifier takes the weight after it with it even when the pair is
            // one the table has no number for: answering 300 from the "Light"
            // half of a SemiLight would be inventing a weight class.
            Assert.AreEqual(0, WeightOf("Barlow Semi Light"),
                "an unnumbered pair is 'no opinion', not the weight of its second half");
        }

        [Test]
        public void Two_Weights_Or_None_Is_No_Opinion()
        {
            // The rule that makes this safe to run unattended on 6,717 labels: a
            // name it does not understand leaves the font where it was. A default
            // weight looks like something nobody set; a wrong weight looks like
            // something somebody chose.
            Assert.AreEqual(0, WeightOf("Oswald-Medium-Bold"), "two weights is no weight");
            Assert.AreEqual(0, WeightOf("Barlow Extra Bold Light"),
                "a pair and a single are still two");
            Assert.AreEqual(0, WeightOf("Josefin Sans"), "no weight word at all");
            Assert.AreEqual(0, WeightOf("어그로"));
            Assert.AreEqual(0, WeightOf(string.Empty));
            Assert.AreEqual(0, WeightOf(null));

            Assert.IsFalse(FontWeightNames.Infer("Josefin Sans").Found,
                "and 'no opinion' is a state the caller can see, not a 400");
        }

        [Test]
        public void A_Name_That_Begins_With_A_Weight_Word_Is_Naming_A_Family()
        {
            // Styles are written after the family they modify. Black Han Sans is
            // a family whose designer put the word at the front, and reading it
            // as a 900 would be wrong on every label using it.
            Assert.AreEqual(0, WeightOf("Black Han Sans"));
            Assert.AreEqual(0, WeightOf("Black Han Sans SDF"));

            // Which leaves its Bold readable, from the word in style position.
            Assert.AreEqual(700, WeightOf("Black Han Sans-Bold"));
        }

        [Test]
        public void Italic_Is_Read_Too_And_Only_Set_Where_There_Is_An_Axis()
        {
            var guess = FontWeightNames.Infer("Pretendard-BoldItalic");
            Assert.IsTrue(guess.HasWeight);
            Assert.AreEqual(700, guess.Weight);
            Assert.IsTrue(guess.Italic);

            Assert.IsTrue(FontWeightNames.Infer("Cairo-Oblique").Italic);
            Assert.IsFalse(FontWeightNames.Infer("Cairo-Bold").Italic);

            // `ital` is a switch and `slnt` is an angle, and a font offers one
            // or the other.
            var switched = FontWeightNames.Variations(guess,
                new[] { new FontAxis("wght", 100f, 400f, 900f), new FontAxis("ital", 0f, 0f, 1f) });
            CollectionAssert.Contains(switched, new FontVariation("ital", 1f));

            var slanted = FontWeightNames.Variations(guess,
                new[] { new FontAxis("wght", 100f, 400f, 900f), new FontAxis("slnt", -11f, 0f, 0f) });
            CollectionAssert.Contains(slanted, new FontVariation("slnt", -10f));

            // A font with neither is a font whose italic is a separate file, and
            // there is nothing here that can conjure one.
            var upright = FontWeightNames.Variations(guess,
                new[] { new FontAxis("wght", 100f, 400f, 900f) });
            Assert.AreEqual(1, upright.Length, "only the weight should have been set");
            Assert.AreEqual("wght", upright[0].Tag);
        }

        [Test]
        public void A_Weight_Is_Clamped_To_What_The_Font_Can_Actually_Do()
        {
            // A family whose variable file covers 400–700 cannot be a Black, and
            // asking for 900 anyway would have HarfBuzz pin it there silently.
            // Saying 700 is the same rendering and an answer somebody can read.
            var narrow = new[] { new FontAxis("wght", 400f, 400f, 700f) };
            var black = FontWeightNames.For("Sen-Black", narrow);
            Assert.AreEqual(1, black.Length);
            Assert.AreEqual(700f, black[0].Value, 1e-4f, "900 clamped into 400–700");

            // Clamping the other way lands on the axis default, and a face that
            // comes out as the font's own default is one there is nothing to
            // set for: this font has no Thin, and 400 is what it would draw.
            Assert.IsEmpty(FontWeightNames.For("Sen-Thin", narrow));

            // A value that lands on the default is not worth recording: it is
            // the face the file already opens as, and setting it would cost
            // every label a second font instance and a second set of tiles.
            Assert.IsEmpty(FontWeightNames.For("Sen-Regular", narrow),
                "the axis default is what the font already is");

            // A variable font with no weight axis at all — rotation, width,
            // optical size — has nothing here to move.
            Assert.IsEmpty(FontWeightNames.For("TiltWarp-Bold",
                new[] { new FontAxis("XROT", -45f, 0f, 45f) }));
        }

        [Test]
        public void A_Static_Face_Is_Left_Exactly_As_It_Is()
        {
            // It is already the weight it is. The name saying "Bold" is the file
            // saying "Bold", and there is no axis to say it on.
            var axes = FontRecovery.VariationAxes(FullPath(StaticFontPath));
            Assert.IsEmpty(FontWeightNames.For("Cairo-ExtraBold", axes));
            Assert.IsEmpty(FontWeightNames.Variations(FontWeightNames.Infer("Cairo-Bold"), null));
        }

        // -------------------------------------------- the weight on the asset

        [Test]
        public void Filling_A_Placeholder_Sets_The_Weight_Its_Face_Was_Named_For()
        {
            // The case the whole thing exists for: four TMP faces, one variable
            // file, and the difference between them living on the asset each of
            // those faces became rather than on the thousands of labels pointing
            // at it.
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Pretendard", new OneFontRecovery
            {
                ExpectedFileName = "Pretendard-ExtraBold.ttf",
                StyleName = "ExtraBold",
            });

            Assert.IsTrue(FontRecovery.Fill(placeholder, FullPath(VariableFontPath)));

            Assert.AreEqual(1, placeholder.BaseVariations.Count,
                "the face's own weight should be recorded on the asset");
            Assert.AreEqual("wght", placeholder.BaseVariations[0].Tag);
            Assert.AreEqual(800f, placeholder.BaseVariations[0].Value, 1e-4f);
        }

        [Test]
        public void A_Static_Fill_Records_No_Weight_And_Says_Nothing()
        {
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo", CairoBoldFacts());

            // CffShapes.otf, not NotoSans.ttf: the Noto in this folder carries an
            // fvar and is therefore exactly the case this test is not about.
            Assert.IsTrue(FontRecovery.Fill(placeholder, FullPath(StaticFontPath), out string said));

            Assert.IsNull(said, "there is nothing to tell the user about a static face");
            Assert.IsEmpty(placeholder.BaseVariations,
                "a font with no axes must come out of this untouched");
        }

        [Test]
        public void A_Labels_Own_Axes_Win_Over_The_Faces()
        {
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Pretendard", new OneFontRecovery
            {
                ExpectedFileName = "Pretendard-ExtraBold.ttf",
                StyleName = "ExtraBold",
            });
            Assert.IsTrue(FontRecovery.Fill(placeholder, FullPath(VariableFontPath)));

            // A label that sets nothing — which is nearly all of them — still
            // has to come out extra bold, so the empty request cannot resolve to
            // the unvaried face.
            var inherited = placeholder.GetVariant(System.Array.Empty<FontVariation>());
            Assert.NotNull(inherited);
            Assert.AreNotSame(placeholder.Font, inherited,
                "a label that asks for nothing should still get the face's own weight");

            // And a label that asks for 800 is asking for the face it already
            // has: same instance, one set of atlas tiles, not two.
            Assert.AreSame(inherited,
                placeholder.GetVariant(new[] { new FontVariation("wght", 800f) }),
                "the label's axes should replace the face's, not stack with them");

            // A label that wants something else gets it.
            var light = placeholder.GetVariant(new[] { new FontVariation("wght", 300f) });
            Assert.AreNotSame(inherited, light, "a label may still overrule the face it was given");
        }

        [Test]
        public void An_Extra_Bold_Is_One_Style_And_Not_A_Family_Called_Extra()
        {
            // Taking only the "Bold" off left a family called "Sen Extra", which
            // matched no catalogue entry and none of the other three faces of
            // Sen the same project has: one missing font became two, and neither
            // of them was findable.
            FontRecovery.ParseFace("Sen-ExtraBold SDF", out string family, out string style);
            Assert.AreEqual("Sen", family);
            Assert.AreEqual("ExtraBold", style);

            FontRecovery.ParseFace("Pretendard-ExtraBold SDF", out family, out style);
            Assert.AreEqual("Pretendard", family);
            Assert.AreEqual("ExtraBold", style);
            Assert.AreEqual(FontSourceMatch.Download, FontSourceCatalog.Match(family).Match,
                "and the family that comes out has to be one the catalogue can answer");

            FontRecovery.ParseFace("Cairo_Line_Black SDF", out family, out style);
            Assert.AreEqual("Cairo Line", family);
            Assert.AreEqual("Black", style);
        }

        // ------------------------------------------------------- the lookup

        /// <summary>
        /// A Cairo METADATA.pb, cut down to the fields that are read. The shape
        /// is what matters: the family's own name sits at the top level and
        /// every face repeats a `name:` of its own inside a block, which is why
        /// this is parsed by depth and not by the first line that matches.
        /// </summary>
        private const string CairoMetadata = @"
name: ""Cairo""
designer: ""Multiple Designers""
license: ""OFL""
category: ""SANS_SERIF""
date_added: ""2014-09-16""
fonts {
  name: ""Cairo""
  style: ""normal""
  weight: 400
  filename: ""Cairo[slnt,wght].ttf""
  post_script_name: ""Cairo-Regular""
  full_name: ""Cairo Regular""
  copyright: ""Copyright 2020 The Cairo Project Authors""
}
subsets: ""arabic""
subsets: ""latin""
axes {
  tag: ""slnt""
  min_value: -11.0
  max_value: 0.0
}
axes {
  tag: ""wght""
  min_value: 200.0
  max_value: 1000.0
}
";

        [Test]
        public void A_Metadata_File_Names_The_Files_A_Family_Name_Cannot()
        {
            // The blocker the catalogue was stuck behind: the raw URL embeds the
            // axis list in the file name, so "Cairo" gives you the directory and
            // never the file. This is where the file name comes from.
            var metadata = FontMetadata.Parse(CairoMetadata);

            Assert.IsTrue(metadata.Found);
            Assert.AreEqual("Cairo", metadata.FamilyName,
                "the family's own name, not the one inside the fonts block");
            Assert.AreEqual("OFL", metadata.LicenceId);
            Assert.AreEqual(1, metadata.FileNames.Count);
            Assert.AreEqual("Cairo[slnt,wght].ttf", metadata.PreferredFile);
            Assert.IsTrue(metadata.Variable);
            Assert.IsTrue(metadata.HasAxes);

            // A directory of static faces: the Regular is the one to fetch,
            // because it is the face the others are described relative to.
            var statics = FontMetadata.Parse(
                "name: \"Alata\"\nlicense: \"OFL\"\n" +
                "fonts {\n  name: \"Alata\"\n  filename: \"Alata-Regular.ttf\"\n}\n");
            Assert.AreEqual("Alata-Regular.ttf", statics.PreferredFile);
            Assert.IsFalse(statics.Variable);

            // A variable family that also publishes an italic file: the upright
            // one is the family, and the italic is a second download nobody
            // asked for.
            var italic = FontMetadata.Parse(
                "name: \"Inter\"\nlicense: \"OFL\"\n" +
                "fonts {\n  filename: \"Inter-Italic[opsz,wght].ttf\"\n}\n" +
                "fonts {\n  filename: \"Inter[opsz,wght].ttf\"\n}\n");
            Assert.AreEqual("Inter[opsz,wght].ttf", italic.PreferredFile);

            // Several static weights and no Regular among them is not a choice
            // to make on somebody's behalf.
            var ambiguous = FontMetadata.Parse(
                "name: \"Ambiguous\"\nlicense: \"OFL\"\n" +
                "fonts {\n  filename: \"Ambiguous-Bold.ttf\"\n}\n" +
                "fonts {\n  filename: \"Ambiguous-Light.ttf\"\n}\n");
            Assert.IsNull(ambiguous.PreferredFile);

            // Nothing thrown at anything: a 404 page parsed as metadata is a
            // family that says nothing, which is a sentence the caller can use.
            Assert.IsFalse(FontMetadata.Parse("<html><body>404</body></html>").Found);
            Assert.IsFalse(FontMetadata.Parse(null).Found);
        }

        [Test]
        public void A_Family_Name_Becomes_A_Directory_Or_It_Becomes_Nothing()
        {
            Assert.AreEqual("cairo", FontSourceCatalog.Slug("Cairo"));
            Assert.AreEqual("josefinsans", FontSourceCatalog.Slug("Josefin Sans"));
            Assert.AreEqual("notosanskr", FontSourceCatalog.Slug("Noto Sans KR"));
            Assert.AreEqual("mplus1p", FontSourceCatalog.Slug("M PLUS 1p"));
            Assert.AreEqual("ptsans", FontSourceCatalog.Slug("PT_Sans"));

            // Conservative on purpose. A family this cannot turn into a
            // directory name comes back "find this yourself", which is a
            // perfectly good answer and much better than a request for a
            // directory that cannot exist.
            Assert.AreEqual(string.Empty, FontSourceCatalog.Slug("어그로"),
                "a Korean family name is a real font and not a directory in this repository");
            Assert.AreEqual(string.Empty, FontSourceCatalog.Slug("Noto Sans 日本語"));
            Assert.AreEqual(string.Empty, FontSourceCatalog.Slug("A"), "one letter is not a family");
            Assert.AreEqual(string.Empty, FontSourceCatalog.Slug(string.Empty));
            Assert.AreEqual(string.Empty, FontSourceCatalog.Slug(null));

            Assert.IsFalse(FontSourceCatalog.CanResolve("어그로"));
            Assert.IsFalse(FontSourceCatalog.CanResolve("Cairo"),
                "a family the hand-written list already answers has nothing to look up");
            Assert.IsTrue(FontSourceCatalog.CanResolve("Libre Baskerville"));
        }

        [Test]
        public void A_Resolved_Family_Is_The_Same_Answer_The_Hand_Written_One_Was()
        {
            // The strongest thing that can be said about the resolution: run it
            // on a family somebody already wrote out by hand and it reproduces
            // that entry, URL for URL. The eleven were never special; they were
            // eleven.
            var resolved = FontSourceCatalog.Candidate(FontMetadata.Parse(CairoMetadata), "ofl")
                .Candidate;
            var written = FontSourceCatalog.Match("Cairo");

            Assert.AreEqual(FontSourceMatch.Download, resolved.Match);
            Assert.AreEqual(written.FamilyName, resolved.FamilyName);
            Assert.AreEqual(written.FileName, resolved.FileName,
                "the axis list belongs in the URL and not in a Unity asset path");
            Assert.AreEqual(written.DownloadUrl, resolved.DownloadUrl);
            Assert.AreEqual(written.LicenceName, resolved.LicenceName);
            Assert.AreEqual(written.LicenceUrl, resolved.LicenceUrl);
            Assert.AreEqual(written.LicenceFileUrl, resolved.LicenceFileUrl);
            Assert.AreEqual(written.HomeUrl, resolved.HomeUrl);

            Assert.IsTrue(resolved.Resolved, "and it says which of the two it is");
            Assert.IsFalse(written.Resolved);
        }

        [Test]
        public void The_Licence_Is_The_One_The_Repository_Actually_Keeps_It_Under()
        {
            // Most of google/fonts is OFL and assuming that is how somebody gets
            // offered an Apache font under a licence name that is not its own.
            var apache = FontSourceCatalog.Candidate(FontMetadata.Parse(
                "name: \"Roboto\"\nlicense: \"APACHE2\"\n" +
                "fonts {\n  filename: \"Roboto[wdth,wght].ttf\"\n}\n"), "apache").Candidate;

            Assert.AreEqual(FontSourceMatch.Download, apache.Match);
            Assert.AreEqual("Apache License 2.0", apache.LicenceName);
            StringAssert.Contains("/apache/roboto/", apache.DownloadUrl);
            StringAssert.EndsWith("/apache/roboto/LICENSE.txt", apache.LicenceFileUrl);

            var ubuntu = FontSourceCatalog.Candidate(FontMetadata.Parse(
                "name: \"Ubuntu\"\nlicense: \"UFL\"\n" +
                "fonts {\n  filename: \"Ubuntu-Regular.ttf\"\n}\n"), "ufl").Candidate;
            Assert.AreEqual("Ubuntu Font Licence 1.0", ubuntu.LicenceName);
        }

        [Test]
        public void A_Licence_That_Cannot_Be_Established_Is_A_Manual_Download()
        {
            // The rule the button rests on: the person pressing it has read the
            // name of what they are agreeing to. Where that name is in doubt
            // there is no button, only a page.
            var contradictory = FontSourceCatalog.Candidate(FontMetadata.Parse(
                "name: \"Confused\"\nlicense: \"APACHE2\"\n" +
                "fonts {\n  filename: \"Confused-Regular.ttf\"\n}\n"), "ofl").Candidate;

            Assert.AreEqual(FontSourceMatch.Manual, contradictory.Match,
                "a publisher that contradicts itself is not one to fetch on somebody's behalf");
            Assert.IsNull(contradictory.DownloadUrl);
            Assert.IsNotEmpty(contradictory.HomeUrl, "and the page is still worth naming");

            var unknownDirectory = FontSourceCatalog.Candidate(FontMetadata.Parse(
                "name: \"Elsewhere\"\nfonts {\n  filename: \"Elsewhere-Regular.ttf\"\n}\n"),
                "somewhere").Candidate;
            Assert.AreEqual(FontSourceMatch.Manual, unknownDirectory.Match);

            // WOFF is refused by name as well as by its magic bytes later:
            // knowing before the download saves thirty seconds and a puzzle.
            var wrapped = FontSourceCatalog.Candidate(FontMetadata.Parse(
                "name: \"Webby\"\nlicense: \"OFL\"\n" +
                "fonts {\n  filename: \"Webby-Regular.woff2\"\n}\n"), "ofl").Candidate;
            Assert.AreEqual(FontSourceMatch.Manual, wrapped.Match);
            StringAssert.Contains(".woff2", wrapped.Note);

            // And a metadata file that names no files at all is nothing to show.
            var empty = FontSourceCatalog.Candidate(FontMetadata.Parse("name: \"Nothing\""), "ofl");
            Assert.AreEqual(FontSourceOutcome.NoCandidate, empty.Outcome);
            Assert.IsFalse(empty.Candidate.Found);
        }

        [Test]
        public void The_Hand_Written_Entry_Wins_Over_The_Lookup_And_Costs_No_Request()
        {
            // Pretendard is not in google/fonts at all, and the note on Cairo is
            // a sentence somebody wrote about what will surprise you. Neither
            // survives being replaced by a resolution, so neither is.
            var cairo = FontSourceCatalog.Resolve("Cairo");
            Assert.IsTrue(cairo.Ok);
            Assert.IsFalse(cairo.Candidate.Resolved, "this came out of the list, not the network");
            Assert.AreEqual(FontSourceCatalog.Match("Cairo").Note, cairo.Candidate.Note,
                "the hand-written note is the reason the hand-written entry wins");

            var pretendard = FontSourceCatalog.Resolve("Pretendard");
            Assert.IsTrue(pretendard.Ok);
            StringAssert.Contains("orioncactus", pretendard.Candidate.DownloadUrl,
                "a source that is not Google's is one resolution could never produce");

            var seed = FontSourceCatalog.Resolve("LINE Seed KR");
            Assert.AreEqual(FontSourceMatch.Manual, seed.Candidate.Match,
                "including the ones the list deliberately refuses to fetch");

            // A name with nowhere to look is answered without a request too.
            var korean = FontSourceCatalog.Resolve("어그로");
            Assert.AreEqual(FontSourceOutcome.NoCandidate, korean.Outcome);
            StringAssert.Contains("yourself", korean.Message);
        }
    }
}
