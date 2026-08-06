using System.Collections.Generic;
using UnityEngine;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>One string, laid out in one style, measured against one box.</summary>
    public struct GalleryCell
    {
        public TextEntry Entry;

        /// <summary>Style this cell was laid out with, or null for the project default.</summary>
        public OneTextStyle Style;

        public string StyleName;

        /// <summary>Size the text actually needs, with nothing truncated.</summary>
        public float Width, Height;

        public int LineCount;

        /// <summary>Text wider or taller than the box it has to live in.</summary>
        public bool Overflow;

        /// <summary>Overflow the label's own settings would hide: a clipped or ellipsized string.</summary>
        public bool WouldTruncate;

        /// <summary>Characters in this string that no font in the chain can draw.</summary>
        public int MissingGlyphs;

        public bool Ok => !Overflow && MissingGlyphs == 0;
    }

    /// <summary>The box and the settings every cell is laid out with.</summary>
    public struct GalleryOptions
    {
        public float BoxWidth, BoxHeight;
        public float FontSize;
        public TextWrap Wrap;
        public TextOverflow Overflow;
        public float LineSpacing;
        public AsianTypography.Kinsoku Kinsoku;

        public static GalleryOptions Default => new GalleryOptions
        {
            BoxWidth = 320f,
            BoxHeight = 64f,
            FontSize = 28f,
            Wrap = TextWrap.Wrap,
            Overflow = TextOverflow.Ellipsis,
            LineSpacing = 1f,
            Kinsoku = AsianTypography.Kinsoku.Normal,
        };
    }

    /// <summary>
    /// Lays every string out, in every style, without a scene.
    ///
    /// Localization QA's most expensive pass is screenshotting every screen in
    /// every language to find the three that overflow their buttons. The layout
    /// engine does not need a scene, a canvas or a play session to answer that:
    /// give it the string, the style and the box, and it returns the size the
    /// text wants. So the expensive pass becomes a table you scroll and a
    /// filter for the red rows.
    ///
    /// Flipped around, one string across every style, it is how a typeface
    /// gets chosen in the first place, which is the view Korean font sites are
    /// built around and the reason anyone browses type with their own sentence.
    /// </summary>
    public static class StringGallery
    {
        /// <summary>Measures each string against each style. Order is strings-major, so a row is one string.</summary>
        public static List<GalleryCell> Measure(IReadOnlyList<TextEntry> entries,
            IReadOnlyList<OneTextStyle> styles, in GalleryOptions options)
        {
            var cells = new List<GalleryCell>();
            if (entries == null || entries.Count == 0) return cells;

            var engine = new TextLayoutEngine();
            var result = new TextLayoutResult();
            var stacks = new Dictionary<OneTextStyle, FontStack>();
            var projectStack = TextDoctor.ProjectFontStack();

            int styleCount = styles == null || styles.Count == 0 ? 1 : styles.Count;
            foreach (var entry in entries)
            {
                for (int s = 0; s < styleCount; s++)
                {
                    var style = styles == null || styles.Count == 0 ? null : styles[s];
                    var fonts = StackFor(style, projectStack, stacks);
                    cells.Add(Measure(engine, result, entry, style, fonts, options));
                }
            }
            return cells;
        }

        private static GalleryCell Measure(TextLayoutEngine engine, TextLayoutResult result,
            in TextEntry entry, OneTextStyle style, FontStack fonts, in GalleryOptions options)
        {
            var cell = new GalleryCell
            {
                Entry = entry,
                Style = style,
                StyleName = style != null ? style.name : "(project default)",
            };
            if (fonts == null || fonts.Primary == null) return cell;

            var settings = TextLayoutSettings.Default(fonts, FontSizeFor(style, options));
            settings.MaxWidth = options.BoxWidth;
            // Deliberately unbounded in height and never truncating: the
            // question is how big the text wants to be, and a measurement taken
            // through the truncation being tested for answers a different one.
            settings.MaxHeight = 0f;
            settings.Overflow = TextOverflow.Overflow;
            settings.Wrap = options.Wrap;
            settings.LineSpacing = LineSpacingFor(style, options);
            settings.Language = entry.Locale;
            settings.Kinsoku = options.Kinsoku;
            settings.KoreanWordWrap = TextDoctor.PrimarySubtag(entry.Locale) == "ko";
            settings.BaseDirection = BidiAlgorithm.AutoDirection;

            engine.Layout(entry.Value ?? string.Empty, settings, result);

            cell.Width = result.Width;
            cell.Height = result.Height;
            cell.LineCount = result.Lines.Count;
            cell.Overflow = result.Width > options.BoxWidth + 0.5f ||
                            result.Height > options.BoxHeight + 0.5f;
            cell.WouldTruncate = cell.Overflow && options.Overflow != TextOverflow.Overflow;
            cell.MissingGlyphs = MissingGlyphs(entry.Value, fonts);
            return cell;
        }

        private static int MissingGlyphs(string text, FontStack fonts)
        {
            int missing = 0;
            foreach (int codepoint in TextDoctor.Codepoints(text))
            {
                if (codepoint == '\n' || codepoint == '\t' || codepoint == ' ') continue;
                if (!fonts.Covers(codepoint)) missing++;
            }
            return missing;
        }

        private static float FontSizeFor(OneTextStyle style, in GalleryOptions options) =>
            style != null && style.Sets(OneTextStyle.Fields.Size) && style.FontSize > 0f
                ? style.FontSize
                : options.FontSize;

        private static float LineSpacingFor(OneTextStyle style, in GalleryOptions options) =>
            style != null && style.Sets(OneTextStyle.Fields.LineSpacing) && style.LineSpacing > 0f
                ? style.LineSpacing
                : Mathf.Max(0.01f, options.LineSpacing);

        /// <summary>
        /// The chain a label using this style would get: the style's font on
        /// top of the project fallbacks, so a cell that renders here renders in
        /// the game for the same reason.
        /// </summary>
        private static FontStack StackFor(OneTextStyle style, FontStack projectStack,
            Dictionary<OneTextStyle, FontStack> cache)
        {
            if (style == null || !style.Sets(OneTextStyle.Fields.Font) || style.Font == null)
                return projectStack;
            if (cache.TryGetValue(style, out var cached)) return cached;

            var stack = new FontStack();
            stack.Add(style.Font.Font, style.Font.Language);
            var settings = OneTextSettings.Instance;
            if (settings != null)
                foreach (var fallback in settings.FallbackFonts)
                    if (fallback != null) stack.Add(fallback.Font, fallback.Language);

            cache[style] = stack;
            return stack;
        }
    }
}
