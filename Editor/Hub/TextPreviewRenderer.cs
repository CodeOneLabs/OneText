using System;
using UnityEngine;
using UnityEngine.UI;
using OneText.UGUI;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>
    /// Renders text to a texture with no scene, for editor windows that want to
    /// show what the engine will actually draw.
    ///
    /// A gallery of measurements is useful and a gallery of pictures is what
    /// people mean by a gallery: the whole reason to preview a string in a
    /// typeface is that the answer is visual. This is the proof generators'
    /// offscreen canvas, kept alive between draws (one camera, one render
    /// texture, one label, reused per cell), because a window that builds a
    /// scene per cell is a window nobody scrolls twice.
    ///
    /// Everything it creates is hidden and unsaved, and disposing it takes the
    /// scene with it.
    /// </summary>
    public sealed class TextPreviewRenderer : IDisposable
    {
        private Camera _camera;
        private GameObject _canvasGo;
        private OneTextLabel _label;
        private RenderTexture _target;
        private int _width, _height;

        /// <summary>Background the previews are composited over.</summary>
        public Color Background = new Color(0.14f, 0.14f, 0.16f, 1f);

        /// <summary>Text colour, when a style does not set one.</summary>
        public Color Foreground = Color.white;

        /// <summary>
        /// Draws one string into a fresh texture the caller owns.
        ///
        /// Returns null when there is no font to draw with, which an editor
        /// window has to handle anyway; a project without a default font is
        /// the state every project starts in.
        /// </summary>
        public Texture2D Render(string text, OneFontAsset font, string language, float fontSize,
            int width, int height, TextWrap wrap, TextAlignment alignment, Color? color = null)
        {
            width = Mathf.Clamp(width, 8, 2048);
            height = Mathf.Clamp(height, 8, 2048);
            if (!EnsureScene(width, height)) return null;

            _label.Font = font;
            _label.Text = text ?? string.Empty;
            _label.FontSize = Mathf.Max(1f, fontSize);
            _label.Wrap = wrap;
            _label.Alignment = alignment;
            _label.VerticalAlignment = VerticalAlignment.Top;
            _label.Overflow = TextOverflow.Overflow;
            _label.Language = language;
            _label.Kinsoku = TextDoctor.PrimarySubtag(language) == "ja" ||
                             TextDoctor.PrimarySubtag(language) == "zh"
                ? AsianTypography.Kinsoku.Normal
                : AsianTypography.Kinsoku.Off;
            _label.color = color ?? Foreground;
            if (_label.Font == null) return null;

            var rectTransform = _label.rectTransform;
            rectTransform.sizeDelta = new Vector2(width, height);
            rectTransform.anchoredPosition = Vector2.zero;

            Canvas.ForceUpdateCanvases();
            // Tiles baked for this preview are uploaded by the atlas scheduler
            // on its own frame, which an editor window does not have; outside
            // play mode the scheduler flushes immediately, and this is the call
            // that makes that true for a render happening inside one repaint.
            AtlasFlushScheduler.FlushNow();
            _camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = _target;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false);
            RenderTexture.active = previous;
            return texture;
        }

        private bool EnsureScene(int width, int height)
        {
            if (_camera != null && _width == width && _height == height) return true;
            Dispose();

            _width = width;
            _height = height;

            var cameraGo = new GameObject("OneText Preview Camera")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _camera = cameraGo.AddComponent<Camera>();
            _camera.backgroundColor = Background;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.orthographic = true;
            _camera.enabled = false; // rendered by hand, never on its own
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _camera.targetTexture = _target;

            _canvasGo = new GameObject("OneText Preview Canvas")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _camera;
            canvas.planeDistance = 5f;
            _canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);

            // Explicit components: batch-mode AddComponent does not honour
            // RequireComponent inherited through a base class, and an editor
            // window can be opened in batch mode by a proof generator.
            var labelGo = new GameObject("OneText Preview Label",
                typeof(RectTransform), typeof(CanvasRenderer))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            labelGo.transform.SetParent(_canvasGo.transform, false);
            _label = labelGo.AddComponent<OneTextLabel>();
            var rectTransform = _label.rectTransform;
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            return true;
        }

        public void Dispose()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(_camera.gameObject);
                _camera = null;
            }
            if (_target != null)
            {
                UnityEngine.Object.DestroyImmediate(_target);
                _target = null;
            }
            if (_canvasGo != null)
            {
                UnityEngine.Object.DestroyImmediate(_canvasGo);
                _canvasGo = null;
            }
            _label = null;
        }
    }
}
