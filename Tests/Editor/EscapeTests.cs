using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Backslash escapes: the two characters "\n" a localization table stores
    /// against the one character the translator meant.
    ///
    /// The rule under test is the same one the markup parser lives by: resolve
    /// exactly what is well-formed and leave everything else untouched. A
    /// Windows path full of backslashes must come out of this parser the way
    /// it went in, because text that silently changes is worse than an escape
    /// that never resolved.
    /// </summary>
    public class EscapeTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        // ------------------------------------------------------- the parser

        [TestCase(@"one\ntwo", "one\ntwo")]
        [TestCase(@"a\tb", "a\tb")]
        [TestCase(@"a\vb", "a\vb")]
        [TestCase(@"a\rb", "a\rb")]
        [TestCase(@"a\\b", @"a\b")]
        [TestCase(@"\n", "\n")]
        [TestCase(@"line one\nline two\nline three", "line one\nline two\nline three")]
        public void KnownEscapes_Resolve(string source, string expected)
        {
            Assert.AreEqual(expected, EscapeParser.Unescape(source));
        }

        [Test]
        public void EscapedBackslash_Protects_The_Letter_After_It()
        {
            // Someone who wants a visible "\n" writes "\\n", same as in C#.
            Assert.AreEqual(@"\n", EscapeParser.Unescape(@"\\n"));
        }

        [TestCase(@"a\zb")]         // unknown escape
        [TestCase(@"C:\Users\me")]  // \U without eight hex digits, \m unknown
        [TestCase(@"a\u12")]        // \u cut short
        [TestCase(@"a\u12ZZ")]      // \u with non-hex digits
        [TestCase(@"a\U0001F60")]   // \U cut short
        [TestCase(@"a\UFFFFFFFF")]  // \U beyond U+10FFFF
        [TestCase(@"a\U0000D800")]  // \U naming a surrogate
        [TestCase(@"trailing\")]    // backslash at the very end
        public void MalformedEscapes_StayLiteral(string source)
        {
            Assert.AreEqual(source, EscapeParser.Unescape(source));
        }

        [Test]
        public void UnicodeEscapes_Resolve()
        {
            Assert.AreEqual("A", EscapeParser.Unescape(@"\u0041"));
            Assert.AreEqual("한", EscapeParser.Unescape(@"\uD55C"));
            Assert.AreEqual("\U0001F600", EscapeParser.Unescape(@"\U0001F600"));
            Assert.AreEqual("A", EscapeParser.Unescape(@"\U00000041"), "BMP scalar through the long form");
        }

        [Test]
        public void HexDigits_Are_Case_Insensitive()
        {
            Assert.AreEqual("\U0001F600", EscapeParser.Unescape(@"\U0001f600"));
            Assert.AreEqual("\uD7FB", EscapeParser.Unescape(@"\ud7fb"));
        }

        [Test]
        public void NoEscapes_Returns_The_Same_Instance()
        {
            const string plain = "nothing to do here";
            Assert.AreSame(plain, EscapeParser.Unescape(plain));
            Assert.IsNull(EscapeParser.Unescape(null));
            Assert.AreEqual(string.Empty, EscapeParser.Unescape(string.Empty));
        }

        [Test]
        public void AllLiteralBackslashes_Return_The_Same_Instance()
        {
            // Backslashes that resolve nothing must not pay for a copy either.
            const string path = @"C:\Users\me\Documents";
            Assert.AreSame(path, EscapeParser.Unescape(path));
        }

        // -------------------------------------------------------- the label

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
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(1600f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.Text = text;
            label.FontSize = 32f;
            return label;
        }

        [Test]
        public void Label_Resolves_Escapes_By_Default()
        {
            var label = NewLabel(@"one\ntwo");
            Assert.AreEqual("one\ntwo", label.DisplayText);
        }

        [Test]
        public void Label_Leaves_Escapes_Literal_When_Asked()
        {
            var label = NewLabel(@"one\ntwo");
            label.ParseEscapes = false;
            Assert.AreEqual(@"one\ntwo", label.DisplayText);

            // And toggling back re-parses: the cache saw the flag change.
            label.ParseEscapes = true;
            Assert.AreEqual("one\ntwo", label.DisplayText);
        }

        [Test]
        public void Escapes_Resolve_Before_Markup_So_Link_Indices_Fit()
        {
            var label = NewLabel("a\\n<link=here>b</link>");
            Assert.AreEqual("a\nb", label.DisplayText);
            Assert.AreEqual(1, label.Links.Count);
            Assert.AreEqual(2, label.Links[0].Start, "link starts after the resolved newline");
            Assert.AreEqual(3, label.Links[0].End);
        }

        [Test]
        public void Resolved_Newline_Breaks_The_Line()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            var result = new TextLayoutResult();
            using var engine = new TextLayoutEngine();
            engine.Layout(EscapeParser.Unescape(@"one\ntwo"),
                TextLayoutSettings.Default(fonts, 32f), result);

            Assert.AreEqual(2, result.Lines.Count);
        }
    }
}
