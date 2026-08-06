using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>One string a project ships, and where it came from.</summary>
    public struct TextEntry
    {
        /// <summary>Key, row name or JSON path: whatever the file called it.</summary>
        public string Key;

        /// <summary>The string itself.</summary>
        public string Value;

        /// <summary>Locale the string belongs to, or null when the file did not say.</summary>
        public string Locale;

        /// <summary>Project path of the file it was read from.</summary>
        public string Source;
    }

    /// <summary>What one scan found.</summary>
    public sealed class TextScanResult
    {
        public readonly List<TextEntry> Entries = new List<TextEntry>();
        public readonly List<string> Skipped = new List<string>();
        public int FilesScanned;

        /// <summary>Locales seen, in first-seen order, with the unnamed one last.</summary>
        public List<string> Locales()
        {
            var seen = new List<string>();
            bool unnamed = false;
            foreach (var entry in Entries)
            {
                if (string.IsNullOrEmpty(entry.Locale)) { unnamed = true; continue; }
                if (!seen.Contains(entry.Locale)) seen.Add(entry.Locale);
            }
            if (unnamed) seen.Add(null);
            return seen;
        }

        /// <summary>Every distinct character in every string, sorted, no whitespace.</summary>
        public string CharactersAsString(string locale = null)
        {
            var seen = new SortedSet<int>();
            foreach (var entry in Entries)
            {
                if (locale != null && entry.Locale != locale) continue;
                string value = entry.Value;
                if (string.IsNullOrEmpty(value)) continue;
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        seen.Add(char.ConvertToUtf32(c, value[i + 1]));
                        i++;
                    }
                    else seen.Add(c);
                }
            }

            var builder = new StringBuilder(seen.Count);
            foreach (int cp in seen) builder.Append(char.ConvertFromUtf32(cp));
            return builder.ToString();
        }
    }

    /// <summary>
    /// Reads the strings a project actually ships out of the files they live in:
    /// localization tables, dialogue scripts, anything textual under a folder.
    ///
    /// The scene scan that came before this one catches static UI, and a play
    /// session catches what was assembled at runtime, but a game's real
    /// characters live in its string tables, which are the one place neither
    /// looks. Everything downstream of here wants the same list: prewarm and
    /// subsetting want its characters, the gallery lays each string out, Doctor
    /// asks whether any font can draw them.
    ///
    /// Every format is read as data, never executed, and a file that does not
    /// parse is reported rather than guessed at.
    /// </summary>
    public static class TextSourceScanner
    {
        /// <summary>Extensions read as text. Anything else under the folder is left alone.</summary>
        public static readonly string[] TextExtensions = { ".csv", ".tsv", ".json", ".txt", ".md", ".xml" };

        /// <summary>Scans project folders (paths relative to the project root, e.g. "Assets/Localization").</summary>
        public static TextScanResult Scan(IEnumerable<string> folders)
        {
            var result = new TextScanResult();
            if (folders == null) return result;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                string full = Path.GetFullPath(folder);
                if (!Directory.Exists(full))
                {
                    result.Skipped.Add($"{folder}: no such folder");
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!visited.Add(file)) continue;
                    ScanFile(file, result);
                }

                ScanLocalizationTables(folder, result);
            }
            return result;
        }

        private static void ScanFile(string fullPath, TextScanResult result)
        {
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (Array.IndexOf(TextExtensions, extension) < 0) return;

            string project = ToProjectPath(fullPath);
            string text;
            try
            {
                text = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (IOException e)
            {
                result.Skipped.Add($"{project}: {e.Message}");
                return;
            }

            result.FilesScanned++;
            switch (extension)
            {
                case ".csv": ReadSeparated(text, ',', project, result); break;
                case ".tsv": ReadSeparated(text, '\t', project, result); break;
                case ".json": ReadJson(text, project, result); break;
                default: ReadLines(text, project, result); break;
            }
        }

        // ------------------------------------------------------------ formats

        /// <summary>
        /// A separated-value table: first column the key, every other column a
        /// locale named by the header. That is the shape of every localization
        /// export the engine-agnostic tools produce, and of the CSV Unity's own
        /// Localization package imports and exports.
        /// </summary>
        private static void ReadSeparated(string text, char separator, string source, TextScanResult result)
        {
            var rows = ParseSeparated(text, separator);
            if (rows.Count == 0) return;

            var header = rows[0];
            bool hasHeader = LooksLikeHeader(header);
            for (int r = hasHeader ? 1 : 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 0) continue;
                string key = row[0];
                for (int c = 1; c < row.Count; c++)
                {
                    if (string.IsNullOrWhiteSpace(row[c])) continue;
                    result.Entries.Add(new TextEntry
                    {
                        Key = key,
                        Value = row[c],
                        Locale = hasHeader && c < header.Count ? NormalizeLocale(header[c]) : null,
                        Source = source,
                    });
                }

                // A single-column file is a list of strings, not a table.
                if (row.Count == 1 && !string.IsNullOrWhiteSpace(key))
                {
                    result.Entries.Add(new TextEntry
                    {
                        Key = $"line {r + 1}", Value = key, Locale = null, Source = source,
                    });
                }
            }
        }

        /// <summary>
        /// RFC 4180 enough for real exports: quoted fields, doubled quotes
        /// inside them, and newlines inside quotes, which is how a translator's
        /// two-line string arrives and how a naive split loses half a table.
        /// </summary>
        internal static List<List<string>> ParseSeparated(string text, char separator)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, any = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') { field.Append(c); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }
                    quoted = false;
                    continue;
                }

                if (c == '"' && field.Length == 0) { quoted = true; any = true; continue; }
                if (c == separator) { row.Add(field.ToString()); field.Clear(); any = true; continue; }
                if (c == '\r') continue;
                if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    if (any || row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    any = false;
                    continue;
                }
                field.Append(c);
                any = true;
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// A header row is one whose cells all look like identifiers: no
        /// spaces, no sentence punctuation. Guessing wrong costs one row of
        /// characters in a charset, which is why this is allowed to guess.
        /// </summary>
        private static bool LooksLikeHeader(List<string> row)
        {
            if (row.Count < 2) return false;
            for (int i = 1; i < row.Count; i++)
            {
                string cell = row[i].Trim();
                if (cell.Length == 0 || cell.Length > 32) return false;
                foreach (char c in cell)
                    if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '(' && c != ')' && c != ' ')
                        return false;
            }
            return true;
        }

        /// <summary>
        /// Every string leaf in a JSON document, keyed by its path.
        ///
        /// Written by hand rather than deserialized into a shape, because the
        /// shape is whatever the project chose: i18next's nested objects, a flat
        /// key-value map, an array of dialogue records. The one thing they share
        /// is that the translatable text is the string leaves.
        /// </summary>
        internal static void ReadJson(string text, string source, TextScanResult result)
        {
            string fileLocale = LocaleFromFileName(source);
            int index = 0;
            var path = new List<string>();
            try
            {
                SkipSpace(text, ref index);
                ReadJsonValue(text, ref index, path, fileLocale, source, result, depth: 0);
            }
            catch (FormatException e)
            {
                result.Skipped.Add($"{source}: {e.Message}");
            }
        }

        private static void ReadJsonValue(string text, ref int i, List<string> path, string locale,
            string source, TextScanResult result, int depth)
        {
            if (depth > 64) throw new FormatException("nested past 64 levels");
            SkipSpace(text, ref i);
            if (i >= text.Length) return;

            switch (text[i])
            {
                case '{':
                    i++;
                    while (true)
                    {
                        SkipSpace(text, ref i);
                        if (i >= text.Length) throw new FormatException("unterminated object");
                        if (text[i] == '}') { i++; return; }
                        if (text[i] == ',') { i++; continue; }
                        if (text[i] != '"') throw new FormatException($"expected a key at offset {i}");

                        string key = ReadJsonString(text, ref i);
                        SkipSpace(text, ref i);
                        if (i >= text.Length || text[i] != ':') throw new FormatException($"expected ':' at offset {i}");
                        i++;

                        // A top-level key that is a locale code names the block
                        // below it, the other half of the two ways JSON
                        // localization files are written.
                        string inner = depth == 0 && IsLocaleCode(key) ? NormalizeLocale(key) : locale;
                        path.Add(key);
                        ReadJsonValue(text, ref i, path, inner, source, result, depth + 1);
                        path.RemoveAt(path.Count - 1);
                    }
                case '[':
                    i++;
                    int element = 0;
                    while (true)
                    {
                        SkipSpace(text, ref i);
                        if (i >= text.Length) throw new FormatException("unterminated array");
                        if (text[i] == ']') { i++; return; }
                        if (text[i] == ',') { i++; continue; }
                        path.Add(element.ToString());
                        ReadJsonValue(text, ref i, path, locale, source, result, depth + 1);
                        path.RemoveAt(path.Count - 1);
                        element++;
                    }
                case '"':
                    string value = ReadJsonString(text, ref i);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Entries.Add(new TextEntry
                        {
                            Key = string.Join(".", path),
                            Value = value,
                            Locale = locale,
                            Source = source,
                        });
                    }
                    return;
                default:
                    while (i < text.Length && text[i] != ',' && text[i] != '}' && text[i] != ']') i++;
                    return;
            }
        }

        private static string ReadJsonString(string text, ref int i)
        {
            if (text[i] != '"') throw new FormatException($"expected a string at offset {i}");
            i++;
            var builder = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (i >= text.Length) break;

                char escape = text[i++];
                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (i + 4 > text.Length) throw new FormatException("truncated \\u escape");
                        builder.Append((char)Convert.ToInt32(text.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: builder.Append(escape); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static void SkipSpace(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }

        private static void ReadLines(string text, string source, TextScanResult result)
        {
            string locale = LocaleFromFileName(source);
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                result.Entries.Add(new TextEntry
                {
                    Key = $"line {i + 1}", Value = line, Locale = locale, Source = source,
                });
            }
        }

        // -------------------------------------------------- Unity Localization

        /// <summary>
        /// Unity's own Localization package keeps its strings in ScriptableObject
        /// tables, not files, so they are read through the asset database, and
        /// through reflection, because the package is optional and this assembly
        /// must compile in a project that never installed it.
        /// </summary>
        private static void ScanLocalizationTables(string folder, TextScanResult result)
        {
            var tableType = Type.GetType("UnityEngine.Localization.Tables.StringTable, Unity.Localization");
            if (tableType == null) return;

            string[] guids;
            try
            {
                guids = AssetDatabase.FindAssets($"t:{tableType.Name}", new[] { folder.TrimEnd('/') });
            }
            catch (ArgumentException)
            {
                return; // folder outside Assets/, so the file scan already covered it
            }

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var table = AssetDatabase.LoadAssetAtPath(path, tableType);
                if (table == null) continue;
                result.FilesScanned++;

                string locale = null;
                var localeProperty = tableType.GetProperty("LocaleIdentifier");
                var identifier = localeProperty?.GetValue(table);
                if (identifier != null)
                {
                    var code = identifier.GetType().GetProperty("Code")?.GetValue(identifier) as string;
                    locale = NormalizeLocale(code);
                }

                if (!(table is System.Collections.IEnumerable rows)) continue;
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    var type = row.GetType();
                    string value = type.GetProperty("LocalizedValue")?.GetValue(row) as string
                        ?? type.GetProperty("Value")?.GetValue(row) as string;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    string key = type.GetProperty("Key")?.GetValue(row) as string
                        ?? type.GetProperty("KeyId")?.GetValue(row)?.ToString();
                    result.Entries.Add(new TextEntry
                    {
                        Key = key, Value = value, Locale = locale, Source = path,
                    });
                }
            }
        }

        // ------------------------------------------------------------- locales

        /// <summary>"en", "ko-KR", "zh_Hans" and friends; anything else is not a locale.</summary>
        public static bool IsLocaleCode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2 || text.Length > 12) return false;
            int letters = 0;
            foreach (char c in text)
            {
                if (char.IsLetter(c)) { letters++; continue; }
                if (c != '-' && c != '_') return false;
            }
            if (letters < 2) return false;

            // Two or three leading letters is the language subtag; a longer first
            // run is a word, and words are keys.
            int run = 0;
            while (run < text.Length && char.IsLetter(text[run])) run++;
            return run >= 2 && run <= 3;
        }

        public static string NormalizeLocale(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string trimmed = text.Trim().Replace('_', '-');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string LocaleFromFileName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (IsLocaleCode(name)) return NormalizeLocale(name);

            // "strings.ko-KR.json" and "dialogue_ko.txt" both name their locale
            // in the last segment; a file that does not is left unnamed rather
            // than assigned a guess.
            int separator = name.LastIndexOfAny(new[] { '.', '_', '-' });
            if (separator > 0 && separator < name.Length - 1)
            {
                string tail = name.Substring(separator + 1);
                if (IsLocaleCode(tail)) return NormalizeLocale(tail);
            }
            return null;
        }

        public static string ToProjectPath(string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            string root = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
            return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(root.Length)
                : normalized;
        }
    }
}
