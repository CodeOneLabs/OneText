#if ONETEXT_INPUT_SYSTEM
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace OneText.UGUI
{
    /// <summary>
    /// Composition through the Input System package. This lives in its own
    /// assembly, constrained on the package being installed, because an
    /// assembly definition that references a package which is not there does
    /// not fail to find it — it fails to compile.
    ///
    /// The Input System pushes composition at us instead of letting us poll it,
    /// so the string is cached as it arrives and read back on the field's own
    /// update. Ending a composition arrives as an empty string, which is
    /// exactly the signal the field is waiting for.
    /// </summary>
    internal sealed class InputSystemImeInput : IImeInput
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private string _composition = string.Empty;
        private bool _listening;

        public bool IsAvailable => Keyboard.current != null;

        public void Begin()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || _listening) return;

            keyboard.onIMECompositionChange += OnCompositionChange;
            keyboard.SetIMEEnabled(true);
            _listening = true;
        }

        public void End()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (_listening) keyboard.onIMECompositionChange -= OnCompositionChange;
                keyboard.SetIMEEnabled(false);
            }
            _listening = false;
            _composition = string.Empty;
        }

        public void SetCursorScreenPosition(Vector2 screenPosition) =>
            Keyboard.current?.SetIMECursorPosition(screenPosition);

        public bool TryGetComposition(out string text, out int caret, out int clauseStart, out int clauseLength)
        {
            text = _composition;
            caret = -1;
            clauseStart = 0;
            clauseLength = 0;
            return !string.IsNullOrEmpty(text);
        }

        private void OnCompositionChange(IMECompositionString composition)
        {
            _builder.Clear();
            foreach (char character in composition) _builder.Append(character);
            _composition = _builder.ToString();
        }

        /// <summary>
        /// Offers this backend to <see cref="ImeInput"/>. Registration runs on
        /// load rather than on demand so the UGUI assembly never has to name a
        /// type it cannot reference.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => ImeInput.Register(() => new InputSystemImeInput());
    }
}
#endif
