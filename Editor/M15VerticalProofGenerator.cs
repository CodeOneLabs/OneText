using System;
using System.IO;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M15's other half: 縦書き.
    ///
    /// Vertical writing is a claim about a picture in a way even ruby is not.
    /// A test can say a run is rotated, that a column advanced by an em, that
    /// an annotation's baseline is on the right side of its base's, and say
    /// nothing at all about whether the page reads. Half a dozen sign errors
    /// pass every one of those assertions and render a column of text upside
    /// down, mirrored, or stacked into one square. So this is the check that
    /// the numbers add up to something a reader recognises.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M15VerticalProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M15VerticalProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string JapaneseFont =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKjp-Regular.otf";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            RenderVertical(Path.Combine(outDir, "onetext-m15-vertical.png"));
            Debug.Log($"M15 vertical proof written to {outDir}");
        }

        private static void RenderVertical(string path)
        {
            const int W = 1580, H = 960;
            var scene = new Scene(W, H);

            // A paragraph, wrapped into columns that progress right to left,
            // with kinsoku holding the punctuation off the column heads. This
            // is the picture the milestone is about; everything else on the
            // page is a detail of it.
            Note(scene, "a paragraph, right to left",
                "columns wrapped at the box height, kinsoku at the column ends", 30f);
            var paragraph = Vertical(scene, JapaneseFont,
                "吾輩は猫である。名前はまだ無い。どこで生れたか" +
                "とんと見当がつかぬ。何でも薄暗いじめじめした所で" +
                "ニャーニャー泣いていた事だけは記憶している。",
                34f, new Rect(400f, 24f, 460f, 620f));
            paragraph.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
            paragraph.PunctuationCompression = true;

            // Mixed Latin. The runs on either side stand upright and the Latin
            // between them lies on its side, centred in the same column, which
            // is what every Japanese book does with a roman word.
            Note(scene, "mixed Latin - column 2",
                "CJK upright, Latin turned ninety degrees (UAX #50), one column", 160f);
            Vertical(scene, JapaneseFont, "図はOK です", 40f, new Rect(900f, 24f, 90f, 460f));

            // The vertical forms, against the same characters set across the
            // page. 。 moves from the bottom left of its square to the top
            // right; 「」 turn; small kana shift off the baseline. None of this
            // is a transform the engine applies; it is the font's own vert
            // feature, reached by shaping the run top to bottom.
            Note(scene, "vertical forms - column 3",
                "the font's own vert feature, reached by shaping top to bottom; " +
                "the same characters across the page, below", 280f);
            var flat = Label(scene, JapaneseFont, "「ちょっと」、あっ。", 36f,
                new Rect(40f, 386f, 380f, 60f));
            flat.Language = "ja";
            Vertical(scene, JapaneseFont, "「ちょっと」、あっ。", 40f,
                new Rect(1040f, 24f, 70f, 460f));

            // Ruby, which JLREQ puts beside a vertical column rather than over
            // it, and which the engine reaches by subtracting the base's own
            // ascent from the same baseline it subtracts across the page,
            // because a right-to-left column's block axis runs leftward.
            Note(scene, "ruby - column 4",
                "half size, to the right of its base: the horizontal rule, rotated", 470f);
            var ruby = Vertical(scene, JapaneseFont,
                "<ruby=かんじ>漢字</ruby>と<ruby=よ>読</ruby>み", 40f,
                new Rect(1160f, 24f, 110f, 460f));
            ruby.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;

            // A decorated span, to show the tiles reach the mesh with the same
            // channels they always had: a turned tile is still an SDF tile.
            Note(scene, "decorations - column 5",
                "an outlined span in a column, and a glow on a rotated word", 590f);

            Vertical(scene, JapaneseFont,
                "<outline=#7fd0ffff w=0.35>縦書き</outline>は<glow=#ffbb44ff w=0.6>OK</glow>",
                40f, new Rect(1320f, 24f, 110f, 460f));

            // And the same label with the mode left alone, so the page carries
            // its own control: nothing about horizontal text moved.
            Note(scene, "horizontal is untouched",
                "the default mode, byte for byte what it was", 710f);
            var control = Label(scene, JapaneseFont,
                "吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。",
                26f, new Rect(40f, 786f, 340f, 130f));
            control.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
            control.PunctuationCompression = true;
            control.Language = "ja";

            Caption(scene, "every column is one canvas, one material, one draw call",
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

        /// <summary>A titled note down the left margin, on a fixed grid.</summary>
        private static void Note(Scene scene, string title, string body, float y)
        {
            Caption(scene, title, new Rect(40f, y, 330f, 32f));
            Caption(scene, body, new Rect(40f, y + 34f, 330f, 60f), 15f);
        }

        private static void Caption(Scene scene, string text, Rect rect, float size = 24f)
        {
            var label = Label(scene, LatinFont, text, size, rect);
            label.color = new Color(0.60f, 0.66f, 0.76f, 1f);
        }

        /// <summary>A label set in columns, filling its box from the top right.</summary>
        private static OneTextLabel Vertical(Scene scene, string fontPath, string text, float size,
            Rect rect)
        {
            var label = Label(scene, fontPath, text, size, rect);
            label.WritingMode = TextWritingMode.VerticalRightToLeft;
            label.Language = "ja";
            // In a vertical label the two alignments have swapped axes: Left is
            // the top of the column, Top is the right edge of the box.
            label.Alignment = TextAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;
            return label;
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
