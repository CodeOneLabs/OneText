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

        // What the composition said before the report last changed it, and
        // what has been handed over against that. A character arriving in the
        // update the report changed is far more likely to be paying for the
        // composition that was just replaced than for the one that replaced
        // it, and when the two are the same syllable nothing but this tells
        // them apart. Worth exactly one update: Tick ages it out. See
        // NoteHandedOver, where the reason lives.
        private string _replaced = string.Empty;
        private bool _replacedThisUpdate;
        private string _paidTheReplaced = string.Empty;

        // Whether the report said something new this update: a composition was
        // adopted, or the one on screen changed its text. Aged out by Tick the
        // way _replaced is, and for the same reason — the poll runs before the
        // key queue drains, so "this update" has to survive the Tick between
        // them. Measured (Tools/ImeProbe~, three recordings, ~4,500 frames,
        // macOS): every genuine commit character arrives in the frame the
        // report changes; the only characters that arrive against an unmoved
        // report are a jamo the platform committed and re-issued identically.
        // NoteHandedOver keys on this to tell those apart.
        private bool _reportMoved;
        private bool _reportMovedThisUpdate;

        // Whether a payment was credited to an identical, finished predecessor
        // while this composition stayed alive — see NoteHandedOver. If the
        // report later empties, the commit window opens owing nothing: should
        // the platform send nothing (the shape the old reasoned tests assumed,
        // and no recording shows), the window must not insert a copy of text
        // the credit already inserted.
        private bool _paidForward;

        // Whether the composition on screen was arrived at by the user taking a
        // jamo off the one before it — a report that shrank to a prefix of
        // itself with nothing paid for what it dropped. That is a backspace
        // inside the IME, and it is the one way a composition ends without the
        // platform ever meaning to commit it: 아, backspace, ㅇ, backspace,
        // nothing. The commit window that the emptying opens must owe nothing
        // there, or the syllable the user just deleted is inserted two updates
        // later and the delete reads as swallowed — the second bug report of
        // 2026-08-20, where the third press was the one that appeared to work.
        //
        // A shrink is told from a split by what pays for it, not by the shape
        // alone: 앙 giving up 아 is also a prefix shrink, but a character
        // arrives in that update to pay for what was dropped, and any payment
        // at all clears this. Nothing is measured here that the platform has to
        // agree to send — the composition channel and the character channel
        // are both already being read, and this is a fact about the two of them
        // together. That matters because the key event is exactly what is not
        // to be counted on: the Windows report says the IMM eats the backspace.
        private bool _shrankUnpaid;

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
                _reportMoved = true;
                _reportMovedThisUpdate = true;
            }

            if (composing.Length == 0)
            {
                string previous = _composition.Text;
                bool owesNothing = _paidForward || _shrankUnpaid;
                EndComposition();
                Arbiter.AwaitPlatformCommit(previous, owesNothing);
                return textChanged;
            }

            // A composition that says something new is a composition nothing
            // has been handed over for yet — and the one it replaced is what
            // a character arriving in this update is most likely paying for.
            // Ordinal on purpose, unlike the comparisons that ask whether two
            // strings are the same text: this one asks whether the platform's
            // report changed, and a report that changed shape changed.
            if (!string.Equals(_composition.Text, composing, StringComparison.Ordinal))
            {
                _replaced = _composition.Text ?? string.Empty;
                _replacedThisUpdate = _replaced.Length != 0;
                _paidTheReplaced = string.Empty;
                _handedOver = string.Empty;
                _reportMoved = true;
                _reportMovedThisUpdate = true;
                // A report that moved on is a new syllable being built; its
                // commit will be its own, and the window it opens owes it.
                _paidForward = false;
                // A report that grew, or that changed to something it was never
                // the start of, is not a deletion and clears this.
                _shrankUnpaid = ImeCommitArbiter.IsShortenedFrom(_replaced, composing);
            }

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
            // The composition a character can still be paying for is the one
            // the report replaced in this update, not one it replaced a
            // thousand updates ago. The two look identical in the strings
            // alone: a recording of macOS has 아 committed off 앙 in the same
            // update as the report changing to the next 아, and the same 아
            // paid for later against a report that had said 아 all along. The
            // first belongs to the composition that was replaced; the second
            // is the platform settling up for the one it is still showing. So
            // the register lives for the update it was written in and the
            // characters that update delivers, and ages out here. This runs
            // after the poll and before the key queue is drained, which is why
            // the marker set by the poll survives the first Tick and not the
            // second. See NoteHandedOver.
            if (_replacedThisUpdate) _replacedThisUpdate = false;
            else if (_replaced.Length != 0) { _replaced = string.Empty; _paidTheReplaced = string.Empty; }

            // The same clock for the same reason: whether the report moved is a
            // fact about this update, judged by the characters this update
            // delivers after it.
            if (_reportMovedThisUpdate) _reportMovedThisUpdate = false;
            else _reportMoved = false;

            string owed = Arbiter.Tick();
            return owed != null && InsertAtCaret(owed);
        }

        /// <summary>
        /// Inserts a commit the platform has announced and not delivered yet,
        /// so that whatever is about to edit the value edits all of it. True if
        /// the value changed. See <see cref="ImeCommitArbiter.TakeOwedNow"/>
        /// for the keystroke this exists for.
        /// </summary>
        public bool SettleOwedCommit()
        {
            string owed = Arbiter.TakeOwedNow();
            if (owed == null || !InsertAtCaret(owed)) return false;

            // It is in the value now, so it is text the platform can deliver a
            // second time — the syllable that comes back after a click away and
            // a click in again.
            Arbiter.NotePlatformCommitted(owed);
            return true;
        }

        /// <summary>
        /// Removes the syllable the platform has taken back into the live
        /// composition, so that it is not drawn twice — once as committed text
        /// and once inside the composition that now owns it. True when it was
        /// there to remove.
        ///
        /// Refuses unless the text really does end with it at the composition,
        /// because everything upstream of this is inference about a platform
        /// that never says what it is doing, and the cost of being wrong here
        /// is a character the user typed.
        /// </summary>
        public bool TakeBackCommitted(string committed)
        {
            if (ReadOnly || !_composition.Active || string.IsNullOrEmpty(committed)) return false;

            int start = _composition.Start;
            if (start < committed.Length) return false;
            if (string.CompareOrdinal(_text, start - committed.Length, committed, 0, committed.Length) != 0)
                return false;

            _text = _text.Remove(start - committed.Length, committed.Length);
            _composition.Start = start - committed.Length;
            _caret = _composition.Start;
            _anchor = _caret;
            _displayDirty = true;
            return true;
        }

        /// <summary>
        /// Throws away a commit the platform announced and has not delivered,
        /// because the user deleted the composition it belongs to. The
        /// platform's copy of it is swallowed when it arrives; the committed
        /// text is not touched. True when there was one to throw away.
        /// </summary>
        public bool DiscardOwedCommit() => Arbiter.TakeOwedNow() != null;

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

            // Whose composition this pays for, which is not always the one on
            // screen. At a syllable boundary a Hangul IME finishes one syllable
            // and starts the next in a single keystroke — 아 plus ㅇ composes
            // 앙, and the ㅏ after it commits 아 and leaves 아 composing — and
            // the field reads the composition before it drains the key queue,
            // so the new syllable is already the live one when the character
            // for the old one arrives. Type the same syllable twice and that
            // character matches the live composition exactly.
            //
            // Crediting it to the live one is the recording this was read off:
            // 아 typed three times, and the third one lost. The composition ends
            // "paid" and is registered as a replay, so every report of the
            // second 아 — the one the user is actually typing — is refused and
            // nothing is drawn; and because it was never adopted, the register
            // that refuses the platform repeating a commit is never retired by
            // it, and when the platform finally commits that syllable on its
            // own, as focus leaves, the character is swallowed as the repeat.
            // Two 아 in the field, three typed.
            //
            // The composition that was replaced is what tells them apart. A
            // syllable splits because its last jamo belongs to the next one, so
            // what the platform commits is always the previous composition
            // minus that jamo: 앙 gives up 아, and 아 is a prefix of 앙 in the
            // decomposed form every comparison here is made in. Payment the
            // composition of a moment ago accounts for is that one's, and the
            // live one is still owed.
            //
            // The ordinary case is untouched by that. A platform paying late
            // for the syllable it is still reporting pays the whole of what the
            // report says, and what a report grew out of is shorter than what
            // it says (하 became 한), so that payment is a prefix of nothing and
            // goes on being credited below. Counted apart from what the live
            // composition has been paid, and not merely skipped, because the
            // two can run in one update — the ㅏ that splits 앙 and the click
            // that commits the rest, in the same frame — and a jamo of the
            // syllable that split off must not be left in the live one's total.
            if (_replaced.Length != 0)
            {
                string toReplaced = _paidTheReplaced + character;
                if (ImeCommitArbiter.TextStartsWith(_replaced, toReplaced))
                {
                    _paidTheReplaced = toReplaced;
                    // Paid for, so the report did not shrink because the user
                    // deleted anything: this is a syllable splitting.
                    _shrankUnpaid = false;
                    return;
                }
            }

            // A character arriving while this composition stands is the
            // platform paying for something. Whatever it turns out to pay for,
            // the shrink that brought this report here was not a silent one.
            _shrankUnpaid = false;
            _handedOver += character;

            // The same text, not the same code units, and through the arbiter's
            // comparison so that this agrees with the two the arbiter makes
            // itself. A platform that composes 한 as U+D55C and hands it over
            // as U+1112 U+1161 U+11AB has still paid for it, and an equality
            // that said otherwise would leave the composition owed, drawn a
            // second time under the caret, and committed again behind it.
            if (!ImeCommitArbiter.SameText(_handedOver, _composition.Text))
            {
                // A total that has stopped being a prefix of the composition can
                // never add up to it, however much arrives — so a character
                // that paid for nothing (닭 splitting into 달 and 가 hands 달
                // over, which is no part of 가) is not left in the total to
                // spoil the payment that follows it. What the tail of a
                // mismatch may still be the start of is kept.
                if (!ImeCommitArbiter.TextStartsWith(_composition.Text, _handedOver))
                {
                    string alone = character.ToString();
                    _handedOver = ImeCommitArbiter.TextStartsWith(_composition.Text, alone) ? alone : string.Empty;
                }
                return;
            }

            // Paid in full — but paid for what? Measured, not assumed
            // (Tools/ImeProbe~, recordings of 2026-08-20, ~4,500 frames): every
            // genuine commit on this platform arrives in the frame the report
            // changes — the split (안 committed as the report flips to ㄴ), the
            // ending (요 committed as the report empties), the Enter and the
            // click (한 committed as the report empties). The one shape that
            // arrives against a report that has not moved is a jamo that cannot
            // combine with itself: the platform commits the first ㅁ and opens
            // a second whose report reads exactly the same, and there is no
            // edge for anything upstream to notice. So a payment completing
            // here with the report unmoved is credited to that finished,
            // identical predecessor, and the live composition — the one the
            // user is typing now — stays adopted, drawn and owed. Crediting it
            // to the live one instead was five presses of ㅁ becoming one.
            //
            // If a platform exists that really does pay the composition it is
            // still showing with no report change (none of the recordings has
            // one; the old tests here assumed it), the value still comes out
            // right: the character was inserted once by this credit, and the
            // window the report's eventual emptying opens owes nothing — it
            // will take a late character but will not invent one. The cost there is
            // the syllable drawn twice until the report empties, which is what
            // uGUI's own field does unconditionally.
            if (!_reportMoved)
            {
                _handedOver = string.Empty;
                _paidForward = true;
                return;
            }

            string spent = _composition.Text;
            EndComposition();
            Arbiter.IgnoreReplayOf(spent);
            // And it is a commit the platform made, which is a different fact
            // from the composition it may still be reporting: this is the text
            // it can deliver a second time.
            Arbiter.NotePlatformCommitted(spent);
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
            _replaced = string.Empty;
            _replacedThisUpdate = false;
            _paidTheReplaced = string.Empty;
            _reportMoved = false;
            _reportMovedThisUpdate = false;
            _paidForward = false;
            _shrankUnpaid = false;
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
