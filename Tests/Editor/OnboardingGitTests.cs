using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText.Editor;

namespace OneText.Tests
{
    /// <summary>
    /// The safety net under the TMP migration: the thing that decides whether
    /// the dialog says "this is revertible" or "you are about to lose work".
    ///
    /// Almost all of this is a process and a filesystem, which is the awkward
    /// kind of code to test, so it is shaped so that the part that decides
    /// anything is neither. Parsing what git printed and deciding what it means
    /// are pure functions of two strings, and those are tested here directly
    /// against output captured verbatim from a real git — including the
    /// escaping git does to a Korean filename, which is not a hypothetical in
    /// this project.
    ///
    /// The rest needs a real git and says so. Those tests build a repository in
    /// a temporary folder, delete it in TearDown, and stand down with
    /// Inconclusive on a machine with no git, in the same spirit as the
    /// golden-image suite standing down on a renderer it has no baselines for.
    /// </summary>
    public class OnboardingGitTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            // One folder deep and not two, so that TearDown deleting it leaves
            // nothing at all behind — not even the empty parent that a nested
            // scratch folder always survives as.
            _folder = Path.Combine(Path.GetTempPath(), "OneTextGitTests-" + Path.GetRandomFileName());
        }

        [TearDown]
        public void TearDown()
        {
            // A test that leaves a git repository lying around in the temp
            // folder is a test that will be found by somebody's disk cleaner
            // one day and blamed on something else, so this runs whether or not
            // the folder was ever created.
            if (!Directory.Exists(_folder)) return;
            try { Directory.Delete(_folder, true); }
            catch (IOException) { }
            catch (System.UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------- quoting

        [Test]
        public void A_Korean_Filename_Comes_Back_As_Its_Name_And_Not_As_Octal()
        {
            // This is what `git status --porcelain` prints for 한글.cs without
            // -z: quoted, and escaped one octal byte at a time. The dialog used
            // to show the user exactly these characters.
            Assert.AreEqual("한글.cs", OnboardingGit.Unquote("\"\\355\\225\\234\\352\\270\\200.cs\""));
        }

        [Test]
        public void Octal_Is_Gathered_Into_Bytes_Before_It_Is_Decoded()
        {
            // Three escapes, one syllable. Decoding each escape on its own
            // would produce three replacement characters, which is the failure
            // this test exists to pin: it passes with per-byte gathering and
            // fails with per-escape decoding, and both look plausible in code.
            Assert.AreEqual("한", OnboardingGit.Unquote("\"\\355\\225\\234\""));
            Assert.AreEqual("A한B", OnboardingGit.Unquote("\"A\\355\\225\\234B\""));
        }

        [Test]
        public void The_C_Escapes_Git_Emits_Are_All_Undone()
        {
            Assert.AreEqual("a\tb.cs", OnboardingGit.Unquote("\"a\\tb.cs\""));
            Assert.AreEqual("a\nb.cs", OnboardingGit.Unquote("\"a\\nb.cs\""));
            Assert.AreEqual("a\rb.cs", OnboardingGit.Unquote("\"a\\rb.cs\""));
            Assert.AreEqual("a\"b.cs", OnboardingGit.Unquote("\"a\\\"b.cs\""));
            Assert.AreEqual("a\\b.cs", OnboardingGit.Unquote("\"a\\\\b.cs\""));
        }

        [Test]
        public void An_Unquoted_Path_Is_Left_Exactly_Alone()
        {
            // Including one full of the characters that would have been escaped
            // had git decided to escape it. With core.quotepath=false git quotes
            // only for control characters, so a path can arrive with its Hangul
            // and its spaces intact and must survive untouched.
            Assert.AreEqual("Assets/한글 파일.cs", OnboardingGit.Unquote("Assets/한글 파일.cs"));
            Assert.AreEqual("Assets/A B.cs", OnboardingGit.Unquote("Assets/A B.cs"));
            Assert.AreEqual("", OnboardingGit.Unquote(""));
        }

        [Test]
        public void A_Quoted_Path_May_Still_Hold_Raw_Non_Ascii()
        {
            // core.quotepath=false quotes this one for its tab and leaves the
            // Hangul as bytes. Both halves have to come out, which is why the
            // literal runs are flushed through UTF-8 rather than appended as
            // chars.
            Assert.AreEqual("한글\t.cs", OnboardingGit.Unquote("\"한글\\t.cs\""));
        }

        // ------------------------------------------------------------- parsing

        [Test]
        public void Porcelain_Z_Is_Split_On_Nul_And_Never_Unquoted()
        {
            // Captured from `git status --porcelain -z -uall`. Note the Korean
            // name arriving raw: -z is the shape in which a path is the path.
            var changes = OnboardingGit.ParsePorcelain(
                " M unity/Assets/Modified.cs\0" +
                " M unity/Assets/한글 파일.cs\0" +
                "?? unity/Assets/Untracked.cs\0");

            Assert.AreEqual(3, changes.Count);
            Assert.AreEqual(" M", changes[0].Code);
            Assert.AreEqual("unity/Assets/Modified.cs", changes[0].Path);
            Assert.AreEqual("unity/Assets/한글 파일.cs", changes[1].Path);
            Assert.AreEqual("??", changes[2].Code);
        }

        [Test]
        public void A_Name_That_Starts_With_A_Quote_Survives_The_Nul_Shape()
        {
            // The reason the parser decides on NULs and not on quotes: in the
            // -z shape this file is not quoted, it is simply named that, and
            // unquoting it would be the octal bug pointed the other way.
            var changes = OnboardingGit.ParsePorcelain("?? unity/Assets/\"odd\".cs\0");

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("unity/Assets/\"odd\".cs", changes[0].Path);
        }

        [Test]
        public void A_Rename_Carries_Both_Names_In_Either_Shape()
        {
            // -z puts the old name in the following field, new name first.
            var z = OnboardingGit.ParsePorcelain(
                "R  unity/Assets/Renamed.cs\0unity/Assets/Clean.cs\0 M unity/Assets/Other.cs\0");

            Assert.AreEqual(2, z.Count, "the old name was counted as an entry of its own");
            Assert.AreEqual("unity/Assets/Renamed.cs", z[0].Path);
            Assert.AreEqual("unity/Assets/Clean.cs", z[0].From);
            Assert.AreEqual("unity/Assets/Other.cs", z[1].Path);

            // The newline shape reverses them and puts an arrow in between.
            var lines = OnboardingGit.ParsePorcelain(
                "R  \"unity/Assets/\\355\\225\\234.cs\" -> unity/Assets/New.cs\n");

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("unity/Assets/한.cs", lines[0].From);
            Assert.AreEqual("unity/Assets/New.cs", lines[0].Path);
        }

        [Test]
        public void An_Arrow_Inside_A_Filename_Is_Not_The_Rename_Arrow()
        {
            var changes = OnboardingGit.ParsePorcelain("R  \"a -> b.cs\" -> c.cs\n");

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("a -> b.cs", changes[0].From);
            Assert.AreEqual("c.cs", changes[0].Path);
        }

        [Test]
        public void Nothing_To_Report_Parses_As_Nothing()
        {
            Assert.AreEqual(0, OnboardingGit.ParsePorcelain("").Count);
            Assert.AreEqual(0, OnboardingGit.ParsePorcelain(null).Count);
            Assert.AreEqual(0, OnboardingGit.SplitPaths("").Count);
        }

        [Test]
        public void Ls_Files_Splits_On_Nul_Without_Losing_Anything_Odd()
        {
            var paths = OnboardingGit.SplitPaths(
                "unity/.gitignore\0unity/Assets/한글 파일.cs\0unity/Assets/quo\"te.cs\0");

            Assert.AreEqual(3, paths.Count);
            Assert.AreEqual("unity/Assets/한글 파일.cs", paths[1]);
            Assert.AreEqual("unity/Assets/quo\"te.cs", paths[2]);
        }

        // ---------------------------------------------------------- classifying

        /// <summary>
        /// The two strings a repository in a subdirectory produces, with one
        /// file in each of the states that matter.
        /// </summary>
        private const string Tracked =
            "unity/Assets/Clean.cs\0unity/Assets/Modified.cs\0unity/.gitignore\0";

        private const string Status =
            " M unity/Assets/Modified.cs\0?? unity/Assets/Untracked.cs\0";

        [Test]
        public void Committed_Modified_And_Untracked_Are_Three_Answers()
        {
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/Clean.cs", "Assets/Modified.cs", "Assets/Untracked.cs" },
                "unity/", Tracked, Status);

            Assert.IsTrue(report.Answered);
            CollectionAssert.AreEqual(new[] { "Assets/Clean.cs" }, Paths(report.Committed));
            CollectionAssert.AreEqual(new[] { "Assets/Modified.cs" }, Paths(report.Modified));
            CollectionAssert.AreEqual(new[] { "Assets/Untracked.cs" }, Paths(report.Untracked));
        }

        [Test]
        public void An_Ignored_File_Is_Not_Clean_Merely_Because_It_Is_Quiet()
        {
            // The whole defect in one assertion. An ignored, untracked file is
            // absent from `git status` in exactly the way a committed file with
            // nothing to report is absent from it, so it used to fall through to
            // "git reports every one of them committed, so this is revertible" —
            // said of a file git has never heard of and cannot put back.
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/ign/Ignored.cs" }, "unity/", Tracked, Status);

            Assert.AreEqual(0, report.Committed.Count, "an ignored file was called committed");
            Assert.AreEqual(1, report.Untracked.Count);
            Assert.AreEqual(GitFileState.Untracked, report.Untracked[0].State);
            Assert.AreEqual(1, report.AtRisk);
        }

        [Test]
        public void The_Repository_Root_Being_Above_The_Project_Is_Accounted_For()
        {
            // A Unity project checked in under a larger repository. git spells
            // everything from the repository root and the callers spell
            // everything from the project, and getting this wrong makes every
            // file look untracked — which warns, loudly and always, and trains
            // the user to click through the warning.
            var wrong = OnboardingGit.Classify(
                new List<string> { "Assets/Clean.cs" }, "", Tracked, Status);
            Assert.AreEqual(1, wrong.Untracked.Count, "the fixture no longer proves anything");

            var right = OnboardingGit.Classify(
                new List<string> { "Assets/Clean.cs" }, "unity/", Tracked, Status);
            Assert.AreEqual(1, right.Committed.Count);
        }

        [Test]
        public void A_Project_That_Is_The_Repository_Needs_No_Prefix()
        {
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/A.cs", "Assets/B.cs" }, "",
                "Assets/A.cs\0", " M Assets/A.cs\0");

            Assert.AreEqual(1, report.Modified.Count);
            Assert.AreEqual(1, report.Untracked.Count);
        }

        [Test]
        public void A_Korean_Path_Matches_Itself_All_The_Way_Through()
        {
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/한글 파일.cs" }, "unity/",
                "unity/Assets/한글 파일.cs\0", " M unity/Assets/한글 파일.cs\0");

            Assert.AreEqual(1, report.Modified.Count);
            Assert.AreEqual("M Assets/한글 파일.cs", report.Modified[0].ToString(),
                "the dialog would have shown the user something other than the filename");
        }

        [Test]
        public void The_Vanished_Half_Of_A_Rename_Is_Not_Called_Untracked()
        {
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/Clean.cs", "Assets/Renamed.cs" }, "unity/",
                "unity/Assets/Renamed.cs\0",
                "R  unity/Assets/Renamed.cs\0unity/Assets/Clean.cs\0");

            Assert.AreEqual(2, report.Modified.Count);
            Assert.AreEqual(0, report.Untracked.Count);
        }

        [Test]
        public void Windows_Separators_And_Leading_Dots_Are_The_Same_Path()
        {
            var report = OnboardingGit.Classify(
                new List<string> { @"Assets\Clean.cs", "./Assets/Modified.cs" },
                "unity/", Tracked, Status);

            Assert.AreEqual(1, report.Committed.Count);
            Assert.AreEqual(1, report.Modified.Count);
        }

        [Test]
        public void The_Same_File_Twice_Is_Counted_Once()
        {
            // The scene scan can reach the same prefab down two paths, and a
            // dialog that says "2 of the file(s)" about one file is a dialog
            // that is wrong about the only number in it.
            var report = OnboardingGit.Classify(
                new List<string> { "Assets/Modified.cs", "Assets/Modified.cs" },
                "unity/", Tracked, Status);

            Assert.AreEqual(1, report.AtRisk);
        }

        [Test]
        public void An_Unanswered_Report_Is_Not_An_Empty_One()
        {
            var silent = GitReport.Silent("git is not on the PATH");

            Assert.IsFalse(silent.Answered);
            Assert.AreEqual(0, silent.AtRisk, "which is exactly why Answered has to be read first");
        }

        // ------------------------------------------------------- the timeout

        [Test]
        public void A_Git_That_Does_Not_Answer_Is_Bounded_And_Not_Waited_On_Forever()
        {
            RequireGit();

            // `hash-object --stdin` reads standard input until it closes, and
            // nothing here is going to close it. Whether that hangs depends on
            // what the editor's own stdin is attached to — a terminal in CI, a
            // closed handle under a double-clicked Unity — so the assertion is
            // the one that holds either way and is the one that matters: the
            // call comes back. What it replaced could not have made that
            // promise, because ReadToEnd ran before the timeout did and has no
            // timeout of its own.
            var clock = Stopwatch.StartNew();
            var outcome = OnboardingGit.Execute("hash-object --stdin", 1500, Path.GetTempPath());
            clock.Stop();

            Assert.IsTrue(outcome.Started);
            Assert.Less(clock.ElapsedMilliseconds, 15000,
                "the wall clock was not bounded by the timeout");
            if (!outcome.Exited)
                Assert.AreEqual(0, outcome.Output.Length, "a killed git reported output anyway");
        }

        [Test]
        public void Run_Still_Returns_Null_For_Every_Way_Git_Can_Fail()
        {
            RequireGit();

            // The four call sites in the Hub depend on this staying a string or
            // null, and on null meaning "I do not know" rather than "clean".
            Assert.IsNull(OnboardingGit.Run("cat-file -p 0000000000000000000000000000000000000000"),
                "a failing git returned output");
            Assert.IsNotNull(OnboardingGit.Run("--version"));
        }

        // ------------------------------------------------------- with real git

        [Test]
        public void A_Real_Repository_Answers_All_Four_Ways()
        {
            var repo = BuildRepository();

            var report = OnboardingGit.Ask(new List<string>
            {
                "Assets/Clean.cs",      // committed
                "Assets/Modified.cs",   // committed, then edited
                "Assets/Untracked.cs",  // never added
                "Assets/ign/Build.cs",  // ignored, which is the one that lied
            }, repo);

            Assert.IsTrue(report.Answered, report.Trouble);
            CollectionAssert.AreEqual(new[] { "Assets/Clean.cs" }, Paths(report.Committed));
            CollectionAssert.AreEqual(new[] { "Assets/Modified.cs" }, Paths(report.Modified));
            CollectionAssert.AreEquivalent(
                new[] { "Assets/Untracked.cs", "Assets/ign/Build.cs" }, Paths(report.Untracked));
        }

        [Test]
        public void A_Korean_Filename_Round_Trips_Through_A_Real_Git()
        {
            // Its own test rather than a fifth path in the one above, because
            // the way this fails is specific and worth reading on its own: macOS
            // hands git the decomposed spelling of a Hangul filename and git
            // hands back the composed one, reconciled by core.precomposeunicode,
            // which git sets when it initialises a repository here. Where that
            // reconciliation does not happen the name matches nothing, every
            // Korean-named asset reads as untracked, and the dialog cries wolf
            // over the whole project.
            var repo = BuildRepository();

            var report = OnboardingGit.Ask(new List<string> { "Assets/한글 파일.cs" }, repo);

            Assert.IsTrue(report.Answered, report.Trouble);
            CollectionAssert.AreEqual(new[] { "Assets/한글 파일.cs" }, Paths(report.Committed),
                "the Korean filename git was given is not the one it gave back");
        }

        [Test]
        public void A_Real_Repository_Answers_About_Thousands_Of_Paths_At_Once()
        {
            // The other defect, end to end. These paths on one command line
            // come to a quarter of a megabyte, which is eight times what
            // Windows will accept and enough to fail on macOS too; git would
            // never have run, and the dialog would have announced that git
            // could not be asked at the exact moment it could have answered.
            var repo = BuildRepository();

            var paths = new List<string>();
            var arguments = new StringBuilder();
            for (int i = 0; i < 2000; i++)
            {
                string path = $"Assets/Many/AVeryOrdinarilyNamedPrefabNumber{i}.prefab";
                paths.Add(path);
                arguments.Append('"').Append(path).Append("\" ");
                File.WriteAllText(Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar)),
                    "x");
            }

            Assert.Greater(arguments.Length, 32767, "the fixture no longer proves anything");

            var report = OnboardingGit.Ask(paths, repo);
            Assert.IsTrue(report.Answered, report.Trouble);
            Assert.AreEqual(paths.Count, report.Untracked.Count);
        }

        [Test]
        public void A_Folder_With_No_Repository_Above_It_Says_So_In_Those_Words()
        {
            RequireGit();
            Directory.CreateDirectory(_folder);

            // Only meaningful if the temp folder is not itself inside somebody's
            // repository, which it is not on any machine this runs on, but the
            // test says what it assumes rather than asserting it blindly.
            var report = OnboardingGit.Ask(new List<string> { "Assets/A.cs" }, _folder);

            if (report.Answered)
                Assert.Ignore("the temporary folder is inside a git repository");

            Assert.AreEqual("this project is not inside a git repository", report.Trouble);
        }

        [Test]
        public void Dirty_Keeps_Its_Old_Shape_For_Its_Old_Callers()
        {
            var repo = BuildRepository();

            var dirty = OnboardingGit.Dirty(new List<string>
                { "Assets/Clean.cs", "Assets/Modified.cs", "Assets/Untracked.cs" }, repo);

            Assert.IsNotNull(dirty, "a repository that exists reported that it could not be asked");
            CollectionAssert.AreEquivalent(
                new[] { "M Assets/Modified.cs", "?? Assets/Untracked.cs" }, dirty);
        }

        // ------------------------------------------------------------ fixtures

        private static string[] Paths(List<GitEntry> entries)
        {
            var paths = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++) paths[i] = entries[i].Path;
            return paths;
        }

        private static void RequireGit()
        {
            if (!OnboardingGit.Execute("--version", 5000, Path.GetTempPath()).Ok)
                Assert.Ignore(
                    "No git on the PATH. These cover the half of OnboardingGit that is a " +
                    "process; the half that decides anything is covered above without one.");
        }

        /// <summary>
        /// A repository with one file in each state the dialog distinguishes,
        /// built in the folder TearDown deletes.
        /// </summary>
        private string BuildRepository()
        {
            RequireGit();

            string repo = Path.Combine(_folder, "project");
            Directory.CreateDirectory(Path.Combine(repo, "Assets", "ign"));
            Directory.CreateDirectory(Path.Combine(repo, "Assets", "Many"));

            void Write(string path, string text) =>
                File.WriteAllText(Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar)),
                    text, new UTF8Encoding(false));

            Write(".gitignore", "Assets/ign/\nAssets/Many/\n");
            Write("Assets/Clean.cs", "clean\n");
            Write("Assets/Modified.cs", "before\n");
            Write("Assets/한글 파일.cs", "korean\n");
            Write("Assets/ign/Build.cs", "generated\n");

            Git("init -q", repo);

            // Identity and signing are forced on the command rather than left to
            // whatever the machine's global config says, because a developer who
            // signs every commit should not have a test suite that waits on a
            // passphrase prompt.
            Git("-c user.name=OneText -c user.email=onetext@example.invalid " +
                "-c commit.gpgsign=false add -A", repo);
            Git("-c user.name=OneText -c user.email=onetext@example.invalid " +
                "-c commit.gpgsign=false commit -q -m fixture", repo);

            Write("Assets/Modified.cs", "after\n");
            Write("Assets/Untracked.cs", "never added\n");
            return repo;
        }

        private static void Git(string arguments, string root)
        {
            var outcome = OnboardingGit.Execute(arguments, 20000, root);
            if (!outcome.Ok)
                Assert.Ignore(
                    $"`git {arguments}` would not build the fixture: {outcome.Error.Trim()}");
        }
    }
}
