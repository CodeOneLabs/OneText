using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// Draws the caret bar and selection highlight for
    /// <see cref="OneTextInputField"/>. It is a plain untextured graphic that
    /// shares the text label's rect, so the rectangles it receives are already
    /// in the right space.
    /// </summary>
    [AddComponentMenu("")] // created by the input field, not added by hand
    public sealed class OneTextCaret : MaskableGraphic
    {
        [SerializeField] private Color _selectionColor = new Color(0.24f, 0.50f, 0.87f, 0.55f);
        [SerializeField] private Color _compositionColor = new Color(1f, 1f, 1f, 0.7f);
        [SerializeField] private Color _clauseColor = new Color(0.24f, 0.50f, 0.87f, 0.35f);

        private readonly List<Rect> _selection = new List<Rect>();
        private readonly List<Rect> _composition = new List<Rect>();
        private readonly List<Rect> _clause = new List<Rect>();
        private Rect _caret;
        private bool _caretVisible;

        public Color SelectionColor
        {
            get => _selectionColor;
            set { _selectionColor = value; SetVerticesDirty(); }
        }

        /// <summary>Colour of the underline drawn beneath uncommitted IME text.</summary>
        public Color CompositionColor
        {
            get => _compositionColor;
            set { _compositionColor = value; SetVerticesDirty(); }
        }

        /// <summary>Colour of the block behind the clause a Japanese IME is converting.</summary>
        public Color ClauseColor
        {
            get => _clauseColor;
            set { _clauseColor = value; SetVerticesDirty(); }
        }

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        /// <summary>Replaces the drawn geometry; all rects are in local space.</summary>
        public void SetGeometry(Rect caret, bool caretVisible, List<Rect> selection)
        {
            _caret = caret;
            _caretVisible = caretVisible;
            _selection.Clear();
            if (selection != null) _selection.AddRange(selection);
            SetVerticesDirty();
        }

        /// <summary>
        /// Marks the range an input method is still composing: an underline
        /// under all of it, and a block behind the clause being converted. The
        /// rects are the selection rects of those ranges; the caller squashes
        /// them to underline height, because only it knows the text size.
        /// </summary>
        public void SetComposition(List<Rect> underlines, List<Rect> clause)
        {
            _composition.Clear();
            _clause.Clear();
            if (underlines != null) _composition.AddRange(underlines);
            if (clause != null) _clause.AddRange(clause);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            foreach (var rect in _selection)
                AddQuad(vh, rect, _selectionColor);
            foreach (var rect in _clause)
                AddQuad(vh, rect, _clauseColor);
            foreach (var rect in _composition)
                AddQuad(vh, rect, _compositionColor);
            if (_caretVisible && _caret.height > 0f)
                AddQuad(vh, _caret, color);
        }

        private static void AddQuad(VertexHelper vh, Rect rect, Color32 tint)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(rect.xMin, rect.yMin), tint, Vector2.zero);
            vh.AddVert(new Vector3(rect.xMin, rect.yMax), tint, Vector2.zero);
            vh.AddVert(new Vector3(rect.xMax, rect.yMax), tint, Vector2.zero);
            vh.AddVert(new Vector3(rect.xMax, rect.yMin), tint, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
