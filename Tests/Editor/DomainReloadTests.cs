using System.Collections;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// Two play sessions with Domain Reload disabled.
    ///
    /// Turning off Domain Reload is the default advice for iteration speed, and
    /// it changes one assumption the whole engine leans on: statics no longer
    /// die between play sessions. Ours hold a <c>Texture2DArray</c>, a registry
    /// of live graphics, a hidden watcher GameObject and persistent
    /// <c>NativeArray</c>s. Every one of those points at something that
    /// <em>does</em> die at the session boundary, and the symptom of getting it
    /// wrong is not an exception; it is blank text, or quads drawn from a
    /// texture that no longer exists, in the second session only.
    ///
    /// So the test is the ritual: enter, draw, exit, enter, draw again, and
    /// require the second drawing to be as good as the first.
    /// </summary>
    public class DomainReloadTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private bool _previousEnabled;
        private EnterPlayModeOptions _previousOptions;

        [SetUp]
        public void DisableDomainReload()
        {
            _previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            // Scene reload stays ON: the point is surviving statics, not a
            // surviving scene, and a fresh scene per session is what a user gets.
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        }

        [TearDown]
        public void RestoreDomainReload()
        {
            EditorSettings.enterPlayModeOptionsEnabled = _previousEnabled;
            EditorSettings.enterPlayModeOptions = _previousOptions;
        }

        [UnityTest]
        public IEnumerator TextStillDraws_InASecondPlaySession()
        {
            var fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            int firstSessionVerts = 0;

            // Hold the shared atlas open for the whole test. Without this the
            // last label's OnDestroy releases it at every play-mode exit and the
            // next session gets a brand-new one, which is the easy case. The
            // hard case, and the one Domain Reload off creates, is a static that
            // is still holding the previous session's engine objects.
            var held = SharedGlyphAtlas.Acquire();
            try
            {
                for (int session = 0; session < 2; session++)
                {
                    yield return new EnterPlayMode();

                    var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
                    canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

                    var labelObject = new GameObject("Label",
                        typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
                    labelObject.transform.SetParent(canvasObject.transform, worldPositionStays: false);
                    var label = labelObject.GetComponent<OneTextLabel>();
                    label.rectTransform.sizeDelta = new Vector2(400f, 120f);
                    label.SetFont(fontBytes);
                    label.Text = "Hamburgefonstiv";
                    label.FontSize = 32f;

                    // Drive the mesh build directly. A batch-mode editor never
                    // renders a canvas, so waiting for a frame would wait forever,
                    // and it is the mesh build, not the frame, that lays out the text
                    // and rasterizes it into the atlas.
                    yield return null;
                    label.SetAllDirty();
                    label.Rebuild(CanvasUpdate.PreRender);

                    int glyphs = label.LayoutResult.Glyphs.Count;
                    Assert.Greater(glyphs, 0, $"session {session}: the label laid out nothing");
                    if (session == 0) firstSessionVerts = glyphs;
                    else Assert.AreEqual(firstSessionVerts, glyphs,
                        "the second play session drew a different amount of text than the first");

                    Assert.IsTrue(SharedGlyphAtlas.Exists,
                        $"session {session}: no shared atlas after drawing text");
                    Assert.IsTrue(SharedGlyphAtlas.Atlas.IsUsable,
                        $"session {session}: the atlas survived the session boundary but its texture did not; " +
                        "every glyph drawn from it is blank");
                    Assert.Greater(SharedGlyphAtlas.Atlas.GetStats().TileCount, 0,
                        $"session {session}: no glyphs were rasterized");

                    // The watcher is what rebuilds meshes when the atlas moves tiles.
                    // It is a scene object, so it dies with the session while the
                    // static that guards its creation does not.
                    Assert.IsNotNull(GameObject.Find("OneText Atlas Watcher"),
                        $"session {session}: the atlas watcher was never re-created, so nothing will " +
                        "rebuild meshes after a compaction");

                    yield return new ExitPlayMode();

                    // Back in edit mode, still holding the same atlas: this is the
                    // reference that carries session N's engine objects into
                    // session N+1.
                    Assert.IsTrue(held.IsUsable,
                        $"the atlas acquired before session {session} lost its texture when the session ended; " +
                        "anything still holding it now draws nothing");
                }
            }
            finally
            {
                SharedGlyphAtlas.Release();
            }
        }

        [UnityTest]
        public IEnumerator RasterizerBuffers_AreNotCarriedBetweenSessions()
        {
            for (int session = 0; session < 2; session++)
            {
                yield return new EnterPlayMode();

                // Read inside the loop, after the session has started, and not
                // once before it.
                //
                // The [SetUp] above turns Domain Reload off, but a project that
                // had it on when the run started still reloads on the way into
                // the first play session — the setting lands, and the entry
                // already under way does not get it. A reload discards this
                // iterator's locals, so bytes read before the loop come back
                // empty and FontData.Load throws on an empty array, in the
                // first test of the class only. The dev project this is written
                // against already has Domain Reload off in its EditorSettings,
                // so the first entry never reloads and the failure cannot
                // happen there. It is CI-only for that reason and nothing else.
                //
                // Reading per session is also just accurate: two sessions each
                // rasterising from their own bytes is the thing being tested.
                var fontBytes = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

                // Rasterizing at all is the assertion: the persistent NativeArrays
                // behind the batch path are Allocator.Persistent, and a leftover
                // one from the previous session is either disposed (throwing on
                // use) or double-disposed at quit.
                using (var font = FontData.Load(fontBytes))
                using (var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 }))
                {
                    using var shaper = new Shaper();
                    var shaped = new System.Collections.Generic.List<ShapedGlyph>();
                    shaper.Shape(font, "session " + session, shaped);
                    foreach (var glyph in shaped) atlas.GetOrAdd(font, glyph.GlyphId, 48f);
                    atlas.Flush();

                    Assert.Greater(atlas.GetStats().TileCount, 0,
                        $"session {session}: rasterizing produced no tiles");
                }

                yield return new ExitPlayMode();
            }
        }
    }
}
