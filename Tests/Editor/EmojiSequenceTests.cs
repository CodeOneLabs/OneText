using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// UTS #51's own emoji list, shaped.
    ///
    /// An emoji is not a character. A family is seven codepoints joined by
    /// zero-width joiners, a flag is two regional indicators, a skin tone is a
    /// modifier that has to merge into the glyph before it, and a keycap is a
    /// digit plus a variation selector plus U+20E3. Each of those has to come
    /// out as ONE glyph, and a text engine that treats them as characters draws
    /// a family as five people standing in a row, which is what TextMeshPro
    /// does, and the reason this file exists.
    ///
    /// The measure is deliberately narrow: a sequence whose parts the font all
    /// covers must shape to exactly one glyph. Sequences the font has never
    /// heard of are counted and reported rather than failed, because a font
    /// missing an emoji from a later release is a fact about the font.
    ///
    /// Needs the colour emoji font from <c>Tools/fetch_coverage_fonts.py</c>.
    /// </summary>
    [Category("Coverage")]
    public class EmojiSequenceTests
    {
        private const string EmojiTestPath = "Packages/com.onetext.core/Tests/UnicodeData/emoji-test.txt";
        private const string EmojiFontPath =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoColorEmoji.ttf";

        private readonly struct Sequence
        {
            public readonly int[] Codepoints;
            public readonly string Text;
            public readonly string Description;

            public Sequence(int[] codepoints, string text, string description)
            {
                Codepoints = codepoints;
                Text = text;
                Description = description;
            }
        }

        /// <summary>
        /// The fully-qualified sequences: the forms a keyboard actually emits.
        /// Minimally-qualified and unqualified forms are legacy spellings, and
        /// components (a lone skin tone) are not emoji on their own.
        /// </summary>
        private static List<Sequence> FullyQualified()
        {
            var sequences = new List<Sequence>(4000);

            foreach (string raw in File.ReadLines(Path.GetFullPath(EmojiTestPath)))
            {
                int hash = raw.IndexOf('#');
                string data = hash >= 0 ? raw.Substring(0, hash) : raw;
                string comment = hash >= 0 ? raw.Substring(hash + 1).Trim() : string.Empty;

                int semicolon = data.IndexOf(';');
                if (semicolon < 0) continue;

                string status = data.Substring(semicolon + 1).Trim();
                if (status != "fully-qualified") continue;

                var tokens = data.Substring(0, semicolon).Trim()
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var codepoints = new int[tokens.Length];
                var text = new StringBuilder(tokens.Length * 2);
                for (int i = 0; i < tokens.Length; i++)
                {
                    codepoints[i] = int.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    text.Append(char.ConvertFromUtf32(codepoints[i]));
                }

                sequences.Add(new Sequence(codepoints, text.ToString(), comment));
            }

            return sequences;
        }

        private static bool FontCoversBase(FontData font, Sequence sequence)
        {
            foreach (int codepoint in sequence.Codepoints)
            {
                // A variation selector and a joiner are instructions, not glyphs;
                // a font is not expected to have a cmap entry for either.
                if (codepoint == 0xFE0F || codepoint == 0xFE0E || codepoint == 0x200D) continue;
                if (!font.HasGlyph(codepoint)) return false;
            }
            return true;
        }

        [Test]
        public void Every_Supported_Emoji_Sequence_Shapes_To_One_Glyph()
        {
            string fontPath = Path.GetFullPath(EmojiFontPath);
            if (!File.Exists(fontPath))
                Assert.Inconclusive("No colour emoji font. Run: python3 Tools/fetch_coverage_fonts.py");

            var sequences = FullyQualified();
            Assert.That(sequences.Count, Is.GreaterThan(3000),
                "emoji-test.txt should list thousands of fully-qualified sequences");

            using var font = FontData.Load(File.ReadAllBytes(fontPath));
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();

            int covered = 0, single = 0, notInFont = 0;
            var split = new List<string>();

            foreach (var sequence in sequences)
            {
                if (!FontCoversBase(font, sequence)) { notInFont++; continue; }
                covered++;

                glyphs.Clear();
                shaper.Shape(font, sequence.Text, 0, sequence.Text.Length,
                    Shaper.Direction.LeftToRight, glyphs, null);

                if (glyphs.Count == 1) single++;
                else if (split.Count < 12) split.Add($"{sequence.Description} → {glyphs.Count} glyphs");
            }

            Debug.Log($"[emoji] {sequences.Count} fully-qualified sequences; " +
                      $"{covered} covered by the font, {single} shaped to one glyph " +
                      $"({(covered == 0 ? 0 : 100.0 * single / covered):F2} %), " +
                      $"{notInFont} not in this font");

            Assert.That(single, Is.EqualTo(covered),
                "every sequence the font covers must merge into one glyph; split: "
                + string.Join("; ", split));
        }

        [Test]
        public void The_Hard_Sequence_Kinds_Each_Merge()
        {
            string fontPath = Path.GetFullPath(EmojiFontPath);
            if (!File.Exists(fontPath))
                Assert.Inconclusive("No colour emoji font. Run: python3 Tools/fetch_coverage_fonts.py");

            using var font = FontData.Load(File.ReadAllBytes(fontPath));
            using var shaper = new Shaper();
            var glyphs = new List<ShapedGlyph>();

            // One of each mechanism, named, so a regression says which kind
            // broke instead of moving a percentage.
            var cases = new (string Text, string Kind)[]
            {
                ("\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466", "ZWJ family of four"),
                ("\U0001F469‍\U0001F4BB", "ZWJ woman technologist"),
                ("\U0001F1F0\U0001F1F7", "regional-indicator flag (KR)"),
                ("\U0001F44D\U0001F3FF", "skin-tone modifier"),
                ("1️⃣", "keycap"),
                ("❤️", "variation selector"),
                ("\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", "tag sequence (Scotland)"),
            };

            var failures = new List<string>();
            foreach (var (text, kind) in cases)
            {
                glyphs.Clear();
                shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, glyphs, null);
                if (glyphs.Count != 1) failures.Add($"{kind}: {glyphs.Count} glyphs");
            }

            Assert.That(failures, Is.Empty, string.Join("; ", failures));
        }

        [Test]
        public void Emoji_Sequences_Survive_The_Whole_Label_Path()
        {
            // The two tests above stop at Shaper.Shape, and everything after it
            // is where a one-glyph sequence can still come apart: the colour
            // path skips clustering and goes one glyph to one tile, the atlas
            // key is built by hand from (face, glyph, ppem, tint), and the
            // separators between emoji are glyphs of their own with no bitmap.
            // So this drives a real label and reads the finished geometry:
            // three sequences in, three tiles out, in the order they were typed.
            string fontPath = Path.GetFullPath(EmojiFontPath);
            if (!File.Exists(fontPath))
                Assert.Inconclusive("No colour emoji font. Run: python3 Tools/fetch_coverage_fonts.py");

            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(430f, 72f);
                label.SetFont(File.ReadAllBytes(fontPath));
                label.FontSize = 38f;
                label.Wrap = TextWrap.NoWrap;
                // A ZWJ family, a regional-indicator flag and a skin-tone
                // modifier: the three mechanisms by which an emoji is not a
                // character, one of each, separated by ordinary spaces.
                label.Text = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466 " +
                             "\U0001F1F0\U0001F1F7 \U0001F44D\U0001F3FF";
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                var layout = label.LayoutResult;
                // 21 code units, five glyphs: the three sequences plus the two
                // spaces between them. A regression that stopped ligating would
                // show up here first, as nine.
                Assert.AreEqual(5, layout.Glyphs.Count,
                    "the sequences did not ligate on the way through layout");
                Assert.AreEqual(5, layout.GraphemeCount);

                var quads = label.Quads;
                Assert.AreEqual(3, quads.Count,
                    "three emoji must draw three tiles; a space has no bitmap and no outline " +
                    "in a colour font, and must not fall through to a notdef box");
                Assert.AreEqual(3, label.DrawnQuads.Count, "reveal dropped a tile at full reveal");

                for (int i = 0; i < quads.Count; i++)
                {
                    Assert.IsTrue(quads[i].IsColor,
                        $"tile {i} came from the SDF atlas: a CBDT bitmap was not decoded, " +
                        "and an SDF of a glyph with no outline is a blank");
                    // One tile per sequence, addressed as one grapheme: an
                    // effect moves it whole and a typewriter reveals it whole.
                    Assert.AreEqual(i * 2, quads[i].FirstGrapheme,
                        $"tile {i} is not addressed as the grapheme it draws");
                    Assert.AreEqual(quads[i].FirstGrapheme, quads[i].LastGrapheme);
                }

                // Left to right in typed order. The colour path emits per glyph
                // while the SDF path emits per ink-sorted cluster, and lifting
                // the wrong one of those two orders here would reorder emoji.
                Assert.Less(quads[0].Position.x, quads[1].Position.x);
                Assert.Less(quads[1].Position.x, quads[2].Position.x);

                // Three distinct tiles. The colour atlas key is assembled from
                // shifted fields, and a collision between two of them would
                // draw one emoji three times, which reads as "it works" in
                // every count-based assertion above.
                Assert.AreNotEqual(quads[0].UvRect, quads[1].UvRect);
                Assert.AreNotEqual(quads[1].UvRect, quads[2].UvRect);
                Assert.AreNotEqual(quads[0].UvRect, quads[2].UvRect);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }
    }
}
