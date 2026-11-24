using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The label writes its mesh itself instead of through VertexHelper. That
    /// is only a speed-up if nothing can tell: the canvas has to be handed the
    /// same vertices in the same order either way, and anything written
    /// against VertexHelper has to keep being given one.
    /// </summary>
    public class DirectMeshTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static readonly MethodInfo s_populate = typeof(Graphic).GetMethod("OnPopulateMesh",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(VertexHelper) }, null);

        private GameObject _canvas;
        private OneTextLabel _label;

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_canvas.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 120f);
            _label = go.AddComponent<OneTextLabel>();
            _label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            _label.FontSize = 24f;
            OneTextLabel.DirectMesh = true;
        }

        [TearDown]
        public void TearDown()
        {
            OneTextLabel.DirectMesh = true;
            if (_canvas != null) Object.DestroyImmediate(_canvas);
        }

        private void Rebuild()
        {
            _label.SetVerticesDirty();
            _label.Rebuild(CanvasUpdate.PreRender);
        }

        [TestCase("7")]
        [TestCase("Score 12345")]
        [TestCase("two\nlines of it")]
        [TestCase("<color=#ff0000>red</color> and <b>bold</b> and <u>under</u>")]
        [TestCase("<outline=#000000>edged</outline> <shadow=#00000080>dropped</shadow>")]
        public void Both_Routes_Hand_The_Canvas_The_Same_Mesh(string text)
        {
            _label.Text = text;

            int before = OneTextLabel.DirectUploads;
            Rebuild();
            Assert.AreEqual(before + 1, OneTextLabel.DirectUploads, "the label did not take the direct route");
            var direct = OneTextLabel.LastDirectMesh;
            Assert.Greater(direct.vertexCount, 0, "nothing was drawn, so nothing was compared");

            var helper = new VertexHelper();
            s_populate.Invoke(_label, new object[] { helper });
            var expected = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                helper.FillMesh(expected);
                CollectionAssert.AreEqual(expected.vertices, direct.vertices, "positions");
                CollectionAssert.AreEqual(expected.colors32, direct.colors32, "colours");
                CollectionAssert.AreEqual(expected.normals, direct.normals, "normals");
                CollectionAssert.AreEqual(expected.tangents, direct.tangents, "tangents");
                CollectionAssert.AreEqual(expected.triangles, direct.triangles, "triangles");
                // What the canvas reads to decide how much of the buffers to
                // take. Both buffers can be right and this still say "none".
                var want0 = expected.GetSubMesh(0);
                var got0 = direct.GetSubMesh(0);
                Assert.AreEqual(want0.vertexCount, got0.vertexCount, "submesh vertex count");
                Assert.AreEqual(want0.firstVertex, got0.firstVertex, "submesh first vertex");
                Assert.AreEqual(want0.indexCount, got0.indexCount, "submesh index count");
                Assert.AreEqual(want0.topology, got0.topology, "submesh topology");
                for (int channel = 0; channel < 4; channel++)
                {
                    var want = new List<Vector4>();
                    var got = new List<Vector4>();
                    expected.GetUVs(channel, want);
                    direct.GetUVs(channel, got);
                    CollectionAssert.AreEqual(want, got, "TEXCOORD" + channel);
                }
                // To a thousandth of a unit, not to the bit: Unity keeps bounds
                // as a centre and an extent, and its own RecalculateBounds and
                // this route's SetMinMax round that pair differently.
                Assert.Less(Vector3.Distance(expected.bounds.min, direct.bounds.min), 1e-3f, "bounds min");
                Assert.Less(Vector3.Distance(expected.bounds.max, direct.bounds.max), 1e-3f, "bounds max");
            }
            finally
            {
                helper.Dispose();
                Object.DestroyImmediate(expected);
            }
        }

        [Test]
        public void A_Label_With_A_Mesh_Modifier_Keeps_The_Route_That_Feeds_It()
        {
            _label.Text = "shadowed";
            // uGUI's Shadow is a BaseMeshEffect: it rewrites the VertexHelper,
            // and a label that skipped VertexHelper would draw without it.
            _label.gameObject.AddComponent<Shadow>();

            int before = OneTextLabel.DirectUploads;
            Rebuild();
            Assert.AreEqual(before, OneTextLabel.DirectUploads,
                "a label carrying an IMeshModifier went around it");
        }

        [Test]
        public void The_Remembered_Modifier_Answer_Follows_Every_Add_And_Remove()
        {
            // Every order of adding and removing two mesh effects and one
            // component that is not one, with one or two changes between
            // rebuilds, and the route checked after each rebuild against
            // asking the GameObject outright.
            _label.Text = "route";
            var kinds = new[] { typeof(Shadow), typeof(Outline), typeof(LayoutElement) };
            int checkedRoutes = 0;
            foreach (var order in Permutations(new[] { 0, 1, 2, 3, 4, 5 }))
            foreach (int batch in new[] { 1, 2 })
            {
                var live = new Component[kinds.Length];
                int pending = 0;
                foreach (int step in order)
                {
                    int kind = step % 3;
                    bool add = step < 3;
                    // Removing something not there yet adds it instead, so
                    // every order is a real sequence of changes.
                    if (add || live[kind] == null)
                    {
                        if (live[kind] == null) live[kind] = _label.gameObject.AddComponent(kinds[kind]);
                    }
                    else
                    {
                        Object.DestroyImmediate(live[kind]);
                        live[kind] = null;
                    }
                    if (++pending < batch) continue;
                    pending = 0;
                    bool truth = _label.TryGetComponent<IMeshModifier>(out _);
                    int before = OneTextLabel.DirectUploads;
                    Rebuild();
                    bool direct = OneTextLabel.DirectUploads == before + 1;
                    Assert.AreEqual(!truth, direct,
                        $"order {string.Join(",", order)} batch {batch}: modifier present {truth}, direct route {direct}");
                    checkedRoutes++;
                }
                foreach (var c in live) if (c != null) Object.DestroyImmediate(c);
                Rebuild();
            }
            Assert.Greater(checkedRoutes, 3000);
        }

        private static IEnumerable<int[]> Permutations(int[] items)
        {
            if (items.Length <= 1) { yield return items; yield break; }
            for (int i = 0; i < items.Length; i++)
            {
                var rest = new int[items.Length - 1];
                for (int j = 0, k = 0; j < items.Length; j++) if (j != i) rest[k++] = items[j];
                foreach (var tail in Permutations(rest))
                {
                    var whole = new int[items.Length];
                    whole[0] = items[i];
                    tail.CopyTo(whole, 1);
                    yield return whole;
                }
            }
        }

        [Test]
        public void An_Empty_Label_Hands_Over_An_Empty_Mesh()
        {
            _label.Text = "something";
            Rebuild();
            _label.Text = "";
            Rebuild();
            Assert.AreEqual(0, OneTextLabel.LastDirectMesh.vertexCount);
        }

        [Test]
        public void Switched_Off_Every_Label_Goes_Through_VertexHelper()
        {
            _label.Text = "the old way";
            OneTextLabel.DirectMesh = false;
            int before = OneTextLabel.DirectUploads;
            Rebuild();
            Assert.AreEqual(before, OneTextLabel.DirectUploads);
        }
    }
}
