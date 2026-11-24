using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Where a glyph lives in the atlas, plus how to build its quad:
    /// the quad spans [OriginUnits, OriginUnits + SizeUnits] in glyph space
    /// (relative to the pen position) and samples UvRect on Layer.
    /// </summary>
    public readonly struct GlyphLocation
    {
        public readonly bool HasPixels;
        public readonly Vector2 OriginUnits;
        public readonly Vector2 SizeUnits;
        public readonly Rect UvRect;
        public readonly int Layer;

        internal GlyphLocation(bool hasPixels, Vector2 originUnits, Vector2 sizeUnits, Rect uvRect, int layer)
        {
            HasPixels = hasPixels;
            OriginUnits = originUnits;
            SizeUnits = sizeUnits;
            UvRect = uvRect;
            Layer = layer;
        }
    }

    /// <summary>
    /// How much texture memory the atlas may spend. A project rendering CJK at
    /// several sizes needs more than a Latin one, and the difference is large
    /// enough that a constant cannot serve both.
    /// </summary>
    [Serializable]
    public struct GlyphAtlasSettings : IEquatable<GlyphAtlasSettings>
    {
        /// <summary>Edge length of each layer in pixels (power of two, 256 to 4096).</summary>
        public int TextureSize;

        /// <summary>Number of layers in the texture array (1 to 16).</summary>
        public int LayerCount;

        public static GlyphAtlasSettings Default => new GlyphAtlasSettings
        {
            TextureSize = 1024,
            LayerCount = 4,
        };

        /// <summary>
        /// Texture memory this configuration occupies (R8, no mips). A precise
        /// atlas holds the same texels in four channels and costs four times
        /// this; <see cref="GlyphAtlasStats.MemoryBytes"/> reports what a given
        /// atlas actually took.
        /// </summary>
        public long MemoryBytes => (long)TextureSize * TextureSize * LayerCount;

        /// <summary>Clamps to what the atlas can actually allocate.</summary>
        public GlyphAtlasSettings Validated()
        {
            int size = Mathf.NextPowerOfTwo(Mathf.Clamp(TextureSize <= 0 ? 1024 : TextureSize, 256, 4096));
            return new GlyphAtlasSettings
            {
                TextureSize = size,
                LayerCount = Mathf.Clamp(LayerCount <= 0 ? 4 : LayerCount, 1, 16),
            };
        }

        public bool Equals(GlyphAtlasSettings other) =>
            TextureSize == other.TextureSize && LayerCount == other.LayerCount;

        public override bool Equals(object obj) => obj is GlyphAtlasSettings o && Equals(o);

        public override int GetHashCode() => TextureSize * 397 + LayerCount;

        public override string ToString() =>
            $"{TextureSize}x{TextureSize}x{LayerCount} ({MemoryBytes / (1024f * 1024f):0.#} MB)";
    }

    /// <summary>What the atlas currently holds. Useful for budgeting and tests.</summary>
    public struct GlyphAtlasStats
    {
        public int TileCount;
        public long UsedPixels;
        public long CapacityPixels;
        public long MemoryBytes;
        public int Evictions;
        public int Compactions;
        public int ShelfCount;

        /// <summary>
        /// Tiles that found no room even after compaction and eviction: one
        /// frame asked for more than the atlas holds. They are not cached as
        /// failures, so they come back on a later frame; a non-zero count means
        /// the budget is too small for the working set, not that glyphs are
        /// permanently gone.
        /// </summary>
        public int Drops;

        /// <summary>Uploads that carried only the changed tiles.</summary>
        public int PartialUploads;

        /// <summary>Uploads that pushed the whole texture array.</summary>
        public int FullUploads;

        /// <summary>Live tiles that were baked by a prewarm rather than by drawing.</summary>
        public int PrewarmedTiles;

        /// <summary>Pixels those tiles occupy, the prewarmed slice of the pie.</summary>
        public long PrewarmedPixels;

        /// <summary>
        /// Distinct tiles this atlas has ever baked, and the pixels they would
        /// occupy all at once.
        ///
        /// The question an occupancy gauge cannot answer is the only one worth
        /// asking of a budget: not "how full is it now" (an atlas under
        /// pressure recycles and reads 30% full forever) but "how much did this
        /// session actually want". Demand counts each key once, whether it was
        /// evicted and re-baked ten times or never left, so a session that
        /// thrashes reports the size that would have stopped it.
        ///
        /// Zero unless <see cref="GlyphAtlas.TrackDemand"/> was on.
        /// </summary>
        public int DemandTiles;

        /// <inheritdoc cref="DemandTiles"/>
        public long DemandPixels;

        /// <summary>Fraction of the atlas occupied by live tiles (excludes shelf slack).</summary>
        public float UsedFraction => CapacityPixels == 0 ? 0f : UsedPixels / (float)CapacityPixels;

        /// <summary>Live pixels baked on demand while drawing, not by a prewarm.</summary>
        public long RuntimePixels => UsedPixels - PrewarmedPixels;

        /// <summary>Live tiles baked on demand while drawing, not by a prewarm.</summary>
        public int RuntimeTiles => TileCount - PrewarmedTiles;
    }

    /// <summary>
    /// Shared glyph SDF cache. Glyphs rasterize at a uniform density
    /// (quantized pixels-per-em), producing snugly-sized rectangular tiles
    /// that are shelf-packed into a Texture2DArray. Uniform density is what
    /// keeps neighboring glyphs' edges registered to each other at joints
    /// (connected scripts overlap their joins by design; resolve the
    /// overlap and there is nothing left to seam).
    ///
    /// Under pressure the atlas reclaims <em>tiles</em>, not layers: the
    /// least recently used tile is freed, its span returns to its shelf and
    /// merges with neighbouring free spans. When the free space is there but
    /// too scattered to fit a tile, the atlas compacts (repacks every live
    /// tile) instead of throwing work away. Both paths bump
    /// <see cref="Version"/>, because meshes bake UVs and have to be rebuilt
    /// when a tile they reference moves or disappears.
    /// </summary>
    public sealed class GlyphAtlas : IDisposable, GlyphRasterizer.IBatchOwner
    {
        // Dense enough that magnification stays under ~1.2x: junction
        // details are ~1-2px features and blur visibly beyond that.
        private static readonly int[] PpemBuckets =
            { 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256 };

        // Shelf heights snap to this ladder (~25% steps). A shelf's height is
        // set by whatever landed on it first, so unquantized heights both waste
        // the difference on every later tile and fragment the layer into shelves
        // nothing else can reuse.
        private static readonly int[] ShelfHeights =
        {
            8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128,
            160, 192, 224, 256, 320, 384, 448, 512,
        };

        private readonly int _textureSize;
        private readonly int _layerCount;
        private readonly int _bytesPerTexel;

        public Texture2DArray Texture { get; }

        /// <summary>
        /// Whether this atlas holds multi-channel fields (RGBA, four bytes a
        /// texel) rather than single-channel ones (R8). Set once at
        /// construction: a <c>Texture2DArray</c> has one format for every
        /// slice, so the two cannot share an atlas, and the shader reconstructs
        /// them with different maths.
        /// </summary>
        public bool Precise { get; }

        /// <summary>
        /// False once the backing texture has been destroyed out from under
        /// this instance, which only happens when a managed reference outlives
        /// the engine object, i.e. a play session boundary with Domain Reload
        /// off. Everything else about the atlas still looks healthy at that
        /// point, so this is the only cheap way to notice.
        /// </summary>
        public bool IsUsable => Texture != null;

        /// <summary>The size and layer count this atlas was created with.</summary>
        public GlyphAtlasSettings Settings =>
            new GlyphAtlasSettings { TextureSize = _textureSize, LayerCount = _layerCount };

        /// <summary>
        /// Whether the atlas may repack itself when a tile does not fit but
        /// the space exists. On by default; turning it off trades capacity for
        /// never paying a repack (and is how the repack's value is measured).
        /// </summary>
        public bool AutoCompact { get; set; } = true;

        /// <summary>
        /// Whether tiles committed from now on are marked as prewarmed.
        /// <see cref="AtlasPrewarm"/> holds this on for the duration of a warm;
        /// nothing else should touch it.
        ///
        /// The distinction is the whole content of the occupancy split: a tile
        /// the project predicted and a tile the frame discovered cost the same
        /// pixels and mean opposite things.
        /// </summary>
        public bool MarkingPrewarmed { get; set; }

        /// <summary>
        /// Whether atlases record every distinct tile they bake, so
        /// <see cref="GlyphAtlasStats.DemandPixels"/> can answer what budget a
        /// session needed. On in the editor and in development builds, where
        /// the Hub reads it; off in a release build, where nothing does and the
        /// bookkeeping would grow with the session.
        /// </summary>
        public static bool TrackDemand = Debug.isDebugBuild;

        /// <summary>
        /// Bumped whenever a live tile is evicted or moved. Meshes bake UVs, so
        /// anything that drew from this atlas has to rebuild when this changes.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Largest rasterization bucket NOT exceeding the requested size:
        /// SDFs reconstruct smoothly under magnification, but minification
        /// aliases (phase-dependent edge wobble, the last seam residue).
        /// </summary>
        /// <summary>
        /// Round the bucket up instead of down, so a glyph is minified rather
        /// than magnified.
        ///
        /// Here to be measured, not to ship: the comment above says magnifying
        /// is the better trade and nobody has put that next to TextMesh Pro,
        /// which bakes far above the size it draws at and looks smoother at
        /// small sizes for it. Remove this once the pictures have settled the
        /// question one way or the other.
        /// </summary>
        public static bool RoundBucketUp;

        public static int QuantizePixelsPerEm(float pixelsPerEm)
        {
            if (RoundBucketUp)
            {
                foreach (var b in PpemBuckets)
                    if (b >= pixelsPerEm) return b;
                return PpemBuckets[PpemBuckets.Length - 1];
            }

            int best = PpemBuckets[0];
            foreach (var b in PpemBuckets)
            {
                if (b <= pixelsPerEm) best = b;
                else break;
            }
            return best;
        }

        private readonly struct Key : IEquatable<Key>
        {
            /// <summary>
            /// <see cref="FontData.CacheId"/>, and specifically not the native
            /// handle.
            ///
            /// The handle is the real identity of a face while it lives, and it
            /// stops being an identity the moment the face is destroyed: the
            /// allocator hands the same address to the next
            /// <c>hb_font_create</c>, and a key built on it then matches tiles
            /// baked for a font that no longer exists. That is not theoretical.
            /// A label doing <c>SetVariations</c> per frame of a slider drag
            /// destroys one face and creates the next, lands on the same
            /// address every time after the first, and — since a face that is
            /// varied once always reads <c>Generation</c> 1 — collides on every
            /// field of the key. The weight the user asked for shaped
            /// correctly, so the advances moved and the tiles did not: text
            /// that changes width without changing weight, and glyphs drawn at
            /// the ink box of whichever weight got there first. The counter is
            /// monotonic for the process, so it cannot alias.
            /// </summary>
            private readonly int _font;

            private readonly long _id; // glyph id, or a cluster hash (top bit set)
            private readonly int _ppem;
            private readonly int _generation; // variable-font instance
            private readonly int _outline;    // flattening tolerance in force when baked
            private readonly int _raster;     // rasterizer settings in force when baked

            /// <summary>
            /// Single- or multi-channel field. Redundant while the two live in
            /// separate atlas instances, and kept regardless: a key that
            /// identified a tile by glyph and size alone would hand a precise
            /// label a single-channel tile the day anything lets the two share
            /// a cache, and that is not a bug anyone would come looking for
            /// here.
            /// </summary>
            private readonly bool _precise;

            public Key(FontData font, long id, int ppem, bool precise)
            {
                _font = font.CacheId;
                _id = id;
                _ppem = ppem;
                _generation = font.Generation;
                _outline = OutlineExtractor.Generation;
                _raster = GlyphRasterizer.Generation;
                _precise = precise;
            }

            /// <summary>
            /// The same key for another id: everything but the id is a property
            /// of the run, and a run's clusters were each re-reading the font's
            /// identity, its generation and two global generations to build it.
            /// </summary>
            public Key WithId(long id) => new Key(this, id);

            private Key(in Key template, long id)
            {
                _font = template._font;
                _id = id;
                _ppem = template._ppem;
                _generation = template._generation;
                _outline = template._outline;
                _raster = template._raster;
                _precise = template._precise;
            }

            /// <summary>
            /// A hash that costs a few multiplies rather than a seven-round
            /// HashCode.Combine. The id is a 64-bit FNV hash for a cluster and a
            /// small integer for a glyph; the rest are a font id and a handful
            /// of numbers that are the same for nearly every tile in the atlas.
            /// The hash only places a key in a bucket: equality is still
            /// <see cref="Equals(Key)"/>, every field of it.
            /// </summary>
            public int CheapHash()
            {
                unchecked
                {
                    ulong h = (ulong)_id * 0x9E3779B97F4A7C15UL;
                    h ^= (ulong)(uint)_font * 0xC2B2AE3D27D4EB4FUL;
                    h ^= (ulong)(uint)_ppem * 0x165667B19E3779F9UL;
                    h ^= (ulong)(uint)(_generation ^ (_outline << 10) ^ (_raster << 20) ^ (_precise ? 1 << 30 : 0))
                        * 0x27D4EB2F165667C5UL;
                    return (int)(h >> 32) ^ (int)h;
                }
            }

            /// <summary>The <see cref="FontData.CacheId"/> the tile was baked from.</summary>
            public int FontId => _font;

            public bool Equals(Key o) => _font == o._font && _id == o._id &&
                _ppem == o._ppem && _generation == o._generation && _outline == o._outline &&
                _raster == o._raster && _precise == o._precise;

            public override int GetHashCode() =>
                HashCode.Combine(_font, _id, _ppem, _generation, _outline, _raster, _precise);
        }

        /// <summary>
        /// Off keeps the dictionary on <see cref="Key.GetHashCode"/>, keys built
        /// field by field for every cluster, and an LRU relink on every hit even
        /// for the tile already at the tail. Read when an atlas is constructed,
        /// because a dictionary cannot change its hash under keys it holds.
        /// </summary>
        public static bool FastKeys = true;

        private sealed class CheapKeyComparer : IEqualityComparer<Key>
        {
            public static readonly CheapKeyComparer Instance = new CheapKeyComparer();
            public bool Equals(Key a, Key b) => a.Equals(b);
            public int GetHashCode(Key key) => key.CheapHash();
        }

        private readonly bool _fastKeys;

        /// <summary>
        /// Off sends every hit through the dictionary. On, a small table of
        /// the most recent hit per hash slot answers first: a warm frame asks
        /// for the same few dozen tiles over and over (every damage number is
        /// made of the same ten digits), and an array slot and one key compare
        /// are cheaper than a dictionary probe. Only with
        /// <see cref="FastKeys"/>, whose hash it indexes by; read when an atlas
        /// is constructed.
        /// </summary>
        public static bool RecentHits = true;

        private const int RecentSlots = 256;
        private readonly Entry[] _recent;

        private sealed class Entry
        {
            public Key Key;
            public GlyphLocation Location;
            public bool HasSlot;
            public bool Prewarmed;
            public int Layer, X, Y, Width, Height, ShelfIndex;
            public long LastUse;
            public LinkedListNode<Entry> Node; // position in the LRU list

            /// <summary>
            /// True exactly while this entry is the one <see cref="_entries"/>
            /// holds for its key: set where it is added, cleared everywhere one
            /// is removed or replaced. What lets the recent-hit table below
            /// trust an entry without asking the dictionary.
            /// </summary>
            public bool Live;

            // With FastEviction: this entry's place in the LRU order of the
            // tiles of its height, and in that of the tiles on its shelf.
            // Both lists are kept in the order of the global one (oldest at
            // the head), which is what makes their heads the answers the
            // eviction walk used to search the whole atlas for.
            public Entry BucketPrev, BucketNext, ShelfPrev, ShelfNext;
            public Bucket InBucket;
            public Shelf OnShelf;
        }

        /// <summary>The tiles of one shelf height, oldest first (FastEviction).</summary>
        private sealed class Bucket
        {
            public Entry Head, Tail;
        }

        private struct Span
        {
            public int X, Width;
        }

        private sealed class Shelf
        {
            public int Y, Capacity, Height, X;   // Capacity = rows owned, Height = current bucket, X = fill cursor
            public int LiveTiles;
            public readonly List<Span> Free = new List<Span>();

            // No free span is wider than this (an upper bound, tightened to
            // the exact widest whenever a scan finds nothing that fits).
            public int FreeBound;

            // The tiles on this shelf, oldest first (FastEviction).
            public Entry Head, Tail;

            public bool IsEmpty => LiveTiles == 0;
        }

        private sealed class LayerState
        {
            public readonly List<Shelf> Shelves = new List<Shelf>();
            public int YCursor;
        }

        private readonly Dictionary<Key, Entry> _entries;
        // Oldest first: eviction walks from the head, a hit moves to the tail.
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private readonly LayerState[] _layers;
        private long _useStamp;
        private long _flushStamp;
        private long _usedPixels;
        private long _prewarmedPixels;
        private int _prewarmedTiles;
        private int _evictions, _compactions, _drops;

        // Every key ever committed, with the pixels it takes. Diagnostic only,
        // and unbounded by design; see GlyphAtlasStats.DemandTiles for why the
        // set has to survive eviction to mean anything.
        private Dictionary<Key, int> _demand;
        private long _demandPixels;
        private bool _reportedOverflow;
        private int _partialUploads, _fullUploads;
        private bool _dirty;
        private bool _compactedSinceFlush;

        // ------------------------------------------------------- device support

        /// <summary>
        /// Whether this device can hold an atlas at all.
        ///
        /// <para>Every atlas in the package is a <c>Texture2DArray</c>, and a
        /// device without array textures does not merely draw them badly: the
        /// constructor throws. It throws from the atlas a label acquires in
        /// <c>OnEnable</c>, so a headless server, a <c>-nographics</c> player or
        /// a GLES2-class GPU could not so much as switch a label on — even
        /// though nothing above the atlas wants pixels. Layout, measurement, hit
        /// testing and the input field are arithmetic over font tables and work
        /// perfectly without a GPU.</para>
        ///
        /// <para>So the answer is an atlas that exists and holds nothing:
        /// <see cref="IsUsable"/> false, every pixel path a no-op, every lookup
        /// a location with no pixels. That is the same state an atlas reaches
        /// when its texture dies at a play session boundary, which the whole
        /// package already copes with, so the capability costs one branch per
        /// entry point rather than a second code path.</para>
        /// </summary>
        internal static bool SupportsAtlasTextures =>
            ForceUnsupportedForTests.HasValue
                ? !ForceUnsupportedForTests.Value
                : SystemInfo.supports2DArrayTextures;

        /// <summary>
        /// Makes the check above answer false regardless of the device.
        ///
        /// <c>SystemInfo</c> cannot be written to, and every machine that runs
        /// the suite has array textures, so without this hook the headless path
        /// would be the one part of the atlas no test ever enters. Null, the
        /// default, asks the device. Tests set it in SetUp and clear it in
        /// TearDown; nothing else should touch it.
        /// </summary>
        internal static bool? ForceUnsupportedForTests;

        // Once per session, not once per label. The missing-shader message in
        // SharedGlyphAtlas learned that lesson the expensive way: a property
        // every label touches on enable turned one condition into eight
        // thousand identical lines of CI log.
        private static bool s_unsupportedReported;

        /// <summary>
        /// Says, once, that this process will lay text out but not draw it.
        /// A warning rather than an error: on a headless device nothing is
        /// wrong, and an error here fails every test that adds a label.
        /// </summary>
        private static void ReportUnsupported()
        {
            if (s_unsupportedReported) return;
            s_unsupportedReported = true;
            Debug.LogWarning("OneText: this device reports no 2D array texture support " +
                $"(graphics device: {SystemInfo.graphicsDeviceType}), so no glyph atlas is " +
                "allocated. Text still lays out, measures and edits; nothing will be drawn. " +
                "This is the expected state in a headless or -nographics process and is not a " +
                "fault in the package.");
        }

        public GlyphAtlas() : this(GlyphAtlasSettings.Default) { }

        /// <param name="precise">
        /// Hold multi-channel fields instead of single-channel ones. Four bytes
        /// a texel rather than one, which is why it is the caller's decision and
        /// not the atlas's.
        /// </param>
        public GlyphAtlas(GlyphAtlasSettings settings, bool precise = false)
        {
            settings = settings.Validated();
            _fastKeys = FastKeys;
            _entries = _fastKeys
                ? new Dictionary<Key, Entry>(CheapKeyComparer.Instance)
                : new Dictionary<Key, Entry>();
            _batchPending = _fastKeys
                ? new HashSet<Key>(CheapKeyComparer.Instance)
                : new HashSet<Key>();
            _recent = _fastKeys && RecentHits ? new Entry[RecentSlots] : null;
            _fastEviction = FastEviction;
            _textureSize = settings.TextureSize;
            _layerCount = settings.LayerCount;
            _layers = new LayerState[_layerCount];
            Precise = precise;
            _bytesPerTexel = precise ? 4 : 1;

            // Shelves and bookkeeping exist either way: GetStats, Compact and
            // the packer all walk the layer array, and an atlas that is missing
            // its texture should differ from a live one in exactly one place.
            for (int layer = 0; layer < _layerCount; layer++) _layers[layer] = new LayerState();

            if (!SupportsAtlasTextures)
            {
                // Texture stays null, so IsUsable is false and every member
                // that would touch a pixel returns without doing anything.
                ReportUnsupported();
                return;
            }

            Texture = new Texture2DArray(_textureSize, _textureSize, _layerCount,
                precise ? TextureFormat.RGBA32 : TextureFormat.R8, mipChain: false, linear: true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = precise ? "OneText Glyph Atlas (precise)" : "OneText Glyph Atlas",
                // Not cosmetic. Without DontSave, a texture created during a
                // play session is destroyed when that session ends, and with
                // Domain Reload disabled the managed GlyphAtlas holding it
                // survives, so the next session draws every glyph from a
                // destroyed texture. That is the blank-text-on-second-play bug,
                // and it is invisible with Domain Reload on.
                hideFlags = HideFlags.HideAndDontSave,
            };

            for (int layer = 0; layer < _layerCount; layer++) ClearLayerPixels(layer);
            Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        }

        /// <summary>Current occupancy, tile count and reclamation counters.</summary>
        public GlyphAtlasStats GetStats()
        {
            int shelves = 0;
            foreach (var layer in _layers) shelves += layer.Shelves.Count;
            return new GlyphAtlasStats
            {
                TileCount = _lru.Count,
                UsedPixels = _usedPixels,
                CapacityPixels = (long)_textureSize * _textureSize * _layerCount,
                MemoryBytes = (long)_textureSize * _textureSize * _layerCount * _bytesPerTexel,
                Evictions = _evictions,
                Compactions = _compactions,
                ShelfCount = shelves,
                Drops = _drops,
                PartialUploads = _partialUploads,
                FullUploads = _fullUploads,
                PrewarmedTiles = _prewarmedTiles,
                PrewarmedPixels = _prewarmedPixels,
                DemandTiles = _demand?.Count ?? 0,
                DemandPixels = _demandPixels,
            };
        }

        /// <summary>
        /// Returns the glyph's atlas location at a density bucket chosen from
        /// <paramref name="pixelsPerEm"/> (typically the label's font size),
        /// rasterizing on first use.
        /// </summary>
        public GlyphLocation GetOrAdd(FontData font, uint glyphId, float pixelsPerEm = 64f)
        {
            // No texture, no tile. Answered before rasterizing rather than
            // after: the field would have nowhere to go, and a headless frame
            // must not pay for outlines it cannot keep.
            if (!IsUsable) return default;

            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            var key = new Key(font, glyphId, ppem, Precise);
            if (TryTouch(key, out var hit)) return hit;

            CompletePendingBakes();
            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            var raster = GlyphRasterizer.Rasterize(font, glyphId, pixelsPerUnit, Precise);
            return Commit(key, raster, ppem);
        }

        /// <summary>
        /// Returns the atlas location of a cluster of connected glyphs baked
        /// as ONE merged SDF (joints live in the field interior: seam-proof).
        /// Offsets inside the tile are relative to the cluster's pen start.
        /// </summary>
        public GlyphLocation GetOrAddCluster(FontData font, float pixelsPerEm,
            List<PositionedGlyph> positioned, int start, int count, long hash)
        {
            if (!IsUsable) return default;

            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            var key = new Key(font, hash, ppem, Precise);
            if (TryTouch(key, out var hit)) return hit;

            // Written now, so what is pending goes first, and its pooled lines
            // are free again only after it has baked.
            CompletePendingBakes();
            long outlineStart = AtlasDiagnostics.Now;
            s_merged.Clear();
            s_mergedGroups.Clear();
            ResetPoolsIfIdle();
            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            BuildClusterContours(font, positioned, start, count, s_merged, s_mergedGroups, pixelsPerUnit);

            AtlasDiagnostics.Add(ref AtlasDiagnostics.OutlineTicks, outlineStart);
            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OutlineCount += count;

            var known = KnownOverlapsOf(font, positioned, start, count, s_mergedKnown);
            var raster = GlyphRasterizer.RasterizeContours(s_merged, s_mergedGroups, pixelsPerUnit, Precise, known);
            return Commit(key, raster, ppem);
        }

        /// <summary>
        /// Frees every tile baked from <paramref name="font"/>, at every size,
        /// and returns how many there were. For a face that is about to be
        /// destroyed: its cache id is never issued again, so its tiles could
        /// only ever be evicted, and until then they take room a live font
        /// needs. <see cref="Version"/> moves when anything is freed, so a mesh
        /// that somehow still points at one rebuilds rather than drawing
        /// whatever is written there next.
        /// </summary>
        public int Forget(FontData font)
        {
            if (font == null || _entries.Count == 0) return 0;
            int id = font.CacheId;
            List<Entry> doomed = null;
            foreach (var entry in _entries.Values)
                if (entry.Key.FontId == id) (doomed ??= new List<Entry>()).Add(entry);
            if (doomed == null) return 0;
            foreach (var entry in doomed) Release(entry);
            Version++;
            return doomed.Count;
        }

        /// <summary>
        /// Commits an already-rasterized tile under a key of the caller's
        /// choosing. Tests only: it is how a test places tiles of exact sizes,
        /// which is what decides how a flush packs them for upload.
        /// </summary>
        internal GlyphLocation CommitForTests(FontData font, long id, int ppem, RasterizedGlyph raster)
        {
            CompletePendingBakes();
            return IsUsable ? Commit(new Key(font, id, ppem, Precise), raster, ppem) : default;
        }

        /// <summary>True if the atlas already holds this glyph at this size.</summary>
        public bool Contains(FontData font, long id, float pixelsPerEm) =>
            _entries.ContainsKey(new Key(font, id, QuantizePixelsPerEm(pixelsPerEm), Precise));

        /// <summary>
        /// Bakes every cluster of a run that is not already cached, in one
        /// dispatch.
        ///
        /// Rasterizing a glyph at a time spends most of its cost on scheduling
        /// and temporary buffers rather than on the distance field: at 18 ppem a
        /// Hangul tile measured ~132 ns per texel against ~34 ns for a tile five
        /// times larger. Callers that know a whole run's clusters up front,
        /// which is every text frontend, should call this before reading the
        /// locations back, and the reads then all hit the cache.
        /// </summary>
        public void PrepareClusters(FontData font, float pixelsPerEm,
            List<PositionedGlyph> positioned, List<GlyphClusters.Cluster> clusters) =>
            PrepareClusters(font, pixelsPerEm, positioned, clusters, null);

        /// <summary>
        /// The same, filling <paramref name="locations"/> with one entry per
        /// cluster and answering true when it did.
        ///
        /// A warm run used to look every cluster up twice: once here to decide
        /// whether it needed baking, and once again in
        /// <see cref="GetOrAddCluster"/> to find out where it went. The key is
        /// seven fields wide and the dictionary is the hot one, so the second
        /// lookup is not free — and it is the common case, because a warm atlas
        /// is what a running game has.
        ///
        /// Only when nothing was baked: a bake can evict or repack, which moves
        /// tiles this pass already found, so a location taken before it is not
        /// a location afterwards. Then this answers false and the caller
        /// resolves cluster by cluster, exactly as it did before.
        /// </summary>
        public bool PrepareClusters(FontData font, float pixelsPerEm,
            List<PositionedGlyph> positioned, List<GlyphClusters.Cluster> clusters,
            List<GlyphLocation> locations) =>
            PrepareClusters(font, pixelsPerEm, positioned, clusters, locations, deferBake: false);

        /// <summary>
        /// Off bakes every run's misses in a dispatch of their own, inside the
        /// run's <see cref="PrepareClusters(FontData, float, List{PositionedGlyph}, List{GlyphClusters.Cluster}, List{GlyphLocation}, bool)"/>.
        /// On lets a caller that promises a <see cref="Flush"/> before anything
        /// is drawn (the uGUI label) place the tiles now and leave the fields
        /// to one dispatch for the whole frame. See <see cref="CompletePendingBakes"/>.
        /// </summary>
        public static bool DeferredBake = true;

        /// <summary>
        /// The same, and with <paramref name="deferBake"/> (and
        /// <see cref="DeferredBake"/>) the misses are placed but not baked:
        /// every tile gets its slot, its location and its place in the LRU
        /// exactly as an immediate bake would give it, and its field is
        /// computed with every other deferred miss of the frame in one
        /// dispatch, before the next upload (<see cref="Flush"/>) or anything
        /// else that reads or writes texels.
        ///
        /// Why it pays: a frame that retexts fifty labels with unseen glyphs
        /// was making one dispatch per run, ~1.6 tiles each, and blocking on
        /// every one. A dispatch that small cannot fill the workers, and the
        /// main thread waits for each in turn. One dispatch of the frame's
        /// two hundred tiles spreads over every core and is waited on once.
        ///
        /// What it must not change: nothing is drawn from the CPU copy, only
        /// from the GPU texture, which sees tiles only at <see cref="Flush"/>,
        /// and the flush bakes first. Every write that is not deferred, and
        /// every read of texels (<see cref="Compact"/>, the upload), completes
        /// the pending bakes first, so writes land in the order they were
        /// made, the same order the immediate path makes them in — including
        /// a tile placed and evicted in the same frame, whose bytes the
        /// immediate path wrote and the next tile in its slot overwrote.
        /// </summary>
        public bool PrepareClusters(FontData font, float pixelsPerEm,
            List<PositionedGlyph> positioned, List<GlyphClusters.Cluster> clusters,
            List<GlyphLocation> locations, bool deferBake)
        {
            locations?.Clear();
            // Nothing can be baked without a texture, so answer the way a pass
            // that baked something answers: false sends the caller back through
            // GetOrAddCluster, which hands out no-pixel locations one at a time.
            if (!IsUsable) return false;
            if (clusters == null || clusters.Count == 0) return locations != null;
            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            bool collecting = locations != null;

            bool defer = deferBake && DeferredBake;
            // An immediate bake writes now, so whatever is pending goes first.
            if (!defer) CompletePendingBakes();
            _batchRequests.Clear();
            _batchKeys.Clear();
            _batchPending.Clear();
            // Pending requests still own their pooled lists until they bake.
            ResetPoolsIfIdle();

            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            int glyphsExtracted = 0;
            var template = new Key(font, 0, ppem, Precise);
            for (int c = 0; c < clusters.Count; c++)
            {
                var cluster = clusters[c];
                var key = _fastKeys ? template.WithId(cluster.Hash) : new Key(font, cluster.Hash, ppem, Precise);
                if (collecting)
                {
                    // TryTouch rather than ContainsKey: it answers the same
                    // question, hands back the location the caller is about to
                    // ask for, and refreshes the entry's place in the LRU. A
                    // tile drawn every frame through a path that only ever asked
                    // "is it there?" would otherwise age as if nothing used it,
                    // and be the first thing evicted under pressure.
                    if (TryTouch(key, out var found))
                    {
                        locations.Add(found);
                        continue;
                    }
                    collecting = false;
                    locations.Clear();
                }
                // Repeated words in one run share a tile; bake it once.
                if (_entries.ContainsKey(key) || !_batchPending.Add(key)) continue;

                // Timed around the extraction alone, not around the loop. A warm
                // run walks every cluster here and extracts none, and charging
                // that walk to "outline" reported 24 us a frame of outline work
                // against three glyphs actually extracted, a number that reads
                // as a hot rasterizer and is really the cache saying no. The
                // walk is lookup, and the caller already counts it as lookup.
                long outlineStart = AtlasDiagnostics.Now;
                RasterizeRequest request;
                if (FlatContours && !Precise && TryBuildFlat(font, positioned, cluster.Start, cluster.Count,
                        pixelsPerUnit, out int flatFirst, out int flatCount))
                {
                    RentContours(); // keeps the known-overlap pool in step
                    request = new RasterizeRequest
                    {
                        Flat = _flat,
                        FlatFirst = flatFirst,
                        FlatCount = flatCount,
                        PixelsPerUnit = pixelsPerUnit,
                    };
                }
                else
                {
                    var contours = RentContours();
                    var groups = RentGroups();
                    BuildClusterContours(font, positioned, cluster.Start, cluster.Count, contours, groups, pixelsPerUnit);
                    request = new RasterizeRequest
                    {
                        Contours = contours,
                        Groups = groups,
                        PixelsPerUnit = pixelsPerUnit,
                    };
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.OutlineTicks, outlineStart);
                glyphsExtracted += cluster.Count;

                request.KnownOverlaps = KnownOverlapsOf(font, positioned, cluster.Start, cluster.Count, RentKnown());
                _batchRequests.Add(request);
                _batchKeys.Add(key);
            }

            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OutlineCount += glyphsExtracted;
            if (_batchRequests.Count == 0) return collecting;

            if (defer)
            {
                // Placed in the order an immediate bake commits them, with the
                // tile the batch will fill, so slots, evictions and versions
                // come out exactly as they would have.
                for (int i = 0; i < _batchKeys.Count; i++)
                    Place(_batchKeys[i], _batchRequests[i], GlyphRasterizer.Measure(_batchRequests[i], Precise), ppem);
                if (BackgroundBake && _pendingTexels >= BackgroundBakeTexels) BakeInBackground();
                locations?.Clear();
                return false;
            }

            GlyphRasterizer.RasterizeBatch(_batchRequests, _batchResults, Precise);
            for (int i = 0; i < _batchKeys.Count && i < _batchResults.Count; i++)
                Commit(_batchKeys[i], _batchResults[i], ppem);
            locations?.Clear();
            return false;
        }

        /// <summary>
        /// One tile placed but not yet baked: the request that will bake it and
        /// the slot it was given. The slot is kept here rather than read off
        /// the entry because the entry may be evicted before the bake, and the
        /// immediate path would still have written its bytes there.
        /// </summary>
        private struct PendingBake
        {
            public int Layer, X, Y, Width, Height;
        }

        private List<PendingBake> _pendingBakes = new List<PendingBake>();
        private List<RasterizeRequest> _pendingRequests = new List<RasterizeRequest>();
        private int _pendingTexels;

        // The bakes handed to the workers in the background, waiting for
        // their fields; always placed before everything in _pendingBakes.
        private List<PendingBake> _flightBakes = new List<PendingBake>();
        private List<RasterizeRequest> _flightRequests = new List<RasterizeRequest>();
        private readonly List<RasterizedGlyph> _flightResults = new List<RasterizedGlyph>();

        /// <summary>
        /// Off bakes a frame's deferred tiles all at the flush, with the main
        /// thread waiting for the workers. On, every
        /// <see cref="BackgroundBakeTexels"/> texels of placed tiles go to the
        /// workers at once and the frame carries on building labels while
        /// they bake; the next batch (or the flush) collects them. The flush
        /// then waits only for the last few labels' tiles. Same requests, same
        /// fields, written in placement order.
        /// </summary>
        public static bool BackgroundBake = true;

        /// <summary>
        /// Texels of placed tiles that make a background batch: enough for the
        /// workers to share (a CJK tile at UI sizes is ~800), small enough that
        /// what is left for the flush to wait on is short.
        /// </summary>
        public static int BackgroundBakeTexels = 16384;

        /// <summary>True while placed tiles are waiting for their fields.</summary>
        internal bool HasPendingBakes => _pendingBakes.Count > 0 || _flightBakes.Count > 0;

        private void ResetPoolsIfIdle()
        {
            if (_pendingBakes.Count > 0 || _flightBakes.Count > 0) return;
            _pooledUsed = 0;
            _pooledLines = 0;
            _flat.Clear();
        }

        /// <summary>
        /// Off builds every miss's contours as lists of lists, copied out of
        /// the extracted outline, walked again to size the tile and again to
        /// pack it for the job: four passes of managed code over every point,
        /// a millisecond and more of a frame that bakes two hundred glyphs. On
        /// (single-channel atlases, with the native outline path), the outline
        /// is drawn straight into one flat buffer, offset and bounded as it
        /// goes, and sizing and packing read the bounds and copy the points
        /// once. Same points, same bounds, same tiles (<c>BakePathTests</c>).
        /// </summary>
        public static bool FlatContours = true;

        private readonly FlatContours _flat = new FlatContours();

        /// <summary>
        /// A cluster's contours into <see cref="_flat"/>, as
        /// <see cref="BuildClusterContours"/> would build them; false (and
        /// nothing kept) when the native outline path is unavailable.
        /// </summary>
        private bool TryBuildFlat(FontData font, List<PositionedGlyph> positioned, int start, int count,
            float pixelsPerUnit, out int first, out int contours)
        {
            first = _flat.ContourCount;
            int points = _flat.PointCount;
            for (int i = start; i < start + count; i++)
            {
                var g = positioned[i];
                if (!OutlineExtractor.AppendFlat(font, g.GlyphId, pixelsPerUnit, new Vector2(g.X, g.Y), i - start, _flat))
                {
                    _flat.ContourCount = first;
                    _flat.PointCount = points;
                    contours = 0;
                    return false;
                }
            }
            contours = _flat.ContourCount - first;
            return true;
        }

        /// <summary>Hands what is placed so far to the workers, collecting the previous batch first.</summary>
        private void BakeInBackground()
        {
            FinishBatchInternal();
            if (_pendingBakes.Count == 0) return;
            (_flightBakes, _pendingBakes) = (_pendingBakes, _flightBakes);
            (_flightRequests, _pendingRequests) = (_pendingRequests, _flightRequests);
            _pendingTexels = 0;
            if (!GlyphRasterizer.BeginBackground(_flightRequests, _flightResults, Precise, this))
                WriteFlight(); // nothing to bake: nothing to wait for either
        }

        void GlyphRasterizer.IBatchOwner.FinishBatch() => FinishBatchInternal();

        private void FinishBatchInternal()
        {
            if (_flightBakes.Count == 0) return;
            GlyphRasterizer.EndBatch();
            WriteFlight();
        }

        private void WriteFlight()
        {
            if (IsUsable)
            {
                long copyStart = AtlasDiagnostics.Now;
                for (int i = 0; i < _flightBakes.Count && i < _flightResults.Count; i++)
                {
                    var slot = _flightBakes[i];
                    var raster = _flightResults[i];
                    if (raster.IsEmpty || raster.Pixels == null ||
                        raster.Width != slot.Width || raster.Height != slot.Height) continue;
                    CopyIntoSlot(slot.Layer, slot.X, slot.Y, raster);
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.CopyTicks, copyStart);
            }
            _flightBakes.Clear();
            _flightRequests.Clear();
            _flightResults.Clear();
        }

        /// <summary>
        /// Bakes every placed-but-unbaked tile in one dispatch and writes each
        /// into the slot it was given, in placement order. Called by
        /// <see cref="Flush"/>, by <see cref="Compact"/>, and before any tile is
        /// written immediately; harmless to call when nothing is pending.
        /// </summary>
        public void CompletePendingBakes()
        {
            // The background batch was placed first, so it is written first.
            FinishBatchInternal();
            if (_pendingBakes.Count == 0) return;
            _pendingTexels = 0;
            GlyphRasterizer.RasterizeBatch(_pendingRequests, _batchResults, Precise);
            if (IsUsable)
            {
                long copyStart = AtlasDiagnostics.Now;
                for (int i = 0; i < _pendingBakes.Count && i < _batchResults.Count; i++)
                {
                    var slot = _pendingBakes[i];
                    var raster = _batchResults[i];
                    // Measured by the same code that sized the batch; a mismatch
                    // would mean the request changed under us.
                    if (raster.IsEmpty || raster.Width != slot.Width || raster.Height != slot.Height) continue;
                    CopyIntoSlot(slot.Layer, slot.X, slot.Y, raster);
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.CopyTicks, copyStart);
            }
            _pendingBakes.Clear();
            _pendingRequests.Clear();
        }

        private readonly List<RasterizeRequest> _batchRequests = new List<RasterizeRequest>();
        private readonly List<Key> _batchKeys = new List<Key>();
        private readonly HashSet<Key> _batchPending;
        private readonly List<RasterizedGlyph> _batchResults = new List<RasterizedGlyph>();
        private readonly List<List<List<Vector2>>> _contourPool = new List<List<List<Vector2>>>();
        private readonly List<List<int>> _groupPool = new List<List<int>>();
        private readonly List<List<Vector2>> _linePool = new List<List<Vector2>>();
        private int _pooledUsed, _pooledLines;

        // Batches are rebuilt every frame; keeping the lists alive keeps the
        // per-frame allocation out of the profile.
        private List<List<Vector2>> RentContours()
        {
            while (_contourPool.Count <= _pooledUsed) _contourPool.Add(new List<List<Vector2>>());
            var list = _contourPool[_pooledUsed++];
            list.Clear();
            return list;
        }

        private List<int> RentGroups()
        {
            while (_groupPool.Count < _pooledUsed) _groupPool.Add(new List<int>());
            var list = _groupPool[_pooledUsed - 1];
            list.Clear();
            return list;
        }

        private List<int> RentKnown()
        {
            while (_knownPool.Count < _pooledUsed) _knownPool.Add(new List<int>());
            var list = _knownPool[_pooledUsed - 1];
            list.Clear();
            return list;
        }

        private readonly List<List<int>> _knownPool = new List<List<int>>();

        /// <summary>
        /// Which glyphs of a cluster the font knows to have overlapping
        /// contours, by their index in the cluster (the group the rasterizer
        /// sees), into <paramref name="into"/>; null when the font has no index
        /// and the rasterizer has to look for itself.
        /// </summary>
        private static List<int> KnownOverlapsOf(FontData font, List<PositionedGlyph> positioned,
            int start, int count, List<int> into)
        {
            into.Clear();
            for (int i = start; i < start + count; i++)
            {
                if (!font.TryGetOverlap(positioned[i].GlyphId, out bool overlaps)) return null;
                if (overlaps) into.Add(i - start);
            }
            return into;
        }

        private static readonly List<int> s_mergedKnown = new List<int>();

        private List<Vector2> RentLine()
        {
            while (_linePool.Count <= _pooledLines) _linePool.Add(new List<Vector2>());
            var list = _linePool[_pooledLines++];
            list.Clear();
            return list;
        }

        /// <summary>
        /// Flattens a cluster's glyphs into one set of positioned contours,
        /// tagging each with the glyph it came from so signed distance stays
        /// per-glyph and unions cleanly at the joins.
        /// </summary>
        private void BuildClusterContours(FontData font, List<PositionedGlyph> positioned,
            int start, int count, List<List<Vector2>> merged, List<int> groups,
            float pixelsPerUnit)
        {
            for (int i = start; i < start + count; i++)
            {
                var g = positioned[i];
                s_scratch.Clear();
                OutlineExtractor.Extract(font, g.GlyphId, s_scratch, pixelsPerUnit);
                foreach (var contour in s_scratch.Contours)
                {
                    if (contour.Count < 2) continue;
                    var moved = RentLine();
                    var offset = new Vector2(g.X, g.Y);
                    foreach (var p in contour)
                        moved.Add(p + offset);
                    merged.Add(moved);
                    groups.Add(i - start); // per-glyph signed-distance group
                }
            }
        }

        private static readonly GlyphOutline s_scratch = new GlyphOutline();
        private static readonly List<List<Vector2>> s_merged = new List<List<Vector2>>();
        private static readonly List<int> s_mergedGroups = new List<int>();

        private bool TryTouch(Key key, out GlyphLocation location)
        {
            Entry hit;
            if (_recent != null)
            {
                int slot = key.CheapHash() & (RecentSlots - 1);
                hit = _recent[slot];
                if (hit == null || !hit.Live || !hit.Key.Equals(key))
                {
                    if (!_entries.TryGetValue(key, out hit))
                    {
                        location = default;
                        return false;
                    }
                    _recent[slot] = hit;
                }
            }
            else if (!_entries.TryGetValue(key, out hit))
            {
                location = default;
                return false;
            }
            if (hit.HasSlot) Touch(hit);
            location = hit.Location;
            return true;
        }

        private void Touch(Entry entry)
        {
            entry.LastUse = ++_useStamp;
            if (entry.Node == null) return;
            // Already the newest: unlinking and relinking it at the tail puts
            // it exactly where it is.
            if (_fastKeys && _lru.Last == entry.Node) return;
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
            if (_fastEviction) Relink(entry);
        }

        // ------------------------------------------------------ FastEviction

        /// <summary>
        /// Off walks the whole LRU list to choose every eviction, and a second
        /// time, building a dictionary of every shelf, whenever the wanted
        /// height has nothing cold enough. Under sustained pressure that is
        /// the atlas's four thousand tiles walked per new glyph: a frame of
        /// fifty retexts with unseen glyphs evicts ~230 tiles and spent over a
        /// millisecond choosing them.
        ///
        /// On keeps, beside the global LRU list, one per shelf height and one
        /// per shelf, in the same order. The eviction rule is unchanged and
        /// so is every tile it picks: the oldest tile of the wanted height
        /// (the head of its list) when it is not newer than the last upload,
        /// else the oldest tile of whichever other shelf's newest tile is the
        /// oldest (a scan of shelf tails, not of tiles), else that recent
        /// head. Stamps are unique, so "oldest" has one answer either way.
        /// Read when an atlas is constructed.
        /// </summary>
        public static bool FastEviction = true;

        /// <summary>
        /// Off has every allocation look at every free span of every shelf of
        /// the wanted height. Under sustained pressure the shelves fill with
        /// slivers too narrow for anything, and a frame of two hundred new
        /// glyphs spent 1.4 ms walking ~360,000 of them. On keeps an upper
        /// bound on each shelf's widest free span and skips the walk when the
        /// tile is wider; the bound is exact again after any walk that finds
        /// nothing. The same span is chosen either way.
        /// </summary>
        public static bool FastShelfScan = true;

        private readonly bool _fastEviction;
        private readonly Dictionary<int, Bucket> _buckets = new Dictionary<int, Bucket>();

        private Bucket BucketFor(int wanted, bool create)
        {
            if (_buckets.TryGetValue(wanted, out var bucket) || !create) return bucket;
            bucket = new Bucket();
            _buckets.Add(wanted, bucket);
            return bucket;
        }

        /// <summary>Appends a newly placed tile to its height's and its shelf's lists.</summary>
        private void Link(Entry entry, Shelf shelf)
        {
            var bucket = BucketFor(ShelfHeightFor(entry.Height), create: true);
            entry.InBucket = bucket;
            entry.BucketPrev = bucket.Tail;
            entry.BucketNext = null;
            if (bucket.Tail != null) bucket.Tail.BucketNext = entry; else bucket.Head = entry;
            bucket.Tail = entry;
            LinkShelf(entry, shelf);
        }

        private static void LinkShelf(Entry entry, Shelf shelf)
        {
            entry.OnShelf = shelf;
            entry.ShelfPrev = shelf.Tail;
            entry.ShelfNext = null;
            if (shelf.Tail != null) shelf.Tail.ShelfNext = entry; else shelf.Head = entry;
            shelf.Tail = entry;
        }

        private static void Unlink(Entry entry)
        {
            var bucket = entry.InBucket;
            if (bucket != null)
            {
                if (entry.BucketPrev != null) entry.BucketPrev.BucketNext = entry.BucketNext; else bucket.Head = entry.BucketNext;
                if (entry.BucketNext != null) entry.BucketNext.BucketPrev = entry.BucketPrev; else bucket.Tail = entry.BucketPrev;
                entry.InBucket = null;
                entry.BucketPrev = entry.BucketNext = null;
            }
            UnlinkShelf(entry);
        }

        private static void UnlinkShelf(Entry entry)
        {
            var shelf = entry.OnShelf;
            if (shelf == null) return;
            if (entry.ShelfPrev != null) entry.ShelfPrev.ShelfNext = entry.ShelfNext; else shelf.Head = entry.ShelfNext;
            if (entry.ShelfNext != null) entry.ShelfNext.ShelfPrev = entry.ShelfPrev; else shelf.Tail = entry.ShelfPrev;
            entry.OnShelf = null;
            entry.ShelfPrev = entry.ShelfNext = null;
        }

        /// <summary>A touched tile is the newest of its height and of its shelf too.</summary>
        private static void Relink(Entry entry)
        {
            var bucket = entry.InBucket;
            if (bucket != null && bucket.Tail != entry)
            {
                if (entry.BucketPrev != null) entry.BucketPrev.BucketNext = entry.BucketNext; else bucket.Head = entry.BucketNext;
                entry.BucketNext.BucketPrev = entry.BucketPrev;
                entry.BucketPrev = bucket.Tail;
                entry.BucketNext = null;
                bucket.Tail.BucketNext = entry;
                bucket.Tail = entry;
            }
            var shelf = entry.OnShelf;
            if (shelf != null && shelf.Tail != entry)
            {
                if (entry.ShelfPrev != null) entry.ShelfPrev.ShelfNext = entry.ShelfNext; else shelf.Head = entry.ShelfNext;
                entry.ShelfNext.ShelfPrev = entry.ShelfPrev;
                entry.ShelfPrev = shelf.Tail;
                entry.ShelfNext = null;
                shelf.Tail.ShelfNext = entry;
                shelf.Tail = entry;
            }
        }

        /// <summary><see cref="EvictForFit"/>'s choice, from the list heads.</summary>
        private Entry FastEvictionTarget(int wanted)
        {
            var bucket = BucketFor(wanted, create: false);
            Entry recentFallback = null;
            if (bucket?.Head != null)
            {
                if (bucket.Head.LastUse <= _flushStamp) return bucket.Head;
                recentFallback = bucket.Head;
            }

            Shelf coldest = null;
            long coldestUse = long.MaxValue;
            for (int li = 0; li < _layerCount; li++)
            {
                var shelves = _layers[li].Shelves;
                for (int si = 0; si < shelves.Count; si++)
                {
                    var shelf = shelves[si];
                    if (shelf.Tail == null || shelf.Height == wanted) continue;
                    if (shelf.Tail.LastUse >= coldestUse) continue;
                    coldestUse = shelf.Tail.LastUse;
                    coldest = shelf;
                }
            }
            return coldest != null ? coldest.Head : recentFallback;
        }

        /// <summary>
        /// Writes a baked tile now. Every caller completes the pending bakes
        /// before it rasterizes (so the bytes land in placement order, and so
        /// that bake cannot reuse the rasterizer's buffer
        /// <paramref name="raster"/> points into).
        /// </summary>
        private GlyphLocation Commit(Key key, RasterizedGlyph raster, int ppem) =>
            Place(key, default, raster, ppem, immediate: true);

        /// <summary>
        /// Gives a tile its slot and its entry. Immediately, it writes
        /// <paramref name="raster"/>'s pixels; otherwise <paramref name="raster"/>
        /// is only the tile's shape (<see cref="GlyphRasterizer.Measure"/>) and
        /// <paramref name="request"/> is queued to bake into the slot later.
        /// </summary>
        private GlyphLocation Place(Key key, in RasterizeRequest request, RasterizedGlyph raster, int ppem,
            bool immediate = false)
        {
            // The single place a tile is written, so the single place that has
            // to refuse when there is nowhere to write it. The callers above
            // check too, to save the rasterization; this is what makes it safe.
            if (!IsUsable) return default;

            // Empty content (spaces, control glyphs) is cached without a tile.
            if (raster.IsEmpty)
            {
                var emptyLocation = new GlyphLocation(false, Vector2.zero, Vector2.zero, default, 0);
                RetireReplaced(key);
                _entries[key] = new Entry { Key = key, Location = emptyLocation, Live = true };
                return emptyLocation;
            }

            if (!Allocate(raster.Width, raster.Height, out int layer, out int x, out int y, out int shelfIndex))
            {
                // Deliberately NOT cached. A frame that asks for more tiles than
                // the atlas holds is a transient condition: the tiles that lost
                // the race are wanted again next frame, when the ones that beat
                // them are no longer pinned. Caching the failure would turn
                // "this frame was too crowded" into "this glyph is blank for the
                // rest of the session", which is the failure mode where a
                // character silently disappears and never comes back.
                _drops++;
                if (!_reportedOverflow)
                {
                    _reportedOverflow = true;
                    Debug.LogError($"OneText: {raster.Width}x{raster.Height}@{ppem}ppem tile does not fit " +
                        $"a {Settings} atlas. Raise the atlas budget in Project Settings > OneText. " +
                        "(Further overflow this session is counted in GlyphAtlasStats.Drops, not logged.)");
                }
                return new GlyphLocation(false, Vector2.zero, Vector2.zero, default, 0);
            }

            var entry = new Entry
            {
                Key = key,
                Layer = layer,
                X = x,
                Y = y,
                Width = raster.Width,
                Height = raster.Height,
                ShelfIndex = shelfIndex,
                HasSlot = true,
                Prewarmed = MarkingPrewarmed,
            };
            if (immediate)
            {
                entry.Location = WriteSlot(layer, x, y, raster);
            }
            else
            {
                _pendingBakes.Add(new PendingBake
                {
                    Layer = layer, X = x, Y = y, Width = raster.Width, Height = raster.Height,
                });
                _pendingRequests.Add(request);
                _pendingTexels += raster.Width * raster.Height;
                MarkTileDirty(layer, x, y, raster.Width, raster.Height);
                entry.Location = LocationOf(layer, x, y, raster);
            }
            entry.Node = _lru.AddLast(entry);
            entry.LastUse = ++_useStamp;
            if (_fastEviction) Link(entry, _layers[layer].Shelves[shelfIndex]);
            RetireReplaced(key);
            entry.Live = true;
            _entries[key] = entry;
            int pixels = raster.Width * raster.Height;
            _usedPixels += pixels;
            if (entry.Prewarmed)
            {
                _prewarmedPixels += pixels;
                _prewarmedTiles++;
            }
            if (TrackDemand)
            {
                _demand ??= new Dictionary<Key, int>();
                // A key re-baked after eviction is the same demand, not more of
                // it; that is the point of counting keys rather than commits.
                if (_demand.TryAdd(key, pixels)) _demandPixels += pixels;
            }
            return entry.Location;
        }

        /// <summary>
        /// An entry about to be overwritten in the dictionary under the same
        /// key stops being live. Callers commit after a miss, so there is
        /// normally nothing there; the test hook can commit over a key.
        /// </summary>
        private void RetireReplaced(Key key)
        {
            if (_recent != null && _entries.TryGetValue(key, out var replaced)) replaced.Live = false;
        }

        /// <summary>
        /// True when tiles have been written but not yet uploaded. An atlas
        /// with no texture has nothing pending and never will, so the schedulers
        /// that ask this never reach a flush they cannot perform.
        /// </summary>
        public bool HasPendingUpload => IsUsable && _dirty;

        // ------------------------------------------------------------ allocation

        private static int ShelfHeightFor(int height)
        {
            foreach (var h in ShelfHeights)
                if (h >= height) return h;
            return height;
        }

        /// <summary>
        /// Finds room for a tile: free spans first, then a fresh shelf, then
        /// compaction, then LRU eviction. Only the last two cost anything the
        /// caller can notice, and both are the alternative to the old
        /// whole-layer recycle.
        /// </summary>
        private bool Allocate(int w, int h, out int layer, out int x, out int y, out int shelfIndex)
        {
            if (w > _textureSize || h > _textureSize)
            {
                layer = x = y = shelfIndex = 0;
                return false;
            }

            if (TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;

            // Space may exist but be scattered across half-used shelves.
            if (TryCompact(w, h) && TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;

            long evictStart = AtlasDiagnostics.Now;
            try
            {
                while (EvictForFit(w, h))
                {
                    if (AtlasDiagnostics.Enabled) AtlasDiagnostics.EvictCount++;
                    if (TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;
                    if (TryCompact(w, h) && TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;
                }
                return false;
            }
            finally
            {
                AtlasDiagnostics.Add(ref AtlasDiagnostics.EvictTicks, evictStart);
            }
        }

        /// <summary>
        /// Compacts, but only when it can plausibly help: if live tiles already
        /// fill the atlas, repacking them frees nothing and the answer is
        /// eviction. Once per flush cycle at most: a full atlas would
        /// otherwise repack on every single new glyph.
        /// </summary>
        private bool TryCompact(int w, int h)
        {
            if (!AutoCompact || _compactedSinceFlush) return false;
            long capacity = (long)_textureSize * _textureSize * _layerCount;
            // 0.85 leaves room for the shelf slack a perfect repack still keeps.
            if (_usedPixels + w * h > capacity * 0.85f) return false;
            Compact();
            return true;
        }

        private bool TryAlloc(int w, int h, out int layer, out int x, out int y, out int shelfIndex)
        {
            int wanted = ShelfHeightFor(h);

            for (int li = 0; li < _layerCount; li++)
            {
                var state = _layers[li];
                for (int si = 0; si < state.Shelves.Count; si++)
                {
                    var shelf = state.Shelves[si];
                    // An empty shelf can serve anything that fits in the rows it
                    // owns, and re-types itself to that bucket; a shelf in use
                    // keeps its bucket, or short tiles would strand the slack
                    // above them for as long as the shelf lives.
                    if (shelf.IsEmpty)
                    {
                        if (wanted > shelf.Capacity) continue;
                        shelf.Height = wanted;
                    }
                    else if (shelf.Height != wanted)
                    {
                        continue;
                    }

                    // A fragmented shelf can hold hundreds of slivers; when
                    // none of them can be wide enough there is no reason to
                    // look at them (FastShelfScan). The first that fits is
                    // still the first in the list.
                    int widest = 0;
                    bool scan = !FastShelfScan || shelf.FreeBound >= w;
                    for (int fi = 0; scan && fi < shelf.Free.Count; fi++)
                    {
                        var span = shelf.Free[fi];
                        if (span.Width < w)
                        {
                            if (span.Width > widest) widest = span.Width;
                            continue;
                        }
                        x = span.X;
                        y = shelf.Y;
                        layer = li;
                        shelfIndex = si;
                        if (span.Width == w) shelf.Free.RemoveAt(fi);
                        else shelf.Free[fi] = new Span { X = span.X + w, Width = span.Width - w };
                        shelf.LiveTiles++;
                        return true;
                    }
                    if (scan) shelf.FreeBound = widest;

                    if (shelf.X + w <= _textureSize)
                    {
                        x = shelf.X;
                        y = shelf.Y;
                        layer = li;
                        shelfIndex = si;
                        shelf.X += w;
                        shelf.LiveTiles++;
                        return true;
                    }
                }

                if (state.YCursor + wanted <= _textureSize)
                {
                    var shelf = new Shelf
                    {
                        Y = state.YCursor, Capacity = wanted, Height = wanted, X = w, LiveTiles = 1,
                    };
                    state.YCursor += wanted;
                    state.Shelves.Add(shelf);
                    x = 0;
                    y = shelf.Y;
                    layer = li;
                    shelfIndex = state.Shelves.Count - 1;
                    return true;
                }
            }

            layer = x = y = shelfIndex = 0;
            return false;
        }

        /// <summary>
        /// Drops one tile, chosen so the eviction actually helps a tile of
        /// this shape get in.
        ///
        /// Plain LRU is the wrong target here: shelf packing means a freed
        /// 40px tile does nothing for a 90px one, so evicting by age alone can
        /// walk through half the atlas before it frees anything usable. So:
        /// prefer the oldest tile on a shelf of the right height, and when no
        /// such shelf exists, work through the coldest shelf until it empties
        /// and can re-type itself. Tiles touched since the last upload are
        /// spared while anything colder remains: a single label's rebuild must
        /// not evict the tiles it just placed.
        /// </summary>
        private bool EvictForFit(int w, int h)
        {
            int wanted = ShelfHeightFor(h);

            if (_fastEviction)
            {
                var chosen = FastEvictionTarget(wanted);
                if (chosen == null) return false;
                Release(chosen);
                _evictions++;
                Version++;
                return true;
            }

            Entry target = null, recentFallback = null;
            foreach (var entry in _lru)
            {
                if (!entry.HasSlot) continue;
                if (_layers[entry.Layer].Shelves[entry.ShelfIndex].Height != wanted) continue;
                if (entry.LastUse > _flushStamp) { recentFallback ??= entry; continue; }
                target = entry;
                break;
            }

            if (target == null) target = OldestOnColdestShelf(wanted);
            target ??= recentFallback;
            if (target == null) return false;

            Release(target);
            _evictions++;
            Version++;
            return true;
        }

        /// <summary>
        /// The oldest tile on whichever shelf has gone unused the longest.
        /// Emptying one shelf frees a band the layer can re-type to any height,
        /// which is the only way a tile of a new size gets in once every shelf
        /// belongs to some other size.
        /// </summary>
        private Entry OldestOnColdestShelf(int wanted)
        {
            (int layer, int shelf) coldest = (-1, -1);
            long coldestUse = long.MaxValue;
            var newest = new Dictionary<(int, int), long>();

            foreach (var entry in _lru)
            {
                if (!entry.HasSlot) continue;
                var key = (entry.Layer, entry.ShelfIndex);
                newest[key] = entry.LastUse; // LRU order: the last write wins
            }
            foreach (var pair in newest)
            {
                // A shelf already the right height was covered by the first pass.
                if (_layers[pair.Key.Item1].Shelves[pair.Key.Item2].Height == wanted) continue;
                if (pair.Value >= coldestUse) continue;
                coldestUse = pair.Value;
                coldest = pair.Key;
            }
            if (coldest.layer < 0) return null;

            foreach (var entry in _lru)
                if (entry.HasSlot && entry.Layer == coldest.layer && entry.ShelfIndex == coldest.shelf)
                    return entry;
            return null;
        }

        private void Release(Entry entry)
        {
            entry.Live = false;
            _entries.Remove(entry.Key);
            if (entry.Node != null) _lru.Remove(entry.Node);
            entry.Node = null;
            if (_fastEviction) Unlink(entry);
            if (!entry.HasSlot) return;

            FreeSpan(_layers[entry.Layer], entry.ShelfIndex, entry.X, entry.Width);
            _usedPixels -= entry.Width * entry.Height;
            if (entry.Prewarmed)
            {
                _prewarmedPixels -= entry.Width * entry.Height;
                _prewarmedTiles--;
            }
            entry.HasSlot = false;
        }

        private void FreeSpan(LayerState state, int shelfIndex, int x, int width)
        {
            var shelf = state.Shelves[shelfIndex];
            shelf.LiveTiles--;

            if (x + width == shelf.X)
            {
                // Freed the last tile on the shelf: pull the cursor back, and
                // keep pulling over free spans that now sit at the end.
                shelf.X = x;
                bool merged = true;
                while (merged)
                {
                    merged = false;
                    for (int i = 0; i < shelf.Free.Count; i++)
                    {
                        if (shelf.Free[i].X + shelf.Free[i].Width != shelf.X) continue;
                        shelf.X = shelf.Free[i].X;
                        shelf.Free.RemoveAt(i);
                        merged = true;
                        break;
                    }
                }
            }
            else
            {
                var span = new Span { X = x, Width = width };
                // Coalesce with neighbours so a long-lived shelf does not
                // decay into unusable slivers.
                for (int i = shelf.Free.Count - 1; i >= 0; i--)
                {
                    var other = shelf.Free[i];
                    if (other.X + other.Width == span.X)
                    {
                        span = new Span { X = other.X, Width = other.Width + span.Width };
                        shelf.Free.RemoveAt(i);
                    }
                    else if (span.X + span.Width == other.X)
                    {
                        span = new Span { X = span.X, Width = span.Width + other.Width };
                        shelf.Free.RemoveAt(i);
                    }
                }
                shelf.Free.Add(span);
                if (span.Width > shelf.FreeBound) shelf.FreeBound = span.Width;
            }

            if (!shelf.IsEmpty) return;

            shelf.Free.Clear();
            shelf.FreeBound = 0;
            shelf.X = 0;
            shelf.Height = shelf.Capacity; // free to serve any bucket again
            // An empty shelf at the top of the layer gives its rows back. Only
            // trailing shelves are removed, so no live entry's shelf index moves.
            for (int i = state.Shelves.Count - 1; i >= 0; i--)
            {
                if (!state.Shelves[i].IsEmpty) break;
                if (state.Shelves[i].Y + state.Shelves[i].Capacity != state.YCursor) break;
                state.YCursor = state.Shelves[i].Y;
                state.Shelves.RemoveAt(i);
            }
        }

        /// <summary>
        /// Repacks every live tile from scratch, tallest first. Fragmentation
        /// is what makes a dynamic atlas fail well below its real capacity;
        /// this is the alternative to throwing away work that is still in use.
        /// Tiles move, so <see cref="Version"/> changes and meshes rebuild.
        /// </summary>
        public void Compact()
        {
            // It reads every live tile's texels, and a placed tile has none yet.
            CompletePendingBakes();
            if (!IsUsable || _lru.Count == 0) return;
            long compactStart = AtlasDiagnostics.Now;
            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.CompactCount++;

            var live = new List<Entry>(_lru.Count);
            foreach (var entry in _lru)
                if (entry.HasSlot) live.Add(entry);
            if (live.Count == 0) return;

            // Snapshot first: repacking writes over rows that other tiles are
            // still being read from.
            long total = 0;
            foreach (var entry in live) total += entry.Width * entry.Height * _bytesPerTexel;
            var pixels = new byte[total];
            long cursor = 0;
            var offsets = new long[live.Count];
            for (int i = 0; i < live.Count; i++)
            {
                var entry = live[i];
                offsets[i] = cursor;
                var src = Texture.GetPixelData<byte>(0, entry.Layer);
                for (int row = 0; row < entry.Height; row++)
                {
                    NativeArray<byte>.Copy(src, ((entry.Y + row) * _textureSize + entry.X) * _bytesPerTexel,
                        pixels, (int)cursor + row * entry.Width * _bytesPerTexel,
                        entry.Width * _bytesPerTexel);
                }
                cursor += entry.Width * entry.Height * _bytesPerTexel;
            }

            foreach (var layer in _layers)
            {
                layer.Shelves.Clear();
                layer.YCursor = 0;
            }
            for (int layer = 0; layer < _layerCount; layer++) ClearLayerPixels(layer);
            // Recounted rather than adjusted, because the loop below may drop a
            // tile it cannot re-place, and a provenance count that drifts is
            // worse than none: the pie would stop summing to the occupancy.
            _usedPixels = 0;
            _prewarmedPixels = 0;
            _prewarmedTiles = 0;

            // Tallest first: shelf packing wastes the least when a shelf's
            // height is set by the tallest thing that will ever sit on it.
            var order = new int[live.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int byHeight = live[b].Height.CompareTo(live[a].Height);
                return byHeight != 0 ? byHeight : live[b].Width.CompareTo(live[a].Width);
            });

            foreach (int i in order)
            {
                var entry = live[i];
                if (!TryAlloc(entry.Width, entry.Height, out int layer, out int x, out int y, out int shelf))
                {
                    // Cannot happen for tiles that already fitted, but dropping
                    // is still better than a corrupt entry.
                    entry.Live = false;
                    _entries.Remove(entry.Key);
                    if (entry.Node != null) _lru.Remove(entry.Node);
                    entry.Node = null;
                    if (_fastEviction) Unlink(entry);
                    entry.HasSlot = false;
                    continue;
                }

                var dst = Texture.GetPixelData<byte>(0, layer);
                for (int row = 0; row < entry.Height; row++)
                {
                    NativeArray<byte>.Copy(pixels, (int)offsets[i] + row * entry.Width * _bytesPerTexel,
                        dst, ((y + row) * _textureSize + x) * _bytesPerTexel,
                        entry.Width * _bytesPerTexel);
                }

                entry.Layer = layer;
                entry.X = x;
                entry.Y = y;
                entry.ShelfIndex = shelf;
                entry.Location = new GlyphLocation(true, entry.Location.OriginUnits, entry.Location.SizeUnits,
                    UvRectOf(x, y, entry.Width, entry.Height), layer);
                _usedPixels += entry.Width * entry.Height;
                if (entry.Prewarmed)
                {
                    _prewarmedPixels += entry.Width * entry.Height;
                    _prewarmedTiles++;
                }
            }

            if (_fastEviction)
            {
                // The shelves are new objects; their lists are rebuilt in the
                // global order. Heights did not change, so neither did buckets.
                foreach (var entry in _lru)
                {
                    entry.OnShelf = null;
                    entry.ShelfPrev = entry.ShelfNext = null;
                }
                foreach (var entry in _lru)
                    if (entry.HasSlot) LinkShelf(entry, _layers[entry.Layer].Shelves[entry.ShelfIndex]);
            }

            _compactions++;
            _compactedSinceFlush = true;
            Version++;
            MarkFullUpload();
            AtlasDiagnostics.Add(ref AtlasDiagnostics.CompactTicks, compactStart);
        }

        private Rect UvRectOf(int x, int y, int w, int h) => new Rect(
            x / (float)_textureSize, y / (float)_textureSize,
            w / (float)_textureSize, h / (float)_textureSize);

        private void ClearLayerPixels(int layer)
        {
            var pixels = Texture.GetPixelData<byte>(0, layer);
            var zeros = new NativeArray<byte>(pixels.Length, Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            pixels.CopyFrom(zeros);
            zeros.Dispose();
        }

        private GlyphLocation WriteSlot(int layer, int x, int y, RasterizedGlyph raster)
        {
            long copyStart = AtlasDiagnostics.Now;
            CopyIntoSlot(layer, x, y, raster);
            AtlasDiagnostics.Add(ref AtlasDiagnostics.CopyTicks, copyStart);
            MarkTileDirty(layer, x, y, raster.Width, raster.Height);
            return LocationOf(layer, x, y, raster);
        }

        /// <summary>
        /// Off copies a tile into the atlas a row at a time through
        /// <c>NativeArray.Copy</c>, which checks its arguments and the safety
        /// handle on every row; a frame of two hundred new tiles made ~6,000
        /// of those calls. On copies the same rows with a plain memcpy from
        /// the pinned tile into the layer. Same bytes, same places.
        /// </summary>
        public static bool RawTileCopy = true;

        private unsafe void CopyIntoSlot(int layer, int x, int y, RasterizedGlyph raster)
        {
            if (RawTileCopy)
            {
                var layerPixels = Texture.GetPixelData<byte>(0, layer);
                int bytes = raster.Width * _bytesPerTexel;
                int stride = _textureSize * _bytesPerTexel;
                if (raster.PixelStart < 0 || raster.PixelStart + bytes * raster.Height > raster.Pixels.Length ||
                    x + raster.Width > _textureSize || y + raster.Height > _textureSize)
                    throw new ArgumentOutOfRangeException(nameof(raster), "tile does not fit its slot");
                byte* target = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                    .GetUnsafePtr(layerPixels) + ((long)y * _textureSize + x) * _bytesPerTexel;
                fixed (byte* source = raster.Pixels)
                {
                    byte* from = source + raster.PixelStart;
                    for (int row = 0; row < raster.Height; row++)
                        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(
                            target + (long)row * stride, from + (long)row * bytes, bytes);
                }
                return;
            }

            var dst = Texture.GetPixelData<byte>(0, layer);
            // Row-wise memcpy: a per-byte loop here costs more than the GPU
            // upload it feeds.
            int rowBytes = raster.Width * _bytesPerTexel;
            for (int row = 0; row < raster.Height; row++)
            {
                NativeArray<byte>.Copy(raster.Pixels, raster.PixelStart + row * rowBytes,
                    dst, ((y + row) * _textureSize + x) * _bytesPerTexel, rowBytes);
            }
        }

        private GlyphLocation LocationOf(int layer, int x, int y, RasterizedGlyph raster)
        {
            var sizeUnits = new Vector2(
                raster.Width * raster.UnitsPerPixel, raster.Height * raster.UnitsPerPixel);
            return new GlyphLocation(true, raster.OriginUnits, sizeUnits,
                UvRectOf(x, y, raster.Width, raster.Height), layer);
        }

        // ---------------------------------------------------------------- upload

        private struct DirtyTile
        {
            public int Layer, X, Y, Width, Height;
        }

        // Past this many tiles the per-tile copies cost more than one full
        // upload of a 1024^2 layer; measured on Metal, see PerformanceTests.
        private const int MaxPartialTiles = 64;

        private readonly List<DirtyTile> _pendingTiles = new List<DirtyTile>();
        private bool _fullUpload;
        private List<StagingSlot>[] _staging;

        private void MarkTileDirty(int layer, int x, int y, int w, int h)
        {
            _dirty = true;
            if (_fullUpload) return;
            if (_pendingTiles.Count >= MaxPartialTiles)
            {
                MarkFullUpload();
                return;
            }
            _pendingTiles.Add(new DirtyTile { Layer = layer, X = x, Y = y, Width = w, Height = h });
        }

        private void MarkFullUpload()
        {
            _dirty = true;
            _fullUpload = true;
            _pendingTiles.Clear();
        }

        /// <summary>
        /// True when this device can upload single tiles instead of the whole
        /// array. WebGL 1 and any device without cross-type copies fall back to
        /// a full <c>Apply</c>.
        /// </summary>
        public static bool SupportsPartialUpload
        {
            get
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    return false;
                const UnityEngine.Rendering.CopyTextureSupport needed =
                    UnityEngine.Rendering.CopyTextureSupport.Basic |
                    UnityEngine.Rendering.CopyTextureSupport.DifferentTypes;
                return (SystemInfo.copyTextureSupport & needed) == needed;
            }
        }

        /// <summary>
        /// Uploads pending tile writes to the GPU. A handful of new tiles goes
        /// up as a handful of small copies; a compaction, a cleared layer, or a
        /// flood of new glyphs goes up as one full <c>Apply</c>. Call this once
        /// per frame after every rebuild that ran, not once per label.
        /// Frontends should go through their own scheduler (see
        /// OneText.UGUI.AtlasFlushScheduler).
        /// </summary>
        public void Flush()
        {
            // Placed tiles get their fields before anything goes up.
            CompletePendingBakes();
            if (!IsUsable || !_dirty) return;
            long uploadStart = AtlasDiagnostics.Now;
            if (AtlasDiagnostics.Enabled)
            {
                AtlasDiagnostics.UploadCount++;
                AtlasDiagnostics.UploadedTiles += _pendingTiles.Count;
            }

            if (!_fullUpload && _pendingTiles.Count > 0 && SupportsPartialUpload && UploadPendingTiles())
            {
                _partialUploads++;
            }
            else
            {
                Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                _fullUploads++;
            }

            AtlasDiagnostics.Add(ref AtlasDiagnostics.UploadTicks, uploadStart);
            _pendingTiles.Clear();
            _fullUpload = false;
            _dirty = false;
            _compactedSinceFlush = false;
            _flushStamp = _useStamp;
        }

        /// <summary>
        /// Packs every dirty tile into one staging texture, uploads that once,
        /// and blits the tiles into place on the GPU. False when they do not
        /// fit any staging texture, and the caller uploads the whole array.
        ///
        /// The obvious version, one staging texture and one <c>Apply</c> per
        /// tile, measured slower than uploading the whole 4 MB array, because
        /// the cost of an <c>Apply</c> is dominated by its fixed overhead, not
        /// by its bytes. One upload of the packed tiles plus N GPU-side copies
        /// keeps the byte count small and the overhead paid once.
        ///
        /// Once is also the only safe number. An <c>Apply</c> is not ordered
        /// against the <c>CopyTexture</c> calls queued before it: on Metal the
        /// copies of one frame ran after a later <c>Apply</c> of the same
        /// staging texture had replaced its pixels, so a flush that refilled the
        /// staging texture from (0,0) when it ran out of room stamped its second
        /// group of tiles over its first ("Yoji" drawn as "iYoji"). So every
        /// tile is packed before anything is written, the edge grows until they
        /// all fit, and a staging texture already uploaded this frame is only
        /// ever written below the rows that earlier flush used (see
        /// <see cref="StagingSlotFor"/>), never over them.
        /// </summary>
        private bool UploadPendingTiles()
        {
            int frame = Time.frameCount;
            for (int edge = StagingEdgeFor(_pendingTiles); edge <= _textureSize; edge *= 2)
            {
                if (!PackPending(edge, out int height)) continue;
                var slot = StagingSlotFor(edge, height, frame);
                if (slot == null) continue;

                // Rows an earlier flush of this frame uploaded stay as they are:
                // its copies may not have run yet, and they read them.
                int top = slot.Frame == frame ? slot.Rows : 0;
                var staging = slot.Texture;
                var dst = staging.GetPixelData<byte>(0);
                for (int i = 0; i < _pendingTiles.Count; i++)
                {
                    var tile = _pendingTiles[i];
                    var at = _packedAt[i];
                    var src = Texture.GetPixelData<byte>(0, tile.Layer);
                    for (int row = 0; row < tile.Height; row++)
                    {
                        NativeArray<byte>.Copy(src, ((tile.Y + row) * _textureSize + tile.X) * _bytesPerTexel,
                            dst, ((top + at.y + row) * edge + at.x) * _bytesPerTexel,
                            tile.Width * _bytesPerTexel);
                    }
                }

                staging.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                for (int i = 0; i < _pendingTiles.Count; i++)
                {
                    var tile = _pendingTiles[i];
                    var at = _packedAt[i];
                    Graphics.CopyTexture(staging, 0, 0, at.x, top + at.y, tile.Width, tile.Height,
                        Texture, tile.Layer, 0, tile.X, tile.Y);
                }
                slot.Frame = frame;
                slot.Rows = top + height;
                _packedAt.Clear();
                return true;
            }
            _packedAt.Clear();
            return false;
        }

        private readonly List<Vector2Int> _packedAt = new List<Vector2Int>();

        /// <summary>
        /// Row-packs the dirty tiles into an edge-wide square from its top row,
        /// filling <see cref="_packedAt"/>; false when they do not all fit.
        /// <paramref name="height"/> is the rows the packing used.
        /// </summary>
        private bool PackPending(int edge, out int height)
        {
            _packedAt.Clear();
            int cursorX = 0, cursorY = 0, rowHeight = 0;
            height = 0;
            for (int i = 0; i < _pendingTiles.Count; i++)
            {
                var tile = _pendingTiles[i];
                if (tile.Width > edge) return false;
                if (cursorX + tile.Width > edge)
                {
                    cursorX = 0;
                    cursorY += rowHeight;
                    rowHeight = 0;
                }
                if (cursorY + tile.Height > edge) return false;
                _packedAt.Add(new Vector2Int(cursorX, cursorY));
                cursorX += tile.Width;
                rowHeight = Mathf.Max(rowHeight, tile.Height);
            }
            height = cursorY + rowHeight;
            return true;
        }

        /// <summary>Smallest power-of-two staging texture the dirty tiles pack into.</summary>
        private int StagingEdgeFor(List<DirtyTile> tiles)
        {
            long area = 0;
            int widest = 1, tallest = 1;
            foreach (var tile in tiles)
            {
                area += tile.Width * tile.Height;
                widest = Mathf.Max(widest, tile.Width);
                tallest = Mathf.Max(tallest, tile.Height);
            }
            // 1.4x for the slack row packing leaves behind.
            int edge = Mathf.CeilToInt(Mathf.Sqrt(area) * 1.4f);
            edge = Mathf.Max(edge, Mathf.Max(widest, tallest));
            return Mathf.Clamp(Mathf.NextPowerOfTwo(edge), 32, _textureSize);
        }

        /// <summary>One staging texture, and the rows of it this frame has uploaded.</summary>
        private sealed class StagingSlot
        {
            public Texture2D Texture;
            public int Frame = int.MinValue;
            public int Rows;
        }

        // Past this many textures of one edge a frame's further flushes move up
        // an edge (and past the largest, to a full upload) instead of adding
        // more. Two covers the frame that flushes from both the canvas pass and
        // a world-space mesh; edit mode flushing per label is the case that
        // would otherwise grow without bound.
        private const int StagingSlotsPerEdge = 2;

        /// <summary>
        /// A staging texture of <paramref name="edge"/> with
        /// <paramref name="height"/> rows nobody has uploaded this frame, or
        /// null when every one this edge may have is spoken for.
        ///
        /// A frame's uploads and its copies are not ordered against each other,
        /// so a staging texture is written over only once a frame boundary has
        /// sent its copies on their way. Until then a later flush packs below
        /// the rows already in it: <c>Apply</c> uploads the whole texture, and
        /// the rows it did not touch go up holding what they held. The common
        /// frame flushes once and always lands on the first texture, at row 0.
        /// </summary>
        private StagingSlot StagingSlotFor(int edge, int height, int frame)
        {
            int index = 0;
            while ((1 << index) < edge) index++;
            _staging ??= new List<StagingSlot>[14];
            var slots = _staging[index] ??= new List<StagingSlot>(StagingSlotsPerEdge);

            foreach (var slot in slots)
            {
                if (slot.Frame == frame && slot.Rows + height > edge) continue;
                if (slot.Texture == null)
                {
                    // Destroyed under us (a play session boundary with Domain
                    // Reload off): a new one holds nothing an earlier flush
                    // could still be reading.
                    slot.Texture = CreateStaging(edge);
                    slot.Frame = int.MinValue;
                }
                return slot;
            }
            if (slots.Count >= StagingSlotsPerEdge) return null;

            var created = new StagingSlot { Texture = CreateStaging(edge) };
            slots.Add(created);
            return created;
        }

        // Same format as the array it feeds: Graphics.CopyTexture requires it,
        // and a mismatch is a silent no-op on some devices rather than an error.
        private Texture2D CreateStaging(int edge) => new Texture2D(edge, edge,
            Precise ? TextureFormat.RGBA32 : TextureFormat.R8, false, true)
        {
            name = $"OneText Atlas Staging {edge}",
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
        };

        public void Dispose()
        {
            if (Texture != null)
                UnityEngine.Object.DestroyImmediate(Texture);
            if (_staging != null)
            {
                foreach (var slots in _staging)
                {
                    if (slots == null) continue;
                    foreach (var slot in slots)
                        if (slot.Texture != null) UnityEngine.Object.DestroyImmediate(slot.Texture);
                }
                _staging = null;
            }
            // A batch still baking writes into buffers it does not own once we
            // are gone; collect it, then drop everything.
            if (_flightBakes.Count > 0) GlyphRasterizer.EndBatch();
            _flightBakes.Clear();
            _flightRequests.Clear();
            _flightResults.Clear();
            _pendingBakes.Clear();
            _pendingRequests.Clear();
            _pendingTexels = 0;
            foreach (var entry in _entries.Values) entry.Live = false;
            if (_recent != null) Array.Clear(_recent, 0, _recent.Length);
            _entries.Clear();
            _lru.Clear();
            _buckets.Clear();
        }
    }
}
