using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Batch-mode build entry point for the Windows smoke job in CI.
///
/// This is the CI sibling of the local dev project's SmokeBuild.cs: same
/// generated scene, same SmokeSelfTest component, same "exit non-zero the
/// moment anything goes wrong" rule. It only knows how to build the one
/// player CI can both produce and run: a Windows standalone, cross-built on
/// a Linux runner and executed on a Windows one.
///
/// Mono rather than IL2CPP, deliberately. Cross-building Windows IL2CPP
/// needs the Visual Studio toolchain inside the build container, which the
/// plain windows-mono editor image does not carry, and the mobile smoke
/// tiers already put IL2CPP with High stripping through its paces. What only
/// this job can answer is the Windows-shaped part: does the packager ship
/// libHarfBuzzSharp.dll where the player looks for it, does the SDF shader
/// compile for D3D11, does a real Windows player boot, shape and draw.
///
///   -executeMethod SmokeBuildCI.Windows -smokeOut build/windows/OneTextSmoke.exe
/// </summary>
public static class SmokeBuildCI
{
    private const string SceneDir = "Assets/Smoke";
    private const string ScenePath = SceneDir + "/SmokeScene.unity";

    public static void Windows()
    {
        try
        {
            string output = RequiredArg("-smokeOut");
            // Anchor a relative path at the project root, not at whatever
            // working directory the CI container happened to launch Unity from.
            if (!Path.IsPathRooted(output))
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", output));
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                    throw new Exception("Could not switch the active build target to StandaloneWindows64. " +
                                        "Is Windows Build Support installed in this editor?");
            }

            var standalone = NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);

            PlayerSettings.SetApplicationIdentifier(standalone, "com.onetext.smoke");
            PlayerSettings.productName = "OneTextSmoke";
            PlayerSettings.companyName = "OneText";

            // The runner has no one watching the window: the player has to run
            // its checks and quit while unfocused, and must not stall on a
            // resolution dialog or claim the whole desktop.
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 800;
            PlayerSettings.defaultScreenHeight = 600;

            CreateScene();

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.StrictMode,
            };

            Debug.Log("[SmokeBuildCI] building StandaloneWindows64 -> " + output);
            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                string errors = string.Join("\n", report.steps
                    .SelectMany(step => step.messages)
                    .Where(m => m.type == LogType.Error || m.type == LogType.Exception)
                    .Select(m => "  " + m.content)
                    .Take(20));
                throw new Exception("Build " + summary.result + " (" + summary.totalErrors + " errors)\n" + errors);
            }

            Debug.Log("[SmokeBuildCI] OK " + output + " (" + summary.totalSize + " bytes, " + summary.totalTime + ")");
        }
        catch (Exception e)
        {
            Debug.LogError("[SmokeBuildCI] FAILED: " + e.Message + "\n" + e.StackTrace);
            Console.Error.WriteLine("[SmokeBuildCI] FAILED: " + e.Message);
            Console.Error.Flush();
            EditorApplication.Exit(1);
        }
    }

    private static void CreateScene()
    {
        Directory.CreateDirectory(SceneDir);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var host = new GameObject("SmokeSelfTest");
        host.AddComponent<SmokeSelfTest>();

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new Exception("Could not save the generated smoke scene to " + ScenePath);

        AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[SmokeBuildCI] generated scene " + ScenePath);
    }

    private static string RequiredArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        throw new Exception("Missing required argument " + name + " <path>.");
    }
}
