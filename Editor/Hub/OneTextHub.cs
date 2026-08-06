using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// One window for everything the package can tell you about its own text.
    ///
    /// TextMesh Pro's lesson is not that its features are bad; it is that a
    /// feature living in a context menu on an asset nobody thinks to
    /// right-click effectively does not exist. Fonts, styles, charsets,
    /// dictionaries, the atlas and Doctor were all reachable before this
    /// window, from six different places, which is the same as being reachable
    /// from none.
    ///
    /// Each section pairs one view with the one action that view makes obvious:
    /// the atlas shows what a session baked and offers to remember it, Doctor
    /// shows what will not render and offers the font that would fix it.
    ///
    /// It is built with UI Toolkit and skinned to the project's own site rather
    /// than to the editor. That is a decision about who this window is for: a
    /// person meeting the package for the first time reads a headline, a
    /// sentence saying what the panel is for, and one obvious button, not a
    /// column of inspector rows that assume they already know.
    /// </summary>
    public sealed class OneTextHub : EditorWindow
    {
        public enum Tab
        {
            Overview,
            Fonts,
            Styles,
            Charsets,
            Dictionaries,
            Atlas,
            Gallery,
            Doctor,
            Forensics,
        }

        private const string SectionKey = "OneText.Hub.Section";
        private const string FoldersKey = "OneText.Hub.Folders";

        private readonly List<HubSection> _sections = new List<HubSection>();
        private HubShell _shell;
        private Tab _pending = Tab.Overview;

        /// <summary>
        /// Folders of strings the project ships, shared by every section that
        /// needs them: the gallery lays them out, Doctor checks them, the
        /// dictionaries measure against them. Asked for once.
        /// </summary>
        public readonly List<string> StringFolders = new List<string>();

        /// <summary>Every section, in sidebar order.</summary>
        public IReadOnlyList<HubSection> Sections => _sections;

        [MenuItem("Window/OneText/Hub", false, 1000)]
        public static OneTextHub Open()
        {
            var window = GetWindow<OneTextHub>("OneText");
            window.minSize = new Vector2(720f, 460f);
            window.Show();
            return window;
        }

        /// <summary>Opens the Hub on a particular section: what the other editors link to.</summary>
        public static void Open(Tab tab)
        {
            var window = Open();
            window.Select(tab);
        }

        /// <summary>Shows one section by its old tab name.</summary>
        public void Select(Tab tab)
        {
            _pending = tab;
            var section = Find(tab);
            if (section != null) _shell?.Select(section);
            Repaint();
        }

        public HubSection Find(Tab tab)
        {
            foreach (var section in _sections) if (section.Tab == tab) return section;
            return _sections.Count > 0 ? _sections[0] : null;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("OneText");

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
            _sections.Add(new HubFontsTab());
            _sections.Add(new HubStylesTab());
            _sections.Add(new HubCharsetsTab());
            _sections.Add(new HubDictionariesTab());
            _sections.Add(new HubAtlasTab());
            _sections.Add(new HubGalleryTab());
            _sections.Add(new HubDoctorTab());
            _sections.Add(new HubForensicsTab());
        }

        private void CreateGUI()
        {
            rootVisualElement.style.flexGrow = 1f;
            _shell = new HubShell(this);
            rootVisualElement.Add(_shell.Root);
            _shell.Select(Find(_pending));
        }

        private void OnDisable()
        {
            if (_shell?.Current != null)
                EditorPrefs.SetString(SectionKey, _shell.Current.Tab.ToString());
            EditorPrefs.SetString(FoldersKey, string.Join("\n", StringFolders));
            foreach (var section in _sections) section.Dispose();
        }

        /// <summary>The atlas section watches a running game; the rest do not need to.</summary>
        private void Update() => _shell?.Current?.Tick();

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
