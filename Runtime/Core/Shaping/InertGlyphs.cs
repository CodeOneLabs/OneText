using System;
using System.Collections.Generic;
using System.Threading;
using OneText.Native;

namespace OneText
{
    /// <summary>
    /// The characters of one font that HarfBuzz provably cannot change, and
    /// what HarfBuzz would answer for a run made only of them without asking it.
    ///
    /// <para>
    /// Shaping a run is, for most scripts most of the time, the identity: one
    /// glyph per character, the glyph <c>cmap</c> names, the advance
    /// <c>hmtx</c> gives, no offsets. HarfBuzz still costs a buffer reset, a
    /// copy in, a plan lookup, two passes over GSUB and GPOS and a copy out to
    /// arrive there, and for a label of four digits that is most of the layout.
    /// This class answers the identity case directly, and only that case.
    /// </para>
    ///
    /// <para>
    /// <b>Why the answer is exactly HarfBuzz's.</b> Everything HarfBuzz does to
    /// a run on top of <c>cmap</c> + <c>hmtx</c> comes from one of these, and
    /// each is shut off:
    /// </para>
    /// <list type="bullet">
    /// <item><b>GSUB and GPOS lookups.</b> Every lookup type, contextual ones
    /// included, applies at a position only if the glyph there is in the
    /// subtable's first coverage table; a nested lookup applies only inside a
    /// parent match, which itself began at a covered glyph; and lookups never
    /// see the pre- and post-context outside the buffer. So if no glyph of the
    /// run is in the first coverage of any lookup HarfBuzz could select, no
    /// lookup fires anywhere in it. "Could select" is taken wide: every lookup
    /// reachable from any feature HarfBuzz turns on by itself, under every
    /// script and every language system in the font, whatever the run's own
    /// script or language tag (so <c>locl</c> for every language counts). A
    /// font with a required feature or with FeatureVariations is not analysed
    /// at all. One refinement, argued at <see cref="AddLigatures"/>: a glyph
    /// that only begins ligatures which need a component no admitted
    /// character maps to stays inert, since in an answered run every glyph is
    /// one an admitted character mapped to.</item>
    /// <item><b>Marks.</b> GDEF mark-class glyphs have their advances zeroed and
    /// can be positioned without GPOS; they are never inert. Unicode marks are
    /// not on the character list below at all.</item>
    /// <item><b>Character-level processing</b>: normalization, default
    /// ignorables, grapheme continuations (which merge clusters), Hangul jamo
    /// composition, Thai/Lao decomposition, the fraction slash, the space
    /// fallback, mirroring. The character list below admits only characters
    /// none of these touch, in scripts that select HarfBuzz's default or Hangul
    /// shaper, and only when the font has a real glyph for them.</item>
    /// <item><b>Tables HarfBuzz reads outside GSUB/GPOS</b>: <c>kern</c>,
    /// <c>kerx</c>, <c>morx</c>, <c>mort</c>, <c>trak</c>. A font with any of
    /// them is not analysed.</item>
    /// <item><b>Direction.</b> Only an explicit left-to-right run is answered.</item>
    /// </list>
    ///
    /// <para>
    /// The analysis belongs to one <see cref="FontData"/> handle at one
    /// <see cref="FontData.Generation"/>: a variation change rebuilds it,
    /// because advances move with the axes. <c>InertShapingTests</c> shapes
    /// every admitted codepoint of every test font both ways, and long random
    /// runs of them, and compares glyph for glyph.
    /// </para>
    /// </summary>
    internal sealed class InertGlyphs
    {
        /// <summary>
        /// Set the first time the native library turns out not to export an
        /// entry point this needs (the Linux binary exports only what
        /// HarfBuzzApi.cs listed when it was built, and is rebuilt in CI).
        /// From then on every run goes to HarfBuzz, which is always correct.
        /// </summary>
        internal static bool NativeUnavailable;

        /// <summary>
        /// The entry points only this class calls, and so the only ones a
        /// binary may lack without anything breaking (NativesTests holds the
        /// Linux binary to that until it is rebuilt).
        /// </summary>
        internal static readonly string[] EntryPoints =
        {
            "hb_face_reference_table", "hb_face_collect_unicodes", "hb_ot_layout_collect_lookups",
            "hb_ot_layout_get_glyphs_in_class", "hb_font_get_glyph_h_advance",
            "hb_set_clear", "hb_set_next_range",
        };

        internal readonly int Generation;

        /// <summary>False when the font has a table or a structure this does not reason about.</summary>
        internal readonly bool Eligible;

        /// <summary>Why <see cref="Eligible"/> is false, for tests and diagnostics.</summary>
        internal readonly string Refusal;

        private readonly IntPtr _font;

        // Glyph ids a lookup could act on, or that are marks. 65,536 bits.
        private readonly ulong[] _touched;

        // Per codepoint, lazily: Glyph 0 = not asked yet, -1 = refused,
        // otherwise the glyph id; Advance is meaningful only for a glyph.
        private struct Entry
        {
            public int Glyph;
            public int Advance;
        }

        private readonly Entry[][] _pages;

        private InertGlyphs(int generation, IntPtr font, ulong[] touched, string refusal)
        {
            Generation = generation;
            _font = font;
            _touched = touched;
            Refusal = refusal;
            Eligible = refusal == null;
            if (Eligible) _pages = new Entry[0x110000 >> 8][];
        }

        /// <summary>
        /// Answers a left-to-right run of <paramref name="text"/> if every
        /// character in it is inert in this font, appending exactly what
        /// <c>hb_shape</c> would. Otherwise returns false with
        /// <paramref name="output"/> as it was.
        /// </summary>
        internal bool TryShape(ReadOnlySpan<char> text, int start, int length, List<ShapedGlyph> output)
        {
            if (!Eligible) return false;
            int end = start + length;
            if (start < 0 || end > text.Length) return false;

            // One pass that writes as it goes and takes its glyphs back on a
            // refusal, so the output is exactly as it was and HarfBuzz starts
            // from a clean slate. Most refusals are at the first character.
            int written = output.Count;
            for (int i = start; i < end;)
            {
                int cp = text[i];
                int width = 1;
                if ((cp & 0xF800) == 0xD800)
                {
                    // A pair must be whole inside the run: HarfBuzz replaces a
                    // half pair with U+FFFD, which this does not model.
                    int low = i + 1 < end ? text[i + 1] : 0;
                    if (cp > 0xDBFF || low < 0xDC00 || low > 0xDFFF) return Refuse(output, written);
                    cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    width = 2;
                }

                var page = _pages[cp >> 8];
                int glyph, advance;
                if (page != null && (glyph = Volatile.Read(ref page[cp & 0xFF].Glyph)) != 0)
                    advance = page[cp & 0xFF].Advance;
                else
                    glyph = Resolve(cp, out advance);
                if (glyph < 0) return Refuse(output, written);

                output.Add(new ShapedGlyph((uint)glyph, i, advance, 0, 0, 0));
                i += width;
            }
            return true;
        }

        private static bool Refuse(List<ShapedGlyph> output, int written)
        {
            if (output.Count > written) output.RemoveRange(written, output.Count - written);
            return false;
        }

        /// <summary>Asks the font once per codepoint and remembers the answer.</summary>
        private int Resolve(int cp, out int advance)
        {
            var page = _pages[cp >> 8];
            if (page == null)
            {
                page = new Entry[256];
                _pages[cp >> 8] = page;
            }
            var resolved = Classify(cp);
            ref var slot = ref page[cp & 0xFF];
            // Advance before glyph: a reader that sees the glyph sees its advance.
            slot.Advance = resolved.Advance;
            Volatile.Write(ref slot.Glyph, resolved.Glyph);
            advance = resolved.Advance;
            return resolved.Glyph;
        }

        private Entry Classify(int cp)
        {
            var refused = new Entry { Glyph = -1 };
            if (!IsPlainCharacter(cp)) return refused;
            if (HarfBuzzApi.hb_font_get_nominal_glyph(_font, (uint)cp, out uint glyph) == 0) return refused;
            if (glyph == 0 || glyph > 0xFFFF) return refused;
            if ((_touched[glyph >> 6] & (1UL << (int)(glyph & 63))) != 0) return refused;
            return new Entry { Glyph = (int)glyph, Advance = HarfBuzzApi.hb_font_get_glyph_h_advance(_font, glyph) };
        }

        /// <summary>
        /// Characters HarfBuzz hands to <c>cmap</c> and leaves alone when the
        /// font has a glyph for them: no general category M or Cf, not
        /// default-ignorable, not a grapheme continuation (U+FF9E/U+FF9F, emoji
        /// modifiers, regional indicators, tags), no singleton decomposition
        /// that could matter (CJK compatibility ideographs), not the fraction
        /// slash (U+2044 is what turns on <c>frac</c>/<c>numr</c>/<c>dnom</c>,
        /// which is why those features are not in <see cref="Features"/>), not a
        /// conjoining jamo or Hangul tone mark, and in a script that uses
        /// HarfBuzz's default shaper (Latin, Greek, Cyrillic, Han, kana,
        /// Common) or its Hangul shaper with a precomposed syllable. Narrow on
        /// purpose: what is not here is shaped by HarfBuzz, which is merely
        /// slower.
        /// </summary>
        internal static bool IsPlainCharacter(int cp)
        {
            if (cp < 0x80) return cp >= 0x20 && cp < 0x7F;
            if (cp < 0x0300) return cp >= 0xA0 && cp != 0xAD;                          // Latin-1, Ext-A/B, IPA, modifier letters
            if (cp < 0x0370) return false;                                             // combining diacritics
            if (cp < 0x0530)
                return (cp <= 0x03E1 || cp >= 0x03F0) && (cp < 0x0483 || cp > 0x0489); // Greek (no Coptic), Cyrillic (no combining)
            if (cp < 0x1E00) return false;
            if (cp < 0x2000) return true;                                              // Latin Ext Additional, Greek Extended
            if (cp < 0x2010) return false;                                             // spaces with fallback widths, ZW*, LRM/RLM
            if (cp < 0x2028) return true;                                              // dashes, quotes, bullets, ellipsis
            if (cp < 0x2030) return false;                                             // separators, bidi embeddings, NNBSP
            if (cp < 0x205F) return cp != 0x2044;                                      // per mille .. the fraction slash excluded
            if (cp < 0x2070) return false;                                             // MMSP, word joiner, invisible operators, bidi isolates
            if (cp < 0x20C1) return true;                                              // super/subscripts, currency
            if (cp < 0x2100) return false;                                             // combining marks for symbols
            if (cp < 0x2C00) return true;                                              // letterlike .. misc symbols and arrows
            if (cp < 0x3000) return false;
            if (cp < 0x3040) return cp < 0x302A || cp > 0x302F;                        // CJK punctuation, no tone marks
            if (cp < 0x3100) return (cp > 0x3040 && cp < 0x3097) || cp >= 0x309B;     // kana, no combining voicing marks
            if (cp < 0x3200) return cp >= 0x31F0;                                      // katakana phonetic extensions
            if (cp < 0x3400) return true;                                              // enclosed CJK, CJK compatibility
            if (cp < 0x4DC0) return true;                                              // Han extension A
            if (cp < 0x4E00) return false;
            if (cp < 0xA000) return true;                                              // Han
            if (cp < 0xAC00) return false;
            if (cp < 0xD7A4) return true;                                              // Hangul syllables
            if (cp < 0xFF01) return false;
            if (cp < 0xFF9E) return true;                                              // full width forms, half width kana
            if (cp < 0xFFE0) return false;                                             // voicing marks, half width jamo
            if (cp < 0xFFEF) return true;                                              // full width signs
            if (cp < 0x20000) return false;
            if (cp < 0x2F800) return true;                                             // Han extensions B..F, I
            if (cp < 0x30000) return false;                                            // compatibility ideographs supplement
            if (cp < 0x323B0) return true;                                             // Han extensions G, H
            return false;
        }

        /// <summary>
        /// Every feature HarfBuzz 14.2.1 enables without being asked, for the
        /// two shapers <see cref="IsPlainCharacter"/> can select, in either
        /// horizontal direction: hb-ot-shape.cc's common and horizontal
        /// feature arrays, the ones <c>hb_ot_shape_collect_features</c> adds by
        /// hand (minus <c>frac</c>/<c>numr</c>/<c>dnom</c>, see
        /// <see cref="IsPlainCharacter"/>, and <c>vert</c>, vertical only), and
        /// the Hangul shaper's jamo features.
        /// </summary>
        private static readonly string[] Features =
        {
            "abvm", "blwm", "ccmp", "locl", "mark", "mkmk", "rlig",
            "calt", "clig", "curs", "dist", "kern", "liga", "rclt",
            "rvrn", "ltra", "ltrm", "rtla", "rtlm", "rand", "trak",
            "Harf", "HARF", "Buzz", "BUZZ",
            "ljmo", "vjmo", "tjmo",
        };

        private static readonly string[] RefusedTables = { "kern", "kerx", "morx", "mort", "trak" };

        internal static InertGlyphs Build(FontData font)
        {
            int generation = font.Generation;
            if (NativeUnavailable) return new InertGlyphs(generation, font.Font, null, "native entry points missing");
            try
            {
                var touched = new ulong[65536 / 64];
                string refusal = Analyse(new Analysis { Face = font.Face, Font = font.Font, Touched = touched });
                return new InertGlyphs(generation, font.Font, refusal == null ? touched : null, refusal);
            }
            catch (EntryPointNotFoundException)
            {
                NativeUnavailable = true;
                return new InertGlyphs(generation, font.Font, null, "native entry points missing");
            }
        }

        /// <summary>What one analysis reads and writes.</summary>
        private sealed class Analysis
        {
            public IntPtr Face, Font;
            public ulong[] Touched;

            // Every glyph cmap gives an admitted character: a superset of every
            // glyph an answered run can contain. Built on first need.
            private ulong[] _admitted;

            public bool IsAdmitted(int glyph)
            {
                if (_admitted == null)
                {
                    _admitted = new ulong[65536 / 64];
                    IntPtr unicodes = HarfBuzzApi.hb_set_create();
                    try
                    {
                        HarfBuzzApi.hb_face_collect_unicodes(Face, unicodes);
                        uint first = uint.MaxValue, last = uint.MaxValue;
                        while (HarfBuzzApi.hb_set_next_range(unicodes, ref first, ref last) != 0)
                            for (uint cp = first; cp <= last && cp < 0x110000; cp++)
                                if (IsPlainCharacter((int)cp) &&
                                    HarfBuzzApi.hb_font_get_nominal_glyph(Font, cp, out uint g) != 0 && g <= 0xFFFF)
                                    Mark(_admitted, g);
                    }
                    finally
                    {
                        HarfBuzzApi.hb_set_destroy(unicodes);
                    }
                }
                return (_admitted[glyph >> 6] & (1UL << (glyph & 63))) != 0;
            }
        }

        private static string Analyse(Analysis a)
        {
            IntPtr face = a.Face;
            ulong[] touched = a.Touched;
            foreach (var tag in RefusedTables)
                if (TableLength(face, Tag(tag)) > 0) return $"has a '{tag}' table";

            IntPtr set = HarfBuzzApi.hb_set_create();
            try
            {
                foreach (uint table in new[] { HarfBuzzApi.HB_OT_TAG_GSUB, HarfBuzzApi.HB_OT_TAG_GPOS })
                {
                    string refusal = AnalyseTable(a, table, set);
                    if (refusal != null) return refusal;
                }
                HarfBuzzApi.hb_set_clear(set);
                HarfBuzzApi.hb_ot_layout_get_glyphs_in_class(face, HarfBuzzApi.HB_OT_LAYOUT_GLYPH_CLASS_MARK, set);
                uint first = uint.MaxValue, last = uint.MaxValue;
                while (HarfBuzzApi.hb_set_next_range(set, ref first, ref last) != 0)
                    for (uint g = first; g <= last && g <= 0xFFFF; g++) Mark(touched, g);
            }
            finally
            {
                HarfBuzzApi.hb_set_destroy(set);
            }
            return null;
        }

        private static unsafe string AnalyseTable(Analysis a, uint tableTag, IntPtr set)
        {
            IntPtr face = a.Face;
            IntPtr blob = HarfBuzzApi.hb_face_reference_table(face, tableTag);
            try
            {
                IntPtr data = HarfBuzzApi.hb_blob_get_data(blob, out uint length);
                if (length == 0 || data == IntPtr.Zero) return null;
                var t = new Reader((byte*)data, length);
                string name = HarfBuzzApi.TagToString(tableTag);
                int major = t.U16(0), minor = t.U16(2);
                if (!t.Ok || major != 1) return $"{name} version {major}.{minor}";
                int scriptList = t.U16(4), lookupList = t.U16(8);
                if (minor >= 1 && t.U32(10) != 0) return $"{name} has FeatureVariations";
                if (!t.Ok) return $"{name} header is truncated";
                if (scriptList != 0 && HasRequiredFeature(t, scriptList)) return $"{name} has a required feature";
                if (!t.Ok) return $"{name} script list is truncated";

                HarfBuzzApi.hb_set_clear(set);
                var tags = stackalloc uint[Features.Length + 1];
                for (int i = 0; i < Features.Length; i++) tags[i] = Tag(Features[i]);
                tags[Features.Length] = 0;
                HarfBuzzApi.hb_ot_layout_collect_lookups(face, tableTag, null, null, tags, set);

                if (lookupList == 0) return null;
                int lookupCount = t.U16(lookupList);
                uint first = uint.MaxValue, last = uint.MaxValue;
                while (HarfBuzzApi.hb_set_next_range(set, ref first, ref last) != 0)
                {
                    for (uint index = first; index <= last; index++)
                    {
                        if (index >= lookupCount) return $"{name} lookup {index} is out of range";
                        int lookup = lookupList + t.U16(lookupList + 2 + 2 * (int)index);
                        int type = t.U16(lookup);
                        int subtables = t.U16(lookup + 4);
                        for (int s = 0; s < subtables && t.Ok; s++)
                            AddSubtable(t, tableTag, type, lookup + t.U16(lookup + 6 + 2 * s), a);
                        if (!t.Ok) return $"{name} lookup {index} is truncated or of an unknown type";
                    }
                }
                return null;
            }
            finally
            {
                HarfBuzzApi.hb_blob_destroy(blob);
            }
        }

        private static bool HasRequiredFeature(Reader t, int scriptList)
        {
            int scripts = t.U16(scriptList);
            for (int s = 0; s < scripts && t.Ok; s++)
            {
                int script = scriptList + t.U16(scriptList + 2 + 6 * s + 4);
                int defaultLangSys = t.U16(script);
                if (defaultLangSys != 0 && t.U16(script + defaultLangSys + 2) != 0xFFFF) return true;
                int languages = t.U16(script + 2);
                for (int l = 0; l < languages && t.Ok; l++)
                {
                    int langSys = script + t.U16(script + 4 + 6 * l + 4);
                    if (t.U16(langSys + 2) != 0xFFFF) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Marks every glyph a subtable can begin a match at, plus, where the
        /// format names them, the other glyphs it acts on (bases, ligatures,
        /// second marks, every input position of a format-3 context). More than
        /// the first coverage only ever makes fewer glyphs inert.
        /// </summary>
        private static void AddSubtable(Reader t, uint tableTag, int type, int at, Analysis a)
        {
            ulong[] touched = a.Touched;
            bool gsub = tableTag == HarfBuzzApi.HB_OT_TAG_GSUB;
            int extension = gsub ? 7 : 9;
            if (type == extension)
            {
                if (t.U16(at) != 1) { t.Fail(); return; }
                int innerType = t.U16(at + 2);
                if (innerType == extension) { t.Fail(); return; }
                AddSubtable(t, tableTag, innerType, at + (int)t.U32(at + 4), a);
                return;
            }
            int contextType = gsub ? 5 : 7, chainType = gsub ? 6 : 8;
            int format = t.U16(at);
            if (type == contextType && format == 3)
            {
                int glyphCount = t.U16(at + 2);
                for (int i = 0; i < glyphCount; i++) AddCoverage(t, at + t.U16(at + 6 + 2 * i), touched);
                return;
            }
            if (type == chainType && format == 3)
            {
                int backtrack = t.U16(at + 2);
                int input = at + 4 + 2 * backtrack;
                int inputCount = t.U16(input);
                for (int i = 0; i < inputCount; i++) AddCoverage(t, at + t.U16(input + 2 + 2 * i), touched);
                return;
            }
            if (type < 1 || type > 8 || format < 1 || format > 3) { t.Fail(); return; }
            if (gsub && type == 4 && format == 1)
            {
                AddLigatures(t, at, a);
                return;
            }
            AddCoverage(t, at + t.U16(at + 2), touched);
            // GPOS 4/5/6: the second coverage (bases, ligatures, mark2s).
            if (!gsub && type >= 4 && type <= 6) AddCoverage(t, at + t.U16(at + 4), touched);
        }

        /// <summary>
        /// A ligature substitution is the one lookup that is let off: its first
        /// glyph is left inert when every ligature it begins needs, somewhere
        /// among its components, a glyph that no admitted character maps to.
        /// Components are matched by glyph id against the glyphs that follow,
        /// and in an answered run every glyph is one an admitted character
        /// mapped to, so such a ligature cannot complete there. This is what
        /// keeps Noto Sans's '0' (zero + U+FE00 is its slashed form, in
        /// <c>ccmp</c>) a plain digit. A ligature of one component, or one
        /// whose components could all occur, marks the glyph like any lookup.
        /// </summary>
        private static void AddLigatures(Reader t, int at, Analysis a)
        {
            int coverage = at + t.U16(at + 2);
            int sets = t.U16(at + 4);
            int format = t.U16(coverage);
            int count = t.U16(coverage + 2);
            for (int i = 0; i < count && t.Ok; i++)
            {
                if (format == 1)
                {
                    AddLigatureSet(t, at, sets, i, t.U16(coverage + 4 + 2 * i), a);
                }
                else if (format == 2)
                {
                    int startGlyph = t.U16(coverage + 4 + 6 * i), endGlyph = t.U16(coverage + 6 + 6 * i);
                    int startIndex = t.U16(coverage + 8 + 6 * i);
                    for (int g = startGlyph; g <= endGlyph && t.Ok; g++)
                        AddLigatureSet(t, at, sets, startIndex + g - startGlyph, g, a);
                }
                else t.Fail();
            }
        }

        private static void AddLigatureSet(Reader t, int at, int sets, int index, int glyph, Analysis a)
        {
            if (index >= sets) { Mark(a.Touched, (uint)glyph); return; }
            int set = at + t.U16(at + 6 + 2 * index);
            int ligatures = t.U16(set);
            for (int l = 0; l < ligatures && t.Ok; l++)
            {
                int ligature = set + t.U16(set + 2 + 2 * l);
                int components = t.U16(ligature + 2);
                bool blocked = false;
                for (int c = 1; c < components && !blocked && t.Ok; c++)
                    blocked = !a.IsAdmitted(t.U16(ligature + 4 + 2 * (c - 1)));
                if (!blocked)
                {
                    Mark(a.Touched, (uint)glyph);
                    return;
                }
            }
        }

        private static void AddCoverage(Reader t, int at, ulong[] touched)
        {
            int format = t.U16(at);
            int count = t.U16(at + 2);
            if (format == 1)
            {
                for (int i = 0; i < count && t.Ok; i++) Mark(touched, (uint)t.U16(at + 4 + 2 * i));
            }
            else if (format == 2)
            {
                for (int i = 0; i < count && t.Ok; i++)
                {
                    int startGlyph = t.U16(at + 4 + 6 * i), endGlyph = t.U16(at + 6 + 6 * i);
                    for (int g = startGlyph; g <= endGlyph; g++) Mark(touched, (uint)g);
                }
            }
            else t.Fail();
        }

        private static void Mark(ulong[] touched, uint glyph) =>
            touched[glyph >> 6] |= 1UL << (int)(glyph & 63);

        private static long TableLength(IntPtr face, uint tag)
        {
            IntPtr blob = HarfBuzzApi.hb_face_reference_table(face, tag);
            try { return HarfBuzzApi.hb_blob_get_length(blob); }
            finally { HarfBuzzApi.hb_blob_destroy(blob); }
        }

        private static uint Tag(string tag) =>
            ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

        /// <summary>Big-endian reads that latch a failure instead of reading past the table.</summary>
        private unsafe sealed class Reader
        {
            private readonly byte* _data;
            private readonly uint _length;
            public bool Ok = true;

            public Reader(byte* data, uint length)
            {
                _data = data;
                _length = length;
            }

            public void Fail() => Ok = false;

            public int U16(int at)
            {
                if (at < 0 || (uint)at + 2 > _length) { Ok = false; return 0; }
                return (_data[at] << 8) | _data[at + 1];
            }

            public uint U32(int at)
            {
                if (at < 0 || (uint)at + 4 > _length) { Ok = false; return 0; }
                return ((uint)_data[at] << 24) | ((uint)_data[at + 1] << 16) | ((uint)_data[at + 2] << 8) | _data[at + 3];
            }
        }
    }
}
