using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OneText.Tests
{
    /// <summary>
    /// M12: text editing that survives an input method.
    ///
    /// The cases are the bug reports. Korean users have been filing the same
    /// three against Unity's own input field for a decade (the last syllable
    /// disappears when focus moves, backspace eats the text behind the
    /// composition, Enter submits the form while the user was only confirming a
    /// candidate), and the reason they survive is that nobody can write a
    /// regression test for them without a Korean IME attached to the machine.
    /// So the field's editing state is a plain object, and the IME is an
    /// interface. Both are driven here at compile speed.
    /// </summary>
    public class EditingTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        // "안녕" typed, then "하" being composed: ᄒ, 하, 한 (the states a Hangul
        // IME actually walks through as three keys are pressed).
        private const string Hangul_H = "ㅎ";      // ㅎ
        private const string Hangul_HA = "하";     // 하
        private const string Hangul_HAN = "한";    // 한

        // The same syllable as Hangul_HAN, taken apart: the three conjoining
        // jamo it is built from. macOS hands Hangul over in this shape as
        // readily as in the other one, and the two are the same text and not
        // the same string — which is the whole of what these tests are about.
        //
        // Escapes rather than the characters themselves, deliberately: typed
        // out, this constant is indistinguishable from the one above in every
        // editor and every diff, and the first tool to normalize the file would
        // quietly turn this into a second copy of a test we already have.
        private const string Hangul_HAN_Jamo = "\u1112\u1161\u11AB";

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
        public void A_Composition_The_Field_Committed_Itself_Is_Not_Adopted_Back()
        {
            // The bug report: type Korean, stop, resume, and the last syllable
            // is there twice. Committing on the way out of focus is only half
            // of that commit — the platform was never told, so it goes on
            // reporting 국, and a field that believed it would draw the
            // syllable it had just committed as a fresh composition and commit
            // it again behind that.
            var model = new TextEditingModel { Text = "한" };
            model.SetCaret(1, false);
            model.SetComposition("국");
            model.CommitComposition();
            Assert.AreEqual("한국", model.Text);

            // Polled and ticked the way a focused field does it, for far longer
            // than any grace window: the platform is holding, not late.
            for (int update = 0; update < 8; update++)
            {
                Assert.IsFalse(model.SetComposition("국"), "nothing about the value changed");
                Assert.IsFalse(model.IsComposing, "the syllable came back as a new composition");
                Assert.AreEqual("한국", model.DisplayText);
                model.Tick();
            }

            // And the echo guard is still up when the platform finally does
            // send the character, which is the half of the duplicate that
            // arrives on the other channel.
            Assert.IsFalse(model.AcceptCharacter('국', out bool changed),
                "the platform's own commit was applied on top of ours");
            Assert.IsFalse(changed);
            Assert.AreEqual("한국", model.Text);
        }

        [Test]
        public void The_Platform_Moving_On_Retires_The_Refusal()
        {
            var model = new TextEditingModel { Text = "한" };
            model.SetCaret(1, false);
            model.SetComposition("국");
            model.CommitComposition();
            model.SetComposition("국"); // refused
            model.Tick();

            // The user types the next jamo. The IME finalises 국 and starts
            // composing ㅅ, and the field sees the new composition first
            // because it polls before it drains the key queue.
            Assert.IsFalse(model.SetComposition("ㅅ"));
            Assert.IsTrue(model.IsComposing, "a different composition is the user, not the platform repeating itself");
            Assert.AreEqual("ㅅ", model.Composition.Text);

            Assert.IsFalse(model.AcceptCharacter('국', out _),
                "the echo lands after the poll and is still recognised");
            Assert.AreEqual("한국", model.Text);
            Assert.AreEqual("한국" + "ㅅ", model.DisplayText);

            // The next 국 the user types for themselves is theirs.
            Assert.IsTrue(model.AcceptCharacter('국', out _));
            Assert.AreEqual("한국국", model.Text);
        }

        [Test]
        public void A_Syllable_Composed_As_Jamo_And_Committed_Whole_Is_One_Syllable()
        {
            // macOS is entitled to report the composition in one shape and hand
            // the same syllable back in another, and it does. Every guard in
            // this subsystem used to compare code units, so the two shapes of
            // 한 looked like different text to all of them at once — the replay
            // was adopted, the echo was typed, and the console showed two
            // characters that were spelled identically.
            var model = new TextEditingModel { Text = "안" };
            model.SetCaret(1, false);
            model.SetComposition(Hangul_HAN_Jamo);
            model.CommitComposition();
            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.Text);

            // Replayed in the other shape: still the syllable we just took.
            model.SetComposition(Hangul_HAN);
            Assert.IsFalse(model.IsComposing,
                "the same syllable in the other encoding was adopted as a new composition");

            // And echoed in the other shape: still the commit we already made.
            Assert.IsFalse(model.AcceptCharacter(Hangul_HAN[0], out bool changed),
                "the same syllable in the other encoding was typed a second time");
            Assert.IsFalse(changed);
            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.Text);
        }

        [Test]
        public void An_Echo_Delivered_One_Jamo_At_A_Time_Is_Still_One_Echo()
        {
            // The reverse, and the reason the echo cannot be matched by index:
            // three character events against a composition of one. There is no
            // position in "한" that U+1112 sits at.
            var model = new TextEditingModel { Text = "안" };
            model.SetCaret(1, false);
            model.SetComposition(Hangul_HAN);
            model.CommitComposition();
            Assert.AreEqual("안" + Hangul_HAN, model.Text);

            foreach (char jamo in Hangul_HAN_Jamo)
                Assert.IsFalse(model.AcceptCharacter(jamo, out _),
                    "every jamo of the echo belongs to the syllable already committed");

            Assert.AreEqual("안" + Hangul_HAN, model.Text, "the syllable was typed again, jamo by jamo");
            Assert.IsTrue(model.AcceptCharacter('x', out _), "and the guard is spent afterwards");
        }

        [Test]
        public void A_Syllable_Handed_Over_As_Jamo_Still_Pays_For_The_Composition()
        {
            // The third comparison, in NoteHandedOver: the platform composes
            // 한 as one character and hands it over as three. That is still
            // payment, and a composition that stays live after being paid is
            // drawn under the caret a second time and committed again behind it.
            var model = new TextEditingModel { Text = "안" };
            model.SetCaret(1, false);
            model.SetComposition(Hangul_HAN);

            foreach (char jamo in Hangul_HAN_Jamo)
                model.AcceptCharacter(jamo, out _);

            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.Text, "the jamo are the value now");
            Assert.IsFalse(model.IsComposing,
                "the composition was still owed after the platform had paid it in another shape");
            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.DisplayText, "so the syllable was drawn twice");
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
        public void Assigning_The_Value_Keeps_A_Selection_That_Still_Fits()
        {
            var model = new TextEditingModel { Text = "hello world" };
            model.SetSelection(2, 7);

            model.Text = "HELLO WORLD"; // same length, so both ends are still real
            Assert.AreEqual(2, model.Anchor, "an assignment is not a reason to drop a selection");
            Assert.AreEqual(7, model.Caret);
            Assert.AreEqual("LLO W", model.SelectedText);

            // And an end that no longer exists is the only one that moves.
            model.Text = "HELL";
            Assert.AreEqual(2, model.Anchor, "this end still fits");
            Assert.AreEqual(4, model.Caret, "and this one was clamped to what is left");
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
        public void The_Arbiter_Refuses_A_Composition_The_Platform_Is_Replaying()
        {
            var arbiter = new ImeCommitArbiter();
            arbiter.SuppressEchoOf("국");

            // Held, not late. A platform that never heard the composition was
            // over goes on reporting it for as long as the user leaves it
            // alone, so the window may not run out underneath it.
            for (int update = 0; update < 8; update++)
            {
                Assert.IsTrue(arbiter.ShouldSwallowComposition("국"), "the platform is repeating itself");
                Assert.IsNull(arbiter.Tick());
            }

            Assert.IsFalse(arbiter.ShouldSwallowComposition("ㅅ"), "a different composition is the user");
            Assert.IsTrue(arbiter.ShouldSwallow('국'),
                "and the character the platform sent as it let go is still an echo");
            Assert.IsTrue(arbiter.IsIdle);
            Assert.IsFalse(arbiter.ShouldSwallowComposition("국"), "the refusal retired with it");
        }

        [Test]
        public void A_Half_Delivered_Echo_That_Turns_Into_Something_Else_Stands_Down()
        {
            // The state an accumulating match has that an indexing one did not:
            // an echo that is neither complete nor wrong yet. The first two
            // jamo of 한 could still be the syllable we committed arriving in
            // pieces, so they are swallowed; the third event says they were
            // not, and from there the user is typing.
            var arbiter = new ImeCommitArbiter();
            arbiter.SuppressEchoOf(Hangul_HAN);

            Assert.IsTrue(arbiter.ShouldSwallow(Hangul_HAN_Jamo[0]), "this could still be the echo");
            Assert.IsTrue(arbiter.ShouldSwallow(Hangul_HAN_Jamo[1]), "and so could this");
            Assert.IsFalse(arbiter.ShouldSwallow('x'), "this could not; the user has moved on");
            Assert.IsTrue(arbiter.IsIdle, "and the guard does not resume");
        }

        [Test]
        public void An_Echo_That_Does_Not_Match_Stops_Being_Swallowed()
        {
            var arbiter = new ImeCommitArbiter();
            arbiter.SuppressEchoOf("ab");

            Assert.IsFalse(arbiter.ShouldSwallow('z'), "not our text; the user got there first");
            Assert.IsFalse(arbiter.ShouldSwallow('a'), "and the guard does not resume");
        }

        // -------------------------------------------------- choosing a backend

        /// <summary>
        /// The measurement the whole backend choice now rests on, kept as a
        /// test so that a Unity version or a platform where it stops being true
        /// says so here rather than in somebody's bug report.
        ///
        /// <c>UnityEngine.Input</c>'s input-method members answer under every
        /// Active Input Handling setting, including "Input System Package
        /// (New)", where its device members throw. Believing otherwise — the
        /// <c>#if ENABLE_LEGACY_INPUT_MANAGER</c> that used to be around
        /// <c>ImguiImeInput</c> — is what left every project using the Input
        /// System composing through a channel that reports nothing on macOS,
        /// and typing 안녕하세요 into one of those produced
        /// ㅇㅏㄴㄴㅕㅇㅎㅏㅅㅔㅇㅛ: every jamo committed on its own, because
        /// the platform was never asked to compose.
        /// </summary>
        [Test]
        public void The_Platform_Input_Method_Answers_Whatever_The_Input_Backend_Is()
        {
            Assert.IsTrue(ImeInput.PlatformImeAnswers(),
                "UnityEngine.Input's input-method members did not answer in the configuration " +
                "these tests run in. If that is a real change and not a bug, the Input System " +
                "backend is the fallback and this is where the decision to lean on it gets " +
                "made — but nothing about the current arrangement is safe until somebody " +
                "looks.");
        }

        /// <summary>
        /// And that a field actually gets it. Registration wins over the
        /// built-in backend, which is what lets these tests drive composition
        /// by hand, so the assertion has to be made with nothing registered.
        /// </summary>
        [Test]
        public void A_Field_With_Nothing_Registered_Gets_The_Built_In_Input_Method()
        {
            ImeInput.Unregister();
            try
            {
                Assert.IsInstanceOf<ImguiImeInput>(ImeInput.Create(),
                    "a field would compose through something else, or through nothing: " +
                    ImeInput.Describe());
            }
            finally
            {
                // The fixture registered the fake in SetUp and the next test
                // expects it there.
                ImeInput.Register(() => _ime);
            }
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

            /// <summary>
            /// Which of the two real backends this fake is standing in for
            /// when the field ends the session, because they answer the same
            /// question differently and the difference is load-bearing.
            ///
            /// On, it is <c>ImguiImeInput</c>: a poll of the platform, which
            /// goes on reporting a composition the platform never dropped —
            /// nothing in <see cref="IImeInput"/> can ask it to. Off (the
            /// default, and what every test that does not care about this
            /// wants), it is <c>InputSystemImeInput</c>: a cache of what the
            /// platform last pushed, emptied when the session ends, so it
            /// reports nothing until an event refills it whether the platform
            /// let go or not. A test drives that second shape by setting
            /// <see cref="Composition"/> again after some idle updates.
            /// </summary>
            public bool KeepsComposingAfterEnd;

            public bool IsAvailable => true;

            public void Begin() => Running = true;

            public void End()
            {
                Running = false;
                if (!KeepsComposingAfterEnd) Composition = string.Empty;
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
            // The arbiter is one object for one platform now, which is the
            // point of it — but it means these tests share it the way two
            // fields do. A syllable one test left registered is a syllable the
            // next one's platform appears to be holding.
            ImeCommitArbiter.Shared.Forget();
        }

        [TearDown]
        public void UnregisterFakeIme()
        {
            ImeInput.Unregister();
            ImeCommitArbiter.Shared.Forget();
        }

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

        private static Event Key(KeyCode code, char character = '\0',
            EventModifiers modifiers = EventModifiers.None) =>
            new Event
            {
                type = EventType.KeyDown,
                keyCode = code,
                character = character,
                modifiers = modifiers,
            };

        /// <summary>
        /// A press at a point in the label's own space, in the screen
        /// coordinates the EventSystem would have delivered it in. The canvas
        /// these tests build has no camera, which is the case where a screen
        /// point and a world point are the same number.
        /// </summary>
        private static PointerEventData PointerAt(OneTextLabel label, Vector2 localPoint)
        {
            var world = label.transform.TransformPoint(new Vector3(localPoint.x, localPoint.y, 0f));
            return new PointerEventData(EventSystem.current) { position = world };
        }

        /// <summary>A point to the left of everything, which hit-tests as index 0.</summary>
        private static Vector2 BeforeTheFirstCharacter(OneTextLabel label)
        {
            var head = label.GetCaretRect(0, 2f);
            return new Vector2(head.center.x - 20f, head.center.y);
        }

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
        public void The_Syllable_Committed_On_The_Way_Out_Does_Not_Come_Back_On_The_Way_In()
        {
            // The bug report, end to end and through the field: type Korean,
            // stop, resume, and the last syllable is entered twice. The fake
            // IME is told to behave like the platform actually does — it goes
            // on composing after the field has ended the session, because
            // nothing in IImeInput can tell it to stop.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "한";
            field.caretPosition = 1;

            _ime.KeepsComposingAfterEnd = true;
            _ime.Composition = "국";
            field.UpdateEditing();
            Assert.AreEqual("한", field.text, "the composition is not the value yet");

            field.DeactivateInputField();
            Assert.AreEqual("한국", field.text, "the syllable survived losing focus");

            // Input resumes. The platform is still reporting 국.
            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            for (int update = 0; update < 8; update++) field.UpdateEditing();

            Assert.IsFalse(field.isComposing, "the committed syllable was adopted back as a new composition");
            Assert.AreEqual("한국", field.text);
            Assert.AreEqual("한국", field.displayText, "and drawn a second time behind the caret");

            // Typing the next jamo is what finally makes the platform let go:
            // it finalises 국 as a character and starts composing ㅅ.
            _ime.Composition = "ㅅ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '국'));

            Assert.AreEqual("한국", field.text, "the platform's commit was applied on top of the field's");
            Assert.AreEqual("ㅅ", field.compositionString);
            Assert.AreEqual("한국ㅅ", field.displayText);

            Destroy(field);
        }

        [Test]
        public void A_Syllable_Handed_Over_As_A_Character_Is_Not_Also_Owed_As_A_Composition()
        {
            // The report with nothing exotic in it: no click, no focus change,
            // just typing. The Input Manager backend reports what the platform
            // is holding, and the platform does not have to update that report
            // on the same frame it hands the finished syllable over as a
            // character. For one frame it says both — here is 한, and I am still
            // composing 한 — and a field that believes both draws it twice and
            // commits it twice.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            Assert.AreEqual("안녕", field.text);

            // The character arrives; the report has not caught up.
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual("안녕" + Hangul_HAN, field.text);
            Assert.IsFalse(field.isComposing,
                "the composition was still owed after the platform had paid it");
            Assert.AreEqual("안녕" + Hangul_HAN, field.displayText, "so the syllable was drawn twice");

            // The report goes on saying 한 for as long as the platform holds it,
            // and none of those polls may put it back.
            for (int update = 0; update < 6; update++) field.UpdateEditing();
            Assert.IsFalse(field.isComposing);
            Assert.AreEqual("안녕" + Hangul_HAN, field.displayText);

            // Then the platform lets go, and the grace window must not decide
            // it is owed a commit nobody sent.
            _ime.Composition = string.Empty;
            for (int update = 0; update < 6; update++) field.UpdateEditing();

            Assert.AreEqual("안녕" + Hangul_HAN, field.text,
                "a syllable appeared that the user did not type");

            Destroy(field);
        }

        [Test]
        public void The_Echo_Of_A_Committed_Syllable_Does_Not_Retire_The_Refusal()
        {
            // The same shape one step later. The field commits on its way out
            // of focus, the platform goes on reporting the syllable, and then
            // sends its own commit of it as a character. Swallowing that
            // character is right; reading it as "the platform has let go" is
            // not — at a syllable boundary a character means the opposite — and
            // the poll after it would adopt the composition still being held.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안";
            field.caretPosition = 1;

            _ime.KeepsComposingAfterEnd = true; // the Input Manager shape
            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();

            field.DeactivateInputField();
            Assert.AreEqual("안" + Hangul_HAN, field.text);

            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            field.UpdateEditing();
            Assert.IsFalse(field.isComposing, "the replay was adopted before the echo even arrived");

            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));
            Assert.AreEqual("안" + Hangul_HAN, field.text, "the echo was inserted");

            for (int update = 0; update < 6; update++) field.UpdateEditing();
            Assert.IsFalse(field.isComposing,
                "the character retired the refusal and the held composition walked back in");
            Assert.AreEqual("안" + Hangul_HAN, field.text);
            Assert.AreEqual("안" + Hangul_HAN, field.displayText);

            Destroy(field);
        }

        [Test]
        public void A_Backend_That_Goes_Blank_While_Focus_Is_Away_Is_Still_Guarded()
        {
            // The other backend, and the one that made the first version of
            // this fix miss. InputSystemImeInput does not poll the platform, it
            // caches what the platform pushes, and ending the session empties
            // that cache — so every update between focus returning and the next
            // composition event reports nothing at all. Reading that as "the
            // platform let go" retired both guards during the gap, and the
            // replay then arrived to find nothing left to stop it.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "한";
            field.caretPosition = 1;

            _ime.Composition = "국";
            field.UpdateEditing();
            field.DeactivateInputField();

            Assert.AreEqual("한국", field.text);
            Assert.AreEqual(string.Empty, _ime.Composition, "the cache was emptied, as the real one is");

            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            for (int update = 0; update < 6; update++) field.UpdateEditing();

            // The composition event finally fires, with the syllable the
            // platform was holding all along.
            _ime.Composition = "국";
            field.UpdateEditing();

            Assert.IsFalse(field.isComposing, "the replay arrived after the refusal had timed out");
            Assert.AreEqual("한국", field.text);
            Assert.AreEqual("한국", field.displayText);

            // And the character it sends when it does let go is still an echo,
            // however many updates of silence went past first.
            _ime.Composition = "ㅅ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '국'));

            Assert.AreEqual("한국", field.text, "the platform's commit landed on top of the field's");
            Assert.AreEqual("ㅅ", field.compositionString);

            Destroy(field);
        }

        [Test]
        public void A_Listener_That_Rewrites_The_Value_Does_Not_Hand_The_Duplicate_Back()
        {
            // onEndEdit fires one line after the commit that armed the guards,
            // and what half the projects using it do there is put a tidied-up
            // version of the value straight back. That assignment used to
            // cancel the arbiter outright, which handed the reported bug back
            // to exactly the fields most likely to have it.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "한 ";
            field.caretPosition = 1;
            field.onEndEdit.AddListener(value => field.text = value.Trim());

            _ime.KeepsComposingAfterEnd = true;
            _ime.Composition = "국";
            field.UpdateEditing();
            field.DeactivateInputField();
            Assert.AreEqual("한국", field.text, "the listener trimmed the committed value");

            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            for (int update = 0; update < 4; update++) field.UpdateEditing();
            Assert.IsFalse(field.isComposing, "the assignment cleared the refusal the commit had just set");
            Assert.AreEqual("한국", field.displayText);

            _ime.Composition = "ㅅ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '국'));
            Assert.AreEqual("한국", field.text, "and cleared the echo guard with it");

            Destroy(field);
        }

        [Test]
        public void A_Syllable_Abandoned_In_One_Field_Does_Not_Arrive_In_The_Next()
        {
            // The half of the report no per-field guard could ever have caught,
            // and the reason the register is one object for the whole process.
            // There is one input method. A user filling in a form who leaves a
            // syllable composing in the name field and clicks straight into the
            // address field leaves the platform holding it — and the address
            // field polls that same platform. With a register of its own it has
            // never heard of the syllable, so it adopts it, draws it, and
            // commits it: a character nobody typed, in a box nobody typed it
            // in. Both channels can carry it there, and the character is the
            // one that lands in the wrong field even on a platform that drops
            // the composition.
            var first = CreateField(out _);
            var second = CreateField(out _);
            second.text = "b";

            first.ActivateInputField();
            first.text = "안";
            first.caretPosition = 1;

            _ime.KeepsComposingAfterEnd = true; // the platform is never told
            _ime.Composition = Hangul_HAN;
            first.UpdateEditing();
            Assert.AreEqual("안", first.text, "the composition is not the value yet");

            // Moving on without finishing the syllable: the field commits it
            // because nobody else will, and the platform goes on holding it.
            first.DeactivateInputField();
            Assert.AreEqual("안" + Hangul_HAN, first.text, "the syllable survived losing focus");

            second.ActivateInputField();
            second.caretPosition = 1;
            for (int update = 0; update < 6; update++) second.UpdateEditing();

            Assert.IsFalse(second.isComposing,
                "the syllable the first field committed was adopted by the second");
            Assert.AreEqual("b", second.text);
            Assert.AreEqual("b", second.displayText, "and drawn in it");

            // And the platform's own commit of that syllable, whenever it
            // arrives, arrives at whichever field has the keyboard now.
            second.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual("b", second.text, "the character followed it into the next field");
            Assert.AreEqual("안" + Hangul_HAN, first.text, "and the field it belonged to still has it once");

            Destroy(first);
            Destroy(second);
        }

        [Test]
        public void A_Commit_The_Platform_Delivers_Twice_Is_Only_Typed_Once()
        {
            // The bug report, finally read off a recording of the platform
            // instead of reasoned about. On macOS, when focus leaves on the
            // same frame the composition ends, the committed syllable is
            // delivered twice: once there and then, and once more on the first
            // keystroke after the field is focused again — a hundred frames
            // later, and in the same frame as the composition for the syllable
            // the user is actually typing now. Pressing Enter avoids it because
            // the composition then ends long before focus moves.
            //
            // The frames below are the ones from that log.
            var field = CreateField(out _);
            field.ActivateInputField();

            // f=800..885: ㅎ, 하, 한.
            foreach (string state in new[] { Hangul_H, Hangul_HA, Hangul_HAN })
            {
                _ime.Composition = state;
                field.UpdateEditing();
            }
            Assert.AreEqual(string.Empty, field.text, "a composition is not the value yet");

            // f=947: the composition ends, the platform delivers the syllable,
            // and focus leaves — all in the one frame.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));
            Assert.AreEqual(Hangul_HAN, field.text, "the platform's commit is the value");

            field.DeactivateInputField();
            Assert.AreEqual(Hangul_HAN, field.text);

            // f=1004: clicked back in.
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // f=1048: the next keystroke starts its own composition, and the
            // platform delivers the previous syllable a second time in the
            // same frame. The keycode-only event is the ㄱ the user pressed.
            _ime.Composition = "ㄱ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.R));
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual(Hangul_HAN, field.text,
                "the platform said it twice and the field typed it twice");
            Assert.AreEqual("ㄱ", field.compositionString,
                "and refusing the repeat disturbed the composition that arrived with it");
            Assert.AreEqual(Hangul_HAN + "ㄱ", field.displayText);

            Destroy(field);
        }

        [Test]
        public void The_Same_Syllable_Typed_Twice_Is_Typed_Twice()
        {
            // The other side of that guard, and the reason it retires on the
            // composition ending rather than on a clock: a user typing 한한
            // sends the same character twice too. What tells them apart is that
            // the user's second one is announced — a whole composition of its
            // own, ending, before the character arrives — and the platform's
            // repeat is not announced by anything.
            var field = CreateField(out _);
            field.ActivateInputField();

            for (int syllable = 0; syllable < 2; syllable++)
            {
                foreach (string state in new[] { Hangul_H, Hangul_HA, Hangul_HAN })
                {
                    _ime.Composition = state;
                    field.UpdateEditing();
                }

                _ime.Composition = string.Empty;
                field.UpdateEditing();
                field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));
            }

            Assert.AreEqual(Hangul_HAN + Hangul_HAN, field.text,
                "the guard against the platform repeating itself swallowed the user repeating themselves");

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

        /// <summary>
        /// Clicking into an empty field has to show a caret, which is the one
        /// state where there is no text to measure one against. Unity's field
        /// and TextMesh Pro's both draw a full line-height bar there; ours drew
        /// nothing, so an empty field looked like it had not taken focus.
        /// </summary>
        [Test]
        public void An_Empty_Field_Still_Draws_A_Caret()
        {
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.UpdateVisuals();

            var caret = label.GetCaretRect(0, 2f);
            Assert.Greater(caret.height, 0f,
                $"nothing is drawn for the caret of an empty field, so clicking into one looks " +
                $"like nothing happened. (preferredHeight of the empty label is " +
                $"{label.preferredHeight}, which says whether the layout made a line to " +
                $"measure or none at all.)");
            Assert.Greater(caret.width, 0f, "a caret with no width is not drawn either");

            Destroy(field);
        }

        /// <summary>
        /// And that the bar is where the text would have started, rather than
        /// at the origin of a rect nobody laid out: a centred empty field puts
        /// its caret in the middle, the way both of the others do.
        /// </summary>
        [Test]
        public void The_Empty_Caret_Sits_Where_The_First_Character_Would()
        {
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.UpdateVisuals();
            var empty = label.GetCaretRect(0, 2f);

            field.text = "x";
            field.UpdateVisuals();
            var typed = label.GetCaretRect(0, 2f);

            Assert.AreEqual(typed.x, empty.x, 0.01f,
                "the caret moves when the first character arrives, so it was not where that " +
                "character was going to be");
            Assert.AreEqual(typed.height, empty.height, 0.5f,
                "the empty caret is a different height from the one beside a character");

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

        // ------------------------------------------------- the syllable boundary

        [Test]
        public void A_Committed_Syllable_Lands_While_The_Next_One_Is_Composing()
        {
            // The bug this fixture is named for, driven the way the platform
            // drives it. The IME is polled before the key queue is drained, so
            // on the frame 한 is committed the composition has already moved on
            // to ㄱ: the field sees the next syllable first and the finished one
            // second, and both have to end up in the right order.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            foreach (string state in new[] { Hangul_H, Hangul_HA, Hangul_HAN })
            {
                _ime.Composition = state;
                field.UpdateEditing();
            }
            Assert.AreEqual("안녕", field.text, "nothing is committed while the syllable builds");

            _ime.Composition = "ㄱ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual("안녕" + Hangul_HAN, field.text, "the syllable was dropped on the floor");
            Assert.AreEqual("ㄱ", field.compositionString, "and the next one is still being typed");
            Assert.AreEqual("안녕" + Hangul_HAN + "ㄱ", field.displayText,
                "the composition is drawn after the syllable that finished, not in front of it");

            Destroy(field);
        }

        [Test]
        public void A_Committed_Syllable_That_Arrives_A_Poll_Late_Still_Lands_In_Order()
        {
            // Same two events, one update apart, because the order they arrive
            // in is the platform's business and not something to depend on.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();

            _ime.Composition = "ㄱ";
            field.UpdateEditing();
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual("안녕" + Hangul_HAN, field.text);
            Assert.AreEqual("ㄱ", field.compositionString);
            Assert.AreEqual("안녕" + Hangul_HAN + "ㄱ", field.displayText);

            Destroy(field);
        }

        [Test]
        public void A_Japanese_Conversion_Replaces_The_Composition_Without_Committing_It()
        {
            // Pressing space over へんかん replaces the whole composition with
            // 変換 and sends no character at all. It looks exactly like a
            // commit and is not one, which is why nothing here may treat a
            // changed composition as one: doing that would put 変換 in the text
            // and leave it in the composition too.
            var field = CreateField(out _);
            field.ActivateInputField();

            _ime.Composition = "へんかん";
            field.UpdateEditing();

            _ime.Composition = "変換";
            field.UpdateEditing();

            Assert.AreEqual(string.Empty, field.text, "converting is not committing");
            Assert.AreEqual("変換", field.compositionString);
            Assert.AreEqual("変換", field.displayText, "and it is drawn once");

            for (int update = 0; update < 4; update++) field.UpdateEditing();
            Assert.AreEqual(string.Empty, field.text, "nothing arrives later to double it");
            Assert.AreEqual("変換", field.displayText);

            Destroy(field);
        }

        [Test]
        public void The_Keys_An_Input_Method_Uses_Are_Still_Not_Text()
        {
            // The other half of letting committed characters through: every
            // event that is not one still belongs to the IME. Tab and Return
            // carry a character and are not text; a shortcut carries one and
            // belongs to the application; Delete carries none.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            bool submitted = false;
            field.onSubmit.AddListener(_ => submitted = true);

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();

            field.ProcessKeyEvent(Key(KeyCode.Tab, '\t'));
            field.ProcessKeyEvent(Key(KeyCode.KeypadEnter, '\r'));
            field.ProcessKeyEvent(Key(KeyCode.Delete));
            field.ProcessKeyEvent(Key(KeyCode.RightArrow));
            field.ProcessKeyEvent(Key(KeyCode.V, 'v', EventModifiers.Control));

            Assert.AreEqual("안녕", field.text, "one of these edited the text behind the composition");
            Assert.AreEqual(2, field.caretPosition);
            Assert.AreEqual(Hangul_HAN, field.compositionString, "and the composition survived all of them");
            Assert.IsFalse(submitted, "Enter confirmed a candidate; it did not submit the form");

            Destroy(field);
        }

        // ------------------------------------------------------- focus and clicks

        [Test]
        public void Taking_Focus_Selects_The_Whole_Value()
        {
            var field = CreateField(out _);
            field.text = "old value";
            Assert.IsFalse(field.editingModel.HasSelection, "nothing is selected before anyone arrives");

            field.ActivateInputField();

            Assert.AreEqual(0, field.selectionAnchorPosition);
            Assert.AreEqual("old value".Length, field.caretPosition);
            Assert.AreEqual("old value", field.editingModel.SelectedText);

            // Which is what the selection is for: the first keystroke replaces
            // the value rather than lengthening it.
            field.ProcessKeyEvent(Key(KeyCode.None, 'x'));
            Assert.AreEqual("x", field.text);

            Destroy(field);
        }

        [Test]
        public void The_First_Click_Places_The_Caret_Rather_Than_Selecting_Everything()
        {
            // A click says where in the value the user wants to be, so the
            // field puts the caret there and leaves the value alone — first
            // click included. Highlighting all of it instead is what makes the
            // next keystroke delete an edit somebody was coming back to finish.
            var field = CreateField(out var label);
            field.text = "abcdef";
            field.UpdateVisuals();

            field.OnPointerDown(PointerAt(label, BeforeTheFirstCharacter(label)));

            Assert.IsTrue(field.isFocused);
            Assert.AreEqual(0, field.caretPosition, "the click landed before the first character");
            Assert.IsFalse(field.editingModel.HasSelection, "and selected nothing");

            Destroy(field);
        }

        [Test]
        public void A_Second_Click_Places_The_Caret_Again()
        {
            // Nothing about the first click was special, and this is the test
            // that says so: a field already being edited answers a click the
            // same way an unfocused one does.
            var field = CreateField(out var label);
            field.text = "abcdef";
            field.UpdateVisuals();
            field.ActivateInputField(); // focused, and selected by the keyboard rule
            Assert.IsTrue(field.editingModel.HasSelection);

            field.OnPointerDown(PointerAt(label, BeforeTheFirstCharacter(label)));

            Assert.AreEqual(0, field.caretPosition, "the click landed before the first character");
            Assert.IsFalse(field.editingModel.HasSelection, "and replaced the selection with a caret");

            Destroy(field);
        }

        [Test]
        public void A_Field_That_Does_Not_Select_On_Focus_Keeps_Its_Caret_When_Activated()
        {
            // The flag governs the keyboard and script paths, which are the
            // only ones that ever selected: switching it off leaves the caret
            // exactly where the value left it.
            var field = CreateField(out _);
            field.onFocusSelectAll = false;
            field.text = "abcdef";
            field.caretPosition = 2;

            field.ActivateInputField();

            Assert.AreEqual(2, field.caretPosition);
            Assert.IsFalse(field.editingModel.HasSelection);

            Destroy(field);
        }

        [Test]
        public void A_Click_While_The_Input_Method_Composes_Leaves_The_Caret_To_It()
        {
            // The caret is inside the composition and the composition is
            // anchored to the caret, so moving one moves the other. The field
            // used to resolve that by committing the syllable where it stood —
            // a commit the platform was never told about, and the start of the
            // duplicate this fixture is full of tests for.
            var field = CreateField(out var label);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;
            field.UpdateVisuals();

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            field.UpdateVisuals();

            field.OnPointerDown(PointerAt(label, BeforeTheFirstCharacter(label)));

            Assert.IsTrue(field.isComposing, "the click committed the syllable behind the IME's back");
            Assert.AreEqual("안녕", field.text);
            Assert.AreEqual(2, field.caretPosition, "and moved the caret out from under the composition");
            Assert.AreEqual("안녕" + Hangul_HAN, field.displayText);

            Destroy(field);
        }
    }
}
