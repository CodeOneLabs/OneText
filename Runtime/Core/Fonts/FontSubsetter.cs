using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OneText.Native;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Cuts a font down to the characters a project actually draws.
    ///
    /// Compression already got a 55 MB Korean face to 12 MB in a build.
    /// Subsetting is the bigger lever on the same number: a game that draws
    /// 2,350 Hangul syllables and some Latin is shipping tens of thousands of
    /// glyphs it will never rasterize, and those glyphs <em>are</em> the file.
    ///
    /// Almost none of this is new code. `CharsetRecorder` already collects what
    /// a play session drew and `OneTextCharset` stores it — that is exactly a
    /// subsetter's input, and it was built for prewarming. The subsetter itself
    /// is in the binary already: the HarfBuzz we bundle exports 31
    /// <c>hb_subset_*</c> symbols on every platform. So this is a P/Invoke
    /// binding and an import option, not a font-format project.
    ///
    /// <para><b>It cuts against what this engine is for.</b> A subset face
    /// cannot draw what nobody predicted, and "the charset you cannot
    /// enumerate" — a chat window, a name entry field, user-generated content —
    /// is the workload this engine wins. So subsetting is opt-in, off by
    /// default, the full face is a one-click revert, and a project that subsets
    /// is told plainly what it gave up. It is the right answer for a
    /// fixed-charset game and the wrong one for a chat window, which is the
    /// same line the benchmarks draw between us and a prebaked atlas.</para>
    /// </summary>
    public static class FontSubsetter
    {
        /// <summary>
        /// What a subset run produced, whether or not it worked.
        /// </summary>
        public readonly struct Report
        {
            public readonly bool Succeeded;
            public readonly int OriginalBytes;
            public readonly int SubsetBytes;
            public readonly int CodepointsKept;
            public readonly string Failure;

            public Report(bool succeeded, int originalBytes, int subsetBytes,
                int codepointsKept, string failure)
            {
                Succeeded = succeeded;
                OriginalBytes = originalBytes;
                SubsetBytes = subsetBytes;
                CodepointsKept = codepointsKept;
                Failure = failure;
            }

            /// <summary>Subset size as a fraction of the original.</summary>
            public float Fraction => OriginalBytes > 0 ? SubsetBytes / (float)OriginalBytes : 1f;

            public override string ToString() => Succeeded
                ? $"subset: {CodepointsKept} codepoints, " +
                  $"{OriginalBytes / 1024} KB -> {SubsetBytes / 1024} KB ({Fraction:P1})"
                : $"subset failed: {Failure}";
        }

        /// <summary>
        /// Flags passed to <c>hb_subset_input_set_flags</c>. Only the ones this
        /// engine has a reason to touch; the rest keep HarfBuzz's defaults,
        /// which are the right ones.
        /// </summary>
        [Flags]
        private enum SubsetFlags : uint
        {
            Default = 0x00000000u,
            NoHinting = 0x00000001u,
            RetainGids = 0x00000002u,
            DesubroutinizeD = 0x00000004u,
            NameLegacy = 0x00000008u,
            SetOverlapsFlag = 0x00000010u,
            PassthroughUnrecognized = 0x00000020u,
            NotdefOutline = 0x00000040u,
            GlyphNames = 0x00000080u,
            NoPruneUnicodeRanges = 0x00000100u,
        }

        /// <summary>True when the loaded HarfBuzz can subset at all.</summary>
        public static bool IsAvailable => HarfBuzzSubset.IsAvailable;

        /// <summary>
        /// Subsets <paramref name="fontBytes"/> to <paramref name="codepoints"/>.
        ///
        /// The layout tables are the thing to get right, and the reason this is
        /// a binding rather than a hand-rolled table rewriter: GSUB and GPOS
        /// entries reference glyph ids, so renumbering glyphs without rewriting
        /// them silently breaks ligatures, marks and kerning — and Arabic stops
        /// joining, which is the failure that only shows up in the one language
        /// nobody on the team reads. hb-subset does this correctly; the test
        /// beside this file is the proof rather than the hope.
        /// </summary>
        public static bool TrySubset(byte[] fontBytes, IReadOnlyCollection<int> codepoints,
            out byte[] subsetBytes, out Report report)
        {
            subsetBytes = null;

            if (fontBytes == null || fontBytes.Length == 0)
            {
                report = new Report(false, 0, 0, 0, "no font bytes");
                return false;
            }
            if (codepoints == null || codepoints.Count == 0)
            {
                // Subsetting to nothing would produce a font that draws nothing,
                // which is never what anyone meant.
                report = new Report(false, fontBytes.Length, 0, 0, "no codepoints to keep");
                return false;
            }
            if (!IsAvailable)
            {
                report = new Report(false, fontBytes.Length, 0, 0,
                    "the loaded HarfBuzz was built without harfbuzz-subset");
                return false;
            }

            var handle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
            IntPtr blob = IntPtr.Zero, face = IntPtr.Zero, input = IntPtr.Zero, result = IntPtr.Zero;
            try
            {
                blob = HarfBuzzApi.hb_blob_create(handle.AddrOfPinnedObject(),
                    (uint)fontBytes.Length, HarfBuzzApi.HB_MEMORY_MODE_READONLY,
                    IntPtr.Zero, IntPtr.Zero);
                face = HarfBuzzApi.hb_face_create(blob, 0);
                if (face == IntPtr.Zero)
                {
                    report = new Report(false, fontBytes.Length, 0, 0, "font could not be parsed");
                    return false;
                }

                input = HarfBuzzApi.hb_subset_input_create_or_fail();
                if (input == IntPtr.Zero)
                {
                    report = new Report(false, fontBytes.Length, 0, 0, "subset input could not be created");
                    return false;
                }

                var unicodes = HarfBuzzApi.hb_subset_input_unicode_set(input);
                foreach (int codepoint in codepoints)
                    if (codepoint >= 0) HarfBuzzApi.hb_set_add(unicodes, (uint)codepoint);

                // Keep the layout tables and drop what a game never reads. Glyph
                // names cost real size in a CJK face and nothing renders from
                // them; hinting is dead weight for an SDF pipeline, which
                // rasterizes from outlines at its own density.
                var flags = (SubsetFlags)HarfBuzzApi.hb_subset_input_get_flags(input);
                flags |= SubsetFlags.NoHinting;
                flags &= ~SubsetFlags.GlyphNames;
                flags &= ~SubsetFlags.RetainGids;
                HarfBuzzApi.hb_subset_input_set_flags(input, (uint)flags);

                result = HarfBuzzApi.hb_subset_or_fail(face, input);
                if (result == IntPtr.Zero)
                {
                    report = new Report(false, fontBytes.Length, 0, codepoints.Count,
                        "hb_subset_or_fail refused the font");
                    return false;
                }

                var resultBlob = HarfBuzzApi.hb_face_reference_blob(result);
                try
                {
                    var data = HarfBuzzApi.hb_blob_get_data(resultBlob, out uint length);
                    if (data == IntPtr.Zero || length == 0)
                    {
                        report = new Report(false, fontBytes.Length, 0, codepoints.Count,
                            "the subset face produced no bytes");
                        return false;
                    }

                    subsetBytes = new byte[length];
                    Marshal.Copy(data, subsetBytes, 0, (int)length);
                }
                finally
                {
                    HarfBuzzApi.hb_blob_destroy(resultBlob);
                }

                report = new Report(true, fontBytes.Length, subsetBytes.Length, codepoints.Count, null);
                return true;
            }
            finally
            {
                if (result != IntPtr.Zero) HarfBuzzApi.hb_face_destroy(result);
                if (input != IntPtr.Zero) HarfBuzzApi.hb_subset_input_destroy(input);
                if (face != IntPtr.Zero) HarfBuzzApi.hb_face_destroy(face);
                if (blob != IntPtr.Zero) HarfBuzzApi.hb_blob_destroy(blob);
                if (handle.IsAllocated) handle.Free();
            }
        }

        /// <summary>
        /// Convenience: subset to the characters a charset asset names — which
        /// is the same asset prewarming already uses, and the reason most of
        /// this milestone was already built.
        /// </summary>
        public static bool TrySubset(byte[] fontBytes, OneTextCharset charset,
            out byte[] subsetBytes, out Report report)
        {
            if (charset == null)
            {
                subsetBytes = null;
                report = new Report(false, fontBytes?.Length ?? 0, 0, 0, "no charset");
                return false;
            }
            return TrySubset(fontBytes, charset.Codepoints(), out subsetBytes, out report);
        }
    }
}
