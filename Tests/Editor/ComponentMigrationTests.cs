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
    /// The component swap itself, on the two text components Unity ships, in
    /// memory.
    ///
    /// This is the dangerous half of the migration and it is deliberately the
    /// half with no TextMesh Pro in it: destroying a component and building
    /// another one on the same GameObject is the same code whether the thing
    /// destroyed was a TMP label or a uGUI one, so it is tested where every CI
    /// machine can run it.
    ///
    /// The reference tests are the ones that matter most. A migration that
    /// swaps components and leaves every field that pointed at them holding
    /// null produces a project that compiles, opens, and does nothing when you
    /// press the button — and the only moment that is cheap to notice is this
    /// one.
    /// </summary>
    public class ComponentMigrationTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made) if (go != null) Object.DestroyImmediate(go);
            _made.Clear();
        }

        private GameObject NewObject(string name, params System.Type[] parts)
        {
            var go = new GameObject(name, parts);
            _made.Add(go);
            return go;
        }

        private GameObject[] Roots(params GameObject[] roots) => roots;

        // ---------------------------------------------------------- uGUI Text

        [Test]
        public void UiText_BecomesALabelCarryingItsValues()
        {
            var go = NewObject("Legacy", typeof(RectTransform), typeof(CanvasRenderer));
            var text = go.AddComponent<Text>();
            text.text = "carried across";
            text.fontSize = 27;
            text.supportRichText = false;
            text.alignment = TextAnchor.LowerRight;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.lineSpacing = 1.5f;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = 44;
            text.color = new Color(0.2f, 0.4f, 0.6f, 0.8f);
            text.raycastTarget = false;

            var report = ComponentMigration.ConvertInPlace(Roots(go), "(test)", false);

            Assert.AreEqual(1, report.Converted, "nothing was swapped");
            Assert.IsNull(go.GetComponent<Text>(), "the old component is still there");

            var label = go.GetComponent<OneTextLabel>();
            Assert.NotNull(label, "no OneTextLabel arrived");
            Assert.AreEqual("carried across", label.Text);
            Assert.AreEqual(27f, label.FontSize, 1e-4f);
            Assert.IsFalse(label.RichText);
            Assert.AreEqual(TextAlignment.Right, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Bottom, label.VerticalAlignment);
            Assert.AreEqual(TextWrap.NoWrap, label.Wrap);
            Assert.AreEqual(TextOverflow.Truncate, label.Overflow);
            Assert.AreEqual(1.5f, label.LineSpacing, 1e-4f);
            Assert.IsTrue(label.AutoSize);
            Assert.AreEqual(12f, label.AutoSizeMin, 1e-4f);
            Assert.AreEqual(44f, label.AutoSizeMax, 1e-4f);
            Assert.AreEqual(new Color(0.2f, 0.4f, 0.6f, 0.8f), label.color);
            Assert.IsFalse(label.raycastTarget);
        }

        [Test]
        public void Scanning_ChangesNothing()
        {
            var go = NewObject("Legacy", typeof(RectTransform), typeof(CanvasRenderer));
            var text = go.AddComponent<Text>();
            text.text = "still here";

            var report = ComponentMigration.ScanInPlace(Roots(go), "(test)");

            Assert.AreEqual(1, report.Targets.Count, "the scan did not see it");
            Assert.AreEqual(0, report.Converted, "a scan converted something");
            Assert.NotNull(go.GetComponent<Text>(), "a scan destroyed a component");
            Assert.IsNull(go.GetComponent<OneTextLabel>(), "a scan added a component");
        }

        [Test]
        public void ConvertingTwice_FindsNothingTheSecondTime()
        {
            // Idempotence is not a nicety here: prefab variants are converted
            // by converting their base, and every one of them is then opened
            // again in its own right. If a second pass did anything at all,
            // every variant in a project would end up with two labels.
            var go = NewObject("Legacy", typeof(RectTransform), typeof(CanvasRenderer));
            go.AddComponent<Text>().text = "once";

            ComponentMigration.ConvertInPlace(Roots(go), "(test)", false);
            var second = ComponentMigration.ScanInPlace(Roots(go), "(test)");

            Assert.AreEqual(0, second.Targets.Count,
                "a converted object still looks like something to convert");
            Assert.AreEqual(1, go.GetComponents<OneTextLabel>().Length,
                "the object ended up with more than one label");
        }

        // ------------------------------------------------------------ references

        [Test]
        public void References_ArePointedAtTheReplacementOrReportedAsErrors()
        {
            var root = NewObject("Root", typeof(RectTransform));

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            labelGo.transform.SetParent(root.transform, false);
            var text = labelGo.AddComponent<Text>();
            text.text = "pointed at";

            // A field declared as Graphic can hold what replaces it.
            var buttonGo = new GameObject("Button", typeof(RectTransform));
            buttonGo.transform.SetParent(root.transform, false);
            var button = buttonGo.AddComponent<Button>();
            button.targetGraphic = text;

            // A field declared as Text cannot, and that is exactly the shape of
            // the TMP_Text field this whole module warns about.
            var fieldGo = new GameObject("Field", typeof(RectTransform));
            fieldGo.transform.SetParent(root.transform, false);
            var field = fieldGo.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = text;

            var scan = ComponentMigration.ScanInPlace(Roots(root), "(test)");
            Assert.Greater(CountOf(scan, "dangling-reference"), 0,
                "the scan did not notice anything pointing at the label");

            var report = ComponentMigration.ConvertInPlace(Roots(root), "(test)", false);
            var label = labelGo.GetComponent<OneTextLabel>();
            Assert.NotNull(label);

            Assert.AreSame(label, button.targetGraphic,
                "a Graphic field was not re-pointed at the replacement");
            Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");

            var textComponent = new SerializedObject(field).FindProperty("m_TextComponent");
            Assert.IsNull(textComponent.objectReferenceValue,
                "a Text-typed field somehow accepted a OneTextLabel");

            bool named = false;
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "dangling-reference" ||
                    finding.Severity != DoctorSeverity.Error) continue;
                StringAssert.Contains("m_TextComponent", finding.Message);
                named = true;
            }
            Assert.IsTrue(named,
                "the reference that could not be re-pointed was not reported as an error");
        }

        private static int CountOf(MigrationReport report, string rule)
        {
            int n = 0;
            foreach (var finding in report.Findings) if (finding.Rule == rule) n++;
            return n;
        }

        // ------------------------------------------------------------ TextMesh

        [Test]
        public void TextMesh_BecomesWorldTextAndGainsARect()
        {
            var go = NewObject("World");
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = "in the world";
            mesh.fontSize = 100;
            mesh.characterSize = 0.1f;
            mesh.anchor = TextAnchor.UpperCenter;
            mesh.alignment = UnityEngine.TextAlignment.Center;
            mesh.lineSpacing = 1.25f;
            mesh.color = Color.green;

            Assert.IsNull(go.GetComponent<RectTransform>(), "a TextMesh should start without one");

            var report = ComponentMigration.ConvertInPlace(Roots(go), "(test)", false);

            Assert.AreEqual(1, report.Converted);
            Assert.IsNull(go.GetComponent<TextMesh>());
            Assert.NotNull(go.GetComponent<RectTransform>(),
                "OneTextMesh lays out in a rect and one was not added");

            var world = go.GetComponent<OneTextMesh>();
            Assert.NotNull(world);
            Assert.AreEqual("in the world", world.Text);
            Assert.AreEqual(10f, world.FontSize, 1e-3f, "fontSize × characterSize is the world size");
            Assert.AreEqual(TextAlignment.Center, world.Alignment);
            Assert.AreEqual(VerticalAlignment.Top, world.VerticalAlignment);
            Assert.AreEqual(1.25f, world.LineSpacing, 1e-4f);
            Assert.AreEqual(Color.green, world.Color);
        }

        // -------------------------------------------------------------- fonts

        [Test]
        public void ABuiltInFont_IsAnErrorRatherThanAGuess()
        {
            // Arial and LegacyRuntime live inside the editor and have no file
            // to rasterise. Leaving the label fontless (so the project default
            // applies) and saying so is the only honest outcome.
            var go = NewObject("BuiltIn", typeof(RectTransform), typeof(CanvasRenderer));
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = "built in";

            var report = ComponentMigration.ScanInPlace(Roots(go), "(test)");

            Assert.AreEqual(1, CountOf(report, "font-source-missing"),
                "a built-in font was not reported");
            Assert.IsFalse(report.Passed, "that has to be an error, not a note");
        }

        [Test]
        public void UnsupportedMarkup_IsNamedBeforeItIsPrinted()
        {
            var go = NewObject("Tagged", typeof(RectTransform), typeof(CanvasRenderer));
            go.AddComponent<Text>().text = "H<sub>2</sub>O and <material=1>stuff";

            var report = ComponentMigration.ScanInPlace(Roots(go), "(test)");

            Assert.AreEqual(1, CountOf(report, "unsupported-tag"));
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "unsupported-tag") continue;
                Assert.AreEqual(DoctorSeverity.Warning, finding.Severity);
                StringAssert.Contains("sub", finding.Message);
                StringAssert.Contains("material", finding.Message);
            }
        }

        // ---------------------------------------------------------- prefab order

        [Test]
        public void Prefabs_AreOrderedBaseBeforeVariant()
        {
            // The single ordering decision the whole prefab pass rests on. A
            // variant converted first records the swap as an override on a base
            // that still holds the old component, and the object ends up
            // carrying both.
            const string folder = "Assets/OneTextMigrationOrderTest";
            string basePath = folder + "/Base.prefab";
            string variantPath = folder + "/Variant.prefab";

            try
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();

                var source = new GameObject("Base", typeof(RectTransform), typeof(CanvasRenderer));
                source.AddComponent<Text>();
                var basePrefab = PrefabUtility.SaveAsPrefabAsset(source, basePath);
                Object.DestroyImmediate(source);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
                Object.DestroyImmediate(instance);
                AssetDatabase.Refresh();

                var ordered = ComponentMigration.OrderedPrefabPaths();
                int baseIndex = ordered.IndexOf(basePath);
                int variantIndex = ordered.IndexOf(variantPath);

                Assert.GreaterOrEqual(baseIndex, 0, "the base prefab was not found");
                Assert.GreaterOrEqual(variantIndex, 0, "the variant prefab was not found");
                Assert.Less(baseIndex, variantIndex,
                    "the variant would be converted before the base it is built from");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                AssetDatabase.Refresh();
            }
        }
    }
}
