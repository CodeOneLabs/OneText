using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// One place for everything the package can tell you about its own text —
    /// and, since it is mounted by <see cref="OneTextSettingsProvider"/>, that
    /// place is Project Settings &gt; OneText.
    ///
    /// TextMesh Pro's lesson is not that its features are bad; it is that a
    /// feature living in a context menu on an asset nobody thinks to
    /// right-click effectively does not exist. Fonts, styles, charsets,
    /// dictionaries, the atlas and Doctor were all reachable before this
    /// window, from six different places, which is the same as being reachable
    /// from none. Having then put them in a window of its own, the project
    /// settings page was still a second place holding the project's own
    /// defaults — the first place anybody looks for them, and the one screen
    /// this window did not contain. So the window moved into it.
    ///
    /// Each section pairs one view with the one action that view makes obvious:
    /// the atlas shows what a session baked and offers to remember it, Doctor
    /// shows what will not render and offers the font that would fix it.
    ///
    /// It is built with UI Toolkit and skinned to the project's own site rather
    /// than to the editor. That is a decision about who this is for: a person
    /// meeting the package for the first time reads a headline, a sentence
    /// saying what the panel is for, and one obvious button, not a column of
    /// inspector rows that assume they already know.
    ///
    /// The class is a ScriptableObject rather than an EditorWindow because it
    /// no longer owns a window: it owns the sections, the shell they hang in
    /// and the state they share, and the settings page borrows all three.
    /// </summary>
    public sealed class OneTextHub : ScriptableObject
    {
        public enum Tab
        {
            Overview,
            Settings,
            Fonts,
            Styles,
            Charsets,
            Dictionaries,
            Atlas,
            Gallery,
            Doctor,
            Forensics,
            Onboarding,
        }

        /// <summary>The settings page this window lives on.</summary>
        public const string SettingsPath = "Project/OneText";

        private const string SectionKey = "OneText.Hub.Section";
        private const string FoldersKey = "OneText.Hub.Folders";

        private readonly List<HubSection> _sections = new List<HubSection>();
        private HubShell _shell;
        private Tab _pending = Tab.Overview;

        /// <summary>The one mounted on the settings page, or null when it is closed.</summary>
        private static OneTextHub s_mounted;

        /// <summary>Where to go the moment a Hub is mounted, if somebody asked.</summary>
        private static Tab? s_requested;

        /// <summary>
        /// Folders of strings the project ships, shared by every section that
        /// needs them: the gallery lays them out, Doctor checks them, the
        /// dictionaries measure against them. Asked for once.
        /// </summary>
        public readonly List<string> StringFolders = new List<string>();

        /// <summary>Every section, in sidebar order.</summary>
        public IReadOnlyList<HubSection> Sections => _sections;

        /// <summary>Opens Project Settings &gt; OneText.</summary>
        [MenuItem("Window/OneText/Hub", false, 1000)]
        public static void Open() => Open(Tab.Overview);

        /// <summary>
        /// Opens the Hub on a particular section: what the other editors link
        /// to. The section is remembered rather than applied, because opening
        /// the settings window is what builds the Hub that has to receive it.
        /// </summary>
        public static void Open(Tab tab)
        {
            s_requested = tab;
            SettingsService.OpenProjectSettings(SettingsPath);
            if (s_mounted == null) return;
            s_requested = null;
            s_mounted.Select(tab);
        }

        /// <summary>Shows one section by its tab name.</summary>
        public void Select(Tab tab)
        {
            _pending = tab;
            var section = Find(tab);
            if (section != null) _shell?.Select(section);
        }

        public HubSection Find(Tab tab)
        {
            foreach (var section in _sections) if (section.Tab == tab) return section;
            return _sections.Count > 0 ? _sections[0] : null;
        }

        private void OnEnable()
        {
            StringFolders.Clear();
            string stored = EditorPrefs.GetString(FoldersKey, "");
            if (!string.IsNullOrEmpty(stored))
                foreach (string folder in stored.Split('\n'))
                    if (!string.IsNullOrEmpty(folder)) StringFolders.Add(folder);

            BuildSections();

            string name = EditorPrefs.GetString(SectionKey, Tab.Overview.ToString());
            _pending = System.Enum.TryParse(name, out Tab tab) ? tab : Tab.Overview;
        }

        private void BuildSections()
        {
            _sections.Clear();
            _sections.Add(new HubOverviewTab());
            _sections.Add(new HubSettingsTab());
            _sections.Add(new HubFontsTab());
            _sections.Add(new HubStylesTab());
            _sections.Add(new HubCharsetsTab());
            _sections.Add(new HubDictionariesTab());
            _sections.Add(new HubAtlasTab());
            _sections.Add(new HubGalleryTab());
            _sections.Add(new HubDoctorTab());
            _sections.Add(new HubForensicsTab());
            _sections.Add(new HubOnboardingTab());
        }

        // ------------------------------------------------------------ mounting

        /// <summary>
        /// Builds the whole thing into one element, for whoever is hosting it.
        /// Today that is the project settings page; the tests and the
        /// screenshot pass call the same method with no host at all.
        /// </summary>
        public VisualElement CreateUI()
        {
            _shell = new HubShell(this);
            if (s_requested.HasValue)
            {
                _pending = s_requested.Value;
                s_requested = null;
            }
            _shell.Select(Find(_pending));
            return _shell.Root;
        }

        /// <summary>Makes a Hub and marks it as the one the settings page shows.</summary>
        public static OneTextHub Mount()
        {
            if (s_mounted != null) Unmount(s_mounted);
            s_mounted = CreateInstance<OneTextHub>();
            s_mounted.hideFlags = HideFlags.DontSave;
            return s_mounted;
        }

        /// <summary>Puts one away: remembers where it was, then destroys it.</summary>
        public static void Unmount(OneTextHub hub)
        {
            if (hub == null) return;
            if (s_mounted == hub) s_mounted = null;
            DestroyImmediate(hub);
        }

        private void OnDisable()
        {
            if (_shell?.Current != null)
                EditorPrefs.SetString(SectionKey, _shell.Current.Tab.ToString());
            EditorPrefs.SetString(FoldersKey, string.Join("\n", StringFolders));
            foreach (var section in _sections) section.Dispose();
        }

        /// <summary>The atlas section watches a running game; the rest do not need to.</summary>
        public void Tick() => _shell?.Current?.Tick();

        /// <summary>Says what an action just did.</summary>
        public void Notify(string message, bool bad = false) => _shell?.Notify(message, bad);

        /// <summary>Re-reads the sidebar's status pills.</summary>
        public void RefreshNav() => _shell?.RefreshNav();

        /// <summary>Shows a section from inside another one.</summary>
        public void Go(Tab tab) => Select(tab);

        // ------------------------------------------------------ project queries

        /// <summary>Every style asset in the project, for the gallery and the styles section.</summary>
        public static List<OneTextStyle> AllStyles()
        {
            var styles = new List<OneTextStyle>();
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(OneTextStyle)}"))
            {
                var style = AssetDatabase.LoadAssetAtPath<OneTextStyle>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (style != null) styles.Add(style);
            }
            return styles;
        }

        /// <summary>
        /// How many font assets the project has, without opening any of them.
        ///
        /// The sidebar asks this on every refresh, and a font asset carries a
        /// compressed copy of its .ttf: loading forty of them to print "40" is
        /// what made this window feel slow. The search index already knows the
        /// count.
        /// </summary>
        public static int FontCount() => AssetDatabase.FindAssets($"t:{nameof(OneFontAsset)}").Length;

        /// <summary>How many style assets the project has, without opening any.</summary>
        public static int StyleCount() => AssetDatabase.FindAssets($"t:{nameof(OneTextStyle)}").Length;

        /// <summary>Every font asset in the project.</summary>
        public static List<OneFontAsset> AllFonts()
        {
            var fonts = new List<OneFontAsset>();
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(OneFontAsset)}"))
            {
                var font = AssetDatabase.LoadAssetAtPath<OneFontAsset>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (font != null) fonts.Add(font);
            }
            return fonts;
        }
    }
}
