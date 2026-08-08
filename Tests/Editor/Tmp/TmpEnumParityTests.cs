using System;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>
    /// The two enums OneText declares under TextMesh Pro's names, against the
    /// real thing.
    ///
    /// <c>OneText.UGUI.TextAlignmentOptions</c> exists so that
    /// <c>label.alignment = TextAlignmentOptions.MidlineLeft</c> compiles in a
    /// project that dropped TMP an hour ago, and the whole of its value is that
    /// every member means what the same name meant there — projects cast these
    /// to int, serialize them, and switch over them. A typo'd value would
    /// compile everywhere and quietly mean a different corner of the box. This
    /// assembly is the only one in the repository that can see both types at
    /// once, so this is where they are held together: every TMP member exists
    /// here under the same name with the same value, and vice versa.
    /// </summary>
    public class TmpEnumParityTests
    {
        [Test]
        public void TextAlignmentOptions_MatchesTmpNameForNameAndValueForValue()
        {
            AssertSameMembers(typeof(TMPro.TextAlignmentOptions),
                typeof(OneText.UGUI.TextAlignmentOptions));
        }

        [Test]
        public void TextWrappingModes_MatchesTmpNameForNameAndValueForValue()
        {
            // Looked up by name rather than written as a type: the enum arrived
            // in TMP 3.2, and this file compiles against whichever TMP the
            // project has. No enum in an old TMP is a pass — there is nothing
            // to be out of parity with.
            var tmp = typeof(TMPro.TextAlignmentOptions).Assembly
                .GetType("TMPro.TextWrappingModes");
            if (tmp == null) Assert.Ignore("this TMP predates TextWrappingModes");

            AssertSameMembers(tmp, typeof(OneText.UGUI.TextWrappingModes));
        }

        private static void AssertSameMembers(Type theirs, Type ours)
        {
            foreach (string name in Enum.GetNames(theirs))
            {
                Assert.IsTrue(Enum.IsDefined(ours, name),
                    $"{ours.Name} is missing {name}");
                Assert.AreEqual(
                    Convert.ToInt64(Enum.Parse(theirs, name)),
                    Convert.ToInt64(Enum.Parse(ours, name)),
                    $"{name} has a different value in {ours.FullName}");
            }

            foreach (string name in Enum.GetNames(ours))
            {
                Assert.IsTrue(Enum.IsDefined(theirs, name),
                    $"{ours.Name}.{name} does not exist in TMP: a member invented " +
                    "here is a member a migrated project cannot have written");
            }
        }
    }
}
