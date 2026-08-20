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
    /// <c>GameObject &gt; UI &gt; OneText &gt; Input Field</c> to create one wired up.
    /// </summary>
    [AddComponentMenu("OneText/OneText Input Field")]
    public sealed class OneTextInputField : Selectable,
        IUpdateSelectedHandler, IPointerClickHandler,
        IBeginDragHandler, IDragHandler, ISubmitHandler
    {
        [SerializeField] private OneTextLabel _textComponent;
        [SerializeField] private OneTextLabel _placeholder;
        [SerializeField] private OneTextCaret _caret;

        // The masked box the text has to stay inside. A field had none: the
        // label was parented straight to the background, so a value longer than
        // the field drew out of the left edge and kept going across whatever was
        // beside it. Unity's InputField and TextMesh Pro's both interpose this
        // layer — Unity calls it the Text Area, TMP exposes it under this name —
        // and it is the layer that was missing, not the scrolling, which
        // ScrollCaretIntoView has always done.
        //
        // Optional, and it has to stay optional: every field already in a
        // project's scenes was authored without one, and a component that
        // rearranged somebody's hierarchy on load to add it would be a worse
        // thing than a field that does not clip. Null means the old behaviour,
        // exactly, and the inspector says so.
        [SerializeField] private RectTransform _textViewport;

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
        // Blinks per second, and TextMesh Pro's number rather than the 1.7 this
        // field shipped with. Both compute the period as 1/rate and show the
        // caret for half of it, so the old default was not a different look, it
        // was the same look at twice the speed.
        [SerializeField] private float _caretBlinkRate = 0.85f;

        // The three colours the caret graphic draws with, kept here rather than
        // on it. The caret GameObject is built at runtime by EnsureCaretGraphic
        // and carries HideFlags.DontSave, so at author time there is no
        // instance to select and no reference to reach one through: a colour
        // that only exists on that object is a colour nobody can change, from
        // the inspector or from code. These are pushed onto it every time the
        // geometry is rebuilt.
        [SerializeField] private Color _selectionColor = new Color32(168, 206, 255, 192);
        [SerializeField] private Color _compositionColor = new Color(1f, 1f, 1f, 0.7f);
        [SerializeField] private Color _clauseColor = new Color(0.24f, 0.50f, 0.87f, 0.35f);

        [Tooltip("Tabbing into the field, or focusing it from a script, selects " +
                 "the whole value so that typing replaces it. Clicking always " +
                 "places the caret where it landed.")]
        [SerializeField] private bool _onFocusSelectAll = true;

        [SerializeField] private UnityEvent<string> _onValueChanged = new UnityEvent<string>();
        [SerializeField] private UnityEvent<string> _onSubmit = new UnityEvent<string>();

        // Serialized like the two above, and for the same reason: these are the
        // events a field is wired up with, and a listener list that can only be
        // built from code is half an event. See the parity region at the bottom
        // for what each of them means.
        [SerializeField] private UnityEvent<string> _onEndEdit = new UnityEvent<string>();
        [SerializeField] private UnityEvent<string> _onSelect = new UnityEvent<string>();
        [SerializeField] private UnityEvent<string> _onDeselect = new UnityEvent<string>();

        private readonly TextEditingModel _model = new TextEditingModel();
        private readonly List<Rect> _selectionRects = new List<Rect>();
        private readonly List<Rect> _compositionRects = new List<Rect>();
        private readonly List<Rect> _clauseRects = new List<Rect>();
        private readonly List<Rect> _rectScratch = new List<Rect>();
        private readonly Event _processingEvent = new Event();
        private IImeInput _ime;

        // A backspace pressed while the IME was composing, waiting to see
        // whether the composition survives the update. See ReleaseHeldBackspace.
        private bool _backspaceHeld;

        // Set for the rest of the update by the backspace that emptied a
        // composition, so that the extra events macOS sends behind it do not
        // delete anything. See ProcessKeyEvent.
        private bool _swallowingCompositionTail;

        // A commit the platform may have reclaimed into a composition of a
        // single jamo, which is also what the user starting a syllable looks
        // like. Held for the update; a keystroke in it settles the question.
        private string _reclaimIfNoKeyFollows;

        /// <summary>
        /// Logs every decision the editing path makes, one line each, for a bug
        /// that only a real input method reproduces. Off, and no cost when off:
        /// the tests replay recordings frame for frame and there is a class of
        /// difference they cannot model, so the field has to be able to say
        /// what it did rather than be reasoned about.
        /// </summary>
        public static bool LogEditing;

        private void Trace(string what)
        {
            if (!LogEditing) return;
            Debug.Log($"[field] f={Time.frameCount} {what} | value={Quote(_model.Text)} " +
                      $"caret={_model.Caret} anchor={_model.Anchor} composing={Quote(_model.Composition.Text)} " +
                      $"held={_backspaceHeld} arbiter=" +
                      (_model.Arbiter.IsAwaitingPlatform ? "awaiting" :
                       _model.Arbiter.IsSuppressingEcho ? "suppressing" : "idle") +
                      $" pending={Quote(_model.Arbiter.PendingText)}");
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var text = new System.Text.StringBuilder("\"").Append(value).Append("\"[");
            for (int i = 0; i < value.Length; i++)
                text.Append(i > 0 ? " " : string.Empty).Append("U+").Append(((int)value[i]).ToString("X4"));
            return text.Append(']').ToString();
        }
        // Whether this field is the one that switched the input method on. See
        // StopInputMethod, which is reached by fields that never began.
        private bool _imeBegun;
        private MobileTextInput _mobile;
        private bool _focused;
        // True only inside OnPointerDown, so that Focus — which that call
        // reaches synchronously through OnSelect — can tell a click from a Tab.
        private bool _pointerIsFocusing;
        private float _blinkStart;
        private float _desiredCaretX = float.NaN;
        private bool _visualsDirty = true;
        private int _lastEditingFrame = -1;
        // Whether the current value has already been reported as final. See
        // RaiseEndEdit.
        private bool _endEditReported;

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
                _endEditReported = false;
                _onValueChanged.Invoke(_text);
            }
        }

        /// <summary>Caret index, in UTF-16 code units of <see cref="text"/>.</summary>
        public int caretPosition
        {
            get => _model.Caret;
            set => SetCaret(value, extendSelection: false);
        }

        /// <summary>
        /// The other end of the selection; equal to the caret when nothing is
        /// selected. Assigning moves that end and leaves the caret where it is,
        /// which together with <see cref="selectionFocusPosition"/> is how the
        /// input fields this one is named after let a script select a range.
        /// </summary>
        public int selectionAnchorPosition
        {
            get => _model.Anchor;
            set => SetSelection(value, _model.Caret);
        }

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

        /// <summary>
        /// The masked box the text is kept inside, named as TextMesh Pro names
        /// it. Null on a field that was authored without one, which scrolls the
        /// same as it always did and clips nothing.
        /// </summary>
        public RectTransform textViewport
        {
            get => _textViewport;
            set { _textViewport = value; _visualsDirty = true; }
        }

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
            _onSelect.Invoke(_model.Text);
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            base.OnDeselect(eventData);
            // End the session first, so a listener on either event sees the
            // committed value rather than whatever the IME was still holding.
            EndEditing();
            _onDeselect.Invoke(_model.Text);
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
        /// composing: the character that every Unity input field loses here.
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
            // Putting the caret back in the field reopens the question this
            // session's value already answered, so the next end of it is
            // reportable again.
            _endEditReported = false;
            if (wasFocused) return;

            StartInputMethod();
            // Arriving in a field selects what is in it, so that the first
            // thing typed replaces the old value instead of being appended to
            // it — but only when the user arrived without saying where. Tab
            // and ActivateInputField name no position, so selecting the value
            // is the useful answer; a click names one, and a click that
            // highlighted the whole value would throw it away on the next
            // keystroke of an edit the user was resuming.
            //
            // TextMesh Pro selects on a click too, its own default, and this is
            // a deliberate divergence from it rather than a copy: the field the
            // report came from is one people click into to change three
            // characters of what is already there.
            if (_onFocusSelectAll && !_pointerIsFocusing) SelectAll();
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
            // resolved first, then the live composition is committed; the
            // other way round, committing would arm the echo guard and the
            // flush would immediately disarm it.
            bool changed = _model.FlushCommit();
            changed |= _model.CommitComposition();

            _focused = false;
            StopInputMethod();
            _visualsDirty = true;
            if (changed) Changed();
            // Last, so onEndEdit carries the value the commit above produced
            // and not the one it replaced.
            RaiseEndEdit();
        }

        /// <summary>
        /// Reports the value as final, once per editing session.
        ///
        /// Two things end an edit and both have to raise it: focus leaving,
        /// which is where the composition is committed, and Return on a
        /// single-line field, which is a user saying they are done. TextMesh
        /// Pro collapses those — its Return deactivates the field, so the two
        /// are one moment there — and OneText's Return leaves the caret where
        /// it is, which is the behaviour that field has always had and not
        /// something to change under a parity member. So the guard does the
        /// collapsing instead: pressing Return and then clicking away reports
        /// one end of edit, not two, and typing anything in between (or
        /// clicking back into the field) makes the next one reportable again.
        /// </summary>
        private void RaiseEndEdit()
        {
            if (_endEditReported) return;
            _endEditReported = true;
            _onEndEdit.Invoke(_model.Text);
        }

        /// <summary>
        /// The user committing the value with Return: both events, in the order
        /// TextMesh Pro raises them.
        /// </summary>
        private void Submit()
        {
            _onSubmit.Invoke(_model.Text);
            RaiseEndEdit();
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
            if (_ime == null || _imeBegun) return;
            _ime.Begin();
            _imeBegun = true;
        }

        private void StopInputMethod()
        {
            // Only if this field turned it on. An input method is one switch
            // for the whole process — Input.imeCompositionMode is, and so is
            // the Input System's — and EndEditing is reached by more than the
            // end of an editing session: OnDisable calls it, and so does
            // clearing inputMethodEnabled, on fields that have not been focused
            // for ten minutes. A field that never began must not end, or hiding
            // a panel switches the input method off underneath whichever field
            // is composing at that moment, mid-syllable.
            if (_imeBegun)
            {
                _ime?.End();
                _imeBegun = false;
            }
            if (_mobile != null) { _mobile.Close(); _mobile = null; }
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            // Raised for the whole of this call, and lowered in the finally so
            // that it cannot survive one. Focus does not arrive after this
            // method, it arrives inside it: Selectable.OnPointerDown selects the
            // object through the EventSystem, and selection runs OnSelect and
            // Focus synchronously, before the line below it. A field cannot ask
            // afterwards where its focus came from, so it is told beforehand.
            _pointerIsFocusing = true;
            try
            {
                base.OnPointerDown(eventData);
                if (!IsActive() || !IsInteractable()) return;

                // While an input method is composing, the mouse belongs to it as
                // much as the keyboard does. This used to commit the composition
                // so the caret could move away from it, and that commit is the
                // worst kind of divergence: the platform is not told, goes on
                // holding the syllable, and offers it back as a new composition
                // a frame later. Leaving the caret where the IME put it until
                // the syllable is finished costs one click; getting it wrong
                // costs a character the user never typed.
                if (_model.IsComposing) return;

                ActivateInputField();
                SetCaret(IndexAt(eventData), extendSelection: false);
            }
            finally
            {
                _pointerIsFocusing = false;
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // Same rule as OnPointerDown: a selection made under a live
            // composition would be deleted by the commit that composition is
            // heading for.
            if (_model.IsComposing) return;

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

        // And the same rule again: the caret belongs to the input method until
        // the syllable is finished, so a drag over a live composition does
        // nothing rather than dragging the text out from under it.
        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_model.IsComposing) SetCaret(IndexAt(eventData), extendSelection: true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_model.IsComposing) SetCaret(IndexAt(eventData), extendSelection: true);
        }

        public void OnSubmit(BaseEventData eventData)
        {
            if (!_multiline) Submit();
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

                ReleaseHeldBackspace();
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
            // Anything held by the last update and not released with it. The
            // state it is judged against is the state that update left behind,
            // so reading it here is reading it then.
            ReleaseHeldBackspace();
            if (_reclaimIfNoKeyFollows != null)
            {
                // Nothing carrying a key or a character arrived behind it, so
                // the platform started that composition on its own — which it
                // only does with a syllable it has taken back.
                string reclaimed = _reclaimIfNoKeyFollows;
                _reclaimIfNoKeyFollows = null;
                if (_model.IsComposing) Reclaim(reclaimed);
            }
            _swallowingCompositionTail = false;

            // A composition that was live when the last update ended may be
            // the user building a syllable of their own, which retires the
            // platform's right to repeat its last commit — or it may be that
            // commit being carried on, which does not. The arbiter is asked
            // rather than told: see NoteComposing. Read here rather than where
            // the composition is adopted, because the repeat lands in the same
            // update as the adoption.
            if (_model.IsComposing) _model.Arbiter.NoteComposing(_model.Composition.Text);

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
                bool wasComposing = _model.IsComposing;
                if (_model.SetComposition(composing, caret, clauseStart, clauseLength)) Changed();
                Trace($"composition report {Quote(composing)}");

                // A composition that starts life as the syllable already
                // committed, with its final moved, is that syllable being
                // reclaimed to be edited inside. It has to leave the value now
                // rather than an update later: the platform can split it back
                // apart in the very next update, and a value still holding the
                // original takes the piece it commits as a second one.
                if (!wasComposing && _model.IsComposing)
                {
                    string reclaimed = _model.Arbiter.ReclaimedInto(
                        _model.Composition.Text, out bool certain);
                    if (reclaimed != null && certain) Reclaim(reclaimed);
                    else _reclaimIfNoKeyFollows = reclaimed;
                }
                // Only when something is or was on screen. A composition the
                // model refuses as a replay of one the field already committed
                // leaves the display exactly as it was, and marking it dirty
                // would redraw the field every frame for as long as the
                // platform kept reporting it.
                if (wasComposing || _model.IsComposing) _visualsDirty = true;
            }

            // When both sides are idle, whether that is news depends on the
            // backend, and the difference is the second half of the bug report
            // this milestone closes. InputSystemImeInput caches what the
            // platform pushes and empties that cache when the session ends, so
            // its silence says the same thing whether the platform let go or is
            // still holding a syllable it has not pushed again — no news, and
            // the arbiter retires a refusal on other evidence instead.
            // ImguiImeInput is the platform answering, and an empty answer from
            // it is the platform saying it holds nothing at all.
            //
            // Which matters because the only other thing that retires a refusal
            // is a composition that differs, and a user typing the same
            // syllable twice — 아 아 — never sends one. The second 아 was
            // refused as the platform replaying the first, and stayed refused
            // until some later keystroke changed the string: "it is in the
            // field, but nothing shows until I press an arrow key."
            else if (composing.Length == 0 && !_model.IsComposing && _ime.ReportsPlatformState)
                _model.Arbiter.NotePlatformReleased();

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
        /// Takes the syllable the platform reclaimed out of the value, so that
        /// it is not held in both places at once.
        /// </summary>
        private void Reclaim(string committed)
        {
            if (!_model.TakeBackCommitted(committed)) return;

            // It is not in the value any more, so there is nothing left for the
            // platform to repeat.
            _model.Arbiter.ForgetPlatformCommit();
            Changed();
            Trace($"reclaimed {Quote(committed)} into the composition");
        }

        /// <summary>
        /// Applies a backspace the field held while a composition was live, if
        /// that composition did not survive the update.
        ///
        /// Held rather than dropped because the two things a backspace can mean
        /// are told apart by what happens to the composition, and not before.
        /// While the IME is composing, the key is the IME's: it shortens the
        /// syllable, and acting on it as well would delete committed text
        /// behind the composition. But the press that would empty the
        /// composition is not the IME's at all — on macOS it hands back what is
        /// left of the syllable, ends the composition and lets the key through
        /// for the field to delete what it just handed over, and dropping it
        /// there is a backspace the user pressed and nothing happened for.
        ///
        /// Both arrive in one update and in either order: the composition can
        /// end at the poll, before the key queue is drained, or on the
        /// character the IME hands over in the middle of draining it. So the
        /// question is asked once, at the end, when the update has settled —
        /// composition still live, the key was the IME's and is dropped;
        /// composition gone, it was the field's.
        /// </summary>
        private void ReleaseHeldBackspace()
        {
            if (!_backspaceHeld) return;
            _backspaceHeld = false;
            if (_model.IsComposing) { Trace("dropped the held backspace (still composing)"); return; }
            Trace("releasing the held backspace");

            if (_model.DiscardOwedCommit())
            {
                _swallowingCompositionTail = true;
                _visualsDirty = true;
                Trace("dropped the composition the held backspace emptied");
                return;
            }

            // The syllable the IME handed back may still be owed rather than
            // inserted, and a backspace applied to a value missing it deletes
            // the character in front instead. See SettleOwedCommit.
            if (_model.SettleOwedCommit()) Changed();
            if (_model.Backspace()) Changed();
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

            // A key that carries something is the user; the keycode-less,
            // character-less one that rides behind every composition change
            // carries nothing and says nothing about who caused it.
            if (keyEvent.keyCode != KeyCode.None || keyEvent.character != 0)
                _reclaimIfNoKeyFollows = null;

            if (_model.IsComposing)
            {
                // Escape abandons the composition and keeps the field, which is
                // what an OS text field does with it.
                if (keyEvent.keyCode == KeyCode.Escape)
                {
                    _model.CancelComposition();
                    _visualsDirty = true;
                    return;
                }

                // Backspace is held rather than dropped, and released at the
                // end of the update by ReleaseHeldBackspace, which is where the
                // reason is written down.
                if (keyEvent.keyCode == KeyCode.Backspace)
                {
                    _backspaceHeld = true;
                    Trace("held a backspace (composing)");
                    return;
                }

                // The rest of the keyboard belongs to the IME while it
                // composes. Backspace shortens the composition, the arrows walk
                // the candidate list, Enter accepts a candidate, and every one
                // of those arrives here as well as there; acting on them edits
                // the committed text behind the composition, or submits a form
                // the user was only confirming a syllable in.
                //
                // What does not belong to the IME is the commit, and dropping
                // that along with the rest is why typing Korean into this field
                // produced nothing at all. A Hangul IME hands the finished
                // syllable back as an ordinary character event — GCS_RESULTSTR
                // turned into a WM_CHAR on Windows, insertText: on macOS —
                // while the keys that drive the composition never carry a
                // character, because the IME consumes them before the platform
                // ever translates them. So a printable character arriving
                // mid-composition is committed text and nothing else, on every
                // desktop Unity ships; everything that is not one stops here.
                // char.IsControl is the whole list on its own: Return, Tab and
                // the rest of what the IME uses are C0 controls, and naming
                // them again beside it would only read as though they were not.
                char composed = keyEvent.character;
                bool modified = (keyEvent.modifiers &
                                 (EventModifiers.Control | EventModifiers.Command)) != 0;
                if (modified || composed == 0 || char.IsControl(composed)) return;

                // Through the arbiter, like any other character, and without
                // arming it: at a syllable boundary it is idle, so the
                // character inserts exactly once. Arming it to expect a commit
                // here would mean Tick inserting a second copy during a
                // Japanese conversion, which replaces the whole composition
                // (へんかん becomes 変換) and sends no character at all.
                if (_model.AcceptCharacter(composed, out bool committed) && committed) Changed();
                else _visualsDirty = true;
                return;
            }

            // The composition may have ended in this very update. The field
            // reads the input method before it drains the key queue, so a key
            // pressed at the moment a composition finished arrives here with
            // the syllable it committed still owed: announced by the
            // composition ending, not yet delivered as a character. Until it
            // lands the value is one syllable short, and a key that acts on the
            // value acts on the wrong string.
            //
            // Which is the whole of the recorded bug. Backspacing away a Hangul
            // composition, the last press is the one the IME does not eat: it
            // hands back what was left of the syllable and lets the key through
            // for the field to delete it. Applied to a value that syllable had
            // not reached yet, the backspace deleted the character in front of
            // it instead — 강ㄱ, one backspace, and both of them gone.
            //
            // Only for a key that carries no text of its own. One that does is
            // the delivery itself, and it goes to the arbiter below, which
            // stands down for it rather than inserting a second copy.
            char pressed = keyEvent.character;
            Trace($"key {keyEvent.keyCode} char={Quote(pressed == 0 ? string.Empty : pressed.ToString())} (not composing)");

            // An event with neither a key nor a character can do nothing, and
            // macOS sends one ahead of the real press when a composition
            // terminates. Reaching the settle below, it spends the owed
            // syllable a step before the backspace behind it can say the user
            // deleted it — which is how one press still emptied the field with
            // everything else here already right.
            if (keyEvent.keyCode == KeyCode.None && pressed == 0) return;

            // A backspace arriving while the platform still owes the
            // composition it has only just ended is the press that emptied
            // that composition. The syllable it announced is one the user has
            // deleted, so it is thrown away rather than inserted, the
            // platform's copy of it is swallowed when it turns up, and the
            // committed text behind it is not touched at all. 강ㄱ, one press,
            // 강 — where before the owed syllable was inserted and then the
            // character in front of it deleted instead.
            //
            // And then everything else this press produces is dropped, because
            // macOS produces more than one. The recording shows a backspace
            // that empties a Hangul composition arriving as a stray keycode-less
            // event, the backspace itself, the committed jamo, and the backspace
            // *again* — four events for one press, of which only the first two
            // carry anything. Unity's own InputField suppresses the lot with the
            // same test (character 0, no modifiers, composition just gone) and
            // therefore never deletes anything at all, which is the other half
            // of the bug report. This keeps the press and drops its tail.
            if (keyEvent.keyCode == KeyCode.Backspace && _model.DiscardOwedCommit())
            {
                _swallowingCompositionTail = true;
                _visualsDirty = true;
                Trace("dropped the composition the backspace emptied");
                return;
            }

            if (_swallowingCompositionTail && pressed == 0 &&
                keyEvent.keyCode == KeyCode.Backspace)
            {
                Trace("dropped a repeat of that backspace");
                return;
            }
            if ((pressed == 0 || char.IsControl(pressed)) && _model.SettleOwedCommit())
            {
                Changed();
                Trace("settled the owed commit");
            }

            bool shift = (keyEvent.modifiers & EventModifiers.Shift) != 0;
            bool command = (keyEvent.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            bool word = command || (keyEvent.modifiers & EventModifiers.Alt) != 0;

            switch (keyEvent.keyCode)
            {
                case KeyCode.Backspace:
                    if (_model.Backspace()) Changed();
                    Trace("backspaced");
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
                    else Submit();
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
            bool taken = _model.AcceptCharacter(character, out bool changed);
            Trace($"character {Quote(character.ToString())} taken={taken} changed={changed}");
            if (taken && changed) Changed();
            else _visualsDirty = true;

            // A character the arbiter swallowed can have the tail of its own
            // press riding behind it. Read off a recording of 21 Aug 2026:
            // ㅇㅇㅇㅇ committed, a click away and back, one press of Backspace
            // — and macOS sends the same four events it sends when a backspace
            // empties a composition: an empty one, the backspace, the jamo the
            // platform was still holding, and the backspace again. The first
            // backspace deletes the syllable, the jamo is swallowed as the
            // platform repeating a commit it already made — and the second
            // backspace, taken at face value, deleted a syllable the user
            // never asked about: one press, two characters gone. The swallowed
            // character is the one signal that the volley is a single press,
            // so it is what arms the guard that drops the tail.
            if (!taken) _swallowingCompositionTail = true;
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
            // A new value is a new thing to report the end of, whatever was
            // reported about the old one.
            _endEditReported = false;
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

        /// <summary>
        /// Makes sure something is cutting the text off at the edge of the
        /// field, whether or not anybody authored a viewport.
        ///
        /// A field with no <see cref="textViewport"/> clipped nothing at all,
        /// and that is nearly every field in existence: they were authored
        /// before the viewport was, or they arrived by having this component
        /// swapped onto somebody else's object. A long value ran out of the left
        /// edge while it was being typed, because the caret-follow scroll pushes
        /// it that way, and out of the right edge as soon as focus left, because
        /// the scroll goes back to zero and the value draws from its start.
        /// Reported from a real project against a screenshot, and the answer
        /// cannot be "author a viewport": nobody is going to open every scene.
        ///
        /// So the field makes one. The mask goes on the field's own object,
        /// which is the only thing here that is already above both labels, and
        /// it is marked DontSave for the same reason the caret graphic is —
        /// this belongs to the running field, not to anybody's saved scene, and
        /// nothing about their data changes.
        ///
        /// What this costs, said plainly. It clips at the field's own edge
        /// rather than at the inset an authored Text Area would give, so the
        /// text stops a padding-width later than TextMesh Pro would stop it. It
        /// clips every graphic under the field, not only the two labels, so a
        /// decoration somebody deliberately hung over the edge of a field is cut
        /// now. And it is a whole RectMask2D per field. All three are worth it
        /// against text drawing across the screen, and all three go away the
        /// moment a real viewport is authored — which the inspector has a button
        /// for.
        /// </summary>
        private void EnsureClipping()
        {
            // Authored. Their hierarchy, their arrangement; the inspector says
            // so if the thing they pointed at has no mask on it.
            if (_textViewport != null) return;

            // Already done, or already theirs. Either way the answer is the
            // same and this runs on every visual update, so it is worth one
            // GetComponent to stop before the walk.
            if (gameObject.GetComponent<RectMask2D>() != null) return;

            // Starting above the label rather than at it, because uGUI ignores a
            // RectMask2D that sits on the very graphic it would be clipping —
            // MaskUtilities skips the clippable's own object — so a mask there
            // would look like clipping and do nothing.
            for (var at = _textComponent.transform.parent; at != null; at = at.parent)
            {
                // Anything already cutting between the label and the field is a
                // shape somebody meant: an authored Text Area, the one a
                // converted TextMesh Pro field brings across with it, a mask a
                // project put there itself. Leave all of it alone.
                if (at.GetComponent<RectMask2D>() != null || at.GetComponent<Mask>() != null) return;
                if (at != transform) continue;

                if (gameObject.GetComponent<RectMask2D>() == null)
                    gameObject.AddComponent<RectMask2D>().hideFlags = HideFlags.DontSave;
                return;
            }

            // The walk reached the top of the scene without passing this field,
            // so the label is not underneath it — somebody pointed the reference
            // at a label living somewhere else entirely. There is nothing here
            // that contains it, and reaching up into whatever does own it to put
            // a mask on that would be editing a part of the scene this field has
            // no business touching. It stays unclipped, exactly as it is today,
            // and the inspector is where that gets said.
        }

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
            EnsureClipping();

            // Whatever the user types is text, not markup: a name with an angle
            // bracket in it must not turn half the field bold.
            _textComponent.RichText = false;
            // Same for escapes: a typed "\n" is a backslash and an n.
            _textComponent.ParseEscapes = false;
            // And a field is horizontal, the same way and for the same reason:
            // the field owns this label, and editing in a column (a caret that
            // moves down, arrow keys that mean the other axis, an IME candidate
            // window beside a column) is not implemented. Better to decline
            // here than to draw a field whose caret is at right angles to it.
            _textComponent.WritingMode = TextWritingMode.Horizontal;
            _textComponent.Text = _model.DisplayText;
            if (_placeholder != null)
                _placeholder.enabled = _model.DisplayText.Length == 0;

            ScrollCaretIntoView();
            UpdateCaretGeometry();
        }

        /// <summary>
        /// The box the caret has to stay inside, in the label's own space.
        ///
        /// The viewport when there is one, because that is the edge the mask
        /// cuts at and therefore the edge past which the caret stops being
        /// visible — and once a viewport exists the label underneath it is free
        /// to be bigger than the box, which is the whole point of having one.
        /// Without a viewport it is the label's own rect, which is what this
        /// measured against before and is what every field authored before the
        /// viewport existed still needs it to be.
        ///
        /// Converted through world space rather than read off either rect
        /// directly: the two are different transforms, and a label that is
        /// larger than its viewport or offset inside it would otherwise be
        /// compared against a box in somebody else's coordinates. When the label
        /// is stretched to the viewport, which is how the menu entry builds one,
        /// the conversion lands exactly on the label's own rect and nothing
        /// about the old behaviour changes.
        /// </summary>
        private Rect CaretBox()
        {
            var label = _textComponent.rectTransform;
            if (_textViewport == null || _textViewport == label) return label.rect;

            var box = _textViewport.rect;
            var min = label.InverseTransformPoint(_textViewport.TransformPoint(box.min));
            var max = label.InverseTransformPoint(_textViewport.TransformPoint(box.max));
            return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y),
                Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
        }

        /// <summary>Nudges the text label's scroll offset until the caret is inside the box.</summary>
        private void ScrollCaretIntoView()
        {
            if (!_focused)
            {
                // What the user left in view stays in view. Rewinding to the
                // start here is what every other field does and it reads well
                // in a list of values — but it costs the thing a user does far
                // more often, which is click back into a long value to carry on
                // typing at the end of it. The end was on screen when they left;
                // rewound, the click lands wherever the twelfth character
                // happens to be, and 입니다 goes into the middle of the string.
                //
                // Only clamped, so that a value replaced from script cannot
                // leave the field holding a window into a string that is no
                // longer there: scrolled past the end of the text, the offset
                // comes back until the end sits at the edge again.
                ClampScrollToEndOfText();
                return;
            }

            var box = CaretBox();
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

        /// <summary>
        /// Pulls the scroll back until the end of the text is no further left
        /// than the edge of the box, and never past the start. What it is for
        /// is a value assigned from script while the field is not focused: the
        /// offset the user left belongs to the string they left, and a shorter
        /// one would otherwise be drawn from a window past its own end.
        /// </summary>
        private void ClampScrollToEndOfText()
        {
            var offset = _textComponent.ScrollOffset;
            if (offset.x <= 0f && offset.y >= 0f) return;

            var box = CaretBox();
            var end = _textComponent.GetCaretRect(_model.Text.Length, _caretWidth);
            float overshoot = box.xMax - end.xMax;
            if (overshoot > 0f) offset.x -= overshoot;

            _textComponent.ScrollOffset = new Vector2(Mathf.Max(0f, offset.x), Mathf.Min(0f, offset.y));
        }

        private void UpdateCaretGeometry()
        {
            if (_caret == null || _textComponent == null) return;

            _caret.color = _caretColor;
            _caret.SelectionColor = _selectionColor;
            _caret.CompositionColor = _compositionColor;
            _caret.ClauseColor = _clauseColor;
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

        // ====================================================================
        // Input-field parity
        //
        // Unlike the region at the bottom of OneTextLabel, nothing here is
        // hidden from completion, because none of it is a second name for
        // something. This class already speaks the input-field vocabulary —
        // text, caretPosition, characterLimit, ActivateInputField — for the
        // same reason Unity's own field and TextMesh Pro's both do: it is what
        // the last twenty years of Unity code is written in, and a field that
        // renamed all of it would be a field nobody could migrate to. The
        // members below are the ones that vocabulary contains and this class
        // was missing, found by converting a real project and reading the
        // compiler errors.
        //
        // What is not here, and will not be: contentType. TextMesh Pro's
        // password, email and integer modes are validation and masking, and
        // OneText has neither, so the member could only accept the assignment
        // and go on drawing the password in clear text. The Onboarding report
        // names it instead, with what to do about it.
        // ====================================================================

        /// <summary>
        /// Raised when the value stops being edited: focus leaves the field, or
        /// the user commits with Return. Once per edit, and after the value has
        /// settled, so the string it carries is final — which is what makes it
        /// the right event for saving a setting or validating a name, where
        /// <see cref="onValueChanged"/> would fire on every keystroke.
        ///
        /// A composition the input method was still assembling is committed
        /// before this raises, so the last syllable is in the string.
        /// </summary>
        public UnityEvent<string> onEndEdit => _onEndEdit;

        /// <summary>Raised when the field gains focus, carrying the value as it stood.</summary>
        public UnityEvent<string> onSelect => _onSelect;

        /// <summary>
        /// Raised when the field loses focus. Fires after <see cref="onEndEdit"/>
        /// and carries the same committed value.
        /// </summary>
        public UnityEvent<string> onDeselect => _onDeselect;

        /// <summary>
        /// Sets the value without raising <see cref="onValueChanged"/>.
        ///
        /// For the case that makes the ordinary setter awkward: a field that
        /// listens to its own event to push edits somewhere, and has to be
        /// refilled from that same somewhere without the refill reading as an
        /// edit. Identical to assigning <see cref="text"/> in every other
        /// respect — the same character limit applies, the caret is clamped the
        /// same way, and the field redraws.
        /// </summary>
        public void SetTextWithoutNotify(string value)
        {
            value ??= string.Empty;
            PushSettings();
            if (_model.Text == value) return;
            _model.Text = value;
            _text = _model.Text;
            _visualsDirty = true;
            _endEditReported = false;
        }

        /// <summary>
        /// The label drawn while the field is empty. The field owns it: it is
        /// enabled and disabled as the value comes and goes.
        /// </summary>
        public OneTextLabel placeholder => _placeholder;

        /// <summary>
        /// Colour of the highlight drawn behind selected text.
        ///
        /// The colour belongs to the caret graphic, which draws it, but it is
        /// held here because that graphic is built at runtime and saved
        /// nowhere: on a field authored in a scene there is no instance of it
        /// for an inspector or a script to reach. TextMesh Pro spells the
        /// member exactly this way.
        /// </summary>
        public Color selectionColor
        {
            get => _selectionColor;
            set { _selectionColor = value; _visualsDirty = true; }
        }

        /// <summary>
        /// Colour of the underline drawn beneath text an input method is still
        /// composing. Named to sit beside <see cref="selectionColor"/> and not
        /// after anything: TextMesh Pro draws no composition, so there is no
        /// member here to be parity with.
        /// </summary>
        public Color compositionColor
        {
            get => _compositionColor;
            set { _compositionColor = value; _visualsDirty = true; }
        }

        /// <summary>
        /// Colour of the block behind the clause a Japanese input method is
        /// converting. Ours as well, for the same reason as
        /// <see cref="compositionColor"/>.
        /// </summary>
        public Color clauseColor
        {
            get => _clauseColor;
            set { _clauseColor = value; _visualsDirty = true; }
        }

        /// <summary>
        /// Whether focus arriving without a position selects the whole value,
        /// so that the first thing typed replaces it. On by default, as in
        /// TextMesh Pro.
        ///
        /// "Without a position" is where this parts company with TextMesh Pro,
        /// which selects on a mouse click as well. Tab and
        /// <see cref="ActivateInputField"/> say nothing about where in the value
        /// the user wants to be, and selecting it is the useful answer. A click
        /// says exactly where, every time and including the first, because a
        /// click that highlighted the whole value would throw it away on the
        /// next keystroke of an edit somebody was resuming.
        /// </summary>
        public bool onFocusSelectAll
        {
            get => _onFocusSelectAll;
            set => _onFocusSelectAll = value;
        }

        /// <summary>The caret end of the selection; the end that moves as you drag or shift-arrow.</summary>
        public int selectionFocusPosition
        {
            get => _model.Caret;
            set => SetSelection(_model.Anchor, value);
        }

        /// <summary>
        /// The caret as an index into <see cref="text"/>.
        ///
        /// The same number as <see cref="caretPosition"/>, and it exists
        /// because in TextMesh Pro it is not: that field counts its own
        /// characters in one member and the string's UTF-16 units in the other,
        /// and code written against the string index says so by using this
        /// name. OneText only ever counted the string, so both names answer it.
        /// </summary>
        public int stringPosition
        {
            get => _model.Caret;
            set => SetCaret(value, extendSelection: false);
        }

        /// <summary>
        /// Puts the caret before the first character.
        /// <paramref name="shift"/> extends the selection instead of collapsing it.
        /// </summary>
        public void MoveTextStart(bool shift) => SetCaret(0, shift);

        /// <summary>
        /// Puts the caret after the last character — the usual thing to do
        /// after filling a field from code.
        /// <paramref name="shift"/> extends the selection instead of collapsing it.
        /// </summary>
        public void MoveTextEnd(bool shift) => SetCaret(_model.Text.Length, shift);

        /// <summary>
        /// What Return does, in the three-way shape the other input fields
        /// state it in. OneText holds the same decision as
        /// <see cref="multiline"/>, one value narrower: this is the axis, and
        /// the two names are the same setting.
        /// </summary>
        public enum LineType
        {
            /// <summary>One line; Return commits the value.</summary>
            SingleLine = 0,

            /// <summary>Several lines, but Return still commits rather than breaking.</summary>
            MultiLineSubmit = 1,

            /// <summary>Several lines; Return inserts a newline.</summary>
            MultiLineNewline = 2,
        }

        /// <summary>
        /// Parity alias for <see cref="multiline"/> in the three-value enum.
        ///
        /// Lossy in one direction, and the same loss the Onboarding migration
        /// takes: <see cref="LineType.MultiLineSubmit"/> asks for two things at
        /// once — several lines, and a Return that commits — and this field
        /// spends one bit on both. It sets multiline, because losing the lines
        /// is the more visible half of getting it wrong, and reads back as
        /// <see cref="LineType.MultiLineNewline"/>. A field that genuinely
        /// wants both should stay multiline and call the commit itself from a
        /// key handler.
        /// </summary>
        public LineType lineType
        {
            get => _multiline ? LineType.MultiLineNewline : LineType.SingleLine;
            set
            {
                // Guarded because MultiLineSubmit never reads back as itself,
                // so the compare-then-assign idiom would otherwise re-assign
                // every frame and redraw the field for it.
                bool wanted = value != LineType.SingleLine;
                if (_multiline == wanted) return;
                multiline = wanted;
            }
        }
    }
}
