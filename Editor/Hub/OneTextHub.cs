using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

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
    /// Each tab pairs one view with the one action that view makes obvious: the
    /// atlas shows what a session baked and offers to remember it, Doctor shows
    /// what will not render and offers the font that would fix it.
    /// </summary>
    public sealed class OneTextHub : EditorWindow
    {
        public enum Tab
        {
            Fonts,
            Styles,
            Charsets,
            Dictionaries,
            Atlas,
            Gallery,
            Doctor,
            Forensics,
        }

        private const string TabKey = "OneText.Hub.Tab";
        private const string FoldersKey = "OneText.Hub.Folders";

        private Tab _tab;
        private Vector2 _scroll;

        // Tab state lives in the tab objects, so a tab keeps its scan and its
        // scroll position while the user is off looking at another one.
        private readonly HubCharsetsTab _charsets = new HubCharsetsTab();
        private readonly HubDictionariesTab _dictionaries = new HubDictionariesTab();
        private readonly HubAtlasTab _atlas = new HubAtlasTab();
        private readonly HubGalleryTab _gallery = new HubGalleryTab();
        private readonly HubDoctorTab _doctor = new HubDoctorTab();
        private readonly HubForensicsTab _forensics = new HubForensicsTab();
        private readonly HubFontsTab _fonts = new HubFontsTab();
        private readonly HubStylesTab _styles = new HubStylesTab();

        /// <summary>
        /// Folders of strings the project ships, shared by every tab that needs
        /// them — the gallery lays them out, Doctor checks them, charsets take
        /// their characters. Asked for once.
        /// </summary>
        public readonly List<string> StringFolders = new List<string>();

        [MenuItem("Window/OneText/Hub", false, 1000)]
        public static OneTextHub Open()
        {
            var window = GetWindow<OneTextHub>("OneText");
            window.minSize = new Vector2(560f, 420f);
            window.Show();
            return window;
        }

        /// <summary>Opens the Hub on a particular tab — what the other editors link to.</summary>
        public static void Open(Tab tab)
        {
            var window = Open();
            window._tab = tab;
            window.Repaint();
        }

        private void OnEnable()
        {
            _tab = (Tab)EditorPrefs.GetInt(TabKey, 0);
            StringFolders.Clear();
            string stored = EditorPrefs.GetString(FoldersKey, "");
            if (!string.IsNullOrEmpty(stored))
                StringFolders.AddRange(stored.Split('\n'));
            titleContent = new GUIContent("OneText");
        }

        private void OnDisable()
        {
            EditorPrefs.SetInt(TabKey, (int)_tab);
            EditorPrefs.SetString(FoldersKey, string.Join("\n", StringFolders));
            _gallery.Dispose();
            _forensics.Dispose();
        }

        private void OnGUI()
        {
            DrawTabs();
            EditorGUILayout.Space(4f);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case Tab.Fonts: _fonts.Draw(this); break;
                case Tab.Styles: _styles.Draw(this); break;
                case Tab.Charsets: _charsets.Draw(this); break;
                case Tab.Dictionaries: _dictionaries.Draw(this); break;
                case Tab.Atlas: _atlas.Draw(this); break;
                case Tab.Gallery: _gallery.Draw(this); break;
                case Tab.Doctor: _doctor.Draw(this); break;
                case Tab.Forensics: _forensics.Draw(this); break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawTabs()
        {
            var names = System.Enum.GetNames(typeof(Tab));
            int selected = GUILayout.Toolbar((int)_tab, names, EditorStyles.toolbarButton);
            if (selected != (int)_tab)
            {
                _tab = (Tab)selected;
                GUI.FocusControl(null);
            }
        }

        /// <summary>The atlas tab repaints itself while the game runs; the rest do not need to.</summary>
        private void Update()
        {
            if (_tab == Tab.Atlas && Application.isPlaying) Repaint();
        }

        // ------------------------------------------------------ shared drawing

        /// <summary>The folder list, drawn wherever a tab needs strings.</summary>
        public void DrawStringFolders()
        {
            EditorGUILayout.LabelField("String folders", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Localization tables, dialogue scripts, any text under a path. CSV, TSV, JSON, " +
                "plain text and Unity Localization string tables are read; a game's real " +
                "characters live in its string tables rather than its scenes.",
                MessageType.None);

            for (int i = 0; i < StringFolders.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    StringFolders[i] = EditorGUILayout.TextField(StringFolders[i]);
                    if (GUILayout.Button("...", GUILayout.Width(28f)))
                    {
                        string picked = EditorUtility.OpenFolderPanel("Folder of strings",
                            StringFolders[i], "");
                        if (!string.IsNullOrEmpty(picked))
                            StringFolders[i] = TextSourceScanner.ToProjectPath(picked);
                    }
                    if (GUILayout.Button("-", GUILayout.Width(24f)))
                    {
                        StringFolders.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (GUILayout.Button("Add folder"))
            {
                string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
                if (!string.IsNullOrEmpty(picked))
                    StringFolders.Add(TextSourceScanner.ToProjectPath(picked));
            }
        }

        /// <summary>A heading with an explanation under it — every tab opens with one.</summary>
        public static void Header(string title, string explanation)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(explanation, style);
            EditorGUILayout.Space(4f);
        }

        /// <summary>Every style asset in the project, for the gallery and the styles tab.</summary>
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
