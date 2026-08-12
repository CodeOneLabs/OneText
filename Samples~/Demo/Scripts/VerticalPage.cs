using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// Vertical text is not a rotated paragraph.
    ///
    /// The shortcut every engine reaches for first is to lay the text out
    /// horizontally and turn the whole block ninety degrees. It is easy, it
    /// looks plausible in a screenshot, and it is wrong in a way that anybody
    /// who reads the language spots instantly: the characters end up lying on
    /// their sides.
    ///
    /// What vertical setting actually requires is a per-character decision.
    /// CJK ideographs stay upright. Latin runs rotate, as a run, so a word
    /// embedded in a column reads down the side rather than one letter under
    /// another. Brackets, dashes and ellipses are <em>replaced</em> with
    /// different glyphs the font ships for exactly this — a vertical
    /// parenthesis is its own character shape, not a turned one. Small kana
    /// shift position. The line box's advance direction changes, so what was
    /// line height is now column width, and lines stack right to left.
    ///
    /// So the page draws the shortcut beside the real thing. The shortcut is a
    /// genuine horizontal layout with a ninety-degree rotation on its transform
    /// — not a caricature, just the easy implementation — and the difference is
    /// visible without being able to read a word of it.
    /// </summary>
    internal sealed class VerticalPage : DemoPage
    {
        // Chosen so that every rule above has something to bite on — upright
        // Hangul and kana, a Latin run that must rotate as a unit, corner
        // brackets and an em dash that must be substituted rather than turned,
        // and small kana that move.
        //
        // No kanji, deliberately. The face this sample ships covers Hangul,
        // kana, the brackets and the dash; a CJK font that also covers kanji is
        // sixteen megabytes, which is not a thing to put in a package so that
        // one line of one page can be in Japanese. The rules being shown are
        // the same either way.
        private const string Sample =
            "세로쓰기 「본문」에\n" +
            "Unity 라는 말과\n" +
            "ダッシュ —— 도 섞어서.";

        internal override string Title => "Vertical";

        internal override string Claim =>
            "Turning the block ninety degrees lays every character on its side. " +
            "Real vertical setting decides upright, rotated or substituted per character.";

        protected override void Build(RectTransform host)
        {
            var wrong = Column(host, "the shortcut · a horizontal block, rotated 90°", 0f, 0.5f);
            var right = Column(host, "vertical writing mode", 0.5f, 1f);

            // The easy implementation, honestly built: laid out horizontally,
            // then the transform turned. Nothing about the text is asked to
            // change, which is exactly the problem.
            var rotated = DemoUi.Label("rotated", wrong, Sample, 30f, DemoUi.Bad, Fonts);
            var rotatedRect = (RectTransform)rotated.transform;
            rotatedRect.anchorMin = new Vector2(0.5f, 0.5f);
            rotatedRect.anchorMax = new Vector2(0.5f, 0.5f);
            rotatedRect.pivot = new Vector2(0.5f, 0.5f);
            rotatedRect.sizeDelta = new Vector2(460f, 120f);
            rotatedRect.localRotation = Quaternion.Euler(0f, 0f, -90f);
            rotated.Alignment = TextAlignment.Start;
            rotated.VerticalAlignment = VerticalAlignment.Top;

            var real = DemoUi.Label("vertical", right, Sample, 30f, DemoUi.Ink, Fonts);
            var realRect = DemoUi.Fill((RectTransform)real.transform, 16f);
            _ = realRect;
            real.WritingMode = TextWritingMode.VerticalRightToLeft;
            real.Alignment = TextAlignment.Start;
            real.VerticalAlignment = VerticalAlignment.Top;

            var notes = DemoUi.Label("notes", host,
                "<b>What changed, character by character</b>\n" +
                "<color=" + DemoUi.DimHex + ">" +
                "세로쓰기, 본문, 말  — Hangul and kana stay upright.\n" +
                "Unity  — a Latin run rotates as one run, so the word still reads as a word.\n" +
                "「 」  — corner brackets are swapped for the vertical glyphs the font ships " +
                "for them, not turned on their side.\n" +
                "——  — the dash is substituted too, and runs along the column.\n" +
                "ッ, ュ  — small kana move to the position vertical setting puts them in.\n" +
                "Lines stack right to left, and the line box advances across the column " +
                "rather than down the page." +
                "</color>",
                DemoUi.Caption, DemoUi.Ink, Fonts);
            var notesRect = (RectTransform)notes.transform;
            notesRect.anchorMin = new Vector2(0f, 0f);
            notesRect.anchorMax = new Vector2(1f, 0f);
            notesRect.pivot = new Vector2(0f, 0f);
            notesRect.anchoredPosition = new Vector2(8f, 6f);
            notesRect.sizeDelta = new Vector2(-16f, 132f);
        }

        private RectTransform Column(RectTransform host, string title, float x0, float x1)
        {
            var column = DemoUi.Rect(title, host);
            column.anchorMin = new Vector2(x0, 0f);
            column.anchorMax = new Vector2(x1, 1f);
            column.offsetMin = new Vector2(4f, 144f);
            column.offsetMax = new Vector2(-4f, -4f);
            return DemoUi.PanelWithTitle("panel", column, title, Fonts);
        }
    }
}
