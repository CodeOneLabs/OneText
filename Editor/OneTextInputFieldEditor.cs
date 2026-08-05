using OneText.UGUI;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>Inspector for <see cref="OneTextInputField"/>.</summary>
    [CustomEditor(typeof(OneTextInputField))]
    [CanEditMultipleObjects]
    public sealed class OneTextInputFieldEditor : SelectableEditor
    {
        private SerializedProperty _textComponent, _placeholder, _caret, _text;
        private SerializedProperty _multiline, _readOnly, _characterLimit, _inputMethodEnabled;
        private SerializedProperty _caretColor, _caretWidth, _caretBlinkRate;
        private SerializedProperty _onValueChanged, _onSubmit;

        protected override void OnEnable()
        {
            base.OnEnable();
            _textComponent = serializedObject.FindProperty("_textComponent");
            _placeholder = serializedObject.FindProperty("_placeholder");
            _caret = serializedObject.FindProperty("_caret");
            _text = serializedObject.FindProperty("_text");
            _multiline = serializedObject.FindProperty("_multiline");
            _readOnly = serializedObject.FindProperty("_readOnly");
            _characterLimit = serializedObject.FindProperty("_characterLimit");
            _inputMethodEnabled = serializedObject.FindProperty("_inputMethodEnabled");
            _caretColor = serializedObject.FindProperty("_caretColor");
            _caretWidth = serializedObject.FindProperty("_caretWidth");
            _caretBlinkRate = serializedObject.FindProperty("_caretBlinkRate");
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
            if (_textComponent.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a OneText Label to draw the text. GameObject > UI > OneText Input Field " +
                    "creates a fully wired field.", MessageType.Warning);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_text);
            EditorGUILayout.PropertyField(_multiline);
            EditorGUILayout.PropertyField(_readOnly, new GUIContent("Read only"));
            EditorGUILayout.PropertyField(_characterLimit, new GUIContent("Character limit"));
            EditorGUILayout.PropertyField(_inputMethodEnabled, new GUIContent("Input method (IME)"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Caret", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_caretColor, new GUIContent("Color"));
            EditorGUILayout.PropertyField(_caretWidth, new GUIContent("Width"));
            EditorGUILayout.PropertyField(_caretBlinkRate, new GUIContent("Blink rate"));

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
    }
}
