using NUnit.Framework;
using OneText;
using OneText.Editor;
using OneText.UGUI;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The outline, the shadow and the glow, which TextMesh Pro keeps on the
    /// material and OneText keeps on the label.
    ///
    /// A migration that reads only the component carries none of them, and on a
    /// real project that is most of what the text looks like: 69 of the 70 TMP
    /// materials in Five-Dice carry an effect, 67 of them an outline. Every
    /// label converted cleanly, reported nothing, and came out flat.
    ///
    /// The numbers cross unscaled because the two definitions agree — TMP's
    /// width, softness and outer reach are 0..1 and OneText quantises 0..1; the
    /// underlay offsets are -1..1 both sides. That is asserted here rather than
    /// commented, because "the units look the same" is how a silent factor of
    /// two gets in.
    /// </summary>
    public sealed class MaterialEffectTests
    {
        private const string Folder = "Assets/OneTextMaterialEffectTest";

        private GameObject _root;
        private Material _material;

        [SetUp]
        public void MakeFolder()
        {
            System.IO.Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void Clean()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
            if (_material != null) Object.DestroyImmediate(_material);
            _material = null;
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// A TextMesh Pro shader by name, or a skipped test.
        ///
        /// These shaders arrive with TMP's Essential Resources, which is an
        /// import into <c>Assets/</c> and not part of the package, so a bare CI
        /// project has the TMP types and none of its shaders. Without this
        /// guard the whole fixture fails there on <c>new Material(null)</c> and
        /// reads as a broken migration rather than a missing import — see the
        /// fifteen guards that stopped saying Inconclusive for the same reason.
        /// </summary>
        private static Shader Sdf(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null)
                Assert.Ignore($"{name} is not in this project. " +
                    "TMP's Essential Resources have not been imported, so there is no TMP " +
                    "material to read an effect off.");
            return shader;
        }

        /// <summary>
        /// A label whose material carries an outline and a shadow, wired the way
        /// TMP wires one: the material on <c>m_fontMaterial</c>, which is where
        /// the per-object override lives.
        /// </summary>
        private TextMeshProUGUI Build(out Material material)
        {
            _root = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            var text = _root.AddComponent<TextMeshProUGUI>();
            text.text = "outlined";

            material = _material = new Material(Sdf("TextMeshPro/Distance Field"));
            material.name = "Outlined SDF";
            // UNDERLAY_ON because this shader has one and draws nothing without
            // it. OUTLINE_ON is set here too and means nothing on this shader —
            // it declares no such keyword — which is the whole point of
            // OnAShaderWithNoOutlineKeyword_TheValueIsTheWholeStory below.
            material.EnableKeyword("OUTLINE_ON");
            material.EnableKeyword("UNDERLAY_ON");
            // The unit conversion switched off, so every test below reads the
            // mapping itself rather than the label's baked density: with no
            // gradient scale there is no TMP-side spread to convert from, and
            // the migration leaves the numbers in their own units.
            // AReachOfTMPs_IsConvertedToAReachOfOurs covers the conversion.
            material.SetFloat("_GradientScale", 0f);
            material.SetColor("_OutlineColor", new Color(1f, 0f, 0f, 1f));
            material.SetFloat("_OutlineWidth", 0.25f);
            material.SetColor("_UnderlayColor", new Color(0f, 0f, 1f, 0.75f));
            material.SetFloat("_UnderlayOffsetX", 0.32f);
            material.SetFloat("_UnderlayOffsetY", -0.4f);
            material.SetFloat("_UnderlaySoftness", 0.5f);

            var serialized = new SerializedObject(text);
            serialized.FindProperty("m_fontMaterial").objectReferenceValue = material;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return text;
        }

        [Test]
        public void TheOutlineAndTheShadow_CrossOntoTheLabel_Unscaled()
        {
            var text = Build(out _);
            var report = ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            Assert.Greater(report.Converted, 0, "nothing was converted");

            var label = _root.GetComponent<OneTextLabel>();
            Assert.IsNotNull(label, "the label was not converted");

            var decoration = (TextDecoration)new SerializedObject(label)
                .FindProperty("_decoration").boxedValue;

            Assert.IsTrue(decoration.HasOutline,
                "the outline did not come across at all, so the label draws flat and the report " +
                "said the conversion was clean");
            Assert.AreEqual(new Color32(255, 0, 0, 255), decoration.OutlineColor,
                "the outline colour did not survive — this is the branch that depends on how " +
                "Unity chose to serialize a Color32, and it is why this is measured");
            // The material has no dilate, and TMP still eats half the outline
            // into the face: the face lands at -0.125 and the outline's outer
            // edge, which is what OutlineWidth holds, at +0.125.
            Assert.AreEqual(-0.125f, decoration.FaceDilate, 1e-3f,
                "an outlined label's face is not thinned, so it draws through the half of the " +
                "outline TextMesh Pro puts inside it");
            Assert.AreEqual(0.125f, decoration.OutlineWidth, 1e-3f,
                "the outline's outer edge is not where TMP puts it; _ScaleRatioA here is the " +
                "shader's default of 1, so the slider is the whole of it before the face");

            Assert.IsTrue(decoration.HasShadow, "the shadow did not come across");
            Assert.AreEqual(new Color32(0, 0, 255, 191), decoration.ShadowColor);
            Assert.AreEqual(0.32f, decoration.ShadowOffset.x, 1e-3f);
            Assert.AreEqual(-0.4f, decoration.ShadowOffset.y, 1e-3f);
            Assert.AreEqual(0.5f, decoration.ShadowSoftness, 1e-3f);

            Assert.IsFalse(decoration.HasGlow,
                "a glow arrived that the material never asked for");
        }

        /// <summary>
        /// A material carrying every value and none of the keywords, on a shader
        /// that has the keywords — which is what most of a real project's
        /// materials are: the numbers came with whatever preset they were made
        /// from and the checkbox that turns the effect on was never ticked.
        ///
        /// Reading the values alone put an outline on some two thousand seven
        /// hundred labels in one project and a drop shadow on two thousand eight
        /// hundred, none of which TextMesh Pro had ever drawn. The face dilate
        /// is the one that must still come across, because that one TMP applies
        /// with no keyword at all — and it is the reason the text looked thin.
        /// </summary>
        [Test]
        public void ValuesWithoutTheirKeywords_DrawNothing_ExceptTheFace()
        {
            var text = Build(out var material);
            // The mobile shader, because it is the one that has an OUTLINE_ON to
            // leave switched off. See the desktop case below.
            material.shader = Sdf("TextMeshPro/Mobile/Distance Field");
            material.DisableKeyword("OUTLINE_ON");
            material.DisableKeyword("UNDERLAY_ON");
            material.SetFloat("_FaceDilate", 0.237f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            Assert.IsFalse(decoration.HasOutline,
                "an outline was drawn that TextMesh Pro was not drawing: the width is set on the " +
                "material but OUTLINE_ON is not, and this shader gates on the keyword");
            Assert.IsFalse(decoration.HasShadow,
                "a drop shadow was added that nothing asked for — _UnderlayColor defaults to an " +
                "alpha of 0.5, so reading the value alone finds a shadow on almost every material");

            Assert.IsTrue(decoration.HasFace,
                "the face dilate did not come across, and it is the one TMP applies without a " +
                "keyword — the whole visible difference between the two renderers");
            Assert.AreEqual(0.237f, decoration.FaceDilate, 1e-3f);
        }

        /// <summary>
        /// The same absent keyword on the shader most projects actually use.
        ///
        /// <c>OUTLINE_ON</c> is declared only by the Mobile SDF shaders.
        /// <c>TMP_SDF.shader</c> and its SSD, Overlay and Surface siblings have
        /// no such keyword and draw the outline from the value alone. A material
        /// answers false to <c>IsKeywordEnabled</c> for a keyword its shader
        /// never declared, so gating on it dropped the outline from every label
        /// on the desktop shader — 166 of them on one real project, converted
        /// with the face dilate carried and the outline silently gone, which is
        /// text that comes out fat and unedged.
        /// </summary>
        [Test]
        public void OnAShaderWithNoOutlineKeyword_TheValueIsTheWholeStory()
        {
            var text = Build(out var material);
            Assert.AreEqual("TextMeshPro/Distance Field", material.shader.name,
                "this test is about the desktop shader and is standing somewhere else");
            // Read out of the source, not out of the keyword space: the space
            // holds every global keyword the project declares anywhere,
            // including the Mobile shader's OUTLINE_ON, and so answers yes here.
            string source = System.IO.File.ReadAllText(
                AssetDatabase.GetAssetPath(material.shader));
            StringAssert.DoesNotContain("OUTLINE_ON", source,
                "this shader grew an OUTLINE_ON, so the case this test is about is gone");

            // Not enabled, because a real material for this shader never has it:
            // there is no checkbox writing it and nothing reading it.
            material.DisableKeyword("OUTLINE_ON");
            material.SetFloat("_OutlineWidth", 0.23f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            Assert.IsTrue(decoration.HasOutline,
                "the outline was dropped from a label TextMesh Pro draws one on");
            // No dilate on this material, so the ring straddles the glyph's own
            // edge: face at -0.115, outer edge at +0.115.
            Assert.AreEqual(0.23f, decoration.OutlineWidth - decoration.FaceDilate, 1e-3f,
                "the ring is not as thick as TMP's");
        }

        /// <summary>
        /// The two edges an outlined label has, put where TextMesh Pro puts
        /// them.
        ///
        /// TMP straddles the face's edge with the outline: `faceAlpha` is read
        /// at `d - outline*0.5` and `outlineAlpha` at `d + outline*0.5`, so half
        /// the outline is drawn inside the face and the dilate underneath it
        /// never shows. OneText's outline is wholly outside a face the width
        /// does not touch. Copied across unchanged, a real material's dilate of
        /// 0.225 under an outline of 0.207 drew a face 45% heavier in ink than
        /// the same material in TMP — the difference a screenshot of the two
        /// side by side was reported for.
        /// </summary>
        [Test]
        public void AnOutline_TakesHalfItsWidthOutOfTheFace()
        {
            var text = Build(out var material);
            material.SetFloat("_FaceDilate", 0.4f);
            material.SetFloat("_OutlineWidth", 0.2f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            Assert.AreEqual(0.4f - 0.1f, decoration.FaceDilate, 1e-3f,
                "the face kept the whole dilate, so it draws through the half of the outline TMP " +
                "puts on top of it and the text comes out heavier than it was");

            // The width is the outer edge, because that is what this shader
            // measures: both thresholds come off the undilated glyph edge, so a
            // width holding only the ring's thickness has the face eat into it.
            Assert.AreEqual(0.4f + 0.1f, decoration.OutlineWidth, 1e-3f,
                "the outline's width is the ring's thickness rather than its outer edge, so the " +
                "dilated face eats into it — at a dilate past the width it swallows it whole and " +
                "no outline is drawn at all");

            // Which is to say the ring itself is as thick as TMP's.
            Assert.AreEqual(0.2f, decoration.OutlineWidth - decoration.FaceDilate, 1e-3f);
        }

        /// <summary>
        /// One unit of TextMesh Pro's field converted into one of ours.
        ///
        /// Both renderers measure their effects in the width of their own
        /// distance field and both call it 1. TMP's is the atlas's gradient
        /// scale over the point size it was sampled at; OneText's is four
        /// texels at the density the label is baked at. Copied rather than
        /// converted, a real material's outline drew as a hairline where TMP
        /// had a two-pixel edge — the outline was 16% of TMP's ink in a render
        /// of the two side by side.
        /// </summary>
        [Test]
        public void AReachOfTMPs_IsConvertedToAReachOfOurs()
        {
            var text = Build(out var material);
            material.SetFloat("_GradientScale", 10f);
            material.SetFloat("_OutlineWidth", 0.2f);
            material.SetFloat("_FaceDilate", 0f);
            text.fontSize = 40f;

            float points = text.font.faceInfo.pointSize;
            Assert.Greater(points, 0f, "the fixture's font asset has no sampling point size, so " +
                                       "this test cannot say what a TMP reach is worth");

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            // 10 texels over the sampled point size, against four texels over
            // the density a 40-point label bakes at.
            float expected = 0.2f * (10f / points) / (4f / GlyphAtlas.QuantizePixelsPerEm(40f));
            Assert.AreEqual(expected, decoration.OutlineWidth - decoration.FaceDilate, 1e-3f,
                "the outline was copied in TMP's units instead of converted into ours");
        }

        /// <summary>
        /// The face on its own, which nothing is drawn over: no outline, no
        /// correction, the dilate as it is.
        /// </summary>
        [Test]
        public void WithNoOutline_TheFaceKeepsItsWholeDilate()
        {
            var text = Build(out var material);
            material.shader = Sdf("TextMeshPro/Mobile/Distance Field");
            material.DisableKeyword("OUTLINE_ON");
            material.SetFloat("_FaceDilate", 0.4f);
            material.SetFloat("_OutlineWidth", 0.2f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            Assert.IsFalse(decoration.HasOutline);
            Assert.AreEqual(0.4f, decoration.FaceDilate, 1e-3f,
                "the face was thinned for an outline that is not drawn");
        }

        /// <summary>
        /// TextMesh Pro's scale ratios, which are the unit its effect sliders
        /// are in.
        ///
        /// Nobody types them: the material inspector computes them from the font
        /// asset's gradient scale, padding and sampling point size, and the
        /// shader multiplies every effect value by one of them. So the same 0.25
        /// on two font assets is two different thicknesses, and a migration that
        /// copies the slider without the ratio copies a number and leaves its
        /// unit behind. A real project's font asset had 0.9.
        /// </summary>
        [Test]
        public void TheScaleRatios_AreTheUnitTheSlidersAreIn()
        {
            var text = Build(out var material);
            material.SetFloat("_ScaleRatioA", 0.9f);
            material.SetFloat("_ScaleRatioC", 0.5f);
            material.SetFloat("_OutlineWidth", 0.2f);
            material.SetFloat("_OutlineSoftness", 0.4f);
            material.SetFloat("_FaceDilate", 0.25f);
            material.SetFloat("_WeightNormal", 0.4f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var decoration = (TextDecoration)new SerializedObject(
                _root.GetComponent<OneTextLabel>()).FindProperty("_decoration").boxedValue;

            // Face plus thickness, both scaled: the outer edge.
            Assert.AreEqual((0.25f + 0.4f / 4f) * 0.9f + (0.2f * 0.9f) / 2f,
                decoration.OutlineWidth, 1e-3f,
                "the outline width ignored _ScaleRatioA, or holds the ring's thickness rather " +
                "than its outer edge");
            Assert.AreEqual(0.4f * 0.9f, decoration.OutlineSoftness, 1e-3f,
                "the outline softness ignored _ScaleRatioA");

            // weight = lerp(_WeightNormal, _WeightBold, bold) / 4.0;
            // weight = (weight + _FaceDilate) * _ScaleRatioA * 0.5;
            // ...and then half the outline off it, because TMP's outline eats
            // half its width into the face and OneText's does not.
            Assert.AreEqual((0.25f + 0.4f / 4f) * 0.9f - (0.2f * 0.9f) / 2f,
                decoration.FaceDilate, 1e-3f,
                "the face dilate ignored _ScaleRatioA, the material's normal weight, or the half " +
                "of the outline that TextMesh Pro draws inside the face's edge");

            Assert.AreEqual(0.32f * 0.5f, decoration.ShadowOffset.x, 1e-3f,
                "the underlay offset ignored _ScaleRatioC");
            Assert.AreEqual(0.5f * 0.5f, decoration.ShadowSoftness, 1e-3f,
                "the underlay softness ignored _ScaleRatioC");
        }

        [Test]
        public void AMaterialWithNoEffects_LeavesTheLabelUndecorated()
        {
            _root = new GameObject("Plain", typeof(RectTransform), typeof(CanvasRenderer));
            var text = _root.AddComponent<TextMeshProUGUI>();
            text.text = "plain";

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var label = _root.GetComponent<OneTextLabel>();
            var decoration = (TextDecoration)new SerializedObject(label)
                .FindProperty("_decoration").boxedValue;

            Assert.IsTrue(decoration.IsNone,
                "a label with nothing on its material came out decorated, which would put an " +
                "outline on every plain label in a project");
        }

        /// <summary>
        /// Face dilate and outline softness, which the label had no field for
        /// when this was first written and was told to report as lost. The
        /// channels found room for them, so the assertion is now that they
        /// arrive — and that nothing still calls them missing, because a report
        /// that apologises for something it did is its own kind of wrong.
        /// </summary>
        [Test]
        public void TheFaceDilateAndTheOutlineSoftness_ArriveRatherThanBeingApologisedFor()
        {
            var text = Build(out var material);
            material.SetFloat("_FaceDilate", 0.3f);
            material.SetFloat("_OutlineSoftness", 0.4f);

            ComponentMigration.ConvertInPlace(new[] { _root }, "(test)", false);
            var label = _root.GetComponent<OneTextLabel>();
            var decoration = (TextDecoration)new SerializedObject(label)
                .FindProperty("_decoration").boxedValue;

            Assert.IsTrue(decoration.HasFace, "the face dilate did not come across");
            // Less half the outline this fixture also carries — see
            // AnOutline_TakesHalfItsWidthOutOfTheFace.
            Assert.AreEqual(0.3f - 0.25f / 2f, decoration.FaceDilate, 1e-3f);
            Assert.AreEqual(0.4f, decoration.OutlineSoftness, 1e-3f,
                "the outline came across with a hard edge");

            var report = ComponentMigration.ScanInPlace(new[] { _root }, "(test)");
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "material-effect") continue;
                StringAssert.DoesNotContain("face dilate", finding.Message);
                StringAssert.DoesNotContain("outline softness", finding.Message);
            }
        }

        [Test]
        public void WhatStillCannotCrossOver_IsNamedRatherThanDropped()
        {
            var text = Build(out var material);
            material.SetFloat("_UnderlayDilate", 0.17f);

            var report = ComponentMigration.ScanInPlace(new[] { _root }, "(test)");

            bool named = false;
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "material-effect") continue;
                StringAssert.Contains("underlay dilate", finding.Message);
                named = true;
            }
            Assert.IsTrue(named,
                "the shadow comes across at the wrong weight and the report says nothing");
        }

        [Test]
        public void TheScan_DoesNotInstantiateAMaterial()
        {
            var text = Build(out var material);
            int before = material.GetInstanceID();

            ComponentMigration.ScanInPlace(new[] { _root }, "(test)");

            // Reading TMP_Text.fontMaterial instead of the serialized field would
            // have made a copy here, and a scan that promises to write nothing
            // would be leaving a material asset behind for every label.
            var after = new SerializedObject(text).FindProperty("m_fontMaterial")
                .objectReferenceValue;
            Assert.AreEqual(before, after.GetInstanceID(),
                "the scan replaced the label's material with an instance of it");
        }
    }
}
