using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// A device that cannot hold a <c>Texture2DArray</c> at all.
    ///
    /// <para>Headless servers, players started with <c>-nographics</c> and
    /// GLES2-class GPUs all answer false to
    /// <c>SystemInfo.supports2DArrayTextures</c>, and the atlas constructor
    /// threw on every one of them. It threw from <c>OnEnable</c>, so merely
    /// switching a label on was fatal: a dedicated server could not load the
    /// scene its client loads, and a <c>-nographics</c> test run failed on the
    /// first label it created rather than on anything it meant to test.</para>
    ///
    /// <para>Nothing above the atlas wants pixels — layout, measurement,
    /// hit testing and the input field are arithmetic over font tables — so the
    /// contract pinned down here is: a label is creatable and enableable,
    /// layout is exactly what it would be with a GPU, drawing produces nothing,
    /// and the session says so once rather than once per label.</para>
    ///
    /// <para><c>SystemInfo</c> cannot be written to and every machine that runs
    /// this suite has array textures, so the capability is faked through
    /// <see cref="GlyphAtlas.ForceUnsupportedForTests"/>. TearDown both clears
    /// it and rebuilds the shared atlas: a textureless atlas left in the static
    /// would blank the text of every fixture that runs afterwards, and the
    /// failures would point anywhere but here.</para>
    /// </summary>
    public class HeadlessAtlasTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private GameObject _canvas;

        [SetUp]
        public void PretendTheDeviceHasNoArrayTextures()
        {
            GlyphAtlas.ForceUnsupportedForTests = true;
            // Forced, because the atlas another fixture left behind is alive
            // and usable and would be kept: the budget has not changed, only
            // the pretence about the device has.
            SharedGlyphAtlas.Reconfigure(force: true);
        }

        [TearDown]
        public void RestoreTheDevice()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
            _canvas = null;
            GlyphAtlas.ForceUnsupportedForTests = null;
            SharedGlyphAtlas.Reconfigure(force: true);
        }

        private OneTextLabel CreateLabel(string text)
        {
            _canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            // The component is added by the constructor, so OnEnable — and with
            // it the atlas acquisition that used to throw — runs on this line.
            var labelObject = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            labelObject.transform.SetParent(_canvas.transform, worldPositionStays: false);

            var label = labelObject.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(400f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 32f;
            label.Text = text;
            return label;
        }

        // A batch-mode editor never renders a canvas, so it is the mesh build
        // and not the frame that lays the text out and asks the atlas for tiles.
        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            Canvas.ForceUpdateCanvases();
        }

        [Test]
        public void Enabling_A_Label_Without_Array_Textures_Does_Not_Throw()
        {
            var label = CreateLabel("Headless");

            Assert.IsFalse(SharedGlyphAtlas.Atlas.IsUsable,
                "the atlas must report itself unusable so every pixel path stays a no-op");
            Assert.IsNull(SharedGlyphAtlas.Atlas.Texture,
                "no Texture2DArray may be allocated on a device that cannot hold one");
            Assert.IsFalse(SharedGlyphAtlas.Exists,
                "an atlas with no texture is not an atlas anything should bind");

            Assert.DoesNotThrow(() => Draw(label),
                "a rebuild must run to completion and simply emit nothing");
            Assert.AreEqual(0, SharedGlyphAtlas.Atlas.GetStats().TileCount,
                "nothing can have been baked without a texture to bake it into");
        }

        [Test]
        public void Layout_Is_Unaffected_By_The_Missing_Atlas()
        {
            var label = CreateLabel("Hello\nWorld");

            var result = label.EnsureLayout();
            Assert.AreEqual(2, result.Lines.Count, "the hard break still starts a second line");
            Assert.AreEqual(10, result.Glyphs.Count,
                "every character still shapes and positions; only its picture is missing");
            Assert.Greater(result.Width, 0f, "advances come from the font, not from the atlas");
            Assert.Greater(result.Height, 0f);
        }

        [Test]
        public void Nothing_Is_Ever_Pending_And_The_Flush_Scheduler_Cannot_Throw()
        {
            var label = CreateLabel("Upload nothing");
            Draw(label);

            Assert.IsFalse(SharedGlyphAtlas.Atlas.HasPendingUpload,
                "an atlas that cannot be written to can never owe an upload");
            Assert.DoesNotThrow(() => AtlasFlushScheduler.Request());
            Assert.DoesNotThrow(() => AtlasFlushScheduler.FlushNow());
            Assert.DoesNotThrow(() => SharedGlyphAtlas.Atlas.Flush());
            Assert.DoesNotThrow(() => SharedGlyphAtlas.Atlas.Compact());
            Assert.IsFalse(SharedGlyphAtlas.Atlas.HasPendingUpload);
        }

        [Test]
        public void The_Unusable_Atlas_Is_Kept_Rather_Than_Rebuilt()
        {
            // The shared getter discards an atlas whose texture died, because
            // that means a play session ended underneath it. An atlas that
            // never had a texture looks identical and means the opposite, and
            // discarding it would dispose and re-create one on every access —
            // an allocation loop driven by the getter every label calls.
            var first = SharedGlyphAtlas.Atlas;
            var second = SharedGlyphAtlas.Atlas;
            Assert.AreSame(first, second, "a second access rebuilt the atlas");

            var label = CreateLabel("Same atlas");
            Draw(label);
            Assert.AreSame(first, SharedGlyphAtlas.Atlas,
                "enabling and drawing a label rebuilt the atlas");
        }

        [Test]
        public void Dropping_The_Pretence_Gives_A_Drawing_Atlas_Back()
        {
            if (!SystemInfo.supports2DArrayTextures)
                Assert.Ignore("this runner has no array textures either, so there is " +
                    "nothing to restore to; the -nographics run is the other half of the proof");

            Assert.IsFalse(SharedGlyphAtlas.Atlas.IsUsable, "SetUp should have left a dead atlas");

            // Exactly what TearDown does, asserted here rather than trusted:
            // the failure mode of leaking the dead atlas is unrelated fixtures
            // drawing blank text, which points anywhere but at this file.
            GlyphAtlas.ForceUnsupportedForTests = null;
            SharedGlyphAtlas.Reconfigure(force: true);

            var label = CreateLabel("Alive again");
            Draw(label);

            Assert.IsTrue(SharedGlyphAtlas.Atlas.IsUsable,
                "the capability came back but the atlas did not");
            Assert.IsNotNull(SharedGlyphAtlas.Atlas.Texture);
            Assert.IsTrue(SharedGlyphAtlas.Exists);
            Assert.Greater(label.LayoutResult.Glyphs.Count, 0);
        }
    }
}
