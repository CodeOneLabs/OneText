using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// What the atlas holds, split by where it came from, and the button that
    /// closes the loop.
    ///
    /// Occupancy on its own is the number that lies: an atlas under pressure
    /// recycles rather than fills, so a project whose budget is far too small
    /// reads a comfortable 30% forever while it re-bakes the same glyphs every
    /// frame. The three-way split says which of those tiles the project
    /// predicted; the demand figure beside it says how much the session
    /// actually wanted, counting each tile once however many times it was
    /// evicted and re-baked.
    ///
    /// And then the button: everything this session discovered at runtime is
    /// exactly what the next session should prewarm.
    /// </summary>
    public sealed class HubAtlasTab
    {
        private static readonly Color Prewarmed = new Color(0.35f, 0.72f, 0.98f);
        private static readonly Color Runtime = new Color(0.98f, 0.72f, 0.30f);
        private static readonly Color Free = new Color(0.28f, 0.30f, 0.34f);

        private OneTextCharset _promoteInto;

        // Evictions per sample, so the shape of the pressure is visible rather
        // than a single number that only ever goes up.
        private const int History = 120;
        private readonly int[] _evictionHistory = new int[History];
        private int _historyCursor;
        private int _lastEvictions;
        private double _lastSample;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Atlas",
                "Live occupancy of the shared glyph atlas. Prewarmed tiles were predicted by a " +
                "charset; runtime tiles were discovered by a frame that needed them.");

            if (!SharedGlyphAtlas.Exists)
            {
                EditorGUILayout.HelpBox(
                    "No atlas yet — it is created the first time something draws text. " +
                    "Enter play mode, or open a scene with a label in it.", MessageType.Info);
                return;
            }

            var atlas = SharedGlyphAtlas.Atlas;
            var stats = atlas.GetStats();
            Sample(stats);

            DrawPie(stats);
            EditorGUILayout.Space();
            DrawNumbers(atlas, stats);
            EditorGUILayout.Space();
            DrawBudgetAdvice(atlas, stats);
            EditorGUILayout.Space();
            DrawPromote();
        }

        // --------------------------------------------------------------- pie

        private void DrawPie(in GlyphAtlasStats stats)
        {
            var rect = GUILayoutUtility.GetRect(10f, 168f);
            float radius = 72f;
            var center = new Vector2(rect.x + radius + 12f, rect.y + radius + 8f);

            long capacity = Mathf.Max(1, (int)Mathf.Min(stats.CapacityPixels, int.MaxValue));
            float prewarmed = stats.PrewarmedPixels / (float)capacity;
            float runtime = stats.RuntimePixels / (float)capacity;

            if (Event.current.type == EventType.Repaint)
            {
                // Handles draw in GUI coordinates here, which is why this is
                // guarded on Repaint: a layout pass would leave arcs behind.
                Handles.BeginGUI();
                float angle = 0f;
                angle += Slice(center, radius, angle, prewarmed, Prewarmed);
                angle += Slice(center, radius, angle, runtime, Runtime);
                Slice(center, radius, angle, Mathf.Max(0f, 1f - prewarmed - runtime), Free);
                Handles.EndGUI();
            }

            var legend = new Rect(center.x + radius + 24f, rect.y + 8f, rect.width - radius * 2f - 48f, 20f);
            Legend(ref legend, Prewarmed, "prewarmed",
                $"{stats.PrewarmedTiles:n0} tiles, {Megabytes(stats.PrewarmedPixels)}");
            Legend(ref legend, Runtime, "baked at runtime",
                $"{stats.RuntimeTiles:n0} tiles, {Megabytes(stats.RuntimePixels)}");
            Legend(ref legend, Free, "free",
                Megabytes(stats.CapacityPixels - stats.UsedPixels));
        }

        private static float Slice(Vector2 center, float radius, float from, float fraction, Color color)
        {
            if (fraction <= 0.0001f) return 0f;
            Handles.color = color;
            float degrees = fraction * 360f;
            Handles.DrawSolidArc(center, Vector3.forward, DirectionOf(from), degrees, radius);
            return degrees;
        }

        private static Vector3 DirectionOf(float degrees)
        {
            // Clockwise from the top, because a pie that starts anywhere else
            // reads as an arbitrary picture rather than a proportion.
            float radians = -degrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), -Mathf.Cos(radians), 0f);
        }

        private static void Legend(ref Rect rect, Color color, string label, string detail)
        {
            var swatch = new Rect(rect.x, rect.y + 4f, 12f, 12f);
            EditorGUI.DrawRect(swatch, color);
            var text = new Rect(rect.x + 18f, rect.y, rect.width - 18f, rect.height);
            EditorGUI.LabelField(text, label, detail);
            rect.y += 22f;
        }

        // ----------------------------------------------------------- numbers

        private void DrawNumbers(GlyphAtlas atlas, in GlyphAtlasStats stats)
        {
            EditorGUILayout.LabelField("Budget",
                $"{atlas.Settings} — {stats.MemoryBytes / (1024f * 1024f):0.#} MB");
            EditorGUILayout.LabelField("Occupancy",
                $"{stats.UsedFraction:P1} of capacity, {stats.TileCount:n0} tiles on " +
                $"{stats.ShelfCount:n0} shelves");
            EditorGUILayout.LabelField("Reclamation",
                $"{stats.Evictions:n0} eviction(s), {stats.Compactions:n0} compaction(s), " +
                $"{stats.Drops:n0} drop(s)");
            EditorGUILayout.LabelField("Uploads",
                $"{stats.PartialUploads:n0} partial, {stats.FullUploads:n0} full");

            if (stats.Drops > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{stats.Drops:n0} tile(s) did not fit even after eviction and compaction — a " +
                    "frame asked for more than this atlas holds. Those glyphs are not lost, they " +
                    "come back next frame, but the budget is too small for the working set.",
                    MessageType.Warning);
            }

            DrawEvictionHistory();
        }

        private void DrawEvictionHistory()
        {
            var rect = GUILayoutUtility.GetRect(10f, 46f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.15f));
            if (Event.current.type != EventType.Repaint) return;

            int peak = 1;
            foreach (int value in _evictionHistory) peak = Mathf.Max(peak, value);

            float barWidth = rect.width / History;
            for (int i = 0; i < History; i++)
            {
                int value = _evictionHistory[(_historyCursor + i) % History];
                if (value <= 0) continue;
                float height = rect.height * value / peak;
                EditorGUI.DrawRect(
                    new Rect(rect.x + i * barWidth, rect.yMax - height, Mathf.Max(1f, barWidth - 1f), height),
                    Runtime);
            }

            EditorGUI.LabelField(new Rect(rect.x + 4f, rect.y, 240f, 16f),
                peak > 1 ? $"evictions per second (peak {peak:n0})" : "no evictions",
                EditorStyles.miniLabel);
        }

        private void Sample(in GlyphAtlasStats stats)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSample < 1.0) return;

            // The first sample is a baseline, not a spike: everything evicted
            // before the tab was opened happened over an unknown stretch of
            // time, and charting it as one second of pressure is a lie.
            bool first = _lastSample == 0.0;
            _lastSample = now;
            _evictionHistory[_historyCursor] = first ? 0 : Mathf.Max(0, stats.Evictions - _lastEvictions);
            _historyCursor = (_historyCursor + 1) % History;
            _lastEvictions = stats.Evictions;
        }

        /// <summary>
        /// The budget question, answered from demand rather than occupancy.
        ///
        /// "What budget does my game need" is what everyone actually wants from
        /// this screen, and it is answerable: the atlas knows every distinct
        /// tile it has ever baked. Ten minutes of playing gives a number, and
        /// the number is the recommendation.
        /// </summary>
        private static void DrawBudgetAdvice(GlyphAtlas atlas, in GlyphAtlasStats stats)
        {
            EditorGUILayout.LabelField("Demand", EditorStyles.boldLabel);
            if (stats.DemandTiles == 0)
            {
                EditorGUILayout.LabelField(
                    GlyphAtlas.TrackDemand
                        ? "Nothing baked yet this session."
                        : "Demand tracking is off in this build.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField("This session wanted",
                $"{stats.DemandTiles:n0} distinct tiles, {Megabytes(stats.DemandPixels)}");

            // The same headroom prewarm leaves: an atlas packed to the last
            // pixel evicts on the first glyph nobody predicted.
            long needed = (long)(stats.DemandPixels / 0.85f);
            if (needed <= stats.CapacityPixels)
            {
                EditorGUILayout.LabelField("Verdict",
                    $"the current {atlas.Settings} budget covers it.");
                return;
            }

            var recommended = SmallestBudgetFor(needed, atlas.Settings.TextureSize);
            EditorGUILayout.HelpBox(
                $"This session needed {Megabytes(needed)} of tiles including headroom, and the " +
                $"budget is {Megabytes(stats.CapacityPixels)}. {recommended} would hold it.",
                MessageType.Warning);
            if (GUILayout.Button("Open Project Settings"))
                SettingsService.OpenProjectSettings("Project/OneText");
        }

        private static GlyphAtlasSettings SmallestBudgetFor(long neededPixels, int preferredSize)
        {
            foreach (int size in new[] { preferredSize, 1024, 2048, 4096 })
            {
                for (int layers = 1; layers <= 16; layers++)
                {
                    var candidate = new GlyphAtlasSettings { TextureSize = size, LayerCount = layers }
                        .Validated();
                    if (candidate.MemoryBytes >= neededPixels) return candidate;
                }
            }
            return new GlyphAtlasSettings { TextureSize = 4096, LayerCount = 16 }.Validated();
        }

        // ---------------------------------------------------------- promote

        /// <summary>
        /// Everything this session baked at runtime, appended to a charset.
        ///
        /// The recorder is the source rather than the atlas itself, and that is
        /// not a shortcut: atlas tiles are keyed by cluster hash, and a cluster
        /// is not a character — a ligature or a Hangul syllable is one tile from
        /// several code points, and there is no way back from the hash. What a
        /// charset needs is characters, which is what the recorder has.
        /// </summary>
        private void DrawPromote()
        {
            EditorGUILayout.LabelField("Promote", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Append what this session drew to a charset, so the next run prewarms what this " +
                "one paid to discover.", EditorStyles.wordWrappedMiniLabel);

            if (!CharsetRecorder.Enabled && CharsetRecorder.CodepointCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "Character recording is off, so there is nothing to promote. Turn on " +
                    "'Record Charset In Play Mode' in Project Settings > OneText and play.",
                    MessageType.Info);
                return;
            }

            _promoteInto = (OneTextCharset)EditorGUILayout.ObjectField("Into charset",
                _promoteInto, typeof(OneTextCharset), false);

            using (new EditorGUI.DisabledScope(_promoteInto == null ||
                CharsetRecorder.CodepointCount == 0))
            {
                if (GUILayout.Button($"Promote {CharsetRecorder.CodepointCount:n0} recorded character(s)"))
                    Promote();
            }
        }

        private void Promote()
        {
            int before = _promoteInto.Codepoints().Count;
            Undo.RecordObject(_promoteInto, "Promote runtime glyphs");
            _promoteInto.Characters = HubCharsetsTab.Merge(_promoteInto.Characters,
                CharsetRecorder.CharactersAsString());
            foreach (float size in CharsetRecorder.SizesSorted())
                if (!_promoteInto.Sizes.Contains(size)) _promoteInto.Sizes.Add(size);
            EditorUtility.SetDirty(_promoteInto);
            AssetDatabase.SaveAssets();

            int after = _promoteInto.Codepoints().Count;
            Debug.Log($"OneText: promoted {after - before:n0} new character(s) into " +
                $"{_promoteInto.name} ({after:n0} total).", _promoteInto);
        }

        private static string Megabytes(long pixels) => $"{pixels / (1024f * 1024f):0.##} MB";
    }
}
