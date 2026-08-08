using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Asks git whether the files about to be overwritten hold work nobody has
    /// committed, and says so before overwriting them.
    ///
    /// Both halves of this tab rewrite things in place — one rewrites source
    /// files, the other destroys components in scenes and prefabs — and neither
    /// has an undo worth the name once the editor has moved on. Version control
    /// is the undo, so the dialog's job is to say whether there is one.
    ///
    /// The important detail is that "git could not answer" is a third answer,
    /// not a synonym for clean. No repository, no git on the path, a timeout:
    /// each of those means the user is about to overwrite files with no safety
    /// net at all, which is the case that most deserves a sentence of its own.
    /// </summary>
    public static class OnboardingGit
    {
        /// <summary>Working-tree status for these project-relative paths, or null when git cannot say.</summary>
        public static List<string> Dirty(IList<string> projectPaths)
        {
            string status = Run($"status --porcelain -- {Quote(projectPaths)}");
            if (status == null) return null;

            var dirty = new List<string>();
            foreach (string line in status.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) dirty.Add(trimmed);
            }
            return dirty;
        }

        /// <summary>
        /// The confirmation both flows share: what is about to be overwritten,
        /// and whether git says it is recoverable.
        /// </summary>
        public static bool ConfirmOverwrite(string title, string what, IList<string> projectPaths,
            string yes)
        {
            var dirty = Dirty(projectPaths);

            if (dirty == null)
            {
                return HubUI.Confirm(title,
                    $"{what}\n\n" +
                    "git could not be asked whether they hold uncommitted work — there may be no " +
                    "repository here, or no git on the path. This edit cannot be undone from " +
                    "inside Unity.",
                    yes + " anyway");
            }

            if (dirty.Count == 0)
            {
                return HubUI.Confirm(title,
                    $"{what} git reports every one of them committed, so this is revertible.",
                    yes);
            }

            var listing = new StringBuilder();
            for (int i = 0; i < dirty.Count && i < 10; i++) listing.Append('\n').Append(dirty[i]);
            if (dirty.Count > 10) listing.Append($"\n… and {dirty.Count - 10} more");

            return HubUI.Confirm("Uncommitted changes",
                $"{dirty.Count} of the file(s) about to be changed hold uncommitted work:\n" +
                listing + "\n\nOverwriting loses it, and Unity cannot undo that. Commit or stash " +
                "first.",
                yes + " anyway");
        }

        private static string Quote(IList<string> paths)
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
        /// Runs git, and returns null for every way it can fail to answer:
        /// absent, not a repository, or too slow. "I do not know" and "clean"
        /// are different answers and the caller says different things about them.
        /// </summary>
        public static string Run(string arguments)
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

        public static string ProjectRoot() =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
    }
}
