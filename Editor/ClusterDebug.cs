using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Diagnoses the cluster pipeline in isolation: prints how a run splits
    /// into clusters and dumps the merged SDF tile (raw + 0.5-thresholded)
    /// so rasterizer defects are visible without the whole render stack.
    /// </summary>
    public static class ClusterDebug
    {
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        public static void Run()
        {
            string outDir = System.Environment.GetCommandLineArgs() is var args &&
                System.Array.IndexOf(args, "-oneOut") is var i && i >= 0 && i + 1 < args.Length
                ? args[i + 1] : Path.GetTempPath();

            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ArabicFont)));
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "يظهر", glyphs);

            Debug.Log($"CLUSTERDBG glyphs={glyphs.Count}");
            int pen = 0;
            foreach (var g in glyphs)
            {
                bool ink = font.TryGetInkBounds(g.GlyphId, out var lo, out var hi);
                Debug.Log($"CLUSTERDBG gid={g.GlyphId} adv={g.XAdvance} off=({g.XOffset},{g.YOffset}) " +
                          $"ink={(ink ? $"[{pen + g.XOffset + lo.x:F0}..{pen + g.XOffset + hi.x:F0}]" : "none")}");
                pen += g.XAdvance;
            }

            var clusters = new List<GlyphClusters.Cluster>();
            var positioned = new List<PositionedGlyph>();
            GlyphClusters.Split(font, glyphs, clusters, positioned, 100000f, 125f);
            Debug.Log($"CLUSTERDBG clusters={clusters.Count}");
            foreach (var cl in clusters)
                Debug.Log($"CLUSTERDBG cluster start={cl.Start} count={cl.Count} penX={cl.PenX}");

            // Rasterize the first cluster exactly like the atlas does.
            using var atlas = new GlyphAtlas();
            var cluster = clusters[0];
            var loc = atlas.GetOrAddCluster(font, 84f, positioned, cluster.Start, cluster.Count, cluster.Hash);
            atlas.Flush();
            Debug.Log($"CLUSTERDBG tile uv={loc.UvRect} layer={loc.Layer} hasPixels={loc.HasPixels}");

            var data = atlas.Texture.GetPixelData<byte>(0, loc.Layer);
            int texSize = atlas.Texture.width;
            int x0 = Mathf.RoundToInt(loc.UvRect.xMin * texSize);
            int y0 = Mathf.RoundToInt(loc.UvRect.yMin * texSize);
            int w = Mathf.RoundToInt(loc.UvRect.width * texSize);
            int h = Mathf.RoundToInt(loc.UvRect.height * texSize);

            var raw = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var thresholded = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = data[(y0 + y) * texSize + x0 + x];
                    raw.SetPixel(x, y, new Color32(v, v, v, 255));
                    byte t = v >= 128 ? (byte)255 : (byte)0;
                    thresholded.SetPixel(x, y, new Color32(t, t, t, 255));
                }
            }
            raw.Apply();
            thresholded.Apply();
            File.WriteAllBytes(Path.Combine(outDir, "cluster-sdf-raw.png"), raw.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(outDir, "cluster-sdf-mask.png"), thresholded.EncodeToPNG());

            RenderSolo(outDir);
            Debug.Log("CLUSTERDBG done");
        }

        /// <summary>One label, one word, nothing else: isolates the render path.</summary>
        private static void RenderSolo(string outDir)
        {
            const int W = 700, H = 300;
            var camGo = new GameObject("SoloCam");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("SoloCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;

            var go = new GameObject("Solo", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvasGo.transform, false);
            var label = go.AddComponent<UGUI.OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(600f, 260f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(ArabicFont)));
            label.Text = "يظهر";
            label.FontSize = 84f;
            label.color = Color.white;

            Canvas.ForceUpdateCanvases();
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(outDir, "solo-render.png"), tex.EncodeToPNG());

            Object.DestroyImmediate(tex);
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(camGo);
        }
    }
}
