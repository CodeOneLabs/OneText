using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace OneText.Editor
{
    /// <summary>One line the rewriter would change, as it is and as it becomes.</summary>
    public readonly struct TmpRewriteChange
    {
        /// <summary>1-based line number in the <em>original</em> file.</summary>
        public readonly int Line;

        /// <summary>The line as it stands.</summary>
        public readonly string Original;

        /// <summary>
        /// What it becomes. May hold more than one line (a <c>using</c> that
        /// turns into two), and is <c>null</c> when the line goes away.
        /// </summary>
        public readonly string Replacement;

        public TmpRewriteChange(int line, string original, string replacement)
        {
            Line = line;
            Original = original;
            Replacement = replacement;
        }

        public override string ToString() =>
            Replacement == null ? $"{Line}: −{Original}" : $"{Line}: {Original} → {Replacement}";
    }

    /// <summary>
    /// What a leftover TextMesh Pro name actually is, which decides whether it
    /// is in the way.
    /// </summary>
    public enum TmpResidualKind
    {
        /// <summary>
        /// A bare type name — <c>TMP_MeshInfo</c>, <c>TMP_SpriteAsset</c> — which
        /// only compiles for as long as <c>using TMPro;</c> is above it. These
        /// are the ones that decide a file cannot be rewritten.
        /// </summary>
        Reference,

        /// <summary>
        /// A name written out in full, <c>TMPro.TMP_Settings</c>, or the
        /// namespace itself. Nothing about the <c>using</c> line changes what it
        /// resolves to, so it is work to do but not work in the way.
        /// </summary>
        Qualified,

        /// <summary>
        /// Not a type at all: something the project named after one. DOTween Pro
        /// has an enum member called <c>TextMeshProUGUI</c>, and there is
        /// nothing to migrate about it — renaming it would break every
        /// <c>TargetType.TextMeshProUGUI</c> in the package, and renaming a
        /// serialized field would quietly empty it in every scene.
        /// </summary>
        Declaration,
    }

    /// <summary>
    /// A TextMesh Pro name still standing after the rewrite: something with no
    /// OneText counterpart, or one the rewriter refuses to guess at.
    /// </summary>
    public readonly struct TmpResidual
    {
        /// <summary>The identifier, e.g. <c>TMP_SpriteAsset</c>.</summary>
        public readonly string Name;

        /// <summary>1-based line number in the <em>rewritten</em> text.</summary>
        public readonly int Line;

        /// <summary>Whether it is a type reference, a qualified one, or a name.</summary>
        public readonly TmpResidualKind Kind;

        public TmpResidual(string name, int line) : this(name, line, TmpResidualKind.Reference)
        {
        }

        public TmpResidual(string name, int line, TmpResidualKind kind)
        {
            Name = name;
            Line = line;
            Kind = kind;
        }

        public override string ToString() => $"{Name} (line {Line})";
    }

    /// <summary>What one file would become.</summary>
    public sealed class TmpRewriteResult
    {
        public TmpRewriteResult(string text, List<TmpRewriteChange> changes,
            List<TmpResidual> residuals)
            : this(text, changes, residuals, null, null, null)
        {
        }

        public TmpRewriteResult(string text, List<TmpRewriteChange> changes,
            List<TmpResidual> residuals, string blocker, List<string> blockingNames,
            List<string> requiredAssemblies)
        {
            Text = text;
            Changes = changes;
            Residuals = residuals;
            Blocker = blocker;
            BlockingNames = blockingNames ?? new List<string>();
            RequiredAssemblies = requiredAssemblies ?? new List<string>();
        }

        /// <summary>The whole file, rewritten.</summary>
        public string Text { get; }

        /// <summary>Every line that differs, in file order.</summary>
        public List<TmpRewriteChange> Changes { get; }

        /// <summary>TextMesh Pro names the rewrite could not carry over.</summary>
        public List<TmpResidual> Residuals { get; }

        public bool Changed => Changes.Count > 0;

        /// <summary>
        /// Whether the rewrite is one this file can actually survive.
        ///
        /// A file that loses <c>using TMPro;</c> while still naming
        /// <c>TMP_MeshInfo</c> does not half-migrate; it stops compiling, and
        /// no amount of the rest of the rewrite being right makes up for it.
        /// When this is false <see cref="Text"/> is the original, byte for byte,
        /// <see cref="Changed"/> is false, and <see cref="Blocker"/> says why —
        /// the file is manual work, and saying so before the button is pressed
        /// is the entire point.
        /// </summary>
        public bool Viable => Blocker == null;

        /// <summary>One sentence naming what stands in the way, or <c>null</c>.</summary>
        public string Blocker { get; }

        /// <summary>The names in the way, so a report can list them.</summary>
        public List<string> BlockingNames { get; }

        /// <summary>
        /// The OneText assemblies the rewritten file would name. Empty when the
        /// rewrite is not viable, since nothing is going to be applied.
        /// </summary>
        public List<string> RequiredAssemblies { get; }
    }

    /// <summary>One file the scan turned up.</summary>
    public sealed class TmpScriptFinding
    {
        public TmpScriptFinding(string path, TmpRewriteResult result)
            : this(path, result, null, null, false)
        {
        }

        public TmpScriptFinding(string path, TmpRewriteResult result,
            TmpAssemblyDefinition assembly, List<string> missingReferences, bool thirdParty)
        {
            Path = path;
            Result = result;
            Assembly = assembly;
            MissingReferences = missingReferences ?? new List<string>();
            IsLikelyThirdParty = thirdParty;
        }

        public string Path { get; }

        public TmpRewriteResult Result { get; }

        /// <summary>
        /// The assembly definition governing this file, or <c>null</c> for the
        /// predefined assembly — which needs nothing, since OneText is
        /// auto-referenced and that is all auto-referencing covers.
        /// </summary>
        public TmpAssemblyDefinition Assembly { get; }

        /// <summary>
        /// OneText assemblies <see cref="Assembly"/> would have to gain before
        /// the rewritten file compiles. Applying the rewrite without this is
        /// twenty-one CS0246s in somebody else's folder.
        /// </summary>
        public List<string> MissingReferences { get; }

        public bool NeedsAssemblyPatch => MissingReferences.Count > 0;

        /// <summary>
        /// A hint that this file came with an imported package rather than being
        /// written here. Nothing is decided by it — the Hub leaves these
        /// unticked, because rewriting a vendor's source is a decision a person
        /// makes once and then owns for ever.
        /// </summary>
        public bool IsLikelyThirdParty { get; }
    }

    /// <summary>
    /// Files that have to be applied together or not at all.
    ///
    /// A file that declares <c>public static void DOText(this TMP_Text …)</c>
    /// and a file that calls it are one unit: convert the caller alone and the
    /// argument no longer matches, convert the extension alone and the call no
    /// longer resolves, and neither can be repaired by reverting the caller,
    /// because it is the provider that moved. Per-file checkboxes cannot
    /// express that, so the scan says it instead.
    /// </summary>
    public sealed class TmpFileGroup
    {
        public TmpFileGroup(List<string> paths, string reason)
        {
            Paths = paths;
            Reason = reason;
        }

        /// <summary>Every file in the unit, in path order.</summary>
        public List<string> Paths { get; }

        /// <summary>The name that ties them together, for the sentence the Hub shows.</summary>
        public string Reason { get; }

        public override string ToString() => $"{Paths.Count} files ({Reason})";
    }

    /// <summary>Everything one scan of a project has to say.</summary>
    public sealed class TmpScanReport
    {
        public TmpScanReport(List<TmpScriptFinding> files, List<TmpFileGroup> groups)
        {
            Files = files;
            Groups = groups;
        }

        public List<TmpScriptFinding> Files { get; }

        /// <summary>
        /// The groupings that make a partial selection unsafe. Only groups of
        /// two or more files are here; a file that owes nothing to another is
        /// its own business.
        /// </summary>
        public List<TmpFileGroup> Groups { get; }
    }

    /// <summary>
    /// The mechanical half of moving a project off TextMesh Pro: the type names
    /// and the <c>using</c>, in every script, done the same way every time.
    ///
    /// This is text processing and nothing else. It never loads TMP, never
    /// references a TMPro type, and works in a project where the package was
    /// removed an hour ago and nothing compiles — which is exactly the project
    /// that needs it, because a codebase mid-migration cannot run a Roslyn
    /// rename over itself.
    ///
    /// Conservative on purpose, in two directions. It rewrites the handful of
    /// type names that have a counterpart and one <c>using</c>, and stops: a
    /// member it does not understand is left
    /// for the compiler to point at, which is a better guide than a rewriter
    /// guessing. And it will not touch a character inside a string literal, a
    /// character literal or a comment, which is why the scanner below is a
    /// lexer rather than a regular expression. A project's own dialogue lines
    /// and its build scripts are full of the word TextMeshPro; a regex would
    /// silently edit a shipped string, and nobody would notice until a
    /// localization diff.
    ///
    /// Whatever it cannot handle it still reports. A file that mentions
    /// <c>TMP_SpriteAsset</c> comes back with that name and a line number, so the
    /// person migrating is told where the manual work is before they press the
    /// button rather than by a wall of compile errors after.
    ///
    /// And where the report and the rewrite contradict each other, the report
    /// wins. A file whose <c>using TMPro;</c> would go while <c>TMP_MeshInfo</c>
    /// stayed is not a file that half migrated; it is a file that does not
    /// compile, and it comes back untouched with a sentence saying which names
    /// are in the way. Under-doing this is recoverable and over-doing it is not,
    /// which is the same reason a name is only substituted where the tokens
    /// around it say a type can stand.
    /// </summary>
    public static class TmpScriptRewriter
    {
        /// <summary>
        /// The whole of the type map. Within a dotted family, longest name
        /// first, so <c>TextMeshProUGUI</c> is never read as
        /// <c>TextMeshPro</c> with a suffix; entries that are not prefixes of
        /// each other owe each other nothing, and the whole-chain match below
        /// keeps even a prefix from stealing a longer name's member access.
        /// The qualified forms are the same map under <c>TMPro.</c>, which is
        /// how a file that never had a <c>using</c> is handled.
        /// </summary>
        private static readonly (string From, string To)[] Types =
        {
            ("TMPro.TextMeshProUGUI", "OneText.UGUI.OneTextLabel"),
            ("TMPro.TMP_InputField", "OneText.UGUI.OneTextInputField"),
            ("TMPro.TextMeshPro", "OneText.OneTextMesh"),
            // The per-character surface, before TMP_Text: every one of these
            // begins with it, and read in the other order "TMP_TextInfo" would
            // come back as "OneTextLabelInfo".
            ("TMPro.TMP_TextInfo", "OneText.UGUI.OneTextTextInfo"),
            ("TMPro.TMP_CharacterInfo", "OneText.UGUI.OneTextCharacterInfo"),
            ("TMPro.TMP_LineInfo", "OneText.UGUI.OneTextLineInfo"),
            ("TMPro.TMP_WordInfo", "OneText.UGUI.OneTextWordInfo"),
            ("TMPro.TMP_LinkInfo", "OneText.UGUI.OneTextLinkInfo"),
            ("TMPro.TMP_MeshInfo", "OneText.UGUI.OneTextMeshInfo"),
            ("TMPro.TMP_VertexDataUpdateFlags", "OneText.UGUI.OneTextVertexDataUpdateFlags"),
            ("TMPro.TMPro_EventManager", "OneText.UGUI.OneTextEvents"),
            // Not a TextMesh Pro name, and here anyway: converting the labels a
            // dropdown points at forces the dropdown itself to be swapped, and a
            // field still typed UnityEngine.UI.Dropdown would then be holding
            // something it cannot hold. Only the qualified form is mapped —
            // "Dropdown" on its own is a name a project may well have given
            // something of its own, and the cost of being wrong about it is
            // someone else's class quietly renamed.
            ("UnityEngine.UI.Dropdown", "OneText.UGUI.OneTextDropdown"),
            // TextMesh Pro's dropdown, for the same reason and with no
            // ambiguity to be careful of: the name is TMP's own, so unlike the
            // bare "Dropdown" below there is no project that might mean
            // something else by it.
            ("TMPro.TMP_Dropdown", "OneText.UGUI.OneTextDropdown"),
            ("TMPro.TMP_Text", "OneText.UGUI.OneTextLabel"),
            // The two enums the parity aliases take. Only the qualified forms
            // are here: OneText declares these under the names TMP used, so an
            // unqualified TextAlignmentOptions is already correct once the
            // TMPro using has become a OneText.UGUI one, and rewriting it would
            // be changing a name to itself.
            ("TMPro.TextAlignmentOptions", "OneText.UGUI.TextAlignmentOptions"),
            ("TMPro.TextWrappingModes", "OneText.UGUI.TextWrappingModes"),
            ("TMPro.TextOverflowModes", "OneText.UGUI.TextOverflowModes"),
            ("TextMeshProUGUI", "OneTextLabel"),
            ("TMP_InputField", "OneTextInputField"),
            ("TMP_Dropdown", "OneTextDropdown"),
            ("TextMeshPro", "OneTextMesh"),
            ("TMP_TextInfo", "OneTextTextInfo"),
            ("TMP_CharacterInfo", "OneTextCharacterInfo"),
            ("TMP_LineInfo", "OneTextLineInfo"),
            ("TMP_WordInfo", "OneTextWordInfo"),
            ("TMP_LinkInfo", "OneTextLinkInfo"),
            ("TMP_MeshInfo", "OneTextMeshInfo"),
            ("TMP_VertexDataUpdateFlags", "OneTextVertexDataUpdateFlags"),
            ("TMPro_EventManager", "OneTextEvents"),
            ("TMP_Text", "OneTextLabel"),
            // Guarded: only substituted in a file that imports UnityEngine.UI —
            // see UsesUGui. "Dropdown" is a name a project may have given
            // something of its own, and the one place it certainly means uGUI's
            // is a file that asked for uGUI's namespace.
            ("Dropdown", "OneTextDropdown"),
        };

        /// <summary>Short names that only mean what they say inside a uGUI-importing file.</summary>
        private static readonly HashSet<string> UGuiOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "Dropdown",
        };

        /// <summary>The namespace the aliases live in.</summary>
        private const string UGuiNamespace = "OneText.UGUI";

        /// <summary>Where <c>OneTextMesh</c> lives, needed only by world text.</summary>
        private const string CoreNamespace = "OneText";

        // ================================================================ api

        /// <summary>
        /// Rewrites one file's text. Pure: same input, same output, and no
        /// second pass changes anything a first pass did not.
        /// </summary>
        public static TmpRewriteResult Rewrite(string source)
        {
            var changes = new List<TmpRewriteChange>();
            if (string.IsNullOrEmpty(source))
                return new TmpRewriteResult(source ?? string.Empty, changes, new List<TmpResidual>());

            bool[] code = CodeMask(source);
            var lines = new List<Line>();
            SplitLines(source, lines);
            var enums = EnumBodies(source, code);

            // Types first: whether the file needs `using OneText;` is decided by
            // whether anything in it turned into a OneTextMesh.
            var edits = TypeEdits(source, code, enums, out bool needsCore, out bool needsUGui,
                out bool anyShortForm);

            // Which usings the file already has, so the TMPro one is not
            // replaced by a duplicate of a line three rows above it.
            bool hasUGui = HasUsing(lines, code, UGuiNamespace);
            bool hasCore = HasUsing(lines, code, CoreNamespace);

            // Whether the file has a TMPro using at all, and whether it is one
            // this understands well enough to convert. The two are not the same
            // question, and the gap between them is a file left with rewritten
            // type names and no namespace to find them in.
            bool sawUsing = false, convertedUsing = false;

            // Where a using would go in a file that has no TMPro one to convert.
            // Everything the rewriter used to touch came in through that line,
            // so replacing it was enough; a Dropdown does not, and a file that
            // gains OneTextDropdown without gaining the namespace it lives in
            // is a file the rewrite has broken.
            int lastUsing = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text.TrimStart();
                if (text.StartsWith("using ", StringComparison.Ordinal)) lastUsing = i;
                else if (text.Length > 0 && !text.StartsWith("//", StringComparison.Ordinal) &&
                         !text.StartsWith("/*", StringComparison.Ordinal) &&
                         !text.StartsWith("*", StringComparison.Ordinal) && lastUsing >= 0) break;
            }

            var builder = new StringBuilder(source.Length + 32);
            int edit = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                string original = line.Text;

                // Every edit that falls inside this line, in order.
                string rewritten = original;
                if (edit < edits.Count && edits[edit].Start < line.Start + line.Length)
                {
                    var text = new StringBuilder(original.Length + 16);
                    int at = line.Start;
                    while (edit < edits.Count && edits[edit].Start < line.Start + line.Length)
                    {
                        var e = edits[edit++];
                        text.Append(source, at, e.Start - at);
                        text.Append(e.To);
                        at = e.Start + e.Length;
                    }
                    text.Append(source, at, line.Start + line.Length - at);
                    rewritten = text.ToString();
                }

                if (OpensUsingOf(original, line.Start, code, "TMPro"))
                {
                    sawUsing = true;
                    if (IsUsingOf(original, line.Start, code, "TMPro", out string trailer))
                    {
                        rewritten = ReplacementUsings(original,
                            line.EndingLength == 2 ? "\r\n" : "\n", needsCore, trailer,
                            ref hasUGui, ref hasCore);
                        convertedUsing = true;
                    }
                }

                if (rewritten != original)
                    changes.Add(new TmpRewriteChange(i + 1, original, rewritten));

                if (rewritten == null) continue; // the line, and its newline, go
                builder.Append(rewritten);
                builder.Append(source, line.Start + line.Length, line.EndingLength);

                // A file with no TMPro using still needs the namespace for
                // whatever it just gained, so it goes in after the last using
                // the file already had.
                if (i == lastUsing && !convertedUsing && (needsUGui || needsCore))
                {
                    string newline = line.EndingLength == 2 ? "\r\n" : "\n";
                    var added = new StringBuilder();
                    if (needsCore && !hasCore)
                    {
                        added.Append("using ").Append(CoreNamespace).Append(';').Append(newline);
                        hasCore = true;
                    }
                    if (needsUGui && !hasUGui)
                    {
                        added.Append("using ").Append(UGuiNamespace).Append(';').Append(newline);
                        hasUGui = true;
                    }
                    if (added.Length > 0)
                    {
                        builder.Append(added);
                        changes.Add(new TmpRewriteChange(i + 1, original,
                            original + newline + added.ToString().TrimEnd('\r', '\n')));
                    }
                }
            }

            string output = builder.ToString();
            var residuals = Residuals(output);

            var blocking = new List<string>();
            string blocker = Blocker(changes.Count > 0, sawUsing, convertedUsing, anyShortForm,
                residuals, blocking);
            if (blocker != null)
                return new TmpRewriteResult(source, new List<TmpRewriteChange>(), residuals,
                    blocker, blocking, null);

            return new TmpRewriteResult(output, changes, residuals, null, null,
                Assemblies(needsCore, needsUGui));
        }

        /// <summary>
        /// Whether the candidate rewrite is one to offer, and if not, why not.
        ///
        /// The rewriter already knew: it computed the residuals and then shipped
        /// them in a report while writing the file anyway. Two shapes of file
        /// come out of that not compiling, and both are the same mistake — the
        /// type names and the <c>using</c> were decided separately and the
        /// answers disagree.
        ///
        /// A file whose <c>using TMPro;</c> went while <c>TMP_MeshInfo</c>
        /// stayed has lost the namespace that name resolved through. A file
        /// whose <c>using</c> could not be read has short OneText names in it
        /// and no <c>using OneText.UGUI;</c> to find them. Neither is worth
        /// half-doing, so neither is done: the original comes back, and the
        /// person migrating is told which names to deal with first.
        /// </summary>
        private static string Blocker(bool changed, bool sawUsing, bool convertedUsing,
            bool anyShortForm, List<TmpResidual> residuals, List<string> blocking)
        {
            if (!changed) return null;

            if (sawUsing && !convertedUsing)
            {
                if (!anyShortForm) return null;
                return "the file's `using TMPro;` is not a line this can convert, " +
                       "so the renamed types would have no namespace to resolve in";
            }

            if (!convertedUsing) return null;

            foreach (var residual in residuals)
            {
                if (residual.Kind != TmpResidualKind.Reference) continue;
                if (!blocking.Contains(residual.Name)) blocking.Add(residual.Name);
            }
            if (blocking.Count == 0) return null;

            return "removing `using TMPro;` would leave " + string.Join(", ", blocking.ToArray()) +
                   " with nowhere to resolve; this file is manual work";
        }

        /// <summary>
        /// The OneText assemblies a rewritten file names. Assembly definition
        /// references are not transitive, so a file that says
        /// <c>OneTextLabel</c> needs both the assembly the label is in and the
        /// core one its own API is written in terms of.
        /// </summary>
        private static List<string> Assemblies(bool needsCore, bool needsUGui)
        {
            var wanted = new List<string>();
            if (!needsCore && !needsUGui) return wanted;
            wanted.Add(TmpAssemblyGraph.CoreAssembly);
            if (needsUGui) wanted.Add(TmpAssemblyGraph.UGuiAssembly);
            if (needsCore) wanted.Add(TmpAssemblyGraph.MeshAssembly);
            return wanted;
        }

        /// <summary>
        /// Reads each file and reports the ones with something to say: a
        /// rewrite to offer, a TextMesh Pro name left over, or both.
        /// </summary>
        public static List<TmpScriptFinding> Scan(IEnumerable<string> csFilePaths) =>
            ScanProject(csFilePaths, null).Files;

        /// <summary>
        /// The same scan, plus the two things a per-file checkbox cannot say:
        /// which assemblies have to gain a reference before any of this
        /// compiles, and which files have to be applied together.
        ///
        /// <paramref name="assetsRoot"/> is where assembly definitions are
        /// indexed from, which is only needed to read a <c>GUID:…</c> reference
        /// belonging to somebody else; left null it is found by walking up from
        /// the first file to a folder called <c>Assets</c>, and a scan of files
        /// that are not in a project simply has nothing to say about assemblies.
        /// </summary>
        public static TmpScanReport ScanProject(IEnumerable<string> csFilePaths,
            string assetsRoot = null)
        {
            var findings = new List<TmpScriptFinding>();
            var sources = new List<string>();
            if (csFilePaths == null) return new TmpScanReport(findings, new List<TmpFileGroup>());

            TmpAssemblyGraph graph = null;
            foreach (string path in csFilePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string source;
                try
                {
                    source = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                // The cheap gate first: most of a project's scripts have never
                // heard of TextMesh Pro, and lexing all of them to find that
                // out is the difference between a scan you wait for and one you
                // do not.
                if (!MightMentionTmp(source)) continue;

                var result = Rewrite(source);
                if (!result.Changed && result.Residuals.Count == 0) continue;

                if (graph == null)
                    graph = TmpAssemblyGraph.Build(assetsRoot ?? TmpAssemblyGraph.AssetsRootOf(path));

                var owner = graph.Owner(path);
                findings.Add(new TmpScriptFinding(path, result, owner,
                    graph.Missing(owner, result.RequiredAssemblies), IsLikelyThirdParty(path)));
                sources.Add(source);
            }

            return new TmpScanReport(findings, Groups(findings, sources));
        }

        /// <summary>
        /// Whether a file looks like it came with an imported package.
        ///
        /// A hint and nothing more: no vendor list, no special cases, no
        /// behaviour of its own. It exists so the Hub can leave somebody else's
        /// source unticked by default, because a project that rewrites a
        /// package's files has taken over maintaining them and should be
        /// deciding that on purpose.
        /// </summary>
        public static bool IsLikelyThirdParty(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string segment in path.Replace('\\', '/').Split('/'))
            {
                foreach (string vendor in VendorFolders)
                    if (string.Equals(segment, vendor, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        private static readonly string[] VendorFolders =
        {
            "Plugins", "ThirdParty", "Third Party", "3rdParty", "External", "Vendor",
            "Standard Assets", "Asset Store", "AssetStore",
        };

        // ============================================================= groups

        /// <summary>
        /// The names whose meaning changes when a file is rewritten, and which
        /// other files are reading them.
        ///
        /// Two shapes matter. A file that declares a public member written in
        /// terms of a mapped type has put that type in its API, so everything
        /// naming that class has to move with it. And an extension method on a
        /// mapped type is worse, because its call sites never name the class
        /// that declares it — <c>CombatDiceView</c> says
        /// <c>label.DOTextWithCallbacks(…)</c> and nothing in that line points
        /// at <c>TextMeshProExtensions</c> — so the method's own name is what
        /// has to be looked for.
        ///
        /// Conservative in the direction that costs nothing: a file swept into a
        /// group it did not need to be in is applied alongside its neighbours,
        /// which was going to happen anyway. A file left out of one is a
        /// compile error the Hub promised would not happen.
        /// </summary>
        private static HashSet<string> _replacedExtensions;

        /// <summary>
        /// Extension methods OneText itself offers for its own text components:
        /// the ones a converted call site will bind to without its old provider
        /// moving anywhere.
        ///
        /// Read off the loaded assemblies, so it is whatever is actually
        /// installed and compiled right now — an integration switched off by its
        /// define contributes nothing, which is the correct answer rather than a
        /// special case.
        /// </summary>
        private static HashSet<string> ReplacedExtensions()
        {
            if (_replacedExtensions != null) return _replacedExtensions;

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (Exception) { continue; } // a half-loaded assembly is not worth a stack trace

                foreach (var type in types)
                {
                    if (!type.IsSealed || !type.IsAbstract || type.IsNested) continue; // static class
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (!method.IsDefined(typeof(ExtensionAttribute), false)) continue;
                        var parameters = method.GetParameters();
                        if (parameters.Length == 0) continue;

                        string owner = parameters[0].ParameterType.FullName;
                        if (owner == null || !owner.StartsWith("OneText.", StringComparison.Ordinal))
                            continue;
                        found.Add(method.Name);
                    }
                }
            }
            return _replacedExtensions = found;
        }

        private static List<TmpFileGroup> Groups(List<TmpScriptFinding> findings,
            List<string> sources)
        {
            var groups = new List<TmpFileGroup>();
            int count = findings.Count;
            if (count < 2) return groups;

            var words = new HashSet<string>[count];
            var exports = new List<string>[count];
            for (int i = 0; i < count; i++)
            {
                words[i] = new HashSet<string>(StringComparer.Ordinal);
                exports[i] = Exports(sources[i], words[i]);
            }

            var owner = new int[count];
            var why = new string[count];
            for (int i = 0; i < count; i++) owner[i] = i;

            var replaced = ReplacedExtensions();

            for (int provider = 0; provider < count; provider++)
            {
                foreach (string name in exports[provider])
                {
                    // A group exists because a caller and the extension method it
                    // calls cannot move apart. That stops being true the moment
                    // something else offers the same method for the type the
                    // caller is moving to: OneText ships DOText, DOColor and the
                    // rest for its own components, so a call site can convert on
                    // its own and bind to those, and the file that used to
                    // provide them is best left exactly where it is — rewriting
                    // it would put a second DOText(this OneTextLabel, …) in the
                    // same namespace and make every call ambiguous.
                    //
                    // Asked of the assemblies rather than a list kept here,
                    // because a list would be a second copy of the shortcuts file
                    // and would be wrong the first time anyone edited it. When
                    // the integration is switched off there is nothing to find
                    // and the grouping goes back to holding.
                    if (replaced.Contains(name)) continue;

                    for (int consumer = 0; consumer < count; consumer++)
                    {
                        if (consumer == provider || !words[consumer].Contains(name)) continue;
                        int a = Root(owner, provider), b = Root(owner, consumer);
                        if (a == b) continue;
                        owner[b] = a;
                        if (why[a] == null) why[a] = name;
                    }
                }
            }

            var members = new Dictionary<int, List<string>>();
            for (int i = 0; i < count; i++)
            {
                int root = Root(owner, i);
                if (!members.TryGetValue(root, out var paths))
                    members[root] = paths = new List<string>();
                paths.Add(findings[i].Path);
            }

            foreach (var pair in members)
            {
                if (pair.Value.Count < 2) continue;
                pair.Value.Sort(StringComparer.Ordinal);
                groups.Add(new TmpFileGroup(pair.Value, why[pair.Key] ?? string.Empty));
            }
            groups.Sort((a, b) => string.CompareOrdinal(a.Paths[0], b.Paths[0]));
            return groups;
        }

        private static int Root(int[] owner, int i)
        {
            while (owner[i] != i) i = owner[i] = owner[owner[i]];
            return i;
        }

        /// <summary>
        /// The names this file publishes that are written in terms of a mapped
        /// TextMesh Pro type, and, on the way past, every identifier it uses —
        /// which is how the other half of the question gets answered without a
        /// second read of the file.
        /// </summary>
        private static List<string> Exports(string source, HashSet<string> words)
        {
            var exports = new List<string>();
            var declared = new List<string>();
            bool publishes = false;
            if (string.IsNullOrEmpty(source)) return exports;

            bool[] code = CodeMask(source);
            for (int i = 0; i < source.Length; i++)
            {
                if (!code[i] || !IsIdentifierStart(source[i])) continue;
                char before = i > 0 ? source[i - 1] : ' ';
                if (IsIdentifierPart(before))
                {
                    while (i < source.Length && IsIdentifierPart(source[i])) i++;
                    i--;
                    continue;
                }

                int end = i;
                while (end < source.Length && IsIdentifierPart(source[end])) end++;
                string word = source.Substring(i, end - i);
                words.Add(word);

                if (before != '.' && before != '@')
                {
                    if (word == "class" || word == "struct" || word == "interface")
                    {
                        string named = NextToken(source, code, end);
                        if (named.Length > 0 && IsIdentifierStart(named[0])) declared.Add(named);
                    }
                    else if (word == "this")
                    {
                        string open = PreviousToken(source, code, i, out int at);
                        if (open == "(" && Mapped(NextToken(source, code, end)))
                        {
                            string method = PreviousToken(source, code, at, out _);
                            if (method.Length > 0 && IsIdentifierStart(method[0]))
                                exports.Add(method);
                        }
                    }
                    else if (word == "public" || word == "internal" || word == "protected")
                    {
                        if (SignatureMentionsMapped(source, code, end)) publishes = true;
                    }
                }

                i = end - 1;
            }

            if (publishes) exports.AddRange(declared);
            return exports;
        }

        /// <summary>
        /// Does the declaration starting here name a mapped type before it gets
        /// to its body? The signature is everything up to the brace, the
        /// semicolon or the assignment, which is as much of a member as decides
        /// whether another assembly can still call it.
        /// </summary>
        private static bool SignatureMentionsMapped(string source, bool[] code, int from)
        {
            for (int i = from; i < source.Length; i++)
            {
                if (!code[i]) continue;
                char c = source[i];
                if (c == '{' || c == ';' || c == '=') return false;
                if (!IsIdentifierStart(c)) continue;
                char before = i > 0 ? source[i - 1] : ' ';
                int end = i;
                while (end < source.Length && IsIdentifierPart(source[end])) end++;
                if (!IsIdentifierPart(before) && before != '@' &&
                    Mapped(source.Substring(i, end - i)))
                    return true;
                i = end - 1;
            }
            return false;
        }

        /// <summary>A type this rewriter would move, by its short or its namespace name.</summary>
        private static bool Mapped(string word) =>
            word == "TextMeshProUGUI" || word == "TextMeshPro" || word == "TMP_Text" ||
            word == "TMP_InputField" || word == "TMP_Dropdown" || word == "TMPro";

        /// <summary>
        /// Every <c>.cs</c> file under a folder, skipping the ones no human
        /// wrote. Given <c>Assets</c>, that is the project's own scripts and
        /// not a line of any package's.
        /// </summary>
        public static List<string> ScriptsUnder(string root)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return paths;
            Collect(root, paths);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        private static void Collect(string folder, List<string> paths)
        {
            foreach (string file in Directory.GetFiles(folder, "*.cs"))
                paths.Add(file.Replace('\\', '/'));

            foreach (string child in Directory.GetDirectories(folder))
            {
                string name = Path.GetFileName(child);
                if (name.Length == 0 || name[0] == '.') continue;
                // Build output and imported packages: generated, or somebody
                // else's, and in both cases not ours to rewrite.
                if (name == "obj" || name == "bin" || name == "Library" ||
                    name == "Temp" || name == "Packages") continue;
                Collect(child, paths);
            }
        }

        /// <summary>
        /// Worth lexing at all?
        ///
        /// TextMesh Pro's names are the bulk of it, and <c>Dropdown</c> is here
        /// because converting the labels a dropdown owns forces the dropdown
        /// itself to be swapped, and Unity refuses to remove a component another
        /// script declares it requires. A file holding
        /// <c>[RequireComponent(typeof(Dropdown))]</c> and nothing of TMP's is a
        /// file that has to be rewritten first or the swap cannot happen at all —
        /// measured on a real project, where one such file blocked nine
        /// dropdowns across eight scenes.
        /// </summary>
        private static bool MightMentionTmp(string source) =>
            source.IndexOf("TMP", StringComparison.Ordinal) >= 0 ||
            source.IndexOf("TextMeshPro", StringComparison.Ordinal) >= 0 ||
            source.IndexOf("Dropdown", StringComparison.Ordinal) >= 0;

        // ============================================================== types

        private readonly struct Edit
        {
            public readonly int Start;
            public readonly int Length;
            public readonly string To;

            public Edit(int start, int length, string to)
            {
                Start = start;
                Length = length;
                To = to;
            }
        }

        /// <summary>
        /// Every whole-identifier type replacement, in file order.
        ///
        /// A name is only a candidate where a name can start: not after another
        /// identifier character (so <c>MyTextMeshProUGUIWrapper</c> is one word
        /// and matches nothing) and not after a dot (so <c>label.text</c> is a
        /// member access and stays one). The dotted chain is then taken whole,
        /// which is what lets <c>TMPro.TMP_Text</c> match while
        /// <c>Other.TMP_Text</c> does not.
        ///
        /// And then, before anything is written, the position is read. A name is
        /// only substituted where a type can stand; where the same characters
        /// are the thing being named they are left exactly as they were.
        /// </summary>
        private static List<Edit> TypeEdits(string source, bool[] code,
            List<EnumBody> enums, out bool needsCore, out bool needsUGui, out bool anyShortForm)
        {
            // A file that never asked for uGUI's namespace cannot be talking
            // about uGUI's Dropdown by its short name.
            bool usesUGui = source.IndexOf("using UnityEngine.UI;", StringComparison.Ordinal) >= 0;
            var edits = new List<Edit>();
            needsCore = false;
            needsUGui = false;
            anyShortForm = false;

            for (int i = 0; i < source.Length; i++)
            {
                if (!code[i] || !IsIdentifierStart(source[i])) continue;
                char before = i > 0 ? source[i - 1] : ' ';
                if (IsIdentifierPart(before) || before == '.' || before == '@') continue;

                int end = ChainEnd(source, code, i);
                string chain = source.Substring(i, end - i);

                foreach (var map in Types)
                {
                    if (!chain.StartsWith(map.From, StringComparison.Ordinal)) continue;
                    if (UGuiOnly.Contains(map.From) && !usesUGui) continue;
                    // A whole name, or a whole name followed by a member: never
                    // a prefix of a longer identifier.
                    if (chain.Length != map.From.Length && chain[map.From.Length] != '.') continue;
                    if (Where(source, code, i, end, enums) != Position.Type) break;

                    edits.Add(new Edit(i, map.From.Length, map.To));
                    if (map.To.EndsWith("OneTextMesh", StringComparison.Ordinal)) needsCore = true;
                    else needsUGui = true;
                    if (map.From.IndexOf('.') < 0) anyShortForm = true;
                    break;
                }

                i = end - 1;
            }
            return edits;
        }

        // =========================================================== positions

        /// <summary>What the characters are doing where they stand.</summary>
        private enum Position
        {
            /// <summary>A type is named here, and a type can be substituted for it.</summary>
            Type,

            /// <summary>This <em>is</em> the name — of a field, a method, an enum member.</summary>
            Declaration,

            /// <summary>Neither could be established, which is reason enough to stop.</summary>
            Unclear,
        }

        /// <summary>
        /// The tokens after which a type name can stand. Not a grammar: a list,
        /// deliberately, because the cost of the two answers is not the same.
        ///
        /// Reading a type as a name loses the rewrite and the compiler says so.
        /// Reading a name as a type rewrites a declaration whose uses were left
        /// alone — CS0117 at best, and at worst a serialized field that Unity
        /// matches by name, renamed, silently emptied in every scene and prefab
        /// that ever set it, with nothing anywhere to say it happened. So
        /// anything not on this list is not rewritten.
        /// </summary>
        private static readonly HashSet<string> Introducers = new HashSet<string>(StringComparer.Ordinal)
        {
            "(", ",", "<", "[", "{", "}", ";", "=", ":", "=>",
            "new", "as", "is", "in", "out", "ref", "return", "where", "this", "typeof",
            "public", "private", "protected", "internal", "static", "readonly", "const",
            "override", "virtual", "abstract", "sealed", "partial", "extern", "unsafe",
            "volatile", "event", "params", "case", "throw", "operator", "using", "nameof",
        };

        /// <summary>
        /// Whether a type may be substituted at <paramref name="start"/>.
        ///
        /// Enum bodies are answered first and answered flatly. DOTween Pro
        /// declares <c>enum TargetType { … TextMeshPro, TextMeshProUGUI }</c>,
        /// and every use of those members is <c>TargetType.TextMeshProUGUI</c>,
        /// which the dot rule already leaves alone. Rename the declarations and
        /// the two halves disagree; leave both and the package goes on
        /// compiling exactly as it did.
        /// </summary>
        private static Position Where(string source, bool[] code, int start, int end,
            List<EnumBody> enums)
        {
            string previous = PreviousToken(source, code, start, out int at);

            if (InEnumBody(enums, start))
            {
                if (previous == "{" || previous == "," || previous.Length == 0)
                    return Position.Declaration;
                string next = NextToken(source, code, end);
                if (next == "," || next == "}" || next == "=" || next.Length == 0)
                    return Position.Declaration;
            }

            if (previous.Length == 0) return Position.Type;
            if (previous == "]") return AfterBracket(source, code, at);
            if (Introducers.Contains(previous)) return Position.Type;

            // An identifier or a keyword this does not recognise is a type that
            // has just been written out, which makes the candidate the name it
            // is being given: `Foo TextMeshPro;`, `void TextMeshPro(`.
            if (IsIdentifierStart(previous[0])) return Position.Declaration;

            return Position.Unclear;
        }

        /// <summary>
        /// A closing bracket says one of two opposite things. In
        /// <c>[SerializeField] TMP_Text label;</c> it ends an attribute and a
        /// type follows; in <c>int[] TextMeshPro;</c> it ends an array type and
        /// a <em>name</em> follows. Which one it is depends on what stands
        /// before the matching open bracket, so that is what is read.
        /// </summary>
        private static Position AfterBracket(string source, bool[] code, int closeAt)
        {
            int depth = 0;
            for (int i = closeAt; i >= 0; i--)
            {
                if (!code[i]) continue;
                if (source[i] == ']') depth++;
                else if (source[i] == '[')
                {
                    depth--;
                    if (depth > 0) continue;
                    string before = PreviousToken(source, code, i, out _);
                    if (before.Length == 0) return Position.Type;
                    return IsIdentifierStart(before[0]) || before == ">"
                        ? Position.Declaration
                        : Position.Type;
                }
            }
            return Position.Unclear;
        }

        /// <summary>
        /// The last token of code before <paramref name="from"/>, skipping
        /// whitespace and anything the lexer marked as text. An empty string
        /// means there was nothing, which is the start of the file and a
        /// perfectly good place for a type to be.
        /// </summary>
        private static string PreviousToken(string source, bool[] code, int from, out int at)
        {
            int i = from - 1;
            while (i >= 0 && (!code[i] || IsSpace(source[i]))) i--;
            at = i;
            if (i < 0) return string.Empty;

            if (IsIdentifierPart(source[i]))
            {
                int begin = i;
                while (begin > 0 && code[begin - 1] && IsIdentifierPart(source[begin - 1])) begin--;
                at = begin;
                return source.Substring(begin, i - begin + 1);
            }

            // A lambda arrow is one token. A lone `>` closes a generic argument
            // list and the name after it is a declaration, so the two must not
            // read the same.
            if (source[i] == '>' && i > 0 && source[i - 1] == '=' && code[i - 1])
            {
                at = i - 1;
                return "=>";
            }
            return source[i].ToString();
        }

        private static string NextToken(string source, bool[] code, int from)
        {
            int i = from;
            while (i < source.Length && (!code[i] || IsSpace(source[i]))) i++;
            if (i >= source.Length) return string.Empty;
            if (!IsIdentifierPart(source[i])) return source[i].ToString();

            int end = i;
            while (end < source.Length && code[end] && IsIdentifierPart(source[end])) end++;
            return source.Substring(i, end - i);
        }

        private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

        /// <summary>The span between an enum's braces.</summary>
        private readonly struct EnumBody
        {
            public readonly int Start;
            public readonly int End;

            public EnumBody(int start, int end)
            {
                Start = start;
                End = end;
            }
        }

        /// <summary>
        /// Every enum body in the file, so that what is inside one can be told
        /// from what is not. Nothing here needs to know an enum's name or its
        /// members; it only needs the braces, and braces are the one thing an
        /// enum body cannot contain.
        /// </summary>
        private static List<EnumBody> EnumBodies(string source, bool[] code)
        {
            var bodies = new List<EnumBody>();
            for (int i = 0; i < source.Length; i++)
            {
                if (!code[i] || !IsIdentifierStart(source[i])) continue;
                char before = i > 0 ? source[i - 1] : ' ';
                if (IsIdentifierPart(before) || before == '.' || before == '@') continue;

                int end = i;
                while (end < source.Length && IsIdentifierPart(source[end])) end++;
                if (end - i != 4 || string.CompareOrdinal(source, i, "enum", 0, 4) != 0)
                {
                    i = end - 1;
                    continue;
                }

                int open = end;
                while (open < source.Length && (source[open] != '{' || !code[open]))
                {
                    if (code[open] && (source[open] == ';' || source[open] == '}')) break;
                    open++;
                }
                if (open >= source.Length || source[open] != '{' || !code[open])
                {
                    i = end - 1;
                    continue;
                }

                int depth = 0, close = open;
                for (; close < source.Length; close++)
                {
                    if (!code[close]) continue;
                    if (source[close] == '{') depth++;
                    else if (source[close] == '}' && --depth == 0) break;
                }
                bodies.Add(new EnumBody(open, Math.Min(close, source.Length - 1)));
                i = open;
            }
            return bodies;
        }

        private static bool InEnumBody(List<EnumBody> bodies, int at)
        {
            foreach (var body in bodies)
                if (at > body.Start && at < body.End) return true;
            return false;
        }

        /// <summary>End of the dotted identifier chain starting at <paramref name="start"/>.</summary>
        private static int ChainEnd(string source, bool[] code, int start)
        {
            int i = start;
            while (i < source.Length && IsIdentifierPart(source[i])) i++;
            while (i + 1 < source.Length && source[i] == '.' && code[i] &&
                   IsIdentifierStart(source[i + 1]) && code[i + 1])
            {
                i++;
                while (i < source.Length && IsIdentifierPart(source[i])) i++;
            }
            return i;
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        // ============================================================= usings

        /// <summary>
        /// Does this line open with <c>using NS;</c>, in code rather than in a
        /// comment? Still strict about the front — an alias or a
        /// <c>using static</c> is left to the type pass and, failing that, to
        /// the residual report — but it says nothing about what follows the
        /// semicolon, which is what makes it the question the consistency check
        /// needs: whether the file <em>has</em> such a line, whether or not this
        /// can convert it.
        /// </summary>
        private static bool OpensUsingOf(string line, int start, bool[] code, string ns)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || !code[start + i]) return false;
            if (!Match(line, ref i, "using")) return false;
            if (i >= line.Length || (line[i] != ' ' && line[i] != '\t')) return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (!Match(line, ref i, ns)) return false;
            if (i < line.Length && IsIdentifierPart(line[i])) return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i < line.Length && line[i] == ';';
        }

        /// <summary>
        /// Is this a <c>using NS;</c> line this can replace, and what has to
        /// come with it?
        ///
        /// A real file had <c>using TMPro; // TextMeshPro를 사용하는 경우</c>.
        /// The earlier version of this required the line to end at the
        /// semicolon, so it declined — and then the type pass rewrote the whole
        /// file anyway, leaving renamed types under a namespace that no longer
        /// contained them and a dead <c>using TMPro;</c> above them. So a
        /// trailing comment is now read and carried over: the person who wrote
        /// a note to themselves in 2021 keeps it.
        ///
        /// Anything else after the semicolon — a second statement, most
        /// plausibly — is still declined, and now the file is declined with it
        /// rather than being half-rewritten around it.
        /// </summary>
        private static bool IsUsingOf(string line, int start, bool[] code, string ns,
            out string trailer)
        {
            trailer = string.Empty;
            if (!OpensUsingOf(line, start, code, ns)) return false;

            int i = line.IndexOf(';') + 1;
            int tail = i;
            while (i < line.Length)
            {
                if (line[i] == '\r' || line[i] == ' ' || line[i] == '\t') { i++; continue; }
                // Past the semicolon, only what the lexer already called text is
                // allowed to be here, which is exactly a comment.
                if (code[start + i]) return false;
                i++;
            }

            string rest = line.Substring(tail).TrimEnd(' ', '\t', '\r');
            trailer = rest.Trim(' ', '\t');
            return true;
        }

        private static bool IsUsingOf(string line, int start, bool[] code, string ns) =>
            IsUsingOf(line, start, code, ns, out _);

        private static bool Match(string line, ref int i, string word)
        {
            if (i + word.Length > line.Length) return false;
            if (string.CompareOrdinal(line, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static bool HasUsing(List<Line> lines, bool[] code, string ns)
        {
            foreach (var line in lines)
                if (IsUsingOf(line.Text, line.Start, code, ns)) return true;
            return false;
        }

        /// <summary>
        /// What <c>using TMPro;</c> becomes: the usings the rewritten file
        /// actually needs and does not already have, at the original's indent,
        /// or nothing at all when it has them both — in which case the line is
        /// removed rather than replaced by a duplicate. A comment that was
        /// riding on the end of the line rides on the end of the last one
        /// written, and keeps the line alive on its own when there is nothing
        /// left to write.
        /// </summary>
        private static string ReplacementUsings(string original, string newline, bool needsCore,
            string trailer, ref bool hasUGui, ref bool hasCore)
        {
            int indent = 0;
            while (indent < original.Length && (original[indent] == ' ' || original[indent] == '\t'))
                indent++;
            string pad = original.Substring(0, indent);

            var wanted = new List<string>();
            if (needsCore && !hasCore)
            {
                wanted.Add($"{pad}using {CoreNamespace};");
                hasCore = true;
            }
            if (!hasUGui)
            {
                wanted.Add($"{pad}using {UGuiNamespace};");
                hasUGui = true;
            }

            bool commented = !string.IsNullOrEmpty(trailer);
            if (wanted.Count == 0) return commented ? pad + trailer : null;

            wanted[wanted.Count - 1] += commented ? " " + trailer : string.Empty;
            return string.Join(newline, wanted);
        }

        // ========================================================== residuals

        /// <summary>
        /// TextMesh Pro names still standing in the rewritten text.
        ///
        /// The point is not completeness of the map; it is that the person
        /// migrating is told, before they apply anything, that this file still
        /// has work in it. <c>TMP_SpriteAsset</c>, <c>TMP_FontAsset</c> and
        /// <c>TMP_Settings</c> all land here, and so does a stray
        /// <c>using TMP = TMPro;</c> alias.
        /// </summary>
        private static List<TmpResidual> Residuals(string text)
        {
            var found = new List<TmpResidual>();
            if (string.IsNullOrEmpty(text)) return found;

            bool[] code = CodeMask(text);
            var enums = EnumBodies(text, code);
            int line = 1;
            var seen = new HashSet<string>();

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    continue;
                }
                if (!code[i] || !IsIdentifierStart(text[i])) continue;
                char before = i > 0 ? text[i - 1] : ' ';
                if (IsIdentifierPart(before) || before == '.' || before == '@') continue;

                int at = i;
                int end = i;
                while (end < text.Length && IsIdentifierPart(text[end])) end++;
                string word = text.Substring(i, end - i);
                i = end - 1;

                string name = null;
                // Both prefixes: TextMesh Pro ships TMP_Text and TMPro_EventManager
                // side by side, and a gate that only knew the first let the
                // second through — the using line went, the name stayed, and the
                // file was written in a state that does not compile. Exactly what
                // the gate exists to prevent, missed on a spelling.
                if (word.StartsWith("TMP_", StringComparison.Ordinal) ||
                    word.StartsWith("TMPro_", StringComparison.Ordinal)) name = word;
                else if (word == "TMPro" || word == "TextMeshPro" || word == "TextMeshProUGUI")
                    name = word;
                if (name == null) continue;

                // The namespace is never held up by the using that imports it,
                // and neither is anything written out through it.
                var kind = word == "TMPro"
                    ? TmpResidualKind.Qualified
                    : Where(text, code, at, end, enums) == Position.Declaration
                        ? TmpResidualKind.Declaration
                        : TmpResidualKind.Reference;

                // A qualified name reports the type rather than the namespace:
                // TMPro.TMP_SpriteAsset is a sprite problem, not a using problem.
                if (name == "TMPro" && end + 1 < text.Length && text[end] == '.' &&
                    IsIdentifierStart(text[end + 1]))
                {
                    int tail = end + 1;
                    while (tail < text.Length && IsIdentifierPart(text[tail])) tail++;
                    name = text.Substring(end + 1, tail - end - 1);
                    i = tail - 1;
                }

                if (seen.Add($"{name}@{line}")) found.Add(new TmpResidual(name, line, kind));
            }
            return found;
        }

        // ============================================================== lexer

        private readonly struct Line
        {
            public readonly int Start;
            public readonly int Length;       // without the line ending
            public readonly int EndingLength; // 0, 1 or 2
            public readonly string Text;

            public Line(int start, int length, int endingLength, string text)
            {
                Start = start;
                Length = length;
                EndingLength = endingLength;
                Text = text;
            }
        }

        private static void SplitLines(string source, List<Line> lines)
        {
            int start = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] != '\n') continue;
                int ending = i > start && source[i - 1] == '\r' ? 2 : 1;
                int length = i + 1 - start - ending;
                lines.Add(new Line(start, length, ending, source.Substring(start, length)));
                start = i + 1;
            }
            if (start <= source.Length)
                lines.Add(new Line(start, source.Length - start, 0,
                    source.Substring(start, source.Length - start)));
        }

        /// <summary>
        /// Which characters are code: not in a comment, a string of any of the
        /// four kinds, or a character literal.
        ///
        /// A lexer and not a regular expression, because the thing being
        /// protected is the project's own text. <c>"TMP_Text"</c> in a log
        /// message, a verbatim path, an interpolated hole, or a commented-out
        /// line from last year all have to come out the far side byte for byte
        /// identical, and a pattern with word boundaries in it cannot tell any
        /// of them from code.
        /// </summary>
        internal static bool[] CodeMask(string s)
        {
            var code = new bool[s.Length];
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i = Math.Min(s.Length, i + 2);
                    continue;
                }

                if (c == '"' || c == '\'' || StartsPrefixedString(s, i))
                {
                    i = SkipLiteral(s, i);
                    continue;
                }

                code[i] = true;
                i++;
            }
            return code;
        }

        /// <summary>An <c>@"…"</c>, <c>$"…"</c>, <c>$@"…"</c> or <c>@$"…"</c> opener.</summary>
        private static bool StartsPrefixedString(string s, int i)
        {
            if (s[i] != '@' && s[i] != '$') return false;
            int j = i;
            while (j < s.Length && (s[j] == '@' || s[j] == '$')) j++;
            return j < s.Length && s[j] == '"' && j > i;
        }

        /// <summary>Index just past the literal beginning at <paramref name="i"/>.</summary>
        private static int SkipLiteral(string s, int i)
        {
            if (s[i] == '\'') return SkipQuoted(s, i, '\'');
            if (s[i] == '"') return SkipQuoted(s, i, '"');

            bool verbatim = false, interpolated = false;
            int j = i;
            while (j < s.Length && (s[j] == '@' || s[j] == '$'))
            {
                if (s[j] == '@') verbatim = true;
                else interpolated = true;
                j++;
            }
            if (j >= s.Length || s[j] != '"') return i + 1;
            j++;

            // An interpolated string is skipped whole, holes included. Code in a
            // hole is code, but a rewrite there would have to be spliced back
            // into a string, and no project has a type name in a hole often
            // enough to be worth the class of bug that buys. The brace depth is
            // tracked only to find the closing quote, since a hole may contain
            // a string of its own.
            int depth = 0;
            while (j < s.Length)
            {
                char c = s[j];

                if (!verbatim && c == '\\')
                {
                    j += 2;
                    continue;
                }
                if (verbatim && c == '"' && j + 1 < s.Length && s[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }
                if (c == '"')
                {
                    if (depth == 0) return j + 1;
                    j = SkipLiteral(s, j);
                    continue;
                }
                if (interpolated && c == '{')
                {
                    if (j + 1 < s.Length && s[j + 1] == '{') { j += 2; continue; }
                    depth++;
                    j++;
                    continue;
                }
                if (interpolated && c == '}')
                {
                    if (depth == 0 && j + 1 < s.Length && s[j + 1] == '}') { j += 2; continue; }
                    if (depth > 0) depth--;
                    j++;
                    continue;
                }
                // An unterminated non-verbatim string ends at the newline rather
                // than swallowing the rest of the file.
                if (!verbatim && c == '\n') return j;
                j++;
            }
            return j;
        }

        private static int SkipQuoted(string s, int i, char quote)
        {
            int j = i + 1;
            while (j < s.Length)
            {
                char c = s[j];
                if (c == '\\')
                {
                    j += 2;
                    continue;
                }
                if (c == quote) return j + 1;
                if (c == '\n') return j;
                j++;
            }
            return j;
        }
    }
}
