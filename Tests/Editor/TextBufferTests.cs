using System;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The setters that exist so a game does not have to build a string.
    ///
    /// A score, a timer and a countdown are the text that changes every frame,
    /// and <c>label.Text = value.ToString()</c> makes a string every one of
    /// them. These write the characters into the label's own buffer instead,
    /// which is only worth having if what comes out is exactly what the string
    /// would have said — so that is what these check, against the string the
    /// caller would otherwise have made.
    /// </summary>
    public class TextBufferTests
    {
        private GameObject _canvas;
        private OneTextLabel _label;

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_canvas.transform, false);
            _label = go.AddComponent<OneTextLabel>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) UnityEngine.Object.DestroyImmediate(_canvas);
        }

        [TestCase(0)]
        [TestCase(7)]
        [TestCase(-7)]
        [TestCase(900)]
        [TestCase(int.MaxValue)]
        [TestCase(int.MinValue)]
        public void SetText_Int_Says_What_ToString_Says(int value)
        {
            _label.SetText(value);
            Assert.AreEqual(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _label.Text);
        }

        [TestCase(1.5f, 1, "1.5")]
        [TestCase(1.25f, 1, "1.3")]     // away from zero, like ToString("F1")
        [TestCase(-1.25f, 1, "-1.3")]
        [TestCase(0.04f, 2, "0.04")]
        [TestCase(12.3456f, 3, "12.346")]
        [TestCase(5f, 0, "5")]
        [TestCase(-0.4f, 0, "0")]        // rounds to zero, and zero has no sign
        public void SetText_Float_Rounds_Like_A_Fixed_Format(float value, int decimals, string expected)
        {
            _label.SetText(value, decimals);
            Assert.AreEqual(expected, _label.Text);
        }

        [Test]
        public void SetText_Span_And_CharArray_Set_The_Text()
        {
            var buffer = "score 1234".ToCharArray();
            _label.SetText(buffer, 6, 4);
            Assert.AreEqual("1234", _label.Text);

            _label.SetText("hello world".AsSpan(6, 5));
            Assert.AreEqual("world", _label.Text);
        }

        [Test]
        public void A_Buffer_Setter_Then_A_String_Setter_Agree()
        {
            // The string field and the buffer are two ways of holding the same
            // text and they must not drift: whichever was written last is what
            // the label says.
            _label.SetText(42);
            Assert.AreEqual("42", _label.Text);

            _label.Text = "forty two";
            Assert.AreEqual("forty two", _label.Text);

            _label.SetText(43);
            Assert.AreEqual("43", _label.Text);
        }

        [Test]
        public void The_Same_Number_Twice_Rebuilds_Nothing()
        {
            _label.SetText(7);
            _label.EnsureLayout();
            int runs = _label.LayoutRuns;

            _label.SetText(7);
            _label.EnsureLayout();

            Assert.AreEqual(runs, _label.LayoutRuns,
                "writing the number already shown must not re-run layout");
        }

        [Test]
        public void Markup_Reaches_The_Label_Through_The_Buffer()
        {
            // The parse reads the buffer, so text set without a string has to
            // reach the parser intact — tags and all.
            _label.RichText = true;
            _label.SetText("<b>bold</b> plain".AsSpan());
            Assert.AreEqual("bold plain", _label.DisplayText);
        }
    }
}
