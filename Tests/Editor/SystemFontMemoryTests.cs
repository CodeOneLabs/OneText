using NUnit.Framework;
using OneText;

namespace OneText.Tests
{
    /// <summary>
    /// What the system-font tier remembers between characters.
    ///
    /// The tier walks the machine's fonts to find one that covers a character
    /// the project's own fonts missed, and that list is about four hundred
    /// files on a Mac. Unseen characters do not arrive one at a time — they
    /// arrive in floods, one script at a time, when a language changes or a
    /// screen of new text opens. So the file that answered for the last
    /// character of a script is asked first for the next one.
    ///
    /// Counted in files rather than milliseconds: "asked one file instead of
    /// four hundred" is the claim, and a stopwatch on a machine with a warm
    /// page cache is not evidence for it.
    /// </summary>
    public class SystemFontMemoryTests
    {
        [SetUp]
        public void SetUp()
        {
            SystemFonts.Forget();
            SystemFonts.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
        }

        [Test]
        public void The_Second_Character_Of_A_Script_Asks_What_Answered_For_The_First()
        {
            // 가 and 나 are both Hangul syllables; whatever drew one draws the
            // other, and the machine has to be told that only once.
            if (SystemFonts.Resolve('가') == null)
                Assert.Ignore("no system font on this machine covers Hangul");

            int afterFirst = SystemFonts.FilesProbed;
            Assert.Greater(afterFirst, 0, "the first character did not probe anything");

            Assert.NotNull(SystemFonts.Resolve('나'), "the second syllable found no face");
            int forSecond = SystemFonts.FilesProbed - afterFirst;

            Assert.AreEqual(1, forSecond,
                $"the second syllable of the same script asked {forSecond} files; " +
                "the one that answered for the first should have been enough");
        }

        [Test]
        public void A_Different_Script_Does_Not_Inherit_The_Memory()
        {
            // The memory is per script on purpose: a Korean face answering for
            // Hangul says nothing about Arabic, and a list that pretended
            // otherwise would put the wrong file first for ever.
            if (SystemFonts.Resolve('가') == null)
                Assert.Ignore("no system font on this machine covers Hangul");

            SystemFonts.Resolve('나');
            int beforeArabic = SystemFonts.FilesProbed;
            SystemFonts.Resolve('ب');

            Assert.GreaterOrEqual(SystemFonts.FilesProbed - beforeArabic, 1,
                "Arabic resolved without asking anything, which cannot be right");
        }
    }
}
