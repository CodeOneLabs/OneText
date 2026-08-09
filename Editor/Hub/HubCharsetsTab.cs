using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Charsets, and the folders they are built from.
    ///
    /// The recorder answers "what did this play session draw" and the project
    /// scan answers "what is typed into a prefab". Neither answers "what will
    /// this game ever draw", because that lives in the string tables, so a
    /// charset can name folders, and the folders are rescanned when they
    /// change. One output feeds prewarm and subsetting alike.
    /// </summary>
    public sealed class HubCharsetsTab : HubSection
    {
        private OneTextCharset _selected;
        private CharsetFolderScan.Report _lastReport;
        private bool _scanned;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Charsets;

        public override string Title => "Charsets";

        public override string Eyebrow => "Before the first frame";

        public override string Lede =>
            "Characters to rasterise before anything asks for them. A charset that scans folders " +
            "follows the string tables as translators change them, instead of being correct on " +
            "the day it was made.";

        public override string NavHint => "characters to pre-bake";

        public override string NavGroup => "What gets baked";

        public override string BadgeText =>
            CharsetRecorder.Enabled && CharsetRecorder.CodepointCount > 0
                ? "REC"
                : null;

        public override HubTone BadgeTone => HubTone.Info;

        protected override void Compose(VisualElement content)
        {
            content.Add(PickerCard());
            if (_selected == null)
            {
                content.Add(RecorderCard());
                return;
            }

            content.Add(ContentsCard());
            content.Add(SourcesCard());
            content.Add(PrewarmCard());
            content.Add(RecorderCard());
        }

        // -------------------------------------------------------------- picker

        private VisualElement PickerCard()
        {
            var card = HubUI.MakeCard("Charset",
                "One list of characters, one list of sizes. Pre-baked together.");

            var picker = HubUI.AssetPicker<OneTextCharset>(
                () => _selected,
                value => { _selected = value; Refresh(); },
                "Choose a charset…",
                "Create a new charset…",
                () => CreateAsset<OneTextCharset>("OneTextCharset", "Where should the charset go?"));
            card.Add(HubUI.Field("Charset", picker));

            if (_selected == null)
            {
                card.Add(HubUI.Empty("No charset chosen",
                    "A charset is what the atlas bakes at startup, so the first line of dialogue " +
                    "does not hitch while its glyphs are rasterised. Make one, point it at the " +
                    "folder your strings live in, and it keeps itself current.",
                    "Create a charset…", () =>
                    {
                        var made = CreateAsset<OneTextCharset>("OneTextCharset",
                            "Where should the charset go?");
                        if (made == null) return;
                        _selected = made;
                        Refresh();
                        Say($"Created {made.name}. Add the folders your strings live in below.");
                    }, "▦"));
            }
            return card.Root;
        }

        private VisualElement ContentsCard()
        {
            var codepoints = _selected.Codepoints();
            var card = HubUI.MakeCard("Contents",
                $"{_selected.name}: everything counted here is baked at startup.").Flush();
            card.Act(HubUI.Quiet("Select asset", () =>
            {
                Selection.activeObject = _selected;
                EditorGUIUtility.PingObject(_selected);
            }));
            card.Add(HubUI.KeyValue("Characters", $"{codepoints.Count:n0}",
                codepoints.Count == 0 ? HubTone.Warn : HubTone.Good));
            card.Add(HubUI.KeyValue("Sizes",
                _selected.Sizes.Count == 0
                    ? "none listed; the label's own size is used"
                    : string.Join(", ", _selected.Sizes)));
            card.Add(HubUI.KeyValue("Ranges",
                _selected.Ranges.Count == 0 ? "none" : $"{_selected.Ranges.Count:n0}"));
            return card.Root;
        }

        // ------------------------------------------------------------- sources

        private VisualElement SourcesCard()
        {
            var folders = _selected.SourceFolders;
            var card = HubUI.MakeCard("Source folders",
                folders.Count == 0
                    ? "Nothing scanned: this charset only holds what was typed into it."
                    : $"{folders.Count} folder(s) this charset takes its characters from, " +
                      "rescanned on demand.");
            card.Act(HubUI.Ghost("Add folder…", AddFolder));

            if (folders.Count == 0)
            {
                card.Add(HubUI.Notice(
                    "A charset with no folders is correct on the day it was made. Point it at the " +
                    "folder your localisation tables live in and it follows them.", HubTone.Warn));
            }

            for (int i = 0; i < folders.Count; i++)
            {
                int index = i;
                var row = HubUI.Box("folder-row");
                var field = HubUI.Input(folders[index]);
                field.isDelayed = true;
                field.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(_selected, "Edit charset source folder");
                    folders[index] = evt.newValue;
                    EditorUtility.SetDirty(_selected);
                });
                HubUI.Mono(field);
                row.Add(field);
                row.Add(HubUI.Quiet("Remove", () =>
                {
                    Undo.RecordObject(_selected, "Remove charset source folder");
                    folders.RemoveAt(index);
                    EditorUtility.SetDirty(_selected);
                    Refresh();
                }));
                card.Add(row);
            }

            var actions = HubUI.Box("row");
            actions.Add(HubUI.Primary("Rescan now", () =>
            {
                _lastReport = CharsetFolderScan.Rescan(_selected);
                _scanned = true;
                Refresh();
                Say(_lastReport.Added > 0
                    ? $"Rescan added {_lastReport.Added:n0} character(s)."
                    : "Rescan found nothing new.");
            }));
            actions.Add(HubUI.Pill("Rescan on import", _selected.AutoRescan, on =>
            {
                Undo.RecordObject(_selected, "Toggle charset auto-rescan");
                _selected.AutoRescan = on;
                EditorUtility.SetDirty(_selected);
                Say(on
                    ? "This charset now refreshes whenever a file under those folders changes."
                    : "Automatic rescanning is off.");
            }));
            actions.style.marginTop = 6f;
            card.Add(actions);

            if (_scanned)
            {
                bool skipped = _lastReport.Skipped != null && _lastReport.Skipped.Count > 0;
                card.Add(HubUI.Notice(_lastReport.ToString(),
                    skipped ? HubTone.Warn : HubTone.Good));
                if (skipped)
                {
                    var list = new VisualElement();
                    foreach (string path in _lastReport.Skipped)
                        list.Add(HubUI.Mono(HubUI.Text(path, "hint")));
                    card.Add(HubUI.Disclose(
                        $"{_lastReport.Skipped.Count} file(s) skipped", list));
                }
            }
            return card.Root;
        }

        private void AddFolder()
        {
            string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
            if (string.IsNullOrEmpty(picked)) return;
            Undo.RecordObject(_selected, "Add charset source folder");
            _selected.SourceFolders.Add(TextSourceScanner.ToProjectPath(picked));
            EditorUtility.SetDirty(_selected);
            Refresh();
            Say("Folder added. Rescan to take its characters.");
        }

        // ------------------------------------------------------------- prewarm

        private VisualElement PrewarmCard()
        {
            var card = HubUI.MakeCard("Prewarm",
                "Rasterises every character in this charset into the atlas, the way startup will.");

            bool ready = Application.isPlaying || _selected.BuildFontStack().Primary != null;
            if (!ready)
            {
                card.Add(HubUI.Notice(
                    "This charset lists no font of its own and there is no project default to " +
                    "borrow, so there is nothing to rasterise with. Set a default in project " +
                    "settings, or add a font to the charset.", HubTone.Warn));
                return card.Root;
            }

            card.Add(HubUI.Primary("Prewarm now", () =>
            {
                var report = _selected.Prewarm();
                Debug.Log($"OneText {report}", _selected);
                Say(report.ToString());
            }));

            var settings = OneTextSettings.Instance;
            if (settings != null && settings.PrewarmCharset == _selected)
                card.Add(HubUI.Notice("This is the charset the project bakes at startup.",
                    HubTone.Good));
            else
                card.Add(HubUI.Notice(
                    "The project does not bake this charset at startup yet; set it as the " +
                    "prewarm charset in Global Settings.", HubTone.Neutral));
            return card.Root;
        }

        // ------------------------------------------------------------ recorder

        /// <summary>
        /// The recorder, which is the other half: what a play session drew is
        /// what no static scan can predict.
        /// </summary>
        private VisualElement RecorderCard()
        {
            var card = HubUI.MakeCard("Recorder",
                "What this session actually drew: the characters a scan cannot predict (a " +
                "player's name, a number, a line built at runtime).");

            if (!CharsetRecorder.Enabled)
            {
                card.Add(HubUI.Notice(
                    "Recording is off. Turn on 'Record Charset In Play Mode' in Global Settings, " +
                    "play the game, and everything it drew shows up here.", HubTone.Neutral));
                card.Add(HubUI.Ghost("Global settings",
                    () => OneTextHub.Open(OneTextHub.Tab.Settings)));
                return card.Root;
            }

            card.Add(HubUI.KeyValue("Recorded this session",
                $"{CharsetRecorder.CodepointCount:n0} character(s)",
                CharsetRecorder.CodepointCount > 0 ? HubTone.Good : HubTone.Neutral));

            if (CharsetRecorder.CodepointCount == 0)
            {
                card.Add(HubUI.Notice("Nothing recorded yet: enter play mode and draw some text.",
                    HubTone.Neutral));
                return card.Root;
            }

            var row = HubUI.Box("row");
            if (_selected != null)
            {
                row.Add(HubUI.Primary($"Add {CharsetRecorder.CodepointCount:n0} to {_selected.name}",
                    () =>
                    {
                        int before = _selected.Codepoints().Count;
                        Undo.RecordObject(_selected, "Add recorded characters");
                        _selected.Characters = Merge(_selected.Characters,
                            CharsetRecorder.CharactersAsString());
                        foreach (float size in CharsetRecorder.SizesSorted())
                            if (!_selected.Sizes.Contains(size)) _selected.Sizes.Add(size);
                        EditorUtility.SetDirty(_selected);
                        int after = _selected.Codepoints().Count;
                        Refresh();
                        Say($"Added {after - before:n0} new character(s) to {_selected.name} " +
                            $"({after:n0} total).");
                    }));
            }
            row.Add(HubUI.Danger("Clear recording", () =>
            {
                if (!HubUI.Confirm("Clear the recording?",
                    $"{CharsetRecorder.CodepointCount:n0} character(s) recorded this session will " +
                    "be forgotten. Playing again records them anew.", "Clear")) return;
                CharsetRecorder.Clear();
                Refresh();
                Say("Recording cleared.");
            }));
            card.Add(row);
            return card.Root;
        }

        /// <summary>Union of two character sets, sorted, without duplicates or whitespace.</summary>
        internal static string Merge(string a, string b)
        {
            var seen = new System.Collections.Generic.SortedSet<int>();
            foreach (string source in new[] { a, b })
            {
                if (string.IsNullOrEmpty(source)) continue;
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                    if (char.IsHighSurrogate(c) && i + 1 < source.Length &&
                        char.IsLowSurrogate(source[i + 1]))
                    {
                        seen.Add(char.ConvertToUtf32(c, source[i + 1]));
                        i++;
                    }
                    else seen.Add(c);
                }
            }

            var builder = new System.Text.StringBuilder(seen.Count);
            foreach (int cp in seen) builder.Append(char.ConvertFromUtf32(cp));
            return builder.ToString();
        }
    }
}
