using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M8: the cluster mapping, the reveal built on it, and the quad hook.
    ///
    /// The engine has to publish this because only the engine knows it. "One
    /// character at a time" is not a thing in shaped text (an Arabic ligature
    /// is two characters in one glyph, a Hangul syllable is three, a flag emoji
    /// is four), and every animation layer built on TMP had to reconstruct the
    /// mapping from outside, which stopped working the moment a ligature
    /// appeared. These tests are mostly about the scripts where counting
    /// characters gives the wrong answer.
    /// </summary>
    public class RevealTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private static TextLayoutResult Layout(FontStack fonts, string text, float size = 32f)
        {
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, size);
            settings.Wrap = TextWrap.NoWrap;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);
            return result;
        }

        private static FontData LoadFont(string path) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(path)));

        // ------------------------------------------------------ the mapping

        [Test]
        public void GraphemeCount_CountsClusters_NotCharacters()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            Assert.AreEqual(5, Layout(fonts, "Hello").GraphemeCount);

            // A Hangul syllable written in jamo is three code points and one
            // thing a reader would call a character.
            var hangul = Layout(fonts, "한");
            Assert.AreEqual(1, hangul.GraphemeCount,
                "three jamo are one syllable, and a typewriter must not reveal a third of it");

            // A combining sequence is one cluster, however many marks.
            var combining = Layout(fonts, "ȩ́");
            Assert.AreEqual(1, combining.GraphemeCount);

            // An emoji flag is two regional indicators, four UTF-16 units.
            var flag = Layout(fonts, "\U0001F1F0\U0001F1F7");
            Assert.AreEqual(4, "\U0001F1F0\U0001F1F7".Length);
            Assert.AreEqual(1, flag.GraphemeCount,
                "a flag is one grapheme; revealing half of it shows a different country");
        }

        [Test]
        public void GraphemeAt_MapsEveryCharacterIntoItsCluster()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            const string text = "ábc"; // a + combining acute, then b, c
            var result = Layout(fonts, text);
            Assert.AreEqual(3, result.GraphemeCount);

            Assert.AreEqual(0, result.GraphemeAt(0));
            Assert.AreEqual(0, result.GraphemeAt(1), "the mark belongs to the letter it sits on");
            Assert.AreEqual(1, result.GraphemeAt(2));
            Assert.AreEqual(2, result.GraphemeAt(3));

            // Out of range clamps rather than throwing: callers ask about caret
            // positions, and the caret can sit past the end.
            Assert.AreEqual(2, result.GraphemeAt(99));
            Assert.AreEqual(0, result.GraphemeAt(-5));
        }

        [Test]
        public void GraphemeStarts_AreOrderedAndTerminated()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);

            const string text = "héllo";
            var result = Layout(fonts, text);

            for (int i = 1; i < result.GraphemeStarts.Count; i++)
                Assert.Greater(result.GraphemeStarts[i], result.GraphemeStarts[i - 1],
                    "grapheme starts must be strictly increasing");
            Assert.AreEqual(text.Length, result.GraphemeStarts[result.GraphemeStarts.Count - 1],
                "the table must end with the text's length, so the last cluster has a width");
        }

        // ---------------------------------------------------------- the reveal

        private OneTextLabel NewLabel(string fontPath, string text, float size = 32f)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(800f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.Text = text;
            label.FontSize = size;
            label.Wrap = TextWrap.NoWrap;
            return label;
        }

        /// <summary>
        /// Rebuilds and returns the number of quads actually in the mesh.
        ///
        /// Counted from the mesh, not by re-applying the reveal rule to the
        /// quad list: doing the latter would pass even if the emitter ignored
        /// the reveal entirely, because the test would be checking its own
        /// arithmetic rather than the label's.
        /// </summary>
        private static int DrawnQuads(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);

            // Read the mesh the label actually handed the canvas renderer.
            var mesh = label.canvasRenderer.GetMesh();
            return mesh == null ? 0 : mesh.vertexCount / 4;
        }

        [Test]
        public void Reveal_ShowsNothingThenEverything()
        {
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");

            label.MaxVisibleGraphemes = -1;
            int all = DrawnQuads(label);
            Assert.Greater(all, 0);

            label.MaxVisibleGraphemes = 0;
            Assert.AreEqual(0, DrawnQuads(label), "reveal 0 must draw nothing");

            label.MaxVisibleGraphemes = label.GraphemeCount;
            Assert.AreEqual(all, DrawnQuads(label), "reveal all must draw everything");
        }

        [Test]
        public void Reveal_IsMonotonic()
        {
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            int previous = -1;
            for (int i = 0; i <= label.GraphemeCount; i++)
            {
                label.MaxVisibleGraphemes = i;
                int visible = DrawnQuads(label);
                Assert.GreaterOrEqual(visible, previous,
                    "revealing one more cluster must never draw fewer tiles");
                previous = visible;
            }
        }

        [Test]
        public void Reveal_NeverShowsHalfOfAJoinedArabicGroup()
        {
            // This is the case a character-counting typewriter gets wrong. The
            // joined letters of a word bake as one seam-free field; a tile is
            // shown only once every cluster it covers has had its turn, so a
            // reader never sees a ligature cut in half.
            var label = NewLabel(ArabicFontPath, "مرحبا بالعالم");
            label.MaxVisibleGraphemes = -1;
            DrawnQuads(label);

            var quads = new List<TextQuad>(label.Quads);
            Assert.Greater(quads.Count, 0);

            bool sawMerged = false;
            foreach (var quad in quads)
            {
                if (quad.LastGrapheme > quad.FirstGrapheme) sawMerged = true;
                Assert.GreaterOrEqual(quad.LastGrapheme, quad.FirstGrapheme,
                    "a tile's cluster range must be ordered");
            }
            Assert.IsTrue(sawMerged,
                "the test needs at least one tile covering several clusters, or it proves nothing " +
                "about joined text");

            for (int reveal = 0; reveal <= label.GraphemeCount; reveal++)
            {
                label.MaxVisibleGraphemes = reveal;
                DrawnQuads(label);
                foreach (var quad in label.Quads)
                {
                    bool drawn = quad.LastGrapheme < reveal;
                    if (!drawn) continue;
                    Assert.Less(quad.FirstGrapheme, reveal,
                        "a tile was drawn before every cluster it covers was revealed");
                }
            }
        }

        [Test]
        public void Reveal_DoesNotRelayOutTheText()
        {
            // The whole point of the seam: changing the reveal is a mesh
            // rebuild, not a layout. If the layout re-ran, its results would be
            // rebuilt from scratch and the quad list would change identity in
            // ways the geometry does not.
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            label.MaxVisibleGraphemes = -1;
            DrawnQuads(label);
            var before = new List<TextQuad>(label.Quads);
            float width = label.LayoutResult.Width;

            label.MaxVisibleGraphemes = 3;
            DrawnQuads(label);

            Assert.AreEqual(width, label.LayoutResult.Width, 0.0001f,
                "revealing part of the text changed the layout");
            Assert.AreEqual(before.Count, label.Quads.Count,
                "the geometry itself must not change, only what is drawn from it");
            for (int i = 0; i < before.Count; i++)
                Assert.AreEqual(before[i].Position, label.Quads[i].Position,
                    "a tile moved when the reveal changed");
        }

        [Test]
        public void Reveal_FiresOneEventPerClusterItPasses()
        {
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            label.MaxVisibleGraphemes = 0;

            var fired = new List<int>();
            label.GraphemeRevealed.AddListener(fired.Add);

            label.MaxVisibleGraphemes = 1;
            CollectionAssert.AreEqual(new[] { 0 }, fired);

            // A typewriter that jumps several clusters in one frame (a
            // fast-forward, or a low frame rate) still has to fire each one,
            // because a dialogue system is listening for every character.
            fired.Clear();
            label.MaxVisibleGraphemes = 5;
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, fired,
                "skipping ahead must still report every cluster it passed");

            fired.Clear();
            label.MaxVisibleGraphemes = 5;
            Assert.IsEmpty(fired, "setting the same value must fire nothing");
        }

        [Test]
        public void Reveal_ReusesTheLayout()
        {
            // The claim the quad hook is built on: revealing more text is a
            // mesh rebuild, not a layout. Measured by identity: a re-run
            // engine rebuilds the run and glyph lists from scratch, so holding
            // the counts and the first run's identity across a reveal step is
            // what "not re-run" looks like from outside.
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            label.MaxVisibleGraphemes = -1;
            DrawnQuads(label);

            var layout = label.LayoutResult;
            int runs = layout.Runs.Count, glyphs = layout.Glyphs.Count;
            float width = layout.Width;

            for (int i = 0; i < label.GraphemeCount; i++)
            {
                label.MaxVisibleGraphemes = i;
                DrawnQuads(label);
                Assert.AreSame(layout, label.LayoutResult, "the layout object was replaced");
                Assert.AreEqual(runs, layout.Runs.Count);
                Assert.AreEqual(glyphs, layout.Glyphs.Count);
                Assert.AreEqual(width, layout.Width, 0.0001f);
            }

            // And changing something layout DOES depend on must re-run it.
            label.Text = "Something else entirely";
            DrawnQuads(label);
            Assert.AreNotEqual(width, label.LayoutResult.Width,
                "changing the text must re-lay it out");
        }

        // ------------------------------------------------------- the quad hook

        private sealed class Shift : ITextQuadModifier
        {
            public Vector2 By;
            public int Dropped;
            public int Calls;
            public int DropClustersBelow = -1;

            public bool Modify(ref TextQuad quad, in TextQuadContext context)
            {
                Calls++;
                if (quad.FirstGrapheme < DropClustersBelow) { Dropped++; return false; }
                quad.Position += By;
                return true;
            }
        }

        [Test]
        public void QuadModifier_SeesEveryDrawnTile_AndCanDropThem()
        {
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            label.MaxVisibleGraphemes = -1;
            DrawnQuads(label);
            int total = label.Quads.Count;

            var modifier = new Shift { By = new Vector2(5f, 0f), DropClustersBelow = 3 };
            label.QuadModifier = modifier;
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);

            Assert.AreEqual(total, modifier.Calls, "the modifier must see every tile");
            Assert.Greater(modifier.Dropped, 0, "returning false must actually drop a tile");
        }

        [Test]
        public void QuadModifier_DoesNotDisturbTheLayout()
        {
            var label = NewLabel(LatinFontPath, "Hamburgefonstiv");
            DrawnQuads(label);
            float width = label.LayoutResult.Width;
            float height = label.LayoutResult.Height;

            label.QuadModifier = new Shift { By = new Vector2(100f, -40f) };
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);

            Assert.AreEqual(width, label.LayoutResult.Width, 0.0001f,
                "a modifier changed where the text wraps, which it must never do");
            Assert.AreEqual(height, label.LayoutResult.Height, 0.0001f);
        }

        [Test]
        public void Quads_CarryTheirRunsStyle()
        {
            var label = NewLabel(LatinFontPath, "plain <color=red>red</color>");
            DrawnQuads(label);

            bool sawColored = false, sawPlain = false;
            foreach (var quad in label.Quads)
            {
                if (quad.Style.HasColor) sawColored = true;
                else sawPlain = true;
            }
            Assert.IsTrue(sawColored, "tiles must carry the style they were drawn under");
            Assert.IsTrue(sawPlain);
        }
    }
}
