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
        [SerializeField] private float m_AlphaFadeSpeed = 0.15f;

        private GameObject m_Dropdown;
        private GameObject m_Blocker;
        private readonly List<Item> m_Items = new List<Item>();
        private bool m_Validated;

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
            ImmediatelyDestroyDropdown();
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
            var itemTemplate = ItemIn(m_Dropdown);
            if (itemTemplate == null)
            {
                ImmediatelyDestroyDropdown();
                return;
            }

            m_Items.Clear();
            var content = itemTemplate.rectTransform.parent as RectTransform;
            itemTemplate.rectTransform.gameObject.SetActive(true);

            for (int i = 0; i < options.Count; i++)
            {
                var item = AddItem(options[i], i, itemTemplate, content);
                if (item == null) continue;
                item.toggle.isOn = i == m_Value;
                int index = i;
                item.toggle.onValueChanged.AddListener(on => { if (on) Select(index); });
                m_Items.Add(item);
            }

            // The template's own item is the pattern, not a row.
            itemTemplate.rectTransform.gameObject.SetActive(false);

            m_Blocker = Blocker(root, dropdownRect);
            m_Template.gameObject.SetActive(false);
            Select(m_Value, notify: false);
        }

        /// <summary>Closes the list, if it is open.</summary>
        public void Hide()
        {
            if (m_Dropdown == null) return;
            ImmediatelyDestroyDropdown();
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

        private Canvas Canvas()
        {
            var canvases = GetComponentsInParent<Canvas>(false);
            return canvases != null && canvases.Length > 0 ? canvases[0] : null;
        }

        /// <summary>
        /// A transparent full-screen graphic behind the open list, so a click
        /// anywhere else closes it. Sorted just under the list and just over
        /// everything else, which is what stops it swallowing the list's own
        /// clicks.
        /// </summary>
        private GameObject Blocker(Canvas root, RectTransform dropdown)
        {
            var blocker = new GameObject("Blocker", typeof(RectTransform), typeof(Canvas),
                typeof(GraphicRaycaster), typeof(Image));
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

            var button = blocker.AddComponent<Button>();
            button.onClick.AddListener(Hide);
            return blocker;
        }

        private void ImmediatelyDestroyDropdown()
        {
            if (m_Dropdown != null) DestroyImmediateIfEditor(m_Dropdown);
            if (m_Blocker != null) DestroyImmediateIfEditor(m_Blocker);
            m_Dropdown = null;
            m_Blocker = null;
            m_Items.Clear();
            if (m_Template != null) m_Template.gameObject.SetActive(false);
        }

        private static void DestroyImmediateIfEditor(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
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
    }
}
