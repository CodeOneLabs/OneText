using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OneText
{
    /// <summary>
    /// One <see cref="FontData"/> per distinct font file, however many labels
    /// asked. <c>SetFont(bytes)</c> used to parse a HarfBuzz face per label —
    /// measured at a hundred labels sharing one font, that was 347 ms of
    /// creating the same face a hundred times where 19 ms was available — and
    /// worse than the parses, the hundred faces had a hundred cache ids, so
    /// the atlas baked a hundred copies of every glyph the labels shared.
    ///
    /// Identity first, content second: the common case is every label handed
    /// the same array, which the weak table answers without reading a byte.
    /// A caller that re-reads the file per label pays one full hash per new
    /// array (never per acquire) and still shares the face. The hash covers
    /// every byte, because a false positive here renders the wrong font and
    /// looks like data corruption, not like a cache bug.
    ///
    /// Refcounted, disposed at zero: fonts pin their bytes for HarfBuzz, and
    /// a cache that kept dead faces alive would hold a CJK file's sixteen
    /// megabytes for a scene that unloaded it. A pooled UI keeps a label
    /// alive and therefore keeps its face; a scene teardown returns to zero
    /// and frees native memory exactly as the per-label loads did.
    ///
    /// Variated faces never come from here: <see cref="FontData.SetVariations"/>
    /// mutates the face, and a shared face set to weight 700 would embolden
    /// every other label sharing it. The label keeps those private.
    ///
    /// Main thread only, like the label lifecycle that drives it.
    /// </summary>
    public static class SharedFontBytes
    {
        private sealed class Entry
        {
            public FontData Font;
            public int Refs;
            public long Key;
        }

        private static readonly Dictionary<long, Entry> s_byContent = new Dictionary<long, Entry>();
        private static readonly Dictionary<int, Entry> s_byCacheId = new Dictionary<int, Entry>();

        // Array instance -> content key, so identity hits never rehash. Weak
        // on the array: this table must not be what keeps a font's bytes
        // alive after every label dropped them.
        private static readonly ConditionalWeakTable<byte[], object> s_keyOf =
            new ConditionalWeakTable<byte[], object>();

        /// <summary>Live shared faces, for tests and the Hub.</summary>
        public static int LiveEntries => s_byContent.Count;

        /// <summary>
        /// The shared face for these bytes, parsing only if no live label
        /// already holds it. Every acquire is owed one <see cref="Release"/>.
        /// </summary>
        public static FontData Acquire(byte[] bytes, uint faceIndex = 0)
        {
            long key = KeyOf(bytes) ^ ((long)faceIndex << 56);
            if (s_byContent.TryGetValue(key, out var entry))
            {
                if (entry.Font.IsValid)
                {
                    entry.Refs++;
                    return entry.Font;
                }
                // A play-session boundary with Domain Reload off tears the
                // native side down under the statics; drop the husk and load
                // fresh rather than handing out a dead face.
                s_byCacheId.Remove(entry.Font.CacheId);
                s_byContent.Remove(key);
            }

            var font = FontData.Load(bytes, faceIndex);
            entry = new Entry { Font = font, Refs = 1, Key = key };
            s_byContent[key] = entry;
            s_byCacheId[font.CacheId] = entry;
            return font;
        }

        /// <summary>
        /// Returns a face taken from <see cref="Acquire"/>; the last release
        /// disposes it. A face this cache never issued is ignored, so a
        /// caller may release its whole list without bookkeeping which
        /// entries were shared.
        /// </summary>
        public static void Release(FontData font)
        {
            if (font == null || !s_byCacheId.TryGetValue(font.CacheId, out var entry)) return;
            if (--entry.Refs > 0) return;

            s_byCacheId.Remove(font.CacheId);
            s_byContent.Remove(entry.Key);
            entry.Font.Dispose();
        }

        private static long KeyOf(byte[] bytes)
        {
            if (s_keyOf.TryGetValue(bytes, out var cached)) return (long)cached;

            // FNV-1a over the whole file. Run once per array instance and
            // remembered; ~5 ms for a sixteen-megabyte CJK face, against the
            // face parse it is standing in for.
            unchecked
            {
                long hash = unchecked((long)0xCBF29CE484222325);
                for (int i = 0; i < bytes.Length; i++)
                    hash = (hash ^ bytes[i]) * 0x100000001B3;
                hash ^= bytes.LongLength << 1;
                s_keyOf.Add(bytes, hash);
                return hash;
            }
        }
    }
}
