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
    /// The uGUI input field, which is here for the same reason the dropdown is:
    /// <c>m_TextComponent</c> is declared <c>Text</c>, so leaving the field alone
    /// while converting the label inside it produces a field that types into
    /// nothing. Reported as a broken reference for as long as this component was
    /// not a migration target, which is a true report of an avoidable injury.
    ///
    /// What is asserted is the part that fails quietly. A field that converts and
    /// draws is easy to see; a field that converts, draws, and has lost its
    /// pressed colour or its On End Edit wiring looks identical until somebody
    /// uses it.
    /// </summary>
    public sealed class InputFieldMigrationTests
    {
        private const string Folder = "Assets/OneTextInputFieldTest";
        private const string Path = Folder + "/InputField.prefab";

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

        private static readonly Color Normal = new Color(0.1f, 0.2f, 0.3f, 1f);
        private static readonly Color Highlighted = new Color(0.4f, 0.5f, 0.6f, 1f);

        private static string Build(out int changed, out int ended)
        {
            var root = new GameObject("InputField", typeof(RectTransform), typeof(CanvasRenderer));
            var background = root.AddComponent<Image>();
            var field = root.AddComponent<InputField>();

            var text = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            text.transform.SetParent(root.transform, false);
            var textComponent = text.AddComponent<Text>();
            textComponent.font = null;

            var placeholder = new GameObject("Placeholder", typeof(RectTransform),
                typeof(CanvasRenderer));
            placeholder.transform.SetParent(root.transform, false);
            var placeholderComponent = placeholder.AddComponent<Text>();
            placeholderComponent.font = null;
            placeholderComponent.text = "Enter name...";

            field.textComponent = textComponent;
            field.placeholder = placeholderComponent;
            field.targetGraphic = background;
            // The limit first would truncate the value on the way in, and the
            // assertion would then be measuring the fixture.
            field.characterLimit = 32;
            field.text = "typed already";
            field.interactable = false;

            var colors = field.colors;
            colors.normalColor = Normal;
            colors.highlightedColor = Highlighted;
            field.colors = colors;

            // Both events, because they are carried by two different paths and
            // On End Edit is the one that used to be dropped outright.
            UnityEventTools.AddPersistentListener(field.onValueChanged, root.SendMessage);
            UnityEventTools.AddPersistentListener(field.onEndEdit, root.SendMessage);
            changed = field.onValueChanged.GetPersistentEventCount();
            ended = field.onEndEdit.GetPersistentEventCount();

            PrefabUtility.SaveAsPrefabAsset(root, Path);
            Object.DestroyImmediate(root);
            return Path;
        }

        [Test]
        public void AUGuiInputField_BecomesOneTexts_KeepingItsLabels_ColoursAndWiring()
        {
            string path = Build(out int changed, out int ended);
            Assert.Greater(changed, 0, "the fixture wired no value-changed listener");
            Assert.Greater(ended, 0, "the fixture wired no end-edit listener");
            AssetDatabase.Refresh();

            var report = ComponentMigration.Apply(new ComponentMigration.Options
            {
                IncludeScenes = false,
                OnlyContainers = new List<string> { path },
            });
            Assert.Greater(report.Converted, 0, "nothing was converted");

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNull(root.GetComponent<InputField>(), "the old input field is still there");

            var made = root.GetComponent<OneTextInputField>();
            Assert.IsNotNull(made, "no OneTextInputField replaced it");

            Assert.IsNotNull(made.textComponent, "the text component was not re-pointed");
            Assert.AreEqual("Text", made.textComponent.gameObject.name);
            Assert.IsNotNull(made.placeholder, "the placeholder was not re-pointed");
            Assert.AreEqual("Placeholder", made.placeholder.gameObject.name);

            Assert.AreEqual("typed already", made.text, "the value did not survive");
            Assert.AreEqual(32, made.characterLimit);
            Assert.IsFalse(made.interactable, "Selectable's own state did not survive");

            Assert.AreEqual(Normal, made.colors.normalColor,
                "the colour block was replaced by the defaults, which is a field that converts, " +
                "reports nothing, and stops looking like the rest of the UI");
            Assert.AreEqual(Highlighted, made.colors.highlightedColor);

            Assert.AreEqual(changed, made.onValueChanged.GetPersistentEventCount(),
                "the inspector's wiring on On Value Changed was dropped");
            Assert.AreEqual(ended, made.onEndEdit.GetPersistentEventCount(),
                "the inspector's wiring on On End Edit was dropped, and nothing else records it");

            foreach (var finding in report.Findings)
            {
                Assert.AreNotEqual("reference-would-break", finding.Rule,
                    "the label inside the field held back the conversion, which is what making " +
                    "the field a migration target was supposed to stop: " + finding.Message);
            }
        }
    }
}
