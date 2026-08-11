using OneText.UGUI;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>Inspector for <see cref="OneTextInputField"/>.</summary>
    [CustomEditor(typeof(OneTextInputField))]
    [CanEditMultipleObjects]
    public sealed class OneTextInputFieldEditor : SelectableEditor
    {
        private SerializedProperty _textComponent, _placeholder, _caret, _text;
        private SerializedProperty _textViewport;
        private SerializedProperty _multiline, _readOnly, _characterLimit, _inputMethodEnabled;
        private SerializedProperty _caretColor, _caretWidth, _caretBlinkRate;
        private SerializedProperty _selectionColor, _compositionColor, _clauseColor;
        private SerializedProperty _onFocusSelectAll;
        private SerializedProperty _onValueChanged, _onSubmit;

        protected override void OnEnable()
        {
            base.OnEnable();
            _textComponent = serializedObject.FindProperty("_textComponent");
            _placeholder = serializedObject.FindProperty("_placeholder");
            _textViewport = serializedObject.FindProperty("_textViewport");
            _caret = serializedObject.FindProperty("_caret");
            _text = serializedObject.FindProperty("_text");
            _multiline = serializedObject.FindProperty("_multiline");
            _readOnly = serializedObject.FindProperty("_readOnly");
            _characterLimit = serializedObject.FindProperty("_characterLimit");
            _inputMethodEnabled = serializedObject.FindProperty("_inputMethodEnabled");
            _caretColor = serializedObject.FindProperty("_caretColor");
            _caretWidth = serializedObject.FindProperty("_caretWidth");
            _caretBlinkRate = serializedObject.FindProperty("_caretBlinkRate");
            _selectionColor = serializedObject.FindProperty("_selectionColor");
            _compositionColor = serializedObject.FindProperty("_compositionColor");
            _clauseColor = serializedObject.FindProperty("_clauseColor");
            _onFocusSelectAll = serializedObject.FindProperty("_onFocusSelectAll");
            _onValueChanged = serializedObject.FindProperty("_onValueChanged");
            _onSubmit = serializedObject.FindProperty("_onSubmit");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI(); // Selectable: interactable, transitions, navigation
            EditorGUILayout.Space();
            serializedObject.Update();

            EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_textComponent, new GUIContent("Text"));
            EditorGUILayout.PropertyField(_placeholder);
            EditorGUILayout.PropertyField(_caret, new GUIContent("Caret graphic"));
            EditorGUILayout.PropertyField(_textViewport, new GUIContent("Text viewport"));
            if (_textComponent.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a OneText Label to draw the text. GameObject > UI > OneText > Input Field " +
                    "creates a fully wired field.", MessageType.Warning);
            }
            // Said separately from the label warning, and as information rather
            // than a warning, because a field without a viewport is not broken:
            // it types, it scrolls, and the field puts a mask on itself at
            // runtime so the text still stops at its own edge. What it is
            // missing is the padding-width inset and the authored shape, which
            // is a thing to offer rather than a thing to complain about.
            if (_textViewport.objectReferenceValue == null && targets.Length == 1)
                DrawViewportOffer(target as OneTextInputField);
            else if (_textViewport.objectReferenceValue is RectTransform viewport &&
                     viewport.GetComponent<RectMask2D>() == null && viewport.GetComponent<Mask>() == null)
            {
                EditorGUILayout.HelpBox(
                    "The text viewport has no RectMask2D or Mask on it, so nothing clips: the " +
                    "reference is what the caret is kept inside, the mask is what cuts the text.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_text);
            EditorGUILayout.PropertyField(_multiline);
            EditorGUILayout.PropertyField(_readOnly, new GUIContent("Read only"));
            EditorGUILayout.PropertyField(_characterLimit, new GUIContent("Character limit"));
            EditorGUILayout.PropertyField(_inputMethodEnabled, new GUIContent("Input method (IME)"));
            EditorGUILayout.PropertyField(_onFocusSelectAll,
                new GUIContent("Select all on keyboard focus"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Caret", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_caretColor, new GUIContent("Color"));
            EditorGUILayout.PropertyField(_caretWidth, new GUIContent("Width"));
            EditorGUILayout.PropertyField(_caretBlinkRate, new GUIContent("Blink rate"));
            // The caret graphic these three end up on is made at runtime and
            // never saved, so this inspector is the only place they can be
            // authored at all.
            EditorGUILayout.PropertyField(_selectionColor, new GUIContent("Selection color"));
            EditorGUILayout.PropertyField(_compositionColor, new GUIContent("Composition underline"));
            EditorGUILayout.PropertyField(_clauseColor, new GUIContent("Converting clause"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.PropertyField(_onValueChanged);
            EditorGUILayout.PropertyField(_onSubmit);

            // Composition is the state that is hardest to reason about from the
            // outside, so the inspector shows it while the game runs.
            if (Application.isPlaying && targets.Length == 1 && target is OneTextInputField field &&
                field.isComposing)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox($"Composing: \u201c{field.compositionString}\u201d", MessageType.None);
                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// What to say, and what to offer, to a field with no viewport.
        ///
        /// Three different states hide behind one empty reference, and telling
        /// somebody they are broken when they are not is worse than saying
        /// nothing: a field whose labels already sit under a mask is fine, a
        /// field whose labels sit under the field itself is clipped at runtime
        /// and can be upgraded here, and a field pointed at a label living
        /// somewhere else entirely cannot be helped from this inspector at all.
        /// </summary>
        private void DrawViewportOffer(OneTextInputField field)
        {
            var label = field == null ? null : field.textComponent;
            if (label == null) return;

            if (!IsUnder(label.transform, field.transform))
            {
                EditorGUILayout.HelpBox(
                    "The Text label is not a child of this field, so nothing here can clip it and " +
                    "a long value will draw outside the field. Move the label under the field, or " +
                    "assign a masked viewport that contains it.", MessageType.Warning);
                return;
            }

            if (MaskAbove(label.transform, field.transform) != null)
            {
                EditorGUILayout.HelpBox(
                    "No text viewport assigned, but the labels are already inside a mask, so the " +
                    "text is clipped. Assigning that object as the Text Viewport is what makes " +
                    "the caret scroll to its edge rather than the label's.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "No text viewport. The field masks itself at runtime so a long value stops at the " +
                "field's edge, but it clips everything under the field and cannot inset the text. " +
                "Adding a Text Area gives it the shape a new field is built with.",
                MessageType.Info);

            if (GUILayout.Button("Add Text Area"))
                AddTextArea(field);
        }

        private static bool IsUnder(Transform child, Transform ancestor)
        {
            for (var at = child; at != null; at = at.parent)
                if (at == ancestor) return true;
            return false;
        }

        /// <summary>
        /// A mask between the label and the field, exclusive of the label's own
        /// object — uGUI ignores a RectMask2D sitting on the graphic it would be
        /// clipping, so one there does not count.
        /// </summary>
        private static Component MaskAbove(Transform label, Transform field)
        {
            for (var at = label.parent; at != null; at = at.parent)
            {
                var rect = at.GetComponent<RectMask2D>();
                if (rect != null) return rect;
                var stencil = at.GetComponent<Mask>();
                if (stencil != null) return stencil;
                if (at == field) return null;
            }
            return null;
        }

        /// <summary>
        /// Interposes the Text Area the menu entry builds, on a field that was
        /// made before there was one.
        ///
        /// The new object takes the text label's own rect rather than a guess at
        /// the padding, so nothing on screen moves: whatever box the text was
        /// drawing in is the box the mask now cuts at. The labels are then
        /// stretched to fill it, which is where they already were.
        ///
        /// Every step is undoable, and it is one undo rather than five, because
        /// a half-undone hierarchy is worse than either end of it.
        /// </summary>
        private void AddTextArea(OneTextInputField field)
        {
            const string Name = "Add Text Area";
            var label = field.textComponent.rectTransform;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            var go = new GameObject("Text Area", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, Name);
            Undo.SetTransformParent(go.transform, field.transform, Name);

            var viewport = (RectTransform)go.transform;
            viewport.SetSiblingIndex(label.GetSiblingIndex());
            viewport.anchorMin = label.anchorMin;
            viewport.anchorMax = label.anchorMax;
            viewport.pivot = label.pivot;
            viewport.anchoredPosition = label.anchoredPosition;
            viewport.sizeDelta = label.sizeDelta;
            viewport.offsetMin = label.offsetMin;
            viewport.offsetMax = label.offsetMax;
            viewport.localScale = Vector3.one;
            Undo.AddComponent<RectMask2D>(go);

            Adopt(field.textComponent, viewport, Name);
            Adopt(field.placeholder, viewport, Name);

            serializedObject.Update();
            _textViewport.objectReferenceValue = viewport;
            serializedObject.ApplyModifiedProperties();

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = field.gameObject;
        }

        private static void Adopt(OneTextLabel label, RectTransform viewport, string undoName)
        {
            if (label == null) return;

            Undo.SetTransformParent(label.transform, viewport, undoName);
            var rect = label.rectTransform;
            Undo.RecordObject(rect, undoName);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;
        }
    }
}
