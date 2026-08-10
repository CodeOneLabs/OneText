using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The shared face behind <c>SetFont(bytes)</c>: a hundred labels handed
    /// the same font parse it once and bake one set of tiles, and the last
    /// label out frees it. The variation exception matters as much as the
    /// sharing: a variated face is mutated in place, so it must stay private
    /// or one label's weight becomes everybody's.
    /// </summary>
    public class FontShareTests
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

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private OneTextLabel NewLabel(Canvas canvas, byte[] fontBytes, string text)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);
            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(fontBytes);
            label.FontSize = 32f;
            label.Text = text;
            return label;
        }

        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        private static byte[] Bytes() => File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

        [Test]
        public void LabelsSharingBytes_ShareOneFace()
        {
            int before = SharedFontBytes.LiveEntries;
            var canvas = NewCanvas();
            var bytes = Bytes();

            var first = NewLabel(canvas, bytes, "one");
            var second = NewLabel(canvas, bytes, "two");
            Draw(first);
            Draw(second);

            Assert.AreEqual(before + 1, SharedFontBytes.LiveEntries,
                "two labels on the same array parsed two faces");
        }

        [Test]
        public void ACopyOfTheBytes_StillSharesTheFace()
        {
            // The caller that calls File.ReadAllBytes once per label hands a
            // different array with the same content, and content is what the
            // cache is keyed on; identity is only the fast path.
            int before = SharedFontBytes.LiveEntries;
            var canvas = NewCanvas();

            var first = NewLabel(canvas, Bytes(), "one");
            var second = NewLabel(canvas, Bytes(), "two");
            Draw(first);
            Draw(second);

            Assert.AreEqual(before + 1, SharedFontBytes.LiveEntries,
                "equal content in a different array re-parsed the face");
        }

        [Test]
        public void TheLastLabelOut_FreesTheFace()
        {
            int before = SharedFontBytes.LiveEntries;
            var canvas = NewCanvas();
            var bytes = Bytes();
            var first = NewLabel(canvas, bytes, "one");
            var second = NewLabel(canvas, bytes, "two");
            Draw(first);
            Draw(second);

            Object.DestroyImmediate(first.gameObject);
            Assert.AreEqual(before + 1, SharedFontBytes.LiveEntries,
                "the face died while a label still drew with it");

            Object.DestroyImmediate(second.gameObject);
            Assert.AreEqual(before, SharedFontBytes.LiveEntries,
                "the last label released and the face survived anyway");
        }

        [Test]
        public void AVariatedLabel_KeepsItsFacePrivate()
        {
            int before = SharedFontBytes.LiveEntries;
            var canvas = NewCanvas();
            var bytes = Bytes();

            var plain = NewLabel(canvas, bytes, "plain");
            var bold = NewLabel(canvas, bytes, "bold");
            bold.SetVariations(new FontVariation("wght", 700f));
            Draw(plain);
            Draw(bold);

            // One shared face for the plain label; the variated one is not
            // the cache's business at all.
            Assert.AreEqual(before + 1, SharedFontBytes.LiveEntries,
                "the variated face went through the shared cache");
            Assert.Greater(plain.DrawnQuads.Count, 0);
            Assert.Greater(bold.DrawnQuads.Count, 0);
        }
    }
}
