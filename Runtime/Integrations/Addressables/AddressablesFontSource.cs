using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace OneText
{
    /// <summary>
    /// Fonts loaded through Addressables, keyed by address.
    ///
    /// <para>Compiled only when the project has the Addressables package
    /// (1.17 or later, for <c>WaitForCompletion</c>): the assembly's define
    /// comes from a version define on <c>com.unity.addressables</c>, so a
    /// project without it never sees this file and never gets the
    /// dependency.</para>
    ///
    /// <para>Every load is its own handle and every release gives one back, in
    /// the order they were taken, which is the bookkeeping Addressables needs
    /// for its reference count to reach zero and the bundle to unload.
    /// <see cref="FontResidency"/> pairs them; a caller using this source
    /// directly must too.</para>
    /// </summary>
    public sealed class AddressablesFontSource : IFontSource
    {
        private readonly Dictionary<string, Stack<AsyncOperationHandle<OneFontAsset>>> _handles =
            new Dictionary<string, Stack<AsyncOperationHandle<OneFontAsset>>>(StringComparer.Ordinal);

        /// <summary>
        /// Makes this the source the project settings get when they say
        /// Addressables. Runs before anything can ask for a font.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() =>
            FontResidency.RegisterSource(OneFontSourceKind.Addressables,
                () => new AddressablesFontSource());

        public void Load(string key, Action<OneFontAsset> done)
        {
            if (string.IsNullOrEmpty(key))
            {
                done?.Invoke(null);
                return;
            }
            var handle = Addressables.LoadAssetAsync<OneFontAsset>(key);
            handle.Completed += completed => done?.Invoke(Take(key, completed));
        }

        public OneFontAsset LoadNow(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var handle = Addressables.LoadAssetAsync<OneFontAsset>(key);
            handle.WaitForCompletion();
            return Take(key, handle);
        }

        public void Release(string key, OneFontAsset font)
        {
            if (key == null || !_handles.TryGetValue(key, out var held) || held.Count == 0) return;
            var handle = held.Pop();
            if (held.Count == 0) _handles.Remove(key);
            if (handle.IsValid()) Addressables.Release(handle);
        }

        private OneFontAsset Take(string key, AsyncOperationHandle<OneFontAsset> handle)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                // A failed load still holds a handle, and an unreleased one
                // pins whatever bundle it got as far as opening.
                if (handle.IsValid()) Addressables.Release(handle);
                return null;
            }
            if (!_handles.TryGetValue(key, out var held))
                _handles[key] = held = new Stack<AsyncOperationHandle<OneFontAsset>>();
            held.Push(handle);
            return handle.Result;
        }
    }
}
