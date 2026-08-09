using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The referring component as it is written before the script rewrite: one
    /// narrowly typed field naming a component in another file.
    ///
    /// It lives in the runtime test assembly rather than beside the tests that
    /// use it because Unity refuses to attach a MonoBehaviour compiled into an
    /// editor assembly — "it is an editor script" — and this one has to go on a
    /// GameObject in a prefab to be the thing it is imitating.
    /// <see cref="CrossContainerHolder"/> can stay next to the tests only
    /// because a ScriptableObject is never attached to anything.
    ///
    /// A file to itself, like that one, because Unity gives a MonoScript only to
    /// the class its file is named after, and a prefab whose script pointer is
    /// zero fails tests for reasons that have nothing to do with what they ask.
    /// </summary>
    public sealed class CrossContainerTyped : MonoBehaviour
    {
        public Text Typed;
    }
}
