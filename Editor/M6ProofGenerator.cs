using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M6: how the atlas packs, what a compaction
    /// does to the packing, and — the part that matters — that text rendered
    /// after a compaction is pixel-identical to text rendered before it.
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M6ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M6ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        // Not vendored and not required: a system CJK font, used only to
        // measure how many CJK tiles a budget really holds on this machine.
        private const string SystemKoreanFont = "/System/Library/Fonts/AppleSDGothicNeo.ttc";

        private const string Sample =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "áàâäãåçéèêëíìîïñóòôöõúùûüýÿÁÀÂÄÃÅÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜÝ" +
            "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);

            PackingProof(outDir);
            CompactionIsInvisible(outDir);
            CjkCapacity();
            Debug.Log($"M6 proof written to {outDir}");
        }

        /// <summary>
        /// Fragments an atlas by asking for many sizes, then compacts it. The
        /// two dumps show the same tiles before and after the repack.
        /// </summary>
        private static void PackingProof(string outDir)
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFont)));
            using var arabic = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ArabicFont)));
            using var shaper = new Shaper();

            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, Sample, glyphs);
            var arabicGlyphs = new List<ShapedGlyph>();
            shaper.Shape(arabic, "السلام عليكم ورحمة الله وبركاته", arabicGlyphs);

            // Two atlases driven through the identical mixed-size sequence. The
            // only difference is whether they are allowed to repack, which is
            // the one way to price what defragmentation buys.
            var fragmented = Fill(font, arabic, glyphs, arabicGlyphs, autoCompact: false);
            var compacted = Fill(font, arabic, glyphs, arabicGlyphs, autoCompact: true);

            var before = fragmented.GetStats();
            var after = compacted.GetStats();
            Dump(fragmented, Path.Combine(outDir, "onetext-m6-packing-before.png"));
            Dump(compacted, Path.Combine(outDir, "onetext-m6-packing-after.png"));

            // How much more fits before the atlas has to start evicting again:
            // occupancy alone cannot show this, because a repack moves tiles
            // without changing how much ink they cover.
            int headroomFragmented = Headroom(fragmented, font, glyphs);
            int headroomCompacted = Headroom(compacted, font, glyphs);

            Debug.Log($"[m6] packing without defrag: {before.TileCount} tiles, " +
                $"{before.UsedFraction:P1} occupancy, {before.ShelfCount} shelves, " +
                $"{before.Evictions} evictions, {headroomFragmented} more tiles before the next eviction. " +
                $"With defrag: {after.TileCount} tiles, {after.UsedFraction:P1}, {after.ShelfCount} shelves, " +
                $"{after.Evictions} evictions, {headroomCompacted} more tiles.");

            fragmented.Dispose();
            compacted.Dispose();
        }

        private static GlyphAtlas Fill(FontData font, FontData arabic,
            List<ShapedGlyph> glyphs, List<ShapedGlyph> arabicGlyphs, bool autoCompact)
        {
            var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 1 })
            {
                AutoCompact = autoCompact,
            };
            foreach (int ppem in new[] { 32, 64, 40, 96, 48, 128, 56 })
            {
                foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, ppem);
                foreach (var glyph in arabicGlyphs) atlas.GetOrAdd(arabic, glyph.GlyphId, ppem);
                atlas.Flush();
            }
            return atlas;
        }

        /// <summary>New tiles the atlas accepts before it has to evict one.</summary>
        private static int Headroom(GlyphAtlas atlas, FontData font, List<ShapedGlyph> glyphs)
        {
            int evictionsAtStart = atlas.GetStats().Evictions;
            int placed = 0;
            foreach (int ppem in new[] { 80, 112, 160, 192, 224, 256 })
            {
                foreach (var glyph in glyphs)
                {
                    atlas.GetOrAdd(font, glyph.GlyphId, ppem);
                    atlas.Flush();
                    if (atlas.GetStats().Evictions > evictionsAtStart) return placed;
                    placed++;
                }
            }
            return placed;
        }

        /// <summary>
        /// The correctness claim of moving tiles: a label redrawn after the
        /// atlas repacked itself must look exactly the same. Renders the same
        /// text twice, hammering the shared atlas in between.
        /// </summary>
        private static void CompactionIsInvisible(string outDir)
        {
            const int W = 1200, H = 400;
            var camGo = new GameObject("M6Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("M6Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;

            var label = Label(canvasGo.transform, LatinFont, "Compaction moves tiles.", 64f, 90f);
            var arabic = Label(canvasGo.transform, ArabicFont, "ورحمة الله وبركاته", 64f, -60f);

            Canvas.ForceUpdateCanvases();
            AtlasFlushScheduler.FlushNow();
            cam.Render();
            var first = Capture(rt, W, H);
            File.WriteAllBytes(Path.Combine(outDir, "onetext-m6-text-before.png"), first.EncodeToPNG());

            // Fill the shared atlas until it evicts and compacts underneath the
            // labels, exactly as a busy scene would.
            var atlas = SharedGlyphAtlas.Atlas;
            using (var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFont))))
            using (var shaper = new Shaper())
            {
                var glyphs = new List<ShapedGlyph>();
                shaper.Shape(font, Sample, glyphs);
                foreach (int ppem in new[] { 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256 })
                {
                    foreach (var glyph in glyphs) atlas.GetOrAdd(font, glyph.GlyphId, ppem);
                    atlas.Flush();
                }
            }
            atlas.Compact();
            atlas.Flush();

            label.SetVerticesDirty();
            arabic.SetVerticesDirty();
            Canvas.ForceUpdateCanvases();
            AtlasFlushScheduler.FlushNow();
            cam.Render();
            var second = Capture(rt, W, H);
            File.WriteAllBytes(Path.Combine(outDir, "onetext-m6-text-after.png"), second.EncodeToPNG());

            var stats = atlas.GetStats();
            int differing = 0, maxDelta = 0;
            var a = first.GetPixels32();
            var b = second.GetPixels32();
            for (int i = 0; i < a.Length; i++)
            {
                int delta = Mathf.Max(Mathf.Abs(a[i].r - b[i].r),
                    Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
                if (delta == 0) continue;
                differing++;
                maxDelta = Mathf.Max(maxDelta, delta);
            }
            Debug.Log($"[m6] after {stats.Evictions} evictions and {stats.Compactions} compactions: " +
                $"{differing} of {a.Length} pixels differ (max delta {maxDelta}) — 0 means the repack is invisible");

            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(camGo);
        }

        /// <summary>
        /// The number the whole milestone is about: how many CJK glyphs a
        /// budget holds. Uses a system Korean font when one is present — it is
        /// a measurement, not a dependency.
        /// </summary>
        private static void CjkCapacity()
        {
            if (!File.Exists(SystemKoreanFont))
            {
                Debug.Log("[m6] CJK capacity: no system Korean font on this machine, skipped");
                return;
            }

            using var font = FontData.Load(File.ReadAllBytes(SystemKoreanFont));
            var codepoints = new List<int>();
            for (int cp = 0xAC00; cp < 0xAC00 + 2350; cp++) codepoints.Add(cp); // KS X 1001 span
            using var stack = FontStack.Single(font);

            foreach (var settings in new[]
            {
                new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 4 },   // 4 MB, the old constant
                new GlyphAtlasSettings { TextureSize = 2048, LayerCount = 4 },   // 16 MB
            })
            {
                using var atlas = new GlyphAtlas(settings);
                var report = AtlasPrewarm.Warm(atlas, stack, codepoints, new[] { 48f });
                var stats = atlas.GetStats();
                Debug.Log($"[m6] CJK capacity at 48px in {settings}: {report.Baked} glyphs baked, " +
                    $"{report.Skipped} did not fit, {stats.UsedFraction:P0} occupancy");
            }
        }

        private static void Dump(GlyphAtlas atlas, string path)
        {
            var tex = new Texture2D(atlas.Texture.width, atlas.Texture.height,
                TextureFormat.R8, false, true);
            tex.SetPixelData(atlas.Texture.GetPixelData<byte>(0, 0), 0);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private static Texture2D Capture(RenderTexture rt, int w, int h)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            return tex;
        }

        private static OneTextLabel Label(Transform parent, string fontPath, string text,
            float size, float yOffset)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<OneTextLabel>();
            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 0.5f);
            rectTransform.anchorMax = new Vector2(1f, 0.5f);
            rectTransform.sizeDelta = new Vector2(-120f, 200f);
            rectTransform.anchoredPosition = new Vector2(0f, yOffset);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.Text = text;
            label.FontSize = size;
            label.color = Color.white;
            return label;
        }

        private static string GetArg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
