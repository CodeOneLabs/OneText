using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace OneText.Tests
{
    /// <summary>
    /// World-space text through MeshFilter/MeshRenderer: no canvas anywhere in
    /// these tests, which is the point of the component.
    /// </summary>
    public class OneTextMeshTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static OneTextMesh Create(float width = 10f, float height = 4f)
        {
            var go = new GameObject("WorldText", typeof(RectTransform));
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
            var text = go.AddComponent<OneTextMesh>();
            text.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            text.FontSize = 10f; // points: one local unit per em (TMP scale)
            return text;
        }

        private static void Destroy(OneTextMesh text) =>
            Object.DestroyImmediate(text.gameObject);

        private static Mesh MeshOf(OneTextMesh text) =>
            text.GetComponent<MeshFilter>().sharedMesh;

        [Test]
        public void Builds_Vertices_Without_A_Canvas()
        {
            var text = Create();
            text.Text = "Hello";
            text.ForceRebuild();

            var mesh = MeshOf(text);
            Assert.Greater(mesh.vertexCount, 0, "five letters must produce quads");
            Assert.AreEqual(0, mesh.vertexCount % 4, "quads come four vertices at a time");
            Assert.Greater(mesh.triangles.Length, 0);
            Destroy(text);
        }

        [Test]
        public void Empty_Text_Builds_An_Empty_Mesh()
        {
            var text = Create();
            text.Text = "";
            text.ForceRebuild();

            Assert.AreEqual(0, MeshOf(text).vertexCount);
            Destroy(text);
        }

        [Test]
        public void The_Renderer_Gets_The_World_Material_With_Depth_Testing()
        {
            var text = Create();
            text.Text = "Hello";
            text.ForceRebuild();

            var material = text.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.IsNotNull(material, "the world material comes from the shared SDF shader");
            Assert.AreEqual(SharedGlyphAtlas.ShaderName, material.shader.name);
            Assert.AreEqual((float)CompareFunction.LessEqual,
                material.GetFloat("unity_GUIZTestMode"),
                "world text depth-tests like world geometry, not like an overlay");
            Assert.IsNotNull(material.GetTexture("_GlyphTex"), "the atlas is bound");
            Destroy(text);
        }

        [Test]
        public void Vertex_Channels_Follow_The_Shader_Contract()
        {
            var text = Create();
            text.Text = "Hello";
            text.ForceRebuild();

            var mesh = MeshOf(text);
            var uv0 = new List<Vector4>();
            var uv2 = new List<Vector4>();
            mesh.GetUVs(0, uv0);
            mesh.GetUVs(2, uv2);

            Assert.AreEqual(mesh.vertexCount, uv0.Count);
            Assert.AreEqual(mesh.vertexCount, uv2.Count);
            // Both of these channels are two bytes now, and the passenger this
            // test is about is the high one in each.
            for (int i = 0; i < uv0.Count; i++)
            {
                Assert.GreaterOrEqual(Mathf.RoundToInt(uv0[i].z) >> 8, 0,
                    "TEXCOORD0.z high byte is the atlas layer");
                Assert.AreEqual(0, Mathf.RoundToInt(uv2[i].w) >> 8,
                    "TEXCOORD2.w high byte picks the SDF atlas for plain text");
            }
            Destroy(text);
        }

        [Test]
        public void Precise_Rides_The_Msdf_Discriminator()
        {
            var text = Create();
            text.Text = "Hello";
            text.Precise = true;
            text.ForceRebuild();

            var uv2 = new List<Vector4>();
            MeshOf(text).GetUVs(2, uv2);
            Assert.Greater(uv2.Count, 0);
            foreach (var v in uv2)
                Assert.AreEqual(2, Mathf.RoundToInt(v.w) >> 8,
                    "TEXCOORD2.w high byte = 2 is the multi-channel atlas");
            Destroy(text);
        }

        [Test]
        public void Changing_The_Text_Changes_The_Mesh()
        {
            var text = Create();
            text.Text = "Hi";
            text.ForceRebuild();
            int before = MeshOf(text).vertexCount;

            text.Text = "A considerably longer line of text";
            text.ForceRebuild();
            Assert.Greater(MeshOf(text).vertexCount, before);
            Destroy(text);
        }

        [Test]
        public void Vertical_Writing_Builds()
        {
            var text = Create(4f, 10f);
            text.Text = "vertical";
            text.WritingMode = TextWritingMode.VerticalRightToLeft;
            text.ForceRebuild();

            // Latin in a column takes the rotated-run path; what matters here
            // is that the vertical frame emits quads at all.
            Assert.Greater(MeshOf(text).vertexCount, 0);
            Destroy(text);
        }

        [Test]
        public void A_Color_Tag_Tints_Its_Run()
        {
            var text = Create();
            text.Text = "<color=#ff0000>red</color> plain";
            text.ForceRebuild();

            var colors = new List<Color32>();
            MeshOf(text).GetColors(colors);
            Assert.Greater(colors.Count, 0);
            bool sawRed = false, sawWhite = false;
            foreach (var c in colors)
            {
                if (c.r == 255 && c.g == 0 && c.b == 0) sawRed = true;
                if (c.r == 255 && c.g == 255 && c.b == 255) sawWhite = true;
            }
            Assert.IsTrue(sawRed, "the tagged run is red");
            Assert.IsTrue(sawWhite, "the rest is untinted");
            Destroy(text);
        }

        [Test]
        public void Ten_Points_Are_One_Local_Unit()
        {
            var text = Create(20f, 5f);
            text.Text = "H";
            text.ForceRebuild();

            // TMP's world scale: size 10 is one unit per em, so a capital's
            // ink (~0.7 em plus the SDF spread) lands well inside [0.4, 1.2]
            // units. This is the contract that makes TMP values port verbatim.
            float height = MeshOf(text).bounds.size.y;
            Assert.Greater(height, 0.4f);
            Assert.Less(height, 1.2f);
            Destroy(text);
        }

        [Test]
        public void Markup_Sizes_Are_Points_Too()
        {
            var text = Create(40f, 10f);
            text.Text = "<size=20>big</size>";
            text.ForceRebuild();
            float tagged = MeshOf(text).bounds.size.y;

            text.Text = "big";
            text.ForceRebuild();
            float plain = MeshOf(text).bounds.size.y;

            Assert.Greater(tagged, plain * 1.5f,
                "a <size=20> run over a size-10 base is twice the height, " +
                "so the tag's number converted on the same scale as the base");
            Destroy(text);
        }

        [Test]
        public void Auto_Size_Fits_The_Rect()
        {
            var text = Create(4f, 1.2f);
            text.Text = "a line long enough that it cannot possibly fit at the maximum size";
            text.AutoSize = true;
            text.AutoSizeMin = 0.5f;  // points; 0.05 units
            text.AutoSizeMax = 30f;   // points; 3 units
            text.ForceRebuild();

            float fitted = text.FittedFontSize;
            Assert.Less(fitted, 30f);
            Assert.GreaterOrEqual(fitted, 0.5f);
            Assert.LessOrEqual(text.Layout.BlockExtent, 1.2f + 0.1f);
            Destroy(text);
        }

        [Test]
        public void Quality_Is_The_Multiplier_Its_Name_Implies()
        {
            // The values are the arithmetic: EmitTextRuns casts the enum to int
            // and multiplies. Renaming a member is free; renumbering one is a
            // silent change to every world text in every project.
            Assert.AreEqual(1, (int)TextQuality.Performance);
            Assert.AreEqual(2, (int)TextQuality.Medium);
            Assert.AreEqual(4, (int)TextQuality.High);
        }

        [Test]
        public void A_New_Mesh_Asks_For_Medium()
        {
            // World text is usually approached, and the point size cannot say
            // so: this default is the component's answer to that, so it is part
            // of the contract rather than a tuning knob's resting place.
            var text = Create();
            Assert.AreEqual(TextQuality.Medium, text.Quality);
            Destroy(text);
        }

        [Test]
        public void The_Density_Ladder_Is_Where_Quality_Stops()
        {
            // High is the last rung, and the wall behind it is the atlas's own
            // ladder rather than the enum: past 256 pixels per em there is no
            // bucket to land on. Worth pinning, because it is the reason not to
            // add another member — above the ceiling a bigger multiplier changes
            // the setting without changing the picture.
            const int Top = 256;

            // A hundred and twenty-eight points is where Medium alone already
            // reaches the top, so asking for four times instead of two buys the
            // same tile and Quality has stopped meaning anything at all.
            Assert.AreEqual(Top, GlyphAtlas.QuantizePixelsPerEm(128f * (int)TextQuality.Medium));
            Assert.AreEqual(Top, GlyphAtlas.QuantizePixelsPerEm(128f * (int)TextQuality.High),
                "past the ladder every setting lands on its top");

            // Below that the rungs are real, which is the case world text is in:
            // small in points, large on screen.
            Assert.Greater(GlyphAtlas.QuantizePixelsPerEm(30f * (int)TextQuality.High),
                GlyphAtlas.QuantizePixelsPerEm(30f * (int)TextQuality.Medium));
            Assert.Greater(GlyphAtlas.QuantizePixelsPerEm(30f * (int)TextQuality.Medium),
                GlyphAtlas.QuantizePixelsPerEm(30f * (int)TextQuality.Performance));
        }

        [Test]
        public void Raising_Quality_Bakes_A_Denser_Tile()
        {
            // The claim in atlas pixels. A point size is all a world mesh has to
            // ask a density with, and it is not a screen size — so the multiplier
            // has to actually reach the atlas, not merely be stored.
            //
            // Forty points so neither end lands on the bucket ladder's floor:
            // Performance asks for 40 and High for 160, four times the texels an
            // em wide and sixteen times the area.
            // Measured off the mesh's own uv rect rather than the shared
            // atlas's totals: the atlas outlives a test and may already hold
            // these glyphs, which would make a before-and-after difference
            // report zero and prove nothing.
            float TileTexels(TextQuality quality)
            {
                var text = Create(40f, 12f);
                text.FontSize = 40f;
                text.Quality = quality;
                text.Text = "A";
                text.ForceRebuild();

                var bounds = new List<Vector4>();
                MeshOf(text).GetUVs(2, bounds);
                Assert.Greater(bounds.Count, 0, "no quad to measure");
                // uv2.yz are the tile's u-min and u-max in atlas space.
                float width = (bounds[0].z - bounds[0].y) * SharedGlyphAtlas.Atlas.Texture.width;
                Destroy(text);
                return width;
            }

            float low = TileTexels(TextQuality.Performance);
            float high = TileTexels(TextQuality.High);

            Assert.Greater(low, 0f, "the low-quality bake produced no tile");
            Assert.Greater(high, low * 3f,
                $"High baked a tile {high:F0} texels wide against Performance's {low:F0}: " +
                "four times the density has to be about four times the texels across, so " +
                "the multiplier is not reaching the atlas");
        }

        /// <summary>
        /// The two channels that carry two bytes each, from the world mesh's
        /// side of the same contract <c>DecorationChannelTests</c> asserts for
        /// the canvas's.
        ///
        /// Both spare bytes arrived after this component was written, and
        /// neither is a zero when it means nothing: a raw zero in the face byte
        /// is a whole reach of erosion, which draws every world glyph as a
        /// hairline skeleton, and a raw layer in the low byte reads back as
        /// slice zero, which draws somebody else's tile.
        /// </summary>
        [Test]
        public void Its_Vertices_Say_Neutral_Face_And_Put_The_Layer_In_The_High_Byte()
        {
            var text = Create();
            text.Text = "Hamburg 가나다";
            text.ForceRebuild();

            var mesh = MeshOf(text);
            var uv0 = new List<Vector4>();
            var uv2 = new List<Vector4>();
            mesh.GetUVs(0, uv0);
            mesh.GetUVs(2, uv2);
            Assert.Greater(uv0.Count, 0, "no quads to inspect");

            for (int i = 0; i < uv0.Count; i++)
            {
                int faceByte = Mathf.RoundToInt(uv2[i].w) & 0xFF;
                Assert.AreEqual(128, faceByte,
                    $"vertex {i} asks the shader to move the face threshold by " +
                    $"{(faceByte - 128) / 127f:0.###} of a reach; 128 is the only byte that " +
                    "means 'draw this glyph as the font drew it'");

                int softByte = Mathf.RoundToInt(uv0[i].z) & 0xFF;
                Assert.AreEqual(0, softByte,
                    $"vertex {i} carries {softByte} in the outline-softness byte, which is " +
                    "where the atlas slice lands when it is written raw");
                Assert.Less(Mathf.RoundToInt(uv0[i].z) >> 8, 16,
                    "the slice index is the high byte and the atlas has sixteen at most");
            }

            Destroy(text);
        }

        [Test]
        public void The_Mesh_Component_Shares_The_Label_Atlas()
        {
            var text = Create();
            text.Text = "Shared";
            text.ForceRebuild();

            Assert.IsTrue(SharedGlyphAtlas.Exists,
                "world text draws from the same shared atlas as every label");
            Assert.Greater(SharedGlyphAtlas.Atlas.GetStats().TileCount, 0);
            Destroy(text);
        }
    }
}
