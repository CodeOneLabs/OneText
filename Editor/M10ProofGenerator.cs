using System;
using System.IO;
using OneText.UGUI;
using OneText.Unicode;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for the three M10 rules that landed after the
    /// milestone: punctuation compression at a line edge, kinsoku on the
    /// emergency break, and locale-driven glyph selection through `locl`.
    ///
    /// Each panel is a before/after of one rule, because each of them is a
    /// claim about a picture (half an em of white space at a margin, a 。 at
    /// the start of a line, a Han character with the wrong regional shape), and
    /// a passing test says the number moved without saying it looks right.
    /// The boxes are drawn behind the text on purpose: line-edge compression is
    /// invisible without the margin it is measured against.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M10ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M10ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string LoclFont = "Packages/com.onetext.core/Tests/Fonts~/LoclRegional.ttf";

        // Not vendored and not required: a system CJK face, because the rules
        // in this file are about full-width marks and no font in the test data
        // has any. The panels that need it are skipped when it is absent.
        private const string SystemCjkFont = "/System/Library/Fonts/Hiragino Sans GB.ttc";

        private const string Japanese =
            "彼は「こんにちは」と言った。それから（しばらく）黙っていた。" +
            "窓の外では雨が降っていて、誰も何も言わなかった。";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            if (File.Exists(SystemCjkFont))
            {
                RenderLineEdge(Path.Combine(outDir, "onetext-m10-line-edge.png"));
                RenderEmergencyBreak(Path.Combine(outDir, "onetext-m10-emergency-break.png"));
            }
            else
            {
                Debug.LogWarning($"No CJK face at {SystemCjkFont}; skipped two panels.");
            }
            RenderLocl(Path.Combine(outDir, "onetext-m10-locl.png"));
            Debug.Log($"M10 proof written to {outDir}");
        }

        /// <summary>
        /// 行頭・行末の約物. The same paragraph in the same box twice. On the
        /// right, marks that land at a margin give up the blank half nobody
        /// would see, so the text edge is flush, and the width the wrapper no
        /// longer counts is width the next character can have.
        /// </summary>
        private static void RenderLineEdge(string path)
        {
            const int W = 1180, H = 620;
            var scene = new Scene(W, H);

            var left = new Rect(60f, 90f, 460f, 300f);
            var right = new Rect(660f, 90f, 460f, 300f);

            Caption(scene, "punctuation compression off", new Rect(60f, 40f, 460f, 40f));
            Caption(scene, "punctuation compression on", new Rect(660f, 40f, 460f, 40f));

            Box(scene, left);
            Box(scene, right);

            // One toggle, because the line-edge rule is part of 約物詰め rather
            // than a second setting on top of it; a project that wants typeset
            // CJK wants both halves, and one that does not wants neither.
            foreach (var (rect, compress) in new[] { (left, false), (right, true) })
            {
                var label = Label(scene, SystemCjkFont, Japanese, 38f, rect);
                label.Kinsoku = AsianTypography.Kinsoku.Normal;
                label.PunctuationCompression = compress;
            }

            // The line-start half on its own, at a size where half an em is not
            // a matter of opinion. Off, the line is indented by a bracket
            // nobody asked to indent it with, and every line in a paragraph
            // that happens to begin with one is indented differently from its
            // neighbours, which is what the eye actually catches.
            Caption(scene, "a line that begins with a bracket", new Rect(60f, 420f, 620f, 40f));
            foreach (var (x, compress) in new[] { (60f, false), (660f, true) })
            {
                var rect = new Rect(x, 470f, 460f, 110f);
                Box(scene, rect);
                var label = Label(scene, SystemCjkFont, "「はい」と答えた", 52f, rect);
                label.Wrap = TextWrap.NoWrap;
                label.PunctuationCompression = compress;
            }

            scene.Save(path);
        }

        /// <summary>
        /// 追い出し on the emergency break. A box too narrow for any legal break
        /// opportunity, so every line comes from the emergency path, where
        /// kinsoku used to have no vote at all, and a 。 could open a line.
        /// </summary>
        private static void RenderEmergencyBreak(string path)
        {
            const int W = 1100, H = 460;
            var scene = new Scene(W, H);

            // The emergency path only runs when no legal break opportunity
            // fits, which in pure CJK is almost never; a break is allowed
            // between any two ideographs. It is an order number, a URL or an ID
            // that gets there: one unbreakable run, and a 。 waiting on the far
            // side of it. The box is measured rather than guessed, so the panel
            // shows the same case whatever face the machine has.
            const string text = "ORDER8827。ご確認ください";
            const float size = 44f;
            float box = MeasureNoWrap(SystemCjkFont, "ORDER8827", size) + 1f;

            var left = new Rect(60f, 90f, box, 320f);
            var right = new Rect(600f, 90f, box, 320f);

            // The captions are set in the Latin face, so they say it in words.
            Caption(scene, "kinsoku off: the stop opens a line", new Rect(60f, 40f, 500f, 40f));
            Caption(scene, "kinsoku normal: pushed out", new Rect(600f, 40f, 500f, 40f));

            Box(scene, left);
            Box(scene, right);

            Label(scene, SystemCjkFont, text, size, left).Kinsoku = AsianTypography.Kinsoku.Off;
            Label(scene, SystemCjkFont, text, size, right).Kinsoku = AsianTypography.Kinsoku.Normal;

            scene.Save(path);
        }

        /// <summary>Width of one unwrapped line, for sizing a box to the case.</summary>
        private static float MeasureNoWrap(string fontPath, string text, float size)
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var settings = TextLayoutSettings.Default(fonts, size);
            settings.Wrap = TextWrap.NoWrap;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);
            return result.Width;
        }

        /// <summary>
        /// `locl`, in the only font that can show it. The three bars are three
        /// glyphs for one codepoint; the regional two are not in the cmap, so a
        /// bar that is not the default one is proof the language tag reached
        /// HarfBuzz and the feature ran.
        /// </summary>
        private static void RenderLocl(string path)
        {
            const int W = 1200, H = 420;
            var scene = new Scene(W, H);

            var locales = new[] { (null, "no locale"), ("ja", "ja"), ("zh-Hans", "zh-Hans") };
            for (int i = 0; i < locales.Length; i++)
            {
                var (tag, caption) = locales[i];
                float x = 80f + i * 360f;
                Caption(scene, caption, new Rect(x, 40f, 300f, 40f));
                var label = Label(scene, LoclFont, "直", 200f, new Rect(x, 100f, 300f, 260f));
                label.Language = tag;
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

        private static RectTransform Place(Scene scene, GameObject go, Rect rect)
        {
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            return rectTransform;
        }

        /// <summary>The text box itself, so a margin is something you can see.</summary>
        private static void Box(Scene scene, Rect rect)
        {
            var go = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer));
            Place(scene, go, rect);
            go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);
        }

        private static void Caption(Scene scene, string text, Rect rect)
        {
            var label = Label(scene, LatinFont, text, 26f, rect);
            label.color = new Color(0.62f, 0.68f, 0.78f, 1f);
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
