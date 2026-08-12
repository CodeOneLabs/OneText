using System;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// The platform's input method, reduced to the four things a text field
    /// needs from it: turn it on, tell it where the caret is on screen so the
    /// candidate window lands there, read what it is composing, turn it off.
    ///
    /// The interface exists so that a project which cannot reach one input
    /// method can be given another, and so that a test can drive composition
    /// by hand. It used to say here that <c>UnityEngine.Input</c> "throws
    /// outright in a project that switched to the Input System package": that
    /// is true of its device members and false of its input-method members,
    /// which is the whole of the bug <see cref="ImguiImeInput"/> now records.
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
    /// <see cref="ImguiImeInput"/> is the one that runs, in every project and
    /// under every Active Input Handling setting, because the input-method
    /// members of <c>UnityEngine.Input</c> answer under all of them and because
    /// it is the input method belonging to the same stack OneText reads its
    /// characters from. It is not referenced by name below only so that a test,
    /// or a platform where it turns out not to answer, can put something else
    /// in front of it with <see cref="Register"/>.
    ///
    /// The Input System backend cannot be referenced from here — its assembly
    /// only exists when the package is installed — so it registers itself from
    /// an assembly that is compiled out entirely when the package is absent.
    /// It now registers only where the built-in one cannot run, which today is
    /// nowhere that has been found; see the note on its Register method.
    ///
    /// A field with no backend at all still edits. It just cannot compose.
    /// </summary>
    public static class ImeInput
    {
        private static Func<IImeInput> _registered;

        // What the last Create() decided, and why. Kept because the answer is
        // otherwise invisible: a project with no backend types perfectly and
        // composes nothing, so the failure looks like "Korean is broken" from
        // every angle except the one that names the cause.
        private static string _lastChoice = "nothing has asked yet";
        private static bool _saidThereIsNoBackend;

        /// <summary>
        /// Installs a backend, ahead of the built-in one. Called by the Input
        /// System bridge, and by tests that want to drive composition by hand.
        /// </summary>
        public static void Register(Func<IImeInput> factory)
        {
            _registered = factory;
            // A backend arriving is new evidence, so the complaint about there
            // being none is allowed to be made again. Registration happens on
            // SubsystemRegistration, before any field can have asked.
            _saidThereIsNoBackend = false;
        }

        /// <summary>Forgets a registered backend, restoring the built-in choice.</summary>
        public static void Unregister()
        {
            _registered = null;
            _saidThereIsNoBackend = false;
        }

        /// <summary>
        /// Which backend the last <see cref="Create"/> picked, or why it picked
        /// none. One line, meant for a bug report or a probe: the reporter can
        /// read it out without knowing what an assembly definition is.
        /// </summary>
        public static string Describe() => _lastChoice;

        /// <summary>Creates a backend for one field, or null when none can run.</summary>
        public static IImeInput Create()
        {
            string refused = null;
            if (_registered != null)
            {
                var registered = _registered();
                if (registered != null && registered.IsAvailable)
                {
                    _lastChoice = $"{registered.GetType().Name}, registered";
                    return registered;
                }

                // Registered and refused, which is a different state from never
                // registered and has a different fix.
                refused = registered == null
                    ? "the registered backend factory returned nothing"
                    : $"{registered.GetType().Name} reports it cannot run here";
            }

            var builtIn = new ImguiImeInput();
            if (builtIn.IsAvailable)
            {
                _lastChoice = refused == null
                    ? "ImguiImeInput, the built-in one"
                    : $"ImguiImeInput, after {refused}";
                return builtIn;
            }

            _lastChoice = refused ?? "no backend registered, and UnityEngine.Input's " +
                "input-method members do not answer in this project";
            WarnThereIsNoBackend(refused);
            return null;
        }

        /// <summary>
        /// Whether <c>UnityEngine.Input</c>'s input-method members answer in
        /// this project, which is what <see cref="ImguiImeInput"/> needs and
        /// the only thing it needs.
        ///
        /// Asked by trying rather than by testing a define, because the define
        /// answers a different question. <c>ENABLE_LEGACY_INPUT_MANAGER</c>
        /// says whether the Input Manager is enabled; these members answer
        /// whether or not it is, on every configuration that has been measured,
        /// and reading the define was exactly the mistake that left Korean
        /// broken for every project using the Input System.
        ///
        /// Both the getter and the setter, because a getter-only probe would
        /// pass on a platform where only the setter is gated, and the value is
        /// put back so that asking cannot change what a field or another
        /// component had already set.
        /// </summary>
        public static bool PlatformImeAnswers()
        {
            try
            {
                var was = Input.imeCompositionMode;
                Input.imeCompositionMode = was;
                _ = Input.compositionString;
                return true;
            }
            catch (InvalidOperationException)
            {
                // The shape of "you switched Active Input Handling". Anything
                // else is not this question being answered no, so it is not
                // caught: a null reference in here is a bug to see, not a
                // reason to silently fall through to another backend.
                return false;
            }
        }

        /// <summary>
        /// Says once per session that this project cannot compose, and says
        /// which of the two shapes of that it is in.
        ///
        /// It is a warning and not a silence because of what the silence cost:
        /// a field with no backend accepts every ASCII keystroke and drops
        /// every composition, so the project looks fine to whoever built it
        /// and Korean, Japanese and Chinese produce nothing at all for whoever
        /// uses it. Nothing in the console said so, and nothing in the
        /// inspector did either. Once, because a field asks on every focus.
        /// </summary>
        private static void WarnThereIsNoBackend(string refused)
        {
            if (_saidThereIsNoBackend) return;
            _saidThereIsNoBackend = true;

            const string Header =
                "[OneText] No input method backend: this field will accept ordinary " +
                "typing and compose nothing, so Korean, Japanese and Chinese produce " +
                "no text at all. ";

            const string BuiltIn =
                "UnityEngine.Input's input-method members (imeCompositionMode, " +
                "compositionString) did not answer in this project, which is the first " +
                "configuration found where they do not: everywhere they have been " +
                "measured they answer under every Active Input Handling setting, " +
                "including \"Input System Package (New)\". Please report this one with " +
                "the Unity version, the platform, and whether it is the editor or a " +
                "player — it is the case the Input System backend exists for and it has " +
                "never been seen.";

            Debug.LogWarning(refused != null
                ? Header + refused + ", and " + BuiltIn
                : Header + BuiltIn);
        }
    }
}
