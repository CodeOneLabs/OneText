using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// A camera, a canvas and a render target, in screen-pixel coordinates:
    /// the same trick the M-series proof generators use, factored out so a
    /// golden case and a proof picture cannot drift apart.
    ///
    /// Everything about it is nailed down on purpose. A golden image is only
    /// worth having if two runs of the same code produce the same bytes, and
    /// every default that could wobble between runs (multisampling, the
    /// canvas's pixel-perfect snapping, the atlas a previous test left behind)
    /// is set here rather than inherited.
    /// </summary>
    public sealed class GoldenScene : IDisposable
    {
        /// <summary>
        /// Font files, read once per process. The CJK face is sixteen
        /// megabytes and four cases want it; re-reading it per case is most of
        /// the suite's wall clock for no benefit. HarfBuzz pins the array
        /// read-only, so several faces may share one buffer safely.
        /// </summary>
        private static readonly Dictionary<string, byte[]> FontBytes =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public readonly int Width, Height;
        public readonly GameObject CanvasGo;

        private readonly Camera _camera;
        private readonly RenderTexture _target;
        private readonly List<GameObject> _created = new List<GameObject>();

        public GoldenScene(int width, int height)
        {
            Width = width;
            Height = height;

            PrepareRendering();

            var camGo = new GameObject("GoldenCamera");
            _created.Add(camGo);
            _camera = camGo.AddComponent<Camera>();
            _camera.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.orthographic = true;
            _camera.allowMSAA = false;
            _camera.allowHDR = false;

            // Deliberately the same constructor the M-series generators use,
            // with multisampling pinned off: whatever the quality level says
            // about MSAA must not decide what a baseline looks like.
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                filterMode = FilterMode.Point,
            };
            _target.Create();
            _camera.targetTexture = _target;

            CanvasGo = new GameObject("GoldenCanvas");
            _created.Add(CanvasGo);
            var canvas = CanvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _camera;
            canvas.planeDistance = 5f;
            // Snapping a rect to the physical pixel grid is a good idea in a
            // game and a bad one here: it makes the picture depend on where the
            // canvas happened to land, which is exactly the wobble a golden
            // test would report as a regression.
            canvas.pixelPerfect = false;
            CanvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
        }

        /// <summary>
        /// Global state a golden case must not inherit from whatever ran
        /// before it.
        ///
        /// The shader global because the normal canvas path sets it and a
        /// hand-driven render does not, and the SDF shader reads it for its
        /// ZTest. The atlas because a stale one leaves the previous case's
        /// packing in this one's uv rectangles. The dictionary because it is
        /// process-wide, the Asian typography tests install and clear word
        /// lists as they go, and a Thai paragraph wraps differently depending
        /// on which of them ran last.
        /// </summary>
        private static void PrepareRendering()
        {
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);
            SharedGlyphAtlas.Reconfigure(force: true);
            Unicode.DictionaryLineBreaker.ResetToDefaults();
        }

        /// <summary>Font bytes for a package-relative path, cached per process.</summary>
        public static byte[] Font(string packagePath)
        {
            if (FontBytes.TryGetValue(packagePath, out var cached)) return cached;
            var bytes = File.ReadAllBytes(Path.GetFullPath(packagePath));
            FontBytes[packagePath] = bytes;
            return bytes;
        }

        /// <summary>
        /// A label at <paramref name="rect"/>, measured in pixels from the top
        /// left of the canvas, in the primary font with the rest as fallbacks.
        /// </summary>
        public OneTextLabel Label(string fontPath, string text, float size, Rect rect,
            params string[] fallbackPaths)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            _created.Add(go);
            go.transform.SetParent(CanvasGo.transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);

            var label = go.AddComponent<OneTextLabel>();
            var fallbacks = new byte[fallbackPaths.Length][];
            for (int i = 0; i < fallbackPaths.Length; i++) fallbacks[i] = Font(fallbackPaths[i]);
            label.SetFont(Font(fontPath), fallbacks);
            label.Text = text;
            label.FontSize = size;
            label.Alignment = TextAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Wrap = TextWrap.Wrap;
            label.color = Color.white;
            return label;
        }

        /// <summary>
        /// Renders the canvas and reads it back as RGBA32. The caller owns the
        /// texture.
        ///
        /// Twice, deliberately. The first build is where the glyphs are
        /// discovered, rasterized and uploaded, and a label that baked its uvs
        /// before the atlas grew has to rebuild against the new one; a single
        /// pass would make the picture depend on whether the atlas happened to
        /// be warm, which is the one thing a golden test must not depend on.
        /// </summary>
        public Texture2D Render()
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Canvas.ForceUpdateCanvases();
                _camera.Render();
            }

            var previous = RenderTexture.active;
            RenderTexture.active = _target;
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            texture.Apply(false);
            RenderTexture.active = previous;
            return texture;
        }

        public void Dispose()
        {
            if (_camera != null) _camera.targetTexture = null;
            if (_target != null) UnityEngine.Object.DestroyImmediate(_target);
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }
    }
}
