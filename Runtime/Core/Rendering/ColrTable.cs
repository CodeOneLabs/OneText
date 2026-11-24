using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The version 1 half of a font's <c>COLR</c> table, read in place.
    ///
    /// Version 0 is a list of layers and HarfBuzz reads it for us. Version 1
    /// is a graph of paint tables — fills, gradients, transforms, clips and
    /// compositing — and HarfBuzz's way in is a set of callbacks
    /// (<c>hb_font_paint_glyph</c>) that the Linux binary this package ships
    /// does not export. So the table is walked here, from the bytes, the same
    /// on every platform; the format is public and fixed (OpenType 1.9,
    /// "COLR — Color Table").
    ///
    /// Nothing is copied: Noto's table is over a megabyte and a glyph reads a
    /// few hundred bytes of it. Every read is bounds-checked, because a font
    /// file is untrusted input, and a read past the end sets
    /// <see cref="Truncated"/> rather than throwing — the caller abandons the
    /// glyph and it goes down the next path.
    ///
    /// Not read: the variation data (<c>VarIndexMap</c>, the
    /// <c>ItemVariationStore</c>). The variable paint formats are understood
    /// and drawn at their default values, so a variable colour font draws its
    /// default instance's colours and gradient geometry whatever the axes say.
    /// </summary>
    internal sealed unsafe class ColrTable
    {
        internal const uint Tag = ('C' << 24) | ('O' << 16) | ('L' << 8) | 'R';

        private readonly byte* _data;
        private readonly int _length;

        private readonly uint _baseGlyphs, _baseGlyphCount;
        private readonly uint _layers, _layerCount;
        private readonly uint _clips, _clipCount;

        /// <summary>A read ran off the end of the table since this was last cleared.</summary>
        public bool Truncated;

        // Whether a glyph's paint graph asks for the text colour anywhere.
        // Asked once per glyph per label build, and the answer is a walk of
        // the graph, so it is kept.
        internal readonly Dictionary<uint, bool> FollowsText = new Dictionary<uint, bool>();

        // Glyphs whose graph had to be abandoned, and the smallest scale it
        // happened at: zero when the graph itself is at fault (cyclic, too
        // deep, cut short) and no size will draw it. Without this the caller,
        // which is told only "not in colour", would walk the same graph to
        // the same limit every time the label is rebuilt.
        private readonly Dictionary<uint, float> _abandonedAt = new Dictionary<uint, float>();

        /// <summary>True when drawing this glyph at this scale is already known to fail.</summary>
        public bool IsAbandoned(uint glyphId, float pixelsPerUnit) =>
            _abandonedAt.TryGetValue(glyphId, out float at) && pixelsPerUnit >= at;

        /// <summary>
        /// Remembers a glyph that was abandoned. <paramref name="pixelsPerUnit"/>
        /// is the scale it ran out of room at, or zero for a graph that is
        /// broken at any size.
        /// </summary>
        public void Abandon(uint glyphId, float pixelsPerUnit)
        {
            if (!_abandonedAt.TryGetValue(glyphId, out float at) || pixelsPerUnit < at)
                _abandonedAt[glyphId] = pixelsPerUnit;
        }

        private ColrTable(byte* data, int length)
        {
            _data = data;
            _length = length;

            // version, then the three version 0 fields, then:
            //   14 baseGlyphListOffset  18 layerListOffset  22 clipListOffset
            uint baseGlyphs = U32(14), layers = U32(18), clips = U32(22);
            // A count is what the file says, not what the file holds. Each is
            // cut to the records that fit, so a search never probes past the
            // end and the same glyph gets the same answer whatever was asked
            // for before it.
            if (baseGlyphs != 0)
            {
                _baseGlyphs = baseGlyphs;
                _baseGlyphCount = Fitting(U32(baseGlyphs), baseGlyphs + 4L, 6);
            }
            if (layers != 0)
            {
                _layers = layers;
                _layerCount = Fitting(U32(layers), layers + 4L, 4);
            }
            // Format 1 is the only clip list there is.
            if (clips != 0 && U8(clips) == 1)
            {
                _clips = clips;
                _clipCount = Fitting(U32(clips + 1), clips + 5L, 7);
            }
            if (Truncated) _baseGlyphCount = 0;
            Truncated = false;
        }

        /// <summary>How many of <paramref name="count"/> records of this size lie inside the table.</summary>
        private uint Fitting(uint count, long firstRecord, int recordSize)
        {
            long room = _length - firstRecord;
            if (room <= 0) return 0;
            return (uint)Math.Min(count, room / recordSize);
        }

        /// <summary>True when the table has any version 1 glyph at all.</summary>
        public bool HasPaint => _baseGlyphCount > 0;

        // Keyed by FontData.CacheId for the reason ColorGlyphs gives: a native
        // pointer is reused the moment its font is freed. A font with no
        // version 1 data is remembered as null, so it is asked once.
        private static readonly Dictionary<int, ColrTable> s_tables = new Dictionary<int, ColrTable>();

        public static ColrTable For(FontData font)
        {
            if (font == null || !font.IsValid) return null;
            if (s_tables.TryGetValue(font.CacheId, out var table)) return table;

            table = null;
            if (font.TryGetTable(Tag, out var data, out int length) && length >= 34)
            {
                var bytes = (byte*)data;
                int version = (bytes[0] << 8) | bytes[1];
                if (version >= 1)
                {
                    var candidate = new ColrTable(bytes, length);
                    if (candidate.HasPaint) table = candidate;
                }
            }
            s_tables[font.CacheId] = table;
            return table;
        }

        public static void Forget(int cacheId) => s_tables.Remove(cacheId);
        public static void ClearCache() => s_tables.Clear();

        // ---------------------------------------------------------------- reads

        public uint U8(uint offset)
        {
            if (offset >= (uint)_length) { Truncated = true; return 0; }
            return _data[offset];
        }

        public uint U16(uint offset)
        {
            if ((long)offset + 2 > _length) { Truncated = true; return 0; }
            return ((uint)_data[offset] << 8) | _data[offset + 1];
        }

        public uint U24(uint offset)
        {
            if ((long)offset + 3 > _length) { Truncated = true; return 0; }
            return ((uint)_data[offset] << 16) | ((uint)_data[offset + 1] << 8) | _data[offset + 2];
        }

        public uint U32(uint offset)
        {
            if ((long)offset + 4 > _length) { Truncated = true; return 0; }
            return ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) |
                   ((uint)_data[offset + 2] << 8) | _data[offset + 3];
        }

        /// <summary>FWORD: a signed distance in font units.</summary>
        public float I16(uint offset) => (short)U16(offset);

        /// <summary>F2DOT14: a signed fraction, 2.14 fixed point.</summary>
        public float F2Dot14(uint offset) => (short)U16(offset) / 16384f;

        /// <summary>Fixed: 16.16 signed fixed point.</summary>
        public float Fixed(uint offset) => (int)U32(offset) / 65536f;

        // -------------------------------------------------------------- lookups

        /// <summary>
        /// The root paint of a glyph, as an offset into the table. The base
        /// glyph list is sorted by glyph id, which the format requires and a
        /// binary search relies on; a font that breaks the rule loses glyphs
        /// rather than gaining wrong ones.
        /// </summary>
        public bool TryFindRoot(uint glyphId, out uint paint)
        {
            // Asked from outside a walk, where nobody is watching the flag: a
            // read that ran off the end must not be left standing to fail
            // every lookup after it.
            Truncated = false;
            bool found = TryFindPaint(glyphId, out paint);
            Truncated = false;
            return found;
        }

        /// <summary>The same lookup from inside a walk, which abandons the glyph on a short read.</summary>
        public bool TryFindPaint(uint glyphId, out uint paint)
        {
            paint = 0;
            uint lo = 0, hi = _baseGlyphCount;
            while (lo < hi)
            {
                uint mid = lo + (hi - lo) / 2;
                uint record = _baseGlyphs + 4 + mid * 6;
                uint glyph = U16(record);
                if (Truncated) return false;
                if (glyph == glyphId)
                {
                    uint offset = U32(record + 2);
                    if (offset == 0 || Truncated) return false;
                    paint = _baseGlyphs + offset;
                    return true;
                }
                if (glyph < glyphId) lo = mid + 1;
                else hi = mid;
            }
            return false;
        }

        /// <summary>One entry of the layer list, which PaintColrLayers indexes into.</summary>
        public bool TryLayer(uint index, out uint paint)
        {
            paint = 0;
            if (index >= _layerCount) return false;
            uint offset = U32(_layers + 4 + index * 4);
            if (offset == 0 || Truncated) return false;
            paint = _layers + offset;
            return true;
        }

        /// <summary>
        /// The box a glyph's drawing is clipped to, in font units, if the font
        /// gives one. It is also the font's own statement of how big the
        /// glyph's picture is, which a paint graph otherwise cannot say
        /// without being drawn.
        /// </summary>
        public bool TryClipBox(uint glyphId, out Vector2 min, out Vector2 max)
        {
            min = max = default;
            uint lo = 0, hi = _clipCount;
            while (lo < hi)
            {
                uint mid = lo + (hi - lo) / 2;
                uint record = _clips + 5 + mid * 7;
                uint first = U16(record), last = U16(record + 2);
                if (Truncated) return false;
                if (glyphId < first) hi = mid;
                else if (glyphId > last) lo = mid + 1;
                else
                {
                    uint offset = U24(record + 4);
                    if (offset == 0) return false;
                    uint box = _clips + offset;
                    // Format 1 and format 2 differ only by a trailing
                    // variation index, which is not read.
                    uint format = U8(box);
                    if (format != 1 && format != 2) return false;
                    min = new Vector2(I16(box + 1), I16(box + 3));
                    max = new Vector2(I16(box + 5), I16(box + 7));
                    return !Truncated && max.x > min.x && max.y > min.y;
                }
            }
            return false;
        }
    }
}
