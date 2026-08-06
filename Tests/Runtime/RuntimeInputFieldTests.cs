using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// The input field with an EventSystem behind it and frames going past.
    ///
    /// EditMode can call every method on the field and read every property
    /// back; what it cannot do is put the field in a running scene where
    /// selection actually changes, where UpdateEditing runs once per frame off
    /// the EventSystem rather than because a test called it, and where the
    /// caret graphic is created, moved and destroyed by the component itself.
    /// Every field bug that ever shipped lived in that gap: a caret left
    /// behind after the field lost focus, a selection that survived a
    /// deactivate, text that only appeared on the frame after it was typed.
    ///
    /// Nothing here touches a platform IME. An OS composition window cannot be
    /// driven from a batch run and its behaviour is the platform's, not this
    /// package's; the composition rules have their own EditMode fixture with a
    /// fake IME behind them.
    /// </summary>
    public class RuntimeInputFieldTests
    {
        private readonly PlayHarness _harness = new PlayHarness();
        private EventSystem _events;

        [SetUp]
        public void Setup()
        {
            _harness.Setup();
            _events = _harness.EventSystem();
        }

        [TearDown]
        public void Teardown() => _harness.Teardown();

        private static Event Key(KeyCode code, char character = '\0',
            EventModifiers modifiers = EventModifiers.None) =>
            new Event
            {
                type = EventType.KeyDown,
                keyCode = code,
                character = character,
                modifiers = modifiers,
            };

        private static void TypeText(OneTextInputField field, string text)
        {
            foreach (char c in text)
                field.ProcessKeyEvent(Key(KeyCode.None, c));
        }

        [UnityTest]
        public IEnumerator Typing_Characters_Builds_The_Text_And_Drives_The_Label()
        {
            var field = _harness.InputField(string.Empty, out var label);
            yield return PlayHarness.Frame();

            var observed = new List<string>();
            field.onValueChanged.AddListener(observed.Add);

            field.ActivateInputField();
            yield return PlayHarness.Frame();
            Assert.IsTrue(field.isFocused, "the field did not take focus");
            Assert.AreSame(field.gameObject, _events.currentSelectedGameObject,
                "activating the field did not select it in the EventSystem");

            // A character a frame, which is what a keyboard actually delivers.
            const string typed = "Hello";
            foreach (char c in typed)
            {
                field.ProcessKeyEvent(Key(KeyCode.None, c));
                yield return PlayHarness.Frame();
            }

            Assert.AreEqual(typed, field.text);
            Assert.AreEqual(typed, label.Text, "the label did not follow the field");
            Assert.AreEqual(typed.Length, field.caretPosition);
            CollectionAssert.AreEqual(new[] { "H", "He", "Hel", "Hell", "Hello" }, observed,
                "onValueChanged did not report one value per keystroke");
            Assert.Greater(PlayHarness.DrawnQuads(label), 0);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Backspace_And_Delete_Walk_The_Text_Back()
        {
            var field = _harness.InputField("abcdef", out var label);
            field.ActivateInputField();
            field.caretPosition = 3;
            yield return PlayHarness.Frame();

            field.ProcessKeyEvent(Key(KeyCode.Backspace));
            yield return PlayHarness.Frame();
            Assert.AreEqual("abdef", field.text);
            Assert.AreEqual(2, field.caretPosition);

            field.ProcessKeyEvent(Key(KeyCode.Delete));
            yield return PlayHarness.Frame();
            Assert.AreEqual("abef", field.text);
            Assert.AreEqual(2, field.caretPosition);

            Assert.AreEqual("abef", label.Text);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Arrow_Keys_And_Shift_Build_A_Selection_The_Label_Can_Draw()
        {
            var field = _harness.InputField("hello world", out var label);
            field.ActivateInputField();
            field.caretPosition = 0;
            yield return PlayHarness.Frame();

            for (int i = 0; i < 5; i++)
            {
                field.ProcessKeyEvent(Key(KeyCode.RightArrow, modifiers: EventModifiers.Shift));
                yield return PlayHarness.Frame();
            }

            Assert.AreEqual(0, field.selectionAnchorPosition);
            Assert.AreEqual(5, field.caretPosition);

            var rects = new List<Rect>();
            label.GetSelectionRects(field.selectionAnchorPosition, field.caretPosition, rects);
            Assert.AreEqual(1, rects.Count, "a selection inside one line is one rectangle");
            Assert.Greater(rects[0].width, 0f);

            // Collapsing it is the other half: a caret move without shift must
            // drop the anchor where the caret lands.
            field.ProcessKeyEvent(Key(KeyCode.RightArrow));
            yield return PlayHarness.Frame();
            Assert.AreEqual(field.caretPosition, field.selectionAnchorPosition);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Select_All_Then_Type_Replaces_Everything()
        {
            var field = _harness.InputField("throw this away", out var label);
            field.ActivateInputField();
            yield return PlayHarness.Frame();

            field.SelectAll();
            yield return PlayHarness.Frame();
            Assert.AreEqual(0, field.selectionAnchorPosition);
            Assert.AreEqual("throw this away".Length, field.caretPosition);

            TypeText(field, "new");
            yield return PlayHarness.Frame();

            Assert.AreEqual("new", field.text);
            Assert.AreEqual("new", label.Text);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Focus_Lost_Through_The_EventSystem_Ends_The_Session()
        {
            var field = _harness.InputField("focused", out _);
            field.ActivateInputField();
            yield return PlayHarness.Frame();
            Assert.IsTrue(field.isFocused);

            // Deselecting through the EventSystem, not by calling
            // DeactivateInputField: that is the path a click elsewhere takes,
            // and the one where OnDeselect has to do the work.
            _events.SetSelectedGameObject(null);
            yield return PlayHarness.Frames(2);

            Assert.IsFalse(field.isFocused, "the field kept focus after being deselected");
            Assert.AreEqual("focused", field.text, "losing focus changed the text");

            field.ActivateInputField();
            yield return PlayHarness.Frame();
            Assert.IsTrue(field.isFocused, "the field could not be focused a second time");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Multiline_Enter_Inserts_A_Line_And_Single_Line_Submits()
        {
            var field = _harness.InputField("one", out _);
            field.multiline = true;
            field.ActivateInputField();
            field.caretPosition = 3;
            yield return PlayHarness.Frame();

            field.ProcessKeyEvent(Key(KeyCode.Return, '\n'));
            TypeText(field, "two");
            yield return PlayHarness.Frame();
            Assert.AreEqual("one\ntwo", field.text);

            field.multiline = false;
            string submitted = null;
            field.onSubmit.AddListener(value => submitted = value);
            field.ProcessKeyEvent(Key(KeyCode.Return, '\n'));
            yield return PlayHarness.Frame();

            Assert.AreEqual(field.text, submitted, "Enter on a single-line field did not submit");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Field_Destroyed_While_Focused_Takes_Its_Caret_With_It()
        {
            var field = _harness.InputField("about to go", out var label);
            field.ActivateInputField();
            yield return PlayHarness.Frames(2);

            var caret = label.GetComponentInChildren<OneTextCaret>(includeInactive: true);
            Assert.IsNotNull(caret, "a focused field never made a caret");

            Object.Destroy(field.gameObject);
            yield return PlayHarness.Frames(2);

            Assert.IsTrue(caret == null, "the caret outlived the field that owned it");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Hundred_Keystrokes_Across_Frames_Stay_Consistent()
        {
            var field = _harness.InputField(string.Empty, out var label);
            field.ActivateInputField();
            yield return PlayHarness.Frame();

            var expected = new System.Text.StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                char c = (char)('a' + i % 26);
                expected.Append(c);
                field.ProcessKeyEvent(Key(KeyCode.None, c));
                yield return PlayHarness.Frame();

                Assert.AreEqual(expected.Length, field.text.Length,
                    $"the field lost a keystroke at {i}");
                Assert.AreEqual(expected.Length, field.caretPosition,
                    $"the caret fell behind the text at {i}");
            }

            Assert.AreEqual(expected.ToString(), field.text);
            Assert.AreEqual(expected.ToString(), label.Text);
            Assert.Greater(PlayHarness.DrawnQuads(label), 0);
            PlayHarness.ExpectNoErrors();
        }
    }
}
