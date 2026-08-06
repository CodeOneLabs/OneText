using System;
using System.IO;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M14's decorations: the same string plain,
    /// outlined, shadowed and glowing, plus the row that is the actual claim:
    /// all four of them, and an undecorated one, in a single canvas sharing a
    /// single material.
    ///
    /// A decoration is a claim about a picture. A test can say the bytes reached
    /// the vertex channel and say nothing at all about whether the outline sits
    /// outside the letter or eats it, so this milestone is not done until there
    /// is a render of it.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M14ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M14ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private const string Specimen = "Handgloves";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            RenderDecorations(Path.Combine(outDir, "onetext-m14-decorations.png"));
            RenderPrecise(Path.Combine(outDir, "onetext-m14-precise.png"));
            Debug.Log($"M14 proof written to {outDir}");
        }

        /// <summary>
        /// The other half of M14: the same letters through the single-channel
        /// field and through the multi-channel one, at a size where the
        /// difference is a corner and not a rumour.
        ///
        /// The specimen is chosen for corners (the apex of an A, the vertices
        /// of a W, the elbow of a K) because that is the only place the two
        /// rows can differ. Both rows are in one canvas with one material, on
        /// purpose: the picture has to show that the option did not fork the
        /// batch.
        /// </summary>
        private static void RenderPrecise(string path)
        {
            const int W = 1320, H = 1000;
            const string Corners = "AW4Z";
            var scene = new Scene(W, H);

            // Larger than the densest rasterization bucket on purpose: a
            // distance field is magnified from the tile it was baked at, and
            // magnification is exactly the condition under which a rounded
            // corner stops being a texel and starts being visible.
            Caption(scene, "SDF", new Rect(40f, 100f, 200f, 40f));
            Caption(scene, "one channel: a corner is a cone, and the sampler rounds it",
                new Rect(40f, 136f, 320f, 60f), 15f);
            var plain = Label(scene, LatinFont, Corners, 320f, new Rect(400f, 20f, 880f, 360f));
            plain.Wrap = TextWrap.NoWrap;

            Caption(scene, "precise", new Rect(40f, 480f, 200f, 40f));
            Caption(scene, "three channels, median of: the corner is where the glyph has one",
                new Rect(40f, 516f, 320f, 60f), 15f);
            var precise = Label(scene, LatinFont, Corners, 320f, new Rect(400f, 400f, 880f, 360f));
            precise.Wrap = TextWrap.NoWrap;
            precise.Precise = true;

            // Decorations read the same field through the same channels, so
            // they have to survive the switch; an option that quietly dropped
            // the outline off a heading would be worse than no option.
            Caption(scene, "precise + tags", new Rect(40f, 830f, 200f, 40f));
            Caption(scene, "outline and shadow on the multi-channel field",
                new Rect(40f, 866f, 320f, 60f), 15f);
            var decorated = Label(scene, LatinFont,
                $"<shadow x=0.5 y=-0.5 soft=0.3><outline=#ffcc33ff w=0.4>{Corners}</outline></shadow>",
                150f, new Rect(400f, 790f, 880f, 180f));
            decorated.Wrap = TextWrap.NoWrap;
            decorated.Precise = true;

            Caption(scene, "one material and one draw call, either way; precise is per label",
                new Rect(40f, H - 30f, 900f, 40f), 16f);

            scene.Save(path);
        }

        private static void RenderDecorations(string path)
        {
            const int W = 1320, H = 760;
            var scene = new Scene(W, H);

            var rows = new[]
            {
                ("plain", Specimen, "no tag: the row every other row has to batch with"),
                ("outline", $"<outline>{Specimen}</outline>", "a second threshold on the same field"),
                ("shadow", $"<shadow>{Specimen}</shadow>", "one offset sample, softened"),
                ("glow", $"<glow=#66ccff>{Specimen}</glow>", "a falloff on the distance, no sample at all"),
                ("both", $"<shadow><outline=#102040ff>{Specimen}</outline></shadow>",
                    "nested tags, parts merged"),
            };

            for (int i = 0; i < rows.Length; i++)
            {
                var (name, markup, note) = rows[i];
                float y = 34f + i * 116f;
                Caption(scene, name, new Rect(40f, y + 18f, 200f, 40f));
                Caption(scene, note, new Rect(40f, y + 54f, 320f, 40f), 15f);
                Label(scene, LatinFont, markup, 76f, new Rect(400f, y, 880f, 100f));
            }

            // Arabic, because the thing a TMP outline preset cannot survive is a
            // fallback face, and because these letters join, so an outline that
            // was per-glyph rather than per-cluster would show a seam through
            // every joint.
            float bottom = 34f + rows.Length * 116f;
            Caption(scene, "shaped", new Rect(40f, bottom + 18f, 200f, 40f));
            Caption(scene, "joins keep their outline: one merged field, one contour",
                new Rect(40f, bottom + 54f, 340f, 60f), 15f);
            Label(scene, ArabicFont, "<outline=#ffcc33ff w=0.45>مرحبا بالعالم</outline>", 76f,
                new Rect(400f, bottom, 880f, 100f));

            Caption(scene, "every row above is one material, one canvas, one draw call",
                new Rect(40f, H - 44f, 900f, 40f), 16f);

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
                _camera.backgroundColor = new Color(0.11f, 0.12f, 0.15f, 1f);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.orthographic = true;
                _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                _camera.targetTexture = _target;

                CanvasGo = new GameObject("ProofCanvas");
                var canvas = CanvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _camera;
                canvas.planeDistance = 5f;
                CanvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
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

        private static void Place(Scene scene, GameObject go, Rect rect)
        {
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
        }

        private static void Caption(Scene scene, string text, Rect rect, float size = 24f)
        {
            var label = Label(scene, LatinFont, text, size, rect);
            label.color = new Color(0.60f, 0.66f, 0.76f, 1f);
        }

        private static OneTextLabel Label(Scene scene, string fontPath, string text, float size,
            Rect rect)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            var label = go.AddComponent<OneTextLabel>();
            Place(scene, go, rect);

            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.Text = text;
            label.FontSize = size;
            label.Alignment = TextAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Wrap = TextWrap.Wrap;
            label.color = Color.white;
            return label;
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
