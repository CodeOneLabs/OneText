using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// One entry in the Hub's sidebar and the panel it shows.
    ///
    /// A section owns its state between visits and rebuilds its own subtree
    /// when that state changes. There is no binding system here on purpose:
    /// the data behind these panels is a project scan or a live atlas, not a
    /// serialized object, and "clear and compose again" is both the cheapest
    /// thing to get right and the only thing that works the same on 2022.3 and
    /// on Unity 6.
    /// </summary>
    public abstract class HubSection
    {
        /// <summary>The old tab identity, kept so other editors can still link here.</summary>
        public abstract OneTextHub.Tab Tab { get; }

        /// <summary>The name in the sidebar and the headline of the panel.</summary>
        public abstract string Title { get; }

        /// <summary>The small caps line above the headline.</summary>
        public abstract string Eyebrow { get; }

        /// <summary>One sentence saying what this section is for. Not optional.</summary>
        public abstract string Lede { get; }

        /// <summary>A few words under the name in the sidebar.</summary>
        public virtual string NavHint => null;

        /// <summary>The heading this section sits under in the sidebar.</summary>
        public virtual string NavGroup => null;

        /// <summary>A status pill in the sidebar, or null.</summary>
        public virtual string BadgeText => null;

        public virtual HubTone BadgeTone => HubTone.Neutral;

        protected OneTextHub Hub { get; private set; }

        public VisualElement Root { get; private set; }

        /// <summary>Builds the panel. Safe to call with no window on screen.</summary>
        public VisualElement Build(OneTextHub hub)
        {
            Hub = hub;
            if (Root == null)
            {
                Root = new VisualElement { name = $"section-{Tab}" };
                Root.AddToClassList("section");
            }
            Rebuild();
            return Root;
        }

        /// <summary>Composes the panel again from current state.</summary>
        public void Rebuild()
        {
            if (Root == null) return;
            Root.Clear();
            Compose(Root);
        }

        protected abstract void Compose(VisualElement content);

        /// <summary>Called when the section becomes the visible one.</summary>
        public virtual void OnShow() { }

        /// <summary>Called while the window is open, a few times a second.</summary>
        public virtual void Tick() { }

        public virtual void Dispose() { }

        // ------------------------------------------------------------ helpers

        /// <summary>Rebuild this panel and refresh the sidebar's badges with it.</summary>
        protected void Refresh()
        {
            Rebuild();
            Hub?.RefreshNav();
        }

        /// <summary>Says what just happened, where the eye already is.</summary>
        protected void Say(string message) => Hub?.Notify(message);

        protected void SayBadly(string message) => Hub?.Notify(message, true);

        /// <summary>
        /// The folders of strings the project ships, shared by every section
        /// that needs them.
        ///
        /// Doctor checks them, the gallery lays them out and the dictionaries
        /// measure coverage against them; they are asked for once, in the same
        /// card, wherever that question comes up.
        /// </summary>
        protected VisualElement StringSources(string why)
        {
            var folders = Hub.StringFolders;
            var card = HubUI.MakeCard("String sources", why);
            card.Act(HubUI.Ghost("Add folder…", () =>
            {
                string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
                if (string.IsNullOrEmpty(picked)) return;
                folders.Add(TextSourceScanner.ToProjectPath(picked));
                Refresh();
                Say($"Added {TextSourceScanner.ToProjectPath(picked)}.");
            }));

            if (folders.Count == 0)
            {
                card.Add(HubUI.Empty("No string folders yet",
                    "Localization tables, dialogue scripts, any text under a path. CSV, TSV, JSON, " +
                    "plain text and Unity Localization string tables are read; a game's real " +
                    "characters live in its string tables rather than in its scenes.",
                    "Choose a folder…", () =>
                    {
                        string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
                        if (string.IsNullOrEmpty(picked)) return;
                        folders.Add(TextSourceScanner.ToProjectPath(picked));
                        Refresh();
                    }, "▤"));
                return card.Root;
            }

            for (int i = 0; i < folders.Count; i++)
            {
                int index = i;
                var row = HubUI.Box("folder-row");
                var field = HubUI.Input(folders[index], null, value => folders[index] = value);
                HubUI.Mono(field);
                row.Add(field);
                row.Add(HubUI.Quiet("Browse…", () =>
                {
                    string picked = EditorUtility.OpenFolderPanel("Folder of strings",
                        folders[index], "");
                    if (string.IsNullOrEmpty(picked)) return;
                    folders[index] = TextSourceScanner.ToProjectPath(picked);
                    Refresh();
                }));
                row.Add(HubUI.Quiet("Remove", () =>
                {
                    folders.RemoveAt(index);
                    Refresh();
                }));
                card.Add(row);
            }
            return card.Root;
        }

        /// <summary>Every style asset in the project.</summary>
        protected static List<OneTextStyle> AllStyles() => OneTextHub.AllStyles();

        /// <summary>Every font asset in the project.</summary>
        protected static List<OneFontAsset> AllFonts() => OneTextHub.AllFonts();

        /// <summary>How many there are, without opening them: what a badge wants.</summary>
        protected static int FontCount() => OneTextHub.FontCount();

        protected static int StyleCount() => OneTextHub.StyleCount();

        /// <summary>
        /// Creates a ScriptableObject asset where the user says to put it, or
        /// returns null when they change their mind.
        /// </summary>
        protected static T CreateAsset<T>(string defaultName, string prompt)
            where T : ScriptableObject
        {
            string path = EditorUtility.SaveFilePanelInProject(prompt, defaultName, "asset", prompt);
            if (string.IsNullOrEmpty(path)) return null;

            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            return asset;
        }
    }
}
