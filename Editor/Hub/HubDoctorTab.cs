using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Doctor: the project's strings against the fonts and break data it ships,
    /// with the same verdict CI gets.
    ///
    /// Nothing in or around Unity answers "will every string in this build
    /// render", and every part of the answer is decidable before the build.
    /// </summary>
    public sealed class HubDoctorTab : HubSection
    {
        private DoctorReport _report;
        private bool _showInfo = true;

        /// <summary>The last run, for the overview to report.</summary>
        public DoctorReport LastReport => _report;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Doctor;

        public override string Title => "Doctor";

        public override string Eyebrow => "Will it render?";

        public override string Lede =>
            "Characters no font in the chain can draw, Japanese that will come out in Chinese " +
            "shapes, a locale whose line breaking needs a dictionary nobody installed. All of it " +
            "decidable before the build, and all of it decided here.";

        public override string NavHint => "renderability lint";

        public override string NavGroup => "Checks";

        public override string BadgeText
        {
            get
            {
                if (_report == null) return null;
                if (_report.Passed) return "PASS";
                int errors = 0;
                foreach (var finding in _report.Findings)
                    if (finding.Severity == DoctorSeverity.Error) errors++;
                return errors.ToString("n0");
            }
        }

        public override HubTone BadgeTone =>
            _report == null ? HubTone.Neutral
            : _report.Passed ? HubTone.Good
            : HubTone.Bad;

        protected override void Compose(VisualElement content)
        {
            content.Add(StringSources(
                "Doctor reads every string it finds under these paths and checks each one."));
            content.Add(RunCard());
            if (_report != null && _report.Findings.Count > 0) content.Add(Findings());
            content.Add(CiCard());
        }

        private VisualElement RunCard()
        {
            var card = HubUI.MakeCard("The check",
                "One pass over your strings, the same one CI runs.");
            card.Act(HubUI.Primary("Run Doctor", () =>
            {
                _report = TextDoctor.Run(Hub.StringFolders);
                Refresh();
                Say(_report.Summary());
            }));

            if (_report == null)
            {
                card.Add(HubUI.Empty("Not run yet",
                    Hub.StringFolders.Count == 0
                        ? "Add a folder of strings above; Doctor has nothing to check until it " +
                          "knows where your text is."
                        : "One pass says whether every string in this project can be drawn with " +
                          "the fonts and word lists it ships.",
                    Hub.StringFolders.Count == 0 ? null : "Run Doctor",
                    Hub.StringFolders.Count == 0 ? (System.Action)null : () =>
                    {
                        _report = TextDoctor.Run(Hub.StringFolders);
                        Refresh();
                    }, "✚"));
                return card.Root;
            }

            card.Add(HubUI.Notice(_report.Summary(),
                _report.Passed ? HubTone.Good : HubTone.Bad));

            if (_report.Findings.Count == 0)
            {
                card.Add(HubUI.Notice("Nothing to report: every string renders.", HubTone.Good));
                return card.Root;
            }

            card.Add(HubUI.Pill("Show notes", _showInfo, on =>
            {
                _showInfo = on;
                Refresh();
            }));
            return card.Root;
        }

        private VisualElement Findings()
        {
            var host = new VisualElement();
            foreach (var finding in _report.Findings)
            {
                if (finding.Severity == DoctorSeverity.Info && !_showInfo) continue;
                host.Add(Finding(finding));
            }
            return host;
        }

        private VisualElement Finding(in DoctorFinding finding)
        {
            var tone = finding.Severity switch
            {
                DoctorSeverity.Error => HubTone.Bad,
                DoctorSeverity.Warning => HubTone.Warn,
                _ => HubTone.Neutral,
            };

            var card = HubUI.MakeCard(finding.Message, null);
            card.TitleLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            card.Actions.Add(HubUI.Badge(finding.Rule, tone));

            switch (tone)
            {
                case HubTone.Bad: card.Root.style.borderLeftWidth = 2f;
                    card.Root.style.borderLeftColor = new Color(1f, 0.482f, 0.447f); break;
                case HubTone.Warn: card.Root.style.borderLeftWidth = 2f;
                    card.Root.style.borderLeftColor = new Color(1f, 0.8f, 0.4f); break;
                default: card.Root.style.borderLeftWidth = 2f;
                    card.Root.style.borderLeftColor = new Color(0.839f, 0.898f, 0.867f, 0.2f); break;
            }

            if (string.IsNullOrEmpty(finding.Source) && string.IsNullOrEmpty(finding.Sample))
            {
                card.Body.style.display = DisplayStyle.None;
                return card.Root;
            }

            if (!string.IsNullOrEmpty(finding.Sample))
                card.Add(HubUI.KeyValue("Sample", finding.Sample));

            if (!string.IsNullOrEmpty(finding.Source))
            {
                var row = HubUI.Box("row");
                var path = HubUI.Mono(HubUI.Text(finding.Source, "kv__value"));
                row.Add(path);
                var asset = AssetDatabase.LoadAssetAtPath<Object>(finding.Source);
                if (asset != null)
                    row.Add(HubUI.Quiet("Show in project", () => EditorGUIUtility.PingObject(asset)));
                card.Add(row);
            }
            return card.Root;
        }

        /// <summary>
        /// The command line, spelled out. A lint that only runs when somebody
        /// opens a window is a document; the point of this one is the exit code.
        /// </summary>
        private VisualElement CiCard()
        {
            var card = HubUI.MakeCard("On CI",
                "Run it on every merge. Exits 1 when a string cannot render, so an unrenderable " +
                "string fails the pull request rather than the release.");

            const string command =
                "Unity -batchmode -projectPath . -executeMethod " +
                "OneText.Editor.TextDoctor.RunFromCommandLine -oneStrings Assets/Localization";

            card.Add(HubUI.Mono(HubUI.Text(command, "code")));
            card.Act(HubUI.Quiet("Copy", () =>
            {
                EditorGUIUtility.systemCopyBuffer = command;
                Say("Command copied to the clipboard.");
            }));
            return card.Root;
        }
    }
}
