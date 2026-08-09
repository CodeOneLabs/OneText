using NUnit.Framework;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// What a new label or world text is created as, and where those numbers
    /// come from.
    ///
    /// The point of the settings asset is that they are the project's numbers
    /// rather than the package's, so the two things worth asserting are that a
    /// project which has said nothing gets the documented answer, and that a
    /// project which has said something gets what it said.
    /// </summary>
    public class ProjectDefaultsTests
    {
        private OneTextSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<OneTextSettings>();

        [TearDown]
        public void TearDown()
        {
            // Invalidate rather than restoring what was cached: the project's
            // real asset, if it has one, is found again on the next question.
            OneTextSettings.Invalidate();
            if (_settings != null) Object.DestroyImmediate(_settings);
            _settings = null;
        }

        private static OneTextLabel NewLabel()
        {
            var go = new GameObject("DefaultsLabel", typeof(RectTransform));
            return go.AddComponent<OneTextLabel>();
        }

        private static OneTextMesh NewWorldText()
        {
            var go = new GameObject("DefaultsMesh", typeof(RectTransform));
            return go.AddComponent<OneTextMesh>();
        }

        [Test]
        public void A_Fresh_Settings_Asset_Agrees_With_The_Built_In_Answer()
        {
            // Creating the asset must not change a single default, or the act
            // of opening project settings would silently restyle a project.
            var asset = _settings.Defaults;
            var builtIn = OneTextSettings.TextDefaults.Default;

            Assert.AreEqual(builtIn.FontSize, asset.FontSize);
            Assert.AreEqual(builtIn.AutoSizeMin, asset.AutoSizeMin);
            Assert.AreEqual(builtIn.AutoSizeMax, asset.AutoSizeMax);
            Assert.AreEqual(builtIn.RaycastTarget, asset.RaycastTarget);
            Assert.AreEqual(builtIn.RichText, asset.RichText);
            Assert.AreEqual(builtIn.ParseEscapes, asset.ParseEscapes);
            Assert.AreEqual(builtIn.Wrap, asset.Wrap);
            Assert.AreEqual(builtIn.CanvasSize, asset.CanvasSize);
            Assert.AreEqual(builtIn.WorldSize, asset.WorldSize);
        }

        [Test]
        public void With_No_Settings_Asset_The_Package_Answers()
        {
            OneTextSettings.Instance = null;

            var defaults = OneTextSettings.ProjectDefaults;

            // TextMesh Pro's numbers, so a project arriving from it recognises
            // what it gets.
            Assert.AreEqual(36f, defaults.FontSize);
            Assert.IsTrue(defaults.RaycastTarget);
            Assert.IsTrue(defaults.RichText);
            Assert.AreEqual(TextWrap.Wrap, defaults.Wrap);
            Assert.AreEqual(new Vector2(20f, 5f), defaults.WorldSize);
        }

        [Test]
        public void A_Label_Created_In_Code_Starts_At_The_Documented_Size()
        {
            var label = NewLabel();

            Assert.AreEqual(36f, label.FontSize, "the field default drifted from the settings one");
            Assert.AreEqual("New Text", label.Text);
            Assert.IsTrue(label.raycastTarget);

            Object.DestroyImmediate(label.gameObject);
        }

        [Test]
        public void A_Label_Takes_The_Projects_Answers()
        {
            var serialized = new SerializedObject(_settings);
            serialized.FindProperty("_defaultFontSize").floatValue = 24f;
            serialized.FindProperty("_defaultAutoSizeMin").floatValue = 6f;
            serialized.FindProperty("_defaultAutoSizeMax").floatValue = 48f;
            serialized.FindProperty("_defaultRaycastTarget").boolValue = false;
            serialized.FindProperty("_defaultRichText").boolValue = false;
            serialized.FindProperty("_defaultParseEscapes").boolValue = false;
            serialized.FindProperty("_defaultWrap").enumValueIndex = (int)TextWrap.NoWrap;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            OneTextSettings.Instance = _settings;

            var label = NewLabel();
            label.ApplyProjectDefaults();

            Assert.AreEqual(24f, label.FontSize);
            Assert.AreEqual(6f, label.AutoSizeMin);
            Assert.AreEqual(48f, label.AutoSizeMax);
            Assert.IsFalse(label.raycastTarget, "a project that said no clicks got clicks");
            Assert.IsFalse(label.RichText);
            Assert.IsFalse(label.ParseEscapes);
            Assert.AreEqual(TextWrap.NoWrap, label.Wrap);

            Object.DestroyImmediate(label.gameObject);
        }

        [Test]
        public void World_Text_Takes_The_Same_Answers()
        {
            var serialized = new SerializedObject(_settings);
            serialized.FindProperty("_defaultFontSize").floatValue = 12f;
            serialized.FindProperty("_defaultAutoSizeMin").floatValue = 4f;
            serialized.FindProperty("_defaultAutoSizeMax").floatValue = 20f;
            serialized.FindProperty("_defaultRichText").boolValue = false;
            serialized.FindProperty("_defaultWrap").enumValueIndex = (int)TextWrap.NoWrap;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            OneTextSettings.Instance = _settings;

            var mesh = NewWorldText();
            mesh.ApplyProjectDefaults();

            Assert.AreEqual(12f, mesh.FontSize);
            Assert.AreEqual(4f, mesh.AutoSizeMin);
            Assert.AreEqual(20f, mesh.AutoSizeMax);
            Assert.IsFalse(mesh.RichText);
            Assert.AreEqual(TextWrap.NoWrap, mesh.Wrap);

            Object.DestroyImmediate(mesh.gameObject);
        }

        [Test]
        public void Container_Sizes_Are_The_Projects_To_Change()
        {
            var serialized = new SerializedObject(_settings);
            serialized.FindProperty("_defaultCanvasSize").vector2Value = new Vector2(640f, 120f);
            serialized.FindProperty("_defaultWorldSize").vector2Value = new Vector2(8f, 2f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            OneTextSettings.Instance = _settings;

            Assert.AreEqual(new Vector2(640f, 120f), OneTextSettings.ProjectDefaults.CanvasSize);
            Assert.AreEqual(new Vector2(8f, 2f), OneTextSettings.ProjectDefaults.WorldSize);
        }
    }
}
