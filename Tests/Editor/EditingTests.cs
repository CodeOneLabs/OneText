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

        /// <summary>
        /// The second report of 2026-08-20, from Windows: 아, backspace,
        /// backspace. The IMM handles both presses itself — the field is never
        /// told a key was pressed, and all it sees is the report shrinking to
        /// ㅇ and then to nothing. The syllable was deleted, not committed, and
        /// the window the emptying opens must not make a commit of it: the
        /// user's account of the bug is that the delete appeared to be
        /// swallowed and a third press was needed, which is this insert two
        /// updates later putting the ㅇ back as ordinary text.
        ///
        /// The presses are separated by idle updates on purpose. The signal is
        /// a property of the composition, not of two polls landing next to each
        /// other; a user who pauses between backspaces gets the same answer.
        /// </summary>
        [Test]
        public void A_Composition_The_User_Backspaces_Away_Is_Not_Committed()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);

            model.SetComposition("\u3147");   // ㅇ
            model.Tick();
            model.SetComposition("\uC544");   // 아
            for (int idle = 0; idle < 4; idle++) model.Tick();

            model.SetComposition("\u3147");   // backspace, inside the IME
            Assert.AreEqual("안녕", model.Text, "a shortened composition commits nothing");
            for (int idle = 0; idle < 4; idle++) model.Tick();

            model.SetComposition(string.Empty); // backspace again: gone
            Assert.IsFalse(model.IsComposing);

            for (int update = 0; update < ImeCommitArbiter.DefaultGraceUpdates + 4; update++)
                model.Tick();

            Assert.AreEqual("안녕", model.Text, "the deleted syllable came back as committed text");
            Assert.AreEqual("안녕", model.DisplayText);
        }

        /// <summary>
        /// The same shape, paid for: a syllable splitting shrinks the report to
        /// a prefix of itself too, and that one is a commit. What tells them
        /// apart is the character arriving to pay for what was dropped, so the
        /// commit still lands.
        /// </summary>
        [Test]
        public void A_Syllable_That_Splits_Is_Still_Committed_Though_Its_Report_Shrank()
        {
            var model = new TextEditingModel { Text = "안녕" };
            model.SetCaret(2, false);

            model.SetComposition("\uC559");   // 앙
            model.Tick();
            model.SetComposition("\uC544");   // 아 — 앙 gave up its final ㅇ
            Assert.IsTrue(model.AcceptCharacter('\uC544', out _), "the split pays on the character channel");
            for (int idle = 0; idle < 4; idle++) model.Tick();

            model.SetComposition(string.Empty);
            for (int update = 0; update < ImeCommitArbiter.DefaultGraceUpdates + 4; update++)
                model.Tick();

            Assert.AreEqual("안녕\uC544\uC544", model.Text, "a paid shrink is a split, and both syllables land");
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
            // payment, and a payment that failed to match would leave the
            // syllable owed, drawn a second time and committed again behind it.
            //
            // The commit arrives the way the probe recordings show every
            // commit arriving (Tools/ImeProbe~, macOS, 2026-08-20: 안, 녕, 하
            // and 세 each land in the frame the report flips to the next
            // syllable): the report has already moved on to ㄱ when the jamo
            // are handed over, and they pay for the 한 that was replaced, not
            // for the ㄱ on screen. An earlier version of this test had the
            // platform paying 한 while still reporting 한, unchanged — a shape
            // written down before any recording existed and observed in none
            // of them; the model now reads that shape as a committed twin plus
            // a re-issue instead.
            var model = new TextEditingModel { Text = "안" };
            model.SetCaret(1, false);
            model.SetComposition(Hangul_HAN);
            model.Tick();

            model.SetComposition("ㄱ"); // the report moves; the payment lands behind it
            foreach (char jamo in Hangul_HAN_Jamo)
                model.AcceptCharacter(jamo, out _);

            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.Text, "the jamo are the value now");
            Assert.IsTrue(model.IsComposing, "and the next syllable is still being typed");
            Assert.AreEqual("ㄱ", model.Composition.Text);
            Assert.AreEqual("안" + Hangul_HAN_Jamo + "ㄱ", model.DisplayText,
                "the syllable that finished is drawn once, ahead of the composition");

            for (int update = 0; update < 4; update++) model.Tick();
            Assert.AreEqual("안" + Hangul_HAN_Jamo, model.Text,
                "and nothing arrives later to double it");
        }

        [Test]
        public void A_Character_That_Pays_For_Nothing_Does_Not_Spoil_The_Payment_Behind_It()
        {
            // 닭 and ㅏ: the platform commits 달 and composes 가, and 달 is no
            // part of 가 — against the composition on screen it pays for
            // nothing. Left in the running total, everything that arrives
            // after it would be matched as "달…" and never fit, and the
            // composition would stay owed after being paid.
            //
            // Driven at the recorded timings rather than the assumed ones
            // (Tools/ImeProbe~, macOS, 2026-08-20). The split's commit may
            // land a poll late — the order the two channels arrive in is the
            // platform's business — so here 달 arrives after the register
            // that remembers the replaced 닭 has aged out, and lands in the
            // running total, which is the case this test exists for. The 가
            // behind it commits the way every recorded commit does, in the
            // frame the report moves — here, to empty. An earlier version had
            // 가 paying against a report that never moved, a shape no
            // recording shows.
            var model = new TextEditingModel();
            model.SetComposition("달");
            model.Tick();
            model.SetComposition("닭");
            model.Tick();
            model.SetComposition("가"); // ㅏ: the report moves on ...
            model.Tick();
            model.Tick();               // ... and the commit arrives late, past the register

            Assert.IsTrue(model.AcceptCharacter('달', out _));
            Assert.IsTrue(model.IsComposing, "달 is not 가, so 가 is still owed");
            Assert.AreEqual("달가", model.DisplayText, "and both are drawn, each once");

            // The report empties and 가 lands in that frame, as recorded.
            model.SetComposition(string.Empty);
            Assert.IsTrue(model.AcceptCharacter('가', out _));
            Assert.AreEqual("달가", model.Text);
            Assert.IsFalse(model.IsComposing, "the composition was paid and a stale character got in the way");

            for (int update = 0; update < 4; update++) model.Tick();
            Assert.AreEqual("달가", model.Text, "and the grace window invents nothing on top");
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

            /// <summary>
            /// The same fact as <see cref="KeepsComposingAfterEnd"/> read from
            /// the other end: a fake standing in for the poll of the platform
            /// is one whose empty report means the platform is empty, and one
            /// standing in for the pushed cache is not.
            /// </summary>
            public bool ReportsPlatformState => KeepsComposingAfterEnd;

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

        /// <summary>
        /// The second report of 2026-08-20, at the field: on Windows the user
        /// typed 아, pressed backspace and was left with ㅇ, pressed it again
        /// and nothing happened, and had to press a third time to be rid of it.
        ///
        /// Driven with no key events at all, which is the point. Whether the
        /// IMM passes the backspace on to Unity is exactly what this field
        /// cannot know — the report is that it does not — so the deletion has
        /// to hold on the composition channel alone. The one before this test
        /// covers the platform that does deliver the key.
        /// </summary>
        [Test]
        public void A_Composition_Backspaced_Away_Behind_The_Fields_Back_Stays_Away()
        {
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = "\u3147";      // ㅇ
            field.UpdateEditing();
            _ime.Composition = "\uC544";      // 아
            field.UpdateEditing();
            Assert.AreEqual("안녕", field.text);
            Assert.AreEqual("\uC544", field.compositionString);

            _ime.Composition = "\u3147";      // backspace: the IMM shortens it
            field.UpdateEditing();
            Assert.AreEqual("\u3147", field.compositionString, "the composition lost its vowel");
            Assert.AreEqual("안녕", field.text);

            _ime.Composition = string.Empty;   // backspace again: the IMM drops it
            for (int idle = 0; idle < 40; idle++) field.UpdateEditing();

            Assert.AreEqual("안녕", field.text, "the deleted syllable came back as committed text");
            Assert.AreEqual("안녕", field.displayText);
            Assert.IsFalse(field.isComposing);

            Destroy(field);
        }

        /// <summary>
        /// The same deletion with the key event arriving as well, before the
        /// poll that empties the report — the ordering the Windows fix was
        /// nearly built on. The field must not treat that press as an ordinary
        /// backspace: the composition is still standing when it arrives, so the
        /// value is not its to touch, and the emptying that follows still
        /// commits nothing.
        /// </summary>
        [Test]
        public void A_Backspace_Delivered_Before_The_Emptying_Deletes_Nothing_Else()
        {
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = "\uC544";      // 아
            field.UpdateEditing();
            _ime.Composition = "\u3147";      // backspace 1: shortened
            field.UpdateEditing();

            field.ProcessKeyEvent(Key(KeyCode.Backspace)); // backspace 2, key first
            _ime.Composition = string.Empty;               // then the report empties
            for (int idle = 0; idle < 40; idle++) field.UpdateEditing();

            Assert.AreEqual("안녕", field.text, "the value was neither added to nor cut into");
            Assert.AreEqual("안녕", field.displayText);

            Destroy(field);
        }

        /// <summary>
        /// And with the press arriving after the emptying as a character rather
        /// than a keycode — which is how IMGUI hands a key that carries text to
        /// a field, one event for the code and one for the character, and a
        /// backspace carries U+0008. The keycode event is caught by the discard
        /// above; this one is not, and it reaches the settle that makes an owed
        /// commit real. A window opened by a deletion owes nothing to that
        /// either, or the syllable the user removed is put back by the second
        /// half of the very press that removed it.
        /// </summary>
        [Test]
        public void The_Character_Half_Of_A_Backspace_Settles_No_Commit()
        {
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = "\uC544";      // 아
            field.UpdateEditing();
            _ime.Composition = "\u3147";      // backspace 1
            field.UpdateEditing();

            _ime.Composition = string.Empty;   // backspace 2: the report empties
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '\b'));
            for (int idle = 0; idle < 40; idle++) field.UpdateEditing();

            Assert.AreEqual("안녕", field.text, "the character half of the press settled a commit owed to nobody");
            Assert.AreEqual("안녕", field.displayText);

            Destroy(field);
        }

        [Test]
        public void Backspacing_The_Last_Jamo_Leaves_The_Committed_Text_Alone()
        {
            // Recorded on macOS, typing 안녕 and then backspacing it away. The
            // last backspace is the one that empties the composition, and the
            // platform reports all of it in a single update: the key, the
            // syllable as a character, and the composition ending.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안";
            field.caretPosition = 1;

            _ime.Composition = "\u3134"; // ㄴ, all that is left of 녕
            field.UpdateEditing();
            Assert.IsTrue(field.isComposing);

            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.Backspace));
            field.ProcessKeyEvent(Key(KeyCode.None, '\u3134'));

            Assert.AreEqual("안", field.text,
                "the backspace belonged to the composition it emptied, not to the text behind it");

            Destroy(field);
        }

        [Test]
        public void The_Same_Syllable_Typed_Twice_Is_Drawn_The_Second_Time()
        {
            // "I type 아, then 아 again, and it is in the field but nothing
            // shows until I press an arrow key or Enter." Driven the way the
            // recording shows macOS driving it: the second 아 is never composed
            // from nothing. The ㅇ that starts it is taken as the final of the
            // first — 앙 — and the ㅏ after it splits that into 아, committed
            // on the character channel, and 아, composing, in one update. That
            // character matches the live composition exactly, and crediting it
            // there ended a composition nobody had paid for, registered it as
            // a replay, and refused every report of the second 아 after it.
            var field = CreateField(out _);
            _ime.KeepsComposingAfterEnd = true; // the platform-poll backend
            field.ActivateInputField();

            _ime.Composition = "\u3147"; // ㅇ
            field.UpdateEditing();
            _ime.Composition = "아";
            field.UpdateEditing();
            _ime.Composition = "앙";
            field.UpdateEditing();
            Assert.AreEqual(string.Empty, field.text, "nothing is committed while the syllable builds");

            // The split: the report has moved on to the next 아 before the
            // character for the first one is drained.
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));

            Assert.AreEqual("아", field.text, "the first 아 is the value");
            Assert.IsTrue(field.isComposing,
                "the second 아 is the user typing, not the platform replaying the first");
            Assert.AreEqual("아", field.compositionString,
                "and it is the composition, drawn under the caret rather than refused");
            Assert.AreEqual("아아", field.displayText);

            // And it stays drawn for as long as the platform holds it.
            for (int update = 0; update < 6; update++) field.UpdateEditing();
            Assert.IsTrue(field.isComposing);
            Assert.AreEqual("아아", field.displayText);

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
            // What this test protects is the ending: a character taken once,
            // and a grace window that must not invent a commit nobody sent on
            // top of it. What it used to assume about the platform — that a
            // syllable is handed over as a character while the report goes on
            // saying it, unchanged — was written down before any recording
            // existed, and three probe recordings later (Tools/ImeProbe~,
            // macOS, 2026-08-20, ~4,500 frames) it has never been observed:
            // every commit arrives in the frame the report changes, and the
            // only characters that arrive against an unmoved report are a jamo
            // committed and re-issued identically. The model therefore reads
            // this shape as that re-issue: the character is credited to the
            // finished twin and the composition stays adopted and drawn — the
            // same artifact uGUI's own field produces unconditionally, since
            // it splices Input.compositionString into the display every
            // rebuild with no arbitration at all.
            //
            // The shape stays tested because Windows is unmeasured and might
            // be the platform the old premise described. If it is, this is
            // the contract that keeps the value right however long the stale
            // report lingers: the character reached the value once, the
            // lingering report costs a doubled drawing and nothing else, and
            // the window the report's eventual emptying opens is prepaid — it
            // inserts nothing.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안녕";
            field.caretPosition = 2;

            _ime.Composition = Hangul_HAN;
            field.UpdateEditing();
            field.UpdateEditing(); // the report stands still ...
            field.UpdateEditing();
            Assert.AreEqual("안녕", field.text);

            // ... and the character arrives against it, no report change in sight.
            field.ProcessKeyEvent(Key(KeyCode.None, Hangul_HAN[0]));

            Assert.AreEqual("안녕" + Hangul_HAN, field.text, "the character is the value, once");
            Assert.IsTrue(field.isComposing,
                "read as a committed twin and a re-issue, the composition stands");
            Assert.AreEqual("안녕" + Hangul_HAN + Hangul_HAN, field.displayText,
                "drawn behind the value until the report moves — the cost of a shape no recording shows");

            // The report goes on saying 한 for as long as the platform holds
            // it, and none of those polls may double the value.
            for (int update = 0; update < 6; update++) field.UpdateEditing();
            Assert.AreEqual("안녕" + Hangul_HAN, field.text);

            // Then the platform lets go with nothing else arriving, and the
            // grace window must not decide it is owed a commit nobody sent:
            // it was paid when the character was credited.
            _ime.Composition = string.Empty;
            for (int update = 0; update < 6; update++) field.UpdateEditing();

            Assert.AreEqual("안녕" + Hangul_HAN, field.text,
                "a syllable appeared that the user did not type");
            Assert.IsFalse(field.isComposing);
            Assert.AreEqual("안녕" + Hangul_HAN, field.displayText);

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
        public void Typing_An_Then_Backspacing_The_Jamo_Off_It_Replayed_From_A_Recording()
        {
            // Frame for frame off the probe: 안, then 녕 backspaced away one
            // jamo at a time. Every composition value here is what
            // Input.compositionString actually read on the update the field
            // polled it, and every key is one the recording shows arriving.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform

            Compose(field, "\u3147");            // ㅇ
            Compose(field, "아");
            Compose(field, "안");

            // The platform hands 안 over as a character and has already flipped
            // the composition to ㄴ by the time the field polls.
            Compose(field, "\u3134");            // ㄴ
            field.ProcessKeyEvent(Key(KeyCode.None, '안'));
            Assert.AreEqual("안", field.text, "the syllable the platform handed over");

            Compose(field, "녀");
            Compose(field, "녕");
            Compose(field, "녀");                 // backspaces the IME ate whole
            Compose(field, "\u3134");            // ㄴ

            // The last one: the IME hands back what is left, ends the
            // composition, and lets the key through — four times over. This is
            // the tail macOS sends behind a backspace that empties a
            // composition, in the order the field pops it, and the reason a
            // replay that modelled one press as one event could never
            // reproduce the bug.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None));            // no key, no character
            field.ProcessKeyEvent(Key(KeyCode.Backspace));
            field.ProcessKeyEvent(Key(KeyCode.None, '\u3134')); // the jamo handed back
            field.ProcessKeyEvent(Key(KeyCode.Backspace));       // and the press again

            Assert.AreEqual("안", field.text,
                "one press deletes the composition it emptied and nothing else");

            Destroy(field);
        }

        /// <summary>One update in which the platform reports this composition.</summary>
        private void Compose(OneTextInputField field, string composition)
        {
            _ime.Composition = composition;
            field.UpdateEditing();
        }

        [Test]
        public void A_Syllable_The_Platform_Reclaims_To_Edit_Leaves_The_Value()
        {
            // 삼겹살, click away, click back, backspace. The IME consumes the
            // key itself and reports composing 사 — 살 with its final jamo gone
            // — so the syllable being deleted is in the composition while the
            // value still holds it: 삼겹살사.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.text = "삼겹";
            field.caretPosition = 2;

            _ime.Composition = "살";
            field.UpdateEditing();
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.F));
            field.ProcessKeyEvent(Key(KeyCode.None, '살'));
            Assert.AreEqual("삼겹살", field.text);

            field.DeactivateInputField();
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // The IME consumed the backspace itself and reports 살 with its
            // final taken off.
            _ime.Composition = "사";
            field.UpdateEditing();

            Assert.AreEqual("삼겹", field.text,
                "the 살 the platform took back to edit is still in the value as well");
            Assert.AreEqual("사", field.compositionString);
            Assert.AreEqual("삼겹사", field.displayText,
                "which is what 삼겹살 with one jamo taken off it looks like");

            Destroy(field);
        }

        [Test]
        public void A_Syllable_Reclaimed_Down_To_Its_Lead_Leaves_The_Value()
        {
            // 우리집에, click away, click back, backspace. 에 is a lead and a
            // vowel, so taking the vowel off leaves the lead alone — reported
            // as the compatibility ㅇ, which shares no code point with the
            // conjoining one a decomposed 에 starts with. Structure cannot tell
            // this from the ㅇ of a syllable being started; silence can.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.text = "우리집";
            field.caretPosition = 3;

            _ime.Composition = "에";
            field.UpdateEditing();
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.P));
            field.ProcessKeyEvent(Key(KeyCode.None, '에'));
            Assert.AreEqual("우리집에", field.text);

            field.DeactivateInputField();
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // The IME consumed the backspace itself: a composition appears and
            // nothing carrying a key or a character comes with it.
            _ime.Composition = "\u3147"; // ㅇ
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None)); // the event that rides behind every change
            field.UpdateEditing();

            Assert.AreEqual("우리집", field.text,
                "에 is in the composition now and cannot be in the value as well");
            Assert.AreEqual("우리집\u3147", field.displayText,
                "which is 에 with its vowel taken off, and not a jamo beside it");

            Destroy(field);
        }

        [Test]
        public void A_Lead_The_User_Types_After_A_Two_Jamo_Syllable_Is_Their_Own()
        {
            // The same shape with the keystroke that made it: 우리집에 and then
            // ㅇ for the next syllable. Nothing of theirs may be taken back.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true;
            field.text = "우리집";
            field.caretPosition = 3;

            _ime.Composition = "에";
            field.UpdateEditing();
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.P));
            field.ProcessKeyEvent(Key(KeyCode.None, '에'));
            Assert.AreEqual("우리집에", field.text);

            _ime.Composition = "\u3147"; // ㅇ
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None)); // the one that rides behind
            field.ProcessKeyEvent(Key(KeyCode.D));    // and the keystroke that made it
            field.UpdateEditing();

            Assert.AreEqual("우리집에", field.text,
                "the user pressed a key, so the syllable behind the caret is theirs to keep");

            Destroy(field);
        }

        [Test]
        public void A_Syllable_The_Platform_Reclaims_To_Add_To_Leaves_The_Value()
        {
            // The same reclaim in the other direction, and the one that had
            // 삼겹살 come back as 삼겹살살: pressing ㅅ after 삼겹살 makes the IME
            // take 살 back and turn it into 삸, then split that into 살 and 사.
            // With 살 still in the value, the piece it commits is a second one.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.text = "삼겹";
            field.caretPosition = 2;

            _ime.Composition = "살";
            field.UpdateEditing();
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.F));
            field.ProcessKeyEvent(Key(KeyCode.None, '살'));
            Assert.AreEqual("삼겹살", field.text);

            field.DeactivateInputField();
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // ㅅ, and the platform answers with 살 carrying it: 삸.
            _ime.Composition = "삸";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.T));

            Assert.AreEqual("삼겹", field.text,
                "살 belongs to the composition now, and cannot stay in the value too");

            // Which it then splits back into 살 and the 사 being typed.
            _ime.Composition = "사";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.K));
            field.ProcessKeyEvent(Key(KeyCode.None, '살'));

            Assert.AreEqual("삼겹살", field.text,
                "the 살 it committed is the one it took, not a second one");
            Assert.AreEqual("삼겹살사", field.displayText);

            Destroy(field);
        }

        [Test]
        public void A_Syllable_The_User_Starts_Is_Not_Mistaken_For_A_Reclaim()
        {
            // The other side of it, and the reason the reclaim waits an update:
            // ㅅ after committing 살 is a prefix of 살 too, and taking that as a
            // reclaim would delete a syllable the user meant to keep. A key
            // arriving with the composition is what says the user made it.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true;
            field.text = "삼겹";
            field.caretPosition = 2;

            _ime.Composition = "살";
            field.UpdateEditing();
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.F));
            field.ProcessKeyEvent(Key(KeyCode.None, '살'));
            Assert.AreEqual("삼겹살", field.text);

            // A composition that starts life as a whole syllable and is a
            // prefix of what was just committed — the shape a reclaim has —
            // but with the keystroke that made it arriving behind it.
            // A syllable the user starts arrives one jamo at a time, and as a
            // compatibility jamo at that — which shares nothing with the
            // conjoining one a decomposed 살 begins with.
            _ime.Composition = "\u3145"; // ㅅ
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.T));
            field.UpdateEditing();

            Assert.AreEqual("삼겹살", field.text,
                "a syllable the user is starting is not the last one coming back");
            Assert.AreEqual("삼겹살\u3145", field.displayText,
                "and what they are composing sits after it, not inside it");

            Destroy(field);
        }

        [Test]
        public void The_Same_Syllable_Typed_Over_And_Over_Advances_Every_Time()
        {
            // 아 아 아, and the field read 아아. Replayed frame by frame from
            // the recording of 20 Aug: every 아 after the first is born at a
            // split — 앙 giving up 아 on the character channel and leaving 아
            // composing, in the same update — and the last one is committed by
            // the platform on its own, as focus leaves, with the report already
            // empty. Before this, the character at each split was credited to
            // the live 아 instead of the 앙 it came off, which ended the
            // composition "paid", refused every report of the real second 아
            // as a replay (five hundred frames of it in the log), and left the
            // register that refuses a repeated commit armed with nothing able
            // to retire it — so the commit of the third 아, arriving bare, was
            // swallowed as the platform repeating the first.
            var field = CreateField(out _);
            _ime.KeepsComposingAfterEnd = true; // ImguiImeInput, as recorded
            field.ActivateInputField();

            // f1006 ㅇ, f1118 ㅏ, f1543 ㅇ
            _ime.Composition = "\u3147";
            field.UpdateEditing();
            _ime.Composition = "아";
            field.UpdateEditing();
            _ime.Composition = "앙";
            field.UpdateEditing();
            Assert.AreEqual(string.Empty, field.text);

            // f1629 ㅏ: the poll sees the next 아 first, then the key queue
            // delivers K without a character, the committed 아, and an empty
            // event behind it — exactly as logged.
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.K));
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));
            field.ProcessKeyEvent(Key(KeyCode.None));
            Assert.AreEqual("아", field.text);
            Assert.AreEqual("아", field.compositionString, "the second 아 is on screen, not refused");

            // f1630..f1923: the platform goes on reporting the second 아.
            for (int update = 0; update < 8; update++) field.UpdateEditing();
            Assert.AreEqual("아", field.text);
            Assert.AreEqual("아", field.compositionString);

            // f1924 ㅇ, f2033 ㅏ: the same split again.
            _ime.Composition = "앙";
            field.UpdateEditing();
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.K));
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));
            field.ProcessKeyEvent(Key(KeyCode.None));
            Assert.AreEqual("아아", field.text);
            Assert.AreEqual("아", field.compositionString);

            for (int update = 0; update < 8; update++) field.UpdateEditing();

            // f2577: the platform commits the third 아 itself — the report is
            // empty at the poll and the character follows, with no composition
            // of the user's in between to tell anyone it is not a repeat.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.K));
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));
            Assert.AreEqual("아아아", field.text, "the third 아 was swallowed as the platform repeating the first");
            Assert.IsFalse(field.isComposing);

            // And the focus loss that came with it adds nothing.
            field.DeactivateInputField();
            Assert.AreEqual("아아아", field.text);

            Destroy(field);
        }

        [Test]
        public void The_Same_Jamo_Typed_Over_And_Over_Advances_Every_Time()
        {
            // ㅁ five times, and the field read ㅁ. The test above it is the
            // same complaint with a syllable, and it is fixed by the register
            // that remembers what the report replaced — which is armed by the
            // report's string changing, 앙 to 아.
            //
            // A jamo that cannot combine with itself never changes it. ㅁ plus
            // ㅁ is not a cluster, so the platform commits the first and opens
            // a second whose report reads exactly the same, and there is no 앙
            // in between for anything to notice. The character was credited to
            // the live composition, which ended it "paid" and registered it as
            // a replay, and from there every report of the ㅁ the user was
            // actually typing was refused and every commit behind it swallowed
            // as the platform repeating itself. Five presses, one ㅁ, and the
            // same for ㅏㅏㅏ; ㄱㄱ escapes only because it combines into ㄲ.
            //
            // What tells the two apart is measured, not assumed. Three probe
            // recordings (Tools/ImeProbe~, macOS, 2026-08-20, ~4,500 frames)
            // show every genuine commit arriving in the frame the report
            // changes — to the next syllable at a split, to empty at an Enter
            // or a click — and the only characters that ever arrive against an
            // unmoved report are exactly these re-issues. So a payment that
            // completes with the report unmoved is credited to the finished,
            // identical twin, and the composition the user is typing stays
            // adopted and drawn: every press lands, and every press is on
            // screen.
            var field = CreateField(out _);
            _ime.KeepsComposingAfterEnd = true; // ImguiImeInput, as recorded
            field.ActivateInputField();

            _ime.Composition = "ㅁ";
            field.UpdateEditing();
            Assert.AreEqual(string.Empty, field.text);
            Assert.AreEqual("ㅁ", field.compositionString);

            // Each press after the first: the poll sees the new composition —
            // the same string as the old one — and the key queue then delivers
            // the ㅁ the platform committed to open it.
            for (int press = 2; press <= 5; press++)
            {
                _ime.Composition = "ㅁ";
                field.UpdateEditing();
                field.ProcessKeyEvent(Key(KeyCode.M));
                field.ProcessKeyEvent(Key(KeyCode.None, 'ㅁ'));
                for (int idle = 0; idle < 3; idle++) field.UpdateEditing();

                Assert.AreEqual(new string('ㅁ', press - 1), field.text,
                    $"press {press} did not land");
                Assert.AreEqual("ㅁ", field.compositionString,
                    $"the composition press {press} opened is not on screen");
                Assert.AreEqual(new string('ㅁ', press), field.displayText,
                    $"press {press}: everything typed so far is what is drawn");
            }

            // Focus leaves and the platform commits the last one itself, with
            // the report already empty — the delivery that used to be swallowed
            // as a repeat of the first.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, 'ㅁ'));
            field.DeactivateInputField();

            Assert.AreEqual("ㅁㅁㅁㅁㅁ", field.text);

            Destroy(field);
        }

        [Test]
        public void The_Same_Syllable_The_Platform_Carries_On_Is_Not_Delivered_Twice()
        {
            // The 안녕하세요요 case with a syllable that is its own neighbour:
            // 아, click away, click back, ㅇ, ㅏ. The platform reopens the 아
            // it already delivered, makes 앙 of it, and splits that into 아
            // and 아 — handing the first over a second time. It reads exactly
            // as a user typing 아 again, and the register that refuses a
            // repeated commit must still be armed when it lands: a fix that
            // retired it the moment an equal composition was adopted took the
            // repeat as the user's and typed 아아 where 아 was meant.
            var field = CreateField(out _);
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.ActivateInputField();

            _ime.Composition = "\u3147";
            field.UpdateEditing();
            _ime.Composition = "아";
            field.UpdateEditing();

            // Focus leaves: the platform ends the composition and delivers.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));
            Assert.AreEqual("아", field.text);
            field.DeactivateInputField();

            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // ㅇ reopens the delivered syllable …
            _ime.Composition = "앙";
            field.UpdateEditing();
            field.UpdateEditing();

            // … and ㅏ splits it, handing 아 over again.
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));

            Assert.AreEqual("아", field.text, "the 아 the platform carried on is the one already in the value");
            Assert.IsTrue(field.isComposing);
            Assert.AreEqual("아", field.compositionString, "and the one being typed now is on screen");

            // Typing on from there is typed: one repeat is one repeat.
            field.UpdateEditing();
            _ime.Composition = "앙";
            field.UpdateEditing();
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '아'));
            Assert.AreEqual("아아", field.text, "the user's next 아 was taken for another repeat");
            Assert.AreEqual("아", field.compositionString);

            Destroy(field);
        }

        [Test]
        public void A_Syllable_The_Platform_Carries_On_Is_Not_Delivered_Twice()
        {
            // 안녕하세요, click away, click back, press ㅇ — and the platform
            // reports 용, because it kept 요 open and took the ㅇ as its final.
            // It then splits that into 요 and 아 and hands 요 over a second
            // time. Read frame by frame off a recording; uGUI's own field has
            // the same extra 요.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.text = "안녕하세";
            field.caretPosition = 4;

            _ime.Composition = "요";
            field.UpdateEditing();

            // The composition ends and the field takes the commit itself.
            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.Y));
            field.ProcessKeyEvent(Key(KeyCode.None, '요'));
            Assert.AreEqual("안녕하세요", field.text);

            field.DeactivateInputField();
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // One press of ㅇ, and the platform answers with the syllable it
            // already gave us plus that consonant.
            _ime.Composition = "용";
            field.UpdateEditing();
            field.UpdateEditing();

            // Which it then splits, handing 요 back a second time.
            _ime.Composition = "아";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '요'));

            Assert.AreEqual("안녕하세요", field.text,
                "the 요 the platform carried on is the one already in the value");

            Destroy(field);
        }

        [Test]
        public void A_Commit_The_Field_Took_For_The_Platform_Is_Not_Taken_Again()
        {
            // Read frame by frame off a recording of 안녕 and a click. The
            // composition ends, the field arms the window for the 녕 the
            // platform announced, and the click ends the session in the same
            // frame — before the character is drained — so the field inserts
            // 녕 itself. A thousand frames later the platform delivers it for
            // the first time, and for four milestones there was nothing
            // anywhere that had heard of it: 안녕녕.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            field.text = "안";
            field.caretPosition = 1;

            _ime.Composition = "녕";
            field.UpdateEditing();

            _ime.Composition = string.Empty;
            field.UpdateEditing();
            field.DeactivateInputField();
            Assert.AreEqual("안녕", field.text, "the field inserted what the platform had not sent");

            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            field.UpdateEditing();

            // The first keystroke back: the delivery, alongside the composition
            // for the syllable being typed now.
            _ime.Composition = "ㅇ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '녕'));

            Assert.AreEqual("안녕", field.text,
                "the field had already taken that syllable on the platform's behalf");
            Assert.IsTrue(field.isComposing, "and the one being typed now is untouched");

            Destroy(field);
        }

        [Test]
        public void A_Commit_The_Platform_Makes_Twice_Is_Only_Taken_Once()
        {
            // Recorded on macOS: 안녕, click away, click back, and 녕 arrives a
            // second time on the first keystroke of what the user types next —
            // 안녕녕. Every syllable here is committed the way a Hangul IME
            // commits: the character arrives while the composition is still
            // being reported, which is the door that armed nothing.
            var field = CreateField(out _);
            field.ActivateInputField();
            _ime.KeepsComposingAfterEnd = true; // the poll of the platform

            _ime.Composition = "안";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '안'));
            Assert.AreEqual("안", field.text);

            _ime.Composition = "녕";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '녕'));
            Assert.AreEqual("안녕", field.text);

            field.DeactivateInputField();
            field.ActivateInputField();
            field.caretPosition = field.text.Length;

            // The first keystroke back. The platform replays the syllable it
            // already delivered, in the same update as the composition for the
            // one the user is actually typing now.
            _ime.Composition = "ㅇ";
            field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '녕'));

            Assert.AreEqual("안녕", field.text,
                "the platform said 녕 twice and the field took it once");
            Assert.IsTrue(field.isComposing, "and the syllable being typed now is untouched");

            Destroy(field);
        }

        [Test]
        public void A_Platform_That_Went_Quiet_Still_Has_Its_Echo_Swallowed()
        {
            // The commit the field made on its way out of focus, and a platform
            // that lets go of the composition without delivering the syllable
            // until the user comes back and types. Nothing between those two
            // moments is evidence of anything, and a guard that counted updates
            // through them came down before the echo it was there to swallow:
            // 안녕, click away, click back, and the field reads 안녕녕.
            var field = CreateField(out _);
            field.ActivateInputField();
            field.text = "안";
            field.caretPosition = 1;

            _ime.KeepsComposingAfterEnd = true; // the poll of the platform
            _ime.Composition = "녕";
            field.UpdateEditing();

            field.DeactivateInputField();
            Assert.AreEqual("안녕", field.text, "the field committed what nobody else would");

            // The platform is reporting nothing now — it did let go — and the
            // syllable it owes arrives with the first keystroke after the user
            // clicks back in.
            _ime.Composition = string.Empty;
            field.ActivateInputField();
            field.caretPosition = field.text.Length;
            for (int update = 0; update < 6; update++) field.UpdateEditing();
            field.ProcessKeyEvent(Key(KeyCode.None, '녕'));

            Assert.AreEqual("안녕", field.text,
                "the platform's copy of a syllable the field already committed is an echo, however late");

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
