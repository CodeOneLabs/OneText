using System;
using System.IO;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Walks the path a new user actually takes — create a font asset from a
    /// .ttf, make it the project default, drop in a label that has no font of
    /// its own — and renders the result. If this produces text, no onboarding
    /// step is missing.
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.OnboardingProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class OnboardingProofGenerator
    {
        private const string SourceFont = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";
        private const string WorkFolder = "Assets/OneTextOnboarding";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);

            // Step 1: a font file in the project becomes a font asset.
            Directory.CreateDirectory(WorkFolder);
            string latinCopy = CopyIntoProject(SourceFont, "NotoSans.ttf");
            string arabicCopy = CopyIntoProject(ArabicFont, "NotoSansArabic.ttf");
            AssetDatabase.Refresh();

            var latinAsset = OneFontAssetCreator.CreateFromFontFile(latinCopy);
            var arabicAsset = OneFontAssetCreator.CreateFromFontFile(arabicCopy);
            AssetDatabase.SaveAssets();

            // Step 2: it becomes the project default, with a fallback behind it.
            var settings = OneTextSettingsProvider.GetOrCreate();
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("_defaultFont").objectReferenceValue = latinAsset;
            var fallbacks = serialized.FindProperty("_fallbackFonts");
            fallbacks.arraySize = 1;
            fallbacks.GetArrayElementAtIndex(0).objectReferenceValue = arabicAsset;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            OneTextSettings.Invalidate();
            OneTextSettings.Instance = settings;

            // Step 3: a label with nothing assigned renders anyway.
            RenderProof(Path.Combine(outDir, "onetext-onboarding.png"), latinAsset);

            Debug.Log($"Onboarding proof written to {outDir}");
        }

        private static string CopyIntoProject(string packagePath, string fileName)
        {
            string destination = $"{WorkFolder}/{fileName}";
            File.Copy(Path.GetFullPath(packagePath), destination, overwrite: true);
            return destination;
        }

        private static void RenderProof(string path, OneFontAsset fontAsset)
        {
            const int W = 1400, H = 420;

            var camGo = new GameObject("ProofCamera");
            var camera = camGo.AddComponent<Camera>();
            camera.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographic = true;
            var target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;

            var canvasGo = new GameObject("ProofCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 5f;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(W, H);

            // No SetFont, no font assigned: the project default has to carry it,
            // and the Arabic has to come from the project fallback.
            var label = NewLabel(canvasGo.transform, new Rect(40f, 40f, 1320f, 120f));
            label.Text = "No font assigned — the project default renders it, مرحبا from the fallback.";
            label.FontSize = 34f;

            var assigned = NewLabel(canvasGo.transform, new Rect(40f, 180f, 1320f, 120f));
            assigned.Font = fontAsset;
            assigned.Text = $"Font asset: {fontAsset.FamilyName} — " +
                            $"{fontAsset.FontFileSize / 1024} KB font stored as " +
                            $"{fontAsset.StoredSize / 1024} KB in the asset.";
            assigned.FontSize = 30f;
            assigned.color = new Color(1f, 1f, 1f, 0.75f);

            var shared = NewLabel(canvasGo.transform, new Rect(40f, 300f, 1320f, 80f));
            shared.Text = "Both labels share one atlas and one material.";
            shared.FontSize = 26f;
            shared.color = new Color(0.6f, 0.85f, 1f, 1f);

            Canvas.ForceUpdateCanvases();
            camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = previous;
            File.WriteAllBytes(path, tex.EncodeToPNG());

            Debug.Log($"Onboarding: label material shared = " +
                      $"{ReferenceEquals(label.material, shared.material)}");

            UnityEngine.Object.DestroyImmediate(tex);
            camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        private static OneTextLabel NewLabel(Transform parent, Rect rect)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<OneTextLabel>();

            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            label.VerticalAlignment = VerticalAlignment.Top;
            label.color = Color.white;
            return label;
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }
    }
}
