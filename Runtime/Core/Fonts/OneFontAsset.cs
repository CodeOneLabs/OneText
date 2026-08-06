using System.Collections.Generic;
using System.IO;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using System.IO.Compression;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A font in the project: the font file itself, stored compressed inside
    /// the asset so nothing has to be renamed to <c>.bytes</c> and nothing is
    /// read from disk at runtime.
    ///
    /// The asset owns the parsed face, so every label referencing it shares one
    /// parse and one set of rasterized glyphs. Variable-font instances are
    /// cached per axis combination.
    /// </summary>
    // The Project window draws this rather than the default script sheet.
    [Icon("Packages/com.onetext.core/Editor/Icons/OneFontAsset.png")]
    public sealed class OneFontAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] private byte[] _data;
        [SerializeField, HideInInspector] private bool _compressed;
        [SerializeField, HideInInspector] private int _uncompressedLength;
        [SerializeField, HideInInspector] private Codec _codec = Codec.Deflate;

        /// <summary>
        /// How the embedded font file is packed. Stored per asset so fonts
        /// imported before a codec changed keep loading; the field defaults to
        /// <see cref="Codec.Deflate"/>, which is what those assets contain.
        /// </summary>
        public enum Codec
        {
            Deflate,
            Brotli,
        }

        [SerializeField] private string _familyName;
        [SerializeField] private string _sourcePath;

        [Tooltip("Language this face is designed for: ja, zh-Hans, zh-Hant, ko. Optional, and " +
            "only meaningful for the unified scripts: a Japanese label with a tagged Japanese " +
            "font gets Japanese shapes for 直 even when a Chinese font sits above it in the chain.")]
        [SerializeField] private string _language;

        private FontData _font;
        private Dictionary<string, FontData> _variants;

        /// <summary>Family name read from the font's name table at import time.</summary>
        public string FamilyName => string.IsNullOrEmpty(_familyName) ? name : _familyName;

        /// <summary>Project path of the font file this asset was created from.</summary>
        public string SourcePath => _sourcePath;

        /// <summary>
        /// Language tag this face is registered under in a font stack, or null.
        ///
        /// Han is unified in Unicode and not in type: 直 is one code point with
        /// a Japanese and a Chinese form, and which one a reader sees is decided
        /// by which font drew it. Without a tag the chain answers by position,
        /// so a Japanese string in a project that lists a Chinese font first is
        /// quietly wrong in a way no test and no screenshot review catches.
        /// </summary>
        public string Language { get => _language; set => _language = value; }

        /// <summary>Size of the embedded font file in bytes, before compression.</summary>
        public int FontFileSize => _uncompressedLength;

        /// <summary>Size actually stored in the asset.</summary>
        public int StoredSize => _data?.Length ?? 0;

        /// <summary>The shared parsed font. Never dispose it; the asset owns it.</summary>
        public FontData Font
        {
            get
            {
                if (_font == null || !_font.IsValid)
                {
                    var bytes = GetFontBytes();
                    if (bytes == null || bytes.Length == 0) return null;
                    _font = FontData.Load(bytes);
                }
                return _font;
            }
        }

        /// <summary>
        /// A shared instance of this font with the given variation axes applied.
        /// Instances are cached, so two labels asking for <c>wght 700</c> get the
        /// same handle, and therefore the same atlas entries.
        /// </summary>
        public FontData GetVariant(IReadOnlyList<FontVariation> variations)
        {
            var basis = Font;
            if (basis == null || variations == null || variations.Count == 0) return basis;

            string key = VariationKey(variations);
            _variants ??= new Dictionary<string, FontData>();
            if (_variants.TryGetValue(key, out var cached) && cached.IsValid) return cached;

            var array = new FontVariation[variations.Count];
            for (int i = 0; i < variations.Count; i++) array[i] = variations[i];
            var variant = basis.CreateVariant(array);
            _variants[key] = variant;
            return variant;
        }

        private static string VariationKey(IReadOnlyList<FontVariation> variations)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var variation in variations)
                builder.Append(variation.Tag).Append('=').Append(variation.Value).Append(';');
            return builder.ToString();
        }

        /// <summary>The raw font file bytes (decompressed on demand).</summary>
        public byte[] GetFontBytes()
        {
            if (_data == null || _data.Length == 0) return null;
            if (!_compressed) return _data;

            using var input = new MemoryStream(_data);
            using Stream deflate = _codec == Codec.Brotli
                ? new BrotliStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            var output = new byte[_uncompressedLength];
            int read = 0;
            while (read < output.Length)
            {
                int chunk = deflate.Read(output, read, output.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            return output;
        }

        /// <summary>
        /// Fills the asset from a font file. Editor-side setup.
        ///
        /// Brotli rather than deflate: font tables are repetitive enough that
        /// the difference is not marginal. A 55 MB Korean face measured 30.9 MB
        /// deflated and 12.0 MB brotli'd: 21.6 % of the original against
        /// 55.8 %, or 19 MB off the build for one font. Packing is slow (tens
        /// of seconds for a face that size) but happens once, at import;
        /// unpacking, which is what a player waits for, stays in the same range
        /// as deflate: 119 ms against 77 ms for that same 55 MB face, once at
        /// load rather than per frame.
        /// </summary>
        public void Initialize(byte[] fontBytes, string familyName, string sourcePath)
        {
            _uncompressedLength = fontBytes.Length;
            _familyName = familyName;
            _sourcePath = sourcePath;

            using var output = new MemoryStream();
            using (var packer = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                packer.Write(fontBytes, 0, fontBytes.Length);
            var compressed = output.ToArray();

            // Some fonts (already-compressed tables) do not shrink; keep whichever is smaller.
            if (compressed.Length < fontBytes.Length)
            {
                _data = compressed;
                _compressed = true;
                _codec = Codec.Brotli;
            }
            else
            {
                _data = fontBytes;
                _compressed = false;
                _codec = Codec.Brotli;
            }

            Release();
        }

        private void OnDisable() => Release();

        private void Release()
        {
            if (_variants != null)
            {
                foreach (var variant in _variants.Values) variant.Dispose();
                _variants.Clear();
            }
            _font?.Dispose();
            _font = null;
        }
    }
}
