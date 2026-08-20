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
    public sealed class GlyphAtlas : IDisposable
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

            public bool Equals(Key o) => _font == o._font && _id == o._id &&
                _ppem == o._ppem && _generation == o._generation && _outline == o._outline &&
                _raster == o._raster && _precise == o._precise;

            public override int GetHashCode() =>
                HashCode.Combine(_font, _id, _ppem, _generation, _outline, _raster, _precise);
        }

        private sealed class Entry
        {
            public Key Key;
            public GlyphLocation Location;
            public bool HasSlot;
            public bool Prewarmed;
            public int Layer, X, Y, Width, Height, ShelfIndex;
            public long LastUse;
            public LinkedListNode<Entry> Node; // position in the LRU list
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

            public bool IsEmpty => LiveTiles == 0;
        }

        private sealed class LayerState
        {
            public readonly List<Shelf> Shelves = new List<Shelf>();
            public int YCursor;
        }

        private readonly Dictionary<Key, Entry> _entries = new Dictionary<Key, Entry>();
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

        public GlyphAtlas() : this(GlyphAtlasSettings.Default) { }

        /// <param name="precise">
        /// Hold multi-channel fields instead of single-channel ones. Four bytes
        /// a texel rather than one, which is why it is the caller's decision and
        /// not the atlas's.
        /// </param>
        public GlyphAtlas(GlyphAtlasSettings settings, bool precise = false)
        {
            settings = settings.Validated();
            _textureSize = settings.TextureSize;
            _layerCount = settings.LayerCount;
            _layers = new LayerState[_layerCount];
            Precise = precise;
            _bytesPerTexel = precise ? 4 : 1;

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

            for (int layer = 0; layer < _layerCount; layer++)
            {
                _layers[layer] = new LayerState();
                ClearLayerPixels(layer);
            }
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
            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            var key = new Key(font, glyphId, ppem, Precise);
            if (TryTouch(key, out var hit)) return hit;

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
            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            var key = new Key(font, hash, ppem, Precise);
            if (TryTouch(key, out var hit)) return hit;

            long outlineStart = AtlasDiagnostics.Now;
            s_merged.Clear();
            s_mergedGroups.Clear();
            _pooledLines = 0;
            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            BuildClusterContours(font, positioned, start, count, s_merged, s_mergedGroups, pixelsPerUnit);

            AtlasDiagnostics.Add(ref AtlasDiagnostics.OutlineTicks, outlineStart);
            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OutlineCount += count;

            var raster = GlyphRasterizer.RasterizeContours(s_merged, s_mergedGroups, pixelsPerUnit, Precise);
            return Commit(key, raster, ppem);
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
            List<GlyphLocation> locations)
        {
            locations?.Clear();
            if (clusters == null || clusters.Count == 0) return locations != null;
            int ppem = QuantizePixelsPerEm(pixelsPerEm);
            bool collecting = locations != null;

            _batchRequests.Clear();
            _batchKeys.Clear();
            _batchPending.Clear();
            _pooledUsed = 0;
            _pooledLines = 0;

            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            int glyphsExtracted = 0;
            foreach (var cluster in clusters)
            {
                var key = new Key(font, cluster.Hash, ppem, Precise);
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

                var contours = RentContours();
                var groups = RentGroups();
                // Timed around the extraction alone, not around the loop. A warm
                // run walks every cluster here and extracts none, and charging
                // that walk to "outline" reported 24 us a frame of outline work
                // against three glyphs actually extracted, a number that reads
                // as a hot rasterizer and is really the cache saying no. The
                // walk is lookup, and the caller already counts it as lookup.
                long outlineStart = AtlasDiagnostics.Now;
                BuildClusterContours(font, positioned, cluster.Start, cluster.Count, contours, groups, pixelsPerUnit);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.OutlineTicks, outlineStart);
                glyphsExtracted += cluster.Count;

                _batchRequests.Add(new RasterizeRequest
                {
                    Contours = contours,
                    Groups = groups,
                    PixelsPerUnit = pixelsPerUnit,
                });
                _batchKeys.Add(key);
            }

            if (AtlasDiagnostics.Enabled) AtlasDiagnostics.OutlineCount += glyphsExtracted;
            if (_batchRequests.Count == 0) return collecting;

            GlyphRasterizer.RasterizeBatch(_batchRequests, _batchResults, Precise);
            for (int i = 0; i < _batchKeys.Count && i < _batchResults.Count; i++)
                Commit(_batchKeys[i], _batchResults[i], ppem);
            locations?.Clear();
            return false;
        }

        private readonly List<RasterizeRequest> _batchRequests = new List<RasterizeRequest>();
        private readonly List<Key> _batchKeys = new List<Key>();
        private readonly HashSet<Key> _batchPending = new HashSet<Key>();
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
            if (!_entries.TryGetValue(key, out var hit))
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
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
        }

        private GlyphLocation Commit(Key key, RasterizedGlyph raster, int ppem)
        {
            // Empty content (spaces, control glyphs) is cached without a tile.
            if (raster.IsEmpty)
            {
                var emptyLocation = new GlyphLocation(false, Vector2.zero, Vector2.zero, default, 0);
                _entries[key] = new Entry { Key = key, Location = emptyLocation };
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
            entry.Location = WriteSlot(layer, x, y, raster);
            entry.Node = _lru.AddLast(entry);
            entry.LastUse = ++_useStamp;
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

        /// <summary>True when tiles have been written but not yet uploaded.</summary>
        public bool HasPendingUpload => _dirty;

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

            while (EvictForFit(w, h))
            {
                if (TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;
                if (TryCompact(w, h) && TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;
            }
            return false;
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

                    for (int fi = 0; fi < shelf.Free.Count; fi++)
                    {
                        var span = shelf.Free[fi];
                        if (span.Width < w) continue;
                        x = span.X;
                        y = shelf.Y;
                        layer = li;
                        shelfIndex = si;
                        if (span.Width == w) shelf.Free.RemoveAt(fi);
                        else shelf.Free[fi] = new Span { X = span.X + w, Width = span.Width - w };
                        shelf.LiveTiles++;
                        return true;
                    }

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
            _entries.Remove(entry.Key);
            if (entry.Node != null) _lru.Remove(entry.Node);
            entry.Node = null;
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
            }

            if (!shelf.IsEmpty) return;

            shelf.Free.Clear();
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
            if (_lru.Count == 0) return;

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
                    _entries.Remove(entry.Key);
                    if (entry.Node != null) _lru.Remove(entry.Node);
                    entry.Node = null;
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

            _compactions++;
            _compactedSinceFlush = true;
            Version++;
            MarkFullUpload();
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
            var dst = Texture.GetPixelData<byte>(0, layer);
            // Row-wise memcpy: a per-byte loop here costs more than the GPU
            // upload it feeds.
            int rowBytes = raster.Width * _bytesPerTexel;
            for (int row = 0; row < raster.Height; row++)
            {
                NativeArray<byte>.Copy(raster.Pixels, raster.PixelStart + row * rowBytes,
                    dst, ((y + row) * _textureSize + x) * _bytesPerTexel, rowBytes);
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.CopyTicks, copyStart);
            MarkTileDirty(layer, x, y, raster.Width, raster.Height);

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
        private Texture2D[] _staging;

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
            if (!_dirty) return;
            long uploadStart = AtlasDiagnostics.Now;
            if (AtlasDiagnostics.Enabled)
            {
                AtlasDiagnostics.UploadCount++;
                AtlasDiagnostics.UploadedTiles += _pendingTiles.Count;
            }

            if (!_fullUpload && _pendingTiles.Count > 0 && SupportsPartialUpload)
            {
                UploadPendingTiles();
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
        /// and blits the tiles into place on the GPU.
        ///
        /// The obvious version, one staging texture and one <c>Apply</c> per
        /// tile, measured slower than uploading the whole 4 MB array, because
        /// the cost of an <c>Apply</c> is dominated by its fixed overhead, not
        /// by its bytes. One upload of the packed tiles plus N GPU-side copies
        /// keeps the byte count small and the overhead paid once.
        /// </summary>
        private void UploadPendingTiles()
        {
            int edge = StagingEdgeFor(_pendingTiles);
            var staging = GetStaging(edge);
            var dst = staging.GetPixelData<byte>(0);

            int cursorX = 0, cursorY = 0, rowHeight = 0, packed = 0;
            for (int i = 0; i < _pendingTiles.Count; i++)
            {
                var tile = _pendingTiles[i];
                if (cursorX + tile.Width > edge)
                {
                    cursorX = 0;
                    cursorY += rowHeight;
                    rowHeight = 0;
                }
                if (cursorY + tile.Height > edge)
                {
                    // Staging is full: flush what is packed and start over.
                    staging.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                    CopyPacked(staging, i - packed, packed, edge);
                    cursorX = cursorY = rowHeight = packed = 0;
                }

                var src = Texture.GetPixelData<byte>(0, tile.Layer);
                for (int row = 0; row < tile.Height; row++)
                {
                    NativeArray<byte>.Copy(src, ((tile.Y + row) * _textureSize + tile.X) * _bytesPerTexel,
                        dst, ((cursorY + row) * edge + cursorX) * _bytesPerTexel,
                        tile.Width * _bytesPerTexel);
                }

                _packedAt.Add(new Vector2Int(cursorX, cursorY));
                cursorX += tile.Width;
                rowHeight = Mathf.Max(rowHeight, tile.Height);
                packed++;
            }

            staging.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            CopyPacked(staging, _pendingTiles.Count - packed, packed, edge);
            _packedAt.Clear();
        }

        private readonly List<Vector2Int> _packedAt = new List<Vector2Int>();

        private void CopyPacked(Texture2D staging, int first, int count, int edge)
        {
            for (int i = first; i < first + count; i++)
            {
                var tile = _pendingTiles[i];
                var at = _packedAt[i];
                Graphics.CopyTexture(staging, 0, 0, at.x, at.y, tile.Width, tile.Height,
                    Texture, tile.Layer, 0, tile.X, tile.Y);
            }
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

        private Texture2D GetStaging(int edge)
        {
            int index = 0;
            while ((1 << index) < edge) index++;
            _staging ??= new Texture2D[14];
            if (_staging[index] == null)
            {
                // Same format as the array it feeds: Graphics.CopyTexture
                // requires it, and a mismatch is a silent no-op on some devices
                // rather than an error.
                _staging[index] = new Texture2D(1 << index, 1 << index,
                    Precise ? TextureFormat.RGBA32 : TextureFormat.R8, false, true)
                {
                    name = $"OneText Atlas Staging {1 << index}",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                };
            }
            return _staging[index];
        }

        public void Dispose()
        {
            if (Texture != null)
                UnityEngine.Object.DestroyImmediate(Texture);
            if (_staging != null)
            {
                foreach (var staging in _staging)
                    if (staging != null) UnityEngine.Object.DestroyImmediate(staging);
                _staging = null;
            }
            _entries.Clear();
            _lru.Clear();
        }
    }
}
