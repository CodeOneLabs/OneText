using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// A text input field built on the OneText pipeline: shaped, bidi-aware
    /// text with a caret that moves by grapheme clusters (one press of the
    /// arrow key steps over "é" or a family emoji, not over half of it) and
    /// word jumps that follow UAX #29 word boundaries.
    ///
    /// It composes. While an input method is assembling a Hangul syllable,
    /// converting kana or offering pinyin candidates, that text is drawn inline
    /// at the caret with an underline and is not yet part of
    /// <see cref="text"/>; the keys that drive the IME are left to the IME
    /// rather than being applied twice; and when focus leaves mid-composition
    /// the field commits what was there instead of dropping it. The state
    /// behind all of that is <see cref="TextEditingModel"/>, which is where the
    /// tests are.
    ///
    /// The field owns three children: the text label, an optional placeholder
    /// label, and the caret/selection graphic. Use
    /// <c>GameObject &gt; UI &gt; OneText Input Field</c> to create one wired up.
    /// </summary>
    [AddComponentMenu("OneText/OneText Input Field")]
    public sealed class OneTextInputField : Selectable,
        IUpdateSelectedHandler, IPointerClickHandler,
        IBeginDragHandler, IDragHandler, ISubmitHandler
    {
        [SerializeField] private OneTextLabel _textComponent;
        [SerializeField] private OneTextLabel _placeholder;
        [SerializeField] private OneTextCaret _caret;

        [TextArea]
        [SerializeField] private string _text = string.Empty;

        [Tooltip("Enter inserts a newline instead of submitting.")]
        [SerializeField] private bool _multiline;

        [SerializeField] private bool _readOnly;

        [Tooltip("0 means unlimited.")]
        [SerializeField] private int _characterLimit;

        [Tooltip("Accept composition from the platform input method (Korean, " +
                 "Japanese, Chinese). Turn off for fields that must take raw " +
                 "keystrokes only, such as a key-binding prompt.")]
        [SerializeField] private bool _inputMethodEnabled = true;

        [SerializeField] private Color _caretColor = Color.white;
        [SerializeField] private float _caretWidth = 2f;
        [SerializeField] private float _caretBlinkRate = 1.7f;

        [SerializeField] private UnityEvent<string> _onValueChanged = new UnityEvent<string>();
        [SerializeField] private UnityEvent<string> _onSubmit = new UnityEvent<string>();

        private readonly TextEditingModel _model = new TextEditingModel();
        private readonly List<Rect> _selectionRects = new List<Rect>();
        private readonly List<Rect> _compositionRects = new List<Rect>();
        private readonly List<Rect> _clauseRects = new List<Rect>();
        private readonly List<Rect> _rectScratch = new List<Rect>();
        private readonly Event _processingEvent = new Event();
        private IImeInput _ime;
        private MobileTextInput _mobile;
        private bool _focused;
        private float _blinkStart;
        private float _desiredCaretX = float.NaN;
        private bool _visualsDirty = true;
        private int _lastEditingFrame = -1;

        public string text
        {
            get => _model.Text;
            set
            {
                value ??= string.Empty;
                PushSettings();
                if (_model.Text == value) return;
                _model.Text = value;
                _text = _model.Text;
                _visualsDirty = true;
                _onValueChanged.Invoke(_text);
            }
        }

        /// <summary>Caret index, in UTF-16 code units of <see cref="text"/>.</summary>
        public int caretPosition
        {
            get => _model.Caret;
            set => SetCaret(value, extendSelection: false);
        }

        /// <summary>The other end of the selection; equal to the caret when nothing is selected.</summary>
        public int selectionAnchorPosition => _model.Anchor;

        public bool isFocused => _focused;

        /// <summary>True while an input method is composing text at the caret.</summary>
        public bool isComposing => _model.IsComposing;

        /// <summary>
        /// What the input method is composing right now, drawn at the caret and
        /// not yet part of <see cref="text"/>. Empty when nothing is.
        /// </summary>
        public string compositionString => _model.IsComposing ? _model.Composition.Text : string.Empty;

        /// <summary>The text as drawn: <see cref="text"/> with the composition spliced in.</summary>
        public string displayText => _model.DisplayText;

        /// <summary>The editing state itself, for tests and for tools that drive a field.</summary>
        public TextEditingModel editingModel => _model;

        public bool readOnly
        {
            get => _readOnly;
            set
            {
                _readOnly = value;
                _model.ReadOnly = value;
                // A field that just became read-only must not keep showing text
                // it will never accept.
                if (value && _model.IsComposing) { _model.CancelComposition(); _visualsDirty = true; }
            }
        }

        public bool multiline
        {
            get => _multiline;
            set { _multiline = value; _visualsDirty = true; }
        }

        /// <summary>Whether the platform input method may compose into this field.</summary>
        public bool inputMethodEnabled
        {
            get => _inputMethodEnabled;
            set
            {
                if (_inputMethodEnabled == value) return;
                _inputMethodEnabled = value;
                if (!value) StopInputMethod();
                else if (_focused) StartInputMethod();
            }
        }

        public int characterLimit
        {
            get => _characterLimit;
            set { _characterLimit = Mathf.Max(0, value); PushSettings(); }
        }

        public OneTextLabel textComponent => _textComponent;

        public UnityEvent<string> onValueChanged => _onValueChanged;

        public UnityEvent<string> onSubmit => _onSubmit;

        protected override void OnEnable()
        {
            base.OnEnable();
            PushSettings();
            if (_model.Text != _text) _model.Text = _text ?? string.Empty;
            _visualsDirty = true;
        }

        protected override void OnDisable()
        {
            // Leaving the IME switched on for a field that no longer exists is
            // how the next field to take focus inherits a composition.
            EndEditing();
            base.OnDisable();
        }

        protected override void Start()
        {
            base.Start();
            EnsureCaretGraphic();
            _visualsDirty = true;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            _text ??= string.Empty;
            PushSettings();
            if (!Application.isPlaying) _model.Text = _text;
            _visualsDirty = true;
        }
#endif

        private void LateUpdate()
        {
            // The EventSystem drives editing while the field is selected; this
            // catches the frames where it does not (no EventSystem at all, or a
            // soft keyboard that reports on its own schedule).
            if (_focused && _lastEditingFrame != Time.frameCount) UpdateEditing();

            if (_visualsDirty) UpdateVisuals();
            else if (_focused && _caretBlinkRate > 0f) UpdateCaretGeometry();
        }

        private void PushSettings()
        {
            _model.ReadOnly = _readOnly;
            _model.CharacterLimit = Mathf.Max(0, _characterLimit);
        }

        // ------------------------------------------------------------- selection

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            Focus();
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            base.OnDeselect(eventData);
            EndEditing();
        }

        /// <summary>Gives the field keyboard focus.</summary>
        public void ActivateInputField()
        {
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(gameObject);
            Focus();
        }

        /// <summary>
        /// Drops keyboard focus, committing anything the input method was still
        /// composing — the character that every Unity input field loses here.
        /// </summary>
        public void DeactivateInputField()
        {
            EndEditing();
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == gameObject)
                EventSystem.current.SetSelectedGameObject(null);
        }

        /// <summary>Selects [<paramref name="anchor"/>, <paramref name="caret"/>).</summary>
        public void SetSelection(int anchor, int caret)
        {
            _model.SetSelection(anchor, caret);
            _desiredCaretX = float.NaN;
            _visualsDirty = true;
        }

        public void SelectAll()
        {
            _model.SelectAll();
            _visualsDirty = true;
        }

        private void Focus()
        {
            bool wasFocused = _focused;
            _focused = true;
            _blinkStart = Time.unscaledTime;
            _visualsDirty = true;
            if (!wasFocused) StartInputMethod();
        }

        /// <summary>
        /// Ends the editing session: whatever the IME still held becomes text,
        /// the input method is released, and the caret stops drawing.
        /// </summary>
        private void EndEditing()
        {
            if (!_focused)
            {
                StopInputMethod();
                return;
            }

            // Order matters. A commit the platform owed us but never sent is
            // resolved first, then the live composition is committed — the
            // other way round, committing would arm the echo guard and the
            // flush would immediately disarm it.
            bool changed = _model.FlushCommit();
            changed |= _model.CommitComposition();

            _focused = false;
            StopInputMethod();
            _visualsDirty = true;
            if (changed) Changed();
        }

        private void StartInputMethod()
        {
            if (_readOnly || !_inputMethodEnabled) return;

            if (MobileTextInput.IsSupported)
            {
                _mobile ??= new MobileTextInput();
                if (!_mobile.IsOpen) _mobile.Open(_model.Text, _multiline, _characterLimit);
                return;
            }

            _ime ??= ImeInput.Create();
            _ime?.Begin();
        }

        private void StopInputMethod()
        {
            _ime?.End();
            if (_mobile != null) { _mobile.Close(); _mobile = null; }
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (!IsActive() || !IsInteractable()) return;

            // Clicking somewhere else in the same field ends the composition
            // where it stands; the caret is about to move away from it.
            if (_model.IsComposing && _model.CommitComposition()) Changed();

            ActivateInputField();
            SetCaret(IndexAt(eventData), extendSelection: false);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.clickCount == 2)
            {
                string value = _model.Text;
                TextHitTest.GetWordAt(value, Mathf.Min(_model.Caret, Mathf.Max(0, value.Length - 1)),
                    out int start, out int end);
                SetSelection(start, end);
            }
            else if (eventData.clickCount >= 3)
            {
                SelectAll();
            }
        }

        public void OnBeginDrag(PointerEventData eventData) => SetCaret(IndexAt(eventData), extendSelection: true);

        public void OnDrag(PointerEventData eventData) => SetCaret(IndexAt(eventData), extendSelection: true);

        public void OnSubmit(BaseEventData eventData)
        {
            if (!_multiline) _onSubmit.Invoke(_model.Text);
        }

        private int IndexAt(PointerEventData eventData)
        {
            if (_textComponent == null) return 0;
            return _textComponent.GetIndexAtScreenPoint(eventData.position, eventData.pressEventCamera);
        }

        // ------------------------------------------------------------- keyboard

        public void OnUpdateSelected(BaseEventData eventData)
        {
            if (!_focused) return;

            // Composition is read before the key queue is drained, so that the
            // frame an IME commits is a frame where we already know it did.
            // Reading it afterwards would mean applying the committed
            // characters and only then noticing the composition had ended,
            // which is how a commit gets counted twice.
            UpdateEditing();

            if (_mobile == null)
            {
                while (Event.PopEvent(_processingEvent))
                {
                    if (_processingEvent.rawType == EventType.KeyDown)
                        ProcessKeyEvent(_processingEvent);
                }
            }

            eventData.Use();
        }

        /// <summary>
        /// One editing update: read the input method, then let the commit
        /// window advance by one. Called every frame the field is focused, and
        /// public so a test can drive editing without an EventSystem.
        /// </summary>
        public void UpdateEditing()
        {
            _lastEditingFrame = Time.frameCount;

            if (_mobile != null)
            {
                if (!_mobile.Poll(_model, out bool mobileChanged))
                    DeactivateInputField();
                if (mobileChanged) Changed();
                return;
            }

            PollInputMethod();
            if (_model.Tick()) Changed();
        }

        private void PollInputMethod()
        {
            if (_ime == null) return;

            if (!_ime.TryGetComposition(out string composing, out int caret,
                    out int clauseStart, out int clauseLength))
            {
                composing = string.Empty;
                caret = -1;
                clauseStart = clauseLength = 0;
            }

            var current = _model.Composition;
            bool differs = !string.Equals(composing, _model.IsComposing ? current.Text : string.Empty,
                                StringComparison.Ordinal)
                           || (_model.IsComposing && (clauseStart != current.ClauseStart ||
                                                      clauseLength != current.ClauseLength));
            if (differs)
            {
                if (_model.SetComposition(composing, caret, clauseStart, clauseLength)) Changed();
                _visualsDirty = true;
            }

            if (_model.IsComposing) _ime.SetCursorScreenPosition(CaretScreenPosition());
        }

        /// <summary>
        /// Where the caret is on screen, for the candidate window. An IME that
        /// is not told this opens its list in the corner of the display, which
        /// on a phone-shaped game window covers the field being typed into.
        /// </summary>
        private Vector2 CaretScreenPosition()
        {
            if (_textComponent == null) return Vector2.zero;

            var rect = _textComponent.GetCaretRect(_model.DisplayCaret, _caretWidth);
            var world = _textComponent.transform.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f));
            var canvas = _textComponent.canvas;
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            return RectTransformUtility.WorldToScreenPoint(camera, world);
        }

        /// <summary>
        /// Applies one key event, exactly as the EventSystem does. Public
        /// because that is the only way to test the composition rules without a
        /// platform IME to type into.
        /// </summary>
        public void ProcessKeyEvent(Event keyEvent)
        {
            if (keyEvent == null) return;
            _blinkStart = Time.unscaledTime;

            if (_model.IsComposing)
            {
                // While an IME composes, the keyboard belongs to it. Backspace
                // shortens the composition, the arrows walk the candidate list,
                // Enter accepts a candidate — and every one of those also
                // arrives here. Acting on them edits the committed text behind
                // the composition, or submits a form the user was only
                // confirming a syllable in.
                if (keyEvent.keyCode == KeyCode.Escape)
                {
                    _model.CancelComposition();
                    _visualsDirty = true;
                }
                return;
            }

            bool shift = (keyEvent.modifiers & EventModifiers.Shift) != 0;
            bool command = (keyEvent.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            bool word = command || (keyEvent.modifiers & EventModifiers.Alt) != 0;

            switch (keyEvent.keyCode)
            {
                case KeyCode.Backspace:
                    if (_model.Backspace()) Changed();
                    return;
                case KeyCode.Delete:
                    if (_model.ForwardDelete()) Changed();
                    return;
                case KeyCode.LeftArrow:
                    MoveHorizontally(-1, word, shift);
                    return;
                case KeyCode.RightArrow:
                    MoveHorizontally(1, word, shift);
                    return;
                case KeyCode.UpArrow:
                    MoveVertically(-1, shift);
                    return;
                case KeyCode.DownArrow:
                    MoveVertically(1, shift);
                    return;
                case KeyCode.Home:
                    SetCaret(LineEdge(toStart: true), shift);
                    return;
                case KeyCode.End:
                    SetCaret(LineEdge(toStart: false), shift);
                    return;
                case KeyCode.Escape:
                    DeactivateInputField();
                    return;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_multiline) Insert("\n");
                    else _onSubmit.Invoke(_model.Text);
                    return;
                case KeyCode.A when command:
                    SelectAll();
                    return;
                case KeyCode.C when command:
                    Copy();
                    return;
                case KeyCode.X when command:
                    Copy();
                    if (_model.DeleteSelection()) Changed();
                    return;
                case KeyCode.V when command:
                    Paste();
                    return;
                case KeyCode.Tab:
                    return; // navigation, not text
            }

            char character = keyEvent.character;
            if (command || character == 0 || character == '\t') return;
            if (character == '\n' || character == '\r')
            {
                if (_multiline) Insert("\n");
                return;
            }
            if (char.IsControl(character)) return;

            // The arbiter gets a look first: this may be the platform echoing a
            // composition the field already committed on its way out of focus.
            if (_model.AcceptCharacter(character, out bool changed) && changed) Changed();
            else _visualsDirty = true;
        }

        // -------------------------------------------------------------- editing

        private void Insert(string value)
        {
            if (_model.Insert(value)) Changed();
        }

        private void Copy()
        {
            if (!_model.HasSelection) return;
            GUIUtility.systemCopyBuffer = _model.SelectedText;
        }

        private void Paste()
        {
            string clipboard = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clipboard)) return;
            if (!_multiline) clipboard = clipboard.Replace("\r", string.Empty).Replace("\n", " ");
            Insert(clipboard);
        }

        private void Changed()
        {
            _text = _model.Text;
            _desiredCaretX = float.NaN;
            _visualsDirty = true;
            _onValueChanged.Invoke(_text);
        }

        // -------------------------------------------------------- caret movement

        private void SetCaret(int index, bool extendSelection)
        {
            _model.SetCaret(index, extendSelection);
            _desiredCaretX = float.NaN;
            _blinkStart = Time.unscaledTime;
            _visualsDirty = true;
        }

        private void MoveHorizontally(int direction, bool byWord, bool extendSelection)
        {
            _model.MoveHorizontally(direction, byWord, extendSelection);
            _desiredCaretX = float.NaN;
            _blinkStart = Time.unscaledTime;
            _visualsDirty = true;
        }

        private void MoveVertically(int direction, bool extendSelection)
        {
            if (_textComponent == null) return;
            var layout = _textComponent.EnsureLayout();
            if (float.IsNaN(_desiredCaretX))
                _desiredCaretX = TextHitTest.GetCaretRect(layout, _model.Caret, 0f).center.x;

            int target = TextHitTest.MoveVertically(layout, _model.Caret, direction, _desiredCaretX);
            float desired = _desiredCaretX;
            SetCaret(target, extendSelection);
            _desiredCaretX = desired; // survives the move, so up/down does not drift
        }

        private int LineEdge(bool toStart)
        {
            if (_textComponent == null) return toStart ? 0 : _model.Text.Length;
            var layout = _textComponent.EnsureLayout();
            if (layout.Lines.Count == 0) return toStart ? 0 : _model.Text.Length;

            var line = layout.Lines[TextHitTest.GetLineForIndex(layout, _model.Caret)];
            return toStart ? line.TextStart : line.TextStart + line.TextLength;
        }

        // -------------------------------------------------------------- visuals

        private void EnsureCaretGraphic()
        {
            if (_caret != null || _textComponent == null) return;

            var go = new GameObject("Caret", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_textComponent.transform, false);
            go.hideFlags = HideFlags.DontSave;
            _caret = go.AddComponent<OneTextCaret>();

            // Sharing the label's rect AND pivot puts the caret in exactly the
            // coordinate frame the label reports its geometry in.
            var rect = _caret.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = _textComponent.rectTransform.pivot;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }

        /// <summary>Pushes text, scroll offset and caret geometry to the children.</summary>
        public void UpdateVisuals()
        {
            _visualsDirty = false;
            if (_textComponent == null) return;

            EnsureCaretGraphic();

            // Whatever the user types is text, not markup: a name with an angle
            // bracket in it must not turn half the field bold.
            _textComponent.RichText = false;
            _textComponent.Text = _model.DisplayText;
            if (_placeholder != null)
                _placeholder.enabled = _model.DisplayText.Length == 0;

            ScrollCaretIntoView();
            UpdateCaretGeometry();
        }

        /// <summary>Nudges the text label's scroll offset until the caret is inside the box.</summary>
        private void ScrollCaretIntoView()
        {
            if (!_focused)
            {
                _textComponent.ScrollOffset = Vector2.zero;
                return;
            }

            var box = _textComponent.rectTransform.rect;
            var offset = _textComponent.ScrollOffset;
            for (int pass = 0; pass < 2; pass++)
            {
                var caret = _textComponent.GetCaretRect(_model.DisplayCaret, _caretWidth);
                float dx = 0f, dy = 0f;
                if (caret.xMax > box.xMax) dx = caret.xMax - box.xMax;
                else if (caret.xMin < box.xMin) dx = caret.xMin - box.xMin;
                if (caret.yMax > box.yMax) dy = -(caret.yMax - box.yMax);
                else if (caret.yMin < box.yMin) dy = box.yMin - caret.yMin;

                if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f)) break;
                offset += new Vector2(dx, dy);
                _textComponent.ScrollOffset = offset;
            }

            // Never scroll past the start of the text.
            if (_textComponent.ScrollOffset.x < 0f || _textComponent.ScrollOffset.y > 0f)
            {
                _textComponent.ScrollOffset = new Vector2(
                    Mathf.Max(0f, _textComponent.ScrollOffset.x),
                    Mathf.Min(0f, _textComponent.ScrollOffset.y));
            }
        }

        private void UpdateCaretGeometry()
        {
            if (_caret == null || _textComponent == null) return;

            _caret.color = _caretColor;
            if (!_focused)
            {
                _caret.SetGeometry(default, false, null);
                _caret.SetComposition(null, null);
                return;
            }

            int selectionStart = _model.DisplaySelectionStart;
            int selectionEnd = _model.DisplaySelectionEnd;
            _selectionRects.Clear();
            if (selectionEnd > selectionStart)
                _textComponent.GetSelectionRects(selectionStart, selectionEnd, _selectionRects);

            BuildCompositionMarks();

            bool visible = _caretBlinkRate <= 0f ||
                (Time.unscaledTime - _blinkStart) % (1f / _caretBlinkRate) < 0.5f / _caretBlinkRate;
            var caretRect = _textComponent.GetCaretRect(_model.DisplayCaret, _caretWidth);
            _caret.SetGeometry(caretRect, visible && selectionEnd == selectionStart, _selectionRects);
        }

        /// <summary>
        /// Underlines the composition and blocks in the clause being converted.
        /// Both come from the same selection-rect machinery the mouse uses, so
        /// they wrap across lines and follow RTL runs for free; the underline
        /// is that rect squashed to the bottom of the line.
        /// </summary>
        private void BuildCompositionMarks()
        {
            _compositionRects.Clear();
            _clauseRects.Clear();

            if (_model.TryGetCompositionRange(out int start, out int end))
            {
                _textComponent.GetSelectionRects(start, end, _rectScratch);
                float thickness = Mathf.Max(1f, _textComponent.FontSize * 0.06f);
                foreach (var rect in _rectScratch)
                    _compositionRects.Add(new Rect(rect.xMin, rect.yMin, rect.width, thickness));

                if (_model.TryGetClauseRange(out int clauseStart, out int clauseEnd) &&
                    clauseEnd > clauseStart)
                {
                    _textComponent.GetSelectionRects(clauseStart, clauseEnd, _rectScratch);
                    _clauseRects.AddRange(_rectScratch);
                }
            }

            _caret.SetComposition(_compositionRects, _clauseRects);
        }
    }
}
