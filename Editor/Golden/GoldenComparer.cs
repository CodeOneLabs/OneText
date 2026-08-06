using System;
using System.IO;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Compares a render against its baseline, and writes the pictures a human
    /// needs when the two disagree.
    ///
    /// Two tolerances rather than one, because the two failure modes are
    /// different shapes. A driver update that shifts every antialiased edge by
    /// one 255th touches most of the picture and means nothing; a glyph that
    /// moved half an em touches a small fraction of the picture and means
    /// everything. A single "average difference" number cannot tell those
    /// apart: it forgives the second to make room for the first. So a pixel is
    /// either within <see cref="ChannelTolerance"/> on every channel or it is
    /// not, and the test fails on how MANY are not.
    /// </summary>
    public static class GoldenComparer
    {
        /// <summary>
        /// Per-channel difference, in 0-255 units, that counts as "the same
        /// pixel". Two is the observed run-to-run noise floor of the SDF
        /// shader's edge antialiasing; zero would make the suite a coin flip
        /// and eight would hide a whole hairline stroke.
        /// </summary>
        public const int ChannelTolerance = 2;

        /// <summary>
        /// Fraction of pixels allowed to exceed <see cref="ChannelTolerance"/>.
        /// A tenth of a percent of a 512x256 canvas is 131 pixels: far more
        /// than edge noise has ever produced here, and far less than any real
        /// glyph, stroke or shadow covers.
        /// </summary>
        public const float DifferingPixelBudget = 0.001f;

        /// <summary>Where actual/diff pictures are written when a case fails.</summary>
        public static string OutputDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("ONETEXT_GOLDEN_OUT");
                return string.IsNullOrEmpty(configured)
                    ? Path.Combine(Path.GetTempPath(), "onetext-golden")
                    : configured;
            }
        }

        public readonly struct Result
        {
            public readonly bool Passed;
            public readonly int DifferingPixels;
            public readonly int TotalPixels;
            public readonly int WorstChannelDelta;
            public readonly string Message;

            public Result(bool passed, int differingPixels, int totalPixels, int worstChannelDelta,
                string message)
            {
                Passed = passed;
                DifferingPixels = differingPixels;
                TotalPixels = totalPixels;
                WorstChannelDelta = worstChannelDelta;
                Message = message;
            }

            public float DifferingFraction =>
                TotalPixels == 0 ? 0f : DifferingPixels / (float)TotalPixels;
        }

        /// <summary>
        /// Compares <paramref name="actual"/> with the baseline PNG at
        /// <paramref name="baselinePath"/>. On any failure the actual render
        /// and a difference heatmap are written under
        /// <see cref="OutputDirectory"/> and named in the message.
        /// </summary>
        public static Result Compare(string caseName, Texture2D actual, string baselinePath)
        {
            if (!File.Exists(baselinePath))
            {
                string written = WriteActual(caseName, actual);
                return new Result(false, 0, 0, 0,
                    $"No baseline for '{caseName}' at {baselinePath}. " +
                    $"The render is at {written}. " +
                    "Approve it with: Unity -batchmode -quit -executeMethod " +
                    "OneText.Editor.GoldenRegen.RegenerateAll");
            }

            var baseline = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!baseline.LoadImage(File.ReadAllBytes(baselinePath), markNonReadable: false))
                    return new Result(false, 0, 0, 0,
                        $"Baseline for '{caseName}' at {baselinePath} is not a readable PNG.");

                if (baseline.width != actual.width || baseline.height != actual.height)
                {
                    string written = WriteActual(caseName, actual);
                    return new Result(false, 0, 0, 0,
                        $"'{caseName}' rendered {actual.width}x{actual.height}, baseline is " +
                        $"{baseline.width}x{baseline.height}. The render is at {written}.");
                }

                var expectedPixels = baseline.GetPixels32();
                var actualPixels = actual.GetPixels32();
                int total = expectedPixels.Length;

                var deltas = new byte[total];
                int differing = 0, worst = 0;
                for (int i = 0; i < total; i++)
                {
                    var e = expectedPixels[i];
                    var a = actualPixels[i];
                    int delta = Math.Abs(e.r - a.r);
                    delta = Math.Max(delta, Math.Abs(e.g - a.g));
                    delta = Math.Max(delta, Math.Abs(e.b - a.b));
                    delta = Math.Max(delta, Math.Abs(e.a - a.a));

                    deltas[i] = (byte)Math.Min(delta, 255);
                    if (delta <= ChannelTolerance) continue;
                    differing++;
                    if (delta > worst) worst = delta;
                }

                float fraction = differing / (float)total;
                if (fraction <= DifferingPixelBudget)
                    return new Result(true, differing, total, worst,
                        $"{differing}/{total} pixels differ ({fraction:P4}), worst channel delta {worst}");

                string actualPath = WriteActual(caseName, actual);
                string diffPath = WriteDiff(caseName, baseline, deltas);
                return new Result(false, differing, total, worst,
                    $"'{caseName}' differs from its baseline: {differing}/{total} pixels " +
                    $"({fraction:P4}) exceed a channel delta of {ChannelTolerance}, which is over " +
                    $"the {DifferingPixelBudget:P4} budget. Worst channel delta {worst}.\n" +
                    $"  baseline: {baselinePath}\n" +
                    $"  actual:   {actualPath}\n" +
                    $"  diff:     {diffPath}\n" +
                    "If the new picture is correct, approve it with: Unity -batchmode -quit " +
                    "-executeMethod OneText.Editor.GoldenRegen.RegenerateAll");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baseline);
            }
        }

        private static string WriteActual(string caseName, Texture2D actual)
        {
            Directory.CreateDirectory(OutputDirectory);
            string path = Path.Combine(OutputDirectory, caseName + "_actual.png");
            File.WriteAllBytes(path, actual.EncodeToPNG());
            return path;
        }

        /// <summary>
        /// A heatmap: the baseline dimmed to a grey ghost so the shapes are
        /// still readable, with every differing pixel painted from yellow at
        /// the tolerance to red at a full-scale miss. A diff that is only a
        /// difference mask tells you a hundred pixels moved and not which
        /// hundred.
        /// </summary>
        private static string WriteDiff(string caseName, Texture2D baseline, byte[] deltas)
        {
            Directory.CreateDirectory(OutputDirectory);
            var basePixels = baseline.GetPixels32();
            var heat = new Color32[deltas.Length];

            for (int i = 0; i < deltas.Length; i++)
            {
                var b = basePixels[i];
                byte grey = (byte)((b.r * 54 + b.g * 183 + b.b * 19) / 256 / 3);

                int delta = deltas[i];
                if (delta <= ChannelTolerance)
                {
                    heat[i] = new Color32(grey, grey, grey, 255);
                    continue;
                }

                // Yellow just over the line, red at a wholesale miss: the eye
                // reads "how wrong" faster from hue than from brightness.
                float t = Mathf.Clamp01((delta - ChannelTolerance) / 96f);
                heat[i] = new Color32(255, (byte)Mathf.RoundToInt(255f * (1f - t)), 0, 255);
            }

            var texture = new Texture2D(baseline.width, baseline.height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(heat);
                texture.Apply(false);
                string path = Path.Combine(OutputDirectory, caseName + "_diff.png");
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
