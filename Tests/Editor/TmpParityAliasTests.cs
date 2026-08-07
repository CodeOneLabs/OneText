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
        public void TheAliases_AreHiddenFromCompletion()
        {
            // They are a migration ramp, not API: new code written against this
            // class should never see them offered. The attribute is the whole
            // mechanism for that, so its absence is a silent regression.
            foreach (string name in new[]
            {
                "text", "fontSize", "richText", "enableAutoSizing",
                "fontSizeMin", "fontSizeMax", "maxVisibleCharacters",
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
        public void TheTrapsAreStillNotAliased()
        {
            // lineSpacing is an offset in TMP and a multiplier here, and
            // alignment names an enum that does not exist in this package. An
            // alias for either compiles and then quietly lays every paragraph
            // in the project out differently, which is worse than the compile
            // error the absence produces.
            Assert.IsNull(typeof(OneTextLabel).GetProperty("lineSpacing"),
                "lineSpacing was aliased: TMP's is an offset, OneText's is a multiplier");
            Assert.IsNull(typeof(OneTextLabel).GetProperty("alignment"),
                "alignment was aliased to an enum with different members");
        }
    }
}
