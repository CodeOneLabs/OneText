using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// What a <c>&lt;color&gt;</c> tag does under a label that has a colour of
    /// its own.
    ///
    /// Reported by a user: a red label with <c>&lt;color=blue&gt;</c> in it drew
    /// the tagged word black, because the label's colour was multiplied into the
    /// tag's, rgb and all. A tag is the more specific instruction, so it names
    /// the rgb, the way it does in TMP; the label still gives it its alpha, so
    /// fading a label fades its coloured words too. Untagged text is unchanged:
    /// it is the label's colour.
    /// </summary>
    public class TagColorTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 Blue = new Color32(0, 0, 255, 255);

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private OneTextLabel NewLabel(string text, Color32 color)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 48f;
            label.Wrap = TextWrap.NoWrap;
            label.color = color;
            label.Text = text;
            return label;
        }

        private static List<TextQuad> Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            return label.DrawnQuads.ToList();
        }

        // The run index is the honest way to tell tagged from untagged here:
        // "a<color>b</color>" lays out as two runs, and run 1 is the tag's.
        private static Color32 GlyphColorOfRun(List<TextQuad> quads, int run)
        {
            var glyphs = quads.Where(q => q.RunIndex == run && !q.IsSolid).ToList();
            Assert.Greater(glyphs.Count, 0, $"run {run} drew no glyph");
            return glyphs[0].Color;
        }

        [Test]
        public void ColorTag_InAColouredLabel_KeepsItsOwnRgb()
        {
            var quads = Draw(NewLabel("a<color=#0000FF>b</color>", Red));

            Assert.AreEqual(Red, GlyphColorOfRun(quads, 0),
                "untagged text should be the label's colour");
            Assert.AreEqual(Blue, GlyphColorOfRun(quads, 1),
                "the tag's rgb was multiplied by the label's (red times blue is black)");
        }

        [Test]
        public void ColorTag_TakesTheLabelsAlpha()
        {
            // The reason the label's colour reaches tagged text at all: a fade
            // has to take the coloured words with it.
            var quads = Draw(NewLabel("a<color=#0000FF>b</color>",
                new Color32(255, 0, 0, 128)));

            Assert.AreEqual(new Color32(255, 0, 0, 128), GlyphColorOfRun(quads, 0));
            Assert.AreEqual(new Color32(0, 0, 255, 128), GlyphColorOfRun(quads, 1),
                "fading the label must fade a tagged word, without tinting it");
        }

        [Test]
        public void TranslucentColorTag_MultipliesItsAlphaWithTheLabels()
        {
            var quads = Draw(NewLabel("a<color=#0000FF80>b</color>",
                new Color32(255, 0, 0, 128)));

            Assert.AreEqual(new Color32(0, 0, 255, 64), GlyphColorOfRun(quads, 1),
                "a half-transparent tag in a half-faded label is a quarter opaque");
        }

        [Test]
        public void AlphaTag_LeavesTheRgbToTheLabel()
        {
            // <alpha> names no colour, so the label's rgb is still the one in
            // force; only <color> takes it over.
            var quads = Draw(NewLabel("a<alpha=#80>b", Red));

            Assert.AreEqual(new Color32(255, 0, 0, 128), GlyphColorOfRun(quads, 1));
        }

        [Test]
        public void WhiteLabel_DrawsTheTagExactly()
        {
            // What almost every label is, and what the old multiply got right
            // by accident: it has to stay right.
            var quads = Draw(NewLabel("a<color=#3366CC>b</color>", Color.white));

            Assert.AreEqual(new Color32(0x33, 0x66, 0xCC, 255), GlyphColorOfRun(quads, 1));
        }

        [Test]
        public void Underline_FollowsTheTagUnderIt()
        {
            var quads = Draw(NewLabel("<u>a<color=#0000FF>b</color></u>", Red));

            var bars = quads.Where(q => q.IsSolid).ToList();
            Assert.IsTrue(bars.Any(q => q.RunIndex == 0 && q.Color.Equals(Red)),
                "the untagged underline should be the label's colour");
            Assert.IsTrue(bars.Any(q => q.RunIndex == 1 && q.Color.Equals(Blue)),
                "a tagged run's underline should be the tag's colour, not a product");
        }

        [Test]
        public void MarkWash_KeepsItsColour_UnderALabelColour()
        {
            // <mark=…> names its colour as plainly as <color=…> does, and
            // shares its run with any underline, so the wash has to say so
            // itself rather than be told apart by its style.
            var quads = Draw(NewLabel("<mark=#FFFF00FF><u>a</u></mark>", Red));

            var bars = quads.Where(q => q.IsSolid).ToList();
            Assert.IsTrue(bars.Any(q => q.Color.Equals(new Color32(255, 255, 0, 255))),
                "the mark wash was tinted by the label");
            Assert.IsTrue(bars.Any(q => q.Color.Equals(Red)),
                "the underline in the same run, untagged, should still be the label's");
        }

        private sealed class PaintGreen : ITextQuadModifier
        {
            public bool Owns;

            public bool Modify(ref TextQuad quad, in TextQuadContext context)
            {
                quad.Color = new Color32(0, 255, 0, 255);
                quad.OwnsColor = Owns;
                return true;
            }
        }

        [Test]
        public void Modifier_ChoosesWhetherTheLabelTintsItsColour()
        {
            var label = NewLabel("a", new Color32(255, 0, 0, 128));
            var modifier = new PaintGreen();
            label.QuadModifier = modifier;

            Assert.AreEqual(new Color32(0, 0, 0, 128), Draw(label)[0].Color,
                "a modifier's colour is tinted by the label unless it says otherwise");

            modifier.Owns = true;
            Assert.AreEqual(new Color32(0, 255, 0, 128), Draw(label)[0].Color,
                "OwnsColor should keep a modifier's rgb and still take the label's alpha");
        }

        [Test]
        public void Mesh_ColorTag_KeepsItsOwnRgb()
        {
            var go = new GameObject("WorldText", typeof(RectTransform));
            _created.Add(go);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(10f, 4f);
            var text = go.AddComponent<OneTextMesh>();
            text.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            text.FontSize = 10f;
            text.Color = new Color32(255, 0, 0, 128);
            text.Text = "a<color=#0000FF>b</color>";
            text.ForceRebuild();

            var colors = go.GetComponent<MeshFilter>().sharedMesh.colors32;
            Assert.IsTrue(colors.Any(c => c.Equals(new Color32(255, 0, 0, 128))),
                "untagged world text should be the component's colour");
            Assert.IsTrue(colors.Any(c => c.Equals(new Color32(0, 0, 255, 128))),
                "a tag in world text should keep its rgb and take the component's alpha");
            Assert.IsFalse(colors.Any(c => c.r == 0 && c.g == 0 && c.b == 0),
                "something drew black: the tag was multiplied by the component colour");
        }
    }
}
