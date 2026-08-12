using System.Collections.Generic;
using System.Text;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// A character is not a glyph.
    ///
    /// This is the claim everything else in the library rests on, and the one
    /// that is hardest to make anybody feel. "Supports Arabic" is a checkbox.
    /// What it actually means is that four Arabic letters written one after
    /// another are drawn as three shapes, that each letter has a different form
    /// depending on what is beside it, and that the sequence runs right to
    /// left while the string in memory does not.
    ///
    /// So the page shows the same string twice. On the left, each character is
    /// drawn as its own glyph in memory order — which is exactly what a
    /// renderer without a shaping engine produces, and is what you get from
    /// anything that treats a string as an array of characters to look up in a
    /// table. On the right, the shaped result. Nobody needs to read Arabic to
    /// see which one is broken.
    ///
    /// Underneath, the mechanism: every glyph the engine produced, with the
    /// source characters it came from. That table is the whole argument in
    /// numeric form — rows where several characters collapse into one glyph are
    /// ligatures, rows where the same character appears with different glyph
    /// ids are contextual forms, and the order of the rows against the order of
    /// the string is bidi.
    ///
    /// The table is not computed here. <see cref="ShapedGlyph.Cluster"/> is
    /// what HarfBuzz reported and what the engine already carries; this page
    /// only reads <see cref="OneTextLabel.LayoutResult"/>.
    /// </summary>
    internal sealed class ShapingPage : DemoPage
    {
        private readonly struct Specimen
        {
            internal readonly string Name;
            internal readonly string Text;
            internal readonly string Note;

            internal Specimen(string name, string text, string note)
            {
                Name = name;
                Text = text;
                Note = note;
            }
        }

        private static readonly Specimen[] Samples =
        {
            new Specimen("Arabic", "العربية",
                "Every letter has four forms. Which one is drawn depends on its neighbours, " +
                "and the run reads right to left."),
            new Specimen("Thai", "ภาษาไทย",
                "Vowels and tone marks stack above and below the consonant they belong to, " +
                "and there are no spaces between words."),
            new Specimen("Devanagari", "हिन्दी",
                "Consonant clusters fuse into conjuncts, and one vowel sign is written " +
                "before the consonant it is pronounced after."),
            new Specimen("Hangul", "한국어",
                "Three jamo compose into one syllable block — and the composed block is a " +
                "single glyph, not three stacked pieces."),
        };

        private int _index;
        private OneTextLabel _shaped;
        private OneTextLabel _note;
        private OneTextLabel _table;
        private RectTransform _naiveRow;
        private readonly List<OneTextLabel> _naive = new List<OneTextLabel>();
        private readonly List<int> _starts = new List<int>();
        private readonly StringBuilder _scratch = new StringBuilder(1024);

        internal override string Title => "Shaping";

        internal override string Claim =>
            "A character is not a glyph. Turn shaping off and the same string comes apart.";

        protected override void Build(RectTransform host)
        {
            var picker = DemoUi.Rect("picker", host);
            picker.anchorMin = new Vector2(0f, 1f);
            picker.anchorMax = new Vector2(1f, 1f);
            picker.pivot = new Vector2(0f, 1f);
            picker.sizeDelta = new Vector2(0f, 26f);
            var row = picker.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = false;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.spacing = 4f;
            for (int i = 0; i < Samples.Length; i++)
            {
                int captured = i;
                DemoUi.Button(Samples[i].Name, picker, Fonts, () => Select(captured), 112f);
            }

            // Left: no shaping. Right: shaping. Same string, same face, same
            // size — the only difference is whether the characters were allowed
            // to see each other.
            var wrong = Panel(host, "without shaping", 0f, 0.5f);
            var right = Panel(host, "with shaping", 0.5f, 1f);

            _naiveRow = DemoUi.Rect("chars", wrong);
            _naiveRow.anchorMin = new Vector2(0f, 0.5f);
            _naiveRow.anchorMax = new Vector2(1f, 0.5f);
            _naiveRow.pivot = new Vector2(0.5f, 0.5f);
            _naiveRow.sizeDelta = new Vector2(-24f, 120f);
            var naiveGroup = _naiveRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            naiveGroup.childControlWidth = false;
            naiveGroup.childControlHeight = true;
            naiveGroup.childForceExpandWidth = false;
            naiveGroup.childAlignment = TextAnchor.MiddleCenter;
            naiveGroup.spacing = 0f;

            _shaped = DemoUi.Label("shaped", right, string.Empty, 72f, DemoUi.Ink, Fonts);
            var shapedRect = DemoUi.Fill((RectTransform)_shaped.transform, 12f);
            shapedRect.anchorMin = new Vector2(0f, 0.5f);
            shapedRect.anchorMax = new Vector2(1f, 0.5f);
            shapedRect.sizeDelta = new Vector2(-24f, 120f);
            _shaped.Alignment = TextAlignment.Center;
            _shaped.VerticalAlignment = VerticalAlignment.Middle;
            _shaped.Wrap = TextWrap.NoWrap;

            _note = DemoUi.Label("note", host, string.Empty, DemoUi.Caption, DemoUi.Dim, Fonts);
            var noteRect = (RectTransform)_note.transform;
            noteRect.anchorMin = new Vector2(0f, 0f);
            noteRect.anchorMax = new Vector2(1f, 0f);
            noteRect.pivot = new Vector2(0f, 0f);
            noteRect.anchoredPosition = new Vector2(4f, 4f);
            noteRect.sizeDelta = new Vector2(-8f, 40f);

            var tablePanel = DemoUi.PanelWithTitle("table", host,
                "what the shaper produced · glyph id ← source characters", Fonts);
            var tableRect = (RectTransform)tablePanel.parent;
            tableRect.anchorMin = new Vector2(0f, 0f);
            tableRect.anchorMax = new Vector2(1f, 0.44f);
            tableRect.offsetMin = new Vector2(0f, 48f);
            tableRect.offsetMax = new Vector2(0f, 0f);

            _table = DemoUi.Label("rows", tablePanel, string.Empty, DemoUi.Caption, DemoUi.Ink, Fonts);
            DemoUi.Fill((RectTransform)_table.transform, 10f);
            _table.Wrap = TextWrap.NoWrap;

            Select(0);
        }

        private RectTransform Panel(RectTransform host, string title, float x0, float x1)
        {
            var column = DemoUi.Rect(title, host);
            column.anchorMin = new Vector2(x0, 0.46f);
            column.anchorMax = new Vector2(x1, 1f);
            column.offsetMin = new Vector2(4f, 4f);
            column.offsetMax = new Vector2(-4f, -32f);
            return DemoUi.PanelWithTitle("panel", column, title, Fonts);
        }

        private void Select(int index)
        {
            _index = Mathf.Clamp(index, 0, Samples.Length - 1);
            var sample = Samples[_index];

            _shaped.Text = sample.Text;
            _note.Text = sample.Note;
            BuildNaive(sample.Text);
            BuildTable(sample.Text);
        }

        /// <summary>
        /// One label per UTF-16 code unit, in memory order.
        ///
        /// Each label shapes its own single character, which for an isolated
        /// letter means the isolated form — so Arabic comes out as a row of
        /// disconnected letters, Devanagari's vowel sign sits after the
        /// consonant it should precede, and Thai's marks land beside their base
        /// rather than on it. This is not a caricature: it is what a renderer
        /// that maps characters to glyphs one at a time can produce, and it is
        /// why "we have a font atlas" is not the same as "we can draw text".
        /// </summary>
        private void BuildNaive(string text)
        {
            for (int i = 0; i < _naive.Count; i++)
                if (_naive[i] != null) Object.Destroy(_naive[i].gameObject);
            _naive.Clear();

            foreach (char c in text)
            {
                var label = DemoUi.Label("c", _naiveRow, c.ToString(), 72f, DemoUi.Bad, Fonts);
                label.Alignment = TextAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Middle;
                label.Wrap = TextWrap.NoWrap;
                var element = label.gameObject.AddComponent<LayoutElement>();
                element.preferredWidth = 56f;
                _naive.Add(label);
            }
        }

        /// <summary>
        /// The glyph stream, with the characters each glyph came from.
        ///
        /// Glyphs sharing a cluster are one row: that is a ligature or a
        /// composed syllable, several characters that became one shape. A
        /// character appearing under two different glyph ids across specimens
        /// is a contextual form. Reading the cluster column downwards against
        /// the string tells you the direction the run was laid out in.
        /// </summary>
        private void BuildTable(string text)
        {
            var layout = _shaped.EnsureLayout();
            _scratch.Clear();
            _scratch.Append("<mspace=0.62em>");
            _scratch.Append("glyph   cluster  characters\n");

            var glyphs = layout.Glyphs;

            // A cluster value is where the run of characters behind a glyph
            // starts; nothing records where it ends. It ends where the next one
            // begins, so the extents come from the sorted set of distinct
            // values and not from the glyph order — which matters because in a
            // right-to-left run the glyph order is descending, and reading the
            // extent off the next glyph would give a negative span.
            _starts.Clear();
            for (int i = 0; i < glyphs.Count; i++)
                if (!_starts.Contains(glyphs[i].Cluster)) _starts.Add(glyphs[i].Cluster);
            _starts.Sort();

            for (int i = 0; i < glyphs.Count; i++)
            {
                var glyph = glyphs[i];
                int start = glyph.Cluster;
                int slot = _starts.IndexOf(start);
                int end = slot >= 0 && slot + 1 < _starts.Count ? _starts[slot + 1] : text.Length;
                if (end <= start) end = Mathf.Min(start + 1, text.Length);

                _scratch.Append(glyph.GlyphId.ToString().PadRight(8));
                _scratch.Append(start.ToString().PadRight(9));
                for (int c = start; c < end && c < text.Length; c++)
                {
                    _scratch.Append("U+").Append(((int)text[c]).ToString("X4"));
                    if (c + 1 < end) _scratch.Append(' ');
                }
                if (end - start > 1) _scratch.Append("   ← ").Append(end - start)
                    .Append(" characters, one glyph");
                _scratch.Append('\n');
            }

            _scratch.Append("</mspace>\n")
                .Append("<color=" + DemoUi.DimHex + ">")
                .Append(text.Length).Append(" characters in memory → ")
                .Append(glyphs.Count).Append(" glyphs on screen")
                .Append("</color>");
            _table.Text = _scratch.ToString();
        }
    }
}
