using System;
using System.Collections;
using System.Collections.Generic;
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

            // The extent of what has been written, kept as it is written so the
            // upload does not have to walk 108-byte vertices again to find it.
            // Only true while every quad came through ReserveTrackedQuad; one
            // that did not (a rotated tile, a caller's own vertices) sends the
            // upload back to measuring the vertices themselves.
            public bool Tracked = true;
            public float MinX = float.MaxValue, MinY = float.MaxValue;
            public float MaxX = float.MinValue, MaxY = float.MinValue;

            public void Clear()
            {
                VertexCount = 0;
                Tracked = true;
                MinX = float.MaxValue;
                MinY = float.MaxValue;
                MaxX = float.MinValue;
                MaxY = float.MinValue;
            }

            /// <summary>Room for one more quad; the caller writes [start, start + 4).</summary>
            public MeshVertex[] ReserveQuad(out int start)
            {
                Tracked = false;
                start = VertexCount;
                if (start + 4 > Vertices.Length) Array.Resize(ref Vertices, Vertices.Length * 2);
                VertexCount = start + 4;
                return Vertices;
            }

            /// <summary>
            /// The same, for an axis-aligned quad whose corners are x or
            /// <paramref name="right"/> across and y or <paramref name="top"/>
            /// up. Folded in in the order <see cref="BoundsOf"/> meets them
            /// (x before x + w, y before y + h), with its comparisons, so the
            /// two agree to the bit, signed zeroes and NaN included.
            /// </summary>
            public MeshVertex[] ReserveTrackedQuad(float x, float y, float right, float top, out int start)
            {
                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (right < MinX) MinX = right;
                if (right > MaxX) MaxX = right;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
                if (top < MinY) MinY = top;
                if (top > MaxY) MaxY = top;
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

        /// <summary>
        /// The label's tiles. A list of its own rather than a List&lt;TextQuad&gt;
        /// because a tile is about 120 bytes (it carries a whole TextStyle), and
        /// List hands every one of them out and takes every one of them in by
        /// value: built in a temporary, copied into Add, copied into the array,
        /// copied out of the indexer, copied into the drawn list. This one can
        /// also give a reference to the slot, which is what the straight path
        /// writes and reads through.
        /// </summary>
        private sealed class QuadList : IReadOnlyList<TextQuad>
        {
            private TextQuad[] _items = new TextQuad[16];
            private int _count;

            public int Count => _count;

            public TextQuad this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
                    return _items[index];
                }
            }

            /// <summary>The slot itself. Valid until the next Add.</summary>
            public ref TextQuad At(int index) => ref _items[index];

            public void Clear() => _count = 0;

            public void Add(TextQuad quad)
            {
                if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                _items[_count++] = quad;
            }

            /// <summary>
            /// A new slot at the end, for the caller to fill. Not cleared: the
            /// caller writes every field, which is the contract that makes this
            /// cheaper than Add.
            /// </summary>
            public ref TextQuad AddSlot()
            {
                if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                return ref _items[_count++];
            }

            public IEnumerator<TextQuad> GetEnumerator()
            {
                for (int i = 0; i < _count; i++) yield return _items[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// The tiles as drawn. On the straight path (nothing between the cache
        /// and the mesh can move, drop or recolour a tile but the two rules
        /// below) the drawn list is a pure function of the cached one, so it is
        /// recorded as exactly that and only written out if someone reads it.
        /// Every frame of every label used to copy each tile into it, for a list
        /// almost nothing reads.
        /// </summary>
        private sealed class DrawnList : IReadOnlyList<TextQuad>
        {
            private readonly QuadList _own = new QuadList();
            private QuadList _source;
            private bool _pending, _whitenPictures;

            public int Count
            {
                get
                {
                    Materialize();
                    return _own.Count;
                }
            }

            public TextQuad this[int index]
            {
                get
                {
                    Materialize();
                    return _own[index];
                }
            }

            public void Clear()
            {
                _pending = false;
                _source = null;
                _own.Clear();
            }

            public void Add(TextQuad quad)
            {
                Materialize();
                _own.Add(quad);
            }

            /// <summary>
            /// Says this list is <paramref name="source"/> with the straight
            /// path's two rules applied: a picture keeps its colours (when the
            /// label does not tint them) and a tile with no alpha is not drawn.
            /// Those are the whole of what EmitQuads does to an untinted tile
            /// with no reveal, animator, modifier or vertex override in play.
            /// The source must not change until this is read or replaced; it
            /// is the label's quad cache, which only a rebuild writes, and every
            /// rebuild ends by emitting and so by replacing this.
            /// </summary>
            public void AliasOf(QuadList source, bool whitenPictures)
            {
                _own.Clear();
                _source = source;
                _whitenPictures = whitenPictures;
                _pending = true;
            }

            private void Materialize()
            {
                if (!_pending) return;
                _pending = false;
                var source = _source;
                _source = null;
                for (int i = 0; i < source.Count; i++)
                {
                    var quad = source.At(i);
                    if (quad.IsColor && _whitenPictures) quad.Color = new Color32(255, 255, 255, quad.Color.a);
                    if (quad.Color.a == 0) continue;
                    _own.Add(quad);
                }
            }

            public IEnumerator<TextQuad> GetEnumerator()
            {
                Materialize();
                return _own.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Off sends every label's quads through the general path: per-run and
        /// per-cluster work redone for every cluster, every tile copied through
        /// the reveal, animator, modifier and tint checks and into the drawn
        /// list. On, a label with none of those in play takes a path that skips
        /// what it cannot need. Same quads, same vertices; the switch is for
        /// A/B measurement and for the tests that hold the two together.
        /// </summary>
        public static bool StraightQuads = true;

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

        /// <summary>
        /// Off measures the mesh bounds by walking the uploaded vertices, as it
        /// always did, instead of reading the extent kept while they were
        /// written. Same box either way; the switch is for A/B and the tests.
        /// </summary>
        public static bool TrackedBounds = true;

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
            if (!DirectMesh || HasMeshModifier())
            {
                base.UpdateGeometry();
                // The renderer now holds uGUI's worker mesh, not ours.
                ReleaseHeldMesh();
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
                if (!tooLarge) Upload(this, quads, canvasRenderer);
            }
            finally
            {
                ReturnQuads(shared);
            }
            if (tooLarge)
            {
                base.UpdateGeometry();
                ReleaseHeldMesh();
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.GeometryTicks, startedAt);
        }

        // For the tests that hold the two routes against each other: what the
        // last label to take this route handed its canvas, and how many have.
        internal static Mesh LastDirectMesh { get; private set; }
        internal static int DirectUploads { get; private set; }

        private static void Upload(OneTextLabel label, QuadBuffer quads, CanvasRenderer renderer)
        {
            DirectUploads++;
            if (quads.VertexCount == 0)
            {
                if (s_emptyMesh == null)
                    s_emptyMesh = new Mesh { name = "OneText Empty", hideFlags = HideFlags.HideAndDontSave };
                renderer.SetMesh(s_emptyMesh);
                label.ReleaseHeldMesh();
                LastDirectMesh = s_emptyMesh;
                return;
            }

            Mesh mesh;
            int declaredQuads;
            LabelMesh taken = null;
            if (PooledMeshes)
            {
                taken = TakeMesh();
                mesh = taken.Mesh;
                declaredQuads = taken.Quads;
            }
            else
            {
                if (s_directMesh == null)
                {
                    s_directMesh = new Mesh { name = "OneText Label", hideFlags = HideFlags.HideAndDontSave };
                    s_directMesh.MarkDynamic();
                    s_directQuads = -1;
                }
                mesh = s_directMesh;
                declaredQuads = s_directQuads;
            }

            int vertexCount = quads.VertexCount;
            int quadCount = vertexCount / 4;
            if (quadCount != declaredQuads)
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
                if (taken != null) taken.Quads = quadCount;
                else s_directQuads = quadCount;
            }
            mesh.SetVertexBufferData(quads.Vertices, 0, 0, vertexCount, 0, Quiet);
            mesh.bounds = TrackedBounds && quads.Tracked ? TrackedBoundsOf(quads) : BoundsOf(quads);
            renderer.SetMesh(mesh);
            if (taken != null)
            {
                // Released after the renderer has moved on to the new one, not
                // before: until then it is still what this label is showing.
                label.ReleaseHeldMesh();
                label._heldMesh = taken;
                taken.Owner = label;
            }
            LastDirectMesh = mesh;
        }

        // ------------------------------------------------------- mesh pool
        //
        // Why a mesh per label and not one for all of them. What the numbers
        // say (C3, per label): with one mesh shared by every label,
        // SetVertexBufferData cost 0.78 us and SetMesh 0.28; writing into a
        // mesh no renderer has been handed since its last write, 0.31 and
        // 0.08. Clearing the shared mesh before each write also brought the
        // write down to 0.21, which is what a copy-on-write would do: SetMesh
        // appears to keep a reference to the mesh's data rather than copy it,
        // and the next write to that mesh copies the data out from under the
        // renderer first. A renderer that is handed another mesh lets go of
        // the old one, so each label holds the mesh its renderer is showing
        // and gives the previous one back here, and the next label writes
        // into a mesh nobody is reading. (Clearing instead cost more than it
        // saved: every upload then re-declares both buffers and the submesh.)
        //
        // If that reading is right the memory is what it was, one block of
        // vertex data per label: the shared mesh's old data stayed with the
        // renderer that held it. Either way what is new is a Mesh object per
        // label that draws through this route, plus the few on the free list.

        /// <summary>
        /// Off uploads every label through one shared mesh, as it always did.
        /// Same vertices, indices and bounds; for A/B and the tests.
        /// </summary>
        public static bool PooledMeshes = true;

        private sealed class LabelMesh
        {
            public Mesh Mesh;
            /// <summary>Quads the buffers were last declared for; -1 before the first.</summary>
            public int Quads = -1;
            /// <summary>The label whose renderer holds this mesh; null while it is free.</summary>
            public OneTextLabel Owner;
        }

        private static readonly Stack<LabelMesh> s_freeMeshes = new Stack<LabelMesh>();
        private static readonly List<LabelMesh> s_allMeshes = new List<LabelMesh>();
        private static int s_reclaimCursor;

        /// <summary>The pooled mesh this label's renderer is showing, if any.</summary>
        private LabelMesh _heldMesh;

        private void ReleaseHeldMesh()
        {
            var held = _heldMesh;
            if (held == null) return;
            _heldMesh = null;
            held.Owner = null;
            s_freeMeshes.Push(held);
        }

        private static LabelMesh TakeMesh()
        {
            while (s_freeMeshes.Count > 0)
            {
                var free = s_freeMeshes.Pop();
                if (free.Mesh != null) return free;
                s_allMeshes.Remove(free);
            }

            // A destroyed label never gives its mesh back, so before making a
            // new one look at a few held ones for an owner that is gone. A few
            // per miss rather than all of them: a pool only misses while labels
            // are being created, and a scan per creation would be quadratic in
            // the number of labels.
            for (int n = 0; n < 4 && s_allMeshes.Count > 0; n++)
            {
                s_reclaimCursor = (s_reclaimCursor + 1) % s_allMeshes.Count;
                var candidate = s_allMeshes[s_reclaimCursor];
                if (ReferenceEquals(candidate.Owner, null) || candidate.Owner != null) continue;
                candidate.Owner = null;
                if (candidate.Mesh != null) return candidate;
            }

            var mesh = new Mesh { name = "OneText Label", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            var made = new LabelMesh { Mesh = mesh };
            s_allMeshes.Add(made);
            return made;
        }

        /// <summary>
        /// Destroys every pooled mesh. Their managed handles do not survive a
        /// domain reload and the meshes, being HideAndDontSave, would; every
        /// label rebuilds on the other side of the reload, so nothing is left
        /// showing one.
        /// </summary>
        private static void ReleaseMeshPool()
        {
            foreach (var pooled in s_allMeshes)
            {
                if (pooled.Owner != null) pooled.Owner._heldMesh = null;
                if (pooled.Mesh != null) DestroyImmediate(pooled.Mesh);
            }
            s_allMeshes.Clear();
            s_freeMeshes.Clear();
            s_reclaimCursor = 0;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ReleaseMeshPoolOnAssemblyReload() =>
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseMeshPool;
#endif

        /// <summary>
        /// Off asks the GameObject for an <see cref="IMeshModifier"/> on every
        /// rebuild, as it always did, instead of remembering the answer for as
        /// long as the component list provably has not changed. Same route
        /// either way; the switch is for A/B and the tests.
        /// </summary>
        public static bool CachedModifierCheck = true;

        // The component list the answer below was read from: how many there
        // were and which one was last. A player can only change the list
        // through AddComponent, which appends (count and last both change),
        // and Destroy/DestroyImmediate, which shrinks it (count changes), so
        // the pair moves whenever the list does. The editor can also put a
        // component back in the middle (undo, paste, reorder), which can leave
        // both alone; every such operation publishes an object-change event or
        // an undo, and either bumps s_componentEpoch.
        private int _modifierListCount = -1;
        private Component _modifierListLast;
        private int _modifierListEpoch;
        private bool _hasMeshModifier;
        private static int s_componentEpoch;

        private bool HasMeshModifier()
        {
            if (!CachedModifierCheck) return TryGetComponent<IMeshModifier>(out _);
            var go = gameObject;
            int count = go.GetComponentCount();
            var last = count > 0 ? go.GetComponentAtIndex(count - 1) : null;
            if (count != _modifierListCount || !ReferenceEquals(last, _modifierListLast) ||
                _modifierListEpoch != s_componentEpoch)
            {
                _hasMeshModifier = TryGetComponent<IMeshModifier>(out _);
                _modifierListCount = count;
                _modifierListLast = last;
                _modifierListEpoch = s_componentEpoch;
            }
            return _hasMeshModifier;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ForgetComponentListsOnEditorChanges()
        {
            UnityEditor.Undo.undoRedoPerformed += () => s_componentEpoch++;
            UnityEditor.ObjectChangeEvents.changesPublished += (ref UnityEditor.ObjectChangeEventStream _) => s_componentEpoch++;
        }
#endif

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
            return BoxOf(minX, minY, maxX, maxY);
        }

        private static Bounds TrackedBoundsOf(QuadBuffer quads) =>
            BoxOf(quads.MinX, quads.MinY, quads.MaxX, quads.MaxY);

        /// <summary>
        /// The box the way Mesh.RecalculateBounds builds it, centre (min + max)
        /// / 2 and extent (max - min) / 2, which is what the VertexHelper route
        /// hands the canvas. Bounds.SetMinMax takes the centre as min + extent
        /// instead, and the two round differently: a label whose box did not
        /// happen to be exact came out an ulp away from the one uGUI would
        /// have given it.
        /// </summary>
        private static Bounds BoxOf(float minX, float minY, float maxX, float maxY) =>
            new Bounds(new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f),
                new Vector3(maxX - minX, maxY - minY, 0f));

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

        /// <summary>
        /// The quad build of <see cref="PopulateMesh"/>, with every answer that
        /// cannot change inside one build asked once instead of once per run
        /// or per cluster. Same tiles, in the same order, with the same fields;
        /// DirectMeshTests' straight-path cases hold the two builds together.
        /// </summary>
        private void BuildQuadsStraight(GlyphAtlas atlas, bool vertical)
        {
            var runs = _layout.Runs;

            // One pass to learn whether either band pass has anything to do.
            // Both used to walk every run, copying each, to find out that a
            // label with no <u>, <s> or <mark> has nothing for them.
            bool anyMark = false, anyLine = false;
            for (int r = 0; r < runs.Count; r++)
            {
                var flags = runs[r].Style.Flags;
                if ((flags & TextStyle.Flag.HasMark) != 0) anyMark = true;
                if ((flags & (TextStyle.Flag.Underline | TextStyle.Flag.Strikethrough)) != 0) anyLine = true;
            }
            if (anyMark) EmitBands(vertical, behind: true);

            // Decorations vary inside a run only by markup position; without
            // decoration markup the answer is the run's, and it is asked once.
            bool decorationsByPosition = _markup.HasMarkup && _markup.Decorations.Count > 0;

            FontData lastFont = null;
            bool lastIsColor = false;
            float lastSize = float.NaN, lastDensity = 0f;
            int lastPpem = 0;

            int runIndex = 0;
            foreach (var run in runs)
            {
                var font = run.Font;
                float runSize = run.FontSize > 0f ? run.FontSize : EffectiveFontSize;
                // DensityFor reads nothing a build changes, so a run the same
                // size as the one before has the same density and bucket.
                // A NaN size never equals the last one, so it is asked again.
                if (!(runSize == lastSize))
                {
                    lastSize = runSize;
                    lastDensity = DensityFor(runSize);
                    lastPpem = GlyphAtlas.QuantizePixelsPerEm(lastDensity);
                }
                float runDensity = lastDensity;
                int runPpem = lastPpem;
                float scale = runSize / font.UnitsPerEm;
                var runColor = run.Style.ResolveColor();
                float unitsPerTilePixel = font.UnitsPerEm / (float)runPpem;
                float maxClusterUnits = 1000f * unitsPerTilePixel;
                float mergeGapUnits = GlyphClusters.DefaultMergeGapUnits(font);

                var frame = FrameOf(run, vertical, scale);

                if (run.Style.IsSprite)
                {
                    EmitSprite(run, runSize, scale, frame, runColor, runIndex);
                    runIndex++;
                    continue;
                }

                if (!ReferenceEquals(font, lastFont))
                {
                    lastFont = font;
                    lastIsColor = ColorGlyphs.IsColorFont(font);
                }
                if (lastIsColor)
                {
                    EmitColorRun(run, font, runSize, scale, frame, runColor, runIndex, atlas);
                    runIndex++;
                    continue;
                }

                long splitStartedAt = AtlasDiagnostics.Now;
                if (frame.Vertical && !frame.Rotated)
                {
                    GlyphClusters.SplitUpright(font, _layout.Glyphs, run.GlyphStart, run.GlyphCount,
                        _clusters, _positioned);
                }
                else
                {
                    GlyphClusters.Split(font, _layout.Glyphs, run.GlyphStart, run.GlyphCount,
                        _clusters, _positioned, maxClusterUnits, mergeGapUnits);
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.SplitTicks, splitStartedAt);

                long lookupStartedAt = AtlasDiagnostics.Now;
                bool located = atlas.PrepareClusters(font, runDensity, _positioned,
                    _clusters, _clusterLocations, deferBake: true) &&
                    _clusterLocations.Count == _clusters.Count;
                AtlasDiagnostics.Add(ref AtlasDiagnostics.LookupTicks, lookupStartedAt);

                // Resolved at the first tile that is drawn, not at the top of
                // the run: a run with no ink never interned its decoration, and
                // interning it here would renumber every later run's.
                int runDecoration = -1;
                float baseline = frame.Baseline;
                bool precise = _precise;
                for (int clusterIndex = 0; clusterIndex < _clusters.Count; clusterIndex++)
                {
                    var cluster = _clusters[clusterIndex];
                    GlyphLocation loc;
                    if (located)
                    {
                        loc = _clusterLocations[clusterIndex];
                    }
                    else
                    {
                        lookupStartedAt = AtlasDiagnostics.Now;
                        loc = atlas.GetOrAddCluster(font, runDensity,
                            _positioned, cluster.Start, cluster.Count, cluster.Hash);
                        AtlasDiagnostics.Add(ref AtlasDiagnostics.LookupTicks, lookupStartedAt);
                    }
                    if (!loc.HasPixels) continue;

                    frame.Place(cluster.PenX, cluster.PenY, loc.OriginUnits, loc.SizeUnits,
                        out var position, out var size, out float rotation);
                    int first = _layout.GraphemeAt(cluster.TextStart);
                    // GraphemeAt(Max(start, end - 1)) is GraphemeAt(start)
                    // whenever end - 1 does not pass start: one cluster, one
                    // character, which is nearly every tile.
                    int last = cluster.TextEnd - 1 > cluster.TextStart
                        ? _layout.GraphemeAt(cluster.TextEnd - 1)
                        : first;
                    int decoration;
                    if (decorationsByPosition)
                    {
                        decoration = ResolveDecoration(cluster.TextStart, run.Style, run.SyntheticBold);
                    }
                    else
                    {
                        if (runDecoration < 0)
                            runDecoration = ResolveDecoration(cluster.TextStart, run.Style, run.SyntheticBold);
                        decoration = runDecoration;
                    }

                    ref var quad = ref _quads.AddSlot();
                    quad.Position = position;
                    quad.Size = size;
                    quad.Rotation = rotation;
                    quad.UvRect = loc.UvRect;
                    quad.Layer = loc.Layer;
                    quad.Color = runColor;
                    quad.FirstGrapheme = first;
                    quad.LastGrapheme = last;
                    quad.RunIndex = runIndex;
                    quad.Baseline = baseline;
                    quad.Style = run.Style;
                    quad.Decoration = decoration;
                    quad.IsColor = false;
                    quad.IsPrecise = precise;
                    quad.IsSolid = false;
                }
                runIndex++;
            }

            if (anyLine) EmitBands(vertical, behind: false);
        }

        /// <summary>
        /// <see cref="EmitQuads"/> for a label with no reveal, no animator, no
        /// modifier, no vertex override and an opaque white tint: what is left
        /// of the general loop is a picture keeping its colours, a tile with no
        /// alpha being skipped, and the vertices. Read through references, so
        /// no tile is copied on its way to the mesh.
        /// </summary>
        private void EmitQuadsStraight(QuadBuffer sink)
        {
            bool whitenPictures = !_tintColorGlyphs;
            var packed = _packedDecorations;
            int packedCount = packed.Count;
            int count = _quads.Count;
            for (int i = 0; i < count; i++)
            {
                ref var quad = ref _quads.At(i);
                var color = quad.Color;
                if (quad.IsColor && whitenPictures) color = new Color32(255, 255, 255, color.a);
                if (color.a == 0) continue;

                var decoration = quad.IsColor || quad.IsSolid || quad.Decoration <= 0
                        || quad.Decoration >= packedCount
                    ? DecorationChannels.None
                    : packed[quad.Decoration];
                if (quad.Rotation != 0f)
                {
                    var turned = quad;
                    turned.Color = color;
                    EmitRotatedQuad(sink, turned, decoration);
                }
                else
                {
                    EmitQuad(sink, quad.Position.x, quad.Position.y, quad.Size.x, quad.Size.y,
                        quad.UvRect, quad.Layer, color, AtlasOf(quad), decoration);
                }
            }
            _drawn.AliasOf(_quads, whitenPictures);
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
