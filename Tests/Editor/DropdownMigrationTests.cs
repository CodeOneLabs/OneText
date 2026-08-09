using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The dropdown swap, and specifically the parts of it that fail quietly.
    ///
    /// A dropdown that converts, reports nothing and comes out with an empty
    /// options list is worse than one that refuses: the scene looks right in the
    /// hierarchy, the caption is blank at runtime, and nothing in the report
    /// points at it. Same for the event — a designer's inspector wiring is not
    /// recoverable from anywhere else once it is gone. So both are asserted
    /// here rather than left to the swap being "obviously" total.
    /// </summary>
    public sealed class DropdownMigrationTests
    {
        private const string Folder = "Assets/OneTextDropdownTest";
        private const string Path = Folder + "/Dropdown.prefab";

        [SetUp]
        public void MakeFolder()
        {
            System.IO.Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void DropFolder()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// A dropdown the way a scene has one: a caption label, a template with
        /// one item in it, and options a designer typed.
        /// </summary>
        private static string Build(out int listeners)
        {
            var root = new GameObject("Dropdown", typeof(RectTransform), typeof(CanvasRenderer));
            var dropdown = root.AddComponent<Dropdown>();

            var caption = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            caption.transform.SetParent(root.transform, false);
            var captionText = caption.AddComponent<Text>();
            captionText.font = null;

            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(root.transform, false);
            var item = new GameObject("Item", typeof(RectTransform));
            item.transform.SetParent(template.transform, false);
            item.AddComponent<Toggle>();
            var itemLabel = new GameObject("Item Label", typeof(RectTransform), typeof(CanvasRenderer));
            itemLabel.transform.SetParent(item.transform, false);
            var itemText = itemLabel.AddComponent<Text>();
            itemText.font = null;

            dropdown.template = template.transform as RectTransform;
            dropdown.captionText = captionText;
            dropdown.itemText = itemText;
            dropdown.options = new List<Dropdown.OptionData>
            {
                new Dropdown.OptionData("First"),
                new Dropdown.OptionData("Second"),
                new Dropdown.OptionData("Third"),
            };
            dropdown.value = 2;
            dropdown.interactable = false;

            // Inspector wiring, which is the thing with nowhere else to live.
            UnityEventTools.AddPersistentListener(dropdown.onValueChanged,
                root.transform.SetSiblingIndex);
            listeners = dropdown.onValueChanged.GetPersistentEventCount();

            PrefabUtility.SaveAsPrefabAsset(root, Path);
            Object.DestroyImmediate(root);
            return Path;
        }

        [Test]
        public void ADropdown_KeepsItsOptions_AndItsWiring_AndGetsOneTextLabels()
        {
            string path = Build(out int listeners);
            Assert.Greater(listeners, 0, "the fixture did not wire a listener, so this test is " +
                                         "not standing where the loss would be");
            AssetDatabase.Refresh();

            var report = ComponentMigration.Apply(new ComponentMigration.Options
            {
                IncludeScenes = false,
                OnlyContainers = new List<string> { path },
            });
            Assert.Greater(report.Converted, 0, "nothing was converted");

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNull(root.GetComponent<Dropdown>(), "the old dropdown is still there");

            var made = root.GetComponent<OneTextDropdown>();
            Assert.IsNotNull(made, "no OneTextDropdown replaced it");

            Assert.AreEqual(3, made.options.Count, "the options list did not survive the swap");
            Assert.AreEqual("First", made.options[0].text);
            Assert.AreEqual("Third", made.options[2].text);
            Assert.AreEqual(2, made.value, "the selected value did not survive");
            Assert.IsFalse(made.interactable, "Selectable's own state did not survive");

            Assert.AreEqual(listeners, made.onValueChanged.GetPersistentEventCount(),
                "the inspector's wiring on onValueChanged was dropped, which nothing else " +
                "records and nobody can recover");

            Assert.IsNotNull(made.captionText,
                "the caption label was not re-pointed at what replaced it");
            Assert.AreEqual("Label", made.captionText.gameObject.name);
            Assert.IsNotNull(made.itemText, "the item label was not re-pointed");
            Assert.AreEqual("Item Label", made.itemText.gameObject.name);
        }
    }
}
