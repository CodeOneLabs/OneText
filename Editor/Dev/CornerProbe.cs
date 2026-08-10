using System;
using System.IO;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Temporary, like <see cref="BucketProbe"/> was. Renders the one
    /// comparison the `precise` option's existence hangs on, now that density
    /// is measured and capped: text past the cap, where magnification is the
    /// only place MSDF can still show anything. Body-size text at 1:1 is the
    /// control, where the two fields must be indistinguishable.
    ///
    /// Delete once the keep-or-cut decision is made.
    /// </summary>
    public static class CornerProbe
    {
        private const string Font = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("ONETEXT_PROBE_OUT")
                            ?? Path.Combine(Path.GetTempPath(), "onetext-corner");
            Directory.CreateDirectory(outDir);

            float capWas = OneText.UGUI.OneTextLabel.PpemCap;
            try
            {
                foreach (var (name, size, precise, cap) in new (string, float, bool, float)[]
                {
                    // Typical UI size, ~1.1x bucket magnification: the control.
                    ("a-sdf-36pt", 36f, false, 128f),
                    ("b-msdf-36pt", 36f, true, 128f),
                    // Past the cap: baked 128, drawn 2.25x magnified.
                    ("c-sdf-288pt-cap128", 288f, false, 128f),
                    ("d-msdf-288pt-cap128", 288f, true, 128f),
                    // Ground truth: the cap lifted, baked 256, near 1:1.
                    ("e-sdf-288pt-cap256", 288f, false, 256f),
                })
                {
                    OneText.UGUI.OneTextLabel.PpemCap = cap;
                    using (var scene = new GoldenScene(1500, 420))
                    {
                        var label = scene.Label(Font, "New Text", size,
                            new Rect(8f, 8f, 1480f, 404f));
                        label.Wrap = TextWrap.NoWrap;
                        label.Precise = precise;

                        var texture = scene.Render();
                        File.WriteAllBytes(Path.Combine(outDir, name + ".png"),
                            texture.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                    Debug.Log($"CornerProbe: {name} done");
                }
            }
            finally
            {
                OneText.UGUI.OneTextLabel.PpemCap = capWas;
                SharedGlyphAtlas.Reconfigure(force: true);
            }
            Debug.Log("CornerProbe: pictures in " + outDir);
        }
    }
}
