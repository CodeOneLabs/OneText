using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace OneText.Samples.Editor
{
    /// <summary>
    /// The WebGL build behind <c>page~/demo/</c>.
    ///
    /// That build is eleven megabytes of committed binary with no CI behind it
    /// — a Unity licence and a quarter of an hour per deploy was not a trade
    /// worth making for a demo — so the settings that produced it live here
    /// rather than in somebody's memory of which checkboxes they ticked.
    /// Getting one of them wrong is not a build error: Brotli without the
    /// fallback produces a build that works on every machine that has a server
    /// which can set <c>Content-Encoding</c>, and a blank frame on GitHub
    /// Pages, which cannot.
    ///
    /// Menu: Tools &gt; OneText &gt; Samples &gt; Build Web Demo, which writes
    /// beside the project. Or, for a build machine:
    ///
    ///   Unity -batchmode -quit -buildTarget WebGL -projectPath &lt;project&gt;
    ///       -executeMethod OneText.Samples.Editor.DemoWebBuild.Run
    ///
    /// with <c>ONETEXT_WEB_OUT</c> set to the output directory. Copy the
    /// resulting <c>Build/</c> over <c>page~/demo/Build/</c> and leave
    /// <c>page~/demo/index.html</c> alone: that page is hand-written — the
    /// site's palette, a 16:9 frame, a note for visitors on a phone — and the
    /// HTML this emits is thrown away.
    /// </summary>
    public static class DemoWebBuild
    {
        private const string ScenePath = "Assets/OneTextDemo/OneTextPrinciples.unity";

        [MenuItem("Tools/OneText/Samples/Build Web Demo")]
        public static void BuildBesideProject() =>
            Build(Path.Combine(Directory.GetCurrentDirectory(), "WebDemo", "demo"));

        /// <summary>Batch entry point; reads <c>ONETEXT_WEB_OUT</c>.</summary>
        public static void Run()
        {
            string output = Environment.GetEnvironmentVariable("ONETEXT_WEB_OUT");
            if (string.IsNullOrEmpty(output))
                throw new Exception("ONETEXT_WEB_OUT is not set to an output directory.");
            Build(output);
        }

        private static void Build(string output)
        {
            if (!File.Exists(ScenePath))
                DemoSceneBuilder.GeneratePrinciples();

            var web = NamedBuildTarget.WebGL;
            PlayerSettings.companyName = "CodeOneLabs";
            PlayerSettings.productName = "OneText";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.SetScriptingBackend(web, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(web, Il2CppCompilerConfiguration.Master);
            PlayerSettings.SetManagedStrippingLevel(web, ManagedStrippingLevel.High);

            // Brotli *with* the decompression fallback. Plain Brotli expects
            // the server to answer with Content-Encoding, and GitHub Pages
            // cannot set a header; the fallback ships a decompressor in the
            // loader and asks the server for nothing.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
            PlayerSettings.WebGL.dataCaching = true;
            // The stock template. The page's own index.html is committed beside
            // the build, so whatever HTML this emits is discarded.
            PlayerSettings.WebGL.template = "APPLICATION:Default";
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultWebScreenWidth = 1600;
            PlayerSettings.defaultWebScreenHeight = 900;

            // The output files are named after the last path component, and
            // page~/demo/index.html asks for Build/demo.*.
            if (Path.GetFileName(output) != "demo")
                Debug.LogWarning("OneText demo: the output path should end in \"demo\", or the " +
                                 "files will not be named what page~/demo/index.html loads.");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            });

            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("OneText demo: WebGL build " + report.summary.result + ".");

            Debug.Log("OneText demo: built to " + output + " (" +
                      (report.summary.totalSize / (1024f * 1024f)).ToString("0.0") + " MB). " +
                      "Copy its Build/ over page~/demo/Build/.");
        }
    }
}
