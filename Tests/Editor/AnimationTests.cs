using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M9: tag-driven animation.
    ///
    /// The claim is not "text can wobble"; anything can wobble. It is that
    /// effects animate <em>grapheme clusters</em>, which is possible here and
    /// not possible in an animation layer bolted onto a text engine: from
    /// outside there is no way to know which character became which glyph, and
    /// the answer stops existing entirely once a ligature appears. So most of
    /// these tests are about what an effect is addressed by, and about the
    /// promise that animating costs vertex writes rather than rebuilds.
    /// </summary>
    public class AnimationTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private OneTextLabel NewLabel(string text, string fontPath = LatinFontPath)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.FontSize = 32f;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        /// <summary>
        /// Rebuilds and returns the tiles <em>as drawn</em>, after effects.
        /// Reading the pre-effect geometry would test that layout is stable,
        /// which it is, and nothing about whether the animation ran.
        /// </summary>
        private static List<TextQuad> Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            return new List<TextQuad>(label.DrawnQuads);
        }

        // -------------------------------------------------------------- parsing

        [Test]
        public void EffectTags_ParseIntoSpans()
        {
            var result = new RichTextResult();
            RichTextParser.Parse("calm <wave>moving</wave> calm", result);

            Assert.AreEqual("calm moving calm", result.Text);
            Assert.AreEqual(1, result.Effects.Count);
            Assert.AreEqual("wave", result.Effects[0].Name);
            Assert.AreEqual(5, result.Effects[0].Start);
            Assert.AreEqual(11, result.Effects[0].End);
        }

        [Test]
        public void EffectTags_DoNotSplitRuns()
        {
            // An effect changes no glyph and no advance, so making it split a
            // run would re-shape the text for something only the vertex pass
            // reads.
            var result = new RichTextResult();
            RichTextParser.Parse("a<shake>b</shake>c", result);
            foreach (var span in result.Spans)
                Assert.AreEqual(TextStyle.Default, span.Style,
                    "an effect tag leaked into the style and will split a run for nothing");
        }

        [Test]
        public void EffectParameters_ReadNamedAndBareForms()
        {
            var named = new RichTextResult();
            RichTextParser.Parse("<wave amp=2 freq=1.5 speed=3>x</wave>", named);
            Assert.AreEqual(1, named.Effects.Count);
            Assert.AreEqual(2f, named.Effects[0].Parameters.Amplitude, 0.001f);
            Assert.AreEqual(1.5f, named.Effects[0].Parameters.Frequency, 0.001f);
            Assert.AreEqual(3f, named.Effects[0].Parameters.Speed, 0.001f);

            // <shake=3> is what people actually type.
            var bare = new RichTextResult();
            RichTextParser.Parse("<shake=3>x</shake>", bare);
            Assert.AreEqual(3f, bare.Effects[0].Parameters.Amplitude, 0.001f);

            // Unset is NaN, not zero: amplitude 0 is a legitimate request for a
            // still effect, and defaulting it to 0 would make every plain
            // <wave> do nothing at all.
            Assert.IsTrue(float.IsNaN(bare.Effects[0].Parameters.Frequency));
        }

        [Test]
        public void UnknownEffectName_StaysLiteral()
        {
            Assert.AreEqual("<notaneffect>x</notaneffect>",
                ParseText("<notaneffect>x</notaneffect>"),
                "an unrecognised tag is text, like every other unrecognised tag");
        }

        [Test]
        public void RegisteredEffect_BecomesATag()
        {
            // The extension point: one call, and markup finds it.
            BuiltInEffects.Register("testonlyslide", p => new DelegateTextEffect(
                (input, parameters) =>
                {
                    var output = TextEffectOutput.Identity;
                    output.Translate = new Vector2(7f, 0f);
                    return output;
                }, p));

            var result = new RichTextResult();
            RichTextParser.Parse("<testonlyslide>x</testonlyslide>", result);
            Assert.AreEqual("x", result.Text);
            Assert.AreEqual(1, result.Effects.Count);
            Assert.IsNotNull(BuiltInEffects.Create("testonlyslide", TextEffectParameters.Default));
        }

        private static string ParseText(string source)
        {
            var result = new RichTextResult();
            RichTextParser.Parse(source, result);
            return result.Text;
        }

        // ------------------------------------------------------------- effects

        [Test]
        public void Wave_MovesOnlyTheClustersItCovers()
        {
            var label = NewLabel("aaaa<wave>bbbb</wave>");
            label.AnimationTime = 0f;
            var still = Draw(label);

            label.AnimationTime = 0.37f;
            var moved = Draw(label);

            Assert.AreEqual(still.Count, moved.Count);
            int movedCount = 0, stillCount = 0;
            for (int i = 0; i < still.Count; i++)
            {
                if (still[i].Position != moved[i].Position) movedCount++;
                else stillCount++;
            }
            Assert.Greater(movedCount, 0, "<wave> moved nothing");
            Assert.Greater(stillCount, 0, "<wave> moved text outside its own tag");
        }

        [Test]
        public void AdvancingTheClock_RebuildsThroughTheCanvas()
        {
            // Every other test here calls Rebuild directly, which is why all of
            // them can pass while the inspector's preview shows a frozen frame:
            // the preview reaches the mesh through the dirty flag and the
            // canvas update, and that plumbing is what this pins. Setting
            // AnimationTime must be enough — no SetAllDirty, no direct Rebuild
            // — for the canvas pass to re-emit moved quads, because that is
            // the whole per-tick sequence the editor's preview driver runs.
            var label = NewLabel("<wave>moving</wave>");
            Canvas.ForceUpdateCanvases();

            label.AnimationTime = 0.1f;
            Canvas.ForceUpdateCanvases();
            var before = new List<TextQuad>(label.DrawnQuads);

            label.AnimationTime = 0.37f;
            Canvas.ForceUpdateCanvases();
            var after = new List<TextQuad>(label.DrawnQuads);

            Assert.Greater(before.Count, 0, "nothing drawn through the canvas pass");
            Assert.AreEqual(before.Count, after.Count);
            int moved = 0;
            for (int i = 0; i < before.Count; i++)
                if (before[i].Position != after[i].Position) moved++;
            Assert.Greater(moved, 0,
                "the clock advanced but the canvas pass re-emitted the old frame");
        }

        [Test]
        public void Animation_IsAPureFunctionOfTime()
        {
            // Two labels showing the same text must animate identically, and
            // rewinding the clock must rewind the animation. An effect that
            // reaches for Random or accumulates state does neither, and the
            // symptom is text that twitches while the game is paused.
            var label = NewLabel("<shake>shaking</shake>");

            label.AnimationTime = 1.25f;
            var first = Draw(label);
            label.AnimationTime = 9f;
            Draw(label);
            label.AnimationTime = 1.25f;
            var again = Draw(label);

            for (int i = 0; i < first.Count; i++)
                Assert.AreEqual(first[i].Position, again[i].Position,
                    "the same time produced a different frame");
        }

        [Test]
        public void Animating_DoesNotRelayOutTheText()
        {
            // The promise the whole design rests on: text that is merely moving
            // costs vertex-write time, not rebuild time.
            var label = NewLabel("<wave>animated text here</wave>");
            Draw(label);
            var layout = label.LayoutResult;
            int runs = layout.Runs.Count, glyphs = layout.Glyphs.Count;
            float width = layout.Width;

            for (int frame = 0; frame < 20; frame++)
            {
                label.AnimationTime = frame * 0.016f;
                Draw(label);
                Assert.AreSame(layout, label.LayoutResult, "the layout was rebuilt");
                Assert.AreEqual(runs, layout.Runs.Count);
                Assert.AreEqual(glyphs, layout.Glyphs.Count);
                Assert.AreEqual(width, layout.Width, 0.0001f);
            }
        }

        [Test]
        public void Animating_RebuildsNeitherTheLayoutNorTheQuads()
        {
            // Counted, not inferred. The previous version of this test compared
            // LayoutResult before and after, but LayoutResult is the same
            // object forever and its contents are identical after re-laying out
            // unchanged text, so it passed while every frame re-ran everything.
            // A cache that never hits looks exactly like one that always does,
            // from the outside, unless somebody counts.
            var label = NewLabel("<wave>animated text here</wave>");
            Draw(label);

            int layouts = label.LayoutRuns;
            int builds = label.QuadBuilds;
            Assert.Greater(layouts, 0, "the label never laid anything out");
            Assert.Greater(builds, 0, "the label never built any quads");

            for (int frame = 0; frame < 20; frame++)
            {
                label.AnimationTime = frame * 0.016f;
                Draw(label);
            }

            Assert.AreEqual(layouts, label.LayoutRuns,
                "an animation frame re-laid out the text");
            Assert.AreEqual(builds, label.QuadBuilds,
                "an animation frame re-clustered and re-hashed every tile, which is the " +
                "rebuild time this design exists to avoid paying");

            // And changing the text still rebuilds both.
            label.Text = "<wave>something else</wave>";
            Draw(label);
            Assert.Greater(label.LayoutRuns, layouts, "changing the text must re-lay it out");
            Assert.Greater(label.QuadBuilds, builds, "changing the text must rebuild the quads");
        }

        [Test]
        public void Wobble_ActuallyMovesVertices()
        {
            // Rotation used to be carried as a displacement of the tile's
            // centre about itself, which is identically zero, so <wobble> was
            // a no-op that no test noticed. Vertices, not degrees.
            var label = NewLabel("<wobble>turning</wobble>");
            label.AnimationTime = 0f;
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            var still = ReadVertices(label);

            label.AnimationTime = 0.3f;
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            var turned = ReadVertices(label);

            Assert.AreEqual(still.Count, turned.Count);
            bool moved = false;
            for (int i = 0; i < still.Count; i++)
                if (still[i] != turned[i]) moved = true;
            Assert.IsTrue(moved, "<wobble> did not move a single vertex");

            // A rotation moves corners without moving the centre, so the tile
            // keeps its area: this is a turn, not a translation.
            foreach (var quad in label.DrawnQuads)
                Assert.AreNotEqual(0f, quad.Rotation, "the tile carries no rotation");
        }

        private static List<Vector3> ReadVertices(OneTextLabel label)
        {
            var mesh = label.canvasRenderer.GetMesh();
            var vertices = new List<Vector3>();
            if (mesh != null) mesh.GetVertices(vertices);
            return vertices;
        }

        [Test]
        public void Rainbow_TintsWithoutMoving()
        {
            var label = NewLabel("<rainbow>colours</rainbow>");
            label.AnimationTime = 0f;
            var quads = Draw(label);

            var colors = new HashSet<uint>();
            foreach (var quad in quads)
                colors.Add((uint)(quad.Color.r << 16 | quad.Color.g << 8 | quad.Color.b));
            Assert.Greater(colors.Count, 1, "<rainbow> gave every cluster the same colour");

            foreach (var quad in quads)
                Assert.AreEqual(255, quad.Color.a, "a hue sweep must not change alpha");
        }

        [Test]
        public void CombinedEffects_BothApply()
        {
            // <wave><rainbow> means both, not whichever the parser saw last.
            var label = NewLabel("<wave><rainbow>both</rainbow></wave>");
            label.AnimationTime = 0f;
            var still = Draw(label);
            label.AnimationTime = 0.4f;
            var moved = Draw(label);

            bool anyMoved = false, anyTinted = false;
            for (int i = 0; i < still.Count; i++)
            {
                if (still[i].Position != moved[i].Position) anyMoved = true;
                if (moved[i].Color.r != 255 || moved[i].Color.g != 255 || moved[i].Color.b != 255)
                    anyTinted = true;
            }
            Assert.IsTrue(anyMoved, "the movement effect was lost");
            Assert.IsTrue(anyTinted, "the colour effect was lost");
        }

        // ---------------------------------------------------- cluster addressing

        [Test]
        public void Effects_MoveJoinedArabicAsOneThing()
        {
            // The part no TMP-based animator can do. Joined letters bake as one
            // tile, so an effect addressed by cluster moves them together:
            // the word shakes as joined letters rather than coming apart at
            // the joins.
            var label = NewLabel("<shake>مرحبا</shake>", ArabicFontPath);
            label.AnimationTime = 0f;
            var still = Draw(label);
            label.AnimationTime = 0.21f;
            var moved = Draw(label);

            Assert.Greater(still.Count, 0);
            bool sawMergedTile = false;
            for (int i = 0; i < still.Count; i++)
            {
                if (still[i].LastGrapheme > still[i].FirstGrapheme) sawMergedTile = true;

                // Whatever the effect did, a tile is displaced as a unit: its
                // size is untouched and its corners keep their relationship.
                Assert.AreEqual(still[i].Size, moved[i].Size,
                    "a translation effect changed a tile's size");
            }
            Assert.IsTrue(sawMergedTile,
                "the test needs a tile covering several clusters, or it proves nothing about " +
                "joined text");
        }

        [Test]
        public void AppearanceEffects_KeyOffRevealNotWallTime()
        {
            // A fade-in on a typewriter has to mean "this character's turn just
            // came", not "the label has been alive for two seconds". Clusters
            // revealed later must still be mid-fade when earlier ones are done.
            // A one-second fade, sampled a tenth of a second after the later
            // clusters were revealed: long enough that they are visibly
            // mid-fade rather than fully transparent, because a tile at zero
            // alpha is dropped before it reaches the mesh and would not be
            // there to measure.
            var label = NewLabel("<fade speed=1>abcdefgh</fade>");
            label.MaxVisibleGraphemes = 0;
            // A clock that has started: at exactly zero nothing is advancing it,
            // and appearance effects are shown finished rather than freezing
            // the text invisible.
            label.AnimationTime = 0.001f;
            Draw(label);

            // Reveal two clusters now, then let time pass and reveal more.
            label.MaxVisibleGraphemes = 2;
            Draw(label);
            label.AnimationTime = 1f;
            label.MaxVisibleGraphemes = 4;
            Draw(label);

            label.AnimationTime = 1.1f;
            var quads = Draw(label);

            byte earlyAlpha = 0, lateAlpha = 255;
            bool sawLate = false;
            foreach (var quad in quads)
            {
                if (quad.FirstGrapheme < 2) earlyAlpha = (byte)Mathf.Max(earlyAlpha, quad.Color.a);
                else if (quad.FirstGrapheme < 4)
                {
                    lateAlpha = (byte)Mathf.Min(lateAlpha, quad.Color.a);
                    sawLate = true;
                }
            }

            Assert.IsTrue(sawLate, "the later clusters were not drawn at all");
            Assert.AreEqual(255, earlyAlpha,
                "a cluster revealed a second ago should have finished fading in");
            Assert.Less(lateAlpha, 255,
                "a cluster revealed a moment ago should still be fading in; the effect is keyed " +
                "off wall time rather than off the reveal");
        }

        [Test]
        public void Reveal_HidesTextRegardlessOfEffects()
        {
            var label = NewLabel("<wave>typewriter</wave>");
            label.AnimationTime = 0.5f;

            label.MaxVisibleGraphemes = 0;
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            var mesh = label.canvasRenderer.GetMesh();
            Assert.AreEqual(0, mesh == null ? 0 : mesh.vertexCount,
                "an animated label still has to respect the reveal");

            label.MaxVisibleGraphemes = -1;
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            Assert.Greater(label.canvasRenderer.GetMesh().vertexCount, 0);
        }

        [Test]
        public void AppearanceEffects_ShowFinished_WhenNothingIsAdvancingTheClock()
        {
            // A designer typing <fade> into a label in the Scene view must not
            // watch their text disappear. With no clock running there is no
            // "moment the character's turn came", so the honest answer is to
            // show the effect finished rather than never-started; the latter
            // is alpha zero, which is text that has vanished.
            var label = NewLabel("<fade>visible in the editor</fade>");
            label.AnimationTime = 0f;
            var quads = Draw(label);

            Assert.Greater(quads.Count, 0, "an unanimated label with <fade> drew nothing at all");
            foreach (var quad in quads)
                Assert.AreEqual(255, quad.Color.a,
                    "a frozen clock left the fade at zero alpha, so the text is invisible");
        }

        // ------------------------------------------------------------- the API

        [Test]
        public void EffectOutputs_Combine_Predictably()
        {
            var a = TextEffectOutput.Identity;
            a.Translate = new Vector2(1f, 2f);
            a.Scale = new Vector2(2f, 2f);
            a.RotationDegrees = 10f;
            a.Tint = new Color(1f, 0.5f, 1f, 1f);

            var b = TextEffectOutput.Identity;
            b.Translate = new Vector2(3f, -1f);
            b.Scale = new Vector2(0.5f, 3f);
            b.RotationDegrees = 5f;
            b.Tint = new Color(0.5f, 1f, 1f, 0.5f);

            var combined = a.Combine(b);
            Assert.AreEqual(new Vector2(4f, 1f), combined.Translate, "translations add");
            Assert.AreEqual(15f, combined.RotationDegrees, 0.001f, "rotations add");
            Assert.AreEqual(new Vector2(1f, 6f), combined.Scale, "scales multiply");
            Assert.AreEqual(0.5f, combined.Tint.r, 0.001f, "tints multiply");
            Assert.AreEqual(0.5f, combined.Tint.g, 0.001f);
            Assert.AreEqual(0.5f, combined.Tint.a, 0.001f);
        }

        [Test]
        public void Apply_ScalesAboutTheCentre()
        {
            // Any other pivot makes a scaling letter drift off its line.
            var quad = new TextQuad
            {
                Position = new Vector2(10f, 20f),
                Size = new Vector2(8f, 16f),
                Color = new Color32(255, 255, 255, 255),
            };
            var centre = quad.Center;

            var output = TextEffectOutput.Identity;
            output.Scale = new Vector2(2f, 2f);
            TextAnimator.Apply(ref quad, output);

            Assert.AreEqual(centre, quad.Center, "scaling moved the tile's centre");
            Assert.AreEqual(new Vector2(16f, 32f), quad.Size);
        }

        // ------------------------------------------------------------ duration

        [Test]
        public void EnvelopeWeight_FullDuringRun_ZeroAfter_EasesBetween()
        {
            Assert.AreEqual(1f, TextAnimator.EnvelopeWeight(float.NaN, 100f),
                "no duration means for ever");
            Assert.AreEqual(1f, TextAnimator.EnvelopeWeight(0f, 100f),
                "non-positive duration means for ever");
            Assert.AreEqual(1f, TextAnimator.EnvelopeWeight(0.5f, 0.1f),
                "full weight while running");
            Assert.AreEqual(0f, TextAnimator.EnvelopeWeight(0.5f, 0.5f),
                "zero at the deadline");
            Assert.AreEqual(0f, TextAnimator.EnvelopeWeight(0.5f, 9f),
                "zero after the deadline");
            float mid = TextAnimator.EnvelopeWeight(0.5f, 0.425f); // inside the fade window
            Assert.Greater(mid, 0f, "the settle is gradual, not a hard stop");
            Assert.Less(mid, 1f);
        }

        [Test]
        public void ForParameter_StopsTheEffect_AndTextSettlesHome()
        {
            var label = NewLabel("<wave amp=9 for=0.5>hi</wave>");
            var still = NewLabel("hi");

            label.AnimationTime = 0.2f;
            var during = Draw(label);
            label.AnimationTime = 1.0f;
            var after = Draw(label);
            var home = Draw(still);

            Assert.AreEqual(home.Count, after.Count);
            bool movedDuring = false;
            for (int i = 0; i < after.Count; i++)
            {
                movedDuring |= Mathf.Abs(during[i].Position.y - home[i].Position.y) > 0.01f;
                Assert.AreEqual(home[i].Position.y, after[i].Position.y, 0.001f,
                    "after its duration the effect must leave the text exactly where " +
                    "an unanimated label puts it");
            }
            Assert.IsTrue(movedDuring, "the wave never ran at all");
        }

        // ------------------------------------------------------- new effects

        [Test]
        public void Pop_OvershootsPastFullSize_ThenSettlesExactly()
        {
            var effect = BuiltInEffects.Create("pop",
                RichTextParser.ParseEffectParameters(""));
            bool overshot = false;
            for (float t = 0.01f; t < 0.25f; t += 0.01f)
            {
                var output = effect.Evaluate(new TextEffectInput(t, 0, 1, 1f, t, Vector2.zero));
                overshot |= output.Scale.x > 1.01f;
            }
            Assert.IsTrue(overshot, "pop without an overshoot is just swell");
            var settled = effect.Evaluate(new TextEffectInput(9f, 0, 1, 1f, 9f, Vector2.zero));
            Assert.AreEqual(1f, settled.Scale.x, 0.0001f, "must settle at exactly full size");
            Assert.AreEqual(1f, settled.Tint.a, 0.0001f);
        }

        [Test]
        public void Drop_LandsExactlyHome_AndStretchKeepsAntiphase()
        {
            var drop = BuiltInEffects.Create("drop",
                RichTextParser.ParseEffectParameters(""));
            var start = drop.Evaluate(new TextEffectInput(0f, 0, 1, 1f, 0f, Vector2.zero));
            Assert.Greater(start.Translate.y, 5f, "a drop starts above its line");
            var landed = drop.Evaluate(new TextEffectInput(9f, 0, 1, 1f, 9f, Vector2.zero));
            Assert.AreEqual(0f, landed.Translate.y, 0.0001f, "and ends exactly on it");

            var stretch = BuiltInEffects.Create("stretch",
                RichTextParser.ParseEffectParameters(""));
            var squashed = stretch.Evaluate(new TextEffectInput(0.26f, 0, 1, 1f, 0.26f, Vector2.zero));
            Assert.AreEqual(2f, squashed.Scale.x + squashed.Scale.y, 0.0001f,
                "x and y move in antiphase about 1");
            Assert.AreNotEqual(squashed.Scale.x, squashed.Scale.y, "it does actually stretch");
        }

        [Test]
        public void Glitch_IsDeterministic_AndMostlyStill()
        {
            var effect = BuiltInEffects.Create("glitch",
                RichTextParser.ParseEffectParameters(""));
            int torn = 0, samples = 200;
            for (int i = 0; i < samples; i++)
            {
                var input = new TextEffectInput(i * 0.037f, i % 7, 7, 1f, i * 0.037f, Vector2.zero);
                var a = effect.Evaluate(input);
                var b = effect.Evaluate(input);
                Assert.AreEqual(a.Translate, b.Translate,
                    "same input must tear the same way; a paused game must not twitch");
                if (Mathf.Abs(a.Translate.x) > 0.001f) torn++;
            }
            Assert.Greater(torn, 0, "it never glitched at all");
            Assert.Less(torn, samples / 2, "a glitch that is mostly on is just a shake");
        }

        [Test]
        public void ForParameter_DoesNotLimitOtherStackedEffects()
        {
            // <rainbow> tints for ever; the wave's for= must not silence it.
            var label = NewLabel("<rainbow><wave amp=9 for=0.3>hi</wave></rainbow>");
            label.AnimationTime = 5f;
            var drawn = Draw(label);
            bool tinted = false;
            foreach (var quad in drawn)
                tinted |= quad.Color.r != quad.Color.g || quad.Color.g != quad.Color.b;
            Assert.IsTrue(tinted, "the un-timed rainbow stopped with the timed wave");
        }
    }
}
