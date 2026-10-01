using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Fonts read from font files in a folder on disk — by default
    /// <c>StreamingAssets/OneTextFonts</c> — by mapping them, with another
    /// source behind it for every key it cannot serve.
    ///
    /// <para><b>Why.</b> A font asset carries its font packed, and using it
    /// means unpacking the whole file onto the managed heap: a CJK face is
    /// 16 MB there for as long as it is loaded, however few of its glyphs are
    /// on screen. A font file the player can open is mapped instead
    /// (<see cref="FontData.LoadFile"/>): HarfBuzz reads it in place, only the
    /// pages it touches come into memory, and they are clean, file-backed
    /// pages, not heap. The cost moves to disk — the file is stored
    /// uncompressed — which on desktop is the cheaper of the two.</para>
    ///
    /// <para><b>Keys.</b> The same keys the fallback source takes, so one
    /// on-demand list in the project settings serves both: key
    /// <c>Fonts/NotoSansCJKsc-Regular Font</c> is the file
    /// <c>&lt;root&gt;/Fonts/NotoSansCJKsc-Regular Font.otf</c> (or <c>.ttf</c>,
    /// <c>.ttc</c>, <c>.otc</c>, or the key itself when it has an extension).
    /// A key that would leave the root (<c>..</c>, an absolute path) names
    /// nothing.</para>
    ///
    /// <para><b>Where files cannot be mapped</b> the fallback serves every key,
    /// without anyone having to say so: on Android StreamingAssets lives
    /// compressed inside the APK and is not a folder of files, and on Web it is
    /// a URL. A key with no file under the root falls back too, so a project
    /// can ship some fonts as files and the rest as assets.</para>
    ///
    /// <para>An asset this source made is a runtime object over the file
    /// (<see cref="OneFontAsset.FromFile"/>); releasing it unmaps the file and
    /// destroys the object. The language comes from the settings entry for the
    /// key, which is where <see cref="FontResidency"/> reads it.</para>
    /// </summary>
    public sealed class FileFontSource : IFontSource
    {
        /// <summary>The folder under StreamingAssets the built-in source reads.</summary>
        public const string DefaultFolder = "OneTextFonts";

        private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };

        private readonly string _root;
        private readonly IFontSource _fallback;
        private readonly bool _readable;
        private readonly HashSet<OneFontAsset> _made = new HashSet<OneFontAsset>();

        /// <param name="root">The folder font files are under.</param>
        /// <param name="fallback">
        /// Where keys go that this cannot serve: no file, or a root that is not
        /// a folder of files on this platform. Null for none.
        /// </param>
        public FileFontSource(string root, IFontSource fallback = null)
        {
            _root = root;
            _fallback = fallback;
            _readable = IsFileRoot(root);
        }

        /// <summary>
        /// The built-in source for <see cref="OneFontSourceKind.StreamingAssets"/>:
        /// <c>StreamingAssets/OneTextFonts</c>, falling back to Resources.
        /// </summary>
        public static FileFontSource StreamingAssets() =>
            new FileFontSource(DefaultRoot, new ResourcesFontSource());

        /// <summary><c>StreamingAssets/OneTextFonts</c> on this platform, as a path or URL.</summary>
        public static string DefaultRoot => Path.Combine(Application.streamingAssetsPath, DefaultFolder);

        /// <summary>The folder this reads.</summary>
        public string Root => _root;

        /// <summary>Where keys go that this cannot serve.</summary>
        public IFontSource Fallback => _fallback;

        /// <summary>
        /// Whether this platform can map files out of <see cref="Root"/> at all.
        /// False on Android (StreamingAssets is inside the APK) and on Web, and
        /// then every key goes to the fallback.
        /// </summary>
        public bool ReadsFiles => _readable;

        /// <summary>Fonts this source has made and not yet been given back.</summary>
        public int Live => _made.Count;

        /// <summary>
        /// Whether <paramref name="root"/> is a folder this platform can open
        /// files in: not a URL, not inside an archive, and a platform that can
        /// map files.
        /// </summary>
        public static bool IsFileRoot(string root)
        {
            if (string.IsNullOrEmpty(root) || !MappedFontFile.IsSupported) return false;
            // Android's streamingAssetsPath is jar:file://…!/assets; Web's is http(s)://.
            if (root.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
            if (root.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            return false;
#else
            return true;
#endif
        }

        /// <summary>
        /// The font file behind <paramref name="key"/>, or null when there is
        /// none here (the fallback's turn).
        /// </summary>
        public string PathFor(string key)
        {
            if (!_readable || string.IsNullOrEmpty(key)) return null;
            if (Path.IsPathRooted(key)) return null;
            foreach (string part in key.Split('/', '\\'))
                if (part == "..") return null;

            string basePath = Path.Combine(_root, key);
            if (HasFontExtension(key) && File.Exists(basePath)) return basePath;
            foreach (string extension in Extensions)
            {
                string candidate = basePath + extension;
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static bool HasFontExtension(string key)
        {
            string extension = Path.GetExtension(key);
            foreach (string known in Extensions)
                if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Load(string key, Action<OneFontAsset> done)
        {
            // Mapping is a system call, not a read: there is nothing to wait for.
            var path = PathFor(key);
            if (path != null)
            {
                done?.Invoke(Make(key, path));
                return;
            }
            if (_fallback != null) _fallback.Load(key, done);
            else done?.Invoke(null);
        }

        public OneFontAsset LoadNow(string key)
        {
            var path = PathFor(key);
            if (path != null) return Make(key, path);
            return _fallback?.LoadNow(key);
        }

        public void Release(string key, OneFontAsset font)
        {
            if (font == null) return;
            if (!_made.Remove(font))
            {
                _fallback?.Release(key, font);
                return;
            }
            font.Unload();
            if (Application.isPlaying) UnityEngine.Object.Destroy(font);
            else UnityEngine.Object.DestroyImmediate(font);
        }

        private OneFontAsset Make(string key, string path)
        {
            var asset = OneFontAsset.FromFile(path, LanguageOf(key));
            _made.Add(asset);
            return asset;
        }

        private static string LanguageOf(string key)
        {
            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references == null) return null;
            for (int i = 0; i < references.Count; i++)
                if (string.Equals(references[i].Key, key, StringComparison.Ordinal))
                    return references[i].Language;
            return null;
        }
    }
}
