using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Auto-size: the label picks the largest size in [min, max] at which the
    /// whole text fits its rect.
    /// </summary>
    public class AutoSizeTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private const string LongText =
            "the quick brown fox jumps over the lazy dog and keeps on running " +
            "until the sentence is long enough to need shrinking";

        private static OneTextLabel CreateLabel(float width, float height)
        {
            var go = new GameObject("AutoSizeLabel", typeof(RectTransform));
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
            var label = go.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            return label;
        }

        private static void Destroy(OneTextLabel label) =>
            Object.DestroyImmediate(label.gameObject);

        [Test]
        public void Short_Text_In_A_Roomy_Rect_Uses_Max()
        {
            var label = CreateLabel(500f, 200f);
            label.Text = "Hi";
            label.AutoSize = true;
            label.AutoSizeMin = 10f;
            label.AutoSizeMax = 40f;

            Assert.AreEqual(40f, label.FittedFontSize, "nothing constrains it: max wins");
            Destroy(label);
        }

        [Test]
        public void Long_Text_Shrinks_Until_The_Block_Fits()
        {
            var label = CreateLabel(200f, 60f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 64f;

            float fitted = label.FittedFontSize;
            Assert.Less(fitted, 64f, "the text cannot fit at max");
            Assert.GreaterOrEqual(fitted, 5f);

            var layout = label.EnsureLayout();
            Assert.LessOrEqual(layout.BlockExtent, 60f + 1f, "the fitted block fits the rect");
            Assert.LessOrEqual(layout.InlineExtent, 200f + 1f);
            Destroy(label);
        }

        [Test]
        public void An_Impossible_Fit_Clamps_To_Min()
        {
            var label = CreateLabel(40f, 14f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 20f;
            label.AutoSizeMax = 60f;

            Assert.AreEqual(20f, label.FittedFontSize,
                "when even min overflows, min is the honest answer");
            Destroy(label);
        }

        [Test]
        public void The_Fit_Tracks_The_Rect()
        {
            var label = CreateLabel(400f, 120f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 4f;
            label.AutoSizeMax = 64f;

            float roomy = label.FittedFontSize;
            label.rectTransform.sizeDelta = new Vector2(150f, 40f);
            float cramped = label.FittedFontSize;

            Assert.Less(cramped, roomy, "a smaller rect fits only a smaller size");
            Destroy(label);
        }

        [Test]
        public void Auto_Size_Off_Uses_The_Configured_Size()
        {
            var label = CreateLabel(200f, 60f);
            label.Text = "Hello";
            label.FontSize = 24f;

            Assert.AreEqual(24f, label.FittedFontSize);
            Destroy(label);
        }

        [Test]
        public void Turning_Auto_Size_Off_Restores_The_Configured_Size()
        {
            var label = CreateLabel(120f, 40f);
            label.Text = LongText;
            label.FontSize = 48f;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 64f;

            Assert.Less(label.FittedFontSize, 48f, "auto-size shrank the text");
            label.AutoSize = false;
            Assert.AreEqual(48f, label.FittedFontSize, "off means the field is back in charge");
            Destroy(label);
        }

        [Test]
        public void Vertical_Text_Fits_Against_Its_Own_Axes()
        {
            var label = CreateLabel(80f, 300f);
            label.Text = "こんにちは世界、今日は良い天気ですね";
            label.WritingMode = TextWritingMode.VerticalRightToLeft;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 72f;

            float fitted = label.FittedFontSize;
            Assert.GreaterOrEqual(fitted, 5f);
            var layout = label.EnsureLayout();
            Assert.LessOrEqual(layout.BlockExtent, 80f + 1f,
                "down a column the block axis is the width");
            Assert.LessOrEqual(layout.InlineExtent, 300f + 1f);
            Destroy(label);
        }

        [Test]
        public void Repeated_Asks_Do_Not_Relayout()
        {
            var label = CreateLabel(200f, 60f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 64f;

            label.EnsureLayout();
            int runs = label.LayoutRuns;
            label.EnsureLayout();
            label.EnsureLayout();
            Assert.AreEqual(runs, label.LayoutRuns,
                "the fit is part of the layout key: nothing changed, nothing re-ran");
            Destroy(label);
        }

        [Test]
        public void Fitted_Size_Lands_On_The_Half_Point_Grid()
        {
            var label = CreateLabel(200f, 60f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 64f;

            float fitted = label.FittedFontSize;
            Assert.AreEqual(fitted, Mathf.Floor(fitted * 2f) * 0.5f,
                "fractional sizes churn atlas ppem buckets for nothing");
            Destroy(label);
        }

        // ------------------------------------------------------- the inspector

        [Test]
        public void The_Size_Row_Shows_The_Size_Auto_Size_Chose()
        {
            // The bug report: switch auto-size on, watch the text change size,
            // and the Size field goes on reading whatever was typed into it.
            // The row is disabled while auto-size owns the size, and a disabled
            // row showing a number the label is ignoring is worse than no row —
            // it is the one control that claims to say how big the text is.
            var label = CreateLabel(120f, 40f);
            label.Text = LongText;
            label.FontSize = 48f;
            label.AutoSize = true;
            label.AutoSizeMin = 5f;
            label.AutoSizeMax = 64f;

            float shown = Editor.OneTextLabelEditor.SizeShownFor(
                new Object[] { label }, out bool mixed);

            Assert.AreEqual(label.FittedFontSize, shown, "the row and the text disagree about the size");
            Assert.Less(shown, 48f, "the row showed the authored size the label is ignoring");
            Assert.IsFalse(mixed);
            Destroy(label);
        }

        [Test]
        public void The_Size_Row_Follows_The_Rect()
        {
            var label = CreateLabel(400f, 120f);
            label.Text = LongText;
            label.AutoSize = true;
            label.AutoSizeMin = 4f;
            label.AutoSizeMax = 64f;

            float roomy = Editor.OneTextLabelEditor.SizeShownFor(new Object[] { label }, out _);
            label.rectTransform.sizeDelta = new Vector2(150f, 40f);
            float cramped = Editor.OneTextLabelEditor.SizeShownFor(new Object[] { label }, out _);

            Assert.Less(cramped, roomy, "the row is stale until something else redraws it");
            Destroy(label);
        }

        [Test]
        public void Two_Labels_Fitted_Differently_Are_A_Mixed_Size()
        {
            // A multi-selection has one row for however many answers, and a
            // number picked from whichever label happened to be first is a
            // number somebody will read as all of them.
            var roomy = CreateLabel(400f, 120f);
            var cramped = CreateLabel(150f, 40f);
            foreach (var label in new[] { roomy, cramped })
            {
                label.Text = LongText;
                label.AutoSize = true;
                label.AutoSizeMin = 4f;
                label.AutoSizeMax = 64f;
            }

            Editor.OneTextLabelEditor.SizeShownFor(new Object[] { roomy, cramped }, out bool mixed);
            Assert.IsTrue(mixed);

            Editor.OneTextLabelEditor.SizeShownFor(new Object[] { roomy, roomy }, out mixed);
            Assert.IsFalse(mixed, "two labels that fitted the same size do not disagree");

            Destroy(roomy);
            Destroy(cramped);
        }
    }
}
