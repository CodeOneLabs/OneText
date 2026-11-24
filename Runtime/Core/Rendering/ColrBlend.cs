using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The twenty-eight ways a COLR version 1 <c>PaintComposite</c> can combine
    /// two pictures: the Porter-Duff operators, and the separable and
    /// non-separable blend modes of the W3C Compositing and Blending
    /// specification, which is where the format takes its definitions from.
    ///
    /// Both planes are premultiplied RGBA; the result replaces the backdrop.
    /// </summary>
    internal static class ColrBlend
    {
        public static void Composite(float[] source, float[] backdrop, int texels, int mode)
        {
            for (int c = 0, end = texels * 4; c < end; c += 4)
            {
                float sr = source[c], sg = source[c + 1], sb = source[c + 2], sa = source[c + 3];
                float dr = backdrop[c], dg = backdrop[c + 1], db = backdrop[c + 2], da = backdrop[c + 3];
                if (sa <= 0f && da <= 0f) continue;

                // How much of each side survives; every Porter-Duff operator
                // is a choice of these two numbers.
                float fs, fd;
                switch (mode)
                {
                    case 0: fs = 0f; fd = 0f; break;            // clear
                    case 1: fs = 1f; fd = 0f; break;            // source
                    case 2: fs = 0f; fd = 1f; break;            // destination
                    case 3: fs = 1f; fd = 1f - sa; break;       // source over
                    case 4: fs = 1f - da; fd = 1f; break;       // destination over
                    case 5: fs = da; fd = 0f; break;            // source in
                    case 6: fs = 0f; fd = sa; break;            // destination in
                    case 7: fs = 1f - da; fd = 0f; break;       // source out
                    case 8: fs = 0f; fd = 1f - sa; break;       // destination out
                    case 9: fs = da; fd = 1f - sa; break;       // source atop
                    case 10: fs = 1f - da; fd = sa; break;      // destination atop
                    case 11: fs = 1f - da; fd = 1f - sa; break; // xor
                    case 12: fs = 1f; fd = 1f; break;           // plus
                    default:
                        Blend(mode, c, backdrop, sr, sg, sb, sa, dr, dg, db, da);
                        continue;
                }

                backdrop[c] = Mathf.Min(1f, sr * fs + dr * fd);
                backdrop[c + 1] = Mathf.Min(1f, sg * fs + dg * fd);
                backdrop[c + 2] = Mathf.Min(1f, sb * fs + db * fd);
                backdrop[c + 3] = Mathf.Min(1f, sa * fs + da * fd);
            }
        }

        /// <summary>
        /// A blend mode: where both sides are present their colours are mixed
        /// by the mode's function; where only one is, it shows through as it
        /// is. Alpha is source-over's.
        /// </summary>
        private static void Blend(int mode, int c, float[] backdrop,
            float sr, float sg, float sb, float sa, float dr, float dg, float db, float da)
        {
            // The functions are defined on straight colour.
            float sInv = sa > 0f ? 1f / sa : 0f, dInv = da > 0f ? 1f / da : 0f;
            float csr = sr * sInv, csg = sg * sInv, csb = sb * sInv;
            float cbr = dr * dInv, cbg = dg * dInv, cbb = db * dInv;

            float r, g, b;
            if (mode >= 24 && mode <= 27)
            {
                NonSeparable(mode, cbr, cbg, cbb, csr, csg, csb, out r, out g, out b);
            }
            else if (mode >= 13 && mode <= 23)
            {
                r = Separable(mode, cbr, csr);
                g = Separable(mode, cbg, csg);
                b = Separable(mode, cbb, csb);
            }
            else
            {
                // A mode this does not know: source over, which is what the
                // glyph would have been without the composite.
                r = csr; g = csg; b = csb;
            }

            float both = sa * da, onlySource = 1f - da, onlyBackdrop = 1f - sa;
            backdrop[c] = sr * onlySource + dr * onlyBackdrop + both * r;
            backdrop[c + 1] = sg * onlySource + dg * onlyBackdrop + both * g;
            backdrop[c + 2] = sb * onlySource + db * onlyBackdrop + both * b;
            backdrop[c + 3] = sa + da - both;
        }

        private static float Separable(int mode, float cb, float cs)
        {
            switch (mode)
            {
                case 13: return cb + cs - cb * cs;                          // screen
                case 14: return HardLight(cs, cb);                          // overlay
                case 15: return Mathf.Min(cb, cs);                          // darken
                case 16: return Mathf.Max(cb, cs);                          // lighten
                case 17:                                                    // colour dodge
                    if (cb <= 0f) return 0f;
                    return cs >= 1f ? 1f : Mathf.Min(1f, cb / (1f - cs));
                case 18:                                                    // colour burn
                    if (cb >= 1f) return 1f;
                    return cs <= 0f ? 0f : 1f - Mathf.Min(1f, (1f - cb) / cs);
                case 19: return HardLight(cb, cs);                          // hard light
                case 20:                                                    // soft light
                    if (cs <= 0.5f) return cb - (1f - 2f * cs) * cb * (1f - cb);
                    float d = cb <= 0.25f ? ((16f * cb - 12f) * cb + 4f) * cb : Mathf.Sqrt(cb);
                    return cb + (2f * cs - 1f) * (d - cb);
                case 21: return Mathf.Abs(cb - cs);                         // difference
                case 22: return cb + cs - 2f * cb * cs;                     // exclusion
                default: return cb * cs;                                    // 23: multiply
            }
        }

        /// <summary>Multiply or screen, chosen by the second argument.</summary>
        private static float HardLight(float cb, float cs) =>
            cs <= 0.5f ? cb * 2f * cs : cb + (2f * cs - 1f) - cb * (2f * cs - 1f);

        private static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

        private static float Sat(float r, float g, float b) =>
            Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b));

        private static void SetLum(ref float r, ref float g, ref float b, float lum)
        {
            float d = lum - Lum(r, g, b);
            r += d; g += d; b += d;

            // Back into gamut, holding the luminance.
            float l = Lum(r, g, b);
            float n = Mathf.Min(r, Mathf.Min(g, b)), x = Mathf.Max(r, Mathf.Max(g, b));
            if (n < 0f)
            {
                float k = l / (l - n);
                r = l + (r - l) * k; g = l + (g - l) * k; b = l + (b - l) * k;
            }
            if (x > 1f)
            {
                float k = (1f - l) / (x - l);
                r = l + (r - l) * k; g = l + (g - l) * k; b = l + (b - l) * k;
            }
        }

        private static void SetSat(ref float r, ref float g, ref float b, float sat)
        {
            float n = Mathf.Min(r, Mathf.Min(g, b)), x = Mathf.Max(r, Mathf.Max(g, b));
            if (x <= n)
            {
                r = g = b = 0f;
                return;
            }
            float k = sat / (x - n);
            r = (r - n) * k; g = (g - n) * k; b = (b - n) * k;
        }

        private static void NonSeparable(int mode, float cbr, float cbg, float cbb,
            float csr, float csg, float csb, out float r, out float g, out float b)
        {
            switch (mode)
            {
                case 24: // hue: the source's hue, the backdrop's saturation and luminance
                    r = csr; g = csg; b = csb;
                    SetSat(ref r, ref g, ref b, Sat(cbr, cbg, cbb));
                    SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb));
                    break;
                case 25: // saturation
                    r = cbr; g = cbg; b = cbb;
                    SetSat(ref r, ref g, ref b, Sat(csr, csg, csb));
                    SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb));
                    break;
                case 26: // colour: the source's hue and saturation
                    r = csr; g = csg; b = csb;
                    SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb));
                    break;
                default: // 27, luminosity
                    r = cbr; g = cbg; b = cbb;
                    SetLum(ref r, ref g, ref b, Lum(csr, csg, csb));
                    break;
            }
        }
    }
}
