using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A glyph SDF bitmap plus the mapping back into glyph units.
    /// The bitmap covers [OriginUnits, OriginUnits + (Width,Height) *
    /// UnitsPerPixel], row 0 at the bottom (y-up, matching Unity textures).
    /// </summary>
    public struct RasterizedGlyph
    {
        /// <summary>
        /// The tile's texels: <see cref="Width"/> * <see cref="Height"/> *
        /// <see cref="Channels"/> bytes starting at <see cref="PixelStart"/>,
        /// row 0 first, channels interleaved.
        ///
        /// The array belongs to the rasterizer and holds the whole batch; it
        /// stays valid only until the next call. Anything that needs to keep
        /// the tile takes a copy with <see cref="CopyPixels"/>.
        /// </summary>
        public byte[] Pixels;
        public int PixelStart;
        public int Width, Height;
        public Vector2 OriginUnits;  // glyph-space position of the tile's bottom-left
        public float UnitsPerPixel;
        public bool IsEmpty;         // no contours (space etc.)

        /// <summary>
        /// Bytes per texel: 1 for the single-channel field (R8), 4 for the
        /// multi-channel one (RGBA, distance per channel plus the true distance
        /// in alpha). Zero only on a default-constructed value, which is why
        /// every read goes through <see cref="BytesPerTexel"/>.
        /// </summary>
        public int Channels;

        /// <inheritdoc cref="Channels"/>
        public int BytesPerTexel => Channels <= 0 ? 1 : Channels;

        /// <summary>This tile's texels in an array of their own.</summary>
        public byte[] CopyPixels()
        {
            var copy = new byte[Width * Height * BytesPerTexel];
            if (Pixels != null) System.Array.Copy(Pixels, PixelStart, copy, 0, copy.Length);
            return copy;
        }
    }

    /// <summary>
    /// Turns a glyph outline into an SDF tile via the Burst rasterize job.
    /// Rasterization density is caller-chosen (pixels per font unit) and
    /// uniform across glyphs: consistent density is what keeps abutting
    /// joints in connected scripts registered to each other.
    /// Main-thread synchronous for now; the async pipeline comes later.
    /// </summary>
    public static partial class GlyphRasterizer
    {
        public const int Padding = 4;
        public const float SpreadPixels = 4f;
        public const int MaxTileEdge = 512;

        /// <summary>
        /// Off only so tests can compare the culled rasterizer against the
        /// naive one, byte for byte.
        /// </summary>
        public static bool Cull = true;

        private static readonly GlyphOutline s_outline = new GlyphOutline();

        public static RasterizedGlyph Rasterize(FontData font, uint glyphId, float pixelsPerUnit,
            bool precise = false)
        {
            s_outline.Clear();
            OutlineExtractor.Extract(font, glyphId, s_outline, pixelsPerUnit);
            return RasterizeContours(s_outline.Contours, null, pixelsPerUnit, precise);
        }

        /// <summary>
        /// Rasterizes a set of already-positioned contours (glyph units) into
        /// one SDF tile. Used both for single glyphs and for merged clusters
        /// of connected glyphs: a cluster baked as one field has its joints
        /// in the SDF interior, which is what makes them seam-proof.
        /// <paramref name="groups"/> assigns each contour to its source glyph
        /// (ascending; null = all one group): signed distance is resolved per
        /// group and unioned, so edges buried inside another glyph's ink
        /// cannot dent the field.
        /// <paramref name="precise"/> bakes a multi-channel field instead:
        /// four bytes a texel, corners that survive the sampler.
        /// </summary>
        public static RasterizedGlyph RasterizeContours(List<List<Vector2>> contours,
            List<int> groups, float pixelsPerUnit, bool precise = false)
        {
            // One request through the batch path: a single implementation to
            // keep correct, and a single tile is just a batch of one.
            s_singleRequest.Clear();
            s_singleRequest.Add(new RasterizeRequest
            {
                Contours = contours,
                Groups = groups,
                PixelsPerUnit = pixelsPerUnit,
            });
            RasterizeBatch(s_singleRequest, s_singleResult, precise);
            return s_singleResult.Count > 0 ? s_singleResult[0] : new RasterizedGlyph { IsEmpty = true };
        }

        private static readonly List<RasterizeRequest> s_singleRequest = new List<RasterizeRequest>(1);
        private static readonly List<RasterizedGlyph> s_singleResult = new List<RasterizedGlyph>(1);

    }
}
