using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Tests
{
    /// <summary>
    /// The half of "convert part of this project" that lives on the screen.
    ///
    /// The engine's half is asserted next door, in ComponentMigrationTests: a
    /// run over one container mends the references reaching into it from every
    /// container it was not given, and widens itself to whatever the selection
    /// is built out of. None of that is reachable if the screen stops offering
    /// a way to select, which is a thing a card can lose in a refactor without
    /// anything failing to compile — so what is asserted here is that the
    /// affordance is on screen at all, and that it says how much work it is
    /// about to do rather than a bare verb.
    /// </summary>
    public sealed class HubSelectionTests
    {
        private OneTextHub _window;

        [SetUp]
        public void SetUp() => _window = ScriptableObject.CreateInstance<OneTextHub>();

        [TearDown]
        public void TearDown()
        {
            if (_window != null) Object.DestroyImmediate(_window);
            _window = null;
        }

        private static readonly string[] Containers =
        {
            "Assets/Made/Up.prefab", "Assets/Made/Down.prefab", "Assets/Made/Sideways.unity",
        };

        /// <summary>Three containers, two components each: a report with corners to pick.</summary>
        private static MigrationReport Report()
        {
            var report = new MigrationReport { ContainersScanned = Containers.Length };
            foreach (string container in Containers)
            {
                for (int i = 0; i < 2; i++)
                {
                    report.Add(new MigrationTarget
                    {
                        Kind = MigrationKind.Label,
                        ComponentType = "Text",
                        Container = container,
                        Path = $"Root/Label {i}",
                    });
                }
            }
            return report;
        }

        private string Screen(bool converted)
        {
            var tab = _window.Find(OneTextHub.Tab.Onboarding) as HubOnboardingTab;
            Assert.NotNull(tab, "the Hub has no onboarding tab to draw a report on");

            tab.Adopt(Report(), converted);
            var text = new System.Text.StringBuilder();
            Gather(tab.Build(_window), text);
            return text.ToString();
        }

        [Test]
        public void AScannedProject_OffersAWayToConvertPartOfIt()
        {
            string screen = Screen(converted: false);

            StringAssert.Contains("Tick all", screen,
                "there is no way to select containers, so the only conversion on offer is the " +
                "whole project — which is the state this screen was changed to stop being in");
            StringAssert.Contains("Tick none", screen);

            foreach (string container in Containers)
            {
                StringAssert.Contains(System.IO.Path.GetFileNameWithoutExtension(container), screen,
                    $"{container} has no tick of its own");
            }
        }

        [Test]
        public void WithNothingTicked_TheButtonOffersTheWholeProject_AndSaysHowMuchThatIs()
        {
            string screen = Screen(converted: false);

            // Six components across three containers. The number is the point:
            // "Convert" on its own is a button somebody presses to find out what
            // it does, on a project where finding out takes four minutes and
            // rewrites six thousand files.
            StringAssert.Contains("Convert all 6 component(s)", screen,
                "the button does not say what pressing it converts");
        }

        [Test]
        public void AfterAConversion_TheTicksAreGone()
        {
            string screen = Screen(converted: true);

            StringAssert.DoesNotContain("Tick all", screen,
                "the report on screen describes work already done, and ticking a row of it " +
                "offers a conversion of something that has already been converted");
        }

        /// <summary>
        /// The nesting closure the Hub asks for before it draws its confirmation.
        ///
        /// Asserted on paths that do not exist, because that is the honest
        /// contract: nothing is loaded, the asset database is asked about
        /// dependencies, and a selection with no answer comes back as itself
        /// rather than as an exception on the way to a dialog.
        /// </summary>
        [Test]
        public void ASelectionOfNothingInParticular_ComesBackAsItself()
        {
            var picked = new List<string> { "Assets/Made/Up.prefab", "Assets/Made/Down.prefab" };
            var closure = ComponentMigration.WithWhatTheyNest(picked);

            Assert.AreEqual(picked.Count, closure.Count,
                "the closure invented a dependency for a prefab that is not on disk");
            CollectionAssert.AreEqual(picked, closure,
                "the selection came back in a different order, and the order is the run order");
        }

        [Test]
        public void ASelectionOfNothing_IsNotAnException()
        {
            Assert.AreEqual(0, ComponentMigration.WithWhatTheyNest(null).Count);
            Assert.AreEqual(0, ComponentMigration.WithWhatTheyNest(new List<string>()).Count);
        }

        private static void Gather(VisualElement element, System.Text.StringBuilder into)
        {
            if (element is Label label && !string.IsNullOrEmpty(label.text))
                into.Append(label.text).Append('\n');
            if (element is Button button && !string.IsNullOrEmpty(button.text))
                into.Append(button.text).Append('\n');
            foreach (var child in element.Children()) Gather(child, into);
        }
    }
}
