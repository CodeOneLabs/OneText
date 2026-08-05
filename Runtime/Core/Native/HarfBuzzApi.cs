using System;
using System.Runtime.InteropServices;

namespace OneText.Native
{
    /// <summary>
    /// Raw P/Invoke surface of libHarfBuzzSharp (plain HarfBuzz C API).
    /// Keep this file mechanical: one extern per hb_* entry point, no logic.
    /// </summary>
    internal static class HarfBuzzApi
    {
        // The `lib` is part of the name, not a prefix the loader will supply.
        //
        // Two mistakes are baked into this one line, and both were made here.
        // The first: `__Internal`, which names a library linked into the
        // executable. The iOS binary in this package is a dynamic framework —
        // the NuGet family the other platforms come from ships no static build
        // — and a dynamic library is resolved by name like any other.
        //
        // The second: `HarfBuzzSharp`, on the theory that every loader tries
        // `lib` + name + extension. POSIX does (il2cpp's Posix LibraryLoader
        // walks prefix/suffix variations, which is why macOS, Linux and Android
        // all worked). Windows does not: its ProbeForLibrary opens the name it
        // was given and nothing else, so `HarfBuzzSharp` looks for
        // `HarfBuzzSharp.dll` and never finds `libHarfBuzzSharp.dll`. That is a
        // DllNotFoundException in the Windows editor and in both player
        // scripting backends, on a package that compiled perfectly on macOS.
        //
        // Spelling the `lib` out satisfies every platform: Windows opens the
        // file by that exact name, and POSIX finds `libHarfBuzzSharp.dylib` /
        // `.so` through the same variation walk. It is also what SkiaSharp
        // itself does, for exactly this reason.
        private const string Lib = "libHarfBuzzSharp";
        internal const int HB_MEMORY_MODE_READONLY = 1;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_version_string();

        // --- blob / face / font ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_blob_create(IntPtr data, uint length, int memoryMode, IntPtr userData, IntPtr destroyFunc);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_blob_destroy(IntPtr blob);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_face_create(IntPtr blob, uint index);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_face_destroy(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_face_get_upem(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_face_make_immutable(IntPtr face);

        // --- layout tables, for diagnostics only ---

        /// <summary>
        /// Feature tags in GSUB or GPOS. <paramref name="count"/> is in/out, as
        /// the variation-axis call is; passing a zero count reports how many
        /// there are without writing any.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_ot_layout_table_get_feature_tags(IntPtr face, uint tableTag,
            uint startOffset, ref uint count, [Out] uint[] tags);

        /// <summary>OpenType table tags, in HarfBuzz's packed-four-character form.</summary>
        internal const uint HB_OT_TAG_GSUB = ('G' << 24) | ('S' << 16) | ('U' << 8) | 'B';
        internal const uint HB_OT_TAG_GPOS = ('G' << 24) | ('P' << 16) | ('O' << 8) | 'S';

        /// <summary>A packed tag back to its four characters.</summary>
        internal static string TagToString(uint tag) => new string(new[]
        {
            (char)((tag >> 24) & 0xFF), (char)((tag >> 16) & 0xFF),
            (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF),
        });

        // --- colour glyphs (CBDT/sbix bitmaps, COLRv0 layers, CPAL palettes) ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_png(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_layers(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_palettes(IntPtr face);

        /// <summary>Returns a blob owning a PNG image; destroy it when done.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_ot_color_glyph_reference_png(IntPtr font, uint glyph);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_blob_get_length(IntPtr blob);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_color_glyph_get_layers(IntPtr face, uint glyph,
            uint startOffset, ref uint count, HBOtColorLayer* layers);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_color_palette_get_colors(IntPtr face,
            uint paletteIndex, uint startOffset, ref uint count, uint* colors);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_ot_color_palette_get_count(IntPtr face);

        // --- subsetting (harfbuzz-subset; a separate library in HarfBuzz's
        //     build, and the reason every platform binary has to be checked
        //     for it rather than assumed to have it) ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_input_create_or_fail();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_subset_input_destroy(IntPtr input);

        /// <summary>The set of Unicode codepoints to keep. Owned by the input.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_input_unicode_set(IntPtr input);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_subset_input_get_flags(IntPtr input);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_subset_input_set_flags(IntPtr input, uint flags);

        /// <summary>Returns a new face; destroy it with hb_face_destroy.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_or_fail(IntPtr source, IntPtr input);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_face_reference_blob(IntPtr face);

        // --- sets ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_set_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_destroy(IntPtr set);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_add(IntPtr set, uint codepoint);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_add_range(IntPtr set, uint first, uint last);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_face_is_immutable(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_font_create(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_font_destroy(IntPtr font);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_h_extents(IntPtr font, out HBFontExtents extents);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_nominal_glyph(IntPtr font, uint unicode, out uint glyph);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_glyph_extents(IntPtr font, uint glyph,
            out HBGlyphExtents extents);

        // --- variable fonts ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_var_has_data(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_ot_var_get_axis_count(IntPtr face);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_var_get_axis_infos(IntPtr face, uint startOffset,
            ref uint axesCount, HBVarAxisInfo* axesArray);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void hb_font_set_variations(IntPtr font,
            HBVariation* variations, uint variationsLength);

        // --- buffer / shaping ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_buffer_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_destroy(IntPtr buffer);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_reset(IntPtr buffer);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void hb_buffer_add_utf16(IntPtr buffer, ushort* text, int textLength, uint itemOffset, int itemLength);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_guess_segment_properties(IntPtr buffer);

        // --- language (BCP 47) ---

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_language_from_string(
            [MarshalAs(UnmanagedType.LPStr)] string str, int length);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_language_to_string(IntPtr language);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_set_language(IntPtr buffer, IntPtr language);

        internal const int HB_DIRECTION_LTR = 4;
        internal const int HB_DIRECTION_RTL = 5;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_set_direction(IntPtr buffer, int direction);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_shape(IntPtr font, IntPtr buffer, IntPtr features, uint numFeatures);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe HBGlyphInfo* hb_buffer_get_glyph_infos(IntPtr buffer, out uint length);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe HBGlyphPosition* hb_buffer_get_glyph_positions(IntPtr buffer, out uint length);

        // --- draw (outline extraction) ---

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DrawMoveToFunc(IntPtr dfuncs, IntPtr drawData, IntPtr drawState, float toX, float toY, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DrawLineToFunc(IntPtr dfuncs, IntPtr drawData, IntPtr drawState, float toX, float toY, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DrawQuadraticToFunc(IntPtr dfuncs, IntPtr drawData, IntPtr drawState, float controlX, float controlY, float toX, float toY, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DrawCubicToFunc(IntPtr dfuncs, IntPtr drawData, IntPtr drawState, float control1X, float control1Y, float control2X, float control2Y, float toX, float toY, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DrawClosePathFunc(IntPtr dfuncs, IntPtr drawData, IntPtr drawState, IntPtr userData);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_draw_funcs_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_destroy(IntPtr dfuncs);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_move_to_func(IntPtr dfuncs, DrawMoveToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_line_to_func(IntPtr dfuncs, DrawLineToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_quadratic_to_func(IntPtr dfuncs, DrawQuadraticToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_cubic_to_func(IntPtr dfuncs, DrawCubicToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_close_path_func(IntPtr dfuncs, DrawClosePathFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_font_draw_glyph(IntPtr font, uint glyph, IntPtr dfuncs, IntPtr drawData);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HBGlyphInfo
    {
        public uint Codepoint; // glyph id after shaping
        public uint Mask;
        public uint Cluster;   // UTF-16 code-unit index into the source string
        public uint Var1;
        public uint Var2;
    }

    /// <summary>
    /// A glyph's ink box. Y grows upward, so <see cref="Height"/> is negative:
    /// the box runs from the bearing downward.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HBGlyphExtents
    {
        public int XBearing, YBearing, Width, Height;
    }

    /// <summary>hb_font_extents_t: three metrics plus nine private fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HBFontExtents
    {
        public int Ascender;
        public int Descender;
        public int LineGap;
        private int _reserved9, _reserved8, _reserved7, _reserved6, _reserved5;
        private int _reserved4, _reserved3, _reserved2, _reserved1;
    }

    /// <summary>hb_variation_t: one variation axis setting.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HBVariation
    {
        public uint Tag;
        public float Value;
    }

    /// <summary>hb_ot_var_axis_info_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HBVarAxisInfo
    {
        public uint AxisIndex;
        public uint Tag;
        public uint NameId;
        public uint Flags;
        public float MinValue;
        public float DefaultValue;
        public float MaxValue;
        private uint _reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HBGlyphPosition
    {
        public int XAdvance; // font units
        public int YAdvance;
        public int XOffset;
        public int YOffset;
        public uint Var;
    }

    /// <summary>One COLRv0 layer: a glyph to draw and which palette entry to draw it in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HBOtColorLayer
    {
        public uint Glyph;

        /// <summary>
        /// Index into the CPAL palette, or 0xFFFF meaning "the text colour" —
        /// which is how a COLR font says a layer should follow the label rather
        /// than the palette.
        /// </summary>
        public uint ColorIndex;
    }
}
