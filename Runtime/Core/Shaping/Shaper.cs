using System;
using System.Collections.Generic;
using OneText.Native;

namespace OneText
{
    /// <summary>
    /// OpenType shaping via HarfBuzz: turns a string plus a font into
    /// positioned glyphs (GSUB/GPOS — ligatures, contextual forms, kerning,
    /// mark positioning).
    ///
    /// M1 scope: one run, script/direction guessed from content. Script
    /// itemization and BiDi will feed runs into this in M3.
    /// </summary>
    public sealed class Shaper : IDisposable
    {
        private IntPtr _buffer;

        public Shaper()
        {
            _buffer = HarfBuzzApi.hb_buffer_create();
        }

        public static string HarfBuzzVersion
        {
            get
            {
                unsafe
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
                        HarfBuzzApi.hb_version_string());
                }
            }
        }

        /// <summary>Run direction for shaping. Auto lets HarfBuzz guess from content.</summary>
        public enum Direction { Auto, LeftToRight, RightToLeft }

        /// <summary>
        /// Shape a substring of <paramref name="text"/> with <paramref name="font"/>,
        /// appending glyphs in visual order to <paramref name="output"/>.
        /// Direction should come from BiDi level parity when shaping runs.
        /// </summary>
        public void Shape(FontData font, string text, int start, int length,
            Direction direction, List<ShapedGlyph> output) =>
            Shape(font, text, start, length, direction, output, null);

        /// <summary>
        /// Same, with a BCP 47 language tag.
        ///
        /// The tag is what drives OpenType's <c>locl</c> feature, and
        /// <c>locl</c> is how one Han codepoint draws differently for a
        /// Japanese reader and a Chinese one. Passing it is the difference
        /// between a font that <em>can</em> do the right thing and one that
        /// does.
        /// </summary>
        public void Shape(FontData font, string text, int start, int length,
            Direction direction, List<ShapedGlyph> output, string language)
        {
            if (font == null || !font.IsValid) throw new ArgumentException("Invalid font.", nameof(font));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrEmpty(text) || length <= 0) return;

            HarfBuzzApi.hb_buffer_reset(_buffer);
            unsafe
            {
                fixed (char* p = text)
                {
                    HarfBuzzApi.hb_buffer_add_utf16(_buffer, (ushort*)p, text.Length, (uint)start, length);
                }
                HarfBuzzApi.hb_buffer_guess_segment_properties(_buffer);
                if (!string.IsNullOrEmpty(language))
                    HarfBuzzApi.hb_buffer_set_language(_buffer, LanguageOf(language));
                if (direction == Direction.LeftToRight)
                    HarfBuzzApi.hb_buffer_set_direction(_buffer, HarfBuzzApi.HB_DIRECTION_LTR);
                else if (direction == Direction.RightToLeft)
                    HarfBuzzApi.hb_buffer_set_direction(_buffer, HarfBuzzApi.HB_DIRECTION_RTL);
                HarfBuzzApi.hb_shape(font.Font, _buffer, IntPtr.Zero, 0);

                HBGlyphInfo* infos = HarfBuzzApi.hb_buffer_get_glyph_infos(_buffer, out uint count);
                HBGlyphPosition* positions = HarfBuzzApi.hb_buffer_get_glyph_positions(_buffer, out _);

                for (uint i = 0; i < count; i++)
                {
                    output.Add(new ShapedGlyph(
                        infos[i].Codepoint, (int)infos[i].Cluster,
                        positions[i].XAdvance, positions[i].YAdvance,
                        positions[i].XOffset, positions[i].YOffset));
                }
            }
        }

        /// <summary>Shape an entire string as a single auto-direction run.</summary>
        public void Shape(FontData font, string text, List<ShapedGlyph> output) =>
            Shape(font, text, 0, text?.Length ?? 0, Direction.Auto, output);

        /// <summary>
        /// Interned <c>hb_language_t</c> for a tag. HarfBuzz interns these
        /// itself and never frees them, so caching is about avoiding the string
        /// marshal on every run rather than about ownership.
        /// </summary>
        private static IntPtr LanguageOf(string tag)
        {
            if (s_languages.TryGetValue(tag, out var cached)) return cached;
            var language = HarfBuzzApi.hb_language_from_string(tag, -1);
            s_languages[tag] = language;
            return language;
        }

        private static readonly Dictionary<string, IntPtr> s_languages =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (_buffer != IntPtr.Zero)
            {
                HarfBuzzApi.hb_buffer_destroy(_buffer);
                _buffer = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~Shaper()
        {
            Dispose();
        }
    }
}
