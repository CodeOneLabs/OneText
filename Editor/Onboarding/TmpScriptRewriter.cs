using System;
using System.Collections.Generic;
using System.IO;
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
    /// A TextMesh Pro name still standing after the rewrite: something with no
    /// OneText counterpart, or one the rewriter refuses to guess at.
    /// </summary>
    public readonly struct TmpResidual
    {
        /// <summary>The identifier, e.g. <c>TMP_Dropdown</c>.</summary>
        public readonly string Name;

        /// <summary>1-based line number in the <em>rewritten</em> text.</summary>
        public readonly int Line;

        public TmpResidual(string name, int line)
        {
            Name = name;
            Line = line;
        }

        public override string ToString() => $"{Name} (line {Line})";
    }

    /// <summary>What one file would become.</summary>
    public sealed class TmpRewriteResult
    {
        public TmpRewriteResult(string text, List<TmpRewriteChange> changes,
            List<TmpResidual> residuals)
        {
            Text = text;
            Changes = changes;
            Residuals = residuals;
        }

        /// <summary>The whole file, rewritten.</summary>
        public string Text { get; }

        /// <summary>Every line that differs, in file order.</summary>
        public List<TmpRewriteChange> Changes { get; }

        /// <summary>TextMesh Pro names the rewrite could not carry over.</summary>
        public List<TmpResidual> Residuals { get; }

        public bool Changed => Changes.Count > 0;
    }

    /// <summary>One file the scan turned up.</summary>
    public sealed class TmpScriptFinding
    {
        public TmpScriptFinding(string path, TmpRewriteResult result)
        {
            Path = path;
            Result = result;
        }

        public string Path { get; }

        public TmpRewriteResult Result { get; }
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
    /// Conservative on purpose, in two directions. It rewrites four type names
    /// and one <c>using</c>, and stops: a member it does not understand is left
    /// for the compiler to point at, which is a better guide than a rewriter
    /// guessing. And it will not touch a character inside a string literal, a
    /// character literal or a comment, which is why the scanner below is a
    /// lexer rather than a regular expression. A project's own dialogue lines
    /// and its build scripts are full of the word TextMeshPro; a regex would
    /// silently edit a shipped string, and nobody would notice until a
    /// localization diff.
    ///
    /// Whatever it cannot handle it still reports. A file that mentions
    /// <c>TMP_Dropdown</c> comes back with that name and a line number, so the
    /// person migrating is told where the manual work is before they press the
    /// button rather than by a wall of compile errors after.
    /// </summary>
    public static class TmpScriptRewriter
    {
        /// <summary>
        /// The whole of the type map, longest name first so
        /// <c>TextMeshProUGUI</c> is never read as <c>TextMeshPro</c> with a
        /// suffix. The qualified forms are the same map under <c>TMPro.</c>,
        /// which is how a file that never had a <c>using</c> is handled.
        /// </summary>
        private static readonly (string From, string To)[] Types =
        {
            ("TMPro.TextMeshProUGUI", "OneText.UGUI.OneTextLabel"),
            ("TMPro.TMP_InputField", "OneText.UGUI.OneTextInputField"),
            ("TMPro.TextMeshPro", "OneText.OneTextMesh"),
            ("TMPro.TMP_Text", "OneText.UGUI.OneTextLabel"),
            ("TextMeshProUGUI", "OneTextLabel"),
            ("TMP_InputField", "OneTextInputField"),
            ("TextMeshPro", "OneTextMesh"),
            ("TMP_Text", "OneTextLabel"),
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

            // Types first: whether the file needs `using OneText;` is decided by
            // whether anything in it turned into a OneTextMesh.
            var edits = TypeEdits(source, code, out bool needsCore);

            // Which usings the file already has, so the TMPro one is not
            // replaced by a duplicate of a line three rows above it.
            bool hasUGui = HasUsing(lines, code, UGuiNamespace);
            bool hasCore = HasUsing(lines, code, CoreNamespace);

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

                if (IsUsingOf(original, line.Start, code, "TMPro"))
                {
                    rewritten = ReplacementUsings(original,
                        line.EndingLength == 2 ? "\r\n" : "\n", needsCore,
                        ref hasUGui, ref hasCore);
                }

                if (rewritten != original)
                    changes.Add(new TmpRewriteChange(i + 1, original, rewritten));

                if (rewritten == null) continue; // the line, and its newline, go
                builder.Append(rewritten);
                builder.Append(source, line.Start + line.Length, line.EndingLength);
            }

            string output = builder.ToString();
            return new TmpRewriteResult(output, changes, Residuals(output));
        }

        /// <summary>
        /// Reads each file and reports the ones with something to say: a
        /// rewrite to offer, a TextMesh Pro name left over, or both.
        /// </summary>
        public static List<TmpScriptFinding> Scan(IEnumerable<string> csFilePaths)
        {
            var findings = new List<TmpScriptFinding>();
            if (csFilePaths == null) return findings;

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
                findings.Add(new TmpScriptFinding(path, result));
            }
            return findings;
        }

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

        /// <summary>Worth lexing at all?</summary>
        private static bool MightMentionTmp(string source) =>
            source.IndexOf("TMP", StringComparison.Ordinal) >= 0 ||
            source.IndexOf("TextMeshPro", StringComparison.Ordinal) >= 0;

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
        /// </summary>
        private static List<Edit> TypeEdits(string source, bool[] code, out bool needsCore)
        {
            var edits = new List<Edit>();
            needsCore = false;

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
                    // A whole name, or a whole name followed by a member: never
                    // a prefix of a longer identifier.
                    if (chain.Length != map.From.Length && chain[map.From.Length] != '.') continue;
                    edits.Add(new Edit(i, map.From.Length, map.To));
                    if (map.To.EndsWith("OneTextMesh", StringComparison.Ordinal)) needsCore = true;
                    break;
                }

                i = end - 1;
            }
            return edits;
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
        /// Is this line exactly <c>using NS;</c>, in code rather than in a
        /// comment? Deliberately strict: an alias or a <c>using static</c> is
        /// left to the type pass and, failing that, to the residual report.
        /// </summary>
        private static bool IsUsingOf(string line, int start, bool[] code, string ns)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || !code[start + i]) return false;
            if (!Match(line, ref i, "using")) return false;
            if (i >= line.Length || (line[i] != ' ' && line[i] != '\t')) return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (!Match(line, ref i, ns)) return false;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || line[i] != ';') return false;
            i++;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t' || line[i] == '\r')) i++;
            return i == line.Length;
        }

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
        /// removed rather than replaced by a duplicate.
        /// </summary>
        private static string ReplacementUsings(string original, string newline, bool needsCore,
            ref bool hasUGui, ref bool hasCore)
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

            if (wanted.Count == 0) return null;
            return string.Join(newline, wanted);
        }

        // ========================================================== residuals

        /// <summary>
        /// TextMesh Pro names still standing in the rewritten text.
        ///
        /// The point is not completeness of the map; it is that the person
        /// migrating is told, before they apply anything, that this file still
        /// has work in it. <c>TMP_Dropdown</c>, <c>TMP_FontAsset</c> and
        /// <c>TMP_Settings</c> all land here, and so does a stray
        /// <c>using TMP = TMPro;</c> alias.
        /// </summary>
        private static List<TmpResidual> Residuals(string text)
        {
            var found = new List<TmpResidual>();
            if (string.IsNullOrEmpty(text)) return found;

            bool[] code = CodeMask(text);
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

                int end = i;
                while (end < text.Length && IsIdentifierPart(text[end])) end++;
                string word = text.Substring(i, end - i);
                i = end - 1;

                string name = null;
                if (word.StartsWith("TMP_", StringComparison.Ordinal)) name = word;
                else if (word == "TMPro" || word == "TextMeshPro" || word == "TextMeshProUGUI")
                    name = word;
                if (name == null) continue;

                // A qualified name reports the type rather than the namespace:
                // TMPro.TMP_Dropdown is a dropdown problem, not a using problem.
                if (name == "TMPro" && end + 1 < text.Length && text[end] == '.' &&
                    IsIdentifierStart(text[end + 1]))
                {
                    int tail = end + 1;
                    while (tail < text.Length && IsIdentifierPart(text[tail])) tail++;
                    name = text.Substring(end + 1, tail - end - 1);
                    i = tail - 1;
                }

                if (seen.Add($"{name}@{line}")) found.Add(new TmpResidual(name, line));
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
