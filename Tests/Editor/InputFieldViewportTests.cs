using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The box the text has to stay inside.
    ///
    /// A field had none. Both labels were parented straight to the background
    /// image, and a value longer than the field drew out of its left edge and
    /// kept going across whatever was next to it — reported from a real project,
    /// with a screenshot. The scrolling half had always worked: the field moves
    /// the label's ScrollOffset to keep the caret in view. It is the clipping
    /// half that was never built, and clipping needs a layer to clip at, which
    /// is what Unity's InputField calls the Text Area and TextMesh Pro exposes
    /// as textViewport.
    ///
    /// What cannot be asserted here is the pixels. Clipping is done by the
    /// canvas renderer and the SDF shader between them, and a batch EditMode run
    /// draws nothing. So these assert the two things that decide whether the
    /// clipping happens at all — that the layer exists with a mask on it and
    /// both labels beneath it, and that the text overflows a box which does not
    /// grow to meet it — plus the thing the change could most easily have
    /// broken, which is every field authored before any of this existed.
    /// </summary>
    public sealed class InputFieldViewportTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private const string LongValue =
            "a value far longer than three hundred points of field will ever hold";

        private readonly HashSet<GameObject> _standing = new HashSet<GameObject>();
        private GameObject _canvas;

        [SetUp]
        public void RememberTheScene()
        {
            _standing.Clear();
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                _standing.Add(go);

            _canvas = new GameObject("Field Test Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }

        [TearDown]
        public void PutItBack()
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_standing.Contains(go)) Object.DestroyImmediate(go);
            _standing.Clear();
            _canvas = null;
        }

        private OneTextInputField Make()
        {
            OneTextMenuItems.CreateInputField(new MenuCommand(_canvas));
            var field = _canvas.GetComponentInChildren<OneTextInputField>(true);
            Assert.IsNotNull(field, "the menu entry put no input field under the canvas it was given");
            // A real font, because half of what is measured below is text
            // metrics, and an OS input method cannot be driven from a batch run.
            field.textComponent.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            field.inputMethodEnabled = false;
            return field;
        }

        /// <summary>
        /// A field the shape nearly every existing one is: both labels parented
        /// straight to the background, no viewport, no mask anywhere. Authored
        /// before the viewport existed, or arrived by having this component
        /// swapped onto somebody else's object.
        /// </summary>
        private OneTextInputField MakeBareField(bool withOwnTextArea = false)
        {
            var root = new GameObject("Old Field", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(_canvas.transform, false);
            ((RectTransform)root.transform).sizeDelta = new Vector2(300f, 60f);
            root.AddComponent<Image>();

            var host = root.transform;
            if (withOwnTextArea)
            {
                var area = new GameObject("Text Area", typeof(RectTransform));
                area.transform.SetParent(root.transform, false);
                Stretch((RectTransform)area.transform);
                area.AddComponent<RectMask2D>();
                host = area.transform;
            }

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(host, false);
            Stretch((RectTransform)textGo.transform);
            var label = textGo.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 28f;
            label.Wrap = TextWrap.NoWrap;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            field.inputMethodEnabled = false;
            return field;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// The field with no viewport — which is to say the field everybody
        /// already has — ends up clipped anyway.
        ///
        /// The mask is not looked for by hand: <c>MaskUtilities</c> is asked
        /// which RectMask2D uGUI itself would clip this label with, which is the
        /// same question the renderer asks. That matters here more than usual,
        /// because uGUI ignores a mask sitting on the graphic it would clip, so
        /// a mask in the wrong place would look right to any assertion that went
        /// looking for a component and be worth nothing.
        ///
        /// The limit of this test: it proves the clipping is set up and points
        /// at the right box. It cannot prove a pixel was cut. Clipping happens
        /// in the canvas renderer and the SDF shader between them, and a batch
        /// EditMode run draws nothing at all.
        /// </summary>
        [Test]
        public void A_Field_Nobody_Gave_A_Viewport_Still_Gets_Clipped()
        {
            var field = MakeBareField();
            field.text = LongValue;

            field.UpdateVisuals();

            var mask = MaskUtilities.GetRectMaskForClippable(field.textComponent);
            Assert.IsNotNull(mask, "nothing clips this label, so a long value draws out of the " +
                                   "field and across whatever is beside it — which is the report " +
                                   "this whole thing came from");
            Assert.AreEqual(field.gameObject, mask.gameObject,
                "the mask is somewhere other than the field, so what it cuts at is not the field's " +
                "own edge");
            Assert.AreEqual(HideFlags.DontSave, mask.hideFlags,
                "the mask would be written into the user's scene: nothing may be added to their " +
                "saved data for a fix they did not ask for");

            var box = (RectTransform)mask.transform;
            Assert.AreEqual(300f, box.rect.width, 0.01f,
                "the box the text is cut at is not the width of the field it belongs to");
            Assert.GreaterOrEqual(box.rect.width, field.textComponent.rectTransform.rect.width,
                "the mask is narrower than the label it clips, so it cuts text that was inside " +
                "the field to begin with");
            Assert.Greater(field.textComponent.preferredWidth, box.rect.width,
                "the value is not wider than the field, so nothing here needed clipping and the " +
                "test proves nothing");
        }

        /// <summary>
        /// The reason the field keeps the view rather than rewinding, read off
        /// a recording: 23 jamo typed, a click away, a click back at what looked
        /// like the end — and the caret landed on the twelfth character, because
        /// the twelfth is what was under that pixel once the field had rewound
        /// to the start. Everything typed next went into the middle of the
        /// string.
        /// </summary>
        [Test]
        public void A_Long_Value_Still_Shows_Its_End_After_Focus_Leaves()
        {
            var field = MakeBareField();
            field.text = LongValue;

            field.ActivateInputField();
            field.caretPosition = LongValue.Length;
            field.UpdateVisuals();

            float scrolled = field.textComponent.ScrollOffset.x;
            Assert.Greater(scrolled, 0f,
                "the field never scrolled to the caret, so this proves nothing");

            field.DeactivateInputField();
            field.UpdateVisuals();

            Assert.AreEqual(scrolled, field.textComponent.ScrollOffset.x, 0.01f,
                "the end of the value was on screen when focus left and has to still be there, " +
                "or clicking back in at the end lands in the middle");
        }

        /// <summary>
        /// And the one thing keeping it costs: an offset belongs to the string
        /// it was measured against, so a shorter value assigned from script has
        /// to bring the view back rather than draw from past its own end.
        /// </summary>
        [Test]
        public void A_Shorter_Value_Assigned_While_Unfocused_Comes_Back_Into_View()
        {
            var field = MakeBareField();
            field.text = LongValue;

            field.ActivateInputField();
            field.caretPosition = LongValue.Length;
            field.UpdateVisuals();
            Assert.Greater(field.textComponent.ScrollOffset.x, 0f);

            field.DeactivateInputField();
            field.text = "짧다";
            field.UpdateVisuals();

            Assert.AreEqual(0f, field.textComponent.ScrollOffset.x, 0.01f,
                "the field is still holding a window into the value it no longer has");
        }

        /// <summary>
        /// Both directions the report described, which are one cause seen twice:
        /// while the value is being typed the caret-follow scroll pushes it out
        /// of the left edge, and it is still out of it after focus leaves,
        /// because the field keeps the view the user left rather than rewinding
        /// to the start. The same mask is what has to be cutting in both states.
        /// </summary>
        [Test]
        public void The_Net_Is_There_While_Typing_And_After_Focus_Leaves()
        {
            var field = MakeBareField();
            field.text = LongValue;

            field.ActivateInputField();
            field.caretPosition = LongValue.Length;
            field.UpdateVisuals();

            Assert.Greater(field.textComponent.ScrollOffset.x, 0f,
                "the field never scrolled to the caret, so this is not the typing case");
            Assert.IsNotNull(MaskUtilities.GetRectMaskForClippable(field.textComponent),
                "nothing clips the text that the caret has just scrolled off the left edge");

            field.DeactivateInputField();
            field.UpdateVisuals();

            Assert.Greater(field.textComponent.ScrollOffset.x, 0f,
                "focus left and the field rewound to the start of the value, so a click back in " +
                "at the end of what was on screen would land in the middle of the string");
            Assert.IsNotNull(MaskUtilities.GetRectMaskForClippable(field.textComponent),
                "nothing clips the value still drawing off the left edge");
        }

        [Test]
        public void A_Field_Whose_Labels_Are_Already_Masked_Is_Left_Alone()
        {
            // The shape a converted TextMesh Pro field arrives in: its Text Area
            // and the mask on it survive the component swap, only the reference
            // is lost. Putting a second mask on the field would be clipping
            // something already clipped, at a wider box, for nothing.
            var field = MakeBareField(withOwnTextArea: true);
            field.text = LongValue;

            field.UpdateVisuals();

            Assert.IsNull(field.GetComponent<RectMask2D>(),
                "a mask was added to a field whose labels were already inside one");
            var mask = MaskUtilities.GetRectMaskForClippable(field.textComponent);
            Assert.IsNotNull(mask);
            Assert.AreEqual("Text Area", mask.gameObject.name,
                "the label is clipped by something other than the mask that was already there");
        }

        [Test]
        public void A_Field_With_An_Authored_Viewport_Is_Left_Alone()
        {
            var field = Make();

            field.text = LongValue;
            field.UpdateVisuals();

            Assert.IsNull(field.GetComponent<RectMask2D>(),
                "the field masked itself even though it has a viewport that already does it, " +
                "which clips everything under the field for no reason");
            var mask = MaskUtilities.GetRectMaskForClippable(field.textComponent);
            Assert.IsNotNull(mask);
            Assert.AreEqual(field.textViewport.gameObject, mask.gameObject,
                "the label is clipped by something other than its own viewport");
        }

        [Test]
        public void The_Menu_Makes_A_Masked_Viewport_With_Both_Labels_Under_It()
        {
            var field = Make();

            var viewport = field.textViewport;
            Assert.IsNotNull(viewport, "the field has no viewport, so nothing clips it");
            Assert.AreEqual(field.transform, viewport.parent,
                "the viewport is not between the field and its labels");
            Assert.IsNotNull(viewport.GetComponent<RectMask2D>(),
                "the viewport carries no mask, so it is a rectangle that clips nothing: the " +
                "reference is what the caret is kept inside, the mask is what cuts the text");

            Assert.AreEqual(viewport, field.textComponent.transform.parent,
                "the text label is not under the viewport, so the mask never reaches it");
            Assert.IsNotNull(field.placeholder, "no placeholder was wired");
            Assert.AreEqual(viewport, field.placeholder.transform.parent,
                "the placeholder is not under the viewport — a long placeholder would draw " +
                "outside the field even though the value no longer does");
        }

        /// <summary>
        /// The visible text box is where it was before the viewport existed. The
        /// padding moved from the two labels onto the layer above them, so a
        /// field created today has to measure the same as one created yesterday;
        /// anything else would restyle every screen the moment somebody made a
        /// new field.
        /// </summary>
        [Test]
        public void Interposing_The_Viewport_Did_Not_Move_The_Text()
        {
            var field = Make();
            var root = (RectTransform)field.transform;
            var viewport = field.textViewport;

            Assert.AreEqual(root.rect.width - 20f, viewport.rect.width, 0.01f,
                "the viewport is not the field inset by the padding the labels used to carry");
            Assert.AreEqual(root.rect.height - 20f, viewport.rect.height, 0.01f);

            var label = field.textComponent.rectTransform;
            Assert.AreEqual(viewport.rect.width, label.rect.width, 0.01f,
                "the label is not stretched to the viewport, so the text sits somewhere other " +
                "than where it used to");
            Assert.AreEqual(viewport.rect.height, label.rect.height, 0.01f);
        }

        [Test]
        public void A_Value_Longer_Than_The_Field_Does_Not_Widen_The_Box()
        {
            var field = Make();
            var viewport = field.textViewport;
            float boxBefore = viewport.rect.width;

            field.text = LongValue;
            field.UpdateVisuals();

            // The label's own rect is pinned to the viewport, so what runs past
            // the edge is the drawn text rather than the rect around it — which
            // is exactly the state a mask is for. Both numbers matter: text
            // wider than the box, and a box that did not quietly grow to fit it.
            Assert.Greater(field.textComponent.preferredWidth, viewport.rect.width,
                "the value is not actually wider than the field, so this test is not standing " +
                "where the report was");
            Assert.AreEqual(boxBefore, viewport.rect.width, 0.01f,
                "the viewport grew to fit the value, which is not clipping, it is a field that " +
                "changes size as you type");
            Assert.AreEqual(viewport.rect.width, field.textComponent.rectTransform.rect.width, 0.01f,
                "the label came off its viewport, so the mask and the text no longer agree on " +
                "where the edge is");
        }

        [Test]
        public void The_Caret_Is_Kept_Inside_The_Viewport()
        {
            var field = Make();
            var label = field.textComponent;

            field.text = LongValue;
            field.ActivateInputField();
            field.caretPosition = LongValue.Length;
            field.UpdateVisuals();

            Assert.Greater(label.ScrollOffset.x, 0f,
                "the field never scrolled to the end of a value it cannot fit, so the caret is " +
                "off the edge of the box");
            var caret = label.GetCaretRect(field.caretPosition, 2f);
            Assert.LessOrEqual(caret.xMax, label.rectTransform.rect.xMax + 0.5f,
                "the caret is outside the box the mask cuts at, so a user typing past the end of " +
                "the field is typing at a caret they cannot see");
        }

        /// <summary>
        /// The fields that already exist.
        ///
        /// None of them has a viewport and none of them can be given one without
        /// somebody rearranging their hierarchy, so an empty reference has to
        /// mean exactly what it meant before there was a reference at all. This
        /// asserts that by measuring the same field twice — once through its
        /// viewport and once with the reference taken away — because the two
        /// answers being equal is the whole of the promise.
        /// </summary>
        [Test]
        public void A_Field_With_No_Viewport_Scrolls_Exactly_As_It_Did_Before()
        {
            var field = Make();
            var label = field.textComponent;
            field.text = LongValue;
            field.ActivateInputField();
            field.caretPosition = LongValue.Length;

            label.ScrollOffset = Vector2.zero;
            field.UpdateVisuals();
            var scrolled = label.ScrollOffset;
            Assert.Greater(scrolled.x, 0f,
                "nothing scrolled, so the comparison below is between two zeroes and would hold " +
                "however the fallback behaved");

            field.textViewport = null;
            label.ScrollOffset = Vector2.zero;
            field.UpdateVisuals();

            Assert.AreEqual(scrolled.x, label.ScrollOffset.x, 0.01f,
                "a field whose viewport reference is empty scrolls differently from one that has " +
                "it, which means every field authored before the viewport existed changed " +
                "behaviour when this was added");
            Assert.AreEqual(scrolled.y, label.ScrollOffset.y, 0.01f);
        }
    }
}
