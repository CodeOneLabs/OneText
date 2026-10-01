using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A font the project settings name by key instead of by reference, so
    /// loading the settings does not load the font.
    ///
    /// <para>The coverage is what lets a font stay unloaded until it is
    /// actually needed. A label that meets a character no loaded font has
    /// can only reach for an unloaded one if something says which unloaded
    /// one draws it, and asking the font would mean loading it. So the answer
    /// is written down ahead of time, by the editor, from the font's own
    /// <c>cmap</c>: <see cref="FontCoverage.Of(FontData)"/>.</para>
    /// </summary>
    [Serializable]
    public struct OneFontReference
    {
        [Tooltip("Resources path without extension, or Addressables address, depending on the " +
            "font source in Project Settings > OneText.")]
        [SerializeField] private string _key;

        [Tooltip("Language this font is for: zh-Hans, ja, ko. FontResidency.SetLanguages loads " +
            "the fonts whose language is asked for and lets go of the rest. Empty means the font " +
            "is only ever loaded on demand or by key.")]
        [SerializeField] private string _language;

        // Codepoint ranges the font draws, as inclusive [first, last] pairs in
        // ascending order. Filled in by the editor; empty means "unknown", and
        // a font with unknown coverage is never loaded on demand.
        [SerializeField, HideInInspector] private int[] _coverage;

        // Whether the font draws in colour (CBDT or COLR), recorded with the
        // coverage and for the same reason: a keycap or a heart written with
        // VS16 asks for the emoji presentation of a character a text font also
        // covers, and only an answer written down ahead of time can send it to
        // an unloaded colour font rather than to the first text face that has
        // a "1".
        [SerializeField, HideInInspector] private bool _color;

        public OneFontReference(string key, string language, int[] coverage)
            : this(key, language, coverage, false)
        {
        }

        public OneFontReference(string key, string language, int[] coverage, bool color)
        {
            _key = key;
            _language = language;
            _coverage = coverage;
            _color = color;
        }

        /// <summary>The same reference declared for another language.</summary>
        public OneFontReference WithLanguage(string language) =>
            new OneFontReference(_key, language, _coverage, _color);

        /// <summary>What <see cref="IFontSource"/> loads this font by.</summary>
        public string Key => _key;

        /// <summary>BCP 47 tag the font serves, or null.</summary>
        public string Language => string.IsNullOrEmpty(_language) ? null : _language;

        /// <summary>Whether the font draws in colour, as recorded with its coverage.</summary>
        public bool IsColor => _color;

        /// <summary>Whether the coverage was recorded. Without it, no on-demand loading.</summary>
        public bool HasCoverage => _coverage != null && _coverage.Length >= 2;

        /// <summary>The recorded ranges, as inclusive pairs. Never null.</summary>
        public IReadOnlyList<int> Coverage => _coverage ?? Array.Empty<int>();

        /// <summary>Whether the font draws this character, by the recorded coverage.</summary>
        public bool Covers(int codepoint) => FontCoverage.Contains(_coverage, codepoint);

        /// <summary>
        /// Whether this font serves <paramref name="language"/>: the same
        /// primary-subtag prefix rule the font stack uses, so a font declared
        /// <c>zh</c> serves <c>zh-Hans</c> and one declared <c>zh-Hant</c> does not.
        /// </summary>
        public bool Serves(string language) => FontCoverage.LanguageMatches(Language, language);
    }

    /// <summary>
    /// Which characters a font draws, as a sorted list of inclusive ranges.
    /// Computed from a loaded face once, at edit time, and stored where the
    /// face is not.
    /// </summary>
    public static class FontCoverage
    {
        /// <summary>
        /// The ranges of codepoints <paramref name="font"/> maps to a glyph.
        /// Walks every Unicode scalar value, which is a few hundred
        /// milliseconds for a CJK face: an editor operation, not a runtime one.
        /// </summary>
        public static int[] Of(FontData font)
        {
            var ranges = new List<int>();
            if (font == null || !font.IsValid) return ranges.ToArray();

            int start = -1;
            for (int codepoint = 0; codepoint <= 0x10FFFF; codepoint++)
            {
                // Surrogates are not characters and no cmap should claim them.
                bool has = (codepoint < 0xD800 || codepoint > 0xDFFF) && font.HasGlyph(codepoint);
                if (has && start < 0) start = codepoint;
                else if (!has && start >= 0)
                {
                    ranges.Add(start);
                    ranges.Add(codepoint - 1);
                    start = -1;
                }
            }
            if (start >= 0)
            {
                ranges.Add(start);
                ranges.Add(0x10FFFF);
            }
            return ranges.ToArray();
        }

        /// <summary>Binary search over inclusive pairs.</summary>
        public static bool Contains(int[] ranges, int codepoint)
        {
            if (ranges == null || ranges.Length < 2) return false;
            int lo = 0, hi = ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (codepoint < ranges[mid * 2]) hi = mid - 1;
                else if (codepoint > ranges[mid * 2 + 1]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        /// <summary>Number of codepoints the ranges cover.</summary>
        public static int Count(IReadOnlyList<int> ranges)
        {
            int total = 0;
            for (int i = 0; i + 1 < ranges.Count; i += 2) total += ranges[i + 1] - ranges[i] + 1;
            return total;
        }

        internal static bool LanguageMatches(string fontLanguage, string wanted)
        {
            if (string.IsNullOrEmpty(fontLanguage) || string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(fontLanguage, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return wanted.Length > fontLanguage.Length &&
                   wanted.StartsWith(fontLanguage, StringComparison.OrdinalIgnoreCase) &&
                   wanted[fontLanguage.Length] == '-';
        }
    }
}
