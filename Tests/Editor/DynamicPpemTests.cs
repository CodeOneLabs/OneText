using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The measured screen density: a label baked for the pixels it will
    /// actually cover, not for the font size alone.
    ///
    /// Three claims, tested separately. The measurement is right (canvas
    /// scale, transform scale, orthographic and perspective projection each
    /// contribute what geometry says they do). The application is hysteretic
    /// (a wobble near a bucket boundary re-bakes nothing; a real move re-bakes
    /// once). And the cap holds (a camera on top of a world canvas cannot ask
    /// the atlas for tiles without bound, while an explicit large font size
    /// still gets exactly what it always got).
    /// </summary>
    public class DynamicPpemTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            // Cameras let go of their target before the target is destroyed,
            // or the engine logs an error the test framework counts as a
            // failure.
            foreach (var created in _created)
                if (created is GameObject go && go != null &&
                    go.TryGetComponent<Camera>(out var camera))
                    camera.targetTexture = null;
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextLabel.DynamicPpem = true;
            OneTextLabel.PpemCap = 128f;
        }

        private Canvas NewCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(go);
            var canvas = go.GetComponent<Canvas>();
            // Explicit, not assumed: a bare Canvas component in a batch-mode
            // scene reports a render mode that sends the measurement down the
            // camera path — toward the default scene's Main Camera, ten units
            // behind the origin — and every number below silently becomes a
            // projection nobody meant to test.
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private OneTextLabel NewLabel(Canvas canvas, string text, float size = 64f)
        {
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = size;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private Camera NewCamera(int pixelHeight)
        {
            var go = new GameObject("Camera", typeof(Camera));
            _created.Add(go);
            var target = new RenderTexture(pixelHeight * 2, pixelHeight, 0);
            _created.Add(target);
            var camera = go.GetComponent<Camera>();
            camera.targetTexture = target;
            return camera;
        }

        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        // -------------------------------------------------------- measurement

        [Test]
        public void OnAPlainCanvas_TheScaleIsTheLossyScale()
        {
            // An overlay canvas maps canvas units to screen pixels one to one,
            // so everything between the label and the screen is transforms —
            // including the scale factor, which the canvas writes into its own
            // root transform. The larger axis wins: the stretched axis is the
            // one that would be magnified.
            var label = NewLabel(NewCanvas(), "W");
            label.transform.localScale = new Vector3(2f, 3f, 1f);

            Assert.AreEqual(3f, ScreenPpem.Compute(label), 1e-3f);
        }

        [Test]
        public void UnderAnOrthographicCamera_PixelsPerUnitIsTheFrustum()
        {
            // An orthographic camera shows 2 * orthographicSize world units
            // across its pixel height, wherever the label sits: 100 pixels over
            // 10 units is 10, with no distance term to get wrong.
            var canvas = NewCanvas();
            canvas.renderMode = RenderMode.WorldSpace;
            var camera = NewCamera(100);
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            canvas.worldCamera = camera;

            var label = NewLabel(canvas, "W");
            label.transform.localScale = Vector3.one;
            canvas.transform.localScale = Vector3.one;

            Assert.AreEqual(10f, ScreenPpem.Compute(label), 1e-2f);
        }

        [Test]
        public void UnderAPerspectiveCamera_TheScaleFallsWithDepth()
        {
            // The perspective divide, measured: at 60 degrees of vertical fov
            // the frustum is 2 * depth * tan(30) units tall, so 400 pixels at
            // depth 10 is 34.6 per unit — and twice the depth is half the
            // scale, which is the fact the watcher exists to notice.
            var canvas = NewCanvas();
            canvas.renderMode = RenderMode.WorldSpace;
            var camera = NewCamera(400);
            camera.orthographic = false;
            camera.fieldOfView = 60f;
            canvas.worldCamera = camera;

            var label = NewLabel(canvas, "W");
            canvas.transform.position = new Vector3(0f, 0f, 10f);
            float near = ScreenPpem.Compute(label);
            Assert.AreEqual(400f / (20f * Mathf.Tan(30f * Mathf.Deg2Rad)), near, 0.1f);

            canvas.transform.position = new Vector3(0f, 0f, 20f);
            Assert.AreEqual(near * 0.5f, ScreenPpem.Compute(label), 0.1f);
        }

        [Test]
        public void BehindThePerspectiveCamera_TheMeasurementDeclines()
        {
            // Zero, not a negative or an infinity: the caller keeps what it
            // last applied, which is the only sane picture for a label the
            // camera cannot currently see.
            var canvas = NewCanvas();
            canvas.renderMode = RenderMode.WorldSpace;
            var camera = NewCamera(400);
            camera.orthographic = false;
            canvas.worldCamera = camera;
            var label = NewLabel(canvas, "W");
            canvas.transform.position = new Vector3(0f, 0f, -10f);

            Assert.AreEqual(0f, ScreenPpem.Compute(label));
        }

        // -------------------------------------------------------- application

        [Test]
        public void ARebuild_BakesAtTheMeasuredScale()
        {
            var label = NewLabel(NewCanvas(), "W");
            label.transform.localScale = Vector3.one * 3f;
            Draw(label);

            Assert.AreEqual(3f, label.AppliedPpemScale, 1e-3f);
        }

        [Test]
        public void ZoomingOut_NeverDropsBelowTheFontSize()
        {
            // The floor at one: a label zoomed away from keeps the tiles its
            // font size asks for — exactly what it drew before any of this —
            // so a zoom-out invalidates nothing and costs nothing.
            var label = NewLabel(NewCanvas(), "W");
            label.transform.localScale = Vector3.one * 0.25f;
            Draw(label);

            Assert.AreEqual(1f, label.AppliedPpemScale);
        }

        [Test]
        public void AWobbleInsideTheBand_RebakesNothing()
        {
            // The hysteresis, end to end: five percent is inside the band, so
            // the applied scale holds and the cached quads survive; a third is
            // outside it, so the label re-bakes exactly once.
            var label = NewLabel(NewCanvas(), "Wave");
            label.transform.localScale = Vector3.one * 3f;
            Draw(label);
            int builds = label.QuadBuilds;

            label.transform.localScale = Vector3.one * 3.15f;
            Draw(label);
            Assert.AreEqual(3f, label.AppliedPpemScale, 1e-3f, "a 5% wobble was applied");
            Assert.AreEqual(builds, label.QuadBuilds, "a 5% wobble rebuilt the quads");

            label.transform.localScale = Vector3.one * 4f;
            Draw(label);
            Assert.AreEqual(4f, label.AppliedPpemScale, 1e-3f);
            Assert.AreEqual(builds + 1, label.QuadBuilds,
                "a real move must rebuild, once");
        }

        [Test]
        public void TheWatcher_NoticesAScaleChangeNothingDirtied()
        {
            // The reason the watcher exists: a camera dolly changes no label
            // property and dirties nothing, and without the canvas-pass check
            // the label would keep drawing its stale density forever.
            var label = NewLabel(NewCanvas(), "W");
            Draw(label);
            Assert.AreEqual(1f, label.AppliedPpemScale);

            label.transform.localScale = Vector3.one * 5f;
            // The sweep the canvas pass runs every frame, invoked directly:
            // batch mode has no player loop to fire the event for us.
            ScreenPpem.PollNow();

            Assert.AreEqual(5f, label.AppliedPpemScale, 1e-3f,
                "the poll did not re-measure the label");
        }

        [Test]
        public void TurnedOff_TheScaleFallsBackToOne()
        {
            var label = NewLabel(NewCanvas(), "W");
            label.transform.localScale = Vector3.one * 3f;
            Draw(label);
            Assert.AreEqual(3f, label.AppliedPpemScale, 1e-3f);

            OneTextLabel.DynamicPpem = false;
            Draw(label);
            Assert.AreEqual(1f, label.AppliedPpemScale,
                "the kill switch left a measured scale applied");
        }

        // --------------------------------------------------------------- cap

        /// <summary>
        /// The widest quad's tile width, in atlas texels — the observable a
        /// density change actually changes. Geometry stays put; the uv rect
        /// widens with the bucket.
        /// </summary>
        private static float WidestTileTexels(OneTextLabel label)
        {
            float widest = 0f;
            foreach (var quad in label.DrawnQuads)
                widest = Mathf.Max(widest, quad.UvRect.width);
            return widest * SharedGlyphAtlas.Atlas.Settings.TextureSize;
        }

        [Test]
        public void TheCap_HoldsTheMeasuredDensityAt128()
        {
            // 64-point text at 8x would ask for 512 ppem; the ladder's 256
            // bucket exists, so only the cap stands between the zoom and a
            // tile sixteen times the area of the honest one. Capped at 128 the
            // tile is about twice the 64 ppem width (plus the fixed padding
            // ring); uncapped it would be about four times.
            var canvas = NewCanvas();
            var reference = NewLabel(canvas, "W");
            Draw(reference);
            float at64 = WidestTileTexels(reference);
            Assert.Greater(at64, 0f);

            var zoomed = NewLabel(canvas, "W");
            zoomed.transform.localScale = Vector3.one * 8f;
            Draw(zoomed);
            float capped = WidestTileTexels(zoomed);

            Assert.Greater(capped, at64 * 1.5f, "the zoom was not densified at all");
            Assert.Less(capped, at64 * 2.5f, "the cap did not hold: the tile kept growing");
        }

        [Test]
        public void AnExplicitLargeFontSize_IsCappedLikeEverythingElse()
        {
            // The cap binds whatever asked, the font size included: a single
            // 256 ppem Hangul glyph is ~70K texels — fifteen to a default
            // layer — and a heading must not eat the atlas because somebody
            // typed a large number into a field. A 288-point label bakes at
            // the 128 bucket and draws 2.25x magnified, which a distance
            // field does smoothly.
            var canvas = NewCanvas();
            var reference = NewLabel(canvas, "W");
            Draw(reference);
            float at64 = WidestTileTexels(reference);
            Assert.Greater(at64, 0f);

            var large = NewLabel(canvas, "W", size: 288f);
            Draw(large);
            float at288 = WidestTileTexels(large);

            Assert.Greater(at288, at64 * 1.5f, "the large size was not densified at all");
            Assert.Less(at288, at64 * 2.5f,
                "an explicit size sailed past the cap the zoom is held to");
        }
    }
}
