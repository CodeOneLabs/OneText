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
        public const string Latin = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        public const string Arabic = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";

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
    public sealed class OneTextSubject : ITextSubject, IPrewarmable
    {
        private readonly GlyphAtlasSettings _budget;
        private readonly bool _prewarm;
        private readonly List<OneFontAsset> _fonts = new List<OneFontAsset>();
        private OneTextSettings _settings;
        private OneTextSettings _previousSettings;

        public OneTextSubject(GlyphAtlasSettings budget, bool prewarm = false)
        {
            _budget = budget.Validated();
            _prewarm = prewarm;
        }

        public string Name => $"OneText {_budget.MemoryBytes / (1024 * 1024)}MB" +
            (_prewarm ? " +prewarm" : "");

        public void Setup()
        {
            _fonts.Add(Load(BenchFonts.Read(BenchFonts.Latin), "NotoSans"));
            _fonts.Add(Load(BenchFonts.Read(BenchFonts.Arabic), "NotoSansArabic"));
            if (BenchFonts.CjkPath != null)
                _fonts.Add(Load(File.ReadAllBytes(BenchFonts.CjkPath), "SystemCJK"));

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
                $"{stats.FullUploads:n0} full uploads";
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
            AtlasFlushScheduler.DeferOutsidePlayMode = false;
            foreach (var asset in _fonts) Object.DestroyImmediate(asset);
            _fonts.Clear();
            SharedGlyphAtlas.Release();
            OneTextSettings.Instance = _previousSettings;
            if (_settings != null) Object.DestroyImmediate(_settings);
        }
    }
}
