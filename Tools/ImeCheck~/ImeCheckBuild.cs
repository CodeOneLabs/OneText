using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using OneText;
using OneText.UGUI;

/// <summary>
///   -executeMethod ImeCheckBuild.Windows -imeOut &lt;path to .exe&gt;
/// </summary>
public static class ImeCheckBuild
{
    private const string SceneDir = "Assets/ImeCheck";
    private const string ScenePath = SceneDir + "/ImeCheckScene.unity";
    private const string FontSource = "Assets/PretendardVariable.ttf";
    private const string FontName = "ImeCheckFont.ttf";

    public static void Windows()
    {
        try
        {
            string output = RequiredArg("-imeOut");
            if (!Path.IsPathRooted(output))
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", output));
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            string streaming = Path.Combine(Application.dataPath, "StreamingAssets");
            Directory.CreateDirectory(streaming);
            File.Copy(Path.GetFullPath(FontSource), Path.Combine(streaming, FontName), true);
            AssetDatabase.Refresh();

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                throw new Exception("no Windows Build Support in this editor");

            var standalone = NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.productName = "OneTextImeCheck";
            PlayerSettings.companyName = "OneText";
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 900;
            PlayerSettings.defaultScreenHeight = 620;
            PlayerSettings.resizableWindow = true;

            CreateScene();

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.StrictMode,
            });

            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("[ImeCheckBuild] FAILED: " + report.summary.result);

            Debug.Log($"[ImeCheckBuild] OK -> {output} ({report.summary.totalSize} bytes)");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[ImeCheckBuild] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    private static void CreateScene()
    {
        Directory.CreateDirectory(SceneDir);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var events = new GameObject("EventSystem", typeof(EventSystem));
        AddInputModule(events);

        var root = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        root.transform.SetParent(canvasGo.transform, false);
        var rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 1f);
        rootRect.anchorMax = new Vector2(0.5f, 1f);
        rootRect.pivot = new Vector2(0.5f, 1f);
        rootRect.anchoredPosition = new Vector2(0f, -32f);
        rootRect.sizeDelta = new Vector2(700f, 64f);
        root.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
        textGo.transform.SetParent(root.transform, false);
        var textRect = textGo.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 6f);
        textRect.offsetMax = new Vector2(-12f, -6f);

        var label = textGo.AddComponent<OneTextLabel>();
        label.FontSize = 32f;
        label.Wrap = TextWrap.NoWrap;

        var field = root.AddComponent<OneTextInputField>();
        var so = new SerializedObject(field);
        so.FindProperty("_textComponent").objectReferenceValue = label;
        so.ApplyModifiedPropertiesWithoutUndo();

        var hudGo = new GameObject("ImeCheckHud");
        var hud = hudGo.AddComponent<ImeCheckHud>();
        hud.Field = field;
        hud.FontFileName = FontName;

        EditorSceneManager.SaveScene(scene, ScenePath);
    }

    private static void AddInputModule(GameObject go)
    {
        // Whichever module this project's input handler actually has.
        var type = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (type != null) { go.AddComponent(type); return; }
        var legacy = Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
        if (legacy != null) go.AddComponent(legacy);
    }

    private static string RequiredArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
        throw new Exception("missing " + name);
    }
}
