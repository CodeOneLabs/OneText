using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// Project Settings &gt; OneText: the Hub, mounted where a Unity project
    /// keeps its project-wide decisions.
    ///
    /// It used to be an inspector for the settings asset with a button to a
    /// window holding everything else, which meant the default font was in one
    /// place and the fonts themselves in another. Project settings is where
    /// people go looking — it is where TextMesh Pro put the same decisions —
    /// so the whole Hub is here now, and the settings asset is one section of
    /// it. This class owns the page's lifetime and the asset's existence;
    /// <see cref="HubSettingsTab"/> owns what the page shows.
    /// </summary>
    public static class OneTextSettingsProvider
    {
        private const string SettingsFolder = "Assets/Resources";
        private const string SettingsPath = SettingsFolder + "/OneTextSettings.asset";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            OneTextHub hub = null;

            return new SettingsProvider(OneTextHub.SettingsPath, SettingsScope.Project)
            {
                label = "OneText",
                keywords = new[]
                {
                    "text", "font", "fallback", "onetext", "atlas", "raycast", "rich text",
                    "wrapping", "auto size", "charset", "dictionary", "doctor", "migration",
                    "quality", "resolution", "sharpness", "canvas scale",
                },
                activateHandler = (_, root) =>
                {
                    hub = OneTextHub.Mount();
                    var ui = hub.CreateUI();
                    ui.style.flexGrow = 1f;
                    // The settings window hands out a panel that is as tall as
                    // its content unless something asks for height. The Hub is
                    // two scrolling columns and has no natural height at all,
                    // so it says so: this is the old window's minimum.
                    ui.style.minHeight = 460f;
                    // And it keeps its scrolling to itself. Without this the
                    // settings window's own scroll view measures the Hub's full
                    // composed height on every layout pass, which on a section
                    // with a few hundred rows is a measurable stutter for a
                    // scrollbar nobody wants.
                    ui.style.overflow = Overflow.Hidden;
                    root.style.flexGrow = 1f;
                    root.Add(ui);

                    // Nothing ticks a settings page. The atlas section watches a
                    // running game, so it is given a clock of its own — at the
                    // rate that section already throttles itself to, since it is
                    // the only one that has ever wanted one.
                    var mounted = hub;
                    root.schedule.Execute(() => mounted.Tick()).Every(500);
                },
                deactivateHandler = () =>
                {
                    OneTextHub.Unmount(hub);
                    hub = null;
                },
            };
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
