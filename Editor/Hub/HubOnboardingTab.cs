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
    /// The hard part of leaving TMP was never the pixels; it is that
    /// <c>TextMeshProUGUI</c> is written in four hundred files and a person has
    /// to open all of them. This scans the project's own scripts, shows exactly
    /// which lines would change and which TMP names it cannot carry over, and
    /// rewrites the files you tick — and it does all of that as text, so it
    /// works in a project where TMP was already removed and nothing compiles,
    /// which is the project that needs it most.
    ///
    /// Nothing is written without the diff being on screen first, and nothing
    /// is written over uncommitted work without saying so.
    /// </summary>
    public sealed class HubOnboardingTab : HubSection
    {
        private List<TmpScriptFinding> _findings;
        private readonly HashSet<string> _skipped = new HashSet<string>();
        private int _preview = -1;
        private int _scanned;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Onboarding;

        public override string Title => "Onboarding";

        public override string Eyebrow => "Coming from TextMesh Pro?";

        public override string Lede =>
            "The type names and the using, rewritten across your own scripts, with the diff on " +
            "screen before anything is written and every TMP name it cannot carry over named up " +
            "front. Pure text: it works even with the package already gone.";

        public override string NavHint => "migrate off TMP";

        public override string NavGroup => "Migrate";

        public override string BadgeText =>
            _findings == null ? "—" : _findings.Count.ToString("n0");

        public override HubTone BadgeTone =>
            _findings == null ? HubTone.Neutral
            : _findings.Count == 0 ? HubTone.Good
            : HubTone.Info;

        protected override void Compose(VisualElement content)
        {
            content.Add(ScanCard());
            if (_findings == null || _findings.Count == 0) return;
            content.Add(FilesCard());
            content.Add(PreviewCard());
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
        /// in this window is. So git is asked first, and when git cannot answer
        /// — no repository, no git on the path — that is said too, rather than
        /// quietly treated as "clean".
        /// </summary>
        private void Apply()
        {
            var targets = Selected();
            if (targets.Count == 0)
            {
                SayBadly("Nothing ticked.");
                return;
            }

            if (!ConfirmOverwrite(targets)) return;

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
            bool bom = false;
            using (var stream = File.OpenRead(path))
            {
                var head = new byte[3];
                int read = stream.Read(head, 0, 3);
                bom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }

        private bool ConfirmOverwrite(List<TmpScriptFinding> targets)
        {
            var relative = new List<string>();
            foreach (var finding in targets) relative.Add(ProjectPath(finding.Path));

            string status = Git($"status --porcelain -- {Quote(relative)}");
            if (status == null)
            {
                return HubUI.Confirm("Rewrite scripts?",
                    $"{targets.Count} file(s) will be overwritten in place.\n\n" +
                    "git could not be asked whether they hold uncommitted work — there may be no " +
                    "repository here, or no git on the path. This edit cannot be undone from " +
                    "inside Unity.",
                    "Rewrite anyway");
            }

            var dirty = new List<string>();
            foreach (string line in status.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                dirty.Add(trimmed);
            }

            if (dirty.Count == 0)
            {
                return HubUI.Confirm("Rewrite scripts?",
                    $"{targets.Count} file(s) will be overwritten in place. git reports every one " +
                    "of them committed, so this is revertible.",
                    "Rewrite");
            }

            var listing = new StringBuilder();
            for (int i = 0; i < dirty.Count && i < 10; i++) listing.Append('\n').Append(dirty[i]);
            if (dirty.Count > 10) listing.Append($"\n… and {dirty.Count - 10} more");

            return HubUI.Confirm("Uncommitted changes",
                $"{dirty.Count} of the file(s) about to be rewritten hold uncommitted work:\n" +
                listing + "\n\nRewriting overwrites it, and Unity cannot undo that. Commit or " +
                "stash first.",
                "Rewrite anyway");
        }

        private static string Quote(List<string> paths)
        {
            var quoted = new StringBuilder();
            foreach (string path in paths)
            {
                if (quoted.Length > 0) quoted.Append(' ');
                quoted.Append('"').Append(path).Append('"');
            }
            return quoted.ToString();
        }

        /// <summary>
        /// Asks git, and returns null for every way it can fail to answer:
        /// absent, not a repository, or too slow. "I do not know" and "clean"
        /// are different answers and the caller says different things about
        /// them.
        /// </summary>
        private static string Git(string arguments)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = ProjectRoot(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    if (process == null) return null;
                    string output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000)) return null;
                    return process.ExitCode == 0 ? output : null;
                }
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static string ProjectRoot() =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private static string ProjectPath(string fullPath) =>
            TextSourceScanner.ToProjectPath(fullPath);
    }
}
