using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// The quality rung: how many atlas texels a piece of text asks for, as a
    /// multiple of what its size implies.
    ///
    /// It existed on <c>OneTextMesh</c> and not on a canvas label, on the
    /// reasoning — written down in the 0.2.0 changelog — that a label's font
    /// size is already in screen pixels. It is not: a CanvasScaler set to
    /// Scale With Screen Size puts a factor between the two, and a label baked
    /// for its font size is magnified by it. So the rung is on both now, with a
    /// gentler ladder on the canvas side (1, 1.5, 2 against the world's 1, 2,
    /// 4) because a scale factor has a ceiling and a camera does not.
    ///
    /// The measured screen density is the automatic answer to the same problem
    /// and is tested in <c>DynamicPpemTests</c>; it is switched off throughout
    /// this file, because what is under test here is the rung on its own.
    /// </summary>
    public class TextQualityTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private readonly List<Object> _created = new List<Object>();
        private OneTextSettings _settings;
        private bool _settingsSwapped;

        [SetUp]
        public void QuietTheMeasurement()
        {
            OneTextLabel.DynamicPpem = false;
        }

        [TearDown]
        public void Cleanup()
        {
            OneTextLabel.DynamicPpem = true;
            if (_settingsSwapped)
            {
                OneTextSettings.Instance = _settings;
                _settingsSwapped = false;
            }
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        /// <summary>
        /// Puts a settings asset in front of whatever the project has, so a
        /// test can state what the project says instead of reading it.
        /// </summary>
        private OneTextSettings WithProjectQuality(TextQuality quality)
        {
            if (!_settingsSwapped)
            {
                _settings = OneTextSettings.Instance;
                _settingsSwapped = true;
            }

            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("_defaultQuality").intValue = (int)quality;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            OneTextSettings.Instance = settings;
            return settings;
        }

        // ------------------------------------------------------------ ladders

        [Test]
        public void The_World_Ladder_Is_The_Member_Value_Itself()
        {
            // The values are the arithmetic on the world side, and renumbering
            // one is a silent change to every world text in every project.
            Assert.AreEqual(1, (int)TextQuality.Performance);
            Assert.AreEqual(2, (int)TextQuality.Medium);
            Assert.AreEqual(4, (int)TextQuality.High);

            Assert.AreEqual(1f, TextQualityScale.ForWorld(TextQuality.Performance));
            Assert.AreEqual(2f, TextQualityScale.ForWorld(TextQuality.Medium));
            Assert.AreEqual(4f, TextQualityScale.ForWorld(TextQuality.High));
        }

        [Test]
        public void The_Canvas_Ladder_Is_Half_The_World_Above_Performance()
        {
            // Two is the top on a canvas because two covers the scale factors
            // that exist. Four would be sixteen times the atlas area for texels
            // no display asks for.
            Assert.AreEqual(1f, TextQualityScale.ForCanvas(TextQuality.Performance));
            Assert.AreEqual(1.5f, TextQualityScale.ForCanvas(TextQuality.Medium));
            Assert.AreEqual(2f, TextQualityScale.ForCanvas(TextQuality.High));
        }

        [Test]
        public void Performance_Is_Exactly_What_There_Was_Before()
        {
            // The default has to be arithmetically inert, not merely small: a
            // multiplier of 1.0 applied to a run size must produce the same
            // float, or every golden image in the repository moves for a
            // feature nobody turned on.
            foreach (float size in new[] { 12f, 24f, 36f, 55f, 108f })
            {
                Assert.AreEqual(size, size * TextQualityScale.ForCanvas(TextQuality.Performance),
                    "the shipped rung must not change a single bake");
            }
        }

        // ------------------------------------------------------- the project

        [Test]
        public void The_Shipped_Project_Answer_Is_Performance()
        {
            // Both halves: a fresh settings asset says Performance, and a
            // project with no settings asset at all gives the same answer — so
            // creating one changes nothing until somebody edits it.
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            Assert.AreEqual(TextQuality.Performance, settings.DefaultQuality);

            _settings = OneTextSettings.Instance;
            _settingsSwapped = true;
            OneTextSettings.Instance = null;
            Assert.AreEqual(TextQuality.Performance, TextQualityScale.Project);
        }

        [Test]
        public void Project_Takes_Whichever_Rung_The_Project_Set()
        {
            WithProjectQuality(TextQuality.High);

            Assert.AreEqual(TextQuality.High, TextQualityScale.Resolve(TextQuality.Project));
            Assert.AreEqual(2f, TextQualityScale.ForCanvas(TextQuality.Project));
            Assert.AreEqual(4f, TextQualityScale.ForWorld(TextQuality.Project));
        }

        [Test]
        public void A_Rung_Named_On_The_Component_Beats_The_Project()
        {
            WithProjectQuality(TextQuality.High);

            Assert.AreEqual(1f, TextQualityScale.ForCanvas(TextQuality.Performance),
                "a component that names a rung is not asking the project");
        }

        [Test]
        public void A_Project_That_Says_Project_Does_Not_Loop()
        {
            // Unreachable through the inspector, reachable through a
            // hand-edited YAML or an asset older than the field.
            var settings = WithProjectQuality(TextQuality.Project);

            Assert.AreEqual(TextQuality.Performance, settings.DefaultQuality);
            Assert.AreEqual(TextQuality.Performance, TextQualityScale.Resolve(TextQuality.Project));
        }

        // -------------------------------------------------------- the label

        [Test]
        public void A_New_Label_Asks_The_Project()
        {
            // Zero is the serialized value every label already in a project
            // reads back for a field that did not exist when it was saved, so
            // "ask the project" is the only default that is right for all of
            // them at once — and it is what makes the project setting usable
            // after a migration rather than only before one.
            var label = NewLabel("W");
            Assert.AreEqual(TextQuality.Project, label.Quality);
        }

        [Test]
        public void Raising_Quality_Bakes_A_Denser_Tile()
        {
            // The claim in atlas texels, which is the only place the setting
            // can be observed: geometry is in font units and does not move.
            // Measured off each label's own uv rect rather than the shared
            // atlas's totals — the atlas outlives a test and may already hold
            // these glyphs, which would make a before-and-after difference
            // report zero and prove nothing.
            float low = TileTexels(TextQuality.Performance);
            float high = TileTexels(TextQuality.High);

            Assert.Greater(low, 0f, "the Performance bake produced no tile");
            Assert.Greater(high, low * 1.5f,
                $"High baked a tile {high:F0} texels wide against Performance's {low:F0}: " +
                "twice the density has to be about twice the texels across, so the " +
                "multiplier is not reaching the atlas");
        }

        [Test]
        public void Raising_Quality_Leaves_The_Text_The_Same_Size()
        {
            // The failure this guards against is the easy one to write: reuse
            // the multiplied size for the layout scale as well and the label
            // silently draws twice as large. A denser field is the same letters
            // off more texels.
            var performance = NewLabel("Wave");
            performance.Quality = TextQuality.Performance;
            Draw(performance);
            float plainWidth = performance.preferredWidth;
            Vector2 plainQuad = performance.DrawnQuads[0].Size;
            Vector2 plainAt = performance.DrawnQuads[0].Position;

            var high = NewLabel("Wave");
            high.Quality = TextQuality.High;
            Draw(high);

            Assert.AreEqual(plainWidth, high.preferredWidth, 1e-3f,
                "the quality rung moved the layout");

            // The quad is the padded field rather than the ink, and the padding
            // ring is a fixed four texels (GlyphRasterizer.Padding), so at twice
            // the density it is half as many font units and the quad comes out
            // slightly SMALLER: 40.5 against 45 for this string at forty points.
            // The number to pin is therefore a band, not an equality — and the
            // failure it guards against is unmistakable at this width, because
            // reusing the multiplied size for the layout scale as well would
            // put the quad at about twice the size rather than nine tenths.
            Vector2 dense = high.DrawnQuads[0].Size;
            Assert.Less(dense.x, plainQuad.x * 1.05f,
                "the quality rung scaled the text instead of the tile");
            Assert.Greater(dense.x, plainQuad.x * 0.8f,
                "the quad shrank by more than the padding ring can account for");
            Assert.Less(dense.y, plainQuad.y * 1.05f);

            // And the ink inside it has not moved. The quad's corner does move
            // — by exactly one padding ring, 2 units here — because a thinner
            // ring starts closer to the letter, so the corner is the wrong
            // thing to pin and the centre is the right one. It matches to a
            // quarter of a unit, which is the ink box rounding to whole texels
            // at two different bucket sizes.
            Assert.AreEqual(plainAt.x + plainQuad.x * 0.5f,
                high.DrawnQuads[0].Position.x + dense.x * 0.5f, 0.5f,
                "the quality rung moved the ink off its pen");
        }

        // ----------------------------------------------------------- helpers

        private float TileTexels(TextQuality quality)
        {
            var label = NewLabel("W");
            label.Quality = quality;
            Draw(label);

            float widest = 0f;
            foreach (var quad in label.DrawnQuads)
                widest = Mathf.Max(widest, quad.UvRect.width);
            return widest * SharedGlyphAtlas.Atlas.Settings.TextureSize;
        }

        private OneTextLabel NewLabel(string text, float size = 40f)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvasGo.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(900f, 200f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            // Forty points so neither end of the ladder lands on the bucket
            // ladder's floor or its ceiling: Performance asks for 40 and High
            // for 80, which are different buckets with room either side.
            label.FontSize = size;
            label.Wrap = TextWrap.NoWrap;
            label.Text = text;
            return label;
        }

        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }
    }
}
