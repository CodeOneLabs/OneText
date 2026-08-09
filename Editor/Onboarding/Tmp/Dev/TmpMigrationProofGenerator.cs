using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// The migration, performed on a scene built for the purpose, photographed
    /// on both sides of the swap.
    ///
    /// A test can assert that a field holds the number it should. It cannot
    /// assert that the label still looks like the label, and "looks like the
    /// label" is the only thing a person migrating a project actually cares
    /// about. So this builds the three components a real screen is made of — a
    /// rich-text TMP label with auto-sizing on, a legacy UnityEngine.UI.Text,
    /// and a TMP input field with a persistent listener wired in the inspector
    /// — renders them, runs the real scan and the real conversion over the real
    /// saved scene, and renders the same camera again.
    ///
    /// It lives in the gated assembly because building the "before" needs TMP.
    /// Everything after the shutter is the shipping code path.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.TmpMigrationProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class TmpMigrationProofGenerator
    {
        private const string WorkFolder = "Assets/OneTextMigrationProof";
        private const string ScenePath = WorkFolder + "/MigrationProof.unity";
        private const string CaptionFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private const int Width = 1400;
        private const int Half = 460;

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            Shader.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);

            Directory.CreateDirectory(WorkFolder);
            var captionFont = CaptionFontAsset();

            BuildScene();

            var before = Render("BEFORE  ·  TextMesh Pro and UnityEngine.UI.Text", captionFont);

            var report = ComponentMigration.Apply(new ComponentMigration.Options
            {
                AllScenes = true,
                IncludeScenes = true,
                IncludePrefabs = false,
                OnlyContainers = new List<string> { ScenePath },
                AdoptProjectFontDefaults = true,
            });

            foreach (var finding in report.Findings)
                Debug.Log($"OneText migration proof: {finding}");
            Debug.Log($"OneText migration proof: {report.Summary()}; " +
                      $"{report.Converted} converted, {report.FontsCreated} font asset(s), " +
                      $"{report.Relinked} reference(s) re-pointed.");

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Verify();

            // The asset database was saved and refreshed in between, so the
            // caption font is asked for again rather than held across it.
            captionFont = CaptionFontAsset();
            var after = Render("AFTER  ·  OneTextLabel, OneTextMesh, OneTextInputField", captionFont);

            string path = Path.Combine(outDir, "onetext-migration.png");
            Composite(before, after, path);
            Debug.Log($"OneText migration proof written to {path}");
        }

        // ------------------------------------------------------------- subject

        private static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(Width, Half);

            // A rich-text TMP label with auto-sizing on: the shape of every
            // headline in every project that ever installed TMP.
            var headline = NewChild(canvasGo.transform, "Headline",
                new Rect(40f, 24f, 1320f, 90f));
            var tmp = headline.AddComponent<TextMeshProUGUI>();
            tmp.text = "<b>Leaving TMP</b> is a <color=#7fd1ff>scan</color> and a diff.";
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 24f;
            tmp.fontSizeMax = 54f;
            tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            tmp.lineSpacing = 10f;
            tmp.color = Color.white;

            // Legacy uGUI text, which is what is left in the corners.
            var legacy = NewChild(canvasGo.transform, "Legacy",
                new Rect(40f, 130f, 1320f, 60f));
            var text = legacy.AddComponent<Text>();
            text.text = "UnityEngine.UI.Text, still here from before the last migration.";
            text.font = LegacyFont();
            text.fontSize = 28;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(1f, 1f, 1f, 0.75f);

            // An input field with its two labels and one listener wired the way
            // a person wires them: in the inspector, where nothing but this
            // module can move them.
            var fieldGo = NewChild(canvasGo.transform, "Input", new Rect(40f, 210f, 800f, 64f));
            var background = fieldGo.AddComponent<Image>();
            background.color = new Color(0.18f, 0.18f, 0.22f, 1f);

            var textArea = NewChild(fieldGo.transform, "Text", new Rect(12f, 8f, 776f, 48f));
            var fieldText = textArea.AddComponent<TextMeshProUGUI>();
            fieldText.text = "typed by a player";
            fieldText.fontSize = 28f;
            fieldText.color = Color.white;

            var placeholderGo = NewChild(fieldGo.transform, "Placeholder",
                new Rect(12f, 8f, 776f, 48f));
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Enter text…";
            placeholder.fontSize = 28f;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);

            var field = fieldGo.AddComponent<TMP_InputField>();
            field.textComponent = fieldText;
            field.placeholder = placeholder;
            field.targetGraphic = background;
            field.text = "typed by a player";

            // SetText(string) is on both TMP_Text and OneTextLabel, so this
            // listener is exactly the case the carry has to get right: the
            // target is a component that is itself about to be replaced.
            UnityEventTools.AddPersistentListener(field.onValueChanged, placeholder.SetText);

            var caption = NewChild(canvasGo.transform, "Caption", new Rect(40f, 300f, 1320f, 60f));
            var captionText = caption.AddComponent<TextMeshProUGUI>();
            captionText.text = "The listener on this field points at the placeholder label, " +
                               "which is itself being replaced.";
            captionText.fontSize = 22f;
            captionText.color = new Color(0.55f, 0.85f, 1f, 1f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
        }

        private static GameObject NewChild(Transform parent, string name, Rect rect)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var transform = go.GetComponent<RectTransform>();
            transform.anchorMin = new Vector2(0f, 1f);
            transform.anchorMax = new Vector2(0f, 1f);
            transform.pivot = new Vector2(0f, 1f);
            transform.sizeDelta = new Vector2(rect.width, rect.height);
            transform.anchoredPosition = new Vector2(rect.x, -rect.y);
            return go;
        }

        private static Font LegacyFont()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/TextMesh Pro/Fonts/LiberationSans.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        /// <summary>
        /// After the conversion, in the log, in one line: what the scene now
        /// holds and whether the input field kept its wiring.
        /// </summary>
        private static void Verify()
        {
            var labels = UnityEngine.Object.FindObjectsByType<OneTextLabel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var fields = UnityEngine.Object.FindObjectsByType<OneTextInputField>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var leftover = UnityEngine.Object.FindObjectsByType<TMP_Text>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            string wiring = "no input field";
            if (fields.Length > 0)
            {
                var serialized = new SerializedObject(fields[0]);
                var text = serialized.FindProperty("_textComponent").objectReferenceValue;
                int listeners = fields[0].onValueChanged.GetPersistentEventCount();
                wiring = $"text component = {(text == null ? "none" : text.name)}, " +
                         $"{listeners} persistent listener(s)";
            }

            Debug.Log($"OneText migration proof: {labels.Length} OneTextLabel, " +
                      $"{fields.Length} OneTextInputField, {leftover.Length} TMP_Text left; {wiring}");
        }

        // ------------------------------------------------------------- render

        /// <summary>
        /// Renders the open scene and hands back the pixels, not the texture.
        ///
        /// The migration between the two shots saves assets and reloads a
        /// scene, and an unreferenced Texture2D does not survive that: Unity
        /// unloads it, and the "before" half of the proof turns into a
        /// MissingReferenceException at composite time. Pixels are plain
        /// managed memory and nothing can take them away.
        /// </summary>
        private static Color[] Render(string caption, OneFontAsset font)
        {
            var camGo = new GameObject("ProofCamera");
            var camera = camGo.AddComponent<Camera>();
            camera.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
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

                var banner = NewChild(canvas.transform, "ProofCaption",
                    new Rect(40f, 380f, 1320f, 50f));
                var label = banner.AddComponent<OneTextLabel>();
                if (font != null) label.Font = font;
                label.Text = caption;
                label.FontSize = 24f;
                label.VerticalAlignment = VerticalAlignment.Middle;
                label.color = new Color(0.55f, 0.95f, 0.7f, 1f);
            }

            // Twice: a OneText label rasterises the glyphs it needs on its first
            // layout and only meshes them on the next one, and a single forced
            // update photographs the gap.
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
            const int Gap = 8;
            int height = Half * 2 + Gap;
            var sheet = new Texture2D(Width, height, TextureFormat.RGBA32, false);

            var divider = new Color(0.25f, 0.25f, 0.3f, 1f);
            var row = new Color[Width];
            for (int x = 0; x < Width; x++) row[x] = divider;

            // ReadPixels gives bottom-up rows, so "after" goes underneath by
            // being written first.
            sheet.SetPixels(0, 0, Width, Half, after);
            for (int y = Half; y < Half + Gap; y++) sheet.SetPixels(0, y, Width, 1, row);
            sheet.SetPixels(0, Half + Gap, Width, Half, before);
            sheet.Apply(false);

            File.WriteAllBytes(path, sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
        }

        private static OneFontAsset CaptionFontAsset()
        {
            string destination = WorkFolder + "/NotoSans.ttf";
            try
            {
                if (!File.Exists(destination))
                {
                    File.Copy(Path.GetFullPath(CaptionFont), destination, overwrite: true);
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning($"OneText migration proof: no caption font ({error.Message})");
                return null;
            }

            var existing = AssetDatabase.LoadAssetAtPath<OneFontAsset>(
                WorkFolder + "/NotoSans Font.asset");
            if (existing != null) return existing;

            var made = OneFontAssetCreator.CreateFromFontFile(destination);
            AssetDatabase.SaveAssets();
            return made;
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
