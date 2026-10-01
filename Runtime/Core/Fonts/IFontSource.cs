using System;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Where <see cref="FontResidency"/> gets a font asset from, and where it
    /// gives it back.
    ///
    /// <para>The point of a source is the giving back. A font asset referenced
    /// from a scene, a prefab or the settings asset is loaded with whatever
    /// references it and stays for as long as that does, which for the project
    /// settings is the whole process: a game in five languages carries five
    /// fonts' worth of bytes to show one. A font reached through a key is
    /// loaded when somebody asks and can be let go of when nobody does.</para>
    ///
    /// <para>Two ship with the package: <see cref="ResourcesFontSource"/>,
    /// always, and an Addressables one in
    /// <c>OneText.Integrations.Addressables</c>, which compiles only in a
    /// project that has the Addressables package. A project with its own asset
    /// pipeline implements this and sets <see cref="FontResidency.Source"/>.</para>
    /// </summary>
    public interface IFontSource
    {
        /// <summary>
        /// Loads the font asset behind <paramref name="key"/> and calls
        /// <paramref name="done"/> exactly once, with the asset or with null
        /// when there is none. It may call back before returning.
        /// </summary>
        void Load(string key, Action<OneFontAsset> done);

        /// <summary>
        /// Loads synchronously, for the one caller that cannot wait: a label in
        /// the middle of a layout pass that has met a character only this font
        /// draws. Null when the key names nothing.
        /// </summary>
        OneFontAsset LoadNow(string key);

        /// <summary>
        /// Gives back an asset this source handed out. Called once per
        /// successful load, after the face has already been let go of.
        /// </summary>
        void Release(string key, OneFontAsset font);
    }

    /// <summary>Which built-in <see cref="IFontSource"/> the project settings ask for.</summary>
    public enum OneFontSourceKind
    {
        /// <summary>Keys are paths under a <c>Resources</c> folder, without the extension.</summary>
        Resources = 0,

        /// <summary>Keys are Addressables addresses. Needs the Addressables package.</summary>
        Addressables = 1,
    }

    /// <summary>
    /// Fonts under a <c>Resources</c> folder, keyed by their path there without
    /// the extension, the way <c>Resources.Load</c> takes it.
    ///
    /// <para>Releasing calls <c>Resources.UnloadAsset</c> in a player, which is
    /// what actually returns the asset's serialized bytes; a later load reads
    /// it back off disk. Not in the editor: there the loaded object
    /// <em>is</em> the asset in the project, and unloading it would throw away
    /// whatever an inspector had not saved yet. The face and the unpacked copy
    /// of the font are let go of either way, and in the editor they are what
    /// there is to free — the packed bytes are the asset file.</para>
    /// </summary>
    public sealed class ResourcesFontSource : IFontSource
    {
        public void Load(string key, Action<OneFontAsset> done)
        {
            if (string.IsNullOrEmpty(key))
            {
                done?.Invoke(null);
                return;
            }
            var request = Resources.LoadAsync<OneFontAsset>(key);
            request.completed += _ => done?.Invoke(request.asset as OneFontAsset);
        }

        public OneFontAsset LoadNow(string key) =>
            string.IsNullOrEmpty(key) ? null : Resources.Load<OneFontAsset>(key);

        public void Release(string key, OneFontAsset font)
        {
#if !UNITY_EDITOR
            if (font != null) Resources.UnloadAsset(font);
#endif
        }
    }
}
