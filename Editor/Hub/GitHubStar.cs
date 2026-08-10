using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace OneText.Editor
{
    /// <summary>
    /// Starring the repository from inside the editor, on a machine that
    /// already holds the credentials to do it.
    ///
    /// There is no link that stars a repository, and there is not meant to be:
    /// the star is a <c>PUT</c> carrying a token, because a plain GET that
    /// changed state could be planted in an image tag on any page and would
    /// star things on behalf of whoever loaded it. So a web page can only ever
    /// hand somebody to GitHub and hope.
    ///
    /// An editor is not a web page. A developer's machine usually has the
    /// GitHub CLI on it, already signed in, and
    /// <c>gh api --method PUT user/starred/OWNER/REPO</c> is the same call the
    /// website makes when the button is pressed. Where that is true the star
    /// lands without leaving the editor; where it is not, the browser opens on
    /// the repository as before. The click is the whole of the consent either
    /// way — nothing here runs unless somebody presses the button.
    ///
    /// Off the main thread, because it is two or three short-lived processes
    /// and a network round trip, and an editor that freezes to say thank you
    /// has not said it well.
    /// </summary>
    internal static class GitHubStar
    {
        public const string Owner = "CodeOneLabs";
        public const string Repo = "OneText";

        public enum Outcome
        {
            /// <summary>The star landed on this call.</summary>
            Starred,

            /// <summary>It was already there. Said differently, and not said twice.</summary>
            AlreadyStarred,

            /// <summary>No GitHub CLI on this machine; the caller should open a browser.</summary>
            NoCli,

            /// <summary>The CLI is here but nobody is signed in.</summary>
            NotSignedIn,

            /// <summary>It was tried and it did not work. The reason is logged.</summary>
            Failed,
        }

        /// <summary>
        /// Stars the repository, then calls back <em>on the main thread</em>,
        /// which is the only place the caller may touch the editor UI.
        /// </summary>
        public static void StarAsync(Action<Outcome> done)
        {
            if (done == null) return;
            Task.Run(() => Star()).ContinueWith(task =>
            {
                var outcome = task.IsFaulted ? Outcome.Failed : task.Result;
                if (task.IsFaulted) Debug.LogWarning($"OneText: {task.Exception?.GetBaseException().Message}");
                // Back to the main thread. ContinueWith runs on the pool, and
                // a Button's text set from there is a crash waiting for a
                // repaint.
                EditorApplication.delayCall += () => done(outcome);
            });
        }

        private static Outcome Star()
        {
            string cli = FindCli();
            if (cli == null) return Outcome.NoCli;

            // Asked before it is set, only so the thank-you can tell the truth.
            // The PUT is idempotent and would have succeeded either way.
            if (Run(cli, $"api user/starred/{Owner}/{Repo} --silent", out _, out _) == 0)
                return Outcome.AlreadyStarred;

            // A 404 above means "not starred" on a signed-in machine and
            // "who are you" on one that is not, and those want different
            // sentences, so the difference is worth one more process.
            if (Run(cli, "auth status", out _, out _) != 0) return Outcome.NotSignedIn;

            if (Run(cli, $"api --method PUT user/starred/{Owner}/{Repo} --silent",
                    out _, out string error) == 0)
                return Outcome.Starred;

            if (!string.IsNullOrWhiteSpace(error))
                Debug.LogWarning($"OneText: could not star the repository — {error.Trim()}");
            return Outcome.Failed;
        }

        /// <summary>
        /// Runs the CLI and returns its exit code, or -1 when it could not be
        /// run at all. Five seconds is the ceiling: this is one small request,
        /// and a CLI that has not answered by then is not going to.
        /// </summary>
        private static int Run(string cli, string arguments, out string output, out string error)
        {
            output = error = null;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(cli, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                process.Start();
                output = process.StandardOutput.ReadToEnd();
                error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch (Exception) { /* already gone */ }
                    return -1;
                }
                return process.ExitCode;
            }
            catch (Exception)
            {
                // Not there, not executable, blocked by policy: all the same
                // answer to the caller, which is "use the browser".
                return -1;
            }
        }

        private static string s_cli;
        private static bool s_searched;

        /// <summary>
        /// The GitHub CLI, or null.
        ///
        /// Searched in the usual install directories before the bare name,
        /// because an editor launched from the dock inherits a login shell's
        /// <c>PATH</c> on neither macOS nor Linux, and <c>gh</c> lives in
        /// Homebrew's directory on most of the machines that have it.
        /// </summary>
        private static string FindCli()
        {
            if (s_searched) return s_cli;
            s_searched = true;

            foreach (string candidate in Candidates())
            {
                if (candidate == null) continue;
                // A bare name has to be tried rather than tested for: it is
                // PATH that decides, and PATH is the shell's business.
                bool bare = !candidate.Contains(Path.DirectorySeparatorChar) &&
                            !candidate.Contains('/');
                if (!bare && !File.Exists(candidate)) continue;
                if (Run(candidate, "--version", out _, out _) != 0) continue;
                return s_cli = candidate;
            }
            return s_cli = null;
        }

        private static string[] Candidates()
        {
#if UNITY_EDITOR_WIN
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                "gh.exe",
                string.IsNullOrEmpty(programFiles) ? null
                    : Path.Combine(programFiles, "GitHub CLI", "gh.exe"),
                string.IsNullOrEmpty(localAppData) ? null
                    : Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe"),
            };
#else
            return new[]
            {
                "/opt/homebrew/bin/gh",
                "/usr/local/bin/gh",
                "/home/linuxbrew/.linuxbrew/bin/gh",
                "/usr/bin/gh",
                "gh",
            };
#endif
        }

        /// <summary>Forgets the located CLI, so a test or an install can be seen.</summary>
        internal static void Forget()
        {
            s_cli = null;
            s_searched = false;
        }
    }
}
