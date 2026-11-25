using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// A label's preferred size is read several times per layout pass, and a
    /// pass over a parent reads every label below it. The reads are answered
    /// from a cache keyed like the render cache, so these tests check both
    /// halves: repeated reads do not lay out again, and anything that moves
    /// the answer still moves it.
    /// </summary>
    public class PreferredSizeCacheTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private OneTextLabel NewLabel(string text)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var labelObject = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(labelObject);
            labelObject.transform.SetParent(canvas.transform, false);

            var label = labelObject.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(600f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.Text = text;
            label.FontSize = 24f;
            return label;
        }

        [Test]
        public void RepeatedReads_LayOutOncePerAxis()
        {
            var label = NewLabel("the quick brown fox jumps over the lazy dog");
            float width = label.preferredWidth;
            float height = label.preferredHeight;
            int runs = label.MeasureRuns;

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(width, label.preferredWidth);
                Assert.AreEqual(height, label.preferredHeight);
            }

            Assert.AreEqual(runs, label.MeasureRuns,
                "reading an unchanged label's preferred size must not lay it out again");
        }

        [Test]
        public void ChangingTheText_Remeasures()
        {
            var label = NewLabel("short");
            float shortWidth = label.preferredWidth;

            label.Text = "a good deal longer than that";
            Assert.Greater(label.preferredWidth, shortWidth + 1f,
                "the cached width survived a text change");

            label.SetText(12345);
            float digits = label.preferredWidth;
            label.SetText(1);
            Assert.Less(label.preferredWidth, digits,
                "the cached width survived a SetText(int) into the same buffer");
        }

        [Test]
        public void ChangingTheFontSize_Remeasures()
        {
            var label = NewLabel("Hamburgefonstiv");
            float small = label.preferredWidth;
            float smallHeight = label.preferredHeight;

            label.FontSize = 48f;
            Assert.Greater(label.preferredWidth, small * 1.5f);
            Assert.Greater(label.preferredHeight, smallHeight * 1.5f);
        }

        [Test]
        public void ResizingAWrappedLabel_RemeasuresItsHeight()
        {
            var label = NewLabel("the quick brown fox jumps over the lazy dog");
            label.Wrap = TextWrap.Wrap;
            float wide = label.preferredHeight;

            label.rectTransform.sizeDelta = new Vector2(120f, 200f);
            Assert.Greater(label.preferredHeight, wide * 1.5f,
                "a narrower box wraps to more lines; the cached height ignored the width");

            label.Wrap = TextWrap.NoWrap;
            Assert.AreEqual(wide, label.preferredHeight, 0.5f,
                "turning wrapping off is one line again, whatever the box");
        }

        private OneTextStyle NewStyle(string name)
        {
            var style = ScriptableObject.CreateInstance<OneTextStyle>();
            style.name = name;
            _created.Add(style);
            return style;
        }

        [Test]
        public void AStyleEditWhileDisabled_ReachesTheLabelWhenItIsEnabled()
        {
            // A pooled label: measured, put away, the theme changes, taken out.
            // Disabled, it is off the style registry, so the edit never bumped
            // its generation, and letter spacing is not in the layout key.
            var label = NewLabel("Hamburgefonstiv");
            var style = NewStyle("theme");
            style.SetLetterSpacing(0f);
            label.Style = style;
            float plain = label.preferredWidth;
            float drawnPlain = label.EnsureLayout().Width;

            label.gameObject.SetActive(false);
            style.SetLetterSpacing(0.2f);
            label.gameObject.SetActive(true);

            Assert.Greater(label.preferredWidth, plain + 1f,
                "the measured width kept the spacing from before the label was disabled");
            Assert.Greater(label.EnsureLayout().Width, drawnPlain + 1f,
                "the drawn layout kept the spacing from before the label was disabled");
        }

        [Test]
        public void ReEnablingWithNoEdit_DoesNotRemeasure()
        {
            var label = NewLabel("Hamburgefonstiv");
            label.Style = NewStyle("theme");
            float width = label.preferredWidth;
            int runs = label.MeasureRuns;

            label.gameObject.SetActive(false);
            label.gameObject.SetActive(true);

            Assert.AreEqual(width, label.preferredWidth);
            Assert.AreEqual(runs, label.MeasureRuns,
                "a pooled label coming back with nothing changed must not lay out again");
        }

        [Test]
        public void ForcedReparse_Remeasures()
        {
            var label = NewLabel("Hamburgefonstiv");
            float width = label.preferredWidth;
            int runs = label.MeasureRuns;

            // The escape hatch for a change the cache key cannot see has to
            // reach the measured size as well as the drawn one.
            label.ForceMeshUpdate(false, true);
            Assert.AreEqual(width, label.preferredWidth, 1e-3f);
            Assert.Greater(label.MeasureRuns, runs);
        }
    }
}
