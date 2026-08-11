using System.Collections.Generic;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// The atlas sheets, on screen, as they are.
    ///
    /// This is the part of the demo that cannot be faked. Every other panel
    /// reports a number that you have to take on trust; this one hands the
    /// actual <c>Texture2DArray</c> the shader is sampling to a RawImage, so
    /// what you see packed into the sheet is what the labels beside it are
    /// drawing from. Type into the input field and a new glyph appears in the
    /// sheet as it is rasterised.
    ///
    /// Three atlases can exist and the viewer cycles whichever do. None of them
    /// is created on demand here — asking through the <c>…Exists</c> properties
    /// rather than the getters — because a viewer that allocated four bytes a
    /// texel just to show you an empty MSDF sheet would be lying about the
    /// memory panel one row up.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class DemoAtlasViewer : MonoBehaviour
    {
        private const string ShaderResource = "OneTextDemo-AtlasSlice";

        private enum Which { Sdf, Msdf, Colour }

        private readonly List<Which> _available = new List<Which>();

        private RawImage _image;
        private Material _material;
        private OneTextLabel _caption;
        private int _index;
        private int _layer;
        private int _framesSinceRefresh;

        private static readonly int AtlasId = Shader.PropertyToID("_AtlasTex");
        private static readonly int SliceId = Shader.PropertyToID("_Slice");
        private static readonly int BroadcastId = Shader.PropertyToID("_Broadcast");

        public void Bind(RawImage image, OneTextLabel caption)
        {
            _image = image;
            _caption = caption;

            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
            {
                // Not an exception: the rest of the demo is still worth looking
                // at, and a missing sample shader is a project setup problem
                // rather than a bug in anything being demonstrated.
                Debug.LogWarning("OneText demo: " + ShaderResource +
                                 " not found under a Resources folder; the atlas preview is off.");
                if (_caption != null) _caption.Text = "atlas preview shader missing";
                return;
            }

            _material = new Material(shader) { hideFlags = HideFlags.DontSave };
            _image.material = _material;
            _image.color = Color.white;
            // A plain 2D texture, and it is never sampled. The array cannot go
            // here — a CanvasRenderer's texture slot asserts on anything that
            // is not kTexDim2D — so it goes on the material instead and this
            // keeps uGUI's own binding satisfied.
            _image.texture = Texture2D.whiteTexture;
        }

        /// <summary>Next atlas that exists, wrapping.</summary>
        public void NextAtlas()
        {
            Survey();
            if (_available.Count == 0) return;
            _index = (_index + 1) % _available.Count;
            _layer = 0;
            Refresh();
        }

        public void NextLayer(int delta)
        {
            Survey();
            if (_available.Count == 0) return;
            int layers = LayerCount(_available[Mathf.Clamp(_index, 0, _available.Count - 1)]);
            if (layers <= 0) return;
            _layer = (_layer + delta + layers) % layers;
            Refresh();
        }

        private void Update()
        {
            // Frames, not seconds, for the same reason the stats panel counts
            // them: a clock is a thing that can stop.
            if (++_framesSinceRefresh < 15) return;
            _framesSinceRefresh = 0;
            Survey();
            Refresh();
        }

        private void Survey()
        {
            _available.Clear();
            if (SharedGlyphAtlas.Exists) _available.Add(Which.Sdf);
            if (SharedGlyphAtlas.PreciseAtlasExists) _available.Add(Which.Msdf);
            if (SharedGlyphAtlas.ColorAtlasExists) _available.Add(Which.Colour);
            if (_index >= _available.Count) _index = 0;
        }

        private void Refresh()
        {
            if (_image == null || _material == null) return;

            if (_available.Count == 0)
            {
                _material.SetTexture(AtlasId, null);
                if (_caption != null) _caption.Text = "no atlas yet — press +200 glyphs";
                return;
            }

            var which = _available[_index];
            var texture = TextureOf(which);
            if (texture == null) return;

            int layers = Mathf.Max(1, texture.depth);
            if (_layer >= layers) _layer = 0;

            _material.SetTexture(AtlasId, texture);
            _material.SetFloat(SliceId, _layer);
            // Only the R8 sheet needs the broadcast; showing the MSDF sheet's
            // three channels as they are is the interesting part of it.
            _material.SetFloat(BroadcastId, which == Which.Sdf ? 1f : 0f);

            if (_caption != null)
            {
                _caption.Text = Name(which) + "  ·  " + texture.width + "×" + texture.height +
                                "  ·  layer " + (_layer + 1) + "/" + layers +
                                "  ·  " + Format(which);
            }
        }

        private static Texture2DArray TextureOf(Which which)
        {
            switch (which)
            {
                case Which.Sdf: return SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas.Texture : null;
                case Which.Msdf: return SharedGlyphAtlas.PreciseAtlasExists
                    ? SharedGlyphAtlas.PreciseAtlas.Texture : null;
                default: return SharedGlyphAtlas.ColorAtlasExists
                    ? SharedGlyphAtlas.ColorAtlas.Texture : null;
            }
        }

        private static int LayerCount(Which which)
        {
            var texture = TextureOf(which);
            return texture != null ? texture.depth : 0;
        }

        private static string Name(Which which) =>
            which == Which.Sdf ? "sdf atlas"
            : which == Which.Msdf ? "precise atlas (msdf)"
            : "colour atlas";

        private static string Format(Which which) =>
            which == Which.Sdf ? "R8, 1 byte/texel" : "RGBA32, 4 bytes/texel";

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
