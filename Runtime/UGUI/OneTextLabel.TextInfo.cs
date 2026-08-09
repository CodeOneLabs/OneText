using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// The TextMesh Pro-shaped per-character surface: <c>textInfo</c> and the
    /// edit-then-push cycle built on it.
    ///
    /// Kept in its own file because it is a compatibility layer and not part of
    /// how the label works. OneText's own seam for the same job is
    /// <see cref="TextQuad"/> and <see cref="ITextQuadModifier"/>, which address
    /// text by grapheme cluster, survive ligatures and do not ask the caller to
    /// keep a vertex array in step. This exists so a project arriving from TMP —
    /// and third-party code it cannot fork, which in practice means DOTweenPro's
    /// per-character animator — compiles and runs after a rename.
    /// </summary>
    public sealed partial class OneTextLabel
    {
        private OneTextTextInfo _textInfo;

        /// <summary>Layout generation the cached <see cref="_textInfo"/> describes.</summary>
        private int _textInfoStamp = -1;

        /// <summary>
        /// Whether to draw from vertices a caller edited through
        /// <see cref="textInfo"/> and pushed with <see cref="UpdateVertexData()"/>
        /// rather than from the tiles.
        ///
        /// Any re-layout drops it: the indices it was written against no longer
        /// mean anything, and stale geometry over new text is worse than a lost
        /// animation frame.
        /// </summary>
        private bool _vertexOverride;

        /// <summary>
        /// A TextMesh Pro-shaped view of the finished layout.
        ///
        /// Rebuilt when the layout has moved since it was last read, so reading
        /// it straight after setting <c>text</c> is safe. As with TMP, indices
        /// taken from one read stop meaning anything once the text changes.
        /// </summary>
        public OneTextTextInfo textInfo
        {
            get
            {
                EnsureLayout();
                // The tiles are built while the mesh is, so a label nothing has
                // drawn yet has none. TMP behaves the same way and its callers
                // know it — they call ForceMeshUpdate first — but a caller who
                // forgets gets an empty textInfo and no hint why, so it is done
                // for them once rather than reported.
                if (_drawn.Count == 0) ForceMeshUpdate();
                if (_textInfo == null) _textInfo = new OneTextTextInfo();
                if (_textInfoStamp != _layoutRuns)
                {
                    OneTextTextInfoBuilder.Build(this, _textInfo);
                    _textInfoStamp = _layoutRuns;
                }
                return _textInfo;
            }
        }

        /// <summary>
        /// Push vertices edited through <see cref="textInfo"/> to the mesh.
        ///
        /// The flags decide whether there is anything to do and nothing else:
        /// OneText writes one interleaved vertex stream, so there is no cheaper
        /// path for "colours only" to take. They are in the signature because
        /// the code that calls this was written against TMP and passes them.
        /// </summary>
        public void UpdateVertexData(OneTextVertexDataUpdateFlags flags)
        {
            if (flags == OneTextVertexDataUpdateFlags.None) return;
            if (_textInfo == null) return;
            _vertexOverride = true;
            SetVerticesDirty();
        }

        /// <summary>Push every edited stream; the same as passing <c>All</c>.</summary>
        public void UpdateVertexData() => UpdateVertexData(OneTextVertexDataUpdateFlags.All);

        /// <summary>
        /// The other way the same caller pushes the same edit.
        ///
        /// TextMesh Pro draws each material into a Mesh of its own, so its
        /// callers hand one back when they have finished with it. OneText draws
        /// through a CanvasRenderer and has no such Mesh; the one passed here is
        /// the scratch carrier from <see cref="OneTextMeshInfo.mesh"/>, and by
        /// the time it arrives the caller has already written the same vertices
        /// into the array this reads. So the Mesh is accepted, not read — which
        /// is honest rather than lossy, because reading it back would only
        /// return what we already have.
        ///
        /// <paramref name="index"/> is which material's mesh it is. OneText draws
        /// a label in one pass, so there is exactly one and it is always 0.
        /// </summary>
        public void UpdateGeometry(Mesh mesh, int index) =>
            UpdateVertexData(OneTextVertexDataUpdateFlags.All);

        /// <summary>
        /// Go back to drawing the tiles, discarding anything a caller pushed.
        /// Called by the layout path, which invalidates every index those
        /// vertices were written against.
        /// </summary>
        private void DropVertexOverride()
        {
            _vertexOverride = false;
            _textInfoStamp = -1;
        }

        /// <summary>
        /// The glyph fill, which for OneText is the label's colour: there is no
        /// separate face channel to tint underneath it.
        /// </summary>
        public Color32 faceColor
        {
            get => color;
            set => color = value;
        }

        /// <summary>
        /// The label's own outline colour. Setting it turns the outline on;
        /// setting it fully transparent turns it off, which is how the code that
        /// asks for this expects to clear one.
        ///
        /// It lands on the label rather than on a material because OneText packs
        /// decorations into the mesh — one material draws outlined and plain text
        /// in the same batch — so there is no per-label material to write to and
        /// nothing to unbatch by asking.
        /// </summary>
        public Color32 outlineColor
        {
            get => _decoration.HasOutline ? _decoration.OutlineColor : new Color32(0, 0, 0, 0);
            set
            {
                if (value.a == 0)
                {
                    if (!_decoration.HasOutline) return;
                    _decoration.Set &= ~TextDecoration.Parts.Outline;
                }
                else
                {
                    _decoration.Set |= TextDecoration.Parts.Outline;
                    _decoration.OutlineColor = value;
                    if (_decoration.OutlineWidth <= 0f) _decoration.OutlineWidth = 0.1f;
                }
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// The label's own glow colour, on the same terms as
        /// <see cref="outlineColor"/>: transparent turns it off.
        /// </summary>
        public Color32 glowColor
        {
            get => _decoration.HasGlow ? _decoration.GlowColor : new Color32(0, 0, 0, 0);
            set
            {
                if (value.a == 0)
                {
                    if (!_decoration.HasGlow) return;
                    _decoration.Set &= ~TextDecoration.Parts.Glow;
                }
                else
                {
                    _decoration.Set |= TextDecoration.Parts.Glow;
                    _decoration.GlowColor = value;
                    if (_decoration.GlowRadius <= 0f) _decoration.GlowRadius = 0.2f;
                }
                SetVerticesDirty();
            }
        }

        /// <summary>How far the outline reaches, as a fraction of the em.</summary>
        public float outlineWidth
        {
            get => _decoration.HasOutline ? _decoration.OutlineWidth : 0f;
            set
            {
                _decoration.OutlineWidth = Mathf.Max(0f, value);
                if (_decoration.OutlineWidth > 0f) _decoration.Set |= TextDecoration.Parts.Outline;
                else _decoration.Set &= ~TextDecoration.Parts.Outline;
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// The material this label draws with. TextMesh Pro gives a label one
        /// per font asset and hands out both the shared one and an instance;
        /// OneText draws every label of every font through one, so both names
        /// answer with what <c>Graphic</c> already has and neither instantiates
        /// anything behind the caller's back.
        /// </summary>
        public Material fontSharedMaterial
        {
            get => material;
            set => material = value;
        }

        /// <inheritdoc cref="fontSharedMaterial"/>
        public Material fontMaterial
        {
            get => materialForRendering;
            set => material = value;
        }

        /// <summary>
        /// Draws one tile from the caller's vertices instead of its own.
        ///
        /// Returns false when there is nothing pushed for this tile, and the
        /// ordinary emission runs — which is what happens to every tile if the
        /// caller edited only the first few, and is why this is a per-tile
        /// question rather than a per-label one.
        /// </summary>
        private bool EmitOverrideQuad(VertexHelper vh, in TextQuad quad,
            in DecorationChannels decoration, int index)
        {
            var mesh = _textInfo?.meshInfo[0];
            int v = index * 4;
            if (mesh == null || v < 0 || v + 3 >= mesh.vertexCount) return false;

            var uv = quad.UvRect;
            int start = vh.currentVertCount;
            AddOverrideVert(vh, mesh.vertices[v + 0], uv.xMin, uv.yMin, mesh.colors32[v + 0], quad, decoration);
            AddOverrideVert(vh, mesh.vertices[v + 1], uv.xMin, uv.yMax, mesh.colors32[v + 1], quad, decoration);
            AddOverrideVert(vh, mesh.vertices[v + 2], uv.xMax, uv.yMax, mesh.colors32[v + 2], quad, decoration);
            AddOverrideVert(vh, mesh.vertices[v + 3], uv.xMax, uv.yMin, mesh.colors32[v + 3], quad, decoration);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
            return true;
        }

        /// <summary>
        /// <see cref="AddVertAt"/> with the colour taken from the caller rather
        /// than from the tile, which is the one thing a per-character tint has
        /// to be able to change.
        /// </summary>
        private static void AddOverrideVert(VertexHelper vh, Vector3 at, float u, float v,
            Color32 color, in TextQuad quad, in DecorationChannels decoration) =>
            AddVert(vh, at.x, at.y, u, v, quad.Layer, quad.UvRect, color, AtlasOf(quad), decoration);
    }
}
