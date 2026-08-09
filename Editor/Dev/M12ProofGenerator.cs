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
    /// Batch-mode visual proof for M12: what an input field looks like while an
    /// input method is composing into it. Every panel is a real
    /// <see cref="OneTextInputField"/> driven through the same
    /// <see cref="IImeInput"/> the platform backends implement: the
    /// composition is not painted on, it is the field's own state.
    ///
    /// Run: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///      OneText.Editor.M12ProofGenerator.Generate -oneOut &lt;dir&gt;
    /// </summary>
    public static class M12ProofGenerator
    {
        private const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string KoreanFont = "/System/Library/Fonts/AppleSDGothicNeo.ttc";
        private const string JapaneseFont = "/System/Library/Fonts/Hiragino Sans GB.ttc";

        private static string LatinFontFullPath => Path.GetFullPath(LatinFont);

        /// <summary>The IME the panels type through.</summary>
        private sealed class ScriptedIme : IImeInput
        {
            public string Composition = string.Empty;
            public int Caret = -1;
            public int ClauseStart;
            public int ClauseLength;

            public bool IsAvailable => true;

            public void Begin() { }

            public void End() => Composition = string.Empty;

            public void SetCursorScreenPosition(Vector2 screenPosition) { }

            public bool TryGetComposition(out string text, out int caret,
                out int clauseStart, out int clauseLength)
            {
                text = Composition;
                caret = Caret;
                clauseStart = ClauseStart;
                clauseLength = ClauseLength;
                return !string.IsNullOrEmpty(text);
            }
        }

        private static readonly ScriptedIme Ime = new ScriptedIme();

        public static void Generate()
        {
            string outDir = GetArg("-oneOut") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            ImeInput.Register(() => Ime);
            try
            {
                if (File.Exists(KoreanFont))
                    RenderComposition(Path.Combine(outDir, "onetext-m12-composition.png"));
                else
                    Debug.LogWarning($"No Korean face at {KoreanFont}; skipped the composition panel.");

                RenderFocusLoss(Path.Combine(outDir, "onetext-m12-focus-loss.png"));
            }
            finally
            {
                ImeInput.Unregister();
            }

            Debug.Log($"M12 proof written to {outDir}");
        }

        /// <summary>
        /// The states an input method walks through, drawn where the user is
        /// looking: inline at the caret, underlined, with the clause a Japanese
        /// IME is converting blocked in behind the text.
        /// </summary>
        private static void RenderComposition(string path)
        {
            const int W = 1400, H = 700;
            var scene = new Scene(W, H);

            // A Hangul syllable assembling one key at a time. The field's value
            // is "안녕하세" throughout; only the composition changes.
            string[] hangul = { "ㅇ", "요", "용" };
            for (int step = 0; step < hangul.Length; step++)
            {
                var field = Field(scene, KoreanFont, new Rect(40f, 40f + step * 110f, 620f, 90f));
                field.text = "안녕하세";
                field.caretPosition = 4;
                Compose(field, hangul[step]);
                Caption(scene, new Rect(680f, 55f + step * 110f, 680f, 60f),
                    $"composing “{hangul[step]}”; value is still “{field.text}”");
            }

            // Japanese conversion: the whole reading is underlined, the clause
            // being converted is blocked in.
            var japanese = Field(scene, JapaneseFont, new Rect(40f, 380f, 620f, 90f));
            japanese.text = "> ";
            japanese.caretPosition = 2;
            Compose(japanese, "きょうはあめ", caret: 3, clauseStart: 0, clauseLength: 3);
            Caption(scene, new Rect(680f, 395f, 680f, 60f),
                "Japanese: clause 0..3 converting, the rest still a reading");

            // Composing over a selection replaces it; the selection is already
            // gone by the time the first key lands.
            var replacing = Field(scene, KoreanFont, new Rect(40f, 490f, 620f, 90f));
            replacing.text = "replace 이것 please";
            replacing.SetSelection(8, 10);
            Compose(replacing, "저것");
            Caption(scene, new Rect(680f, 505f, 680f, 60f),
                $"composed over a selection; value is now “{replacing.text}”");

            // A read-only field refuses composition outright.
            var readOnly = Field(scene, KoreanFont, new Rect(40f, 600f, 620f, 90f));
            readOnly.text = "읽기 전용";
            readOnly.readOnly = true;
            readOnly.caretPosition = 5;
            Compose(readOnly, "한");
            Caption(scene, new Rect(680f, 615f, 680f, 60f),
                $"read only: composition refused, composing = {readOnly.isComposing}");

            scene.Save(path);
        }

        /// <summary>
        /// The bug this milestone is named after. Left: focus is leaving while
        /// a syllable is still being composed. Right: the same field one
        /// deactivation later: the syllable is in the value, once.
        /// </summary>
        private static void RenderFocusLoss(string path)
        {
            const int W = 1400, H = 320;
            var scene = new Scene(W, H);
            string fontPath = File.Exists(KoreanFont) ? KoreanFont : LatinFont;
            string committed = File.Exists(KoreanFont) ? "안녕하세" : "hello";
            string composing = File.Exists(KoreanFont) ? "요" : "!";

            var before = Field(scene, fontPath, new Rect(40f, 60f, 620f, 90f));
            before.text = committed;
            before.caretPosition = committed.Length;
            Compose(before, composing);
            Caption(scene, new Rect(40f, 160f, 620f, 60f),
                $"focus about to leave: value “{before.text}”, composing “{composing}”");

            var after = Field(scene, fontPath, new Rect(720f, 60f, 620f, 90f));
            after.text = committed;
            after.caretPosition = committed.Length;
            Compose(after, composing);
            after.DeactivateInputField();
            after.UpdateVisuals();
            Caption(scene, new Rect(720f, 160f, 620f, 60f),
                $"after DeactivateInputField: value “{after.text}”, nothing lost, nothing doubled");

            Caption(scene, new Rect(40f, 240f, 1320f, 60f),
                "TMP_InputField drops the last syllable here; the platform echo that follows is swallowed, not applied twice");

            scene.Save(path);
        }

        private static void Compose(OneTextInputField field, string composition,
            int caret = -1, int clauseStart = 0, int clauseLength = 0)
        {
            Ime.Composition = composition;
            Ime.Caret = caret;
            Ime.ClauseStart = clauseStart;
            Ime.ClauseLength = clauseLength;
            field.UpdateEditing();
            field.UpdateVisuals();
            Ime.Composition = string.Empty; // the next field starts clean
        }

        private static void Caption(Scene scene, Rect rect, string text)
        {
            var label = LabelObject(scene, LatinFont, rect, 24f);
            // The captions quote the fields, so they need the fields' scripts:
            // a caption reading “value is still 안녕하세” in a Latin-only face
            // is four boxes and a lie about what the field holds.
            if (File.Exists(KoreanFont) && File.Exists(JapaneseFont))
            {
                label.SetFont(File.ReadAllBytes(LatinFontFullPath),
                    File.ReadAllBytes(KoreanFont), File.ReadAllBytes(JapaneseFont));
            }
            label.Text = text;
            label.Wrap = TextWrap.Wrap;
            label.VerticalAlignment = VerticalAlignment.Middle;
            label.color = new Color(1f, 1f, 1f, 0.6f);
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
                foreach (var field in Fields) field.UpdateVisuals();
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

        private static OneTextInputField Field(Scene scene, string fontPath, Rect rect)
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

            var text = LabelObject(scene, fontPath,
                new Rect(0f, 0f, rect.width - 24f, rect.height - 16f), 34f, root.transform);
            text.rectTransform.anchoredPosition = new Vector2(12f, -8f);
            text.raycastTarget = false;

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = text;
            serialized.FindProperty("_caretBlinkRate").floatValue = 0f; // solid caret for the screenshot
            serialized.FindProperty("_caretWidth").floatValue = 3f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            field.targetGraphic = background;

            field.ActivateInputField();
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
