using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Coming from TextMesh Pro, with the mechanical part done for you.
    ///
    /// Leaving TMP is two migrations wearing one name, and they fail in opposite
    /// ways. The scripts are text: <c>TextMeshProUGUI</c> is written in four
    /// hundred files, a person has to open all of them, and every line missed is
    /// a compile error somebody will trip over within the minute. The scenes and
    /// prefabs are data: a scene that still holds a TMP component opens, plays
    /// and looks exactly right, and stays that way until the package leaves and
    /// every label in it becomes a nameless missing script.
    ///
    /// So this tab does both, in the order a project actually needs them —
    /// components first, because the scan is free and the count is the thing
    /// anybody deciding whether to migrate wants to know, then the script
    /// rewrite. Nothing is written without the report being on screen first, and
    /// nothing is written over uncommitted work without saying so.
    /// </summary>
    public sealed class HubOnboardingTab : HubSection
    {
        private List<TmpScriptFinding> _findings;
        private readonly HashSet<string> _skipped = new HashSet<string>();
        private int _preview = -1;
        private int _scanned;

        private MigrationReport _migration;
        private bool _converted;
        private bool _allScenes;
        private bool _showNotes;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Onboarding;

        public override string Title => "Onboarding";

        public override string Eyebrow => "Coming from TextMesh Pro?";

        public override string Lede =>
            "Every TMP and legacy text component in your scenes and prefabs, counted, judged and " +
            "swapped in place — and the type names in your own scripts rewritten, with the diff " +
            "on screen first. Everything that will not survive is named up front rather than " +
            "found later.";

        public override string NavHint => "migrate off TMP";

        public override string NavGroup => "Migrate";

        public override string BadgeText
        {
            get
            {
                if (_migration == null && _findings == null) return "—";
                int total = (_migration?.Targets.Count ?? 0) + (_findings?.Count ?? 0);
                return total.ToString("n0");
            }
        }

        public override HubTone BadgeTone
        {
            get
            {
                if (_migration == null && _findings == null) return HubTone.Neutral;
                if (_migration != null && _migration.Errors > 0) return HubTone.Bad;
                int total = (_migration?.Targets.Count ?? 0) + (_findings?.Count ?? 0);
                return total == 0 ? HubTone.Good : HubTone.Info;
            }
        }

        protected override void Compose(VisualElement content)
        {
            content.Add(ComponentCard());
            if (_migration != null && _migration.Targets.Count > 0)
            {
                content.Add(ContainersCard());
                content.Add(FindingsCard());
            }

            content.Add(ScanCard());
            if (_findings != null && _findings.Count > 0)
            {
                content.Add(FilesCard());
                content.Add(PreviewCard());
            }

            content.Add(CiCard());
        }

        // ------------------------------------------------------- components

        private VisualElement ComponentCard()
        {
            var card = HubUI.MakeCard("Scenes and prefabs",
                "TextMeshProUGUI and TextMeshPro become OneTextLabel and OneTextMesh, " +
                "TMP_InputField becomes OneTextInputField, and UnityEngine.UI.Text and TextMesh " +
                "come along too. Font assets are made from the .ttf behind them, once each, and " +
                "every reference to a swapped component is re-pointed and read back.");

            card.Act(HubUI.Ghost(_migration == null ? "Scan Scenes & Prefabs…" : "Scan again",
                ScanComponents));
            if (_migration != null && Convertible() > 0)
                card.Act(HubUI.Primary($"Convert {Convertible():n0} component(s)", () => Convert(null)));

            if (!MigrationProviders.HasTextMeshPro)
            {
                card.Add(HubUI.Notice(
                    "TextMesh Pro is not in this project, so nothing here looks for it: the scan " +
                    "covers UnityEngine.UI.Text and TextMesh only. Install TMP if you still have " +
                    "TMP components to convert — this window will find them the moment it is back.",
                    HubTone.Warn));
            }

            card.Add(HubUI.Pill(_allScenes ? "Every scene under Assets" : "Scenes in Build Settings",
                _allScenes, on =>
                {
                    _allScenes = on;
                    Refresh();
                }));

            if (_migration == null)
            {
                card.Add(HubUI.Empty("Not scanned yet",
                    "Opens each scene and prefab, reads every component on every object — " +
                    "inactive ones included — and closes them again. Nothing is saved, nothing " +
                    "is dirtied, and the report says what would and would not survive.",
                    "Scan Scenes & Prefabs…", ScanComponents, "⇄"));
                return card.Root;
            }

            var tiles = HubUI.Box("tiles");
            tiles.Add(HubUI.Tile("labels",
                (_migration.CountOfKind(MigrationKind.Label)).ToString("n0"), "become OneTextLabel"));
            tiles.Add(HubUI.Tile("world text",
                (_migration.CountOfKind(MigrationKind.Mesh)).ToString("n0"), "become OneTextMesh"));
            tiles.Add(HubUI.Tile("input fields",
                (_migration.CountOfKind(MigrationKind.InputField)).ToString("n0"),
                "become OneTextInputField"));
            tiles.Add(HubUI.Tile("no counterpart",
                (_migration.CountOfKind(MigrationKind.ReportOnly)).ToString("n0"), "left alone",
                _migration.CountOfKind(MigrationKind.ReportOnly) == 0 ? HubTone.Good : HubTone.Warn));
            card.Add(tiles);

            var severity = HubUI.Box("tiles");
            severity.Add(HubUI.Tile("containers", _migration.Containers.Count.ToString("n0"),
                $"of {_migration.ContainersScanned:n0} opened"));
            severity.Add(HubUI.Tile("errors", _migration.Errors.ToString("n0"),
                "will not survive as-is", _migration.Errors == 0 ? HubTone.Good : HubTone.Bad));
            severity.Add(HubUI.Tile("warnings", _migration.Warnings.ToString("n0"),
                "survive, but differently",
                _migration.Warnings == 0 ? HubTone.Good : HubTone.Warn));
            severity.Add(HubUI.Tile("notes",
                _migration.Count(DoctorSeverity.Info).ToString("n0"), "worth reading once"));
            card.Add(severity);

            if (_converted)
            {
                card.Add(HubUI.Notice(
                    $"{_migration.Converted:n0} component(s) swapped, {_migration.FontsCreated:n0} " +
                    $"font asset(s) created, {_migration.Relinked:n0} reference(s) re-pointed. " +
                    "Everything still listed below is what remains after the conversion.",
                    _migration.Errors == 0 ? HubTone.Good : HubTone.Warn));
            }
            else if (_migration.Targets.Count == 0)
            {
                card.Add(HubUI.Notice(
                    $"Nothing to convert: none of the {_migration.ContainersScanned:n0} scenes and " +
                    "prefabs scanned hold a text component this module knows.", HubTone.Good));
            }
            else
            {
                card.Add(HubUI.Notice(_migration.Summary(),
                    _migration.Errors == 0 ? HubTone.Info : HubTone.Bad));
            }
            return card.Root;
        }

        private int Convertible()
        {
            if (_migration == null) return 0;
            int n = 0;
            foreach (var target in _migration.Targets) if (target.Convertible) n++;
            return n;
        }

        private void ScanComponents()
        {
            _migration = ComponentMigration.Scan(new ComponentMigration.Options
            {
                AllScenes = _allScenes,
            });
            _converted = false;
            Refresh();
            Say(_migration.Summary());
        }

        /// <summary>
        /// The containers, each with what it holds and a button that converts
        /// only that one.
        ///
        /// One at a time is the option a real project uses. A migration that is
        /// all-or-nothing across four hundred prefabs is a migration nobody
        /// starts on a Tuesday.
        /// </summary>
        private VisualElement ContainersCard()
        {
            var card = HubUI.MakeCard("Where they are",
                "Prefabs convert before scenes, and a base prefab before anything built out of " +
                "it, so a variant never ends up holding both components.").Flush();

            foreach (string container in _migration.Containers)
            {
                int targets = 0, errors = 0;
                foreach (var target in _migration.Targets)
                    if (target.Container == container) targets++;
                foreach (var finding in _migration.Findings)
                    if (finding.Container == container && finding.Severity == DoctorSeverity.Error)
                        errors++;

                var row = HubUI.Box("folder-row");
                var name = HubUI.Mono(HubUI.Text(container, "kv__value"));
                name.style.flexGrow = 1f;
                row.Add(name);
                row.Add(HubUI.Badge($"{targets:n0} component(s)", HubTone.Info));
                if (errors > 0) row.Add(HubUI.Badge($"{errors:n0} error(s)", HubTone.Bad));

                var asset = AssetDatabase.LoadAssetAtPath<Object>(container);
                if (asset != null)
                    row.Add(HubUI.Quiet("Show", () => EditorGUIUtility.PingObject(asset)));

                string only = container;
                if (!_converted)
                    row.Add(HubUI.Quiet("Convert", () => Convert(new List<string> { only })));
                card.Add(row);
            }
            return card.Root;
        }

        private VisualElement FindingsCard()
        {
            var host = new VisualElement();
            var head = HubUI.MakeCard("What will and will not survive",
                "Errors are things that do not come across. Warnings come across differently and " +
                "you should look at them. Notes are arithmetic worth knowing about.");
            head.Add(HubUI.Pill("Show notes", _showNotes, on =>
            {
                _showNotes = on;
                Refresh();
            }));
            host.Add(head.Root);

            if (_migration.Findings.Count == 0)
            {
                host.Add(HubUI.Notice("Nothing to report: every component maps exactly.",
                    HubTone.Good));
                return host;
            }

            foreach (var finding in _migration.Findings)
            {
                if (finding.Severity == DoctorSeverity.Info && !_showNotes) continue;
                host.Add(Finding(finding));
            }
            return host;
        }

        private static VisualElement Finding(in MigrationFinding finding)
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

            card.Root.style.borderLeftWidth = 2f;
            card.Root.style.borderLeftColor = tone switch
            {
                HubTone.Bad => new Color(1f, 0.482f, 0.447f),
                HubTone.Warn => new Color(1f, 0.8f, 0.4f),
                _ => new Color(0.839f, 0.898f, 0.867f, 0.2f),
            };

            if (!string.IsNullOrEmpty(finding.Component) || !string.IsNullOrEmpty(finding.Path))
            {
                card.Add(HubUI.KeyValue(finding.Component ?? "component",
                    finding.Path ?? string.Empty));
            }
            if (!string.IsNullOrEmpty(finding.Sample))
                card.Add(HubUI.KeyValue("Sample", finding.Sample));
            if (!string.IsNullOrEmpty(finding.Container))
            {
                var row = HubUI.Box("row");
                row.Add(HubUI.Mono(HubUI.Text(finding.Container, "kv__value")));
                var asset = AssetDatabase.LoadAssetAtPath<Object>(finding.Container);
                if (asset != null)
                    row.Add(HubUI.Quiet("Show in project", () => EditorGUIUtility.PingObject(asset)));
                card.Add(row);
            }

            if (card.Body.childCount == 0) card.Body.style.display = DisplayStyle.None;
            return card.Root;
        }

        /// <summary>
        /// Converts, after saying out loud what is about to change on disk.
        ///
        /// Scenes and prefabs are assets, and the undo for an asset a batch
        /// touched is version control. So git is asked first, and when git
        /// cannot answer that is said too, rather than quietly treated as
        /// "clean".
        /// </summary>
        private void Convert(List<string> only)
        {
            var containers = only ?? new List<string>(_migration.Containers);
            if (containers.Count == 0)
            {
                SayBadly("Nothing to convert.");
                return;
            }

            int components = 0;
            foreach (var target in _migration.Targets)
                if (target.Convertible && containers.Contains(target.Container)) components++;

            if (!OnboardingGit.ConfirmOverwrite("Convert components?",
                    $"{components:n0} component(s) in {containers.Count:n0} scene(s) and prefab(s) " +
                    "will be destroyed and replaced, and those files will be saved.",
                    containers, "Convert"))
                return;

            _migration = ComponentMigration.Apply(new ComponentMigration.Options
            {
                AllScenes = _allScenes,
                OnlyContainers = only,
                AdoptProjectFontDefaults = true,
            });
            _converted = true;
            Refresh();
            Say($"Converted {_migration.Converted:n0} component(s). {_migration.Summary()}");
        }

        // --------------------------------------------------------------- scan

        private VisualElement ScanCard()
        {
            var card = HubUI.MakeCard("Script rewrite",
                "TextMeshProUGUI and TMP_Text become OneTextLabel, TMP_InputField becomes " +
                "OneTextInputField, TextMeshPro becomes OneTextMesh, and using TMPro becomes the " +
                "namespaces those live in. Type names only: never a member, never a string, " +
                "never a comment.");

            card.Act(HubUI.Ghost(_findings == null ? "Scan Assets…" : "Scan again", Scan));
            if (_findings != null && Selected().Count > 0)
                card.Act(HubUI.Primary($"Rewrite {Selected().Count} file(s)", Apply));

            if (_findings == null)
            {
                card.Add(HubUI.Empty("Not scanned yet",
                    "Reads every .cs file under Assets — packages are left alone — and reports " +
                    "the ones that mention TextMesh Pro. Nothing is written by scanning.",
                    "Scan Assets…", Scan, "⇄"));
                return card.Root;
            }

            if (_findings.Count == 0)
            {
                card.Add(HubUI.Notice(
                    $"Nothing to rewrite: none of the {_scanned:n0} scripts under Assets mention " +
                    "TextMesh Pro.", HubTone.Good));
                return card.Root;
            }

            int changes = 0, residuals = 0;
            foreach (var finding in _findings)
            {
                changes += finding.Result.Changes.Count;
                residuals += finding.Result.Residuals.Count;
            }

            var tiles = HubUI.Box("tiles");
            tiles.Add(HubUI.Tile("files", _findings.Count.ToString("n0"),
                $"of {_scanned:n0} scanned"));
            tiles.Add(HubUI.Tile("lines", changes.ToString("n0"), "would change"));
            tiles.Add(HubUI.Tile("left over", residuals.ToString("n0"),
                "TMP names with no counterpart",
                residuals == 0 ? HubTone.Good : HubTone.Warn));
            card.Add(tiles);

            if (residuals > 0)
            {
                card.Add(HubUI.Notice(
                    "Some files will still need manual work. They are marked below, with the " +
                    "names; after the rewrite the compiler will point at the same lines.",
                    HubTone.Warn));
            }
            return card.Root;
        }

        private void Scan()
        {
            var paths = TmpScriptRewriter.ScriptsUnder(Application.dataPath);
            _scanned = paths.Count;
            _findings = TmpScriptRewriter.Scan(paths);
            _skipped.Clear();
            _preview = _findings.Count > 0 ? 0 : -1;
            Refresh();
            Say(_findings.Count == 0
                ? $"Scanned {_scanned:n0} scripts. Nothing mentions TextMesh Pro."
                : $"Scanned {_scanned:n0} scripts. {_findings.Count:n0} mention TextMesh Pro.");
        }

        // -------------------------------------------------------------- files

        private VisualElement FilesCard()
        {
            var card = HubUI.MakeCard("Files",
                "Tick the ones to rewrite. Click a row to read its diff below.").Flush();

            for (int i = 0; i < _findings.Count; i++)
            {
                int index = i;
                var finding = _findings[i];
                string path = ProjectPath(finding.Path);

                var row = HubUI.Box("folder-row");

                row.Add(HubUI.Pill(System.IO.Path.GetFileName(path), Included(finding), on =>
                {
                    if (on) _skipped.Remove(finding.Path);
                    else _skipped.Add(finding.Path);
                    Refresh();
                }));

                var name = HubUI.Mono(HubUI.Text(path, "kv__value"));
                name.style.flexGrow = 1f;
                row.Add(name);

                row.Add(HubUI.Badge($"{finding.Result.Changes.Count:n0} line(s)",
                    finding.Result.Changed ? HubTone.Info : HubTone.Neutral));
                if (finding.Result.Residuals.Count > 0)
                    row.Add(HubUI.Badge($"{finding.Result.Residuals.Count:n0} left", HubTone.Warn));

                row.Add(HubUI.Quiet(index == _preview ? "Showing" : "Show", () =>
                {
                    _preview = index;
                    Refresh();
                }));
                card.Add(row);
            }
            return card.Root;
        }

        private bool Included(TmpScriptFinding finding) =>
            finding.Result.Changed && !_skipped.Contains(finding.Path);

        private List<TmpScriptFinding> Selected()
        {
            var selected = new List<TmpScriptFinding>();
            if (_findings == null) return selected;
            foreach (var finding in _findings)
                if (Included(finding)) selected.Add(finding);
            return selected;
        }

        // ------------------------------------------------------------ preview

        private VisualElement PreviewCard()
        {
            if (_preview < 0 || _preview >= _findings.Count) _preview = 0;
            var finding = _findings[_preview];
            var result = finding.Result;

            var card = HubUI.MakeCard(ProjectPath(finding.Path),
                result.Changed
                    ? $"{result.Changes.Count:n0} line(s) would change."
                    : "Nothing to rewrite here; only names this cannot carry over.");
            card.TitleLabel.AddToClassList("mono");

            var asset = AssetDatabase.LoadAssetAtPath<Object>(ProjectPath(finding.Path));
            if (asset != null)
                card.Act(HubUI.Quiet("Show in project", () => EditorGUIUtility.PingObject(asset)));

            foreach (var change in result.Changes)
            {
                card.Add(Diff($"{change.Line,5}  − {change.Original.TrimEnd()}", Removed));
                if (change.Replacement == null)
                {
                    card.Add(Diff("       (line removed)", Removed));
                    continue;
                }
                foreach (string line in change.Replacement.Split('\n'))
                    card.Add(Diff($"       + {line.TrimEnd()}", Added));
            }

            if (result.Residuals.Count == 0) return card.Root;

            card.Add(HubUI.Notice(
                "Still TextMesh Pro after the rewrite, with no OneText counterpart to swap in. " +
                "Line numbers are in the rewritten file.", HubTone.Warn));
            foreach (var residual in result.Residuals)
                card.Add(HubUI.KeyValue(residual.Name, $"line {residual.Line:n0}", HubTone.Warn));
            return card.Root;
        }

        private static readonly Color Removed = new Color(1f, 0.482f, 0.447f);
        private static readonly Color Added = new Color(0.51f, 0.882f, 0.62f);

        private static VisualElement Diff(string text, Color tone)
        {
            var label = HubUI.Mono(HubUI.Text(text, "code"));
            label.style.color = tone;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            return label;
        }

        // -------------------------------------------------------------- apply

        /// <summary>
        /// Writes the ticked files, after saying out loud what is about to be
        /// overwritten.
        ///
        /// The dialog is not ceremony. This edits source files in place, and the
        /// undo for that is version control; a rewrite dropped on top of an
        /// afternoon of uncommitted work is unrecoverable in a way nothing else
        /// in this window is.
        /// </summary>
        private void Apply()
        {
            var targets = Selected();
            if (targets.Count == 0)
            {
                SayBadly("Nothing ticked.");
                return;
            }

            var relative = new List<string>();
            foreach (var finding in targets) relative.Add(ProjectPath(finding.Path));
            if (!OnboardingGit.ConfirmOverwrite("Rewrite scripts?",
                    $"{targets.Count} file(s) will be overwritten in place.",
                    relative, "Rewrite"))
                return;

            int written = 0, stale = 0;
            var failed = new List<string>();
            foreach (var finding in targets)
            {
                try
                {
                    // The preview was computed at scan time, and the file may
                    // have moved on since — an editor save, a pulled branch.
                    // What goes to disk is always derived from what is on disk
                    // now, so a stale scan can revert nothing.
                    var fresh = TmpScriptRewriter.Rewrite(File.ReadAllText(finding.Path));
                    if (!fresh.Changed)
                    {
                        stale++;
                        continue;
                    }
                    Write(finding.Path, fresh.Text);
                    written++;
                }
                catch (IOException error)
                {
                    failed.Add($"{ProjectPath(finding.Path)}: {error.Message}");
                }
                catch (System.UnauthorizedAccessException error)
                {
                    failed.Add($"{ProjectPath(finding.Path)}: {error.Message}");
                }
            }

            AssetDatabase.Refresh();

            if (failed.Count > 0)
            {
                SayBadly($"Rewrote {written:n0} file(s); {failed.Count:n0} could not be written. " +
                    failed[0]);
            }
            else
            {
                Say($"Rewrote {written:n0} file(s)." +
                    (stale > 0 ? $" {stale:n0} changed since the scan and had nothing left to rewrite." : "") +
                    " Unity is recompiling; anything left over is listed above and will now be a " +
                    "compile error.");
            }
            Scan();
        }

        /// <summary>
        /// Preserves the byte-order mark the file had, and only that. Unity
        /// writes C# files without one and Visual Studio writes them with one,
        /// so re-encoding on a whim would put every rewritten file in the diff
        /// twice: once for the rewrite and once for a mark nobody touched.
        /// </summary>
        private static void Write(string path, string text)
        {
            bool bom;
            using (var stream = File.OpenRead(path))
            {
                var head = new byte[3];
                int read = stream.Read(head, 0, 3);
                bom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }

        // ----------------------------------------------------------------- CI

        /// <summary>
        /// The component scan, on the command line. A team midway through a
        /// migration can make "no TMP components left" a merge condition rather
        /// than a thing somebody remembers to check.
        /// </summary>
        private VisualElement CiCard()
        {
            var card = HubUI.MakeCard("On CI",
                "Scans and reports; never converts. Exits 1 when something in the project would " +
                "not survive the migration as it stands.");

            const string command =
                "Unity -batchmode -quit -projectPath . -executeMethod " +
                "OneText.Editor.ComponentMigration.RunFromCommandLine -oneAllScenes";

            card.Add(HubUI.Mono(HubUI.Text(command, "code")));
            card.Act(HubUI.Quiet("Copy", () =>
            {
                EditorGUIUtility.systemCopyBuffer = command;
                Say("Command copied to the clipboard.");
            }));
            return card.Root;
        }

        private static string ProjectPath(string fullPath) =>
            TextSourceScanner.ToProjectPath(fullPath);
    }
}
