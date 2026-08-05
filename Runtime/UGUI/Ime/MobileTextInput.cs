using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// The soft-keyboard path for Android and iOS, where composition is not
    /// something the application observes: the operating system owns a text
    /// buffer, edits it with whatever keyboard, dictation or handwriting panel
    /// the user has installed, and hands back a finished string.
    ///
    /// So there is nothing to compose here and nothing to underline. The field
    /// mirrors the buffer instead — which is why
    /// <see cref="TextEditingModel.SetExternalText"/> exists and reports
    /// whether the value actually changed, rather than firing an event once per
    /// poll for text that did not.
    /// </summary>
    internal sealed class MobileTextInput
    {
        private TouchScreenKeyboard _keyboard;
        private string _lastText = string.Empty;

        /// <summary>
        /// True where the field should hand editing to the OS keyboard. Devices
        /// that allow in-place editing (a tablet with a hardware keyboard, the
        /// editor's remote) keep the normal path, IME and all.
        /// </summary>
        public static bool IsSupported =>
            TouchScreenKeyboard.isSupported && !TouchScreenKeyboard.isInPlaceEditingAllowed;

        public bool IsOpen => _keyboard != null && _keyboard.active;

        public void Open(string text, bool multiline, int characterLimit)
        {
            _lastText = text ?? string.Empty;
            _keyboard = TouchScreenKeyboard.Open(
                _lastText,
                TouchScreenKeyboardType.Default,
                autocorrection: false,
                multiline: multiline,
                secure: false,
                alert: false,
                textPlaceholder: string.Empty,
                characterLimit: Mathf.Max(0, characterLimit));
        }

        public void Close()
        {
            if (_keyboard != null) _keyboard.active = false;
            _keyboard = null;
            _lastText = string.Empty;
        }

        /// <summary>
        /// Copies the OS buffer into the model. <paramref name="changed"/> is
        /// true when the value moved; the return value is false once the user
        /// has dismissed the keyboard and the field should drop focus.
        /// </summary>
        public bool Poll(TextEditingModel model, out bool changed)
        {
            changed = false;
            if (_keyboard == null) return false;

            var status = _keyboard.status;
            string current = _keyboard.text ?? string.Empty;
            if (!string.Equals(current, _lastText, System.StringComparison.Ordinal))
            {
                _lastText = current;
                // The caret only means anything while the panel is up; after
                // Done the platforms disagree about what selection reports.
                var selection = status == TouchScreenKeyboard.Status.Visible
                    ? _keyboard.selection
                    : new RangeInt(current.Length, 0);
                changed = model.SetExternalText(current, selection.start, selection.length);
            }

            return status == TouchScreenKeyboard.Status.Visible;
        }
    }
}
