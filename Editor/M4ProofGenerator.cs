using System;
using System.IO;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M4: word wrapping, alignment, justification,
    /// font fallback inside one label, and variable-font weights.
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M4ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M4ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";
        private const string VariableFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansVariable.ttf";

        private const string Paragraph =
            "OneText lays out real text: it wraps at Unicode line break " +
            "opportunities, aligns, justifies, and falls back across fonts.";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            RenderWrapping(Path.Combine(outDir, "onetext-m4-wrap.png"));
            RenderFallback(Path.Combine(outDir, "onetext-m4-fallback.png"));
            RenderVariable(Path.Combine(outDir, "onetext-m4-variable.png"));
            Debug.Log($"M4 proof written to {outDir}");
        }

        private static void RenderWrapping(string path)
        {
            const int W = 1500, H = 700;
            var scene = new Scene(W, H);

            Label(scene, LatinFont, Paragraph, 34f,
                new Rect(40f, 30f, 440f, 300f), TextAlignment.Left);
            Label(scene, LatinFont, Paragraph, 34f,
                new Rect(520f, 30f, 440f, 300f), TextAlignment.Center);
            Label(scene, LatinFont, Paragraph, 34f,
                new Rect(1000f, 30f, 440f, 300f), TextAlignment.Justified);

            // Narrow box: a long word must break at grapheme boundaries.
            Label(scene, LatinFont, "Donaudampfschifffahrtsgesellschaft", 34f,
                new Rect(40f, 380f, 260f, 280f), TextAlignment.Left);

            // Ellipsis overflow inside a two-line box.
            Label(scene, LatinFont, Paragraph, 34f,
                new Rect(340f, 380f, 620f, 100f), TextAlignment.Left,
                TextOverflow.Ellipsis);

            // Right-aligned RTL paragraph, wrapped.
            Label(scene, ArabicFont,
                "هذا نص عربي طويل يلتف تلقائيا داخل الصندوق ويحاذى إلى اليمين", 34f,
                new Rect(1000f, 380f, 440f, 280f), TextAlignment.Start);

            scene.Save(path);
        }

        private static void RenderFallback(string path)
        {
            const int W = 1500, H = 400;
            var scene = new Scene(W, H);

            var label = Label(scene, LatinFont, "Hello مرحبا world 123 عالم!", 64f,
                new Rect(40f, 40f, 1420f, 140f), TextAlignment.Left);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFont)),
                File.ReadAllBytes(Path.GetFullPath(ArabicFont)));

            var rtl = Label(scene, ArabicFont, "مرحبا Hello بالعالم 2026", 64f,
                new Rect(40f, 220f, 1420f, 140f), TextAlignment.Start);
            rtl.SetFont(File.ReadAllBytes(Path.GetFullPath(ArabicFont)),
                File.ReadAllBytes(Path.GetFullPath(LatinFont)));

            scene.Save(path);
        }

        private static void RenderVariable(string path)
        {
            const int W = 1500, H = 460;
            var scene = new Scene(W, H);

            float[] weights = { 100f, 400f, 900f };
            for (int i = 0; i < weights.Length; i++)
            {
                var label = Label(scene, VariableFont, $"Hamburgefonstiv {weights[i]:0}", 72f,
                    new Rect(40f, 30f + i * 140f, 1420f, 120f), TextAlignment.Left);
                label.SetVariations(new FontVariation("wght", weights[i]));
            }

            scene.Save(path);
        }

        /// <summary>Camera + canvas + render target, in screen-pixel coordinates.</summary>
        private sealed class Scene
        {
            public readonly GameObject CanvasGo;
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            private readonly int _width, _height;

            public Scene(int width, int height)
            {
                _width = width;
                _height = height;

                // Rendering a canvas by hand skips the code that normally sets
                // this global, and the SDF shader reads it for its ZTest.
                Shader.SetGlobalFloat("unity_GUIZTestMode",
                    (float)UnityEngine.Rendering.CompareFunction.Always);

                var camGo = new GameObject("ProofCamera");
                _camera = camGo.AddComponent<Camera>();
                _camera.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.orthographic = true;
                _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                _camera.targetTexture = _target;

                CanvasGo = new GameObject("ProofCanvas");
                var canvas = CanvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _camera;
                canvas.planeDistance = 5f;
                var canvasRect = CanvasGo.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(width, height);
            }

            public void Save(string path)
            {
                Canvas.ForceUpdateCanvases();
                _camera.Render();

                var previous = RenderTexture.active;
                RenderTexture.active = _target;
                var tex = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
                tex.Apply(false);
                RenderTexture.active = previous;
                File.WriteAllBytes(path, tex.EncodeToPNG());

                UnityEngine.Object.DestroyImmediate(tex);
                _camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(_target);
                UnityEngine.Object.DestroyImmediate(CanvasGo);
                UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            }
        }

        /// <summary>Creates a label with a fixed top-left rect, in screen pixels.</summary>
        private static OneTextLabel Label(Scene scene, string fontPath, string text, float size,
            Rect rect, TextAlignment alignment, TextOverflow overflow = TextOverflow.Overflow)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var label = go.AddComponent<OneTextLabel>();

            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);

            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.Text = text;
            label.FontSize = size;
            label.Alignment = alignment;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Overflow = overflow;
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
