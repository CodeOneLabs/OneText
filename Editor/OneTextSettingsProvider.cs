using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Project Settings &gt; OneText: the default font and the project-wide
    /// fallback chain, created on demand so a new project never has to know
    /// where the settings asset lives.
    /// </summary>
    public static class OneTextSettingsProvider
    {
        private const string SettingsFolder = "Assets/Resources";
        private const string SettingsPath = SettingsFolder + "/OneTextSettings.asset";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/OneText", SettingsScope.Project)
            {
                label = "OneText",
                keywords = new[] { "text", "font", "fallback", "onetext" },
                guiHandler = _ => DrawSettings(),
            };
        }

        private static UnityEditor.Editor s_editor;

        private static void DrawSettings()
        {
            // The settings page is where people arrive; the Hub is where the
            // tools are. A milestone about discoverability may as well say so
            // in the one place everybody already opens.
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Open the Hub", GUILayout.Width(120f)))
                    OneTextHub.Open();
            }

            var settings = Find();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No settings asset yet. Create one to set the font new labels start with " +
                    "and the fallback fonts every label inherits.", MessageType.Info);
                if (GUILayout.Button("Create settings asset"))
                    Selection.activeObject = GetOrCreate();
                return;
            }

            UnityEditor.Editor.CreateCachedEditor(settings, null, ref s_editor);
            EditorGUI.BeginChangeCheck();
            s_editor.OnInspectorGUI();
            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(settings);
                OneTextSettings.Invalidate();
                // A different budget means a different texture; rebuild it now
                // so the editor shows what the game will get.
                SharedGlyphAtlas.Reconfigure();
            }

            EditorGUILayout.Space();
            DrawAtlasStatus(settings);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Fonts are OneText Font Assets: right-click any .ttf/.otf in the project and " +
                "choose OneText > Create Font Asset.", MessageType.None);
        }

        /// <summary>
        /// What the configured budget buys, and what the live atlas is doing
        /// with it. Tile counts are the number that decides whether a script
        /// fits, so they are worth showing before a project ships.
        /// </summary>
        private static void DrawAtlasStatus(OneTextSettings settings)
        {
            var budget = settings.AtlasSettings;
            EditorGUILayout.LabelField("Atlas budget", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Texture memory", $"{budget.MemoryBytes / (1024f * 1024f):0.#} MB " +
                $"({budget.TextureSize}x{budget.TextureSize} x {budget.LayerCount})");

            // The budget figure above is R8; the precise atlas holds the same
            // texels in four channels from the same setting, so a project with
            // precise labels pays it again times four. Only said when that
            // atlas exists; the existence check never creates one.
            if (SharedGlyphAtlas.PreciseAtlasExists)
            {
                var preciseStats = SharedGlyphAtlas.PreciseAtlas.GetStats();
                EditorGUILayout.LabelField("Precise atlas",
                    $"+{preciseStats.MemoryBytes / (1024f * 1024f):0.#} MB (RGBA32, for labels with " +
                    "Precise on)");
            }

            // A CJK glyph at 48px rasterizes to roughly a 56px square once the
            // SDF padding is added; that ratio is what makes CJK the budget case.
            long capacity = (long)budget.TextureSize * budget.TextureSize * budget.LayerCount;
            EditorGUILayout.LabelField("Rough capacity",
                $"~{capacity / (56 * 56):n0} CJK tiles @48px, ~{capacity / (26 * 26):n0} Latin tiles @36px");

            if (!SharedGlyphAtlas.Exists && !SharedGlyphAtlas.PreciseAtlasExists)
            {
                EditorGUILayout.LabelField("Live atlas", "not created yet");
                return;
            }

            if (SharedGlyphAtlas.Exists)
            {
                var stats = SharedGlyphAtlas.Atlas.GetStats();
                EditorGUILayout.LabelField("Live atlas",
                    $"{stats.TileCount:n0} tiles, {stats.UsedFraction * 100f:0.#}% full, " +
                    $"{stats.Evictions:n0} evictions, {stats.Compactions:n0} compactions");
            }
            if (SharedGlyphAtlas.PreciseAtlasExists)
            {
                var stats = SharedGlyphAtlas.PreciseAtlas.GetStats();
                EditorGUILayout.LabelField("Live precise atlas",
                    $"{stats.TileCount:n0} tiles, {stats.UsedFraction * 100f:0.#}% full, " +
                    $"{stats.Evictions:n0} evictions, {stats.Compactions:n0} compactions");
            }
            EditorGUILayout.LabelField("Partial upload",
                GlyphAtlas.SupportsPartialUpload ? "supported" : "not supported (full upload per flush)");
        }

        /// <summary>The project's settings asset, or null if it does not exist.</summary>
        public static OneTextSettings Find() =>
            AssetDatabase.LoadAssetAtPath<OneTextSettings>(SettingsPath) ??
            Resources.Load<OneTextSettings>(OneTextSettings.ResourcePath);

        /// <summary>The project's settings asset, creating it if needed.</summary>
        public static OneTextSettings GetOrCreate()
        {
            var existing = Find();
            if (existing != null) return existing;

            Directory.CreateDirectory(SettingsFolder);
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();
            OneTextSettings.Invalidate();
            return settings;
        }

        [MenuItem("Assets/OneText/Set as Default Font", true)]
        private static bool ValidateSetDefault() => Selection.activeObject is OneFontAsset;

        [MenuItem("Assets/OneText/Set as Default Font", false, 1201)]
        private static void SetDefault()
        {
            var settings = GetOrCreate();
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("_defaultFont").objectReferenceValue = Selection.activeObject;
            serialized.ApplyModifiedProperties();
            OneTextSettings.Invalidate();
            Debug.Log($"OneText: default font is now {Selection.activeObject.name}.");
        }
    }
}
