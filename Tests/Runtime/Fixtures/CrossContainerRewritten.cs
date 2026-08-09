using UnityEngine;
using OneText.UGUI;

namespace OneText.Tests
{
    /// <summary>
    /// The same component after the script rewrite has been through it: the
    /// field keeps its name and takes the OneText type instead.
    ///
    /// The pair exists to reproduce the order every real migration runs in.
    /// Scripts are rewritten first and Unity recompiles; only then are the
    /// components converted. Between those two moments every field like this one
    /// names a component it can no longer hold, and Unity reads it as None the
    /// moment the assembly reloads — before the migration has done anything at
    /// all. A test that assigns the field through a SerializedObject can never
    /// reach that state, because the editor refuses to write the mismatch that
    /// a text edit to a .cs file creates for free.
    ///
    /// In the runtime test assembly for the same reason as
    /// <see cref="CrossContainerTyped"/>: an editor script cannot be attached to
    /// a GameObject.
    /// </summary>
    public sealed class CrossContainerRewritten : MonoBehaviour
    {
        public OneTextLabel Typed;
    }
}
