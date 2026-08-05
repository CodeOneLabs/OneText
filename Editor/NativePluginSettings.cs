using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Sets the import settings on the vendored HarfBuzz binaries.
    ///
    /// Plugin import settings live in `.meta` files, and a `.meta` with the
    /// wrong platform mask is worse than none: Unity refuses a build when two
    /// plugins of the same name are both enabled for one platform, and every
    /// one of these files is called libHarfBuzzSharp. Hand-writing that YAML is
    /// how it goes wrong quietly, so it is generated here instead, by the API
    /// that defines it — run once when the natives are re-vendored, and the
    /// resulting `.meta` files are what the package ships.
    ///
    /// See Docs/NATIVES.md for where the binaries come from.
    /// </summary>
    public static class NativePluginSettings
    {
        private const string PluginRoot = "Packages/com.onetext.core/Runtime/Plugins";

        private readonly struct Target
        {
            public readonly string Path;      // relative to PluginRoot
            public readonly string Platform;  // BuildTarget name, or "" for the editor-only pass
            public readonly string Cpu;
            public readonly string EditorOs;  // non-empty: also enable for the editor on this OS

            public Target(string path, string platform, string cpu, string editorOs = null)
            {
                Path = path;
                Platform = platform;
                Cpu = cpu;
                EditorOs = editorOs;
            }
        }

        // The editor loads a native plugin for the machine it is running on, and
        // the player loads one for the machine it ships to. Desktop entries
        // therefore claim both; Android and iOS are player-only.
        private static readonly Target[] Targets =
        {
            new Target("macOS/libHarfBuzzSharp.dylib", "StandaloneOSX", "AnyCPU", "OSX"),
            new Target("Windows/x86_64/libHarfBuzzSharp.dll", "StandaloneWindows64", "x86_64", "Windows"),
            new Target("Windows/x86/libHarfBuzzSharp.dll", "StandaloneWindows", "x86", null),
            // Editor too, with a CPU of its own: on Windows-on-ARM this is the
            // one the editor can load, and the CPU field is what keeps it from
            // colliding with the x86_64 build beside it.
            new Target("Windows/ARM64/libHarfBuzzSharp.dll", "StandaloneWindows64", "ARM64", "Windows"),
            new Target("Linux/x86_64/libHarfBuzzSharp.so", "StandaloneLinux64", "x86_64", "Linux"),
            new Target("Android/arm64-v8a/libHarfBuzzSharp.so", "Android", "ARM64", null),
            new Target("Android/armeabi-v7a/libHarfBuzzSharp.so", "Android", "ARMv7", null),
            new Target("Android/x86_64/libHarfBuzzSharp.so", "Android", "X86_64", null),
            // One plugin for iOS, not two. Device and simulator binaries have
            // the same name, and the plugin importer has no device/simulator
            // switch to keep them apart — an .xcframework is the format that
            // carries both and lets Xcode pick, which is why Apple invented it.
            new Target("iOS/libHarfBuzzSharp.xcframework", "iOS", "AnyCPU", null),
        };

        [MenuItem("Tools/OneText/Apply Native Plugin Settings")]
        public static void Apply()
        {
            var applied = new List<string>();
            foreach (var target in Targets)
            {
                string path = $"{PluginRoot}/{target.Path}";
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null)
                {
                    Debug.LogWarning($"OneText: no plugin at {path} — skipped.");
                    continue;
                }

                // Start from "nothing", so a binary added for one platform can
                // never be silently enabled for another by an inherited default.
                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(false);
                foreach (var build in (BuildTarget[])System.Enum.GetValues(typeof(BuildTarget)))
                {
                    if ((int)build < 0) continue; // obsolete members throw on use
                    try { importer.SetCompatibleWithPlatform(build, false); }
                    catch (System.ArgumentException) { /* platform not installed */ }
                }

                if (System.Enum.TryParse<BuildTarget>(target.Platform, out var platform))
                {
                    importer.SetCompatibleWithPlatform(platform, true);
                    importer.SetPlatformData(platform, "CPU", target.Cpu);
                }

                if (!string.IsNullOrEmpty(target.EditorOs))
                {
                    importer.SetCompatibleWithEditor(true);
                    importer.SetEditorData("OS", target.EditorOs);
                    importer.SetEditorData("CPU", target.Cpu);
                }

                if (target.Platform == "iOS")
                {
                    // A dynamic framework has to be embedded and signed, or it
                    // is present in the Xcode project and absent from the app.
                    importer.SetPlatformData(BuildTarget.iOS, "AddToEmbeddedBinaries", "true");
                }

                importer.SaveAndReimport();
                applied.Add(target.Path);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"OneText: applied native plugin settings to {applied.Count} binaries:\n  " +
                string.Join("\n  ", applied));
        }

        /// <summary>Batch-mode entry point: `-executeMethod OneText.Editor.NativePluginSettings.ApplyBatch`.</summary>
        public static void ApplyBatch()
        {
            Apply();
            EditorApplication.Exit(0);
        }

        [MenuItem("Tools/OneText/Report Native Plugin Coverage")]
        public static void Report()
        {
            var report = new System.Text.StringBuilder("OneText native plugins:\n");
            foreach (var target in Targets)
            {
                string path = $"{PluginRoot}/{target.Path}";
                string full = Path.GetFullPath(path);
                bool present = File.Exists(full) || Directory.Exists(full);
                long bytes = present && File.Exists(full) ? new FileInfo(full).Length : 0;
                report.AppendLine($"  {(present ? "ok " : "MISSING")} {target.Platform,-20} " +
                    $"{target.Cpu,-8} {target.Path}{(bytes > 0 ? $" ({bytes / 1024} KB)" : "")}");
            }
            Debug.Log(report.ToString());
        }
    }
}
