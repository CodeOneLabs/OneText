using System.Collections.Generic;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The lowercase TMP-parity aliases on <see cref="OneTextLabel"/>.
    ///
    /// They exist so a project with four hundred <c>label.text = …</c> lines
    /// still compiles the day it swaps packages, which is the difference
    /// between a package somebody tries and one they do not. What makes them
    /// worth a test is that an alias is exactly the kind of member that rots:
    /// somebody adds a backing field to one of them and it stops agreeing with
    /// the property it is supposed to be another name for, and the report comes
    /// in as "half my labels ignore the inspector".
    ///
    /// So every assertion below is the same assertion: writing one name and
    /// reading the other gives the same answer, in both directions.
    /// </summary>
    public class TmpParityAliasTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private OneTextLabel NewLabel()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(400f, 120f);
            return label;
        }

        [Test]
        public void Text_RoundTrips()
        {
            var label = NewLabel();

            label.text = "written through the alias";
            Assert.AreEqual("written through the alias", label.Text);
            Assert.AreEqual("written through the alias", label.text);

            label.Text = "written through the property";
            Assert.AreEqual("written through the property", label.text);
        }

        [Test]
        public void SetText_IsAnAssignment()
        {
            var label = NewLabel();
            label.SetText("via SetText");
            Assert.AreEqual("via SetText", label.Text);
        }

        [Test]
        public void FontSize_RoundTrips()
        {
            var label = NewLabel();

            label.fontSize = 41f;
            Assert.AreEqual(41f, label.FontSize);

            label.FontSize = 17f;
            Assert.AreEqual(17f, label.fontSize);
        }

        [Test]
        public void RichText_RoundTrips()
        {
            var label = NewLabel();

            label.richText = false;
            Assert.IsFalse(label.RichText);

            label.RichText = true;
            Assert.IsTrue(label.richText);
        }

        [Test]
        public void AutoSizing_RoundTrips()
        {
            var label = NewLabel();

            label.enableAutoSizing = true;
            label.fontSizeMin = 12f;
            label.fontSizeMax = 72f;

            Assert.IsTrue(label.AutoSize);
            Assert.AreEqual(12f, label.AutoSizeMin);
            Assert.AreEqual(72f, label.AutoSizeMax);

            label.AutoSize = false;
            label.AutoSizeMin = 8f;
            label.AutoSizeMax = 40f;

            Assert.IsFalse(label.enableAutoSizing);
            Assert.AreEqual(8f, label.fontSizeMin);
            Assert.AreEqual(40f, label.fontSizeMax);
        }

        [Test]
        public void MaxVisibleCharacters_IsTheGraphemeCount()
        {
            // The one alias that is not an exact synonym: OneText reveals
            // grapheme clusters, TMP counted UTF-16 characters. It forwards
            // rather than converting, because there is nothing to convert to —
            // a flag emoji is one cluster and four chars, and the caller's
            // typewriter wants the cluster.
            var label = NewLabel();

            label.maxVisibleCharacters = 5;
            Assert.AreEqual(5, label.MaxVisibleGraphemes);

            label.MaxVisibleGraphemes = -1;
            Assert.AreEqual(-1, label.maxVisibleCharacters);
        }

        [Test]
        public void Alignment_SplitsAcrossTheTwoAxesAndComesBack()
        {
            var label = NewLabel();

            label.alignment = TextAlignmentOptions.BottomRight;
            Assert.AreEqual(TextAlignment.Right, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Bottom, label.VerticalAlignment);
            Assert.AreEqual(TextAlignmentOptions.BottomRight, label.alignment);

            label.Alignment = TextAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Top;
            Assert.AreEqual(TextAlignmentOptions.Top, label.alignment);

            // Every value TMP can hold, assigned and read back: the ones with a
            // OneText equivalent have to survive the round trip exactly.
            foreach (TextAlignmentOptions value in new[]
            {
                TextAlignmentOptions.TopLeft, TextAlignmentOptions.Top,
                TextAlignmentOptions.TopRight, TextAlignmentOptions.TopJustified,
                TextAlignmentOptions.Left, TextAlignmentOptions.Center,
                TextAlignmentOptions.Right, TextAlignmentOptions.Justified,
                TextAlignmentOptions.BottomLeft, TextAlignmentOptions.Bottom,
                TextAlignmentOptions.BottomRight, TextAlignmentOptions.BottomJustified,
            })
            {
                label.alignment = value;
                Assert.AreEqual(value, label.alignment, $"{value} did not survive the round trip");
            }
        }

        [Test]
        public void Alignment_ResolvesWhatOneTextDoesNotDraw()
        {
            // The six TMP distinctions with no counterpart land on the nearest
            // thing, which is the same answer the Onboarding migration gives —
            // it is the same function. The difference is that the migration
            // reports the approximation and a property setter cannot.
            var label = NewLabel();

            label.alignment = TextAlignmentOptions.MidlineLeft;
            Assert.AreEqual(TextAlignment.Left, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Middle, label.VerticalAlignment);

            label.alignment = TextAlignmentOptions.CaplineRight;
            Assert.AreEqual(TextAlignment.Right, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Top, label.VerticalAlignment);

            label.alignment = TextAlignmentOptions.TopFlush;
            Assert.AreEqual(TextAlignment.Justified, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Top, label.VerticalAlignment);

            label.alignment = TextAlignmentOptions.CenterGeoAligned;
            Assert.AreEqual(TextAlignment.Center, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Middle, label.VerticalAlignment);
        }

        [Test]
        public void Alignment_ReadsAStartEdgeAsTheSideItResolvesTo()
        {
            // The lossy direction, asserted rather than left to be discovered:
            // TMP has no start edge, so a label left in OneText's own default
            // answers Left, and assigning that answer back pins it. Nothing the
            // setter produces is ever Start, so this is the only way in.
            var label = NewLabel();
            label.VerticalAlignment = VerticalAlignment.Middle;

            label.Alignment = TextAlignment.Start;
            Assert.AreEqual(TextAlignmentOptions.Left, label.alignment,
                "Start should read as the left edge it resolves to");

            label.Alignment = TextAlignment.End;
            Assert.AreEqual(TextAlignmentOptions.Right, label.alignment);
        }

        [Test]
        public void LineSpacing_IsAnOffsetOnTheAliasAndAMultiplierOnTheProperty()
        {
            var label = NewLabel();

            // Ten percent looser: 10 in TMP's units, 1.1 in OneText's.
            label.lineSpacing = 10f;
            Assert.AreEqual(1.1f, label.LineSpacing, 1e-5f);

            label.lineSpacing = 0f;
            Assert.AreEqual(1f, label.LineSpacing, 1e-5f,
                "TMP's zero is the font's own line height, which is OneText's one");

            label.lineSpacing = -50f;
            Assert.AreEqual(0.5f, label.LineSpacing, 1e-5f);

            label.LineSpacing = 2f;
            Assert.AreEqual(100f, label.lineSpacing, 1e-3f);

            // And the conversion is the migration's, not a second opinion.
            Assert.AreEqual(TmpCompat.LineSpacingFromTmp(37f),
                Assign(label, 37f), 1e-5f);
        }

        private static float Assign(OneTextLabel label, float tmpLineSpacing)
        {
            label.lineSpacing = tmpLineSpacing;
            return label.LineSpacing;
        }

        [Test]
        public void WrappingMode_RoundTripsAndDropsOnlyTheWhitespaceHalf()
        {
            var label = NewLabel();

            label.textWrappingMode = TextWrappingModes.NoWrap;
            Assert.AreEqual(TextWrap.NoWrap, label.Wrap);
            Assert.AreEqual(TextWrappingModes.NoWrap, label.textWrappingMode);

            label.textWrappingMode = TextWrappingModes.Normal;
            Assert.AreEqual(TextWrap.Wrap, label.Wrap);
            Assert.AreEqual(TextWrappingModes.Normal, label.textWrappingMode);

            // The preserving modes set the wrap they imply; the whitespace bit
            // is not something OneText holds, so it reads back as the plain one.
            label.textWrappingMode = TextWrappingModes.PreserveWhitespace;
            Assert.AreEqual(TextWrap.Wrap, label.Wrap);
            Assert.AreEqual(TextWrappingModes.Normal, label.textWrappingMode);

            label.textWrappingMode = TextWrappingModes.PreserveWhitespaceNoWrap;
            Assert.AreEqual(TextWrap.NoWrap, label.Wrap);
            Assert.AreEqual(TextWrappingModes.NoWrap, label.textWrappingMode);
        }

        [Test]
        public void EnableWordWrapping_IsTheSameAxisAsABool()
        {
            var label = NewLabel();

            label.enableWordWrapping = false;
            Assert.AreEqual(TextWrap.NoWrap, label.Wrap);
            Assert.IsFalse(label.enableWordWrapping);

            label.enableWordWrapping = true;
            Assert.AreEqual(TextWrap.Wrap, label.Wrap);
            Assert.IsTrue(label.enableWordWrapping);
        }

        [Test]
        public void TheAliases_AreHiddenFromCompletion()
        {
            // They are a migration ramp, not API: new code written against this
            // class should never see them offered. The attribute is the whole
            // mechanism for that, so its absence is a silent regression.
            foreach (string name in new[]
            {
                "text", "fontSize", "richText", "enableAutoSizing",
                "fontSizeMin", "fontSizeMax", "maxVisibleCharacters",
                "alignment", "lineSpacing", "textWrappingMode", "enableWordWrapping",
            })
            {
                var member = typeof(OneTextLabel).GetProperty(name);
                Assert.NotNull(member, $"the parity alias {name} is gone");
                Assert.IsNotEmpty(
                    member.GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute),
                        false),
                    $"{name} is not hidden from completion");
            }
        }

        [Test]
        public void TheAliasesAndTheMigration_AreTheSameArithmetic()
        {
            // lineSpacing and alignment were traps once: TMP's spacing is an
            // offset and OneText's a multiplier, and TMP's alignment enum did
            // not exist here. They are aliased now, and what makes that safe is
            // that the alias converts rather than forwards — through the same
            // TmpCompat functions the Onboarding migration uses, so a value
            // assigned through the alias and a value carried by the migration
            // cannot disagree. This asserts the sharing indirectly: same
            // inputs, same answers, over the values that would expose a fork.
            TmpCompat.SplitAlignment(
                (int)TextAlignmentOptions.MidlineRight,
                out var horizontal, out var vertical, out bool approximated, out string what);
            Assert.AreEqual(TextAlignment.Right, horizontal);
            Assert.AreEqual(VerticalAlignment.Middle, vertical);
            Assert.IsTrue(approximated, "Midline is a distinction OneText does not draw");
            Assert.AreEqual("Midline", what);

            var label = NewLabel();
            label.alignment = TextAlignmentOptions.MidlineRight;
            Assert.AreEqual(horizontal, label.Alignment);
            Assert.AreEqual(vertical, label.VerticalAlignment);

            label.lineSpacing = 25f;
            Assert.AreEqual(TmpCompat.LineSpacingFromTmp(25f), label.LineSpacing, 1e-5f);
        }
    }
}
