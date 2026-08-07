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
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

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
            for (int i = 0; i < uv0.Count; i++)
            {
                Assert.GreaterOrEqual(uv0[i].z, 0f, "TEXCOORD0.z is the atlas layer");
                Assert.AreEqual(0f, uv2[i].w, "TEXCOORD2.w picks the SDF atlas for plain text");
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
                Assert.AreEqual(2f, v.w, "TEXCOORD2.w = 2 is the multi-channel atlas");
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
