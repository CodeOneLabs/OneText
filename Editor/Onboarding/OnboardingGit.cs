using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// What git says about one of the files about to be overwritten. The three
    /// answers are deliberately three and not two: the middle one is the
    /// warning, and the last one is the warning nobody thinks to ask for.
    /// </summary>
    public enum GitFileState
    {
        /// <summary>Tracked, and what is on disk is what was committed. Revertible.</summary>
        Committed,

        /// <summary>Tracked, and holding work no commit has. Overwriting loses it.</summary>
        Modified,

        /// <summary>
        /// Not in the index at all — ignored by a <c>.gitignore</c>, or simply
        /// never added. There is no committed copy, so there is nothing to
        /// revert to; a repository being present does not help this file.
        /// </summary>
        Untracked,
    }

    /// <summary>One file we asked about, and the answer.</summary>
    public readonly struct GitEntry
    {
        /// <summary>Project-relative, spelled the way the caller spelled it.</summary>
        public readonly string Path;

        /// <summary>
        /// The two-column porcelain code (<c>" M"</c>, <c>"A "</c>, <c>"R "</c>),
        /// or <c>"??"</c> for a file git is not tracking.
        /// </summary>
        public readonly string Code;

        public readonly GitFileState State;

        public GitEntry(string path, string code, GitFileState state)
        {
            Path = path;
            Code = code;
            State = state;
        }

        /// <summary>The shape the dialog lists: <c>M Assets/Foo.cs</c>.</summary>
        public override string ToString() => $"{Code} {Path}".TrimStart();
    }

    /// <summary>
    /// Everything git said about one set of paths, or the reason it said
    /// nothing.
    ///
    /// <see cref="Answered"/> is the whole question. When it is false the lists
    /// below are empty because nobody looked, not because there was nothing to
    /// find, and treating those two as the same thing is the failure this class
    /// exists to make impossible to write by accident.
    /// </summary>
    public sealed class GitReport
    {
        /// <summary>Whether git was reached, understood, and believed.</summary>
        public bool Answered;

        /// <summary>
        /// Why it was not, as a clause the dialog drops into a sentence: "git
        /// is not on the PATH", "this project is not inside a git repository".
        /// Null once <see cref="Answered"/> is true.
        /// </summary>
        public string Trouble;

        /// <summary>Tracked files holding work no commit has.</summary>
        public readonly List<GitEntry> Modified = new List<GitEntry>();

        /// <summary>Files git is not tracking, which no repository can restore.</summary>
        public readonly List<GitEntry> Untracked = new List<GitEntry>();

        /// <summary>Files git can put back exactly as they are now.</summary>
        public readonly List<GitEntry> Committed = new List<GitEntry>();

        /// <summary>The count that decides whether the dialog is a warning.</summary>
        public int AtRisk => Modified.Count + Untracked.Count;

        public static GitReport Silent(string trouble) => new GitReport { Trouble = trouble };
    }

    /// <summary>One entry of <c>git status --porcelain</c>: code, path, and for
    /// a rename or a copy the path it used to have.</summary>
    public readonly struct GitChange
    {
        public readonly string Code;
        public readonly string Path;

        /// <summary>The old name of a rename or a copy, else null.</summary>
        public readonly string From;

        public GitChange(string code, string path, string from = null)
        {
            Code = code;
            Path = path;
            From = from;
        }

        public override string ToString() =>
            From == null ? $"{Code} {Path}" : $"{Code} {From} -> {Path}";
    }

    /// <summary>
    /// What one git invocation actually did, including the several ways it can
    /// do nothing. Separated from its output because "no output" is ambiguous
    /// and every ambiguity here ends in a wrong sentence in front of a
    /// destructive button.
    /// </summary>
    public sealed class GitOutcome
    {
        /// <summary>False when there was no git to start. Nothing else here means anything.</summary>
        public bool Started;

        /// <summary>False when the budget ran out first and the process was killed.</summary>
        public bool Exited;

        public int ExitCode;
        public string Output = "";
        public string Error = "";

        /// <summary>The only combination worth reading the output of.</summary>
        public bool Ok => Started && Exited && ExitCode == 0;
    }

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
    ///
    /// A fourth answer turned out to be hiding inside "clean". A file that is
    /// ignored, or that was never added, is absent from <c>git status</c> in
    /// exactly the way a file with nothing to report is absent from it. Those
    /// two look identical and mean opposite things — one is the safest state
    /// there is and the other is the least safe — so whether a file is tracked
    /// is now asked separately, and answered separately.
    /// </summary>
    public static class OnboardingGit
    {
        /// <summary>How long one git invocation may take.</summary>
        public const int TimeoutMs = 5000;

        /// <summary>
        /// How long everything one dialog asks may take, together. A dialog
        /// that appears late is a nuisance; an editor that never comes back is
        /// a bug report, so the budget is over the whole conversation and not
        /// over each sentence of it.
        /// </summary>
        public const int BudgetMs = 10000;

        /// <summary>
        /// git speaks bytes. On Windows the default here is the console code
        /// page, which cannot spell a Korean filename, and this project is
        /// Korean.
        /// </summary>
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // -------------------------------------------------------------- asking

        /// <summary>
        /// What git says about these project-relative paths — the spelling
        /// <c>Assets/Foo.cs</c>, as the Hub's ProjectPath produces it.
        /// </summary>
        /// <param name="root">
        /// Where to run git, defaulting to the Unity project. The tests pass a
        /// repository they built and delete themselves, because the alternative
        /// is asserting things about whichever repository the machine running
        /// them happens to be sitting in.
        /// </param>
        public static GitReport Ask(IList<string> projectPaths, string root = null)
        {
            if (projectPaths == null || projectPaths.Count == 0)
                return new GitReport { Answered = true };

            var clock = Stopwatch.StartNew();

            // Never below a second: handing a process a budget of nothing left
            // is a slow way of writing "unknown", and the last of three calls
            // deserves the same chance as the first.
            int Left() => (int)Math.Max(1000, BudgetMs - clock.ElapsedMilliseconds);

            // Where the repository root is matters, because a Unity project
            // checked in underneath a larger repository is an ordinary thing to
            // do, and the two halves of the comparison below are spelled from
            // different places: porcelain output is always relative to the
            // repository root, and the callers hand us paths relative to the
            // project. This one command answers both questions — the offset
            // between them, and whether there is a repository here at all.
            var where = Execute("rev-parse --show-prefix", Left(), root);
            if (!where.Started) return GitReport.Silent("git is not on the PATH");
            if (!where.Exited) return GitReport.Silent("git stopped answering and was ended");
            if (where.ExitCode != 0)
                return GitReport.Silent("this project is not inside a git repository");

            string prefix = Prefix(where.Output);

            // Neither of the two commands below will read pathspecs from stdin:
            // --pathspec-from-file exists on the commands that write, and these
            // are the two that only look. Putting the paths on the command line
            // instead is how this used to fail — a migration over a real
            // project passes thousands of them, the argument list overflows,
            // git never runs, and the dialog announces that git could not be
            // asked at the exact moment git could have answered.
            //
            // So neither command is told which files we care about. Both are
            // scoped with the single pathspec ".", which is the Unity project
            // as seen from the working directory and keeps a monorepo's other
            // millions of files out of the answer, and the paths we asked about
            // are picked out of the reply here.
            const string quiet = "-c core.quotepath=false ";

            var tracked = Execute(quiet + "ls-files -z --full-name --cached -- .", Left(), root);
            if (!tracked.Ok) return GitReport.Silent(Because(tracked));

            var status = Execute(quiet + "status --porcelain -z --untracked-files=all -- .",
                Left(), root);
            if (!status.Ok) return GitReport.Silent(Because(status));

            return Classify(projectPaths, prefix, tracked.Output, status.Output);
        }

        /// <summary>
        /// Working-tree status for these project-relative paths, or null when
        /// git cannot say.
        ///
        /// Kept in its original shape — one line per file that is not safely
        /// committed — for anything that only wants the count. <see cref="Ask"/>
        /// is where the distinction between "you will lose work" and "there was
        /// never anything to lose it from" survives.
        /// </summary>
        public static List<string> Dirty(IList<string> projectPaths, string root = null)
        {
            var report = Ask(projectPaths, root);
            if (!report.Answered) return null;

            var dirty = new List<string>(report.AtRisk);
            foreach (var entry in report.Modified) dirty.Add(entry.ToString());
            foreach (var entry in report.Untracked) dirty.Add(entry.ToString());
            return dirty;
        }

        /// <summary>
        /// Decides what git is saying about each path we asked about, given
        /// what the two commands printed.
        ///
        /// Split out and left public because it is the half that can be tested
        /// without a repository on disk: everything above it is a process, and
        /// everything below it is a string.
        /// </summary>
        /// <param name="projectPaths">Paths as the caller spells them, relative to the project.</param>
        /// <param name="prefix">Where the project sits inside the repository, "" or "unity/".</param>
        /// <param name="trackedPaths">Output of <c>git ls-files -z --full-name --cached</c>.</param>
        /// <param name="statusOutput">Output of <c>git status --porcelain -z</c>.</param>
        public static GitReport Classify(IList<string> projectPaths, string prefix,
            string trackedPaths, string statusOutput)
        {
            var report = new GitReport { Answered = true };
            if (projectPaths == null) return report;

            // Ordinal, because the index is: git records the case a file was
            // added with even on a filesystem that does not care. A path whose
            // case has drifted therefore reads as untracked, which over-warns,
            // and over-warning is the correct direction to be wrong in here.
            var tracked = new HashSet<string>(SplitPaths(trackedPaths), StringComparer.Ordinal);

            var listed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var change in ParsePorcelain(statusOutput))
            {
                listed[change.Path] = change.Code;

                // A rename is one code over two paths. The vanished one is the
                // one a caller is least likely to ask about and most likely to
                // be surprised by, so it gets the same answer rather than
                // falling through to "git is not tracking this".
                if (change.From != null && !listed.ContainsKey(change.From))
                    listed[change.From] = change.Code;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string given in projectPaths)
            {
                if (string.IsNullOrEmpty(given)) continue;

                string project = Normalize(given);
                if (!seen.Add(project)) continue;
                string repo = prefix + project;

                if (listed.TryGetValue(repo, out string code) && code != "??")
                    report.Modified.Add(new GitEntry(given, code, GitFileState.Modified));
                else if (tracked.Contains(repo))
                    report.Committed.Add(new GitEntry(given, "  ", GitFileState.Committed));
                else
                    // Either git listed it as "??" or git never mentioned it,
                    // and those are the same file: one the index has never
                    // heard of. The second case is the one that used to read as
                    // clean, because an ignored file is quiet in precisely the
                    // way a committed one is.
                    report.Untracked.Add(new GitEntry(given, "??", GitFileState.Untracked));
            }

            return report;
        }

        // --------------------------------------------------------- the dialog

        /// <summary>
        /// The confirmation both flows share: what is about to be overwritten,
        /// and whether git says it is recoverable.
        /// </summary>
        public static bool ConfirmOverwrite(string title, string what, IList<string> projectPaths,
            string yes)
        {
            var report = Ask(projectPaths);

            if (!report.Answered)
            {
                return HubUI.Confirm(title,
                    $"{what}\n\n" +
                    "git could not be asked whether they hold uncommitted work — " +
                    report.Trouble + ". This edit cannot be undone from inside Unity.",
                    yes + " anyway");
            }

            if (report.AtRisk == 0)
            {
                return HubUI.Confirm(title,
                    $"{what} git reports every one of them committed, so this is revertible.",
                    yes);
            }

            if (report.Modified.Count == 0)
            {
                // Nothing is modified and yet nothing is safe, which is the
                // state that used to be reported as "every one of them
                // committed". A repository exists; it has simply never been
                // shown these files.
                return HubUI.Confirm("Not in version control",
                    $"git is not tracking {report.Untracked.Count} of the file(s) about to be " +
                    "changed — ignored, or never added:" + Listing(report.Untracked) +
                    "\n\nThere is no committed copy to put back, and Unity cannot undo this.",
                    yes + " anyway");
            }

            var body = new StringBuilder();
            body.Append($"{report.Modified.Count} of the file(s) about to be changed hold ")
                .Append("uncommitted work:")
                .Append(Listing(report.Modified))
                .Append("\n\nOverwriting loses it, and Unity cannot undo that. Commit or stash ")
                .Append("first.");

            if (report.Untracked.Count > 0)
            {
                body.Append($"\n\ngit is not tracking a further {report.Untracked.Count} of them ")
                    .Append("at all — ignored, or never added — so those have no committed copy ")
                    .Append("to go back to either.");
            }

            return HubUI.Confirm("Uncommitted changes", body.ToString(), yes + " anyway");
        }

        /// <summary>The first ten, and an honest count of the rest.</summary>
        private static string Listing(List<GitEntry> entries)
        {
            var listing = new StringBuilder();
            for (int i = 0; i < entries.Count && i < 10; i++)
                listing.Append('\n').Append(entries[i]);
            if (entries.Count > 10) listing.Append($"\n… and {entries.Count - 10} more");
            return listing.ToString();
        }

        // --------------------------------------------------------- the parsing

        /// <summary>
        /// Reads <c>git status --porcelain</c>, in either of the two shapes it
        /// comes in.
        ///
        /// NUL-separated (<c>-z</c>) is the shape this file asks for, because it
        /// is the only one in which a path is the path. The newline shape wraps
        /// anything unusual in quotes and escapes every byte outside ASCII to
        /// octal, so a Korean filename arrives as <c>\355\225\234</c> and, until
        /// this was written, went into the dialog that way.
        ///
        /// Both are handled, and the presence of a NUL is what decides which —
        /// not the presence of a quote, because in the <c>-z</c> shape a leading
        /// quote is a file whose name begins with one, and unquoting it would
        /// be this same bug pointed the other way.
        /// </summary>
        public static List<GitChange> ParsePorcelain(string output)
        {
            var changes = new List<GitChange>();
            if (string.IsNullOrEmpty(output)) return changes;

            bool nul = output.IndexOf('\0') >= 0;
            string[] fields = output.Split(nul ? '\0' : '\n');

            for (int i = 0; i < fields.Length; i++)
            {
                string field = nul ? fields[i] : fields[i].TrimEnd('\r');

                // "XY p" is the shortest entry that can exist; anything shorter
                // is the empty tail every terminated list has.
                if (field.Length < 4) continue;

                string code = field.Substring(0, 2);
                string path = field.Substring(3);
                string from = null;

                // A rename or a copy carries the name it had as well, and the
                // two shapes disagree about where they put it: -z gives it the
                // next field, the newline shape puts it on the same line behind
                // an arrow. The order is reversed between them too, which is
                // the kind of detail that is only ever found by looking.
                if (code[0] == 'R' || code[0] == 'C' || code[1] == 'R' || code[1] == 'C')
                {
                    if (nul)
                    {
                        if (i + 1 < fields.Length) from = fields[++i];
                    }
                    else
                    {
                        int arrow = Arrow(path);
                        if (arrow >= 0)
                        {
                            from = path.Substring(0, arrow);
                            path = path.Substring(arrow + 4);
                        }
                    }
                }

                if (!nul)
                {
                    path = Unquote(path);
                    if (from != null) from = Unquote(from);
                }

                changes.Add(new GitChange(code, path, from));
            }

            return changes;
        }

        /// <summary>
        /// Splits a NUL-terminated list of paths, as <c>-z</c> produces it, and
        /// falls back to lines for output that was not asked for that way.
        /// </summary>
        public static List<string> SplitPaths(string output)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(output)) return paths;

            bool nul = output.IndexOf('\0') >= 0;
            foreach (string field in output.Split(nul ? '\0' : '\n'))
            {
                string path = nul ? field : field.TrimEnd('\r');
                if (path.Length == 0) continue;
                paths.Add(nul ? path : Unquote(path));
            }
            return paths;
        }

        /// <summary>
        /// Undoes git's C-style quoting of an unusual path, and returns
        /// anything that was not quoted untouched.
        ///
        /// git escapes per byte and not per character, so the octal has to be
        /// gathered up and decoded as UTF-8 at the end. Decoding each escape on
        /// its own would turn one Hangul syllable into three replacement
        /// characters, which is a more thorough way of showing the user the
        /// wrong filename than showing them the octal was.
        /// </summary>
        public static string Unquote(string field)
        {
            if (field == null) return null;
            if (field.Length < 2 || field[0] != '"' || field[field.Length - 1] != '"') return field;

            var bytes = new List<byte>(field.Length);
            var literal = new StringBuilder();

            // Anything not escaped may still be non-ASCII — core.quotepath=false
            // quotes a name for its control characters and leaves its Hangul
            // alone — so literal runs are flushed whole, which is also the only
            // way a surrogate pair survives the trip through bytes.
            void Flush()
            {
                if (literal.Length == 0) return;
                bytes.AddRange(Utf8.GetBytes(literal.ToString()));
                literal.Length = 0;
            }

            for (int i = 1; i < field.Length - 1; i++)
            {
                char c = field[i];
                if (c != '\\' || i + 1 >= field.Length - 1)
                {
                    literal.Append(c);
                    continue;
                }

                char escape = field[++i];
                if (escape >= '0' && escape <= '7')
                {
                    int value = escape - '0';
                    for (int digits = 1;
                         digits < 3 && i + 1 < field.Length - 1 &&
                         field[i + 1] >= '0' && field[i + 1] <= '7';
                         digits++)
                        value = value * 8 + (field[++i] - '0');

                    Flush();
                    bytes.Add((byte)value);
                    continue;
                }

                switch (escape)
                {
                    case 'a': literal.Append('\a'); break;
                    case 'b': literal.Append('\b'); break;
                    case 't': literal.Append('\t'); break;
                    case 'n': literal.Append('\n'); break;
                    case 'v': literal.Append('\v'); break;
                    case 'f': literal.Append('\f'); break;
                    case 'r': literal.Append('\r'); break;
                    case '"': literal.Append('"'); break;
                    case '\\': literal.Append('\\'); break;

                    // Not an escape git emits. Keeping both characters is the
                    // only choice that cannot lose part of a name.
                    default: literal.Append('\\').Append(escape); break;
                }
            }

            Flush();
            return Utf8.GetString(bytes.ToArray());
        }

        /// <summary>
        /// Where the arrow of a rename is, skipping one that is inside the
        /// quoted name rather than between the two of them.
        /// </summary>
        private static int Arrow(string text)
        {
            int from = 0;
            if (text.Length > 0 && text[0] == '"')
            {
                from = 1;
                while (from < text.Length && text[from] != '"')
                    from += text[from] == '\\' ? 2 : 1;
                if (from >= text.Length) return -1;
            }
            return text.IndexOf(" -> ", from, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>git rev-parse --show-prefix</c> prints where the working directory
        /// sits inside the repository, and prints nothing at all when they are
        /// the same place — which is the common case and the reason this was
        /// never noticed.
        /// </summary>
        private static string Prefix(string output)
        {
            // TrimEnd of the newline only: a directory whose name ends in a
            // space is legal, and Trim() would quietly rename it.
            string prefix = Unquote((output ?? "").TrimEnd('\n', '\r'));
            if (prefix.Length > 0 && !prefix.EndsWith("/", StringComparison.Ordinal)) prefix += "/";
            return prefix;
        }

        /// <summary>
        /// The caller's spelling, reduced to git's. Unity hands out forward
        /// slashes; something that went through Path.Combine on Windows may not
        /// have.
        /// </summary>
        private static string Normalize(string projectPath)
        {
            string path = projectPath.Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
            return path;
        }

        /// <summary>Why git did not answer, as a clause that reads mid-sentence.</summary>
        private static string Because(GitOutcome outcome)
        {
            if (!outcome.Started) return "git is not on the PATH";
            if (!outcome.Exited) return "git stopped answering and was ended";

            string trouble = FirstLine(outcome.Error);
            return trouble.Length > 0
                ? $"git said \"{trouble}\""
                : $"git exited with code {outcome.ExitCode}";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int end = text.IndexOf('\n');
            return (end < 0 ? text : text.Substring(0, end)).Trim();
        }

        // --------------------------------------------------------- the process

        /// <summary>
        /// Runs git, and returns null for every way it can fail to answer:
        /// absent, not a repository, or too slow. "I do not know" and "clean"
        /// are different answers and the caller says different things about them.
        /// </summary>
        public static string Run(string arguments)
        {
            var outcome = Execute(arguments, TimeoutMs);
            return outcome.Ok ? outcome.Output : null;
        }

        /// <summary>
        /// Runs git under a wall-clock bound that is actually a bound.
        ///
        /// What it replaced read both pipes to the end and then waited five
        /// seconds for a process that had, by definition, already finished.
        /// ReadToEnd has no timeout: a git waiting on <c>index.lock</c>, or on a
        /// filesystem that has stopped answering, blocked the editor's main
        /// thread with no dialog and no way out but Force Quit. And because
        /// stdout was drained to the end before stderr was touched at all, a git
        /// with enough to say on stderr to fill that pipe deadlocked against a
        /// reader that was never going to get to it.
        ///
        /// So both pipes are drained by somebody other than the thread doing the
        /// waiting, and the process that misses its budget is killed rather than
        /// left running with its handles held.
        /// </summary>
        public static GitOutcome Execute(string arguments, int timeoutMs, string root = null)
        {
            var outcome = new GitOutcome();
            Process process = null;

            try
            {
                var info = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = root ?? ProjectRoot(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Utf8,
                    StandardErrorEncoding = Utf8,
                };

                // git status would normally take index.lock to write back a
                // refreshed index, which is a courtesy we cannot afford: if
                // another git already holds it we wait, and we are waiting on
                // the editor's main thread. A slightly stale index is a fine
                // price for a dialog that opens.
                info.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";

                // Nothing asked for here touches the network, but a credential
                // helper that decides to prompt would prompt into a pipe nobody
                // is typing into, and would wait for the answer until the heat
                // death of the editor.
                info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

                process = Process.Start(info);
                if (process == null) return outcome;
                outcome.Started = true;

                string output = null, error = null;
                var readOut = Reader(process.StandardOutput, text => output = text);
                var readErr = Reader(process.StandardError, text => error = text);

                bool exited = process.WaitForExit(timeoutMs);
                if (!exited) Kill(process);

                // Killing closes the pipes, which is what ends the readers; the
                // grace is so that neither of them is still inside a stream
                // this method is about to dispose. A reader that is somehow
                // still parked — a grandchild holding the pipe open past its
                // parent — leaves us with half an answer, and half an answer
                // about whether work is recoverable is no answer at all. Both
                // joins run before the verdict, hence & rather than &&.
                bool drained = readOut.Join(1000) & readErr.Join(1000);
                if (!exited || !drained) return outcome;

                outcome.Exited = true;
                outcome.ExitCode = process.ExitCode;
                outcome.Output = output ?? "";
                outcome.Error = error ?? "";
                return outcome;
            }
            catch (Exception)
            {
                // No git on the path arrives here as a Win32Exception, and
                // everything else that can go wrong arrives here as itself.
                // Either way the honest answer is that we do not know.
                return outcome;
            }
            finally
            {
                try { process?.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// A thread whose whole job is to empty one pipe. Background, so that a
        /// reader still parked on a stream that never closes cannot be the
        /// reason Unity refuses to quit.
        /// </summary>
        private static Thread Reader(StreamReader stream, Action<string> keep)
        {
            var thread = new Thread(() =>
            {
                try { keep(stream.ReadToEnd()); }
                catch (Exception) { /* the pipe went away; the timeout is the answer */ }
            })
            {
                IsBackground = true,
                Name = "OneText git reader",
            };
            thread.Start();
            return thread;
        }

        /// <summary>
        /// Ends a git that has stopped answering, and what it started with it.
        ///
        /// The editor's .NET has no Kill(entireProcessTree), and git does much
        /// of its work by running more programs, so the platform's own killer is
        /// asked first and the parent is killed after. Both are best-effort:
        /// the alternative to a kill that fails is one leaked process, and the
        /// alternative to trying is a guaranteed one.
        /// </summary>
        private static void Kill(Process process)
        {
            int id;
            try { id = process.Id; }
            catch (Exception) { return; }

            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            Sweep(windows ? "taskkill" : "pkill", windows ? $"/PID {id} /T /F" : $"-KILL -P {id}");

            try { if (!process.HasExited) process.Kill(); }
            catch (Exception) { }
        }

        private static void Sweep(string file, string arguments)
        {
            try
            {
                // Deliberately not redirected. Nothing here would read those
                // pipes, and an unread pipe is the deadlock this whole method
                // exists downstream of.
                var killer = Process.Start(new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (killer == null) return;

                using (killer)
                {
                    if (!killer.WaitForExit(2000))
                    {
                        try { killer.Kill(); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception)
            {
                // No pkill, no taskkill, no permission. The parent still gets
                // killed by the caller, which is the part that matters.
            }
        }

        /// <summary>
        /// The Unity project root, which is where git is run from — and which is
        /// not necessarily the repository root, hence the prefix that
        /// <see cref="Ask"/> goes and asks for.
        /// </summary>
        public static string ProjectRoot() =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
    }
}
