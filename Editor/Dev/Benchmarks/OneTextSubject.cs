using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEditor;
using UnityEngine;

namespace OneText.Benchmarks
{
    /// <summary>Font files the benchmarks use, and where to find a CJK face.</summary>
    public static class BenchFonts
    {
        public const string Latin = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        public const string Arabic = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        // CJK faces are too large to vendor, so the benchmark borrows one from
        // the machine it runs on. Both systems under test get the same file.
        private static readonly (string path, string osFamily)[] CjkCandidates =
        {
            ("/System/Library/Fonts/AppleSDGothicNeo.ttc", "Apple SD Gothic Neo"),
            ("/System/Library/Fonts/Hiragino Sans GB.ttc", "Hiragino Sans GB"),
            ("C:/Windows/Fonts/malgun.ttf", "Malgun Gothic"),
            ("C:/Windows/Fonts/msgothic.ttc", "MS Gothic"),
        };

        public static string CjkPath
        {
            get
            {
                foreach (var candidate in CjkCandidates)
                    if (File.Exists(candidate.path)) return candidate.path;
                return null;
            }
        }

        public static string CjkOsFamily
        {
            get
            {
                foreach (var candidate in CjkCandidates)
                    if (File.Exists(candidate.path)) return candidate.osFamily;
                return null;
            }
        }

        public static byte[] Read(string path) => File.ReadAllBytes(Path.GetFullPath(path));
    }

    /// <summary>
    /// OneText under test, set up the way a real project would: font assets
    /// shared by every label (so glyphs are rasterized once), the shared atlas
    /// and material, and one upload per frame through the scheduler.
    /// </summary>
    public sealed class OneTextSubject : ITextSubject, IPrewarmable, ICoverageReporting
    {
        private readonly GlyphAtlasSettings _budget;
        private readonly bool _prewarm;

        /// <summary>
        /// Draw only what the configured fonts cover, the way an engine with
        /// no system-font tier does.
        ///
        /// Without this the comparison is not one: OneText walks the operating
        /// system's fonts for a character its own stack missed, so it draws
        /// text the other systems leave blank, and then its frame time is set
        /// beside theirs as though both had done the same work. This turns that
        /// tier off so a like-for-like row exists next to the full-coverage one.
        /// </summary>
        private readonly bool _parity;
        private bool _previousSystemFonts;
        private readonly List<OneTextLabel> _labels = new List<OneTextLabel>();
        private readonly List<OneFontAsset> _fonts = new List<OneFontAsset>();
        private OneTextSettings _settings;
        private OneTextSettings _previousSettings;

        public OneTextSubject(GlyphAtlasSettings budget, bool prewarm = false, bool parity = false)
        {
            _budget = budget.Validated();
            _prewarm = prewarm;
            _parity = parity;
        }

        public string Name => $"OneText {_budget.MemoryBytes / (1024 * 1024)}MB" +
            (_prewarm ? " +prewarm" : "") +
            (_parity ? " (no system fonts)" : "");

        public void Setup()
        {
            _fonts.Add(Load(BenchFonts.Read(BenchFonts.Latin), "NotoSans"));
            _fonts.Add(Load(BenchFonts.Read(BenchFonts.Arabic), "NotoSansArabic"));
            if (BenchFonts.CjkPath != null)
                _fonts.Add(Load(File.ReadAllBytes(BenchFonts.CjkPath), "SystemCJK"));

            _previousSystemFonts = SystemFonts.Enabled;
            if (_parity) SystemFonts.Enabled = false;

            _previousSettings = OneTextSettings.Instance;
            _settings = ScriptableObject.CreateInstance<OneTextSettings>();
            var serialized = new SerializedObject(_settings);
            serialized.FindProperty("_atlasTextureSize").intValue = _budget.TextureSize;
            serialized.FindProperty("_atlasLayerCount").intValue = _budget.LayerCount;
            // Project-wide fallback, which is how a label reaches a CJK face
            // without its own font being changed.
            var fallbacks = serialized.FindProperty("_fallbackFonts");
            fallbacks.ClearArray();
            for (int i = 1; i < _fonts.Count; i++)
            {
                fallbacks.InsertArrayElementAtIndex(fallbacks.arraySize);
                fallbacks.GetArrayElementAtIndex(fallbacks.arraySize - 1).objectReferenceValue = _fonts[i];
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            OneTextSettings.Instance = _settings;

            // Every run starts from an empty atlas of the configured size.
            SharedGlyphAtlas.Acquire();
            SharedGlyphAtlas.Reconfigure(force: true);

            // Outside play mode there is no canvas pass to hook, so the atlas
            // would otherwise upload inside every label's rebuild.
            AtlasFlushScheduler.DeferOutsidePlayMode = true;
        }

        private static OneFontAsset Load(byte[] bytes, string name)
        {
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.name = name;
            asset.Initialize(bytes, name, name);
            return asset;
        }

        public object CreateLabel(Transform parent, Rect rect, float fontSize, int fontIndex)
        {
            var go = new GameObject("OneLabel", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<OneTextLabel>();
            var rectTransform = label.rectTransform;
            rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            _labels.Add(label);
            label.Font = _fonts[Mathf.Min(fontIndex, _fonts.Count - 1)];
            label.FontSize = fontSize;
            label.Wrap = TextWrap.NoWrap;
            label.color = Color.white;
            return label;
        }

        public void SetText(object label, string text) => ((OneTextLabel)label).Text = text;

        public void EndFrame() => AtlasFlushScheduler.FlushNow();

        public long TextureMemoryBytes =>
            SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas.GetStats().MemoryBytes : 0;

        public string Describe()
        {
            if (!SharedGlyphAtlas.Exists) return "atlas not created";
            var stats = SharedGlyphAtlas.Atlas.GetStats();
            return $"atlas {SharedGlyphAtlas.Atlas.Settings}, {stats.TileCount:n0} tiles, " +
                $"{stats.UsedFraction:P0} full, {stats.Evictions:n0} evictions, " +
                $"{stats.Compactions} compactions, {stats.PartialUploads:n0} partial / " +
                $"{stats.FullUploads:n0} full uploads; " + CoverageOfLastFrame();
        }

        /// <summary>
        /// The share of the last frame's characters this system can actually
        /// draw, asked of the font stack the way the TextMeshPro subject asks
        /// its font asset: what the engine would find, without letting it add
        /// anything while answering.
        ///
        /// Reported for every system, because a frame time is only comparable
        /// to another frame time when both drew the same text.
        /// </summary>
        public string CoverageOfLastFrame()
        {
            int drawn = 0, wanted = 0;
            foreach (var label in _labels)
            {
                if (label == null) continue;
                var stack = label.Fonts;
                if (stack == null) continue;
                string text = label.DisplayText;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                    int codepoint = char.IsHighSurrogate(c) && i + 1 < text.Length
                        ? char.ConvertToUtf32(c, text[++i])
                        : c;
                    wanted++;
                    var font = stack.Resolve(codepoint);
                    if (font != null && font.IsValid && font.HasGlyph(codepoint)) drawn++;
                }
            }
            return wanted == 0
                ? "no text to check"
                : $"drew {drawn} of {wanted} characters on the last frame " +
                  $"({drawn / (double)wanted:P0})";
        }

        /// <summary>Characters wanted and drawn on the last frame, for the report's own column.</summary>
        public void CountCoverage(out int drawn, out int wanted)
        {
            drawn = 0;
            wanted = 0;
            foreach (var label in _labels)
            {
                if (label == null) continue;
                var stack = label.Fonts;
                if (stack == null) continue;
                string text = label.DisplayText;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                    int codepoint = char.IsHighSurrogate(c) && i + 1 < text.Length
                        ? char.ConvertToUtf32(c, text[++i])
                        : c;
                    wanted++;
                    var font = stack.Resolve(codepoint);
                    if (font != null && font.IsValid && font.HasGlyph(codepoint)) drawn++;
                }
            }
        }

        /// <summary>Rasterizes a charset before the run, for the +prewarm variant.</summary>
        public void Prewarm(IEnumerable<int> codepoints, IReadOnlyList<float> sizes)
        {
            if (!_prewarm) return;
            var stack = new FontStack();
            foreach (var asset in _fonts) stack.Add(asset.Font);
            AtlasPrewarm.Warm(SharedGlyphAtlas.Atlas, stack, codepoints, sizes);
            AtlasFlushScheduler.FlushNow();
        }

        public void Teardown()
        {
            _labels.Clear();
            SystemFonts.Enabled = _previousSystemFonts;
            AtlasFlushScheduler.DeferOutsidePlayMode = false;
            foreach (var asset in _fonts) Object.DestroyImmediate(asset);
            _fonts.Clear();
            SharedGlyphAtlas.Release();
            OneTextSettings.Instance = _previousSettings;
            if (_settings != null) Object.DestroyImmediate(_settings);
        }
    }
}
