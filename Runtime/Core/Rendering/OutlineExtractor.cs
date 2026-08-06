using System;
using System.Collections.Generic;
using AOT;
using OneText.Native;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A glyph outline as flattened polyline contours, in font design units,
    /// y-up, origin at the glyph's own origin.
    /// </summary>
    public sealed class GlyphOutline
    {
        public readonly List<List<Vector2>> Contours = new List<List<Vector2>>();

        // Cleared outlines hand their point lists back rather than dropping
        // them: extraction runs once per glyph per frame in a churning atlas,
        // and a contour list per contour is the kind of allocation that only
        // shows up as a collection pause much later.
        private readonly List<List<Vector2>> _spare = new List<List<Vector2>>();

        /// <summary>Starts a contour, reusing a retired point list if there is one.</summary>
        public List<Vector2> BeginContour()
        {
            List<Vector2> contour;
            if (_spare.Count > 0)
            {
                contour = _spare[_spare.Count - 1];
                _spare.RemoveAt(_spare.Count - 1);
                contour.Clear();
            }
            else
            {
                contour = new List<Vector2>();
            }
            Contours.Add(contour);
            return contour;
        }

        public void Clear()
        {
            foreach (var contour in Contours) _spare.Add(contour);
            Contours.Clear();
        }
    }

    /// <summary>
    /// Extracts glyph outlines through HarfBuzz's draw API (hb-draw) and
    /// flattens Béziers into polylines. No FreeType needed for unhinted
    /// outlines of TrueType/CFF fonts.
    ///
    /// Safe to call from several threads at once, provided each thread passes a
    /// <see cref="FontData"/> of its own (see <c>FontData.ForCurrentThread</c>):
    /// the callback state below is per thread, and the draw-funcs object is
    /// written once and only read after that. Rasterization is a separate
    /// question: it writes into the atlas, which is main-thread.
    /// </summary>
    public static class OutlineExtractor
    {
        /// <summary>
        /// Deviation a flattened curve may show from the true Bézier, in pixels
        /// at the density it will be rasterized.
        ///
        /// The field stores 8 pixels of distance in 8 bits, so one level is
        /// 8/255 px and this tolerance is about 1.6 of them, chosen against
        /// the quantization step rather than tuned by eye. Measured against as
        /// dense a flattening as the extractor will produce, glyphs come out
        /// within 3 levels at worst and about 1 on average, for TrueType's
        /// quadratics and CFF's cubics alike (`OutlineFormatTests`).
        ///
        /// It replaced a fixed 8 segments per curve regardless of size, and
        /// halved what a chat-stream frame spends rasterizing. Where the two
        /// disagree by up to 24 levels, this is the one that is right: the
        /// fixed subdivision was the approximation. Loosening to 0.1 px is
        /// another ~15% faster and lands past 3 levels, which is the wrong
        /// side of the trade for text.
        /// </summary>
        public static float FlatnessPixels
        {
            get => _flatnessPixels;
            set
            {
                if (value == _flatnessPixels) return;
                _flatnessPixels = value;
                Generation++;
            }
        }

        /// <summary>
        /// Counts changes to the settings that decide an outline's shape.
        ///
        /// Tiles baked at one tolerance and tiles baked at another are not
        /// interchangeable, so the atlas keys entries by this: raising the
        /// tolerance mid-session leaves the old tiles to age out of the cache
        /// instead of mixing two flattenings in one atlas.
        /// </summary>
        public static int Generation { get; private set; }

        private static float _flatnessPixels = 0.05f;

        private const int MaxCurveSegments = 24;

        /// <summary>
        /// Segments per curve when the caller does not say how dense the
        /// rasterization will be (ink bounds, editor tooling).
        /// </summary>
        private const int DefaultCurveSegments = 8;

        // The hb-draw callbacks are static functions with no user-data pointer
        // wired up, so the outline being built has to live somewhere static;
        // and static, here, means per thread. Two threads extracting two glyphs
        // through one set of these fields produce one glyph made of both, and
        // the symptom is a garbled letter rather than a crash.
        [ThreadStatic] private static float t_pixelsPerUnit; // 0 = caller did not say
        [ThreadStatic] private static GlyphOutline t_current;
        [ThreadStatic] private static List<Vector2> t_contour;
        [ThreadStatic] private static Vector2 t_pen;

        // The draw-funcs object is shared: it is written once and only read
        // afterwards, which is the same bargain HarfBuzz strikes for an
        // immutable face.
        private static IntPtr s_dfuncs;
        private static readonly object s_dfuncsSync = new object();

        // Delegates must be rooted so IL2CPP/Mono never collects the thunks.
        private static readonly HarfBuzzApi.DrawMoveToFunc s_moveTo = MoveTo;
        private static readonly HarfBuzzApi.DrawLineToFunc s_lineTo = LineTo;
        private static readonly HarfBuzzApi.DrawQuadraticToFunc s_quadTo = QuadTo;
        private static readonly HarfBuzzApi.DrawCubicToFunc s_cubicTo = CubicTo;
        private static readonly HarfBuzzApi.DrawClosePathFunc s_close = ClosePath;

        /// <summary>
        /// Flattens a glyph's outline. <paramref name="pixelsPerUnit"/> is the
        /// density the result will be rasterized at: curves are then split only
        /// as finely as that resolution can show, which at UI sizes is a
        /// fraction of the fixed subdivision it replaces, and the segment count
        /// is what the distance-field inner loop costs.
        /// </summary>
        public static void Extract(FontData font, uint glyphId, GlyphOutline output,
            float pixelsPerUnit = 0f)
        {
            if (font == null || !font.IsValid) throw new ArgumentException("Invalid font.", nameof(font));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var dfuncs = EnsureDrawFuncs();

            t_current = output;
            t_contour = null;
            t_pixelsPerUnit = pixelsPerUnit;
            HarfBuzzApi.hb_font_draw_glyph(font.Font, glyphId, dfuncs, IntPtr.Zero);
            t_current = null;
            t_contour = null;
            t_pixelsPerUnit = 0f;
        }

        private static IntPtr EnsureDrawFuncs()
        {
            // Paired with the Volatile.Write below: the acquire side is what
            // stops a thread reading the pointer before the callback writes
            // that configured it.
            var existing = System.Threading.Volatile.Read(ref s_dfuncs);
            if (existing != IntPtr.Zero) return existing;

            lock (s_dfuncsSync)
            {
                if (s_dfuncs != IntPtr.Zero) return s_dfuncs;
                var dfuncs = HarfBuzzApi.hb_draw_funcs_create();
                HarfBuzzApi.hb_draw_funcs_set_move_to_func(dfuncs, s_moveTo, IntPtr.Zero, IntPtr.Zero);
                HarfBuzzApi.hb_draw_funcs_set_line_to_func(dfuncs, s_lineTo, IntPtr.Zero, IntPtr.Zero);
                HarfBuzzApi.hb_draw_funcs_set_quadratic_to_func(dfuncs, s_quadTo, IntPtr.Zero, IntPtr.Zero);
                HarfBuzzApi.hb_draw_funcs_set_cubic_to_func(dfuncs, s_cubicTo, IntPtr.Zero, IntPtr.Zero);
                HarfBuzzApi.hb_draw_funcs_set_close_path_func(dfuncs, s_close, IntPtr.Zero, IntPtr.Zero);
                // Published only once fully built: another thread must never see
                // a half-configured object through the unlocked fast path above.
                System.Threading.Volatile.Write(ref s_dfuncs, dfuncs);
                return dfuncs;
            }
        }

        [MonoPInvokeCallback(typeof(HarfBuzzApi.DrawMoveToFunc))]
        private static void MoveTo(IntPtr d, IntPtr dd, IntPtr st, float x, float y, IntPtr u)
        {
            t_contour = t_current.BeginContour();
            t_pen = new Vector2(x, y);
            t_contour.Add(t_pen);
        }

        [MonoPInvokeCallback(typeof(HarfBuzzApi.DrawLineToFunc))]
        private static void LineTo(IntPtr d, IntPtr dd, IntPtr st, float x, float y, IntPtr u)
        {
            t_pen = new Vector2(x, y);
            t_contour.Add(t_pen);
        }

        [MonoPInvokeCallback(typeof(HarfBuzzApi.DrawQuadraticToFunc))]
        private static void QuadTo(IntPtr d, IntPtr dd, IntPtr st, float cx, float cy, float x, float y, IntPtr u)
        {
            var p0 = t_pen;
            var c = new Vector2(cx, cy);
            var p1 = new Vector2(x, y);
            // Chord error of a quadratic split into n pieces is |P0-2C+P1|/(8n²).
            int steps = StepsFor((p0 - 2f * c + p1).magnitude / 8f);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float s = 1f - t;
                t_contour.Add(s * s * p0 + 2f * s * t * c + t * t * p1);
            }
            t_pen = p1;
        }

        [MonoPInvokeCallback(typeof(HarfBuzzApi.DrawCubicToFunc))]
        private static void CubicTo(IntPtr d, IntPtr dd, IntPtr st, float c1x, float c1y, float c2x, float c2y, float x, float y, IntPtr u)
        {
            var p0 = t_pen;
            var c1 = new Vector2(c1x, c1y);
            var c2 = new Vector2(c2x, c2y);
            var p1 = new Vector2(x, y);
            // Same bound taken over both second differences of a cubic.
            float second = Mathf.Max((p0 - 2f * c1 + c2).magnitude, (c1 - 2f * c2 + p1).magnitude);
            int steps = StepsFor(second * 3f / 8f);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float s = 1f - t;
                t_contour.Add(s * s * s * p0 + 3f * s * s * t * c1 + 3f * s * t * t * c2 + t * t * t * p1);
            }
            t_pen = p1;
        }

        /// <summary>
        /// Segments needed so the flattening error stays under
        /// <see cref="FlatnessPixels"/>, given the curve's error coefficient in
        /// font units (error = coefficient / n²).
        /// </summary>
        private static int StepsFor(float errorUnits)
        {
            if (t_pixelsPerUnit <= 0f) return DefaultCurveSegments;
            float errorPixels = errorUnits * t_pixelsPerUnit;
            if (errorPixels <= _flatnessPixels) return 1;
            int steps = Mathf.CeilToInt(Mathf.Sqrt(errorPixels / _flatnessPixels));
            return Mathf.Clamp(steps, 1, MaxCurveSegments);
        }

        [MonoPInvokeCallback(typeof(HarfBuzzApi.DrawClosePathFunc))]
        private static void ClosePath(IntPtr d, IntPtr dd, IntPtr st, IntPtr u)
        {
            if (t_contour != null && t_contour.Count > 0)
                t_contour.Add(t_contour[0]);
        }
    }
}
