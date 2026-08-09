using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The rule that keeps this from being a wrecking ball: a component whose
    /// conversion would leave a field naming nothing is not converted.
    ///
    /// The case is <c>UnityEngine.UI.Text</c>, and it is different in kind from
    /// the TextMesh Pro one. A field declared <c>TMP_Text</c> is narrow today and
    /// wide after the script rewrite runs, so converting and reporting is fair —
    /// the remedy is a button in the same tab. A field declared <c>Text</c> is
    /// narrow forever, because this migration deliberately does not rewrite that
    /// name in source: too many things are called Text. Converting the label it
    /// names would trade a working reference for nothing.
    ///
    /// Both directions are asserted here. A rule that refuses everything is as
    /// wrong as one that refuses nothing, and the only proof it is neither is a
    /// wide field and a narrow field pointed at the same component.
    /// </summary>
    public sealed class WithheldReferenceTests
    {
        private const string Folder = "Assets/OneTextWithheldTest";

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
        /// One label, and above it whichever of the two referrers the test wants:
        /// a <c>Button</c> whose <c>targetGraphic</c> is a <c>Graphic</c>, or a
        /// script whose field is a <c>Text</c>.
        /// </summary>
        private static string Build(string name, bool narrow, bool wide)
        {
            string path = $"{Folder}/{name}.prefab";

            var root = new GameObject(name, typeof(RectTransform));
            var child = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            child.transform.SetParent(root.transform, false);
            var text = child.AddComponent<Text>();
            text.font = null;
            text.text = "named by something";

            if (narrow) root.AddComponent<CrossContainerTyped>().Typed = text;
            if (wide) root.AddComponent<Button>().targetGraphic = text;

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return path;
        }

        private static ComponentMigration.Options Only(string container) =>
            new ComponentMigration.Options
            {
                IncludeScenes = false,
                OnlyContainers = new List<string> { container },
            };

        private static bool Says(MigrationReport report, string rule)
        {
            foreach (var finding in report.Findings) if (finding.Rule == rule) return true;
            return false;
        }

        [Test]
        public void ALabelNamedByATextField_IsLeftAlone_AndTheFieldStillNamesIt()
        {
            string path = Build("Narrow", narrow: true, wide: false);
            AssetDatabase.Refresh();

            var report = ComponentMigration.Apply(Only(path));

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNull(root.GetComponentInChildren<OneTextLabel>(true),
                "the label was converted even though a Text field names it, which empties that " +
                "field with nothing able to fill it again");

            var referrer = root.GetComponent<CrossContainerTyped>();
            Assert.IsFalse(referrer.Typed == null,
                "the field that was the whole reason to hold back reads None anyway");
            Assert.AreEqual("Label", referrer.Typed.gameObject.name);

            Assert.IsTrue(Says(report, "reference-would-break"),
                "nothing was converted and nothing said why, which is the same to the person " +
                "reading the report as the migration having missed it");
        }

        [Test]
        public void ALabelNamedOnlyByAGraphicField_IsConverted()
        {
            string path = Build("Wide", narrow: false, wide: true);
            AssetDatabase.Refresh();

            var report = ComponentMigration.Apply(Only(path));

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(root.GetComponentInChildren<OneTextLabel>(true),
                "a Graphic field is wide enough to hold a OneTextLabel, so holding this one back " +
                "is the rule refusing work it was never meant to refuse");

            var button = root.GetComponent<Button>();
            Assert.IsFalse(button.targetGraphic == null, "the wide field was not re-pointed");
            Assert.IsInstanceOf<OneTextLabel>(button.targetGraphic);

            Assert.IsFalse(Says(report, "reference-would-break"),
                "the run reported holding something back that it converted");
        }

        [Test]
        public void OneNarrowFieldHoldsBackTheLabel_EvenWhenAWideOneAlsoNamesIt()
        {
            string path = Build("Both", narrow: true, wide: true);
            AssetDatabase.Refresh();

            ComponentMigration.Apply(Only(path));

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNull(root.GetComponentInChildren<OneTextLabel>(true),
                "converting for the sake of the field that can hold it breaks the field that " +
                "cannot, and the one that cannot has no remedy");
            Assert.IsFalse(root.GetComponent<CrossContainerTyped>().Typed == null);
        }
    }
}
