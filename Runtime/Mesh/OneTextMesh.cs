using System.Collections.Generic;
using System.ComponentModel;
using OneText.Unicode;
using UnityEngine;
using UnityEngine.Rendering;

namespace OneText
{
    /// <summary>
    /// World-space text: the same shaping, layout and atlas pipeline as
    /// <c>OneText.UGUI.OneTextLabel</c>, rendered through a MeshFilter and
    /// MeshRenderer instead of a Canvas. Nameplates, signs and diegetic UI
    /// place one of these in the scene like any other renderer; no Canvas, and
    /// no dependency on uGUI at all.
    ///
    /// The rect comes from the RectTransform exactly as it does for a label:
    /// wrap, overflow, both alignments and auto-size all measure against it —
    /// the rect is in local units. Font sizes are in points at TextMesh Pro's
    /// world-text convention, ten points to one local unit
    /// (<see cref="PointsToUnits"/>): a TMP nameplate at size 36 in a 20×5
    /// rect ports here with the same numbers and the same on-screen size.
    /// Markup sizes convert too — <c>&lt;size=44&gt;</c> means what it meant
    /// in TMP — while em-relative values (<c>&lt;voffset&gt;</c>,
    /// <c>&lt;cspace&gt;</c>, ruby scale, line spacing) are unitless and
    /// carry over unchanged.
    ///
    /// What this deliberately does not do (the label does): reveal and
    /// animation, decorations (outline/shadow/glow and the underline family),
    /// inline sprites, style assets, and interaction. Text from rich markup
    /// still lays out correctly; tags whose visual this component cannot draw
    /// simply draw nothing extra.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("OneText/OneText Mesh (World)")]
    public sealed class OneTextMesh : MonoBehaviour
    {
        [Tooltip("Font asset to render with. Create one from any .ttf/.otf: " +
                 "right-click the font file, OneText > Create Font Asset.")]
        [SerializeField] private OneFontAsset _font;

        [Tooltip("Extra fonts for characters the main font lacks, tried before the project fallbacks.")]
        [SerializeField] private List<OneFontAsset> _fallbackFonts = new List<OneFontAsset>();

        [TextArea]
        [SerializeField] private string _text = "Hello";

        [Tooltip("Font size in points, TMP-compatible: ten points to one local unit, so " +
                 "size 36 is 3.6 units per em — the same numbers a TextMesh Pro world " +
                 "text used carry over unchanged.")]
        [SerializeField] private float _fontSize = 36f;

        [Tooltip("Pick the largest size (in points) between Min and Max at which the whole " +
                 "text fits the rect. Font Size above is ignored while this is on.")]
        [SerializeField] private bool _autoSize;
        [SerializeField] private float _autoSizeMin = 18f;
        [SerializeField] private float _autoSizeMax = 72f;

        [SerializeField] private Color _color = UnityEngine.Color.white;
        [SerializeField] private TextAlignment _alignment = TextAlignment.Start;
        [SerializeField] private VerticalAlignment _verticalAlignment = VerticalAlignment.Middle;
        [SerializeField] private TextWritingMode _writingMode = TextWritingMode.Horizontal;
        [SerializeField] private TextWrap _wrap = TextWrap.Wrap;
        [SerializeField] private TextOverflow _overflow = TextOverflow.Overflow;
        [SerializeField] private float _lineSpacing = 1f;

        [Tooltip("Parse rich-text markup. Layout tags (<b> <i> <color> <size> <align> <nobr> " +
                 "<voffset> <cspace> <ruby>) apply; tags this component cannot draw " +
                 "(decorations, sprites, effects) lay out but draw nothing extra.")]
        [SerializeField] private bool _richText = true;

        [Tooltip("Precise (MSDF) rendering, for large text and sharp corners; costs more " +
                 "atlas memory. World text is often magnified, so this earns its cost here " +
                 "more readily than on a UI label.")]
        [SerializeField] private bool _precise;

        [Tooltip("Atlas texels per em, as a multiple of what the point size asks for. " +
                 "World text has no screen size until a camera picks one, so this is how " +
                 "you say the player will get close to it. Medium (2x) by default; " +
                 "Performance (1x) matches an unscaled UI label of the same size, High (4x) " +
                 "is for text read close up, and Project takes whichever of those the " +
                 "project sets. Costs the square of itself in atlas area.")]
        [SerializeField] private TextQuality _quality = TextQuality.Medium;

        [Tooltip("BCP 47 language tag: ja, ko, zh-Hans. Empty means the project default.")]
        [SerializeField] private string _language = "";

        [SerializeField] private Unicode.AsianTypography.Kinsoku _kinsoku =
            Unicode.AsianTypography.Kinsoku.Off;
        [SerializeField] private bool _cjkLatinSpacing;
        [SerializeField] private bool _punctuationCompression;
        [Range(0.1f, 1f)]
        [SerializeField] private float _rubyScale = RubyPlacement.DefaultScale;

        // ------------------------------------------------------------ state

        private byte[] _fontBytesOverride;
        private byte[][] _fallbackBytesOverride;
        private FontStack _fonts;
        private readonly List<FontData> _ownedFonts = new List<FontData>();
        private readonly List<FontVariation> _variations = new List<FontVariation>();
        private TextLayoutEngine _engine;
        // NonSerialized: a domain reload resets the atlas's static refcount,
        // and a serialized true here would make OnDestroy release a reference
        // the new domain never counted. See OneTextLabel._atlasHeld.
        [System.NonSerialized]
        private bool _atlasHeld;

        private readonly TextLayoutResult _layout = new TextLayoutResult();
        private readonly TextLayoutResult _measure = new TextLayoutResult();
        private readonly RichTextResult _markup = new RichTextResult();
        private string _displayText;
        private string _parsedFrom;
        private bool _parsedRich;

        private Mesh _mesh;
        // NonSerialized so a domain reload lands on the initializer: the
        // reload nulled _builtAtlas and may have rebuilt the atlas itself, and
        // a serialized false here would leave a mesh nobody re-checks.
        [System.NonSerialized]
        private bool _dirty = true;
        private float _fittedSize;
        private Vector2 _blockOrigin;

        // What the last build baked its uv rects from; a version bump means
        // tiles moved and the mesh must be rebuilt, exactly as for a label.
        private GlyphAtlas _builtAtlas;
        private int _builtAtlasVersion = -1;
        private int _builtColorVersion = -1;

        private readonly List<GlyphClusters.Cluster> _clusters = new List<GlyphClusters.Cluster>();
        private readonly List<PositionedGlyph> _positioned = new List<PositionedGlyph>();

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<Vector4> _uv0 = new List<Vector4>();
        private readonly List<Vector4> _uv1 = new List<Vector4>();
        private readonly List<Vector4> _uv2 = new List<Vector4>();
        private readonly List<Vector4> _uv3 = new List<Vector4>();
        private readonly List<int> _indices = new List<int>();

        private static readonly List<OneTextMesh> s_active = new List<OneTextMesh>();

        // ------------------------------------------------------------ API

        public string Text
        {
            get => _text;
            set { _text = value; _parsedFrom = null; _dirty = true; }
        }

        public float FontSize
        {
            get => _fontSize;
            set { _fontSize = value; _dirty = true; }
        }

        /// <inheritdoc cref="TextQuality"/>
        public TextQuality Quality
        {
            get => _quality;
            set { _quality = value; _dirty = true; }
        }

        /// <summary>See <c>OneTextLabel.AutoSize</c>: same behaviour, same fit.</summary>
        public bool AutoSize
        {
            get => _autoSize;
            set { _autoSize = value; _dirty = true; }
        }

        public float AutoSizeMin
        {
            get => _autoSizeMin;
            set { _autoSizeMin = value; _dirty = true; }
        }

        public float AutoSizeMax
        {
            get => _autoSizeMax;
            set { _autoSizeMax = value; _dirty = true; }
        }

        /// <summary>
        /// The size the text is actually drawn at: the fitted size while
        /// <see cref="AutoSize"/> is on, the configured size otherwise.
        /// Rebuilds first if stale, so the answer is always current.
        /// </summary>
        public float FittedFontSize
        {
            get
            {
                RebuildIfNeeded();
                return EffectiveFontSize;
            }
        }

        public Color Color
        {
            get => _color;
            set { _color = value; _dirty = true; }
        }

        public TextAlignment Alignment
        {
            get => _alignment;
            set { _alignment = value; _dirty = true; }
        }

        public VerticalAlignment VerticalAlignment
        {
            get => _verticalAlignment;
            set { _verticalAlignment = value; _dirty = true; }
        }

        public TextWritingMode WritingMode
        {
            get => _writingMode;
            set { _writingMode = value; _dirty = true; }
        }

        public TextWrap Wrap
        {
            get => _wrap;
            set { _wrap = value; _dirty = true; }
        }

        public TextOverflow Overflow
        {
            get => _overflow;
            set { _overflow = value; _dirty = true; }
        }

        public float LineSpacing
        {
            get => _lineSpacing;
            set { _lineSpacing = value; _dirty = true; }
        }

        public bool RichText
        {
            get => _richText;
            set { _richText = value; _parsedFrom = null; _dirty = true; }
        }

        public bool Precise
        {
            get => _precise;
            set { _precise = value; _dirty = true; }
        }

        /// <summary>The laid-out result of the last rebuild; lays out first if stale.</summary>
        public TextLayoutResult Layout
        {
            get
            {
                RebuildIfNeeded();
                return _layout;
            }
        }

        /// <summary>
        /// Renders with a font loaded from raw bytes instead of the asset
        /// fields; same override semantics as the label's.
        /// </summary>
        public void SetFont(byte[] fontBytes, params byte[][] fallbackBytes)
        {
            _fontBytesOverride = fontBytes;
            _fallbackBytesOverride = fallbackBytes;
            ReleaseFonts();
            _dirty = true;
        }

        /// <summary>Variable-font axes for the main font.</summary>
        public void SetVariations(params FontVariation[] variations)
        {
            _variations.Clear();
            if (variations != null) _variations.AddRange(variations);
            ReleaseFonts();
            _dirty = true;
        }

        /// <summary>Rebuilds the mesh now if anything it depends on changed.</summary>
        public void ForceRebuild()
        {
            _dirty = true;
            RebuildIfNeeded();
        }

        // ------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "OneText Mesh", hideFlags = HideFlags.DontSave };
                _mesh.MarkDynamic();
            }
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            var renderer = GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            s_active.Add(this);
#if UNITY_EDITOR
            EnsureEditorDriver();
#endif
            _dirty = true;
        }

        private void OnDisable()
        {
            s_active.Remove(this);
            if (_mesh != null) _mesh.Clear();
        }

        private void OnDestroy()
        {
            ReleaseFonts();
            _engine?.Dispose();
            _engine = null;
            if (_atlasHeld)
            {
                _atlasHeld = false;
                SharedGlyphAtlas.Release();
            }
            if (_mesh != null)
            {
                if (Application.isPlaying) Destroy(_mesh);
                else DestroyImmediate(_mesh);
                _mesh = null;
            }
        }

        /// <summary>
        /// Copies Project Settings &gt; OneText's new-text defaults onto this
        /// world text: size, auto-size bounds, wrapping and markup. The two
        /// defaults that are about a canvas (escapes and raycast target) have
        /// no meaning out here.
        /// </summary>
        public void ApplyProjectDefaults()
        {
            var defaults = OneTextSettings.ProjectDefaults;
            FontSize = defaults.FontSize;
            AutoSizeMin = defaults.AutoSizeMin;
            AutoSizeMax = defaults.AutoSizeMax;
            Wrap = defaults.Wrap;
            RichText = defaults.RichText;
        }

#if UNITY_EDITOR
        // See OneTextLabel.Reset: the moment the project's answer is wanted.
        private void Reset() => ApplyProjectDefaults();

        private void OnValidate()
        {
            _parsedFrom = null;
            // The inspector writes fields directly, and any of them may have
            // been the font; see OneTextLabel.OnValidate.
            ReleaseFonts();
            _dirty = true;
        }
#endif

        private void OnRectTransformDimensionsChange() => _dirty = true;

        private void LateUpdate() => RebuildIfNeeded();

#if UNITY_EDITOR
        // In edit mode nothing ticks LateUpdate reliably, and the atlas can
        // still move tiles under a built mesh (another label bakes, the atlas
        // compacts). The same editor-update poll AtlasInvalidation uses covers
        // both, driven once for every instance.
        private static bool s_editorDriving;

        private static void EnsureEditorDriver()
        {
            if (s_editorDriving) return;
            s_editorDriving = true;
            UnityEditor.EditorApplication.update += EditorPoll;
        }

        private static void EditorPoll()
        {
            if (Application.isPlaying) return;
            for (int i = s_active.Count - 1; i >= 0; i--)
            {
                var instance = s_active[i];
                if (instance == null) s_active.RemoveAt(i);
                else instance.RebuildIfNeeded();
            }
        }
#endif

        // ------------------------------------------------------------ material

        private static Material s_worldMaterial;

        /// <summary>
        /// One material for every world text, cloned from the shared SDF
        /// material. A clone because the ZTest must differ: the canvas system
        /// drives <c>unity_GUIZTestMode</c> per canvas and nothing drives it
        /// for a MeshRenderer, so this material pins it to LEqual, which is
        /// what world geometry means by depth.
        /// </summary>
        public static Material WorldMaterial
        {
            get
            {
                if (s_worldMaterial != null) return s_worldMaterial;
                var source = SharedGlyphAtlas.Material;
                if (source == null) return null;
                s_worldMaterial = new Material(source)
                {
                    name = "OneText SDF (world)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                s_worldMaterial.SetFloat("unity_GUIZTestMode",
                    (float)CompareFunction.LessEqual);
                return s_worldMaterial;
            }
        }

        /// <summary>
        /// Points the world material at the current atlas textures. Run every
        /// rebuild rather than once: the atlases are recreated by settings
        /// changes and play-mode transitions, and a material holding a dead
        /// texture is invisible text. Re-assigning the same texture is a no-op.
        /// </summary>
        private static void BindWorldMaterial(Material material)
        {
            var atlas = SharedGlyphAtlas.Atlas;
            material.SetTexture("_GlyphTex", atlas.Texture);
            float size = atlas.Texture.width;
            material.SetVector("_GlyphTexelSize",
                new Vector4(1f / size, 1f / atlas.Texture.height, size, atlas.Texture.height));

            if (SharedGlyphAtlas.PreciseAtlasExists)
            {
                var precise = SharedGlyphAtlas.PreciseAtlas;
                material.SetTexture("_MsdfTex", precise.Texture);
                float preciseSize = precise.Texture.width;
                material.SetVector("_MsdfTexelSize", new Vector4(1f / preciseSize,
                    1f / precise.Texture.height, preciseSize, precise.Texture.height));
            }
            if (SharedGlyphAtlas.ColorAtlasExists)
                material.SetTexture("_ColorTex", SharedGlyphAtlas.ColorAtlas.Texture);
        }

        // ------------------------------------------------------------ flush

        /// <summary>
        /// One upload per frame for every world text, without a canvas to
        /// schedule it: outside play mode upload immediately (nothing else
        /// will), in play mode batch to just before rendering.
        /// </summary>
        private static class MeshFlush
        {
            private static bool s_hooked;

            public static void Request()
            {
                if (!Application.isPlaying)
                {
                    FlushNow();
                    return;
                }
                if (s_hooked) return;
                s_hooked = true;
                Application.onBeforeRender += FlushNow;
            }

            private static void FlushNow()
            {
                if (SharedGlyphAtlas.Exists) SharedGlyphAtlas.Atlas.Flush();
                if (SharedGlyphAtlas.PreciseAtlasExists) SharedGlyphAtlas.PreciseAtlas.Flush();
                if (SharedGlyphAtlas.ColorAtlasExists) SharedGlyphAtlas.ColorAtlas.Flush();
            }
        }

        // ------------------------------------------------------------ fonts

        private void ReleaseFonts()
        {
            _fonts?.Dispose();
            _fonts = null;
            foreach (var owned in _ownedFonts) owned.Dispose();
            _ownedFonts.Clear();
        }

        private void BuildFontStack()
        {
            ReleaseFonts();
            _fonts = new FontStack();

            // Length check, not just null: see OneTextLabel.BuildFontStack.
            if (_fontBytesOverride != null && _fontBytesOverride.Length > 0)
            {
                var loaded = FontData.Load(_fontBytesOverride);
                _ownedFonts.Add(loaded);
                if (_variations.Count > 0) loaded.SetVariations(_variations.ToArray());
                _fonts.Add(loaded);

                if (_fallbackBytesOverride != null)
                {
                    foreach (var bytes in _fallbackBytesOverride)
                    {
                        if (bytes == null || bytes.Length == 0) continue;
                        var fallback = FontData.Load(bytes);
                        _ownedFonts.Add(fallback);
                        _fonts.Add(fallback);
                    }
                }
            }
            else
            {
                var settings = OneTextSettings.Instance;
                var main = _font != null ? _font
                    : settings != null ? settings.DefaultFont : null;
                if (main != null)
                {
                    // Same bargain as the canvas label: the designed bold comes
                    // in with its family, and a variable font is left to its own
                    // wght axis.
                    _fonts.Add(main.GetVariant(_variations), main.BoldFace,
                        main.Language, main.LetterSpacingEm);
                }

                foreach (var asset in _fallbackFonts)
                    if (asset != null)
                        _fonts.Add(asset.Font, asset.Language, asset.LetterSpacingEm);

                if (settings != null)
                {
                    foreach (var asset in settings.FallbackFonts)
                        if (asset != null)
                            _fonts.Add(asset.Font, asset.Language, asset.LetterSpacingEm);
                }
            }
        }

        private bool EnsureNativeState()
        {
            // Count, not Primary: an empty stack now answers Primary with a
            // system face, and asking about Primary would settle for it forever
            // — a placeholder somebody drops a .ttf into afterwards has to be
            // picked up, and rebuilding while there is nothing of the project's
            // own is how that happens.
            if (_fonts == null || _fonts.Count == 0 ||
                _fonts.Primary == null || !_fonts.Primary.IsValid)
                BuildFontStack();
            if (_fonts?.Primary == null || !_fonts.Primary.IsValid)
            {
                WarnNoFont(drawing: false);
                return false;
            }
            // Legible, but not in a face this project chose. Worth saying for
            // the same reason the blank case is: nobody assigned it.
            if (_fonts.IsSystemOnly) WarnNoFont(drawing: true);
            else _warnedNoFont = false;

            _engine ??= new TextLayoutEngine();
            if (!_atlasHeld)
            {
                SharedGlyphAtlas.Acquire();
                _atlasHeld = true;
            }
            return WorldMaterial != null;
        }

        // A bool first, so the string MissingFonts keys on is not built on
        // every layout pass of a mesh that has no font. See OneTextLabel.
        [System.NonSerialized] private bool _warnedNoFont;

        private void WarnNoFont(bool drawing)
        {
            if (_warnedNoFont) return;
            _warnedNoFont = true;
            var settings = OneTextSettings.Instance;
            MissingFonts.Warn(this,
                _font != null ? _font : settings != null ? settings.DefaultFont : null,
                drawing);
        }

        private void EnsureDisplayText()
        {
            if (_parsedFrom == _text && _displayText != null && _parsedRich == _richText) return;
            _parsedFrom = _text;
            _parsedRich = _richText;

            if (_richText && RichTextParser.MightHaveMarkup(_text))
            {
                // No style, font or sprite resolvers: this component has no
                // asset lists to resolve against, and an unresolvable tag
                // staying literal is the parser's own policy.
                RichTextParser.Parse(_text, _markup, null, null, null);
                _displayText = _markup.Text;
            }
            else
            {
                _markup.Clear();
                _displayText = _text ?? string.Empty;
            }
        }

        // ------------------------------------------------------------ layout

        private static bool IsKorean(string language) =>
            !string.IsNullOrEmpty(language) &&
            language.StartsWith("ko", System.StringComparison.OrdinalIgnoreCase) &&
            (language.Length == 2 || language[2] == '-');

        /// <summary>
        /// Points to local units, exactly TextMesh Pro's world-text factor.
        /// Not configurable: the constant is the compatibility, and a knob
        /// would make every ported size mean something else per scene.
        /// </summary>
        public const float PointsToUnits = 0.1f;

        private float BaseFontSize => _fontSize;

        /// <summary>The size in force, in points (TMP's unit).</summary>
        private float EffectiveFontSize =>
            _autoSize && _fittedSize > 0f ? _fittedSize : BaseFontSize;

        /// <summary>The same size in local units, which is what layout speaks.</summary>
        private float UnitFontSize => EffectiveFontSize * PointsToUnits;

        private bool IsVertical => _writingMode == TextWritingMode.VerticalRightToLeft;

        private TextLayoutSettings BuildSettings(float maxWidth, float maxHeight, float fontSize) =>
            new TextLayoutSettings
            {
                Fonts = _fonts,
                FontSize = fontSize,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                Alignment = _alignment,
                Wrap = _wrap,
                Overflow = _overflow,
                WritingMode = _writingMode,
                LineSpacing = _lineSpacing,
                BaseDirection = BidiAlgorithm.AutoDirection,
                Language = _language,
                Kinsoku = _kinsoku,
                KoreanWordWrap = IsKorean(_language),
                CjkLatinSpacing = _cjkLatinSpacing,
                PunctuationCompression = _punctuationCompression,
                Spans = ScaledSpans(),
                Alignments = _markup.HasMarkup && _markup.Alignments.Count > 0
                    ? _markup.Alignments
                    : null,
                Rubies = _markup.HasMarkup && _markup.Rubies.Count > 0 ? _markup.Rubies : null,
                RubyScale = _rubyScale,
            };

        private readonly List<TextStyleSpan> _scaledSpans = new List<TextStyleSpan>();

        /// <summary>
        /// The markup's spans with absolute sizes converted from points to
        /// units, so <c>&lt;size=44&gt;</c> means what it meant in TMP.
        /// Multipliers (<c>&lt;size=120%&gt;</c>) and em-relative values are
        /// unitless and pass through untouched.
        /// </summary>
        private IReadOnlyList<TextStyleSpan> ScaledSpans()
        {
            if (!_markup.HasMarkup || _markup.Spans.Count == 0) return null;
            _scaledSpans.Clear();
            foreach (var span in _markup.Spans)
            {
                var style = span.Style;
                if (style.SizeAbsolute > 0f) style.SizeAbsolute *= PointsToUnits;
                _scaledSpans.Add(new TextStyleSpan(span.Start, span.Length, style));
            }
            return _scaledSpans;
        }

        /// <summary>
        /// Same bisection as <c>OneTextLabel.FitFontSize</c>; see there. The
        /// search runs in points (the API's unit); only the measurement
        /// converts.
        /// </summary>
        private float FitFontSize(Rect rect)
        {
            float min = Mathf.Max(0.01f, Mathf.Min(_autoSizeMin, _autoSizeMax));
            float max = Mathf.Max(min, _autoSizeMax);
            if (string.IsNullOrEmpty(_displayText)) return max;

            if (FitsAt(max, rect)) return max;
            if (!FitsAt(min, rect)) return min;

            // World sizes are small numbers (units, not points), so the
            // bracket closes on a fraction of the range rather than half a
            // point, and the answer is not snapped to any grid: ppem
            // quantization already caps what the atlas can be asked for.
            float tolerance = Mathf.Max(0.01f, (max - min) / 256f);
            float fits = min, overflows = max;
            while (overflows - fits > tolerance)
            {
                float mid = (fits + overflows) * 0.5f;
                if (FitsAt(mid, rect)) fits = mid;
                else overflows = mid;
            }
            return fits;
        }

        private bool FitsAt(float size, Rect rect)
        {
            bool vertical = IsVertical;
            float unitSize = size * PointsToUnits;
            var settings = BuildSettings(
                vertical ? 0f : rect.width,
                vertical ? rect.height : 0f,
                unitSize);
            settings.Overflow = TextOverflow.Overflow;
            _engine.Layout(_displayText, settings, _measure);

            float slack = unitSize * 0.01f;
            float inlineBudget = vertical ? rect.height : rect.width;
            float blockBudget = vertical ? rect.width : rect.height;
            return _measure.InlineExtent <= inlineBudget + slack &&
                   _measure.BlockExtent <= blockBudget + slack;
        }

        // ------------------------------------------------------------ rebuild

        private void RebuildIfNeeded()
        {
            if (!isActiveAndEnabled || _mesh == null) return;

            // A version change means the atlas moved tiles whose uv rects this
            // mesh baked; same invalidation a label gets from AtlasInvalidation.
            if (!_dirty && _builtAtlas != null)
            {
                var current = _precise && SharedGlyphAtlas.PreciseAtlasExists
                    ? SharedGlyphAtlas.PreciseAtlas
                    : SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas : null;
                int colorVersion = SharedGlyphAtlas.ColorAtlasExists
                    ? SharedGlyphAtlas.ColorAtlas.Version
                    : 0;
                if (current != _builtAtlas || current == null ||
                    current.Version != _builtAtlasVersion || colorVersion != _builtColorVersion)
                    _dirty = true;
            }
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        private void Rebuild()
        {
            _mesh.Clear();
            if (string.IsNullOrEmpty(_text) || !EnsureNativeState()) return;

            EnsureDisplayText();

            var rect = GetComponent<RectTransform>().rect;
            bool vertical = IsVertical;
            bool budgeted = _overflow != TextOverflow.Overflow;
            float maxWidth = vertical && !budgeted ? 0f : rect.width;
            float maxHeight = vertical ? rect.height : (budgeted ? rect.height : 0f);

            if (_autoSize) _fittedSize = FitFontSize(rect);
            _engine.Layout(_displayText, BuildSettings(maxWidth, maxHeight, UnitFontSize),
                _layout);

            // Block origin: same corner arithmetic as OneTextLabel.EnsureLayout.
            float slack = (vertical ? rect.width : rect.height) - _layout.BlockExtent;
            float inset = _verticalAlignment switch
            {
                VerticalAlignment.Top => 0f,
                VerticalAlignment.Middle => slack * 0.5f,
                _ => slack,
            };
            _blockOrigin = vertical
                ? new Vector2(rect.xMax - inset, rect.yMax)
                : new Vector2(rect.xMin, rect.yMax - inset);

            var atlas = _precise ? SharedGlyphAtlas.PreciseAtlas : SharedGlyphAtlas.Atlas;
            BuildQuads(atlas, vertical);

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _uv0);
            _mesh.SetUVs(1, _uv1);
            _mesh.SetUVs(2, _uv2);
            _mesh.SetUVs(3, _uv3);
            _mesh.SetTriangles(_indices, 0);
            _mesh.RecalculateBounds();

            var renderer = GetComponent<MeshRenderer>();
            var material = WorldMaterial;
            if (renderer.sharedMaterial != material) renderer.sharedMaterial = material;
            BindWorldMaterial(material);

            _builtAtlas = atlas;
            _builtAtlasVersion = atlas.Version;
            _builtColorVersion = SharedGlyphAtlas.ColorAtlasExists
                ? SharedGlyphAtlas.ColorAtlas.Version
                : 0;

            MeshFlush.Request();
        }

        private void BuildQuads(GlyphAtlas atlas, bool vertical)
        {
            _vertices.Clear();
            _colors.Clear();
            _uv0.Clear();
            _uv1.Clear();
            _uv2.Clear();
            _uv3.Clear();
            _indices.Clear();

            var tint = (Color32)_color;

            foreach (var run in _layout.Runs)
            {
                var font = run.Font;
                if (font == null || font.UnitsPerEm == 0) continue;
                // Sprites need a sheet this component does not carry; their
                // advance came from layout, so skipping draws an empty gap
                // rather than shifting the line.
                if (run.Style.IsSprite) continue;

                // run.FontSize is already in units: the settings' size and the
                // scaled spans both went in converted.
                float runSize = run.FontSize > 0f ? run.FontSize : UnitFontSize;
                float scale = runSize / font.UnitsPerEm;

                // Back to points for anything that picks an atlas density.
                // `runSize` is in local units — a tenth of the point size — and
                // every density argument below is a pixels-per-em, so handing
                // them the unit size asks a 55-point mesh for five and a half
                // pixels per em and gets the smallest bucket there is. Every
                // world mesh baked at 24 ppem regardless of its size, which at
                // magnification is the melted look the single-channel field
                // gives and the torn one the multi-channel field gives.
                //
                // Points, not screen pixels, because points are what the
                // convention this component ports from means: a TMP world text
                // at size 36 and a UI label at size 36 ask for the same density
                // here, which is the promise the class comment makes.
                //
                // Times the quality multiplier, because that promise is also
                // the whole of what the point size can say: a label's size is
                // in screen pixels and a mesh's is not, so the multiplier is
                // where a world text gets to say it will be approached. See
                // <see cref="TextQuality"/>.
                float runPixelsPerEm =
                    runSize / PointsToUnits * TextQualityScale.ForWorld(_quality);
                var runColor = run.Style.ResolveColor();
                var color = Multiply(runColor, tint);
                var frame = FrameOf(run, vertical, scale);

                if (ColorGlyphs.IsColorFont(font))
                {
                    EmitColorRun(run, font, runPixelsPerEm, frame, color, atlas);
                    continue;
                }

                float unitsPerTilePixel = font.UnitsPerEm /
                    (float)GlyphAtlas.QuantizePixelsPerEm(runPixelsPerEm);
                float maxClusterUnits = 1000f * unitsPerTilePixel;
                float mergeGapUnits = GlyphClusters.DefaultMergeGapUnits(font);

                if (frame.Vertical && !frame.Rotated)
                {
                    GlyphClusters.SplitUpright(font, _layout.Glyphs, run.GlyphStart,
                        run.GlyphCount, _clusters, _positioned);
                }
                else
                {
                    GlyphClusters.Split(font, _layout.Glyphs, run.GlyphStart, run.GlyphCount,
                        _clusters, _positioned, maxClusterUnits, mergeGapUnits);
                }

                atlas.PrepareClusters(font, runPixelsPerEm, _positioned, _clusters);
                foreach (var cluster in _clusters)
                {
                    var loc = atlas.GetOrAddCluster(font, runPixelsPerEm,
                        _positioned, cluster.Start, cluster.Count, cluster.Hash);
                    if (!loc.HasPixels) continue;

                    frame.Place(cluster.PenX, cluster.PenY, loc.OriginUnits, loc.SizeUnits,
                        out var position, out var size, out float rotation);
                    AddQuad(position, size, rotation, loc.UvRect, loc.Layer, color,
                        _precise ? 2f : 0f);
                }
            }
        }

        /// <param name="pixelsPerEm">
        /// The run's density in points, already converted out of local units by
        /// the caller; everything this method does with it is choose a tile
        /// resolution, and the geometry comes from the frame.
        /// </param>
        private void EmitColorRun(in TextRun run, FontData font, float pixelsPerEm,
            in RunFrame frame, Color32 color, GlyphAtlas sdfAtlas)
        {
            var colorAtlas = SharedGlyphAtlas.ColorAtlas;
            int ppem = GlyphAtlas.QuantizePixelsPerEm(pixelsPerEm);
            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;

            float pen = 0f;
            for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
            {
                var glyph = _layout.Glyphs[i];
                float along = pen + glyph.XOffset;
                pen += glyph.XAdvance;

                // Per glyph, not per run: a colour font carries monochrome
                // glyphs too; see OneTextLabel.EmitColorRun.
                bool followsText = ColorGlyphs.UsesTextColor(font, glyph.GlyphId);
                long key = ColorKey(font, glyph.GlyphId, ppem, followsText ? color : default);

                ColorGlyphAtlas.ColorLocation location = default;
                bool haveColor = colorAtlas.Contains(key);
                if (haveColor) location = colorAtlas.GetOrAdd(key, default);
                else if (ColorGlyphs.TryDecode(font, glyph.GlyphId, pixelsPerUnit, color,
                             out var decoded))
                {
                    location = colorAtlas.GetOrAdd(key, decoded);
                    haveColor = true;
                }

                if (haveColor && location.HasPixels)
                {
                    frame.Place(along, glyph.YOffset, location.OriginUnits, location.SizeUnits,
                        out var position, out var size, out float rotation);
                    AddQuad(position, size, rotation, location.UvRect, location.Layer,
                        color, 1f);
                    continue;
                }

                var sdf = sdfAtlas.GetOrAdd(font, glyph.GlyphId, pixelsPerEm);
                if (!sdf.HasPixels) continue;
                frame.Place(along, glyph.YOffset, sdf.OriginUnits, sdf.SizeUnits,
                    out var sdfPosition, out var sdfSize, out float sdfRotation);
                AddQuad(sdfPosition, sdfSize, sdfRotation, sdf.UvRect, sdf.Layer, color,
                    _precise ? 2f : 0f);
            }
        }

        private static long ColorKey(FontData font, uint glyphId, int ppem, Color32 tint)
        {
            long key = ((long)font.CacheId << 40) ^ ((long)glyphId << 12) ^ ppem;
            if (tint.a != 0 || tint.r != 0 || tint.g != 0 || tint.b != 0)
                key ^= (long)(tint.r << 24 | tint.g << 16 | tint.b << 8 | tint.a) << 8;
            return key;
        }

        // ------------------------------------------------------------ geometry

        /// <summary>Copied from <c>OneTextLabel.RunFrame</c>; the same geometry.</summary>
        private readonly struct RunFrame
        {
            public readonly float BaseX, BaseY;
            public readonly float Scale;
            public readonly bool Vertical, Rotated;

            public RunFrame(float baseX, float baseY, float scale, bool vertical, bool rotated)
            {
                BaseX = baseX;
                BaseY = baseY;
                Scale = scale;
                Vertical = vertical;
                Rotated = rotated;
            }

            public void Place(float along, float across, Vector2 originUnits, Vector2 sizeUnits,
                out Vector2 position, out Vector2 size, out float rotation)
            {
                size = sizeUnits * Scale;
                if (!Vertical)
                {
                    position = new Vector2(BaseX + (along + originUnits.x) * Scale,
                        BaseY + (across + originUnits.y) * Scale);
                    rotation = 0f;
                    return;
                }
                if (!Rotated)
                {
                    position = new Vector2(BaseX + (across + originUnits.x) * Scale,
                        BaseY - along * Scale + originUnits.y * Scale);
                    rotation = 0f;
                    return;
                }
                float gx = (along + originUnits.x) * Scale;
                float gy = (across + originUnits.y) * Scale;
                position = new Vector2(
                    BaseX + gy + (size.y - size.x) * 0.5f,
                    BaseY - gx - (size.x + size.y) * 0.5f);
                rotation = -90f;
            }
        }

        private RunFrame FrameOf(in TextRun run, bool vertical, float scale) => vertical
            ? new RunFrame(
                _blockOrigin.x - run.Baseline - run.CrossAxisBaselineOffset + run.BaselineShift,
                _blockOrigin.y - run.X, scale, true, run.Rotated)
            : new RunFrame(_blockOrigin.x + run.X,
                _blockOrigin.y - run.Baseline + run.BaselineShift, scale, false, false);

        private static Color32 Multiply(Color32 a, Color32 b) => new Color32(
            (byte)(a.r * b.r / 255), (byte)(a.g * b.g / 255),
            (byte)(a.b * b.b / 255), (byte)(a.a * b.a / 255));

        /// <summary>
        /// One tile as four vertices, carrying the channel contract from
        /// <c>OneTextLabel.AddVert</c>: TEXCOORD0 xy uv|layer / z layer|outline
        /// softness / w v-min, TEXCOORD2 x v-max / yz u-bounds / w atlas
        /// discriminator|face dilate. The decoration channels (TEXCOORD1/3) are
        /// written zero: undecorated is a value, not a branch, and this
        /// component draws no decorations.
        ///
        /// Both of those channels carry two bytes rather than one number, and
        /// both got their second byte after this method was written. Neither
        /// spare byte is a zero when it means nothing: the face dilate is
        /// signed and its zero is 128, so a raw zero says "thin this glyph by a
        /// whole reach" and the shader erodes every world glyph to a hairline;
        /// and the layer is the HIGH byte, so a raw slice index lands in the
        /// low one and every tile is read off slice zero. See
        /// <c>OneTextLabel.DecorationChannels.None</c>, which says the same
        /// thing for the canvas side.
        /// </summary>
        private void AddQuad(Vector2 position, Vector2 size, float rotation, Rect uv, int layer,
            Color32 color, float atlasSelector)
        {
            int start = _vertices.Count;

            if (rotation == 0f)
            {
                _vertices.Add(new Vector3(position.x, position.y));
                _vertices.Add(new Vector3(position.x, position.y + size.y));
                _vertices.Add(new Vector3(position.x + size.x, position.y + size.y));
                _vertices.Add(new Vector3(position.x + size.x, position.y));
            }
            else
            {
                float radians = rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
                var centre = position + size * 0.5f;
                var half = size * 0.5f;

                Vector3 Corner(float sx, float sy)
                {
                    float x = sx * half.x, y = sy * half.y;
                    return new Vector3(centre.x + x * cos - y * sin, centre.y + x * sin + y * cos);
                }

                _vertices.Add(Corner(-1f, -1f));
                _vertices.Add(Corner(-1f, 1f));
                _vertices.Add(Corner(1f, 1f));
                _vertices.Add(Corner(1f, -1f));
            }

            // 128 in the low byte is a face dilate of exactly zero; 0 there is
            // a whole reach of erosion.
            float kind = TextDecoration.Pack(
                (byte)atlasSelector, TextDecoration.QuantizeSigned(0f, 1f));
            // The slice index is the high byte, and the low one is the outline
            // softness this component never sets.
            float slice = TextDecoration.Pack((byte)layer, 0);

            var vmax = new Vector4(uv.yMax, uv.xMin, uv.xMax, kind);
            _uv0.Add(new Vector4(uv.xMin, uv.yMin, slice, uv.yMin));
            _uv0.Add(new Vector4(uv.xMin, uv.yMax, slice, uv.yMin));
            _uv0.Add(new Vector4(uv.xMax, uv.yMax, slice, uv.yMin));
            _uv0.Add(new Vector4(uv.xMax, uv.yMin, slice, uv.yMin));
            for (int i = 0; i < 4; i++)
            {
                _colors.Add(color);
                _uv1.Add(Vector4.zero);
                _uv2.Add(vmax);
                _uv3.Add(Vector4.zero);
            }

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);
        }

        // ====================================================================
        // TMP parity — begin
        //
        // The migration turns TextMesh Pro's world component into this one, so
        // the same call sites arrive here that arrive at OneTextLabel, and the
        // aliases exist for the same reason: a project with `label.text = …`
        // written a hundred times over should still compile the afternoon it
        // swaps packages. Each forwards to the PascalCase property that is the
        // real API, holds no state, and stays out of IntelliSense so new code
        // reads OneText's own names. The long version of the argument is at the
        // bottom of OneTextLabel.
        //
        // Shorter here than there, and the missing ones are all one missing
        // thing: alignment, lineSpacing, textWrappingMode, enableWordWrapping
        // and overflowMode convert between TMP's units and OneText's, that
        // arithmetic lives in OneText.UGUI.TmpCompat so the runtime aliases and
        // the Onboarding migration cannot answer differently, and this assembly
        // does not reference uGUI — deliberately, since world text should not
        // need a Canvas package to exist. Writing a second copy of the
        // conversion here would buy five members and cost the guarantee that
        // there is only one answer, so they wait for TmpCompat to move down
        // into the core assembly, where both callers can reach it.
        // ====================================================================

        /// <summary>TMP-migration parity alias for <see cref="Text"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public string text
        {
            get => Text;
            set => Text = value;
        }

        /// <summary>TMP-migration parity alias for <see cref="FontSize"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float fontSize
        {
            get => FontSize;
            set => FontSize = value;
        }

        /// <summary>TMP-migration parity alias for <see cref="Color"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Color color
        {
            get => Color;
            set => Color = value;
        }

        /// <summary>
        /// TMP-migration parity alias for the alpha channel of
        /// <see cref="Color"/>: reads it, and writes the colour back, which is
        /// what runs the invalidation.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float alpha
        {
            get => Color.a;
            set
            {
                var tint = Color;
                tint.a = value;
                Color = tint;
            }
        }

        /// <summary>TMP-migration parity alias for <see cref="RichText"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool richText
        {
            get => RichText;
            set => RichText = value;
        }

        /// <summary>TMP-migration parity alias for <see cref="AutoSize"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool enableAutoSizing
        {
            get => AutoSize;
            set => AutoSize = value;
        }

        /// <summary>TMP-migration parity alias for <see cref="AutoSizeMin"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float fontSizeMin
        {
            get => AutoSizeMin;
            set => AutoSizeMin = value;
        }

        /// <summary>TMP-migration parity alias for <see cref="AutoSizeMax"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float fontSizeMax
        {
            get => AutoSizeMax;
            set => AutoSizeMax = value;
        }

        /// <summary>
        /// TMP-migration parity alias for <see cref="ForceRebuild"/>: lay the
        /// text out and rebuild the mesh now, before this returns. Callers read
        /// the result on the next line, which is the whole reason they call it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ForceMeshUpdate() => ForceRebuild();

        /// <summary>
        /// TMP-migration parity for <c>ForceMeshUpdate(bool, bool)</c>.
        /// <paramref name="ignoreActiveState"/> is accepted and ignored, since
        /// the rebuild below never asks whether the component is enabled;
        /// <paramref name="forceTextReparsing"/> parses the markup again even
        /// when the string did not change.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ForceMeshUpdate(bool ignoreActiveState, bool forceTextReparsing = false)
        {
            if (forceTextReparsing) _parsedFrom = null;
            ForceRebuild();
        }

        /// <summary>
        /// TMP-migration parity for <c>GetParsedText</c>: the text as laid out,
        /// with the markup taken out of it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public string GetParsedText()
        {
            EnsureDisplayText();
            return _displayText;
        }

        /// <summary>TMP-migration parity alias for assigning <see cref="Text"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(string value) => Text = value;

        /// <summary>
        /// TMP-migration parity alias for assigning <see cref="Text"/>.
        /// <paramref name="syncTextInputBox"/> is TMP's flag for keeping the
        /// inspector's text box in step with a runtime assignment; the
        /// inspector here reads the same serialized field either way.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(string value, bool syncTextInputBox) => Text = value;

        /// <summary>
        /// TMP-migration parity alias for assigning <see cref="Text"/> from a
        /// builder. It allocates, where TMP's overload existed not to: OneText's
        /// text is a string. Same result, not the same garbage. The numeric
        /// overloads are deliberately absent — their <c>{0:2}</c> is TMP's own
        /// format syntax and not <c>string.Format</c>'s, so forwarding would
        /// print the wrong number rather than fail to compile.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(System.Text.StringBuilder value) =>
            Text = value != null ? value.ToString() : string.Empty;

        // TMP parity — end
    }
}
