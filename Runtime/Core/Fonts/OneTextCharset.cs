using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>A closed range of code points, e.g. ASCII printable = U+0020..U+007E.</summary>
    [System.Serializable]
    public struct CodepointRange
    {
        public string Name;
        public int First;
        public int Last;

        public CodepointRange(string name, int first, int last)
        {
            Name = name;
            First = first;
            Last = last;
        }

        public int Count => Mathf.Max(0, Last - First + 1);
    }

    /// <summary>
    /// A set of characters and sizes to rasterize before they are needed.
    ///
    /// Four ways to fill one, in the order they are worth reaching for:
    /// record a play session (OneText > Save Recorded Charset; the game
    /// reports its own usage), scan the project's labels, add explicit ranges
    /// for user-generated content, or paste a frequency list for CJK where full
    /// coverage does not fit any budget.
    /// </summary>
    // The Project window draws this rather than the default script sheet.
    [Icon("Packages/com.onetext.core/Editor/Icons/OneTextCharset.png")]
    [CreateAssetMenu(menuName = "OneText/Charset", fileName = "OneTextCharset")]
    public sealed class OneTextCharset : ScriptableObject
    {
        [Tooltip("Characters to prewarm. Duplicates and whitespace are ignored.")]
        [SerializeField, TextArea(3, 12)] private string _characters = "";

        [Tooltip("Code point ranges to prewarm, in addition to the characters above.")]
        [SerializeField] private List<CodepointRange> _ranges = new List<CodepointRange>();

        [Tooltip("Em sizes to rasterize at. Each becomes a density bucket in the atlas.")]
        [SerializeField] private List<float> _sizes = new List<float> { 36f };

        [Tooltip("Fonts to warm. Empty means the project default font and its fallbacks.")]
        [SerializeField] private List<OneFontAsset> _fonts = new List<OneFontAsset>();

        [Tooltip("Stop once this fraction of the atlas is occupied, leaving room for unpredicted glyphs.")]
        [SerializeField, Range(0.1f, 1f)] private float _fillLimit = AtlasPrewarm.DefaultFillLimit;

        [Header("Scanned sources (editor only)")]
        [Tooltip("Project folders of localization tables, dialogue scripts or any other text " +
            "whose characters belong in this charset. Rescanned when those files change.")]
        [SerializeField] private List<string> _sourceFolders = new List<string>();

        [Tooltip("Rescan the folders above whenever a file under them is imported.")]
        [SerializeField] private bool _autoRescan = true;

        [Tooltip("Characters the last folder scan contributed. Kept apart from the ones typed " +
            "by hand so a rescan can replace them without eating anything else.")]
        [SerializeField, HideInInspector] private string _scannedCharacters = "";

        public string Characters { get => _characters; set => _characters = value; }

        /// <summary>
        /// Project folders scanned for text. Editor-side data on a runtime
        /// asset, deliberately: a charset that reports where it came from can be
        /// rebuilt when the string tables change, and one that does not has to
        /// be rebuilt by whoever remembers.
        /// </summary>
        public List<string> SourceFolders => _sourceFolders;

        /// <summary>Whether importing a file under a scanned folder refreshes this charset.</summary>
        public bool AutoRescan { get => _autoRescan; set => _autoRescan = value; }

        /// <summary>
        /// Characters contributed by the last folder scan, held separately from
        /// <see cref="Characters"/> so a rescan replaces exactly what the
        /// previous scan added.
        /// </summary>
        public string ScannedCharacters { get => _scannedCharacters; set => _scannedCharacters = value; }

        public List<CodepointRange> Ranges => _ranges;

        public List<float> Sizes => _sizes;

        public List<OneFontAsset> Fonts => _fonts;

        public float FillLimit { get => _fillLimit; set => _fillLimit = value; }

        /// <summary>Every code point this charset asks for, deduplicated and ordered.</summary>
        public List<int> Codepoints()
        {
            var seen = new HashSet<int>();
            var result = new List<int>();

            AddCharacters(_characters, seen, result);
            AddCharacters(_scannedCharacters, seen, result);

            foreach (var range in _ranges)
            {
                for (int cp = range.First; cp <= range.Last; cp++)
                {
                    if (cp < 0 || cp > 0x10FFFF) break;
                    if (cp >= 0xD800 && cp <= 0xDFFF) continue; // surrogates are not characters
                    if (seen.Add(cp)) result.Add(cp);
                }
            }
            return result;
        }

        private static void AddCharacters(string source, HashSet<int> seen, List<int> result)
        {
            if (string.IsNullOrEmpty(source)) return;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < source.Length &&
                    char.IsLowSurrogate(source[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, source[i + 1]);
                    i++;
                }
                if (seen.Add(cp)) result.Add(cp);
            }
        }

        /// <summary>
        /// Rasterizes this charset into the shared atlas. Safe to call more than
        /// once; anything already resident is skipped.
        /// </summary>
        public PrewarmReport Prewarm() => Prewarm(SharedGlyphAtlas.Atlas);

        public PrewarmReport Prewarm(GlyphAtlas atlas)
        {
            var stack = BuildFontStack();
            // Count, not Primary: an empty stack now answers Primary with a
            // face from the operating system, which is the right thing for a
            // label — text a reader can read beats nothing — and the wrong
            // thing to prewarm. Those tiles belong to whatever font that
            // machine happens to ship, so baking them at startup spends the
            // budget on glyphs the built game may never draw and says nothing
            // about the charset having no font, which is the actual problem.
            if (stack == null || stack.Count == 0 || stack.Primary == null)
            {
                Debug.LogWarning($"OneText: charset '{name}' has no font to prewarm with " +
                    "(assign fonts on the charset, or a default font in Project Settings > OneText).");
                return default;
            }
            return AtlasPrewarm.Warm(atlas, stack, Codepoints(), _sizes, _fillLimit);
        }

        /// <summary>The charset's own fonts, or the project defaults when it has none.</summary>
        public FontStack BuildFontStack()
        {
            var stack = new FontStack();
            foreach (var asset in _fonts)
                if (asset != null) stack.Add(asset.Font, asset.Language);

            if (stack.Count > 0) return stack;

            var settings = OneTextSettings.Instance;
            if (settings == null) return stack;
            if (settings.DefaultFont != null)
                stack.Add(settings.DefaultFont.Font, settings.DefaultFont.Language);
            foreach (var fallback in settings.FallbackFonts)
                if (fallback != null) stack.Add(fallback.Font, fallback.Language);
            return stack;
        }

        /// <summary>Ranges worth offering as one-click presets in the inspector.</summary>
        public static readonly CodepointRange[] Presets =
        {
            new CodepointRange("ASCII printable", 0x0020, 0x007E),
            new CodepointRange("Latin-1 Supplement", 0x00A0, 0x00FF),
            new CodepointRange("Latin Extended-A", 0x0100, 0x017F),
            new CodepointRange("Greek", 0x0370, 0x03FF),
            new CodepointRange("Cyrillic", 0x0400, 0x04FF),
            new CodepointRange("Hebrew", 0x0590, 0x05FF),
            new CodepointRange("Arabic", 0x0600, 0x06FF),
            new CodepointRange("Thai", 0x0E00, 0x0E7F),
            new CodepointRange("Hiragana + Katakana", 0x3040, 0x30FF),
            new CodepointRange("Hangul syllables", 0xAC00, 0xD7A3),
            new CodepointRange("CJK Unified Ideographs", 0x4E00, 0x9FFF),
            new CodepointRange("Punctuation", 0x2010, 0x205E),
        };
    }
}
