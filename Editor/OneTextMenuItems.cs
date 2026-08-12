using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// GameObject menu entries that create OneText components already wired
    /// up: a fresh label renders immediately, a fresh input field is editable
    /// and a fresh dropdown opens, all without touching a single reference
    /// field.
    /// </summary>
    public static class OneTextMenuItems
    {
        [MenuItem("GameObject/UI/OneText/Label", false, 2100)]
        public static void CreateLabel(MenuCommand command)
        {
            var parent = EnsureCanvas(command);
            // Size, wrapping, markup and the rest come from Project Settings >
            // OneText: AddComponent runs Reset, which reads them.
            var go = CreateGraphicObject("OneText Label", parent,
                OneTextSettings.ProjectDefaults.CanvasSize);
            var label = go.AddComponent<OneTextLabel>();
            label.color = Color.white;
            Register(go, "Create OneText Label");
        }

        [MenuItem("GameObject/UI/OneText/Input Field", false, 2101)]
        public static void CreateInputField(MenuCommand command)
        {
            var parent = EnsureCanvas(command);

            var root = CreateGraphicObject("OneText Input Field", parent, new Vector2(320f, 60f));
            var background = root.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1f);

            // The masked box between the background and the labels, which is
            // where Unity's input field and TextMesh Pro's both put one and
            // which this had never had: with the labels parented straight to the
            // background, a value longer than the field drew out of its left
            // edge and across whatever was next to it. The padding that used to
            // be on each label is on this instead, so a field created today
            // looks exactly like one created yesterday and stops at its edges.
            //
            // RectMask2D rather than Mask: no stencil, no extra draw call for
            // the mask graphic, and no graphic needed on this object at all.
            // OneTextLabel is a MaskableGraphic and the OneText SDF shader
            // carries the _ClipRect path RectMask2D drives, so the clipping is
            // the one uGUI already does rather than anything of ours.
            var viewportGo = new GameObject("Text Area", typeof(RectTransform));
            viewportGo.transform.SetParent(root.transform, false);
            var viewport = viewportGo.GetComponent<RectTransform>();
            Stretch(viewport, 10f);
            viewportGo.AddComponent<RectMask2D>();

            var textArea = CreateGraphicObject("Text", viewportGo.transform, Vector2.zero);
            Stretch(textArea.GetComponent<RectTransform>(), 0f);
            var textLabel = textArea.AddComponent<OneTextLabel>();
            textLabel.color = Color.white;
            textLabel.FontSize = 28f;
            textLabel.Wrap = TextWrap.NoWrap;
            textLabel.Alignment = TextAlignment.Start;
            textLabel.VerticalAlignment = VerticalAlignment.Middle;
            textLabel.Text = string.Empty;
            textLabel.raycastTarget = false;

            var placeholderGo = CreateGraphicObject("Placeholder", viewportGo.transform, Vector2.zero);
            Stretch(placeholderGo.GetComponent<RectTransform>(), 0f);
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
            serialized.FindProperty("_textViewport").objectReferenceValue = viewport;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            field.targetGraphic = background;

            EnsureEventSystem();
            Register(root, "Create OneText Input Field");
        }

        /// <summary>
        /// A dropdown that already opens.
        ///
        /// A label or an input field is had by adding a component to an empty
        /// object; a dropdown is not. The list is a template hierarchy that
        /// <see cref="OneTextDropdown"/> duplicates at runtime, and the
        /// component finds its way around that hierarchy entirely by component
        /// search: a Toggle one level under the template and never on it, a
        /// OneTextLabel somewhere under the Toggle, the Toggle's own parent
        /// standing in for the content the rows go into. Miss any of it and
        /// Show says the template is not set up correctly without saying which
        /// part. Before this entry the only way to hold a working one was to
        /// convert a project that already had one, which is no help to anybody
        /// starting a screen from nothing.
        /// </summary>
        [MenuItem("GameObject/UI/OneText/Dropdown", false, 2102)]
        public static void CreateDropdown(MenuCommand command)
        {
            var parent = EnsureCanvas(command);

            var root = CreateGraphicObject("OneText Dropdown", parent, new Vector2(320f, 60f));
            var background = root.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1f);

            var captionGo = CreateGraphicObject("Label", root.transform, Vector2.zero);
            Inset(captionGo, new Vector2(14f, 8f), new Vector2(-14f, -8f));
            var caption = captionGo.AddComponent<OneTextLabel>();
            caption.color = Color.white;
            caption.FontSize = 28f;
            caption.Wrap = TextWrap.NoWrap;
            caption.Alignment = TextAlignment.Start;
            caption.VerticalAlignment = VerticalAlignment.Middle;
            // The caption covers the background the dropdown raycasts against,
            // and OneTextLabel handles pointer clicks itself — so a caption that
            // takes clicks is a dropdown that never opens, the click having been
            // answered by the label and never offered to the parent.
            caption.raycastTarget = false;

            var templateGo = CreateGraphicObject("Template", root.transform, Vector2.zero);
            var templateRect = templateGo.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, 2f);
            templateRect.sizeDelta = new Vector2(0f, 150f);
            var templateBackground = templateGo.AddComponent<Image>();
            templateBackground.color = new Color(0.12f, 0.12f, 0.14f, 1f);
            var scroll = templateGo.AddComponent<ScrollRect>();

            // Nothing in OneTextDropdown looks for a viewport; a list of more
            // options than the template is tall is what looks for one. The
            // graphic under the mask is clear because it is there to be dragged
            // on, not to be seen: without it a drag that starts on empty list
            // space reaches no raycaster and the list will not scroll.
            var viewportGo = CreateGraphicObject("Viewport", templateGo.transform, Vector2.zero);
            var viewportRect = viewportGo.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.pivot = new Vector2(0f, 1f);
            viewportRect.sizeDelta = Vector2.zero;
            viewportGo.AddComponent<Image>().color = Color.clear;
            viewportGo.AddComponent<RectMask2D>();

            // Nothing lays this out, deliberately. Show does the arithmetic
            // itself — it has to, because a dropdown converted from uGUI or TMP
            // arrives with a bare content for exactly the same reason — and a
            // layout group here would spend every rebuild overwriting the
            // positions Show just wrote, with the winner decided by frame order.
            // So the content is authored the way Unity's is: one item tall plus
            // the padding above and below it, which is the only thing Show reads
            // off it. Height 52 is the 44 of a row and 4 either side.
            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = contentGo.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 52f);

            scroll.content = contentRect;
            scroll.viewport = viewportRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            // The Toggle is the item, and it is one level under the template
            // rather than on it because the search that finds it skips the root.
            //
            // Centred in the content rather than pinned to its top, which is
            // Unity's anchoring and is what makes the padding readable: Show
            // measures the gap between this rect and the content's edges and
            // uses it as the list's padding, so an item centred in a content
            // 8 taller than itself means 4 above the first row and 4 below the
            // last one, however many rows there turn out to be.
            var itemGo = new GameObject("Item", typeof(RectTransform));
            itemGo.transform.SetParent(contentGo.transform, false);
            var itemRect = itemGo.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.anchoredPosition = Vector2.zero;
            itemRect.sizeDelta = new Vector2(0f, 44f);
            var toggle = itemGo.AddComponent<Toggle>();
            toggle.isOn = true;

            var itemBackgroundGo = CreateGraphicObject("Item Background", itemGo.transform, Vector2.zero);
            Stretch(itemBackgroundGo.GetComponent<RectTransform>(), 0f);
            var itemBackground = itemBackgroundGo.AddComponent<Image>();
            itemBackground.color = new Color(0.2f, 0.2f, 0.23f, 1f);

            var checkmarkGo = CreateGraphicObject("Item Checkmark", itemGo.transform, new Vector2(16f, 16f));
            var checkmarkRect = checkmarkGo.GetComponent<RectTransform>();
            checkmarkRect.anchorMin = new Vector2(0f, 0.5f);
            checkmarkRect.anchorMax = new Vector2(0f, 0.5f);
            checkmarkRect.anchoredPosition = new Vector2(16f, 0f);
            var checkmark = checkmarkGo.AddComponent<Image>();
            checkmark.color = new Color(0.9f, 0.9f, 0.95f, 1f);

            var itemLabelGo = CreateGraphicObject("Item Label", itemGo.transform, Vector2.zero);
            Inset(itemLabelGo, new Vector2(40f, 2f), new Vector2(-10f, -2f));
            var itemLabel = itemLabelGo.AddComponent<OneTextLabel>();
            itemLabel.color = Color.white;
            itemLabel.FontSize = 28f;
            itemLabel.Wrap = TextWrap.NoWrap;
            itemLabel.Alignment = TextAlignment.Start;
            itemLabel.VerticalAlignment = VerticalAlignment.Middle;
            itemLabel.Text = "Option A";
            // The row's whole job is to be clicked, and the label lies across
            // all of it. Same reason as the caption, one level down: the label
            // answers the click and the Toggle underneath never hears it, so the
            // list would open, highlight nothing and close on nothing.
            itemLabel.raycastTarget = false;

            // Exactly two graphics on the row, and both belong to the Toggle.
            // The search for an option's picture takes the first child Image
            // that is neither of these, so a third one added here would be
            // adopted as the picture and tinted by every option's colour.
            toggle.targetGraphic = itemBackground;
            toggle.graphic = checkmark;

            // Deactivated before the component exists, not after. Awake also
            // deactivates the template, but only for a dropdown that already
            // holds the reference — here the reference is written further down,
            // so without this the list would be open for the frame between.
            templateGo.SetActive(false);

            var dropdown = root.AddComponent<OneTextDropdown>();
            var serialized = new SerializedObject(dropdown);
            serialized.FindProperty("m_Template").objectReferenceValue = templateRect;
            serialized.FindProperty("m_CaptionText").objectReferenceValue = caption;
            // Show and AddItem never read m_ItemText: a row's label is found by
            // searching the instantiated row, which is the only thing that can
            // work once the row is a copy. It is written anyway because the
            // itemText property is public, a converted project's code reads it,
            // and a fresh dropdown answering None to a question every other
            // dropdown answers is a difference with nothing behind it.
            serialized.FindProperty("m_ItemText").objectReferenceValue = itemLabel;
            // m_CaptionImage and m_ItemImage are left None because there is
            // nothing here to point them at: neither Unity's dropdown nor
            // TextMesh Pro's authors an image object either, so a converted
            // dropdown arrives without one too, and both are read behind a null
            // check. A picture slot invented here would be a picture slot every
            // migrated dropdown in the same scene is missing.
            serialized.ApplyModifiedPropertiesWithoutUndo();
            dropdown.targetGraphic = background;

            // The same three Unity seeds a new dropdown with, so a scene that
            // swapped one for the other still reads the same in the inspector.
            dropdown.options.Add(new OneTextDropdown.OptionData("Option A"));
            dropdown.options.Add(new OneTextDropdown.OptionData("Option B"));
            dropdown.options.Add(new OneTextDropdown.OptionData("Option C"));
            dropdown.RefreshShownValue();

            EnsureEventSystem();
            Register(root, "Create OneText Dropdown");
        }

        [MenuItem("GameObject/3D Object/OneText/Text Mesh", false, 2100)]
        public static void CreateWorldText(MenuCommand command)
        {
            // World text lives under whatever was right-clicked (or the scene
            // root), never under a canvas: no canvas is the point.
            var go = new GameObject("OneText Mesh", typeof(RectTransform));
            var parent = command.context as GameObject;
            if (parent != null) go.transform.SetParent(parent.transform, false);
            // TMP's world-text defaults (20×5 rect, size 36) are what the
            // project starts with, so a scene that swapped a TextMeshPro object
            // for this one sees the same box and the same glyph height. Both
            // numbers are the project's to change.
            go.GetComponent<RectTransform>().sizeDelta = OneTextSettings.ProjectDefaults.WorldSize;
            go.AddComponent<OneTextMesh>();
            Register(go, "Create OneText Mesh");
        }

        private static Transform EnsureCanvas(MenuCommand command)
        {
            var context = command.context as GameObject;
            if (context != null && context.GetComponentInParent<Canvas>() != null)
                return context.transform;

            var canvas = Object.FindFirstObjectByType<Canvas>();
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
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            // Not typeof(StandaloneInputModule) in the line above: which module
            // works depends on the project's input backend, and the wrong one
            // throws on every frame. See OneTextEventSystemFactory.
            OneTextEventSystemFactory.AddInputModule(go);
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

        /// <summary>
        /// <see cref="Stretch"/> for the cases where the four sides differ,
        /// which the labels inside a dropdown all do.
        /// </summary>
        private static void Inset(GameObject go, Vector2 min, Vector2 max)
        {
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = min;
            rect.offsetMax = max;
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
