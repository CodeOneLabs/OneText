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
    /// not fail to find it; it fails to compile.
    ///
    /// The Input System pushes composition at us instead of letting us poll it,
    /// so the string is cached as it arrives and read back on the field's own
    /// update. Ending a composition arrives as an empty string, which is
    /// exactly the signal the field is waiting for.
    ///
    /// One instance per process, not one per field, and that is the difference
    /// between this and the shape it had. Everything this backend touches is
    /// process-wide: there is one <see cref="Keyboard"/>, one IME switch on it,
    /// and one composition being reported. A per-field instance made each field
    /// think that switch was its own, and the state that came out of that is
    /// the same class of bug the arbiter was made shared to fix — see
    /// <c>ImeCommitArbiter.Shared</c>, which carries the longer argument.
    /// </summary>
    internal sealed class InputSystemImeInput : IImeInput
    {
        // The one backend, handed to every field that asks.
        private static readonly InputSystemImeInput s_shared = new InputSystemImeInput();

        private readonly StringBuilder _builder = new StringBuilder();
        private string _composition = string.Empty;

        // The device the subscription is actually on, rather than a bool saying
        // that one exists. Keyboard.current is not a constant: unplug a
        // keyboard, switch users on a remote session, or let the Input System
        // re-create the device for any of the reasons it does, and the object
        // this subscribed to stops being the object the platform reports
        // through. A bool cannot tell that from being subscribed correctly, and
        // what it looks like when it happens is composition silently never
        // arriving again for the rest of the session.
        private Keyboard _listeningTo;

        // How many fields are in an editing session. Not a bool, because
        // SetIMEEnabled is a process-wide switch and the fields do not take
        // turns as tidily as they look like they do: a panel being hidden runs
        // OnDisable on a field that has not been focused for ten minutes, and
        // it used to switch the IME off underneath whichever field was
        // composing at that moment. Only the last field out turns it off.
        private int _sessions;

        public bool IsAvailable => Keyboard.current != null;

        public void Begin()
        {
            _sessions++;
            // Whatever the platform was reporting, it was reporting to a field
            // that is not this one. A composition must never survive into a
            // session that did not start it — the same rule ImeCommitArbiter
            // clears itself for at the start of a play session.
            _composition = string.Empty;
            Listen(Keyboard.current);
        }

        public void End()
        {
            if (_sessions > 0) _sessions--;
            // Somebody else is still editing. Their Begin already turned the
            // switch on and their session is what it belongs to now.
            if (_sessions > 0) return;

            Unlisten(disableIme: true);
            _composition = string.Empty;
        }

        public void SetCursorScreenPosition(Vector2 screenPosition) =>
            Keyboard.current?.SetIMECursorPosition(screenPosition);

        /// <summary>
        /// False: this is a cache of the compositions the platform pushed, and
        /// <see cref="End"/> empties it. An empty one means no event has
        /// arrived, which is not the same as the platform holding nothing.
        /// </summary>
        public bool ReportsPlatformState => false;

        /// <summary>
        /// The same platform fact as the other backend's: it is about the IMM,
        /// not about which stack the composition arrives through.
        /// </summary>
        public bool PlatformPaysEveryCommit => ImeInput.PlatformPaysEveryCommit;

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
        /// Moves the subscription onto <paramref name="keyboard"/>, switching
        /// the IME on there. Idempotent: the field calls Begin on every focus
        /// and the device is usually the same one.
        /// </summary>
        private void Listen(Keyboard keyboard)
        {
            if (ReferenceEquals(keyboard, _listeningTo)) return;

            // The old device loses the subscription but not the switch: it may
            // be gone, and telling a device that no longer exists to disable
            // its IME is at best nothing.
            Unlisten(disableIme: false);
            if (keyboard == null) return;

            keyboard.onIMECompositionChange += OnCompositionChange;
            keyboard.SetIMEEnabled(true);
            _listeningTo = keyboard;

            // Watched only while there is something to watch for. A device
            // change with no field editing is not this backend's business.
            UnityEngine.InputSystem.InputSystem.onDeviceChange -= OnDeviceChange;
            UnityEngine.InputSystem.InputSystem.onDeviceChange += OnDeviceChange;
        }

        private void Unlisten(bool disableIme)
        {
            UnityEngine.InputSystem.InputSystem.onDeviceChange -= OnDeviceChange;
            if (_listeningTo == null) return;

            _listeningTo.onIMECompositionChange -= OnCompositionChange;
            // Off rather than left on, because on means every keystroke in the
            // game goes through the input method once nobody is editing. The
            // Input Manager backend can hand the decision back to Unity with
            // IMECompositionMode.Auto and does; there is no Auto here, so the
            // switch is ours to put back.
            if (disableIme) _listeningTo.SetIMEEnabled(false);
            _listeningTo = null;
        }

        /// <summary>
        /// Follows <see cref="Keyboard.current"/> when the Input System
        /// replaces it. Without this the subscription stays on a device the
        /// platform has stopped reporting through, and composition never
        /// arrives again — for the rest of the session, with nothing said.
        /// </summary>
        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (!(device is Keyboard) || _sessions <= 0) return;
            var current = Keyboard.current;
            if (ReferenceEquals(current, _listeningTo)) return;

            _composition = string.Empty;
            Listen(current);
        }

        /// <summary>
        /// Offers this backend to <see cref="ImeInput"/> — but only where the
        /// built-in one cannot run, which so far is nowhere.
        ///
        /// This backend used to register unconditionally and therefore always
        /// won, and that is what broke Korean in every project using the Input
        /// System. Not because it is wrong, but because it is half of a pair:
        /// OneText reads its characters out of the IMGUI event queue, and
        /// composition has to come from the input method feeding that queue. On
        /// macOS, switching the IME on through the Input System device left the
        /// IMGUI path uncomposed, this channel reported nothing at all — zero
        /// events across a whole recorded typing session — and the platform
        /// handed over every jamo already committed, so 안녕하세요 arrived as
        /// ㅇㅏㄴㄴㅕㅇㅎㅏㅅㅔㅇㅛ.
        ///
        /// It is kept rather than deleted because the reason the built-in one
        /// works is a measurement, not a guarantee: the input-method members of
        /// UnityEngine.Input are exempt from the Active Input Handling guard on
        /// every version and platform that has been checked, and this is what
        /// answers on a version or platform where they are not.
        ///
        /// Registration also clears the shared state, and that is not belt and
        /// braces: a static survives a play session when Enter Play Mode
        /// Options are set to skip the domain reload, so without this the
        /// second run starts holding a session count from the first and a
        /// subscription to a device that no longer exists.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            s_shared.Forget();
            if (ImeInput.PlatformImeAnswers()) return;
            ImeInput.Register(() => s_shared);
        }

        private void Forget()
        {
            _sessions = 0;
            _composition = string.Empty;
            // Through Unlisten rather than by nulling the field: without a
            // domain reload the device object outlives the play session too,
            // and a subscription dropped by forgetting the reference to it is
            // a subscription that is still there, called twice on the next run.
            // The switch is left alone — this is a session starting, and
            // nobody has taken focus yet to want it on or off.
            Unlisten(disableIme: false);
        }
    }
}
#endif
