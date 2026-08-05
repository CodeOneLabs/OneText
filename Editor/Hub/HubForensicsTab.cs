using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>
    /// Type a string, click a glyph, get the answer: which font drew it, which
    /// characters it came from, whether the shaper substituted it, and — at the
    /// end of a line — the UAX #14 rule that put the break there, by name.
    ///
    /// The layout is real: the same engine, the same font chain, the same
    /// language. Clicking maps back through the laid-out glyph boxes, so what
    /// is inspected is what is drawn.
    /// </summary>
    public sealed class HubForensicsTab : IDisposable
    {
        private string _text = "The quick brown fox — 「こんにちは」と言った。 ค่าที่ตั้งไว้";
        private OneFontAsset _font;
        private string _language = "";
        private float _fontSize = 34f;
        private float _boxWidth = 420f;
        private int _selected = -1;

        private readonly TextLayoutEngine _engine = new TextLayoutEngine();
        private readonly TextLayoutResult _layout = new TextLayoutResult();
        private readonly TextPreviewRenderer _renderer = new TextPreviewRenderer();
        private List<GlyphReport> _reports;
        private FontStack _fonts;
        private Texture2D _preview;
        private string _previewKey;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Forensics",
                "Why does this glyph look like that, and why did the line break there? Both " +
                "answers are in the engine at the moment it draws; this reads them back out.");

            EditorGUI.BeginChangeCheck();
            _text = EditorGUILayout.TextField("Text", _text);
            _font = (OneFontAsset)EditorGUILayout.ObjectField("Font", _font, typeof(OneFontAsset), false);
            _language = EditorGUILayout.TextField(new GUIContent("Language",
                "Passed to the shaper, so locl runs — ja, zh-Hans, ko, th."), _language);
            _fontSize = EditorGUILayout.Slider("Font size", _fontSize, 8f, 96f);
            _boxWidth = EditorGUILayout.Slider("Box width", _boxWidth, 60f, 900f);
            if (EditorGUI.EndChangeCheck()) Invalidate();

            var font = _font != null ? _font
                : OneTextSettings.Instance != null ? OneTextSettings.Instance.DefaultFont
                : null;
            if (font == null || font.Font == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick a font, or set a project default in Project Settings > OneText.",
                    MessageType.Info);
                return;
            }

            Rebuild(font);
            DrawPreview(font);
            EditorGUILayout.Space();
            DrawSelection(font);
            EditorGUILayout.Space();
            DrawGlyphList();
        }

        private void Rebuild(OneFontAsset font)
        {
            if (_reports != null) return;

            _fonts = new FontStack();
            _fonts.Add(font.Font, font.Language);
            var settings = OneTextSettings.Instance;
            if (settings != null)
                foreach (var fallback in settings.FallbackFonts)
                    if (fallback != null) _fonts.Add(fallback.Font, fallback.Language);

            var layoutSettings = TextLayoutSettings.Default(_fonts, _fontSize);
            layoutSettings.MaxWidth = _boxWidth;
            layoutSettings.Language = string.IsNullOrWhiteSpace(_language) ? null : _language.Trim();
            layoutSettings.Kinsoku = AsianTypography.Kinsoku.Normal;
            _engine.Layout(_text ?? string.Empty, layoutSettings, _layout);
            _reports = GlyphForensics.Inspect(_text, _layout, _fonts);
        }

        /// <summary>
        /// The rendered text, with a click mapped back to a glyph.
        ///
        /// Hit-testing against the glyph boxes rather than the caret positions
        /// on purpose: a caret answers "where would an edit go", and the
        /// question here is "what is this thing I am looking at".
        /// </summary>
        private void DrawPreview(OneFontAsset font)
        {
            int width = Mathf.CeilToInt(_boxWidth);
            int height = Mathf.CeilToInt(Mathf.Max(_layout.Height + _fontSize, _fontSize * 2f));
            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));

            string key = $"{_text}|{font.GetInstanceID()}|{_language}|{_fontSize}|{_boxWidth}";
            if (_preview == null || _previewKey != key)
            {
                if (_preview != null) UnityEngine.Object.DestroyImmediate(_preview);
                _preview = _renderer.Render(_text, font, _language, _fontSize, width, height,
                    TextWrap.Wrap, TextAlignment.Left);
                _previewKey = key;
            }
            if (_preview != null) GUI.DrawTexture(rect, _preview, ScaleMode.ScaleAndCrop);

            // The selected glyph, boxed, in the same coordinates the engine laid
            // it out in: y down from the top of the block.
            if (_selected >= 0 && _selected < _reports.Count)
            {
                var box = BoxOf(_reports[_selected]);
                if (box.width > 0f)
                {
                    box.position += rect.position;
                    DrawBox(box, new Color(0.4f, 0.85f, 1f, 0.9f));
                }
            }

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                var local = Event.current.mousePosition - rect.position;
                _selected = GlyphAt(local);
                Event.current.Use();
            }
        }

        /// <summary>The glyph's box in layout coordinates, or an empty rect.</summary>
        private Rect BoxOf(in GlyphReport report)
        {
            if (report.GlyphIndex < 0 || report.GlyphIndex >= _layout.Glyphs.Count) return default;
            var run = _layout.Runs[report.RunIndex];
            float scale = run.FontSize / Mathf.Max(1f, run.Font?.UnitsPerEm ?? 1000f);

            float x = run.X;
            for (int g = run.GlyphStart; g < report.GlyphIndex; g++)
                x += _layout.Glyphs[g].XAdvance * scale;

            float advance = _layout.Glyphs[report.GlyphIndex].XAdvance * scale;
            var line = _layout.Lines[report.LineIndex];
            return new Rect(x, line.Baseline - line.Ascent, Mathf.Max(2f, advance), line.Height);
        }

        private int GlyphAt(Vector2 point)
        {
            for (int i = 0; i < _reports.Count; i++)
            {
                var box = BoxOf(_reports[i]);
                if (box.width > 0f && box.Contains(point)) return i;
            }
            return -1;
        }

        private static void DrawBox(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        private void DrawSelection(OneFontAsset font)
        {
            if (_selected < 0 || _selected >= _reports.Count)
            {
                EditorGUILayout.LabelField("Click a glyph above.", EditorStyles.miniLabel);
                return;
            }

            var report = _reports[_selected];
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Characters",
                    $"'{report.Characters}'  {CodepointList(report.Characters)}  " +
                    $"at {report.TextStart}..{report.TextStart + report.TextLength}");
                EditorGUILayout.LabelField("Glyph",
                    report.Substituted
                        ? $"{report.GlyphId} — substituted; the cmap maps this character to " +
                          $"{report.NominalGlyphId}"
                        : $"{report.GlyphId}");
                EditorGUILayout.LabelField("Font",
                    report.FontFamily + (string.IsNullOrEmpty(report.FontLanguage)
                        ? "  (no language tag)"
                        : $"  [{report.FontLanguage}]"));
                EditorGUILayout.LabelField("Run",
                    $"line {report.LineIndex}, run {report.RunIndex}, " +
                    (report.RightToLeft ? "right-to-left" : "left-to-right") +
                    (report.Positioned ? ", moved by GPOS" : ""));

                if (report.EndsLine)
                {
                    EditorGUILayout.LabelField("Line break",
                        report.BreakRule + (report.BreakNote != null ? $" — {report.BreakNote}" : ""));
                }

                EditorGUILayout.LabelField("Features",
                    GlyphForensics.FeatureSummary(FontOf(report, font)),
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private FontData FontOf(in GlyphReport report, OneFontAsset fallback)
        {
            if (report.RunIndex >= 0 && report.RunIndex < _layout.Runs.Count)
            {
                var font = _layout.Runs[report.RunIndex].Font;
                if (font != null) return font;
            }
            return fallback != null ? fallback.Font : null;
        }

        private void DrawGlyphList()
        {
            EditorGUILayout.LabelField($"Glyphs ({_reports.Count:n0})", EditorStyles.boldLabel);
            for (int i = 0; i < _reports.Count; i++)
            {
                var report = _reports[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(i == _selected ? "•" : " ",
                        EditorStyles.miniButton, GUILayout.Width(22f)))
                        _selected = i;
                    EditorGUILayout.LabelField(report.ToString(), EditorStyles.miniLabel);
                }
            }
        }

        private static string CodepointList(string text)
        {
            var parts = new List<string>();
            foreach (int codepoint in TextDoctor.Codepoints(text)) parts.Add($"U+{codepoint:X4}");
            return string.Join(" ", parts);
        }

        private void Invalidate()
        {
            _reports = null;
            _selected = -1;
        }

        public void Dispose()
        {
            if (_preview != null) UnityEngine.Object.DestroyImmediate(_preview);
            _preview = null;
            _renderer.Dispose();
        }
    }
}
