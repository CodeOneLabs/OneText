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
    /// The label's own share of a retext made cheaper: the layout-dirty walk
    /// skipped when nobody listens, the native-state check asked once per
    /// rebuild, the canvas reading shared inside one rebuild loop, a faster
    /// tag reader, and markup with no style laid out as plain text. Each has a
    /// switch; every test here either runs both ways and asserts the same
    /// result, or pins the condition the shortcut relies on.
    /// </summary>
    public class LabelHotPathTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static readonly MethodInfo s_populate = typeof(Graphic).GetMethod("OnPopulateMesh",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(VertexHelper) }, null);

        private readonly List<Object> _created = new List<Object>();
        private static byte[] s_font;

        [TearDown]
        public void TearDown()
        {
            foreach (var created in _created)
                if (created is GameObject go && go != null && go.TryGetComponent<Camera>(out var camera))
                    camera.targetTexture = null;
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextLabel.SkipUnheardLayoutDirty = true;
            OneTextLabel.NativeStateOnce = true;
            OneTextLabel.NeutralSpansAsPlain = true;
            ScreenPpem.ShareContextInRebuildLoop = true;
            RichTextParser.FastPath = true;
        }

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private RectTransform NewPanel(Transform parent)
        {
            var go = new GameObject("Panel", typeof(RectTransform));
            _created.Add(go);
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(600f, 400f);
            return rect;
        }

        private OneTextLabel NewLabel(Transform parent, string text)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            _created.Add(go);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(50f, 20f);
            label.SetFont(s_font ??= File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 24f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private static void Pass() => Canvas.ForceUpdateCanvases();

        // ------------------------------------------------------ layout dirty

        [TestCase(true)]
        [TestCase(false)]
        public void A_Fitter_There_From_The_Start_Resizes_The_Label(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var label = NewLabel(NewCanvas().transform, "1");
            var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            Pass();
            float narrow = label.rectTransform.rect.width;

            label.Text = "1234567890";
            Pass();
            Assert.Greater(label.rectTransform.rect.width, narrow * 3f);
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Fitter_Added_To_A_Live_Label_Resizes_It_On_The_Next_Text(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var label = NewLabel(NewCanvas().transform, "1");
            // Live and retexted with nobody listening, so the label has had
            // every chance to conclude that nobody ever will.
            for (int i = 0; i < 5; i++)
            {
                label.SetText(i);
                Pass();
            }
            Assert.AreEqual(50f, label.rectTransform.rect.width, 0.01f);

            var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            Pass();
            float fitted = label.rectTransform.rect.width;
            Assert.AreEqual(label.preferredWidth, fitted, 0.01f, "the fitter's own first pass");

            label.Text = "1234567890";
            Pass();
            Assert.Greater(label.rectTransform.rect.width, fitted * 3f, "the text change after it");
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Fitter_Added_And_A_Text_Change_In_The_Same_Frame_Both_Land(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var label = NewLabel(NewCanvas().transform, "1");
            Pass();
            label.Text = "22";
            Pass();

            var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            label.Text = "1234567890";
            Pass();
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        private static VerticalLayoutGroup AddGroup(GameObject go)
        {
            var group = go.AddComponent<VerticalLayoutGroup>();
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return group;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Layout_Group_There_From_The_Start_Resizes_The_Label(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var panel = NewPanel(NewCanvas().transform);
            AddGroup(panel.gameObject);
            var label = NewLabel(panel, "1");
            Pass();
            float narrow = label.rectTransform.rect.width;

            label.Text = "1234567890";
            Pass();
            Assert.Greater(label.rectTransform.rect.width, narrow * 3f);
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Layout_Group_Added_Above_A_Live_Label_Resizes_It_On_The_Next_Text(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var panel = NewPanel(NewCanvas().transform);
            var label = NewLabel(panel, "1");
            for (int i = 0; i < 5; i++)
            {
                label.SetText(i);
                Pass();
            }

            AddGroup(panel.gameObject);
            Pass();
            float grouped = label.rectTransform.rect.width;
            Assert.AreEqual(label.preferredWidth, grouped, 0.01f, "the group's own first pass");

            label.Text = "1234567890";
            Pass();
            Assert.Greater(label.rectTransform.rect.width, grouped * 3f, "the text change after it");
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Disabled_Layout_Group_Turned_On_Later_Resizes_The_Label(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var panel = NewPanel(NewCanvas().transform);
            var group = AddGroup(panel.gameObject);
            group.enabled = false;
            var label = NewLabel(panel, "1");
            label.Text = "12";
            Pass();
            Assert.AreEqual(50f, label.rectTransform.rect.width, 0.01f, "a disabled group lays nothing out");

            group.enabled = true;
            Pass();
            label.Text = "1234567890";
            Pass();
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Live_Label_Moved_Under_A_Layout_Group_Resizes_There(bool skip)
        {
            OneTextLabel.SkipUnheardLayoutDirty = skip;
            var canvas = NewCanvas();
            var label = NewLabel(canvas.transform, "1");
            label.Text = "12";
            Pass();

            var panel = NewPanel(canvas.transform);
            AddGroup(panel.gameObject);
            label.transform.SetParent(panel, false);
            Pass();
            label.Text = "1234567890";
            Pass();
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        [Test]
        public void A_Dirty_Layout_Callback_Still_Hears_Every_Text_Change()
        {
            var label = NewLabel(NewCanvas().transform, "1");
            label.Text = "2";
            Pass();
            int heard = 0;
            label.RegisterDirtyLayoutCallback(() => heard++);
            label.Text = "3";
            label.SetText(44);
            Assert.AreEqual(2, heard);
        }

        [Test]
        public void With_Nobody_Listening_The_Hierarchy_Is_Asked_Once()
        {
            var label = NewLabel(NewCanvas().transform, "1");
            Pass();
            label.Text = "2";
            int before = OneTextLabel.LayoutHearingChecks;
            for (int i = 0; i < 20; i++)
            {
                label.SetText(i * 7);
                Pass();
            }
            Assert.AreEqual(before, OneTextLabel.LayoutHearingChecks);
        }

        [Test]
        public void Inside_A_Layout_Group_The_Hierarchy_Is_Not_Asked_Per_Retext()
        {
            // The answer "somebody listens" is kept too: a grouped label is
            // walked by every pass its own text change causes, and re-asking
            // each time would charge it the walk twice.
            var panel = NewPanel(NewCanvas().transform);
            AddGroup(panel.gameObject);
            var label = NewLabel(panel, "1");
            Pass();
            label.Text = "2";
            int before = OneTextLabel.LayoutHearingChecks;
            for (int i = 0; i < 20; i++)
            {
                label.SetText(i * 7);
                Pass();
            }
            Assert.AreEqual(before, OneTextLabel.LayoutHearingChecks);
            Assert.AreEqual(label.preferredWidth, label.rectTransform.rect.width, 0.01f);
        }

        // --------------------------------------------------- mesh equivalence

        private static List<UIVertex> Mesh(OneTextLabel label)
        {
            var helper = new VertexHelper();
            try
            {
                s_populate.Invoke(label, new object[] { helper });
                var vertices = new List<UIVertex>();
                helper.GetUIVertexStream(vertices);
                return vertices;
            }
            finally
            {
                helper.Dispose();
            }
        }

        private static void AssertSameMesh(List<UIVertex> want, List<UIVertex> got, string what)
        {
            Assert.AreEqual(want.Count, got.Count, what + ": vertex count");
            Assert.Greater(want.Count, 0, what + ": nothing was drawn, so nothing was compared");
            for (int i = 0; i < want.Count; i++)
            {
                Assert.AreEqual(want[i].position, got[i].position, $"{what}: position {i}");
                Assert.AreEqual(want[i].color, got[i].color, $"{what}: colour {i}");
                Assert.AreEqual(want[i].uv0, got[i].uv0, $"{what}: uv0 {i}");
                Assert.AreEqual(want[i].uv1, got[i].uv1, $"{what}: uv1 {i}");
                Assert.AreEqual(want[i].uv2, got[i].uv2, $"{what}: uv2 {i}");
                Assert.AreEqual(want[i].uv3, got[i].uv3, $"{what}: uv3 {i}");
                Assert.AreEqual(want[i].normal, got[i].normal, $"{what}: normal {i}");
                Assert.AreEqual(want[i].tangent, got[i].tangent, $"{what}: tangent {i}");
            }
        }

        [TestCase("<link=a>123</link>")]
        [TestCase("<wave>12</wave> dmg")]
        [TestCase("<outline=#000000>edged 12</outline>")]
        [TestCase("<shadow>45</shadow><wait=0.5>6")]
        [TestCase("<noparse><b>x</b></noparse>")]
        [TestCase("a<br>b")]
        [TestCase("<b>12</b> <color=#ff8800>dmg</color>")]
        public void Markup_Lays_Out_The_Same_With_Or_Without_The_Plain_Shortcut(string text)
        {
            var label = NewLabel(NewCanvas().transform, text);
            OneTextLabel.NeutralSpansAsPlain = false;
            Pass();
            var before = Mesh(label);
            int runsBefore = label.LayoutResult.Runs.Count;

            OneTextLabel.NeutralSpansAsPlain = true;
            label.Text = "";
            label.Text = text;
            Pass();
            AssertSameMesh(before, Mesh(label), text);
            Assert.AreEqual(runsBefore, label.LayoutResult.Runs.Count, "runs");
        }

        [TestCase("7")]
        [TestCase("Score 12345")]
        [TestCase("<b>12</b> <color=#ff8800>dmg</color>")]
        public void One_Native_Check_Per_Rebuild_Draws_The_Same(string text)
        {
            var label = NewLabel(NewCanvas().transform, text);
            OneTextLabel.NativeStateOnce = false;
            Pass();
            var before = Mesh(label);
            OneTextLabel.NativeStateOnce = true;
            label.Text = "";
            label.Text = text;
            Pass();
            AssertSameMesh(before, Mesh(label), text);
        }

        // ------------------------------------------------- shared ppem reading

        private Camera NewCamera(int pixelHeight)
        {
            var go = new GameObject("Camera", typeof(Camera));
            _created.Add(go);
            var target = new RenderTexture(pixelHeight * 2, pixelHeight, 0);
            _created.Add(target);
            var camera = go.GetComponent<Camera>();
            camera.targetTexture = target;
            camera.orthographic = true;
            return camera;
        }

        private Canvas NewWorldCanvas(Camera camera)
        {
            var canvas = NewCanvas();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.transform.localScale = Vector3.one;
            return canvas;
        }

        /// <summary>The widest quad's tile, in atlas texels: what a density changes.</summary>
        private static float WidestTile(OneTextLabel label)
        {
            float widest = 0f;
            foreach (var quad in label.DrawnQuads)
                widest = Mathf.Max(widest, quad.UvRect.width);
            return widest * SharedGlyphAtlas.Atlas.Settings.TextureSize;
        }

        /// <summary>A rebuild by hand, outside any loop: it measures for itself.</summary>
        private static void Rebake(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Two_Canvases_In_One_Rebuild_Loop_Each_Bake_At_Their_Own_Camera(bool share)
        {
            // The tiles are the evidence, not AppliedPpemScale: the watcher
            // re-measures after the loop and would put a wrong scale right,
            // but not the mesh the loop already baked with it.
            ScreenPpem.ShareContextInRebuildLoop = share;
            var near = NewCamera(400);
            near.orthographicSize = 50f;    // 4 pixels a unit
            var far = NewCamera(400);
            far.orthographicSize = 100f;    // 2
            var a = NewLabel(NewWorldCanvas(near).transform, "W");
            var b = NewLabel(NewWorldCanvas(far).transform, "W");
            var c = NewLabel(a.canvas.transform, "W");
            Pass();
            float aLoop = WidestTile(a), bLoop = WidestTile(b), cLoop = WidestTile(c);
            Assert.Greater(aLoop, bLoop * 1.4f, "the two cameras were not told apart");
            Assert.AreEqual(aLoop, cLoop, "two labels under one camera");

            Rebake(a);
            Rebake(b);
            Assert.AreEqual(aLoop, WidestTile(a), "the loop baked a at another density than a fresh reading");
            Assert.AreEqual(bLoop, WidestTile(b), "the loop baked b at another density than a fresh reading");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A_Camera_Change_Between_Two_Canvas_Passes_Reaches_The_Second(bool share)
        {
            ScreenPpem.ShareContextInRebuildLoop = share;
            var camera = NewCamera(400);
            camera.orthographicSize = 100f;
            var label = NewLabel(NewWorldCanvas(camera).transform, "W");
            var other = NewLabel(label.canvas.transform, "W");
            Pass();
            float first = WidestTile(label);

            camera.orthographicSize = 50f;
            // Both rebuild in the next pass, so its loop has to measure again
            // rather than reuse what the last loop read.
            label.Text = "WW";
            other.Text = "WW";
            Pass();
            float second = WidestTile(label);
            Assert.Greater(second, first * 1.4f);
            Rebake(label);
            Assert.AreEqual(second, WidestTile(label));
        }

        // ------------------------------------------------------------- parser

        private static readonly string[] s_markup =
        {
            "<b>123</b> <color=#ff8800>dmg</color>",
            "<B>x</B><COLOR=#F00>y</Color>",
            "<color=red><b>a</color>b</b>",
            "<ſ>long s</ſ> <ı>dotless</ı>",
            "<x>unknown</x> <bogus=3>",
            "<b>unclosed <i>twice",
            "</> </b> <//> <>",
            "<link=a><b>x</link>y</b>",
            "<link=\"q\">quoted</link>",
            "<wave amp=2 freq=1.5>w</wave><shake=3>s</shake>",
            "<outline=black w=0.6>o</outline><shadow x=0.4>s</shadow><glow=cyan>g</glow>",
            "<align=center>c</align><align=right>r",
            "<size=150%>big</size><size=+4>rel</size><size=30>abs</size>",
            "<voffset=0.2em>v</voffset><cspace=0>c</cspace><mspace=0.6>m</mspace>",
            "<sup>1</sup><sub>2</sub><alpha=#80>a</alpha><mark>m</mark><mark=#ff000080>n</mark>",
            "<u>u</u><s>s</s><nobr>no break</nobr>",
            "<ruby=かん>漢</ruby>",
            "학생<josa=이/가> 왔다",
            "<wait=0.5>later<wait=soon>",
            "<noparse><b>raw</b></noparse> after",
            "a<br>b<BR>c",
            "<sprite=3> <sprite=name>",
            "<style=H1>styled</style><font=Mono>f</font>",
            "1 < 2 and 3 > 2 <",
            "<b",
            "<color=#ff8800",
            "<wave>effect <bounce>nested</wave> rest</bounce>",
        };

        private static int StyleNames(string name) => name == "H1" ? 0 : -1;
        private static int FontNames(string name) => name == "Mono" ? 1 : -1;
        private static int SpriteNames(string name) => name == "name" ? 7 : -1;

        [Test]
        public void The_Fast_Tag_Reader_Parses_Everything_The_Same()
        {
            foreach (var source in s_markup)
            {
                var slow = new RichTextResult();
                var fast = new RichTextResult();
                RichTextParser.FastPath = false;
                RichTextParser.Parse(source, slow, StyleNames, FontNames, SpriteNames);
                RichTextParser.FastPath = true;
                RichTextParser.Parse(source, fast, StyleNames, FontNames, SpriteNames);

                Assert.AreEqual(slow.Text, fast.Text, source + ": text");
                Assert.AreEqual(slow.HasMarkup, fast.HasMarkup, source + ": has markup");
                Assert.AreEqual(slow.Spans.Count, fast.Spans.Count, source + ": span count");
                for (int i = 0; i < slow.Spans.Count; i++)
                {
                    Assert.AreEqual(slow.Spans[i].Start, fast.Spans[i].Start, source + ": span start");
                    Assert.AreEqual(slow.Spans[i].Length, fast.Spans[i].Length, source + ": span length");
                    Assert.IsTrue(slow.Spans[i].Style.Equals(fast.Spans[i].Style), source + ": span style");
                }
                CollectionAssert.AreEqual(slow.Links, fast.Links, source + ": links");
                CollectionAssert.AreEqual(slow.Alignments, fast.Alignments, source + ": alignments");
                CollectionAssert.AreEqual(slow.Effects, fast.Effects, source + ": effects");
                CollectionAssert.AreEqual(slow.Decorations, fast.Decorations, source + ": decorations");
                CollectionAssert.AreEqual(slow.Waits, fast.Waits, source + ": waits");
                CollectionAssert.AreEqual(slow.Rubies, fast.Rubies, source + ": rubies");
            }
        }
    }
}
