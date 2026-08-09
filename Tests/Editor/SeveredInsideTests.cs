using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// A field naming a label in its own file, after the script rewrite has
    /// widened it — the commonest shape in a real project and the one that was
    /// silent longest.
    ///
    /// The order is what does it. Scripts are rewritten first, so by the time
    /// anything is converted the field says <c>OneTextLabel</c> while the file
    /// still names a <c>Text</c>, and Unity does not merely refuse to bind that:
    /// it throws the pointer away, so the loaded component reads 0. The census
    /// asks loaded components. It found nothing, mended nothing, and reported
    /// nothing, and the first anybody heard of it was a NullReferenceException
    /// on somebody's title screen.
    ///
    /// The cross-file version of this was fixed first because it was the visible
    /// one — a prefab in another folder went wrong. This is the version where the
    /// controller and the label it drives are in one prefab, which is most of
    /// them.
    /// </summary>
    public sealed class SeveredInsideTests
    {
        private const string Folder = "Assets/OneTextSeveredInsideTest";

        [SetUp]
        public void MakeFolder()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void DropFolder()
        {
            AssetDatabase.DeleteAsset(Folder);
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

        /// <summary>
        /// One prefab: a controller at the root and the label it names on a child
        /// of the same prefab, with the controller's script swapped underneath it
        /// for one whose field is a <c>OneTextLabel</c>.
        ///
        /// Swapped in the file rather than through the editor because the editor
        /// will not write this state — assigning a <c>Text</c> to a
        /// <c>OneTextLabel</c> field is refused. Editing a .cs file produces it
        /// for free, in every real migration, which is the only reason it has to
        /// be built this way to be stood in at all.
        /// </summary>
        private static string Build(string name)
        {
            string path = $"{Folder}/{name}.prefab";

            var root = new GameObject(name, typeof(RectTransform));
            var child = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            child.transform.SetParent(root.transform, false);
            var text = child.AddComponent<Text>();
            text.font = null;
            text.text = "named from inside the same file";

            root.AddComponent<CrossContainerTyped>().Typed = text;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            string before = ScriptGuidOf<CrossContainerTyped>();
            string after = ScriptGuidOf<CrossContainerRewritten>();
            string file = File.ReadAllText(path);

            // Without a real fileID in the field there is nothing to sever, and
            // the test would be demanding a fix for something no fix can give.
            Assert.IsTrue(Regex.IsMatch(file, @"Typed: \{fileID: -?[1-9]"),
                "the saved prefab does not name a component in its 'Typed' field, so the state " +
                "this test exists to stand in has not been built");
            Assert.IsTrue(file.Contains(before), "the prefab does not name the script to swap");

            File.WriteAllText(path, file.Replace(before, after));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        private static ComponentMigration.Options Only(string container) =>
            new ComponentMigration.Options
            {
                IncludeScenes = false,
                OnlyContainers = new List<string> { container },
            };

        [Test]
        public void AFieldNamingALabelInItsOwnFile_SurvivesTheRewriteAndTheConversion()
        {
            string path = Build("Inside");

            // The premise, measured rather than assumed: after the swap the
            // loaded component holds nothing, which is why the ordinary census
            // cannot see this reference and why this test is not redundant.
            var host = AssetDatabase.LoadAssetAtPath<GameObject>(path)
                .GetComponent<CrossContainerRewritten>();
            Assert.IsNotNull(host, "the script swap did not take");
            Assert.IsTrue(host.Typed == null,
                "the field still binds after the swap, so this fixture is not standing where the " +
                "loss is and would pass without the fix");

            var report = ComponentMigration.Apply(Only(path));
            Assert.Greater(report.Converted, 0, "nothing was converted");

            var made = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var label = made.GetComponentInChildren<OneTextLabel>(true);
            Assert.IsNotNull(label, "the label was not converted");

            var after = made.GetComponent<CrossContainerRewritten>();
            Assert.IsFalse(after.Typed == null,
                "the field the file named is still empty. Nothing else records what it pointed " +
                "at, so this is the reference gone for good — and the run reported success.");
            Assert.AreSame(label, after.Typed,
                "the field was filled with something other than the label that replaced its target");
        }

        [Test]
        public void TheMend_IsCountedAsOne()
        {
            string path = Build("Counted");
            var report = ComponentMigration.Apply(Only(path));

            // Counted, because 'relinked 38' against 134 severed fields was the
            // number that made this look finished.
            Assert.Greater(report.Relinked, 0,
                "the reference was mended without being counted, which is how a run reports a " +
                "clean conversion over a project full of empty fields");
        }
    }
}
