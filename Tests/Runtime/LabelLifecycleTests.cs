using System.Collections;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// Tier 2: what a label does across real frames, which is the half of its
    /// behaviour EditMode cannot reach.
    ///
    /// An EditMode test builds a label, calls Rebuild by hand and reads the
    /// mesh. That covers the geometry and none of the lifetime: OnEnable and
    /// OnDisable never run in pairs, the shared atlas is never acquired and
    /// released as objects come and go, Update never ticks, and a component
    /// destroyed halfway through a canvas rebuild is a state the editor path
    /// cannot produce at all. Every one of those is a shipping failure mode —
    /// a pooled dialogue box, a scene unload, a label toggled by a UI
    /// animation — and every one of them needs a running player loop to fail
    /// in.
    /// </summary>
    public class LabelLifecycleTests
    {
        private readonly PlayHarness _harness = new PlayHarness();

        [SetUp]
        public void Setup() => _harness.Setup();

        [TearDown]
        public void Teardown() => _harness.Teardown();

        [UnityTest]
        public IEnumerator A_Label_Created_At_Runtime_Draws_Within_One_Frame()
        {
            var label = _harness.Label("Created at runtime");
            yield return PlayHarness.Frame();

            Assert.Greater(PlayHarness.DrawnQuads(label), 0,
                "a label built from code drew nothing after a full frame");
            Assert.AreEqual("Created at runtime".Length, label.GraphemeCount,
                "plain ASCII is one grapheme per character");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Enable_Disable_Cycles_Keep_The_Geometry_Coming_Back()
        {
            var label = _harness.Label("Toggled repeatedly");
            yield return PlayHarness.Frame();
            int quads = PlayHarness.DrawnQuads(label);
            Assert.Greater(quads, 0);

            // Ten cycles, each spanning frames, because the failure this
            // catches is a resource released on the way out and not taken
            // again on the way back in: the label comes back blank, and only
            // on the second showing.
            for (int cycle = 0; cycle < 10; cycle++)
            {
                label.enabled = false;
                yield return PlayHarness.Frame();
                Assert.AreEqual(0, PlayHarness.DrawnQuads(label),
                    $"a disabled label still had geometry on cycle {cycle}");

                label.enabled = true;
                yield return PlayHarness.Frame();
                Assert.AreEqual(quads, PlayHarness.DrawnQuads(label),
                    $"the label came back with different geometry on cycle {cycle}");
            }

            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator GameObject_Activation_Cycles_Survive_The_Same_Way()
        {
            var label = _harness.Label("Activated repeatedly");
            yield return PlayHarness.Frame();
            int quads = PlayHarness.DrawnQuads(label);

            for (int cycle = 0; cycle < 5; cycle++)
            {
                label.gameObject.SetActive(false);
                yield return PlayHarness.Frame();
                label.gameObject.SetActive(true);
                yield return PlayHarness.Frame();
            }

            Assert.AreEqual(quads, PlayHarness.DrawnQuads(label));
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Labels_Created_And_Destroyed_Every_Frame_Leave_Nothing_Behind()
        {
            // A pooled dialogue system in miniature: something is always being
            // born and something is always dying, and the shared atlas's
            // reference count has to survive both without the last label out
            // taking the atlas with it while others still draw from it.
            var survivor = _harness.Label("Survivor");
            yield return PlayHarness.Frame();

            for (int frame = 0; frame < 40; frame++)
            {
                var transient = _harness.Label($"Transient {frame}", 24f);
                yield return PlayHarness.Frame();

                Assert.Greater(PlayHarness.DrawnQuads(transient), 0,
                    $"the label made on frame {frame} never drew");
                Object.Destroy(transient.gameObject);
                yield return PlayHarness.Frame();

                Assert.Greater(PlayHarness.DrawnQuads(survivor), 0,
                    $"destroying a neighbour blanked the survivor on frame {frame}");
            }

            Assert.IsTrue(SharedGlyphAtlas.Exists, "the shared atlas went away under a live label");
            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator Destroying_A_Label_In_The_Same_Frame_It_Was_Dirtied_Is_Safe()
        {
            // The awkward order: mark the label dirty, then destroy it before
            // the canvas gets round to rebuilding it. uGUI keeps dirty
            // graphics in a static registry, so this is where a destroyed
            // component gets asked to rebuild itself.
            for (int i = 0; i < 20; i++)
            {
                var label = _harness.Label($"Doomed {i}");
                label.SetAllDirty();
                Object.Destroy(label.gameObject);
                yield return PlayHarness.Frame();
            }

            PlayHarness.ExpectNoErrors();
        }

        [UnityTest]
        public IEnumerator A_Label_Whose_Canvas_Is_Destroyed_Under_It_Does_Not_Throw()
        {
            var label = _harness.Label("Canvas about to go");
            yield return PlayHarness.Frame();

            Object.Destroy(_harness.Canvas.gameObject);
            yield return PlayHarness.Frame();
            yield return PlayHarness.Frame();

            PlayHarness.ExpectNoErrors();
        }
    }
}
