using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// Batch-mode visual proof for M5: input-field caret and selection
    /// (including RTL), and clickable link ranges with their hit-test boxes
    /// drawn underneath so the geometry can be checked by eye.
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M5ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M5ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            RenderInputFields(Path.Combine(outDir, "onetext-m5-input.png"));
            RenderLinks(Path.Combine(outDir, "onetext-m5-links.png"));
            Debug.Log($"M5 proof written to {outDir}");
        }

        private static void RenderInputFields(string path)
        {
            const int W = 1400, H = 560;
            var scene = new Scene(W, H);

            var caretMid = Field(scene, LatinFont, new Rect(40f, 40f, 620f, 90f));
            caretMid.text = "caret sits between graphemes";
            caretMid.caretPosition = 5;

            var selected = Field(scene, LatinFont, new Rect(40f, 170f, 620f, 90f));
            selected.text = "double-click selects a word";
            selected.SetSelection(13, 20);

            var rtl = Field(scene, ArabicFont, new Rect(40f, 300f, 620f, 90f));
            rtl.text = "مرحبا بالعالم";
            rtl.caretPosition = 6;

            var rtlSelection = Field(scene, ArabicFont, new Rect(40f, 430f, 620f, 90f));
            rtlSelection.text = "تحديد النص العربي";
            rtlSelection.SetSelection(0, 6);

            var empty = Field(scene, LatinFont, new Rect(700f, 40f, 620f, 90f), focus: false);
            empty.text = string.Empty;

            var multiline = Field(scene, LatinFont, new Rect(700f, 170f, 620f, 350f));
            multiline.multiline = true;
            multiline.textComponent.Wrap = TextWrap.Wrap;
            multiline.textComponent.VerticalAlignment = VerticalAlignment.Top;
            multiline.text = "multiline editing:\nthe caret moves by line,\nselection spans rows";
            multiline.SetSelection(19, 46);

            foreach (var field in scene.Fields) field.UpdateVisuals();
            scene.Save(path);
        }

        private static void RenderLinks(string path)
        {
            const int W = 1400, H = 400;
            var scene = new Scene(W, H);

            var label = LabelObject(scene, LatinFont, new Rect(40f, 40f, 1320f, 300f), 40f);
            label.Text = "Read the <link=manual>manual</link>, file an " +
                         "<link=issues>issue</link>, or say hi on <link=chat>the chat</link>.";
            label.Wrap = TextWrap.Wrap;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.EnsureLayout();

            // Draw each link's hit-test rectangles behind the text: if a box is
            // off by a pixel from the words it covers, it shows up here.
            var boxes = new GameObject("LinkBoxes", typeof(RectTransform), typeof(CanvasRenderer));
            boxes.transform.SetParent(label.transform, false);
            var boxRect = boxes.GetComponent<RectTransform>();
            boxRect.anchorMin = Vector2.zero;
            boxRect.anchorMax = Vector2.one;
            boxRect.pivot = label.rectTransform.pivot;
            boxRect.sizeDelta = Vector2.zero;
            boxRect.anchoredPosition = Vector2.zero;
            boxes.transform.SetAsFirstSibling();

            var highlight = boxes.AddComponent<OneTextCaret>();
            highlight.SelectionColor = new Color(0.24f, 0.50f, 0.87f, 0.45f);

            var rects = new List<Rect>();
            var all = new List<Rect>();
            foreach (var link in label.Links)
            {
                label.GetSelectionRects(link.Start, link.End, rects);
                all.AddRange(rects);
            }
            highlight.SetGeometry(default, false, all);

            var caption = LabelObject(scene, LatinFont, new Rect(40f, 250f, 1320f, 80f), 26f);
            caption.Text = $"{label.Links.Count} link ranges parsed; boxes are the click targets";
            caption.color = new Color(1f, 1f, 1f, 0.55f);
            caption.VerticalAlignment = VerticalAlignment.Top;

            scene.Save(path);
        }

        private sealed class Scene
        {
            public readonly GameObject CanvasGo;
            public readonly List<OneTextInputField> Fields = new List<OneTextInputField>();
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            private readonly int _width, _height;

            public Scene(int width, int height)
            {
                _width = width;
                _height = height;

                // Rendering a canvas by hand skips the code that normally sets
                // this global, and the SDF shader reads it for its ZTest.
                Shader.SetGlobalFloat("unity_GUIZTestMode",
                    (float)UnityEngine.Rendering.CompareFunction.Always);

                var camGo = new GameObject("ProofCamera");
                _camera = camGo.AddComponent<Camera>();
                _camera.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.orthographic = true;
                _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                _camera.targetTexture = _target;

                CanvasGo = new GameObject("ProofCanvas");
                var canvas = CanvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _camera;
                canvas.planeDistance = 5f;
                CanvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
            }

            public void Save(string path)
            {
                Canvas.ForceUpdateCanvases();
                _camera.Render();

                var previous = RenderTexture.active;
                RenderTexture.active = _target;
                var tex = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
                tex.Apply(false);
                RenderTexture.active = previous;
                File.WriteAllBytes(path, tex.EncodeToPNG());

                UnityEngine.Object.DestroyImmediate(tex);
                _camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(_target);
                UnityEngine.Object.DestroyImmediate(CanvasGo);
                UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            }
        }

        private static OneTextLabel LabelObject(Scene scene, string fontPath, Rect rect, float size,
            Transform parent = null)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent != null ? parent : scene.CanvasGo.transform, false);
            var label = go.AddComponent<OneTextLabel>();

            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);

            label.SetFont(File.ReadAllBytes(Path.GetFullPath(fontPath)));
            label.FontSize = size;
            label.Alignment = TextAlignment.Start;
            label.VerticalAlignment = VerticalAlignment.Middle;
            label.Wrap = TextWrap.NoWrap;
            label.color = Color.white;
            return label;
        }

        /// <summary>An input field with a background, text label and placeholder.</summary>
        private static OneTextInputField Field(Scene scene, string fontPath, Rect rect, bool focus = true)
        {
            var root = new GameObject("InputField", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(scene.CanvasGo.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 1f);
            rootRect.anchorMax = new Vector2(0f, 1f);
            rootRect.pivot = new Vector2(0f, 1f);
            rootRect.sizeDelta = new Vector2(rect.width, rect.height);
            rootRect.anchoredPosition = new Vector2(rect.x, -rect.y);

            var background = root.AddComponent<Image>();
            background.color = new Color(0.17f, 0.17f, 0.20f, 1f);

            var text = LabelObject(scene, fontPath, new Rect(0f, 0f, rect.width - 24f, rect.height - 16f), 30f,
                root.transform);
            text.rectTransform.anchoredPosition = new Vector2(12f, -8f);
            text.raycastTarget = false;

            var placeholder = LabelObject(scene, fontPath,
                new Rect(0f, 0f, rect.width - 24f, rect.height - 16f), 30f, root.transform);
            placeholder.rectTransform.anchoredPosition = new Vector2(12f, -8f);
            placeholder.Text = "Enter text…";
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);
            placeholder.raycastTarget = false;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = text;
            serialized.FindProperty("_placeholder").objectReferenceValue = placeholder;
            serialized.FindProperty("_caretBlinkRate").floatValue = 0f; // solid caret for the screenshot
            serialized.FindProperty("_caretWidth").floatValue = 3f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            field.targetGraphic = background;

            if (focus) field.ActivateInputField();
            scene.Fields.Add(field);
            return field;
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }
    }
}
