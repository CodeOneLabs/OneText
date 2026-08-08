using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The shared SDF material's lifetime against the one thing uGUI forbids:
    /// dirtying a graphic from inside the graphic rebuild loop.
    ///
    /// A label adopts the shared material in <c>OnEnable</c>, outside the loop.
    /// That is only safe if the material it adopted is still alive by the time
    /// its first mesh rebuild runs — otherwise <c>EnsureMaterial</c> re-assigns
    /// it from <c>OnPopulateMesh</c>, and uGUI logs "Trying to add ... for
    /// graphic rebuild while we are already inside a graphic rebuild loop" on
    /// every rebuild. So what these tests pin down is the refcount: an enabled
    /// label must hold the atlas from enable, not from first draw, and the
    /// hold must not survive serialization into a domain where the count it
    /// belongs to no longer exists.
    /// </summary>
    public class MaterialLifecycleTests
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
            return go.GetComponent<Canvas>();
        }

        private OneTextLabel NewLabel(Canvas canvas, string text)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(400f, 120f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 32f;
            label.Text = text;
            return label;
        }

        [Test]
        public void DestroyingTheLastDrawnLabel_DoesNotDirtyANewLabelMidRebuild()
        {
            var canvas = NewCanvas();

            var first = NewLabel(canvas, "first");
            Canvas.ForceUpdateCanvases();

            // The window: this label has enabled (and adopted the shared
            // material) but has not yet been through a rebuild. If enabling did
            // not also take an atlas reference, destroying the only label that
            // did take one drops the refcount to zero and destroys the material
            // this label is pointing at.
            var second = NewLabel(canvas, "second");
            var sharedBeforeDestroy = SharedGlyphAtlas.Material;
            Object.DestroyImmediate(first.gameObject);

            Assert.IsTrue(sharedBeforeDestroy != null,
                "destroying one label destroyed the shared material out from under " +
                "an enabled label that had not drawn yet");

            // This is the second label's first trip through the graphic rebuild
            // loop. If the material died above, EnsureMaterial re-assigns it in
            // here, which dirties the graphic mid-loop and makes uGUI log the
            // "already inside a graphic rebuild loop" error that fails this test.
            Canvas.ForceUpdateCanvases();

            Assert.AreEqual(SharedGlyphAtlas.Material, second.material,
                "the label came out of its first rebuild without the shared material");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TheAtlasHold_DoesNotSurviveSerialization()
        {
            // A domain reload resets the atlas's static refcount to zero, but it
            // serializes private instance fields — including a bool with no
            // [SerializeField] on it. A resurrected true here means the label
            // skips re-acquiring and its OnDestroy releases a reference the new
            // domain never counted, destroying the shared material under every
            // other live label; their next rebuild then re-assigns it from
            // inside the rebuild loop. Only [NonSerialized] opts the flag out.
            foreach (var type in new[] { typeof(OneTextLabel), typeof(OneTextMesh) })
            {
                var field = type.GetField("_atlasHeld",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, $"{type.Name} no longer has _atlasHeld; " +
                    "if the hold moved, move this serialization guard with it");
                Assert.IsTrue(field.IsNotSerialized,
                    $"{type.Name}._atlasHeld must be [NonSerialized]: a domain " +
                    "reload resets the shared atlas refcount, and a serialized " +
                    "hold releases a reference the new domain never counted");
            }
        }
    }
}
