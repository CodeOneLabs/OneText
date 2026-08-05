using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using OneText.Unicode;

namespace OneText.Editor
{
    public enum DoctorSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>One thing wrong, named by the rule that found it.</summary>
    public struct DoctorFinding
    {
        public DoctorSeverity Severity;

        /// <summary>Rule name, so a finding can be looked up rather than interpreted.</summary>
        public string Rule;

        public string Message;

        /// <summary>Locale, key and file of the string that triggered it, where there is one.</summary>
        public string Locale, Key, Source;

        /// <summary>The offending text, trimmed to something a log line can hold.</summary>
        public string Sample;

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(Severity.ToString().ToLowerInvariant()).Append(": [").Append(Rule).Append("] ");
            builder.Append(Message);
            if (!string.IsNullOrEmpty(Locale)) builder.Append("  locale=").Append(Locale);
            if (!string.IsNullOrEmpty(Key)) builder.Append("  key=").Append(Key);
            if (!string.IsNullOrEmpty(Source)) builder.Append("  in ").Append(Source);
            return builder.ToString();
        }
    }

    public sealed class DoctorReport
    {
        public readonly List<DoctorFinding> Findings = new List<DoctorFinding>();
        public int StringsChecked;
        public int CharactersChecked;
        public int FilesScanned;

        public int Count(DoctorSeverity severity)
        {
            int n = 0;
            foreach (var finding in Findings) if (finding.Severity == severity) n++;
            return n;
        }

        public int Errors => Count(DoctorSeverity.Error);
        public int Warnings => Count(DoctorSeverity.Warning);

        /// <summary>What a build should key off: warnings are advice, errors are text nobody can read.</summary>
        public bool Passed => Errors == 0;

        public string Summary() =>
            $"{StringsChecked:n0} string(s) from {FilesScanned} file(s): " +
            $"{Errors} error(s), {Warnings} warning(s), {Count(DoctorSeverity.Info)} note(s)";
    }

    /// <summary>
    /// Static renderability analysis: the project's strings against the fonts
    /// and break data it actually ships.
    ///
    /// Every failure here is one that otherwise appears for the first time in
    /// front of a player, in a language the team does not read — a box instead
    /// of a character, a Japanese sentence in Chinese shapes, Thai wrapped
    /// mid-word. None of them throw, none of them fail a test, and all of them
    /// are decidable before the build: the strings are in the project, the
    /// fonts are in the project, and the question of whether one can draw the
    /// other is a lookup.
    ///
    /// Which is why this exits with a code. A lint nobody runs is a document;
    /// the point is that an unrenderable string fails the merge.
    /// </summary>
    public static class TextDoctor
    {
        /// <summary>Below this, a dictionary is present but is not doing the job.</summary>
        public const float MinimumDictionaryCoverage = 0.9f;

        /// <summary>Locales whose Han characters have a national shape worth arguing about.</summary>
        private static readonly string[] HanLocales = { "ja", "zh", "ko" };

        public static DoctorReport Run(TextScanResult strings, FontStack fonts)
        {
            var report = new DoctorReport();
            if (strings == null) return report;
            report.FilesScanned = strings.FilesScanned;

            foreach (string skipped in strings.Skipped)
            {
                report.Findings.Add(new DoctorFinding
                {
                    Severity = DoctorSeverity.Warning,
                    Rule = "unreadable-source",
                    Message = $"a source file could not be read: {skipped}",
                });
            }

            if (fonts == null || fonts.Primary == null)
            {
                report.Findings.Add(new DoctorFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = "no-font",
                    Message = "no font to check against — assign a default font in " +
                              "Project Settings > OneText.",
                });
                return report;
            }

            CheckRenderable(strings, fonts, report);
            CheckHanUnification(strings, fonts, report);
            CheckDictionaries(strings, report);
            return report;
        }

        /// <summary>Convenience: scan folders, then check what they hold.</summary>
        public static DoctorReport Run(IEnumerable<string> folders) =>
            Run(TextSourceScanner.Scan(folders), ProjectFontStack());

        /// <summary>The chain a label gets with no font of its own — what Doctor judges against.</summary>
        public static FontStack ProjectFontStack()
        {
            var stack = new FontStack();
            var settings = OneTextSettings.Instance;
            if (settings == null) return stack;
            if (settings.DefaultFont != null)
                stack.Add(settings.DefaultFont.Font, settings.DefaultFont.Language);
            foreach (var fallback in settings.FallbackFonts)
                if (fallback != null) stack.Add(fallback.Font, fallback.Language);
            return stack;
        }

        // -------------------------------------------------------------- rules

        /// <summary>
        /// Tofu, predicted. One finding per code point rather than per string:
        /// a missing character is missing everywhere it appears, and a hundred
        /// findings that say the same thing bury the ninety-nine others.
        /// </summary>
        private static void CheckRenderable(TextScanResult strings, FontStack fonts, DoctorReport report)
        {
            var missing = new Dictionary<int, (string Locale, string Key, string Source, int Count)>();
            var checkedPoints = new HashSet<int>();

            foreach (var entry in strings.Entries)
            {
                report.StringsChecked++;
                string value = entry.Value;
                if (string.IsNullOrEmpty(value)) continue;

                foreach (int codepoint in Codepoints(value))
                {
                    report.CharactersChecked++;
                    if (codepoint == '\n' || codepoint == '\t') continue;
                    if (!checkedPoints.Add(codepoint))
                    {
                        if (missing.TryGetValue(codepoint, out var seen))
                            missing[codepoint] = (seen.Locale, seen.Key, seen.Source, seen.Count + 1);
                        continue;
                    }
                    if (fonts.Covers(codepoint)) continue;
                    missing[codepoint] = (entry.Locale, entry.Key, entry.Source, 1);
                }
            }

            foreach (var pair in missing)
            {
                var where = pair.Value;
                report.Findings.Add(new DoctorFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = "tofu",
                    Message = $"no font in the project chain draws U+{pair.Key:X4} " +
                              $"'{char.ConvertFromUtf32(pair.Key)}' " +
                              $"({where.Count:n0} occurrence(s))",
                    Locale = where.Locale,
                    Key = where.Key,
                    Source = where.Source,
                    Sample = char.ConvertFromUtf32(pair.Key),
                });
            }
        }

        /// <summary>
        /// Han unification: the same code point, two correct shapes, and a
        /// chain that answers by position unless somebody tagged it.
        ///
        /// Two findings, because there are two ways to get this wrong. A
        /// tagged chain can still be missing the tag a locale needs — that is
        /// specific and reportable. An untagged chain is worse and quieter: it
        /// is not wrong for any one string, it is undefined for all of them,
        /// and it only becomes visible when a native reader sees the build.
        /// </summary>
        private static void CheckHanUnification(TextScanResult strings, FontStack fonts, DoctorReport report)
        {
            var hanLocales = new List<string>();
            var reported = new HashSet<string>();

            foreach (var entry in strings.Entries)
            {
                string language = PrimarySubtag(entry.Locale);
                if (language == null || Array.IndexOf(HanLocales, language) < 0) continue;
                if (!HasIdeograph(entry.Value)) continue;
                if (!hanLocales.Contains(language)) hanLocales.Add(language);

                // What the label will actually do with this string, asked of
                // the same stack the label uses, with the same language.
                foreach (int codepoint in Codepoints(entry.Value))
                {
                    if (codepoint > char.MaxValue || !AsianTypography.IsIdeographic((char)codepoint)) continue;
                    var font = fonts.Resolve(codepoint, false, false, FontStack.Presentation.Any, entry.Locale);
                    if (font == null) continue;
                    string tag = fonts.LanguageOf(font);
                    if (tag == null) break; // untagged chain: reported once, below

                    if (LanguageServes(tag, entry.Locale)) break;
                    if (!reported.Add($"{entry.Locale}/{tag}")) break;
                    report.Findings.Add(new DoctorFinding
                    {
                        Severity = DoctorSeverity.Warning,
                        Rule = "han-unification",
                        Message = $"{entry.Locale} text resolves through a font tagged '{tag}' — " +
                                  "Han characters will take that language's shapes. Tag a face for " +
                                  $"'{entry.Locale}' on its font asset, or add one to the chain.",
                        Locale = entry.Locale,
                        Key = entry.Key,
                        Source = entry.Source,
                        Sample = Trim(entry.Value),
                    });
                    break;
                }
            }

            if (hanLocales.Count < 2) return;
            bool anyTagged = false;
            foreach (var font in fonts.Fonts)
                if (fonts.LanguageOf(font) != null) { anyTagged = true; break; }
            if (anyTagged) return;

            report.Findings.Add(new DoctorFinding
            {
                Severity = DoctorSeverity.Warning,
                Rule = "han-unification",
                Message = $"this project ships Han text in {string.Join(", ", hanLocales)} and no font " +
                          "in the chain declares a language, so all of them get whichever face comes " +
                          "first. Set Language on the font assets (ja, zh-Hans, zh-Hant, ko).",
            });
        }

        /// <summary>
        /// The scripts UAX #14 hands to a dictionary, checked against the
        /// dictionary that is actually installed.
        ///
        /// Coverage rather than presence: the built-in Thai starter list is
        /// present in every project and answers roughly a tenth of real text.
        /// A number is the only honest way to tell a team that does not read
        /// the language whether their build wraps it correctly.
        /// </summary>
        private static void CheckDictionaries(TextScanResult strings, DoctorReport report)
        {
            DictionaryLineBreaker.EnsureDefaults();
            var samples = new Dictionary<string, StringBuilder>();
            var first = new Dictionary<string, TextEntry>();

            foreach (var entry in strings.Entries)
            {
                if (string.IsNullOrEmpty(entry.Value)) continue;
                foreach (char c in entry.Value)
                {
                    string script = DictionaryLineBreaker.ScriptOf(c);
                    if (script == null) continue;
                    if (!samples.TryGetValue(script, out var sample))
                    {
                        samples[script] = sample = new StringBuilder();
                        first[script] = entry;
                    }
                    // A sample, not the corpus: coverage converges long before
                    // a hundred thousand characters, and Doctor runs on a merge.
                    if (sample.Length < 20000) sample.Append(c);
                }
            }

            foreach (var pair in samples)
            {
                string script = pair.Key;
                var words = DictionaryLineBreaker.GetWordList(script);
                var where = first[script];
                if (words == null)
                {
                    report.Findings.Add(new DoctorFinding
                    {
                        Severity = DoctorSeverity.Error,
                        Rule = "missing-dictionary",
                        Message = $"this project ships {script} text and no {script} word list is " +
                                  "installed, so it will wrap at arbitrary characters. Import one in " +
                                  "Window > OneText > Hub > Dictionaries.",
                        Locale = where.Locale,
                        Key = where.Key,
                        Source = where.Source,
                    });
                    continue;
                }

                float coverage = words.Coverage(pair.Value.ToString());
                if (coverage >= MinimumDictionaryCoverage)
                {
                    report.Findings.Add(new DoctorFinding
                    {
                        Severity = DoctorSeverity.Info,
                        Rule = "dictionary-coverage",
                        Message = $"{script}: {coverage:P1} of sampled text is segmented by the " +
                                  $"installed word list ({words.WordCount:n0} words).",
                        Locale = where.Locale,
                    });
                    continue;
                }

                report.Findings.Add(new DoctorFinding
                {
                    Severity = DoctorSeverity.Warning,
                    Rule = "dictionary-coverage",
                    Message = $"{script}: the installed word list ({words.WordCount:n0} words) segments " +
                              $"only {coverage:P1} of this project's {script} text. Import the full " +
                              "ICU dictionary in Window > OneText > Hub > Dictionaries.",
                    Locale = where.Locale,
                    Key = where.Key,
                    Source = where.Source,
                    Sample = Trim(pair.Value.ToString()),
                });
            }
        }

        // ------------------------------------------------------------- CI

        /// <summary>
        /// Batch-mode entry point. Folders come from <c>-oneStrings a,b</c>,
        /// or from the folders the project's charsets already scan — a project
        /// that told the Hub where its strings are should not have to tell CI
        /// again.
        ///
        /// Exits 1 on any error, so a merge can depend on it.
        ///
        /// Unity -batchmode -projectPath &lt;p&gt; -executeMethod
        ///     OneText.Editor.TextDoctor.RunFromCommandLine [-oneStrings Assets/Localization]
        /// </summary>
        public static void RunFromCommandLine()
        {
            var folders = new List<string>();
            string argument = GetArg("-oneStrings");
            if (!string.IsNullOrEmpty(argument))
                folders.AddRange(argument.Split(','));
            else
                foreach (var charset in CharsetFolderScan.AutoRescanning())
                    foreach (string folder in charset.SourceFolders)
                        if (!folders.Contains(folder)) folders.Add(folder);

            if (folders.Count == 0)
            {
                Debug.LogError("OneText Doctor: no string folders to check. Pass -oneStrings " +
                    "<folder>[,<folder>], or set source folders on a charset asset.");
                EditorApplication.Exit(2);
                return;
            }

            var report = Run(folders);
            foreach (var finding in report.Findings)
            {
                string line = $"OneText Doctor {finding}";
                switch (finding.Severity)
                {
                    case DoctorSeverity.Error: Debug.LogError(line); break;
                    case DoctorSeverity.Warning: Debug.LogWarning(line); break;
                    default: Debug.Log(line); break;
                }
            }

            Debug.Log($"OneText Doctor: {report.Summary()}");
            EditorApplication.Exit(report.Passed ? 0 : 1);
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        // ---------------------------------------------------------- utilities

        public static IEnumerable<int> Codepoints(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    yield return char.ConvertToUtf32(c, text[i + 1]);
                    i++;
                }
                else yield return c;
            }
        }

        private static bool HasIdeograph(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (AsianTypography.IsIdeographic(c)) return true;
            return false;
        }

        public static string PrimarySubtag(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return null;
            int dash = locale.IndexOf('-');
            string language = (dash < 0 ? locale : locale.Substring(0, dash)).ToLowerInvariant();
            return language.Length == 0 ? null : language;
        }

        /// <summary>
        /// Whether a font's tag serves a locale — the same prefix rule the font
        /// stack resolves with, asked from outside so a finding says the same
        /// thing the renderer will do.
        /// </summary>
        public static bool LanguageServes(string fontLanguage, string locale)
        {
            if (string.IsNullOrEmpty(fontLanguage) || string.IsNullOrEmpty(locale)) return false;
            if (string.Equals(fontLanguage, locale, StringComparison.OrdinalIgnoreCase)) return true;
            return locale.Length > fontLanguage.Length &&
                   locale.StartsWith(fontLanguage, StringComparison.OrdinalIgnoreCase) &&
                   locale[fontLanguage.Length] == '-';
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Replace('\n', ' ');
            return text.Length <= 48 ? text : text.Substring(0, 45) + "...";
        }
    }
}
