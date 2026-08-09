using System.Collections.Generic;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// Fills a <see cref="OneTextTextInfo"/> from a label's finished layout and
    /// drawn tiles.
    ///
    /// Everything here is a translation, not a computation: the numbers already
    /// exist in <see cref="TextLayoutResult"/> and <see cref="TextQuad"/>, and
    /// this only re-expresses them in the shape TextMesh Pro's callers expect.
    /// Nothing in the engine consults this type, so a project that never touches
    /// <c>textInfo</c> never pays for it.
    /// </summary>
    internal static class OneTextTextInfoBuilder
    {
        /// <summary>
        /// Rebuild <paramref name="info"/> to describe <paramref name="label"/>
        /// as it is drawn right now.
        /// </summary>
        internal static void Build(OneTextLabel label, OneTextTextInfo info)
        {
            var layout = label.EnsureLayout();
            var quads = label.DrawnQuads;
            string text = label.text ?? string.Empty;

            int graphemes = layout.GraphemeCount;
            info.EnsureCharacters(graphemes);

            // Which drawn tile speaks for each cluster. Solid bars (underline,
            // strikethrough, a <mark> wash) cover the same clusters as the
            // letters they sit under, so they are considered only after the
            // letters have had their turn — otherwise moving "character 3"
            // would move the underline and leave the letter behind.
            var owner = new int[graphemes];
            for (int i = 0; i < graphemes; i++) owner[i] = -1;
            Claim(quads, owner, solid: false);
            Claim(quads, owner, solid: true);

            var mesh = info.meshInfo[0];
            mesh.Resize(quads.Count * 4);

            for (int g = 0; g < graphemes; g++)
            {
                ref var c = ref info.characterInfo[g];
                int start = layout.GraphemeStarts[g];
                int end = g + 1 < layout.GraphemeStarts.Count
                    ? layout.GraphemeStarts[g + 1]
                    : text.Length;

                c.index = start;
                c.stringLength = Mathf.Max(0, end - start);
                c.character = start >= 0 && start < text.Length ? text[start] : '\0';
                c.materialReferenceIndex = 0;
                c.lineNumber = LineOf(layout, start);

                var line = c.lineNumber >= 0 && c.lineNumber < layout.Lines.Count
                    ? layout.Lines[c.lineNumber]
                    : default;
                var baselinePoint = label.LayoutToLocal(new Vector2(0f, line.Baseline));
                c.baseLine = baselinePoint.y;
                c.ascender = c.baseLine + line.Ascent;
                c.descender = c.baseLine - line.Descent;

                int q = owner[g];
                if (q < 0)
                {
                    c.isVisible = false;
                    c.vertexIndex = 0;
                    c.pointSize = layout.FontSize;
                    c.scale = 1f;
                    c.color = default;
                    c.bottomLeft = c.topLeft = c.topRight = c.bottomRight = Vector3.zero;
                    c.origin = 0f;
                    c.aspectRatio = 0f;
                    continue;
                }

                var quad = quads[q];
                c.isVisible = true;
                c.vertexIndex = q * 4;
                c.color = quad.Color;
                c.origin = quad.Position.x;

                var run = quad.RunIndex >= 0 && quad.RunIndex < layout.Runs.Count
                    ? layout.Runs[quad.RunIndex]
                    : default;
                c.pointSize = run.FontSize > 0f ? run.FontSize : layout.FontSize;
                c.scale = layout.FontSize > 0f ? c.pointSize / layout.FontSize : 1f;
                c.aspectRatio = quad.Size.y > 0f ? quad.Size.x / quad.Size.y : 0f;

                Corners(quad, out c.bottomLeft, out c.topLeft, out c.topRight, out c.bottomRight);
            }

            BuildMesh(quads, mesh);
            BuildLines(layout, label, info);
            BuildWords(text, layout, info);
            BuildLinks(label, layout, info);

            info.spriteCount = 0;
            info.pageCount = 1;
        }

        /// <summary>
        /// Give every cluster the first tile that draws it, considering only
        /// tiles of one kind. A merged tile claims each cluster it covers, so a
        /// ligature's clusters all point at the one tile that draws them.
        /// </summary>
        private static void Claim(IReadOnlyList<TextQuad> quads, int[] owner, bool solid)
        {
            for (int i = 0; i < quads.Count; i++)
            {
                var q = quads[i];
                if (q.IsSolid != solid) continue;
                int first = Mathf.Max(0, q.FirstGrapheme);
                int last = Mathf.Min(owner.Length - 1, q.LastGrapheme);
                for (int g = first; g <= last; g++)
                    if (owner[g] < 0) owner[g] = i;
            }
        }

        /// <summary>
        /// The four corners in the order the mesh writes them — bottom-left,
        /// top-left, top-right, bottom-right — which is also TextMesh Pro's
        /// order, so an animator that indexes <c>vertexIndex + 2</c> for the top
        /// right corner finds the top right corner.
        /// </summary>
        private static void Corners(in TextQuad quad,
            out Vector3 bottomLeft, out Vector3 topLeft, out Vector3 topRight, out Vector3 bottomRight)
        {
            if (Mathf.Approximately(quad.Rotation, 0f))
            {
                float x = quad.Position.x, y = quad.Position.y;
                float w = quad.Size.x, h = quad.Size.y;
                bottomLeft = new Vector3(x, y);
                topLeft = new Vector3(x, y + h);
                topRight = new Vector3(x + w, y + h);
                bottomRight = new Vector3(x + w, y);
                return;
            }

            float radians = quad.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
            Vector2 centre = quad.Center, half = quad.Size * 0.5f;

            Vector3 Corner(float sx, float sy)
            {
                float x = sx * half.x, y = sy * half.y;
                return centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            }

            bottomLeft = Corner(-1f, -1f);
            topLeft = Corner(-1f, 1f);
            topRight = Corner(1f, 1f);
            bottomRight = Corner(1f, -1f);
        }

        private static void BuildMesh(IReadOnlyList<TextQuad> quads, OneTextMeshInfo mesh)
        {
            for (int i = 0; i < quads.Count; i++)
            {
                var q = quads[i];
                Corners(q, out var bl, out var tl, out var tr, out var br);
                int v = i * 4;
                mesh.vertices[v + 0] = bl;
                mesh.vertices[v + 1] = tl;
                mesh.vertices[v + 2] = tr;
                mesh.vertices[v + 3] = br;

                mesh.colors32[v + 0] = q.Color;
                mesh.colors32[v + 1] = q.Color;
                mesh.colors32[v + 2] = q.Color;
                mesh.colors32[v + 3] = q.Color;

                var uv = q.UvRect;
                mesh.uvs0[v + 0] = new Vector2(uv.xMin, uv.yMin);
                mesh.uvs0[v + 1] = new Vector2(uv.xMin, uv.yMax);
                mesh.uvs0[v + 2] = new Vector2(uv.xMax, uv.yMax);
                mesh.uvs0[v + 3] = new Vector2(uv.xMax, uv.yMin);
            }
        }

        private static void BuildLines(TextLayoutResult layout, OneTextLabel label, OneTextTextInfo info)
        {
            info.EnsureLines(layout.Lines.Count);
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                ref var l = ref info.lineInfo[i];
                l.width = line.Width;
                l.length = line.TextLength;
                l.lineHeight = line.Height;

                float baseline = label.LayoutToLocal(new Vector2(0f, line.Baseline)).y;
                l.baseline = baseline;
                l.ascender = baseline + line.Ascent;
                l.descender = baseline - line.Descent;

                l.firstCharacterIndex = GraphemeAt(layout, line.TextStart);
                int lastUnit = line.TextStart + Mathf.Max(0, line.TextLength - 1);
                l.lastCharacterIndex = GraphemeAt(layout, lastUnit);
                l.characterCount = Mathf.Max(0, l.lastCharacterIndex - l.firstCharacterIndex + 1);
            }
        }

        /// <summary>
        /// Words as stretches of non-whitespace, counted in clusters.
        ///
        /// The engine has no word concept of its own — line breaking asks ICU
        /// rules a question this cannot answer — so this is the plain reading,
        /// and it is the same reading TMP's word info gives for the scripts
        /// that separate words with spaces. For Thai or Japanese it will say
        /// one word where a reader sees several; that is a limit of the
        /// question, not of the answer.
        /// </summary>
        private static void BuildWords(string text, TextLayoutResult layout, OneTextTextInfo info)
        {
            var words = new List<OneTextWordInfo>();
            int graphemes = layout.GraphemeCount;
            int g = 0;
            while (g < graphemes)
            {
                int start = layout.GraphemeStarts[g];
                if (start >= text.Length) break;
                if (char.IsWhiteSpace(text[start])) { g++; continue; }

                int firstGrapheme = g;
                int lastGrapheme = g;
                while (g < graphemes)
                {
                    int at = layout.GraphemeStarts[g];
                    if (at >= text.Length || char.IsWhiteSpace(text[at])) break;
                    lastGrapheme = g;
                    g++;
                }

                int from = layout.GraphemeStarts[firstGrapheme];
                int to = lastGrapheme + 1 < layout.GraphemeStarts.Count
                    ? layout.GraphemeStarts[lastGrapheme + 1]
                    : text.Length;
                words.Add(new OneTextWordInfo
                {
                    firstCharacterIndex = firstGrapheme,
                    lastCharacterIndex = lastGrapheme,
                    characterCount = lastGrapheme - firstGrapheme + 1,
                    _word = text.Substring(from, Mathf.Max(0, to - from)),
                });
            }
            info.SetWords(words);
        }

        private static void BuildLinks(OneTextLabel label, TextLayoutResult layout, OneTextTextInfo info)
        {
            var links = new List<OneTextLinkInfo>();
            var source = label.Links;
            string text = label.text ?? string.Empty;
            for (int i = 0; i < source.Count; i++)
            {
                var link = source[i];
                int first = GraphemeAt(layout, link.Start);
                int last = GraphemeAt(layout, Mathf.Max(link.Start, link.End - 1));
                int from = Mathf.Clamp(link.Start, 0, text.Length);
                int to = Mathf.Clamp(link.End, from, text.Length);
                links.Add(new OneTextLinkInfo
                {
                    linkIdFirstCharacterIndex = first,
                    linkIdLength = link.Id?.Length ?? 0,
                    linkTextfirstCharacterIndex = first,
                    linkTextLength = Mathf.Max(0, last - first + 1),
                    _id = link.Id,
                    _text = text.Substring(from, to - from),
                });
            }
            info.SetLinks(links);
        }

        /// <summary>Cluster index containing a UTF-16 offset.</summary>
        private static int GraphemeAt(TextLayoutResult layout, int unit)
        {
            var starts = layout.GraphemeStarts;
            int count = layout.GraphemeCount;
            if (count <= 0) return 0;
            int low = 0, high = count - 1, best = 0;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (starts[mid] <= unit) { best = mid; low = mid + 1; }
                else high = mid - 1;
            }
            return best;
        }

        private static int LineOf(TextLayoutResult layout, int unit)
        {
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                if (unit >= line.TextStart && unit < line.TextStart + line.TextLength) return i;
            }
            return layout.Lines.Count > 0 ? layout.Lines.Count - 1 : 0;
        }
    }
}
