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
        private TmpScanReport _report;
        private List<TmpScriptFinding> _findings;
        private readonly HashSet<string> _skipped = new HashSet<string>();
        private int _preview = -1;
        private int _scanned;

        private MigrationReport _migration;
        private bool _converted;
        private bool _allScenes;
        private bool _crossContainer = true;
        private bool _showNotes;

        /// <summary>
        /// The containers ticked for the next conversion, empty meaning all of
        /// them.
        ///
        /// Empty rather than "everything ticked" is the state a fresh scan lands
        /// in, because the button that reads "Convert all 1,204 component(s)" is
        /// the one most projects want and it should not require a hundred clicks
        /// to be unticked back into existence.
        /// </summary>
        private readonly HashSet<string> _picked = new HashSet<string>();

        /// <summary>Whether the conversion that produced the report on screen was a selection.</summary>
        private bool _partial;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Onboarding;

        /// <summary>
        /// Puts a report on the screen as though a conversion — or, with
        /// <paramref name="converted"/> false, a scan — had just produced it.
        ///
        /// Here so a test can hand this tab the report a real project makes and
        /// measure what it draws, which is the only way to catch this screen
        /// growing a card per finding again. The scan half matters for a
        /// different reason: the ticks and the button that follows them only
        /// exist before a conversion, so a screen that quietly stopped offering
        /// them would be invisible to a test that could only adopt an aftermath.
        /// </summary>
        public void Adopt(MigrationReport report, bool converted = true)
        {
            _migration = report;
            _converted = converted;
            _partial = false;
            _picked.Clear();
        }

        public override string Title => "Onboarding";

        public override string Eyebrow => "Coming from TextMesh Pro?";

        public override string Lede =>
            "Move this project off TextMesh Pro. Step 1 converts the text components in your " +
            "scenes and prefabs. Step 2 rewrites the TMP type names in your own scripts. Both " +
            "scan first and show you the list — including what they cannot convert — and neither " +
            "writes anything until you press a button.";

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
            content.Add(StepsCard());
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

            content.Add(LeftBehindCard());
            content.Add(CiCard());
        }

        // ------------------------------------------------------------- steps

        /// <summary>
        /// The four buttons on this screen, in the order they are meant to be
        /// pressed, each with one sentence saying what it does to the project.
        ///
        /// It is here because the rest of this screen was written for somebody
        /// who already knew the shape of the job. Somebody who has just
        /// installed the package does not, and a screen of counts and warnings
        /// with no stated order reads as a wall rather than as a task.
        /// </summary>
        private VisualElement StepsCard()
        {
            var card = HubUI.MakeCard("What to do, in order",
                "Nothing on this screen changes a file until you press Convert or Rewrite. " +
                "Scanning is always safe.").Flush();

            card.Add(HubUI.KeyValue("1 · Scan Scenes & Prefabs",
                "Opens every scene and prefab, counts the TMP and legacy text components, and " +
                "lists the ones that will not convert cleanly. Saves nothing."));
            card.Add(HubUI.KeyValue("2 · Convert",
                "Replaces each component with the OneText one, keeping its values; makes a font " +
                "asset from the .ttf behind each TMP font; re-points every reference to the old " +
                "component. This saves the files."));
            card.Add(HubUI.KeyValue("3 · Scan Assets",
                "Reads the .cs files under Assets and finds the TMP type names in them. You get " +
                "the diff on screen. Writes nothing."));
            card.Add(HubUI.KeyValue("4 · Rewrite",
                "Writes those files. Type names only — never a member, a string or a comment."));
            card.Add(HubUI.KeyValue("Before you start",
                "Commit your work. Both buttons ask git whether the files they are about to " +
                "touch are committed and tell you before they go ahead, but Unity itself cannot " +
                "undo either one."));
            return card.Root;
        }

        // -------------------------------------------------------- left behind

        /// <summary>
        /// Everything the migration could not finish, gathered in one place
        /// rather than spread through two reports.
        ///
        /// This card exists because of what a real project looks like on the
        /// other side of the button. The conversion succeeds and the numbers are
        /// large and encouraging, and then there are two hundred prefabs whose
        /// fonts have no file, a vendor's plugin that will not compile, and four
        /// scripts that were refused for a reason stated once and scrolled past.
        /// None of those are failures of the run; they are the work the run
        /// cannot do, and a person who is handed them as a list will do them.
        /// The same person, handed a green summary and a compiler, will not.
        /// </summary>
        private VisualElement LeftBehindCard()
        {
            var refused = new List<TmpScriptFinding>();
            var assemblies = new SortedDictionary<string, List<string>>();
            var vendored = new List<TmpScriptFinding>();

            if (_findings != null)
            {
                foreach (var finding in _findings)
                {
                    if (!finding.Result.Viable) refused.Add(finding);
                    if (finding.IsLikelyThirdParty) vendored.Add(finding);
                    if (finding.NeedsAssemblyPatch && finding.Assembly != null)
                        assemblies[finding.Assembly.Path] = finding.MissingReferences;
                }
            }

            var groups = _report?.Groups ?? new List<TmpFileGroup>();
            bool anything = refused.Count > 0 || assemblies.Count > 0 || vendored.Count > 0 ||
                            groups.Count > 0 || (_migration?.Recovery?.Count ?? 0) > 0;
            if (!anything) return new VisualElement();

            var card = HubUI.MakeCard("What you have to fix by hand",
                "The run did not fail. These are the jobs it cannot do for you, listed so you do " +
                "not have to find them in the console.");

            var fonts = _migration?.Recovery;
            if (fonts != null && fonts.Count > 0) FontSection(card, fonts);
            if (assemblies.Count > 0) AssemblySection(card, assemblies);
            if (refused.Count > 0) RefusedSection(card, refused);
            if (groups.Count > 0) GroupSection(card, groups);
            if (vendored.Count > 0) VendorSection(card, vendored);
            return card.Root;
        }

        /// <summary>
        /// Fonts the migration could not rasterise, one row per typeface rather
        /// than per label.
        ///
        /// This is the largest single category on a real project and the most
        /// misleading one to report by count: an asset pack ships a baked SDF
        /// atlas and no .ttf, TMP is content with that and OneText cannot be,
        /// and one missing file turns into thousands of errors that are all the
        /// same error. So they are collapsed to the typeface, a placeholder
        /// asset already stands where the font goes, and the row says the one
        /// thing that fixes every label at once: put this file here.
        ///
        /// Where the typeface is open-licensed and we can name a legitimate
        /// source, there is a button. It downloads exactly one font, after the
        /// licence has been on screen, and checks that what came back is a font
        /// before anything touches the project.
        /// </summary>
        private void FontSection(HubUI.Card card, FontRecoveryManifest fonts)
        {
            card.Add(HubUI.Notice(
                $"{fonts.Unfilled:n0} typeface(s) have no font file in this project, and " +
                $"{fonts.LabelCount:n0} label(s) are waiting on them. OneText draws from the " +
                ".ttf or .otf itself, so a baked TMP atlas is not something it can read. A " +
                "placeholder asset is already in place for each — drop the font in and every " +
                "label using it comes back at once.", HubTone.Bad));

            foreach (var entry in fonts.Entries)
            {
                var row = HubUI.Box("folder-row");
                var name = HubUI.Text(entry.FamilyName ?? entry.Key, "kv__value");
                name.style.flexGrow = 1f;
                row.Add(name);
                row.Add(HubUI.Badge($"{entry.LabelCount:n0} label(s)", HubTone.Info));
                row.Add(HubUI.Badge($"{entry.FontAssets.Count:n0} TMP asset(s)", HubTone.Neutral));
                if (entry.Filled) row.Add(HubUI.Badge("filled", HubTone.Good));
                card.Add(row);

                if (entry.Filled) continue;

                card.Add(HubUI.KeyValue("wants", entry.ExpectedFileName ?? "the original font file"));
                if (!string.IsNullOrEmpty(entry.PlaceholderAssetPath))
                {
                    var slot = HubUI.Box("row");
                    slot.Add(HubUI.Mono(HubUI.Text(entry.PlaceholderAssetPath, "kv__value")));
                    var placeholder = AssetDatabase.LoadAssetAtPath<Object>(entry.PlaceholderAssetPath);
                    if (placeholder != null)
                        slot.Add(HubUI.Quiet("Show", () => EditorGUIUtility.PingObject(placeholder)));
                    card.Add(slot);
                }

                var candidate = entry.Candidate;
                if (!candidate.Found)
                {
                    // Looking a family up is a request, so it is a click rather
                    // than something a scan does on your behalf a thousand
                    // times. The offline half already knows whether there is
                    // anywhere to look, which is what decides the button.
                    if (!entry.Resolved && FontSourceCatalog.CanResolve(entry.FamilyName))
                    {
                        var looking = entry;
                        card.Add(HubUI.KeyValue("source",
                            "Not in the short list this window carries. It can ask the Google " +
                            "Fonts catalogue whether this family is published there — one " +
                            "request, and the licence comes back with it.", HubTone.Neutral));
                        card.Add(HubUI.Quiet($"Look up {entry.FamilyName}", () =>
                        {
                            var found = FontRecovery.Resolve(looking);
                            Refresh();
                            if (found.Ok) Say(found.Message); else SayBadly(found.Message);
                        }));
                        continue;
                    }

                    card.Add(HubUI.KeyValue("source",
                        "Not one this window can name. Asset-pack and commercial faces are not " +
                        "ours to fetch — find the file you licensed and drop it on the " +
                        "placeholder.", HubTone.Warn));
                    continue;
                }

                // The licence is on screen before the button is, which is the
                // whole point of having a button: the person clicking it is
                // agreeing to something specific rather than to a download.
                card.Add(HubUI.KeyValue($"licence — {candidate.LicenceName}",
                    candidate.LicenceUrl ?? candidate.HomeUrl ?? "see the source"));
                if (!string.IsNullOrEmpty(candidate.Note))
                    card.Add(HubUI.KeyValue("note", candidate.Note, HubTone.Warn));

                if (string.IsNullOrEmpty(candidate.DownloadUrl))
                {
                    card.Add(HubUI.KeyValue("source",
                        $"{candidate.HomeUrl} — published behind a page rather than a file, so " +
                        "this one is a manual download.", HubTone.Warn));
                    continue;
                }

                var wanted = entry;
                var actions = HubUI.Box("row");
                actions.Add(HubUI.Quiet($"Download {candidate.FamilyName} ({candidate.LicenceName})",
                    () =>
                    {
                        var outcome = FontRecovery.Recover(wanted);
                        AssetDatabase.Refresh();
                        Refresh();
                        if (outcome.Outcome == FontSourceOutcome.Ok) Say(outcome.Message);
                        else SayBadly(outcome.Message);
                    }));
                actions.Add(HubUI.Quiet("Open source page",
                    () => Application.OpenURL(candidate.HomeUrl ?? candidate.DownloadUrl)));
                card.Add(actions);
            }
        }

        /// <summary>
        /// Assemblies that will not see OneText once their files are rewritten.
        ///
        /// OneText's own assemblies are auto-referenced, which covers
        /// <c>Assembly-CSharp</c> and nothing else. A file under somebody's
        /// <c>.asmdef</c> compiles against exactly what that file lists, so a
        /// rewrite there produces a type the assembly has never heard of. The
        /// fix is one line in a JSON file, which is why there is a button for
        /// it rather than a paragraph.
        /// </summary>
        private void AssemblySection(HubUI.Card card, SortedDictionary<string, List<string>> assemblies)
        {
            card.Add(HubUI.Notice(
                $"{assemblies.Count:n0} assembly definition(s) do not reference OneText. Rewriting " +
                "the files inside them produces types those assemblies cannot see — the rewrite " +
                "compiles nowhere until this is added.", HubTone.Bad));

            foreach (var pair in assemblies)
            {
                string path = pair.Key;
                var missing = pair.Value;

                var row = HubUI.Box("folder-row");
                var name = HubUI.Mono(HubUI.Text(ProjectPath(path), "kv__value"));
                name.style.flexGrow = 1f;
                row.Add(name);
                row.Add(HubUI.Badge(string.Join(", ", missing), HubTone.Warn));
                row.Add(HubUI.Quiet("Add references", () =>
                {
                    if (!OnboardingGit.ConfirmOverwrite("Patch assembly definition?",
                            $"{ProjectPath(path)} will gain {string.Join(", ", missing)}.",
                            new List<string> { ProjectPath(path) }, "Patch"))
                        return;

                    if (TmpAssemblyGraph.Patch(path, missing))
                    {
                        AssetDatabase.Refresh();
                        Scan();
                        Say($"Added {string.Join(", ", missing)} to {System.IO.Path.GetFileName(path)}.");
                    }
                    else
                    {
                        SayBadly($"{ProjectPath(path)} could not be written.");
                    }
                }));
                card.Add(row);
            }
        }

        /// <summary>
        /// Files the rewriter looked at and declined, each with the sentence
        /// saying why.
        ///
        /// A refusal is the rewriter working. Rewriting a file it knows it
        /// cannot finish leaves a project that does not build, and the names it
        /// could not carry are the reason — so they are printed, and the file is
        /// left exactly as it was.
        /// </summary>
        private void RefusedSection(HubUI.Card card, List<TmpScriptFinding> refused)
        {
            card.Add(HubUI.Notice(
                $"{refused.Count:n0} file(s) were left untouched on purpose. Each needs a hand " +
                "first; rewriting them as they stand would not compile.", HubTone.Warn));

            foreach (var finding in refused)
            {
                var row = HubUI.Box("folder-row");
                var name = HubUI.Mono(HubUI.Text(ProjectPath(finding.Path), "kv__value"));
                name.style.flexGrow = 1f;
                row.Add(name);

                var asset = AssetDatabase.LoadAssetAtPath<Object>(ProjectPath(finding.Path));
                if (asset != null)
                    row.Add(HubUI.Quiet("Show", () => EditorGUIUtility.PingObject(asset)));
                card.Add(row);

                card.Add(HubUI.KeyValue("why", finding.Result.Blocker ?? "unstated", HubTone.Warn));
                if (finding.Result.BlockingNames != null && finding.Result.BlockingNames.Count > 0)
                    card.Add(HubUI.KeyValue("in the way",
                        string.Join(", ", finding.Result.BlockingNames)));
            }
        }

        /// <summary>
        /// Files that only compile if they move together.
        ///
        /// A file declaring <c>void Fade(this TMP_Text t)</c> and every file
        /// calling it are one unit: convert the declaration alone and the calls
        /// stop resolving, convert the calls alone and they find nothing.
        /// Unticking one row of a group is the way to break a build with every
        /// individual choice looking reasonable, so the group is stated and the
        /// selection is held to it.
        /// </summary>
        private void GroupSection(HubUI.Card card, List<TmpFileGroup> groups)
        {
            card.Add(HubUI.Notice(
                $"{groups.Count:n0} set(s) of files have to be rewritten together — one names a " +
                "TextMesh Pro type in something the others use. Ticking part of a set is how a " +
                "build breaks with every single choice looking sensible.", HubTone.Warn));

            foreach (var group in groups)
            {
                bool stuck = Stuck(group);
                card.Add(HubUI.KeyValue(stuck ? "held back" : "together",
                    group.Reason ?? "shared type", stuck ? HubTone.Bad : HubTone.Neutral));

                if (stuck)
                {
                    var blockers = new List<string>();
                    foreach (var finding in _findings)
                        if (!finding.Result.Viable && group.Paths.Contains(finding.Path))
                            blockers.Add(System.IO.Path.GetFileName(finding.Path));

                    card.Add(HubUI.Notice(
                        $"None of these will be rewritten while {string.Join(" and ", blockers)} " +
                        "cannot be. That file is refused above, and the set only compiles whole — " +
                        "so the refusal has to be dealt with by hand first, and then this scanned " +
                        "again.", HubTone.Bad));
                }

                foreach (string path in group.Paths)
                    card.Add(HubUI.Mono(HubUI.Text($"    {ProjectPath(path)}", "code")));
            }
        }

        /// <summary>
        /// Somebody else's code, and the one vendor we meet halfway.
        ///
        /// A rewrite inside a vendor's folder is undone by their next update,
        /// silently, months later. For DOTween — the one integration OneText
        /// ships — there is a supported answer that does not involve editing
        /// their source at all, so it is spelled out here rather than left to be
        /// discovered.
        /// </summary>
        private void VendorSection(HubUI.Card card, List<TmpScriptFinding> vendored)
        {
            card.Add(HubUI.Notice(
                $"{vendored.Count:n0} file(s) live in somebody else's folder and start unticked. " +
                "A rewrite there is replaced by their next update, without a word.", HubTone.Warn));

            foreach (var finding in vendored)
                card.Add(HubUI.Mono(HubUI.Text($"    {ProjectPath(finding.Path)}", "code")));

            bool dotween = false;
            foreach (var finding in vendored)
                if (finding.Path.Replace('\\', '/').IndexOf("/Demigiant/", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    finding.Path.IndexOf("DOTween", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    dotween = true;
            if (!dotween) return;

            card.Add(HubUI.Notice(
                "DOTween is here, and OneText ships an integration for it so its source never has " +
                "to be edited. Three things it cannot do for you:", HubTone.Info));
            card.Add(HubUI.KeyValue("1. define ONETEXT_DOTWEEN",
                "Player Settings > Scripting Define Symbols, per build target. Until then the " +
                "integration assembly does not compile and none of the DO* shortcuts resolve. " +
                "Installed from OpenUPM instead? Then it is already on."));
            card.Add(HubUI.KeyValue("2. re-run DOTween's setup",
                "Tools > Demigiant > DOTween Utility Panel > Setup DOTween. DOTween Pro guards " +
                "its own TMP files with a marker that only its panel can flip; with TMP gone and " +
                "the marker still set, their source stops compiling."));
            card.Add(HubUI.KeyValue("3. re-author DOTweenAnimation components",
                "Any set to TargetType.TextMeshPro or TextMeshProUGUI dispatch inside DOTween's " +
                "compiled code on the component type, so a converted object animates nothing, " +
                "quietly. Those need a code-driven tween or a re-authored animation."));
            card.Add(HubUI.KeyValue("no counterpart",
                "DOFaceColor, DOFaceFade, DOOutlineColor and DOGlowColor tween TMP material " +
                "properties OneText does not have — it carries face, outline and glow as per-quad " +
                "decoration. DOTweenTMPAnimator rewrites TMP mesh vertices from outside; OneText's " +
                "equivalent is ITextQuadModifier, which a wrapper cannot stand in for."));
        }

        // ------------------------------------------------------- components

        private VisualElement ComponentCard()
        {
            var card = HubUI.MakeCard("Step 1 · Scenes and prefabs",
                "TextMeshProUGUI and TextMeshPro become OneTextLabel and OneTextMesh, " +
                "TMP_InputField becomes OneTextInputField, and UnityEngine.UI.Text and TextMesh " +
                "come along too. Font assets are made from the .ttf behind them, once each, and " +
                "every reference to a swapped component is re-pointed and read back.");

            card.Act(HubUI.Ghost(_migration == null ? "Scan Scenes & Prefabs…" : "Scan again",
                ScanComponents));

            // One button, saying which of the two jobs it is about to do. A
            // second button for "convert everything" next to a selection is how
            // somebody converts a project they meant to convert a corner of.
            if (_migration != null && !_converted && Convertible() > 0)
            {
                card.Act(_picked.Count == 0
                    ? HubUI.Primary($"Convert all {Convertible():n0} component(s)",
                        () => Convert(null))
                    : HubUI.Primary(
                        $"Convert {ConvertibleIn(_picked):n0} component(s) in " +
                        $"{_picked.Count:n0} container(s)",
                        () => Convert(new List<string>(_picked))));
            }

            if (!MigrationProviders.HasTextMeshPro)
            {
                card.Add(HubUI.Notice(
                    "TextMesh Pro is not in this project, so nothing here looks for it: the scan " +
                    "covers UnityEngine.UI.Text and TextMesh only. Install TMP if you still have " +
                    "TMP components to convert — this window will find them the moment it is back.",
                    HubTone.Warn));
            }

            card.Add(HubUI.Pill("Also scan scenes that are not in Build Settings",
                _allScenes, on =>
                {
                    _allScenes = on;
                    Refresh();
                }));

            // The two toggles belong together: what a narrowed run cannot mend
            // is exactly the references reaching in from the containers it left
            // out, so the warning about one is really a nudge about the other.
            card.Add(HubUI.Pill("Fix references that cross from one scene or prefab into another",
                _crossContainer, on =>
            {
                _crossContainer = on;
                Refresh();
            }));

            if (!_crossContainer)
            {
                card.Add(HubUI.Notice(
                    "A field in one prefab that names a label in another is broken by converting " +
                    "the second, and with this off nothing will mend it or say so. It is only a " +
                    "switch because it costs one extra open of each asset that nests another.",
                    HubTone.Warn));
            }
            else if (!_allScenes)
            {
                card.Add(HubUI.Notice(
                    "References are followed, but only into the containers this run covers. " +
                    "Scenes outside Build Settings are not being read, so a field in one of them " +
                    "pointing at a converted label will not be mended — turn on every scene above " +
                    "if that matters here.", HubTone.Info));
            }

            if (_migration == null)
            {
                card.Add(HubUI.Empty("Start here: scan your scenes and prefabs",
                    "Opens each one, reads every component on every object — inactive ones " +
                    "included — and closes it again. Nothing is saved and nothing is marked " +
                    "dirty. You get a count and a list of what would not convert cleanly.",
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
            // Where "no counterpart · stay as TMP" used to be. The dropdown was
            // the last component that stayed, so that tile could only ever say
            // zero — and the count worth four tiles' worth of space is the one
            // that replaced it. Components with no counterpart are still
            // reported; they are findings on a component that converts, not a
            // component that does not.
            tiles.Add(HubUI.Tile("dropdowns",
                (_migration.CountOfKind(MigrationKind.Dropdown)).ToString("n0"),
                "become OneTextDropdown"));
            card.Add(tiles);

            var severity = HubUI.Box("tiles");
            severity.Add(HubUI.Tile("containers", _migration.Containers.Count.ToString("n0"),
                $"of {_migration.ContainersScanned:n0} opened"));
            severity.Add(HubUI.Tile("errors", _migration.Errors.ToString("n0"),
                "need you to fix them", _migration.Errors == 0 ? HubTone.Good : HubTone.Bad));
            severity.Add(HubUI.Tile("warnings", _migration.Warnings.ToString("n0"),
                "convert, but not identically",
                _migration.Warnings == 0 ? HubTone.Good : HubTone.Warn));
            severity.Add(HubUI.Tile("notes",
                _migration.Count(DoctorSeverity.Info).ToString("n0"), "read once, no action"));
            card.Add(severity);

            if (_converted)
            {
                card.Add(HubUI.Notice(
                    $"{_migration.Converted:n0} component(s) swapped, {_migration.FontsCreated:n0} " +
                    $"font asset(s) created, {_migration.Relinked:n0} reference(s) re-pointed. " +
                    "Everything still listed below is what remains after the conversion.",
                    _migration.Errors == 0 ? HubTone.Good : HubTone.Warn));

                // A narrowed run reports on the containers it was given and on
                // nothing else — it never opened the rest — so the report on
                // screen is not a picture of the project any more. Saying so is
                // the difference between "part of the job is done" and "the job
                // looks done".
                if (_partial)
                {
                    card.Add(HubUI.Notice(
                        "That was the part you picked, and this report covers only those " +
                        "containers — the rest of the project was not opened to be counted. " +
                        "Scan again for what is left; anything already converted is skipped, so " +
                        "the count only ever goes down.", HubTone.Info));
                }
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

        private int ConvertibleIn(ICollection<string> containers)
        {
            if (_migration == null) return 0;
            int n = 0;
            foreach (var target in _migration.Targets)
                if (target.Convertible && containers.Contains(target.Container ?? string.Empty)) n++;
            return n;
        }

        private void ScanComponents()
        {
            _migration = ComponentMigration.Scan(new ComponentMigration.Options
            {
                AllScenes = _allScenes,
                CrossContainerReferences = _crossContainer,
            });
            // Scanning writes nothing, so no placeholder is made here: the list
            // is built so the count can be on screen before anybody commits.
            FontRecovery.Collect(_migration);
            _converted = false;
            _partial = false;

            // The ticks named containers in the report that has just been
            // replaced. Keeping the ones that are still here would be tidier and
            // wronger: a tick nobody put there is how a conversion happens to a
            // prefab nobody chose.
            _picked.Clear();
            Refresh();
            Say(_migration.Summary());
        }

        /// <summary>
        /// The containers, each with what it holds, a tick, and a button that
        /// converts only that one.
        ///
        /// A few at a time is the option a real project uses. A migration that
        /// is all-or-nothing across four hundred prefabs is a migration nobody
        /// starts on a Tuesday — and the reason it used to be all-or-nothing was
        /// not squeamishness, it was that a narrow run left dead references in
        /// every file it did not open. It no longer does: whatever the tick
        /// says, the references reaching into what was converted are followed
        /// across the whole project and re-pointed.
        /// </summary>
        private VisualElement ContainersCard()
        {
            var card = HubUI.MakeCard("Where they are",
                "Tick the ones to convert, or convert them all and skip the ticking. Prefabs " +
                "convert before scenes, and a base prefab before anything built out of it, so a " +
                "variant never ends up holding both components.").Flush();

            if (!_converted)
            {
                card.Act(HubUI.Quiet("Tick all", () =>
                {
                    foreach (string container in _migration.Containers) _picked.Add(container);
                    Refresh();
                }));
                card.Act(HubUI.Quiet("Tick none", () =>
                {
                    _picked.Clear();
                    Refresh();
                }));
            }

            // Counted in two passes over the report rather than one pass per
            // container. A project with a thousand containers and six thousand
            // findings makes the nested version six million comparisons, every
            // time this screen is drawn.
            var targetsIn = new Dictionary<string, int>();
            var errorsIn = new Dictionary<string, int>();
            foreach (var target in _migration.Targets)
            {
                targetsIn.TryGetValue(target.Container ?? "", out int n);
                targetsIn[target.Container ?? ""] = n + 1;
            }
            foreach (var finding in _migration.Findings)
            {
                if (finding.Severity != DoctorSeverity.Error) continue;
                errorsIn.TryGetValue(finding.Container ?? "", out int n);
                errorsIn[finding.Container ?? ""] = n + 1;
            }

            foreach (string container in _migration.Containers)
            {
                targetsIn.TryGetValue(container, out int targets);
                errorsIn.TryGetValue(container, out int errors);

                // Loaded when the button is pressed, not when the row is built:
                // building the row for every container would otherwise load
                // every prefab in the project into memory to decide whether to
                // draw a button.
                string only = container;

                var row = HubUI.Box("folder-row");
                if (!_converted)
                {
                    row.Add(HubUI.Pill(System.IO.Path.GetFileNameWithoutExtension(container),
                        _picked.Contains(container), on =>
                        {
                            if (on) _picked.Add(only);
                            else _picked.Remove(only);
                            Refresh();
                        }));
                }

                var name = HubUI.Mono(HubUI.Text(container, "kv__value"));
                name.style.flexGrow = 1f;
                row.Add(name);
                row.Add(HubUI.Badge($"{targets:n0} component(s)", HubTone.Info));
                if (errors > 0) row.Add(HubUI.Badge($"{errors:n0} error(s)", HubTone.Bad));

                row.Add(HubUI.Quiet("Show", () => Ping(only)));
                if (!_converted)
                    row.Add(HubUI.Quiet("Convert", () => Convert(new List<string> { only })));
                card.Add(row);
            }
            return card.Root;
        }

        private static void Ping(string container)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(container);
            if (asset != null) EditorGUIUtility.PingObject(asset);
            else Debug.LogWarning($"OneText: nothing is at {container} any more.");
        }

        /// <summary>How many of one rule's findings are drawn before the rest are counted.</summary>
        private const int PerRule = 50;

        /// <summary>
        /// Below this many findings in total, every group opens: a short list is
        /// easier read flat than clicked open, and the collapsing here exists for
        /// the long one.
        /// </summary>
        private const int OpenBelow = 20;

        /// <summary>
        /// The findings, gathered by the rule that found them.
        ///
        /// One card per finding is the right shape for a project with twelve of
        /// them and the wrong shape for a real one. Five-Dice produces 6,422
        /// errors and warnings, 5,487 of which are the same missing font file
        /// reported once per label — and drawing 6,422 cards is not a report, it
        /// is a hang, followed by a wall nobody reads. Which is the failure this
        /// whole screen was written against.
        ///
        /// So: one row per rule with its count, the rows shut, and the rules that
        /// need a person sorted to the top. The fonts are not here at all — they
        /// have a section of their own further down that already collapses them
        /// to one row per typeface, which is the unit that actually gets fixed.
        /// </summary>
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

            bool fontsHaveTheirOwnSection = (_migration.Recovery?.Count ?? 0) > 0;
            int elsewhere = 0;

            var order = new List<string>();
            var byRule = new Dictionary<string, List<MigrationFinding>>();
            foreach (var finding in _migration.Findings)
            {
                if (finding.Severity == DoctorSeverity.Info && !_showNotes) continue;
                if (fontsHaveTheirOwnSection && finding.Rule == FontRule)
                {
                    elsewhere++;
                    continue;
                }

                string rule = string.IsNullOrEmpty(finding.Rule) ? "unnamed" : finding.Rule;
                if (!byRule.TryGetValue(rule, out var list))
                {
                    byRule[rule] = list = new List<MigrationFinding>();
                    order.Add(rule);
                }
                list.Add(finding);
            }

            if (elsewhere > 0)
            {
                host.Add(HubUI.Notice(
                    $"{elsewhere:n0} finding(s) are one label each waiting on a missing font file. " +
                    "They are not listed here: they are the same handful of typefaces over and " +
                    "over, and 'What you have to fix by hand' below has them one row per typeface, " +
                    "which is the unit that fixes them.", HubTone.Warn));
            }

            if (order.Count == 0)
            {
                host.Add(HubUI.Notice("Nothing left to report beyond the fonts.", HubTone.Good));
                return host;
            }

            // Worst first, and within a severity the largest group first: the
            // order somebody would work in if they were choosing.
            order.Sort((a, b) =>
            {
                int severity = Worst(byRule[b]).CompareTo(Worst(byRule[a]));
                if (severity != 0) return severity;
                int size = byRule[b].Count.CompareTo(byRule[a].Count);
                return size != 0 ? size : string.CompareOrdinal(a, b);
            });

            int total = 0;
            foreach (var pair in byRule) total += pair.Value.Count;

            foreach (string rule in order) host.Add(RuleGroup(rule, byRule[rule], total <= OpenBelow));
            return host;
        }

        /// <summary>The rule whose findings are one per label rather than one per fix.</summary>
        private const string FontRule = "font-source-missing";

        private static DoctorSeverity Worst(List<MigrationFinding> findings)
        {
            var worst = DoctorSeverity.Info;
            foreach (var finding in findings) if (finding.Severity > worst) worst = finding.Severity;
            return worst;
        }

        private static VisualElement RuleGroup(string rule, List<MigrationFinding> findings, bool open)
        {
            var tone = Worst(findings) switch
            {
                DoctorSeverity.Error => HubTone.Bad,
                DoctorSeverity.Warning => HubTone.Warn,
                _ => HubTone.Neutral,
            };

            var body = new VisualElement();
            int drawn = System.Math.Min(findings.Count, PerRule);
            for (int i = 0; i < drawn; i++) body.Add(Finding(findings[i]));

            if (findings.Count > drawn)
            {
                var row = HubUI.Box("folder-row");
                var rest = HubUI.Text(
                    $"{findings.Count - drawn:n0} more not drawn, because drawing them is slower " +
                    "than reading them.", "kv__value");
                rest.style.flexGrow = 1f;
                row.Add(rest);

                var all = findings;
                string named = rule;
                row.Add(HubUI.Quiet("Print all to Console", () =>
                {
                    foreach (var finding in all)
                    {
                        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null,
                            "OneText migration · {0} · {1} · {2}\n{3}",
                            named, finding.Container, finding.Path, finding.Message);
                    }
                }));
                body.Add(row);
            }

            var group = HubUI.Disclose($"{rule}  ·  {findings.Count:n0}", body, open);
            group.style.borderLeftWidth = 2f;
            group.style.borderLeftColor = tone switch
            {
                HubTone.Bad => new Color(1f, 0.482f, 0.447f),
                HubTone.Warn => new Color(1f, 0.8f, 0.4f),
                _ => new Color(0.839f, 0.898f, 0.867f, 0.2f),
            };
            return group;
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
                string container = finding.Container;
                row.Add(HubUI.Quiet("Show in project", () => Ping(container)));
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
            // Asked here rather than left to the run, so the dialog lists the
            // files that are really about to be written. A selection quietly
            // growing between the confirmation and the work is the one thing a
            // confirmation cannot survive.
            int picked = only?.Count ?? 0;
            if (only != null) only = ComponentMigration.WithWhatTheyNest(only);

            var containers = only ?? new List<string>(_migration.Containers);
            if (containers.Count == 0)
            {
                SayBadly("Nothing to convert.");
                return;
            }

            int components = 0;
            foreach (var target in _migration.Targets)
                if (target.Convertible && containers.Contains(target.Container)) components++;

            // Said out loud because it is the one thing about a partial run that
            // surprises people in a diff: converting four prefabs writes those
            // four, and also whichever files elsewhere held a field naming
            // something inside them. That write is the point — the alternative
            // is those fields reading None — but a person about to commit it
            // should have read the sentence first.
            string spread = only != null && _crossContainer
                ? " Files outside this selection that point at a component in it are opened " +
                  "afterwards and re-pointed, so some of them will be saved too."
                : string.Empty;

            string nested = only != null && only.Count > picked
                ? $" {only.Count - picked:n0} of these you did not tick: your selection is built " +
                  "out of them, and a label that lives in a nested prefab has to be converted " +
                  "there rather than as an override."
                : string.Empty;

            if (!OnboardingGit.ConfirmOverwrite("Convert components?",
                    $"{components:n0} component(s) in {containers.Count:n0} scene(s) and prefab(s) " +
                    "will be destroyed and replaced, and those files will be saved." +
                    nested + spread,
                    containers, "Convert"))
                return;

            _migration = ComponentMigration.Apply(new ComponentMigration.Options
            {
                AllScenes = _allScenes,
                OnlyContainers = only,
                AdoptProjectFontDefaults = true,
                CrossContainerReferences = _crossContainer,
            });
            FontRecovery.Collect(_migration, createPlaceholders: true);
            _converted = true;
            _partial = only != null;
            _picked.Clear();
            Refresh();
            Say($"Converted {_migration.Converted:n0} component(s). {_migration.Summary()}");
        }

        // --------------------------------------------------------------- scan

        private VisualElement ScanCard()
        {
            var card = HubUI.MakeCard("Step 2 · Your scripts",
                "TextMeshProUGUI and TMP_Text become OneTextLabel, TMP_InputField becomes " +
                "OneTextInputField, TextMeshPro becomes OneTextMesh, TMPro-qualified " +
                "TextAlignmentOptions and TextWrappingModes re-qualify to OneText's own, and " +
                "using TMPro becomes the namespaces those live in. Type names only: never a " +
                "member, never a string, never a comment.");

            card.Act(HubUI.Ghost(_findings == null ? "Scan Assets…" : "Scan again", Scan));
            if (_findings != null && Selected().Count > 0)
                card.Act(HubUI.Primary($"Rewrite {Selected().Count} file(s)", Apply));

            if (_findings == null)
            {
                card.Add(HubUI.Empty("Scan your scripts",
                    "Reads every .cs file under Assets — packages are left alone — and lists the " +
                    "ones that mention TextMesh Pro. Scanning writes nothing.",
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
                    "Some files still need you afterwards: they use TMP names OneText has no " +
                    "counterpart for. They are marked below with the names, and after the " +
                    "rewrite the compiler points at the same lines.",
                    HubTone.Warn));
            }
            return card.Root;
        }

        private void Scan()
        {
            var paths = TmpScriptRewriter.ScriptsUnder(Application.dataPath);
            _scanned = paths.Count;
            _report = TmpScriptRewriter.ScanProject(paths, Application.dataPath);
            _findings = _report.Files;
            _skipped.Clear();

            // Somebody else's folder starts unticked. A vendor's source is
            // replaced wholesale by the next update, so a rewrite there is work
            // that will be undone by a version bump nobody connects to it — and
            // in the one case that matters, DOTween, the integration assembly is
            // the supported answer rather than editing their files.
            foreach (var finding in _findings)
                if (finding.IsLikelyThirdParty) _skipped.Add(finding.Path);

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

                if (!finding.Result.Viable)
                    row.Add(HubUI.Badge("refused", HubTone.Bad));
                else
                    row.Add(HubUI.Badge($"{finding.Result.Changes.Count:n0} line(s)",
                        finding.Result.Changed ? HubTone.Info : HubTone.Neutral));

                if (finding.Result.Residuals.Count > 0)
                    row.Add(HubUI.Badge($"{finding.Result.Residuals.Count:n0} left", HubTone.Warn));
                if (finding.IsLikelyThirdParty)
                    row.Add(HubUI.Badge("third party", HubTone.Warn));
                if (finding.NeedsAssemblyPatch)
                    row.Add(HubUI.Badge("asmdef", HubTone.Warn));

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

        /// <summary>
        /// A group nothing can satisfy, because one of its members was refused.
        ///
        /// A group is the set of files that only compile together — a
        /// declaration written in TMP terms and everyone who reads it. If one
        /// member cannot be rewritten at all, then rewriting the rest is the
        /// exact failure the grouping exists to prevent: the declaration moves
        /// to OneText and a caller left behind can no longer reach it. So the
        /// group is held back whole, and the refusal that is blocking it is what
        /// the person has to deal with first.
        /// </summary>
        private bool Stuck(TmpFileGroup group)
        {
            if (_findings == null) return false;
            foreach (var finding in _findings)
                if (!finding.Result.Viable && group.Paths.Contains(finding.Path)) return true;
            return false;
        }

        /// <summary>
        /// The ticked files, plus whatever they drag along, minus what cannot
        /// move at all.
        ///
        /// Grouping is not advice. A file that declares an extension method on
        /// a TMP type and the files that call it cannot be rewritten apart in
        /// either direction, so ticking one member of a group ticks the group —
        /// and a group with a refusal in it ticks nothing. Doing all of that
        /// here rather than in the click handler means the count on the button
        /// is already the truth.
        /// </summary>
        private List<TmpScriptFinding> Selected()
        {
            var selected = new List<TmpScriptFinding>();
            if (_findings == null) return selected;

            var wanted = new HashSet<string>();
            foreach (var finding in _findings)
                if (Included(finding)) wanted.Add(finding.Path);

            var blocked = new HashSet<string>();
            if (_report != null)
            {
                foreach (var group in _report.Groups)
                {
                    if (Stuck(group))
                    {
                        foreach (string path in group.Paths) blocked.Add(path);
                        continue;
                    }
                    bool any = false;
                    foreach (string path in group.Paths) if (wanted.Contains(path)) any = true;
                    if (!any) continue;
                    foreach (string path in group.Paths) wanted.Add(path);
                }
            }

            foreach (var finding in _findings)
            {
                if (blocked.Contains(finding.Path)) continue;
                if (wanted.Contains(finding.Path) && finding.Result.Changed) selected.Add(finding);
            }
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
