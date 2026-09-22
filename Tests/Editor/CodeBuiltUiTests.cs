using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// A UI built from code rather than from prefabs.
    ///
    /// Reported from a project that constructs every screen at runtime: the
    /// label had no renderer to draw into, the field's labels and caret
    /// colour could only be reached by reflection into private fields, and
    /// the project's default font could only be set through a
    /// SerializedObject. Each of those is a public surface now, and these
    /// assert that the surface does what the private field did — including
    /// the part reflection could never do, which is telling a label that is
    /// already on screen that the settings changed under it.
    /// </summary>
    public sealed class CodeBuiltUiTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private GameObject _canvas;

        [SetUp]
        public void MakeCanvas()
        {
            _canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        }

        [TearDown]
        public void DropCanvas()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
        }

        private OneTextLabel Label(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(_canvas.transform, false);
            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(300f, 40f);
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            return label;
        }

        private static OneFontAsset Asset(string path)
        {
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.Initialize(File.ReadAllBytes(Path.GetFullPath(path)), "Test Family", path);
            return asset;
        }

        // ---------------------------------------------------------- renderer

        [Test]
        public void ALabelAddedFromCode_BringsItsOwnCanvasRenderer()
        {
            var go = new GameObject("Bare", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);

            go.AddComponent<OneTextLabel>();

            Assert.IsNotNull(go.GetComponent<CanvasRenderer>(),
                "a label must require its renderer the way Image does, or a label added " +
                "from code has nothing to draw into and a prefab baked from it saves none");
        }

        // -------------------------------------------------------------- field

        [Test]
        public void AFieldBuiltFromCode_TakesItsLabelsAndCaretThroughProperties()
        {
            var root = new GameObject("Field", typeof(RectTransform), typeof(OneTextInputField));
            root.transform.SetParent(_canvas.transform, false);
            var field = root.GetComponent<OneTextInputField>();
            var value = Label("Value");
            var placeholder = Label("Placeholder");

            field.textComponent = value;
            field.placeholder = placeholder;
            field.caretColor = Color.red;
            field.caretWidth = 3f;
            field.caretBlinkRate = 0f;
            field.text = "hi";
            field.UpdateVisuals();

            Assert.AreEqual("hi", value.Text, "the value label was not wired");
            Assert.IsFalse(placeholder.enabled, "a placeholder shows only while the value is empty");
            var caret = value.GetComponentInChildren<OneTextCaret>(true);
            Assert.IsNotNull(caret, "the caret is built under the value label");
            Assert.AreEqual((Color)Color.red, caret.color, "the caret colour did not reach the caret");
            Assert.AreEqual(Color.red, field.caretColor);
            Assert.AreEqual(3f, field.caretWidth);
            Assert.AreEqual(0f, field.caretBlinkRate);
        }

        [Test]
        public void ReplacingTheFieldsLabels_MovesTheCaretAndFreesTheOldPlaceholder()
        {
            var root = new GameObject("Field", typeof(RectTransform), typeof(OneTextInputField));
            root.transform.SetParent(_canvas.transform, false);
            var field = root.GetComponent<OneTextInputField>();
            var first = Label("First");
            var second = Label("Second");
            var firstPlaceholder = Label("First placeholder");
            var secondPlaceholder = Label("Second placeholder");

            field.textComponent = first;
            field.placeholder = firstPlaceholder;
            field.text = "hi";
            field.UpdateVisuals();
            Assert.IsNotNull(first.GetComponentInChildren<OneTextCaret>(true));
            Assert.IsFalse(firstPlaceholder.enabled);

            field.textComponent = second;
            field.placeholder = secondPlaceholder;
            field.UpdateVisuals();

            Assert.IsNull(first.GetComponentInChildren<OneTextCaret>(true),
                "the caret under the old label must go with the old label");
            Assert.IsNotNull(second.GetComponentInChildren<OneTextCaret>(true),
                "a caret is built under the new label");
            Assert.AreEqual("hi", second.Text, "the value follows the field to its new label");
            Assert.IsTrue(firstPlaceholder.enabled,
                "a placeholder the field no longer owns is handed back visible");
            Assert.IsFalse(secondPlaceholder.enabled);
        }

        // ----------------------------------------------------------- settings

        [Test]
        public void ChangingTheProjectFonts_ReachesALabelAlreadyOnScreen()
        {
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            var latin = Asset(LatinFontPath);
            var arabic = Asset(ArabicFontPath);
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(_canvas.transform, false);
            try
            {
                OneTextSettings.Instance = settings;
                settings.DefaultFont = latin;

                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(300f, 40f);
                label.Text = "abc";
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);
                Assert.AreSame(latin.Font, label.ResolvedFonts.Primary,
                    "a label with no font of its own draws in the project default");

                settings.DefaultFont = arabic;
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);
                Assert.AreSame(arabic.Font, label.ResolvedFonts.Primary,
                    "a default font set from code must reach a label that is already built");

                settings.SetFallbackFonts(new[] { latin, null });
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);
                Assert.AreEqual(1, settings.FallbackFonts.Count, "null entries are dropped");
                CollectionAssert.Contains(label.ResolvedFonts.Fonts, latin.Font,
                    "a fallback set from code must reach the live label's stack");
            }
            finally
            {
                Object.DestroyImmediate(go);
                OneTextSettings.Invalidate();
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(latin);
                Object.DestroyImmediate(arabic);
            }
        }
    }
}
