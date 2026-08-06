using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M11: the Hub.
    ///
    /// The milestone is a window, and a window cannot be screenshotted in batch
    /// mode, so the panels here are the *views themselves*, drawn with the same
    /// data the tabs draw: the gallery's grid laid out by the layout engine,
    /// Doctor's findings from a real scan of a real folder, and the atlas pie
    /// from an atlas that was actually prewarmed and then drawn into. Every
    /// number and every red box on these images was computed, not authored.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M11ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M11ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        // Not vendored and not required, as in M10: a system CJK face, for the
        // rows whose point is a script the test fonts do not cover.
        private const string SystemCjkFont = "/System/Library/Fonts/Hiragino Sans GB.ttc";
        private const string SystemKoreanFont = "/System/Library/Fonts/AppleSDGothicNeo.ttc";
        private const string SystemThaiFont = "/System/Library/Fonts/Thonburi.ttc";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            RenderGallery(Path.Combine(outDir, "onetext-m11-gallery.png"));
            RenderDoctor(Path.Combine(outDir, "onetext-m11-doctor.png"));
            RenderAtlas(Path.Combine(outDir, "onetext-m11-atlas.png"));

            Debug.Log($"M11 proof written to {outDir}");
        }

        /// <summary>
        /// The gallery: one string per row, one locale per row, each in the box
        /// it has to live in. The red frames are computed by the same
        /// measurement the tab uses: text that does not fit its button.
        /// </summary>
        private static void RenderGallery(string path)
        {
            const int W = 1180, H = 620;
            var scene = new Scene(W, H);

            Caption(scene, "every string, in its box, laid out headlessly; red does not fit",
                new Rect(50f, 30f, 1080f, 34f));

            var rows = new List<(string Locale, string Text, string Font)>
            {
                ("en", "Continue", LatinFont),
                ("de", "Fortfahren und Einstellungen speichern", LatinFont),
                ("ar", "متابعة", ArabicFont),
            };
            if (File.Exists(SystemKoreanFont)) rows.Add(("ko", "설정 저장", SystemKoreanFont));
            if (File.Exists(SystemCjkFont)) rows.Add(("ja", "続けて設定を保存してから終了する", SystemCjkFont));

            const float boxWidth = 300f, boxHeight = 58f;
            float y = 100f;
            foreach (var row in rows)
            {
                var box = new Rect(180f, y, boxWidth, boxHeight);
                Caption(scene, row.Locale, new Rect(60f, y + 12f, 100f, 30f));

                bool overflows = Measure(row.Text, row.Font, 30f, boxWidth) > boxWidth;
                Box(scene, box, overflows
                    ? new Color(0.95f, 0.35f, 0.35f, 0.16f)
                    : new Color(1f, 1f, 1f, 0.06f));
                Frame(scene, box, overflows
                    ? new Color(0.95f, 0.35f, 0.35f, 0.95f)
                    : new Color(1f, 1f, 1f, 0.16f));

                var label = Label(scene, row.Font, row.Text, 30f,
                    new Rect(box.x + 10f, box.y + 10f, box.width - 20f, box.height - 20f));
                label.Wrap = TextWrap.NoWrap;

                Caption(scene, overflows
                        ? $"overflows: needs {Measure(row.Text, row.Font, 30f, boxWidth):0} px"
                        : "fits",
                    new Rect(820f, y + 12f, 320f, 30f));
                y += boxHeight + 26f;
            }

            Caption(scene,
                "the pass this replaces is opening every screen in every language, by hand",
                new Rect(50f, H - 70f, 1080f, 34f));
            scene.Save(path);
        }

        /// <summary>
        /// Doctor, on a folder of strings written for the occasion: a Korean
        /// string against a Latin-only chain, and Thai against the built-in
        /// starter dictionary. Both findings are produced by the real rules.
        /// </summary>
        private static void RenderDoctor(string path)
        {
            string folder = Path.Combine(Path.GetTempPath(), "OneTextM11Proof");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ui.csv"),
                "key,en,ko,th\n" +
                "start,Start,시작하기,เริ่มเกม\n" +
                "settings,Settings,설정,ค่าที่ตั้งไว้\n",
                System.Text.Encoding.UTF8);

            using var latin = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFont)));
            var report = TextDoctor.Run(TextSourceScanner.Scan(new[] { folder }),
                FontStack.Single(latin));

            const int W = 1180, H = 400;
            var scene = new Scene(W, H);
            Caption(scene, "Doctor: the project's strings against the fonts it ships",
                new Rect(50f, 30f, 1080f, 34f));
            Caption(scene, report.Summary(), new Rect(50f, 74f, 1080f, 30f));

            // One row per rule rather than per finding: twenty-two tofu
            // findings are the right output for a build log and the wrong one
            // for a picture, where they would bury the two findings that say
            // something different.
            var seen = new List<string>();
            float y = 130f;
            foreach (var finding in report.Findings)
            {
                if (seen.Contains(finding.Rule)) continue;
                seen.Add(finding.Rule);

                int sameRule = 0;
                foreach (var other in report.Findings)
                    if (other.Rule == finding.Rule) sameRule++;

                var color = finding.Severity == DoctorSeverity.Error
                    ? new Color(0.95f, 0.42f, 0.42f)
                    : finding.Severity == DoctorSeverity.Warning
                        ? new Color(0.96f, 0.78f, 0.36f)
                        : new Color(0.62f, 0.68f, 0.78f);

                Box(scene, new Rect(50f, y + 6f, 8f, 34f), color);

                var text = $"[{finding.Rule}] {finding.Message}" +
                    (sameRule > 1 ? $"  (+{sameRule - 1} more like it)" : "");
                var label = Label(scene, LatinFont, text, 21f, new Rect(72f, y, 1050f, 96f));
                label.color = color;
                y += 104f;
                if (y > H - 90f) break;
            }

            Caption(scene, "exits 1 on an error, so an unrenderable string fails the merge",
                new Rect(50f, H - 60f, 1080f, 30f));
            scene.Save(path);

            Directory.Delete(folder, true);
        }

        /// <summary>
        /// The atlas pie, from an atlas that was prewarmed with one set of
        /// characters and then drawn into with another, which is exactly the
        /// split the tab exists to show.
        /// </summary>
        private static void RenderAtlas(string path)
        {
            using var latin = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFont)));
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings
            {
                TextureSize = 512,
                LayerCount = 1,
            });

            var prewarmed = new List<int>();
            for (char c = 'A'; c <= 'Z'; c++) prewarmed.Add(c);
            AtlasPrewarm.Warm(atlas, FontStack.Single(latin), prewarmed, new[] { 32f });

            // And then the characters nobody predicted, which is every game.
            foreach (char c in "abcdefghijklmnopqrstuvwxyz0123456789")
                atlas.GetOrAdd(latin, latin.NominalGlyph(c), 32f);

            var stats = atlas.GetStats();

            const int W = 1180, H = 520;
            var scene = new Scene(W, H);
            Caption(scene, "the atlas, split by where each tile came from",
                new Rect(50f, 30f, 1080f, 34f));

            var pie = PieTexture(560,
                stats.PrewarmedPixels / (float)stats.CapacityPixels,
                stats.RuntimePixels / (float)stats.CapacityPixels);
            var image = new GameObject("Pie", typeof(RectTransform), typeof(CanvasRenderer));
            Place(scene, image, new Rect(60f, 90f, 360f, 360f));
            var graphic = image.AddComponent<RawImage>();
            graphic.texture = pie;

            var legend = new (string Label, string Detail, Color Color)[]
            {
                ("prewarmed", $"{stats.PrewarmedTiles} tiles, {Mb(stats.PrewarmedPixels)}",
                    new Color(0.35f, 0.72f, 0.98f)),
                ("baked at runtime", $"{stats.RuntimeTiles} tiles, {Mb(stats.RuntimePixels)}",
                    new Color(0.98f, 0.72f, 0.30f)),
                ("free", Mb(stats.CapacityPixels - stats.UsedPixels),
                    new Color(0.28f, 0.30f, 0.34f)),
            };

            float y = 130f;
            foreach (var entry in legend)
            {
                Box(scene, new Rect(470f, y + 8f, 18f, 18f), entry.Color);
                var label = Label(scene, LatinFont, $"{entry.Label} · {entry.Detail}", 24f,
                    new Rect(504f, y, 620f, 40f));
                label.color = Color.white;
                y += 46f;
            }

            var demand = Label(scene, LatinFont,
                $"this session wanted {stats.DemandTiles} distinct tiles, {Mb(stats.DemandPixels)}: " +
                "the number occupancy cannot give you, because an atlas under pressure recycles " +
                "instead of filling", 21f, new Rect(470f, y + 20f, 650f, 160f));
            demand.color = new Color(0.62f, 0.68f, 0.78f, 1f);

            scene.Save(path);
            UnityEngine.Object.DestroyImmediate(pie);
        }

        /// <summary>The pie itself, rasterized by hand: no Handles outside a window.</summary>
        private static Texture2D PieTexture(int size, float prewarmed, float runtime)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[size * size];

            var colors = new[]
            {
                new Color(0.35f, 0.72f, 0.98f),
                new Color(0.98f, 0.72f, 0.30f),
                new Color(0.28f, 0.30f, 0.34f),
            };
            float[] ends =
            {
                prewarmed,
                prewarmed + runtime,
                1f,
            };

            float radius = size * 0.48f;
            var center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var offset = new Vector2(x + 0.5f, y + 0.5f) - center;
                    if (offset.magnitude > radius)
                    {
                        pixels[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // Clockwise from the top, like the tab.
                    float angle = Mathf.Atan2(offset.x, offset.y) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;
                    float fraction = angle / 360f;

                    var color = colors[2];
                    for (int i = 0; i < ends.Length; i++)
                    {
                        if (fraction > ends[i]) continue;
                        color = colors[i];
                        break;
                    }
                    pixels[y * size + x] = color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private static string Mb(long pixels) => $"{pixels / (1024f * 1024f):0.##} MB";

        /// <summary>Width the text wants, measured the way the gallery measures it.</summary>
        private static float Measure(string text, string fontPath, float size, float boxWidth)
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            var stack = FontStack.Single(font);
            var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();
            var settings = TextLayoutSettings.Default(stack, size);
            settings.Wrap = TextWrap.NoWrap;
            settings.MaxWidth = boxWidth;
            engine.Layout(text, settings, result);
            return result.Width;
        }

        // ------------------------------------------------------------- scene

        private sealed class Scene
        {
            private readonly int _width, _height;
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            public readonly GameObject CanvasGo;

            public Scene(int width, int height)
            {
                _width = width;
                _height = height;
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

        private static void Box(Scene scene, Rect rect, Color color)
        {
            var go = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer));
            Place(scene, go, rect);
            go.AddComponent<Image>().color = color;
        }

        private static void Frame(Scene scene, Rect rect, Color color)
        {
            Box(scene, new Rect(rect.x, rect.y, rect.width, 2f), color);
            Box(scene, new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), color);
            Box(scene, new Rect(rect.x, rect.y, 2f, rect.height), color);
            Box(scene, new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), color);
        }

        private static void Caption(Scene scene, string text, Rect rect)
        {
            var label = Label(scene, LatinFont, text, 22f, rect);
            label.color = new Color(0.62f, 0.68f, 0.78f, 1f);
        }

        private static OneTextLabel Label(Scene scene, string fontPath, string text, float size,
            Rect rect)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            var label = go.AddComponent<OneTextLabel>();
            Place(scene, go, rect);

            // System faces behind the panel font, so a finding that quotes the
            // Korean character no font in the *tested* chain can draw is still
            // legible in the picture describing it.
            var fallbacks = new List<byte[]>();
            foreach (string systemFont in new[] { SystemKoreanFont, SystemCjkFont, SystemThaiFont })
                if (File.Exists(systemFont)) fallbacks.Add(File.ReadAllBytes(systemFont));
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)), fallbacks.ToArray());
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
