using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>
    /// Word lists for the scripts that write no spaces, imported as assets, with
    /// the number that says whether they are doing anything.
    ///
    /// M10 left Thai correct in mechanism and thin in data: the trie, the
    /// segmenter and the ICU file format all work, and what ships built in is
    /// about ninety words. The gap is invisible from the outside; a dictionary
    /// breaker does not crash when it is short of words, it wraps mid-word in a
    /// language nobody on the team reads. So this section is built around
    /// coverage against the project's own strings, before and after: "11% ->
    /// 99.2%" is the only honest way to report this to a team that cannot read
    /// the text.
    /// </summary>
    public sealed class HubDictionariesTab : HubSection
    {
        private static readonly string[] Scripts = { "Thai", "Lao", "Khmer", "Myanmar" };

        private string _script = "Thai";
        private string _notice =
            "Word list from the ICU project (Unicode licence). See THIRD-PARTY-NOTICES.md.";
        private readonly Dictionary<string, float> _coverageBefore = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _coverageAfter = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _sampleSize = new Dictionary<string, int>();
        private bool _measured;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Dictionaries;

        public override string Title => "Dictionaries";

        public override string Eyebrow => "Where words end";

        public override string Lede =>
            "Thai, Lao, Khmer and Burmese put no spaces between words, so the line-breaking " +
            "standard defers to a dictionary. Without a good one, text wraps in the middle of " +
            "words and nobody on the team can see it.";

        public override string NavHint => "Thai, Lao, Khmer, Burmese";

        public override string NavGroup => "What gets baked";

        public override string BadgeText
        {
            get
            {
                if (!_measured) return null;
                foreach (var pair in _coverageAfter)
                    if (pair.Value < TextDoctor.MinimumDictionaryCoverage) return "THIN";
                return "OK";
            }
        }

        public override HubTone BadgeTone => BadgeText == "THIN" ? HubTone.Warn : HubTone.Good;

        protected override void Compose(VisualElement content)
        {
            content.Add(InstalledCard());
            content.Add(BundledCard());
            content.Add(CoverageCard());
            content.Add(ImportCard());
        }

        // ------------------------------------------------------------- bundled

        /// <summary>The four ICU lists that ship inside the package, by file.</summary>
        private static readonly (string File, string Script)[] Bundled =
        {
            ("thaidict.txt", "Thai"),
            ("laodict.txt", "Lao"),
            ("khmerdict.txt", "Khmer"),
            ("burmesedict.txt", "Myanmar"),
        };

        /// <summary>
        /// What the install button promises, so a test can check the package
        /// still ships it.
        /// </summary>
        public static IReadOnlyList<(string File, string Script)> BundledLists => Bundled;

        /// <summary>Where the package keeps its word lists, or null if it does not.</summary>
        public static string BundledWordListFolder() => BundledFolder();

        /// <summary>
        /// The Package Manager's sample import, as a button that is where the
        /// question is asked.
        ///
        /// The lists have always been in the package: 4.2 MB of ICU word data
        /// under <c>Samples~</c>, which Unity hides from the project until
        /// somebody finds the Samples tab in the Package Manager and presses
        /// Import. That is two windows away from the screen that says Thai is
        /// 40% segmented, and the number of people who make that journey is the
        /// number of people who already knew. So the button is here, it copies
        /// out of the package rather than off a network, and it registers what
        /// it makes for builds.
        /// </summary>
        private VisualElement BundledCard()
        {
            int installed = InstalledFromBundle();
            var card = HubUI.MakeCard("Word lists",
                "The full ICU lists for all four scripts, bundled inside the package; nothing is " +
                "downloaded.");

            if (installed == Bundled.Length)
            {
                card.Add(HubUI.Notice(
                    "All four word lists are installed and registered for builds.", HubTone.Good));
                card.Add(HubUI.Quiet("Reinstall", () => Install(true)));
                return card.Root;
            }

            if (BundledFolder() == null)
            {
                card.Add(HubUI.Notice(
                    "The bundled lists are not on disk next to this package. Install the " +
                    "\"Word-break dictionaries\" sample from the Package Manager instead: " +
                    "Window > Package Manager > OneText > Samples.", HubTone.Warn));
                return card.Root;
            }

            if (installed > 0)
                card.Add(HubUI.Notice(
                    $"{installed} of {Bundled.Length} installed. The rest are one click away.",
                    HubTone.Warn));

            card.Add(HubUI.Primary("Install word lists (4.2 MB)", () => Install(false)));
            card.Add(HubUI.Text(
                "Thai, Lao, Khmer and Burmese, stored compressed as assets under " +
                $"Assets/Samples/OneText/{PackageVersion()}/ and registered in project settings so " +
                "a build ships them. Unicode licence: the notice travels on each asset. The " +
                "Package Manager's Samples tab imports the same files as raw text if you would " +
                "rather have them that way.", "hint"));
            return card.Root;
        }

        /// <summary>How many of the four bundled scripts this project has registered.</summary>
        private static int InstalledFromBundle()
        {
            var settings = OneTextSettings.Instance;
            if (settings == null) return 0;

            int count = 0;
            foreach (var (_, script) in Bundled)
            {
                foreach (var dictionary in settings.Dictionaries)
                {
                    if (dictionary == null || dictionary.Script != script) continue;
                    // The starter list is ninety words; anything registered as an
                    // asset is the real thing.
                    count++;
                    break;
                }
            }
            return count;
        }

        /// <summary>Where the package keeps its word lists, or null if it does not.</summary>
        private static string BundledFolder()
        {
            string root = null;
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(HubDictionariesTab).Assembly);
                if (info != null) root = info.resolvedPath;
            }
            catch (System.Exception)
            {
                // A project that embedded the package under Assets has no
                // package info, and the path below still finds it.
            }

            foreach (string candidate in new[]
            {
                root == null ? null : Path.Combine(root, "Samples~", "Dictionaries"),
                Path.GetFullPath("Packages/com.onetext.core/Samples~/Dictionaries"),
            })
            {
                if (candidate == null) continue;
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string PackageVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(HubDictionariesTab).Assembly);
                return info != null ? info.version : "0.1.0";
            }
            catch (System.Exception)
            {
                return "0.1.0";
            }
        }

        /// <summary>
        /// Copies the bundled lists in, in the same shape a hand import makes:
        /// one compressed asset per script, installed into this editor and
        /// registered in the project settings so a build carries them.
        /// </summary>
        private void Install(bool reinstall)
        {
            string source = BundledFolder();
            if (source == null)
            {
                SayBadly("The bundled word lists are not on disk. Import the sample from the " +
                    "Package Manager instead.");
                return;
            }

            // Asked once, not once per list: Register() puts up a dialog when
            // there is nowhere to register, and four of those is a fight.
            if (OneTextSettings.Instance == null &&
                !HubUI.Confirm("No settings asset",
                    "The word lists will be imported as assets, but this project has no OneText " +
                    "settings asset to register them in, so the editor will wrap correctly and a " +
                    "build will not. Create one in project settings first, or import anyway.",
                    "Import anyway"))
                return;

            // Before, with whatever is installed now: the only moment it can
            // be taken.
            if (Hub.StringFolders.Count > 0) Measure(_coverageBefore);

            string folder = $"Assets/Samples/OneText/{PackageVersion()}/" +
                "Word-break dictionaries (Thai, Lao, Khmer, Burmese)";
            EnsureFolder(folder);

            int made = 0;
            long words = 0;
            try
            {
                for (int i = 0; i < Bundled.Length; i++)
                {
                    var (file, script) = Bundled[i];
                    EditorUtility.DisplayProgressBar("Installing word lists",
                        $"{script}: {file}", (i + 0.5f) / Bundled.Length);

                    string path = Path.Combine(source, file);
                    if (!File.Exists(path)) continue;

                    string text;
                    try
                    {
                        text = File.ReadAllText(path, Encoding.UTF8);
                    }
                    catch (IOException error)
                    {
                        Debug.LogError($"OneText: cannot read {path}: {error.Message}");
                        continue;
                    }

                    string assetPath = $"{folder}/OneText{script}Dictionary.asset";
                    var dictionary = AssetDatabase.LoadAssetAtPath<OneTextDictionary>(assetPath);
                    bool isNew = dictionary == null;
                    if (isNew) dictionary = ScriptableObject.CreateInstance<OneTextDictionary>();
                    dictionary.Initialize(text, script, $"{file} (bundled with OneText)",
                        "Word list from the ICU project (Unicode licence). " +
                        "See THIRD-PARTY-NOTICES.md.");
                    if (isNew) AssetDatabase.CreateAsset(dictionary, assetPath);
                    else EditorUtility.SetDirty(dictionary);

                    dictionary.Install();
                    if (OneTextSettings.Instance != null) Register(dictionary);
                    made++;
                    words += dictionary.WordCount;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (made == 0)
            {
                SayBadly("Nothing was installed: the bundled files could not be read.");
                return;
            }

            if (Hub.StringFolders.Count > 0) Measure(_coverageAfter);

            Debug.Log($"OneText: installed {made} bundled word list(s), {words:n0} words, into " +
                $"{folder}.");
            Refresh();
            Say(reinstall
                ? $"Reinstalled {made} word list(s), {words:n0} words."
                : $"Installed {made} word list(s), {words:n0} words, registered for builds." +
                  (Hub.StringFolders.Count > 0 ? " Coverage re-measured." : ""));
        }

        /// <summary>Creates a project folder and every folder above it.</summary>
        private static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{built}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }

        // ----------------------------------------------------------- installed

        private VisualElement InstalledCard()
        {
            var card = HubUI.MakeCard("Installed word lists",
                "What this editor is breaking lines with right now.").Flush();
            DictionaryLineBreaker.EnsureDefaults();

            foreach (string script in Scripts)
            {
                var words = DictionaryLineBreaker.GetWordList(script);
                card.Add(HubUI.KeyValue(script,
                    words == null
                        ? "none"
                        : $"{words.WordCount:n0} words, longest {words.LongestWord}",
                    words == null ? HubTone.Warn
                        : words.WordCount < 1000 ? HubTone.Warn
                        : HubTone.Good));
            }

            var settings = OneTextSettings.Instance;
            var body = new VisualElement();
            body.style.paddingLeft = 16f;
            body.style.paddingRight = 16f;
            body.style.paddingTop = 12f;
            body.style.paddingBottom = 12f;

            if (settings == null)
            {
                body.Add(HubUI.Notice(
                    "No settings asset, so an imported dictionary cannot be registered for builds. " +
                    "Create one in project settings.", HubTone.Warn));
            }
            else if (settings.Dictionaries.Count == 0)
            {
                body.Add(HubUI.Notice(
                    "The lists above are the built-in starters: about ninety Thai words, and " +
                    "nothing at all for the other three. Install the bundled lists below.",
                    HubTone.Warn));
            }
            else
            {
                foreach (var dictionary in settings.Dictionaries)
                {
                    if (dictionary == null) continue;
                    var captured = dictionary;
                    var row = HubUI.Box("style-row");
                    row.Add(HubUI.Text($"{dictionary.Script}: {dictionary.name}", "style-row__name"));
                    row.Add(HubUI.Mono(HubUI.Text(
                        $"{dictionary.WordCount:n0} words · {dictionary.StoredSize / 1024f:n0} KB stored",
                        "kv__value")));
                    row.Add(HubUI.Quiet("Select", () =>
                    {
                        Selection.activeObject = captured;
                        EditorGUIUtility.PingObject(captured);
                    }));
                    body.Add(row);
                }
            }
            card.Add(body);
            return card.Root;
        }

        // ------------------------------------------------------------ coverage

        /// <summary>
        /// Coverage against the project's own strings, before and after.
        ///
        /// Measured on the project's text rather than a canned sample on
        /// purpose: a game's Thai is names, numbers and its own vocabulary, and
        /// a number from somebody else's corpus would be a number about
        /// somebody else's game.
        /// </summary>
        private VisualElement CoverageCard()
        {
            var content = new VisualElement();
            content.Add(StringSources(
                "Coverage is measured against every string found under these paths."));

            var card = HubUI.MakeCard("Segmentation coverage",
                "How much of your own text the installed word lists can find word boundaries in. " +
                "Measured against your strings, not somebody else's corpus.");
            card.Act(HubUI.Primary("Measure now", () =>
            {
                Measure(_coverageAfter);
                Refresh();
            }));

            if (!_measured)
            {
                card.Add(HubUI.Empty("Not measured yet",
                    Hub.StringFolders.Count == 0
                        ? "Add a folder of strings above first; this number is about your text."
                        : "One scan of the folders above says whether the built-in lists are " +
                          "enough for this project, in a number you can put in a ticket.",
                    Hub.StringFolders.Count == 0 ? null : "Measure now",
                    Hub.StringFolders.Count == 0 ? (System.Action)null : () =>
                    {
                        Measure(_coverageAfter);
                        Refresh();
                    }, "%"));
                content.Add(card.Root);
                return content;
            }

            if (_coverageAfter.Count == 0)
            {
                card.Add(HubUI.Notice(
                    "No Thai, Lao, Khmer or Burmese text was found in those folders. This project " +
                    "does not need a dictionary.", HubTone.Good));
                content.Add(card.Root);
                return content;
            }

            foreach (var pair in _coverageAfter)
            {
                string script = pair.Key;
                bool thin = pair.Value < TextDoctor.MinimumDictionaryCoverage;
                string before = _coverageBefore.TryGetValue(script, out float b)
                    ? $"{HubUI.Percent(b, 1)}  →  " : "";

                var block = new VisualElement();
                block.style.marginBottom = 12f;
                block.Add(HubUI.KeyValue(script,
                    $"{before}{HubUI.Percent(pair.Value, 1)} of {_sampleSize[script]:n0} sampled characters",
                    thin ? HubTone.Warn : HubTone.Good));
                block.Add(HubUI.Meter(pair.Value, thin ? HubTone.Warn : HubTone.Good));
                if (thin)
                    block.Add(HubUI.Notice(
                        $"{script} text is only {HubUI.Percent(pair.Value, 1)} segmented; it will wrap in the " +
                        "wrong places. Install the bundled word lists above.", HubTone.Warn));
                card.Add(block);
            }

            content.Add(card.Root);
            return content;
        }

        private void Measure(Dictionary<string, float> into)
        {
            var scan = TextSourceScanner.Scan(Hub.StringFolders);
            var samples = new Dictionary<string, StringBuilder>();
            foreach (var entry in scan.Entries)
            {
                if (string.IsNullOrEmpty(entry.Value)) continue;
                foreach (char c in entry.Value)
                {
                    string script = DictionaryLineBreaker.ScriptOf(c);
                    if (script == null) continue;
                    if (!samples.TryGetValue(script, out var sample))
                        samples[script] = sample = new StringBuilder();
                    if (sample.Length < 20000) sample.Append(c);
                }
            }

            into.Clear();
            _sampleSize.Clear();
            DictionaryLineBreaker.EnsureDefaults();
            foreach (var pair in samples)
            {
                var words = DictionaryLineBreaker.GetWordList(pair.Key);
                string sample = pair.Value.ToString();
                into[pair.Key] = words?.Coverage(sample) ?? 0f;
                _sampleSize[pair.Key] = sample.Length;
            }
            _measured = true;

            Say(samples.Count == 0
                ? "No Thai, Lao, Khmer or Burmese text in those folders, no dictionary needed."
                : $"Measured {samples.Count} script(s) against {scan.FilesScanned} file(s).");
        }

        // -------------------------------------------------------------- import

        private VisualElement ImportCard()
        {
            var card = HubUI.MakeCard("Custom word list",
                "Any ICU-format list from disk: a language the package does not bundle, or a " +
                "list your team maintains itself.");

            int index = Mathf.Max(0, System.Array.IndexOf(Scripts, _script));
            card.Add(HubUI.Field("Script",
                HubUI.Segments(Scripts, index, i => _script = Scripts[i])));

            card.Add(HubUI.Primary("Choose a word list file…", Import));

            var advanced = new VisualElement();
            var notice = HubUI.Input(_notice, null, value => _notice = value);
            advanced.Add(HubUI.Field("Licence notice", notice,
                "Carried on the asset, so the shipping project can reproduce it."));
            card.Add(HubUI.Disclose("Advanced", advanced));

            card.Add(HubUI.Text(
                "The import registers the asset in project settings for you; an import that left " +
                "the list unreferenced would ship a project whose editor wraps Thai correctly and " +
                "whose build does not.", "hint"));
            return card.Root;
        }

        /// <summary>
        /// Imports a word list, registers it in the project settings, and says
        /// what it bought: the before number is taken with the old list still
        /// installed, which is the only moment it can be taken.
        /// </summary>
        private void Import()
        {
            string path = EditorUtility.OpenFilePanel(
                "ICU word list (thaidict.txt and friends)", "", "txt,dict,csv");
            if (string.IsNullOrEmpty(path)) return;

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException e)
            {
                EditorUtility.DisplayDialog("Could not read the file", e.Message, "OK");
                return;
            }

            // Before, with whatever is installed now.
            if (Hub.StringFolders.Count > 0) Measure(_coverageBefore);

            string assetPath = EditorUtility.SaveFilePanelInProject("Save the word list",
                $"OneText{_script}Dictionary", "asset",
                "Where should the dictionary asset go?");
            if (string.IsNullOrEmpty(assetPath)) return;

            var dictionary = ScriptableObject.CreateInstance<OneTextDictionary>();
            dictionary.Initialize(text, _script, path, _notice);
            AssetDatabase.CreateAsset(dictionary, assetPath);
            AssetDatabase.SaveAssets();
            dictionary.Install();

            Register(dictionary);
            if (Hub.StringFolders.Count > 0) Measure(_coverageAfter);

            Selection.activeObject = dictionary;
            Debug.Log($"OneText: imported {dictionary.WordCount:n0} {_script} words " +
                $"({dictionary.SourceSize / 1024f:n0} KB -> {dictionary.StoredSize / 1024f:n0} KB stored).",
                dictionary);
            Refresh();
            Say($"Imported {dictionary.WordCount:n0} {_script} words and registered them for builds.");
        }

        /// <summary>
        /// Puts the asset in the project settings, so a build installs it.
        ///
        /// The step everyone forgets, which is exactly why it is not a step: an
        /// import that leaves the list unreferenced would ship a project whose
        /// editor wraps Thai correctly and whose build does not.
        /// </summary>
        private static void Register(OneTextDictionary dictionary)
        {
            var settings = OneTextSettings.Instance;
            if (settings == null)
            {
                EditorUtility.DisplayDialog("No settings asset",
                    "The dictionary was imported but not registered: this project has no OneText " +
                    "settings asset. Create one in Project Settings > OneText, then add the " +
                    "dictionary to its list.", "OK");
                return;
            }

            var serialized = new SerializedObject(settings);
            var list = serialized.FindProperty("_dictionaries");
            for (int i = 0; i < list.arraySize; i++)
            {
                var existing = list.GetArrayElementAtIndex(i).objectReferenceValue as OneTextDictionary;
                // One list per script: a second Thai dictionary would install
                // over the first in whatever order the list happens to be in.
                if (existing == null || existing.Script != dictionary.Script) continue;
                list.GetArrayElementAtIndex(i).objectReferenceValue = dictionary;
                serialized.ApplyModifiedProperties();
                return;
            }

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = dictionary;
            serialized.ApplyModifiedProperties();
        }
    }
}
