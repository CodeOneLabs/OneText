using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OneText.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// What happens when the project supplies no font at all — as opposed to
    /// <c>SystemFontTests</c>, which is about a project that supplies fonts
    /// and one character they miss.
    ///
    /// It used to be: nothing, silently. <c>FontStack.Resolve</c> returned null
    /// before it reached the system tier when the stack was empty, so the one
    /// case the tier was most needed for never got it; both components then
    /// failed their native-state check and returned, which on screen is text
    /// that is simply not there; and neither <c>OneTextLabel</c> nor
    /// <c>OneTextMesh</c> contained a single log call to say so. Three ways of
    /// being invisible, stacked, and the migration's own path for "the font
    /// file named by this asset is not in the project" fed straight into them
    /// by writing a null onto the label.
    /// </summary>
    public class MissingFontTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        /// <summary>U+D55C HANGUL SYLLABLE HAN: absent from every bundled test font.</summary>
        private const int Hangul = 0xD55C;

        private readonly List<Object> _made = new List<Object>();
        private readonly List<string> _assetPaths = new List<string>();

        [SetUp]
        public void EnableTier()
        {
            SystemFonts.Enabled = true;
            SystemFonts.Forget();
            MissingFonts.Forget();
        }

        [TearDown]
        public void Cleanup()
        {
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
            MissingFonts.Forget();
            foreach (string path in _assetPaths) AssetDatabase.DeleteAsset(path);
            _assetPaths.Clear();
            foreach (var made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
        }

        private static void RequireASystemFont()
        {
            if (SystemFonts.Resolve('A') == null)
                Assert.Ignore("no font on this machine has 'A'; nothing to test the floor against");
        }

        // -------------------------------------------------------- the floor

        [Test]
        public void An_Empty_Stack_Still_Asks_The_Operating_System()
        {
            // The bug, in one line. The early return that used to be at the top
            // of Resolve read as an optimisation and was a policy: no fonts, no
            // answer, not even the machine's.
            RequireASystemFont();
            using var fonts = new FontStack();

            Assert.IsNotNull(fonts.Resolve('A'),
                "a stack with no fonts must still reach the system tier");
            Assert.IsTrue(SystemFonts.IsSystemFont(fonts.Resolve('A')));
        }

        [Test]
        public void An_Empty_Stack_Has_A_Head_To_Draw_With()
        {
            // Primary is what every caller downstream tests for: the layout
            // engine's own guard, both components' native-state check, the
            // prewarm. A null there is the difference between text and nothing,
            // and it is the value an empty stack always used to give.
            RequireASystemFont();
            using var fonts = new FontStack();

            Assert.IsNotNull(fonts.Primary, "an empty stack answered null and drew nothing");
            Assert.IsTrue(fonts.IsSystemOnly, "and it should know that is what it is doing");
        }

        [Test]
        public void A_Real_Font_Takes_The_Head_Back()
        {
            // The stand-in must not outlive its reason: assigning a font, or
            // dropping a .ttf into a placeholder, has to be visible at once
            // rather than after a domain reload.
            RequireASystemFont();
            using var fonts = new FontStack();
            Assert.IsTrue(fonts.IsSystemOnly);

            using var latin = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            fonts.Add(latin);

            Assert.AreSame(latin, fonts.Primary, "the project's own font must take the head back");
            Assert.IsFalse(fonts.IsSystemOnly);
        }

        [Test]
        public void An_Empty_Stack_Still_Routes_Per_Character()
        {
            // The head is Latin because Primary is asked for before any text is
            // known. That must not become the answer for every character: a
            // Korean string on an empty stack still has to find a Korean face.
            RequireASystemFont();
            if (SystemFonts.Resolve(Hangul) == null)
                Assert.Ignore("no font on this machine has U+D55C");
            using var fonts = new FontStack();

            // The claim is that the face which comes back can draw the
            // character, not that it is a different file from the Latin one —
            // on a machine whose Latin face also covers Hangul, one file is the
            // correct answer to both.
            var korean = fonts.Resolve(Hangul);
            Assert.IsNotNull(korean);
            Assert.IsTrue(korean.HasGlyph(Hangul),
                "the head of the stack answered for a character it cannot draw");
        }

        [Test]
        public void With_The_Tier_Off_An_Empty_Stack_Is_Still_Empty()
        {
            // The floor is the system tier, so turning the tier off returns the
            // old behaviour exactly. A project that wants device-independent
            // output keeps it.
            SystemFonts.Enabled = false;
            using var fonts = new FontStack();

            Assert.IsNull(fonts.Primary);
            Assert.IsNull(fonts.Resolve('A'));
            Assert.IsFalse(fonts.IsSystemOnly);
        }

        // ------------------------------------------------------ saying so

        [Test]
        public void A_Missing_Font_Is_Reported_Once()
        {
            // Once per missing font, not once per component: the projects this
            // matters to are the converted ones, where one absent .ttf is six
            // thousand labels, and six thousand identical lines is why people
            // turn the console off.
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo",
                new OneFontRecovery { ExpectedFileName = "Cairo-Bold.ttf" });

            LogAssert.Expect(LogType.Warning, new Regex("Cairo-Bold\\.ttf"));
            MissingFonts.Warn(null, placeholder, drawing: false);
            MissingFonts.Warn(null, placeholder, drawing: false);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Drawing_In_A_Device_Font_Is_Its_Own_Report()
        {
            // Legible-but-wrong and invisible are different outcomes and get
            // different sentences; a project that has read the first one still
            // needs to be told when a label falls all the way through.
            var placeholder = New<OneFontAsset>();
            placeholder.InitializePlaceholder("Cairo",
                new OneFontRecovery { ExpectedFileName = "Cairo-Bold.ttf" });

            LogAssert.Expect(LogType.Warning, new Regex("font from this device"));
            MissingFonts.Warn(null, placeholder, drawing: true);

            LogAssert.Expect(LogType.Warning, new Regex("does not draw"));
            MissingFonts.Warn(null, placeholder, drawing: false);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void An_Unassigned_Font_Says_So_Rather_Than_Naming_One()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no font is assigned"));
            MissingFonts.Warn(null, null, drawing: false);
        }

        // ------------------------------------------ the migration's own null

        [Test]
        public void A_Named_File_That_Is_Not_There_Still_Gets_A_Placeholder()
        {
            // The path with the least excuse for a null: the font asset named
            // the file exactly, so there is no guessing to do about what to go
            // and find — and it was the branch that wrote a null onto the label
            // anyway. One placeholder per file, whatever asked for it.
            var report = new MigrationReport();

            var placeholder = FontRecovery.PlaceholderForFile(
                report, "Assets/Fonts/Cairo-Bold.ttf");

            Assert.IsNotNull(placeholder, "the branch still answers with nothing");
            Track(placeholder);
            Assert.IsTrue(placeholder.IsPlaceholder);
            Assert.AreEqual("Cairo-Bold.ttf", placeholder.Recovery.ExpectedFileName,
                "the file name is known exactly here and must not be re-guessed");
        }

        [Test]
        public void Two_Labels_Wanting_One_Missing_File_Share_One_Placeholder()
        {
            var report = new MigrationReport();

            var first = FontRecovery.PlaceholderForFile(report, "Assets/Fonts/Sen-Regular.ttf");
            var second = FontRecovery.PlaceholderForFile(report, "Assets/Other/Sen-Regular.ttf");

            Track(first);
            Assert.AreSame(first, second,
                "one file to find is one placeholder, wherever it was referenced from");
        }

        // ----------------------------------------------------------- helpers

        private T New<T>() where T : ScriptableObject
        {
            var made = ScriptableObject.CreateInstance<T>();
            _made.Add(made);
            return made;
        }

        private void Track(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path)) _assetPaths.Add(path);
        }
    }
}
