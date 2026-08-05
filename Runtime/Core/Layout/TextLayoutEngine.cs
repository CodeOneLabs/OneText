using System;
using System.Collections.Generic;
using OneText.Unicode;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Stage 4 of the pipeline: turns a string into positioned lines of shaped
    /// glyphs. Paragraphs split at mandatory breaks (UAX #14), each paragraph
    /// resolves its bidi levels (UAX #9) and splits into runs by level and by
    /// font coverage, lines wrap at break opportunities (UAX #14) with an
    /// emergency break at grapheme cluster boundaries (UAX #29), and every line
    /// is reordered visually before it is placed.
    ///
    /// The engine has no UI-framework dependency: it produces a
    /// <see cref="TextLayoutResult"/> that any frontend can turn into a mesh.
    /// </summary>
    public sealed class TextLayoutEngine : IDisposable
    {
        private const string EllipsisText = "…";

        /// <summary>A stretch of text with one font, bidi level and style, in logical order.</summary>
        private struct Item
        {
            public FontData Font;
            public byte Level;
            public int Start, End;
            public TextStyle Style;
            public float FontSize;     // the style's size, resolved against the label's

            // Where this item's glyphs sit in _measured, or a count of -1 if it
            // was never shaped (a sprite). The measuring pass shapes every item
            // to fill the wrapper's advances, and the emitting pass shapes the
            // identical range again whenever a line did not cut the item in two
            // — which, for a label that fits on one line, is every item. Keeping
            // the first result is one buffer against a second call into
            // HarfBuzz, measured at 0.79 us against 0.61 us for the same work.
            public int MeasuredStart, MeasuredCount;
        }

        private readonly Shaper _shaper = new Shaper();
        private readonly List<BidiRuns.Run> _bidiRuns = new List<BidiRuns.Run>();
        private readonly List<Item> _items = new List<Item>();
        private readonly List<int> _graphemes = new List<int>();
        private readonly List<ShapedGlyph> _scratch = new List<ShapedGlyph>();
        // Every item's shaped glyphs for this layout, kept until the emitting
        // pass has had its chance to reuse them. Reused between layouts.
        private readonly List<ShapedGlyph> _measured = new List<ShapedGlyph>();
        private readonly List<TextRun> _lineRuns = new List<TextRun>();

        private LineBreaker.Opportunity[] _opportunities = new LineBreaker.Opportunity[64];
        private float[] _advances = new float[64];
        private bool[] _graphemeStart = new bool[65];

        // What a full-width mark would give up if a line began or ended on it,
        // in layout units, over and above the adjacency compression already in
        // its advance. Measured here rather than at the line edge because the
        // wrapper has to know it *before* it decides where the edge is — that
        // is the whole point of the rule: half an em of invisible blank should
        // not push a word to the next line.
        private float[] _startGive = new float[64];
        private float[] _endGive = new float[64];

        /// <summary>Whether this layout is costing line edges at all.</summary>
        private bool _edgeGive;

        /// <summary>Lays <paramref name="text"/> out into <paramref name="result"/>.</summary>
        public void Layout(string text, in TextLayoutSettings settings, TextLayoutResult result)
        {
            result.Clear();
            _spanCursor = 0;
            _edgeGive = settings.PunctuationCompression;
            var primary = settings.Fonts?.Primary;
            if (primary == null || settings.FontSize <= 0f) return;
            result.FontSize = settings.FontSize;

            int n = text?.Length ?? 0;
            if (n == 0)
            {
                // An empty label still occupies one line box.
                result.Height = LineHeight(primary, settings);
                return;
            }

            EnsureCapacity(n);
            _simple = IsSimple(text, settings, n);

            long breakStart = AtlasDiagnostics.Now;
            if (_simple)
            {
                // No break opportunities at all: printable ASCII carries no
                // mandatory break, and NoWrap means nothing reads the allowed
                // ones. The paragraph loop below sees one paragraph.
                Array.Clear(_opportunities, 0, n + 1);
            }
            else
            {
            LineBreaker.Analyze(text, _opportunities);
            SuppressNoBreakOpportunities(settings, n);
            // Tailorings, in the order the specs layer them: the Korean rule
            // decides where syllables may break at all, then kinsoku takes away
            // the breaks that would strand punctuation.
            // Thai and its neighbours have no spaces, and UAX #14 explicitly
            // defers them to dictionary analysis — so this is where the gap
            // between "ships Thai shaping" and "ships Thai" closes.
            DictionaryLineBreaker.Apply(text, _opportunities);
            if (settings.KoreanWordWrap) AsianTypography.ApplyKoreanWordWrap(text, _opportunities);
            AsianTypography.ApplyKinsoku(text, _opportunities, settings.Kinsoku);
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.BreakTicks, breakStart);

            long segmentStart = AtlasDiagnostics.Now;
            if (_simple)
            {
                // One character, one grapheme, all the way down.
                _graphemes.Clear();
                for (int i = 0; i <= n; i++) _graphemes.Add(i);
                for (int i = 0; i <= n; i++) _graphemeStart[i] = true;
            }
            else
            {
                TextSegmenter.GraphemeBoundaries(text, _graphemes);
                Array.Clear(_graphemeStart, 0, n + 1);
                foreach (int boundary in _graphemes) _graphemeStart[boundary] = true;
            }

            // Published for anything that has to count units of text a reader
            // would call "one character" — reveal, carets, per-cluster effects.
            // Only the engine knows where these fall, so only the engine can
            // say; reconstructing it from the outside is what every animation
            // layer on TMP had to do, and could not once ligatures appeared.
            result.GraphemeStarts.Clear();
            foreach (int boundary in _graphemes)
                if (boundary < n) result.GraphemeStarts.Add(boundary);
            result.GraphemeStarts.Add(n);
            AtlasDiagnostics.Add(ref AtlasDiagnostics.SegmentTicks, segmentStart);

            float cursorY = 0f;
            for (int paragraphStart = 0; paragraphStart < n;)
            {
                int paragraphEnd = paragraphStart + 1;
                while (paragraphEnd < n && _opportunities[paragraphEnd] != LineBreaker.Opportunity.Mandatory)
                    paragraphEnd++;
                if (paragraphEnd > n) paragraphEnd = n;

                // The newline characters themselves are not rendered.
                int contentEnd = paragraphEnd;
                while (contentEnd > paragraphStart && IsMandatoryBreakChar(text, contentEnd - 1))
                    contentEnd--;

                long itemizeStart = AtlasDiagnostics.Now;
                byte paragraphLevel = BuildItems(text, settings, paragraphStart, contentEnd);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.ItemizeTicks, itemizeStart);

                long wrapStart = AtlasDiagnostics.Now;
                WrapParagraph(text, settings, result, paragraphStart, contentEnd, paragraphLevel, ref cursorY);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.WrapTicks, wrapStart);

                paragraphStart = paragraphEnd;
            }

            result.Height = cursorY;
            foreach (var line in result.Lines)
                if (line.Width > result.Width) result.Width = line.Width;

            long alignStart = AtlasDiagnostics.Now;
            Align(settings, result);
            AtlasDiagnostics.Add(ref AtlasDiagnostics.AlignTicks, alignStart);
        }

        /// <summary>Convenience overload for a single font.</summary>
        public void Layout(string text, FontData font, float fontSize, TextLayoutResult result)
        {
            using var stack = FontStack.Single(font);
            Layout(text, TextLayoutSettings.Default(stack, fontSize), result);
        }

        /// <summary>
        /// Turns off wrapping inside <c>&lt;nobr&gt;</c>.
        ///
        /// This edits the opportunity table rather than teaching the wrapper
        /// about styles, because that is what nobr means: a stretch of text
        /// with no break opportunities inside it. Mandatory breaks survive —
        /// an explicit newline is not a wrapping decision, and swallowing it
        /// would lose the author's text.
        /// </summary>
        private void SuppressNoBreakOpportunities(in TextLayoutSettings settings, int length)
        {
            var spans = settings.Spans;
            if (spans == null) return;

            foreach (var span in spans)
            {
                if (!span.Style.NoBreak) continue;
                // The opportunity AT the span's start is where the line may
                // break before it, which nobr does not forbid; the ones inside
                // it are.
                for (int i = span.Start + 1; i < span.End && i <= length; i++)
                    if (_opportunities[i] == LineBreaker.Opportunity.Allowed)
                        _opportunities[i] = LineBreaker.Opportunity.None;
            }
        }

        /// <summary>
        /// True when the general path's answer is knowable without running it.
        ///
        /// A nameplate reading "427" or "Player 3" still pays UAX #14 break
        /// analysis, Thai dictionary segmentation, kinsoku, UAX #29 grapheme
        /// boundaries and the full bidi algorithm, and every one of those has a
        /// forced answer for text like that. On a scene retexting fifty such
        /// labels a frame those four stages measured 453 of 1,359 us.
        ///
        /// The conditions are deliberately narrow, because the cost of being
        /// wrong here is not a slow label but a wrong one:
        ///
        /// - printable ASCII only, so every character is its own grapheme (no
        ///   combining marks, no surrogates, no ZWJ, no regional indicators),
        ///   carries no mandatory break, and is strongly LTR or neutral;
        /// - a base direction that is not explicitly RTL, so those neutrals
        ///   cannot reorder;
        /// - the primary font covers all of it, so itemization cannot split;
        /// - no style spans, so it cannot split for a colour or a size either;
        /// - NoWrap, which is what lets the opportunity table go unbuilt —
        ///   FindLineEnd returns the paragraph end without reading it.
        ///
        /// Anything else — one Thai character, one emoji, one span, one line
        /// break — takes the general path exactly as before.
        /// </summary>
        private bool IsSimple(string text, in TextLayoutSettings settings, int length)
        {
            if (settings.Wrap != TextWrap.NoWrap) return false;
            if (settings.Spans != null && settings.Spans.Count > 0) return false;
            if (settings.BaseDirection != 0 &&
                settings.BaseDirection != BidiAlgorithm.AutoDirection) return false;

            var primary = settings.Fonts?.Primary;
            if (primary == null) return false;

            for (int i = 0; i < length; i++)
            {
                char c = text[i];
                if (c < ' ' || c > '~') return false;
                if (!primary.HasGlyph(c)) return false;
            }
            return true;
        }

        // Set once per Layout, read by BuildItems. Not a setting: it is a fact
        // about this call's text that two places need and only one can afford
        // to compute.
        private bool _simple;

        /// <summary>
        /// Shapes every item once and records the per-character advances the
        /// wrapper measures with. Shared by both itemization paths: the simple
        /// one still has to shape and still has to measure — all it gets to
        /// skip is deciding where the items are.
        /// </summary>
        private void MeasureItems(string text, in TextLayoutSettings settings)
        {
            _measured.Clear();
            for (int itemIndex = 0; itemIndex < _items.Count; itemIndex++)
            {
                var item = _items[itemIndex];
                item.MeasuredCount = -1;
                if (item.Style.IsSprite)
                {
                    // A sprite is not shaped: there is no glyph to shape. It
                    // gets the advance its picture needs, and the placeholder
                    // character it stands on never reaches a font — which is
                    // the point, because the font almost certainly has no glyph
                    // for U+FFFC and would draw tofu.
                    _advances[item.Start] = SpriteAdvance(settings, item);
                    _items[itemIndex] = item;
                    continue;
                }

                int measuredStart = _measured.Count;
                long measureShapeStart = AtlasDiagnostics.Now;
                _shaper.Shape(item.Font, text, item.Start, item.End - item.Start,
                    (item.Level & 1) != 0 ? Shaper.Direction.RightToLeft : Shaper.Direction.LeftToRight,
                    _measured, settings.Language);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.MeasureShapeTicks, measureShapeStart);
                if (AtlasDiagnostics.Enabled) AtlasDiagnostics.MeasureShapeCount++;
                item.MeasuredStart = measuredStart;
                item.MeasuredCount = _measured.Count - measuredStart;
                _items[itemIndex] = item;

                float scale = item.FontSize / item.Font.UnitsPerEm;
                int trackingUnits = TrackingUnits(item);
                // The same spacing the shaping pass will apply, applied here
                // too. Tracking already learned this lesson: a wrapper that
                // measures one width and a renderer that draws another accepts
                // a line as fitting and then draws it wider than the box.
                for (int g = item.MeasuredStart; g < _measured.Count; g++)
                {
                    var glyph = _measured[g];
                    int cluster = Math.Min(Math.Max(glyph.Cluster, item.Start), item.End - 1);
                    int extra = TrackingFor(glyph, trackingUnits)
                        + AsianSpacingFor(text, settings, item, glyph);
                    _advances[cluster] += (glyph.XAdvance + extra) * scale;

                    // Both edges are costed for every character, because which
                    // ones end up at a line edge is exactly what the wrapper is
                    // about to decide. A give is a width the character does not
                    // need *if* the line breaks there, so it is subtracted from
                    // the candidate line rather than from the advance.
                    //
                    // Behind a flag, not behind the settings check inside the
                    // rule: this is the innermost loop in the engine, and most
                    // projects will never turn 約物詰め on. They should not pay
                    // two multiplies and two array writes per glyph for it.
                    // The range test is the same argument one level down — with
                    // the rule on, the rule still has nothing to say about
                    // nineteen characters in twenty.
                    if (!_edgeGive || !AsianTypography.MayCompress(text[cluster])) continue;
                    _startGive[cluster] +=
                        -LineEdgeCompressionFor(text, settings, item, glyph, LineEdge.Start) * scale;
                    _endGive[cluster] +=
                        -LineEdgeCompressionFor(text, settings, item, glyph, LineEdge.End) * scale;
                }
            }
        }

        private void EnsureCapacity(int length)
        {
            if (_opportunities.Length < length + 1)
                _opportunities = new LineBreaker.Opportunity[length * 2 + 2];
            if (_advances.Length < length + 1)
            {
                _advances = new float[length * 2 + 2];
                _startGive = new float[length * 2 + 2];
                _endGive = new float[length * 2 + 2];
            }
            if (_graphemeStart.Length < length + 1)
                _graphemeStart = new bool[length * 2 + 2];
        }

        // ------------------------------------------------------------ itemization

        /// <summary>
        /// Splits [start, end) into runs of one bidi level and one font, shapes
        /// each for measurement, and records per-character advances.
        /// </summary>
        private byte BuildItems(string text, in TextLayoutSettings settings, int start, int end)
        {
            _items.Clear();
            int span = Math.Max(0, end - start);
            Array.Clear(_advances, start, span);
            if (_edgeGive)
            {
                Array.Clear(_startGive, start, span);
                Array.Clear(_endGive, start, span);
            }
            if (start >= end) return settings.BaseDirection == BidiAlgorithm.AutoDirection
                ? (byte)0 : settings.BaseDirection;

            if (_simple)
            {
                // One font, one level, one style — so one item, and no bidi.
                // The guard has already proved every character is covered by
                // the primary face and cannot reorder.
                _items.Add(new Item
                {
                    Font = settings.Fonts.Primary,
                    Level = 0,
                    Start = start,
                    End = end,
                    Style = TextStyle.Default,
                    FontSize = settings.FontSize,
                });
                MeasureItems(text, settings);
                return 0;
            }

            byte paragraphLevel = BidiRuns.GetLogicalRuns(
                text, start, end - start, settings.BaseDirection, _bidiRuns);

            // Plain text takes the cheap path: no style lookup, no style
            // comparison, no per-character size arithmetic. This is the common
            // case and it has to cost what it cost before markup existed.
            bool styled = settings.Spans != null && settings.Spans.Count > 0;

            foreach (var run in _bidiRuns)
            {
                int runEnd = Math.Min(run.Start + run.Length, end);
                FontData current = null;
                var currentStyle = TextStyle.Default;
                float currentSize = settings.FontSize;
                int itemStart = run.Start;
                for (int i = run.Start; i < runEnd;)
                {
                    int cp = char.ConvertToUtf32(text, i);
                    int width = char.IsHighSurrogate(text[i]) ? 2 : 1;

                    // Font and style are decided per grapheme cluster: marks
                    // must never be split away from the base they attach to,
                    // and neither must the style that base is drawn in.
                    if (_graphemeStart[i] || current == null)
                    {
                        var style = styled ? ResolveStyle(settings, i) : TextStyle.Default;
                        float size = styled ? style.ResolveSize(settings.FontSize) : settings.FontSize;
                        var presentation = PresentationAt(text, i, width, runEnd);
                        var font = styled
                            ? ResolveFont(settings, style, cp, presentation)
                            : settings.Fonts.Resolve(cp, false, false, presentation, settings.Language);
                        if (current == null)
                        {
                            current = font;
                            currentStyle = style;
                            currentSize = size;
                        }
                        // A sprite never merges with its neighbour, even an
                        // identical one. Two <sprite=0> placeholders compare
                        // equal as styles, and merging them gives one advance
                        // and one quad for two icons: a row of repeated icons
                        // silently becomes a single icon on a short line.
                        else if (font != current || style.IsSprite || currentStyle.IsSprite ||
                                 (styled && (size != currentSize || !style.Equals(currentStyle))))
                        {
                            _items.Add(new Item
                            {
                                Font = current, Level = run.Level, Start = itemStart, End = i,
                                Style = currentStyle, FontSize = currentSize,
                            });
                            current = font;
                            currentStyle = style;
                            currentSize = size;
                            itemStart = i;
                        }
                    }

                    i += width;
                }
                if (current != null && itemStart < runEnd)
                    _items.Add(new Item
                    {
                        Font = current, Level = run.Level, Start = itemStart, End = runEnd,
                        Style = currentStyle, FontSize = currentSize,
                    });
            }

            MeasureItems(text, settings);

            return paragraphLevel;
        }

        /// <summary>
        /// The style at a character, with any named style folded in.
        ///
        /// The span list is contiguous and ordered, and itemization walks
        /// forward, so the last span found is almost always the right one —
        /// hence the cursor rather than a binary search per character.
        /// </summary>
        private TextStyle ResolveStyle(in TextLayoutSettings settings, int index)
        {
            var spans = settings.Spans;
            if (spans == null || spans.Count == 0) return TextStyle.Default;

            if (_spanCursor >= spans.Count || index < spans[_spanCursor].Start) _spanCursor = 0;
            while (_spanCursor < spans.Count && index >= spans[_spanCursor].End) _spanCursor++;
            if (_spanCursor >= spans.Count) return TextStyle.Default;

            var style = spans[_spanCursor].Style;
            // Named styles resolve here rather than in the parser, because the
            // parser does not know what a style asset contains — and because a
            // style asset can be edited after the text was parsed.
            if (style.NamedStyle >= 0 && settings.ResolveNamedStyle != null)
                style = settings.ResolveNamedStyle(style.NamedStyle, style);
            return style;
        }

        private int _spanCursor;

        /// <summary>
        /// The font for a codepoint under a style: an explicit
        /// <c>&lt;font&gt;</c> override first, then the stack's own fallback
        /// walk — asked for bold or italic when the style says so.
        /// </summary>
        private static FontData ResolveFont(in TextLayoutSettings settings, in TextStyle style,
            int codepoint, FontStack.Presentation presentation)
        {
            if (style.FontOverride >= 0 && settings.ResolveFontOverride != null)
            {
                var overridden = settings.ResolveFontOverride(style.FontOverride);
                if (overridden != null && overridden.IsValid) return overridden;
            }
            return settings.Fonts.Resolve(codepoint, style.Bold, style.Italic, presentation,
                settings.Language);
        }

        /// <summary>
        /// Reads a variation selector following this character.
        ///
        /// U+FE0E and U+FE0F are how Unicode lets a codepoint that has both a
        /// text and an emoji form say which one is meant — ✔︎ against ✔️, ☺︎
        /// against ☺️. They are not a rendering hint the font resolves; they
        /// decide which font in the stack should get the cluster at all, which
        /// is a decision only the fallback walk can make.
        /// </summary>
        private static FontStack.Presentation PresentationAt(string text, int index, int width, int end)
        {
            int next = index + width;
            if (next >= end || next >= text.Length) return FontStack.Presentation.Any;
            char c = text[next];
            if (c == '\uFE0E') return FontStack.Presentation.Text;
            if (c == '\uFE0F') return FontStack.Presentation.Emoji;
            return FontStack.Presentation.Any;
        }

        // ------------------------------------------------------------- wrapping

        private void WrapParagraph(string text, in TextLayoutSettings settings, TextLayoutResult result,
            int start, int end, byte paragraphLevel, ref float cursorY)
        {
            if (start >= end)
            {
                EmitLine(text, settings, result, start, end, true, false, paragraphLevel, ref cursorY);
                return;
            }

            for (int lineStart = start; lineStart < end;)
            {
                int lineEnd = FindLineEnd(text, settings, lineStart, end);
                bool last = lineEnd >= end;
                if (!EmitLine(text, settings, result, lineStart, lineEnd, last, false, paragraphLevel, ref cursorY))
                    return; // the line did not fit the height budget
                lineStart = lineEnd;
            }
        }

        /// <summary>Greedy line breaking: the last opportunity whose line still fits.</summary>
        private int FindLineEnd(string text, in TextLayoutSettings settings, int lineStart, int end)
        {
            if (settings.Wrap == TextWrap.NoWrap || settings.MaxWidth <= 0f) return end;

            if (MeasureVisible(text, lineStart, end) <= settings.MaxWidth) return end;

            int best = -1;
            float width = 0f;
            for (int i = lineStart + 1; i <= end; i++)
            {
                width += _advances[i - 1];
                if (i >= end || _opportunities[i] != LineBreaker.Opportunity.Allowed) continue;
                if (width - TrailingSpaceWidth(text, lineStart, i) - EdgeGive(lineStart, i)
                    <= settings.MaxWidth) best = i;
                else break;
            }
            if (best > lineStart) return best;

            // Emergency break: no opportunity fits, so break the word itself at
            // the last grapheme cluster boundary that fits (never zero-length).
            //
            // Kinsoku gets a vote here, which it did not before. Walking the
            // break back to a legal boundary is 追い出し — push-out — and it is
            // what a typesetter does with a 。 that lands at a line start.
            int lastFit = -1, lastLegalFit = -1;
            width = 0f;
            for (int i = lineStart + 1; i <= end; i++)
            {
                width += _advances[i - 1];
                if (i >= end || !_graphemeStart[i]) continue;
                if (width > settings.MaxWidth) break;
                lastFit = i;
                if (LegalBreak(text, settings, i)) lastLegalFit = i;
            }
            // But it is a vote, not a veto, and the bound on the walk is the
            // one thing here worth getting right: TMP's famous ！！！！ bug is an
            // unbounded push-out losing the line, and a fixed cap would be a
            // magic number in a milestone whose whole claim is that its rules
            // are published ones. The bound is push-out's own purpose. Giving
            // up width buys a legal line start; if the line we push onto has no
            // legal break inside the box either, it buys nothing, and we have
            // shortened this line to move the same fault one line down. So:
            // walk back only when the walk actually resolves something.
            if (lastLegalFit > lineStart && lastLegalFit != lastFit &&
                !PushOutHelps(text, settings, lastLegalFit, end))
                lastLegalFit = -1;

            if (lastLegalFit > lineStart) return lastLegalFit;
            if (lastFit > lineStart) return lastFit;

            for (int i = lineStart + 1; i <= end; i++)
                if (i == end || _graphemeStart[i]) return i;
            return end;
        }

        /// <summary>
        /// Whether pushing the break back to <paramref name="lineStart"/> gains
        /// anything: can the line starting there end legally inside the box?
        ///
        /// One line of lookahead is enough. Either the pushed-out run fits with
        /// a legal break after it — the ordinary case, a 。 or a 」 that just
        /// needed to travel with the character before it — or the rest of the
        /// text fits entirely, or the run of forbidden characters is wider than
        /// the box, which is the case no rule can win and the one the bug
        /// tracker is full of.
        /// </summary>
        private bool PushOutHelps(string text, in TextLayoutSettings settings, int lineStart, int end)
        {
            float width = 0f;
            for (int i = lineStart + 1; i <= end; i++)
            {
                width += _advances[i - 1];
                if (width > settings.MaxWidth) return false;
                if (i == end) return true;
                if (_graphemeStart[i] && LegalBreak(text, settings, i)) return true;
            }
            return false;
        }

        /// <summary>
        /// True if breaking at <paramref name="index"/> would not violate
        /// kinsoku — the same question <see cref="AsianTypography.ApplyKinsoku"/>
        /// asks of the opportunity table, asked again where there is no
        /// opportunity to consult.
        /// </summary>
        private static bool LegalBreak(string text, in TextLayoutSettings settings, int index)
        {
            if (settings.Kinsoku == AsianTypography.Kinsoku.Off || index <= 0 ||
                index >= text.Length) return true;
            return !AsianTypography.ForbiddenAtLineStart(text[index], settings.Kinsoku) &&
                   !AsianTypography.ForbiddenAtLineEnd(text[index - 1], settings.Kinsoku);
        }

        private float MeasureVisible(string text, int start, int end)
        {
            float width = 0f;
            for (int i = start; i < end; i++) width += _advances[i];
            return width - TrailingSpaceWidth(text, start, end) - EdgeGive(start, end);
        }

        /// <summary>
        /// The width [start, end) would give back by being a line: the blank
        /// half of an opening mark at its start, of a closing mark at its end.
        ///
        /// Read here and applied to the glyphs in <see cref="ShapeRun"/>, both
        /// from <see cref="LineEdgeCompressionFor"/> — the same discipline the
        /// adjacency rule and tracking already follow, because a wrapper that
        /// measures one width and a renderer that draws another is the bug this
        /// engine keeps not having.
        /// </summary>
        private float EdgeGive(int start, int end) =>
            !_edgeGive || end <= start ? 0f : _startGive[start] + _endGive[end - 1];

        private float TrailingSpaceWidth(string text, int start, int end)
        {
            float width = 0f;
            for (int i = end - 1; i >= start && char.IsWhiteSpace(text[i]); i--) width += _advances[i];
            return width;
        }

        // ----------------------------------------------------------- line output

        /// <returns>False if the line was dropped because the box is full.</returns>
        private bool EmitLine(string text, in TextLayoutSettings settings, TextLayoutResult result,
            int start, int end, bool lastInParagraph, bool withEllipsis, byte paragraphLevel,
            ref float cursorY)
        {
            int runStart = result.Runs.Count;
            int glyphStart = result.Glyphs.Count;

            _lineRuns.Clear();
            foreach (var item in _items)
            {
                int s = Math.Max(item.Start, start);
                int e = Math.Min(item.End, end);
                if (s >= e) continue;
                _lineRuns.Add(ShapeRun(text, settings, result, item, s, e, start, end));
            }
            if (withEllipsis)
            {
                _lineRuns.Add(ShapeEllipsis(text, settings, result, paragraphLevel, end));
            }

            float ascent = 0f, descent = 0f, gap = 0f;
            foreach (var run in _lineRuns)
            {
                // Per run, not per label: a <size=200%> word makes its line
                // taller, and a raised one makes it taller still.
                float scale = run.FontSize / run.Font.UnitsPerEm;
                if (run.Style.IsSprite)
                {
                    // A sprite sits on the baseline and rises a full em, which
                    // is taller than most fonts' ascenders. Taking the line's
                    // height from the font the placeholder happened to resolve
                    // to lets the icon poke into the line above.
                    ascent = Math.Max(ascent, run.FontSize + run.BaselineShift);
                    descent = Math.Max(descent, -run.BaselineShift);
                    continue;
                }
                ascent = Math.Max(ascent, run.Font.Ascender * scale + run.BaselineShift);
                descent = Math.Max(descent, -run.Font.Descender * scale - run.BaselineShift);
                gap = Math.Max(gap, run.Font.LineGap * scale);
            }
            if (_lineRuns.Count == 0)
            {
                var font = settings.Fonts.Primary;
                float scale = settings.FontSize / font.UnitsPerEm;
                ascent = font.Ascender * scale;
                descent = -font.Descender * scale;
                gap = font.LineGap * scale;
            }

            float spacing = settings.LineSpacing > 0f ? settings.LineSpacing : 1f;
            float height = (ascent + descent + gap) * spacing;

            // Height budget: keep at least one line, then stop. The re-emitted
            // ellipsis line is exempt — it replaces a line that already fit.
            if (!withEllipsis && settings.MaxHeight > 0f && settings.Overflow != TextOverflow.Overflow &&
                result.Lines.Count > 0 && cursorY + height > settings.MaxHeight)
            {
                result.Runs.RemoveRange(runStart, result.Runs.Count - runStart);
                result.Glyphs.RemoveRange(glyphStart, result.Glyphs.Count - glyphStart);
                result.Truncated = true;
                if (settings.Overflow == TextOverflow.Ellipsis) ApplyEllipsis(text, settings, result);
                return false;
            }

            float visibleWidth = MeasureRuns(_lineRuns) - TrailingSpaceWidth(text, start, end);
            if (AlignmentFor(settings, start) == TextAlignment.Justified && !lastInParagraph &&
                settings.MaxWidth > 0f && visibleWidth < settings.MaxWidth)
            {
                Justify(text, settings, result, settings.MaxWidth - visibleWidth);
                visibleWidth = MeasureRuns(_lineRuns) - TrailingSpaceWidth(text, start, end);
            }

            ReorderVisually(_lineRuns, paragraphLevel);

            float baseline = cursorY + (height - (ascent + descent)) * 0.5f + ascent;
            float x = 0f;
            foreach (var run in _lineRuns)
            {
                var placed = run;
                placed.X = x;
                placed.Baseline = baseline;
                result.Runs.Add(placed);
                x += run.Width;
            }

            result.Lines.Add(new TextLine
            {
                RunStart = runStart,
                RunCount = result.Runs.Count - runStart,
                TextStart = start,
                TextLength = end - start,
                Width = visibleWidth,
                Baseline = baseline,
                Ascent = ascent,
                Descent = descent,
                Height = height,
                ParagraphLevel = paragraphLevel,
                IsParagraphEnd = lastInParagraph,
            });

            cursorY += height;
            return true;
        }

        /// <summary>An inline sprite's advance in render units.</summary>
        private static float SpriteAdvance(in TextLayoutSettings settings, in Item item)
        {
            float aspect = settings.ResolveSpriteAspect?.Invoke(item.Style.Sprite) ?? 1f;
            return item.FontSize * Mathf.Max(0.01f, aspect);
        }

        /// <summary>
        /// Shapes [start, end) of one item. <paramref name="lineStart"/> and
        /// <paramref name="lineEnd"/> are the line this run belongs to, which
        /// the run itself may only be part of — the line-edge punctuation rule
        /// is the one thing here that cannot be decided from the run alone.
        /// </summary>
        private TextRun ShapeRun(string text, in TextLayoutSettings settings, TextLayoutResult result,
            in Item item, int start, int end, int lineStart, int lineEnd)
        {
            if (item.Style.IsSprite)
            {
                // One synthetic glyph so the run has something to place, carrying
                // the advance in font units so every scale downstream still works.
                int glyphIndex = result.Glyphs.Count;
                float advance = SpriteAdvance(settings, item);
                int units = Mathf.RoundToInt(advance / item.FontSize * item.Font.UnitsPerEm);
                result.Glyphs.Add(new ShapedGlyph(0, start, units, 0, 0, 0));
                return new TextRun
                {
                    Font = item.Font,
                    GlyphStart = glyphIndex,
                    GlyphCount = 1,
                    TextStart = start,
                    TextLength = end - start,
                    Width = advance,
                    Level = item.Level,
                    FontSize = item.FontSize,
                    Style = item.Style,
                    BaselineShift = item.Style.BaselineShiftEm * item.FontSize,
                };
            }

            int glyphStart = result.Glyphs.Count;
            // Only when the line took the whole item. A wrapped item is a
            // different shaping problem — the range decides which contextual
            // forms, ligatures and marks HarfBuzz produces, so a slice of the
            // full result is not the same as shaping the slice, and using it
            // would break exactly the scripts this engine exists for. Same
            // font, same range, same direction, same language means the same
            // answer; anything else asks again.
            bool reusable = item.MeasuredCount >= 0 && start == item.Start && end == item.End;
            if (reusable)
            {
                for (int i = 0; i < item.MeasuredCount; i++)
                    result.Glyphs.Add(_measured[item.MeasuredStart + i]);
            }
            else
            {
                long emitShapeStart = AtlasDiagnostics.Now;
                _shaper.Shape(item.Font, text, start, end - start,
                    (item.Level & 1) != 0 ? Shaper.Direction.RightToLeft : Shaper.Direction.LeftToRight,
                    result.Glyphs, settings.Language);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.EmitShapeTicks, emitShapeStart);
                if (AtlasDiagnostics.Enabled) AtlasDiagnostics.EmitShapeCount++;
            }

            float scale = item.FontSize / item.Font.UnitsPerEm;
            // Letter spacing is baked into the glyphs' own advances, exactly as
            // justification is. Adding it to the run's width instead would make
            // the run claim a width its glyphs do not occupy: the text would
            // draw at normal spacing inside a box sized for spaced-out text,
            // which is a trailing gap when left-aligned and a visibly wrong
            // line when right-aligned. It also has to be the same rule the
            // wrapper measured with, or a line can be accepted as fitting and
            // then come out wider.
            // Asian spacing first, then tracking. The order matters because
            // compression is a fraction of an advance, and the measuring pass
            // takes that fraction of the shaper's advance — so this pass has to
            // take it before letter spacing has inflated the number, or a label
            // with both wraps by one width and draws at another.
            ApplyAsianSpacing(text, settings, result, item, glyphStart, lineStart, lineEnd);

            int trackingUnits = TrackingUnits(item);
            if (trackingUnits != 0f)
            {
                for (int i = glyphStart; i < result.Glyphs.Count; i++)
                {
                    var glyph = result.Glyphs[i];
                    int extra = TrackingFor(glyph, trackingUnits);
                    if (extra == 0) continue;
                    result.Glyphs[i] = new ShapedGlyph(glyph.GlyphId, glyph.Cluster,
                        glyph.XAdvance + extra, glyph.YAdvance, glyph.XOffset, glyph.YOffset);
                }
            }

            float width = 0f;
            for (int i = glyphStart; i < result.Glyphs.Count; i++)
                width += result.Glyphs[i].XAdvance * scale;

            return new TextRun
            {
                Font = item.Font,
                GlyphStart = glyphStart,
                GlyphCount = result.Glyphs.Count - glyphStart,
                TextStart = start,
                TextLength = end - start,
                Width = width,
                Level = item.Level,
                FontSize = item.FontSize,
                Style = item.Style,
                BaselineShift = item.Style.BaselineShiftEm * item.FontSize,
            };
        }

        /// <summary>
        /// The two East Asian spacing rules, written into glyph advances so
        /// everything downstream — wrapping, alignment, the caret, the mesh —
        /// sees one consistent width.
        ///
        /// The CJK–Latin gap is what every East Asian layout spec asks for and
        /// people currently type by hand. Punctuation compression is 約物詰め,
        /// which nothing in the Unity ecosystem has: a full-width bracket is a
        /// glyph in half a square box, and two of them in a row leave an em of
        /// white space in the middle of a sentence.
        /// </summary>
        private static void ApplyAsianSpacing(string text, in TextLayoutSettings settings,
            TextLayoutResult result, in Item item, int glyphStart, int lineStart, int lineEnd)
        {
            if (!settings.CjkLatinSpacing && !settings.PunctuationCompression) return;

            for (int i = glyphStart; i < result.Glyphs.Count; i++)
            {
                var glyph = result.Glyphs[i];
                int extra = AsianSpacingFor(text, settings, item, glyph)
                    + LineEdgeCompressionFor(text, settings, item, glyph, EdgeAt(glyph, lineStart, lineEnd));
                if (extra == 0) continue;

                // An opening bracket's ink sits in the right half of its box, so
                // the width it gives up is the blank on its left — and taking it
                // off the advance alone would leave the ink where it was and
                // push the next glyph into it. The offset moves the ink back by
                // the same amount.
                int shift = 0;
                if (extra < 0 && glyph.Cluster >= 0 && glyph.Cluster < text.Length &&
                    AsianTypography.IsOpeningPunctuation(text[glyph.Cluster]))
                {
                    shift = extra;
                }

                result.Glyphs[i] = new ShapedGlyph(glyph.GlyphId, glyph.Cluster,
                    glyph.XAdvance + extra, glyph.YAdvance, glyph.XOffset + shift, glyph.YOffset);
            }
        }

        /// <summary>
        /// The width one glyph gains or loses to the East Asian spacing rules,
        /// in the font's design units.
        ///
        /// Shared by the measuring pass and the shaping pass on purpose: the
        /// only way two passes cannot disagree about a width is for there to be
        /// one function that decides it.
        /// </summary>
        private static int AsianSpacingFor(string text, in TextLayoutSettings settings,
            in Item item, in ShapedGlyph glyph)
        {
            if (!settings.CjkLatinSpacing && !settings.PunctuationCompression) return 0;

            int cluster = glyph.Cluster;
            if (cluster < 0 || cluster >= text.Length) return 0;

            char current = text[cluster];
            char next = cluster + 1 < text.Length ? text[cluster + 1] : '\0';
            char previous = cluster > 0 ? text[cluster - 1] : '\0';

            int extra = 0;
            if (settings.CjkLatinSpacing && next != '\0' &&
                AsianTypography.WantsLatinGap(current, next))
            {
                extra += (int)item.Font.UnitsPerEm / 4;
            }

            if (settings.PunctuationCompression && AsianTypography.IsCompressible(current))
            {
                float give = AsianTypography.CompressionFor(previous, current, next);
                extra -= (int)(glyph.XAdvance * give);
            }
            return extra;
        }

        /// <summary>Which line edge, if either, a character sits on.</summary>
        private enum LineEdge { None, Start, End }

        private static LineEdge EdgeAt(in ShapedGlyph glyph, int lineStart, int lineEnd)
        {
            if (glyph.Cluster == lineStart) return LineEdge.Start;
            if (glyph.Cluster == lineEnd - 1) return LineEdge.End;
            return LineEdge.None;
        }

        /// <summary>
        /// The width a full-width mark gives up for sitting at a line edge, in
        /// the font's design units — 行頭・行末の約物. Negative, like every
        /// other compression here.
        ///
        /// The two halves of the rule do not stack. A mark has one blank half
        /// to surrender, so a 」 that is both preceded by another closing mark
        /// and last on its line gives up half an em, not a whole one — hence
        /// the subtraction rather than a second term. That is also what keeps
        /// this correct at a line *start*, where the adjacency rule was decided
        /// against a neighbour that is now on the previous line: whatever it
        /// concluded, the answer for an opening mark at a line start is half an
        /// em either way.
        /// </summary>
        private static int LineEdgeCompressionFor(string text, in TextLayoutSettings settings,
            in Item item, in ShapedGlyph glyph, LineEdge edge)
        {
            if (!settings.PunctuationCompression || edge == LineEdge.None) return 0;

            // The rule is about the visual edges of the line, and inside an RTL
            // run the logical first character is the visual last one. Rather
            // than compress the wrong end, leave right-to-left runs alone: a
            // full-width CJK mark logically first on a line and visually last
            // is not a case these specs are written about.
            if ((item.Level & 1) != 0) return 0;

            int cluster = glyph.Cluster;
            if (cluster < 0 || cluster >= text.Length) return 0;

            char current = text[cluster];
            float give = edge == LineEdge.Start
                ? AsianTypography.CompressionAtLineStart(current)
                : AsianTypography.CompressionAtLineEnd(current);
            if (give <= 0f) return 0;

            char next = cluster + 1 < text.Length ? text[cluster + 1] : '\0';
            char previous = cluster > 0 ? text[cluster - 1] : '\0';
            give -= AsianTypography.CompressionFor(previous, current, next);
            return give <= 0f ? 0 : -(int)(glyph.XAdvance * give);
        }

        /// <summary>Letter spacing for a run, in the font's design units.</summary>
        private static int TrackingUnits(in Item item) =>
            item.Style.LetterSpacingEm == 0f
                ? 0
                : (int)Math.Round(item.Style.LetterSpacingEm * item.Font.UnitsPerEm);

        /// <summary>
        /// How much spacing a glyph gets. Zero-advance glyphs are marks sitting
        /// on the glyph before them; spacing them would push the mark off its
        /// base. Applying it to advancing glyphs only also makes the rule
        /// identical in the measuring pass and the shaping pass, which is what
        /// stops the wrapper and the renderer disagreeing about a line's width.
        /// </summary>
        private static int TrackingFor(in ShapedGlyph glyph, int trackingUnits) =>
            glyph.XAdvance == 0 ? 0 : trackingUnits;

        private TextRun ShapeEllipsis(string text, in TextLayoutSettings settings, TextLayoutResult result,
            byte paragraphLevel, int textPosition)
        {
            var item = new Item
            {
                Font = settings.Fonts.Resolve(EllipsisText[0]),
                Level = paragraphLevel,
                Start = 0,
                End = EllipsisText.Length,
                Style = TextStyle.Default,
                FontSize = settings.FontSize,
            };
            // No line edge: the indices here are into "…", not into the text,
            // so a line-edge rule keyed on them would be answering a different
            // question. The ellipsis is not a compressible mark in any case.
            var run = ShapeRun(EllipsisText, settings, result, item, 0, EllipsisText.Length, -1, -1);
            run.TextStart = textPosition;
            run.TextLength = 0;
            return run;
        }

        /// <summary>Re-emits the last line shortened by an ellipsis.</summary>
        private void ApplyEllipsis(string text, in TextLayoutSettings settings, TextLayoutResult result)
        {
            if (result.Lines.Count == 0) return;

            var line = result.Lines[result.Lines.Count - 1];
            int firstRun = line.RunStart;
            int firstGlyph = result.Runs[firstRun].GlyphStart;
            result.Runs.RemoveRange(firstRun, result.Runs.Count - firstRun);
            result.Glyphs.RemoveRange(firstGlyph, result.Glyphs.Count - firstGlyph);
            result.Lines.RemoveAt(result.Lines.Count - 1);

            float cursorY = line.Baseline - (line.Height - (line.Ascent + line.Descent)) * 0.5f - line.Ascent;

            int start = line.TextStart;
            int end = start + line.TextLength;
            if (settings.MaxWidth > 0f)
            {
                var ellipsisFont = settings.Fonts.Resolve(EllipsisText[0]);
                float ellipsisWidth = Measure(EllipsisText, settings, ellipsisFont);
                while (end > start &&
                       MeasureVisible(text, start, end) + ellipsisWidth > settings.MaxWidth)
                {
                    end--;
                    while (end > start && !_graphemeStart[end]) end--;
                }
            }

            EmitLine(text, settings, result, start, end, true, true, line.ParagraphLevel, ref cursorY);
        }

        private float Measure(string text, in TextLayoutSettings settings, FontData font)
        {
            _scratch.Clear();
            _shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, _scratch);
            float scale = settings.FontSize / font.UnitsPerEm;
            float width = 0f;
            foreach (var glyph in _scratch) width += glyph.XAdvance * scale;
            return width;
        }

        private static float MeasureRuns(List<TextRun> runs)
        {
            float width = 0f;
            foreach (var run in runs) width += run.Width;
            return width;
        }

        /// <summary>Spreads the slack of a justified line across its spaces.</summary>
        private void Justify(string text, in TextLayoutSettings settings, TextLayoutResult result, float slack)
        {
            int spaces = 0;
            foreach (var run in _lineRuns)
                for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
                    if (IsExpandableSpace(text, result.Glyphs[i].Cluster)) spaces++;
            if (spaces == 0 || slack <= 0f) return;

            float perSpace = slack / spaces;
            for (int r = 0; r < _lineRuns.Count; r++)
            {
                var run = _lineRuns[r];
                float scale = run.FontSize / run.Font.UnitsPerEm;
                int extraUnits = (int)Math.Round(perSpace / scale);
                float added = 0f;
                for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
                {
                    var glyph = result.Glyphs[i];
                    if (!IsExpandableSpace(text, glyph.Cluster)) continue;
                    result.Glyphs[i] = new ShapedGlyph(glyph.GlyphId, glyph.Cluster,
                        glyph.XAdvance + extraUnits, glyph.YAdvance, glyph.XOffset, glyph.YOffset);
                    added += extraUnits * scale;
                }
                run.Width += added;
                _lineRuns[r] = run;
            }
        }

        private static bool IsExpandableSpace(string text, int index) =>
            index >= 0 && index < text.Length && char.IsWhiteSpace(text[index]);

        /// <summary>UAX #9 L2, applied to whole runs: reverse by descending level.</summary>
        private static void ReorderVisually(List<TextRun> runs, byte paragraphLevel)
        {
            if (runs.Count <= 1) return;

            int maxLevel = 0;
            int minOddLevel = byte.MaxValue;
            foreach (var run in runs)
            {
                if (run.Level > maxLevel) maxLevel = run.Level;
                int odd = run.Level | 1;
                if (odd < minOddLevel) minOddLevel = odd;
            }

            for (int level = maxLevel; level >= minOddLevel; level--)
            {
                for (int i = 0; i < runs.Count;)
                {
                    if (runs[i].Level < level) { i++; continue; }
                    int j = i;
                    while (j < runs.Count && runs[j].Level >= level) j++;
                    runs.Reverse(i, j - i);
                    i = j;
                }
            }
        }

        // ----------------------------------------------------------- alignment

        private static void Align(in TextLayoutSettings settings, TextLayoutResult result)
        {
            float box = settings.MaxWidth > 0f ? settings.MaxWidth : result.Width;

            for (int l = 0; l < result.Lines.Count; l++)
            {
                var line = result.Lines[l];
                float slack = box - line.Width;
                // <align> is a line property, so it is applied here rather than
                // carried on runs: alignment is the one thing markup changes
                // that no run needs to know about.
                var alignment = AlignmentFor(settings, line.TextStart);
                float offset = alignment switch
                {
                    TextAlignment.Left => 0f,
                    TextAlignment.Right => slack,
                    TextAlignment.Center => slack * 0.5f,
                    TextAlignment.Start => line.IsRightToLeft ? slack : 0f,
                    TextAlignment.End => line.IsRightToLeft ? 0f : slack,
                    // A justified line already fills the box; its last line falls
                    // back to the paragraph's start edge.
                    TextAlignment.Justified => line.IsParagraphEnd && line.IsRightToLeft ? slack : 0f,
                    _ => 0f,
                };
                if (offset == 0f) continue;

                for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
                {
                    var run = result.Runs[r];
                    run.X += offset;
                    result.Runs[r] = run;
                }
            }
        }

        private static TextAlignment AlignmentFor(in TextLayoutSettings settings, int textStart)
        {
            var alignment = settings.Alignment;
            var overrides = settings.Alignments;
            if (overrides == null) return alignment;
            foreach (var entry in overrides)
            {
                if (entry.Start > textStart) break;
                // A negative alignment is </align>: back to the label's own,
                // not to some other override.
                alignment = (int)entry.Alignment < 0 ? settings.Alignment : entry.Alignment;
            }
            return alignment;
        }

        private static float LineHeight(FontData font, in TextLayoutSettings settings)
        {
            float scale = settings.FontSize / font.UnitsPerEm;
            float spacing = settings.LineSpacing > 0f ? settings.LineSpacing : 1f;
            return (font.Ascender - font.Descender + font.LineGap) * scale * spacing;
        }

        private static bool IsMandatoryBreakChar(string text, int index)
        {
            var cls = BreakData.GetLineClass(text[index]);
            return cls == LineBreakClass.BK || cls == LineBreakClass.CR ||
                   cls == LineBreakClass.LF || cls == LineBreakClass.NL;
        }

        public void Dispose()
        {
            _shaper?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
