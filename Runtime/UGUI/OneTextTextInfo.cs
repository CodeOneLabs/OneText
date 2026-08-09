using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// Which vertex streams a caller has edited and wants pushed to the mesh.
    ///
    /// Named and shaped after TextMesh Pro's flags because the code that reads
    /// them is somebody else's: a project migrating off TMP, or a package like
    /// DOTweenPro whose per-character animator was written against exactly this
    /// enum. A prettier name here would cost every one of them a hand edit.
    /// </summary>
    [Flags]
    public enum OneTextVertexDataUpdateFlags
    {
        None = 0,
        Vertices = 1,
        Uv0 = 2,
        Uv2 = 4,
        Uv4 = 8,
        Colors32 = 16,
        All = 255,
    }

    /// <summary>
    /// One addressable unit of laid-out text: where it sits, what it is, and
    /// which four vertices draw it.
    ///
    /// The unit is a <em>grapheme cluster</em>, not a UTF-16 char, because that
    /// is the only unit shaped text can be addressed by. An Arabic ligature is
    /// two characters in one tile, a Hangul syllable is three, a flag emoji is
    /// four; asking for "character 3" of any of them is a question with no
    /// answer. <see cref="index"/> still reports the UTF-16 offset, so code
    /// that slices the source string keeps working.
    ///
    /// Where several clusters share one merged tile, they report the same
    /// <see cref="vertexIndex"/> and the same corners: moving one moves the
    /// ligature, which is the only thing that can happen to it. The first
    /// cluster of such a group is the one flagged <see cref="isVisible"/>.
    /// </summary>
    public struct OneTextCharacterInfo
    {
        /// <summary>First UTF-16 code unit of the cluster; the whole cluster if it is one unit.</summary>
        public char character;

        /// <summary>UTF-16 index of the cluster's start in the source string.</summary>
        public int index;

        /// <summary>UTF-16 code units this cluster spans.</summary>
        public int stringLength;

        /// <summary>True if this cluster owns drawn geometry this frame.</summary>
        public bool isVisible;

        /// <summary>Zero-based line this cluster was laid out on.</summary>
        public int lineNumber;

        /// <summary>
        /// Index of this cluster's first vertex in <see cref="OneTextMeshInfo.vertices"/>.
        /// Four vertices follow it, in the order bottom-left, top-left,
        /// top-right, bottom-right — the same order TextMesh Pro uses, so an
        /// animator written against TMP indexes correctly without changing.
        /// </summary>
        public int vertexIndex;

        /// <summary>Which <see cref="OneTextTextInfo.meshInfo"/> entry holds those vertices.</summary>
        public int materialReferenceIndex;

        public Vector3 bottomLeft, topLeft, topRight, bottomRight;

        /// <summary>Pen position the cluster was placed at, on its baseline.</summary>
        public float origin;

        /// <summary>The baseline this cluster sits on, in the label's local space.</summary>
        public float baseLine;

        /// <summary>Line-box extents above and below the baseline, in local units.</summary>
        public float ascender, descender;

        /// <summary>Em size this cluster was shaped at.</summary>
        public float pointSize;

        /// <summary>
        /// Width over height of the drawn tile. Zero for a cluster that draws
        /// nothing, which is also what a caller multiplying by it wants: no
        /// skew on a character that is not there.
        /// </summary>
        public float aspectRatio;

        /// <summary>Colour the cluster is drawn with, after markup and tint.</summary>
        public Color32 color;

        /// <summary>Scale applied to the cluster relative to its run's em size.</summary>
        public float scale;
    }

    /// <summary>One laid-out line, addressed the way TMP's <c>TMP_LineInfo</c> is.</summary>
    public struct OneTextLineInfo
    {
        public int characterCount;
        public int firstCharacterIndex, lastCharacterIndex;
        public float lineHeight;
        public float ascender, descender, baseline;

        /// <summary>Extent along the inline axis, excluding trailing whitespace.</summary>
        public float width;

        /// <summary>UTF-16 code units the line spans in the source string.</summary>
        public int length;
    }

    /// <summary>A run of non-whitespace, for callers that animate word by word.</summary>
    public struct OneTextWordInfo
    {
        public int firstCharacterIndex, lastCharacterIndex, characterCount;

        /// <summary>The source text this word covers.</summary>
        public string GetWord() => _word ?? string.Empty;

        internal string _word;
    }

    /// <summary>One <c>&lt;link&gt;</c> span, for hit-testing and callbacks.</summary>
    public struct OneTextLinkInfo
    {
        public int linkIdFirstCharacterIndex, linkIdLength;
        public int linkTextfirstCharacterIndex, linkTextLength;

        internal string _id, _text;

        public string GetLinkID() => _id ?? string.Empty;
        public string GetLinkText() => _text ?? string.Empty;
    }

    /// <summary>
    /// The vertex streams behind one material, exposed for direct editing.
    ///
    /// OneText draws a label in a single pass, so there is exactly one of these
    /// and <see cref="OneTextCharacterInfo.materialReferenceIndex"/> is always
    /// 0. The array shape is kept because callers loop over it.
    /// </summary>
    public sealed class OneTextMeshInfo
    {
        public Vector3[] vertices = Array.Empty<Vector3>();
        public Color32[] colors32 = Array.Empty<Color32>();
        public Vector2[] uvs0 = Array.Empty<Vector2>();

        /// <summary>Vertices actually in use; the arrays may be longer.</summary>
        public int vertexCount;

        private Mesh _mesh;

        /// <summary>
        /// A carrier, and only a carrier.
        ///
        /// OneText draws through a <c>CanvasRenderer</c> and has no Mesh of its
        /// own to hand out. This one exists because the code that asks for it
        /// does exactly one thing with it — <c>meshInfo.mesh.vertices =
        /// meshInfo.vertices; target.UpdateGeometry(meshInfo.mesh, index);</c> —
        /// and both halves of that work if the Mesh is a scratch object whose
        /// contents nobody but the caller ever reads. Made on demand, so a
        /// project that never animates a character never allocates one.
        /// </summary>
        public Mesh mesh
        {
            get
            {
                if (_mesh == null) _mesh = new Mesh { name = "OneText scratch", hideFlags = HideFlags.HideAndDontSave };
                return _mesh;
            }
        }

        internal void Resize(int count)
        {
            if (vertices.Length < count)
            {
                Array.Resize(ref vertices, count);
                Array.Resize(ref colors32, count);
                Array.Resize(ref uvs0, count);
            }
            vertexCount = count;
        }

        /// <summary>A copy deep enough that editing one does not move the other.</summary>
        internal OneTextMeshInfo Copy() => new OneTextMeshInfo
        {
            vertices = (Vector3[])vertices.Clone(),
            colors32 = (Color32[])colors32.Clone(),
            uvs0 = (Vector2[])uvs0.Clone(),
            vertexCount = vertexCount,
        };
    }

    /// <summary>
    /// A TextMesh Pro-shaped view of a label's finished layout.
    ///
    /// This is a facade and says so: OneText's own per-character seam is
    /// <see cref="TextQuad"/>, which is richer (it knows about clusters,
    /// ligatures, rotation and merged tiles) and is what new code should use.
    /// This type exists so that code written against TMP — a project being
    /// migrated, or a third-party package whose source nobody wants to fork —
    /// compiles and behaves after a rename and nothing more.
    ///
    /// It is rebuilt on demand from the label's drawn quads, so reading it
    /// after changing the text is safe; holding one across a rebuild and
    /// expecting stale indices to stay valid is not, exactly as with TMP.
    /// </summary>
    public sealed class OneTextTextInfo
    {
        public int characterCount;
        public int spriteCount;
        public int lineCount;
        public int wordCount;
        public int linkCount;
        public int pageCount = 1;

        public OneTextCharacterInfo[] characterInfo = Array.Empty<OneTextCharacterInfo>();
        public OneTextLineInfo[] lineInfo = Array.Empty<OneTextLineInfo>();
        public OneTextWordInfo[] wordInfo = Array.Empty<OneTextWordInfo>();
        public OneTextLinkInfo[] linkInfo = Array.Empty<OneTextLinkInfo>();

        /// <summary>One entry, because OneText draws a label in one pass.</summary>
        public readonly OneTextMeshInfo[] meshInfo = { new OneTextMeshInfo() };

        /// <summary>
        /// A snapshot of the vertex streams as they stand, for a caller that
        /// wants to animate away from the laid-out positions and back again.
        ///
        /// The array is the caller's to keep and edit; nothing here holds a
        /// reference to it. That is the whole contract the per-character
        /// animators rely on — they take one of these when the text changes and
        /// lerp against it every frame.
        /// </summary>
        public OneTextMeshInfo[] CopyMeshInfoVertexData()
        {
            var copy = new OneTextMeshInfo[meshInfo.Length];
            for (int i = 0; i < meshInfo.Length; i++) copy[i] = meshInfo[i].Copy();
            return copy;
        }

        internal void EnsureCharacters(int count)
        {
            if (characterInfo.Length < count) Array.Resize(ref characterInfo, count);
            characterCount = count;
        }

        internal void EnsureLines(int count)
        {
            if (lineInfo.Length < count) Array.Resize(ref lineInfo, count);
            lineCount = count;
        }

        internal void SetWords(List<OneTextWordInfo> words)
        {
            if (wordInfo.Length < words.Count) Array.Resize(ref wordInfo, words.Count);
            for (int i = 0; i < words.Count; i++) wordInfo[i] = words[i];
            wordCount = words.Count;
        }

        internal void SetLinks(List<OneTextLinkInfo> links)
        {
            if (linkInfo.Length < links.Count) Array.Resize(ref linkInfo, links.Count);
            for (int i = 0; i < links.Count; i++) linkInfo[i] = links[i];
            linkCount = links.Count;
        }
    }
}
