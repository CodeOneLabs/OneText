using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OneText
{
    /// <summary>
    /// Decides who owns a composition once the input method lets go of it.
    ///
    /// A composition ends in one of two ways, and which one you get depends on
    /// the platform, the IME and, for the bug reports that made this milestone,
    /// on whether the field kept focus. Either the platform delivers the
    /// composed text a second time as ordinary character events, or it delivers
    /// nothing at all and the last thing the user typed disappears. A field
    /// that assumes the first loses characters; a field that assumes the second
    /// doubles them. Both assumptions are wrong somewhere, so this waits.
    ///
    /// Two directions, one grace window:
    ///
    /// <list type="bullet">
    /// <item><see cref="AwaitPlatformCommit"/>: the composition cleared on its
    /// own. If characters arrive before the window closes, they are the commit
    /// and nothing else happens. If none do, <see cref="Tick"/> hands the text
    /// back and the field inserts it.</item>
    /// <item><see cref="SuppressEchoOf"/>: the field finished with the
    /// composition itself — committed it because focus was leaving and nobody
    /// else would, or abandoned it on Escape or an assignment to the value.
    /// Characters that arrive afterwards are the platform's echo of the same
    /// text and are swallowed, one matching character at a time, and so is the
    /// composition itself if the platform reports it again.</item>
    /// </list>
    ///
    /// The window is counted in editor/player updates rather than seconds: the
    /// echo, when it comes, comes on the same frame or the next one, and a
    /// wall-clock timeout would make the behaviour depend on the frame rate.
    ///
    /// There is a third thing, off to one side of those two, and it is the one
    /// the bug report "the last Korean character is entered twice when I resume
    /// typing" turned out to be. When the field commits a composition itself,
    /// it has no way to tell the platform. <c>IImeInput</c> can start a session
    /// and end one; ending it is <c>imeCompositionMode = Auto</c> on one backend
    /// and <c>SetIMEEnabled(false)</c> on the other, and neither is documented
    /// to do anything to a composition already in flight — the second is a
    /// command handed to the native backend, whose answer is the platform's and
    /// is not reported back. So the platform may well go on holding the syllable
    /// we already took, and the next poll reads it as a brand new composition
    /// and draws it a second time. <see cref="ShouldSwallowComposition"/> refuses
    /// that replay the way <see cref="ShouldSwallow(char)"/> refuses an echoed
    /// character; the two together are what makes a self-commit final on both
    /// channels rather than only on one.
    ///
    /// The replay is deliberately not on the update clock, and while one is
    /// registered the echo window is not either. A platform holds its
    /// composition until the user does something about it, which is seconds and
    /// not frames, and until it lets go it has not sent the character it owes
    /// us either. So both guards wait for evidence instead of for a countdown:
    /// a composition report that differs, or any character at all, means the
    /// platform moved on, and only then does the clock start.
    ///
    /// And a fourth thing, which a recording of macOS finally showed and six
    /// rounds of reasoning had not: the platform can deliver the same commit
    /// twice. When focus leaves on the frame the composition ends, the syllable
    /// arrives once immediately and again on the first keystroke after the
    /// field is focused a second time. <see cref="IsThePlatformSayingItAgain"/>
    /// is that one, and it is the opposite direction from
    /// <see cref="SuppressEchoOf"/>: there the field committed and the platform
    /// may repeat it, here the platform committed, the field accepted, and the
    /// platform repeated itself.
    ///
    /// Waiting for evidence rather than for silence is what makes this work on
    /// both backends. <c>InputSystemImeInput</c> is not a poll of the platform
    /// but a cache of what it last pushed, and ending a session empties that
    /// cache — so the updates just after focus returns report no composition
    /// whether the platform has one or not, and a replay reaches us only when
    /// the next composition event fires. Silence from that backend is "no news"
    /// and never "released", so nothing here may retire on it.
    /// </summary>
    public sealed class ImeCommitArbiter
    {
        /// <summary>Updates a commit is given to show up before we act for it.</summary>
        public const int DefaultGraceUpdates = 2;

        /// <summary>
        /// The one arbiter, because there is one input method.
        ///
        /// This was a field on <see cref="TextEditingModel"/> for four rounds of
        /// fixes — one per input field — and the mismatch that hid in that is
        /// the half of the bug report no per-field guard could ever have
        /// caught. What the platform is still holding is a fact about the
        /// process, not about whichever field happened to be focused when the
        /// syllable started. Leave one composing in a name field, move to the
        /// address field without finishing it, and the platform hands it to
        /// whoever polls next: a field that, keeping a register of its own, had
        /// never heard of the syllable, adopted it, and drew a character nobody
        /// typed into a box nobody typed it in.
        ///
        /// Sharing the debt along with the guards is safe, and the invariant is
        /// worth stating because it is what makes this simple: text the
        /// platform owes us is always settled by the field that incurred it,
        /// because <c>EndEditing</c> flushes the window before focus can move
        /// and <c>OnDisable</c> ends editing. No field can be handed another
        /// field's owed syllable to insert.
        /// </summary>
        public static ImeCommitArbiter Shared { get; } = new ImeCommitArbiter();

        private enum Mode
        {
            Idle,

            /// <summary>The IME dropped the composition; we expect character events.</summary>
            AwaitingPlatform,

            /// <summary>We inserted the text; character events would be a duplicate.</summary>
            SuppressingEcho,
        }

        private Mode _mode;
        private string _pending = string.Empty;
        private int _updatesLeft;

        // The echo as it arrives, rather than an index into _pending. The
        // platform is not obliged to hand a syllable back in the shape it
        // reported composing it — macOS routinely delivers 한 as its three
        // conjoining jamo where the composition said U+D55C — and three
        // characters cannot be matched against one by index, at any position.
        // Accumulating and comparing the accumulation is the only shape that
        // works for both, and it costs one small string per echo.
        private string _echo = string.Empty;

        // Outside the mode machine on purpose, and outside Reset with it: this
        // is a fact about the platform, not a window we are counting down, and
        // it has to outlive both the grace window and the editing session. The
        // field that commits on its way out of focus stops ticking the instant
        // it does, and the platform is still holding that composition when the
        // user clicks back in a second later.
        private string _replay;

        // Decomposed forms of the two strings every comparison here is against,
        // computed once where they are set rather than once per poll. The
        // composition channel asks the same question every frame while a replay
        // is being refused, and normalizing on that path would allocate every
        // frame for an answer that cannot have changed.
        private string _pendingDecomposed = string.Empty;
        private string _replayDecomposed;

        // The last commit the platform made and this field accepted, which the
        // platform may make again — see IsThePlatformSayingItAgain. Outside the
        // mode machine and outside Reset with it, like the replay register and
        // for the same reason: the repeat arrives across a focus gap, long
        // after every window has closed.
        private string _platformCommit;
        private string _platformCommitDecomposed;
        private string _repeatSoFar = string.Empty;

        /// <summary>How many updates the grace window lasts.</summary>
        public int GraceUpdates { get; set; } = DefaultGraceUpdates;

        /// <summary>True while we are still waiting to see whether the platform commits.</summary>
        public bool IsAwaitingPlatform => _mode == Mode.AwaitingPlatform;

        /// <summary>True while incoming characters may be an echo of what we already inserted.</summary>
        public bool IsSuppressingEcho => _mode == Mode.SuppressingEcho;

        public bool IsIdle => _mode == Mode.Idle;

        /// <summary>The text at stake, in either direction.</summary>
        public string PendingText => _pending;

        /// <summary>
        /// The composition the platform is believed to be still holding after
        /// the field committed or abandoned it; null when there is none.
        /// </summary>
        public string ReplayedComposition => _replay;

        /// <summary>
        /// The last commit the platform made and this field accepted; null when
        /// there is none. A second delivery of exactly this is the platform
        /// repeating itself. See <see cref="IsThePlatformSayingItAgain"/>.
        /// </summary>
        public string AcceptedPlatformCommit => _platformCommit;

        /// <summary>
        /// The input method cleared a non-empty composition without our asking.
        /// Either the characters are on their way or they were never sent.
        /// </summary>
        public void AwaitPlatformCommit(string composed)
        {
            if (string.IsNullOrEmpty(composed)) { Reset(); return; }
            _mode = Mode.AwaitingPlatform;
            _pending = composed;
            _pendingDecomposed = Decomposed(composed);
            _echo = string.Empty;
            _updatesLeft = GraceUpdates;

            // A composition ending is the platform announcing a commit, and it
            // is the only thing that can put a syllable on the character
            // channel. So it is also what retires the last one: a user typing
            // 한 twice gets here twice, with a whole composition in between,
            // and the second 한 is theirs. A repeat of the first arrives with
            // no such announcement, which is exactly what tells the two apart.
            ForgetPlatformCommit();
        }

        /// <summary>
        /// The field finished with <paramref name="committed"/> itself, by
        /// committing it or by abandoning it, and the platform was not told.
        /// Swallow the platform's version of it if one turns up — as a
        /// character, or as the same composition reported all over again.
        ///
        /// The one place the replay register is set, because there is only one
        /// thing that puts the two sides out of step: the field deciding on its
        /// own that a composition is over. Nothing clears the register but
        /// evidence from the platform, so an onEndEdit listener that assigns a
        /// tidied-up value one line after the commit no longer takes the guard
        /// down with it.
        /// </summary>
        public void SuppressEchoOf(string committed)
        {
            if (string.IsNullOrEmpty(committed)) { Reset(); return; }
            _mode = Mode.SuppressingEcho;
            _pending = committed;
            _pendingDecomposed = Decomposed(committed);
            _echo = string.Empty;
            _updatesLeft = GraceUpdates;
            RegisterReplay(committed);
        }

        /// <summary>
        /// Registers a composition the field stopped believing in without the
        /// platform being told, when no character is owed either way: the
        /// platform handed the text over on the character channel while still
        /// reporting it as a composition, so there is an echo to refuse but no
        /// echo to swallow.
        ///
        /// Registering nothing disturbs nothing. A caller with no composition
        /// to hand over is not evidence that the platform released the one it
        /// may still be holding.
        /// </summary>
        public void IgnoreReplayOf(string composition)
        {
            if (!string.IsNullOrEmpty(composition)) RegisterReplay(composition);
        }

        /// <summary>
        /// The one place <see cref="_replay"/> moves, so that the decomposed
        /// form it is compared against can never be left behind by it.
        /// </summary>
        private void RegisterReplay(string composition)
        {
            _replay = string.IsNullOrEmpty(composition) ? null : composition;
            _replayDecomposed = _replay == null ? null : Decomposed(_replay);
        }

        /// <summary>
        /// Offers a character event to the arbiter. Returns true when the field
        /// must drop it because it duplicates text we already inserted.
        /// </summary>
        public bool ShouldSwallow(char character)
        {
            // The replay register is deliberately not touched here, and an
            // earlier version of this method that cleared it was the bug
            // report coming back. A character is not evidence that the platform
            // let go of what it is showing: at a Hangul syllable boundary the
            // platform commits the finished syllable as a character and starts
            // composing the next one in the same step, which is the whole
            // reason ProcessKeyEvent accepts characters while composing. On the
            // Input Manager backend, where the report is the platform's live
            // state rather than a cache, clearing the register on the echo left
            // the very next poll free to adopt the composition still being held
            // — one syllable, arriving from nowhere. Only the composition
            // channel can retire a replay, because only it says what the
            // platform is actually holding.
            switch (_mode)
            {
                case Mode.AwaitingPlatform:
                    // Something arrived, so the platform is delivering after
                    // all. Let it through and stand down, even if it is not
                    // the text we were holding, because a character after a
                    // composition means the composition was committed, and
                    // inserting our copy on top of it is the duplicate bug.
                    //
                    // And remember what was committed on the way through: this
                    // is the delivery the platform may make a second time.
                    _platformCommit = _pending;
                    _platformCommitDecomposed = _pendingDecomposed;
                    _repeatSoFar = string.Empty;
                    Reset();
                    return false;

                case Mode.SuppressingEcho:
                    // The echo is matched as an accumulation rather than by
                    // index, because the platform is free to hand a syllable
                    // back in a different shape from the one it composed it in.
                    // Both sides are compared decomposed, and that is the form
                    // rather than composed for a reason worth keeping: only in
                    // the decomposed form is a half-delivered syllable a prefix
                    // of the whole one. Composed, the first jamo of an echo of
                    // 한 is neither equal to it nor a prefix of it, and the
                    // guard would stand down in the middle of the echo it was
                    // there to swallow.
                    _echo += character;
                    string echo = Decomposed(_echo);

                    if (string.Equals(echo, _pendingDecomposed, StringComparison.Ordinal))
                    {
                        // All of it, however many events it took.
                        Reset();
                        return true;
                    }

                    if (_pendingDecomposed.StartsWith(echo, StringComparison.Ordinal))
                        return true; // still arriving

                    // Not our echo: the user typed something new, and anything
                    // still owed to us is not coming.
                    Reset();
                    return false;

                default:
                    return IsThePlatformSayingItAgain(character);
            }
        }

        /// <summary>
        /// Whether this character is the platform delivering, a second time, a
        /// commit the field has already accepted from it.
        ///
        /// The bug this is for, read off a recording rather than guessed at.
        /// When focus leaves on the same frame the composition ends, macOS
        /// delivers the committed syllable twice: once there and then, which
        /// the field takes and inserts correctly, and once more on the first
        /// keystroke after the field is focused again — a hundred frames later,
        /// across a gap in which the field was not running at all, and in the
        /// same frame as the composition for the syllable the user is actually
        /// typing now. Pressing Enter avoids it because the composition then
        /// ends long before focus moves, and there is only ever one delivery.
        ///
        /// This is not <see cref="SuppressEchoOf"/>, which guards the other
        /// direction: there the field committed and the platform may say the
        /// same thing afterwards. Here the platform committed, the field
        /// accepted it, and the platform said it again.
        ///
        /// Nothing about it is on a clock, for the same reason nothing else
        /// here is any more: a hundred frames passed, and the field spent some
        /// of them not ticking. It retires on evidence instead — the platform
        /// announcing a fresh commit, or a character that is not this one.
        /// </summary>
        private bool IsThePlatformSayingItAgain(char character)
        {
            if (_platformCommit == null) return false;

            _repeatSoFar += character;
            string repeat = Decomposed(_repeatSoFar);

            if (string.Equals(repeat, _platformCommitDecomposed, StringComparison.Ordinal))
            {
                // Said in full. The register stays armed rather than retiring:
                // a syllable can only reach this field again through a
                // composition, and a composition ending is what retires it.
                _repeatSoFar = string.Empty;
                return true;
            }

            if (_platformCommitDecomposed.StartsWith(repeat, StringComparison.Ordinal))
                return true; // still arriving, the way a multi-character commit does

            // Something else: the user is typing, and whatever the platform
            // said before is behind us.
            ForgetPlatformCommit();
            return false;
        }

        private void ForgetPlatformCommit()
        {
            _platformCommit = null;
            _platformCommitDecomposed = null;
            _repeatSoFar = string.Empty;
        }

        /// <summary>
        /// Offers the composition the platform is reporting. Returns true when
        /// the field must refuse it because it is the platform replaying one
        /// the field already finished with.
        ///
        /// Only this channel retires a refusal, and it takes ordinal equality
        /// with the whole registered string. That leaves one thing a user can
        /// lose: a composition typed in a single keystroke, identical to the
        /// one the field just took, is refused until the platform reports
        /// something else — which the next keystroke does, since a syllable
        /// cannot be built without changing the string. So the cost is that the
        /// first keystroke of it is not drawn until the second, and only if the
        /// user leaves the field in between is it lost.
        ///
        /// Since the register became <see cref="Shared"/> that cost is shared
        /// too: the keystroke refused may be the first one typed into a
        /// <em>different</em> field, if the syllable abandoned in the last one
        /// happened to be the same single keystroke. It is the same bounded
        /// glitch rather than a new one — one keystroke, undrawn until the
        /// next — but it is no longer confined to the field that caused it,
        /// and that is the price of the register being able to see the case it
        /// exists for.
        ///
        /// That asymmetry is the whole reason to be conservative here. Refusing
        /// a composition the user meant can delay drawing a keystroke; adopting
        /// one the platform is only repeating puts a character in the value
        /// that nobody typed. The first is a glitch, the second is the bug
        /// report, so everything ambiguous is refused.
        /// </summary>
        public bool ShouldSwallowComposition(string composing)
        {
            if (_replay == null) return false;

            // The same composition still: the platform has not let go, so this
            // is it repeating itself and not the user typing. "The same" is
            // asked of the text and not of the code units, because the report
            // and the register can reach us through different channels and in
            // different shapes.
            if (SameText(composing, _replay, _replayDecomposed)) return true;

            // Anything else is the user, and the platform can only have got
            // there by finishing what it was holding. The echo for the text we
            // committed lands this update or the next one — the field polls
            // before it drains the key queue — so the window starts counting
            // from here rather than from the commit.
            RegisterReplay(null);
            return false;
        }

        /// <summary>
        /// Advances the grace window by one update. Returns the text the field
        /// must insert itself (the platform never sent it), or null.
        /// </summary>
        public string Tick()
        {
            if (_mode == Mode.Idle) return null;

            // A window that cannot see the platform must not close on it.
            // While the composition we committed is still registered as
            // unreleased, the echo it owes has not been sent and the updates
            // going past are not evidence of anything — least of all on a
            // backend whose cache was emptied when the session ended, where
            // every update in the gap reports nothing at all. Counting there is
            // how the guard came down before the duplicate arrived.
            if (_mode == Mode.SuppressingEcho && _replay != null) return null;

            if (--_updatesLeft > 0) return null;
            return Flush();
        }

        /// <summary>
        /// Closes the window now rather than on a later update, and returns any
        /// text the platform still owed. The field calls this as focus leaves:
        /// a commit addressed to a field nobody is typing into is never coming.
        /// </summary>
        public string Flush()
        {
            string owed = _mode == Mode.AwaitingPlatform ? _pending : null;
            Reset();
            return owed;
        }

        /// <summary>
        /// Drops whatever was pending: the composition was cancelled (Escape,
        /// or a field that stopped editing), so nobody owes anybody text.
        ///
        /// What the platform is still holding is a different question and this
        /// does not answer it. Cancelling is something the field decided; the
        /// platform was not consulted about that either, so
        /// <see cref="ReplayedComposition"/> stands until the platform itself
        /// shows it has moved on.
        /// </summary>
        public void Cancel() => Reset();

        /// <summary>
        /// Everything, the replay register included: nobody is composing,
        /// nobody is owed, and whatever the platform was holding is no longer
        /// this process's business.
        ///
        /// Shared state has to be able to say that, because the moments when it
        /// stops being true are not moments any field observes — see the resets
        /// below, and note that a register carried into a new play session with
        /// a stale syllable in it would refuse the first thing the user typed.
        /// </summary>
        public void Forget()
        {
            Reset();
            RegisterReplay(null);
            ForgetPlatformCommit();
        }

        // ------------------------------------------------------- session ends
        //
        // Three of them, because a static outlives more than people expect.
        // Domain reload takes care of itself by rebuilding the class; the other
        // two do not, and the one that matters most is entering play mode with
        // reload turned off, which is the default advice for iteration speed
        // and the case where yesterday's syllable is still in the register.

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ForgetOnPlaySession()
        {
            Shared.Forget();
            // Minus before plus: with domain reload off this method runs once
            // per play session against a delegate list that survived the last
            // one, and a handler added twice would forget twice per scene.
            SceneManager.sceneLoaded -= ForgetOnSceneLoad;
            SceneManager.sceneLoaded += ForgetOnSceneLoad;
        }

        private static void ForgetOnSceneLoad(Scene scene, LoadSceneMode mode) => Shared.Forget();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void WatchPlayModeInEditor()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= ForgetOnPlayModeChange;
            UnityEditor.EditorApplication.playModeStateChanged += ForgetOnPlayModeChange;
        }

        private static void ForgetOnPlayModeChange(UnityEditor.PlayModeStateChange change)
        {
            // Both ends of leaving: a register armed by the last thing typed in
            // play mode has no business in edit mode, where OnValidate drives
            // the same model with no input method behind it at all.
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode ||
                change == UnityEditor.PlayModeStateChange.EnteredEditMode)
                Shared.Forget();
        }
#endif

        // ------------------------------------------------------- the same text
        //
        // Everything above compares a string that came from one channel with a
        // string that came from another, and the platform does not promise they
        // will arrive in the same shape. macOS composes 한 as U+D55C and is
        // perfectly entitled to hand it back as U+1112 U+1161 U+11AB, which is
        // the same syllable and not the same string. Ordinal equality answers
        // "no" to that, silently, and while looking identical in any console
        // anyone would check it in — so every comparison in this file goes
        // through here instead.

        /// <summary>
        /// Whether two strings from the input method are the same text, in the
        /// sense that matters here: the same syllables, however either side
        /// happens to be encoded.
        ///
        /// Exposed because <see cref="TextEditingModel"/> asks the same
        /// question of the characters handed over against the composition they
        /// pay for, and that comparison has to agree with these ones or the two
        /// halves of the guard disagree about what happened.
        /// </summary>
        public static bool SameText(string left, string right) =>
            SameText(left, right, right == null ? null : Decomposed(right));

        private static bool SameText(string left, string right, string rightDecomposed)
        {
            // Free, and the answer almost every time: two sides already in the
            // same shape never reach the normalizer, so nothing about a project
            // that never sees a decomposed composition changes, cost included.
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            return string.Equals(Decomposed(left), rightDecomposed, StringComparison.Ordinal);
        }

        // A one-entry memo of a pure function. The composition channel asks
        // about the same string on every frame that a replay is refused, so
        // without this the refusal would allocate a string per frame for an
        // answer it already had.
        private static string s_memoRaw;
        private static string s_memoDecomposed;

        /// <summary>
        /// <paramref name="value"/> with its syllables taken apart, which is the
        /// form both channels can be compared in.
        ///
        /// Decomposed rather than composed because one comparison here is a
        /// prefix test — see the echo in <see cref="ShouldSwallow(char)"/> — and
        /// half of a composed syllable is not a prefix of it. Nothing is stored
        /// in this form; the value the field holds is whatever the platform
        /// sent, untouched.
        /// </summary>
        private static string Decomposed(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (string.Equals(value, s_memoRaw, StringComparison.Ordinal)) return s_memoDecomposed;

            string decomposed;
            try
            {
                decomposed = value.Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // A composition caught mid-keystroke can hold half a surrogate
                // pair, and normalizing that throws. Compare it as it stands
                // rather than taking an input method's bad frame as an
                // exception in the middle of typing.
                decomposed = value;
            }

            s_memoRaw = value;
            s_memoDecomposed = decomposed;
            return decomposed;
        }

        /// <summary>
        /// Ends the grace window. Leaves <see cref="ReplayedComposition"/>
        /// alone: what the platform is still holding does not stop being true
        /// because our window ran out.
        /// </summary>
        private void Reset()
        {
            _mode = Mode.Idle;
            _pending = string.Empty;
            _pendingDecomposed = string.Empty;
            _echo = string.Empty;
            _updatesLeft = 0;
        }
    }
}
