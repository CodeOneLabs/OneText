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
        /// A label whose material carries an outline and a shadow, wired the way
        /// TMP wires one: the material on <c>m_fontMaterial</c>, which is where
        /// the per-object override lives.
        /// </summary>
        private TextMeshProUGUI Build(out Material material)
        {
            _root = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            var text = _root.AddComponent<TextMeshProUGUI>();
            text.text = "outlined";

            material = _material = new Material(Shader.Find("TextMeshPro/Distance Field"));
            material.name = "Outlined SDF";
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
            Assert.AreEqual(0.25f, decoration.OutlineWidth, 1e-3f,
                "the outline width came across scaled; TMP's _OutlineWidth and OneText's " +
                "OutlineWidth are both 0..1 and nothing should be applied between them");

            Assert.IsTrue(decoration.HasShadow, "the shadow did not come across");
            Assert.AreEqual(new Color32(0, 0, 255, 191), decoration.ShadowColor);
            Assert.AreEqual(0.32f, decoration.ShadowOffset.x, 1e-3f);
            Assert.AreEqual(-0.4f, decoration.ShadowOffset.y, 1e-3f);
            Assert.AreEqual(0.5f, decoration.ShadowSoftness, 1e-3f);

            Assert.IsFalse(decoration.HasGlow,
                "a glow arrived that the material never asked for");
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
            Assert.AreEqual(0.3f, decoration.FaceDilate, 1e-3f);
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
