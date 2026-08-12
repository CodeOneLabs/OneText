using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// Dragging an axis, which is the one thing a variable-font demo does and
    /// the one shape of use these caches were not built for.
    ///
    /// A slider emits a new coordinate several times a second, and each one
    /// asks for a face that did not exist a frame ago. Two caches sit under
    /// that — the atlas, keyed by face, and the label's own stack — and both
    /// were wrong in the same direction: they answered a question about the new
    /// coordinate with a tile baked for the old one. On screen that is a weight
    /// slider that moves the advances and not the strokes, letters set at the
    /// ink box of a weight two drags ago, and, with enough of them, glyphs that
    /// grow and shrink as the handle moves.
    /// </summary>
    public class VariableSweepTests
    {
        private const string VariableFontPath =
            "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        private static readonly float[] Weights = { 100f, 300f, 500f, 700f, 900f };

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private static byte[] Bytes() => File.ReadAllBytes(Path.GetFullPath(VariableFontPath));

        [Test]
        public void AFaceDestroyedAndRemade_DoesNotInheritTheOldOnesTiles()
        {
            // Exactly what a label did per slider step before this was fixed:
            // load, vary, draw, destroy, load the next one. The allocator hands
            // back the address it just freed, so a cache keyed on the native
            // handle saw one face where there were five — and since a face
            // varied once always reads generation 1, every field of the key
            // matched too.
            var bytes = Bytes();
            var atlas = SharedGlyphAtlas.Atlas;
            var boxes = new List<float>();
            var tiles = new List<Rect>();

            foreach (float weight in Weights)
            {
                var font = FontData.Load(bytes);
                font.SetVariations(new FontVariation("wght", weight));
                var location = atlas.GetOrAdd(font, font.NominalGlyph('H'), 64f);
                boxes.Add(location.SizeUnits.x);
                tiles.Add(location.UvRect);
                font.Dispose();
            }

            CollectionAssert.AllItemsAreUnique(tiles,
                "two weights were served the same tile: the atlas key is aliasing " +
                "destroyed faces onto live ones");

            for (int i = 1; i < boxes.Count; i++)
            {
                Assert.Greater(boxes[i], boxes[i - 1],
                    $"wght {Weights[i]} baked no wider a stem than wght {Weights[i - 1]}, " +
                    "so it was handed the lighter weight's tile");
            }
        }

        [Test]
        public void ALabelSweptAlongAnAxis_RedrawsAtEveryStep()
        {
            // The user-visible half of the same bug. The advances come from the
            // live face and were always right; the tiles came from the cache
            // and were not, so the line changed width without changing weight.
            var canvas = NewCanvas();
            var label = NewLabel(canvas, Bytes(), "Handgloves");

            var ink = new List<float>();
            foreach (float weight in Weights)
            {
                label.SetVariations(new FontVariation("wght", weight));
                Draw(label);
                ink.Add(TileArea(label));
            }

            for (int i = 1; i < ink.Count; i++)
            {
                // Deliberately the tiles and not the advances. The advances
                // were never the broken half: they are read from the live face
                // every layout and moved on cue, which is why the failure read
                // as a line changing width while its strokes stayed put.
                Assert.Greater(ink[i], ink[i - 1],
                    $"wght {Weights[i]} drew the same ink as wght {Weights[i - 1]}: " +
                    "the advances moved and the tiles did not");
            }
        }

        [Test]
        public void SweepingAnAxis_DoesNotReparseTheFace()
        {
            // The face is re-varied where it stands rather than destroyed and
            // reloaded, which is what keeps a six-megabyte parse off the frame
            // a slider is being dragged in. Observable as identity: the label's
            // primary keeps the cache id it started with.
            var canvas = NewCanvas();
            var label = NewLabel(canvas, Bytes(), "Handgloves");
            label.SetVariations(new FontVariation("wght", Weights[0]));
            Draw(label);
            int first = PrimaryCacheId(label);

            foreach (float weight in Weights)
            {
                label.SetVariations(new FontVariation("wght", weight));
                Draw(label);
                Assert.AreEqual(first, PrimaryCacheId(label),
                    "the label threw its face away and parsed the file again");
            }
        }

        // ------------------------------------------------------------ helpers

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private OneTextLabel NewLabel(Canvas canvas, byte[] fontBytes, string text)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);
            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(fontBytes);
            label.FontSize = 64f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        /// <summary>
        /// Total area of the tiles the label drew. A tile is sized by the ink
        /// box the atlas baked, so this rises with weight and — unlike the
        /// advances — only if the tiles are the ones this coordinate asked for.
        /// </summary>
        private static float TileArea(OneTextLabel label)
        {
            Assert.Greater(label.DrawnQuads.Count, 0, "the label drew nothing");
            float area = 0f;
            foreach (var quad in label.DrawnQuads) area += quad.Size.x * quad.Size.y;
            return area;
        }

        private static int PrimaryCacheId(OneTextLabel label)
        {
            var stack = label.ResolvedFonts;
            Assert.IsNotNull(stack, "the label has no font stack");
            return stack.Primary.CacheId;
        }
    }
}
