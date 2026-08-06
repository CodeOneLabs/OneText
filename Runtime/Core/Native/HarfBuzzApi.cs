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
        // executable. The iOS binary in this package is a dynamic framework
        // (the NuGet family the other platforms come from ships no static build),
        // and a dynamic library is resolved by name like any other.
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
        //
        // Web is the exception, and it is the one place `__Internal` is right.
        // There is no library to find at runtime: Emscripten links
        // `Runtime/Plugins/WebGL/libHarfBuzzSharp.a` into the player itself, so
        // the hb_* symbols are already in the module before any managed code
        // runs. `__Internal` is how il2cpp is told to call them directly rather
        // than to go looking for a file that a browser has no way to open.
        // The editor is excluded because in play mode the editor is macOS,
        // Windows or Linux and loads the dynamic library like anywhere else;
        // UNITY_WEBGL is defined there too whenever Web is the active target.
#if UNITY_WEBGL && !UNITY_EDITOR
        private const string Lib = "__Internal";

        /// <summary>
        /// Web is also the one platform where the symbols are not called
        /// `hb_*`. Unity's own TextRenderingModule statically links a HarfBuzz
        /// of its own (8.0.1 in editor 6000.0.77f1) into every Web player,
        /// and two HarfBuzzes in one link is 591 duplicate symbols and no
        /// build. Ours is compiled with every HarfBuzz identifier renamed, so
        /// the two can sit side by side: Unity keeps `hb_shape`, we get
        /// `onetext_hb_shape`. Tools/build_webgl_natives.sh does the renaming
        /// and refuses to write an archive that still collides.
        ///
        /// This is why every DllImport below names its EntryPoint explicitly.
        /// On the other nine platforms the prefix is empty and each attribute
        /// names exactly the symbol the method name already bound to, so
        /// nothing about them changes.
        /// </summary>
        private const string HbPrefix = "onetext_";
#else
        private const string Lib = "libHarfBuzzSharp";
        private const string HbPrefix = "";
#endif
        internal const int HB_MEMORY_MODE_READONLY = 1;

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_version_string",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_version_string();

        // --- blob / face / font ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_blob_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_blob_create(IntPtr data, uint length, int memoryMode, IntPtr userData, IntPtr destroyFunc);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_blob_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_blob_destroy(IntPtr blob);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_face_create(IntPtr blob, uint index);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_face_destroy(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_get_upem",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_face_get_upem(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_make_immutable",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_face_make_immutable(IntPtr face);

        // --- layout tables, for diagnostics only ---

        /// <summary>
        /// Feature tags in GSUB or GPOS. <paramref name="count"/> is in/out, as
        /// the variation-axis call is; passing a zero count reports how many
        /// there are without writing any.
        /// </summary>
        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_layout_table_get_feature_tags",
            CallingConvention = CallingConvention.Cdecl)]
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

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_has_png",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_png(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_has_layers",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_layers(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_has_palettes",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_color_has_palettes(IntPtr face);

        /// <summary>Returns a blob owning a PNG image; destroy it when done.</summary>
        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_glyph_reference_png",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_ot_color_glyph_reference_png(IntPtr font, uint glyph);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_blob_get_data",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_blob_get_length",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_blob_get_length(IntPtr blob);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_glyph_get_layers",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_color_glyph_get_layers(IntPtr face, uint glyph,
            uint startOffset, ref uint count, HBOtColorLayer* layers);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_palette_get_colors",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_color_palette_get_colors(IntPtr face,
            uint paletteIndex, uint startOffset, ref uint count, uint* colors);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_color_palette_get_count",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_ot_color_palette_get_count(IntPtr face);

        // --- subsetting (harfbuzz-subset; a separate library in HarfBuzz's
        //     build, and the reason every platform binary has to be checked
        //     for it rather than assumed to have it) ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_input_create_or_fail",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_input_create_or_fail();

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_input_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_subset_input_destroy(IntPtr input);

        /// <summary>The set of Unicode codepoints to keep. Owned by the input.</summary>
        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_input_unicode_set",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_input_unicode_set(IntPtr input);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_input_get_flags",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_subset_input_get_flags(IntPtr input);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_input_set_flags",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_subset_input_set_flags(IntPtr input, uint flags);

        /// <summary>Returns a new face; destroy it with hb_face_destroy.</summary>
        [DllImport(Lib, EntryPoint = HbPrefix + "hb_subset_or_fail",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_subset_or_fail(IntPtr source, IntPtr input);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_reference_blob",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_face_reference_blob(IntPtr face);

        // --- sets ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_set_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_set_create();

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_set_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_destroy(IntPtr set);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_set_add",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_add(IntPtr set, uint codepoint);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_set_add_range",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_set_add_range(IntPtr set, uint first, uint last);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_face_is_immutable",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_face_is_immutable(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_font_create(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_font_destroy(IntPtr font);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_get_h_extents",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_h_extents(IntPtr font, out HBFontExtents extents);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_get_nominal_glyph",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_nominal_glyph(IntPtr font, uint unicode, out uint glyph);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_get_glyph_extents",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_font_get_glyph_extents(IntPtr font, uint glyph,
            out HBGlyphExtents extents);

        // --- metrics outside the line box ---

        /// <summary>
        /// One OpenType metric in font units, synthesized when the table that
        /// should carry it does not. The "_with_fallback" half matters: the
        /// four metrics read through here live in <c>post</c> and <c>OS/2</c>,
        /// both of which a subset or a bitmap-only face may omit, and a zero
        /// underline thickness draws nothing at all. There is no return value
        /// to check because there is no failure: something always comes back.
        /// </summary>
        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_metrics_get_position_with_fallback",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_ot_metrics_get_position_with_fallback(IntPtr font,
            uint metricsTag, out int position);

        /// <summary>
        /// <c>hb_ot_metrics_tag_t</c>, in the same packed-four-character form
        /// as every other OpenType tag. An offset is measured from the baseline
        /// and names the top of the stroke; a size is its thickness.
        /// </summary>
        internal const uint HB_OT_METRICS_TAG_UNDERLINE_SIZE =
            ('u' << 24) | ('n' << 16) | ('d' << 8) | 's';

        internal const uint HB_OT_METRICS_TAG_UNDERLINE_OFFSET =
            ('u' << 24) | ('n' << 16) | ('d' << 8) | 'o';

        internal const uint HB_OT_METRICS_TAG_STRIKEOUT_SIZE =
            ('s' << 24) | ('t' << 16) | ('r' << 8) | 's';

        internal const uint HB_OT_METRICS_TAG_STRIKEOUT_OFFSET =
            ('s' << 24) | ('t' << 16) | ('r' << 8) | 'o';

        // --- variable fonts ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_var_has_data",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hb_ot_var_has_data(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_var_get_axis_count",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint hb_ot_var_get_axis_count(IntPtr face);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_ot_var_get_axis_infos",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint hb_ot_var_get_axis_infos(IntPtr face, uint startOffset,
            ref uint axesCount, HBVarAxisInfo* axesArray);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_set_variations",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void hb_font_set_variations(IntPtr font,
            HBVariation* variations, uint variationsLength);

        // --- buffer / shaping ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_buffer_create();

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_destroy(IntPtr buffer);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_reset",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_reset(IntPtr buffer);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_add_utf16",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void hb_buffer_add_utf16(IntPtr buffer, ushort* text, int textLength, uint itemOffset, int itemLength);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_guess_segment_properties",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_guess_segment_properties(IntPtr buffer);

        // --- language (BCP 47) ---

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_language_from_string",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_language_from_string(
            [MarshalAs(UnmanagedType.LPStr)] string str, int length);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_language_to_string",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_language_to_string(IntPtr language);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_set_language",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_set_language(IntPtr buffer, IntPtr language);

        internal const int HB_DIRECTION_LTR = 4;
        internal const int HB_DIRECTION_RTL = 5;

        /// <summary>
        /// Vertical, top to bottom. Setting it is the whole of the shaping
        /// side of 縦書き: HarfBuzz turns on <c>vert</c> and answers with
        /// <c>vmtx</c>/<c>VORG</c> metrics, so the vertical forms and the
        /// vertical advances both arrive without another entry point.
        /// </summary>
        internal const int HB_DIRECTION_TTB = 6;

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_set_direction",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_buffer_set_direction(IntPtr buffer, int direction);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_shape",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_shape(IntPtr font, IntPtr buffer, IntPtr features, uint numFeatures);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_get_glyph_infos",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe HBGlyphInfo* hb_buffer_get_glyph_infos(IntPtr buffer, out uint length);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_buffer_get_glyph_positions",
            CallingConvention = CallingConvention.Cdecl)]
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

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hb_draw_funcs_create();

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_destroy(IntPtr dfuncs);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_set_move_to_func",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_move_to_func(IntPtr dfuncs, DrawMoveToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_set_line_to_func",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_line_to_func(IntPtr dfuncs, DrawLineToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_set_quadratic_to_func",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_quadratic_to_func(IntPtr dfuncs, DrawQuadraticToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_set_cubic_to_func",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_cubic_to_func(IntPtr dfuncs, DrawCubicToFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_draw_funcs_set_close_path_func",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hb_draw_funcs_set_close_path_func(IntPtr dfuncs, DrawClosePathFunc func, IntPtr userData, IntPtr destroy);

        [DllImport(Lib, EntryPoint = HbPrefix + "hb_font_draw_glyph",
            CallingConvention = CallingConvention.Cdecl)]
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
        /// Index into the CPAL palette, or 0xFFFF meaning "the text colour",
        /// which is how a COLR font says a layer should follow the label rather
        /// than the palette.
        /// </summary>
        public uint ColorIndex;
    }
}
