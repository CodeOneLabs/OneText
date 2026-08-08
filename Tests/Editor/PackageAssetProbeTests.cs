using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText;
using UnityEditor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// A probe, not a gate. Nothing here asserts.
    ///
    /// On a game-ci runner — Linux and Windows alike, cold cache, working
    /// graphics device — every test that loads something through Unity's asset
    /// system fails: the SDF shader, the Hub's UXML, the Hub's USS. Every test
    /// that reads a file with File.ReadAllBytes passes, and so does every test
    /// that only needs the package's code. Those are the same three assets
    /// because they are the only three this suite loads that way, so the shape
    /// of the failure is not "shaders do not import" but "this package's assets
    /// are not in the AssetDatabase", and the run log cannot tell the two
    /// apart: it prints no importer line for .cs either, and the code compiles.
    ///
    /// So this walks the four steps between a file on disk and a typed object
    /// and prints where the chain breaks. Delete it once that is known.
    /// </summary>
    public class PackageAssetProbeTests
    {
        private const string PackageName = "com.onetext.core";
        private const string PackageRoot = "Packages/" + PackageName;

        private static readonly string[] Suspects =
        {
            PackageRoot + "/Runtime/Shaders/Resources/OneText-SDF.shader",
            PackageRoot + "/Editor/Hub/UI/OneTextHub.uxml",
            PackageRoot + "/Editor/Hub/UI/OneTextHub.uss",
        };

        [Test]
        public void Probe_WhereThePackagesAssetsStop()
        {
            var report = new StringBuilder();
            report.AppendLine("[probe] ---- package asset chain ----");
            report.AppendLine($"[probe] platform={Application.platform} unity={Application.unityVersion}");
            report.AppendLine($"[probe] cwd={Directory.GetCurrentDirectory()}");

            // 1. Does the package manager know where the package lives?
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                PackageRoot + "/package.json");
            report.AppendLine(info == null
                ? "[probe] PackageInfo: NULL — the package manager does not resolve this path"
                : $"[probe] PackageInfo: source={info.source} version={info.version}\n" +
                  $"[probe]   assetPath={info.assetPath}\n" +
                  $"[probe]   resolvedPath={info.resolvedPath}");

            // 2. Is the file on disk, at the path the tests use?
            foreach (string path in Suspects)
            {
                string full = Path.GetFullPath(path);
                report.AppendLine($"[probe] onDisk {File.Exists(full),-5} {full}");
            }

            // 3. Is it in the AssetDatabase at all? Counting the package's own
            // entries separates "this asset is missing" from "none of them are".
            var byExtension = new Dictionary<string, int>();
            int owned = 0;
            foreach (string p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith(PackageRoot)) continue;
                owned++;
                string ext = Path.GetExtension(p);
                byExtension.TryGetValue(ext, out int n);
                byExtension[ext] = n + 1;
            }
            report.AppendLine($"[probe] AssetDatabase entries under {PackageRoot}: {owned}");
            foreach (var pair in byExtension)
                report.AppendLine($"[probe]   {pair.Key,-12} {pair.Value}");

            // AssetPathToGUID rather than AssetPathExists: the latter is a
            // Unity 6 call and this suite also runs on 2022.3.
            foreach (string path in Suspects)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                report.AppendLine($"[probe] guid {(string.IsNullOrEmpty(guid) ? "NONE" : guid),-34} {path}");
            }

            // 4. If it is in there, does it come back as the type it should be?
            report.AppendLine($"[probe] FindAssets t:Shader under package: " +
                              $"{AssetDatabase.FindAssets("t:Shader", new[] { PackageRoot }).Length}");
            var loaded = AssetDatabase.LoadAssetAtPath<Shader>(Suspects[0]);
            report.AppendLine($"[probe] LoadAssetAtPath<Shader>: {(loaded == null ? "NULL" : loaded.name)}");

            var main = AssetDatabase.LoadMainAssetAtPath(Suspects[0]);
            report.AppendLine($"[probe] LoadMainAssetAtPath type: " +
                              $"{(main == null ? "NULL" : main.GetType().FullName)}");

            var fromResources = Resources.Load<Shader>(SharedGlyphAtlas.ShaderResourcePath);
            report.AppendLine($"[probe] Resources.Load<Shader>(\"{SharedGlyphAtlas.ShaderResourcePath}\"): " +
                              $"{(fromResources == null ? "NULL" : fromResources.name)}");

            report.AppendLine("[probe] ---- end ----");
            Debug.Log(report.ToString());
        }
    }
}
