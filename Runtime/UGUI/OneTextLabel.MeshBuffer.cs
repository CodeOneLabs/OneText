using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace OneText.UGUI
{
    // The label's mesh, written once.
    //
    // uGUI's own route is VertexHelper: eight List<T>.Add calls per vertex into
    // eight pooled lists, then FillMesh hands each list to the Mesh on its own
    // (Clear, SetVertices, SetColors, four SetUVs, SetNormals, SetTangents,
    // SetTriangles, RecalculateBounds), then CanvasRenderer.SetMesh. Measured on
    // fifty 1-4 digit labels a frame, that was 91 us of emit and 157 us of
    // FillMesh+SetMesh out of a 760 us frame — a third of the frame spent
    // moving sixteen vertices through eleven native calls, per label.
    //
    // Here the vertices are written interleaved into one array, in the layout
    // the Mesh is declared with, and uploaded in one call. The index buffer is
    // the same 0-1-2 0-2-3 pattern for every quad there has ever been, so it is
    // only touched when the quad count differs from the previous upload.
    //
    // VertexHelper is still served: OnPopulateMesh copies this buffer into it,
    // so a label carrying an IMeshModifier (uGUI's Shadow and Outline, anyone's
    // BaseMeshEffect) goes down uGUI's route exactly as before and the modifier
    // sees the vertices it always saw.
    public sealed partial class OneTextLabel
    {
        /// <summary>One corner, in the order Unity lays vertex attributes out.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MeshVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector4 Tangent;
            public Color32 Color;
            public Vector4 Uv0, Uv1, Uv2, Uv3;
        }

        private static readonly VertexAttributeDescriptor[] s_meshLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4),
        };

        /// <summary>Quads on their way to a mesh. Four vertices each, no index list: the pattern is fixed.</summary>
        private sealed class QuadBuffer
        {
            public MeshVertex[] Vertices = new MeshVertex[256];
            public int VertexCount;

            public void Clear() => VertexCount = 0;

            /// <summary>Room for one more quad; the caller writes [start, start + 4).</summary>
            public MeshVertex[] ReserveQuad(out int start)
            {
                start = VertexCount;
                if (start + 4 > Vertices.Length) Array.Resize(ref Vertices, Vertices.Length * 2);
                VertexCount = start + 4;
                return Vertices;
            }

            /// <summary>The same quads through uGUI's helper, for the route that still needs it.</summary>
            public void CopyTo(VertexHelper vh)
            {
                for (int i = 0; i < VertexCount; i += 4)
                {
                    int start = vh.currentVertCount;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        ref var v = ref Vertices[i + corner];
                        vh.AddVert(v.Position, v.Color, v.Uv0, v.Uv1, v.Uv2, v.Uv3, v.Normal, v.Tangent);
                    }
                    vh.AddTriangle(start, start + 1, start + 2);
                    vh.AddTriangle(start, start + 2, start + 3);
                }
            }
        }

        // One buffer for every label, because uGUI rebuilds one graphic at a
        // time on one thread. The exception is a quad modifier that forces
        // another label to rebuild from inside its callback; that nested build
        // gets a buffer of its own rather than this one out from under its
        // caller.
        private static readonly QuadBuffer s_sharedQuads = new QuadBuffer();
        private static bool s_sharedQuadsBusy;

        private static QuadBuffer RentQuads(out bool shared)
        {
            shared = !s_sharedQuadsBusy;
            if (!shared) return new QuadBuffer();
            s_sharedQuadsBusy = true;
            s_sharedQuads.Clear();
            return s_sharedQuads;
        }

        private static void ReturnQuads(bool shared)
        {
            if (shared) s_sharedQuadsBusy = false;
        }

        /// <summary>
        /// Off sends every label down uGUI's VertexHelper route. For measuring
        /// one against the other in the same session, and for a project that
        /// finds a canvas this route draws differently.
        /// </summary>
        public static bool DirectMesh = true;

        // uGUI stops at 65000 vertices and says so; past that the old route
        // runs so the message is still the one people can search for.
        private const int MaxDirectVertices = 65000;

        private static Mesh s_directMesh, s_emptyMesh;
        private static int s_directQuads = -1;
        private static ushort[] s_quadIndices = Array.Empty<ushort>();

        private const MeshUpdateFlags Quiet =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers |
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds;

        protected override void UpdateGeometry()
        {
            long startedAt = AtlasDiagnostics.Now;
            // An IMeshModifier is written against VertexHelper, so a label that
            // carries one keeps the route that has a VertexHelper in it.
            if (!DirectMesh || TryGetComponent<IMeshModifier>(out _))
            {
                base.UpdateGeometry();
                AtlasDiagnostics.Add(ref AtlasDiagnostics.GeometryTicks, startedAt);
                return;
            }

            var quads = RentQuads(out bool shared);
            bool tooLarge = false;
            try
            {
                // The same gate Graphic.DoMeshGeneration applies: an inverted
                // rect draws nothing.
                var rect = rectTransform.rect;
                if (rect.width >= 0f && rect.height >= 0f) Populate(quads);
                tooLarge = quads.VertexCount > MaxDirectVertices;
                if (!tooLarge) Upload(quads, canvasRenderer);
            }
            finally
            {
                ReturnQuads(shared);
            }
            if (tooLarge) base.UpdateGeometry();
            AtlasDiagnostics.Add(ref AtlasDiagnostics.GeometryTicks, startedAt);
        }

        // For the tests that hold the two routes against each other: what the
        // last label to take this route handed its canvas, and how many have.
        internal static Mesh LastDirectMesh { get; private set; }
        internal static int DirectUploads { get; private set; }

        private static void Upload(QuadBuffer quads, CanvasRenderer renderer)
        {
            DirectUploads++;
            if (quads.VertexCount == 0)
            {
                if (s_emptyMesh == null)
                    s_emptyMesh = new Mesh { name = "OneText Empty", hideFlags = HideFlags.HideAndDontSave };
                renderer.SetMesh(s_emptyMesh);
                LastDirectMesh = s_emptyMesh;
                return;
            }

            if (s_directMesh == null)
            {
                s_directMesh = new Mesh { name = "OneText Label", hideFlags = HideFlags.HideAndDontSave };
                s_directMesh.MarkDynamic();
                s_directQuads = -1;
            }

            var mesh = s_directMesh;
            int vertexCount = quads.VertexCount;
            int quadCount = vertexCount / 4;
            if (quadCount != s_directQuads)
            {
                int indexCount = quadCount * 6;
                EnsureQuadIndices(quadCount);
                mesh.SetVertexBufferParams(vertexCount, s_meshLayout);
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
                mesh.SetIndexBufferData(s_quadIndices, 0, 0, indexCount, Quiet);
                // The vertex range is spelled out because Quiet carries
                // DontRecalculateBounds, and that flag is also what stops
                // Unity working out firstVertex and vertexCount. Left at their
                // defaults the submesh says it has no vertices, and the canvas
                // believes it: every label drew nothing, with a mesh whose
                // vertex and index buffers were both correct.
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount)
                {
                    firstVertex = 0,
                    vertexCount = vertexCount,
                }, Quiet);
                s_directQuads = quadCount;
            }
            mesh.SetVertexBufferData(quads.Vertices, 0, 0, vertexCount, 0, Quiet);
            mesh.bounds = BoundsOf(quads);
            renderer.SetMesh(mesh);
            LastDirectMesh = mesh;
        }

        private static Bounds BoundsOf(QuadBuffer quads)
        {
            var vertices = quads.Vertices;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < quads.VertexCount; i++)
            {
                float x = vertices[i].Position.x, y = vertices[i].Position.y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            var bounds = new Bounds();
            bounds.SetMinMax(new Vector3(minX, minY, 0f), new Vector3(maxX, maxY, 0f));
            return bounds;
        }

        private static void EnsureQuadIndices(int quadCount)
        {
            int have = s_quadIndices.Length / 6;
            if (have >= quadCount) return;
            int want = Mathf.Max(quadCount, Mathf.Max(64, have * 2));
            var indices = new ushort[want * 6];
            Array.Copy(s_quadIndices, indices, s_quadIndices.Length);
            for (int q = have; q < want; q++)
            {
                int v = q * 4, i = q * 6;
                indices[i] = (ushort)v;
                indices[i + 1] = (ushort)(v + 1);
                indices[i + 2] = (ushort)(v + 2);
                indices[i + 3] = (ushort)v;
                indices[i + 4] = (ushort)(v + 2);
                indices[i + 5] = (ushort)(v + 3);
            }
            s_quadIndices = indices;
        }

        private static void WriteVertex(ref MeshVertex vertex, float x, float y, float u, float v,
            in QuadChannels shared, Color32 c, in DecorationChannels decoration)
        {
            vertex.Position.x = x;
            vertex.Position.y = y;
            vertex.Position.z = 0f;
            vertex.Normal = s_Normal;
            vertex.Tangent = s_Tangent;
            vertex.Color = c;
            vertex.Uv0.x = u;
            vertex.Uv0.y = v;
            vertex.Uv0.z = shared.PackedLayer;
            vertex.Uv0.w = shared.UvMinV;
            vertex.Uv1 = decoration.Colors;
            vertex.Uv2 = shared.VMax;
            vertex.Uv3 = decoration.Shape;
        }
    }
}
