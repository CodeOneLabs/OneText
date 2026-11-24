using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace OneText
{
    /// <summary>
    /// Hands a managed vertex or index array to a mesh through the NativeArray overloads, as a
    /// view over the pinned array: no copy beyond the one the mesh makes anyway.
    ///
    /// The array overloads of <c>Mesh.SetVertexBufferData</c> / <c>SetIndexBufferData</c> first
    /// ask <c>UnsafeUtility.IsArrayBlittable</c>, which under IL2CPP is a reflection query on the
    /// element type (a type-object hash lookup behind a mutex) on every call: two per label
    /// rebuild, ~4 % of a phone's warm frame. The NativeArray overloads take the element size
    /// from <c>UnsafeUtility.SizeOf&lt;T&gt;</c> and skip it. The bytes the mesh receives are
    /// the same.
    /// </summary>
    public static unsafe class MeshUpload
    {
        public static void SetVertices<T>(Mesh mesh, T[] data, int count, MeshUpdateFlags flags) where T : unmanaged
        {
            fixed (T* p = data)
            {
                var view = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(p, count, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref view, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
                mesh.SetVertexBufferData(view, 0, 0, count, 0, flags);
            }
        }

        public static void SetIndices<T>(Mesh mesh, T[] data, int count, MeshUpdateFlags flags) where T : unmanaged
        {
            fixed (T* p = data)
            {
                var view = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(p, count, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref view, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
                mesh.SetIndexBufferData(view, 0, 0, count, flags);
            }
        }
    }
}
