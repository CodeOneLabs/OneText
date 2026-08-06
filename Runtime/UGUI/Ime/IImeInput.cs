using System;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// The platform's input method, reduced to the four things a text field
    /// needs from it: turn it on, tell it where the caret is on screen so the
    /// candidate window lands there, read what it is composing, turn it off.
    ///
    /// There is one implementation per Unity input backend. The interface
    /// exists because <c>UnityEngine.Input</c> throws outright in a project
    /// that switched to the Input System package, so the field cannot simply
    /// call it.
    /// </summary>
    public interface IImeInput
    {
        /// <summary>False when this backend cannot run in the current project.</summary>
        bool IsAvailable { get; }

        /// <summary>Starts accepting composition; called when a field takes focus.</summary>
        void Begin();

        /// <summary>Stops accepting composition, and drops anything in flight.</summary>
        void End();

        /// <summary>
        /// Where the caret is, in screen pixels, so the candidate window opens
        /// next to the text instead of in the corner of the screen.
        /// </summary>
        void SetCursorScreenPosition(Vector2 screenPosition);

        /// <summary>
        /// The text being composed right now. False when nothing is.
        /// <paramref name="caret"/> is -1 when the backend cannot report one,
        /// and <paramref name="clauseLength"/> is 0 when it reports no
        /// converting clause, which, on every backend Unity ships today, is
        /// always.
        /// </summary>
        bool TryGetComposition(out string text, out int caret, out int clauseStart, out int clauseLength);
    }

    /// <summary>
    /// Picks the input method backend for the project this is running in.
    ///
    /// The Input System backend cannot be referenced from here (its assembly
    /// only exists when the package is installed), so it registers itself with
    /// <see cref="Register"/> from an assembly that is compiled out entirely
    /// when the package is absent. The legacy backend is the fallback, and a
    /// field with neither still edits: it just cannot compose.
    /// </summary>
    public static class ImeInput
    {
        private static Func<IImeInput> _registered;

        /// <summary>
        /// Installs a backend, ahead of the built-in one. Called by the Input
        /// System bridge, and by tests that want to drive composition by hand.
        /// </summary>
        public static void Register(Func<IImeInput> factory) => _registered = factory;

        /// <summary>Forgets a registered backend, restoring the built-in choice.</summary>
        public static void Unregister() => _registered = null;

        /// <summary>Creates a backend for one field, or null when none can run.</summary>
        public static IImeInput Create()
        {
            if (_registered != null)
            {
                var registered = _registered();
                if (registered != null && registered.IsAvailable) return registered;
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            var legacy = new LegacyImeInput();
            if (legacy.IsAvailable) return legacy;
#endif
            return null;
        }
    }
}
