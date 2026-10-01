using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Writes the on-demand fonts out as plain font files, for
    /// <see cref="FileFontSource"/> to map at runtime.
    ///
    /// <para>The settings' on-demand list names fonts by key, and with the
    /// font source set to <see cref="OneFontSourceKind.StreamingAssets"/> a key
    /// is read from <c>StreamingAssets/OneTextFonts/&lt;key&gt;.&lt;ext&gt;</c> where the
    /// file exists. Nobody has to keep a second copy of each font in the
    /// project for that: <see cref="Build"/> writes the files from the font
    /// assets just before a player build for a platform that can map them, and
    /// deletes them after. Android and Web are skipped — their StreamingAssets
    /// is not a folder of files — and read the assets from Resources as
    /// before.</para>
    ///
    /// <para>A build that fails before it finishes leaves the files behind;
    /// the next build overwrites them. Keep
    /// <c>Assets/StreamingAssets/OneTextFonts/</c> out of version control.</para>
    /// </summary>
    public static class OneFontFiles
    {
        /// <summary>Where the files go in the project.</summary>
        public const string StreamingRoot = "Assets/StreamingAssets/" + FileFontSource.DefaultFolder;

        /// <summary>
        /// Whether a player for <paramref name="target"/> can map files out of
        /// StreamingAssets. Android keeps them inside the APK; Web has URLs.
        /// </summary>
        public static bool TargetReadsFiles(BuildTarget target) =>
            target != BuildTarget.Android && target != BuildTarget.WebGL;

        /// <summary>
        /// Writes every on-demand font in <paramref name="settings"/> under
        /// <paramref name="root"/> as <c>&lt;key&gt;.&lt;ext&gt;</c>, the extension read off
        /// the font itself. Returns the files written. A key whose asset cannot
        /// be found or has no font is skipped with a warning; at runtime that
        /// key falls back to the asset.
        /// </summary>
        public static List<string> Export(OneTextSettings settings, string root)
        {
            var written = new List<string>();
            if (settings == null || string.IsNullOrEmpty(root)) return written;
            var kind = settings.FontSource == OneFontSourceKind.StreamingAssets
                ? OneFontSourceKind.Resources
                : settings.FontSource;

            foreach (var reference in settings.OnDemandFonts)
            {
                if (string.IsNullOrEmpty(reference.Key)) continue;
                var asset = OneFontReferences.Find(reference, kind);
                var bytes = asset != null ? asset.GetFontBytes() : null;
                if (bytes == null || bytes.Length == 0)
                {
                    Debug.LogWarning($"OneText: no font to write for on-demand key \"{reference.Key}\"; " +
                                     "the player will load it from Resources instead.");
                    continue;
                }

                string path = Path.Combine(root, reference.Key + ExtensionOf(bytes));
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
                if (!File.Exists(path) || !Same(path, bytes)) File.WriteAllBytes(path, bytes);
                written.Add(path);
            }
            return written;
        }

        /// <summary><c>.otf</c> for CFF outlines, <c>.ttc</c>/<c>.otc</c> for collections, <c>.ttf</c> otherwise.</summary>
        public static string ExtensionOf(byte[] font)
        {
            if (font == null || font.Length < 4) return ".ttf";
            uint tag = (uint)(font[0] << 24 | font[1] << 16 | font[2] << 8 | font[3]);
            switch (tag)
            {
                case 0x4F54544F: return ".otf"; // 'OTTO'
                case 0x74746366: return ".ttc"; // 'ttcf'
                default: return ".ttf";
            }
        }

        private static bool Same(string path, byte[] bytes)
        {
            var info = new FileInfo(path);
            if (info.Length != bytes.Length) return false;
            var existing = File.ReadAllBytes(path);
            for (int i = 0; i < existing.Length; i++)
                if (existing[i] != bytes[i]) return false;
            return true;
        }

        /// <summary>
        /// Writes the files before a player build that reads them, removes them
        /// after. Only when the project settings load fonts from
        /// StreamingAssets; nothing happens otherwise.
        /// </summary>
        internal sealed class Build : IPreprocessBuildWithReport, IPostprocessBuildWithReport
        {
            private static readonly List<string> s_written = new List<string>();

            public int callbackOrder => 0;

            public void OnPreprocessBuild(BuildReport report)
            {
                s_written.Clear();
                var settings = OneTextSettings.Instance;
                if (settings == null || settings.FontSource != OneFontSourceKind.StreamingAssets) return;
                if (!TargetReadsFiles(report.summary.platform))
                {
                    // A desktop build's leftovers would ship inside the APK for nothing.
                    Remove(Directory.Exists(StreamingRoot)
                        ? new List<string>(Directory.GetFiles(StreamingRoot, "*", SearchOption.AllDirectories))
                        : new List<string>());
                    return;
                }
                s_written.AddRange(Export(settings, StreamingRoot));
                Debug.Log($"OneText: wrote {s_written.Count} on-demand font file(s) to {StreamingRoot} " +
                          "for this build.");
            }

            public void OnPostprocessBuild(BuildReport report)
            {
                Remove(s_written);
                s_written.Clear();
            }

            private static void Remove(List<string> files)
            {
                foreach (string file in files)
                {
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"OneText: could not remove {file}: {e.Message}");
                    }
                }
                DeleteEmpty(StreamingRoot);
            }

            private static void DeleteEmpty(string directory)
            {
                if (!Directory.Exists(directory)) return;
                foreach (string child in Directory.GetDirectories(directory)) DeleteEmpty(child);
                if (Directory.GetFileSystemEntries(directory).Length > 0) return;
                Directory.Delete(directory);
                if (File.Exists(directory + ".meta")) File.Delete(directory + ".meta");
            }
        }
    }
}
