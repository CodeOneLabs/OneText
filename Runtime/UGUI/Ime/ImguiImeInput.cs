using System;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// Composition through <c>UnityEngine.Input</c>'s input-method members:
    /// <c>imeCompositionMode</c>, <c>compositionString</c> and
    /// <c>compositionCursorPos</c>.
    ///
    /// It was called LegacyImeInput and compiled only under
    /// <c>ENABLE_LEGACY_INPUT_MANAGER</c>, on the stated grounds that "every one
    /// of these properties throws when it is not". That was measured and it is
    /// false. Under Active Input Handling set to "Input System Package (New)",
    /// on 6000.0.77f1: <c>Input.mousePosition</c> and <c>Input.GetKey</c> throw
    /// InvalidOperationException as documented, while <c>imeCompositionMode</c>
    /// (get and set), <c>compositionString</c>, <c>compositionCursorPos</c> and
    /// <c>imeIsSelected</c> all answer normally. The input-method members are
    /// exempt from that guard, and uGUI's own <c>BaseInput</c> reads all three
    /// of them with no <c>#if</c> around it — which is why every built-in
    /// InputField and every TextMesh Pro field composes Korean perfectly well
    /// in a project that has no Input Manager.
    ///
    /// So the guard is gone, and the name with it. What this is, is the input
    /// method belonging to the same stack the characters come from: OneText
    /// reads keystrokes out of the IMGUI event queue with
    /// <c>Event.PopEvent</c>, and <c>imeCompositionMode</c> is the switch that
    /// makes the platform compose into that queue rather than committing into
    /// it. Reading composition from one stack and characters from another is
    /// the bug this replaces: on macOS the Input System's own IME switch left
    /// the IMGUI path uncomposed, so the OS handed over every jamo already
    /// finished — U+3131, U+314F, one per keystroke — and 안녕하세요 arrived as
    /// ㅇㅏㄴㄴㅕㅇㅎㅏㅅㅔㅇㅛ, which no amount of arbitration downstream can
    /// put back together.
    ///
    /// The API is a poll, not a stream: there is one string, it is whatever the
    /// IME currently has, and the moment it empties the composition is over.
    /// It reports no caret inside the composition and no conversion clause, so
    /// this backend answers -1 and 0 for both and the field draws the whole
    /// composition as one underlined run.
    /// </summary>
    internal sealed class ImguiImeInput : IImeInput
    {
        public bool IsAvailable => ImeInput.PlatformImeAnswers();

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
