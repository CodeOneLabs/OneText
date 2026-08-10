using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Temporary. Dumps raw SDF tiles the real rasterizer baked — same
    /// flattening, same 8-bit encoding, same spread — so the bicubic question
    /// can be answered offline against the actual bytes rather than a toy.
    /// Delete with the other probes once the reconstruction verdict is in.
    /// </summary>
    public static class SdfDumpProbe
    {
        private const string Font = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("ONETEXT_PROBE_OUT")
                            ?? Path.Combine(Path.GetTempPath(), "onetext-sdfdump");
            Directory.CreateDirectory(outDir);

            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(Font)));
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();

            foreach (char c in "exNo")
            {
                glyphs.Clear();
                shaper.Shape(font, c.ToString(), glyphs);
                uint gid = glyphs[0].GlyphId;

                foreach (int ppem in new[] { 128, 512 })
                {
                    var tile = GlyphRasterizer.Rasterize(font, gid,
                        ppem / (float)font.UnitsPerEm);
                    var pixels = tile.CopyPixels();
                    string stem = c + "-" + ppem;
                    File.WriteAllBytes(Path.Combine(outDir, stem + ".bin"), pixels);
                    File.WriteAllText(Path.Combine(outDir, stem + ".json"),
                        "{\"width\":" + tile.Width + ",\"height\":" + tile.Height +
                        ",\"originX\":" + tile.OriginUnits.x.ToString("R") +
                        ",\"originY\":" + tile.OriginUnits.y.ToString("R") +
                        ",\"unitsPerPixel\":" + tile.UnitsPerPixel.ToString("R") + "}");
                    Debug.Log($"SdfDumpProbe: {stem} {tile.Width}x{tile.Height}");
                }
            }
            Debug.Log("SdfDumpProbe: tiles in " + outDir);
        }
    }
}
