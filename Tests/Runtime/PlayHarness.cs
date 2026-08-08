using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace OneText.Tests.Play
{
    /// <summary>
    /// The scene a PlayMode test runs in, and the bookkeeping that keeps one
    /// test from being the reason the next one fails.
    ///
    /// Everything here is built from code rather than loaded from a scene
    /// asset, because a scene asset is a fifth place the same canvas would be
    /// configured and the first place a difference would hide.
    /// </summary>
    public sealed class PlayHarness
    {
        public const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        public const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        public const string VariableFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        /// <summary>
        /// Fetched by <c>Tools/fetch_coverage_fonts.py</c> rather than
        /// committed, so anything wanting it has to ask
        /// <see cref="HasFont"/> first and have a plan for "no".
        /// </summary>
        public const string JapaneseFont =
            "Packages/com.onetext.core/Tests/CoverageFonts~/NotoSansCJKjp-Regular.otf";

        private static readonly Dictionary<string, byte[]> FontCache =
            new Dictionary<string, byte[]>();

        public static bool HasFont(string packagePath) =>
            File.Exists(Path.GetFullPath(packagePath));

        private readonly List<Object> _created = new List<Object>();

        public Canvas Canvas { get; private set; }
        public Camera Camera { get; private set; }

        /// <summary>Font bytes, read once per play session.</summary>
        public static byte[] Font(string packagePath)
        {
            if (FontCache.TryGetValue(packagePath, out var cached)) return cached;
            var bytes = File.ReadAllBytes(Path.GetFullPath(packagePath));
            FontCache[packagePath] = bytes;
            return bytes;
        }

        public void Setup()
        {
            var camGo = new GameObject("TestCamera");
            _created.Add(camGo);
            Camera = camGo.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;
            Camera.orthographic = true;

            var canvasGo = new GameObject("TestCanvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvasGo);
            Canvas = canvasGo.GetComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            Canvas.worldCamera = Camera;
            Canvas.planeDistance = 5f;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(800f, 600f);
        }

        /// <summary>
        /// Destroys immediately rather than at the end of the frame. A
        /// deferred Destroy in a teardown outlives the test that asked for it:
        /// the next test's SetUp runs first, and it starts in a scene that
        /// still has the previous one's canvas, event system and labels in it.
        /// </summary>
        public void Teardown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        /// <summary>Registers an object for destruction at teardown.</summary>
        public T Track<T>(T value) where T : Object
        {
            _created.Add(value);
            return value;
        }

        public OneTextLabel Label(string text, float size = 32f,
            string fontPath = LatinFont, Vector2? boxSize = null)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            _created.Add(go);
            go.transform.SetParent(Canvas.transform, false);

            var label = go.AddComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = boxSize ?? new Vector2(600f, 200f);
            label.SetFont(Font(fontPath));
            label.FontSize = size;
            label.Wrap = TextWrap.Wrap;
            label.Text = text;
            return label;
        }

        /// <summary>
        /// An input field wired to its own label. The GameObject is built
        /// inactive and enabled last so the field never runs a frame without
        /// the label it draws through, which is a state no scene can produce
        /// and every reflection-built one can.
        /// </summary>
        public OneTextInputField InputField(string initialText, out OneTextLabel label)
        {
            var root = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer));
            _created.Add(root);
            root.SetActive(false);
            root.transform.SetParent(Canvas.transform, false);
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(600f, 60f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(root.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            label = textGo.AddComponent<OneTextLabel>();
            label.SetFont(Font(LatinFont));
            label.FontSize = 24f;
            label.Wrap = TextWrap.NoWrap;

            var field = root.AddComponent<OneTextInputField>();
            typeof(OneTextInputField)
                .GetField("_textComponent", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(field, label);
            // No platform input method: an OS IME cannot be driven from a test
            // and would put a real composition window in front of a batch run.
            field.inputMethodEnabled = false;
            field.text = initialText;

            root.SetActive(true);
            return field;
        }

        /// <summary>An EventSystem, for the tests that need selection to be real.</summary>
        public EventSystem EventSystem()
        {
            var go = new GameObject("EventSystem", typeof(EventSystem));
            _created.Add(go);
            return go.GetComponent<EventSystem>();
        }

        /// <summary>Quads the label actually handed the canvas renderer.</summary>
        public static int DrawnQuads(OneTextLabel label)
        {
            // Asks the renderer, not the label: a label that builds quads but
            // never uploads them would fool the label's own ledger.
            var mesh = label.canvasRenderer.GetMesh();
            return mesh == null ? 0 : mesh.vertexCount / 4;
        }

        /// <summary>
        /// Waits a real frame and lets uGUI rebuild what it dirtied.
        ///
        /// <see cref="Canvas.ForceUpdateCanvases"/> after the yield rather than
        /// instead of it: the frame is what these tests are for (Update runs,
        /// the typewriter's clock advances, a destroyed object actually goes
        /// away), and the forced rebuild only guarantees the geometry is
        /// readable by the time the assertion looks at it, in a batch run where
        /// nothing is presenting to a screen.
        /// </summary>
        public static IEnumerator Frame()
        {
            yield return null;
            UnityEngine.Canvas.ForceUpdateCanvases();
        }

        public static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return Frame();
        }

        /// <summary>
        /// Fails the test if the code under test logged an error or an
        /// exception; PlayMode swallows both by default, and "no exceptions" is
        /// half of what most of these tests assert.
        /// </summary>
        public static void ExpectNoErrors() => LogAssert.NoUnexpectedReceived();
    }
}
