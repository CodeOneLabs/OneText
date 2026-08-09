using System;
using System.IO;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M2: dumps the SDF glyph atlas and an
    /// actual camera render of OneTextLabel to PNG files.
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M2ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M2ProofGenerator
    {
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        /// <summary>
        /// Rendering a canvas by hand skips the code that normally sets this
        /// global, and the SDF shader reads it for its ZTest.
        /// </summary>
        private static void PrepareCanvasRendering() =>
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            PrepareCanvasRendering();

            DumpAtlas(Path.Combine(outDir, "onetext-m2-atlas.png"));
            RenderLabels(Path.Combine(outDir, "onetext-m2-render.png"));
            Debug.Log($"M2 proof written to {outDir}");
        }

        private static void DumpAtlas(string path)
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(ArabicFont)));
            using var latin = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFont)));
            using var shaper = new Shaper();
            using var atlas = new GlyphAtlas();

            var glyphs = new System.Collections.Generic.List<ShapedGlyph>();
            shaper.Shape(font, "السلام عليكم مرحبا بالعالم", glyphs);
            foreach (var g in glyphs) atlas.GetOrAdd(font, g.GlyphId);
            glyphs.Clear();
            shaper.Shape(latin, "Hello OneText! AVWA quick brown fox 0123", glyphs);
            foreach (var g in glyphs) atlas.GetOrAdd(latin, g.GlyphId);
            atlas.Flush();

            var data = atlas.Texture.GetPixelData<byte>(0, 0);
            var tex = new Texture2D(atlas.Texture.width, atlas.Texture.height,
                TextureFormat.R8, false, true);
            tex.SetPixelData(data, 0);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void RenderLabels(string path)
        {
            const int W = 1400, H = 520;

            var camGo = new GameObject("ProofCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.orthographic = true;

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("ProofCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 5f;

            CreateLabel(canvasGo.transform, ArabicFont, "السلام عليكم ورحمة الله", 92f, 120f);
            CreateLabel(canvasGo.transform, LatinFont, "Hello, OneText! · SDF m2", 76f, -110f);

            // Plain white quad: if this is missing from the output, the
            // canvas/camera path itself failed, not the SDF shader.
            var probe = new GameObject("Probe", typeof(RectTransform), typeof(CanvasRenderer)).AddComponent<Image>();
            probe.transform.SetParent(canvasGo.transform, false);
            probe.rectTransform.anchorMin = probe.rectTransform.anchorMax = new Vector2(0f, 0f);
            probe.rectTransform.anchoredPosition = new Vector2(40f, 40f);
            probe.rectTransform.sizeDelta = new Vector2(30f, 30f);

            Canvas.ForceUpdateCanvases();
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;

            File.WriteAllBytes(path, tex.EncodeToPNG());

            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        private static OneTextLabel CreateLabel(Transform parent, string fontPath, string text, float size, float yOffset)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<OneTextLabel>();
            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 0.5f);
            rectTransform.anchorMax = new Vector2(1f, 0.5f);
            rectTransform.sizeDelta = new Vector2(-120f, 240f);
            rectTransform.anchoredPosition = new Vector2(0f, yOffset);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.Text = text;
            label.FontSize = size;
            label.color = Color.white;
            return label;
        }

        /// <summary>Mixed-direction (BiDi) render: Arabic with embedded digits and Latin.</summary>
        public static void M3Proof()
        {
            PrepareCanvasRendering();
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            const int W = 1500, H = 560;

            var camGo = new GameObject("BidiCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("BidiCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;

            // RTL base with an embedded number and embedded Latin: the
            // classic BiDi cases TextMesh Pro cannot lay out.
            CreateLabel(canvasGo.transform, ArabicFont, "العدد 123 يظهر بشكل صحيح!", 84f, 140f);
            CreateLabel(canvasGo.transform, ArabicFont, "قال المطور Hello World ثم أكمل", 84f, 0f);
            CreateLabel(canvasGo.transform, ArabicFont, "نسبة النجاح 100% في الاختبار", 84f, -140f);

            Canvas.ForceUpdateCanvases();
            cam.Render();
            Save(rt, Path.Combine(outDir, "onetext-m3-bidi.png"), W, H);

            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            UnityEngine.Object.DestroyImmediate(camGo);
            Debug.Log("M3 proof written");
        }

        /// <summary>Large-type render for inspecting joint seams up close.</summary>
        public static void SeamProof()
        {
            PrepareCanvasRendering();
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            const int W = 1500, H = 560;

            var camGo = new GameObject("SeamCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("SeamCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;

            CreateLabel(canvasGo.transform, ArabicFont, "ورحمة الله", 250f, 90f);
            CreateLabel(canvasGo.transform, ArabicFont, "يظهر بشكل ورحمة", 84f, -140f);

            Canvas.ForceUpdateCanvases();
            cam.Render();
            Save(rt, Path.Combine(outDir, "onetext-m3-seam.png"), W, H);

            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            UnityEngine.Object.DestroyImmediate(camGo);
            Debug.Log("Seam proof written");
        }

        /// <summary>
        /// Isolates why the label draws nothing: counts generated vertices via
        /// reflection, inspects the CanvasRenderer's material, and renders one
        /// frame with the default UI material (solid quads) for comparison.
        /// </summary>
        public static void Diagnose()
        {
            PrepareCanvasRendering();
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            const int W = 1400, H = 520;

            var camGo = new GameObject("DiagCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("DiagCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;

            var label = CreateLabel(canvasGo.transform, LatinFont, "OneText DIAG", 96f, 0f);
            Canvas.ForceUpdateCanvases();

            var vh = new VertexHelper();
            typeof(Graphic).GetMethod("OnPopulateMesh",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(VertexHelper) }, null)
                .Invoke(label, new object[] { vh });
            Debug.Log($"DIAG verts={vh.currentVertCount}");

            var cr = label.canvasRenderer;
            Debug.Log($"DIAG materialCount={cr.materialCount} " +
                      $"mat={(cr.materialCount > 0 ? cr.GetMaterial(0)?.shader?.name : "none")} " +
                      $"cull={cr.cull}");

            cam.Render();
            Save(rt, Path.Combine(outDir, "diag-sdf.png"), W, H);

            label.material = Canvas.GetDefaultCanvasMaterial();
            Canvas.ForceUpdateCanvases();
            cam.Render();
            Save(rt, Path.Combine(outDir, "diag-defaultmat.png"), W, H);

            Debug.Log("DIAG done");
        }

        private static void Save(RenderTexture rt, string path, int w, int h)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }
    }
}
