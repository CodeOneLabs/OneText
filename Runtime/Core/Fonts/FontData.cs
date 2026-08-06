using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OneText.Native;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A font loaded from raw bytes (TTF/OTF/TTC), holding the native
    /// HarfBuzz blob/face/font handles. Fonts load from memory only:
    /// no file I/O at runtime.
    /// </summary>
    public sealed class FontData : IDisposable
    {
        private GCHandle _bytesHandle;
        // Variant instances borrow the face and blob from the font they came from.
        private bool _ownsFace;

        // Guards the mutable managed state (ink bounds, the clone table). Cheap
        // and uncontended in the single-threaded case, which is still every
        // caller today; see ForCurrentThread for why the native side needs more
        // than a lock.
        private readonly object _sync = new object();

        // Per-thread handles onto this same parsed face, created on demand and
        // owned by this instance. Keyed by managed thread id, which the runtime
        // recycles: a thread pool reuses a bounded set of ids, so the table
        // stays small. Code that spawns a fresh dedicated thread per unit of
        // work would grow it without bound, one more reason worker threads
        // should be pooled.
        private ConcurrentDictionary<int, FontData> _threadHandles;

        // Handles whose axis settings are out of date. They are not destroyed
        // on the spot because a worker may be inside hb_shape with one right
        // now; see SetVariations.
        private List<FontData> _retiredHandles;

        private FontVariation[] _appliedVariations = Array.Empty<FontVariation>();
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;

        // True on a handle that ForCurrentThread produced. A handle belongs to
        // one thread by construction, so it never sub-leases.
        private bool _isThreadHandle;
        internal IntPtr Blob { get; private set; }
        internal IntPtr Face { get; private set; }
        internal IntPtr Font { get; private set; }

        /// <summary>Design units per em (typically 1000 or 2048).</summary>
        public uint UnitsPerEm { get; private set; }

        /// <summary>Horizontal font metrics in design units (ascender is positive, descender negative).</summary>
        public int Ascender { get; private set; }

        public int Descender { get; private set; }

        public int LineGap { get; private set; }

        /// <summary>
        /// Bumped whenever the outlines change (variation settings), so
        /// rasterized-glyph caches can tell one instance of a variable font
        /// from another.
        /// </summary>
        public int Generation { get; private set; }

        public bool IsValid => Font != IntPtr.Zero;

        /// <summary>
        /// A stable id for this handle, for callers outside the core that need
        /// to key a cache by "which font". The native handle is the real
        /// identity but it is internal, and a raw pointer is not something a
        /// frontend should be holding.
        /// </summary>
        public int CacheId { get; } = System.Threading.Interlocked.Increment(ref s_nextCacheId);

        private static int s_nextCacheId;

        /// <summary>True if the font carries an OpenType variations table.</summary>
        public bool IsVariable => Face != IntPtr.Zero && HarfBuzzApi.hb_ot_var_has_data(Face) != 0;

        public static FontData Load(byte[] fontBytes, uint faceIndex = 0)
        {
            if (fontBytes == null || fontBytes.Length == 0)
                throw new ArgumentException("Font bytes are empty.", nameof(fontBytes));

            var data = new FontData();
            // Pin the managed array instead of copying; the blob reads it in place.
            data._bytesHandle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
            data.Blob = HarfBuzzApi.hb_blob_create(
                data._bytesHandle.AddrOfPinnedObject(), (uint)fontBytes.Length,
                HarfBuzzApi.HB_MEMORY_MODE_READONLY, IntPtr.Zero, IntPtr.Zero);
            data.Face = HarfBuzzApi.hb_face_create(data.Blob, faceIndex);
            data.Font = HarfBuzzApi.hb_font_create(data.Face);
            data.UnitsPerEm = HarfBuzzApi.hb_face_get_upem(data.Face);
            data._ownsFace = true;
            // HarfBuzz's own rule for sharing: an object may be read from any
            // number of threads once it can no longer be modified. The face is
            // the expensive, shared half of a font, and nothing here ever edits
            // it after load; say so, so concurrent shaping is legal rather than
            // merely lucky.
            HarfBuzzApi.hb_face_make_immutable(data.Face);
            data.ReadMetrics();
            return data;
        }

        /// <summary>
        /// A handle onto this same face that belongs to the calling thread.
        ///
        /// The face is immutable and safe to share; an <c>hb_font_t</c> is not.
        /// It carries the variation coordinates and a lazily-populated shaping
        /// cache, so two threads shaping through one handle can read each
        /// other's axis settings; the failure is not a crash but a word that
        /// comes out at the wrong weight, in a build, once. Handles are created
        /// on demand, cached per thread, and disposed with the font that owns
        /// them; on the thread that loaded the font this returns the font
        /// itself, so the common single-threaded path allocates nothing.
        ///
        /// Variants get their own handles too. A variant is where the axes
        /// actually live, so it is the instance most likely to be shaped from
        /// several threads at once; treating it as a leaf would leave the one
        /// case this exists for unprotected.
        ///
        /// The atlas keys tiles by handle, so glyphs baked from a worker thread
        /// do not share cache entries with the main thread's. That is a cache
        /// cost, not a correctness one, and it is the reason rasterization
        /// should stay where the atlas is.
        /// </summary>
        public FontData ForCurrentThread()
        {
            if (!IsValid) throw new InvalidOperationException("Font is not loaded.");
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (thread == _ownerThread || _isThreadHandle) return this;

            // A lock rather than ConcurrentDictionary.GetOrAdd: that factory may
            // run more than once for the same key under contention, and the
            // loser's hb_font would leak. This runs once per thread per font.
            lock (_sync)
            {
                _threadHandles ??= new ConcurrentDictionary<int, FontData>();
                if (_threadHandles.TryGetValue(thread, out var existing)) return existing;
                var handle = CreateVariant(_appliedVariations);
                handle._isThreadHandle = true;
                _threadHandles[thread] = handle;
                return handle;
            }
        }

        /// <summary>
        /// A second font handle over the same parsed face, with its own
        /// variation settings. Two labels can render one variable font at
        /// different weights without fighting over one handle, and the face
        /// (the expensive part) is parsed only once.
        /// </summary>
        public FontData CreateVariant(params FontVariation[] variations)
        {
            if (!IsValid) throw new InvalidOperationException("Font is not loaded.");

            var variant = new FontData
            {
                Face = Face,
                Font = HarfBuzzApi.hb_font_create(Face),
                UnitsPerEm = UnitsPerEm,
                _ownsFace = false,
            };
            variant.ReadMetrics();
            if (variations is { Length: > 0 }) variant.SetVariations(variations);
            return variant;
        }

        private void ReadMetrics()
        {
            if (HarfBuzzApi.hb_font_get_h_extents(Font, out var extents) != 0)
            {
                Ascender = extents.Ascender;
                Descender = extents.Descender;
                LineGap = extents.LineGap;
            }
            else
            {
                // Fonts without hhea/OS2 extents still need a sane line box.
                Ascender = (int)(UnitsPerEm * 0.8f);
                Descender = -(int)(UnitsPerEm * 0.2f);
                LineGap = 0;
            }
        }

        /// <summary>True if the font has a glyph for this codepoint.</summary>
        public bool HasGlyph(int codepoint) =>
            IsValid && HarfBuzzApi.hb_font_get_nominal_glyph(Font, (uint)codepoint, out uint glyph) != 0
            && glyph != 0;

        /// <summary>
        /// The glyph the cmap maps a codepoint to, before any shaping. Zero
        /// when the font has none.
        ///
        /// Diagnostics ask this to tell a substitution from a plain lookup: if
        /// the glyph on screen is not the one the character maps to, some
        /// OpenType feature put it there, and that is usually the thing being
        /// investigated.
        /// </summary>
        public uint NominalGlyph(int codepoint) =>
            IsValid && HarfBuzzApi.hb_font_get_nominal_glyph(Font, (uint)codepoint, out uint glyph) != 0
                ? glyph
                : 0u;

        /// <summary>
        /// Feature tags the font registers in GSUB or GPOS: "liga", "locl",
        /// "kern". What HarfBuzz can apply to this face, not what it applied to
        /// a particular glyph, which OpenType does not report.
        /// </summary>
        public string[] LayoutFeatures(bool substitution)
        {
            if (Face == System.IntPtr.Zero) return System.Array.Empty<string>();
            uint table = substitution ? HarfBuzzApi.HB_OT_TAG_GSUB : HarfBuzzApi.HB_OT_TAG_GPOS;

            uint count = 0;
            uint total = HarfBuzzApi.hb_ot_layout_table_get_feature_tags(Face, table, 0, ref count, null);
            if (total == 0) return System.Array.Empty<string>();

            count = total;
            var tags = new uint[total];
            HarfBuzzApi.hb_ot_layout_table_get_feature_tags(Face, table, 0, ref count, tags);

            var names = new string[count];
            for (uint i = 0; i < count; i++) names[i] = HarfBuzzApi.TagToString(tags[i]);
            return names;
        }

        /// <summary>The variation axes this font exposes (empty for static fonts).</summary>
        public FontAxis[] GetVariationAxes()
        {
            if (!IsVariable) return System.Array.Empty<FontAxis>();
            unsafe
            {
                // The axis count is an in/out parameter: it has to say how much
                // room the array has, so query the real count first.
                uint count = HarfBuzzApi.hb_ot_var_get_axis_count(Face);
                if (count == 0) return System.Array.Empty<FontAxis>();

                var infos = stackalloc HBVarAxisInfo[(int)count];
                HarfBuzzApi.hb_ot_var_get_axis_infos(Face, 0, ref count, infos);
                var axes = new FontAxis[count];
                for (int i = 0; i < count; i++)
                {
                    axes[i] = new FontAxis(TagToString(infos[i].Tag),
                        infos[i].MinValue, infos[i].DefaultValue, infos[i].MaxValue);
                }
                return axes;
            }
        }

        /// <summary>
        /// Applies variation axis settings (e.g. <c>wght</c> 700, <c>wdth</c> 87.5).
        /// Outlines, advances and cached metrics all change, so this bumps
        /// <see cref="Generation"/> and drops the ink-bounds cache.
        /// </summary>
        public void SetVariations(params FontVariation[] variations)
        {
            if (!IsValid) return;
            RetireThreadHandles(variations);

            unsafe
            {
                int count = variations?.Length ?? 0;
                var native = stackalloc HBVariation[count > 0 ? count : 1];
                for (int i = 0; i < count; i++)
                    native[i] = new HBVariation { Tag = StringToTag(variations[i].Tag), Value = variations[i].Value };
                HarfBuzzApi.hb_font_set_variations(Font, native, (uint)count);
            }
            lock (_sync) _inkBounds?.Clear();
            ReadMetrics();
            Generation++;
        }

        /// <summary>
        /// Takes the per-thread handles out of service because the axes are
        /// changing, and records the new axes for the handles built next.
        ///
        /// They are set aside rather than destroyed. A worker may be inside
        /// <c>hb_shape</c> with one at this very moment, and
        /// <c>hb_font_destroy</c> under it is a native use-after-free, a crash
        /// with no managed stack, which is the worst possible way to learn that
        /// a label changed weight while a background layout was running. They
        /// cost one small native object each and are freed when the font is.
        /// </summary>
        private void RetireThreadHandles(FontVariation[] variations)
        {
            lock (_sync)
            {
                _appliedVariations = variations != null && variations.Length > 0
                    ? (FontVariation[])variations.Clone()
                    : Array.Empty<FontVariation>();

                if (_threadHandles == null) return;
                _retiredHandles ??= new List<FontData>();
                foreach (var handle in _threadHandles.Values) _retiredHandles.Add(handle);
                _threadHandles = null;
            }
        }

        private void DisposeThreadHandles()
        {
            ConcurrentDictionary<int, FontData> handles;
            List<FontData> retired;
            lock (_sync)
            {
                handles = _threadHandles;
                retired = _retiredHandles;
                _threadHandles = null;
                _retiredHandles = null;
            }
            if (handles != null)
                foreach (var handle in handles.Values) handle.Dispose();
            if (retired != null)
                foreach (var handle in retired) handle.Dispose();
        }

        private static uint StringToTag(string tag)
        {
            uint value = 0;
            for (int i = 0; i < 4; i++)
                value = (value << 8) | (uint)(byte)(i < tag.Length ? tag[i] : ' ');
            return value;
        }

        private static string TagToString(uint tag) => new string(new[]
        {
            (char)((tag >> 24) & 0xFF), (char)((tag >> 16) & 0xFF),
            (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF),
        });

        private Dictionary<uint, (Vector2 min, Vector2 max, bool hasInk)> _inkBounds;

        // Per-thread, not shared: the outline walk below writes into it, and two
        // threads measuring two glyphs through one scratch object produce one
        // glyph shaped like both.
        [ThreadStatic] private static GlyphOutline t_boundsScratch;

        /// <summary>
        /// Ink bounding box of a glyph in font units (cached). Returns false
        /// for glyphs without contours (spaces, controls).
        ///
        /// This asks HarfBuzz for the box directly rather than flattening the
        /// outline to measure it. Clustering calls this for every glyph of
        /// every line, and a font's glyphs are not all resident: in a CJK chat
        /// stream the misses alone cost about as much as rasterizing, for a
        /// number the font already stores. The outline walk stays as a
        /// fallback for faces whose extents HarfBuzz cannot report.
        /// </summary>
        public bool TryGetInkBounds(uint glyphId, out Vector2 min, out Vector2 max)
        {
            (Vector2 min, Vector2 max, bool hasInk) cached;
            lock (_sync)
            {
                _inkBounds ??= new Dictionary<uint, (Vector2, Vector2, bool)>();
                if (_inkBounds.TryGetValue(glyphId, out cached))
                {
                    min = cached.min;
                    max = cached.max;
                    return cached.hasInk;
                }
            }

            // Measured outside the lock: the outline fallback flattens curves,
            // which is long enough that holding the lock through it would
            // serialize every other thread's cache hits behind it. Two threads
            // racing on the same glyph both measure and agree, so the duplicate
            // work is the cheaper mistake.
            cached = HarfBuzzApi.hb_font_get_glyph_extents(Font, glyphId, out var extents) != 0
                ? FromExtents(extents)
                : FromOutline(glyphId);
            lock (_sync) _inkBounds[glyphId] = cached;

            min = cached.min;
            max = cached.max;
            return cached.hasInk;
        }

        private static (Vector2, Vector2, bool) FromExtents(HBGlyphExtents e)
        {
            // Height runs downward from the bearing.
            var lo = new Vector2(e.XBearing, e.YBearing + e.Height);
            var hi = new Vector2(e.XBearing + e.Width, e.YBearing);
            bool hasInk = e.Width != 0 && e.Height != 0;
            return (lo, hi, hasInk);
        }

        private (Vector2, Vector2, bool) FromOutline(uint glyphId)
        {
            var scratch = t_boundsScratch ??= new GlyphOutline();
            scratch.Clear();
            OutlineExtractor.Extract(this, glyphId, scratch);
            var lo = new Vector2(float.MaxValue, float.MaxValue);
            var hi = new Vector2(float.MinValue, float.MinValue);
            bool hasInk = false;
            foreach (var contour in scratch.Contours)
            {
                if (contour.Count < 2) continue;
                hasInk = true;
                foreach (var p in contour)
                {
                    lo = Vector2.Min(lo, p);
                    hi = Vector2.Max(hi, p);
                }
            }
            return (lo, hi, hasInk);
        }

        public void Dispose()
        {
            ColorGlyphs.Forget(CacheId);
            DisposeThreadHandles();
            if (Font != IntPtr.Zero) { HarfBuzzApi.hb_font_destroy(Font); Font = IntPtr.Zero; }
            if (_ownsFace)
            {
                if (Face != IntPtr.Zero) { HarfBuzzApi.hb_face_destroy(Face); }
                if (Blob != IntPtr.Zero) { HarfBuzzApi.hb_blob_destroy(Blob); }
                if (_bytesHandle.IsAllocated) _bytesHandle.Free();
            }
            Face = IntPtr.Zero;
            Blob = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        ~FontData()
        {
            Dispose();
        }
    }
}
