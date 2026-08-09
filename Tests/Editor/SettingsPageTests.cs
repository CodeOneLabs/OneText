using NUnit.Framework;
using OneText.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Tests
{
    /// <summary>
    /// Project Settings &gt; OneText, built.
    ///
    /// The page is the Hub now, which means the failure worth catching is not a
    /// wrong number but an empty panel: a provider that throws while mounting,
    /// or one that mounts nothing, looks exactly like a package that has no
    /// settings at all. Batch mode has no settings window, so this asks the
    /// provider for its content directly — the same call the window makes.
    /// </summary>
    public class SettingsPageTests
    {
        [Test]
        public void The_Page_Is_Where_Everything_Links_To()
        {
            var provider = OneTextSettingsProvider.Create();
            Assert.AreEqual(OneTextHub.SettingsPath, provider.settingsPath,
                "the path every 'project settings' link in the package uses");
        }

        [Test]
        public void Activating_The_Page_Mounts_The_Whole_Hub()
        {
            var provider = OneTextSettingsProvider.Create();
            var root = new VisualElement();

            Assert.DoesNotThrow(() => provider.OnActivate(string.Empty, root),
                "the settings page threw while building");

            var shell = root.Q("hub-root");
            Assert.NotNull(shell, "the page mounted something that is not the Hub");

            var nav = shell.Q<ScrollView>("nav");
            Assert.NotNull(nav, "no sidebar");
            Assert.That(nav.childCount, Is.GreaterThan(1), "the sidebar lists nothing");

            var content = shell.Q<ScrollView>("content");
            Assert.NotNull(content, "no panel");
            Assert.That(content.childCount, Is.GreaterThan(0), "the open section is empty");

            Assert.DoesNotThrow(provider.OnDeactivate, "the page threw while closing");
        }

        [Test]
        public void Forensics_Still_Lays_Its_Sample_Out()
        {
            // The expensive half of that section is deferred to the frame after
            // the panel appears, and a scheduled callback never runs without a
            // panel — so a headless test has to ask for it by name, or the
            // shaping and rasterizing in here would stop being covered at all.
            var hub = ScriptableObject.CreateInstance<OneTextHub>();
            try
            {
                var section = (HubForensicsTab)hub.Find(OneTextHub.Tab.Forensics);
                section.Build(hub);
                Assert.DoesNotThrow(section.FillStage, "the forensics stage threw while building");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hub);
            }
        }

        [Test]
        public void The_Defaults_Section_Is_On_It()
        {
            var hub = ScriptableObject.CreateInstance<OneTextHub>();
            try
            {
                var section = hub.Find(OneTextHub.Tab.Settings);
                Assert.IsInstanceOf<HubSettingsTab>(section,
                    "the project's own defaults are not a section of the page");

                var root = section.Build(hub);
                Assert.That(root.childCount, Is.GreaterThan(0),
                    "the defaults section built nothing");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hub);
            }
        }
    }
}
