using System.Collections.Generic;
using System.IO;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using System.IO.Compression;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// What a font asset knows about a font file it does not have yet.
    ///
    /// Everything here was read off a baked TextMesh Pro font asset during a
    /// migration, which is the one situation where a font asset has to exist
    /// before its font does: the atlas was shipped, the <c>.ttf</c> was not, and
    /// OneText rasterises from the <c>.ttf</c>. None of these numbers change how
    /// OneText renders — it sizes and pads its own atlas — they are provenance,
    /// so the person holding the placeholder can see which font it is waiting
    /// for and what the old one was built to do.
    /// </summary>
    [System.Serializable]
    public struct OneFontRecovery
    {
        /// <summary>File name the migration expects, e.g. <c>Cairo-Bold.ttf</c>.</summary>
        public string ExpectedFileName;

        /// <summary>Asset path of the font asset this was recovered from.</summary>
        public string RecoveredFrom;

        /// <summary>Style name as the old asset reported it: Regular, Bold, …</summary>
        public string StyleName;

        /// <summary>Point size the old atlas was baked at.</summary>
        public float PointSize;

        /// <summary>Atlas padding, in pixels, of the old atlas.</summary>
        public int Padding;

        public int AtlasWidth;
        public int AtlasHeight;

        /// <summary>How many characters the old atlas held.</summary>
        public int CharacterCount;

        /// <summary>Those characters as hex ranges, e.g. <c>0020-007E,00A0-00FF</c>.</summary>
        public string CharacterRanges;
    }

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

        // How hard the font was packed. Assets made before this existed were
        // packed as hard as this asset packs, which is what Smallest means, and
        // the enum's zero value says so rather than telling them to repack.
        [SerializeField, HideInInspector] private FontPacking _packing = FontPacking.Smallest;

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

        /// <summary>
        /// How hard the font file was packed on the way into the asset.
        ///
        /// Smallest is the zero value because an asset that predates this field
        /// deserializes to zero and was, in fact, packed hard. Fast is what an
        /// import chooses now: see <see cref="Initialize(byte[], string, string)"/>
        /// for the seconds it saves and the 12 % it costs.
        ///
        /// <para>Those older assets were packed at quality 11, where Smallest
        /// now means 10, so they hold slightly fewer bytes than a repack would
        /// produce today. Nothing needs doing about that — they are already
        /// smaller, and <see cref="Repack"/> declines to redo a font that is
        /// packed the way it was asked for.</para>
        /// </summary>
        public enum FontPacking
        {
            Smallest = 0,
            Fast = 1,
        }

        /// <summary>How hard this asset's font is packed.</summary>
        public FontPacking Packing => _packing;

        [SerializeField] private string _familyName;
        [SerializeField] private string _sourcePath;

        [SerializeField, HideInInspector] private bool _awaitingSource;
        [SerializeField, HideInInspector] private OneFontRecovery _recovery;

        [SerializeField, HideInInspector] private FontVariation[] _baseVariations;

        [Tooltip("Language this face is designed for: ja, zh-Hans, zh-Hant, ko. Optional, and " +
            "only meaningful for the unified scripts: a Japanese label with a tagged Japanese " +
            "font gets Japanese shapes for 直 even when a Chinese font sits above it in the chain.")]
        [SerializeField] private string _language;

        private FontData _font;
        private Dictionary<string, FontData> _variants;

        // The unpacked font file, kept alive next to the face that is reading
        // it. Cleared by Release, which is what runs whenever _data is replaced
        // — a refilled placeholder must not go on serving the old font — except
        // when it is the only copy of the font left; see DropPackedData.
        [System.NonSerialized] private byte[] _unpacked;

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

        /// <summary>
        /// True while this asset stands for a font whose file nobody has
        /// supplied yet.
        ///
        /// The migration makes these so that a project leaving TextMesh Pro with
        /// no source fonts still comes out with a coherent reference graph:
        /// every label points at the asset for the font it used to have, and
        /// dropping one <c>.ttf</c> in fixes all of them at once. Both halves of
        /// the condition matter — the flag says what this asset is <em>for</em>,
        /// the byte check says whether it has been answered — because filling
        /// one is exactly <see cref="Initialize"/>, and an asset that has bytes
        /// is a font whatever it used to be.
        /// </summary>
        public bool IsPlaceholder => _awaitingSource && (_data == null || _data.Length == 0);

        /// <summary>
        /// What was recovered from the font asset this one replaced. Meaningful
        /// on a placeholder, and kept after it is filled: knowing a font arrived
        /// through a migration is worth more later than the two strings cost.
        /// </summary>
        public OneFontRecovery Recovery => _recovery;

        /// <summary>Size of the embedded font file in bytes, before compression.</summary>
        public int FontFileSize => _uncompressedLength;

        /// <summary>
        /// Size actually stored in the asset. Zero in a player build once the
        /// font has been unpacked, because the packed copy is let go of at that
        /// point; see <see cref="DropPackedData"/>.
        /// </summary>
        public int StoredSize => _data?.Length ?? 0;

        /// <summary>The shared parsed font. Never dispose it; the asset owns it.</summary>
        public FontData Font
        {
            get
            {
                if (_font == null || !_font.IsValid)
                {
                    var bytes = Unpacked();
                    if (bytes == null || bytes.Length == 0) return StandIn();
                    _font = FontData.Load(bytes);
                }
                return _font;
            }
        }

        /// <summary>
        /// What a placeholder draws with until somebody gives it a font file:
        /// the project default, borrowed.
        ///
        /// A label whose font field is null already falls back to the project
        /// default, so pointing 6,000 labels at a placeholder would be a
        /// regression if the placeholder answered null — the field is no longer
        /// empty, and the label would find nothing to draw with and draw
        /// nothing. Answering with the default keeps the migration's promise
        /// that assigning the placeholder is not worse than leaving the field
        /// blank, and the warning is what stops that being silent.
        ///
        /// The face is borrowed, never cached in <c>_font</c> and never
        /// disposed here: it belongs to the default font asset, which every
        /// other label in the project is also drawing with.
        /// </summary>
        private FontData StandIn()
        {
            if (!_awaitingSource) return null;

            // A placeholder that is itself the project default, or a default
            // that is another placeholder, would ask the same question forever.
            if (s_standingIn) return null;

            var settings = OneTextSettings.Instance;
            var basis = settings != null ? settings.DefaultFont : null;
            bool usable = basis != null && !ReferenceEquals(basis, this);

            // Said whether or not there is anything to stand in with, and said
            // where the drawing happens rather than where the migration ran: a
            // build made from a project with an unfilled placeholder in it is
            // exactly the case nobody was watching the editor for.
            if (!_warned)
            {
                _warned = true;
                Debug.LogWarning($"OneText: the font '{FamilyName}' has no font file yet — it is a " +
                                 "placeholder the TextMesh Pro migration left behind, waiting for " +
                                 (string.IsNullOrEmpty(_recovery.ExpectedFileName)
                                     ? "its source .ttf/.otf"
                                     : _recovery.ExpectedFileName) + ". " +
                                 (usable
                                     ? "Text using it is drawn in the project default font until then."
                                     : "There is no project default font to stand in either, so " +
                                       "text using it does not draw at all."),
                    this);
            }

            if (!usable) return null;

            s_standingIn = true;
            try { return basis.Font; }
            finally { s_standingIn = false; }
        }

        private static bool s_standingIn;

        [System.NonSerialized] private bool _warned;

        /// <summary>
        /// The axis settings this asset <em>is</em>, as opposed to the ones a
        /// label asks for.
        ///
        /// One file, four faces: a TextMesh Pro project with Pretendard-Regular,
        /// -Medium, -Bold and -ExtraBold recovers a single variable
        /// <c>Pretendard.ttf</c>, and the four assets standing in for those four
        /// faces have to differ somehow. They differ here. The weight belongs to
        /// the asset because that is where "this is the ExtraBold" is true once
        /// rather than on each of the several thousand labels that happen to
        /// point at it, and because a label that sets nothing — which is nearly
        /// all of them — still has to come out extra bold.
        ///
        /// A label's own axes win per tag: this is the face, not a lock on it.
        /// </summary>
        public IReadOnlyList<FontVariation> BaseVariations =>
            _baseVariations ?? System.Array.Empty<FontVariation>();

        /// <summary>
        /// Records what face this asset stands for. Editor-side setup, written
        /// by the migration's recovery when it works out which weight a name was
        /// asking for; nothing at runtime changes it.
        /// </summary>
        public void SetBaseVariations(FontVariation[] variations)
        {
            _baseVariations = variations != null && variations.Length > 0 ? variations : null;
            // The cached instances were built against the old answer, including
            // the unvaried one handed out for an empty request.
            Release();
        }

        /// <summary>
        /// A shared instance of this font with the given variation axes applied.
        /// Instances are cached, so two labels asking for <c>wght 700</c> get the
        /// same handle, and therefore the same atlas entries.
        ///
        /// What the label asks for is layered over <see cref="BaseVariations"/>,
        /// tag by tag, so a label on a recovered ExtraBold asset that sets
        /// nothing gets extra bold and one that sets <c>wght 300</c> gets 300.
        /// An asset with no base variations resolves to exactly what it always
        /// did, key and all.
        /// </summary>
        public FontData GetVariant(IReadOnlyList<FontVariation> variations)
        {
            var basis = Font;
            if (basis == null) return null;

            var applied = Applied(variations);
            if (applied == null) return basis;

            string key = VariationKey(applied);
            _variants ??= new Dictionary<string, FontData>();
            if (_variants.TryGetValue(key, out var cached) && cached.IsValid) return cached;

            var variant = basis.CreateVariant(applied);
            _variants[key] = variant;
            return variant;
        }

        /// <summary>
        /// This asset's own axes with the caller's laid over them, or null when
        /// between them they come to nothing.
        /// </summary>
        private FontVariation[] Applied(IReadOnlyList<FontVariation> requested)
        {
            int mine = _baseVariations?.Length ?? 0;
            int asked = requested?.Count ?? 0;
            if (mine == 0 && asked == 0) return null;

            if (mine == 0)
            {
                var only = new FontVariation[asked];
                for (int i = 0; i < asked; i++) only[i] = requested[i];
                return only;
            }

            var merged = new List<FontVariation>(mine + asked);
            for (int i = 0; i < mine; i++) merged.Add(_baseVariations[i]);
            for (int i = 0; i < asked; i++)
            {
                var wanted = requested[i];
                int at = -1;
                for (int j = 0; j < merged.Count; j++)
                {
                    if (merged[j].Tag != wanted.Tag) continue;
                    at = j;
                    break;
                }
                if (at >= 0) merged[at] = wanted;
                else merged.Add(wanted);
            }
            return merged.ToArray();
        }

        private static string VariationKey(IReadOnlyList<FontVariation> variations)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var variation in variations)
                builder.Append(variation.Tag).Append('=').Append(variation.Value).Append(';');
            return builder.ToString();
        }

        /// <summary>
        /// The raw font file bytes, unpacked once and kept, as a copy the caller
        /// owns.
        ///
        /// Unpacking is not free: Noto Sans CJK KR is 93 ms of brotli and a
        /// 15.7 MB allocation on this machine, and a phone is several times
        /// that. It used to be paid on every call — <see cref="Font"/> hid that
        /// by caching the parsed face, and everyone else paid in full.
        ///
        /// The copy is not waste. <see cref="FontData.Load"/> pins the array it
        /// is handed and the native face reads through that pointer for as long
        /// as the face lives, so the kept array is the one every label in the
        /// project is drawing out of. A caller that writes one byte into it
        /// would corrupt the font everywhere at once, and this is a public
        /// method. Callers get their own.
        /// </summary>
        public byte[] GetFontBytes()
        {
            var bytes = Unpacked();
            return bytes == null ? null : (byte[])bytes.Clone();
        }

        /// <summary>
        /// The kept bytes themselves, for the one caller allowed to hold them:
        /// the face this asset owns, loads and disposes.
        /// </summary>
        private byte[] Unpacked()
        {
            // Ahead of the packed check rather than behind it: a player lets go
            // of the packed copy the moment this array exists, so from then on
            // this is the only one, and asking after the other first would make
            // the memory saving's first casualty the asset it was saved on.
            if (_unpacked != null) return _unpacked;

            if (_data == null || _data.Length == 0)
            {
                // A font that was here and is now gone is not a placeholder
                // that never had one, and it must not be left to be reported as
                // one — the symptom is blank labels and a warning blaming the
                // asset for a file it did have. Nothing should reach this:
                // Release keeps the unpacked array whenever it is the last copy
                // of the font. It is said this loudly because it is a canary
                // for some path that got past that rule.
                if (_compressed && _uncompressedLength > 0)
                    Debug.LogError($"OneText: the font '{FamilyName}' unpacked its font file and " +
                                   "then lost it, so text using it will not draw. This is a bug in " +
                                   "OneText rather than anything wrong with the asset.", this);
                return null;
            }

            if (!_compressed) return _data;

            using var input = new MemoryStream(_data);
            using Stream packed = _codec == Codec.Brotli
                ? new BrotliStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            var output = new byte[_uncompressedLength];
            int read = 0;
            while (read < output.Length)
            {
                int chunk = packed.Read(output, read, output.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            _unpacked = output;
#if !UNITY_EDITOR
            DropPackedData();
#endif
            return output;
        }

        /// <summary>
        /// Lets go of the packed copy of the font file, now that the unpacked
        /// one has made it redundant. False when there was nothing to drop.
        ///
        /// A CJK face is what makes this worth doing. Noto Sans CJK KR is
        /// 10.9 MB packed and 15.7 MB unpacked, and an asset holding both spends
        /// 10.9 MB of a phone's memory on bytes nothing will read again: the
        /// face reads the unpacked array and only the unpacked array.
        ///
        /// <para>Player builds only, and called from <see cref="Unpacked"/>
        /// rather than offered as an operation. In the editor this object
        /// <em>is</em> the asset on disk, so emptying a serialized field here
        /// and letting anything call <c>AssetDatabase.SaveAssets</c> afterwards
        /// — the migration, the Hub, somebody pressing Ctrl+S — writes the
        /// emptied asset out and destroys the font for good. The editor also
        /// still reads the packed bytes, for <see cref="Repack"/> and for
        /// <see cref="StoredSize"/>. A player never re-serializes a
        /// ScriptableObject, and one that is unloaded and referenced again is
        /// read back off the disk it was never written to.</para>
        ///
        /// <para>Internal rather than private because the <c>#if</c> above puts
        /// it out of the editor's reach, and a branch no test can take is a
        /// branch that is not tested.</para>
        /// </summary>
        internal bool DropPackedData()
        {
            // Only ever the packed copy, and only once the unpacked one exists
            // to stand in for it. An asset whose font did not shrink stores the
            // file itself in _data, and that array is the one the face is
            // reading through.
            if (!_compressed || _data == null || _unpacked == null) return false;
            _data = null;
            return true;
        }

        /// <summary>
        /// Fills the asset from a font file. Editor-side setup.
        ///
        /// Brotli rather than deflate: font tables are repetitive enough that
        /// the difference is not marginal, and unpacking — the part a player
        /// waits for — costs about what deflate costs.
        ///
        /// <para>What packing costs is a different question, and the honest
        /// answer put a Korean font import at half a minute. Measured through
        /// this same <c>BrotliEncoder</c> call, Noto Sans CJK KR (15.7 MB):
        /// quality 10 takes <b>17 seconds</b> and stores 11.1 MB; quality 6
        /// takes <b>0.4 seconds</b> and stores 12.6 MB. Pretendard (6.4 MB):
        /// 4.6 s → 2.09 MB against 0.15 s → 2.30 MB. So forty times the wait,
        /// with the editor frozen, for the last 12 % of the bytes. Importing
        /// packs fast; <see cref="Repack"/> spends the seconds deliberately, on
        /// the one occasion — shipping — where 12 % of a font is worth
        /// them.</para>
        ///
        /// <para>Ten rather than eleven because that is where brotli stops
        /// paying. Same font: q9 is 3.2 s → 11.96 MB, q10 is 17.2 s →
        /// 11.12 MB, q11 is 35.1 s → 10.93 MB. The expensive search switches on
        /// at ten, so nine gives back most of the win; eleven doubles the wait
        /// for the 1.7 % that is left. Ten is the last setting whose seconds buy
        /// something.</para>
        /// </summary>
        public void Initialize(byte[] fontBytes, string familyName, string sourcePath) =>
            Initialize(fontBytes, familyName, sourcePath, FontPacking.Fast);

        /// <inheritdoc cref="Initialize(byte[], string, string)"/>
        public void Initialize(byte[] fontBytes, string familyName, string sourcePath,
            FontPacking packing)
        {
            _uncompressedLength = fontBytes.Length;
            _familyName = familyName;
            _sourcePath = sourcePath;

            // Whatever this asset was before, it has a font in it now. Filling a
            // placeholder is this call and nothing else, which is what makes
            // "drop the .ttf in" a single action rather than a checklist.
            _awaitingSource = false;
            _warned = false;

            // A different font file is a different face, and the axis settings
            // that said which face the last one stood for do not survive it: a
            // placeholder filled with the ExtraBold and then refilled with the
            // Regular must not stay at 800.
            _baseVariations = null;

            var compressed = Pack(fontBytes, packing);

            // Some fonts (already-compressed tables) do not shrink; keep whichever is smaller.
            if (compressed != null && compressed.Length < fontBytes.Length)
            {
                _data = compressed;
                _compressed = true;
                _codec = Codec.Brotli;
                _packing = packing;
            }
            else
            {
                _data = fontBytes;
                _compressed = false;
                _codec = Codec.Brotli;
                _packing = packing;
            }

            Release();
        }

        /// <summary>
        /// Packs the font again at a different setting, keeping everything else
        /// about the asset. Returns false when there is nothing to pack or the
        /// asset is already packed that way.
        ///
        /// This is the slow one, on purpose and on demand: see
        /// <see cref="Initialize(byte[], string, string)"/> for what the minute
        /// buys.
        /// </summary>
        public bool Repack(FontPacking packing)
        {
            if (_packing == packing) return false;
            var fontBytes = GetFontBytes();
            if (fontBytes == null || fontBytes.Length == 0) return false;

            Initialize(fontBytes, _familyName, _sourcePath, packing);
            return true;
        }

        /// <summary>
        /// Brotli, at a quality the caller chose.
        ///
        /// Through <c>BrotliEncoder</c> rather than <c>BrotliStream</c> because
        /// the stream only offers Fastest (quality 1) and Optimal (quality 11),
        /// and everything worth having is between them. The stream is still the
        /// fallback for any runtime that does not carry the encoder struct.
        /// </summary>
        private static byte[] Pack(byte[] fontBytes, FontPacking packing)
        {
            int quality = packing == FontPacking.Smallest ? 10 : 6;
            try
            {
                var destination = new byte[fontBytes.Length + 1024];
                if (BrotliEncoder.TryCompress(fontBytes, destination, out int written,
                        quality, 22))
                {
                    var packed = new byte[written];
                    System.Buffer.BlockCopy(destination, 0, packed, 0, written);
                    return packed;
                }
            }
            catch (System.Exception)
            {
                // No encoder on this runtime; the stream below is always there.
            }

            using var output = new MemoryStream();
            using (var packer = new BrotliStream(output,
                       packing == FontPacking.Smallest
                           ? CompressionLevel.Optimal
                           : CompressionLevel.Fastest,
                       leaveOpen: true))
                packer.Write(fontBytes, 0, fontBytes.Length);
            return output.ToArray();
        }

        /// <summary>
        /// Sets this asset up as a font that is expected but not yet here.
        ///
        /// The counterpart to <see cref="Initialize"/> for the one case where
        /// there are no bytes to pass: a TextMesh Pro font asset that shipped
        /// its baked atlas and not the font it was baked from. Everything that
        /// could be recovered is kept, so the only thing still missing when the
        /// user finds the file is the file.
        /// </summary>
        public void InitializePlaceholder(string familyName, OneFontRecovery recovery)
        {
            _familyName = familyName;
            _sourcePath = null;
            _recovery = recovery;
            _awaitingSource = true;
            _warned = false;

            _data = null;
            _compressed = false;
            _uncompressedLength = 0;
            _baseVariations = null;

            Release();
        }

        /// <summary>
        /// Records where a filled font came from, when it was recovered rather
        /// than imported. Provenance only; nothing reads it to render.
        /// </summary>
        public void SetRecovery(OneFontRecovery recovery) => _recovery = recovery;

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

            // Dropped with the face, never before it: the face is reading
            // through a pointer into this array.
            //
            // And not dropped at all once it is the only copy of the font left.
            // A player lets go of the packed bytes after unpacking, so for
            // those assets this array is the font, and there would be nothing
            // to load the face back from. Release is not only the way out:
            // SetBaseVariations is public and calls it to rebuild the variants
            // against a new face, which would otherwise take every label on
            // this font blank for the rest of the process.
            if (_compressed && _data == null) return;
            _unpacked = null;
        }
    }
}
