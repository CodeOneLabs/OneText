using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The dropdown the menu makes, opened.
    ///
    /// A creation menu entry is the one piece of a package that cannot be
    /// checked by reading it: every reference it writes is a reference nothing
    /// else in the codebase writes, and the way they fail is silently — a
    /// template pointing at the wrong level, a row label that is not a
    /// OneTextLabel, a list whose three rows all landed on top of each other.
    /// None of that shows up until somebody opens the thing in a scene. So the
    /// assertions here are the ones a designer would make with a mouse: open
    /// it, count the rows, read them, change the value and read the caption.
    ///
    /// <c>Show</c> is called rather than clicked because a click needs a frame
    /// and an EditMode test has not got one; everything it reaches is the same
    /// either way, with the single exception of who wins the raycast — which is
    /// why the sorting of the list against its blocker is asserted as numbers
    /// instead.
    /// </summary>
    public sealed class DropdownCreationTests
    {
        private readonly HashSet<GameObject> _standing = new HashSet<GameObject>();
        private GameObject _canvas;

        /// <summary>
        /// The menu entry creates a canvas and an event system of its own when
        /// the scene has none, so what has to be cleaned up afterwards is not
        /// knowable in advance. Everything the scene was already holding is
        /// remembered instead, and whatever is left over at the end goes.
        /// </summary>
        [SetUp]
        public void RememberTheScene()
        {
            _standing.Clear();
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                _standing.Add(go);

            _canvas = new GameObject("Dropdown Test Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }

        [TearDown]
        public void PutItBack()
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_standing.Contains(go)) Object.DestroyImmediate(go);
            _standing.Clear();
            _canvas = null;
        }

        /// <summary>
        /// The menu entry, called the way the menu calls it. The canvas is
        /// handed over as the context so the dropdown lands somewhere this test
        /// owns rather than in whatever canvas the open scene happens to have.
        /// </summary>
        private OneTextDropdown Make()
        {
            OneTextMenuItems.CreateDropdown(new MenuCommand(_canvas));
            var dropdown = _canvas.GetComponentInChildren<OneTextDropdown>(true);
            Assert.IsNotNull(dropdown, "the menu entry put no dropdown under the canvas it was given");
            return dropdown;
        }

        private static GameObject ListOf(OneTextDropdown dropdown)
        {
            foreach (Transform child in dropdown.transform)
                if (child.gameObject.name == "Dropdown List") return child.gameObject;
            return null;
        }

        private GameObject Blocker() => BlockerUnder(_canvas);

        private static GameObject BlockerUnder(GameObject canvas)
        {
            foreach (Transform child in canvas.transform)
                if (child.gameObject.name == "Blocker") return child.gameObject;
            return null;
        }

        /// <summary>
        /// Something between the canvas and the dropdown, so that switching it
        /// off or throwing it away leaves the blocker — which lives on the
        /// canvas — somewhere only the dropdown's own cleanup can reach.
        /// </summary>
        private GameObject Panel(string name)
        {
            var panel = new GameObject(name, typeof(RectTransform));
            panel.transform.SetParent(_canvas.transform, false);
            return panel;
        }

        /// <summary>
        /// The rows of an open list, which are its active toggles: the item the
        /// rows were copied from is still in there and is switched off at the
        /// end of <c>Show</c>.
        /// </summary>
        private static List<Toggle> RowsOf(GameObject list)
        {
            var rows = new List<Toggle>();
            foreach (var toggle in list.GetComponentsInChildren<Toggle>(true))
                if (toggle.gameObject.activeSelf) rows.Add(toggle);
            return rows;
        }

        private static string TextOn(Component row)
        {
            var label = row.GetComponentInChildren<OneTextLabel>(true);
            Assert.IsNotNull(label, "a row of the list carries no OneTextLabel, which is the only " +
                                    "thing the row's text is ever written to");
            return label.Text;
        }

        [Test]
        public void What_The_Menu_Makes_Opens_With_One_Row_Per_Option()
        {
            var dropdown = Make();

            dropdown.Show();

            Assert.IsTrue(dropdown.IsExpanded, "Show refused the dropdown the menu itself made, " +
                                               "which means the template it wrote is not the shape " +
                                               "the component looks for");
            var list = ListOf(dropdown);
            Assert.IsNotNull(list, "nothing named Dropdown List was instantiated next to the template");

            var rows = RowsOf(list);
            Assert.AreEqual(3, rows.Count, "the open list is not one row per option");
            Assert.AreEqual("Option A", TextOn(rows[0]));
            Assert.AreEqual("Option B", TextOn(rows[1]));
            Assert.AreEqual("Option C", TextOn(rows[2]));
        }

        /// <summary>
        /// The row height the menu entry authors, and the gap it leaves above
        /// the first row and below the last. Both are read back out of the
        /// template by <c>Show</c> rather than known to it, so the numbers here
        /// are the ones the menu entry wrote and nothing else.
        /// </summary>
        private const float AuthoredRow = 44f;
        private const float AuthoredPad = 4f;

        private static float[] RowTops(List<Toggle> rows)
        {
            var tops = new float[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                tops[i] = ((RectTransform)rows[i].transform).anchoredPosition.y;
            return tops;
        }

        [Test]
        public void The_Rows_Are_Laid_Down_In_Order_Instead_Of_On_Top_Of_Each_Other()
        {
            var dropdown = Make();
            dropdown.Show();

            var rows = RowsOf(ListOf(dropdown));
            var y = RowTops(rows);

            // The failure this stands against is all three rows sharing one
            // position, so distinctness is asserted before the spacing is.
            Assert.AreNotEqual(y[0], y[1], "two rows are at the same height: the list is a pile");
            Assert.Greater(y[0], y[1], "the second option is not below the first");
            Assert.Greater(y[1], y[2], "the third option is not below the second");
            Assert.AreEqual(AuthoredRow, y[0] - y[1], 0.001f,
                "the rows are spaced by something other than the height of a row");
            Assert.AreEqual(AuthoredRow, y[1] - y[2], 0.001f, "the spacing is not even");

            foreach (var row in rows)
            {
                var rect = (RectTransform)row.transform;
                Assert.AreEqual(AuthoredRow, rect.rect.height, 0.001f,
                    "a row is not the height the template item was");
                Assert.AreEqual(0f, rect.anchorMin.y, 0.001f,
                    "the rows were not re-anchored to the bottom of the content, so they move " +
                    "when the content is resized under them");
                Assert.AreEqual(0f, rect.anchorMax.y, 0.001f);
            }
        }

        [Test]
        public void The_Content_Grows_And_Shrinks_With_The_Option_Count()
        {
            var dropdown = Make();

            dropdown.Show();
            var content = (RectTransform)RowsOf(ListOf(dropdown))[0].transform.parent;
            Assert.AreEqual(3f * AuthoredRow + 2f * AuthoredPad, content.rect.height, 0.01f,
                "the content is not three rows and the template's own padding tall");
            dropdown.Hide();

            dropdown.options.Add(new OneTextDropdown.OptionData("Option D"));
            dropdown.options.Add(new OneTextDropdown.OptionData("Option E"));

            dropdown.Show();
            content = (RectTransform)RowsOf(ListOf(dropdown))[0].transform.parent;
            Assert.AreEqual(5f * AuthoredRow + 2f * AuthoredPad, content.rect.height, 0.01f,
                "the content did not follow the option count — and since the template is measured " +
                "afresh every open, this also means the last open left its arithmetic behind on " +
                "the template");
        }

        /// <summary>
        /// Where the arrow keys go from a row, which is half of arrow-key
        /// navigation and the half an EditMode test can see.
        ///
        /// The other half — that anybody is standing on a row to begin with —
        /// cannot be checked here at any price. It is <c>EventSystem.current</c>
        /// that says where focus is, and that is assigned in the EventSystem's
        /// own OnEnable, which never runs in edit mode because the component is
        /// not ExecuteAlways. So the field reads null however many event systems
        /// the scene has been given, <c>Selectable.Select</c> declines against
        /// it, and a test here could only ever assert that nothing happened.
        /// <c>DropdownSelectionTests</c> in the PlayMode suite asserts the rest.
        /// </summary>
        [Test]
        public void The_Arrow_Keys_Walk_The_Open_List()
        {
            var dropdown = Make();
            dropdown.Show();

            var rows = RowsOf(ListOf(dropdown));
            foreach (var row in rows)
                Assert.AreEqual(Navigation.Mode.Explicit, row.navigation.mode,
                    "a row navigates by geometry, which from an open list drawn over the whole " +
                    "screen reaches anything at all");

            Assert.AreSame(rows[1], rows[0].navigation.selectOnDown);
            Assert.AreSame(rows[1], rows[0].navigation.selectOnRight);
            Assert.AreSame(rows[0], rows[1].navigation.selectOnUp);
            Assert.AreSame(rows[2], rows[1].navigation.selectOnDown);
        }

        [Test]
        public void The_Caption_Says_Which_Option_Is_Selected()
        {
            var dropdown = Make();

            Assert.IsNotNull(dropdown.captionText, "the caption was never pointed at a label, which " +
                                                   "is the blank-caption failure the whole component " +
                                                   "exists for");
            Assert.AreEqual("Option A", dropdown.captionText.Text,
                "a new dropdown does not show its own first option");

            dropdown.value = 2;

            Assert.AreEqual("Option C", dropdown.captionText.Text,
                "the caption did not follow the value");
        }

        [Test]
        public void Neither_Label_Answers_A_Click_Meant_For_Something_Else()
        {
            var dropdown = Make();

            Assert.IsNotNull(dropdown.itemText, "the item label reference was left None");
            Assert.IsFalse(dropdown.captionText.raycastTarget,
                "the caption takes clicks, and OneTextLabel handles clicks itself — so the click " +
                "that should open the list stops at the caption");
            Assert.IsFalse(dropdown.itemText.raycastTarget,
                "the item label takes clicks, and it covers the row's toggle — so choosing an " +
                "option does nothing at all");

            dropdown.Show();
            foreach (var row in RowsOf(ListOf(dropdown)))
                Assert.IsFalse(row.GetComponentInChildren<OneTextLabel>(true).raycastTarget,
                    "a row copied from the template takes clicks even though the template's own " +
                    "label does not");
        }

        [Test]
        public void The_Template_Is_Already_Off_When_The_Menu_Returns()
        {
            var dropdown = Make();

            Assert.IsNotNull(dropdown.template, "no template was written, so Show has nothing to copy");
            // Awake deactivates the template too, but only for a dropdown that
            // already holds the reference. This one was given it after the
            // component existed, so anything active here is active on screen.
            Assert.IsFalse(dropdown.template.gameObject.activeSelf,
                "the template is left switched on, which is a list hanging open under every fresh " +
                "dropdown until something closes it");
        }

        [Test]
        public void The_Open_List_Sorts_Over_Its_Own_Blocker()
        {
            var dropdown = Make();
            dropdown.Show();

            var listCanvas = ListOf(dropdown).GetComponent<Canvas>();
            Assert.IsNotNull(listCanvas, "the open list is on no canvas of its own, so the blocker " +
                                         "has no order to sit one under");
            Assert.IsTrue(listCanvas.overrideSorting, "the list's canvas does not override sorting, " +
                                                      "so its order is not its own");
            Assert.IsNotNull(ListOf(dropdown).GetComponent<GraphicRaycaster>(),
                "a canvas of its own is a raycast root of its own: without a raycaster the list " +
                "draws on top and answers nothing");

            var blocker = Blocker();
            Assert.IsNotNull(blocker, "no blocker, so a click outside the list never closes it");
            var blockerCanvas = blocker.GetComponent<Canvas>();
            Assert.IsTrue(blockerCanvas.overrideSorting);
            Assert.Less(blockerCanvas.sortingOrder, listCanvas.sortingOrder,
                "the full-screen blocker is level with the list it is supposed to sit behind, and " +
                "which of the two wins a click is then down to the order the raycasters run in");
            Assert.AreEqual(listCanvas.sortingOrder - 1, blockerCanvas.sortingOrder,
                "the blocker is not immediately under the list, so something between the two can " +
                "be clicked while the list is open");
            Assert.AreEqual(listCanvas.sortingLayerID, blockerCanvas.sortingLayerID,
                "the two are on different sorting layers, where their orders do not compare");
        }

        [Test]
        public void The_Blocker_Belongs_To_The_Outermost_Canvas_Not_The_Nearest()
        {
            // The batching pattern, which is the ordinary reason a screen has
            // more than one canvas: a panel with a canvas of its own, no sorting
            // override, nothing else about it different.
            var batched = new GameObject("Batched Panel", typeof(RectTransform), typeof(Canvas));
            batched.transform.SetParent(_canvas.transform, false);
            var dropdown = Make();
            dropdown.transform.SetParent(batched.transform, false);

            dropdown.Show();

            Assert.IsNotNull(Blocker(),
                "the blocker went under the panel's own canvas instead of the screen's, so a click " +
                "anywhere outside that one panel no longer closes the list — and the same wrong " +
                "canvas is the rect the open list is measured against for fitting on screen, so a " +
                "list with room below it turns over as soon as the panel ends");
        }

        [Test]
        public void The_Open_List_Answers_Clicks_The_Same_Way_Its_Canvas_Does()
        {
            // A canvas nothing raycasts. The rule being checked is that the list
            // and the blocker are given the kinds of raycaster their own canvas
            // is read by — this one is read by none, and the answer that used to
            // be given regardless was a GraphicRaycaster, which is the wrong kind
            // on the world-space and headset canvases where the rule matters.
            var bare = new GameObject("Unraycast Canvas", typeof(RectTransform), typeof(Canvas));
            bare.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var dropdown = MakeTheConvertedShape(bare.transform);

            dropdown.Show();

            var list = ListOf(dropdown);
            Assert.IsNotNull(list, "the list did not open, so nothing here is being measured");
            // Asserted together, because "no raycaster" on its own is also true
            // of a list that was given nothing at all: the canvas is what says
            // the list was set up and then deliberately left unraycast.
            Assert.IsNotNull(list.GetComponent<Canvas>(),
                "the list has no canvas of its own, so it was never set up and the assertion " +
                "below would be reading an absence rather than a decision");
            Assert.IsNull(list.GetComponent<GraphicRaycaster>(),
                "the list was given a raycaster of a kind its own canvas has not got");

            var blocker = BlockerUnder(bare);
            Assert.IsNotNull(blocker, "no blocker was made, so the assertion below would pass on " +
                                      "an absence");
            Assert.IsNull(blocker.GetComponent<GraphicRaycaster>(),
                "the blocker was given a raycaster of a kind its own canvas has not got, which on " +
                "a canvas read by something else means it never catches the click it exists for");
        }

        /// <summary>
        /// A dropdown the shape a converted one is, by hand: template, content,
        /// item, label, and nothing whatever on the content.
        ///
        /// The geometry is Unity's own <c>DefaultControls</c> — a 28-tall
        /// content holding a 20-tall item centred in it — because that is
        /// precisely what a prefab converted from uGUI or TextMesh Pro arrives
        /// with, layout group included, which is to say not at all. It cannot be
        /// built through the menu entry: the menu entry is the one dropdown in
        /// the world guaranteed to be authored the way this component likes,
        /// and the bug being guarded here was reported against the dropdowns
        /// that were not.
        /// </summary>
        private static OneTextDropdown MakeTheConvertedShape(Transform parent)
        {
            var root = new GameObject("Converted Dropdown", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(parent, false);
            ((RectTransform)root.transform).sizeDelta = new Vector2(160f, 30f);

            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(root.transform, false);
            var templateRect = (RectTransform)template.transform;
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, 2f);
            templateRect.sizeDelta = new Vector2(0f, 150f);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(template.transform, false);
            var contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 28f);

            var item = new GameObject("Item", typeof(RectTransform));
            item.transform.SetParent(content.transform, false);
            var itemRect = (RectTransform)item.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 20f);
            item.AddComponent<Toggle>();

            var label = new GameObject("Item Label", typeof(RectTransform), typeof(CanvasRenderer));
            label.transform.SetParent(item.transform, false);
            label.AddComponent<OneTextLabel>();

            template.SetActive(false);

            var dropdown = root.AddComponent<OneTextDropdown>();
            var serialized = new SerializedObject(dropdown);
            serialized.FindProperty("m_Template").objectReferenceValue = templateRect;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            dropdown.AddOptions(new List<string> { "First", "Second", "Third" });
            return dropdown;
        }

        [Test]
        public void A_Converted_Template_With_A_Bare_Content_Still_Lays_Its_Rows_Out()
        {
            var dropdown = MakeTheConvertedShape(_canvas.transform);

            dropdown.Show();

            var list = ListOf(dropdown);
            Assert.IsNotNull(list, "a template of the shape every converted dropdown has was " +
                                   "refused");
            var rows = RowsOf(list);
            Assert.AreEqual(3, rows.Count);
            Assert.AreEqual("First", TextOn(rows[0]));
            Assert.AreEqual("Third", TextOn(rows[2]));

            var content = (RectTransform)rows[0].transform.parent;
            Assert.IsNull(content.GetComponent<VerticalLayoutGroup>(),
                "the fixture grew a layout group, so it is no longer standing where the bug was");

            // Unity's numbers for Unity's geometry: three 20-tall rows and the
            // 4 either side that the 28-tall content had around its item.
            Assert.AreEqual(68f, content.rect.height, 0.01f,
                "the content was not sized to its rows");
            var y = RowTops(rows);
            Assert.AreEqual(54f, y[0], 0.01f, "the first row is not one padding down from the top");
            Assert.AreEqual(34f, y[1], 0.01f, "the second row is not one row below the first");
            Assert.AreEqual(14f, y[2], 0.01f, "the last row is not one padding up from the bottom");
        }

        /// <summary>
        /// Tearing a screen down with the list still open.
        ///
        /// This is not a test about tests, though it was the teardown of the
        /// ones above that found it. A dropdown is disabled by whatever is
        /// happening above it — a panel closing, a pooled screen going back in
        /// the pool, a scene unloading — and it answered by destroying its list
        /// on the spot, which Unity refuses underneath an object that is itself
        /// mid-deactivation. What it left was an error in the console and a
        /// full-screen invisible blocker still taking every click on the canvas.
        ///
        /// The absence of the error is one assertion; the blocker actually being
        /// gone is the other. Both are needed and neither is enough: the error
        /// could be silenced by never cleaning up, and the cleanup is deferred a
        /// tick in the editor, so the tick has to be waited for before anything
        /// can be said about it. <c>NoUnexpectedReceived</c> rather than an
        /// expectation of the error, because expecting it would be writing the
        /// defect down as the behaviour.
        ///
        /// The dropdown is put under a screen of its own, one level below the
        /// canvas. That is not decoration: the blocker hangs off the canvas, so
        /// destroying the canvas would take it whatever this component did, and
        /// the assertion would pass against any code at all. Destroying only the
        /// screen leaves the blocker somewhere nothing but the cleanup can reach.
        /// </summary>
        [UnityTest]
        public IEnumerator Destroying_A_Screen_With_The_List_Open_Says_Nothing_And_Leaves_Nothing()
        {
            var screen = Panel("Doomed Screen");
            var dropdown = Make();
            dropdown.transform.SetParent(screen.transform, false);
            dropdown.Show();

            var list = ListOf(dropdown);
            var blocker = Blocker();
            Assert.IsNotNull(list, "nothing was open, so this test is not standing where the " +
                                   "failure was");
            Assert.IsNotNull(blocker, "there is no blocker to be left behind");
            Assert.AreEqual(_canvas.transform, blocker.transform.parent,
                "the blocker is not on the canvas, so destroying the screen would take it and " +
                "this test would pass against anything");

            Object.DestroyImmediate(screen);
            LogAssert.NoUnexpectedReceived();

            // The editor's deferred destruction runs on the next tick, so a
            // test that asserts the objects are gone without waiting for one is
            // asserting nothing. One tick is the guarantee; the rest is margin.
            yield return null;
            yield return null;
            yield return null;

            Assert.IsTrue(list == null, "the open list outlived the screen it belonged to");
            Assert.IsTrue(blocker == null, "the blocker outlived the screen — a full-screen " +
                                           "invisible graphic nobody can see and everything is " +
                                           "behind");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator Disabling_A_Screen_With_The_List_Open_Says_Nothing_And_Leaves_Nothing()
        {
            var screen = Panel("Quiet Screen");
            var dropdown = Make();
            dropdown.transform.SetParent(screen.transform, false);
            dropdown.Show();
            var blocker = Blocker();
            Assert.IsNotNull(blocker);

            screen.SetActive(false);
            LogAssert.NoUnexpectedReceived();

            Assert.IsFalse(dropdown.IsExpanded,
                "the dropdown still believes it is open, so re-enabling the screen and clicking " +
                "it does nothing at all");

            // The editor's deferred destruction runs on the next tick, so a
            // test that asserts the objects are gone without waiting for one is
            // asserting nothing. One tick is the guarantee; the rest is margin.
            yield return null;
            yield return null;
            yield return null;

            // The canvas is still up, so nothing but the cleanup can have taken
            // this: a blocker left here takes every click on the screen for as
            // long as the scene lives.
            Assert.IsTrue(blocker == null, "the blocker outlived the screen going quiet");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Closing_Takes_The_List_And_The_Blocker_With_It()
        {
            var dropdown = Make();
            dropdown.Show();

            dropdown.Hide();

            Assert.IsFalse(dropdown.IsExpanded);
            Assert.IsNull(ListOf(dropdown), "the open list outlived the close");
            Assert.IsNull(Blocker(), "the blocker outlived the close, and a full-screen invisible " +
                                     "graphic left behind takes every click in the scene");
            Assert.IsFalse(dropdown.template.gameObject.activeSelf,
                "the template was left switched on by the close");
        }
    }
}
