using System;
using System.IO;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Temporary. Draws one string at one size under each bucket policy and
    /// each field, and writes the pictures out to be looked at.
    ///
    /// The question it exists for: a label at 36 points takes the 32 bucket and
    /// is magnified 1.125x, because the atlas picks the largest bucket not
    /// exceeding the request. TextMesh Pro bakes once, far above the size it
    /// draws at, and minifies. Somebody's screenshot put the two side by side
    /// and TMP was visibly smoother. Nothing in this repository has ever
    /// measured that trade, so this makes the pictures rather than arguing.
    ///
    /// Delete once the answer is in the atlas rather than in a probe.
    /// </summary>
    public static class BucketProbe
    {
        private const string Font = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("ONETEXT_PROBE_OUT")
                            ?? Path.Combine(Path.GetTempPath(), "onetext-bucket");
            Directory.CreateDirectory(outDir);

            // Big enough to see the edges, and the label is at 36 either way —
            // the picture is the render at its own size, not a magnified one,
            // so what shows up here is what a player would see.
            // The canvas scale is the whole question. A label at 36 points on a
            // canvas scaled by three is drawn at 108 screen pixels, and nothing
            // in OneTextLabel consults the canvas: the field is still baked at
            // the 32 bucket, which is what the font size alone asks for. Three
            // times the pixels off one third of the field.
            //
            // The control is the same 108 pixels asked for honestly — a label at
            // 108 points on an unscaled canvas — which bakes at 96 and should
            // look like text.
            foreach (var (name, size, scale) in new[]
            {
                ("a-36pt-scale1", 36f, 1f),
                ("b-36pt-scale3", 36f, 3f),
                ("c-108pt-scale1", 108f, 1f),
            })
            {
                SharedGlyphAtlas.Reconfigure(force: true);
                using (var scene = new GoldenScene(760, 180))
                {
                    var canvas = scene.CanvasGo.GetComponent<Canvas>();
                    canvas.scaleFactor = scale;
                    scene.Label(Font, "New Text", 36f == size ? 36f : size,
                        new Rect(8f, 12f / scale, 740f / scale, 150f / scale));

                    var texture = scene.Render();
                    File.WriteAllBytes(Path.Combine(outDir, name + ".png"),
                        texture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                Debug.Log($"OneText bucket probe: {name} — {size}pt at canvas scale {scale} " +
                          $"draws {size * scale:0} screen px from ppem " +
                          GlyphAtlas.QuantizePixelsPerEm(size));
            }

            GlyphAtlas.RoundBucketUp = false;
            SharedGlyphAtlas.Reconfigure(force: true);
            Debug.Log("OneText bucket probe: pictures in " + outDir);
        }
    }
}
