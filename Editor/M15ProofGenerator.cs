using System;
using System.IO;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M15's ruby: furigana over kanji at half
    /// size, an annotation too wide for its base, ruby through a decoration,
    /// and a wrapped paragraph where the base and its reading stay together.
    ///
    /// Ruby is a claim about a picture in a way most layout features are not.
    /// A test can say the annotation run exists, sits above the baseline and
    /// has the clusters of its base, and say nothing at all about whether the
    /// reading is over the character it reads. So this milestone is not done
    /// until there is a render of it.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M15ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M15ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string JapaneseFont =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKjp-Regular.otf";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            RenderRuby(Path.Combine(outDir, "onetext-m15-ruby.png"));
            Debug.Log($"M15 proof written to {outDir}");
        }

        private static void RenderRuby(string path)
        {
            const int W = 1320, H = 1260;
            var scene = new Scene(W, H);

            var rows = new[]
            {
                ("furigana", "<ruby=かんじ>漢字</ruby>があります",
                    "half size, centred over the base, the line taller by the annotation"),
                ("distributed", "<ruby=にほんご>日本語</ruby>の文章",
                    "slack spread: double gaps between, half at each end"),
                ("wider", "この<ruby=むずかしいよみかた>難</ruby>字",
                    "too wide to fit: kana neighbours lend no blank, so the base is padded"),
                ("overhang", "読む。<ruby=むずかしい>難</ruby>「字」",
                    "a full stop before and a bracket after have blank facing the base; " +
                    "the reading hangs over that instead of pushing the line apart"),
                ("decorated", "<outline=#102040ff w=0.4><ruby=かんじ>漢字</ruby></outline>",
                    "a decorated span decorates its reading: same style, same field"),
                ("any script", "<ruby=hanja>漢字</ruby> / <ruby=한자>漢字</ruby>",
                    "the annotation is shaped text, not a kana decoration"),
            };

            for (int i = 0; i < rows.Length; i++)
            {
                var (name, markup, note) = rows[i];
                float y = 24f + i * 132f;
                Caption(scene, name, new Rect(40f, y + 30f, 240f, 40f));
                Caption(scene, note, new Rect(40f, y + 66f, 340f, 60f), 15f);
                var label = Label(scene, JapaneseFont, markup, 64f, new Rect(420f, y, 860f, 120f));
                label.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
                label.Language = "ja";
            }

            // Wrapping is the failure mode the community's <voffset>/<size>
            // workaround cannot fix at all: the base goes to the next line and
            // the reading stays behind. The box is deliberately narrow enough
            // that a break inside 日本語 is the greedy answer.
            float bottom = 24f + rows.Length * 132f;
            Caption(scene, "unbreakable", new Rect(40f, bottom + 30f, 240f, 40f));
            Caption(scene, "a base and its reading are one group; the line breaks before it",
                new Rect(40f, bottom + 66f, 340f, 60f), 15f);
            var wrapped = Label(scene, JapaneseFont,
                "ここに<ruby=にほんご>日本語</ruby>があります。",
                56f, new Rect(420f, bottom, 300f, 340f));
            wrapped.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
            wrapped.Language = "ja";

            Caption(scene, "every row is one canvas, one material, one draw call",
                new Rect(40f, H - 40f, 900f, 40f), 16f);

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
