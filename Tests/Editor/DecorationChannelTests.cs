using NUnit.Framework;
using OneText;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The vertex channels a decoration travels in, and the arithmetic on both
    /// ends of them.
    ///
    /// This is a contract with <c>OneText-SDF.shader</c> and there is no
    /// compiler between the two: the packing is C# and the unpacking is HLSL,
    /// and nothing but a test notices when one of them changes. So the shader's
    /// side is written out here in C# and asserted against the real packer. A
    /// number that survives this and not the GPU is a number the golden images
    /// catch; a number that fails here never reaches them.
    /// </summary>
    public sealed class DecorationChannelTests
    {
        /// <summary>The shader's <c>Unpack</c>, transcribed. Both bytes, normalised.</summary>
        private static Vector2 Unpack(float packed)
        {
            float v = Mathf.Floor(packed + 0.5f);
            float hi = Mathf.Floor(v * (1f / 256f));
            return new Vector2(hi, v - hi * 256f) * (1f / 255f);
        }

        [Test]
        public void TheLayerSurvivesTheByteItNowShares()
        {
            // Sixteen slices is the ceiling OneTextSettings clamps to, and the
            // layer is the one passenger here that must come back exact: a
            // layer read as 3.999 samples the wrong slice and every glyph on
            // that page is somebody else's.
            for (int layer = 0; layer <= 15; layer++)
            {
                for (float softness = 0f; softness <= 1f; softness += 0.1f)
                {
                    float packed = TextDecoration.Pack((byte)layer,
                        TextDecoration.Quantize(softness));
                    var got = Unpack(packed);

                    Assert.AreEqual(layer, Mathf.RoundToInt(got.x * 255f),
                        $"layer {layer} came back wrong beside a softness of {softness:0.0}");
                    Assert.AreEqual(softness, got.y, 1.5f / 255f,
                        $"softness {softness:0.0} came back wrong beside layer {layer}");
                }
            }
        }

        [Test]
        public void TheAtlasDiscriminatorSurvivesTheByteItNowShares()
        {
            // Four values, each meaning a different texture or none at all, and
            // the shader tells them apart with thresholds at 0.5, 1.5 and 2.5.
            for (int atlas = 0; atlas <= 3; atlas++)
            {
                foreach (float dilate in new[] { -1f, -0.3f, 0f, 0.11f, 0.25f, 1f })
                {
                    float packed = TextDecoration.Pack((byte)atlas,
                        TextDecoration.QuantizeSigned(dilate, 1f));
                    var got = Unpack(packed);

                    Assert.AreEqual(atlas, Mathf.RoundToInt(got.x * 255f),
                        $"atlas {atlas} came back wrong beside a dilate of {dilate}");
                    float back = (got.y * 255f - 128f) / 127f;
                    Assert.AreEqual(dilate, back, 1.5f / 127f,
                        $"dilate {dilate} came back wrong beside atlas {atlas}");
                }
            }
        }

        [Test]
        public void NoDilate_IsExactlyZero_NotNearlyZero()
        {
            // Every undecorated label in every project writes this byte. If it
            // came back at a thousandth rather than at nothing, every glyph in
            // the world would be a thousandth thicker than the font drew it —
            // which is the sort of thing nobody sees and everybody's golden
            // images fail on.
            float packed = TextDecoration.Pack(0, TextDecoration.QuantizeSigned(0f, 1f));
            float back = (Unpack(packed).y * 255f - 128f) / 127f;

            // Not exactly zero: the unpack divides by 255 and multiplies back,
            // and a float remembers that. The tolerance is what the threshold
            // moves by, not what the number is — 1e-5 of a reach is a shift of
            // five millionths of a texel, and the shader's own "is anything
            // decorated" test uses 0.002, three orders above this.
            Assert.AreEqual(0f, back, 1e-5f,
                "a face nothing spoke about is not being left alone");
        }

        [Test]
        public void TheGlowsInsideAndOutsideShareAByteWithoutTakingFromEachOther()
        {
            // The one place in this design where two parameters live in one
            // byte. Four bits each, so the tolerance is a sixteenth — and the
            // point of the assertion is that neither moves when the other does.
            foreach (float inner in new[] { 0f, 0.2f, 0.5f, 1f })
            {
                foreach (float outer in new[] { 0f, 0.33f, 0.75f, 1f })
                {
                    byte b = TextDecoration.PackNibbles(inner, outer);
                    float n = b;
                    float innerN = Mathf.Floor(n / 16f);
                    float gotInner = innerN / 15f;
                    float gotOuter = (n - innerN * 16f) / 15f;

                    Assert.AreEqual(inner, gotInner, 1f / 25f,
                        $"inner {inner} moved when outer was {outer}");
                    Assert.AreEqual(outer, gotOuter, 1f / 25f,
                        $"outer {outer} moved when inner was {inner}");
                }
            }
        }

        [Test]
        public void AGlowWithNoInside_ReadsAsTheOldSingleRadius()
        {
            // Backward compatibility, stated as an assertion because it is the
            // reason GlowRadius kept its name: a decoration serialized before
            // the glow had an inside has GlowInner 0, and the shader reads a
            // zero inner nibble as "does not fade going inward" — which is what
            // one radius always did.
            byte b = TextDecoration.PackNibbles(0f, 0.75f);
            Assert.AreEqual(0, b >> 4, "an unspoken inside is not landing on zero");
        }

        [Test]
        public void TwoDecorationsDifferingOnlyInTheNewFields_AreNotTheSameDecoration()
        {
            // ResolveDecoration dedupes by Equals before handing out a channel
            // index. A miss here does not draw wrong immediately — it draws the
            // *first* label's dilate on the second one, which is far harder to
            // see and impossible to explain.
            var a = TextDecoration.DefaultOutline;
            var b = a; b.OutlineSoftness = 0.5f;
            var c = a; c.Set |= TextDecoration.Parts.Face; c.FaceDilate = 0.25f;
            var d = TextDecoration.DefaultGlow;
            var e = d; e.GlowInner = 0.4f;

            Assert.AreNotEqual(a, b, "outline softness is not part of a decoration's identity");
            Assert.AreNotEqual(a, c, "face dilate is not part of a decoration's identity");
            Assert.AreNotEqual(d, e, "the glow's inside is not part of a decoration's identity");
        }

        [Test]
        public void ADecorationThatOnlyThickensTheFace_IsNotNothing()
        {
            // IsNone short-circuits the whole decoration path. A face-only
            // decoration read as nothing is a label that silently ignores the
            // one thing it was asked to do.
            var face = new TextDecoration
            {
                Set = TextDecoration.Parts.Face,
                FaceDilate = 0.25f,
            };
            Assert.IsFalse(face.IsNone);

            face.FaceDilate = 0f;
            Assert.IsTrue(face.IsNone, "a dilate of nothing is still nothing to draw");
        }

        [Test]
        public void OverKeepsTheNewFieldsWithTheirOwnPart()
        {
            var under = TextDecoration.DefaultOutline;
            var over = new TextDecoration
            {
                Set = TextDecoration.Parts.Face,
                FaceDilate = 0.4f,
            };

            var merged = over.Over(under);
            Assert.IsTrue(merged.HasOutline, "the outline underneath was lost");
            Assert.IsTrue(merged.HasFace);
            Assert.AreEqual(0.4f, merged.FaceDilate, 1e-5f);
        }
    }
}
