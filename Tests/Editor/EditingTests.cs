using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// M12: text editing that survives an input method.
    ///
    /// The cases are the bug reports. Korean users have been filing the same
    /// three against Unity's own input field for a decade — the last syllable
    /// disappears when focus moves, backspace eats the text behind the
    /// composition, Enter submits the form while the user was only confirming a
    /// candidate — and the reason they survive is that nobody can write a
    /// regression test for them without a Korean IME attached to the machine.
    /// So the field's editing state is a plain object, and the IME is an
    /// interface. Both are driven here at compile speed.
    /// </summary>
    public class EditingTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        // "안녕" typed, then "하" being composed: ᄒ, 하, 한 — the states a Hangul
        // IME actually walks through as three keys are pressed.
        private const string Hangul_H = "ㅎ";      // ㅎ
        private const string Hangul_HA = "하";     // 하
        private const string Hangul_HAN = "한";    // 한

        // ---------------------------------------------------------- the model

        [Test]
        public void Composition_Is_Drawn_But_Is_Not_The_Value()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);

            model.SetComposition(Hangul_H);
            Assert.AreEqual("안녕", model.Text, "a composition is not text yet");
            Assert.AreEqual("안녕" + Hangul_H, model.DisplayText, "but it is drawn");
            Assert.AreEqual(3, model.DisplayCaret, "with the caret after it");

            model.SetComposition(Hangul_HA);
            Assert.AreEqual("안녕" + Hangul_HA, model.DisplayText,
                "the IME replaces its own composition rather than appending to it");

            model.SetComposition(Hangul_HAN);
            Assert.IsTrue(model.TryGetCompositionRange(out int start, out int end));
            Assert.AreEqual(2, start);
            Assert.AreEqual(3, end);
        }

        [Test]
        public void Focus_Loss_Mid_Composition_Keeps_The_Last_Syllable()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);

            Assert.IsTrue(model.CommitComposition(), "the value changed");
            Assert.AreEqual("안녕" + Hangul_HAN, model.Text);
            Assert.IsFalse(model.IsComposing);
            Assert.AreEqual(3, model.Caret);
        }

        [Test]
        public void The_Platforms_Echo_Of_A_Forced_Commit_Is_Dropped()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);
            model.CommitComposition();

            // Windows delivers the composition a second time, as an ordinary
            // character event, once the IME finishes. Applying it would double
            // the syllable.
            Assert.IsFalse(model.AcceptCharacter(Hangul_HAN[0], out bool changed),
                "the echo is recognised and refused");
            Assert.IsFalse(changed);
            Assert.AreEqual("안녕" + Hangul_HAN, model.Text);

            // And the guard is spent: the next identical keystroke is the user.
            Assert.IsTrue(model.AcceptCharacter(Hangul_HAN[0], out changed));
            Assert.AreEqual("안녕" + Hangul_HAN + Hangul_HAN, model.Text);
        }

        [Test]
        public void A_Commit_The_Platform_Never_Sends_Is_Made_By_The_Field()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);

            model.SetComposition(string.Empty); // the IME let go
            Assert.IsFalse(model.IsComposing);
            Assert.AreEqual("안녕", model.Text, "nothing is inserted while we wait to see");

            bool inserted = false;
            for (int update = 0; update < ImeCommitArbiter.DefaultGraceUpdates; update++)
                inserted |= model.Tick();

            Assert.IsTrue(inserted, "the grace window closed with nothing delivered");
            Assert.AreEqual("안녕" + Hangul_HAN, model.Text);
        }

        [Test]
        public void A_Commit_The_Platform_Does_Send_Is_Not_Made_Twice()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);
            model.SetComposition(string.Empty);

            Assert.IsTrue(model.AcceptCharacter(Hangul_HAN[0], out bool changed));
            Assert.IsTrue(changed);
            Assert.AreEqual("안녕" + Hangul_HAN, model.Text);

            for (int update = 0; update < 4; update++)
                Assert.IsFalse(model.Tick(), "the platform already delivered; we owe nothing");
            Assert.AreEqual("안녕" + Hangul_HAN, model.Text);
        }

        [Test]
        public void A_Cancelled_Composition_Commits_Nothing()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);

            model.CancelComposition();
            for (int update = 0; update < 4; update++) model.Tick();

            Assert.AreEqual("안녕", model.Text, "escape means escape");
            Assert.IsFalse(model.IsComposing);
        }

        [Test]
        public void Composing_Over_A_Selection_Replaces_It()
        {
            var model = new TextEditingModel { Text = "hello world" };
            model.SetSelection(6, 11); // "world"

            Assert.IsTrue(model.SetComposition(Hangul_HA), "replacing the selection changed the value");
            Assert.AreEqual("hello ", model.Text);
            Assert.AreEqual("hello " + Hangul_HA, model.DisplayText);

            model.CommitComposition();
            Assert.AreEqual("hello " + Hangul_HA, model.Text);
        }

        [Test]
        public void Read_Only_Refuses_Composition()
        {
            var model = new TextEditingModel { Text = "안녕", ReadOnly = true };
            model.SetCaret(2, false);

            model.SetComposition(Hangul_HAN);
            Assert.IsFalse(model.IsComposing, "a read-only field never shows text it will not accept");
            Assert.AreEqual("안녕", model.DisplayText);

            model.CommitComposition();
            Assert.AreEqual("안녕", model.Text);
        }

        [Test]
        public void A_Composition_Never_Splits_A_Surrogate_Pair()
        {
            // A pinyin IME composing a character outside the BMP, with a caret
            // and a clause the platform reports in its own units. Landing an
            // index between the two halves is how a candidate list crashes a
            // field that indexes strings by code unit and trusts the number.
            const string astral = "\U00020BB7\U0001F600"; // 𠮷 then an emoji
            var model = new TextEditingModel { Text = "中" };
            model.SetCaret(1, false);

            Assert.DoesNotThrow(() => model.SetComposition(astral, caretInComposition: 1,
                clauseStart: 3, clauseLength: 1));

            var composition = model.Composition;
            Assert.AreEqual(0, composition.Caret, "a caret inside a pair moves to its start");
            Assert.AreEqual(2, composition.ClauseStart);
            Assert.AreEqual(0, composition.ClauseLength);
            Assert.AreEqual("中" + astral, model.DisplayText);

            Assert.DoesNotThrow(() => model.SetComposition(astral, caretInComposition: 999,
                clauseStart: -5, clauseLength: 999));
            Assert.AreEqual(astral.Length, model.Composition.Caret);
            Assert.AreEqual(astral.Length, model.Composition.ClauseLength);

            model.CommitComposition();
            Assert.AreEqual("中" + astral, model.Text);
        }

        [Test]
        public void A_Japanese_Clause_Is_Reported_As_A_Range_To_Underline()
        {
            // "きょうは" with the first two kana being converted.
            const string composing = "きょうは";
            var model = new TextEditingModel { Text = "> " };
            model.SetCaret(2, false);
            model.SetComposition(composing, caretInComposition: 2, clauseStart: 0, clauseLength: 2);

            Assert.IsTrue(model.TryGetCompositionRange(out int start, out int end));
            Assert.AreEqual(2, start);
            Assert.AreEqual(2 + composing.Length, end);

            Assert.IsTrue(model.TryGetClauseRange(out int clauseStart, out int clauseEnd));
            Assert.AreEqual(2, clauseStart);
            Assert.AreEqual(4, clauseEnd, "the clause is a sub-range of the composition");
            Assert.AreEqual(4, model.DisplayCaret, "and the caret sits where the IME put it");
        }

        [Test]
        public void Setting_The_Value_Drops_A_Live_Composition()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);
            model.SetComposition(Hangul_HAN);

            model.Text = "reset";
            Assert.IsFalse(model.IsComposing);
            Assert.AreEqual("reset", model.DisplayText);
            for (int update = 0; update < 4; update++) model.Tick();
            Assert.AreEqual("reset", model.Text, "the dropped composition does not come back");
        }

        [Test]
        public void A_Character_Limit_Truncates_A_Commit_Rather_Than_Losing_It()
        {
            var model = new TextEditingModel { Text = "12345", CharacterLimit = 8 };
            model.SetCaret(5, false);

            Assert.IsTrue(model.Insert("abcdef"), "a paste that is too long still pastes what fits");
            Assert.AreEqual("12345abc", model.Text);
            Assert.AreEqual(8, model.Caret);

            Assert.IsFalse(model.Insert("x"), "and then nothing fits");
        }

        [Test]
        public void The_Caret_And_Selection_Still_Move_By_Grapheme_And_Word()
        {
            var model = new TextEditingModel { Text = "é quick" }; // e + combining acute
            model.SetCaret(0, false);

            model.MoveHorizontally(1, byWord: false, extendSelection: false);
            Assert.AreEqual(2, model.Caret, "the combining mark travels with its base");

            model.MoveHorizontally(1, byWord: true, extendSelection: true);
            Assert.AreEqual(3, model.Caret);
            Assert.AreEqual(2, model.Anchor, "shift keeps the anchor");

            Assert.IsTrue(model.Backspace());
            Assert.AreEqual("équick", model.Text);
        }

        [Test]
        public void The_Soft_Keyboards_Buffer_Reports_One_Change_Per_Change()
        {
            var model = new TextEditingModel { Text = "hell" };

            Assert.IsTrue(model.SetExternalText("hello", 5, 0));
            Assert.AreEqual("hello", model.Text);
            Assert.AreEqual(5, model.Caret);

            Assert.IsFalse(model.SetExternalText("hello", 5, 0),
                "polling the same buffer again is not a change");

            model.SetExternalText("hello", 1, 3);
            Assert.AreEqual(1, model.Anchor);
            Assert.AreEqual(4, model.Caret);
        }

        // -------------------------------------------------------- the arbiter

        [Test]
        public void The_Arbiter_Waits_Exactly_One_Grace_Window()
        {
            var arbiter = new ImeCommitArbiter();
            arbiter.AwaitPlatformCommit("한");
            Assert.IsTrue(arbiter.IsAwaitingPlatform);

            for (int update = 1; update < ImeCommitArbiter.DefaultGraceUpdates; update++)
                Assert.IsNull(arbiter.Tick(), "still waiting");

            Assert.AreEqual("한", arbiter.Tick());
            Assert.IsTrue(arbiter.IsIdle);
            Assert.IsNull(arbiter.Tick(), "and it only fires once");
        }

        [Test]
        public void The_Arbiter_Swallows_An_Echo_Character_By_Character()
        {
            var arbiter = new ImeCommitArbiter();
            arbiter.SuppressEchoOf("ab");

            Assert.IsTrue(arbiter.ShouldSwallow('a'));
            Assert.IsTrue(arbiter.ShouldSwallow('b'));
            Assert.IsFalse(arbiter.ShouldSwallow('c'), "the echo is over; this is the user typing");
            Assert.IsTrue(arbiter.IsIdle);
        }

        [Test]
        public void An_Echo_That_Does_Not_Match_Stops_Being_Swallowed()
        {
            var arbiter = new ImeCommitArbiter();
            arbiter.SuppressEchoOf("ab");

            Assert.IsFalse(arbiter.ShouldSwallow('z'), "not our text; the user got there first");
            Assert.IsFalse(arbiter.ShouldSwallow('a'), "and the guard does not resume");
        }

        // ---------------------------------------------------------- the field

        /// <summary>An input method we can type into, in place of the platform's.</summary>
        private sealed class FakeIme : IImeInput
        {
            public string Composition = string.Empty;
            public int Caret = -1;
            public int ClauseStart;
            public int ClauseLength;
            public bool Running;
            public Vector2 CursorPosition = new Vector2(float.NaN, float.NaN);

            public bool IsAvailable => true;

            public void Begin() => Running = true;

            public void End()
            {
                Running = false;
                Composition = string.Empty;
            }

            public void SetCursorScreenPosition(Vector2 screenPosition) => CursorPosition = screenPosition;

            public bool TryGetComposition(out string text, out int caret,
                out int clauseStart, out int clauseLength)
            {
                text = Composition;
                caret = Caret;
                clauseStart = ClauseStart;
                clauseLength = ClauseLength;
                return !string.IsNullOrEmpty(text);
            }
        }

        private FakeIme _ime;

        [SetUp]
        public void RegisterFakeIme()
        {
            _ime = new FakeIme();
            ImeInput.Register(() => _ime);
        }

        [TearDown]
        public void UnregisterFakeIme() => ImeInput.Unregister();

        private static OneTextInputField CreateField(out OneTextLabel label)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var root = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(canvasGo.transform, false);
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 60f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(root.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            label = textGo.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 24f;
            label.Wrap = TextWrap.NoWrap;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new UnityEditor.SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return field;
        }

        private static void Destroy(OneTextInputField field) =>
            Object.DestroyImmediate(field.transform.parent.gameObject);

        private static Event Key(KeyCode code, char character = '\0') =>
            new Event { type = EventType.KeyDown, keyCode = code, character = character };

        [Test]
        public void The_Field_Draws_The_Composition_Inline_And_Underlines_It()
        {
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.text = "hi ";
            field.caretPosition = 3;

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            field.UpdateVisuals();

            Assert.AreEqual("hi ", field.text, "the value is still what was committed");
            Assert.AreEqual("hi " + Hangul_HAN, label.Text, "the label shows what is being typed");
            Assert.IsTrue(field.isComposing);
            Assert.AreEqual(Hangul_HAN, field.compositionString);

            var rects = new List<Rect>();
            label.GetSelectionRects(3, 4, rects);
            Assert.AreEqual(1, rects.Count, "the composition occupies a run the caret graphic can underline");
            Assert.Greater(rects[0].width, 0f);

            Assert.IsFalse(float.IsNaN(_ime.CursorPosition.x),
                "the IME was told where to open its candidate window");

            Destroy(field);
        }

        [Test]
        public void The_Field_Leaves_The_Composition_Keys_To_The_Input_Method()
        {
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            bool submitted = false;
            field.onSubmit.AddListener(_ => submitted = true);

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();

            // The IME handles all three of these itself, and Unity delivers
            // them to us as well.
            field.ProcessKeyEvent(Key(KeyCode.Backspace));
            field.ProcessKeyEvent(Key(KeyCode.LeftArrow));
            field.ProcessKeyEvent(Key(KeyCode.Return, '\n'));

            Assert.AreEqual("안녕", field.text, "backspace shortened the composition, not the text");
            Assert.AreEqual(2, field.caretPosition);
            Assert.IsFalse(submitted, "Enter confirmed a candidate; it did not submit the form");
            Assert.IsTrue(field.isComposing);

            Destroy(field);
        }

        [Test]
        public void Deactivating_The_Field_Commits_What_Was_Being_Composed()
        {
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            string observed = null;
            field.onValueChanged.AddListener(value => observed = value);

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            Assert.AreEqual("안녕", field.text);

            field.DeactivateInputField();

            Assert.AreEqual("안녕" + Hangul_HAN, field.text, "the syllable survived losing focus");
            Assert.AreEqual("안녕" + Hangul_HAN, observed, "and the change was reported once");
            Assert.IsFalse(field.isComposing);
            Assert.IsFalse(_ime.Running, "the input method was released");

            field.UpdateVisuals();
            Assert.AreEqual("안녕" + Hangul_HAN, label.Text);

            Destroy(field);
        }

        [Test]
        public void Escape_Ends_A_Composition_Without_Ending_The_Field()
        {
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.Escape));

            Assert.IsFalse(field.isComposing);
            Assert.AreEqual("안녕", field.text, "an escaped composition is not committed");
            Assert.IsTrue(field.isFocused, "and the field keeps focus, as every OS text field does");

            Destroy(field);
        }

        [Test]
        public void A_Read_Only_Field_Never_Starts_An_Input_Method()
        {
            var field = CreateField(out _);
            field.text = "안녕";
            field.readOnly = true;
            field.ActivateInputField();

            Assert.IsFalse(_ime.Running, "nothing to compose into");

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            field.UpdateVisuals();

            Assert.IsFalse(field.isComposing);
            Assert.AreEqual("안녕", field.text);

            Destroy(field);
        }

        [Test]
        public void Typed_Angle_Brackets_Are_Text_And_Not_Markup()
        {
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.text = "<b>bold?</b>";
            field.UpdateVisuals();

            Assert.AreEqual("<b>bold?</b>", label.DisplayText,
                "an input field shows what was typed, tags included");

            Destroy(field);
        }
    }
}
