using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// One label, decorated the way a shipped project decorates one, on both
    /// sides of the conversion.
    ///
    /// The migration proof next door photographs a whole screen and answers
    /// "did everything survive". This one answers the narrower question that a
    /// screenshot from a real project asked and no test could: does the text
    /// come out the same weight, with the same edge. It takes its numbers from
    /// the font asset's own material rather than from a fixture, so what it
    /// renders is what that project renders.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.TmpDecorationProofGenerator.Generate
    ///      -oneOut &lt;dir&gt; -oneFont &lt;TMP font asset path&gt;
    ///      [-oneText &lt;string&gt;] [-oneSize &lt;points&gt;]
    /// </summary>
    public static class TmpDecorationProofGenerator
    {
        private const string WorkFolder = "Assets/OneTextDecorationProof";
        private const string ScenePath = WorkFolder + "/DecorationProof.unity";

        private const int Width = 1600;
        private const int Half = 300;

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            string fontPath = GetArg("-oneFont");
            string body = GetArg("-oneText") ??
                          "이번 전투에서 10의 피해를 입을 때마다 이번 전투에서의 공격력이 1만큼 증가됩니다. (최대 5)";
            float size = float.TryParse(GetArg("-oneSize"), out float parsed) ? parsed : 40f;

            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(WorkFolder);
            AssetDatabase.Refresh();
            Shader.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);

            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
            if (font == null)
            {
                Debug.LogError($"OneText decoration proof: no TMP font asset at '{fontPath}'. " +
                               "Pass -oneFont with the path to the one the project uses.");
                return;
            }

            var material = font.material;

            // The control: the same label with the decoration taken off both
            // sides. Whatever difference is left at zero is not the decoration's
            // and no amount of mapping it better will move it.
            if (GetArg("-onePlain") != null)
            {
                material.SetFloat("_FaceDilate", 0f);
                material.SetFloat("_OutlineWidth", 0f);
                material.SetFloat("_OutlineSoftness", 0f);
            }

            Debug.Log($"ONETEXT-PROOF: material '{material.name}' on shader '{material.shader.name}' " +
                      $"— _FaceDilate {material.GetFloat("_FaceDilate"):0.###}, " +
                      $"_OutlineWidth {material.GetFloat("_OutlineWidth"):0.###}, " +
                      $"_OutlineSoftness {material.GetFloat("_OutlineSoftness"):0.###}, " +
                      $"_WeightNormal {material.GetFloat("_WeightNormal"):0.###}, " +
                      $"_ScaleRatioA {material.GetFloat("_ScaleRatioA"):0.###}");

            BuildScene(font, body, size);
            var before = Render(new Color(0.06f, 0.30f, 0.28f, 1f));

            var report = ComponentMigration.Apply(new ComponentMigration.Options
            {
                AllScenes = true,
                IncludeScenes = true,
                IncludePrefabs = false,
                OnlyContainers = new List<string> { ScenePath },
                AdoptProjectFontDefaults = false,
            });
            Debug.Log($"ONETEXT-PROOF: {report.Summary()}");

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            ReportDecoration();

            var after = Render(new Color(0.06f, 0.30f, 0.28f, 1f));

            string path = Path.Combine(outDir, "onetext-decoration.png");
            Composite(before, after, path);
            Debug.Log($"ONETEXT-PROOF: written to {path}");
        }

        // ------------------------------------------------------------- subject

        private static void BuildScene(TMP_FontAsset font, string body, float size)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceCamera;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(Width, Half);

            var go = new GameObject("Subject", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvasGo.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Width - 80f, Half - 60f);
            rect.anchoredPosition = new Vector2(40f, -30f);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = body;
            text.fontSize = size;
            text.color = Color.white;
            // Qualified: OneText declares the same enum name for parity, and an
            // unqualified one here is ambiguous between the two.
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
        }

        /// <summary>The numbers the conversion actually wrote, in one log line.</summary>
        private static void ReportDecoration()
        {
            var label = UnityEngine.Object.FindFirstObjectByType<OneTextLabel>(
                FindObjectsInactive.Include);
            if (label == null)
            {
                Debug.LogError("ONETEXT-PROOF: nothing was converted");
                return;
            }

            var serialized = new SerializedObject(label);
            var decoration = (TextDecoration)serialized.FindProperty("_decoration").boxedValue;
            Debug.Log($"ONETEXT-PROOF: decoration — outline {decoration.HasOutline} " +
                      $"width {decoration.OutlineWidth:0.###} soft {decoration.OutlineSoftness:0.###} " +
                      $"colour {(Color)decoration.OutlineColor}; face {decoration.HasFace} " +
                      $"dilate {decoration.FaceDilate:0.###}; shadow {decoration.HasShadow}; " +
                      $"glow {decoration.HasGlow}; font size {label.FontSize:0.##}");
        }

        // -------------------------------------------------------------- render

        private static Color[] Render(Color background)
        {
            var camGo = new GameObject("ProofCamera");
            var camera = camGo.AddComponent<Camera>();
            camera.backgroundColor = background;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographic = true;
            var target = new RenderTexture(Width, Half, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            if (canvas != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 5f;
            }

            // Twice: a OneText label rasterises the glyphs it needs on its first
            // layout and only meshes them on the next one.
            Canvas.ForceUpdateCanvases();
            Canvas.ForceUpdateCanvases();
            camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(Width, Half, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, Width, Half), 0, 0);
            texture.Apply(false);
            RenderTexture.active = previous;

            var pixels = texture.GetPixels();
            camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(camGo);
            return pixels;
        }

        private static void Composite(Color[] before, Color[] after, string path)
        {
            const int Gap = 6;
            int height = Half * 2 + Gap;
            var sheet = new Texture2D(Width, height, TextureFormat.RGBA32, false);

            var divider = new Color(0.9f, 0.35f, 0.2f, 1f);
            var row = new Color[Width];
            for (int x = 0; x < Width; x++) row[x] = divider;

            // ReadPixels gives bottom-up rows, so the converted half goes
            // underneath by being written first: TextMesh Pro on top, OneText
            // below it, the way the two were compared by hand.
            sheet.SetPixels(0, 0, Width, Half, after);
            for (int y = Half; y < Half + Gap; y++) sheet.SetPixels(0, y, Width, 1, row);
            sheet.SetPixels(0, Half + Gap, Width, Half, before);
            sheet.Apply(false);

            File.WriteAllBytes(path, sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
