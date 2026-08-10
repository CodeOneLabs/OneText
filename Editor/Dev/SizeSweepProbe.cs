using System;
using System.IO;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Temporary, with the other probes. Renders one string at small sizes
    /// under three densities — the bucket the size asks for (Performance),
    /// 1.5x (Medium) and 2x (High) — plus a 4x supersampled render to stand
    /// as ground truth once downscaled. Answers whether raising the minimum
    /// bucket (equivalently: densifying small text and minifying) actually
    /// looks better, before anyone changes the ladder over a hunch.
    /// </summary>
    public static class SizeSweepProbe
    {
        private const string Font = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("ONETEXT_PROBE_OUT")
                            ?? Path.Combine(Path.GetTempPath(), "onetext-sizesweep");
            Directory.CreateDirectory(outDir);

            float[] sizes = { 12f, 16f, 20f, 24f, 32f };
            (string tag, TextQuality quality)[] rungs =
            {
                ("q1", TextQuality.Performance),
                ("q15", TextQuality.Medium),
                ("q2", TextQuality.High),
            };

            foreach (float size in sizes)
            {
                foreach (var (tag, quality) in rungs)
                {
                    using var scene = new GoldenScene(360, 56);
                    var label = scene.Label(Font, "New Text", size,
                        new Rect(6f, 6f, 348f, 44f));
                    label.Wrap = TextWrap.NoWrap;
                    label.Quality = quality;
                    Save(scene, Path.Combine(outDir, $"s{size:0}-{tag}.png"));
                }

                // Ground truth: everything four times larger, downscaled later
                // with a box filter. Same shaping, same layout, same shader —
                // only the sampling problem removed.
                using (var scene = new GoldenScene(1440, 224))
                {
                    var label = scene.Label(Font, "New Text", size * 4f,
                        new Rect(24f, 24f, 1392f, 176f));
                    label.Wrap = TextWrap.NoWrap;
                    Save(scene, Path.Combine(outDir, $"truth-s{size:0}.png"));
                }
                Debug.Log($"SizeSweepProbe: {size}pt done");
            }
            Debug.Log("SizeSweepProbe: pictures in " + outDir);
        }

        private static void Save(GoldenScene scene, string path)
        {
            var texture = scene.Render();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
