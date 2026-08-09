using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// A ScriptableObject that points at a label in a prefab, for the tests that
    /// ask what the migration does to an asset that is neither a scene nor a
    /// prefab.
    ///
    /// It has a file to itself because Unity gives one MonoScript to one file
    /// and only to the class the file is named after. A ScriptableObject
    /// declared alongside the tests would make assets whose script pointer is
    /// zero, and a test that fails for that reason is testing the wrong thing.
    ///
    /// The two fields are the whole point. <see cref="Label"/> is declared wide
    /// enough to hold whatever replaces a <c>Text</c>; <see cref="Typed"/> is
    /// not, and that is the shape of every <c>TMP_Text</c> field in every
    /// project leaving TextMesh Pro. The migration can mend the first and can
    /// only report the second, and both of those have to be true.
    /// </summary>
    public sealed class CrossContainerHolder : ScriptableObject
    {
        public Graphic Label;

        public Text Typed;
    }
}
