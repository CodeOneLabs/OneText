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

        /// <summary>Refuses every edit, including composition.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Maximum length of the committed text; 0 means unlimited.</summary>
        public int CharacterLimit { get; set; }

        /// <summary>Arbitrates who inserts a composition once the IME lets go.</summary>
        public ImeCommitArbiter Arbiter { get; } = new ImeCommitArbiter();

        /// <summary>The field's value. Never includes an unfinished composition.</summary>
        public string Text
        {
            get => _text;
            set
            {
                value ??= string.Empty;
                _composition = ImeComposition.None;
                Arbiter.Cancel();
                _text = ApplyLimit(value);
                _caret = _anchor = Clamp(_caret);
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
            EndComposition();
            Arbiter.Cancel();
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
            return true;
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
            return true;
        }

        private void EndComposition()
        {
            if (_composition.Active) _displayDirty = true;
            _composition = ImeComposition.None;
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
