using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// GameObject menu entries that create OneText components already wired
    /// up: a fresh label renders immediately, and a fresh input field is
    /// editable without touching a single reference field.
    /// </summary>
    public static class OneTextMenuItems
    {
        [MenuItem("GameObject/UI/OneText Label", false, 2100)]
        public static void CreateLabel(MenuCommand command)
        {
            var parent = EnsureCanvas(command);
            var go = CreateGraphicObject("OneText Label", parent, new Vector2(320f, 80f));
            var label = go.AddComponent<OneTextLabel>();
            label.color = Color.white;
            Register(go, "Create OneText Label");
        }

        [MenuItem("GameObject/UI/OneText Input Field", false, 2101)]
        public static void CreateInputField(MenuCommand command)
        {
            var parent = EnsureCanvas(command);

            var root = CreateGraphicObject("OneText Input Field", parent, new Vector2(320f, 60f));
            var background = root.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1f);

            var textArea = CreateGraphicObject("Text", root.transform, Vector2.zero);
            Stretch(textArea.GetComponent<RectTransform>(), 10f);
            var textLabel = textArea.AddComponent<OneTextLabel>();
            textLabel.color = Color.white;
            textLabel.FontSize = 28f;
            textLabel.Wrap = TextWrap.NoWrap;
            textLabel.Alignment = TextAlignment.Start;
            textLabel.VerticalAlignment = VerticalAlignment.Middle;
            textLabel.Text = string.Empty;
            textLabel.raycastTarget = false;

            var placeholderGo = CreateGraphicObject("Placeholder", root.transform, Vector2.zero);
            Stretch(placeholderGo.GetComponent<RectTransform>(), 10f);
            var placeholder = placeholderGo.AddComponent<OneTextLabel>();
            placeholder.color = new Color(1f, 1f, 1f, 0.4f);
            placeholder.FontSize = 28f;
            placeholder.Wrap = TextWrap.NoWrap;
            placeholder.VerticalAlignment = VerticalAlignment.Middle;
            placeholder.Text = "Enter text…";
            placeholder.raycastTarget = false;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = textLabel;
            serialized.FindProperty("_placeholder").objectReferenceValue = placeholder;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            field.targetGraphic = background;

            EnsureEventSystem();
            Register(root, "Create OneText Input Field");
        }

        [MenuItem("GameObject/3D Object/OneText Mesh (World)", false, 2102)]
        public static void CreateWorldText(MenuCommand command)
        {
            // World text lives under whatever was right-clicked (or the scene
            // root), never under a canvas: no canvas is the point.
            var go = new GameObject("OneText Mesh", typeof(RectTransform));
            var parent = command.context as GameObject;
            if (parent != null) go.transform.SetParent(parent.transform, false);
            // TMP's world-text defaults (20×5 rect, size 36): a scene that
            // swapped a TextMeshPro object for this one sees the same box and
            // the same glyph height.
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(20f, 5f);
            go.AddComponent<OneTextMesh>();
            Register(go, "Create OneText Mesh");
        }

        private static Transform EnsureCanvas(MenuCommand command)
        {
            var context = command.context as GameObject;
            if (context != null && context.GetComponentInParent<Canvas>() != null)
                return context.transform;

            var canvas = Object.FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                    typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
            }
            EnsureEventSystem();
            return canvas.transform;
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(go, "Create EventSystem");
        }

        /// <summary>
        /// Graphics need their RectTransform and CanvasRenderer up front:
        /// inherited RequireComponent is not honored by AddComponent.
        /// </summary>
        private static GameObject CreateGraphicObject(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            if (size != Vector2.zero) rect.sizeDelta = size;
            return go;
        }

        private static void Stretch(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        private static void Register(GameObject go, string undoName)
        {
            Undo.RegisterCreatedObjectUndo(go, undoName);
            Selection.activeGameObject = go;
        }
    }
}
