using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Turns a font asset into the <see cref="OneFontReference"/> the settings
    /// keep for it: the key its source loads it by, the language it is for, and
    /// the coverage that lets a label reach for it while it is unloaded.
    ///
    /// For editor scripts that set a project up, and for the Hub's on-demand
    /// card, which is the same thing with buttons.
    /// </summary>
    public static class OneFontReferences
    {
        private const string ResourcesFolder = "/Resources/";

        /// <summary>
        /// The key <paramref name="asset"/> loads by from a source of this kind,
        /// or null when it cannot be loaded that way at all — a font outside
        /// every Resources folder, asked for as a Resources key.
        ///
        /// For Addressables this is the asset path, which is the address the
        /// Addressables window gives an entry unless somebody renames it. A
        /// renamed address is typed into the settings by hand.
        /// </summary>
        public static string KeyFor(OneFontAsset asset, OneFontSourceKind kind)
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;
            if (kind == OneFontSourceKind.Addressables) return path;

            int at = path.LastIndexOf(ResourcesFolder, StringComparison.Ordinal);
            if (at < 0) return null;
            string inside = path.Substring(at + ResourcesFolder.Length);
            int dot = inside.LastIndexOf('.');
            return dot > 0 ? inside.Substring(0, dot) : inside;
        }

        /// <summary>
        /// A reference to <paramref name="asset"/>, with its coverage read off
        /// the face now. <paramref name="language"/> defaults to the asset's
        /// own tag. Throws when the asset cannot be loaded by that kind of key,
        /// because a reference that names nothing fails quietly at runtime.
        /// </summary>
        public static OneFontReference Make(OneFontAsset asset, OneFontSourceKind kind,
            string language = null)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            string key = KeyFor(asset, kind);
            if (key == null)
                throw new ArgumentException($"'{AssetDatabase.GetAssetPath(asset)}' is not under a " +
                    "Resources folder, so a Resources source cannot load it.", nameof(asset));

            var font = asset.Font;
            var coverage = font != null && font.IsValid ? FontCoverage.Of(font) : Array.Empty<int>();
            return new OneFontReference(key,
                string.IsNullOrEmpty(language) ? asset.Language : language, coverage);
        }

        /// <summary>
        /// The asset a reference names, looked up the way its source would,
        /// or null. Editor only: it searches the project rather than loading.
        /// </summary>
        public static OneFontAsset Find(OneFontReference reference, OneFontSourceKind kind)
        {
            if (string.IsNullOrEmpty(reference.Key)) return null;
            if (kind == OneFontSourceKind.Addressables)
                return AssetDatabase.LoadAssetAtPath<OneFontAsset>(reference.Key);

            foreach (string guid in AssetDatabase.FindAssets("t:OneFontAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<OneFontAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && KeyFor(asset, kind) == reference.Key) return asset;
            }
            return null;
        }

        /// <summary>
        /// Replaces the settings' on-demand list and saves the change the way
        /// the inspector would: undoable, dirty, and seen by live labels.
        /// </summary>
        public static void Assign(OneTextSettings settings, IEnumerable<OneFontReference> references)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Undo.RecordObject(settings, "On-demand fonts");
            settings.SetOnDemandFonts(references);
            EditorUtility.SetDirty(settings);
        }
    }
}
