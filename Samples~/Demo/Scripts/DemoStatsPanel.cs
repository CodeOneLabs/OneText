using System.Collections.Generic;
using System.Text;
using OneText.UGUI;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// What the demo is actually for: the numbers behind the pretty column.
    ///
    /// Two sources, and they are worth keeping apart because they answer to
    /// different authorities. The renderer and memory rows come from Unity's
    /// own profiler counters through <see cref="ProfilerRecorder"/> — nothing
    /// in this package can inflate them. The atlas rows come from
    /// <see cref="GlyphAtlasStats"/>, which is OneText marking its own
    /// homework, and is labelled as such.
    ///
    /// The renderer counters exist in the editor and in a development build,
    /// and not in a release build; each recorder is checked for
    /// <see cref="ProfilerRecorder.Valid"/> and prints a dash rather than a
    /// confident zero when it has nothing. A demo that reported "0 batches"
    /// because the counter was switched off would be making exactly the claim
    /// it is trying to prove, for exactly the wrong reason.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class DemoStatsPanel : MonoBehaviour
    {
        /// <summary>
        /// Frames between refreshes, not seconds.
        ///
        /// A wall clock looks like the obvious gate and is the wrong one: it
        /// ties the panel to a clock that a paused game, a capture harness or a
        /// batch-mode run can leave standing still, and a stats panel frozen at
        /// frame one is worse than no stats panel, because it is wrong rather
        /// than absent. Counting frames costs nothing and cannot stop.
        /// </summary>
        private const int RefreshFrames = 15;

        private OneTextLabel _target;
        private OneTextDemoFonts _fonts;
        private readonly StringBuilder _builder = new StringBuilder(1024);

        private ProfilerRecorder _batches;
        private ProfilerRecorder _setPass;
        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _vertices;
        private ProfilerRecorder _totalMemory;
        private ProfilerRecorder _gcMemory;
        private ProfilerRecorder _textureMemory;

        private float _frameAccumulator;
        private int _frameCount;
        private float _fps;
        private int _clipGroups;

        /// <summary>Labels the panel counts. The demo owns the list; the panel
        /// only reads it, so a stress run that adds fifty labels needs no
        /// second call to say so.</summary>
        public List<OneTextLabel> Counted { get; } = new List<OneTextLabel>();

        public void Bind(OneTextLabel target, OneTextDemoFonts fonts, Transform uiRoot)
        {
            _target = target;
            _fonts = fonts;

            // Counted once, because it cannot change: the demo builds its masks
            // in Awake and never adds another.
            _clipGroups = uiRoot != null
                ? uiRoot.GetComponentsInChildren<RectMask2D>(true).Length
                : 0;
        }

        private void OnEnable()
        {
            // Category names, not free text: a typo makes an invalid recorder,
            // which is why every read below is guarded rather than trusted.
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            _totalMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Reserved Memory");
            _gcMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
            _textureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
        }

        private void OnDisable()
        {
            _batches.Dispose();
            _setPass.Dispose();
            _drawCalls.Dispose();
            _triangles.Dispose();
            _vertices.Dispose();
            _totalMemory.Dispose();
            _gcMemory.Dispose();
            _textureMemory.Dispose();
        }

        private void Update()
        {
            _frameAccumulator += Time.unscaledDeltaTime;
            _frameCount++;

            if (_frameCount < RefreshFrames) return;

            if (_frameAccumulator > 0f) _fps = _frameCount / _frameAccumulator;
            _frameAccumulator = 0f;
            _frameCount = 0;

            if (_target != null) _target.Text = Compose();
        }

        private string Compose()
        {
            _builder.Clear();

            _builder.Append("<color=#8B949E>frame</color>\n");
            KeyValue("fps", _fps.ToString("0"));
            KeyValue("batches", Count(_batches));
            KeyValue("setpass", Count(_setPass));
            KeyValue("draw calls", Count(_drawCalls));
            KeyValue("triangles", Count(_triangles));
            KeyValue("vertices", Count(_vertices));
            AppendBatchBudget();

            _builder.Append("\n<color=#8B949E>memory</color>\n");
            KeyValue("total reserved", Bytes(_totalMemory));
            KeyValue("gc reserved", Bytes(_gcMemory));
            KeyValue("textures", Bytes(_textureMemory));

            _builder.Append("\n<color=#8B949E>labels</color>\n");
            KeyValue("onetext labels", Counted.Count.ToString());
            int animating = 0, characters = 0;
            for (int i = 0; i < Counted.Count; i++)
            {
                var label = Counted[i];
                if (label == null) continue;
                if (label.IsAnimating) animating++;
                characters += label.Text != null ? label.Text.Length : 0;
            }
            KeyValue("animating", animating.ToString());
            KeyValue("characters", characters.ToString());
            KeyValue("fonts", _fonts != null ? _fonts.Describe() : "-");

            _builder.Append("\n<color=#8B949E>atlas · onetext's own count</color>\n");
            AppendAtlases();

            if (!_batches.Valid)
            {
                _builder.Append(
                    "\n<color=#D29922>renderer counters need a development build</color>");
            }

            return _builder.ToString();
        }

        /// <summary>
        /// Where the batches went, said in the panel rather than left for the
        /// reader to work out with the Frame Debugger open.
        ///
        /// This exists because of the question it pre-empts. Open the Frame
        /// Debugger on this demo and the first thing you notice is that text
        /// does not all merge into one draw, and the natural conclusion — that
        /// the text engine is what is costing you draws — is the opposite of
        /// what is happening. Taken apart by disabling one piece at a time, at
        /// 136 labels:
        ///
        ///     everything                      10 batches, 5 set-pass
        ///     one RectMask2D disabled          7
        ///     both disabled                    5
        ///     ...and the atlas viewer too      4
        ///     text alone, no Images at all     1 batch,  1 set-pass
        ///
        /// One batch. Nine scripts, colour emoji, fourteen animated effects,
        /// 136 labels, one draw — because they all sample one Texture2DArray
        /// through one material. A second TextMeshPro font asset is a second
        /// material and therefore a second batch; a second script here is a
        /// few more atlas tiles and no draws at all.
        ///
        /// The other nine belong to uGUI, and five of them are the two scroll
        /// masks. Different <c>_ClipRect</c> values cannot merge, because the
        /// CanvasRenderer sets that uniform per draw. Note what the numbers do
        /// NOT show: the set-pass count is unmoved by the masks, so the
        /// <c>UNITY_UI_CLIP_RECT</c> keyword variant is not costing a pass here
        /// even though the clip rects are costing draws.
        /// </summary>
        private void AppendBatchBudget()
        {
            if (!_batches.Valid) return;
            _builder.Append("  <color=#8B949E>text alone is 1 batch, measured.\n")
                .Append("  the rest is ").Append(_clipGroups).Append(" scroll mask")
                .Append(_clipGroups == 1 ? "" : "s")
                .Append(", ui images, atlas view —\n")
                .Append("  all uGUI. a clip rect cannot merge with another.</color>\n");
        }

        private void AppendAtlases()
        {
            // Existence checks rather than the getters, all three times: a
            // stats panel that allocated the MSDF atlas in order to report
            // that no MSDF atlas exists would be reporting on itself.
            bool any = false;
            if (SharedGlyphAtlas.Exists)
            {
                Atlas("sdf", SharedGlyphAtlas.Atlas.GetStats());
                any = true;
            }
            if (SharedGlyphAtlas.PreciseAtlasExists)
            {
                Atlas("msdf", SharedGlyphAtlas.PreciseAtlas.GetStats());
                any = true;
            }
            if (SharedGlyphAtlas.ColorAtlasExists)
            {
                var stats = SharedGlyphAtlas.ColorAtlas.GetStats();
                KeyValue("colour tiles", stats.TileCount.ToString());
                KeyValue("colour memory", DemoUi.Megabytes(stats.MemoryBytes));
                any = true;
            }
            if (!any) _builder.Append("  not created yet\n");
        }

        private void Atlas(string prefix, in GlyphAtlasStats stats)
        {
            KeyValue(prefix + " tiles", stats.TileCount + " (" +
                                        stats.PrewarmedTiles + " prewarmed)");
            KeyValue(prefix + " fill", (stats.UsedFraction * 100f).ToString("0.0") + "%");
            KeyValue(prefix + " memory", DemoUi.Megabytes(stats.MemoryBytes));
            KeyValue(prefix + " uploads", stats.PartialUploads + " partial / " +
                                          stats.FullUploads + " full");
            if (stats.Evictions > 0 || stats.Compactions > 0)
            {
                KeyValue(prefix + " churn", stats.Evictions + " evicted, " +
                                            stats.Compactions + " compacted");
            }
            if (stats.Drops > 0)
            {
                _builder.Append("  <color=#F85149>! ").Append(stats.Drops)
                    .Append(" tiles dropped — the budget is too small</color>\n");
            }
        }

        private void KeyValue(string key, string value)
        {
            _builder.Append("  ").Append(key).Append("  <color=#E6EDF3>")
                .Append(value).Append("</color>\n");
        }

        private static string Count(ProfilerRecorder recorder) =>
            recorder.Valid ? recorder.LastValue.ToString("N0") : "—";

        private static string Bytes(ProfilerRecorder recorder) =>
            recorder.Valid ? DemoUi.Megabytes(recorder.LastValue) : "—";
    }
}
