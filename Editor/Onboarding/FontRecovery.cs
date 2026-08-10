using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// One font a project needs the file of, and everything known about it:
    /// which font assets asked for it, how much of the project is waiting, and
    /// where it might legally be found.
    ///
    /// One of these stands for one <em>source font file</em>, not one font
    /// asset. A project that baked Cairo-Bold at three sizes has three font
    /// assets and one missing <c>.ttf</c>, and asking somebody to find the same
    /// file three times is how a list of eighteen problems reads as unsolvable
    /// when it is really six.
    /// </summary>
    public sealed class FontRecoveryEntry
    {
        /// <summary>Family as the old asset named it: "Cairo".</summary>
        public string FamilyName;

        /// <summary>Style within that family: "Bold", or null when there is one face.</summary>
        public string StyleName;

        /// <summary>Family and style, the way a font file is usually named.</summary>
        public string FaceName => string.IsNullOrEmpty(StyleName)
            ? FamilyName
            : $"{FamilyName}-{StyleName}";

        /// <summary>The normalised name everything is deduplicated by.</summary>
        public string Key;

        /// <summary>File the user is looking for, e.g. <c>Cairo-Bold.ttf</c>.</summary>
        public string ExpectedFileName;

        /// <summary>Names of the font assets that all reduced to this one file.</summary>
        public readonly List<string> FontAssets = new List<string>();

        /// <summary>How many components are waiting on it.</summary>
        public int LabelCount;

        /// <summary>The scenes and prefabs those components live in.</summary>
        public readonly List<string> Containers = new List<string>();

        /// <summary>The OneText asset every one of those labels now points at.</summary>
        public OneFontAsset Placeholder;

        public string PlaceholderAssetPath;

        /// <summary>What the catalogue makes of this family. Never a guess.</summary>
        public FontSourceCandidate Candidate;

        /// <summary>
        /// True once somebody has asked for this family to be looked up, whether
        /// or not the lookup found anything. It is what stops the Hub offering
        /// the same request again, and the reason the offer exists at all is
        /// that the lookup is a network call and the scan that produced this
        /// entry made none.
        /// </summary>
        public bool Resolved;

        /// <summary>Atlas settings and character coverage read off the old asset.</summary>
        public OneFontRecovery Facts;

        /// <summary>True once somebody has supplied the font file.</summary>
        public bool Filled => Placeholder != null && !Placeholder.IsPlaceholder;

        public override string ToString() =>
            $"{FaceName}: {LabelCount:n0} label(s) in {Containers.Count:n0} container(s), " +
            $"wants {ExpectedFileName}" + (Filled ? " (filled)" : string.Empty);
    }

    /// <summary>
    /// Every font a migration could not convert, once each, with what it would
    /// take to convert it.
    ///
    /// This is the list a person acts on after the migration: it is deliberately
    /// shorter than the list of errors, because the errors are per label and the
    /// work is per font file.
    /// </summary>
    public sealed class FontRecoveryManifest
    {
        private readonly List<FontRecoveryEntry> _entries = new List<FontRecoveryEntry>();
        private readonly Dictionary<string, FontRecoveryEntry> _byKey =
            new Dictionary<string, FontRecoveryEntry>();
        private readonly Dictionary<string, FontRecoveryEntry> _byFontAsset =
            new Dictionary<string, FontRecoveryEntry>();

        /// <summary>The distinct source fonts, in the order they were first met.</summary>
        public IReadOnlyList<FontRecoveryEntry> Entries => _entries;

        public int Count => _entries.Count;

        /// <summary>Fonts still waiting for a file.</summary>
        public int Unfilled
        {
            get
            {
                int n = 0;
                foreach (var entry in _entries) if (!entry.Filled) n++;
                return n;
            }
        }

        /// <summary>Unfilled fonts the catalogue can fetch on request.</summary>
        public int Downloadable
        {
            get
            {
                int n = 0;
                foreach (var entry in _entries)
                    if (!entry.Filled && entry.Candidate.Match == FontSourceMatch.Download) n++;
                return n;
            }
        }

        /// <summary>Labels waiting on any font in this list.</summary>
        public int LabelCount
        {
            get
            {
                int n = 0;
                foreach (var entry in _entries) n += entry.LabelCount;
                return n;
            }
        }

        /// <summary>The entry a given font asset name was folded into, or null.</summary>
        public FontRecoveryEntry ForFontAsset(string fontAssetName) =>
            fontAssetName != null && _byFontAsset.TryGetValue(fontAssetName, out var entry)
                ? entry
                : null;

        public FontRecoveryEntry Find(string key) =>
            key != null && _byKey.TryGetValue(key, out var entry) ? entry : null;

        public string Summary() =>
            $"{Unfilled:n0} font(s) of {_entries.Count:n0} still need a file, wanted by " +
            $"{LabelCount:n0} component(s); {Downloadable:n0} can be downloaded from here";

        internal FontRecoveryEntry GetOrAdd(string key, System.Func<FontRecoveryEntry> make)
        {
            if (_byKey.TryGetValue(key, out var found)) return found;

            var entry = make();
            entry.Key = key;
            _byKey[key] = entry;
            _entries.Add(entry);
            return entry;
        }

        internal void Map(string fontAssetName, FontRecoveryEntry entry)
        {
            if (string.IsNullOrEmpty(fontAssetName)) return;
            _byFontAsset[fontAssetName] = entry;
            if (!entry.FontAssets.Contains(fontAssetName)) entry.FontAssets.Add(fontAssetName);
        }

        /// <summary>
        /// Forgets who is waiting, keeping the fonts and their placeholders.
        ///
        /// The dependency counts are recomputed from the report's findings every
        /// time the manifest is collected, so that collecting twice — once after
        /// the scan, once after the apply — does not report a project twice the
        /// size it is.
        /// </summary>
        internal void ResetDependencies()
        {
            foreach (var entry in _entries)
            {
                entry.LabelCount = 0;
                entry.Containers.Clear();
            }
        }
    }

    /// <summary>What one attempt to fill placeholders from a font file did.</summary>
    public struct FontRecoveryFill
    {
        /// <summary>The placeholders this file answered.</summary>
        public List<OneFontAsset> Filled;

        /// <summary>A sentence for the user, whether anything was filled or not.</summary>
        public string Message;

        public bool Any => Filled != null && Filled.Count > 0;
    }

    /// <summary>
    /// The way out of the dead end a baked font atlas leaves behind.
    ///
    /// A TextMesh Pro font asset holds a rendered atlas; OneText rasterises from
    /// the <c>.ttf</c>. When a project has the atlas and not the font — an asset
    /// pack that shipped one and not the other, a font file deleted after baking
    /// — there is genuinely nothing to convert, and until now the migration said
    /// so and left the label with no font. That is a correct answer and a
    /// useless one: on the project this was built against it was the answer for
    /// 6,717 of 8,912 components.
    ///
    /// So instead of a null, the migration leaves an asset. A placeholder
    /// <see cref="OneFontAsset"/> carries the family name, the old atlas's
    /// settings and the file name to look for, every label that used the old
    /// font points at it, and the project comes out of the migration with a
    /// reference graph that is complete and a list of files to find. Dropping
    /// one <c>.ttf</c> into the project fills one of those assets and every
    /// label pointing at it, at once, with nothing else to click.
    ///
    /// Nothing here reaches the network. <see cref="FontSourceCatalog"/> does
    /// that, for one font, when somebody asks it to.
    /// </summary>
    public static class FontRecovery
    {
        /// <summary>Where placeholders and downloaded fonts are kept.</summary>
        public const string RecoveryFolder = "Assets/OneText/Recovered Fonts";

        /// <summary>The finding both migration providers raise for this.</summary>
        public const string Rule = "font-source-missing";

        /// <summary>The note the migration adds when it leaves a placeholder behind.</summary>
        public const string RecoveredRule = "font-recovered";

        // ------------------------------------------------------- the manifest

        /// <summary>
        /// Reads a report and returns the fonts it needs files for, deduplicated
        /// by source font.
        ///
        /// Works off the findings rather than off anything the conversion did,
        /// so a scan — which converts nothing and writes nothing — produces the
        /// same list as an apply. Idempotent: call it after the scan to show the
        /// user what is coming, and again after the apply to pick up the
        /// placeholders that were created.
        /// </summary>
        public static FontRecoveryManifest Collect(MigrationReport report,
            bool createPlaceholders = false)
        {
            if (report == null) return new FontRecoveryManifest();

            var manifest = report.Recovery;
            manifest.ResetDependencies();

            // The project may have gained or lost assets since the last pass —
            // a font downloaded, a font asset deleted — and a lookup cache that
            // outlived that is a cache of the wrong answers.
            Cache.Clear();

            foreach (var finding in report.Findings)
            {
                if (finding.Rule != Rule) continue;

                string name = NamedFont(finding.Message);
                if (name == null) continue;

                var entry = Entry(manifest, name);
                entry.LabelCount++;
                if (!string.IsNullOrEmpty(finding.Container) &&
                    !entry.Containers.Contains(finding.Container))
                {
                    entry.Containers.Add(finding.Container);
                }
            }

            if (createPlaceholders)
            {
                foreach (var entry in manifest.Entries) EnsurePlaceholder(entry, report);
                AssetDatabase.SaveAssets();
            }

            return manifest;
        }

        /// <summary>
        /// The entry for a font asset name, made if this is the first sight of
        /// it, and shared with every other asset that reduces to the same file.
        /// </summary>
        internal static FontRecoveryEntry Entry(FontRecoveryManifest manifest, string fontAssetName)
        {
            var known = manifest.ForFontAsset(fontAssetName);
            if (known != null) return known;

            var facts = Describe(fontAssetName, out string family, out string style);
            string key = FontSourceCatalog.Key(family + style);

            var entry = manifest.GetOrAdd(key, () => new FontRecoveryEntry
            {
                FamilyName = family,
                StyleName = style,
                ExpectedFileName = facts.ExpectedFileName,
                Facts = facts,
                Candidate = FontSourceCatalog.Match(family),
            });

            manifest.Map(fontAssetName, entry);
            return entry;
        }

        /// <summary>
        /// The font asset a finding is about, pulled out of its message.
        ///
        /// Both migration providers name the asset the same way, in quotes, at
        /// the front of the sentence — they are adapters that report in prose
        /// and the prose is the only structured thing they carry. A message that
        /// does not quote a name, or quotes a file path (the other way this rule
        /// fires, when a font file is present and unreadable), is not a missing
        /// source and comes back null rather than as a font nobody asked for.
        /// </summary>
        public static string NamedFont(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            int open = message.IndexOf('\'');
            if (open < 0) return null;
            int close = message.IndexOf('\'', open + 1);
            if (close <= open + 1) return null;

            string name = message.Substring(open + 1, close - open - 1);
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return null;

            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".ttf" || extension == ".otf" || extension == ".ttc") return null;

            return name;
        }

        // ------------------------------------------------------ what is known

        /// <summary>
        /// Everything a baked font asset still says about the font it came from.
        ///
        /// Read through <c>SerializedObject</c> rather than through TextMesh
        /// Pro's own types, for the same reason the rest of this module does: it
        /// has to work in a project that removed the package an hour ago, and
        /// this file compiles where TMP does not. When the asset cannot be found
        /// or read at all — no TMP, a missing script, a name that matches
        /// nothing — the name itself is still worth something, and the family
        /// and style are taken from it.
        /// </summary>
        public static OneFontRecovery Describe(string fontAssetName, out string family,
            out string style)
        {
            ParseFace(fontAssetName, out family, out style);

            var recovery = new OneFontRecovery
            {
                StyleName = style,
                ExpectedFileName = FileNameFor(family, style, null),
            };

            string assetPath = FindAsset(fontAssetName);
            if (assetPath == null) return recovery;

            recovery.RecoveredFrom = assetPath;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return recovery;

            SerializedObject serialized;
            try { serialized = new SerializedObject(asset); }
            catch (System.Exception) { return recovery; }

            // The face info is the font's own account of itself, written down
            // when the atlas was baked, and it beats anything a file name can be
            // parsed into: it knows that "NotoSansKR-Bold SDF" is the Bold of
            // Noto Sans KR and not a family called "NotoSansKR-Bold".
            string faceFamily = String(serialized, "m_FaceInfo.m_FamilyName");
            string faceStyle = String(serialized, "m_FaceInfo.m_StyleName");
            if (!string.IsNullOrWhiteSpace(faceFamily))
            {
                family = faceFamily.Trim();
                style = string.IsNullOrWhiteSpace(faceStyle) || faceStyle.Trim() == "Regular"
                    ? style
                    : faceStyle.Trim();
                recovery.StyleName = style;
            }

            recovery.PointSize = Float(serialized, "m_FaceInfo.m_PointSize");
            recovery.Padding = Int(serialized, "m_AtlasPadding");
            recovery.AtlasWidth = Int(serialized, "m_AtlasWidth");
            recovery.AtlasHeight = Int(serialized, "m_AtlasHeight");

            ReadCharacters(serialized, ref recovery);

            // The path the asset remembers is the best answer to "what is the
            // file called": the project once had it, and the name it had then is
            // the name it has wherever the user finds it again.
            string remembered = String(serialized, "m_SourceFontFilePath");
            recovery.ExpectedFileName = FileNameFor(family, style,
                string.IsNullOrEmpty(remembered) ? null : Path.GetFileName(remembered));

            return recovery;
        }

        /// <summary>
        /// The characters the old atlas held, as ranges.
        ///
        /// Not used to build anything — OneText rasterises what a string needs,
        /// when it needs it — but it is the answer to "was this font subset to
        /// 200 glyphs or does it carry the whole of KS X 1001", which decides
        /// whether the file somebody finds is the right one.
        /// </summary>
        private static void ReadCharacters(SerializedObject serialized, ref OneFontRecovery recovery)
        {
            var table = serialized.FindProperty("m_CharacterTable");
            if (table == null || !table.isArray) return;

            recovery.CharacterCount = table.arraySize;

            // A CJK atlas holds tens of thousands of these and every one costs a
            // serialized-property lookup. The ranges are for a person to read,
            // and nobody reads twenty thousand of them.
            int read = Mathf.Min(table.arraySize, 8192);
            var codes = new List<int>(read);
            for (int i = 0; i < read; i++)
            {
                var unicode = table.GetArrayElementAtIndex(i).FindPropertyRelative("m_Unicode");
                if (unicode != null) codes.Add(unicode.intValue);
            }
            if (codes.Count == 0) return;

            codes.Sort();
            var ranges = new StringBuilder();
            int start = codes[0], previous = codes[0], written = 0;
            for (int i = 1; i <= codes.Count; i++)
            {
                bool end = i == codes.Count;
                if (!end && (codes[i] == previous || codes[i] == previous + 1))
                {
                    previous = codes[i];
                    continue;
                }

                if (written == 24)
                {
                    ranges.Append(",…");
                    break;
                }
                if (written > 0) ranges.Append(',');
                ranges.Append(start == previous
                    ? $"{start:X4}"
                    : $"{start:X4}-{previous:X4}");
                written++;

                if (end) break;
                start = previous = codes[i];
            }

            recovery.CharacterRanges = ranges.ToString();
        }

        /// <summary>
        /// A font asset's name split into a family and a style.
        ///
        /// Font assets are named after the file they were baked from plus
        /// whatever the person baking them was tracking — "Cairo-Bold SDF",
        /// "Pretendard-Regular SDF 48", "LINESeedSans_A_Rg Atlas". The tail is
        /// noise, the style is worth keeping separately (it is what tells you
        /// which of four files to look for), and what is left is the family the
        /// catalogue is asked about. Only the tail is ever stripped: a family
        /// really called "Black Han Sans" starts with a weight word and must
        /// survive this intact.
        ///
        /// A style written as two words comes off as two words. "Extra" is not a
        /// style on its own, so taking only the "Bold" out of "Sen-ExtraBold"
        /// used to leave a family called "Sen Extra", which matches no
        /// catalogue entry and none of the other three faces of Sen the same
        /// project has — one missing font turning into two, neither of them
        /// findable.
        /// </summary>
        public static void ParseFace(string fontAssetName, out string family, out string style)
        {
            family = fontAssetName ?? string.Empty;
            style = null;

            var tokens = Tokenise(fontAssetName);
            if (tokens.Count == 0) return;

            var styles = new List<string>();
            while (tokens.Count > 1)
            {
                string last = tokens[tokens.Count - 1].ToLowerInvariant();
                if (IsStyle(last))
                {
                    styles.Insert(0, Capitalise(tokens[tokens.Count - 1]));
                    tokens.RemoveAt(tokens.Count - 1);

                    if (tokens.Count > 1 && IsWeightModifier(tokens[tokens.Count - 1].ToLowerInvariant()))
                    {
                        styles.Insert(0, Capitalise(tokens[tokens.Count - 1]));
                        tokens.RemoveAt(tokens.Count - 1);
                    }
                    continue;
                }

                if (!IsNoise(last)) break;
                tokens.RemoveAt(tokens.Count - 1);
            }

            family = string.Join(" ", tokens);
            if (styles.Count > 0) style = string.Join(string.Empty, styles);
        }

        private static readonly HashSet<string> StyleWords = new HashSet<string>
        {
            "thin", "hairline", "extralight", "ultralight", "light", "regular", "normal", "book",
            "text", "medium", "semibold", "demibold", "demi", "bold", "extrabold", "ultrabold",
            "black", "heavy", "extrablack", "ultrablack", "italic", "oblique",
        };

        private static readonly HashSet<string> NoiseWords = new HashSet<string>
        {
            "sdf", "atlas", "static", "dynamic", "font", "fontasset", "asset", "tmp", "tmppro",
            "pro", "ttf", "otf", "ttc", "outline", "shadow", "glow", "copy", "new", "mobile",
        };

        /// <summary>
        /// Words that are half of a style and never one on their own, so that
        /// they are taken with the word they modify rather than left behind in
        /// the family name. See <see cref="FontWeightNames"/>, which reads the
        /// same pairs as weights.
        /// </summary>
        private static readonly HashSet<string> WeightModifiers = new HashSet<string>
        {
            "extra", "ultra", "semi", "demi",
        };

        private static bool IsStyle(string token) => StyleWords.Contains(token);

        private static bool IsWeightModifier(string token) => WeightModifiers.Contains(token);

        private static bool IsNoise(string token)
        {
            if (NoiseWords.Contains(token)) return true;
            foreach (char c in token) if (!char.IsDigit(c)) return false;
            return token.Length > 0;
        }

        /// <summary>
        /// Words, from a name that may use spaces, hyphens, underscores or
        /// nothing at all to separate them. <c>LINESeedSans</c> is three words
        /// and <c>SDF32</c> is two, and both of those matter to what is stripped.
        /// </summary>
        internal static List<string> Tokenise(string name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name)) return tokens;

            var current = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(tokens, current);
                    continue;
                }

                if (current.Length > 0)
                {
                    char previous = current[current.Length - 1];
                    bool caseBreak = char.IsUpper(c) && char.IsLetter(previous) &&
                                     !char.IsUpper(previous);
                    // "LINESeed": the break is before the capital that starts a
                    // lower-case run, not at every capital, or the acronym
                    // becomes four one-letter words.
                    bool acronymBreak = char.IsUpper(c) && char.IsUpper(previous) &&
                                        i + 1 < name.Length && char.IsLower(name[i + 1]);
                    bool digitBreak = char.IsDigit(c) != char.IsDigit(previous);
                    if (caseBreak || acronymBreak || digitBreak) Flush(tokens, current);
                }
                current.Append(c);
            }
            Flush(tokens, current);
            return tokens;
        }

        private static void Flush(List<string> tokens, StringBuilder current)
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }

        private static string Capitalise(string token) =>
            token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token.Substring(1);

        private static string FileNameFor(string family, string style, string remembered)
        {
            if (!string.IsNullOrEmpty(remembered)) return remembered;
            string face = string.IsNullOrEmpty(style)
                ? family
                : $"{family.Replace(" ", string.Empty)}-{style}";
            return face.Replace(" ", string.Empty) + ".ttf";
        }

        // ----------------------------------------------------- the placeholder

        /// <summary>
        /// The placeholder for a font asset name, made once and reused for
        /// every label that names it.
        ///
        /// Called from the migration while it is writing components, which is
        /// the moment the reference has to exist: a label converted with a null
        /// font is a label somebody has to find again later.
        /// </summary>
        public static OneFontAsset PlaceholderFor(MigrationReport report, string fontAssetName)
        {
            if (report == null || string.IsNullOrEmpty(fontAssetName)) return null;

            var entry = Entry(report.Recovery, fontAssetName);
            return EnsurePlaceholder(entry, report);
        }

        /// <summary>
        /// The placeholder for a font known by the file it should have been
        /// read from, rather than by the name of the baked asset that wanted
        /// it.
        ///
        /// The other half of <see cref="PlaceholderFor"/>. That one answers "a
        /// TextMesh Pro font asset shipped its atlas and not its font"; this
        /// one answers "the font asset named a file and the file is not in the
        /// project" — deleted, or in a package this project no longer has, or
        /// unreadable. Until now that second case converted to a null and the
        /// labels lost the reference, which is the one outcome this module
        /// exists to prevent, and the odd one to have kept: the file name is
        /// known exactly here, so the placeholder can name what to go and find
        /// instead of guessing it from a family and a style.
        /// </summary>
        public static OneFontAsset PlaceholderForFile(MigrationReport report, string sourcePath)
        {
            if (report == null || string.IsNullOrEmpty(sourcePath)) return null;

            string fileName = Path.GetFileName(sourcePath);
            // Deduplicated by face name like everything else, so a project that
            // lost one Cairo-Bold.ttf two hundred labels wanted gets one
            // placeholder and one thing to go and find.
            var entry = Entry(report.Recovery, Path.GetFileNameWithoutExtension(sourcePath));

            // Known, not parsed. Entry() derives an expected file name from the
            // family and style it reads out of the name, which is a good guess
            // and this is not a guess.
            entry.ExpectedFileName = fileName;
            var facts = entry.Facts;
            facts.ExpectedFileName = fileName;
            entry.Facts = facts;

            return EnsurePlaceholder(entry, report);
        }

        /// <summary>
        /// Creates the placeholder asset for an entry, or picks up the one a
        /// previous run left.
        ///
        /// Reusing it is what makes the migration idempotent and what makes
        /// filling it work: converting the same project twice must not produce
        /// two Cairo-Bolds, and a placeholder somebody has already dropped the
        /// font into must come back as the filled font it now is rather than
        /// being blanked back to a placeholder.
        /// </summary>
        public static OneFontAsset EnsurePlaceholder(FontRecoveryEntry entry,
            MigrationReport report = null)
        {
            if (entry == null) return null;
            if (entry.Placeholder != null) return entry.Placeholder;

            string assetPath = $"{RecoveryFolder}/{SafeName(entry.FaceName)} Font.asset";
            var existing = AssetDatabase.LoadAssetAtPath<OneFontAsset>(assetPath);
            if (existing != null)
            {
                entry.Placeholder = existing;
                entry.PlaceholderAssetPath = assetPath;
                return existing;
            }

            if (!FontSourceCatalog.EnsureFolder(RecoveryFolder, out string error))
            {
                Debug.LogError($"OneText: {error}");
                return null;
            }

            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.InitializePlaceholder(entry.FamilyName, entry.Facts);
            AssetDatabase.CreateAsset(asset, assetPath);
            EditorUtility.SetDirty(asset);

            entry.Placeholder = asset;
            entry.PlaceholderAssetPath = assetPath;
            if (report != null) report.PlaceholdersCreated++;
            return asset;
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
                builder.Append(System.Array.IndexOf(invalid, c) >= 0 ? '-' : c);
            return builder.ToString();
        }

        // ------------------------------------------------------------ filling

        /// <summary>Every placeholder in the project that is still waiting.</summary>
        public static List<OneFontAsset> Waiting()
        {
            var waiting = new List<OneFontAsset>();
            foreach (string guid in AssetDatabase.FindAssets("t:OneFontAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<OneFontAsset>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && asset.IsPlaceholder) waiting.Add(asset);
            }
            return waiting;
        }

        /// <summary>
        /// Puts a font file into one placeholder. Every label already pointing
        /// at it is now drawing with that font, with nothing else to do.
        /// </summary>
        public static bool Fill(OneFontAsset placeholder, string fontFilePath) =>
            Fill(placeholder, fontFilePath, out _);

        /// <summary>
        /// The same, saying which axes the face's name turned out to be worth
        /// setting — null when the name said nothing or the file could not have
        /// obeyed it.
        /// </summary>
        public static bool Fill(OneFontAsset placeholder, string fontFilePath, out string variations)
        {
            variations = null;
            if (placeholder == null || string.IsNullOrEmpty(fontFilePath)) return false;

            // Read before the fill, because filling replaces the family name
            // with the one the font file declares: the variable Pretendard calls
            // itself "Pretendard", and the face this asset stands for is
            // Pretendard-ExtraBold. Afterwards there is nothing left to read the
            // weight out of.
            string faceName = FaceNameOf(placeholder);

            if (!OneFontAssetCreator.FillFromFontFile(placeholder, fontFilePath)) return false;

            variations = ApplyInferredVariations(placeholder, fontFilePath, faceName);

            if (AssetDatabase.Contains(placeholder)) AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>
        /// The face a font asset stands for, family and style together, which is
        /// the string the weight is written in.
        /// </summary>
        private static string FaceNameOf(OneFontAsset font)
        {
            string family = font.FamilyName ?? string.Empty;
            string style = font.Recovery.StyleName;
            return string.IsNullOrEmpty(style) ? family : family + "-" + style;
        }

        /// <summary>
        /// Sets the weight the face was named for on the asset that stands for
        /// it, so that every label pointing at it inherits it.
        ///
        /// This is the other half of one variable file answering four
        /// placeholders. Without it the file lands, six thousand labels come
        /// back, and the ones that were extra bold render at the file's default
        /// weight — not missing, not empty, just a different design than the one
        /// that shipped, which is the kind of wrong nobody files a bug about.
        ///
        /// It runs on both paths and cannot do otherwise, because both of them
        /// are this method's caller: the download button fills through
        /// <see cref="Recover"/>, and a person dragging a <c>.ttf</c> into the
        /// Project window fills through <see cref="FontRecoveryImporter"/>.
        ///
        /// A static face gets nothing and is told nothing: it is already the
        /// weight it is, and the name saying "Bold" is the file saying "Bold".
        /// </summary>
        private static string ApplyInferredVariations(OneFontAsset font, string fontFilePath,
            string faceName)
        {
            var guess = FontWeightNames.Infer(faceName);
            if (!guess.Found) return null;

            var axes = VariationAxes(fontFilePath);
            if (axes.Length == 0) return null;

            var variations = FontWeightNames.Variations(guess, axes);
            if (variations.Length == 0) return null;

            font.SetBaseVariations(variations);
            EditorUtility.SetDirty(font);
            return FontWeightNames.Describe(variations);
        }

        /// <summary>
        /// Works out which placeholders a font file answers, and fills them.
        ///
        /// Two rules, in order, and the order is the point. An exact name — the
        /// file is called what a placeholder is waiting for — fills that one and
        /// nothing else. Otherwise the font's own family name is compared with
        /// the families still waiting, which is what lets one variable
        /// <c>Cairo.ttf</c> answer Cairo-Bold and Cairo-Regular at once.
        ///
        /// And that second rule stops where it would start guessing: a family
        /// with several faces waiting, answered by a file with no weight axis,
        /// fills nothing. Making the bold labels regular is not a smaller
        /// mistake than leaving them unfilled, it is just a quieter one.
        /// </summary>
        public static FontRecoveryFill Fill(string fontFilePath)
        {
            var result = new FontRecoveryFill { Filled = new List<OneFontAsset>() };
            if (string.IsNullOrEmpty(fontFilePath) || !File.Exists(fontFilePath))
            {
                result.Message = $"There is no file at {fontFilePath}.";
                return result;
            }

            var waiting = Waiting();
            if (waiting.Count == 0)
            {
                result.Message = "No OneText font is waiting for a file.";
                return result;
            }

            string fileName = Path.GetFileName(fontFilePath);
            string baseName = FontSourceCatalog.Key(Path.GetFileNameWithoutExtension(fontFilePath));

            var exact = new List<OneFontAsset>();
            foreach (var placeholder in waiting)
            {
                string expected = placeholder.Recovery.ExpectedFileName;
                bool named = !string.IsNullOrEmpty(expected) &&
                             string.Equals(expected, fileName,
                                 System.StringComparison.OrdinalIgnoreCase);
                // Either the file is called what this placeholder asked for, or
                // it is called what the face is called — "Cairo-Bold.ttf" for
                // the Bold of Cairo — which is the same answer written the way
                // a font vendor writes it.
                bool sameFace = FontSourceCatalog.Key(placeholder.FamilyName +
                                                      placeholder.Recovery.StyleName) == baseName;
                if (named || sameFace) exact.Add(placeholder);
            }

            if (exact.Count > 0) return FillAll(exact, fontFilePath, result);

            string family = OneFontAssetCreator.FamilyNameOf(fontFilePath);
            string familyKey = FontSourceCatalog.Key(family);
            var relatives = new List<OneFontAsset>();
            foreach (var placeholder in waiting)
                if (FontSourceCatalog.Key(placeholder.FamilyName) == familyKey)
                    relatives.Add(placeholder);

            if (relatives.Count == 0)
            {
                result.Message = $"{fileName} is '{family}', and nothing is waiting for that " +
                                 "family. Assign it to a label by hand if it is the one you want.";
                return result;
            }

            if (relatives.Count > 1 && !HasWeightAxis(fontFilePath))
            {
                var names = new List<string>();
                foreach (var placeholder in relatives) names.Add(placeholder.name);
                result.Message = $"{fileName} is '{family}', and {relatives.Count} faces of that " +
                                 $"family are waiting ({string.Join(", ", names)}). This file has " +
                                 "no weight axis, so it cannot be all of them and nothing was " +
                                 "filled. Rename it to the face it is, or assign it by hand.";
                return result;
            }

            return FillAll(relatives, fontFilePath, result);
        }

        private static FontRecoveryFill FillAll(List<OneFontAsset> placeholders, string fontFilePath,
            FontRecoveryFill result)
        {
            var names = new List<string>();
            var weighted = new List<string>();

            foreach (var placeholder in placeholders)
            {
                if (!Fill(placeholder, fontFilePath, out string variations)) continue;
                result.Filled.Add(placeholder);
                names.Add(placeholder.name);
                if (variations != null) weighted.Add($"{placeholder.name} at {variations}");
            }

            if (result.Filled.Count == 0)
            {
                result.Message = $"{Path.GetFileName(fontFilePath)} could not be read as a font.";
                return result;
            }

            result.Message =
                $"{Path.GetFileName(fontFilePath)} filled {string.Join(", ", names)}. Every " +
                "label pointing at " + (result.Filled.Count == 1 ? "it" : "them") +
                " is drawing with it now." +
                // Said explicitly, because it is a decision this made on the
                // user's behalf from a font's name. If the answer is wrong, the
                // sentence is where they find out.
                (weighted.Count > 0
                    ? " The weight each face was named for is set on its own asset: " +
                      string.Join("; ", weighted) + ". Anything a label sets itself still wins."
                    : string.Empty);
            return result;
        }

        /// <summary>
        /// Whether a font file carries a weight axis, read from its own tables
        /// rather than by loading it. It decides whether one file may stand for
        /// four faces.
        /// </summary>
        public static bool HasWeightAxis(string fontFilePath)
        {
            foreach (var axis in VariationAxes(fontFilePath))
                if (axis.Tag == "wght") return true;
            return false;
        }

        /// <summary>
        /// The variation axes a font file declares, with their ranges, read out
        /// of its own <c>fvar</c> table.
        ///
        /// <c>fvar</c> is a table directory entry and a list of axis records
        /// that start with a tag, which is forty lines of reading and no
        /// rasteriser, no native call and no cost worth measuring. Reading it
        /// here rather than through the loaded font matters for when it is
        /// needed: the recovery has to know what a file can do while deciding
        /// what to write into an asset, which is before anybody has a reason to
        /// parse the face.
        ///
        /// An empty array means a static font, an unreadable one, or one whose
        /// tables are not laid out the way the specification says. All three are
        /// the same answer to the only question being asked — is there an axis
        /// here I may move — and none of them is worth an exception.
        /// </summary>
        public static FontAxis[] VariationAxes(string fontFilePath)
        {
            try
            {
                return VariationAxes(File.ReadAllBytes(fontFilePath));
            }
            catch (System.Exception)
            {
                return System.Array.Empty<FontAxis>();
            }
        }

        /// <summary>The same, from bytes already in hand.</summary>
        public static FontAxis[] VariationAxes(byte[] font)
        {
            if (font == null || font.Length < 12) return System.Array.Empty<FontAxis>();

            int tables = (font[4] << 8) | font[5];
            int fvar = -1;
            for (int i = 0; i < tables; i++)
            {
                int record = 12 + i * 16;
                if (record + 16 > font.Length) return System.Array.Empty<FontAxis>();
                if (font[record] == 'f' && font[record + 1] == 'v' &&
                    font[record + 2] == 'a' && font[record + 3] == 'r')
                {
                    fvar = (font[record + 8] << 24) | (font[record + 9] << 16) |
                           (font[record + 10] << 8) | font[record + 11];
                    break;
                }
            }
            if (fvar < 0 || fvar + 16 > font.Length) return System.Array.Empty<FontAxis>();

            int axesOffset = fvar + ((font[fvar + 4] << 8) | font[fvar + 5]);
            int axisCount = (font[fvar + 8] << 8) | font[fvar + 9];
            int axisSize = (font[fvar + 10] << 8) | font[fvar + 11];

            // An axis record is a tag and three 16.16 fixed values and two
            // identifiers. A file claiming a smaller one is not one to read
            // ranges out of.
            if (axisSize < 20 || axisCount <= 0) return System.Array.Empty<FontAxis>();

            var axes = new List<FontAxis>(axisCount);
            for (int i = 0; i < axisCount; i++)
            {
                int at = axesOffset + i * axisSize;
                if (at < 0 || at + 20 > font.Length) break;

                var tag = new StringBuilder(4);
                for (int c = 0; c < 4; c++) tag.Append((char)font[at + c]);

                axes.Add(new FontAxis(tag.ToString(), Fixed(font, at + 4), Fixed(font, at + 8),
                    Fixed(font, at + 12)));
            }
            return axes.ToArray();
        }

        /// <summary>An OpenType 16.16 fixed-point number.</summary>
        private static float Fixed(byte[] font, int at) =>
            ((font[at] << 24) | (font[at + 1] << 16) | (font[at + 2] << 8) | font[at + 3]) / 65536f;

        // ---------------------------------------------------------- downloads

        /// <summary>
        /// The whole of one recovery: fetch the font the catalogue named, check
        /// it, put it in the project, and fill the placeholders it answers.
        ///
        /// This is the button. It runs for one font, after its licence has been
        /// shown, and every way it can fail comes back as a sentence rather than
        /// as an exception.
        /// </summary>
        public static FontSourceDownload Recover(FontRecoveryEntry entry)
        {
            if (entry == null)
            {
                return new FontSourceDownload
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = "There is no font to recover.",
                };
            }

            FontSourceDownload download;

            // The importer below would fill the placeholders the moment the
            // downloaded file lands, which is right when a person drops a file
            // in and wrong here: this method has to be able to say what it did,
            // and it cannot say that if something else already did it.
            Suspended = true;
            try { download = FontSourceCatalog.Download(entry.Candidate); }
            finally { Suspended = false; }

            if (!download.Ok) return download;

            var fill = Fill(download.FontPath);
            download.Message += " " + fill.Message;
            return download;
        }

        /// <summary>
        /// Asks the Google Fonts repository what it has for one entry's family,
        /// and keeps the answer on the entry.
        ///
        /// The scan cannot do this and must not: it is a network request, and a
        /// project with eighteen unrecoverable fonts would make eighteen of them
        /// before showing anybody anything. So the manifest comes out of the
        /// scan knowing only what the hand-written catalogue could tell it
        /// offline, and this is the button next to one row.
        ///
        /// Worth pressing even when the answer turns out to be "download it
        /// yourself": what comes back names the family as the repository spells
        /// it, the licence, the page, and the file — which is most of the work
        /// of finding it by hand, and the drag-and-drop path is the one most of
        /// these fonts will come back through.
        /// </summary>
        public static FontSourceResolution Resolve(FontRecoveryEntry entry)
        {
            if (entry == null)
            {
                return new FontSourceResolution
                {
                    Outcome = FontSourceOutcome.NoCandidate,
                    Message = "There is no font to look up.",
                };
            }

            var resolution = FontSourceCatalog.Resolve(entry.FamilyName);
            entry.Resolved = true;
            if (resolution.Ok) entry.Candidate = resolution.Candidate;
            return resolution;
        }

        /// <summary>
        /// Set while a recovery is filling placeholders itself, so the import
        /// hook stays out of the way. See <see cref="Recover"/>.
        /// </summary>
        internal static bool Suspended;

        // ------------------------------------------------------- small tools

        private static string FindAsset(string fontAssetName)
        {
            if (string.IsNullOrEmpty(fontAssetName)) return null;
            if (Cache.TryGetValue(fontAssetName, out string cached)) return cached;

            string found = null;
            foreach (string guid in AssetDatabase.FindAssets($"\"{fontAssetName}\""))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetExtension(path) != ".asset") continue;
                if (Path.GetFileNameWithoutExtension(path) != fontAssetName) continue;
                found = path;
                break;
            }

            Cache[fontAssetName] = found;
            return found;
        }

        /// <summary>
        /// Name to asset path, for one editor session. A project with eighteen
        /// broken fonts and 6,717 labels asks the same question thousands of
        /// times, and <c>FindAssets</c> is not free.
        /// </summary>
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        private static string String(SerializedObject serialized, string path)
        {
            var property = serialized.FindProperty(path);
            return property != null && property.propertyType == SerializedPropertyType.String
                ? property.stringValue
                : null;
        }

        private static float Float(SerializedObject serialized, string path)
        {
            var property = serialized.FindProperty(path);
            return property != null && property.propertyType == SerializedPropertyType.Float
                ? property.floatValue
                : 0f;
        }

        private static int Int(SerializedObject serialized, string path)
        {
            var property = serialized.FindProperty(path);
            return property != null && property.propertyType == SerializedPropertyType.Integer
                ? property.intValue
                : 0;
        }
    }

    /// <summary>
    /// The other half of "drop the font in and it works": noticing that somebody
    /// did.
    ///
    /// A placeholder is only useful if filling it is one action, and the action
    /// people already know is dragging a <c>.ttf</c> into the Project window.
    /// This watches for exactly that and fills the placeholder that was waiting
    /// for it — never one that already has a font, never on a guess (see
    /// <see cref="FontRecovery.Fill(string)"/> for where it stops), and always
    /// with a log line naming what it did, because an import that silently
    /// changes an asset is a mystery to whoever finds it later.
    /// </summary>
    public sealed class FontRecoveryImporter : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom)
        {
            if (FontRecovery.Suspended) return;

            foreach (string path in imported)
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".ttf" && extension != ".otf" && extension != ".ttc") continue;

                // Cheap, and it has to be: this runs on every import in the
                // project, and the common case is a project with no placeholders
                // at all, which costs one asset search and stops.
                var waiting = FontRecovery.Waiting();
                if (waiting.Count == 0) return;

                var fill = FontRecovery.Fill(path);
                if (fill.Any) Debug.Log($"OneText: {fill.Message}");
            }
        }
    }
}
