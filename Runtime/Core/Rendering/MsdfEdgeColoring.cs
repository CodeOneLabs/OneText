using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Assigns every polyline segment of a contour a colour-channel mask, the
    /// way a multi-channel distance field needs them: the two edges meeting at
    /// a corner must share exactly one channel, so that the median of the three
    /// channels reconstructs the corner as the intersection of two half-planes
    /// instead of the rounded arc a single channel can express. The scheme is
    /// the one described for msdfgen (Chlumský), built here from that
    /// description; the three colours are the pairs of channels, so any two
    /// distinct ones overlap in exactly one.
    ///
    /// Two things differ from a colouring done on Béziers. The input is
    /// already flattened, so a "corner" has to be told apart from the bend
    /// between two segments of one curve; hence the angle threshold below is
    /// tens of degrees rather than the three a curve-level colouring can
    /// afford. And an edge here is a <em>run</em> of segments between corners,
    /// so the pseudo-distance extension the field needs belongs to the run's
    /// two ends and to nothing in between: that is what the run-start and
    /// run-end bits mark.
    /// </summary>
    public static class MsdfEdgeColoring
    {
        /// <summary>Channel bits, matching the texture's R, G and B.</summary>
        public const byte Red = 1, Green = 2, Blue = 4;

        /// <summary>A segment on no corner at all: every channel sees it.</summary>
        public const byte White = Red | Green | Blue;

        /// <summary>
        /// The segment's first point ends an edge run: the field may extend
        /// this segment's line past it. Only corners carry this, which is what
        /// keeps the extension out of the middle of a flattened curve.
        /// </summary>
        public const byte RunStart = 8;

        /// <inheritdoc cref="RunStart"/>
        public const byte RunEnd = 16;

        /// <summary>Mask of the channel bits alone.</summary>
        public const byte ChannelMask = White;

        /// <summary>
        /// How sharp a turn counts as a corner.
        ///
        /// Contours arrive flattened to <see cref="OutlineExtractor.FlatnessPixels"/>,
        /// and a curve of radius r pixels flattened to that error turns by about
        /// sqrt(8 * flatness / r) at every vertex: eight degrees on the bowl of
        /// an 'o' at 64 ppem, thirteen at 24. msdfgen's three degrees would call
        /// every one of those a corner and colour a smooth curve in stripes.
        /// Thirty is above the flattening's own bends and below every corner
        /// type actually has: a stem end turns ninety degrees, and a bracket
        /// shallower than thirty is meant to read as smooth anyway. Missing a
        /// corner costs sharpness (it renders as the single-channel field
        /// would); inventing one costs nothing but a colour change where the
        /// two edges agree.
        /// </summary>
        public static float CornerAngleDegrees = 30f;

        // Yellow, cyan, magenta: R|G, G|B, R|B.
        private static readonly byte[] s_palette =
        {
            Red | Green, Green | Blue, Red | Blue,
        };

        private static readonly List<int> s_corners = new List<int>();
        private static readonly List<byte> s_colors = new List<byte>();

        /// <summary>
        /// Writes one flag byte per segment of <paramref name="contour"/> into
        /// <paramref name="flags"/> at <paramref name="offset"/>: segment i
        /// runs from point i to point i+1, so the last point has no byte of its
        /// own and the caller may leave it alone.
        ///
        /// A contour whose first and last point coincide (what the extractor's
        /// close-path produces) is treated as a loop; anything else has its two
        /// ends marked as run ends, because an open end is a corner in every
        /// sense that matters here.
        /// </summary>
        public static void Assign(IReadOnlyList<Vector2> contour, byte[] flags, int offset)
        {
            int segments = contour.Count - 1;
            if (segments <= 0) return;

            for (int i = 0; i < segments; i++) flags[offset + i] = White;

            bool closed = (contour[0] - contour[contour.Count - 1]).sqrMagnitude < 1e-12f;
            float cosLimit = Mathf.Cos(CornerAngleDegrees * Mathf.Deg2Rad);

            s_corners.Clear();
            for (int i = 0; i < segments; i++)
            {
                if (i == 0 && !closed)
                {
                    s_corners.Add(0);
                    continue;
                }
                int previous = i == 0 ? segments - 1 : i - 1;
                if (IsCorner(contour, previous, i, cosLimit)) s_corners.Add(i);
            }

            // A closed contour with no corner at all is one smooth loop: every
            // channel takes it, and nothing extends, which reproduces the
            // single-channel field exactly. That is the right answer for an 'o'.
            if (s_corners.Count == 0) return;

            // One corner leaves a run adjacent to itself, which no colouring can
            // separate: a comma or a teardrop. Split it into three so the
            // corner gets two different colours on its two sides.
            if (closed && s_corners.Count == 1 && segments >= 3)
            {
                int first = s_corners[0];
                s_corners.Add((first + segments / 3) % segments);
                s_corners.Add((first + 2 * segments / 3) % segments);
            }

            int runs = s_corners.Count;
            s_colors.Clear();
            for (int k = 0; k < runs; k++) s_colors.Add(s_palette[k % 3]);
            // The runs form a cycle, so the last one is adjacent to the first.
            if (closed && runs > 2 && s_colors[runs - 1] == s_colors[0])
                s_colors[runs - 1] = Third(s_colors[runs - 2], s_colors[0]);

            for (int k = 0; k < runs; k++)
            {
                int start = s_corners[k];
                int next = s_corners[(k + 1) % runs];
                int length = (next - start + segments) % segments;
                if (length == 0) length = segments;

                for (int s = 0; s < length; s++)
                    flags[offset + (start + s) % segments] = s_colors[k];
                flags[offset + start] |= RunStart;
                flags[offset + (start + length - 1) % segments] |= RunEnd;
            }
        }

        /// <summary>The palette entry that is neither of the two given.</summary>
        private static byte Third(byte a, byte b)
        {
            foreach (var candidate in s_palette)
                if (candidate != a && candidate != b) return candidate;
            return White;
        }

        private static bool IsCorner(IReadOnlyList<Vector2> contour, int previous, int current,
            float cosLimit)
        {
            var a = contour[previous + 1] - contour[previous];
            var b = contour[current + 1] - contour[current];
            float lengths = a.magnitude * b.magnitude;
            // A zero-length segment has no direction to turn from; treating it
            // as a corner would scatter colour changes over coincident points.
            if (lengths < 1e-12f) return false;
            return Vector2.Dot(a, b) / lengths <= cosLimit;
        }
    }
}
