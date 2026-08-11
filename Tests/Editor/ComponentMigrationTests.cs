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

        /// <summary>
        /// The two shapes of field that can name a label, and the two different
        /// reasons neither one is left reading None.
        ///
        /// A <c>Button</c>'s <c>targetGraphic</c> is declared <c>Graphic</c>, so
        /// it simply takes the replacement and is re-pointed. An
        /// <c>InputField</c>'s <c>m_TextComponent</c> is declared <c>Text</c> and
        /// never could be, which is why the input field is a migration target in
        /// its own right rather than a bystander to be apologised to afterwards.
        /// </summary>
        [Test]
        public void AWideFieldAndAnInputField_BothEndUpNamingTheReplacement()
        {
            var root = NewObject("Root", typeof(RectTransform));

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            labelGo.transform.SetParent(root.transform, false);
            var text = labelGo.AddComponent<Text>();
            text.text = "pointed at";

            var buttonGo = new GameObject("Button", typeof(RectTransform));
            buttonGo.transform.SetParent(root.transform, false);
            var button = buttonGo.AddComponent<Button>();
            button.targetGraphic = text;

            var fieldGo = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer));
            fieldGo.transform.SetParent(root.transform, false);
            var field = fieldGo.AddComponent<InputField>();
            field.textComponent = text;

            var report = ComponentMigration.ConvertInPlace(Roots(root), "(test)", false);
            var label = labelGo.GetComponent<OneTextLabel>();
            Assert.NotNull(label, "the label was not converted");

            Assert.AreSame(label, button.targetGraphic,
                "a Graphic field was not re-pointed at the replacement");
            Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");

            Assert.IsTrue(field == null, "the old input field is still there");
            var made = fieldGo.GetComponent<OneTextInputField>();
            Assert.NotNull(made, "the input field was left as a bystander rather than converted");
            Assert.AreSame(label, made.textComponent,
                "the converted field does not name the label it used to, so it types into nothing");

            foreach (var finding in report.Findings)
            {
                Assert.AreNotEqual("reference-would-break", finding.Rule,
                    "something was held back, which means one of these two fields was read as " +
                    "unable to hold the replacement: " + finding.Message);
                Assert.AreNotEqual("dangling-reference", finding.Rule,
                    "a reference was left reading None: " + finding.Message);
            }
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
            // <sub> used to be here and is not any more: the parser learned it,
            // so naming it would be the report sending somebody to rewrite text
            // that works. <rotate> and <material> are still genuinely printed.
            go.AddComponent<Text>().text =
                "H<sub>2</sub>O <rotate=8>turned</rotate> and <material=1>stuff";

            var report = ComponentMigration.ScanInPlace(Roots(go), "(test)");

            Assert.AreEqual(1, CountOf(report, "unsupported-tag"));
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "unsupported-tag") continue;
                Assert.AreEqual(DoctorSeverity.Warning, finding.Severity);
                StringAssert.Contains("rotate", finding.Message);
                StringAssert.Contains("material", finding.Message);
                StringAssert.DoesNotContain("sub", finding.Message,
                    "a tag OneText now obeys is still being reported as lost");
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

        // ------------------------------------------------- across containers

        // The other half of the reference problem, and the half that is silent.
        //
        // These touch the asset database, which the tests above go out of their
        // way not to, because the failure only exists on disk: a field in one
        // file naming a component in another is broken by writing the second
        // file, at a moment when the first is not open and nothing in memory
        // knows it exists. So each of these builds real prefabs and real assets,
        // runs the whole of Apply over them, and reads the files back — which is
        // the only vantage point the bug is visible from.

        private const string CrossFolder = "Assets/OneTextCrossContainerTest";

        private static void MakeFolder()
        {
            System.IO.Directory.CreateDirectory(CrossFolder);
            AssetDatabase.Refresh();
        }

        private static void DropFolder()
        {
            AssetDatabase.DeleteAsset(CrossFolder);
            AssetDatabase.Refresh();
        }

        private static ComponentMigration.Options Only(params string[] containers) =>
            new ComponentMigration.Options
            {
                IncludeScenes = false,
                OnlyContainers = new List<string>(containers),
            };

        /// <summary>
        /// A prefab with the label one level down, which is the shape that
        /// breaks: anything pointing at it is naming a component in a file of
        /// its own.
        /// </summary>
        private static string Leaf(string name, bool withAScript = false)
        {
            string path = $"{CrossFolder}/{name}.prefab";

            var root = new GameObject(name, typeof(RectTransform));
            if (withAScript) root.AddComponent<LayoutElement>();

            var label = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            label.transform.SetParent(root.transform, false);

            var text = label.AddComponent<Text>();
            text.font = null; // no font file behind it, so the run writes no font asset anywhere
            text.text = "across the file boundary";

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return path;
        }

        /// <summary>
        /// A prefab that nests another one. With <paramref name="pointAtTheText"/>
        /// it also holds a field naming the label inside it; without, it holds no
        /// component of its own at all, which is what makes it a middle link
        /// rather than an end of the chain.
        /// </summary>
        private static string Wrapper(string name, string nestedPath, bool pointAtTheText)
        {
            string path = $"{CrossFolder}/{name}.prefab";

            var root = new GameObject(name, typeof(RectTransform));
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath));
            nested.transform.SetParent(root.transform, false);

            // A Graphic field is wide enough to hold a OneTextLabel, so the only
            // way it can end up reading None is that nobody re-pointed it.
            if (pointAtTheText)
                root.AddComponent<Button>().targetGraphic = nested.GetComponentInChildren<Text>(true);

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return path;
        }

        private static string Host(string leafPath) => Wrapper("Host", leafPath, true);

        private static string Holder(string name, Graphic wide, Text narrow)
        {
            string path = $"{CrossFolder}/{name}.asset";
            var holder = ScriptableObject.CreateInstance<CrossContainerHolder>();
            holder.Label = wide;
            holder.Typed = narrow;
            AssetDatabase.CreateAsset(holder, path);
            AssetDatabase.SaveAssets();
            return path;
        }

        private static Text LabelIn(string prefabPath) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponentInChildren<Text>(true);

        [Test]
        public void APrefabPointingIntoAnotherPrefab_IsPointedAtWhatReplacedIt()
        {
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string host = Host(leaf);
                AssetDatabase.Refresh();

                var report = ComponentMigration.Apply(Only(leaf, host));

                var button = AssetDatabase.LoadAssetAtPath<GameObject>(host).GetComponent<Button>();
                Assert.IsFalse(button.targetGraphic == null,
                    "the field reads None: the prefab it named was converted and nothing went " +
                    "back to the file that was pointing at it");
                Assert.IsInstanceOf<OneTextLabel>(button.targetGraphic,
                    "the field holds something other than the OneText component that replaced " +
                    "its target");
                Assert.AreEqual("Label", button.targetGraphic.gameObject.name,
                    "the field was pointed at the wrong object");

                Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");
                Assert.Greater(CountOf(report, ContainerReferences.Rule), 0,
                    "the reference that crossed files was never mentioned in the report");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void APrefabPointingTwoLevelsDownIntoANestedPrefab_IsStillMended()
        {
            // Measured on a real project: this shape was silent. The field names
            // a component whose file is two prefabs away — Outer nests Middle
            // nests Leaf, and the label is written in Leaf. Asking Unity once
            // where the component came from answers "Middle", and Middle is the
            // one file in the chain that converts nothing, holds no component of
            // its own and is never written. A reference recorded against it
            // waits for a change that is never announced, while Leaf, which did
            // change, goes unmentioned. Neither mended nor reported, which is
            // the one outcome this module exists to make impossible.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string middle = Wrapper("Middle", leaf, pointAtTheText: false);
                string outer = Wrapper("Outer", middle, pointAtTheText: true);
                AssetDatabase.Refresh();

                var report = ComponentMigration.Apply(Only(leaf, middle, outer));

                var button = AssetDatabase.LoadAssetAtPath<GameObject>(outer).GetComponent<Button>();
                Assert.IsFalse(button.targetGraphic == null,
                    "the field reads None: the label two files down was converted and nothing " +
                    "went back to the file that was pointing at it");
                Assert.IsInstanceOf<OneTextLabel>(button.targetGraphic,
                    "the field holds something other than the OneText component that replaced " +
                    "its target");
                Assert.AreEqual("Label", button.targetGraphic.gameObject.name,
                    "the field was pointed at the wrong object");
                Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");

                // Whatever else happens, this shape must never be silent again.
                Assert.Greater(CountOf(report, ContainerReferences.Rule), 0,
                    "a reference that crossed two files was neither mended nor named");
            }
            finally { DropFolder(); }
        }

        /// <summary>
        /// The host prefab as it stands after the script rewrite: the field is
        /// declared as the OneText type, and the file still names the Text it
        /// was pointing at before anybody edited the script.
        ///
        /// The swap is done to the saved file rather than through the editor
        /// because the editor will not write a mismatch — assigning a
        /// <c>Text</c> to a <c>OneTextLabel</c> field through a SerializedObject
        /// is refused. Editing the .cs file creates exactly this mismatch for
        /// free, in every real migration, which is why it has to be built this
        /// way to be seen at all.
        /// </summary>
        private static string RewrittenHost(string leafPath)
        {
            string path = $"{CrossFolder}/RewrittenHost.prefab";

            var root = new GameObject("RewrittenHost", typeof(RectTransform));
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(leafPath));
            nested.transform.SetParent(root.transform, false);
            root.AddComponent<CrossContainerTyped>().Typed =
                nested.GetComponentInChildren<Text>(true);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            string before = ScriptGuidOf<CrossContainerTyped>();
            string after = ScriptGuidOf<CrossContainerRewritten>();
            string text = System.IO.File.ReadAllText(path);
            Assert.IsTrue(text.Contains(before), "the saved prefab does not name the script to swap");

            // The file has to be naming a component for any of this to mean
            // anything. If the field ever saved as {fileID: 0} — a fixture that
            // stopped finding the label, a Unity that stopped writing the
            // reference — the test below would still see an empty field, still
            // demand it be filled, and be asking for something no fix can give.
            Assert.IsTrue(
                System.Text.RegularExpressions.Regex.IsMatch(text, @"Typed: \{fileID: [1-9]"),
                "the saved prefab does not name a component in its 'Typed' field, so the state " +
                "this test exists to stand in has not been built");

            System.IO.File.WriteAllText(path, text.Replace(before, after));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        [Test]
        public void AFieldTheScriptRewriteAlreadyEmptied_IsStillMendedOrNamed()
        {
            // Measured on a real project, and the one shape that is silent.
            //
            // Scripts are rewritten before components are converted — that is
            // the order the Hub asks for and the only order that compiles. The
            // moment the assembly reloads, every narrowly typed field naming a
            // TMP component reads None, because the file still names a component
            // the field can no longer hold. The census then runs, reads the
            // field, finds nothing there, and writes nothing down: it only ever
            // records references that are alive when it looks. Nothing is
            // recorded, so nothing is mended and nothing is reported, and the
            // conversion goes ahead over a reference that has already been cut.
            //
            // The field is mendable — it is declared as the type that is about
            // to exist — which is what makes this worse than the refusals the
            // module already reports. Those tell the user where to go; this one
            // leaves a project with empty fields and a clean report.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string host = RewrittenHost(leaf);
                AssetDatabase.Refresh();

                var rewritten = AssetDatabase.LoadAssetAtPath<GameObject>(host)
                    .GetComponent<CrossContainerRewritten>();
                Assert.IsNotNull(rewritten, "the script swap did not take");
                Assert.IsTrue(rewritten.Typed == null,
                    "the field still holds something, so this test is no longer standing where " +
                    "the bug is: the point of it is that the rewrite emptied the field before " +
                    "the migration ever ran");

                var report = ComponentMigration.Apply(Only(leaf, host));

                var after = AssetDatabase.LoadAssetAtPath<GameObject>(host)
                    .GetComponent<CrossContainerRewritten>();
                Assert.IsFalse(after.Typed == null,
                    "the field reads None: it was emptied by the script rewrite, the component " +
                    "it named was replaced during this run, and nothing put the replacement back");
                Assert.AreEqual("Label", after.Typed.gameObject.name,
                    "the field was pointed at the wrong object");

                // Whatever else happens, this must never be silent: a run that
                // cannot mend it has to say which field it left empty.
                Assert.Greater(CountOf(report, ContainerReferences.Rule), 0,
                    "a field this run left empty was neither mended nor named");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void AScriptableObjectPointingIntoAPrefab_IsPointedAtWhatReplacedIt()
        {
            // The second half of the same hole. Nothing in the migration has ever
            // opened a ScriptableObject, and a TMP_Text field on a settings asset
            // is an ordinary thing for a project to have.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string asset = Holder("Wide", LabelIn(leaf), null);
                AssetDatabase.Refresh();

                var report = ComponentMigration.Apply(Only(leaf));

                var holder = AssetDatabase.LoadAssetAtPath<CrossContainerHolder>(asset);
                Assert.IsFalse(holder.Label == null,
                    "the asset's field reads None after the prefab it named was converted");
                Assert.IsInstanceOf<OneTextLabel>(holder.Label,
                    "the asset's field was not pointed at the replacement");
                Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void AFieldStillDeclaredAsTheOldType_IsReportedRatherThanLeftInSilence()
        {
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string asset = Holder("Narrow", null, LabelIn(leaf));
                AssetDatabase.Refresh();

                var report = ComponentMigration.Apply(Only(leaf));

                var holder = AssetDatabase.LoadAssetAtPath<CrossContainerHolder>(asset);
                Assert.IsTrue(holder.Typed == null,
                    "a Text-typed field somehow accepted a OneTextLabel");

                bool named = false;
                foreach (var finding in report.Findings)
                {
                    if (finding.Rule != ContainerReferences.Rule ||
                        finding.Severity != DoctorSeverity.Error) continue;
                    StringAssert.Contains("Typed", finding.Message);
                    StringAssert.Contains(leaf, finding.Message);
                    named = true;
                }
                Assert.IsTrue(named,
                    "a field that refused the replacement was left to be discovered at runtime");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void ATargetThatCouldNotBeSaved_LeavesTheAssetsPointingAtItAlone()
        {
            // A prefab holding a missing script is refused before anything in it
            // is converted, so it is unchanged on disk — which means every
            // reference into it is still perfectly good, and re-pointing one
            // would be a write nobody asked for in a file nobody expected to see
            // in the diff.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf", withAScript: true);
                string asset = Holder("Wide", LabelIn(leaf), null);
                BreakTheScriptOn(leaf, ScriptGuidOf<LayoutElement>());

                var report = ComponentMigration.Apply(Only(leaf));

                var holder = AssetDatabase.LoadAssetAtPath<CrossContainerHolder>(asset);
                Assert.IsInstanceOf<Text>(holder.Label,
                    "the prefab was refused and never written, so nothing changed under this " +
                    "asset and its field should still hold the component it always held");
                Assert.AreEqual(0, report.Relinked,
                    "a reference was re-pointed at a container that was never converted");
                Assert.Greater(CountOf(report, "unsaveable-container"), 0,
                    "the prefab with the missing script was not refused");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void RunningItTwice_RePointsNothingTheSecondTime()
        {
            // The first run leaves every one of these fields holding a live
            // OneText component, and a field that holds something was not broken
            // by anything — so the second run has to leave it exactly alone
            // rather than count it again or write the file again.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string host = Host(leaf);
                AssetDatabase.Refresh();

                ComponentMigration.Apply(Only(leaf, host));
                var second = ComponentMigration.Apply(Only(leaf, host));

                Assert.AreEqual(0, second.Converted, "a second run swapped something");
                Assert.AreEqual(0, second.Relinked, "a second run re-pointed something");

                var button = AssetDatabase.LoadAssetAtPath<GameObject>(host).GetComponent<Button>();
                Assert.IsInstanceOf<OneTextLabel>(button.targetGraphic,
                    "the second run undid what the first one mended");
            }
            finally { DropFolder(); }
        }

        // ----------------------------------------------- converting a corner

        // The tests above hand every file in the chain to the same run, which is
        // what the module used to require of anybody who wanted their references
        // to survive. These hand it one file and leave the referrer out, which is
        // what a person actually does when they migrate a project a corner at a
        // time — and which used to be the shape that quietly emptied fields.

        [Test]
        public void APrefabLeftOutOfTheRun_IsStillPointedAtWhatReplacedItsTarget()
        {
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string host = Host(leaf);
                AssetDatabase.Refresh();

                // Only the leaf. The host is not selected, not scanned, and not
                // converted — it is simply a file in the project that happens to
                // name a component inside the one being converted.
                var report = ComponentMigration.Apply(Only(leaf));

                var button = AssetDatabase.LoadAssetAtPath<GameObject>(host).GetComponent<Button>();
                Assert.IsFalse(button.targetGraphic == null,
                    "the field reads None: converting one prefab emptied a field in another that " +
                    "was never part of the run, which is the whole reason partial migrations were " +
                    "not worth offering");
                Assert.IsInstanceOf<OneTextLabel>(button.targetGraphic,
                    "the field holds something other than the component that replaced its target");
                Assert.AreEqual("Label", button.targetGraphic.gameObject.name,
                    "the field was pointed at the wrong object");
                Assert.Greater(report.Relinked, 0, "nothing was counted as re-linked");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void ARunOverOneContainer_SaysNothingAboutHavingBeenNarrow_WhenItMendedEverything()
        {
            // The warning that used to end every narrow run — "convert the whole
            // project and every one of those is found" — was true and is not any
            // more. A warning that fires when nothing is wrong is how the real
            // ones stop being read.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                Host(leaf);
                AssetDatabase.Refresh();

                var report = ComponentMigration.Apply(Only(leaf));

                foreach (var finding in report.Findings)
                {
                    if (finding.Rule != ContainerReferences.Rule) continue;
                    Assert.AreNotEqual(DoctorSeverity.Error, finding.Severity,
                        "a reference was left broken: " + finding.Message);
                    Assert.AreNotEqual(DoctorSeverity.Warning, finding.Severity,
                        "the run mended every reference into it and then warned that it had not: " +
                        finding.Message);
                }
            }
            finally { DropFolder(); }
        }

        [Test]
        public void APrefabLeftOutOfTheRun_ThatPointsAtNothingWhichChanged_IsNotWritten()
        {
            // The mend pass is bounded by what actually converted. A project's
            // other four hundred prefabs must not turn up in the diff of a run
            // that touched one, or "convert a corner" costs the same review as
            // converting everything.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string other = Leaf("Other");
                AssetDatabase.Refresh();

                var before = System.IO.File.GetLastWriteTimeUtc(System.IO.Path.GetFullPath(other));
                ComponentMigration.Apply(Only(leaf));

                Assert.IsNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(other)
                        .GetComponentInChildren<OneTextLabel>(true),
                    "a prefab that was not selected was converted anyway");
                Assert.AreEqual(before,
                    System.IO.File.GetLastWriteTimeUtc(System.IO.Path.GetFullPath(other)),
                    "a prefab that pointed at nothing this run changed was written anyway");
            }
            finally { DropFolder(); }
        }

        [Test]
        public void SelectingOnlyTheHost_ConvertsTheLeafItIsBuiltOutOf_AndNotTwice()
        {
            // Picking the outer prefab and not the inner one is the obvious thing
            // to do and used to be the wrong one: the label lives in the leaf's
            // file, converting it from inside the host writes an override, and
            // the day the leaf is converted for itself the object carries both
            // components. The run widens the selection instead.
            MakeFolder();
            try
            {
                string leaf = Leaf("Leaf");
                string host = Host(leaf);
                AssetDatabase.Refresh();

                var first = ComponentMigration.Apply(Only(host));
                Assert.Greater(CountOf(first, "nested-selection"), 0,
                    "the leaf was pulled into the run without the report saying so");

                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(leaf)
                        .GetComponentInChildren<OneTextLabel>(true),
                    "the label was converted somewhere other than the file it lives in");

                var second = ComponentMigration.Apply(Only(leaf));
                Assert.AreEqual(0, second.Converted, "the second run had something left to swap");

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(host);
                Assert.AreEqual(1, root.GetComponentsInChildren<OneTextLabel>(true).Length,
                    "the host ended up holding the label twice");
                Assert.IsInstanceOf<OneTextLabel>(root.GetComponent<Button>().targetGraphic,
                    "the field that named the label was left holding " +
                    (root.GetComponent<Button>().targetGraphic == null ? "None" : "the wrong thing"));
            }
            finally { DropFolder(); }
        }

        /// <summary>
        /// Points one component's script at a guid nothing owns, which is how a
        /// prefab comes to hold a missing script without anybody having to ship
        /// one.
        /// </summary>
        private static void BreakTheScriptOn(string assetPath, string scriptGuid)
        {
            string file = System.IO.Path.GetFullPath(assetPath);
            string yaml = System.IO.File.ReadAllText(file);
            if (!yaml.Contains(scriptGuid))
            {
                Assert.Ignore("this test rewrites a prefab's YAML and this project does not " +
                              "serialize prefabs as text.");
            }

            System.IO.File.WriteAllText(file,
                yaml.Replace(scriptGuid, "0123456789abcdef0123456789abcdef"));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
        }

        private static string ScriptGuidOf<T>() where T : MonoBehaviour
        {
            var probe = new GameObject("probe");
            try
            {
                var script = MonoScript.FromMonoBehaviour(probe.AddComponent<T>());
                return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
            }
            finally { Object.DestroyImmediate(probe); }
        }
    }
}
