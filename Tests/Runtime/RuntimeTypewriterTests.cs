using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// The typewriter driven by the thing it is actually driven by in a game:
    /// the player loop.
    ///
    /// The EditMode suite already proves what a step is — that a Thai leading
    /// vowel and its consonant are one step, that a ZWJ family is one, that a
    /// full stop is worth a pause — by calling AdvanceReveal with a made-up
    /// delta. What it cannot prove is that Update calls it at all, that the
    /// reveal survives a hundred frames without drifting or stalling, and that
    /// the completion event fires exactly once on the frame the last unit
    /// lands. Those are properties of the running component, and this is the
    /// only place they can be observed.
    ///
    /// <see cref="Time.captureDeltaTime"/> is what makes it deterministic: the
    /// engine advances its clock by a fixed step per frame regardless of how
    /// long the frame really took, so "sixty frames" is exactly one second of
    /// game time on a fast machine and on a loaded one.
    /// </summary>
    public class RuntimeTypewriterTests
    {
        private const float Step = 1f / 50f;

        private readonly PlayHarness _harness = new PlayHarness();

        [SetUp]
        public void Setup()
        {
            _harness.Setup();
            Time.captureDeltaTime = Step;
        }

        [TearDown]
        public void Teardown()
        {
            Time.captureDeltaTime = 0f;
            _harness.Teardown();
        }

        private OneTextLabel TypingLabel(string text, float charactersPerSecond)
        {
            var label = _harness.Label(text, 30f);
            label.CharactersPerSecond = charactersPerSecond;
            label.RestartReveal();
            return label;
        }

        [UnityTest]
        public IEnumerator The_Reveal_Advances_On_Its_Own_Over_Real_Frames()
        {
            // Half the frame rate: one unit every second frame, so a stall and
            // a runaway are both visible against the frame count.
            var label = TypingLabel("The quick brown fox jumps over the lazy dog.", 25f);
            yield return PlayHarness.Frame();

            Assert.Greater(label.RevealUnitCount, 20, "not enough units to measure a walk");

            int previous = label.RevealedUnits;
            for (int frame = 0; frame < 40; frame++)
            {
                yield return PlayHarness.Frame();
                Assert.GreaterOrEqual(label.RevealedUnits, previous,
                    $"the reveal went backwards on frame {frame}");
                previous = label.RevealedUnits;
            }

            // 40 frames at 1/50 s is 0.8 s; at 25 units a second that is 20
            // units, and the bounds are one frame's slack either way.
            Assert.That(label.RevealedUnits, Is.InRange(18, 22),
                "the reveal did not keep the pace it was given");
            Assert.Less(label.RevealedUnits, label.RevealUnitCount,
                "the whole line arrived before its time");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator The_Reveal_Runs_To_The_End_And_Says_So_Once()
        {
            var label = TypingLabel("Twelve units.", 200f);
            int completions = 0;
            label.RevealComplete.AddListener(() => completions++);

            var seen = new List<int>();
            label.CharacterRevealed.AddListener(unit => seen.Add(unit));

            int units = label.RevealUnitCount;
            Assert.Greater(units, 0);

            // Generous: at 200 units a second and a 1/50 s step this needs
            // four frames, and the budget is there so a stall fails as a stall
            // rather than as a timeout of unknown cause.
            for (int frame = 0; frame < 120 && label.RevealedUnits < units; frame++)
                yield return PlayHarness.Frame();

            Assert.AreEqual(units, label.RevealedUnits, "the reveal never finished");
            Assert.AreEqual(1, completions, "RevealComplete fired {0} times", completions);

            // One event per unit, in order, and none of them twice: this is
            // what a dialogue system hangs its typing sound on.
            CollectionAssert.AreEqual(new List<int>(Sequence(units)), seen,
                "the per-unit events did not report every unit exactly once, in order");

            yield return PlayHarness.Frames(10);
            Assert.AreEqual(1, completions, "a finished reveal kept announcing itself");
            PlayHarness.ExpectNoErrors();
        }

        /// <summary>0, 1, 2 ... count-1: the unit indices, in order.</summary>
        private static IEnumerable<int> Sequence(int count)
        {
            for (int i = 0; i < count; i++) yield return i;
        }

        [UnityTest]
        public IEnumerator Skipping_To_The_End_Mid_Walk_Completes_Without_A_Burst_Of_Events()
        {
            var label = TypingLabel("A line long enough to be worth skipping past.", 20f);
            int perUnitEvents = 0;
            label.CharacterRevealed.AddListener(_ => perUnitEvents++);
            bool completed = false;
            label.RevealComplete.AddListener(() => completed = true);

            yield return PlayHarness.Frames(10);
            int beforeSkip = perUnitEvents;
            Assert.Greater(beforeSkip, 0, "nothing had been revealed to skip from");

            label.SkipToEnd();
            yield return PlayHarness.Frames(3);

            Assert.IsTrue(completed, "skipping did not complete the reveal");
            Assert.AreEqual(beforeSkip, perUnitEvents,
                "a skip fired the whole rest of the line's typing sounds in one frame");
            Assert.Greater(PlayHarness.DrawnQuads(label), 0);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator New_Text_Rewinds_The_Typewriter_By_Itself()
        {
            var label = TypingLabel("The first line of dialogue.", 40f);
            yield return PlayHarness.Frames(15);
            Assert.Greater(label.RevealedUnits, 0);

            label.Text = "And then the second line, which starts from nothing.";
            yield return PlayHarness.Frame();

            Assert.LessOrEqual(label.RevealedUnits, 2,
                "new text kept the old line's reveal position");

            yield return PlayHarness.Frames(80);
            Assert.AreEqual(label.RevealUnitCount, label.RevealedUnits,
                "the second line never finished typing");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Typing_Label_Only_Rebuilds_While_It_Is_Typing()
        {
            var label = TypingLabel("Short line.", 100f);
            for (int frame = 0; frame < 60 && label.RevealedUnits < label.RevealUnitCount; frame++)
                yield return PlayHarness.Frame();
            Assert.AreEqual(label.RevealUnitCount, label.RevealedUnits);

            yield return PlayHarness.Frames(3);
            int quadBuilds = label.QuadBuilds;
            yield return PlayHarness.Frames(60);

            Assert.AreEqual(quadBuilds, label.QuadBuilds,
                "a finished typewriter kept rebuilding its mesh every frame");
            PlayHarness.ExpectNoErrors();
        }
    }
}
