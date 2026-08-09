using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

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
    /// A project with precise labels has a second atlas (same budget setting,
    /// four bytes a texel), and it gets the same readout, because its pressure
    /// is invisible in the first one's numbers.
    ///
    /// And then the button: everything this session discovered at runtime is
    /// exactly what the next session should prewarm.
    /// </summary>
    public sealed class HubAtlasTab : HubSection
    {
        private static readonly Color Prewarmed = new Color(0.494f, 0.906f, 0.706f);
        private static readonly Color Runtime = new Color(1f, 0.8f, 0.4f);
        private static readonly Color Free = new Color(0.839f, 0.898f, 0.867f, 0.12f);

        private OneTextCharset _promoteInto;

        // One per atlas: the two evict independently, and a shared history
        // would chart one atlas's pressure as the other's.
        private readonly EvictionSampler _standardEvictions = new EvictionSampler();
        private readonly EvictionSampler _preciseEvictions = new EvictionSampler();

        private AtlasPanel _standardPanel;
        private AtlasPanel _precisePanel;
        private bool _lastStandard;
        private bool _lastPrecise;
        private double _lastTick;

        public override OneTextHub.Tab Tab => OneTextHub.Tab.Atlas;

        public override string Title => "Atlas";

        public override string Eyebrow => "Glyph memory";

        public override string Lede =>
            "Live occupancy of the shared glyph atlas. Prewarmed tiles were predicted by a " +
            "charset; runtime tiles were discovered by a frame that needed them and paid for them.";

        public override string NavHint => "occupancy and budget";

        public override string NavGroup => "What gets baked";

        public override string BadgeText
        {
            get
            {
                if (!SharedGlyphAtlas.Exists) return null;
                var stats = SharedGlyphAtlas.Atlas.GetStats();
                if (stats.Drops > 0) return "DROPS";
                return HubUI.Percent(stats.UsedFraction);
            }
        }

        public override HubTone BadgeTone =>
            SharedGlyphAtlas.Exists && SharedGlyphAtlas.Atlas.GetStats().Drops > 0
                ? HubTone.Bad
                : HubTone.Neutral;

        protected override void Compose(VisualElement content)
        {
            // Existence checks, not the getters: drawing an inspector must
            // never be the thing that allocates an atlas.
            bool standard = SharedGlyphAtlas.Exists;
            bool precise = SharedGlyphAtlas.PreciseAtlasExists;
            _lastStandard = standard;
            _lastPrecise = precise;
            _standardPanel = null;
            _precisePanel = null;

            if (!standard && !precise)
            {
                var card = HubUI.MakeCard("No atlas yet",
                    "One is created the first time something draws text.");
                card.Add(HubUI.Empty("Nothing has been rasterised",
                    "Enter play mode, or open a scene with a label in it, and this screen fills " +
                    "with what the session baked and what it cost.",
                    "Open charsets", () => Hub.Go(OneTextHub.Tab.Charsets), "○"));
                content.Add(card.Root);
                content.Add(PromoteCard());
                return;
            }

            if (standard)
            {
                _standardPanel = new AtlasPanel(
                    precise ? "Standard atlas" : "Atlas",
                    "SDF, one byte a texel: every ordinary label.",
                    _standardEvictions);
                _standardPanel.Update(SharedGlyphAtlas.Atlas, Hub);
                content.Add(_standardPanel.Root);
            }

            if (precise)
            {
                _precisePanel = new AtlasPanel("Precise atlas",
                    "MSDF, four bytes a texel: labels that asked for sharp corners.",
                    _preciseEvictions);
                _precisePanel.Update(SharedGlyphAtlas.PreciseAtlas, Hub);
                content.Add(_precisePanel.Root);
            }

            content.Add(PromoteCard());
        }

        /// <summary>
        /// Live numbers, without rebuilding the panel under the reader's mouse.
        ///
        /// Only when an atlas appears or disappears does the shape of this
        /// screen change, and only then is it composed again.
        /// </summary>
        public override void Tick()
        {
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastTick < 0.5) return;
            _lastTick = now;

            if (SharedGlyphAtlas.Exists != _lastStandard ||
                SharedGlyphAtlas.PreciseAtlasExists != _lastPrecise)
            {
                Refresh();
                return;
            }

            if (_standardPanel != null && SharedGlyphAtlas.Exists)
                _standardPanel.Update(SharedGlyphAtlas.Atlas, Hub);
            if (_precisePanel != null && SharedGlyphAtlas.PreciseAtlasExists)
                _precisePanel.Update(SharedGlyphAtlas.PreciseAtlas, Hub);
        }

        // ----------------------------------------------------------- one atlas

        private sealed class AtlasPanel
        {
            private readonly EvictionSampler _evictions;
            private readonly HubDonut _donut = new HubDonut();
            private readonly HubSparkline _spark = new HubSparkline();
            private readonly Label _prewarmedValue = Value();
            private readonly Label _runtimeValue = Value();
            private readonly Label _freeValue = Value();
            private readonly VisualElement _numbers = new VisualElement();
            private readonly VisualElement _demand = new VisualElement();

            public VisualElement Root { get; }

            public AtlasPanel(string title, string note, EvictionSampler evictions)
            {
                _evictions = evictions;
                var card = HubUI.MakeCard(title, note);

                var charts = HubUI.Box("chart-row");
                charts.Add(_donut);
                var legend = HubUI.Box("legend");
                legend.Add(LegendRow(Prewarmed, "prewarmed", _prewarmedValue));
                legend.Add(LegendRow(Runtime, "baked at runtime", _runtimeValue));
                legend.Add(LegendRow(Free, "free", _freeValue));
                charts.Add(legend);
                card.Add(charts);

                card.Add(_numbers);
                card.Add(_spark);
                card.Add(_demand);
                Root = card.Root;
            }

            public void Update(GlyphAtlas atlas, OneTextHub hub)
            {
                var stats = atlas.GetStats();
                _evictions.Sample(stats);

                // Pixel counts and byte counts are the same number in an R8
                // atlas and a factor of four apart in a precise one; every MB
                // figure on this screen is a byte figure, so the factor is
                // applied once here.
                int bytesPerTexel = stats.CapacityPixels > 0
                    ? (int)(stats.MemoryBytes / stats.CapacityPixels)
                    : 1;

                long capacity = System.Math.Max(1L, stats.CapacityPixels);
                float prewarmed = stats.PrewarmedPixels / (float)capacity;
                float runtime = stats.RuntimePixels / (float)capacity;

                _donut.Slices(
                    (prewarmed, Prewarmed),
                    (runtime, Runtime),
                    (Mathf.Max(0f, 1f - prewarmed - runtime), Free));
                _donut.Caption(HubUI.Percent(stats.UsedFraction), "FULL");

                _prewarmedValue.text =
                    $"{stats.PrewarmedTiles:n0} tiles · {Megabytes(stats.PrewarmedPixels * bytesPerTexel)}";
                _runtimeValue.text =
                    $"{stats.RuntimeTiles:n0} tiles · {Megabytes(stats.RuntimePixels * bytesPerTexel)}";
                _freeValue.text =
                    Megabytes((stats.CapacityPixels - stats.UsedPixels) * bytesPerTexel);

                _numbers.Clear();
                _numbers.style.marginTop = 14f;
                _numbers.style.marginLeft = -16f;
                _numbers.style.marginRight = -16f;
                _numbers.Add(HubUI.KeyValue("Budget",
                    $"{atlas.Settings}, {stats.MemoryBytes / (1024f * 1024f):0.#} MB"));
                _numbers.Add(HubUI.KeyValue("Occupancy",
                    $"{HubUI.Percent(stats.UsedFraction, 1)} of capacity, {stats.TileCount:n0} tiles on " +
                    $"{stats.ShelfCount:n0} shelves"));
                _numbers.Add(HubUI.KeyValue("Reclamation",
                    $"{stats.Evictions:n0} eviction(s), {stats.Compactions:n0} compaction(s), " +
                    $"{stats.Drops:n0} drop(s)",
                    stats.Drops > 0 ? HubTone.Bad : HubTone.Neutral));
                _numbers.Add(HubUI.KeyValue("Uploads",
                    $"{stats.PartialUploads:n0} partial, {stats.FullUploads:n0} full"));

                _spark.Set(_evictions.History, _evictions.Caption);

                _demand.Clear();
                _demand.style.marginTop = 14f;
                if (stats.Drops > 0)
                {
                    _demand.Add(HubUI.Notice(
                        $"{stats.Drops:n0} tile(s) did not fit even after eviction and " +
                        "compaction; a frame asked for more than this atlas holds. Those glyphs " +
                        "are not lost, they come back next frame, but the budget is too small for " +
                        "the working set.", HubTone.Bad));
                }
                Demand(atlas, stats, bytesPerTexel);
            }

            /// <summary>
            /// The budget question, answered from demand rather than occupancy.
            ///
            /// "What budget does my game need" is what everyone actually wants
            /// from this screen, and it is answerable: the atlas knows every
            /// distinct tile it has ever baked. Ten minutes of playing gives a
            /// number, and the number is the recommendation.
            /// </summary>
            private void Demand(GlyphAtlas atlas, in GlyphAtlasStats stats, int bytesPerTexel)
            {
                _demand.Add(HubUI.Text("DEMAND", "h3"));
                if (stats.DemandTiles == 0)
                {
                    _demand.Add(HubUI.Text(
                        GlyphAtlas.TrackDemand
                            ? "Nothing baked yet this session."
                            : "Demand tracking is off in this build.", "hint"));
                    return;
                }

                var rows = new VisualElement();
                rows.style.marginLeft = -16f;
                rows.style.marginRight = -16f;
                rows.Add(HubUI.KeyValue("This session wanted",
                    $"{stats.DemandTiles:n0} distinct tiles · " +
                    $"{Megabytes(stats.DemandPixels * bytesPerTexel)}"));
                _demand.Add(rows);

                // The same headroom prewarm leaves: an atlas packed to the last
                // pixel evicts on the first glyph nobody predicted.
                long needed = (long)(stats.DemandPixels / 0.85f);
                if (needed <= stats.CapacityPixels)
                {
                    rows.Add(HubUI.KeyValue("Verdict",
                        $"the current {atlas.Settings} budget covers it.", HubTone.Good));
                    return;
                }

                var recommended = SmallestBudgetFor(needed, atlas.Settings.TextureSize);
                _demand.Add(HubUI.Notice(
                    $"This session needed {Megabytes(needed * bytesPerTexel)} of tiles including " +
                    $"headroom, and the budget is " +
                    $"{Megabytes(stats.CapacityPixels * bytesPerTexel)}. {recommended} would hold it.",
                    HubTone.Warn));
                _demand.Add(HubUI.Primary("Open Global Settings",
                    () => OneTextHub.Open(OneTextHub.Tab.Settings)));
            }

            private static Label Value()
            {
                var label = HubUI.Text("", "legend__value");
                HubUI.Mono(label);
                return label;
            }

            private static VisualElement LegendRow(Color color, string name, Label value)
            {
                var row = HubUI.Box("legend__row");
                var swatch = HubUI.Box("legend__swatch");
                swatch.style.backgroundColor = color;
                row.Add(swatch);
                row.Add(HubUI.Text(name, "legend__name"));
                row.Add(value);
                return row;
            }
        }

        private static GlyphAtlasSettings SmallestBudgetFor(long neededPixels, int preferredSize)
        {
            // GlyphAtlasSettings.MemoryBytes is one byte a texel, which makes it
            // a texel count too, and texels, not bytes, are what demand is
            // measured in, so the comparison holds for both kinds of atlas.
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

        /// <summary>
        /// Evictions per sample, so the shape of the pressure is visible rather
        /// than a single number that only ever goes up.
        /// </summary>
        private sealed class EvictionSampler
        {
            private const int Length = 120;
            private readonly int[] _history = new int[Length];
            private int _cursor;
            private int _lastEvictions;
            private double _lastSample;

            /// <summary>The window, oldest first.</summary>
            public int[] History
            {
                get
                {
                    var ordered = new int[Length];
                    for (int i = 0; i < Length; i++) ordered[i] = _history[(_cursor + i) % Length];
                    return ordered;
                }
            }

            public string Caption
            {
                get
                {
                    int peak = 0;
                    foreach (int value in _history) peak = Mathf.Max(peak, value);
                    return peak > 0
                        ? $"evictions per second (peak {peak:n0})"
                        : "no evictions";
                }
            }

            public void Sample(in GlyphAtlasStats stats)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastSample < 1.0) return;

                // The first sample is a baseline, not a spike: everything evicted
                // before the window was opened happened over an unknown stretch
                // of time, and charting it as one second of pressure is a lie.
                bool first = _lastSample == 0.0;
                _lastSample = now;
                _history[_cursor] = first ? 0 : Mathf.Max(0, stats.Evictions - _lastEvictions);
                _cursor = (_cursor + 1) % Length;
                _lastEvictions = stats.Evictions;
            }
        }

        // ------------------------------------------------------------ promote

        /// <summary>
        /// Everything this session baked at runtime, appended to a charset.
        ///
        /// The recorder is the source rather than the atlas itself, and that is
        /// not a shortcut: atlas tiles are keyed by cluster hash, and a cluster
        /// is not a character; a ligature or a Hangul syllable is one tile from
        /// several code points, and there is no way back from the hash. What a
        /// charset needs is characters, which is what the recorder has.
        /// </summary>
        private VisualElement PromoteCard()
        {
            var card = HubUI.MakeCard("Runtime discoveries",
                "Append what the game drew to a charset, so the next run prewarms what this one " +
                "paid to find out.");

            if (!CharsetRecorder.Enabled && CharsetRecorder.CodepointCount == 0)
            {
                card.Add(HubUI.Notice(
                    "Character recording is off, so there is nothing to promote. Turn on 'Record " +
                    "Charset In Play Mode' in Global Settings and play.", HubTone.Neutral));
                card.Add(HubUI.Ghost("Global settings",
                    () => OneTextHub.Open(OneTextHub.Tab.Settings)));
                return card.Root;
            }

            card.Add(HubUI.Field("Into charset", HubUI.AssetPicker<OneTextCharset>(
                () => _promoteInto,
                value => { _promoteInto = value; Refresh(); },
                "Choose a charset…",
                "Create a new charset…",
                () => CreateAsset<OneTextCharset>("OneTextCharset",
                    "Where should the charset go?"))));

            if (_promoteInto == null)
            {
                card.Add(HubUI.Notice(
                    $"{CharsetRecorder.CodepointCount:n0} character(s) recorded this session. " +
                    "Pick a charset to append them to.", HubTone.Neutral));
                return card.Root;
            }

            if (CharsetRecorder.CodepointCount == 0)
            {
                card.Add(HubUI.Notice("Nothing recorded yet: enter play mode and draw some text.",
                    HubTone.Neutral));
                return card.Root;
            }

            card.Add(HubUI.Primary(
                $"Add {CharsetRecorder.CodepointCount:n0} recorded character(s) to {_promoteInto.name}",
                Promote));
            return card.Root;
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
            Refresh();
            Say($"Promoted {after - before:n0} new character(s) into {_promoteInto.name} " +
                $"({after:n0} total).");
        }

        private static string Megabytes(long bytes) => $"{bytes / (1024f * 1024f):0.##} MB";
    }
}
