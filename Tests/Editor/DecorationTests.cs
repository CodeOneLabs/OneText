using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M14: text decorations. Outline, shadow (TMP's underlay) and glow.
    ///
    /// The interesting claim is not that a distance field can be thresholded
    /// twice; it is that doing so costs no material. In TextMesh Pro every one
    /// of these is a material preset, so a heading with an outline and the body
    /// text beneath it cannot batch, and a glyph that came from a fallback font
    /// silently loses the effect. So most of what is tested here is what did
    /// <em>not</em> happen: no second material, no extra vertex channel asked of
    /// the canvas, no divergence in the vertex layout between a decorated label
    /// and a plain one sitting in the same canvas.
    ///
    /// The rest is the parser, held to the same all-or-nothing rule as every
    /// other tag, and the packing, which is only correct if what comes back out
    /// of the mesh is what the markup asked for.
    /// </summary>
    public class DecorationTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ColorFontPath = "Packages/com.onetext.core/Tests/Fonts/ColorGlyphs.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            return go.GetComponent<Canvas>();
        }

        private OneTextLabel NewLabel(Canvas canvas, string text,
            string fontPath = LatinFontPath)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.FontSize = 48f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private OneTextLabel NewLabel(string text, string fontPath = LatinFontPath) =>
            NewLabel(NewCanvas(), text, fontPath);

        private static Mesh Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            return label.canvasRenderer.GetMesh();
        }

        // -------------------------------------------------------------- parsing

        [Test]
        public void DecorationTags_ParseIntoSpans()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("plain <outline>edged</outline> plain", result);

            Assert.AreEqual("plain edged plain", result.Text);
            Assert.AreEqual(1, result.Decorations.Count);
            Assert.AreEqual(6, result.Decorations[0].Start);
            Assert.AreEqual(11, result.Decorations[0].End);
            Assert.IsTrue(result.Decorations[0].Decoration.HasOutline);
            Assert.IsFalse(result.Decorations[0].Decoration.HasShadow);
        }

        [Test]
        public void DecorationArguments_RoundTrip()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("<shadow color=#204080ff x=0.75 y=-0.25 soft=0.5>x</shadow>", result);

            Assert.AreEqual(1, result.Decorations.Count);
            var shadow = result.Decorations[0].Decoration;
            Assert.AreEqual(new Color32(0x20, 0x40, 0x80, 0xff), shadow.ShadowColor);
            Assert.AreEqual(0.75f, shadow.ShadowOffset.x, 1e-5f);
            Assert.AreEqual(-0.25f, shadow.ShadowOffset.y, 1e-5f);
            Assert.AreEqual(0.5f, shadow.ShadowSoftness, 1e-5f);
        }

        [Test]
        public void BareArgument_IsTheColour()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("<glow=cyan>lit</glow>", result);
            Assert.AreEqual(new Color32(0, 255, 255, 255),
                result.Decorations[0].Decoration.GlowColor);
        }

        [Test]
        public void MalformedDecoration_StaysLiteral()
        {
            // The house rule: a tag that does not make sense is text. Drawing
            // the default instead would hide the typo, which is the failure the
            // whole parser is written against.
            var result = new RichTextResult();
            RichTextParser.Parse("<outline=chartreuse>x</outline>", result);
            Assert.AreEqual(0, result.Decorations.Count);
            StringAssert.Contains("<outline=chartreuse>", result.Text);

            result = new RichTextResult();
            RichTextParser.Parse("<glow x=0.5>x</glow>", result);
            Assert.AreEqual(0, result.Decorations.Count,
                "a glow has no x; reading it as a radius would be a guess");
        }

        [Test]
        public void NestedDecorations_Merge()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("<outline=red><shadow>both</shadow></outline>", result);

            var at = result.DecorationAt(0);
            Assert.IsTrue(at.HasOutline, "the outer outline was lost");
            Assert.IsTrue(at.HasShadow, "the inner shadow was lost");
            Assert.AreEqual(new Color32(255, 0, 0, 255), at.OutlineColor);
        }

        [Test]
        public void InnerTagWins_TheParts_ItSets()
        {
            // Spans are recorded when they open, not when they close, so
            // resolution runs outside-in. Recording on close reverses that and
            // hands the outer tag the last word, which is the bug this pins.
            var result = new RichTextResult();
            RichTextParser.Parse("<glow=red><glow=blue>x</glow></glow>", result);
            Assert.AreEqual(new Color32(0, 0, 255, 255), result.DecorationAt(0).GlowColor);
        }

        [Test]
        public void UnclosedDecoration_RunsToTheEnd()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("safe <shadow>rest of it", result);
            Assert.AreEqual(1, result.Decorations.Count);
            Assert.AreEqual(result.Text.Length, result.Decorations[0].End);
        }

        // ---------------------------------------------------------- the promise

        [Test]
        public void DecoratedAndPlainLabels_ShareOneMaterial()
        {
            var canvas = NewCanvas();
            var plain = NewLabel(canvas, "plain");
            var outlined = NewLabel(canvas, "<outline>edged</outline>");
            var shadowed = NewLabel(canvas, "<shadow>cast</shadow>");
            var glowing = NewLabel(canvas, "<glow>lit</glow>");

            foreach (var label in new[] { plain, outlined, shadowed, glowing }) Draw(label);

            var shared = SharedGlyphAtlas.Material;
            Assert.IsNotNull(shared);
            foreach (var label in new[] { plain, outlined, shadowed, glowing })
                Assert.AreSame(shared, label.materialForRendering,
                    "a decoration forked the material, which forks the batch: the one thing " +
                    "this design exists to avoid");
        }

        [Test]
        public void Decorations_AskTheCanvasForNoExtraChannels()
        {
            // Normal and Tangent stay off deliberately: turning them on costs
            // seven floats per vertex on every graphic in the canvas, Images and
            // other people's components included. A decoration that needed them
            // would be sending the whole project a bill for a drop shadow.
            var canvas = NewCanvas();
            Draw(NewLabel(canvas, "<outline><shadow><glow>everything</glow></shadow></outline>"));

            Assert.AreEqual(
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.TexCoord2 |
                AdditionalCanvasShaderChannels.TexCoord3,
                canvas.additionalShaderChannels);
        }

        [Test]
        public void MixedCanvas_KeepsOneVertexLayout()
        {
            // uGUI batches by material and by vertex layout. The materials are
            // checked above; this is the other half: a decorated label whose
            // mesh carried a channel the plain one did not would break the batch
            // just as thoroughly, and would do it invisibly.
            var canvas = NewCanvas();
            var plain = NewLabel(canvas, "plain");
            var decorated = NewLabel(canvas, "<outline><shadow>edged</shadow></outline>");

            var plainLayout = Draw(plain).GetVertexAttributes();
            var decoratedLayout = Draw(decorated).GetVertexAttributes();

            Assert.AreEqual(plainLayout.Length, decoratedLayout.Length,
                "the decorated mesh has a different number of vertex streams");
            for (int i = 0; i < plainLayout.Length; i++)
            {
                Assert.AreEqual(plainLayout[i].attribute, decoratedLayout[i].attribute);
                Assert.AreEqual(plainLayout[i].dimension, decoratedLayout[i].dimension);
                Assert.AreEqual(plainLayout[i].format, decoratedLayout[i].format);
            }
        }

        // --------------------------------------------------------------- packing

        [Test]
        public void OutlineReachesTheVertexChannels()
        {
            var label = NewLabel("<outline=#3366ccff w=0.5>edged</outline>");
            var mesh = Draw(label);

            var packed = new List<Vector4>();
            mesh.GetUVs(1, packed);
            Assert.Greater(packed.Count, 0, "TEXCOORD1 never reached the mesh");

            // Same unpacking the shader does, on the same bytes.
            Assert.AreEqual(new Vector2(0x33 / 255f, 0x66 / 255f), Unpack(packed[0].x));
            var blueAndWidth = Unpack(packed[0].y);
            Assert.AreEqual(0xcc / 255f, blueAndWidth.x, 1e-4f);
            Assert.AreEqual(0.5f, blueAndWidth.y, 1.5f / 255f);
        }

        [Test]
        public void ShadowOffset_SurvivesQuantization_AndZeroStaysZero()
        {
            var label = NewLabel("<shadow x=0 y=-0.5>cast</shadow>");
            var mesh = Draw(label);

            var packed = new List<Vector4>();
            mesh.GetUVs(3, packed);
            var offset = Unpack(packed[0].z);
            Assert.AreEqual(0f, Signed(offset.x), 1e-6f,
                "no horizontal offset has to mean none, not half a texel of one");
            Assert.AreEqual(-0.5f, Signed(offset.y), 1.5f / 127f);
        }

        [Test]
        public void PlainText_CarriesNoDecorationBytes()
        {
            var mesh = Draw(NewLabel("plain"));
            var colors = new List<Vector4>();
            var shape = new List<Vector4>();
            mesh.GetUVs(1, colors);
            mesh.GetUVs(3, shape);
            for (int i = 0; i < colors.Count; i++)
            {
                Assert.AreEqual(Vector4.zero, colors[i]);
                Assert.AreEqual(Vector4.zero, shape[i]);
            }
        }

        [Test]
        public void TileBounds_ReachTheChannelThatClampsTheShadowSample()
        {
            // Without the tile's u bounds the offset sample walks sideways out
            // of its tile and into whatever glyph the atlas shelf packed beside
            // it: a ghost of an unrelated letter, drawn as this one's shadow.
            var label = NewLabel("<shadow>cast</shadow>");
            var mesh = Draw(label);

            var bounds = new List<Vector4>();
            mesh.GetUVs(2, bounds);
            var quads = label.DrawnQuads;
            Assert.Greater(quads.Count, 0);
            for (int q = 0; q < quads.Count; q++)
            {
                Assert.AreEqual(quads[q].UvRect.xMin, bounds[q * 4].y, 1e-6f);
                Assert.AreEqual(quads[q].UvRect.xMax, bounds[q * 4].z, 1e-6f);
            }
        }

        // ------------------------------------------------------------- resolution

        [Test]
        public void StyleDecoration_AppliesWithoutAnyMarkup()
        {
            var style = ScriptableObject.CreateInstance<OneTextStyle>();
            _created.Add(style);
            style.SetDecoration(TextDecoration.DefaultGlow);

            var label = NewLabel("themed");
            label.Style = style;
            Draw(label);

            Assert.Greater(label.DrawnQuads.Count, 0);
            Assert.IsTrue(label.DecorationOf(label.DrawnQuads[0]).HasGlow,
                "a style is where a project sets its decoration defaults; markup is for spans");
        }

        [Test]
        public void MarkupWins_TheParts_TheStyleAlsoSets()
        {
            var style = ScriptableObject.CreateInstance<OneTextStyle>();
            _created.Add(style);
            style.SetDecoration(TextDecoration.DefaultShadow);

            var label = NewLabel("<outline=red>both</outline>");
            label.Style = style;
            Draw(label);

            var decoration = label.DecorationOf(label.DrawnQuads[0]);
            Assert.IsTrue(decoration.HasShadow, "the style's shadow was thrown away by a span");
            Assert.IsTrue(decoration.HasOutline);
            Assert.AreEqual(new Color32(255, 0, 0, 255), decoration.OutlineColor);
        }

        [Test]
        public void UndecoratedSpans_ShareSlotZero()
        {
            var label = NewLabel("plain <glow>lit</glow>");
            Draw(label);

            bool sawPlain = false, sawGlow = false;
            foreach (var quad in label.DrawnQuads)
            {
                if (quad.Decoration == 0) sawPlain = true;
                else sawGlow = true;
            }
            Assert.IsTrue(sawPlain, "text outside the tag picked up a decoration");
            Assert.IsTrue(sawGlow, "text inside the tag did not");
        }

        // ----------------------------------------------------------- colour tiles

        [Test]
        public void ColourGlyphs_AreLeftUndecorated()
        {
            // A colour tile is a picture, not a distance field: there is no
            // distance to threshold and the colour atlas has no padding ring an
            // offset sample could land in. Approximating a halo from an alpha
            // edge would draw something visibly worse than nothing, so the
            // channels are written empty and the shader never looks.
            // 'A' is the authored test face's colour glyph; 'D' is its
            // monochrome one, which is the point of drawing both: the
            // monochrome glyph in the same run keeps its shadow.
            var label = NewLabel("<shadow>AD</shadow>", ColorFontPath);
            var mesh = Draw(label);
            Assert.IsNotNull(mesh);

            var quads = label.DrawnQuads;
            Assert.Greater(quads.Count, 0, "the colour face drew nothing to check");

            var colors = new List<Vector4>();
            mesh.GetUVs(1, colors);
            bool sawColour = false, sawMono = false;
            for (int q = 0; q < quads.Count; q++)
            {
                Assert.IsTrue(label.DecorationOf(quads[q]).HasShadow,
                    "the tile should still know it is inside the span");
                if (quads[q].IsColor)
                {
                    sawColour = true;
                    Assert.AreEqual(Vector4.zero, colors[q * 4],
                        "a colour tile must not be handed decoration bytes it cannot read");
                }
                else
                {
                    sawMono = true;
                    Assert.AreNotEqual(Vector4.zero, colors[q * 4],
                        "a monochrome glyph beside an emoji is still a distance field, and " +
                        "still gets its shadow");
                }
            }
            Assert.IsTrue(sawColour && sawMono, "the run did not exercise both paths");
        }

        // Exactly what OneText-SDF.shader does, so a change to one that is not
        // a change to the other fails here rather than on somebody's screen.
        private static Vector2 Unpack(float packed)
        {
            float v = Mathf.Floor(packed + 0.5f);
            float hi = Mathf.Floor(v / 256f);
            return new Vector2(hi, v - hi * 256f) / 255f;
        }

        private static float Signed(float unit) => (unit * 255f - 128f) / 127f;
    }
}
