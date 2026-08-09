using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

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

        /// <summary>Where <see cref="Label"/> parents what it makes; see <see cref="Mask"/>.</summary>
        private Transform _parent;

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
        /// A <c>RectMask2D</c> over <paramref name="rect"/>, in the same
        /// top-left pixel coordinates a label takes, which every label made
        /// after this call goes inside.
        ///
        /// A mask is the one thing in this file that cannot be asserted as
        /// numbers anywhere else in the suite. Whether a glyph is clipped is
        /// not a fact about layout — the layout is identical either way — it is
        /// a fact about what the shader did with _ClipRect, and the only place
        /// that shows up is in the pixels.
        ///
        /// <paramref name="softness"/> is the mask's own <c>softness</c>, which
        /// reaches the shader through a different uniform than the rect does
        /// (_UIMaskSoftness*, not _ClipRect) and is therefore its own case: a
        /// shader can clip correctly and ignore softness entirely, which is
        /// what every version of this one before the RectMask2D fix did.
        /// </summary>
        public RectMask2D Mask(Rect rect, int softness = 0)
        {
            var go = Place("Mask", rect);
            var mask = go.AddComponent<RectMask2D>();
            mask.softness = new Vector2Int(softness, softness);

            _parent = go.transform;
            return mask;
        }

        /// <summary>
        /// The other uGUI mask: a stencil <c>Mask</c> over a plain rectangle,
        /// which everything made after this call goes inside.
        ///
        /// Nothing to do with <see cref="Mask"/> beyond the name they share.
        /// RectMask2D hands the shader a rectangle to test against; this one
        /// draws a shape into the stencil buffer and lets the children through
        /// only where it wrote. A shader can satisfy either and fail the other,
        /// so both have to be looked at.
        /// </summary>
        public Mask StencilMask(Rect rect)
        {
            var go = Place("StencilMask", rect);
            var image = go.AddComponent<Image>();
            image.color = Color.white;
            var mask = go.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            _parent = go.transform;
            return mask;
        }

        /// <summary>
        /// A stencil <c>Mask</c> whose shape is a line of OneText itself:
        /// everything made after this call is visible only through the glyphs.
        ///
        /// This is the case that needs UNITY_UI_ALPHACLIP. A mask writes its
        /// stencil wherever its graphic draws a fragment, so a shader that never
        /// discards writes the whole of every glyph quad and the children come
        /// through as a row of rectangles instead of letters. It is the one
        /// masking arrangement where being <em>text</em> rather than an image is
        /// the entire point, so a rectangle is not a degraded result but a
        /// wrong one.
        /// </summary>
        public OneTextLabel TextMask(string fontPath, string text, float size, Rect rect,
            bool showMaskGraphic = false)
        {
            var label = Label(fontPath, text, size, rect);
            var mask = label.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = showMaskGraphic;

            _parent = label.transform;
            return label;
        }

        /// <summary>
        /// A stencil <c>Mask</c> in the shape of a circle, which everything made
        /// after this call goes inside.
        ///
        /// A rectangle is the shape a mask accidentally has when something is
        /// wrong — an unclipped quad, a stencil written across a whole glyph
        /// tile — so a rectangular mask is the one shape that cannot tell a
        /// working mask from a broken one. A circle can: every curved edge in
        /// the picture is a pixel the stencil test had to decide individually.
        /// </summary>
        public Mask CircleMask(Rect rect)
        {
            var go = Place("CircleMask", rect);
            var image = go.AddComponent<Image>();
            image.sprite = CircleSprite();
            image.color = Color.white;

            var mask = go.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            _parent = go.transform;
            return mask;
        }

        /// <summary>
        /// A white disc with a one-pixel edge ramp, built in code so that no
        /// case depends on an asset that could be reimported at a different
        /// compression setting and quietly move the baseline.
        ///
        /// The ramp is not antialiasing — a stencil test is a yes or a no, and
        /// what it decides is which side of uGUI's alpha-clip threshold each
        /// edge pixel falls on. The text drawn through the hole brings its own
        /// antialiasing, which is the edge quality the picture actually shows.
        /// </summary>
        private static Sprite CircleSprite()
        {
            if (s_circle != null) return s_circle;

            const int Size = 256;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[Size * Size];
            float centre = (Size - 1) * 0.5f;
            float radius = Size * 0.5f - 1f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - centre, dy = y - centre;
                    float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            s_circle = Sprite.Create(texture, new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f), 100f);
            s_circle.hideFlags = HideFlags.HideAndDontSave;
            return s_circle;
        }

        private static Sprite s_circle;

        /// <summary>
        /// A flat rectangle of colour, for a mask to reveal parts of. An Image
        /// and not a label on purpose: it is the thing being masked, and a
        /// checkerboard of solid colour makes the shape of the hole obvious in a
        /// way a second line of text would not.
        /// </summary>
        public Image Panel(Rect rect, Color color)
        {
            var go = Place("Panel", rect);
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        /// <summary>
        /// A child of the current parent at <paramref name="rect"/>, in pixels
        /// from the top left of whatever it lands inside.
        /// </summary>
        private GameObject Place(string name, Rect rect)
        {
            var go = new GameObject(name, typeof(RectTransform));
            _created.Add(go);
            go.transform.SetParent(_parent != null ? _parent : CanvasGo.transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            return go;
        }

        /// <summary>
        /// A label at <paramref name="rect"/>, measured in pixels from the top
        /// left of the canvas, in the primary font with the rest as fallbacks.
        ///
        /// The rect is relative to whatever the label lands inside, which is the
        /// canvas until a <see cref="Mask"/> has been made and that mask
        /// afterwards.
        /// </summary>
        public OneTextLabel Label(string fontPath, string text, float size, Rect rect,
            params string[] fallbackPaths)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            _created.Add(go);
            go.transform.SetParent(_parent != null ? _parent : CanvasGo.transform, false);

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
