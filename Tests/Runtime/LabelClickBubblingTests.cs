using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace OneText.Tests.Play
{
    /// <summary>
    /// What happens to a click the label had no link to spend it on.
    ///
    /// Button &gt; Label is the hierarchy every uGUI project is built out of,
    /// and it stopped working the moment the child was a OneTextLabel with its
    /// default raycastTarget: the input module resolves a click with
    /// <c>ExecuteEvents.GetEventHandler&lt;IPointerClickHandler&gt;</c>, which
    /// walks up from the raycast hit and stops at the first object carrying the
    /// interface, and the label carries it. The Button still got pointerDown
    /// and pointerUp, so it flashed pressed and did nothing. TextMeshProUGUI
    /// implements no pointer interface at all, which is why nobody migrating
    /// from it expected this.
    ///
    /// These are PlayMode tests because the thing under test is event routing:
    /// a Canvas, an EventSystem, and real components deciding among themselves
    /// who a click belongs to. EditMode could call OnPointerClick, but calling
    /// it is the one thing that cannot prove the bug — the bug is about who
    /// gets called.
    ///
    /// The clicks here are dispatched through the same two calls
    /// StandaloneInputModule makes rather than through a GraphicRaycaster.
    /// Raycasting depends on a Graphic having been assigned a canvas depth,
    /// which needs a real render; the routing does not, and the routing is what
    /// broke.
    /// </summary>
    public class LabelClickBubblingTests
    {
        private readonly PlayHarness _harness = new PlayHarness();
        private EventSystem _events;
        private GraphicRaycaster _raycaster;

        [SetUp]
        public void Setup()
        {
            _harness.Setup();
            _events = _harness.EventSystem();

            // Never asked to raycast. It is here to be the event data's
            // raycast module, because that is the only way PointerEventData
            // will report a pressEventCamera, and the label needs one to turn
            // a screen point back into the local point it hit-tests with.
            _raycaster = _harness.Canvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        [TearDown]
        public void Teardown() => _harness.Teardown();

        /// <summary>A rect under the canvas, the shape a Button or a dropdown row has.</summary>
        private GameObject Container(string name)
        {
            var go = _harness.Track(new GameObject(name, typeof(RectTransform)));
            go.transform.SetParent(_harness.Canvas.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(400f, 300f);
            return go;
        }

        private OneTextLabel ChildLabel(GameObject parent, string text)
        {
            var label = _harness.Label(text, boxSize: new Vector2(400f, 300f));
            label.transform.SetParent(parent.transform, false);
            label.raycastTarget = true;
            return label;
        }

        /// <summary>
        /// Delivers a left click the way StandaloneInputModule does: resolve
        /// the handler by walking up from whatever the pointer is over, then
        /// execute on it. Returns the object that was resolved, which is the
        /// half of the bug worth asserting on directly.
        /// </summary>
        private GameObject Click(GameObject over, Vector2 screenPoint)
        {
            var hit = new RaycastResult
            {
                gameObject = over,
                module = _raycaster,
                screenPosition = screenPoint,
            };
            var data = new PointerEventData(_events)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = screenPoint,
                pointerPressRaycast = hit,
                pointerCurrentRaycast = hit,
                pointerPress = over,
            };

            var target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            if (target != null)
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
            return target;
        }

        /// <summary>Where a point in the label's own space lands on screen.</summary>
        private Vector2 ScreenPointOn(OneTextLabel label, Vector2 local) =>
            RectTransformUtility.WorldToScreenPoint(
                _harness.Camera, label.rectTransform.TransformPoint(local));

        private static readonly List<Rect> s_linkRects = new List<Rect>();

        /// <summary>The middle of the first rectangle a link occupies, in label space.</summary>
        private static Rect LinkRect(OneTextLabel label, string id)
        {
            foreach (var link in label.Links)
            {
                if (link.Id != id) continue;
                s_linkRects.Clear();
                label.GetSelectionRects(link.Start, link.End, s_linkRects);
                Assert.Greater(s_linkRects.Count, 0, $"link '{id}' laid out to no rectangles");
                return s_linkRects[0];
            }
            Assert.Fail($"no link '{id}' was parsed out of the text");
            return default;
        }

        [UnityTest]
        public IEnumerator A_Button_Under_A_Plain_Label_Still_Gets_Its_Click()
        {
            var buttonGo = Container("Button");
            var button = buttonGo.AddComponent<Button>();
            var label = ChildLabel(buttonGo, "Click me");
            yield return PlayHarness.Frame();

            int clicks = 0;
            button.onClick.AddListener(() => clicks++);

            Assert.IsTrue(label.raycastTarget,
                "the bug only exists while the label is a raycast target");
            Assert.AreSame(label.gameObject,
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(label.gameObject),
                "the input module is expected to stop at the label; if it no longer does, " +
                "this fixture is testing nothing");

            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            Assert.AreEqual(1, clicks, "the label swallowed the button's click");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Click_On_A_Link_Belongs_To_The_Link_And_Stops_There()
        {
            var buttonGo = Container("Button");
            var button = buttonGo.AddComponent<Button>();
            var label = ChildLabel(buttonGo, "<link=go>go</link> elsewhere");
            yield return PlayHarness.Frame();

            int clicks = 0;
            button.onClick.AddListener(() => clicks++);
            var ids = new List<string>();
            label.LinkClicked.AddListener(ids.Add);

            Click(label.gameObject, ScreenPointOn(label, LinkRect(label, "go").center));

            CollectionAssert.AreEqual(new[] { "go" }, ids);
            Assert.AreEqual(0, clicks,
                "a click the link consumed went on to press the button as well");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Click_That_Misses_Every_Link_Reaches_The_Button()
        {
            var buttonGo = Container("Button");
            var button = buttonGo.AddComponent<Button>();
            var label = ChildLabel(buttonGo, "<link=go>go</link> elsewhere");
            yield return PlayHarness.Frame();

            int clicks = 0;
            button.onClick.AddListener(() => clicks++);
            var ids = new List<string>();
            label.LinkClicked.AddListener(ids.Add);

            // Two line-heights below the link, which is still well inside a
            // 300-tall box holding one centred line.
            var rect = LinkRect(label, "go");
            var miss = rect.center - new Vector2(0f, rect.height * 2f);

            Click(label.gameObject, ScreenPointOn(label, miss));

            CollectionAssert.IsEmpty(ids, "a click nowhere near the link fired it anyway");
            Assert.AreEqual(1, clicks,
                "a click that missed every link span should go up, the way it does in HTML");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Markup_Without_Any_Link_Does_Not_Swallow_The_Click()
        {
            // The fast path out of OnPointerClick is essentially IndexOf('<'),
            // so this text takes the slow path and finds no links there. Both
            // routes have to end in the same place or half of every bold label
            // is still dead.
            var buttonGo = Container("Button");
            var button = buttonGo.AddComponent<Button>();
            var label = ChildLabel(buttonGo, "<b>Click me</b>");
            yield return PlayHarness.Frame();

            int clicks = 0;
            button.onClick.AddListener(() => clicks++);
            Assert.AreEqual(0, label.Links.Count, "this text was not supposed to have links");

            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            Assert.AreEqual(1, clicks, "the slow path swallowed a click it did nothing with");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Handler_On_The_Labels_Own_Object_Consumes_The_Click()
        {
            // The single case where re-dispatching would deliver twice:
            // ExecuteEvents.Execute runs every IPointerClickHandler on the
            // target object, so this button was already pressed by the same
            // call that reached the label. Sending the click up from here would
            // press the outer one too, off a single click.
            var outerGo = Container("Outer");
            var outer = outerGo.AddComponent<Button>();
            var label = ChildLabel(outerGo, "Click me");
            var own = label.gameObject.AddComponent<Button>();
            yield return PlayHarness.Frame();

            int ownClicks = 0, outerClicks = 0;
            own.onClick.AddListener(() => ownClicks++);
            outer.onClick.AddListener(() => outerClicks++);

            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            Assert.AreEqual(1, ownClicks, "the button sharing the label's object fired {0} times",
                ownClicks);
            Assert.AreEqual(0, outerClicks,
                "one click pressed two buttons: the label re-sent a click that was already handled");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Disabled_Handler_On_The_Labels_Object_Does_Not_Hold_The_Click()
        {
            // ExecuteEvents skips disabled components, so a button that was
            // switched off never saw this click and is no reason to keep it
            // from the hierarchy above.
            var outerGo = Container("Outer");
            var outer = outerGo.AddComponent<Button>();
            var label = ChildLabel(outerGo, "Click me");
            var own = label.gameObject.AddComponent<Button>();
            own.enabled = false;
            yield return PlayHarness.Frame();

            int outerClicks = 0;
            outer.onClick.AddListener(() => outerClicks++);

            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            Assert.AreEqual(1, outerClicks,
                "a disabled component on the label's object blocked the click from bubbling");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Toggle_Parent_Toggles()
        {
            // The shape of an open dropdown's list: the row is a Toggle and
            // the item's label lies across the whole of it, so every row of
            // every migrated dropdown was unselectable.
            var rowGo = Container("Item");
            var toggle = rowGo.AddComponent<Toggle>();
            toggle.isOn = false;
            var label = ChildLabel(rowGo, "Option A");
            yield return PlayHarness.Frame();

            var values = new List<bool>();
            toggle.onValueChanged.AddListener(values.Add);

            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            Assert.IsTrue(toggle.isOn, "the row's toggle never heard the click");
            CollectionAssert.AreEqual(new[] { true }, values);
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Label_With_No_Parent_Has_Nowhere_To_Send_It_And_Says_So_Quietly()
        {
            var label = _harness.Label("Click me", boxSize: new Vector2(400f, 300f));
            label.transform.SetParent(null, false);
            label.raycastTarget = true;
            yield return PlayHarness.Frame();

            Assert.IsNull(label.transform.parent, "this test needs a parentless label");
            Click(label.gameObject, ScreenPointOn(label, Vector2.zero));

            PlayHarness.ExpectNoErrors();
        }
    }
}
