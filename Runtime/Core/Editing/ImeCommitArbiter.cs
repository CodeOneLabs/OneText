namespace OneText
{
    /// <summary>
    /// Decides who owns a composition once the input method lets go of it.
    ///
    /// A composition ends in one of two ways, and which one you get depends on
    /// the platform, the IME and — for the bug reports that made this milestone
    /// — on whether the field kept focus. Either the platform delivers the
    /// composed text a second time as ordinary character events, or it delivers
    /// nothing at all and the last thing the user typed disappears. A field
    /// that assumes the first loses characters; a field that assumes the second
    /// doubles them. Both assumptions are wrong somewhere, so this waits.
    ///
    /// Two directions, one grace window:
    ///
    /// <list type="bullet">
    /// <item><see cref="AwaitPlatformCommit"/> — the composition cleared on its
    /// own. If characters arrive before the window closes, they are the commit
    /// and nothing else happens. If none do, <see cref="Tick"/> hands the text
    /// back and the field inserts it.</item>
    /// <item><see cref="SuppressEchoOf"/> — the field committed the composition
    /// itself, because focus was leaving and nobody else would. Characters that
    /// arrive afterwards are the platform's echo of the same text and are
    /// swallowed, one matching character at a time.</item>
    /// </list>
    ///
    /// The window is counted in editor/player updates rather than seconds: the
    /// echo, when it comes, comes on the same frame or the next one, and a
    /// wall-clock timeout would make the behaviour depend on the frame rate.
    /// </summary>
    public sealed class ImeCommitArbiter
    {
        /// <summary>Updates a commit is given to show up before we act for it.</summary>
        public const int DefaultGraceUpdates = 2;

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
        private int _matched;
        private int _updatesLeft;

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
        /// The input method cleared a non-empty composition without our asking.
        /// Either the characters are on their way or they were never sent.
        /// </summary>
        public void AwaitPlatformCommit(string composed)
        {
            if (string.IsNullOrEmpty(composed)) { Reset(); return; }
            _mode = Mode.AwaitingPlatform;
            _pending = composed;
            _matched = 0;
            _updatesLeft = GraceUpdates;
        }

        /// <summary>
        /// We committed <paramref name="committed"/> ourselves. Swallow the
        /// platform's version of it if one turns up.
        /// </summary>
        public void SuppressEchoOf(string committed)
        {
            if (string.IsNullOrEmpty(committed)) { Reset(); return; }
            _mode = Mode.SuppressingEcho;
            _pending = committed;
            _matched = 0;
            _updatesLeft = GraceUpdates;
        }

        /// <summary>
        /// Offers a character event to the arbiter. Returns true when the field
        /// must drop it because it duplicates text we already inserted.
        /// </summary>
        public bool ShouldSwallow(char character)
        {
            switch (_mode)
            {
                case Mode.AwaitingPlatform:
                    // Something arrived, so the platform is delivering after
                    // all. Let it through and stand down — even if it is not
                    // the text we were holding, because a character after a
                    // composition means the composition was committed, and
                    // inserting our copy on top of it is the duplicate bug.
                    Reset();
                    return false;

                case Mode.SuppressingEcho:
                    if (character == _pending[_matched])
                    {
                        if (++_matched >= _pending.Length) Reset();
                        return true;
                    }
                    // Not our echo: the user typed something new, and anything
                    // still owed to us is not coming.
                    Reset();
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Advances the grace window by one update. Returns the text the field
        /// must insert itself — the platform never sent it — or null.
        /// </summary>
        public string Tick()
        {
            if (_mode == Mode.Idle) return null;
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
        /// </summary>
        public void Cancel() => Reset();

        private void Reset()
        {
            _mode = Mode.Idle;
            _pending = string.Empty;
            _matched = 0;
            _updatesLeft = 0;
        }
    }
}
