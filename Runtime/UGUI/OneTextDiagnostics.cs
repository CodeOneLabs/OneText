using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// The Hub's atlas and font diagnostics, on the device, in a development
    /// build.
    ///
    /// The classic text bug appears only in a build: a font that resolved in
    /// the editor and not on the device, an atlas budget a phone cannot afford,
    /// natives that did not ship. The state of the art for debugging it is a
    /// screenshot of nothing, sent by a tester. So the same numbers the editor
    /// shows travel with the build: per-label font resolution, characters no
    /// font could draw, live atlas pressure.
    ///
    /// Development builds and the editor only: <see cref="Debug.isDebugBuild"/>
    /// is the gate, and the component removes itself from a release build
    /// rather than drawing a debug overlay in front of a player.
    /// </summary>
    [AddComponentMenu("OneText/OneText Diagnostics")]
    public sealed class OneTextDiagnostics : MonoBehaviour
    {
        [Tooltip("Key that shows and hides the overlay.")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;

        [Tooltip("Visible from the moment the scene loads, rather than waiting for the key.")]
        [SerializeField] private bool _visibleAtStart;

        [Tooltip("Labels listed at once. The rest are counted, not listed.")]
        [SerializeField] private int _maxLabels = 12;

        private bool _visible;
        private Vector2 _scroll;
        private readonly List<OneTextLabel> _labels = new List<OneTextLabel>();
        private readonly StringBuilder _builder = new StringBuilder();
        private GUIStyle _style;
        private float _nextRefresh;

        /// <summary>
        /// Adds the overlay to any scene, from anywhere; a build that is
        /// already misbehaving is not one you want to rebuild to instrument.
        /// </summary>
        public static OneTextDiagnostics Install()
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return null;
            var existing = FindExisting();
            if (existing != null) return existing;

            var go = new GameObject("OneText Diagnostics");
            DontDestroyOnLoad(go);
            return go.AddComponent<OneTextDiagnostics>();
        }

        private void Awake()
        {
            if (!Debug.isDebugBuild && !Application.isEditor)
            {
                // Not merely hidden: a release build should not be walking every
                // label once a second to build a string nobody will read.
                Destroy(this);
                return;
            }
            _visible = _visibleAtStart;
        }

        private void OnGUI()
        {
            // Read from the event rather than Input: a project on the new input
            // system throws on UnityEngine.Input, and an overlay that crashes
            // the build it was added to debug is worse than no overlay.
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == _toggleKey)
            {
                _visible = !_visible;
                e.Use();
            }
            if (!_visible) return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(11, Screen.height / 60),
                richText = false,
                wordWrap = false,
            };

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.5f;
                Refresh();
            }

            float width = Mathf.Min(Screen.width - 20f, 620f);
            var area = new Rect(10f, 10f, width, Mathf.Min(Screen.height - 20f, 460f));
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label(_builder.ToString(), _style);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Refresh()
        {
            _builder.Clear();
            _builder.Append("OneText diagnostics · ").Append(_toggleKey).Append(" to hide\n\n");

            AppendAtlas();
            AppendLabels();
        }

        private void AppendAtlas()
        {
            // Existence checks, not the getters: a diagnostics overlay must
            // never be the thing that allocates an atlas.
            if (!SharedGlyphAtlas.Exists && !SharedGlyphAtlas.PreciseAtlasExists)
            {
                _builder.Append("atlas: not created yet\n\n");
                return;
            }

            if (SharedGlyphAtlas.Exists)
                AppendAtlas("atlas", SharedGlyphAtlas.Atlas);
            if (SharedGlyphAtlas.PreciseAtlasExists)
                AppendAtlas("precise atlas", SharedGlyphAtlas.PreciseAtlas);
        }

        private void AppendAtlas(string name, GlyphAtlas atlas)
        {
            var stats = atlas.GetStats();
            // Demand is counted in texels; the precise atlas pays four bytes
            // for each of them, and a memory line that forgot that would
            // under-report by the difference.
            long bytesPerTexel = stats.CapacityPixels > 0 ? stats.MemoryBytes / stats.CapacityPixels : 1;
            _builder.Append(name).Append(' ').Append(atlas.Settings)
                .Append("  ").Append(Megabytes(stats.MemoryBytes)).Append('\n');
            _builder.Append("  ").Append(stats.TileCount).Append(" tiles, ")
                .Append((stats.UsedFraction * 100f).ToString("0.0")).Append("% full (")
                .Append(stats.PrewarmedTiles).Append(" prewarmed, ")
                .Append(stats.RuntimeTiles).Append(" runtime)\n");
            _builder.Append("  ").Append(stats.Evictions).Append(" evictions, ")
                .Append(stats.Compactions).Append(" compactions, ")
                .Append(stats.Drops).Append(" drops\n");
            if (stats.DemandTiles > 0)
            {
                _builder.Append("  this session wanted ").Append(stats.DemandTiles)
                    .Append(" distinct tiles, ")
                    .Append(Megabytes(stats.DemandPixels * bytesPerTexel)).Append('\n');
            }
            if (stats.Drops > 0)
                _builder.Append("  ! tiles are being dropped: the budget is too small\n");
            _builder.Append('\n');
        }

        private void AppendLabels()
        {
            _labels.Clear();
            _labels.AddRange(AllLabels());

            _builder.Append("labels: ").Append(_labels.Count).Append('\n');
            int listed = 0;
            foreach (var label in _labels)
            {
                if (listed >= _maxLabels)
                {
                    _builder.Append("  ... and ").Append(_labels.Count - listed).Append(" more\n");
                    break;
                }
                listed++;
                AppendLabel(label);
            }
        }

        private void AppendLabel(OneTextLabel label)
        {
            _builder.Append("  ").Append(label.name).Append(": ");
            var fonts = label.ResolvedFonts;
            if (fonts == null || fonts.Primary == null)
            {
                _builder.Append("NO FONT RESOLVED · this label draws nothing\n");
                return;
            }

            _builder.Append(fonts.Count).Append(" font(s) in chain");

            int missing = 0;
            int firstMissing = 0;
            string text = label.Text;
            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
                    int codepoint = c;
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        codepoint = char.ConvertToUtf32(c, text[i + 1]);
                        i++;
                    }
                    if (fonts.Covers(codepoint)) continue;
                    if (missing == 0) firstMissing = codepoint;
                    missing++;
                }
            }

            if (missing > 0)
            {
                _builder.Append(", ").Append(missing)
                    .Append(" character(s) NO FONT DRAWS (first U+")
                    .Append(firstMissing.ToString("X4")).Append(')');
            }
            _builder.Append('\n');
        }

        private static string Megabytes(long bytes) => (bytes / (1024f * 1024f)).ToString("0.#") + " MB";

        // Unsorted deliberately: the sorting overloads pay for an order nothing
        // here reads, and the overlay repeats this every half second.
        private static OneTextDiagnostics FindExisting() =>
            FindAnyObjectByType<OneTextDiagnostics>();

        private static OneTextLabel[] AllLabels() =>
            FindObjectsByType<OneTextLabel>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
    }
}
