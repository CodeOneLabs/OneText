using System;
using System.Collections.Generic;
using System.Text;

namespace OneText
{
    /// <summary>
    /// Records which characters, at which sizes, a running game actually draws.
    ///
    /// Prewarming is only as good as the charset it is given, and a hand-written
    /// list is a guess: it misses localized strings, user-generated content and
    /// anything assembled at runtime. Turning this on during a play session lets
    /// the game report its own usage, which is then saved as a
    /// <see cref="OneTextCharset"/> asset (OneText > Save Recorded Charset).
    ///
    /// Recording costs a hash-set probe per character per rebuild, so it is off
    /// unless asked for.
    /// </summary>
    public static class CharsetRecorder
    {
        private static readonly HashSet<int> s_codepoints = new HashSet<int>();
        private static readonly HashSet<int> s_sizes = new HashSet<int>();

        /// <summary>When true, every label reports the text it draws.</summary>
        public static bool Enabled { get; set; }

        public static int CodepointCount => s_codepoints.Count;

        public static IReadOnlyCollection<int> Codepoints => s_codepoints;

        /// <summary>Density buckets seen, not raw font sizes: that is what the atlas keys on.</summary>
        public static IReadOnlyCollection<int> PixelsPerEm => s_sizes;

        /// <summary>Records the characters of a string drawn at a given em size.</summary>
        public static void Record(string text, float pixelsPerEm) =>
            Record(text.AsSpan(), pixelsPerEm);

        /// <inheritdoc cref="Record(string, float)"/>
        public static void Record(System.ReadOnlySpan<char> text, float pixelsPerEm)
        {
            if (!Enabled || text.IsEmpty) return;

            s_sizes.Add(GlyphAtlas.QuantizePixelsPerEm(pixelsPerEm));
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    s_codepoints.Add(char.ConvertToUtf32(c, text[i + 1]));
                    i++;
                }
                else
                {
                    s_codepoints.Add(c);
                }
            }
        }

        public static void Clear()
        {
            s_codepoints.Clear();
            s_sizes.Clear();
        }

        /// <summary>The recorded characters, sorted, as a string ready to store in a charset.</summary>
        public static string CharactersAsString()
        {
            var sorted = new List<int>(s_codepoints);
            sorted.Sort();
            var builder = new StringBuilder(sorted.Count);
            foreach (int cp in sorted) builder.Append(char.ConvertFromUtf32(cp));
            return builder.ToString();
        }

        /// <summary>The recorded density buckets, sorted.</summary>
        public static List<float> SizesSorted()
        {
            var sizes = new List<float>();
            foreach (int size in s_sizes) sizes.Add(size);
            sizes.Sort();
            return sizes;
        }
    }
}
