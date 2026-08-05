using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Turns a .ttf/.otf in the project into a <see cref="OneFontAsset"/>.
    /// Unity's own importer owns those extensions, so OneText cannot import
    /// them directly — this is the one-click bridge instead, and it is the only
    /// step between dropping a font in the project and rendering with it.
    /// </summary>
    public static class OneFontAssetCreator
    {
        private const string MenuPath = "Assets/OneText/Create Font Asset";

        [MenuItem(MenuPath, true)]
        private static bool ValidateCreate() => SelectedFontPaths().Length > 0;

        [MenuItem(MenuPath, false, 1200)]
        private static void Create()
        {
            var paths = SelectedFontPaths();
            Object last = null;
            foreach (var path in paths)
            {
                var asset = CreateFromFontFile(path);
                if (asset != null) last = asset;
            }
            if (last == null) return;

            AssetDatabase.SaveAssets();
            Selection.activeObject = last;
            EditorGUIUtility.PingObject(last);
        }

        /// <summary>Creates (or overwrites) the font asset beside a font file.</summary>
        public static OneFontAsset CreateFromFontFile(string fontPath)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fontPath);
            }
            catch (IOException error)
            {
                Debug.LogError($"OneText: cannot read {fontPath} — {error.Message}");
                return null;
            }

            string directory = Path.GetDirectoryName(fontPath) ?? "Assets";
            string baseName = Path.GetFileNameWithoutExtension(fontPath);
            string assetPath = Path.Combine(directory, baseName + " Font.asset").Replace('\\', '/');

            var asset = AssetDatabase.LoadAssetAtPath<OneFontAsset>(assetPath);
            bool isNew = asset == null;
            if (isNew) asset = ScriptableObject.CreateInstance<OneFontAsset>();

            asset.Initialize(bytes, ReadFamilyName(bytes, baseName), fontPath);
            if (isNew) AssetDatabase.CreateAsset(asset, assetPath);
            EditorUtility.SetDirty(asset);

            float ratio = bytes.Length > 0 ? asset.StoredSize / (float)bytes.Length : 1f;
            Debug.Log($"OneText: {assetPath} ({bytes.Length / 1024} KB font, " +
                      $"stored {asset.StoredSize / 1024} KB — {ratio:P0})");
            return asset;
        }

        private static string[] SelectedFontPaths()
        {
            var result = new System.Collections.Generic.List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".ttf" || extension == ".otf" || extension == ".ttc")
                    result.Add(path);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Pulls the family name out of the font's <c>name</c> table (id 1),
        /// so the asset shows a real name instead of the file name.
        /// </summary>
        private static string ReadFamilyName(byte[] font, string fallback)
        {
            try
            {
                int tableCount = ReadUShort(font, 4);
                int nameOffset = -1;
                for (int i = 0; i < tableCount; i++)
                {
                    int record = 12 + i * 16;
                    if (font[record] == 'n' && font[record + 1] == 'a' &&
                        font[record + 2] == 'm' && font[record + 3] == 'e')
                    {
                        nameOffset = (int)ReadULong(font, record + 8);
                        break;
                    }
                }
                if (nameOffset < 0) return fallback;

                int count = ReadUShort(font, nameOffset + 2);
                int storage = nameOffset + ReadUShort(font, nameOffset + 4);
                for (int i = 0; i < count; i++)
                {
                    int record = nameOffset + 6 + i * 12;
                    if (ReadUShort(font, record + 6) != 1) continue; // name id 1 = family

                    int length = ReadUShort(font, record + 8);
                    int offset = storage + ReadUShort(font, record + 10);
                    int platform = ReadUShort(font, record);
                    string value = platform == 3 || platform == 0
                        ? System.Text.Encoding.BigEndianUnicode.GetString(font, offset, length)
                        : System.Text.Encoding.ASCII.GetString(font, offset, length);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch (System.Exception)
            {
                // A malformed name table is not worth failing the import over.
            }
            return fallback;
        }

        private static int ReadUShort(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

        private static uint ReadULong(byte[] data, int offset) =>
            ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
            ((uint)data[offset + 2] << 8) | data[offset + 3];
    }
}
