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
    /// Where keyboard focus goes when the list opens.
    ///
    /// The explicit navigation between rows is wired in <c>Show</c> and is
    /// asserted in EditMode, where it is readable off the toggles. It was also,
    /// for a while, completely inert: nothing ever put focus on a row, so focus
    /// stayed on the dropdown button, whose own navigation is Automatic — the
    /// mode that picks whatever is nearest on screen. The first arrow press
    /// after opening a list went to whatever else the screen happened to have in
    /// that direction, and the careful wiring inside the list was never reached
    /// by anybody.
    ///
    /// This is a PlayMode test and cannot be anything else. The thing under test
    /// is <c>EventSystem.current</c>, which is assigned in the EventSystem's
    /// OnEnable; EventSystem is not ExecuteAlways, so in edit mode it never
    /// enables, the field reads null, and <c>Selectable.Select</c> — which
    /// declines against a null EventSystem — does nothing at all. An EditMode
    /// test would have been asserting the absence of a feature.
    ///
    /// The dropdown is built by hand rather than through the menu entry because
    /// that entry lives in the editor assembly, which a PlayMode test cannot
    /// reach. Its shape is Unity's own DefaultControls geometry, which is also
    /// the shape every converted dropdown arrives in.
    /// </summary>
    public class DropdownSelectionTests
    {
        private readonly PlayHarness _harness = new PlayHarness();
        private EventSystem _events;

        [SetUp]
        public void Setup()
        {
            _harness.Setup();
            _events = _harness.EventSystem();
        }

        [TearDown]
        public void Teardown() => _harness.Teardown();

        private OneTextDropdown Dropdown()
        {
            var root = _harness.Track(new GameObject("Dropdown", typeof(RectTransform)));
            root.transform.SetParent(_harness.Canvas.transform, false);
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

            var label = _harness.Label("Item", size: 14f, boxSize: new Vector2(140f, 20f));
            label.transform.SetParent(item.transform, false);

            // Off before the component exists, so the list is never open for the
            // frame between building it and pointing the dropdown at it.
            template.SetActive(false);

            var dropdown = root.AddComponent<OneTextDropdown>();
            dropdown.template = templateRect;
            dropdown.AddOptions(new List<string> { "First", "Second", "Third" });
            return dropdown;
        }

        /// <summary>The rows of an open list: its toggles, less the one they were copied from.</summary>
        private static List<Toggle> Rows(OneTextDropdown dropdown)
        {
            var rows = new List<Toggle>();
            foreach (Transform child in dropdown.transform)
            {
                if (child.gameObject.name != "Dropdown List") continue;
                foreach (var toggle in child.GetComponentsInChildren<Toggle>(true))
                    if (toggle.gameObject.activeSelf) rows.Add(toggle);
            }
            return rows;
        }

        [UnityTest]
        public IEnumerator Opening_The_List_Puts_Focus_On_The_Chosen_Row()
        {
            var dropdown = Dropdown();
            dropdown.value = 1;

            // Focus starts where opening the list by keyboard would have left
            // it, which is the whole difficulty: on the dropdown itself. If
            // nothing moves it, this is still where it is at the assertion.
            _events.SetSelectedGameObject(dropdown.gameObject);
            yield return PlayHarness.Frame();
            Assert.AreEqual(dropdown.gameObject, EventSystem.current.currentSelectedGameObject,
                "the fixture could not put focus on the dropdown, so nothing below distinguishes " +
                "moving focus from never having had it");

            dropdown.Show();

            var rows = Rows(dropdown);
            Assert.AreEqual(3, rows.Count, "the list did not open with one row per option");
            Assert.AreEqual(rows[1].gameObject, EventSystem.current.currentSelectedGameObject,
                "opening the list left focus on the dropdown button, so the first arrow press goes " +
                "wherever that button's Automatic navigation points — anywhere else on the screen " +
                "— and the explicit navigation between the rows is never reached");

            PlayHarness.ExpectNoErrors();
        }

        /// <summary>
        /// Where focus is left when the list closes, which is the other end of
        /// the same thread.
        ///
        /// Moving focus onto a row made this a question it never used to be. The
        /// row that has focus is destroyed by the close, so unless something
        /// takes focus back the EventSystem is left pointing at an object that
        /// does not exist and the keyboard is stranded — a dropdown that can be
        /// opened and used once, after which the arrow keys do nothing anywhere.
        /// </summary>
        [UnityTest]
        public IEnumerator Choosing_An_Option_Hands_Focus_Back_To_The_Dropdown()
        {
            var dropdown = Dropdown();
            dropdown.Show();
            var rows = Rows(dropdown);
            Assert.AreEqual(3, rows.Count, "the list did not open with one row per option");
            Assert.AreNotEqual(dropdown.gameObject, EventSystem.current.currentSelectedGameObject,
                "focus never left the dropdown, so this test cannot tell the difference between " +
                "handing it back and never taking it");

            // What a click on a row amounts to: the toggle goes on, and the
            // listener Show wired to it does the choosing and the closing.
            rows[2].isOn = true;

            Assert.IsFalse(dropdown.IsExpanded, "choosing an option did not close the list");
            Assert.AreEqual(2, dropdown.value, "choosing an option did not take");

            var selected = EventSystem.current.currentSelectedGameObject;
            // Unity's null, deliberately: a destroyed row is a live C# reference
            // and NUnit's IsNotNull would be perfectly happy with it.
            Assert.IsTrue(selected != null,
                "nothing is selected once the list closes — the row that had focus was destroyed " +
                "with it and nothing took its place, so the next arrow press has nowhere to go");
            Assert.AreEqual(dropdown.gameObject, selected,
                "focus was left somewhere other than the dropdown that was just used");

            PlayHarness.ExpectNoErrors();
            yield return PlayHarness.Frame();
        }

        /// <summary>
        /// Escape over an open list.
        ///
        /// The dropdown implements ICancelHandler, which looks like enough and
        /// is not: the input module sends cancel to the selected object only,
        /// and once the list is open that is a row, not the dropdown. So the
        /// handler upstairs was never asked and Escape — and gamepad B — did
        /// nothing at all over an open list. The dispatch here is the one the
        /// input module makes, on the object it makes it on.
        /// </summary>
        [UnityTest]
        public IEnumerator Cancelling_Over_An_Open_List_Closes_It()
        {
            var dropdown = Dropdown();
            dropdown.Show();
            Assert.IsTrue(dropdown.IsExpanded, "the list is not open, so there is nothing to cancel");

            var selected = EventSystem.current.currentSelectedGameObject;
            Assert.AreNotEqual(dropdown.gameObject, selected,
                "focus is still on the dropdown, so this would be sending cancel to the object " +
                "that has always handled it and would pass either way");

            ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current),
                ExecuteEvents.cancelHandler);

            Assert.IsFalse(dropdown.IsExpanded,
                "cancel over an open list did nothing: it reaches the selected row, and the row " +
                "has no handler for it");
            Assert.AreEqual(dropdown.gameObject, EventSystem.current.currentSelectedGameObject,
                "cancelling closed the list but left focus on the row it destroyed");

            PlayHarness.ExpectNoErrors();
            yield return PlayHarness.Frame();
        }

        /// <summary>
        /// Focus follows the value rather than always landing on the first row,
        /// because a list of twenty opened on the twentieth option and focused
        /// on the first is a list the keyboard has to walk back down.
        /// </summary>
        [UnityTest]
        public IEnumerator Focus_Lands_On_Whichever_Option_Is_Chosen()
        {
            var dropdown = Dropdown();
            dropdown.value = 2;
            yield return PlayHarness.Frame();

            dropdown.Show();

            var rows = Rows(dropdown);
            Assert.AreEqual(rows[2].gameObject, EventSystem.current.currentSelectedGameObject,
                "focus did not land on the chosen option");
            Assert.IsTrue(rows[2].isOn, "the chosen row is not the one switched on either");

            PlayHarness.ExpectNoErrors();
        }
    }
}
