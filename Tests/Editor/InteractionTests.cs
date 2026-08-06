using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>M5: hit-testing, caret movement, link markup and the input field.</summary>
    public class InteractionTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static TextLayoutResult Layout(TextLayoutEngine engine, FontStack fonts,
            string text, float maxWidth = 0f)
        {
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.MaxWidth = maxWidth;
            settings.Wrap = maxWidth > 0f ? TextWrap.Wrap : TextWrap.NoWrap;
            var result = new TextLayoutResult();
            engine.Layout(text, settings, result);
            return result;
        }

        // ------------------------------------------------------------ hit testing

        [Test]
        public void Click_Before_And_After_The_Text_Clamps_To_Its_Ends()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "Hello");

            Assert.AreEqual(0, TextHitTest.GetIndexAtPoint(layout, new Vector2(-50f, 10f)));
            Assert.AreEqual(5, TextHitTest.GetIndexAtPoint(layout, new Vector2(layout.Width + 50f, 10f)));
        }

        [Test]
        public void Caret_X_Increases_Along_An_LTR_Line()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "Hello");

            float previous = float.MinValue;
            for (int i = 0; i <= 5; i++)
            {
                float x = TextHitTest.GetCaretRect(layout, i, 1f).center.x;
                Assert.Greater(x, previous, $"caret {i} must sit right of caret {i - 1}");
                previous = x;
            }
        }

        [Test]
        public void Clicking_A_Glyph_Returns_Its_Own_Index()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "Hello");

            for (int i = 0; i < 5; i++)
            {
                float left = TextHitTest.GetCaretRect(layout, i, 1f).center.x;
                float right = TextHitTest.GetCaretRect(layout, i + 1, 1f).center.x;
                float justInside = left + (right - left) * 0.25f;
                Assert.AreEqual(i, TextHitTest.GetIndexAtPoint(layout, new Vector2(justInside, 10f)),
                    "the left quarter of a glyph belongs to the caret before it");
            }
        }

        [Test]
        public void Rtl_Caret_Runs_Right_To_Left()
        {
            using var font = LoadFont(ArabicFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "مرحبا");

            float first = TextHitTest.GetCaretRect(layout, 0, 1f).center.x;
            float last = TextHitTest.GetCaretRect(layout, 5, 1f).center.x;
            Assert.Greater(first, last, "index 0 sits at the right edge of RTL text");
        }

        [Test]
        public void Selection_Rects_Cover_One_Row_Per_Line()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "one two three four five", 160f);
            Assert.Greater(layout.Lines.Count, 1);

            var rects = new List<Rect>();
            TextHitTest.GetSelectionRects(layout, 0, 23, rects);
            Assert.AreEqual(layout.Lines.Count, rects.Count);
            foreach (var rect in rects)
                Assert.Greater(rect.width, 0f);

            TextHitTest.GetSelectionRects(layout, 4, 4, rects);
            Assert.IsEmpty(rects, "an empty range selects nothing");
        }

        [Test]
        public void Vertical_Movement_Keeps_The_Column()
        {
            using var font = LoadFont(LatinFontPath);
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var layout = Layout(engine, fonts, "one two three four five six seven", 200f);
            Assert.Greater(layout.Lines.Count, 2);

            int start = layout.Lines[0].TextStart + 3;
            float column = TextHitTest.GetCaretRect(layout, start, 1f).center.x;
            int down = TextHitTest.MoveVertically(layout, start, 1, column);

            Assert.AreEqual(1, TextHitTest.GetLineForIndex(layout, down), "moved exactly one line");
            int back = TextHitTest.MoveVertically(layout, down, -1, column);
            Assert.AreEqual(start, back, "the column survives a round trip");
        }

        // -------------------------------------------------------- caret movement

        [Test]
        public void Caret_Steps_Over_Whole_Grapheme_Clusters()
        {
            // e + combining acute, then a family emoji.
            string text = "e\u0301\U0001F468\u200D\U0001F469\u200D\U0001F467!";

            Assert.AreEqual(2, TextHitTest.NextCaret(text, 0), "combining mark travels with its base");
            int afterEmoji = TextHitTest.NextCaret(text, 2);
            Assert.AreEqual(text.Length - 1, afterEmoji, "the whole zwj sequence is one caret step");
            Assert.AreEqual(2, TextHitTest.PreviousCaret(text, afterEmoji));
            Assert.AreEqual(0, TextHitTest.PreviousCaret(text, 2));
        }

        [Test]
        public void Word_Movement_Lands_On_Word_Starts()
        {
            const string text = "the quick brown fox";

            Assert.AreEqual(4, TextHitTest.NextWord(text, 0));
            Assert.AreEqual(10, TextHitTest.NextWord(text, 4));
            Assert.AreEqual(4, TextHitTest.PreviousWord(text, 10));
            Assert.AreEqual(0, TextHitTest.PreviousWord(text, 4));

            TextHitTest.GetWordAt(text, 5, out int start, out int end);
            Assert.AreEqual("quick", text.Substring(start, end - start));
        }

        // ------------------------------------------------------------ link markup
        //
        // Links used to have a parser of their own; they are one tag in the
        // rich-text parser now. These stay as they were, because the behaviour
        // they pin is the behaviour link handling still has to have.

        private static string ParseLinks(string source, List<TextLink> links)
        {
            var result = new RichTextResult();
            RichTextParser.Parse(source, result);
            links.Clear();
            links.AddRange(result.Links);
            return result.Text;
        }

        [Test]
        public void Link_Tags_Are_Stripped_And_Reported()
        {
            var links = new List<TextLink>();
            string display = ParseLinks("see <link=docs>the manual</link> now", links);

            Assert.AreEqual("see the manual now", display);
            Assert.AreEqual(1, links.Count);
            Assert.AreEqual("docs", links[0].Id);
            Assert.AreEqual("the manual", display.Substring(links[0].Start, links[0].Length));
            Assert.IsTrue(links[0].Contains(links[0].Start));
            Assert.IsFalse(links[0].Contains(links[0].End));
        }

        [Test]
        public void Malformed_Markup_Is_Left_Alone()
        {
            var links = new List<TextLink>();

            Assert.AreEqual("a < b and 5<6", ParseLinks("a < b and 5<6", links));
            Assert.IsEmpty(links);

            // An unterminated tag still links to the end of the text.
            string display = ParseLinks("go <link=\"x\">there", links);
            Assert.AreEqual("go there", display);
            Assert.AreEqual(1, links.Count);
            Assert.AreEqual("x", links[0].Id, "quotes around the id are optional");
            Assert.AreEqual("there", display.Substring(links[0].Start, links[0].Length));
        }

        [Test]
        public void Nested_Links_Report_Both_Ranges()
        {
            var links = new List<TextLink>();
            string display = ParseLinks("<link=a>one <link=b>two</link></link>", links);

            Assert.AreEqual("one two", display);
            Assert.AreEqual(2, links.Count);

            // Innermost first, because that is the order they close in, and
            // the order hit-testing wants: it returns the first link containing
            // the point, and for nested links the specific one is the answer.
            Assert.AreEqual("b", links[0].Id);
            Assert.AreEqual("two", display.Substring(links[0].Start, links[0].Length));
            Assert.AreEqual("a", links[1].Id);
            Assert.AreEqual("one two", display.Substring(links[1].Start, links[1].Length));
        }

        // ------------------------------------------------------------ input field

        private static OneTextInputField CreateField(out OneTextLabel label)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var root = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(canvasGo.transform, false);
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 60f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(root.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            label = textGo.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 24f;
            label.Wrap = TextWrap.NoWrap;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new UnityEditor.SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return field;
        }

        [Test]
        public void InputField_Text_Drives_The_Label_And_Fires_Events()
        {
            var field = CreateField(out var label);
            string observed = null;
            field.onValueChanged.AddListener(value => observed = value);

            field.text = "hello";
            field.UpdateVisuals();

            Assert.AreEqual("hello", field.text);
            Assert.AreEqual("hello", observed, "onValueChanged fires for API edits");
            Assert.AreEqual("hello", label.Text);

            Object.DestroyImmediate(field.transform.parent.gameObject);
        }

        [Test]
        public void InputField_Caret_And_Selection_Track_The_Text()
        {
            var field = CreateField(out var label);
            field.text = "hello world";
            field.caretPosition = 5;

            Assert.AreEqual(5, field.caretPosition);
            Assert.AreEqual(5, field.selectionAnchorPosition, "moving the caret collapses the selection");

            field.SelectAll();
            Assert.AreEqual(0, field.selectionAnchorPosition);
            Assert.AreEqual(field.text.Length, field.caretPosition);

            field.UpdateVisuals();
            var rects = new List<Rect>();
            label.GetSelectionRects(0, field.text.Length, rects);
            Assert.AreEqual(1, rects.Count);
            Assert.Greater(rects[0].width, 0f);

            Object.DestroyImmediate(field.transform.parent.gameObject);
        }

        [Test]
        public void InputField_Clamps_The_Caret_When_Text_Shrinks()
        {
            var field = CreateField(out var label);
            field.text = "hello world";
            field.caretPosition = 11;
            field.text = "hi";

            Assert.AreEqual(2, field.caretPosition);

            Object.DestroyImmediate(field.transform.parent.gameObject);
        }
    }
}
