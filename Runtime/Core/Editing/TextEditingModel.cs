using System;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Everything an input field does to a string, with no scene, no canvas and
    /// no input method attached: the committed text, the caret and selection,
    /// the edits, and the composition an IME parks on top of all of it.
    ///
    /// It lives here rather than inside <c>OneTextInputField</c> because the
    /// bugs this milestone exists to avoid (a Korean syllable lost when focus
    /// moves, a backspace that eats committed text instead of the composition,
    /// a Chinese candidate list that splits a surrogate pair) are bugs about
    /// state, not about pixels, and state can be tested at a thousand cases per
    /// second. The field drives this and draws what it reports.
    ///
    /// Indices are UTF-16 code units, matching <see cref="string"/>; caret
    /// motion still steps by grapheme cluster through <see cref="TextHitTest"/>.
    /// </summary>
    public sealed class TextEditingModel
    {
        private string _text = string.Empty;
        private int _caret;
        private int _anchor;
        private ImeComposition _composition = ImeComposition.None;
        private string _displayText = string.Empty;
        private bool _displayDirty = true;

        // What the platform has handed over as characters since the live
        // composition last changed its text. See NoteHandedOver, which is where
        // the reason lives.
        private string _handedOver = string.Empty;

        /// <summary>Refuses every edit, including composition.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Maximum length of the committed text; 0 means unlimited.</summary>
        public int CharacterLimit { get; set; }

        /// <summary>
        /// Arbitrates who inserts a composition once the IME lets go.
        ///
        /// Every field's, not this one's: there is one input method and one
        /// composition, and a syllable abandoned in one field is offered back
        /// to whichever field polls next. <see cref="ImeCommitArbiter.Shared"/>
        /// carries the argument.
        /// </summary>
        public ImeCommitArbiter Arbiter => ImeCommitArbiter.Shared;

        /// <summary>The field's value. Never includes an unfinished composition.</summary>
        public string Text
        {
            get => _text;
            set
            {
                value ??= string.Empty;
                // An assignment takes the value away from the input method,
                // and the two halves of what the arbiter is holding go
                // opposite ways. Text the platform never sent must not turn up
                // inside the new value, so that debt is cancelled; text it may
                // still send must be swallowed for exactly the same reason, so
                // that guard stays up. Cancelling both is how a listener that
                // trims or uppercases the value on onEndEdit — assigning one
                // line after the commit that armed the guard — used to hand the
                // duplicate straight back.
                if (_composition.Active) Arbiter.SuppressEchoOf(_composition.Text);
                else if (Arbiter.IsAwaitingPlatform) Arbiter.Cancel();
                _composition = ImeComposition.None;
                _text = ApplyLimit(value);
                // Each end clamped on its own rather than both collapsed onto
                // the caret. A script that refills a field and expects the
                // range it selected to still be selected gets that here, the
                // way TMP_InputField.SetText gives it: only an end that now
                // points past the string moves, and a selection that still
                // fits is none of the assignment's business.
                _caret = Clamp(_caret);
                _anchor = Clamp(_anchor);
                _displayDirty = true;
            }
        }

        public int Caret => _caret;

        public int Anchor => _anchor;

        public bool HasSelection => _caret != _anchor;

        public int SelectionStart => Mathf.Min(_caret, _anchor);

        public int SelectionEnd => Mathf.Max(_caret, _anchor);

        /// <summary>The selected text, or an empty string.</summary>
        public string SelectedText =>
            HasSelection ? _text.Substring(SelectionStart, SelectionEnd - SelectionStart) : string.Empty;

        public ImeComposition Composition => _composition;

        public bool IsComposing => _composition.Active;

        /// <summary>The committed text with any composition spliced in at the caret.</summary>
        public string DisplayText
        {
            get
            {
                if (_displayDirty)
                {
                    _displayText = _composition.Active && _composition.Length > 0
                        ? _text.Insert(_composition.Start, _composition.Text)
                        : _text;
                    _displayDirty = false;
                }
                return _displayText;
            }
        }

        /// <summary>Caret position in <see cref="DisplayText"/> indices.</summary>
        public int DisplayCaret =>
            _composition.Active ? _composition.Start + _composition.Caret : _caret;

        /// <summary>Selection start in display indices; collapsed while composing.</summary>
        public int DisplaySelectionStart => _composition.Active ? DisplayCaret : SelectionStart;

        /// <summary>Selection end in display indices; collapsed while composing.</summary>
        public int DisplaySelectionEnd => _composition.Active ? DisplayCaret : SelectionEnd;

        /// <summary>The composition's range in display indices, for underlining it.</summary>
        public bool TryGetCompositionRange(out int start, out int end)
        {
            start = _composition.Start;
            end = _composition.End;
            return _composition.Active && _composition.Length > 0;
        }

        /// <summary>The converting clause's range in display indices, if there is one.</summary>
        public bool TryGetClauseRange(out int start, out int end)
        {
            start = _composition.Start + _composition.ClauseStart;
            end = start + _composition.ClauseLength;
            return _composition.Active && _composition.HasClause;
        }

        // ------------------------------------------------------------- editing

        /// <summary>Inserts text at the caret, replacing the selection. True if the value changed.</summary>
        public bool Insert(string value)
        {
            if (ReadOnly || string.IsNullOrEmpty(value)) return false;
            bool deleted = DeleteSelection();
            return InsertAtCaret(value) || deleted;
        }

        /// <summary>Deletes the selection, or one grapheme cluster before the caret.</summary>
        public bool Backspace()
        {
            if (ReadOnly) return false;
            if (HasSelection) return DeleteSelection();
            if (_caret <= 0) return false;

            int previous = TextHitTest.PreviousCaret(_text, _caret);
            _text = _text.Remove(previous, _caret - previous);
            SetCaret(previous, false);
            return true;
        }

        /// <summary>Deletes the selection, or one grapheme cluster after the caret.</summary>
        public bool ForwardDelete()
        {
            if (ReadOnly) return false;
            if (HasSelection) return DeleteSelection();
            if (_caret >= _text.Length) return false;

            int next = TextHitTest.NextCaret(_text, _caret);
            _text = _text.Remove(_caret, next - _caret);
            _displayDirty = true;
            return true;
        }

        public bool DeleteSelection()
        {
            if (ReadOnly || !HasSelection) return false;
            int start = SelectionStart;
            _text = _text.Remove(start, SelectionEnd - start);
            SetCaret(start, false);
            return true;
        }

        // ----------------------------------------------------------- selection

        public void SetCaret(int index, bool extendSelection)
        {
            _caret = Clamp(index);
            if (!extendSelection) _anchor = _caret;
            _displayDirty = true;
        }

        public void SetSelection(int anchor, int caret)
        {
            _anchor = Clamp(anchor);
            _caret = Clamp(caret);
            _displayDirty = true;
        }

        public void SelectAll() => SetSelection(0, _text.Length);

        /// <summary>Moves the caret by one grapheme cluster, or one word.</summary>
        public void MoveHorizontally(int direction, bool byWord, bool extendSelection)
        {
            // A plain arrow key over a selection collapses it to the edge it
            // points at rather than moving from the caret.
            if (!extendSelection && HasSelection && !byWord)
            {
                SetCaret(direction < 0 ? SelectionStart : SelectionEnd, false);
                return;
            }

            int next = direction < 0
                ? (byWord ? TextHitTest.PreviousWord(_text, _caret) : TextHitTest.PreviousCaret(_text, _caret))
                : (byWord ? TextHitTest.NextWord(_text, _caret) : TextHitTest.NextCaret(_text, _caret));
            SetCaret(next, extendSelection);
        }

        // ---------------------------------------------------------- composition

        /// <summary>
        /// Reports what the input method is currently composing. An empty
        /// string ends the composition and hands the question of who commits it
        /// to <see cref="Arbiter"/>.
        /// </summary>
        /// <param name="composing">The composition text, as the IME reports it.</param>
        /// <param name="caretInComposition">Caret offset inside it; -1 means "at the end".</param>
        /// <param name="clauseStart">Converting clause offset, for Japanese conversion.</param>
        /// <param name="clauseLength">Converting clause length; 0 when the IME reports none.</param>
        /// <returns>True when the committed text changed (a selection was replaced).</returns>
        public bool SetComposition(string composing, int caretInComposition = -1,
            int clauseStart = 0, int clauseLength = 0)
        {
            composing ??= string.Empty;

            // A read-only field refuses composition outright: there is no state
            // in which it should show text it will never accept.
            if (ReadOnly)
            {
                CancelComposition();
                return false;
            }

            // A composition this field already finished with, still being
            // reported by a platform that was never told. Adopting it would
            // draw the syllable we just committed a second time and commit it
            // again behind that — which is the bug report about the last
            // Korean character being entered twice when input resumes.
            if (!_composition.Active && Arbiter.ShouldSwallowComposition(composing)) return false;

            bool textChanged = false;
            if (!_composition.Active)
            {
                if (composing.Length == 0) return false;
                textChanged = DeleteSelection(); // composing over a selection replaces it
                _composition.Active = true;
                _composition.Start = _caret;
            }

            if (composing.Length == 0)
            {
                string previous = _composition.Text;
                EndComposition();
                Arbiter.AwaitPlatformCommit(previous);
                return textChanged;
            }

            // A composition that says something new is a composition nothing
            // has been handed over for yet.
            // Ordinal on purpose, unlike the comparisons that ask whether two
            // strings are the same text: this one asks whether the platform's
            // report changed, and a report that changed shape changed.
            if (!string.Equals(_composition.Text, composing, StringComparison.Ordinal))
                _handedOver = string.Empty;

            _composition.Text = composing;
            _composition.Caret = caretInComposition < 0
                ? composing.Length
                : BoundaryBefore(composing, Mathf.Min(caretInComposition, composing.Length));
            _composition.ClauseStart = BoundaryBefore(composing, Mathf.Clamp(clauseStart, 0, composing.Length));
            _composition.ClauseLength = BoundaryBefore(composing,
                Mathf.Clamp(_composition.ClauseStart + Mathf.Max(0, clauseLength), 0, composing.Length))
                - _composition.ClauseStart;
            _displayDirty = true;
            return textChanged;
        }

        /// <summary>
        /// Commits the composition into the text now, because nobody else will:
        /// focus is leaving, or the application asked. The platform's own
        /// commit, if it ever arrives, is swallowed as an echo.
        /// </summary>
        public bool CommitComposition()
        {
            if (!_composition.Active) return false;

            string composed = _composition.Text;
            EndComposition();
            if (composed.Length == 0 || ReadOnly) return false;

            bool changed = InsertAtCaret(composed);
            Arbiter.SuppressEchoOf(composed);
            return changed;
        }

        /// <summary>Drops the composition without committing anything.</summary>
        public void CancelComposition()
        {
            bool wasActive = _composition.Active;
            string abandoned = _composition.Text;
            EndComposition();

            // Nobody owes us this text, and we owe nobody it either — but the
            // platform may not have heard that it is over, and there is nothing
            // in IImeInput that can tell it. So both channels are guarded: an
            // IME that goes on reporting the composition the user escaped has
            // it refused rather than drawn back, and one that finishes it
            // anyway has the characters swallowed rather than typed into a
            // field the user had abandoned.
            if (!string.IsNullOrEmpty(abandoned)) Arbiter.SuppressEchoOf(abandoned);
            else if (Arbiter.IsAwaitingPlatform) Arbiter.Cancel();

            if (wasActive) _displayDirty = true;
        }

        /// <summary>
        /// Advances the commit grace window by one update, inserting the text
        /// the platform turned out never to send. True if the value changed.
        /// </summary>
        public bool Tick()
        {
            string owed = Arbiter.Tick();
            return owed != null && InsertAtCaret(owed);
        }

        /// <summary>
        /// Closes the commit window immediately, inserting anything the
        /// platform still owed. Called when focus leaves: nothing is going to
        /// arrive for a field that is no longer being typed into.
        /// </summary>
        public bool FlushCommit()
        {
            string owed = Arbiter.Flush();
            return owed != null && InsertAtCaret(owed);
        }

        /// <summary>
        /// Offers a character the platform delivered. Returns false when the
        /// arbiter recognised it as an echo of a composition already inserted,
        /// in which case the field must ignore it.
        /// </summary>
        public bool AcceptCharacter(char character, out bool changed)
        {
            changed = false;
            if (Arbiter.ShouldSwallow(character)) return false;
            changed = Insert(character.ToString());
            if (changed) NoteHandedOver(character);
            return true;
        }

        /// <summary>
        /// Records a character the platform handed over while it was still
        /// composing, and ends the composition once those characters add up to
        /// exactly what it says.
        ///
        /// The two channels do not have to move on the same frame. An IME can
        /// deliver the finished syllable as a character while the composition
        /// it is reporting still reads the same, and on the Input Manager
        /// backend that report is the platform's own live state, so it goes on
        /// saying so until the platform itself changes its mind. Left alone,
        /// the field draws the syllable it has just been handed a second time
        /// under the caret, and then commits it a second time as well — as a
        /// composition when focus leaves, or as the commit "the platform never
        /// sent" once the report finally empties and the grace window closes.
        /// That is the character appearing from nowhere, and it needs no click
        /// and no focus change to happen: only the two channels being one frame
        /// apart.
        ///
        /// A composition cannot still be owed once its text has been paid, so
        /// this ends it on the spot and registers it as a replay, which is the
        /// same thing a self-commit does and for the same reason: the platform
        /// was not told, and will go on reporting it.
        /// </summary>
        private void NoteHandedOver(char character)
        {
            if (!_composition.Active) return;

            _handedOver += character;

            // The same text, not the same code units, and through the arbiter's
            // comparison so that this agrees with the two the arbiter makes
            // itself. A platform that composes 한 as U+D55C and hands it over
            // as U+1112 U+1161 U+11AB has still paid for it, and an equality
            // that said otherwise would leave the composition owed, drawn a
            // second time under the caret, and committed again behind it.
            if (!ImeCommitArbiter.SameText(_handedOver, _composition.Text)) return;

            string spent = _composition.Text;
            EndComposition();
            Arbiter.IgnoreReplayOf(spent);
        }

        // ------------------------------------------------------------- external

        /// <summary>
        /// Replaces the whole state from an editor we do not own: the mobile
        /// soft keyboard, which keeps its own buffer and hands back a string.
        /// Returns true when the value changed, so the field can raise its event
        /// once per real change rather than once per poll.
        /// </summary>
        public bool SetExternalText(string text, int selectionStart, int selectionLength)
        {
            text ??= string.Empty;
            EndComposition();

            string limited = ApplyLimit(text);
            bool changed = !string.Equals(_text, limited, StringComparison.Ordinal);
            _text = limited;

            int start = Clamp(BoundaryBefore(_text, selectionStart));
            int end = Clamp(BoundaryBefore(_text, selectionStart + Mathf.Max(0, selectionLength)));
            _anchor = start;
            _caret = end;
            _displayDirty = true;
            return changed;
        }

        // -------------------------------------------------------------- helpers

        private bool InsertAtCaret(string value)
        {
            if (ReadOnly || string.IsNullOrEmpty(value)) return false;

            if (CharacterLimit > 0)
            {
                int room = CharacterLimit - _text.Length;
                if (room <= 0) return false;
                if (value.Length > room)
                {
                    // Truncating on a boundary beats dropping the whole insert:
                    // a paste that is one character too long should still paste.
                    value = value.Substring(0, BoundaryBefore(value, room));
                    if (value.Length == 0) return false;
                }
            }

            _text = _text.Insert(_caret, value);
            SetCaret(_caret + value.Length, false);

            // The composition is anchored to the caret, so an insert that moves
            // the caret has to take it along. Everything that inserts while an
            // IME is still composing hits this: the syllable a Hangul IME
            // commits on the character channel arrives a poll after the
            // composition has already flipped to the next syllable, and the
            // grace window can close on a commit the platform never sent while
            // the user is composing again. Leave Start where it was and it
            // points before the text just inserted, so DisplayText splices the
            // new composition in front of the syllable that finished and the
            // field draws "ㄱ한" — the same two characters, reversed.
            if (_composition.Active) _composition.Start = _caret;
            return true;
        }

        private void EndComposition()
        {
            if (_composition.Active) _displayDirty = true;
            _composition = ImeComposition.None;
            _handedOver = string.Empty;
        }

        private string ApplyLimit(string value) =>
            CharacterLimit > 0 && value.Length > CharacterLimit
                ? value.Substring(0, BoundaryBefore(value, CharacterLimit))
                : value;

        private int Clamp(int index) => BoundaryBefore(_text, Mathf.Clamp(index, 0, _text.Length));

        /// <summary>
        /// Moves an index off the tail of a surrogate pair. Candidate windows
        /// and soft keyboards report offsets in their own units, and a Chinese
        /// or emoji character reported as "one" lands halfway through ours.
        /// </summary>
        private static int BoundaryBefore(string value, int index)
        {
            if (index <= 0) return 0;
            if (index >= value.Length) return value.Length;
            return char.IsLowSurrogate(value[index]) ? index - 1 : index;
        }
    }
}
