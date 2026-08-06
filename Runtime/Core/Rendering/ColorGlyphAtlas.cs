using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The RGBA companion to <see cref="GlyphAtlas"/>: colour emoji and inline
    /// sprites, in a second <c>Texture2DArray</c> sampled by the same shader
    /// and the same material.
    ///
    /// It has to be a second array rather than a corner of the first for two
    /// reasons, and both are absolute. A <c>Texture2DArray</c> has one format
    /// for every slice, so colour cannot live in an R8 array; and the SDF
    /// coverage maths would binarize a colour image into a silhouette. What it
    /// is <em>not</em> is a second draw call: the path flag rides in the unused
    /// <c>w</c> of the existing <c>vmax</c> vertex channel, so a line of text
    /// with emoji in it still batches as one.
    ///
    /// A submesh only becomes necessary if sprites ever need a different blend
    /// or stencil state, which is per-material and cannot be switched per
    /// fragment.
    ///
    /// Packing is the simple half of the SDF atlas: shelves and LRU eviction,
    /// no compaction. Colour tiles are a few hundred at most (an emoji set, a
    /// sprite sheet) where the SDF atlas has to survive a CJK chat stream, so
    /// the fragmentation the compactor exists to fix does not arise. If that
    /// stops being true it is the same algorithm to lift across.
    /// </summary>
    public sealed class ColorGlyphAtlas : IDisposable
    {
        private readonly int _textureSize;
        private readonly int _layerCount;

        public Texture2DArray Texture { get; }

        /// <summary>False once the backing texture has been destroyed.</summary>
        public bool IsUsable => Texture != null;

        /// <summary>Bumped when a tile is evicted, so baked UVs can be rebuilt.</summary>
        public int Version { get; private set; }

        public ColorGlyphAtlas(int textureSize = 1024, int layerCount = 1)
        {
            _textureSize = Mathf.NextPowerOfTwo(Mathf.Clamp(textureSize, 256, 4096));
            _layerCount = Mathf.Clamp(layerCount, 1, 8);

            Texture = new Texture2DArray(_textureSize, _textureSize, _layerCount,
                TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "OneText Color Atlas",
                // Same reason as the SDF atlas: a managed reference outliving
                // the play session must not be left holding a destroyed texture.
                hideFlags = HideFlags.HideAndDontSave,
            };

            _layers = new LayerState[_layerCount];
            for (int i = 0; i < _layerCount; i++)
            {
                _layers[i] = new LayerState();
                Clear(i);
            }
            Texture.Apply(false, false);
        }

        // ------------------------------------------------------------ packing

        private sealed class Shelf
        {
            /// <summary>Rows this shelf owns. Fixed when the shelf is created.</summary>
            public int Y, Capacity;

            /// <summary>Height currently served, and the fill cursor.</summary>
            public int Height, X;

            public int LiveTiles;
        }

        private sealed class LayerState
        {
            public readonly List<Shelf> Shelves = new List<Shelf>();
            public int YCursor;
        }

        private sealed class Entry
        {
            public long Key;
            public int Layer, X, Y, Width, Height, ShelfIndex;
            public ColorLocation Location;
            public LinkedListNode<Entry> Node;
        }

        private readonly LayerState[] _layers;
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private bool _dirty;
        private int _evictions, _drops;

        /// <summary>Where a colour tile lives, and the quad that draws it.</summary>
        public readonly struct ColorLocation
        {
            public readonly bool HasPixels;
            public readonly Vector2 OriginUnits;
            public readonly Vector2 SizeUnits;
            public readonly Rect UvRect;
            public readonly int Layer;

            internal ColorLocation(bool hasPixels, Vector2 originUnits, Vector2 sizeUnits,
                Rect uvRect, int layer)
            {
                HasPixels = hasPixels;
                OriginUnits = originUnits;
                SizeUnits = sizeUnits;
                UvRect = uvRect;
                Layer = layer;
            }
        }

        /// <summary>What the atlas holds, for budgeting and tests.</summary>
        public struct Stats
        {
            public int TileCount;
            public int Evictions;
            public int Drops;
            public long MemoryBytes;
        }

        public Stats GetStats() => new Stats
        {
            TileCount = _lru.Count,
            Evictions = _evictions,
            Drops = _drops,
            MemoryBytes = (long)_textureSize * _textureSize * _layerCount * 4,
        };

        /// <summary>True if this key is already resident.</summary>
        public bool Contains(long key) => _entries.ContainsKey(key);

        /// <summary>
        /// Looks a tile up, or writes one. <paramref name="key"/> identifies the
        /// content: the caller mixes font, glyph and size into it, exactly as
        /// the SDF atlas does.
        /// </summary>
        public ColorLocation GetOrAdd(long key, in ColorGlyph glyph)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                Touch(hit);
                return hit.Location;
            }
            if (glyph.IsEmpty) return default;

            if (!Allocate(glyph.Width, glyph.Height, out int layer, out int x, out int y, out int shelf))
            {
                // Not cached as a failure, for the same reason the SDF atlas
                // does not: a crowded frame must not blank a glyph for the rest
                // of the session.
                _drops++;
                return default;
            }

            Write(layer, x, y, glyph);

            var entry = new Entry
            {
                Key = key,
                Layer = layer, X = x, Y = y,
                Width = glyph.Width, Height = glyph.Height,
                ShelfIndex = shelf,
                Location = new ColorLocation(true, glyph.OriginUnits,
                    new Vector2(glyph.Width * glyph.UnitsPerPixel, glyph.Height * glyph.UnitsPerPixel),
                    UvRectOf(x, y, glyph.Width, glyph.Height), layer),
            };
            entry.Node = _lru.AddLast(entry);
            _entries[key] = entry;
            return entry.Location;
        }

        private void Touch(Entry entry)
        {
            if (entry.Node == null) return;
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
        }

        private bool Allocate(int w, int h, out int layer, out int x, out int y, out int shelfIndex)
        {
            layer = x = y = shelfIndex = 0;
            if (w > _textureSize || h > _textureSize) return false;

            while (true)
            {
                if (TryAlloc(w, h, out layer, out x, out y, out shelfIndex)) return true;
                if (!EvictOne()) return false;
            }
        }

        private bool TryAlloc(int w, int h, out int layer, out int x, out int y, out int shelfIndex)
        {
            for (int li = 0; li < _layerCount; li++)
            {
                var state = _layers[li];
                for (int si = 0; si < state.Shelves.Count; si++)
                {
                    var shelf = state.Shelves[si];
                    // An empty shelf may re-type itself, but only within the
                    // rows it owns. Growing past them writes a tall tile into
                    // the next shelf's pixels while that shelf's entries still
                    // resolve to their old UVs: silent corruption of live
                    // tiles, and invisible until someone looks at the atlas.
                    if (shelf.LiveTiles == 0 && h <= shelf.Capacity) shelf.Height = h;
                    if (h > shelf.Height || shelf.X + w > _textureSize) continue;
                    x = shelf.X;
                    y = shelf.Y;
                    layer = li;
                    shelfIndex = si;
                    shelf.X += w;
                    shelf.LiveTiles++;
                    return true;
                }

                if (state.YCursor + h <= _textureSize)
                {
                    var shelf = new Shelf
                    {
                        Y = state.YCursor, Capacity = h, Height = h, X = w, LiveTiles = 1,
                    };
                    state.YCursor += h;
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

        private bool EvictOne()
        {
            var node = _lru.First;
            if (node == null) return false;
            var entry = node.Value;

            _entries.Remove(entry.Key);
            _lru.Remove(node);
            entry.Node = null;

            // Wipe the tile rather than leave it for a bilinear tap to find.
            var data = Texture.GetPixelData<Color32>(0, entry.Layer);
            var blank = new Color32(0, 0, 0, 0);
            for (int row = 0; row < entry.Height; row++)
            {
                int at = (entry.Y + row) * _textureSize + entry.X;
                for (int col = 0; col < entry.Width; col++) data[at + col] = blank;
            }
            _dirty = true;

            var shelf = _layers[entry.Layer].Shelves[entry.ShelfIndex];
            shelf.LiveTiles--;
            // A shelf only comes back whole: partial reuse would need free-span
            // bookkeeping, and colour tiles are too few for that to pay.
            if (shelf.LiveTiles == 0)
            {
                shelf.X = 0;
                shelf.Height = shelf.Capacity;
            }
            _evictions++;
            Version++;
            return true;
        }

        private void Write(int layer, int x, int y, in ColorGlyph glyph)
        {
            var data = Texture.GetPixelData<Color32>(0, layer);
            for (int row = 0; row < glyph.Height; row++)
            {
                int dst = (y + row) * _textureSize + x;
                int src = row * glyph.Width;
                for (int col = 0; col < glyph.Width; col++) data[dst + col] = glyph.Pixels[src + col];
            }
            _dirty = true;
        }

        private void Clear(int layer)
        {
            var data = Texture.GetPixelData<Color32>(0, layer);
            var blank = new Color32(0, 0, 0, 0);
            for (int i = 0; i < data.Length; i++) data[i] = blank;
        }

        /// <summary>
        /// The tile's uv rect, inset by half a texel.
        ///
        /// Tiles pack flush against each other and the sampler is bilinear, so
        /// sampling exactly at the boundary blends in the neighbour's edge
        /// texel, and since an evicted tile's pixels are never cleared, that
        /// neighbour may be something that is no longer even in the atlas.
        /// </summary>
        private Rect UvRectOf(int x, int y, int w, int h)
        {
            const float inset = 0.5f;
            return new Rect(
                (x + inset) / _textureSize, (y + inset) / _textureSize,
                (w - inset * 2f) / _textureSize, (h - inset * 2f) / _textureSize);
        }

        /// <summary>Uploads pending writes. Cheap: colour tiles change rarely.</summary>
        public void Flush()
        {
            if (!_dirty) return;
            Texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _dirty = false;
        }

        /// <summary>True when tiles have been written but not uploaded.</summary>
        public bool HasPendingUpload => _dirty;

        public void Dispose()
        {
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            _entries.Clear();
            _lru.Clear();
        }
    }
}
