using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>
    /// Word lists for the scripts that write no spaces, imported as assets, with
    /// the number that says whether they are doing anything.
    ///
    /// M10 left Thai correct in mechanism and thin in data: the trie, the
    /// segmenter and the ICU file format all work, and what ships built in is
    /// about ninety words. The gap is invisible from the outside — a dictionary
    /// breaker does not crash when it is short of words, it wraps mid-word in a
    /// language nobody on the team reads. So the tab is built around coverage
    /// against the project's own strings, before and after: "11% -> 99.2%" is
    /// the only honest way to report this to a team that cannot read the text.
    /// </summary>
    public sealed class HubDictionariesTab
    {
        private static readonly string[] Scripts = { "Thai", "Lao", "Khmer", "Myanmar" };

        private string _script = "Thai";
        private string _notice =
            "Word list from the ICU project (Unicode licence). See THIRD-PARTY-NOTICES.md.";
        private readonly Dictionary<string, float> _coverageBefore = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _coverageAfter = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _sampleSize = new Dictionary<string, int>();
        private bool _measured;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Dictionaries",
                "Thai, Lao, Khmer and Burmese have no spaces between words, so UAX #14 defers them " +
                "to a dictionary. The built-in Thai list is a starter; ICU's is the real thing, " +
                "about 200 KB and permissively licensed. It stays an option, not a default — a " +
                "project that never ships Thai should not carry it.");

            DrawInstalled();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Import", EditorStyles.boldLabel);
            _script = Popup("Script", _script, Scripts);
            _notice = EditorGUILayout.TextField(new GUIContent("Licence notice",
                "Carried on the asset, so the shipping project can reproduce it."), _notice);

            if (GUILayout.Button("Import word list file..."))
                Import(hub);

            EditorGUILayout.Space();
            DrawCoverage(hub);
        }

        private void DrawInstalled()
        {
            EditorGUILayout.LabelField("Installed", EditorStyles.boldLabel);
            DictionaryLineBreaker.EnsureDefaults();

            foreach (string script in Scripts)
            {
                var words = DictionaryLineBreaker.GetWordList(script);
                EditorGUILayout.LabelField(script,
                    words == null ? "none" : $"{words.WordCount:n0} words, longest {words.LongestWord}");
            }

            var settings = OneTextSettings.Instance;
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No settings asset, so an imported dictionary cannot be registered for builds. " +
                    "Create one in Project Settings > OneText.", MessageType.Warning);
                return;
            }

            if (settings.Dictionaries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "The lists above are the built-in starters. Nothing is registered in the " +
                    "project settings, so a build ships with them.", MessageType.Info);
                return;
            }

            foreach (var dictionary in settings.Dictionaries)
            {
                if (dictionary == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{dictionary.Script}: {dictionary.name}",
                        $"{dictionary.WordCount:n0} words, {dictionary.StoredSize / 1024f:n0} KB stored");
                    if (GUILayout.Button("Select", GUILayout.Width(60f)))
                        Selection.activeObject = dictionary;
                }
            }
        }

        /// <summary>
        /// Coverage against the project's own strings, before and after.
        ///
        /// Measured on the project's text rather than a canned sample on
        /// purpose: a game's Thai is names, numbers and its own vocabulary, and
        /// a number from somebody else's corpus would be a number about
        /// somebody else's game.
        /// </summary>
        private void DrawCoverage(OneTextHub hub)
        {
            EditorGUILayout.LabelField("Coverage", EditorStyles.boldLabel);
            hub.DrawStringFolders();

            if (GUILayout.Button("Measure against project strings"))
                Measure(hub, _coverageAfter);

            if (!_measured)
            {
                EditorGUILayout.LabelField(
                    "Not measured yet.", EditorStyles.miniLabel);
                return;
            }

            foreach (var pair in _coverageAfter)
            {
                string script = pair.Key;
                string before = _coverageBefore.TryGetValue(script, out float b)
                    ? $"{b:P1} -> " : "";
                EditorGUILayout.LabelField($"{script}",
                    $"{before}{pair.Value:P1} of {_sampleSize[script]:n0} sampled characters");
            }

            foreach (var pair in _coverageAfter)
            {
                if (pair.Value >= TextDoctor.MinimumDictionaryCoverage) continue;
                EditorGUILayout.HelpBox(
                    $"{pair.Key} text is only {pair.Value:P1} segmented — it will wrap in the wrong " +
                    "places. Import the full ICU dictionary above.", MessageType.Warning);
            }
        }

        private void Measure(OneTextHub hub, Dictionary<string, float> into)
        {
            var scan = TextSourceScanner.Scan(hub.StringFolders);
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

            if (samples.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing to measure",
                    "No Thai, Lao, Khmer or Burmese text was found in those folders. " +
                    "This project does not need a dictionary.", "OK");
            }
        }

        /// <summary>
        /// Imports a word list, registers it in the project settings, and says
        /// what it bought — the before number is taken with the old list still
        /// installed, which is the only moment it can be taken.
        /// </summary>
        private void Import(OneTextHub hub)
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
            if (hub.StringFolders.Count > 0) Measure(hub, _coverageBefore);

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
            if (hub.StringFolders.Count > 0) Measure(hub, _coverageAfter);

            Selection.activeObject = dictionary;
            Debug.Log($"OneText: imported {dictionary.WordCount:n0} {_script} words " +
                $"({dictionary.SourceSize / 1024f:n0} KB -> {dictionary.StoredSize / 1024f:n0} KB stored).",
                dictionary);
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

        private static string Popup(string label, string value, string[] options)
        {
            int index = Mathf.Max(0, System.Array.IndexOf(options, value));
            return options[EditorGUILayout.Popup(label, index, options)];
        }
    }
}
