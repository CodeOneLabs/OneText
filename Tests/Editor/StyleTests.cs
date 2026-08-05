using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M8: named styles.
    ///
    /// The claim being tested is not "a style can set a colour" — it is that a
    /// label holds a <em>reference</em>. Everything worth having follows from
    /// that: editing an asset updates every label, theming is a style swap
    /// rather than a walk over the scene, and a runtime font change cannot
    /// silently drop the styling with it, which is the TMP material-preset
    /// failure this replaces.
    /// </summary>
    public class StyleTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        private readonly List<Object> _created = new List<Object>();

        private OneTextStyle NewStyle(string name)
        {
            var style = ScriptableObject.CreateInstance<OneTextStyle>();
            style.name = name;
            _created.Add(style);
            return style;
        }

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        // ---------------------------------------------------------- resolution

        [Test]
        public void AStyleOnlySetsWhatItOverrides()
        {
            var style = NewStyle("title");
            Assert.IsFalse(style.Sets(OneTextStyle.Fields.Color),
                "a fresh style must have no opinion about anything");

            style.SetColor(Color.red);
            Assert.IsTrue(style.Sets(OneTextStyle.Fields.Color));
            Assert.IsFalse(style.Sets(OneTextStyle.Fields.Size),
                "setting a colour must not accidentally claim a size");

            style.ClearOverride(OneTextStyle.Fields.Color);
            Assert.IsFalse(style.Sets(OneTextStyle.Fields.Color));
        }

        [Test]
        public void ExtendsInheritsOnlyUnsetFields()
        {
            var basis = NewStyle("body");
            basis.SetColor(Color.blue);
            basis.SetFontSize(20f);

            var derived = NewStyle("heading");
            var extends = typeof(OneTextStyle).GetField("_extends",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            extends.SetValue(derived, basis);
            derived.SetFontSize(40f);

            Assert.AreEqual(40f, derived.FontSize, 0.001f, "the override wins");
            Assert.AreEqual(Color.blue, derived.Color, "the unset field is inherited");
            Assert.IsTrue(derived.Sets(OneTextStyle.Fields.Color),
                "a field the base sets counts as set");
        }

        [Test]
        public void Apply_DoesNotOverwriteMoreSpecificMarkup()
        {
            // <color=red><style=title> is someone asking for a red title. The
            // style is the general instruction and the tag is the specific one.
            var style = NewStyle("title");
            style.SetColor(Color.green);
            style.SetFontSize(64f);

            var markup = TextStyle.Default;
            markup.Color = new Color32(255, 0, 0, 255);
            markup.Flags |= TextStyle.Flag.HasColor;

            var applied = style.Apply(markup);
            Assert.AreEqual(255, applied.Color.r, "the style overwrote an explicit colour");
            Assert.AreEqual(0, applied.Color.g);
            Assert.AreEqual(64f, applied.SizeAbsolute, 0.001f,
                "the style should still supply what markup did not say");
        }

        // ------------------------------------------------------------- markup

        [Test]
        public void StyleTag_ResolvesThroughTheLabelsTable()
        {
            var title = NewStyle("title");
            title.SetFontSize(64f);

            var result = new RichTextResult();
            RichTextParser.Parse("<style=title>big</style> small", result,
                name => name == "title" ? 0 : -1, null);

            Assert.AreEqual("big small", result.Text);
            Assert.AreEqual(0, result.StyleAt(0).NamedStyle);
            Assert.AreEqual(-1, result.StyleAt(result.Text.Length - 1).NamedStyle,
                "</style> must stop applying it");
        }

        [Test]
        public void NamedStyle_ChangesLayout_ThroughTheEngine()
        {
            using var font = FontData.Load(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();

            var title = NewStyle("title");
            title.SetFontSize(64f);

            var markup = new RichTextResult();
            RichTextParser.Parse("aa<style=title>aa</style>", markup, _ => 0, null);

            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.Wrap = TextWrap.NoWrap;
            settings.Spans = markup.Spans;
            settings.ResolveNamedStyle = (index, style) => title.Apply(style);

            var withStyle = new TextLayoutResult();
            engine.Layout(markup.Text, settings, withStyle);

            settings.ResolveNamedStyle = null;
            var without = new TextLayoutResult();
            engine.Layout(markup.Text, settings, without);

            Assert.Greater(withStyle.Width, without.Width,
                "the named style did not reach the run that referenced it");

            bool sawBig = false;
            foreach (var run in withStyle.Runs)
                if (run.FontSize > 40f) sawBig = true;
            Assert.IsTrue(sawBig, "no run picked up the style's size");
        }

        // ----------------------------------------------------- the live update

        [Test]
        public void EditingAStyle_NotifiesEveryLabelThatUsesIt()
        {
            var used = NewStyle("used");
            var unused = NewStyle("unused");

            var probe = new Probe(used);
            StyleInvalidation.Register(probe);
            try
            {
                unused.SetColor(Color.cyan);
                Assert.AreEqual(0, probe.Notifications,
                    "a label was rebuilt for a style it does not reference");

                used.SetColor(Color.magenta);
                Assert.AreEqual(1, probe.Notifications,
                    "editing a style did not reach the label referencing it — which is the " +
                    "whole reason labels store a reference rather than a copy");
            }
            finally
            {
                StyleInvalidation.Unregister(probe);
            }
        }

        [Test]
        public void EditingABaseStyle_NotifiesLabelsUsingWhatExtendsIt()
        {
            var basis = NewStyle("body");
            var derived = NewStyle("heading");
            typeof(OneTextStyle)
                .GetField("_extends", System.Reflection.BindingFlags.NonPublic |
                                      System.Reflection.BindingFlags.Instance)
                .SetValue(derived, basis);

            var probe = new Probe(derived);
            StyleInvalidation.Register(probe);
            try
            {
                basis.SetColor(Color.yellow);
                Assert.AreEqual(1, probe.Notifications,
                    "a label using a derived style must follow its base — that is what the " +
                    "one level of inheritance is for");
            }
            finally
            {
                StyleInvalidation.Unregister(probe);
            }
        }

        [Test]
        public void LabelFollowsItsStyle_ForSizeAndColour()
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
            label.Text = "Hamburgefonstiv";
            label.FontSize = 16f;

            float plain = label.preferredWidth;
            Assert.Greater(plain, 0f);

            var style = NewStyle("big");
            style.SetFontSize(48f);
            label.Style = style;

            Assert.Greater(label.preferredWidth, plain * 2f,
                "the label did not take its size from the style it was given");

            // And a live edit reaches it without touching the label at all.
            style.SetFontSize(16f);
            Assert.AreEqual(plain, label.preferredWidth, 0.5f,
                "editing the style did not update the label");
        }

        private sealed class Probe : StyleInvalidation.IStyleUser
        {
            private readonly OneTextStyle _style;
            public int Notifications;

            public Probe(OneTextStyle style) => _style = style;

            public bool UsesStyle(OneTextStyle style) =>
                style != null && (style == _style || _style.Extends == style);

            public void OnStyleChanged() => Notifications++;
        }
    }
}
