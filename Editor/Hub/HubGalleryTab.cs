using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Every string the project ships, laid out with its real font, style and
    /// locale, in one grid — with the overflows in red.
    ///
    /// The pass this replaces is a person opening every screen in every
    /// language looking for the three buttons whose German label does not fit.
    /// The layout engine needs no scene to answer that, so it is a table you
    /// scroll rather than a build you play.
    ///
    /// The same view flipped — one string across every style — is how a
    /// typeface gets chosen, with your own sentence rather than the pangram the
    /// foundry picked.
    /// </summary>
    public sealed class HubGalleryTab : IDisposable
    {
        private enum Mode
        {
            EveryString,
            OneStringEveryStyle,
        }

        private Mode _mode;
        private GalleryOptions _options = GalleryOptions.Default;
        private TextScanResult _scan;
        private List<GalleryCell> _cells;
        private string _localeFilter = "(all)";
        private bool _problemsOnly;
        private string _sample = "다람쥐 헌 쳇바퀴에 타고파";
        private bool _preview = true;
        private int _shown;

        private readonly TextPreviewRenderer _renderer = new TextPreviewRenderer();
        private readonly Dictionary<string, Texture2D> _previews = new Dictionary<string, Texture2D>();

        /// <summary>Cells rendered per repaint, so a table of ten thousand strings still scrolls.</summary>
        private const int PreviewBudget = 60;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Gallery",
                "Strings laid out headlessly, at the size and in the box they will have. " +
                "Red means the text does not fit, or that no font in the chain can draw it.");

            _mode = (Mode)EditorGUILayout.EnumPopup("Show", _mode);
            DrawOptions();

            EditorGUILayout.Space();
            if (_mode == Mode.OneStringEveryStyle) DrawOneString();
            else DrawEveryString(hub);
        }

        private void DrawOptions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _options.BoxWidth = EditorGUILayout.FloatField("Box", _options.BoxWidth);
                _options.BoxHeight = EditorGUILayout.FloatField(_options.BoxHeight, GUILayout.Width(60f));
            }
            _options.FontSize = EditorGUILayout.FloatField("Font size", _options.FontSize);
            _options.Wrap = (TextWrap)EditorGUILayout.EnumPopup("Wrap", _options.Wrap);
            _preview = EditorGUILayout.Toggle(new GUIContent("Render previews",
                "Draw each cell with the engine. Off is faster on very large tables."), _preview);
        }

        // ------------------------------------------------- one string, all styles

        private void DrawOneString()
        {
            _sample = EditorGUILayout.TextField("String", _sample);
            var styles = OneTextHub.AllStyles();
            var fonts = OneTextHub.AllFonts();

            if (fonts.Count == 0)
            {
                EditorGUILayout.HelpBox("No fonts imported yet.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            foreach (var style in styles)
            {
                var font = style.Sets(OneTextStyle.Fields.Font) ? style.Font : null;
                DrawRow(style.name, _sample, font,
                    style.Sets(OneTextStyle.Fields.Size) ? style.FontSize : _options.FontSize, null);
            }

            // Styles are how a project should choose type, and font assets are
            // what it has before anybody has made one.
            foreach (var font in fonts)
                DrawRow(font.FamilyName, _sample, font, _options.FontSize, font.Language);
        }

        private void DrawRow(string label, string text, OneFontAsset font, float size, string language)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(160f));
                var rect = GUILayoutUtility.GetRect(_options.BoxWidth, Mathf.Max(24f, size * 1.6f));
                if (!_preview) return;

                var texture = Preview($"{label}|{size}|{text}", text, font, language, size,
                    (int)rect.width, (int)rect.height);
                if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleAndCrop);
                else EditorGUI.LabelField(rect, "(no font)", EditorStyles.miniLabel);
            }
        }

        // ---------------------------------------------------- every string

        private void DrawEveryString(OneTextHub hub)
        {
            hub.DrawStringFolders();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan and lay out")) Rebuild(hub);
                _problemsOnly = GUILayout.Toggle(_problemsOnly, "Problems only",
                    EditorStyles.miniButton, GUILayout.Width(110f));
            }

            if (_cells == null)
            {
                EditorGUILayout.LabelField("Nothing scanned yet.", EditorStyles.miniLabel);
                return;
            }

            var locales = _scan.Locales();
            var options = new List<string> { "(all)" };
            foreach (string locale in locales) options.Add(locale ?? "(unnamed)");
            int index = Mathf.Max(0, options.IndexOf(_localeFilter));
            _localeFilter = options[EditorGUILayout.Popup("Locale", index, options.ToArray())];

            int problems = 0;
            foreach (var cell in _cells) if (!cell.Ok) problems++;
            EditorGUILayout.LabelField("Result",
                $"{_cells.Count:n0} cell(s) from {_scan.FilesScanned} file(s); {problems:n0} " +
                (problems == 1 ? "problem" : "problems"));

            EditorGUILayout.Space();
            _shown = 0;
            foreach (var cell in _cells)
            {
                if (_problemsOnly && cell.Ok) continue;
                if (_localeFilter != "(all)" &&
                    (cell.Entry.Locale ?? "(unnamed)") != _localeFilter) continue;
                DrawCell(cell);
            }

            if (_shown == 0)
                EditorGUILayout.LabelField("Nothing to show with these filters.", EditorStyles.miniLabel);
        }

        private void DrawCell(in GalleryCell cell)
        {
            _shown++;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        $"{cell.Entry.Locale ?? "—"}  {cell.Entry.Key}", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(Status(cell), StatusStyle(cell), GUILayout.Width(220f));
                }

                var rect = GUILayoutUtility.GetRect(_options.BoxWidth,
                    Mathf.Max(24f, _options.BoxHeight));
                rect.width = Mathf.Min(rect.width, _options.BoxWidth);

                if (_preview && _shown <= PreviewBudget)
                {
                    var style = cell.Style;
                    var font = style != null && style.Sets(OneTextStyle.Fields.Font)
                        ? style.Font
                        : OneTextSettings.Instance != null
                            ? OneTextSettings.Instance.DefaultFont
                            : null;
                    var texture = Preview(
                        $"{cell.Entry.Source}|{cell.Entry.Key}|{cell.StyleName}|{_options.FontSize}",
                        cell.Entry.Value, font, cell.Entry.Locale,
                        style != null && style.Sets(OneTextStyle.Fields.Size)
                            ? style.FontSize : _options.FontSize,
                        (int)rect.width, (int)rect.height);
                    if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleAndCrop);
                }
                else
                {
                    EditorGUI.LabelField(rect, cell.Entry.Value, EditorStyles.wordWrappedMiniLabel);
                }

                // The box is drawn as a frame around the render so overflow is
                // something you see rather than a number you compare.
                if (!cell.Ok) DrawOutline(rect, new Color(0.95f, 0.35f, 0.35f, 0.9f));
            }
        }

        private static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        private static string Status(in GalleryCell cell)
        {
            if (cell.MissingGlyphs > 0)
                return $"{cell.MissingGlyphs} character(s) no font draws";
            if (cell.Overflow)
                return $"overflows: needs {cell.Width:0}x{cell.Height:0}" +
                       (cell.WouldTruncate ? " (will be cut)" : "");
            return $"{cell.Width:0}x{cell.Height:0} in {cell.LineCount} line(s)";
        }

        private static GUIStyle StatusStyle(in GalleryCell cell)
        {
            var style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            if (!cell.Ok) style.normal.textColor = new Color(0.95f, 0.45f, 0.45f);
            return style;
        }

        private void Rebuild(OneTextHub hub)
        {
            ClearPreviews();
            _scan = TextSourceScanner.Scan(hub.StringFolders);
            var styles = OneTextHub.AllStyles();
            _cells = StringGallery.Measure(_scan.Entries, styles, _options);
        }

        // ------------------------------------------------------------ previews

        private Texture2D Preview(string key, string text, OneFontAsset font, string language,
            float size, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            string cacheKey = $"{key}|{width}x{height}";
            if (_previews.TryGetValue(cacheKey, out var cached) && cached != null) return cached;

            var texture = _renderer.Render(text, font, language, size, width, height,
                _options.Wrap, TextAlignment.Left);
            _previews[cacheKey] = texture;
            return texture;
        }

        private void ClearPreviews()
        {
            foreach (var texture in _previews.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            _previews.Clear();
        }

        public void Dispose()
        {
            ClearPreviews();
            _renderer.Dispose();
        }
    }
}
