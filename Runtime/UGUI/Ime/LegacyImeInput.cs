#if ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// Composition through the Input Manager: <c>Input.compositionString</c>
    /// and friends. Compiled only where the legacy backend is enabled, because
    /// every one of these properties throws when it is not.
    ///
    /// The API is a poll, not a stream: there is one string, it is whatever the
    /// IME currently has, and the moment it empties the composition is over.
    /// It reports no caret inside the composition and no conversion clause, so
    /// this backend answers -1 and 0 for both and the field draws the whole
    /// composition as one underlined run.
    /// </summary>
    internal sealed class LegacyImeInput : IImeInput
    {
        public bool IsAvailable => true;

        public void Begin() => Input.imeCompositionMode = IMECompositionMode.On;

        public void End()
        {
            // Auto, not Off: Off keeps the IME disabled for the whole
            // application, which would break any other field (including a
            // built-in one) that takes focus next.
            Input.imeCompositionMode = IMECompositionMode.Auto;
        }

        public void SetCursorScreenPosition(Vector2 screenPosition) =>
            Input.compositionCursorPos = screenPosition;

        public bool TryGetComposition(out string text, out int caret, out int clauseStart, out int clauseLength)
        {
            text = Input.compositionString;
            caret = -1;
            clauseStart = 0;
            clauseLength = 0;
            return !string.IsNullOrEmpty(text);
        }
    }
}
#endif
