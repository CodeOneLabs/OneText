using NUnit.Framework;
using OneText.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Tests
{
    /// <summary>
    /// What the migration screen draws when the report is the size a real
    /// project makes.
    ///
    /// Five-Dice produces 6,422 errors and warnings, 5,487 of them the same
    /// missing font file reported once per label. A screen that draws one card
    /// per finding builds 6,422 cards, which is not a report — it is a freeze,
    /// and then a wall nobody scrolls. The counts here are not style: they are
    /// the difference between a tool somebody uses on their project and one they
    /// force-quit halfway through.
    ///
    /// The property asserted is boundedness, not smallness. Drawing more as the
    /// report grows is right up to the cap — those cards are what somebody reads.
    /// Past it the screen has to stop, and it is measured by making the report
    /// twenty times bigger and requiring the same screen back.
    /// </summary>
    public sealed class HubFindingsScaleTests
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

        private static readonly string[] Rules =
        {
            "font-source-missing", "margin-lost", "reference-would-break",
            "unsaveable-container", "font-style",
        };

        private static MigrationReport Report(int perRule)
        {
            var report = new MigrationReport { ContainersScanned = 1 };
            report.Containers.Add("Assets/Made/Up.prefab");
            report.Targets.Add(new MigrationTarget
            {
                Kind = MigrationKind.Label,
                ComponentType = "Text",
                Container = "Assets/Made/Up.prefab",
                Path = "Root/Label",
            });

            foreach (string rule in Rules)
            {
                for (int i = 0; i < perRule; i++)
                {
                    report.Add(new MigrationFinding
                    {
                        Severity = rule == "margin-lost" || rule == "font-style"
                            ? DoctorSeverity.Warning
                            : DoctorSeverity.Error,
                        Rule = rule,
                        Message = $"finding {i} of {rule}",
                        Container = "Assets/Made/Up.prefab",
                        Path = $"Root/Label {i}",
                        Component = "Text",
                    });
                }
            }
            return report;
        }

        private int Elements(int perRule)
        {
            var tab = _window.Find(OneTextHub.Tab.Onboarding) as HubOnboardingTab;
            Assert.NotNull(tab, "the Hub has no onboarding tab to draw a report on");

            tab.Adopt(Report(perRule));
            var root = tab.Build(_window);
            Assert.NotNull(root);
            return Count(root);
        }

        private static int Count(VisualElement element)
        {
            int n = 1;
            foreach (var child in element.Children()) n += Count(child);
            return n;
        }

        [Test]
        public void PastTheCap_TheScreenStopsGrowing()
        {
            const int PerRule = 4000;
            int findings = PerRule * Rules.Length;

            int atCap = Elements(200);
            int wayPast = Elements(PerRule);
            Assert.Greater(atCap, 0, "the tab drew nothing at all");

            // Twenty times the findings, the same screen. Growing up to the cap
            // is the point — those cards are what somebody reads. Growing past
            // it is the freeze.
            Assert.LessOrEqual(wayPast, atCap + 5,
                $"{atCap:n0} elements for 1,000 findings and {wayPast:n0} for {findings:n0}: the " +
                "screen is still following the report's size rather than its own cap.");

            // Stated without a magic number, because a ceiling picked today drifts
            // with every row added to the page and this does not: whatever else
            // is true, the screen has to be smaller than the report it describes.
            Assert.Less(wayPast, findings,
                $"{findings:n0} findings drew {wayPast:n0} elements. A screen that is larger " +
                "than its own report is one card per finding by another name.");
        }

        [Test]
        public void EveryRule_IsStillOnScreen_HoweverManyOfItThereAre()
        {
            var tab = _window.Find(OneTextHub.Tab.Onboarding) as HubOnboardingTab;
            tab.Adopt(Report(400));
            var root = tab.Build(_window);

            // Collapsing must not be losing. Whatever is folded away, the name of
            // every rule and how many of it there are has to be readable without
            // clicking anything.
            var text = new System.Text.StringBuilder();
            Gather(root, text);
            string screen = text.ToString();

            // This report has no font recovery manifest behind it, so there is no
            // section to send the font findings to and they stay in the list —
            // which is the whole point of gating that on the section existing.
            // Nothing may be dropped for having nowhere to go.
            foreach (string rule in Rules)
            {
                StringAssert.Contains(rule, screen,
                    $"{rule} is nowhere on the screen, so the run reported it and the person " +
                    "reading cannot find it");
            }
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
