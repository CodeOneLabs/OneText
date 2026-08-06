using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The typewriter: what one step is, how fast, where it pauses, and what it
    /// tells anyone listening.
    ///
    /// Almost every assertion here is about a script that is not English, which
    /// is the point. A grapheme-stepped reveal is already better than the
    /// character-stepped ones every other Unity text asset ships, and it is
    /// still wrong for the scripts this project exists for: a Thai leading
    /// vowel is stored before the consonant it is pronounced after, a Khmer
    /// subscript is a separate cluster from the coeng that introduces it, and a
    /// Japanese 。 is not a character anyone would put a typing sound on.
    ///
    /// Nothing here needs a font that covers the text. Segmentation, the unit
    /// table and the callbacks are all decided from the text and the shaper's
    /// cluster values, so the assertions hold whether the glyph came out of a
    /// Thai font or came out as .notdef, which is also why they are a
    /// regression test rather than a rendering opinion.
    /// </summary>
    public class TypewriterTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        /// <summary>한글 as jamo: six code points, two syllable blocks.</summary>
        private const string JamoHangeul = "\u1112\u1161\u11AB\u1100\u1173\u11AF";

        /// <summary>Man + woman + girl, joined: eight UTF-16 units, one picture.</summary>
        private const string ZwjFamily =
            "\U0001F468\u200D\U0001F469\u200D\U0001F467";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private OneTextLabel NewLabel(string text)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(1600f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.Text = text;
            label.FontSize = 32f;
            label.Wrap = TextWrap.NoWrap;
            return label;
        }

        private static int UnitsAt(OneTextLabel label, RevealGranularity granularity)
        {
            label.RevealGranularity = granularity;
            return label.RevealUnitCount;
        }

        private static int DrawnQuads(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            var mesh = label.canvasRenderer.GetMesh();
            return mesh == null ? 0 : mesh.vertexCount / 4;
        }

        // ------------------------------------------------------- granularity

        [Test]
        public void Granularity_Korean_StepsOncePerSyllable()
        {
            // Written as jamo: three code points a reader calls one syllable.
            // The grapheme rules (GB6-GB8) already join these, so all three
            // modes must agree: whatever else changes, Korean does not get a
            // third of a syllable per step under any of them.
            var label = NewLabel(JamoHangeul);
            Assert.AreEqual(6, label.Text.Length);
            Assert.AreEqual(2, label.GraphemeCount, "two syllable blocks, six code points");

            Assert.AreEqual(2, UnitsAt(label, RevealGranularity.Grapheme));
            Assert.AreEqual(2, UnitsAt(label, RevealGranularity.Cluster));
            Assert.AreEqual(2, UnitsAt(label, RevealGranularity.Syllable));

            var precomposed = NewLabel("한국어");
            Assert.AreEqual(3, UnitsAt(precomposed, RevealGranularity.Syllable));
        }

        [Test]
        public void Granularity_Thai_DoesNotSplitACluster()
        {
            // แสง: a leading vowel, then two consonants. The vowel is a whole
            // letter by every Unicode property and is written to the LEFT of
            // the consonant it belongs to, so a grapheme-stepped reveal shows a
            // floating แ and then draws nothing on the tick that completes it.
            var label = NewLabel("แสง");
            Assert.AreEqual(3, label.GraphemeCount, "three graphemes, whatever a reader sees");

            Assert.AreEqual(3, UnitsAt(label, RevealGranularity.Grapheme));
            Assert.AreEqual(2, UnitsAt(label, RevealGranularity.Cluster),
                "the leading vowel must arrive with its consonant");

            // No unit may begin at a consonant whose leading vowel is the
            // character before it (stated over a whole sentence, so this keeps
            // holding if the rule is ever rewritten).
            var sentence = NewLabel("ไปโรงเรียนแล้ว");
            sentence.RevealGranularity = RevealGranularity.Cluster;
            for (int u = 1; u < sentence.RevealUnitCount; u++)
            {
                int at = sentence.LayoutResult.GraphemeStarts[sentence.GraphemeOfRevealUnit(u)];
                Assert.IsFalse(RevealUnits.AttachesToNext(sentence.DisplayText[at - 1]),
                    $"unit {u} starts straight after a Thai leading vowel, splitting its cluster");
            }
            Assert.Greater(sentence.RevealUnitCount, 1, "the sentence must have several units");
            Assert.Less(sentence.RevealUnitCount, sentence.GraphemeCount,
                "at least one cluster in this sentence must have merged, or nothing is proven");
        }

        [Test]
        public void Granularity_Cluster_KeepsAStackedConsonantWhole()
        {
            // Khmer ក្ម and Myanmar က္မ: an invisible stacker followed by the
            // subscript consonant it exists to introduce. The assertion is
            // about the unit and deliberately not about how it got there;
            // UAX #29's conjunct rule already joins some of these, and the
            // engine is right to. Either way, a step that shows a bare stacker
            // has drawn a mark with nothing under it.
            foreach (var stack in new[] { "ក្ម", "က္မ" })
            {
                var label = NewLabel(stack);
                Assert.AreEqual(1, UnitsAt(label, RevealGranularity.Cluster),
                    $"{stack} must be one reveal unit");
            }
        }

        [Test]
        public void Granularity_Syllable_RefusesToStartOnAMarkThatCannotStartOne()
        {
            // In がっこう。はい, the sokuon and the full stop are graphemes and
            // are not steps: nobody puts a typing sound on 。
            var label = NewLabel("がっこう。はい");
            Assert.AreEqual(7, label.GraphemeCount);
            Assert.AreEqual(7, UnitsAt(label, RevealGranularity.Grapheme));
            Assert.AreEqual(5, UnitsAt(label, RevealGranularity.Syllable),
                "っ joins が and 。 joins う");
        }

        [Test]
        public void Granularity_Default_IsExactlyTheOldBehaviour()
        {
            var label = NewLabel("Hamburgefonstiv");
            Assert.AreEqual(RevealGranularity.Grapheme, label.RevealGranularity);
            Assert.AreEqual(0f, label.CharactersPerSecond, "the typewriter must be off by default");
            Assert.IsEmpty(label.PunctuationDelays, "the delay table must be empty by default");
            Assert.AreEqual(label.GraphemeCount, label.RevealUnitCount);
            Assert.AreEqual(-1, label.MaxVisibleGraphemes);

            // And with the typewriter off, advancing it is not a thing.
            label.AdvanceReveal(10f);
            Assert.AreEqual(-1, label.MaxVisibleGraphemes);
        }

        // ---------------------------------------------------------- callbacks

        [Test]
        public void CharacterRevealed_FiresOncePerUnit_NotPerCodeUnit()
        {
            var label = NewLabel(JamoHangeul);
            var fired = new List<int>();
            label.CharacterRevealed.AddListener(fired.Add);

            label.MaxVisibleGraphemes = 0;
            fired.Clear();
            label.MaxVisibleGraphemes = label.GraphemeCount;

            CollectionAssert.AreEqual(new[] { 0, 1 }, fired,
                "two syllables, two sounds; not six, and not three per syllable");
        }

        [Test]
        public void CharacterRevealed_FiresOnceForAZwjSequence()
        {
            // Man + ZWJ + woman + ZWJ + girl: eight UTF-16 units, one picture.
            var label = NewLabel(ZwjFamily);
            Assert.AreEqual(8, label.Text.Length);
            Assert.AreEqual(1, label.GraphemeCount, "GB11 makes the family one cluster");
            Assert.AreEqual(1, label.RevealUnitCount);

            int calls = 0;
            label.MaxVisibleGraphemes = 0;
            label.CharacterRevealed.AddListener(_ => calls++);
            label.MaxVisibleGraphemes = 1;
            Assert.AreEqual(1, calls, "one family, one sound");
        }

        [Test]
        public void CharacterRevealed_ReportsHalfARevealedUnitAsNotYetRevealed()
        {
            // Under Cluster granularity แส is one unit made of two graphemes.
            // Firing when the first arrives is firing before the reader has
            // been shown anything, because the tile waits for both.
            var label = NewLabel("แสง");
            label.RevealGranularity = RevealGranularity.Cluster;
            label.MaxVisibleGraphemes = 0;

            int calls = 0;
            label.CharacterRevealed.AddListener(_ => calls++);
            label.MaxVisibleGraphemes = 1;
            Assert.AreEqual(0, calls, "half a cluster is not a revealed unit");
            label.MaxVisibleGraphemes = 2;
            Assert.AreEqual(1, calls);
        }

        // ---------------------------------------------------------- the clock

        [Test]
        public void CharactersPerSecond_AdvancesTheRevealItself()
        {
            var label = NewLabel("Hamburgefonstiv");
            label.CharactersPerSecond = 10f;
            label.RestartReveal();
            Assert.AreEqual(0, label.RevealedUnits);

            label.AdvanceReveal(0.55f);
            Assert.AreEqual(5, label.RevealedUnits, "ten a second for just over half a second");

            label.AdvanceReveal(100f);
            Assert.AreEqual(label.RevealUnitCount, label.RevealedUnits);
        }

        [Test]
        public void CharactersPerSecond_DrivenByHand_StillWorksTheOldWay()
        {
            // The path everything written against M8 uses. Turning the label's
            // own typewriter off must leave it exactly as it was.
            var label = NewLabel("Hamburgefonstiv");
            label.MaxVisibleGraphemes = 4;
            label.AdvanceReveal(10f);
            Assert.AreEqual(4, label.MaxVisibleGraphemes, "an off typewriter must not touch it");
        }

        [Test]
        public void PunctuationDelay_ActuallyDelays()
        {
            const string line = "ab.cd";
            var quick = NewLabel(line);
            quick.CharactersPerSecond = 10f;
            quick.RestartReveal();

            var slow = NewLabel(line);
            slow.CharactersPerSecond = 10f;
            slow.PunctuationDelays.Add(new PunctuationDelay(".", 1f));
            slow.RestartReveal();

            // Enough for a, b and the full stop, and then one more step.
            quick.AdvanceReveal(0.45f);
            slow.AdvanceReveal(0.45f);
            Assert.AreEqual(4, quick.RevealedUnits, "with no delay the fourth unit is due");
            Assert.AreEqual(3, slow.RevealedUnits, "the full stop must hold the next unit back");

            // And it resumes rather than stalling.
            slow.AdvanceReveal(1f);
            Assert.AreEqual(4, slow.RevealedUnits);
            slow.AdvanceReveal(1f);
            Assert.AreEqual(slow.RevealUnitCount, slow.RevealedUnits);
        }

        [Test]
        public void Reveal_ScrubbedMidPause_DoesNotCarryThePauseWithIt()
        {
            var label = NewLabel("ab.cd");
            label.CharactersPerSecond = 10f;
            label.PunctuationDelays.Add(new PunctuationDelay(".", 5f));
            label.RestartReveal();
            label.AdvanceReveal(0.35f);
            Assert.AreEqual(3, label.RevealedUnits, "and now five seconds are owed");

            // A cutscene jumping to a known point. Those five seconds belonged
            // to where the reveal WAS; charging them at the new position is a
            // label that mysteriously stops typing.
            label.MaxVisibleGraphemes = label.GraphemeOfRevealUnit(1);
            label.AdvanceReveal(0.15f);
            Assert.AreEqual(2, label.RevealedUnits, "a scrub must not inherit the old pause");
            label.AdvanceReveal(0.15f);
            Assert.AreEqual(3, label.RevealedUnits);
        }

        [Test]
        public void PunctuationDelay_ReachesCjkAndThai()
        {
            var table = new List<PunctuationDelay>();
            PunctuationDelays.Recommended(table);

            foreach (char c in "、。！？…")
                Assert.IsTrue(Covers(table, c), $"the recommended table must cover {c}");
            foreach (char c in "ฯๆ")
                Assert.IsTrue(Covers(table, c), $"the recommended table must cover Thai {c}");
            foreach (char c in "।។؟")
                Assert.IsTrue(Covers(table, c),
                    $"a table that only knows ASCII and CJK is not a table for this market ({c})");
        }

        private static bool Covers(List<PunctuationDelay> table, char c)
        {
            foreach (var row in table)
                if (!string.IsNullOrEmpty(row.Characters) && row.Characters.IndexOf(c) >= 0)
                    return row.Seconds > 0f;
            return false;
        }

        // -------------------------------------------------------------- <wait>

        [Test]
        public void Wait_ParsesToAPauseAndNoText()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("ab<wait=0.5>cd", result);

            Assert.AreEqual("abcd", result.Text, "a pause writes nothing");
            Assert.AreEqual(1, result.Waits.Count);
            Assert.AreEqual(2, result.Waits[0].Index);
            Assert.AreEqual(0.5f, result.Waits[0].Seconds, 0.0001f);
        }

        [Test]
        public void Wait_MalformedStaysLiteral()
        {
            // The parser's whole rule: a tag that is not well formed is text.
            // Text that silently disappears is the worst failure a text engine
            // has, and <wait> is the tag most likely to be mistyped, because it
            // is the one a writer types most often.
            foreach (var bad in new[] { "<wait>", "<wait=soon>", "<wait=-1>", "<wait=>" })
            {
                var result = new RichTextResult();
                RichTextParser.Parse("a" + bad + "b", result);
                Assert.AreEqual("a" + bad + "b", result.Text, $"{bad} must stay visible");
                Assert.IsEmpty(result.Waits);
            }
        }

        [Test]
        public void Wait_PausesTheRevealAndResumes()
        {
            var label = NewLabel("ab<wait=1>cd");
            Assert.AreEqual("abcd", label.DisplayText);
            label.CharactersPerSecond = 10f;
            label.RestartReveal();

            label.AdvanceReveal(0.45f);
            Assert.AreEqual(2, label.RevealedUnits, "the pause must stop the reveal at the tag");

            label.AdvanceReveal(0.4f);
            Assert.AreEqual(2, label.RevealedUnits, "still inside the second it asked for");

            label.AdvanceReveal(0.5f);
            Assert.AreEqual(3, label.RevealedUnits, "and then it carries on");
            label.AdvanceReveal(1f);
            Assert.AreEqual(4, label.RevealedUnits);
        }

        [Test]
        public void Wait_AtTheStartHoldsTheWholeLine()
        {
            var label = NewLabel("<wait=1>abc");
            label.CharactersPerSecond = 10f;
            label.RestartReveal();

            label.AdvanceReveal(0.5f);
            Assert.AreEqual(0, label.RevealedUnits);
            label.AdvanceReveal(0.7f);
            Assert.Greater(label.RevealedUnits, 0, "and the line starts once it is over");
        }

        // ------------------------------------------------------------- skipping

        [Test]
        public void SkipToEnd_CompletesWithoutABurstOfCallbacks()
        {
            var label = NewLabel("Hamburgefonstiv");
            int all = DrawnQuads(label);
            Assert.Greater(all, 0);

            label.CharactersPerSecond = 10f;
            label.RestartReveal();
            label.AdvanceReveal(0.25f);
            Assert.Less(DrawnQuads(label), all, "the reveal has to be partway for this to prove anything");

            int characters = 0, graphemes = 0, complete = 0;
            label.CharacterRevealed.AddListener(_ => characters++);
            label.GraphemeRevealed.AddListener(_ => graphemes++);
            label.RevealComplete.AddListener(() => complete++);

            label.SkipToEnd();

            Assert.AreEqual(label.RevealUnitCount, label.RevealedUnits, "skip must complete the reveal");
            Assert.AreEqual(0, characters, "a skip is one decision, not two hundred typing sounds");
            Assert.AreEqual(0, graphemes);
            Assert.AreEqual(1, complete, "and it has to say so exactly once");

            label.SkipToEnd();
            Assert.AreEqual(1, complete, "skipping an already-skipped label says nothing new");

            Assert.AreEqual(all, DrawnQuads(label), "everything must be on screen");
        }

        [Test]
        public void RevealComplete_FiresOnTheHandDrivenPathAndRearms()
        {
            var label = NewLabel("Hamburgefonstiv");
            int complete = 0;
            label.RevealComplete.AddListener(() => complete++);

            label.MaxVisibleGraphemes = 0;
            Assert.AreEqual(0, complete);
            label.MaxVisibleGraphemes = label.GraphemeCount;
            Assert.AreEqual(1, complete);

            // Rewound, so the next finish is a new finish; a pooled label
            // retyping a second line must fire it again.
            label.MaxVisibleGraphemes = 2;
            label.MaxVisibleGraphemes = label.GraphemeCount;
            Assert.AreEqual(2, complete);
        }

        [Test]
        public void Reveal_ScrubsBackwards()
        {
            var label = NewLabel("Hamburgefonstiv");
            label.RevealGranularity = RevealGranularity.Syllable;
            int units = label.RevealUnitCount;

            label.MaxVisibleGraphemes = label.GraphemeCount;
            Assert.AreEqual(units, label.RevealedUnits);
            label.MaxVisibleGraphemes = 3;
            Assert.AreEqual(3, label.RevealedUnits, "scrubbing back must move the unit count back");
            label.MaxVisibleGraphemes = 0;
            Assert.AreEqual(0, label.RevealedUnits);
            Assert.AreEqual(0, DrawnQuads(label), "and must actually hide the text again");
        }

        // -------------------------------------------------------------- editor

        [Test]
        public void EditorPreview_ShowsEverything_UntilSomethingDrivesTheReveal()
        {
            // A label typing at runtime has no clock in the Scene view, so its
            // serialized reveal is wherever the last play session left it. A
            // designer shown a blank label cannot tell that from a broken font.
            var label = NewLabel("Hamburgefonstiv");
            int all = DrawnQuads(label);
            Assert.Greater(all, 0);

            label.CharactersPerSecond = 10f;
            Assert.AreEqual(all, DrawnQuads(label),
                "a typing label with no clock must preview fully revealed");

            // But an explicit reveal is an explicit statement, and is obeyed.
            label.RestartReveal();
            Assert.AreEqual(0, DrawnQuads(label));
        }

        [Test]
        public void Reveal_StopsBeingWork_WhenItFinishes()
        {
            // The other half of "a finished label lets the clock stop": once
            // the last unit is out, the reveal must report itself done rather
            // than sitting one step short for ever.
            var label = NewLabel("Hamburgefonstiv");
            label.CharactersPerSecond = 50f;
            label.RestartReveal();
            label.AdvanceReveal(10f);

            Assert.AreEqual(label.GraphemeCount, label.MaxVisibleGraphemes,
                "a finished typewriter must land exactly on the end");
            int at = label.MaxVisibleGraphemes;
            label.AdvanceReveal(10f);
            Assert.AreEqual(at, label.MaxVisibleGraphemes, "and must then do nothing at all");
        }
    }
}
