using System.Collections.Generic;
using OneText.Unicode;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Geometry queries over a finished layout: which character is under a
    /// point, where the caret sits for a character, and which rectangles a
    /// selection covers. Everything is in layout space — x from the block's
    /// left edge, y downward from its top edge.
    ///
    /// Caret positions are grapheme cluster boundaries: a caret never lands
    /// inside "é" or inside a family emoji.
    /// </summary>
    public static class TextHitTest
    {
        /// <summary>Index of the line containing <paramref name="y"/> (clamped).</summary>
        public static int GetLineAt(TextLayoutResult layout, float y)
        {
            if (layout.Lines.Count == 0) return -1;
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                float top = line.Baseline - line.Ascent;
                if (y < top + line.Height) return i;
            }
            return layout.Lines.Count - 1;
        }

        /// <summary>
        /// The text index closest to <paramref name="point"/> — the caret
        /// position a click there should produce.
        /// </summary>
        public static int GetIndexAtPoint(TextLayoutResult layout, Vector2 point)
        {
            int lineIndex = GetLineAt(layout, point.y);
            if (lineIndex < 0) return 0;

            var line = layout.Lines[lineIndex];
            if (line.RunCount == 0) return line.TextStart;

            var firstRun = layout.Runs[line.RunStart];
            var lastRun = layout.Runs[line.RunStart + line.RunCount - 1];
            if (point.x <= firstRun.X) return EdgeIndex(firstRun, leading: true);
            if (point.x >= lastRun.X + lastRun.Width) return EdgeIndex(lastRun, leading: false);

            for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
            {
                var run = layout.Runs[r];
                if (point.x < run.X || point.x > run.X + run.Width) continue;
                return IndexInRun(layout, run, point.x);
            }
            return line.TextStart;
        }

        /// <summary>
        /// Caret rectangle for <paramref name="index"/>: a
        /// <paramref name="width"/>-wide bar spanning the line's height.
        /// </summary>
        public static Rect GetCaretRect(TextLayoutResult layout, int index, float width)
        {
            if (layout.Lines.Count == 0) return new Rect(0f, 0f, width, 0f);

            int lineIndex = GetLineForIndex(layout, index);
            var line = layout.Lines[lineIndex];
            float x = GetCaretX(layout, line, index);
            return new Rect(x - width * 0.5f, line.Baseline - line.Ascent, width, line.Ascent + line.Descent);
        }

        /// <summary>Line containing <paramref name="index"/> (its caret position).</summary>
        public static int GetLineForIndex(TextLayoutResult layout, int index)
        {
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                if (index < line.TextStart + line.TextLength) return i;
            }
            return Mathf.Max(0, layout.Lines.Count - 1);
        }

        /// <summary>
        /// Fills <paramref name="rects"/> with one rectangle per line covered
        /// by the text range [<paramref name="start"/>, <paramref name="end"/>).
        /// </summary>
        public static void GetSelectionRects(TextLayoutResult layout, int start, int end, List<Rect> rects)
        {
            rects.Clear();
            if (start == end) return;
            if (start > end) (start, end) = (end, start);

            foreach (var line in layout.Lines)
            {
                int lineEnd = line.TextStart + line.TextLength;
                int from = Mathf.Max(start, line.TextStart);
                int to = Mathf.Min(end, lineEnd);
                if (from >= to) continue;

                // Visual order can differ from logical order, so take the
                // extent of every run piece the range touches.
                float min = float.MaxValue, max = float.MinValue;
                for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
                {
                    var run = layout.Runs[r];
                    int runEnd = run.TextStart + run.TextLength;
                    int s = Mathf.Max(from, run.TextStart);
                    int e = Mathf.Min(to, runEnd);
                    if (s >= e) continue;

                    float x0 = GetCaretXInRun(layout, run, s);
                    float x1 = GetCaretXInRun(layout, run, e);
                    min = Mathf.Min(min, Mathf.Min(x0, x1));
                    max = Mathf.Max(max, Mathf.Max(x0, x1));
                }
                if (min > max) continue;

                rects.Add(new Rect(min, line.Baseline - line.Ascent, max - min, line.Ascent + line.Descent));
            }
        }

        /// <summary>
        /// Moves the caret one line up (<paramref name="direction"/> = -1) or
        /// down (+1), keeping <paramref name="desiredX"/> — the x the caret had
        /// when vertical movement started, so a run of up/down keys does not
        /// drift toward short lines.
        /// </summary>
        public static int MoveVertically(TextLayoutResult layout, int index, int direction, float desiredX)
        {
            if (layout.Lines.Count == 0) return index;
            int line = GetLineForIndex(layout, index) + direction;
            if (line < 0 || line >= layout.Lines.Count) return index;
            return GetIndexAtPoint(layout, new Vector2(desiredX, layout.Lines[line].Baseline));
        }

        // ------------------------------------------------------- caret movement

        /// <summary>Next caret position after <paramref name="index"/> (one grapheme cluster).</summary>
        public static int NextCaret(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index >= text.Length) return text?.Length ?? 0;
            var boundaries = Boundaries(text);
            foreach (int boundary in boundaries)
                if (boundary > index) return boundary;
            return text.Length;
        }

        /// <summary>Previous caret position before <paramref name="index"/>.</summary>
        public static int PreviousCaret(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index <= 0) return 0;
            var boundaries = Boundaries(text);
            int previous = 0;
            foreach (int boundary in boundaries)
            {
                if (boundary >= index) break;
                previous = boundary;
            }
            return previous;
        }

        /// <summary>Start of the next word, for ctrl/alt-arrow movement.</summary>
        public static int NextWord(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index >= text.Length) return text?.Length ?? 0;
            var boundaries = WordBoundaries(text);
            foreach (int boundary in boundaries)
                if (boundary > index && !IsSpaceRun(text, boundary)) return boundary;
            return text.Length;
        }

        /// <summary>Start of the word before <paramref name="index"/>.</summary>
        public static int PreviousWord(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index <= 0) return 0;
            var boundaries = WordBoundaries(text);
            int previous = 0;
            foreach (int boundary in boundaries)
            {
                if (boundary >= index) break;
                if (!IsSpaceRun(text, boundary)) previous = boundary;
            }
            return previous;
        }

        /// <summary>Word range containing <paramref name="index"/> (double-click selection).</summary>
        public static void GetWordAt(string text, int index, out int start, out int end)
        {
            start = 0;
            end = text?.Length ?? 0;
            if (string.IsNullOrEmpty(text)) return;

            var boundaries = WordBoundaries(text);
            for (int i = 0; i < boundaries.Count - 1; i++)
            {
                if (index >= boundaries[i] && index < boundaries[i + 1])
                {
                    start = boundaries[i];
                    end = boundaries[i + 1];
                    return;
                }
            }
            start = boundaries.Count > 1 ? boundaries[boundaries.Count - 2] : 0;
            end = text.Length;
        }

        [System.ThreadStatic] private static List<int> t_graphemes;
        [System.ThreadStatic] private static List<int> t_words;

        private static List<int> Boundaries(string text)
        {
            var list = t_graphemes ??= new List<int>();
            TextSegmenter.GraphemeBoundaries(text, list);
            return list;
        }

        private static List<int> WordBoundaries(string text)
        {
            var list = t_words ??= new List<int>();
            TextSegmenter.WordBoundaries(text, list);
            return list;
        }

        private static bool IsSpaceRun(string text, int index) =>
            index < text.Length && char.IsWhiteSpace(text[index]);

        // -------------------------------------------------------------- internals

        private static int EdgeIndex(TextRun run, bool leading)
        {
            bool logicalStart = leading != run.IsRightToLeft;
            return logicalStart ? run.TextStart : run.TextStart + run.TextLength;
        }

        private static float GetCaretX(TextLayoutResult layout, TextLine line, int index)
        {
            if (line.RunCount == 0)
                return line.IsRightToLeft ? line.Width : 0f;

            for (int r = line.RunStart; r < line.RunStart + line.RunCount; r++)
            {
                var run = layout.Runs[r];
                if (index >= run.TextStart && index < run.TextStart + run.TextLength)
                    return GetCaretXInRun(layout, run, index);
            }

            // At (or past) the end of the line: use the logically last run's end.
            var lastLogical = layout.Runs[line.RunStart];
            for (int r = line.RunStart + 1; r < line.RunStart + line.RunCount; r++)
                if (layout.Runs[r].TextStart > lastLogical.TextStart) lastLogical = layout.Runs[r];
            int clamped = Mathf.Clamp(index, lastLogical.TextStart,
                lastLogical.TextStart + lastLogical.TextLength);
            return GetCaretXInRun(layout, lastLogical, clamped);
        }

        /// <summary>X of the caret that precedes <paramref name="index"/> inside one run.</summary>
        private static float GetCaretXInRun(TextLayoutResult layout, TextRun run, int index)
        {
            float scale = ScaleOf(layout, run);
            float offset = 0f;
            for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
            {
                var glyph = layout.Glyphs[g];
                // Glyphs come out of shaping in visual order: for an RTL run the
                // characters after the caret are the ones drawn to its left.
                bool before = run.IsRightToLeft ? glyph.Cluster >= index : glyph.Cluster < index;
                if (before) offset += glyph.XAdvance * scale;
            }
            return run.X + offset;
        }

        private static int IndexInRun(TextLayoutResult layout, TextRun run, float x)
        {
            float scale = ScaleOf(layout, run);
            float pen = run.X;
            for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
            {
                var glyph = layout.Glyphs[g];
                float advance = glyph.XAdvance * scale;
                if (x <= pen + advance)
                {
                    bool secondHalf = x > pen + advance * 0.5f;
                    int cluster = glyph.Cluster;
                    int next = NextCluster(layout, run, g, cluster);
                    return run.IsRightToLeft
                        ? (secondHalf ? cluster : next)
                        : (secondHalf ? next : cluster);
                }
                pen += advance;
            }
            return run.IsRightToLeft ? run.TextStart : run.TextStart + run.TextLength;
        }

        /// <summary>The text index just past the cluster drawn by glyph <paramref name="g"/>.</summary>
        private static int NextCluster(TextLayoutResult layout, TextRun run, int g, int cluster)
        {
            if (run.IsRightToLeft)
            {
                // Clusters descend through an RTL run; the next one leftward is
                // the previous glyph's cluster.
                for (int i = g - 1; i >= run.GlyphStart; i--)
                    if (layout.Glyphs[i].Cluster != cluster) return layout.Glyphs[i].Cluster;
                return run.TextStart;
            }
            for (int i = g + 1; i < run.GlyphStart + run.GlyphCount; i++)
                if (layout.Glyphs[i].Cluster != cluster) return layout.Glyphs[i].Cluster;
            return run.TextStart + run.TextLength;
        }

        /// <summary>Render units per font design unit for this run.</summary>
        private static float ScaleOf(TextLayoutResult layout, TextRun run) =>
            run.Font == null || run.Font.UnitsPerEm == 0
                ? 0f
                : layout.FontSize / run.Font.UnitsPerEm;
    }
}
