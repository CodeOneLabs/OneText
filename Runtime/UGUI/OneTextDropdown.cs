using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// A dropdown whose caption and item labels are <see cref="OneTextLabel"/>.
    ///
    /// It exists because <c>UnityEngine.UI.Dropdown</c> declares those two
    /// fields as <c>Text</c>, <c>TMP_Dropdown</c> declares them as
    /// <c>TMP_Text</c>, and nothing can widen either. Convert the labels a
    /// dropdown points at and the fields cannot hold what replaced them: they
    /// read None, the caption goes blank, and the list draws empty rows. On a
    /// real project that was the single largest group of references the
    /// migration could name but not mend — every one of them a dropdown.
    ///
    /// It stands in for both, which is why <c>OptionData</c> carries a colour
    /// Unity's has not got: TextMesh Pro's does, and a dropdown converted from
    /// there arrives holding it.
    ///
    /// Deliberately the same shape as Unity's, member for member, because the
    /// point is that a converted scene keeps working and a project's own code
    /// keeps compiling. <c>options</c>, <c>value</c>, <c>onValueChanged</c>,
    /// <c>template</c>, <c>captionText</c>, <c>captionImage</c>,
    /// <c>itemText</c>, <c>itemImage</c>, <c>Show</c>, <c>Hide</c>,
    /// <c>RefreshShownValue</c>, <c>AddOptions</c>, <c>ClearOptions</c> all mean
    /// what they mean there. What is different is the two label types and that
    /// this one closes on a click anywhere outside, which Unity's also does
    /// through a full-screen blocker built the same way.
    /// </summary>
    [AddComponentMenu("OneText/OneText Dropdown")]
    [RequireComponent(typeof(RectTransform))]
    public sealed class OneTextDropdown : Selectable, IPointerClickHandler, ISubmitHandler,
        ICancelHandler
    {
        [Serializable]
        public class OptionData
        {
            [SerializeField] private string m_Text;
            [SerializeField] private Sprite m_Image;
            // TextMesh Pro's dropdown has this and Unity's does not, and a
            // dropdown converted from TMP arrives holding it. It tints the
            // image, not the text — the same thing it does there.
            [SerializeField] private Color m_Color = Color.white;

            public string text { get => m_Text; set => m_Text = value; }
            public Sprite image { get => m_Image; set => m_Image = value; }
            public Color color { get => m_Color; set => m_Color = value; }

            public OptionData() { }
            public OptionData(string text) { m_Text = text; }
            public OptionData(Sprite image) { m_Image = image; }
            public OptionData(string text, Sprite image) { m_Text = text; m_Image = image; }

            public OptionData(string text, Sprite image, Color color)
            {
                m_Text = text;
                m_Image = image;
                m_Color = color;
            }
        }

        [Serializable]
        public class OptionDataList
        {
            [SerializeField] private List<OptionData> m_Options = new List<OptionData>();

            public List<OptionData> options { get => m_Options; set => m_Options = value; }
        }

        [Serializable]
        public class DropdownEvent : UnityEvent<int> { }

        [SerializeField] private RectTransform m_Template;
        [SerializeField] private OneTextLabel m_CaptionText;
        [SerializeField] private Image m_CaptionImage;
        [SerializeField] private OneTextLabel m_ItemText;
        [SerializeField] private Image m_ItemImage;
        [SerializeField] private int m_Value;
        [SerializeField] private OptionDataList m_Options = new OptionDataList();
        [SerializeField] private DropdownEvent m_OnValueChanged = new DropdownEvent();
        // Nothing reads this and the list does not fade. It is kept, and the
        // migration copies it across, because a converted dropdown arrives
        // holding a number a designer chose and dropping it would be losing
        // something with nowhere else to live — but it is inert, and saying so
        // here is better than an inspector field that quietly does nothing.
        //
        // Not implemented rather than not noticed: Unity fades by keeping the
        // closed list alive for the length of the fade and destroying it from a
        // coroutine afterwards. Closing here cannot wait like that. The same
        // close runs when the component is disabled — under a panel or a scene
        // that is already on its way out, with nothing left to run a coroutine
        // on — and it runs in the editor, where there are no coroutines at all.
        // A fade would mean the list staying open after it was told to close, in
        // exactly those cases.
        [SerializeField] private float m_AlphaFadeSpeed = 0.15f;

        private GameObject m_Dropdown;
        private GameObject m_Blocker;
        private readonly List<Item> m_Items = new List<Item>();
        private bool m_Validated;

        // Unity's dropdown calls this kHighSortingLayer and puts the open list
        // on it. The number is copied rather than chosen so that a converted
        // scene's own canvases stand in the same relation to an open list as
        // they stood to a uGUI or TMP one.
        private const int ListSortingOrder = 30000;

        public RectTransform template { get => m_Template; set { m_Template = value; Refresh(); } }
        public OneTextLabel captionText { get => m_CaptionText; set { m_CaptionText = value; Refresh(); } }
        public Image captionImage { get => m_CaptionImage; set { m_CaptionImage = value; Refresh(); } }
        public OneTextLabel itemText { get => m_ItemText; set { m_ItemText = value; Refresh(); } }
        public Image itemImage { get => m_ItemImage; set { m_ItemImage = value; Refresh(); } }

        public List<OptionData> options
        {
            get => m_Options.options;
            set { m_Options.options = value; Refresh(); }
        }

        public DropdownEvent onValueChanged
        {
            get => m_OnValueChanged;
            set => m_OnValueChanged = value;
        }

        /// <summary>
        /// Kept for parity and carried across by the migration, but inert: this
        /// dropdown opens and closes immediately and reads this number nowhere.
        /// </summary>
        public float alphaFadeSpeed { get => m_AlphaFadeSpeed; set => m_AlphaFadeSpeed = value; }

        /// <summary>Whether the list is open. Read-only; use <see cref="Show"/> and <see cref="Hide"/>.</summary>
        public bool IsExpanded => m_Dropdown != null;

        public int value
        {
            get => m_Value;
            set => SetValue(value, true);
        }

        /// <summary>Sets the value without raising <see cref="onValueChanged"/>.</summary>
        public void SetValueWithoutNotify(int input) => SetValue(input, false);

        private void SetValue(int input, bool notify)
        {
            int clamped = options.Count == 0 ? 0 : Mathf.Clamp(input, 0, options.Count - 1);
            if (Application.isPlaying && (clamped == m_Value || options.Count == 0)) return;

            m_Value = clamped;
            Refresh();
            if (notify && Application.isPlaying) m_OnValueChanged.Invoke(m_Value);
        }

        protected override void Awake()
        {
            base.Awake();
            if (m_Template != null) m_Template.gameObject.SetActive(false);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Refresh();
        }

        protected override void OnDisable()
        {
            // The one caller that is not allowed to destroy anything on the
            // spot: whatever disabled this dropdown is usually still halfway
            // through disabling or destroying something above it, and Unity
            // refuses a destroy underneath that. See <see cref="Discard"/>.
            Close(deferred: true);
            base.OnDisable();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (!IsActive()) return;
            m_Value = options.Count == 0 ? 0 : Mathf.Clamp(m_Value, 0, options.Count - 1);
            Refresh();
        }
#endif

        /// <summary>Puts the current option into the caption. Call after editing <see cref="options"/> in place.</summary>
        public void RefreshShownValue() => Refresh();

        private void Refresh()
        {
            OptionData data = null;
            if (options.Count > 0) data = options[Mathf.Clamp(m_Value, 0, options.Count - 1)];

            if (m_CaptionText != null)
            {
                m_CaptionText.text = data != null && data.text != null ? data.text : string.Empty;
                m_CaptionText.gameObject.SetActive(true);
            }
            if (m_CaptionImage != null)
            {
                m_CaptionImage.sprite = data?.image;
                if (data != null) m_CaptionImage.color = data.color;
                m_CaptionImage.enabled = m_CaptionImage.sprite != null;
            }
        }

        public void AddOptions(List<OptionData> newOptions)
        {
            options.AddRange(newOptions);
            Refresh();
        }

        public void AddOptions(List<string> newOptions)
        {
            foreach (string option in newOptions) options.Add(new OptionData(option));
            Refresh();
        }

        public void AddOptions(List<Sprite> newOptions)
        {
            foreach (var option in newOptions) options.Add(new OptionData(option));
            Refresh();
        }

        public void ClearOptions()
        {
            options.Clear();
            m_Value = 0;
            Refresh();
        }

        // ------------------------------------------------------------- opening

        public void OnPointerClick(PointerEventData eventData) => Show();

        public void OnSubmit(BaseEventData eventData) => Show();

        public void OnCancel(BaseEventData eventData) => Hide();

        /// <summary>
        /// Opens the list.
        ///
        /// The template is a child of this dropdown, inactive, and is used as
        /// the pattern for the open list: it is duplicated, the item inside it
        /// is duplicated once per option, and the whole thing is destroyed on
        /// close. That is Unity's design and it is kept because the prefabs a
        /// migration converts are built against it — a dropdown prefab from any
        /// project already has the template child in exactly this shape.
        /// </summary>
        public void Show()
        {
            if (!IsActive() || !IsInteractable() || m_Dropdown != null) return;
            if (m_Template == null)
            {
                Debug.LogError("OneTextDropdown has no Template set", this);
                return;
            }
            if (!m_Validated && !Validate())
            {
                Debug.LogError("OneTextDropdown's Template is not set up correctly: it needs a " +
                               "child holding the item, with the item's label and image under it.",
                    this);
                return;
            }

            var root = Canvas();
            if (root == null) return;

            m_Template.gameObject.SetActive(true);

            m_Dropdown = UnityEngine.Object.Instantiate(m_Template.gameObject, m_Template.parent);
            m_Dropdown.name = "Dropdown List";
            m_Dropdown.SetActive(true);

            var dropdownRect = m_Dropdown.transform as RectTransform;
            Lift(m_Dropdown, root);
            var itemTemplate = ItemIn(m_Dropdown);
            if (itemTemplate == null)
            {
                Close(deferred: false);
                return;
            }

            m_Items.Clear();
            var content = itemTemplate.rectTransform.parent as RectTransform;
            if (content == null)
            {
                // The rows go wherever the item's parent is, and they are laid
                // out in its rect. A parent without one is a template that was
                // never going to work; refusing beats a null reference from
                // inside the arithmetic.
                Debug.LogError("OneTextDropdown's item is not under a RectTransform, so there is " +
                               "nothing to lay its rows out in.", this);
                Close(deferred: false);
                return;
            }
            itemTemplate.rectTransform.gameObject.SetActive(true);

            // Measured off the template's own item, and measured now, while that
            // item is still the only thing in the content and still sitting
            // where the template author put it. Everything the rows are laid out
            // with comes from these three: how tall a row is, and how much room
            // the author left between the item and the content's own edges,
            // which is the list's padding and is not written down anywhere else.
            var contentBox = content.rect;
            var itemBox = itemTemplate.rectTransform.rect;
            var itemAt = (Vector2)itemTemplate.rectTransform.localPosition;
            var padMin = itemBox.min - contentBox.min + itemAt;
            var padMax = itemBox.max - contentBox.max + itemAt;
            var itemSize = itemBox.size;

            // Put on the one item every row is copied from, so each copy carries
            // it without this having to remember to add it per row.
            if (itemTemplate.rectTransform.GetComponent<Row>() == null)
                itemTemplate.rectTransform.gameObject.AddComponent<Row>();

            Toggle previous = null;
            for (int i = 0; i < options.Count; i++)
            {
                var item = AddItem(options[i], i, itemTemplate, content);
                if (item == null) continue;
                item.toggle.isOn = i == m_Value;
                // Keyboard focus moves into the list, onto the option that is
                // already chosen. Without this the explicit navigation wired
                // below is correct and unreachable: focus stays on the dropdown
                // button, whose own navigation is Automatic, so the first arrow
                // press after opening the list goes to whatever else on the
                // screen happens to lie in that direction. Select is Unity's
                // own, and already declines when there is no EventSystem and
                // when one is mid-selection.
                if (item.toggle.isOn) item.toggle.Select();
                int index = i;
                item.toggle.onValueChanged.AddListener(on => { if (on) Select(index); });
                Walk(previous, item.toggle);
                previous = item.toggle;
                m_Items.Add(item);
            }

            // The template's own item is the pattern, not a row.
            itemTemplate.rectTransform.gameObject.SetActive(false);

            Lay(dropdownRect, content, root, padMin, padMax, itemSize);

            m_Blocker = Blocker(root, dropdownRect);
            Transient(m_Dropdown);
            Transient(m_Blocker);
            m_Template.gameObject.SetActive(false);
            Select(m_Value, notify: false);
        }

        /// <summary>
        /// Closes the list, if it is open, and takes its focus back.
        ///
        /// The focus half is not tidiness. Opening the list moves keyboard focus
        /// onto a row, and closing destroys that row — so without this the
        /// EventSystem is left holding an object that no longer exists, and the
        /// next arrow press has nowhere to go from. Every close does it: picking
        /// an option, clicking outside, cancelling. It is also where somebody who
        /// opened the list with the keyboard expects to be standing afterwards,
        /// which is the same place they started.
        /// </summary>
        public void Hide()
        {
            if (m_Dropdown == null) return;
            Close(deferred: false);
            base.Select();
        }

        private void Select(int index, bool notify = true)
        {
            for (int i = 0; i < m_Items.Count; i++)
                m_Items[i].toggle.SetIsOnWithoutNotify(i == index);

            if (!notify)
                return;

            SetValue(index, true);
            Hide();
        }

        private Item AddItem(OptionData data, int index, Item template, RectTransform content)
        {
            var copy = UnityEngine.Object.Instantiate(template.rectTransform.gameObject, content);
            copy.name = "Item " + index + (data.text != null ? ": " + data.text : string.Empty);
            copy.SetActive(true);

            var item = ItemOn(copy);
            if (item == null) return null;

            if (item.text != null) item.text.text = data.text ?? string.Empty;
            if (item.image != null)
            {
                item.image.sprite = data.image;
                item.image.color = data.color;
                item.image.enabled = item.image.sprite != null;
            }
            return item;
        }

        /// <summary>
        /// Where the rows go, which is a thing this had left to nobody.
        ///
        /// <see cref="AddItem"/> copies a row into the content and never touches
        /// its rect, so every row kept the template item's position and all of
        /// them landed on top of each other: an open list that reads as one row
        /// no matter how many options it holds. That is what a real project
        /// reported, and the reason it is not the template's fault is that a
        /// converted prefab cannot have a layout group on its content — Unity's
        /// dropdown does this arithmetic in code, so a prefab built against
        /// Unity's dropdown has nothing on the content at all. Doing it here is
        /// the only fix that reaches a migrated dropdown, and doing it here for
        /// the authored one too means one code path rather than two.
        ///
        /// The order is Unity's, and each step depends on the one before it: the
        /// content is sized to its rows, a list taller than its content is
        /// shrunk down onto it, a list that would fall off the canvas is flipped
        /// to the other side of the button, and only then are the rows placed —
        /// bottom-anchored and counted from the end, so the first option is the
        /// top one.
        /// </summary>
        private void Lay(RectTransform list, RectTransform content, Canvas root,
            Vector2 padMin, Vector2 padMax, Vector2 itemSize)
        {
            var size = content.sizeDelta;
            size.y = itemSize.y * m_Items.Count + padMin.y - padMax.y;
            content.sizeDelta = size;

            float slack = list.rect.height - content.rect.height;
            if (slack > 0f) list.sizeDelta = new Vector2(list.sizeDelta.x, list.sizeDelta.y - slack);

            Flip(list, root);

            for (int i = 0; i < m_Items.Count; i++)
            {
                var rect = m_Items[i].rectTransform;
                rect.anchorMin = new Vector2(rect.anchorMin.x, 0f);
                rect.anchorMax = new Vector2(rect.anchorMax.x, 0f);
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x,
                    padMin.y + itemSize.y * (m_Items.Count - 1 - i) + itemSize.y * rect.pivot.y);
                rect.sizeDelta = new Vector2(rect.sizeDelta.x, itemSize.y);
            }
        }

        /// <summary>
        /// A list that hangs off the edge of the canvas, turned over onto the
        /// other side of the button. This is what makes a dropdown near the
        /// bottom of the screen open upward, and it is written as an inversion
        /// of whatever the template was anchored to rather than as "open up"
        /// because a template anchored above the button needs the same thing in
        /// the other direction.
        /// </summary>
        private static void Flip(RectTransform list, Canvas root)
        {
            var canvasRect = root.transform as RectTransform;
            if (canvasRect == null) return;

            var corners = new Vector3[4];
            list.GetWorldCorners(corners);
            var bounds = canvasRect.rect;

            for (int axis = 0; axis < 2; axis++)
            {
                bool outside = false;
                for (int i = 0; i < 4; i++)
                {
                    var corner = canvasRect.InverseTransformPoint(corners[i]);
                    // Approximately, because a list sized exactly to the canvas
                    // is not outside it and must not flip back and forth.
                    if ((corner[axis] < bounds.min[axis] && !Mathf.Approximately(corner[axis], bounds.min[axis])) ||
                        (corner[axis] > bounds.max[axis] && !Mathf.Approximately(corner[axis], bounds.max[axis])))
                    {
                        outside = true;
                        break;
                    }
                }
                if (outside) RectTransformUtility.FlipLayoutOnAxis(list, axis, false, false);
            }
        }

        /// <summary>
        /// Explicit navigation from one row to the next, so the arrow keys walk
        /// the list. Rows arrive on Automatic, which navigates by geometry and
        /// has the whole screen to choose from — an open list drawn over the top
        /// of a screen full of other selectables sends a keypress anywhere at
        /// all. Left and up go back, right and down go on, which is what Unity's
        /// dropdown wires and therefore what a converted project's players have
        /// in their hands already.
        /// </summary>
        private static void Walk(Toggle previous, Toggle next)
        {
            if (previous == null) return;

            var back = previous.navigation;
            var on = next.navigation;
            back.mode = Navigation.Mode.Explicit;
            on.mode = Navigation.Mode.Explicit;
            back.selectOnDown = next;
            back.selectOnRight = next;
            on.selectOnLeft = previous;
            on.selectOnUp = previous;
            previous.navigation = back;
            next.navigation = on;
        }

        private bool Validate()
        {
            m_Validated = m_Template != null && ItemIn(m_Template.gameObject) != null;
            return m_Validated;
        }

        private Item ItemIn(GameObject root)
        {
            foreach (var toggle in root.GetComponentsInChildren<Toggle>(true))
            {
                if (toggle.transform == root.transform) continue;
                return ItemOn(toggle.gameObject);
            }
            return null;
        }

        private Item ItemOn(GameObject go)
        {
            var toggle = go.GetComponent<Toggle>();
            if (toggle == null) return null;

            return new Item
            {
                toggle = toggle,
                rectTransform = go.transform as RectTransform,
                text = go.GetComponentInChildren<OneTextLabel>(true),
                image = Picture(go),
            };
        }

        /// <summary>
        /// The item's picture, which is not the toggle's own graphics. A
        /// dropdown row has a background and a checkmark before it has a
        /// picture, and both are Images on the toggle itself.
        /// </summary>
        private static Image Picture(GameObject go)
        {
            var toggle = go.GetComponent<Toggle>();
            foreach (var image in go.GetComponentsInChildren<Image>(true))
            {
                if (toggle != null && (image == toggle.targetGraphic || image == toggle.graphic))
                    continue;
                if (image.transform == go.transform) continue;
                return image;
            }
            return null;
        }

        /// <summary>
        /// The canvas an open list belongs to, which is the outermost one and
        /// not the nearest one.
        ///
        /// This used to answer with the nearest, and the difference is the
        /// pattern where a screen puts a canvas of its own on a panel so that
        /// panel batches separately. Against the nearest canvas the list is
        /// measured for fitting inside that panel rather than inside the screen,
        /// so a list with room below it flips upward because the panel ends —
        /// and the blocker, which goes under this canvas too, only covers the
        /// panel, leaving every click outside it unable to close the list.
        ///
        /// A nearer canvas is taken only when it says it stands on its own, by
        /// being a root canvas or by overriding sorting, which is Unity's rule
        /// and the reason its own dropdown reads the last element of the list
        /// rather than the first.
        /// </summary>
        private Canvas Canvas()
        {
            var canvases = GetComponentsInParent<Canvas>(false);
            if (canvases == null || canvases.Length == 0) return null;

            for (int i = 0; i < canvases.Length; i++)
                if (canvases[i].isRootCanvas || canvases[i].overrideSorting) return canvases[i];

            return canvases[canvases.Length - 1];
        }

        /// <summary>
        /// The open list on its own canvas, far above everything, the way
        /// Unity's dropdown does it — and the reason is the blocker rather than
        /// the list. The blocker asks the list what order it is at and sits one
        /// under it; with no canvas on the list to ask, that question answered
        /// null and the blocker fell back to order 0, which is where the list
        /// was too. A full-screen transparent graphic tied with the rows it was
        /// meant to sit behind, and which of the two won a click came down to
        /// the order the raycasters happened to run in. So the list is lifted
        /// here, before <see cref="Blocker"/> is asked to go under it.
        ///
        /// A template that already carries a canvas is left as the project
        /// authored it: Unity skips its own for the same reason, because
        /// overriding one that was deliberately set changes the sorting layer
        /// of everything under it.
        /// </summary>
        private static void Lift(GameObject dropdown, Canvas root)
        {
            if (dropdown.GetComponent<Canvas>() == null)
            {
                var canvas = dropdown.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = ListSortingOrder;
                canvas.sortingLayerID = root.sortingLayerID;
            }

            Raycasters(dropdown, dropdown.transform.parent);
        }

        /// <summary>
        /// The raycasters something drawn over the dropdown needs, which are
        /// whatever kinds are already reading the canvas it is drawn over.
        ///
        /// A canvas of its own makes the list a raycast root of its own, and a
        /// raycast root without a raycaster draws on top and answers nothing.
        /// Which raycaster is not a question with one answer, though: a
        /// screen-space canvas is read by a GraphicRaycaster, and a world-space
        /// one on a headset by whatever that platform ships — a tracked-device
        /// raycaster, an OVR one — so a list handed a GraphicRaycaster there is
        /// a list nothing can point at. It is given the kinds its own canvas is
        /// using instead, whatever they are, and only something with no canvas
        /// above it at all falls back to the ordinary one.
        ///
        /// A canvas with no raycasters hands on none, and that is deliberate: on
        /// such a canvas nothing else is clickable either, and a list that
        /// answered clicks its own screen cannot would be the odd one out.
        /// </summary>
        private static void Raycasters(GameObject target, Transform under)
        {
            var canvas = Above(under);
            if (canvas == null)
            {
                if (target.GetComponent<GraphicRaycaster>() == null)
                    target.AddComponent<GraphicRaycaster>();
                return;
            }

            foreach (var raycaster in canvas.GetComponents<BaseRaycaster>())
            {
                var kind = raycaster.GetType();
                if (target.GetComponent(kind) == null) target.AddComponent(kind);
            }
        }

        /// <summary>
        /// The nearest canvas at or above a transform. Deliberately the nearest
        /// one and not <see cref="Canvas"/>'s outermost: a panel with a canvas
        /// of its own is also where that panel's raycasters are, and those are
        /// the ones anything drawn over that panel has to match.
        /// </summary>
        private static Canvas Above(Transform transform)
        {
            for (var at = transform; at != null; at = at.parent)
            {
                var canvas = at.GetComponent<Canvas>();
                if (canvas != null) return canvas;
            }
            return null;
        }

        /// <summary>
        /// A transparent full-screen graphic behind the open list, so a click
        /// anywhere else closes it. Sorted just under the list and just over
        /// everything else, which is what stops it swallowing the list's own
        /// clicks — and <see cref="Lift"/> runs first so that "just under" has
        /// a number to be one less than.
        ///
        /// Its raycaster is chosen the same way the list's is, and for the same
        /// reason: a blocker read by a kind of raycaster the screen it covers
        /// does not use is a blocker that never receives the click it exists to
        /// catch, and the list would then only ever close by choosing something.
        /// </summary>
        private GameObject Blocker(Canvas root, RectTransform dropdown)
        {
            var blocker = new GameObject("Blocker", typeof(RectTransform), typeof(Canvas),
                typeof(Image));
            var rect = blocker.GetComponent<RectTransform>();
            rect.SetParent(root.transform, false);
            rect.anchorMin = Vector3.zero;
            rect.anchorMax = Vector3.one;
            rect.sizeDelta = Vector2.zero;

            var canvas = blocker.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            var dropdownCanvas = dropdown.GetComponent<Canvas>();
            canvas.sortingLayerID = dropdownCanvas != null ? dropdownCanvas.sortingLayerID : 0;
            canvas.sortingOrder = dropdownCanvas != null ? dropdownCanvas.sortingOrder - 1 : 0;

            var image = blocker.GetComponent<Image>();
            image.color = Color.clear;

            // Asked of the dropdown's own canvas rather than of the root this is
            // parented to: the two are the same screen, and the first is where a
            // panel that brought its own raycasters keeps them.
            Raycasters(blocker, m_Template.parent);

            var button = blocker.AddComponent<Button>();
            button.onClick.AddListener(Hide);
            return blocker;
        }

        /// <summary>
        /// An open list is not part of the scene, and the editor is told so.
        ///
        /// It is made when the list opens and destroyed when it closes, so
        /// somebody who saves with one open would otherwise write a "Dropdown
        /// List" and a full-screen "Blocker" into the scene file for good. That
        /// is what this flag guarantees and the whole of what it guarantees.
        ///
        /// It does not close the hole in <see cref="Discard"/>, and an earlier
        /// version of this comment claimed that it did. A domain reload landing
        /// inside the one tick between queueing the deferred destroy and its
        /// callback drops the callback, and this flag also means "not destroyed
        /// on scene load" — so rather than being discarded, the two objects may
        /// be carried across, including into play mode if that is what the
        /// reload was for, where nothing on the other side has a reference to
        /// either. What is left of the hole needs a list open in edit mode at
        /// the instant of the reload, which nothing in this package does on its
        /// own, and it is written down here rather than guarded against because
        /// every guard for it costs more than it is worth.
        ///
        /// Editor-only, and that is not laziness. At runtime this same flag also
        /// means "do not destroy on scene load", which would turn a list left
        /// open across a level change into a blocker nothing can ever remove.
        /// </summary>
        private static void Transient(GameObject go)
        {
#if UNITY_EDITOR
            if (go == null || Application.isPlaying) return;
            // Every object under it as well, and not only the one at the top: a
            // flag is a property of one object, and half a list written into a
            // scene would be worse than all of it.
            foreach (var child in go.GetComponentsInChildren<Transform>(true))
                child.gameObject.hideFlags = HideFlags.DontSave;
#endif
        }

        /// <summary>
        /// Closes the list and forgets it.
        ///
        /// The two references are dropped before either object is, so that
        /// whatever happens to the objects afterwards there is no corpse for a
        /// later <see cref="Show"/> to mistake for an open list. Everything that
        /// closes a dropdown comes through here.
        /// </summary>
        private void Close(bool deferred)
        {
            var list = m_Dropdown;
            var blocker = m_Blocker;
            m_Dropdown = null;
            m_Blocker = null;
            m_Items.Clear();

            Discard(list, deferred);
            Discard(blocker, deferred);

            if (m_Template != null) m_Template.gameObject.SetActive(false);
        }

        /// <summary>
        /// Destroying a list, from a caller that may or may not be allowed to do
        /// it now.
        ///
        /// Unity refuses to destroy a GameObject whose parent is in the middle
        /// of being activated or deactivated, and logs an error saying so. That
        /// is exactly the position <see cref="OnDisable"/> is in every time
        /// something above the dropdown is switched off or thrown away — a panel
        /// closing, a pooled screen going back in the pool, a scene unloading —
        /// and what was left behind was the open list and, worse, a full-screen
        /// invisible blocker still eating every click on the canvas.
        ///
        /// Unity's own dropdown never meets this, and not by being careful: it
        /// only ever calls <c>Destroy</c>, which waits for the end of the frame
        /// and is therefore legal in the middle of a deactivation. This one
        /// cannot only call Destroy, because it also runs in the editor, where
        /// there is no frame to wait for. So the editor waits for the next
        /// editor tick instead, which is the same trade in the same shape —
        /// and which tick it waits on turned out to matter, for the reason
        /// written where it is chosen.
        ///
        /// Deferred only when the caller says so. Closing on a click, or backing
        /// out of a <see cref="Show"/> that could not find its item, are both
        /// callers standing outside any activation, and an immediate destroy
        /// there keeps the editor's hierarchy honest with what it is showing.
        /// </summary>
        private static void Discard(GameObject go, bool deferred)
        {
            if (go == null) return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (!deferred)
                {
                    DestroyImmediate(go);
                    return;
                }
                // Queued onto the editor's update loop, and not onto its delayed
                // call queue, which is the obvious place and was the first
                // answer here. A test that destroyed a screen with a list open
                // and then waited three editor ticks found the blocker still
                // standing: delayCall had not run once. Whatever drains it, a
                // batch run doing nothing but running tests does not reach it,
                // and an editor that only sometimes drains a queue is an editor
                // that only sometimes cleans this up.
                //
                // The update loop is the one thing known to be running whenever
                // the editor is running at all — it is what advances an EditMode
                // test between its own frames, so the same ticks that let a test
                // look for the blocker are the ticks that remove it. Deferring
                // is still the point: the callback lands after the deactivation
                // that queued it has finished unwinding, which is the only
                // moment a destroy underneath it is legal.
                //
                // It unsubscribes itself first, so a queued destroy costs one
                // tick and not a permanent listener. By the time it runs the
                // object is often already gone, taken by the same hierarchy that
                // was being torn down when it was queued: the ordinary case, not
                // a failure.
                UnityEditor.EditorApplication.CallbackFunction destroy = null;
                destroy = () =>
                {
                    UnityEditor.EditorApplication.update -= destroy;
                    if (go != null) DestroyImmediate(go);
                };
                UnityEditor.EditorApplication.update += destroy;
                return;
            }
#endif
            Destroy(go);
        }

        /// <summary>
        /// A row of the open list. A class rather than a struct because the
        /// code that builds one has to be able to say it found nothing.
        /// </summary>
        private sealed class Item
        {
            public Toggle toggle;
            public RectTransform rectTransform;
            public OneTextLabel text;
            public Image image;
        }

        /// <summary>
        /// What a row has to answer for itself, which <see cref="Item"/> cannot:
        /// Item is what this class knows about a row, and this is what the
        /// EventSystem knows about it.
        ///
        /// Cancel is the reason it exists. The input module sends cancel to the
        /// selected object and to nowhere else, and once the list is open the
        /// selected object is a row — so the dropdown's own ICancelHandler, up
        /// on the button, is never asked, and Escape and gamepad B over an open
        /// list did nothing at all. The dropdown is found by walking up rather
        /// than held in a field because the row is a copy of a copy and a field
        /// would have to be re-pointed on every one.
        ///
        /// Pointer-enter comes along with it, and Unity's row has it for the
        /// same reason: with focus inside the list, a mouse moving over the rows
        /// and a keyboard walking them have to mean the same thing, or hovering
        /// one row and pressing Escape cancels from another.
        /// </summary>
        private sealed class Row : MonoBehaviour, IPointerEnterHandler, ICancelHandler
        {
            public void OnPointerEnter(PointerEventData eventData)
            {
                var events = EventSystem.current;
                if (events == null || events.alreadySelecting) return;
                events.SetSelectedGameObject(gameObject);
            }

            public void OnCancel(BaseEventData eventData)
            {
                var dropdown = GetComponentInParent<OneTextDropdown>();
                if (dropdown != null) dropdown.Hide();
            }
        }
    }
}
