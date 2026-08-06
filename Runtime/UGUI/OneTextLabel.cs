using System.Collections.Generic;
using OneText.Unicode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// SDF-rendered shaped text inside a uGUI canvas: full layout (wrapping,
    /// alignment, mixed direction, font fallback) on top of the seam-free
    /// cluster rasterizer. Implements <see cref="ILayoutElement"/>, so layout
    /// groups and <c>ContentSizeFitter</c> work the way they do for any other
    /// graphic, and exposes hit-testing so carets, selections and clickable
    /// <c>&lt;link=id&gt;</c> ranges all line up with what is drawn.
    /// </summary>
    [AddComponentMenu("OneText/OneText Label")]
    public sealed class OneTextLabel : MaskableGraphic, ILayoutElement, IPointerClickHandler,
        StyleInvalidation.IStyleUser
    {
        [Tooltip("Base style asset. The label stores the reference, not a copy; editing the " +
                 "style updates every label using it, in the editor and at runtime.")]
        [SerializeField] private OneTextStyle _style;

        [Tooltip("Styles <style=name> may reference. The asset's own name is the name markup uses.")]
        [SerializeField] private List<OneTextStyle> _namedStyles = new List<OneTextStyle>();

        [Tooltip("Fonts <font=name> may reference, by asset name.")]
        [SerializeField] private List<OneFontAsset> _namedFonts = new List<OneFontAsset>();

        [Tooltip("Sprites <sprite=index> draws, in the same atlas and draw call as the text.")]
        [SerializeField] private OneTextSpriteSheet _sprites;

        [Tooltip("BCP 47 language tag: ja, ko, zh-Hans. Drives OpenType locl, keys font " +
                 "fallback so Han unification is not a lottery, and selects line-breaking " +
                 "tailorings. Empty means the project default.")]
        [SerializeField] private string _language = "";

        [Tooltip("Kinsoku severity: which characters may not start or end a line.")]
        [SerializeField] private Unicode.AsianTypography.Kinsoku _kinsoku =
            Unicode.AsianTypography.Kinsoku.Off;

        [Tooltip("Quarter-em gap between Han/Kana and Latin, as East Asian layout specs require.")]
        [SerializeField] private bool _cjkLatinSpacing;

        [Tooltip("Compress full-width punctuation (約物詰め).")]
        [SerializeField] private bool _punctuationCompression;

        [Tooltip("Ruby (furigana) size, as a fraction of the text it annotates. Half is what " +
                 "Japanese typesetting specs ask for and what this defaults to.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float _rubyScale = RubyPlacement.DefaultScale;

        [Tooltip("Font asset to render with. Create one from any .ttf/.otf: " +
                 "right-click the font file, OneText > Create Font Asset.")]
        [SerializeField] private OneFontAsset _font;

        [Tooltip("Extra fonts for characters the main font lacks, tried before the project fallbacks.")]
        [SerializeField] private List<OneFontAsset> _fallbackFonts = new List<OneFontAsset>();

        [TextArea]
        [SerializeField] private string _text = "مرحبا بالعالم";

        [SerializeField] private float _fontSize = 64f;
        [SerializeField] private TextAlignment _alignment = TextAlignment.Start;
        [SerializeField] private VerticalAlignment _verticalAlignment = VerticalAlignment.Middle;

        [Tooltip("Horizontal, or 縦書き: characters top to bottom, columns right to left. " +
                 "In a vertical label the two alignments swap axes: Horizontal places text " +
                 "along its column, Vertical places the columns across the box.")]
        [SerializeField] private TextWritingMode _writingMode = TextWritingMode.Horizontal;
        [SerializeField] private TextWrap _wrap = TextWrap.Wrap;
        [SerializeField] private TextOverflow _overflow = TextOverflow.Overflow;
        [SerializeField] private float _lineSpacing = 1f;

        [Tooltip("Parse rich-text markup: <b> <i> <u> <s> <color> <size> <mark> <nobr> " +
                 "<align> <voffset> <cspace> <link> <wait> <ruby>. Malformed tags stay as " +
                 "literal text.")]
        [SerializeField] private bool _richText = true;

        [Tooltip("Precise (MSDF): multi-channel distance field. Use for large text or " +
                 "sharp corners/curves; costs more atlas memory (four bytes a texel instead " +
                 "of one, in an atlas of its own). Off, the label renders through the " +
                 "ordinary single-channel SDF, which is right for body text.")]
        [SerializeField] private bool _precise;

        [SerializeField] private UnityEvent<string> _linkClicked = new UnityEvent<string>();

        private byte[] _fontBytesOverride;
        private byte[][] _fallbackBytesOverride;
        private FontStack _fonts;
        private readonly List<FontData> _ownedFonts = new List<FontData>();
        private TextLayoutEngine _engine;
        private bool _atlasHeld; // this label's reference to the shared atlas
        private readonly List<FontVariation> _variations = new List<FontVariation>();
        private readonly List<FontVariation> _styleVariations = new List<FontVariation>();
        private readonly TextLayoutResult _layout = new TextLayoutResult();
        private readonly TextLayoutResult _measure = new TextLayoutResult();
        private readonly List<TextLink> _links = new List<TextLink>();
        private readonly RichTextResult _markup = new RichTextResult();
        private string _displayText;
        private string _parsedFrom;
        private bool _parsedRich;

        /// <summary>
        /// Everything the laid-out result depends on. Compared by value, so a
        /// rebuild that changes none of it reuses the layout.
        /// </summary>
        private readonly struct LayoutKey : System.IEquatable<LayoutKey>
        {
            private readonly string _text;
            private readonly float _width, _height, _size, _lineSpacing;
            private readonly TextAlignment _alignment;
            private readonly TextWrap _wrap;
            private readonly TextOverflow _overflow;
            private readonly int _generation;

            public LayoutKey(string text, float width, float height, float size, float lineSpacing,
                TextAlignment alignment, TextWrap wrap, TextOverflow overflow, int generation)
            {
                _text = text;
                _width = width;
                _height = height;
                _size = size;
                _lineSpacing = lineSpacing;
                _alignment = alignment;
                _wrap = wrap;
                _overflow = overflow;
                _generation = generation;
            }

            public bool Equals(LayoutKey other) =>
                ReferenceEquals(_text, other._text) &&
                _width.Equals(other._width) && _height.Equals(other._height) &&
                _size.Equals(other._size) && _lineSpacing.Equals(other._lineSpacing) &&
                _alignment == other._alignment && _wrap == other._wrap &&
                _overflow == other._overflow && _generation == other._generation;
        }

        private LayoutKey _layoutKey;
        private bool _layoutValid;

        // The quad cache's own validity: the layout it was built from, and the
        // atlas version whose uv rects it baked.
        // Everything the cached quads baked into themselves: which layout they
        // came from, and the version of every atlas whose uv rects they hold.
        // A colour tile's rect goes stale on a colour-atlas eviction exactly as
        // a glyph's does on an SDF one, and a sprite sheet swap changes the
        // pictures without changing anything else.
        private bool _quadsValid;
        private int _quadsLayoutGeneration = -1;
        private int _quadsAtlasVersion = -1;
        private int _quadsColorVersion = -1;
        private int _quadsSpriteVersion = -1;

        // Bumped by anything the key cannot see: a font change, a style edit, a
        // markup re-parse. Cheaper and safer than trying to hash the world.
        private int _layoutGeneration;

        /// <summary>
        /// How many times this label has actually laid text out. Exposed
        /// because "animating does not re-lay out" is a claim about work not
        /// done, and a test that compares results cannot tell work-not-done
        /// from work-redone-identically.
        /// </summary>
        public int LayoutRuns => _layoutRuns;

        private int _layoutRuns;

        /// <summary>Same, for the quad build: the other half of a rebuild.</summary>
        public int QuadBuilds => _quadBuilds;

        private int _quadBuilds;

        // Local-space anchor of the layout origin (top-left of the text block).
        private Vector2 _blockOrigin;
        private Vector2 _scrollOffset;

        public string Text
        {
            get => _text;
            set { _text = value; InvalidateText(); }
        }

        public float FontSize
        {
            get => _fontSize;
            set { _fontSize = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        public TextAlignment Alignment
        {
            get => _alignment;
            set { _alignment = value; SetVerticesDirty(); }
        }

        public VerticalAlignment VerticalAlignment
        {
            get => _verticalAlignment;
            set { _verticalAlignment = value; SetVerticesDirty(); }
        }

        /// <summary>
        /// Horizontal (the default) or 縦書き.
        ///
        /// Turning it on rotates the frame the whole label is laid out in, and
        /// the two alignments rotate with it: <see cref="Alignment"/> places
        /// text along its column (Left is the top of the column, Right the
        /// bottom) and <see cref="VerticalAlignment"/> places the stack of
        /// columns across the box, Top against the right edge, because that is
        /// where a right-to-left column starts.
        /// </summary>
        public TextWritingMode WritingMode
        {
            get => _writingMode;
            set
            {
                if (_writingMode == value) return;
                _writingMode = value;
                _layoutGeneration++;
                SetVerticesDirty();
                SetLayoutDirty();
            }
        }

        public TextWrap Wrap
        {
            get => _wrap;
            set { _wrap = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        public TextOverflow Overflow
        {
            get => _overflow;
            set { _overflow = value; SetVerticesDirty(); }
        }

        public float LineSpacing
        {
            get => _lineSpacing;
            set { _lineSpacing = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        /// <summary>
        /// Parse rich-text markup. An input field turns this off on the label
        /// it owns: text the user typed is text, and a name with an angle
        /// bracket in it is not a tag.
        /// </summary>
        public bool RichText
        {
            get => _richText;
            set
            {
                if (_richText == value) return;
                _richText = value;
                InvalidateText();
            }
        }

        /// <summary>
        /// Render this label through a multi-channel distance field (MSDF)
        /// rather than the single-channel one.
        ///
        /// Off by default, and meant to stay off for most text. What it buys is
        /// corners: a single channel stores a cone at every corner and the
        /// bilinear sampler rounds it, which is invisible on body text and
        /// obvious on a display line or a logotype. What it costs is a second
        /// atlas at four bytes a texel instead of one, and a rasterization that
        /// resolves three fields instead of one, so it is a per-label opt-in,
        /// not a project setting.
        ///
        /// The tiles are cached separately from the ordinary ones, so a glyph
        /// drawn both ways is baked both ways; two labels sharing this setting
        /// share their tiles as usual.
        /// </summary>
        public bool Precise
        {
            get => _precise;
            set
            {
                if (_precise == value) return;
                _precise = value;
                // The cached quads hold uv rects from the other atlas, and
                // nothing about the layout changed, so this is a quad rebuild
                // and not a re-layout.
                _quadsValid = false;
                SetVerticesDirty();
            }
        }

        /// <summary>Shifts the drawn text inside the box (used for scrolling an input field).</summary>
        public Vector2 ScrollOffset
        {
            get => _scrollOffset;
            set { _scrollOffset = value; SetVerticesDirty(); }
        }

        /// <summary>The text actually laid out, with link tags removed.</summary>
        public string DisplayText
        {
            get
            {
                EnsureDisplayText();
                return _displayText;
            }
        }

        /// <summary>Clickable ranges parsed from the markup, in display-text indices.</summary>
        public IReadOnlyList<TextLink> Links
        {
            get
            {
                EnsureDisplayText();
                return _links;
            }
        }

        /// <summary>Raised when a <c>&lt;link=id&gt;</c> range is clicked.</summary>
        public UnityEvent<string> LinkClicked => _linkClicked;

        /// <summary>The most recent layout: lines, runs and glyph positions.</summary>
        public TextLayoutResult LayoutResult => _layout;

        /// <summary>
        /// The fallback chain this label resolved, once it has laid anything
        /// out. For the development-build overlay, which exists to answer "why
        /// is this label drawing boxes on the device and not in the editor",
        /// a question about which font actually got used.
        /// </summary>
        internal FontStack ResolvedFonts => _fonts;

        /// <summary>Set the main font from raw TTF/OTF bytes (overrides the font asset).</summary>
        public void SetFont(byte[] fontBytes, params byte[][] fallbackBytes)
        {
            _fontBytesOverride = fontBytes;
            _fallbackBytesOverride = fallbackBytes != null && fallbackBytes.Length > 0 ? fallbackBytes : null;
            ReleaseFonts();
            SetVerticesDirty();
            SetLayoutDirty();
        }

        /// <summary>
        /// Sets variable-font axis values on the main font, e.g.
        /// <c>SetVariations(new FontVariation("wght", 700f))</c>.
        /// </summary>
        public void SetVariations(params FontVariation[] variations)
        {
            _variations.Clear();
            if (variations != null) _variations.AddRange(variations);
            // The instance is picked up from the asset's variant cache, so two
            // labels at the same weight still share one set of atlas entries.
            ReleaseFonts();
            SetVerticesDirty();
            SetLayoutDirty();
        }

        /// <summary>The font asset this label renders with.</summary>
        public OneFontAsset Font
        {
            get => _font;
            set
            {
                _font = value;
                ReleaseFonts();
                if (isActiveAndEnabled) EnsureMaterial();
                SetVerticesDirty();
                SetLayoutDirty();
            }
        }

        /// <summary>Variation axes the main font exposes (empty for static fonts).</summary>
        public FontAxis[] GetVariationAxes() =>
            EnsureNativeState() && _fonts.Primary != null
                ? _fonts.Primary.GetVariationAxes()
                : System.Array.Empty<FontAxis>();

        protected override void OnEnable()
        {
            base.OnEnable();
            // Tiles move when the atlas compacts or evicts; a baked mesh holds
            // their UVs and has to be told.
            AtlasInvalidation.Register(this);
            // And a style is a reference, so editing the asset has to reach the
            // labels pointing at it.
            StyleInvalidation.Register(this);
            EnsureMaterial();
            // A label that comes on screen with a typewriter configured types
            // itself. Without this a prefab whose text was authored in the
            // inspector never starts: nothing invalidated the text, so nothing
            // rewound the reveal, and the whole line is simply there. It also
            // makes a pooled dialogue box retype when it is shown again, which
            // is what pooling one is for.
            if (Application.isPlaying && _charactersPerSecond > 0f) RestartReveal();
        }

        protected override void OnDisable()
        {
            AtlasInvalidation.Unregister(this);
            StyleInvalidation.Unregister(this);
            base.OnDisable();
        }

        // --------------------------------------------------------- named styles

        /// <summary>The base style this label renders with, or null.</summary>
        public OneTextStyle Style
        {
            get => _style;
            set
            {
                _style = value;
                OnStyleChanged();
            }
        }

        /// <summary>Styles <c>&lt;style=name&gt;</c> can reference.</summary>
        public IList<OneTextStyle> NamedStyles => _namedStyles;

        bool StyleInvalidation.IStyleUser.UsesStyle(OneTextStyle style)
        {
            if (style == null) return false;
            if (References(_style, style)) return true;
            foreach (var named in _namedStyles)
                if (References(named, style)) return true;
            return false;
        }

        // A style that extends the changed one is affected too; that is what
        // one level of inheritance is for.
        private static bool References(OneTextStyle held, OneTextStyle changed) =>
            held != null && (held == changed || held.Extends == changed);

        void StyleInvalidation.IStyleUser.OnStyleChanged() => OnStyleChanged();

        private void OnStyleChanged()
        {
            // A style can change the font, so the stack is rebuilt rather than
            // reused: the alternative is a label rendering last frame's face.
            ReleaseFonts();
            _parsedFrom = null;
            _layoutGeneration++;
            SetVerticesDirty();
            SetLayoutDirty();
        }

        /// <summary>Index of a named style by asset name, or -1.</summary>
        private int NamedStyleIndex(string name)
        {
            for (int i = 0; i < _namedStyles.Count; i++)
                if (_namedStyles[i] != null && _namedStyles[i].name == name) return i;
            return -1;
        }

        private int NamedFontIndex(string name)
        {
            for (int i = 0; i < _namedFonts.Count; i++)
                if (_namedFonts[i] != null && _namedFonts[i].name == name) return i;
            return -1;
        }

        private FontData NamedFont(int index) =>
            index >= 0 && index < _namedFonts.Count && _namedFonts[index] != null
                ? _namedFonts[index].Font
                : null;

        private TextStyle ApplyNamedStyle(int index, TextStyle style)
        {
            if (index < 0 || index >= _namedStyles.Count) return style;
            var named = _namedStyles[index];
            return named != null ? named.Apply(style) : style;
        }

        /// <summary>Sprites <c>&lt;sprite=…&gt;</c> draws.</summary>
        public OneTextSpriteSheet Sprites
        {
            get => _sprites;
            set
            {
                _sprites = value;
                InvalidateText();
            }
        }

        private float SpriteAspect(int index) => _sprites != null ? _sprites.AspectOf(index) : 1f;

        /// <summary>BCP 47 language tag for this label.</summary>
        public string Language
        {
            get => _language;
            set
            {
                if (_language == value) return;
                _language = value;
                // The locale changes shaping, fallback and wrapping, so this is
                // a re-layout rather than a redraw.
                ReleaseFonts();
                InvalidateText();
            }
        }

        /// <summary>Kinsoku severity, if this label is Japanese or Chinese.</summary>
        public Unicode.AsianTypography.Kinsoku Kinsoku
        {
            get => _kinsoku;
            set { _kinsoku = value; InvalidateText(); }
        }

        /// <summary>The quarter-em gap between Han/Kana and Latin runs.</summary>
        public bool CjkLatinSpacing
        {
            get => _cjkLatinSpacing;
            set { _cjkLatinSpacing = value; InvalidateText(); }
        }

        /// <summary>Compress full-width punctuation (約物詰め).</summary>
        public bool PunctuationCompression
        {
            get => _punctuationCompression;
            set { _punctuationCompression = value; InvalidateText(); }
        }

        /// <summary>
        /// Size of a <c>&lt;ruby=…&gt;</c> annotation, as a fraction of the
        /// text it annotates.
        /// </summary>
        public float RubyScale
        {
            get => _rubyScale;
            set { _rubyScale = RubyPlacement.ResolveScale(value); InvalidateText(); }
        }

        // A subtag boundary, not a prefix: "kok" is Konkani, and giving it
        // Korean word wrap would be a silent wrong answer in a language even
        // further from anyone's notice than Korean.
        private static bool IsKorean(string language) =>
            !string.IsNullOrEmpty(language) &&
            language.StartsWith("ko", System.StringComparison.OrdinalIgnoreCase) &&
            (language.Length == 2 || language[2] == '-');

        private int NamedSpriteIndex(string spriteName) =>
            _sprites != null ? _sprites.IndexOf(spriteName) : -1;

        /// <summary>The label's own size, with its base style applied.</summary>
        private float EffectiveFontSize =>
            _style != null && _style.Sets(OneTextStyle.Fields.Size) ? _style.FontSize : _fontSize;

        private float EffectiveLineSpacing =>
            _style != null && _style.Sets(OneTextStyle.Fields.LineSpacing)
                ? _style.LineSpacing
                : _lineSpacing;

        private Color EffectiveColor =>
            _style != null && _style.Sets(OneTextStyle.Fields.Color) ? _style.Color * color : color;

        protected override void OnDestroy()
        {
            AtlasInvalidation.Unregister(this);
            StyleInvalidation.Unregister(this);
            ReleaseFonts();
            _engine?.Dispose();
            _engine = null;
            if (_atlasHeld)
            {
                _atlasHeld = false;
                SharedGlyphAtlas.Release();
            }
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            _parsedFrom = null;
            // The inspector writes serialized fields directly, never through
            // the properties that invalidate, so any of them may just have
            // been the font or the fallback list. Rebuilding the stack here is
            // what makes "add an Arabic fallback" take effect on the label you
            // are looking at, instead of on the next domain reload.
            ReleaseFonts();
        }
#endif

        private void InvalidateText()
        {
            _parsedFrom = null;
            _layoutGeneration++;
            _animatorBuilt = false;
            _unitsValid = false;
            _revealCompleteFired = false;
            // Only a running typewriter rewinds on an edit. A label nobody asked
            // to type must not be blanked by one, and a label in the Scene view
            // must not be blanked at all; there is no clock out there to
            // un-blank it with.
            if (_charactersPerSecond > 0f && Application.isPlaying) RestartReveal();
            SetVerticesDirty();
            SetLayoutDirty();
        }

        private void EnsureDisplayText()
        {
            if (_parsedFrom == _text && _displayText != null && _parsedRich == _richText) return;
            _parsedFrom = _text;
            _parsedRich = _richText;

            if (_richText && RichTextParser.MightHaveMarkup(_text))
            {
                RichTextParser.Parse(_text, _markup, NamedStyleIndex, NamedFontIndex, NamedSpriteIndex);
                _displayText = _markup.Text;
                _links.Clear();
                _links.AddRange(_markup.Links);
            }
            else
            {
                _markup.Clear();
                _links.Clear();
                _displayText = _text ?? string.Empty;
            }
        }

        /// <summary>
        /// The effects markup asked for, rebuilt when the text is re-parsed.
        ///
        /// Effect spans are addressed by grapheme cluster, which is why this
        /// waits for a layout: markup gives text indices and only the engine
        /// knows where the cluster boundaries fell.
        /// </summary>
        private void EnsureAnimator()
        {
            if (_animatorBuilt) return;
            EnsureDisplayText();
            if (!_markup.HasMarkup || _markup.Effects.Count == 0)
            {
                _animatorBuilt = true;
                _animator.Clear();
                return;
            }

            // Cluster indices come from the layout, so there has to be one.
            if (!EnsureNativeState()) return;
            EnsureLayout();
            _animatorBuilt = true;
            _animator.Clear();

            foreach (var (name, parameters, start, end) in _markup.Effects)
            {
                var effect = BuiltInEffects.Create(name, parameters);
                if (effect == null) continue;
                _animator.Add(new TextEffectSpan(effect, parameters,
                    _layout.GraphemeAt(start),
                    _layout.GraphemeAt(Mathf.Max(start, end - 1))));
            }
        }

        /// <summary>True if this label has any animation left to run.</summary>
        public bool IsAnimating
        {
            get
            {
                if (!_animate) return false;
                EnsureAnimator();
                return HasAnimationWorkLeft();
            }
        }

        /// <summary>
        /// True while advancing the clock could still change a pixel.
        ///
        /// Asking whether spans exist is the wrong question and the answer is
        /// always yes: a <c>&lt;pop for=0.3&gt;</c> damage number keeps its span
        /// until the text is rebuilt, so it would go on paying a full mesh
        /// re-emit every frame, for ever, to redraw exactly the pixels already
        /// on screen. What matters is whether any effect is still moving,
        /// which for a <c>for=</c> span is its envelope, and for an appearance
        /// effect with no <c>for=</c> is the last reveal stamp plus the settle
        /// the effect declares. Only the ambient effects are genuinely endless.
        ///
        /// Recomputed every call rather than latched into a flag, because every
        /// way work comes back has to restart the tick by itself: new text, new
        /// markup, a pooled label reused, or a script scrubbing AnimationTime
        /// backwards over an effect that had finished. A latched flag turns this
        /// into an effect that silently never plays again.
        /// </summary>
        private bool HasAnimationWorkLeft()
        {
            float endsAt = _animator.WorkEndsAt;
            if (float.IsNegativeInfinity(endsAt)) return false; // nothing to animate
            if (_animationTime < endsAt) return true;

            // Everything the animator knows about has elapsed, but a typewriter
            // mid-reveal is still work: appearance effects are keyed off each
            // cluster's own reveal stamp, and those stamps are read off this
            // clock as the reveal passes them; a cluster whose turn has not
            // come has not played. The animator says the same thing from the
            // stamps it has been given; this says it from the reveal the label
            // is about to draw, and so covers the frames before that draw.
            return _maxVisibleGraphemes >= 0 && _maxVisibleGraphemes < _layout.GraphemeCount;
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            // The typewriter has its own gate and its own early exit. It is not
            // under Animate: a project that turned Animate off to drive
            // AnimationTime by hand still wants its dialogue to type, and the
            // commonest typewriter of all is a label with no effect tags in it.
            if (_charactersPerSecond > 0f) AdvanceReveal(Time.deltaTime);
            if (!_animate) return;
            EnsureAnimator();
            // Only labels with a time-varying effect tick. A finished typewriter
            // with no effects, or a label whose only effect has run to
            // completion, would otherwise dirty its vertices every frame forever
            // and do nothing with them.
            if (!HasAnimationWorkLeft()) return;

            // Animation advances on the label's own clock so a paused game
            // pauses its text; anything wanting another clock turns Animate off
            // and drives AnimationTime itself.
            AnimationTime = _animationTime + Time.deltaTime;
        }

        /// <summary>Style spans over <see cref="DisplayText"/>, or null for plain text.</summary>
        private IReadOnlyList<TextStyleSpan> Spans
        {
            get
            {
                EnsureDisplayText();
                return _markup.HasMarkup ? _markup.Spans : null;
            }
        }

        private void ReleaseFonts()
        {
            _layoutGeneration++;
            _fonts?.Dispose();
            _fonts = null;
            // Only fonts this label loaded from raw bytes are ours to destroy;
            // asset-owned faces are shared with every other label.
            foreach (var owned in _ownedFonts) owned.Dispose();
            _ownedFonts.Clear();
        }

        private bool EnsureNativeState()
        {
            if (_fonts == null || _fonts.Primary == null || !_fonts.Primary.IsValid)
                BuildFontStack();
            if (_fonts?.Primary == null || !_fonts.Primary.IsValid) return false;

            _engine ??= new TextLayoutEngine();
            if (!_atlasHeld)
            {
                SharedGlyphAtlas.Acquire();
                _atlasHeld = true;
            }

            return EnsureMaterial();
        }

        /// <summary>
        /// Points this label at the shared material and asks its canvas for the
        /// vertex channels the SDF shader reads.
        ///
        /// Assigning a material marks the graphic dirty, and uGUI logs an error
        /// for anything that asks to be rebuilt while it is already rebuilding,
        /// so this runs when the label enables or changes canvas, and the
        /// call from inside mesh generation only covers the case where the
        /// material is still missing (it cannot dirty what has not drawn yet).
        /// </summary>
        private bool EnsureMaterial()
        {
            // One shared material for every label: uGUI batches by material, so
            // a per-label instance would mean a draw call per label.
            var shared = SharedGlyphAtlas.Material;
            if (shared == null) return false;
            if (material != shared) material = shared;

            // Joint strips carry per-vertex data in TEXCOORD1/2, which the
            // canvas strips out unless explicitly enabled.
            var owningCanvas = canvas;
            if (owningCanvas != null)
            {
                owningCanvas.additionalShaderChannels |=
                    AdditionalCanvasShaderChannels.TexCoord1 |
                    AdditionalCanvasShaderChannels.TexCoord2 |
                    AdditionalCanvasShaderChannels.TexCoord3;
            }
            return true;
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            if (isActiveAndEnabled) EnsureMaterial();
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            if (isActiveAndEnabled) EnsureMaterial();
        }

        /// <summary>
        /// Assembles the fallback chain: this label's font, its own extra
        /// fonts, then the project-wide fallbacks from
        /// <see cref="OneTextSettings"/>.
        /// </summary>
        private void BuildFontStack()
        {
            ReleaseFonts();
            _fonts = new FontStack();

            // Length check, not just null: a domain reload serializes private
            // fields too, and Unity's serializer resurrects a null array as an
            // empty one, so after the first script recompile every label that
            // never called SetFont holds byte[0] here, which must mean "no
            // override", not "load this".
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
                // A style's font wins over the label's own field: that is what
                // makes swapping a style swap the typeface everywhere, which is
                // the localisation payoff M10 leans on.
                var styleFont = _style != null && _style.Sets(OneTextStyle.Fields.Font)
                    ? _style.Font
                    : null;
                var main = styleFont != null ? styleFont
                    : _font != null ? _font
                    : settings != null ? settings.DefaultFont : null;
                if (main != null)
                {
                    // One precedence story throughout: a style wins for
                    // everything it sets, and the label's own fields are the
                    // fallback for what it does not. The label cannot tell a
                    // deliberate 32 from a default 32, so "the label is more
                    // specific" is not a rule it can actually implement, and
                    // having font follow one rule while axes followed the
                    // opposite was the worst of both.
                    var axes = _variations;
                    if (_style != null && _style.Sets(OneTextStyle.Fields.Variations))
                    {
                        _styleVariations.Clear();
                        _styleVariations.AddRange(_style.Variations);
                        axes = _styleVariations;
                    }
                    _fonts.Add(main.GetVariant(axes), main.Language);
                }

                foreach (var asset in _fallbackFonts)
                    if (asset != null) _fonts.Add(asset.Font, asset.Language);

                if (settings != null)
                {
                    foreach (var asset in settings.FallbackFonts)
                        if (asset != null) _fonts.Add(asset.Font, asset.Language);
                }
            }
        }

        private TextLayoutSettings BuildSettings(float maxWidth, float maxHeight) =>
            new TextLayoutSettings
            {
                Fonts = _fonts,
                FontSize = EffectiveFontSize,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                Alignment = _alignment,
                Wrap = _wrap,
                Overflow = _overflow,
                WritingMode = _writingMode,
                LineSpacing = EffectiveLineSpacing,
                BaseDirection = BidiAlgorithm.AutoDirection,
                ResolveFontOverride = NamedFont,
                ResolveNamedStyle = ApplyNamedStyle,
                ResolveSpriteAspect = SpriteAspect,
                Language = _language,
                Kinsoku = _kinsoku,
                // Korean word wrap follows the locale rather than a toggle: a
                // Korean line in a Japanese UI still wants Korean wrapping, and
                // asking the author to remember that is asking them to get it
                // wrong.
                KoreanWordWrap = IsKorean(_language),
                CjkLatinSpacing = _cjkLatinSpacing,
                PunctuationCompression = _punctuationCompression,
                Spans = Spans,
                Alignments = _markup.HasMarkup && _markup.Alignments.Count > 0
                    ? _markup.Alignments
                    : null,
                Rubies = _markup.HasMarkup && _markup.Rubies.Count > 0 ? _markup.Rubies : null,
                RubyScale = _rubyScale,
            };

        /// <summary>
        /// Lays the text out for the current rect and records where the block's
        /// top-left corner sits in local space, so hit-testing and rendering
        /// always agree.
        /// </summary>
        public TextLayoutResult EnsureLayout()
        {
            if (!EnsureNativeState()) return _layout;
            EnsureDisplayText();

            var rect = GetPixelAdjustedRect();
            // The box side the text wraps on is always passed: the engine only
            // wraps when asked to, but alignment needs to know the box; an
            // RTL single-line label still has to sit against the right edge.
            // The other side is the overflow budget, and only overflow spends
            // it. Which side is which is what the writing mode decides, and
            // this is the one place in the frontend that has to know.
            bool vertical = _writingMode == TextWritingMode.VerticalRightToLeft;
            bool budgeted = _overflow != TextOverflow.Overflow;
            float maxWidth = vertical && !budgeted ? 0f : rect.width;
            float maxHeight = vertical ? rect.height : (budgeted ? rect.height : 0f);

            // Laying out is the expensive half: line-break analysis, grapheme
            // segmentation, bidi and shaping, all of which depend on the text
            // and the box and on nothing else. Revealing one more cluster,
            // ticking an animation or running a quad modifier changes neither,
            // and re-running it every frame for them is exactly the cost the
            // post-layout hook exists to avoid, so the result is kept until
            // something it actually depends on moves.
            var key = new LayoutKey(_displayText, maxWidth, maxHeight, EffectiveFontSize,
                EffectiveLineSpacing, _alignment, _wrap, _overflow, _layoutGeneration);
            if (!_layoutValid || !key.Equals(_layoutKey))
            {
                _engine.Layout(_displayText, BuildSettings(maxWidth, maxHeight), _layout);
                _layoutKey = key;
                _layoutValid = true;
                _quadsValid = false;
                _layoutRuns++;
            }

            // Where the block's start corner sits, which is the corner both
            // axes are measured from: the top left across the page, the top
            // *right* down a column, because a right-to-left column stack
            // starts at the right edge and grows leftward.
            //
            // The vertical alignment is the block axis either way (it places
            // the stack of lines, or the stack of columns), so Top means "at
            // the start edge" in both, and in a vertical label that edge is the
            // right one.
            float slack = (vertical ? rect.width : rect.height) - _layout.BlockExtent;
            float inset = _verticalAlignment switch
            {
                VerticalAlignment.Top => 0f,
                VerticalAlignment.Middle => slack * 0.5f,
                _ => slack,
            };
            _blockOrigin = vertical
                ? new Vector2(rect.xMax - inset - _scrollOffset.x, rect.yMax + _scrollOffset.y)
                : new Vector2(rect.xMin - _scrollOffset.x, rect.yMax - inset + _scrollOffset.y);
            return _layout;
        }

        /// <summary>True if this label is laid out down columns.</summary>
        private bool IsVertical => _writingMode == TextWritingMode.VerticalRightToLeft;

        // ---------------------------------------------------------- hit testing

        /// <summary>
        /// Layout-space point to this graphic's local space.
        ///
        /// Layout space is the engine's two axes and not the screen's: x along
        /// the inline axis, y along the block axis, both growing away from the
        /// block's start corner. Horizontally that is the familiar x-right,
        /// y-down. Down a column it is x-down, y-*left*, which is the whole of
        /// what the frontend has to know about 縦書き geometry; everything that
        /// hit-tests, carets or selects goes through here and needs no vertical
        /// case of its own.
        /// </summary>
        public Vector2 LayoutToLocal(Vector2 layoutPoint) =>
            IsVertical
                ? new Vector2(_blockOrigin.x - layoutPoint.y, _blockOrigin.y - layoutPoint.x)
                : new Vector2(_blockOrigin.x + layoutPoint.x, _blockOrigin.y - layoutPoint.y);

        /// <summary>Local-space point to layout space.</summary>
        public Vector2 LocalToLayout(Vector2 localPoint) =>
            IsVertical
                ? new Vector2(_blockOrigin.y - localPoint.y, _blockOrigin.x - localPoint.x)
                : new Vector2(localPoint.x - _blockOrigin.x, _blockOrigin.y - localPoint.y);

        /// <summary>
        /// Layout-space rect to a local-space rect. The rectangle turns with
        /// the axes: a caret bar spanning a line's height across the page spans
        /// a column's width down it, which is what a caret in vertical text is.
        /// </summary>
        public Rect LayoutToLocal(Rect layoutRect)
        {
            if (IsVertical)
            {
                // (xMin, yMin) is the corner nearest the block start (top
                // right), so the local rect hangs left and down from it.
                var start = LayoutToLocal(new Vector2(layoutRect.xMin, layoutRect.yMin));
                return new Rect(start.x - layoutRect.height, start.y - layoutRect.width,
                    layoutRect.height, layoutRect.width);
            }
            var topLeft = LayoutToLocal(new Vector2(layoutRect.xMin, layoutRect.yMin));
            return new Rect(topLeft.x, topLeft.y - layoutRect.height, layoutRect.width, layoutRect.height);
        }

        /// <summary>Caret index nearest a point given in this graphic's local space.</summary>
        public int GetIndexAtLocalPoint(Vector2 localPoint)
        {
            EnsureLayout();
            return TextHitTest.GetIndexAtPoint(_layout, LocalToLayout(localPoint));
        }

        /// <summary>Caret index nearest a screen point.</summary>
        public int GetIndexAtScreenPoint(Vector2 screenPoint, Camera eventCamera)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, screenPoint, eventCamera, out var local);
            return GetIndexAtLocalPoint(local);
        }

        /// <summary>Caret rectangle for a text index, in local space.</summary>
        public Rect GetCaretRect(int index, float width)
        {
            EnsureLayout();
            return LayoutToLocal(TextHitTest.GetCaretRect(_layout, index, width));
        }

        /// <summary>Selection rectangles for a text range, in local space.</summary>
        public void GetSelectionRects(int start, int end, List<Rect> rects)
        {
            EnsureLayout();
            TextHitTest.GetSelectionRects(_layout, start, end, rects);
            for (int i = 0; i < rects.Count; i++) rects[i] = LayoutToLocal(rects[i]);
        }

        /// <summary>The link under a local-space point, if any.</summary>
        public bool TryGetLinkAtLocalPoint(Vector2 localPoint, out TextLink link)
        {
            link = default;
            EnsureDisplayText();
            if (_links.Count == 0) return false;

            int index = GetIndexAtLocalPoint(localPoint);
            foreach (var candidate in _links)
            {
                if (!candidate.Contains(index)) continue;
                // A caret index alone is ambiguous at a boundary; confirm the
                // point really falls inside one of the link's rectangles.
                GetSelectionRects(candidate.Start, candidate.End, s_rectScratch);
                foreach (var rect in s_rectScratch)
                {
                    if (!rect.Contains(localPoint)) continue;
                    link = candidate;
                    return true;
                }
            }
            return false;
        }

        private static readonly List<Rect> s_rectScratch = new List<Rect>();

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_links.Count == 0 && !RichTextParser.MightHaveMarkup(_text)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out var local);
            if (TryGetLinkAtLocalPoint(local, out var link))
                _linkClicked.Invoke(link.Id);
        }

        // -------------------------------------------------------------- rendering

        private static readonly Vector3 s_Normal = new Vector3(0f, 0f, -1f);
        private static readonly Vector4 s_Tangent = new Vector4(1f, 0f, 0f, -1f);
        private readonly List<GlyphClusters.Cluster> _clusters = new List<GlyphClusters.Cluster>();
        private readonly List<PositionedGlyph> _positioned = new List<PositionedGlyph>();
        private readonly List<TextQuad> _quads = new List<TextQuad>();
        private long _meshHeapMark;

        /// <summary>
        /// The tiles this label last drew, in draw order. Read-only, and only
        /// valid after a rebuild, but it is the finished geometry, addressed by
        /// grapheme cluster, which is what an animator wants and what no
        /// TMP-based one could get.
        /// </summary>
        public IReadOnlyList<TextQuad> Quads => _quads;

        /// <summary>
        /// The tiles as they were actually drawn, after reveal and after every
        /// effect and modifier had its say. <see cref="Quads"/> is the geometry
        /// layout produced; this is what reached the mesh.
        ///
        /// Two lists rather than one because the first is a cache that survives
        /// between frames and the second changes every frame. Overwriting the
        /// cache with animated positions would make the next frame animate the
        /// previous frame's output.
        /// </summary>
        public IReadOnlyList<TextQuad> DrawnQuads => _drawn;

        private readonly List<TextQuad> _drawn = new List<TextQuad>();

        /// <summary>
        /// Every distinct decoration this label's tiles use. Slot 0 is
        /// <see cref="TextDecoration.None"/> and stays there, so an
        /// unset <see cref="TextQuad.Decoration"/> means undecorated.
        /// </summary>
        private readonly List<TextDecoration> _decorations =
            new List<TextDecoration> { TextDecoration.None };

        /// <summary>
        /// The same table already packed for the mesh. Packed once when a
        /// decoration is interned rather than once per tile per frame; an
        /// animated label re-emits its vertices sixty times a second and the
        /// decoration on them has not changed since the text did.
        /// </summary>
        private readonly List<DecorationChannels> _packedDecorations =
            new List<DecorationChannels> { default };

        /// <summary>What a tile is drawn with: outline, shadow, glow.</summary>
        public TextDecoration DecorationOf(in TextQuad quad) =>
            quad.Decoration > 0 && quad.Decoration < _decorations.Count
                ? _decorations[quad.Decoration]
                : TextDecoration.None;

        /// <summary>
        /// The decoration in force at a position in the display text: the
        /// label's own style underneath, a <c>&lt;style=…&gt;</c> over it, and
        /// the decoration tags on top, each winning only the parts it sets, so
        /// a theme's shadow survives a span asking for an outline.
        ///
        /// Returns an index into <see cref="_decorations"/> rather than the
        /// value, and interns as it goes. Linear, because the count is the
        /// number of distinct decorations in one label, which is one or two and
        /// has never in any real text been ten.
        /// </summary>
        private int ResolveDecoration(int textIndex, in TextStyle style)
        {
            var decoration = _style != null ? _style.Decoration : TextDecoration.None;
            if (style.NamedStyle >= 0 && style.NamedStyle < _namedStyles.Count)
            {
                var named = _namedStyles[style.NamedStyle];
                if (named != null) decoration = named.Decoration.Over(decoration);
            }
            if (_markup.HasMarkup && _markup.Decorations.Count > 0)
                decoration = _markup.DecorationAt(textIndex).Over(decoration);

            if (decoration.IsNone) return 0;
            for (int i = 1; i < _decorations.Count; i++)
                if (_decorations[i].Equals(decoration)) return i;
            _decorations.Add(decoration);
            _packedDecorations.Add(Pack(decoration));
            return _decorations.Count - 1;
        }

        /// <summary>
        /// Emits one run of a colour font: emoji, mostly.
        ///
        /// The shaping half of emoji was already done: a ZWJ family is one
        /// grapheme cluster under UAX #29 and one glyph once the font's
        /// ligatures apply, which is why a flag or a skin-toned family arrives
        /// here as a single glyph id. All that is left is getting the colour
        /// out of the font and into an RGBA tile, which is what makes this the
        /// thing TextMesh Pro cannot do at all.
        /// </summary>

        /// <summary>
        /// Where one run's glyphs land in local space, and turned how far.
        ///
        /// Every tile the label draws is placed by two numbers in the font's
        /// units (how far along the run it sits, and how far across it) plus
        /// the tile's own ink box. Horizontally those are x and y and the
        /// arithmetic is two adds. In a column the along axis runs downward and
        /// the across axis runs rightward, and a rotated run turns its tiles
        /// ninety degrees on top of that. Three cases, one signature, resolved
        /// once per run rather than once per tile, so the horizontal path pays
        /// a predictable branch and nothing else.
        /// </summary>
        private readonly struct RunFrame
        {
            /// <summary>Local-space origin: the run's start on its own baseline.</summary>
            public readonly float BaseX, BaseY;

            /// <summary>Render units per font design unit.</summary>
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

            /// <summary>The run's baseline on the axis across it, in local space.</summary>
            public float Baseline => Vertical ? BaseX : BaseY;

            /// <summary>
            /// Places one tile. <paramref name="along"/> and
            /// <paramref name="across"/> are the glyph's pen position in font
            /// units; <paramref name="originUnits"/> and
            /// <paramref name="sizeUnits"/> are its ink box, in the glyph's own
            /// upright frame.
            /// </summary>
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
                    // Upright: the pen runs down the column and the glyph's own
                    // frame is still the screen's, so the ink box is added the
                    // way it always was.
                    position = new Vector2(BaseX + (across + originUnits.x) * Scale,
                        BaseY - along * Scale + originUnits.y * Scale);
                    rotation = 0f;
                    return;
                }
                // Rotated: the whole glyph frame turns clockwise about the run
                // origin, so a point (gx, gy) of it lands at (gy, -gx). The mesh
                // rotates a tile about its own centre, so what it is given is
                // the tile where it would be unturned, centred where the turned
                // one belongs.
                float gx = (along + originUnits.x) * Scale;
                float gy = (across + originUnits.y) * Scale;
                position = new Vector2(
                    BaseX + gy + (size.y - size.x) * 0.5f,
                    BaseY - gx - (size.x + size.y) * 0.5f);
                rotation = -90f;
            }
        }

        /// <summary>
        /// Where a run's glyphs start and how their frame is turned. Down a
        /// column the block axis runs leftward from the block's right edge, and
        /// a rotated run's own baseline sits off the centre line by half its
        /// line box, both of which are arithmetic on the same two numbers the
        /// horizontal path already uses.
        /// </summary>
        private RunFrame FrameOf(in TextRun run, bool vertical, float scale) => vertical
            ? new RunFrame(
                _blockOrigin.x - run.Baseline - run.CrossAxisBaselineOffset + run.BaselineShift,
                _blockOrigin.y - run.X, scale, true, run.Rotated)
            : new RunFrame(_blockOrigin.x + run.X,
                _blockOrigin.y - run.Baseline + run.BaselineShift, scale, false, false);

        /// <summary>
        /// The bars markup asks for that no glyph carries: the wash behind a
        /// <c>&lt;mark&gt;</c>, the line under a <c>&lt;u&gt;</c>, the line
        /// through an <c>&lt;s&gt;</c>.
        ///
        /// Two passes around the glyph loop rather than one folded into it,
        /// because <see cref="_quads"/> is drawn in order and that order is the
        /// whole of what "behind" means: a wash is only a wash with the text on
        /// top of it, and a line is only a line drawn over the text. Between
        /// them nothing else changes; a bar is an ordinary tile, so reveal,
        /// effects and a custom modifier reach it the way they reach a letter.
        /// </summary>
        private void EmitBands(bool vertical, bool behind)
        {
            int runIndex = -1;
            foreach (var run in _layout.Runs)
            {
                runIndex++;
                var style = run.Style;
                bool wanted = behind
                    ? style.HasMark
                    : style.Underline || style.Strikethrough;
                if (!wanted) continue;

                var font = run.Font;
                if (font == null || font.UnitsPerEm == 0) continue;

                float runSize = run.FontSize > 0f ? run.FontSize : EffectiveFontSize;
                var frame = FrameOf(run, vertical, runSize / font.UnitsPerEm);

                // A run set upright in a column is the one case where the face's
                // own numbers do not apply. post and OS/2 measure from a
                // horizontal baseline, and an upright glyph has none: it is
                // centred on the column, so "a tenth of an em below the
                // baseline" lands a tenth of an em left of centre, which is
                // through the character rather than beside it. What the three
                // bars mean in a column is a different sentence in the same
                // language, and it is written against the em box.
                //
                // A rotated run needs none of this. Its whole frame turns with
                // its glyphs, so its own baseline turned with it and the face's
                // numbers are about that baseline exactly as they were.
                bool upright = vertical && !run.Rotated;
                float halfEm = font.UnitsPerEm * 0.5f;

                if (behind)
                {
                    // Across the page the wash covers the run's own line box,
                    // which is the run's font at the run's size and not the
                    // line's: a <size=44> word inside a 28pt sentence is
                    // highlighted to its own height, which is what a reader
                    // expects to see.
                    if (!vertical)
                    {
                        EmitBand(run, frame, runIndex,
                            font.Descender, font.Ascender, style.MarkColor);
                        continue;
                    }
                    // Down a column the wash is the column, one em across and
                    // the same em for every run in it. Line boxes would be
                    // honest and would look wrong: Latin's is a third of an em
                    // wider than the kana's beside it, and a highlight that
                    // steps in and out at every script change reads as a
                    // rendering fault. A rotated run's em is centred on its
                    // line box, because that is where the column centred it.
                    float centre = run.Rotated
                        ? (font.Ascender + font.Descender) * 0.5f
                        : 0f;
                    EmitBand(run, frame, runIndex,
                        centre - halfEm, centre + halfEm, style.MarkColor);
                    continue;
                }

                // The tag's colour, not the label's: the label's is multiplied
                // in at emit for a bar exactly as it is for a letter, so a
                // fading label fades its own underline.
                var color = style.HasColor ? style.Color : new Color32(255, 255, 255, 255);
                if (style.Underline)
                {
                    // Beside the em box, on the left of the column: the side a
                    // rotated run's own underline turns onto, and within a
                    // twentieth of an em of where it lands, so a column of kana
                    // with a stretch of Latin in it wears one unbroken line
                    // rather than a line that changes sides halfway down. The
                    // traditional Japanese sideline is on the right, but a line
                    // that jumps sides mid-column is worse than one on the
                    // unexpected side.
                    float thickness = font.UnderlineThickness;
                    if (upright)
                        EmitBand(run, frame, runIndex, -halfEm - thickness, -halfEm, color);
                    else
                        EmitBand(run, frame, runIndex,
                            font.UnderlineOffset - thickness, font.UnderlineOffset, color);
                }
                if (style.Strikethrough)
                {
                    float thickness = font.StrikeoutThickness;
                    if (upright)
                        EmitBand(run, frame, runIndex,
                            thickness * -0.5f, thickness * 0.5f, color);
                    else
                        EmitBand(run, frame, runIndex,
                            font.StrikeoutOffset - thickness, font.StrikeoutOffset, color);
                }
            }
        }

        /// <summary>
        /// One bar across a run, between two offsets from its baseline in font
        /// units.
        ///
        /// Emitted a glyph at a time rather than as one rectangle over the
        /// run's whole advance, which is what it looks like on screen: the
        /// segments abut exactly because each is one glyph's advance and the
        /// pen is the same pen the glyphs were placed with. Cut per glyph, a
        /// bar can be revealed with the typewriter, faded by a per-character
        /// effect and moved by a modifier along with the letter it belongs to,
        /// none of which one rectangle could do.
        /// </summary>
        private void EmitBand(in TextRun run, in RunFrame frame, int runIndex,
            float acrossMin, float acrossMax, Color32 color)
        {
            float thickness = acrossMax - acrossMin;
            if (thickness <= 0f || color.a == 0) return;

            float pen = 0f;
            for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
            {
                float advance = _layout.Glyphs[i].XAdvance;
                float start = pen;
                pen += advance;
                // A combining mark advances nothing; a bar of no width is four
                // vertices nobody can see.
                if (advance <= 0f) continue;

                // Horizontal text, and a rotated column whose whole frame turns
                // with its glyphs, place the bar the way a glyph's ink box is
                // placed. Upright in a column the along axis runs downward, so
                // the box has to be hung from the pen rather than raised from
                // it, and its two extents swap.
                Vector2 origin, size;
                if (frame.Vertical && !frame.Rotated)
                {
                    origin = new Vector2(0f, -advance);
                    size = new Vector2(thickness, advance);
                }
                else
                {
                    origin = Vector2.zero;
                    size = new Vector2(advance, thickness);
                }

                frame.Place(start, acrossMin, origin, size,
                    out var position, out var placed, out float rotation);
                var graphemes = BandClusterRange(i, run);
                _quads.Add(new TextQuad
                {
                    Position = position,
                    Size = placed,
                    Rotation = rotation,
                    Color = color,
                    FirstGrapheme = graphemes.First,
                    LastGrapheme = graphemes.Last,
                    RunIndex = runIndex,
                    Baseline = frame.Baseline,
                    Style = run.Style,
                    IsSolid = true,
                });
            }
        }

        /// <summary>
        /// The grapheme clusters one glyph covers, found without leaving the
        /// run it came from.
        ///
        /// Same question <see cref="ClusterRange"/> answers, and the same
        /// answer for a bar: a run's clusters are inside its own text range, so
        /// its own glyphs are the only ones that can end this one. The wider
        /// scan exists for ruby, whose clusters point at the base it annotates;
        /// paying for it per glyph would make an underlined paragraph
        /// quadratic in its own length.
        /// </summary>
        private (int First, int Last) BandClusterRange(int glyphIndex, in TextRun run)
        {
            int start = _layout.Glyphs[glyphIndex].Cluster;
            int end = run.TextStart + run.TextLength;
            // Ascending in logical order, descending in a right-to-left run's
            // visual one, so both neighbours are checked and the nearest one
            // above wins.
            for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
            {
                int other = _layout.Glyphs[i].Cluster;
                if (other > start && other < end) end = other;
            }
            return (_layout.GraphemeAt(start), _layout.GraphemeAt(Mathf.Max(start, end - 1)));
        }

        private void EmitColorRun(in TextRun run, FontData font, float runSize, float scale,
            in RunFrame frame, Color32 runColor, int runIndex, GlyphAtlas sdfAtlas)
        {
            var colorAtlas = SharedGlyphAtlas.ColorAtlas;
            int ppem = GlyphAtlas.QuantizePixelsPerEm(runSize);
            float pixelsPerUnit = ppem / (float)font.UnitsPerEm;
            int runTextEnd = run.TextStart + run.TextLength;

            float pen = 0f;
            for (int i = run.GlyphStart; i < run.GlyphStart + run.GlyphCount; i++)
            {
                var glyph = _layout.Glyphs[i];
                float along = pen + glyph.XOffset;

                // A colour font is not a font in which every glyph has colour.
                // Noto Color Emoji carries monochrome glyphs, digits and
                // .notdef, and the earlier version of this dropped every one of
                // them on the floor; a whole run went down the colour path and
                // anything that failed to decode simply advanced the pen. So
                // the decision is per glyph, and the fallback is the ordinary
                // SDF tile rather than nothing.
                // Resolved per glyph, not per run: a colour font carries
                // monochrome glyphs too, and the ones that fall through to the
                // SDF path below are ordinary text that ordinary decorations
                // apply to.
                int decoration = ResolveDecoration(glyph.Cluster, run.Style);
                if (TryEmitColorGlyph(font, glyph, ppem, pixelsPerUnit, runColor, colorAtlas,
                        frame, along, glyph.YOffset, run, runIndex, runTextEnd, i, decoration))
                {
                    pen += glyph.XAdvance;
                    continue;
                }

                var sdf = sdfAtlas.GetOrAdd(font, glyph.GlyphId, runSize);
                if (sdf.HasPixels)
                {
                    AddQuad(sdf.OriginUnits, sdf.SizeUnits, sdf.UvRect, sdf.Layer,
                        frame, along, glyph.YOffset, runColor,
                        run, runIndex, ClusterRange(i, runTextEnd), isColor: false, decoration);
                }
                pen += glyph.XAdvance;
            }
        }

        /// <summary>
        /// Draws an inline sprite from the sheet, through the same RGBA atlas
        /// and the same quad path as colour emoji, so a line of dialogue with
        /// icons in it is still one draw call.
        /// </summary>
        private void EmitSprite(in TextRun run, float runSize, float scale,
            in RunFrame frame, Color32 runColor, int runIndex)
        {
            if (_sprites == null) return;

            int ppem = GlyphAtlas.QuantizePixelsPerEm(runSize);
            var sprite = _sprites[run.Style.Sprite];
            if (sprite == null) return;

            long key = SpriteKey(sprite, ppem);
            var colorAtlas = SharedGlyphAtlas.ColorAtlas;

            ColorGlyphAtlas.ColorLocation location;
            if (colorAtlas.Contains(key)) location = colorAtlas.GetOrAdd(key, default);
            else if (_sprites.TryRead(run.Style.Sprite, ppem, out var tile))
                location = colorAtlas.GetOrAdd(key, tile);
            else return;

            if (!location.HasPixels) return;

            // A sprite sits on the baseline and rises to the line's em, which
            // is what lines an icon up with the text beside it. Down a column
            // it sits on the centre line and takes its em there too, so the ink
            // box is the same box shifted half its width across; an icon in a
            // column is centred in it, not hung off one side.
            float height = runSize;
            float width = height * _sprites.AspectOf(run.Style.Sprite);
            int grapheme = _layout.GraphemeAt(run.TextStart);

            // The icon has no font behind it, so the cell is placed by hand:
            // resting on the baseline across the page, hanging from the pen and
            // centred on the column down one. A rotated run needs neither:
            // its whole frame turns and the cell turns with it.
            float alongUnits = 0f, acrossUnits = 0f;
            if (frame.Vertical && !frame.Rotated && frame.Scale > 0f)
            {
                alongUnits = height / frame.Scale;
                acrossUnits = -width * 0.5f / frame.Scale;
            }
            var cellUnits = frame.Scale > 0f
                ? new Vector2(width / frame.Scale, height / frame.Scale)
                : Vector2.zero;

            frame.Place(alongUnits, acrossUnits, Vector2.zero, cellUnits,
                out var position, out var size, out float rotation);
            _quads.Add(new TextQuad
            {
                Position = position,
                Size = size,
                // A picture has no upright form to preserve: a rotated column
                // turns its icons with its letters, which is what an inline
                // arrow or a face in a line of Latin means.
                Rotation = rotation,
                UvRect = location.UvRect,
                Layer = location.Layer,
                Color = runColor,
                FirstGrapheme = grapheme,
                LastGrapheme = grapheme,
                RunIndex = runIndex,
                Baseline = frame.Baseline,
                Style = run.Style,
                IsColor = true,
            });
        }

        /// <summary>
        /// Cache key for a sprite tile: the sprite's own identity, not its slot
        /// in a sheet. Keying by (sheet, index) survives a sprite being
        /// replaced or the list being reordered, and then the atlas keeps
        /// serving the old picture until something evicts it.
        /// </summary>
        private static long SpriteKey(Sprite sprite, int ppem) =>
            // The instance id is masked rather than cast: a runtime-created
            // object's id is negative, and sign extension would flood the high
            // bits and destroy the discriminator below.
            unchecked((long)0x4000000000000000L
                | ((long)(uint)sprite.GetInstanceID() << 12)
                | (uint)ppem);

        private bool TryEmitColorGlyph(FontData font, in ShapedGlyph glyph, int ppem,
            float pixelsPerUnit, Color32 runColor, ColorGlyphAtlas colorAtlas,
            in RunFrame frame, float along, float across, in TextRun run, int runIndex,
            int runTextEnd, int glyphIndex, int decoration)
        {
            bool followsText = ColorGlyphs.UsesTextColor(font, glyph.GlyphId);
            long key = ColorKey(font, glyph.GlyphId, ppem, followsText ? runColor : default);

            ColorGlyphAtlas.ColorLocation location;
            if (colorAtlas.Contains(key)) location = colorAtlas.GetOrAdd(key, default);
            else if (ColorGlyphs.TryDecode(font, glyph.GlyphId, pixelsPerUnit, runColor, out var decoded))
                location = colorAtlas.GetOrAdd(key, decoded);
            else return false;

            if (!location.HasPixels) return false;

            AddQuad(location.OriginUnits, location.SizeUnits, location.UvRect, location.Layer,
                frame, along, across, runColor,
                run, runIndex, ClusterRange(glyphIndex, runTextEnd), isColor: true, decoration);
            return true;
        }

        private void AddQuad(Vector2 originUnits, Vector2 sizeUnits, Rect uv, int layer,
            in RunFrame frame, float along, float across, Color32 color,
            in TextRun run, int runIndex, (int First, int Last) graphemes, bool isColor,
            int decoration)
        {
            frame.Place(along, across, originUnits, sizeUnits,
                out var position, out var size, out float rotation);
            _quads.Add(new TextQuad
            {
                Position = position,
                Size = size,
                Rotation = rotation,
                UvRect = uv,
                Layer = layer,
                Color = color,
                FirstGrapheme = graphemes.First,
                LastGrapheme = graphemes.Last,
                RunIndex = runIndex,
                Baseline = frame.Baseline,
                Style = run.Style,
                IsColor = isColor,
                // A colour tile is a picture in the colour atlas whatever the
                // label asked for; only the SDF fallback follows the option.
                IsPrecise = !isColor && _precise,
                Decoration = decoration,
            });
        }

        /// <summary>
        /// The grapheme clusters one glyph covers.
        ///
        /// A ligature is one glyph with one cluster value spanning several
        /// characters: "fi" is two, lam-alef is two, and the shaper reports
        /// only where it starts. Its end is where the *next* glyph starts, so
        /// that is what this looks for. Getting it wrong shows a ligature one
        /// reveal step early, with its second letter appearing before its turn.
        /// </summary>
        private (int First, int Last) ClusterRange(int glyphIndex, int runTextEnd)
        {
            int start = _layout.Glyphs[glyphIndex].Cluster;
            int end = runTextEnd;
            // Clusters run ascending in logical order and descending in a
            // right-to-left run's visual order, so both neighbours are checked
            // and the nearest one above wins.
            for (int i = 0; i < _layout.Glyphs.Count; i++)
            {
                int other = _layout.Glyphs[i].Cluster;
                if (other > start && other < end) end = other;
            }
            return (_layout.GraphemeAt(start), _layout.GraphemeAt(Mathf.Max(start, end - 1)));
        }

        /// <summary>
        /// Cache key for a colour tile.
        /// </summary>
        private static long ColorKey(FontData font, uint glyphId, int ppem, Color32 tint)
        {
            long key = ((long)font.CacheId << 40) ^ ((long)glyphId << 12) ^ ppem;
            // The tint is part of the key only for glyphs that actually bake it
            // in: a COLR layer using the "use the text colour" sentinel. Every
            // other tile is colour-independent, and keying them by colour would
            // cost a cache miss per tint for nothing. Leaving it out for the
            // ones that do bake it in is worse: the first label to draw wins
            // and every other colour is silently wrong. The tint that arrives
            // here is the tag's colour, never the label's; the label colour is
            // a vertex multiply at emit, so a fading label reuses one tile
            // instead of baking one per alpha step.
            if (tint.a != 0 || tint.r != 0 || tint.g != 0 || tint.b != 0)
                key ^= (long)(tint.r << 24 | tint.g << 16 | tint.b << 8 | tint.a) << 8;
            return key;
        }

        /// <summary>
        /// Writes the collected tiles into the mesh, applying reveal and the
        /// quad modifier. Split out from the build so the two costs stay
        /// distinguishable: laying text out is expensive, moving finished quads
        /// is not, and an animation that re-lays out every frame is the mistake
        /// this whole seam exists to prevent.
        /// </summary>
        private void EmitQuads(VertexHelper vh)
        {
            long emitStartedAt = AtlasDiagnostics.Now;
            EnsureAnimator();
            // A frozen clock would leave every appearance effect at t=0, which
            // is alpha 0, which is text that has vanished. In the editor, and
            // for anyone who left AnimationTime alone, effects are shown
            // finished rather than never-started: a designer typing <fade> into
            // a label must not watch their text disappear.
            // "Running" means something is advancing the clock: play mode with
            // Animate on, or anyone who has moved AnimationTime themselves. A
            // label sitting in the Scene view with neither is shown finished.
            bool clockRunning = (Application.isPlaying && _animate) || _animationTime > 0f;
            // Never latched: a script driving AnimationTime starts at 0 like
            // everyone else, and stamping its first frame as "finished for ever"
            // would mean appearance effects that never play.
            if (clockRunning) _animator.UnlatchFrozenStamps();
            int reveal = EffectiveMaxVisibleGraphemes;
            _animator.NoteReveal(reveal, _layout.GraphemeCount,
                clockRunning ? _animationTime : float.NegativeInfinity);
            var context = new TextQuadContext(_layout, _layout.GraphemeCount, _animationTime);

            // The label's colour joins here, on the way to the mesh, not in the
            // cached quads, which is what lets a colour tween (the damage-text
            // fade) redraw without rebuilding a thing. Applied after the
            // animator and the modifier so both keep seeing the tag colours
            // they were written against, and a fading label fades their output
            // too.
            var labelColor = (Color32)EffectiveColor;
            // Opaque white multiplies to identity, and that is what almost every
            // label in a scene is: the tint exists for tweens and for themed
            // text, not for the two hundred world-space nameplates that use it
            // to mean "leave my colours alone". Four byte multiplies and four
            // divisions per quad, per label, per frame is not much until it is
            // multiplied by all three of those.
            bool tinted = labelColor.r != 255 || labelColor.g != 255 ||
                          labelColor.b != 255 || labelColor.a != 255;

            _drawn.Clear();
            for (int i = 0; i < _quads.Count; i++)
            {
                var quad = _quads[i];

                // Reveal is by grapheme cluster, and a tile is shown only once
                // every cluster it covers has been revealed. A merged tile holds
                // a joined group as one seam-free field: showing it early would
                // reveal letters that have not had their turn, and clipping it
                // would cut a ligature in half, which is not a thing a reader
                // can be shown.
                if (reveal >= 0 && quad.LastGrapheme >= reveal) continue;

                // The animator runs first and the user's modifier last, so a
                // custom modifier sees what the tags did and can override it.
                if (!_animator.IsEmpty && !_animator.Modify(ref quad, context)) continue;
                if (_modifier != null && !_modifier.Modify(ref quad, context)) continue;
                if (tinted) quad.Color = Multiply(quad.Color, labelColor);
                if (quad.Color.a == 0) continue;

                _drawn.Add(quad);
                // A colour tile is a picture: the fragment shader returns it
                // before it ever looks at these channels, so packing them would
                // be arithmetic nobody reads. Zeroed rather than skipped, so
                // what the mesh says matches what gets drawn.
                var decoration = quad.IsColor || quad.IsSolid || quad.Decoration <= 0
                        || quad.Decoration >= _packedDecorations.Count
                    ? default
                    : _packedDecorations[quad.Decoration];
                if (quad.Rotation != 0f)
                    EmitRotatedQuad(vh, quad, decoration);
                else
                    EmitQuad(vh, quad.Position.x, quad.Position.y, quad.Size.x, quad.Size.y,
                        quad.UvRect, quad.Layer, quad.Color, AtlasOf(quad), decoration);
            }
            AtlasDiagnostics.Add(ref AtlasDiagnostics.EmitTicks, emitStartedAt);
        }

        // -------------------------------------------------------------- reveal

        [Tooltip("Grapheme clusters to draw; -1 draws all. This is the typewriter primitive: " +
                 "in shaped text 'one character' is a cluster, not a char.")]
        [SerializeField] private int _maxVisibleGraphemes = -1;

        /// <summary>
        /// The reveal to actually draw.
        ///
        /// A label whose typewriter is driving has no clock outside play mode,
        /// so its serialized reveal is wherever the last play session left it,
        /// and a designer shown a blank label in the Scene view cannot tell that
        /// from a broken font. Same care, and the same reason, as the
        /// frozen-stamp rule in EmitQuads.
        ///
        /// Only a STALE reveal is overridden. Once something in this session has
        /// moved it (the inspector slider, a script, an editor preview calling
        /// <see cref="RestartReveal"/>), it is a deliberate statement about what
        /// should be on screen and is drawn as written.
        /// </summary>
        private int EffectiveMaxVisibleGraphemes =>
            _charactersPerSecond > 0f && !Application.isPlaying && !_revealMoved
                ? -1
                : _maxVisibleGraphemes;

        private bool _revealMoved;

        private ITextQuadModifier _modifier;
        private float _animationTime;

        [Tooltip("Advance AnimationTime automatically each frame. Turn off to drive it yourself: " +
                 "a paused game should pause its text without the text knowing what paused means.")]
        [SerializeField] private bool _animate = true;

        private readonly TextAnimator _animator = new TextAnimator();
        private bool _animatorBuilt;

        /// <summary>
        /// How many grapheme clusters to draw; -1 (the default) draws all.
        ///
        /// Grapheme clusters, not characters, because "one character at a time"
        /// is not a thing in shaped text: an Arabic ligature is two characters
        /// in one glyph, a Hangul syllable is three, a flag is four. Setting
        /// this does not re-lay the text out; only the mesh is rebuilt.
        /// </summary>
        public int MaxVisibleGraphemes
        {
            get => _maxVisibleGraphemes;
            set
            {
                int clamped = value < 0 ? -1 : value;
                int previous = _maxVisibleGraphemes;
                if (previous == clamped) return;
                _maxVisibleGraphemes = clamped;
                _revealMoved = true;
                SetVerticesDirty();
                FireRevealEvents(previous, clamped);
            }
        }

        /// <summary>
        /// The reveal events for a step from <paramref name="previous"/> to
        /// <paramref name="current"/> grapheme clusters.
        ///
        /// A step to or from -1 ("all") is a jump and not a walk, so no
        /// per-unit event is reported for what it crossed: that burst, two
        /// hundred typing sounds in one frame, is exactly what
        /// <see cref="SkipToEnd"/> exists to avoid. Completion is not a walk
        /// either, so it is decided first and reported however the reveal
        /// arrived.
        /// </summary>
        private void FireRevealEvents(int previous, int current)
        {
            int graphemes = _layout.GraphemeCount;
            if (current < 0 || (graphemes > 0 && current >= graphemes)) RaiseRevealComplete();
            else _revealCompleteFired = false;

            if (current < 0 || previous < 0) return;

            // One event per cluster that just became visible, in order, not
            // one per assignment. A typewriter that jumps forward several
            // clusters in a frame (a fast-forward, a low frame rate) still has
            // to fire the sound effect for each, which is what a dialogue
            // system is actually listening for.
            if (_graphemeRevealed != null)
                for (int i = previous; i < current; i++) _graphemeRevealed.Invoke(i);

            if (_characterRevealed == null) return;
            EnsureUnits();
            int from = RevealUnits.RevealedBy(_unitStarts, previous);
            int to = RevealUnits.RevealedBy(_unitStarts, current);
            for (int u = from; u < to; u++) _characterRevealed.Invoke(u);
        }

        private void RaiseRevealComplete()
        {
            if (_revealCompleteFired) return;
            _revealCompleteFired = true;
            _revealComplete?.Invoke();
        }

        [SerializeField] private UnityEvent<int> _graphemeRevealed = new UnityEvent<int>();

        /// <summary>
        /// Raised once per grapheme cluster as the reveal passes it, with that
        /// cluster's index.
        ///
        /// Kept exactly as it was for everything already listening to it.
        /// <see cref="CharacterRevealed"/> is the one to reach for now: it
        /// reports the unit the reveal actually steps in, so it fires once for a
        /// Thai syllable rather than once per grapheme inside it.
        /// </summary>
        public UnityEvent<int> GraphemeRevealed => _graphemeRevealed;

        /// <summary>Grapheme clusters in the laid-out text: the reveal's end.</summary>
        public int GraphemeCount => EnsureLayout().GraphemeCount;

        // ---------------------------------------------------------- typewriter

        [Tooltip("What one reveal step is. Grapheme is the historical behaviour and the safe " +
                 "default; Cluster keeps a Thai leading vowel with its consonant and a Khmer " +
                 "subscript with its stack; Syllable also refuses to give 。 、 っ ゃ ー steps " +
                 "of their own. Korean is one step per syllable block under all three.")]
        [SerializeField] private RevealGranularity _revealGranularity = RevealGranularity.Grapheme;

        [Tooltip("Reveal speed, in units per second. 0 (the default) leaves the reveal alone " +
                 "for whoever was driving MaxVisibleGraphemes by hand.")]
        [SerializeField] private float _charactersPerSecond;

        [Tooltip("Extra seconds held after a revealed unit containing one of these characters. " +
                 "This is what most <wait> markup is actually asking for.")]
        [SerializeField] private List<PunctuationDelay> _punctuationDelays = new List<PunctuationDelay>();

        [SerializeField] private UnityEvent<int> _characterRevealed = new UnityEvent<int>();
        [SerializeField] private UnityEvent _revealComplete = new UnityEvent();

        /// <summary>Grapheme index each reveal unit starts at, terminated with the count.</summary>
        private readonly List<int> _unitStarts = new List<int>();

        /// <summary><c>&lt;wait&gt;</c> pauses, resolved from text indices onto unit boundaries.</summary>
        private readonly List<(int Unit, float Seconds)> _waits = new List<(int, float)>();

        private bool _unitsValid;
        private int _unitsLayoutRuns = -1;
        private RevealGranularity _unitsGranularity;

        // Seconds banked toward the next unit, and seconds still owed before it
        // may appear. Two accumulators rather than one clock: a pause is not
        // "type slower for a moment", it is time in which nothing types, and
        // folding it into a position computed from a clock loses that the
        // moment two pauses land in one frame.
        private float _revealBudget;
        private float _revealPause;

        /// <summary>Units revealed as of this typewriter's own last step.</summary>
        private int _revealCursor;

        private bool _revealFresh = true;
        private bool _revealCompleteFired;

        /// <summary>What one step of the reveal is. See <see cref="RevealGranularity"/>.</summary>
        public RevealGranularity RevealGranularity
        {
            get => _revealGranularity;
            set
            {
                if (_revealGranularity == value) return;
                _revealGranularity = value;
                _unitsValid = false;
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// Reveal speed in units per second; 0 or less turns the label's own
        /// typewriter off, which is the default and which leaves
        /// <see cref="MaxVisibleGraphemes"/> entirely to the caller.
        /// </summary>
        public float CharactersPerSecond
        {
            get => _charactersPerSecond;
            set
            {
                if (_charactersPerSecond == value) return;
                bool wasOff = _charactersPerSecond <= 0f;
                _charactersPerSecond = value;
                // Turning it on rewinds; turning it off does not. Blanking a
                // label because a script just took the reveal back would be a
                // surprise, and the text it was showing is still correct. Play
                // mode only, for the reason EffectiveMaxVisibleGraphemes gives:
                // out here nothing would ever un-blank it.
                if (wasOff && value > 0f && Application.isPlaying) RestartReveal();
            }
        }

        /// <summary>
        /// Extra pauses after punctuation, as an editable table. Empty by
        /// default; <c>PunctuationDelays.Recommended</c> fills a starting set
        /// covering CJK, Devanagari, Khmer, Arabic and Thai, and the inspector
        /// offers it as a button.
        /// </summary>
        public IList<PunctuationDelay> PunctuationDelays => _punctuationDelays;

        /// <summary>
        /// Raised once per revealed unit, with that unit's index under the
        /// current <see cref="RevealGranularity"/>: the hook for typing sounds.
        ///
        /// Per UNIT, which is the whole reason it exists next to
        /// <see cref="GraphemeRevealed"/>: 한 is three code points, a ZWJ family
        /// is seven, and a Thai syllable is two glyphs. Every one of them is one
        /// sound, and anything counting characters plays three.
        /// </summary>
        public UnityEvent<int> CharacterRevealed => _characterRevealed;

        /// <summary>
        /// Raised once when the reveal reaches the end of the text, however it
        /// got there: the typewriter finishing, a script assigning
        /// <see cref="MaxVisibleGraphemes"/>, or <see cref="SkipToEnd"/>.
        /// Rearmed when the reveal moves back off the end, so a rewound or
        /// retyped label fires it again.
        /// </summary>
        public UnityEvent RevealComplete => _revealComplete;

        /// <summary>Reveal units in the laid-out text, under the current granularity.</summary>
        public int RevealUnitCount
        {
            get { EnsureUnits(); return Mathf.Max(0, _unitStarts.Count - 1); }
        }

        /// <summary>Units fully revealed right now.</summary>
        public int RevealedUnits
        {
            get
            {
                EnsureUnits();
                return _maxVisibleGraphemes < 0
                    ? Mathf.Max(0, _unitStarts.Count - 1)
                    : RevealUnits.RevealedBy(_unitStarts, _maxVisibleGraphemes);
            }
        }

        /// <summary>
        /// The grapheme cluster a reveal unit starts at; assign it to
        /// <see cref="MaxVisibleGraphemes"/> to show exactly that many units.
        /// </summary>
        public int GraphemeOfRevealUnit(int unit)
        {
            EnsureUnits();
            return _unitStarts[Mathf.Clamp(unit, 0, _unitStarts.Count - 1)];
        }

        /// <summary>
        /// Jumps to fully revealed.
        ///
        /// Fires <see cref="RevealComplete"/> once and fires NOTHING per unit:
        /// not <see cref="CharacterRevealed"/> and not
        /// <see cref="GraphemeRevealed"/>. This is the button a player mashes to
        /// stop the typing, and replaying two hundred clicks in a single frame
        /// is the opposite of what they asked for; a dialogue system that needs
        /// to know has RevealComplete, which is one event and says so.
        ///
        /// Leaves the reveal at -1 rather than at the grapheme count, because -1
        /// is "nothing is holding text back" (the state an untyped label is in
        /// and the state the Scene view draws), so a skipped label and a label
        /// that never typed are the same label.
        /// </summary>
        public void SkipToEnd()
        {
            _revealBudget = 0f;
            _revealPause = 0f;
            _revealFresh = true;
            MaxVisibleGraphemes = -1;
            // Explicitly as well as through the setter: a label already fully
            // revealed does not move, and "skip" still has to mean it is over.
            RaiseRevealComplete();
        }

        /// <summary>
        /// Advances the typewriter by <paramref name="deltaSeconds"/>. Called
        /// from Update in play mode; public so a cutscene system with its own
        /// clock can drive it, and so this is testable without a running game.
        ///
        /// Does nothing while <see cref="CharactersPerSecond"/> is 0, and
        /// returns immediately once the reveal is finished, which is what lets
        /// the label's clock stop rather than dirtying a mesh for ever.
        /// </summary>
        public void AdvanceReveal(float deltaSeconds)
        {
            if (_charactersPerSecond <= 0f || deltaSeconds <= 0f) return;
            // Before touching the layout, because this runs every frame on
            // every typing label: -1 is "nothing is holding text back", where a
            // skipped or never-started reveal sits, and neither has work.
            if (_maxVisibleGraphemes < 0) return;
            EnsureUnits();
            int units = _unitStarts.Count - 1;
            if (units <= 0) return;

            int revealed = RevealUnits.RevealedBy(_unitStarts, _maxVisibleGraphemes);
            if (revealed >= units) return;

            // Fresh, or moved by somebody else since the last step: a script
            // scrubbing the reveal is not a step, so the banked time and a pause
            // owed at the old position are both meaningless and would otherwise
            // fire at whatever the scrub landed on.
            if (_revealFresh || revealed != _revealCursor)
            {
                _revealFresh = false;
                _revealBudget = 0f;
                // A <wait> standing in front of this unit still holds; written
                // before the first character, it holds the whole line back,
                // which is what a writer means by putting it there.
                _revealPause = WaitBefore(revealed);
            }

            _revealBudget += deltaSeconds;
            float perUnit = 1f / _charactersPerSecond;
            while (revealed < units)
            {
                if (_revealPause > 0f)
                {
                    float spent = Mathf.Min(_revealPause, _revealBudget);
                    // The pause is paid out of the same budget the steps are,
                    // so a pause that ends halfway through a frame lets the rest
                    // of that frame type. Skipping the pause and charging the
                    // frame anyway is how a long delay turns into a burst.
                    _revealPause -= spent;
                    _revealBudget -= spent;
                    if (_revealPause > 0f) break;
                }
                if (_revealBudget < perUnit) break;
                _revealBudget -= perUnit;
                revealed++;
                // What is owed before the next one: the pause the punctuation
                // just revealed asks for, plus any <wait> written at this exact
                // point. They add: a beat after a full stop and an explicit
                // pause after it are two pauses, not the louder of two.
                _revealPause = PunctuationDelayAfter(revealed - 1) + WaitBefore(revealed);
            }

            _revealCursor = revealed;
            MaxVisibleGraphemes = _unitStarts[revealed];
        }

        /// <summary>
        /// Rewinds the typewriter to the top of the text and lets it run again.
        ///
        /// New text does this by itself in play mode; this is for retyping the
        /// same line, and for driving the reveal from an editor tool or a test,
        /// where there is no play clock to opt in with. Does nothing to the
        /// reveal while <see cref="CharactersPerSecond"/> is 0: with no
        /// typewriter to rewind, blanking the label would leave it blank.
        /// </summary>
        public void RestartReveal()
        {
            _revealBudget = 0f;
            _revealPause = 0f;
            _revealFresh = true;
            _revealCompleteFired = false;
            if (_charactersPerSecond > 0f) MaxVisibleGraphemes = 0;
        }

        /// <summary>
        /// The unit table for the current layout and granularity.
        ///
        /// Keyed on <see cref="LayoutRuns"/> rather than on the layout
        /// generation, because a rect resize re-lays out without bumping the
        /// generation and the glyph list this reads cluster values from is
        /// rebuilt when it does.
        /// </summary>
        private void EnsureUnits()
        {
            EnsureLayout();
            if (_unitsValid && _unitsLayoutRuns == _layoutRuns &&
                _unitsGranularity == _revealGranularity) return;

            RevealUnits.Build(_layout, DisplayText, _revealGranularity, _unitStarts);
            BuildWaits();
            _unitsValid = true;
            _unitsLayoutRuns = _layoutRuns;
            _unitsGranularity = _revealGranularity;
        }

        /// <summary>
        /// Resolves the parser's <c>&lt;wait&gt;</c> text indices onto unit
        /// boundaries, once per layout, so the per-step lookup is a walk over a
        /// handful of entries instead of a binary search into the grapheme
        /// table every frame.
        /// </summary>
        private void BuildWaits()
        {
            _waits.Clear();
            EnsureDisplayText();
            if (!_markup.HasMarkup || _markup.Waits.Count == 0) return;

            foreach (var (index, seconds) in _markup.Waits)
            {
                int unit = RevealUnits.FirstUnitAtOrAfter(_unitStarts, GraphemeAtOrAfter(index));
                // Two pauses written at one point are one longer pause, which is
                // plainly what <wait=0.2><wait=0.3> means.
                if (_waits.Count > 0 && _waits[_waits.Count - 1].Unit == unit)
                    _waits[_waits.Count - 1] = (unit, _waits[_waits.Count - 1].Seconds + seconds);
                else _waits.Add((unit, seconds));
            }
        }

        /// <summary>
        /// The first grapheme cluster starting at or after a text index.
        ///
        /// <see cref="TextLayoutResult.GraphemeAt"/> is the wrong question for a
        /// pause: it clamps into the last cluster, so a <c>&lt;wait&gt;</c>
        /// written at the very end of the string would pause before the last
        /// character rather than after it.
        /// </summary>
        private int GraphemeAtOrAfter(int textIndex)
        {
            var starts = _layout.GraphemeStarts;
            if (starts.Count == 0) return 0;
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (starts[mid] < textIndex) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private float WaitBefore(int unit)
        {
            for (int i = 0; i < _waits.Count; i++)
                if (_waits[i].Unit == unit) return _waits[i].Seconds;
            return 0f;
        }

        /// <summary>
        /// The delay a just-revealed unit asks for: the longest row matching any
        /// character in it.
        ///
        /// The whole unit, not its first character: under Syllable granularity
        /// 。 arrives attached to the character before it, and a table that only
        /// looked at where a unit starts would never see a full stop at all.
        /// Longest rather than sum, because "！？" is one beat and not two.
        /// </summary>
        private float PunctuationDelayAfter(int unit)
        {
            if (_punctuationDelays.Count == 0 || string.IsNullOrEmpty(_displayText)) return 0f;

            var starts = _layout.GraphemeStarts;
            int from = starts[_unitStarts[unit]];
            int to = starts[_unitStarts[unit + 1]];
            float longest = 0f;
            for (int i = from; i < to && i < _displayText.Length; i++)
            {
                char c = _displayText[i];
                for (int d = 0; d < _punctuationDelays.Count; d++)
                {
                    var entry = _punctuationDelays[d];
                    if (string.IsNullOrEmpty(entry.Characters)) continue;
                    if (entry.Characters.IndexOf(c) >= 0 && entry.Seconds > longest)
                        longest = entry.Seconds;
                }
            }
            return longest;
        }

        /// <summary>
        /// A post-layout pass over this label's tiles. Null for none.
        ///
        /// The modifier may move, scale, rotate and tint; it may not change what
        /// the text says or where it wraps, because it runs after both were
        /// decided. That restriction is the feature: text that is merely moving
        /// costs vertex writes, not a rebuild.
        /// </summary>
        public ITextQuadModifier QuadModifier
        {
            get => _modifier;
            set
            {
                _modifier = value;
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// Time handed to the quad modifier. The frontend does not advance it;
        /// whatever drives the animation does, so a paused game pauses the text
        /// without the text having to know what "paused" means.
        /// </summary>
        public float AnimationTime
        {
            get => _animationTime;
            set
            {
                // Exact compare, not Mathf.Approximately: its tolerance is
                // relative, so once the clock is large enough a whole frame's
                // delta falls inside it and the animation silently stops. The
                // only thing worth skipping here is an exact no-op.
                if (_animationTime == value) return;
                _animationTime = value;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            long startedAt = AtlasDiagnostics.Now;
            try { PopulateMesh(vh); }
            finally
            {
                AtlasDiagnostics.Add(ref AtlasDiagnostics.RebuildTicks, startedAt);
                if (AtlasDiagnostics.Enabled) AtlasDiagnostics.RebuildCount++;
            }
        }

        private void PopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (string.IsNullOrEmpty(_text) || !EnsureNativeState()) return;

            long layoutStartedAt = AtlasDiagnostics.Now;
            long heapBefore = AtlasDiagnostics.Heap;
            EnsureLayout();
            AtlasDiagnostics.Add(ref AtlasDiagnostics.LayoutTicks, layoutStartedAt);
            if (AtlasDiagnostics.Enabled)
            {
                AtlasDiagnostics.LayoutCount++;
                long afterLayout = AtlasDiagnostics.Heap;
                if (afterLayout > heapBefore) AtlasDiagnostics.LayoutBytes += afterLayout - heapBefore;
                _meshHeapMark = afterLayout;
            }

            // Fetched per rebuild, not cached: changing the atlas budget in
            // Project Settings replaces the atlas underneath us. A precise
            // label draws from the multi-channel atlas, which is created the
            // first time one asks for it and never otherwise.
            var atlas = _precise ? SharedGlyphAtlas.PreciseAtlas : SharedGlyphAtlas.Atlas;
            CharsetRecorder.Record(DisplayText, EffectiveFontSize);

            // Quad generation depends on the layout and on the atlas, and on
            // nothing an animation frame touches. Regenerating it per frame
            // (clustering every run, hashing every tile, walking the grapheme
            // table per quad) is precisely the "rebuild time" this design
            // promises animated text does not pay. The label's colour is one of
            // those live things: it is multiplied in at emit, never baked into
            // the cached quads, so a colour tween redraws at animation cost.
            int colorVersion = SharedGlyphAtlas.ColorAtlasExists
                ? SharedGlyphAtlas.ColorAtlas.Version
                : 0;
            int spriteVersion = _sprites != null ? _sprites.GetInstanceID() : 0;

            if (_quadsValid && _quadsLayoutGeneration == _layoutGeneration &&
                _quadsAtlasVersion == atlas.Version && _quadsColorVersion == colorVersion &&
                _quadsSpriteVersion == spriteVersion)
            {
                EmitQuads(vh);
                AtlasFlushScheduler.Request();
                if (SharedGlyphAtlas.ColorAtlasExists) SharedGlyphAtlas.ColorAtlas.Flush();
                return;
            }

            _quads.Clear();
            bool vertical = IsVertical;
            // Rebuilt with the quads that index into it, never separately: a
            // table left over from the previous text would have its slots
            // pointing at decorations from a string that is no longer on screen.
            _decorations.Clear();
            _decorations.Add(TextDecoration.None);
            _packedDecorations.Clear();
            _packedDecorations.Add(default);

            // Every wash first, before any glyph: a <mark> is behind all of the
            // text and not merely behind its own run, which is the difference
            // an italic overhanging into the next run would otherwise show.
            EmitBands(vertical, behind: true);

            int runIndex = 0;
            foreach (var run in _layout.Runs)
            {
                var font = run.Font;
                // Per run, not per label: <size> means a run can be a different
                // size from the one the label was configured with, and the
                // atlas density bucket has to follow it or the tile is fetched
                // at the wrong resolution.
                float runSize = run.FontSize > 0f ? run.FontSize : EffectiveFontSize;
                int runPpem = GlyphAtlas.QuantizePixelsPerEm(runSize);
                float scale = runSize / font.UnitsPerEm;
                // Only the tag's colour is baked. The label's own colour is
                // multiplied in at emit time, so tinting or fading a label
                // never invalidates these quads, and never re-bakes a colour
                // tile, which would otherwise put one tile per fade step into
                // the atlas.
                var runColor = run.Style.HasColor ? run.Style.Color : new Color32(255, 255, 255, 255);
                float unitsPerTilePixel = font.UnitsPerEm / (float)runPpem;
                // A cluster's merged tile must fit the atlas; cap its ink width.
                float maxClusterUnits = 1000f * unitsPerTilePixel;
                // Glyphs whose ink joins must share a cluster; anything further
                // apart stays its own tile so the cache survives text edits.
                float mergeGapUnits = GlyphClusters.DefaultMergeGapUnits(font);

                var frame = FrameOf(run, vertical, scale);

                // A sprite run has no glyphs to look up: its one synthetic
                // glyph is an advance, and the picture comes from the sheet.
                if (run.Style.IsSprite)
                {
                    EmitSprite(run, runSize, scale, frame, runColor, runIndex);
                    runIndex++;
                    continue;
                }

                // A colour font's glyphs are pictures, not distance fields:
                // there is nothing to merge and no seam to avoid, so they skip
                // the clustering entirely and go one glyph to one tile.
                if (ColorGlyphs.IsColorFont(font))
                {
                    EmitColorRun(run, font, runSize, scale, frame, runColor, runIndex, atlas);
                    runIndex++;
                    continue;
                }

                // Each cluster of ink-overlapping glyphs bakes as ONE merged SDF,
                // so joints between connected glyphs live in the field's interior;
                // there is no boundary left to seam. Upright text in a column is
                // the exception and takes a tile per glyph; the comment on
                // SplitUpright says why merging has nothing to find there.
                long splitStartedAt = AtlasDiagnostics.Now;
                if (frame.Vertical && !frame.Rotated)
                {
                    GlyphClusters.SplitUpright(font, _layout.Glyphs, run.GlyphStart, run.GlyphCount,
                        _clusters, _positioned);
                }
                else
                {
                    GlyphClusters.Split(font, _layout.Glyphs, run.GlyphStart, run.GlyphCount,
                        _clusters, _positioned, maxClusterUnits, mergeGapUnits);
                }
                AtlasDiagnostics.Add(ref AtlasDiagnostics.SplitTicks, splitStartedAt);

                // Bake everything this run is missing in one dispatch: a job per
                // glyph spends more on scheduling than on the field itself.
                long lookupStartedAt = AtlasDiagnostics.Now;
                atlas.PrepareClusters(font, runSize, _positioned, _clusters);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.LookupTicks, lookupStartedAt);

                foreach (var cluster in _clusters)
                {
                    lookupStartedAt = AtlasDiagnostics.Now;
                    var loc = atlas.GetOrAddCluster(font, runSize,
                        _positioned, cluster.Start, cluster.Count, cluster.Hash);
                    AtlasDiagnostics.Add(ref AtlasDiagnostics.LookupTicks, lookupStartedAt);
                    if (!loc.HasPixels) continue;

                    frame.Place(cluster.PenX, cluster.PenY, loc.OriginUnits, loc.SizeUnits,
                        out var position, out var size, out float rotation);
                    _quads.Add(new TextQuad
                    {
                        Position = position,
                        Size = size,
                        Rotation = rotation,
                        UvRect = loc.UvRect,
                        Layer = loc.Layer,
                        Color = runColor,
                        FirstGrapheme = _layout.GraphemeAt(cluster.TextStart),
                        LastGrapheme = _layout.GraphemeAt(Mathf.Max(cluster.TextStart, cluster.TextEnd - 1)),
                        RunIndex = runIndex,
                        Baseline = frame.Baseline,
                        Style = run.Style,
                        Decoration = ResolveDecoration(cluster.TextStart, run.Style),
                        IsPrecise = _precise,
                    });
                }
                runIndex++;
            }

            // And the lines last, over everything: an underline is drawn on top
            // of the descender it crosses, which is what every renderer that
            // does not carve the glyph out of the stroke does.
            EmitBands(vertical, behind: false);

            // The quads are now current for this layout and these atlases;
            // arming the cache here, at the end of the build, is what lets the
            // next animation frame skip straight to EmitQuads.
            _quadBuilds++;
            _quadsValid = true;
            _quadsLayoutGeneration = _layoutGeneration;
            _quadsAtlasVersion = atlas.Version;
            _quadsColorVersion = SharedGlyphAtlas.ColorAtlasExists
                ? SharedGlyphAtlas.ColorAtlas.Version
                : 0;
            _quadsSpriteVersion = spriteVersion;

            EmitQuads(vh);

            if (AtlasDiagnostics.Enabled)
            {
                long afterMesh = AtlasDiagnostics.Heap;
                if (afterMesh > _meshHeapMark) AtlasDiagnostics.MeshBytes += afterMesh - _meshHeapMark;
            }

            // One upload per frame for the whole canvas, not one per label.
            AtlasFlushScheduler.Request();
            if (SharedGlyphAtlas.ColorAtlasExists) SharedGlyphAtlas.ColorAtlas.Flush();
        }

        // ------------------------------------------------------------ ILayoutElement

        public void CalculateLayoutInputHorizontal() { }

        public void CalculateLayoutInputVertical() { }

        public float minWidth => 0f;

        public float minHeight => 0f;

        // The two preferred sizes are one question asked twice: how long is
        // the text when nothing constrains it, and how far does it stack when
        // the other side is held to the box? Which of width and height is
        // which swaps with the writing mode, because it is the inline axis
        // that runs unconstrained and the block axis that stacks.

        public float preferredWidth
        {
            get
            {
                if (!EnsureNativeState()) return 0f;
                if (IsVertical)
                {
                    // Down a column, width is the stack: how far the columns
                    // reach once each has been cut to the box's height.
                    float height = _wrap == TextWrap.Wrap ? rectTransform.rect.height : 0f;
                    _engine.Layout(DisplayText, BuildSettings(0f, height), _measure);
                    return _measure.Width;
                }
                var settings = BuildSettings(0f, 0f);
                settings.Wrap = TextWrap.NoWrap;
                _engine.Layout(DisplayText, settings, _measure);
                return _measure.Width;
            }
        }

        public float preferredHeight
        {
            get
            {
                if (!EnsureNativeState()) return 0f;
                if (IsVertical)
                {
                    // And height is the run: one unwrapped column, end to end.
                    var vertical = BuildSettings(0f, 0f);
                    vertical.Wrap = TextWrap.NoWrap;
                    _engine.Layout(DisplayText, vertical, _measure);
                    return _measure.Height;
                }
                float width = _wrap == TextWrap.Wrap ? rectTransform.rect.width : 0f;
                _engine.Layout(DisplayText, BuildSettings(width, 0f), _measure);
                return _measure.Height;
            }
        }

        public float flexibleWidth => -1f;

        public float flexibleHeight => -1f;

        public int layoutPriority => 0;

#if ONETEXT_UGUI_HAS_MAX_SIZE
        // uGUI 2.6 added max-size members to ILayoutElement. Implementing them
        // unconditionally would not compile against older uGUI, and not
        // implementing them stops the package compiling against newer uGUI;
        // hence the version define (see OneText.UGUI.asmdef). A label imposes
        // no maximum of its own; negative means "not set", as it does for
        // flexible size.
        public float maxWidth => -1f;

        public float maxHeight => -1f;
#endif

        /// <summary>
        /// The label's colour tinted by a <c>&lt;color&gt;</c> tag. Multiplying
        /// rather than replacing is what makes fading a label out still fade
        /// its coloured words: the tag says "red", not "opaque red".
        /// </summary>
        private static Color32 Multiply(Color32 a, Color32 b) => new Color32(
            (byte)(a.r * b.r / 255), (byte)(a.g * b.g / 255),
            (byte)(a.b * b.b / 255), (byte)(a.a * b.a / 255));

        private static void EmitQuad(VertexHelper vh, float x, float y, float w, float h,
            Rect uv, int layer, Color32 c, float atlas, in DecorationChannels decoration)
        {
            int start = vh.currentVertCount;
            AddVert(vh, x, y, uv.xMin, uv.yMin, layer, uv, c, atlas, decoration);
            AddVert(vh, x, y + h, uv.xMin, uv.yMax, layer, uv, c, atlas, decoration);
            AddVert(vh, x + w, y + h, uv.xMax, uv.yMax, layer, uv, c, atlas, decoration);
            AddVert(vh, x + w, y, uv.xMax, uv.yMin, layer, uv, c, atlas, decoration);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        /// <summary>
        /// A tile turned about its own centre. Separate from the axis-aligned
        /// path because that one is the overwhelmingly common case and a
        /// sin/cos per corner is not something to pay for a still letter.
        /// </summary>
        private static void EmitRotatedQuad(VertexHelper vh, in TextQuad quad,
            in DecorationChannels decoration)
        {
            float radians = quad.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
            var centre = quad.Center;
            var half = quad.Size * 0.5f;
            var uv = quad.UvRect;

            Vector2 Corner(float sx, float sy)
            {
                float x = sx * half.x, y = sy * half.y;
                return centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            }

            int start = vh.currentVertCount;
            AddVertAt(vh, Corner(-1f, -1f), uv.xMin, uv.yMin, quad, decoration);
            AddVertAt(vh, Corner(-1f, 1f), uv.xMin, uv.yMax, quad, decoration);
            AddVertAt(vh, Corner(1f, 1f), uv.xMax, uv.yMax, quad, decoration);
            AddVertAt(vh, Corner(1f, -1f), uv.xMax, uv.yMin, quad, decoration);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddVertAt(VertexHelper vh, Vector2 at, float u, float v,
            in TextQuad quad, in DecorationChannels decoration) =>
            AddVert(vh, at.x, at.y, u, v, quad.Layer, quad.UvRect, quad.Color, AtlasOf(quad), decoration);

        /// <summary>
        /// Which atlas a tile samples, as the shader's discriminator: 0 the
        /// single-channel field, 1 the colour picture, 2 the multi-channel
        /// field. One number rather than a material per atlas, which is what
        /// keeps all three in one draw call.
        /// </summary>
        private static float AtlasOf(in TextQuad quad) =>
            quad.IsSolid ? 3f : quad.IsColor ? 1f : quad.IsPrecise ? 2f : 0f;

        /// <summary>
        /// The decoration parameters as the two vertex channels carry them.
        /// Packed once per tile and written to all four of its corners, because
        /// packing is a dozen rounds and multiplies and a corner is not a
        /// different decoration from the corner beside it.
        /// </summary>
        private readonly struct DecorationChannels
        {
            public readonly Vector4 Colors, Shape;

            public DecorationChannels(Vector4 colors, Vector4 shape)
            {
                Colors = colors;
                Shape = shape;
            }
        }

        /// <summary>
        /// Packs a decoration into the two channels; see the budget above
        /// <see cref="AddVert"/>. A part that is not set is written with a zero
        /// in the byte the shader tests, which is what makes "undecorated" a
        /// value rather than a branch.
        /// </summary>
        private static DecorationChannels Pack(in TextDecoration decoration)
        {
            byte outlineWidth = decoration.HasOutline
                ? TextDecoration.Quantize(decoration.OutlineWidth) : (byte)0;
            byte shadowAlpha = decoration.HasShadow ? decoration.ShadowColor.a : (byte)0;
            byte glowAlpha = decoration.HasGlow ? decoration.GlowColor.a : (byte)0;

            var colors = new Vector4(
                TextDecoration.Pack(decoration.OutlineColor.r, decoration.OutlineColor.g),
                TextDecoration.Pack(decoration.OutlineColor.b, outlineWidth),
                TextDecoration.Pack(decoration.ShadowColor.r, decoration.ShadowColor.g),
                TextDecoration.Pack(decoration.ShadowColor.b, shadowAlpha));
            var shape = new Vector4(
                TextDecoration.Pack(decoration.GlowColor.r, decoration.GlowColor.g),
                TextDecoration.Pack(decoration.GlowColor.b, glowAlpha),
                TextDecoration.Pack(
                    TextDecoration.QuantizeSigned(decoration.ShadowOffset.x, 1f),
                    TextDecoration.QuantizeSigned(decoration.ShadowOffset.y, 1f)),
                TextDecoration.Pack(
                    TextDecoration.Quantize(decoration.ShadowSoftness),
                    TextDecoration.Quantize(decoration.GlowRadius)));
            return new DecorationChannels(colors, shape);
        }

        /// <summary>
        /// The vertex-channel budget, in full, because it is a contract with
        /// <c>OneText-SDF.shader</c> and there is no compiler between the two.
        ///
        /// TEXCOORD0  xy tile uv · z atlas layer · w tile v-min
        /// TEXCOORD1  outline R|G · outline B|width · shadow R|G · shadow B|A
        /// TEXCOORD2  x tile v-max · y tile u-min · z tile u-max · w which atlas
        /// TEXCOORD3  glow R|G · glow B|A · shadow dx|dy · shadow soft|glow radius
        ///
        /// <b>Nothing here is new.</b> The canvas is already asked for
        /// TexCoord1/2/3 (see EnsureMaterial), and TEXCOORD1, TEXCOORD3 and
        /// TEXCOORD2.yz were the second and third sweep-line samples and their
        /// v-maxes, dead since joints moved into the field's interior with
        /// cluster-union rasterization, and written as an unused-slot sentinel
        /// ever since. So decorations cost <em>zero</em> extra bytes per vertex,
        /// and a decorated label still batches with an undecorated one because
        /// nothing about the material changed.
        ///
        /// Normal and Tangent stay off the canvas deliberately. Turning them on
        /// costs seven floats per vertex on <em>every</em> graphic in that
        /// canvas (Images, other people's components, not just ours), which is
        /// a bill we would be handing to the whole project for a drop shadow.
        ///
        /// Two bytes per float, never three: an interpolator that hands back the
        /// constant it was given off by one unit in the last place would, at
        /// three-byte magnitudes, borrow across a field boundary and jump a
        /// colour channel by 255.
        ///
        /// The tile's u bounds are here for the shadow, and only for it: the
        /// face samples inside its own quad by construction, but an offset
        /// sample walks sideways out of the tile and into whatever glyph the
        /// atlas shelf packed next to it: a ghost of an unrelated letter, drawn
        /// as this one's shadow.
        ///
        /// MSDF landed where this said it would: nowhere here. A multi-channel
        /// field changes the atlas format and the coverage maths, not the
        /// per-vertex data; it took the third value of the TEXCOORD2.w
        /// discriminator that already told the colour atlas from the SDF one.
        /// </summary>
        private static void AddVert(VertexHelper vh, float x, float y, float u, float v,
            int layer, Rect uvRect, Color32 c, float atlas, in DecorationChannels decoration)
        {
            var uvA = new Vector4(u, v, layer, uvRect.yMin);
            // vmax.w picks the atlas: single-channel field, colour picture or
            // multi-channel field. It fits in a channel the mesh already
            // carries, so emoji and precise text cost no extra vertex data and
            // no second draw call, which is the whole reason both are flags
            // rather than submeshes.
            var vmax = new Vector4(uvRect.yMax, uvRect.xMin, uvRect.xMax, atlas);
            vh.AddVert(new Vector3(x, y), c, uvA, decoration.Colors, vmax, decoration.Shape,
                s_Normal, s_Tangent);
        }
    }
}
