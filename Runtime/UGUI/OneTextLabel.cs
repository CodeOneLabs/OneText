using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed partial class OneTextLabel : MaskableGraphic, ILayoutElement, IPointerClickHandler,
        StyleInvalidation.IStyleUser
    {
        [Tooltip("Base style asset. The label stores the reference, not a copy; editing the " +
                 "style updates every label using it, in the editor and at runtime.")]
        [SerializeField] private OneTextStyle _style;

        [Tooltip("Styles <style=name> may reference. The asset's own name is the name markup uses.")]
        [SerializeField] private List<OneTextStyle> _namedStyles = new List<OneTextStyle>();

        /// <summary>
        /// An outline, shadow or glow this label wants under everything else.
        /// What the inspector's Decorations table edits.
        ///
        /// Separate from <see cref="_style"/> because that is a shared asset:
        /// a label reaching for "give this one an outline" would be editing
        /// every label using the same style, which is never what was meant. It
        /// sits under the style rather than over it, so a theme still wins —
        /// this is the label's opinion, not its final say.
        ///
        /// And separate from the text, which is where the table once wrote it
        /// as tags, because the text is the one thing an external system owns:
        /// a Localize String Event or a data binding replaces the whole
        /// string, and a decoration riding in it went along. Tags remain the
        /// way to decorate a span; the whole label is decorated here.
        /// </summary>
        [SerializeField] private TextDecoration _decoration;

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
        [SerializeField] private string _text = "New Text";

        [SerializeField] private float _fontSize = 36f;

        [Tooltip("Pick the largest size between Min and Max at which the whole text fits " +
                 "the rect. The Size field above is ignored while this is on.")]
        [SerializeField] private bool _autoSize;
        [SerializeField] private float _autoSizeMin = 10f;
        [SerializeField] private float _autoSizeMax = 128f;

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

        [Tooltip("Interpret backslash escapes: \\n \\t \\v \\r \\\\ \\uXXXX \\UXXXXXXXX. " +
                 "Localization tables store a newline as the two characters \\n; this " +
                 "turns them into the real thing. Unknown escapes stay literal.")]
        [SerializeField] private bool _parseEscapes = true;

        [Tooltip("Precise (MSDF): multi-channel distance field. Use for large text or " +
                 "sharp corners/curves; costs more atlas memory (four bytes a texel instead " +
                 "of one, in an atlas of its own). Off, the label renders through the " +
                 "ordinary single-channel SDF, which is right for body text.")]
        [SerializeField] private bool _precise;

        [Tooltip("Minimum atlas texels per em, as a multiple of what the font size asks " +
                 "for. Canvas scaling and camera zoom are measured and compensated " +
                 "automatically; this floor is for setups the measurement cannot see. " +
                 "Medium is 1.5x, High is 2x, and each costs the square of itself in " +
                 "atlas area. Project takes whichever the project sets, which is " +
                 "Performance (1x) unless somebody changed it.")]
        [SerializeField] private TextQuality _quality = TextQuality.Project;

        [SerializeField] private UnityEvent<string> _linkClicked = new UnityEvent<string>();

        private byte[] _fontBytesOverride;
        private byte[][] _fallbackBytesOverride;
        private FontStack _fonts;
        private readonly List<FontData> _ownedFonts = new List<FontData>();
        // Faces borrowed from SharedFontBytes; released, never disposed.
        private readonly List<FontData> _sharedFonts = new List<FontData>();
        private TextLayoutEngine _engine;
        // NonSerialized because a domain reload resets the atlas's static
        // refcount to zero while serialization would resurrect this as true:
        // the label would skip re-acquiring, and its OnDestroy would then
        // release a reference the new domain never counted, destroying the
        // shared material under every other live label.
        [System.NonSerialized]
        private bool _atlasHeld; // this label's reference to the shared atlas
        private readonly List<FontVariation> _variations = new List<FontVariation>();
        private readonly List<FontVariation> _styleVariations = new List<FontVariation>();
        private readonly TextLayoutResult _layout = new TextLayoutResult();
        private readonly TextLayoutResult _measure = new TextLayoutResult();
        private readonly List<TextLink> _links = new List<TextLink>();
        private readonly RichTextResult _markup = new RichTextResult();
        private string _displayText;
        private bool _parsedValid;
        private bool _parsedRich;
        private bool _parsedEscapes;

        /// <summary>
        /// Everything the laid-out result depends on. Compared by value, so a
        /// rebuild that changes none of it reuses the layout.
        /// </summary>
        private readonly struct LayoutKey : System.IEquatable<LayoutKey>
        {
            // The text as a length and a hash rather than a reference: the
            // text is characters in a buffer now, and the same buffer with
            // different contents is a different layout. The generation counter
            // beside them already changes on every edit, so these two are the
            // belt to its braces.
            private readonly int _length, _hash;
            private readonly float _width, _height, _size, _lineSpacing;
            private readonly TextAlignment _alignment;
            private readonly TextWrap _wrap;
            private readonly TextOverflow _overflow;
            private readonly int _generation;
            private readonly bool _autoSize;
            private readonly float _autoSizeMin, _autoSizeMax;

            public LayoutKey(int length, int hash, float width, float height, float size, float lineSpacing,
                TextAlignment alignment, TextWrap wrap, TextOverflow overflow, int generation,
                bool autoSize, float autoSizeMin, float autoSizeMax)
            {
                _length = length;
                _hash = hash;
                _width = width;
                _height = height;
                _size = size;
                _lineSpacing = lineSpacing;
                _alignment = alignment;
                _wrap = wrap;
                _overflow = overflow;
                _generation = generation;
                _autoSize = autoSize;
                _autoSizeMin = autoSizeMin;
                _autoSizeMax = autoSizeMax;
            }

            public bool Equals(LayoutKey other) =>
                _length == other._length && _hash == other._hash &&
                _width.Equals(other._width) && _height.Equals(other._height) &&
                _size.Equals(other._size) && _lineSpacing.Equals(other._lineSpacing) &&
                _alignment == other._alignment && _wrap == other._wrap &&
                _overflow == other._overflow && _generation == other._generation &&
                _autoSize == other._autoSize && _autoSizeMin.Equals(other._autoSizeMin) &&
                _autoSizeMax.Equals(other._autoSizeMax);
        }

        /// <summary>
        /// A hash of the text as laid out, for the layout cache key. FNV-1a
        /// over the characters: cheap enough to run per layout call, and it
        /// only has to disagree when the text does.
        /// </summary>
        private int DisplayHash
        {
            get
            {
                var text = _markup.TextSpan;
                unchecked
                {
                    int hash = (int)2166136261;
                    for (int i = 0; i < text.Length; i++) hash = (hash ^ text[i]) * 16777619;
                    return hash;
                }
            }
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

        /// <summary>
        /// The text, as a string.
        ///
        /// Reading it after one of the <see cref="SetText(ReadOnlySpan{char})"/>
        /// overloads builds the string those exist to avoid, once, and caches
        /// it — so a game that formats into the buffer every frame and never
        /// reads this back allocates nothing, and one that does read it pays
        /// exactly what it would have paid anyway.
        /// </summary>
        public string Text
        {
            get => _text ??= _sourceLength == 0
                ? string.Empty
                : new string(_sourceBuffer, 0, _sourceLength);
            set
            {
                // Assigning the string the label already holds used to cost a
                // full rebuild — re-parse, re-layout, re-quad — for a result
                // that could not differ, and a scene of labels refreshed from
                // game state pays that every frame. TextMeshPro's setter has
                // guarded this since it existed; measured here it was 380 bytes
                // and a relayout per label per frame.
                if (SourceEquals(value))
                {
                    // The one thing a same-value assignment did that mattered:
                    // a running typewriter retyped the line. RestartReveal is
                    // the documented way to ask for that and touches only the
                    // reveal counters, so the behaviour survives without any of
                    // the work above.
                    if (_charactersPerSecond > 0f && Application.isPlaying) RestartReveal();
                    return;
                }
                _text = value;
                CopyIntoSource(value.AsSpan());
                InvalidateText();
            }
        }

        /// <summary>
        /// The text, in characters this label owns.
        ///
        /// Every setter writes here and the whole pipeline reads it as a span,
        /// so text that arrives as characters — a formatted number, a char[],
        /// a StringBuilder — never has to become a string on the way in. The
        /// serialized field is the same characters when they came from one.
        /// </summary>
        [System.NonSerialized] private char[] _sourceBuffer = new char[16];
        [System.NonSerialized] private int _sourceLength;

        internal ReadOnlySpan<char> SourceSpan
        {
            get
            {
                // A label asked for its text before it was ever enabled — an
                // editor preview, a layout query on a fresh instance — has the
                // serialized string and an empty buffer.
                if (_sourceLength == 0 && !string.IsNullOrEmpty(_text)) CopyIntoSource(_text.AsSpan());
                return new ReadOnlySpan<char>(_sourceBuffer, 0, _sourceLength);
            }
        }

        private bool SourceEquals(ReadOnlySpan<char> value) =>
            value.Length == _sourceLength && value.SequenceEqual(SourceSpan);

        private bool SourceEquals(string value) =>
            value == null ? _sourceLength == 0 : SourceEquals(value.AsSpan());

        /// <summary>
        /// Copies the serialized string into the buffer the pipeline reads.
        ///
        /// Unity fills the field, not the buffer: on deserialization, on a
        /// prefab revert, and on every inspector edit. A label whose buffer was
        /// filled at runtime and whose string is therefore null keeps what it
        /// has — that text did not come from the field and the field is not
        /// where it lives.
        /// </summary>
        private void SyncSourceFromSerialized()
        {
            if (_text == null) return;
            if (SourceEquals(_text)) return;
            CopyIntoSource(_text.AsSpan());
            _parsedValid = false;
        }

        private void CopyIntoSource(ReadOnlySpan<char> value)
        {
            if (_sourceBuffer.Length < value.Length)
                _sourceBuffer = new char[Mathf.Max(16, Mathf.NextPowerOfTwo(value.Length))];
            value.CopyTo(_sourceBuffer);
            _sourceLength = value.Length;
        }

        /// <summary>
        /// Sets the text from characters, without a string anywhere.
        ///
        /// This is the overload a score, a timer or a countdown wants: format
        /// into a buffer once, hand it over, and nothing on the way to the
        /// screen allocates. <see cref="Text"/> still answers with a string if
        /// something asks, built at that moment rather than every frame.
        /// </summary>
        public void SetText(ReadOnlySpan<char> value)
        {
            if (SourceEquals(value))
            {
                if (_charactersPerSecond > 0f && Application.isPlaying) RestartReveal();
                return;
            }
            CopyIntoSource(value);
            _text = null;
            InvalidateText();
        }

        /// <inheritdoc cref="SetText(ReadOnlySpan{char})"/>
        public void SetText(char[] value, int start, int length) =>
            SetText(new ReadOnlySpan<char>(value, start, length));

        /// <summary>
        /// Sets the text to a whole number, written straight into the label's
        /// own buffer. <c>label.SetText(score)</c> where <c>label.Text =
        /// score.ToString()</c> would make a string a frame.
        /// </summary>
        public void SetText(int value)
        {
            Span<char> digits = stackalloc char[12];
            SetText(digits.Slice(0, WriteInt(digits, value)));
        }

        /// <summary>
        /// Sets the text to a number with <paramref name="decimals"/> places,
        /// rounded half away from zero, written into the label's own buffer.
        /// </summary>
        public void SetText(float value, int decimals)
        {
            Span<char> buffer = stackalloc char[32];
            SetText(buffer.Slice(0, WriteFloat(buffer, value, decimals)));
        }

        /// <summary>Digits and a sign, written backwards then reversed.</summary>
        private static int WriteInt(Span<char> buffer, long value)
        {
            bool negative = value < 0;
            // long.MinValue has no positive counterpart, so the digits come off
            // the negative side and the sign is put on afterwards.
            int written = 0;
            do
            {
                long digit = value % 10;
                buffer[written++] = (char)('0' + (negative ? -digit : digit));
                value /= 10;
            } while (value != 0);
            if (negative) buffer[written++] = '-';
            for (int i = 0, j = written - 1; i < j; i++, j--)
            {
                char swap = buffer[i];
                buffer[i] = buffer[j];
                buffer[j] = swap;
            }
            return written;
        }

        private static int WriteFloat(Span<char> buffer, float value, int decimals)
        {
            decimals = Mathf.Clamp(decimals, 0, 9);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                // Nothing sensible to draw, and a format exception in a frame
                // loop is worse than a zero.
                buffer[0] = '0';
                return 1;
            }

            long scale = 1;
            for (int i = 0; i < decimals; i++) scale *= 10;
            bool negative = value < 0f;
            double scaled = System.Math.Round(System.Math.Abs((double)value) * scale,
                System.MidpointRounding.AwayFromZero);
            long units = (long)scaled;

            int written = 0;
            if (negative && units != 0) buffer[written++] = '-';
            written += WriteInt(buffer.Slice(written), units / scale);
            if (decimals == 0) return written;

            buffer[written++] = '.';
            long fraction = units % scale;
            for (long place = scale / 10; place >= 1; place /= 10)
            {
                buffer[written++] = (char)('0' + fraction / place);
                fraction %= place;
            }
            return written;
        }

        public float FontSize
        {
            get => _fontSize;
            set { _fontSize = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        /// <summary>
        /// Fit the text to the rect by choosing the largest size in
        /// [<see cref="AutoSizeMin"/>, <see cref="AutoSizeMax"/>] at which the
        /// whole block fits. While on, <see cref="FontSize"/> is ignored and
        /// <see cref="FittedFontSize"/> reports the chosen size.
        /// </summary>
        public bool AutoSize
        {
            get => _autoSize;
            set
            {
                if (_autoSize == value) return;
                _autoSize = value;
                SetVerticesDirty();
                SetLayoutDirty();
            }
        }

        /// <summary>The smallest size auto-size may shrink to.</summary>
        public float AutoSizeMin
        {
            get => _autoSizeMin;
            set { _autoSizeMin = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        /// <summary>The largest size auto-size may grow to.</summary>
        public float AutoSizeMax
        {
            get => _autoSizeMax;
            set { _autoSizeMax = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        /// <summary>
        /// The size the text is actually drawn at: the fitted size while
        /// <see cref="AutoSize"/> is on, the effective size otherwise. Asking
        /// lays the text out if it is stale, so the answer is always current.
        /// </summary>
        public float FittedFontSize
        {
            get
            {
                EnsureLayout();
                return EffectiveFontSize;
            }
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
            set
            {
                // Guarded because the lineSpacing alias converts units on both
                // sides, and TMP projects write `if (label.lineSpacing != v)
                // label.lineSpacing = v` in Update: the readback is a float
                // round trip and not always bit-equal to what was assigned, so
                // without this that idiom would re-layout the label every
                // frame. The re-converted multiplier IS bit-equal (same
                // arithmetic, same input), so it stops here.
                if (_lineSpacing == value) return;
                _lineSpacing = value;
                SetVerticesDirty();
                SetLayoutDirty();
            }
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
        /// Interpret backslash escapes (\n, \t, \v, \r, \\, \uXXXX,
        /// \UXXXXXXXX) before anything else looks at the text. On by default:
        /// localization data stores a newline as the two characters "\n", and
        /// TextMesh Pro resolves them, so a string out of either would
        /// otherwise print the backslash. An input field turns this off along
        /// with markup: text the user typed is text.
        /// </summary>
        public bool ParseEscapes
        {
            get => _parseEscapes;
            set
            {
                if (_parseEscapes == value) return;
                _parseEscapes = value;
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

        /// <summary>
        /// How dense a tile this label asks the atlas for, as a multiple of
        /// what its font size implies: 1x, 1.5x or 2x, or the project's answer.
        ///
        /// A font size is in canvas units, and a canvas unit is a screen pixel
        /// only while the canvas scale factor is one. Under a CanvasScaler set
        /// to Scale With Screen Size — what most projects ship — a 1080p
        /// reference on a 1440p display puts the factor at 1.33, a phone at 3,
        /// and a label baked for its font size alone is magnified the rest of
        /// the way. The measured screen scale (<see cref="AppliedPpemScale"/>)
        /// now answers that automatically; this rung remains as a floor under
        /// it — "at least this dense" — for the setups the measurement cannot
        /// see, and the larger of the two wins rather than both applying (see
        /// <see cref="DensityFor"/>).
        ///
        /// Changes the tile, never the layout: the same glyphs in the same
        /// places, off a finer field. So it is a quad rebuild and not a
        /// re-layout, exactly as <see cref="Precise"/> is, and two labels at
        /// the same size and rung share their tiles as usual.
        /// </summary>
        public TextQuality Quality
        {
            get => _quality;
            set
            {
                if (_quality == value) return;
                _quality = value;
                _quadsValid = false;
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// The font size the atlas is asked for, as opposed to the one the text
        /// is laid out at. Multiplied here and nowhere else: every geometry
        /// number downstream is in font units, so a denser tile draws at the
        /// same size, and anything that used <c>runSize</c> for both would
        /// silently scale the text by the quality setting.
        ///
        /// Two multipliers meet here and the larger one wins, rather than both
        /// applying: the quality rung is a promise ("at least this dense") and
        /// the measured screen scale is a fact, and a label whose canvas is
        /// scaled by three under a High rung wants 3x, not 6x — the rung was
        /// set to compensate for the very magnification the measurement now
        /// sees. Multiplying them would spend four times the atlas on texels
        /// the screen cannot show.
        ///
        /// And the whole of it is capped, whatever asked: measured zoom,
        /// quality rung, or the font size itself. A distance field's entire
        /// bargain is that density and drawn size need not match — magnified
        /// past the cap it loses fine junction detail, not edge smoothness —
        /// and above the cap the atlas arithmetic stops working: one 256 ppem
        /// Hangul glyph is ~70K texels, fifteen glyphs to a default layer, a
        /// heading to an atlas. A capped label at 288 points draws 2.25x
        /// magnified, which is the trade a text system that bakes at runtime
        /// has to pick on purpose rather than let a font size pick by accident.
        /// </summary>
        private float DensityFor(float runSize)
        {
            float density = runSize * TextQualityScale.ForCanvas(_quality);
            if (_ppemScale > 1f) density = Mathf.Max(density, runSize * _ppemScale);
            return Mathf.Min(density, PpemCap);
        }

        /// <summary>
        /// The densest tile, in pixels per em, a label may ask the atlas for —
        /// from any source: an explicit font size, a quality rung, or the
        /// measured screen scale, which a perspective camera can otherwise
        /// drive without bound. 128 keeps a 36-point label sharp through a
        /// 3.5x zoom and a whole display line inside a fraction of a default
        /// layer; past it text draws magnified, which an SDF does smoothly and
        /// `Precise` does with its corners intact. Raise it for a project that
        /// genuinely inspects giant glyphs and has the atlas budget to match.
        /// </summary>
        public static float PpemCap = 128f;

        /// <summary>
        /// Turns the measured screen density off, leaving the quality rung as
        /// the only multiplier — the pre-0.3 behaviour. A kill switch for
        /// A/B pictures and for projects that manage density by hand.
        /// </summary>
        public static bool DynamicPpem = true;

        /// <summary>
        /// How far the measured scale must move, as a fraction of what was last
        /// applied, before the label re-bakes: the hysteresis that keeps a
        /// camera idling at a bucket boundary from flapping the atlas between
        /// 32 and 40 every frame its float wobbles. A tenth sits under the
        /// smallest gap in the bucket ladder (~1.14x), so crossing a boundary
        /// always requires real movement, never noise.
        /// </summary>
        private const float PpemScaleBand = 0.1f;

        // The screen magnification last APPLIED to this label's density —
        // deliberately not the live measurement, which is re-taken every
        // canvas pass and only lands here when it escapes the band above.
        // NonSerialized: a measurement belongs to a session, and a prefab
        // must not carry one scene's camera distance into another.
        [System.NonSerialized] private float _ppemScale = 1f;


        /// <summary>
        /// The screen scale the atlas density currently includes; 1 until a
        /// measurement says otherwise. Read-only from outside: the Hub shows
        /// it, tests assert it, and the only writer is
        /// <see cref="RefreshPpemScale"/>.
        /// </summary>
        public float AppliedPpemScale => _ppemScale;

        /// <summary>
        /// Re-measures the screen scale and applies it if it escaped the
        /// hysteresis band; true means the cached quads were invalidated and
        /// the caller (the watcher) should dirty the label. Also called at the
        /// top of every mesh build, so a one-shot render — an editor capture, a
        /// label's very first frame — bakes at the measured density instead of
        /// spending its first picture at the wrong one.
        ///
        /// Minification is floored at one: a label zoomed away from stays on
        /// the tiles its font size asks for, which is what it drew before this
        /// existed, and a zoom-out therefore never invalidates anything.
        /// </summary>
        internal bool RefreshPpemScale() =>
            RefreshPpemScale(ScreenPpem.Context.For(canvas));

        /// <summary>
        /// The same, against a canvas context the caller already read. The
        /// watcher polls two hundred labels a frame and they nearly all share
        /// one canvas and one camera; asking per label was most of the cost.
        /// </summary>
        internal bool RefreshPpemScale(in ScreenPpem.Context context)
        {
            if (!DynamicPpem)
            {
                if (_ppemScale == 1f) return false;
                _ppemScale = 1f;
                _quadsValid = false;
                return true;
            }

            float raw = ScreenPpem.Compute(this, context);
            if (raw <= 0f || float.IsNaN(raw) || float.IsInfinity(raw)) return false;
            if (raw < 1f) raw = 1f;

            float ratio = raw / _ppemScale;
            if (ratio < 1f + PpemScaleBand && ratio * (1f + PpemScaleBand) > 1f) return false;

            _ppemScale = raw;
            _quadsValid = false;
            return true;
        }

        /// <summary>Shifts the drawn text inside the box (used for scrolling an input field).</summary>
        public Vector2 ScrollOffset
        {
            get => _scrollOffset;
            set { _scrollOffset = value; SetVerticesDirty(); }
        }

        /// <summary>
        /// The faces this label resolves characters through, in order.
        ///
        /// Public so a tool can ask the same question the renderer asks — which
        /// font would draw this character, and is there one at all. The
        /// benchmark harness reports coverage with it, because a frame time is
        /// only comparable to another frame time when both drew the same text.
        /// </summary>
        public FontStack Fonts
        {
            get
            {
                EnsureNativeState();
                return _fonts;
            }
        }

        /// <summary>The text actually laid out, with link tags removed.</summary>
        public string DisplayText
        {
            get
            {
                EnsureDisplayText();
                return _displayText ??= _markup.Text;
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
            // On the asset route the instance is picked up from the asset's
            // variant cache, so two labels at the same weight still share one
            // set of atlas entries, and dropping the stack is how it gets
            // picked up. On the bytes route there is no such cache and the
            // stack owns its face outright — so it is re-varied where it
            // stands rather than destroyed and reparsed. Both matter: a
            // six-megabyte face reparsed per frame of a slider drag is the
            // obvious one, and the other is that a destroyed face frees a
            // native handle for the next one to be allocated on top of.
            if (!RevaryOwnedFace()) ReleaseFonts();
            SetVerticesDirty();
            SetLayoutDirty();
        }

        /// <summary>
        /// Moves the axes of the face this label loaded for itself, if that is
        /// what it has. Returns false when the stack has to be rebuilt instead
        /// — no stack yet, no bytes override, or a stack whose primary came
        /// from somewhere shared and is therefore not ours to bend.
        /// </summary>
        private bool RevaryOwnedFace()
        {
            // Clearing the axes altogether is a rebuild on purpose: a label
            // with no variations belongs back in the shared cache, and keeping
            // the private face would cost it a second parse of a file another
            // label already has open.
            if (_variations.Count == 0) return false;
            if (_fonts == null || _ownedFonts.Count != 1) return false;
            var owned = _ownedFonts[0];
            if (owned == null || !owned.IsValid || _fonts.Primary != owned) return false;

            owned.SetVariations(_variations.ToArray());
            // Bold and italic were instanced by laying a weight or a slant over
            // the coordinate this face used to have, and that coordinate has
            // just moved.
            _fonts.DropStyledInstances(owned);
            // The atlas keys tiles by face and generation, and SetVariations
            // bumped the generation, so the new coordinate bakes its own tiles.
            // Everything the old coordinate left behind is ordinary cache: the
            // LRU reclaims it when the space is wanted.
            _layoutGeneration++;
            return true;
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
            // Deserialization fills the string, never the buffer.
            SyncSourceFromSerialized();
            // Tiles move when the atlas compacts or evicts; a baked mesh holds
            // their UVs and has to be told.
            AtlasInvalidation.Register(this);
            // And a style is a reference, so editing the asset has to reach the
            // labels pointing at it.
            StyleInvalidation.Register(this);
            // And the screen scale is a fact about cameras and canvases, which
            // change without dirtying any label; the watcher re-measures once
            // a canvas pass and this label re-bakes only when it escapes the
            // hysteresis band.
            ScreenPpem.Register(this);
            // The atlas reference is what keeps the shared material alive, and
            // it has to be taken here, not at the first mesh rebuild: between
            // enable and that rebuild, destroying the last label that held a
            // reference would destroy the material, and this label would then
            // re-assign it from inside the graphic rebuild loop — which uGUI
            // rejects with a "Trying to add ... for graphic rebuild while we
            // are already inside a graphic rebuild loop" error, every rebuild.
            HoldAtlas();
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
            ScreenPpem.Unregister(this);
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

        /// <summary>
        /// The outline, shadow or glow this label draws under everything
        /// markup and styles add. Component state, not a tag in the text: it
        /// survives anything that replaces the string — a Localize String
        /// Event, a binding, a score counter — where a decoration riding in
        /// the text goes with it. A style asset and the tags in the text
        /// still win the parts they set; this is the label's opinion, not
        /// its final say.
        /// </summary>
        public TextDecoration Decoration
        {
            get => _decoration;
            set
            {
                value = value.Clamped();
                if (_decoration.Equals(value)) return;
                _decoration = value;
                // Changes what the tiles draw, never where they sit: a quad
                // rebuild and not a re-layout, exactly as Precise is.
                _quadsValid = false;
                SetVerticesDirty();
            }
        }

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
            _parsedValid = false;
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
        private float BaseFontSize =>
            _style != null && _style.Sets(OneTextStyle.Fields.Size) ? _style.FontSize : _fontSize;

        /// <summary>
        /// The size everything downstream of layout uses. While auto-size is
        /// on this is the fitted size, which only exists once a layout has
        /// run; the base size stands in until then so a first measure is never
        /// zero.
        /// </summary>
        private float EffectiveFontSize =>
            _autoSize && _fittedSize > 0f ? _fittedSize : BaseFontSize;

        private float _fittedSize;

        private float EffectiveLineSpacing =>
            _style != null && _style.Sets(OneTextStyle.Fields.LineSpacing)
                ? _style.LineSpacing
                : _lineSpacing;

        private Color EffectiveColor =>
            _style != null && _style.Sets(OneTextStyle.Fields.Color) ? _style.Color * color : color;

        /// <summary>
        /// Whether the base style has an opinion about letter spacing, and
        /// what it is. There is no field of the label's own behind this: the
        /// style asset is the whole-label knob, deliberately, and the pair
        /// exists because a style that sets spacing to 0 over a font that
        /// ships wide means 0 rather than "no opinion".
        /// </summary>
        private bool HasBaseLetterSpacing =>
            _style != null && _style.Sets(OneTextStyle.Fields.LetterSpacing);

        /// <inheritdoc cref="HasBaseLetterSpacing"/>
        private float EffectiveLetterSpacing =>
            HasBaseLetterSpacing ? _style.LetterSpacingEm : 0f;

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

        /// <summary>
        /// Copies Project Settings &gt; OneText's new-text defaults onto this
        /// label: size, auto-size bounds, wrapping, markup, escapes and whether
        /// it takes clicks.
        ///
        /// Called for you when a label is added in the editor. Public because
        /// the other direction is worth having too: a label that has drifted
        /// from the project's answer can be put back on it.
        /// </summary>
        public void ApplyProjectDefaults()
        {
            var defaults = OneTextSettings.ProjectDefaults;
            FontSize = defaults.FontSize;
            AutoSizeMin = defaults.AutoSizeMin;
            AutoSizeMax = defaults.AutoSizeMax;
            Wrap = defaults.Wrap;
            RichText = defaults.RichText;
            ParseEscapes = defaults.ParseEscapes;
            raycastTarget = defaults.RaycastTarget;
        }

#if UNITY_EDITOR
        // Reset is what Unity calls when the component is added in the editor
        // and when somebody picks Reset from its context menu, which are the
        // two moments "what should a new label be" is being asked.
        protected override void Reset()
        {
            base.Reset();
            ApplyProjectDefaults();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            // The inspector writes the serialized string directly, so the
            // buffer everything downstream reads is now the old text. Nothing
            // else re-syncs it: the setter that normally does is not on this
            // path.
            SyncSourceFromSerialized();
            _parsedValid = false;
            // Any of them may also have been the text, and the spans the
            // animator is holding were built from the last one. Nothing else
            // drops them: only InvalidateText does, and the inspector does not
            // go through the property that calls it. Without this line an
            // effect tag typed into the text box — or clicked on in the
            // toggles above it — parses, lays out, and never moves for the
            // rest of the editor session, on a label that animates correctly
            // the moment play mode deserializes it fresh.
            _animatorBuilt = false;
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
            _parsedValid = false;
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
            if (_parsedValid && _parsedRich == _richText && _parsedEscapes == _parseEscapes) return;
            _parsedValid = true;
            _parsedRich = _richText;
            _parsedEscapes = _parseEscapes;
            _displayText = null;

            // Escapes resolve before markup so every index the parser hands
            // out refers to the text the engine will actually see. The string
            // this makes is the one path in here that still allocates, and it
            // is only entered by text with a backslash in it.
            var source = SourceSpan;
            if (_parseEscapes && EscapeParser.MightHaveEscapes(source))
            {
                _escaped = EscapeParser.Unescape(Text);
                source = _escaped.AsSpan();
            }

            if (_richText && RichTextParser.MightHaveMarkup(source))
            {
                RichTextParser.Parse(source, _markup,
                    _namedStyleIndex ??= NamedStyleIndex,
                    _namedFontIndex ??= NamedFontIndex,
                    _namedSpriteIndex ??= NamedSpriteIndex);
                _links.Clear();
                _links.AddRange(_markup.Links);
            }
            else
            {
                // The markup result carries the text either way, so everything
                // downstream reads one span and does not care which branch
                // filled it.
                _markup.SetPlain(source);
                _links.Clear();
            }
        }

        // Cached for the same reason the layout resolvers are: a method group
        // makes a new delegate every time it is converted, and these three are
        // converted on every parse. Three allocations per markup label per
        // frame, which is what a rebuild with markup still cost after the
        // parser itself stopped allocating.
        private Func<string, int> _namedStyleIndex, _namedFontIndex, _namedSpriteIndex;

        /// <summary>Unescaped source, when there was anything to unescape.</summary>
        [System.NonSerialized] private string _escaped;

        /// <summary>
        /// The text as laid out — markup removed, escapes resolved — without
        /// building a string for it.
        /// </summary>
        internal ReadOnlySpan<char> DisplaySpan
        {
            get
            {
                EnsureDisplayText();
                return _markup.TextSpan;
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

        /// <summary>
        /// Effect spans the animator holds for the text as it stands: what the
        /// tags in the string actually became.
        ///
        /// Not the same question as how many effect tags the string contains,
        /// which anything holding the string can count for itself. The gap
        /// between the two is where a silent failure lives — a tag whose name
        /// no longer resolves to an effect, or an animator that was never
        /// rebuilt after the text changed — and from outside the label there is
        /// no way to see it: the text looks right and nothing moves.
        /// </summary>
        public int EffectSpanCount
        {
            get
            {
                EnsureAnimator();
                return _animator.Spans.Count;
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
            // Shared faces are refcounted, not disposed: the last label out
            // turns the light off inside the cache.
            foreach (var shared in _sharedFonts) SharedFontBytes.Release(shared);
            _sharedFonts.Clear();
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
            // Normally already held from OnEnable; this covers layout queries
            // against a label that has never been enabled.
            HoldAtlas();

            return EnsureMaterial();
        }

        // The gate above runs on every layout pass, and while there is no font
        // it rebuilds the stack on every one of them, so the deduplication that
        // matters most is the cheapest: a bool, before the string that
        // MissingFonts keys on is ever built.
        [System.NonSerialized] private bool _warnedNoFont;

        private void WarnNoFont(bool drawing)
        {
            if (_warnedNoFont) return;
            _warnedNoFont = true;
            MissingFonts.Warn(this, EffectiveFontAsset, drawing);
        }

        /// <summary>
        /// The font asset this label meant to draw with, whether or not it had
        /// anything in it — which is the one the warning has to name. Same
        /// precedence as <see cref="BuildFontStack"/>: a style's font, then the
        /// label's own, then the project default.
        /// </summary>
        private OneFontAsset EffectiveFontAsset
        {
            get
            {
                if (_style != null && _style.Sets(OneTextStyle.Fields.Font) && _style.Font != null)
                    return _style.Font;
                if (_font != null) return _font;
                var settings = OneTextSettings.Instance;
                return settings != null ? settings.DefaultFont : null;
            }
        }

        /// <summary>
        /// Takes this label's reference to the shared atlas, once. Paired with
        /// the release in <see cref="OnDestroy"/>, and deliberately not
        /// released on disable so that toggling the only label in a scene does
        /// not churn the atlas.
        /// </summary>
        private void HoldAtlas()
        {
            if (_atlasHeld) return;
            SharedGlyphAtlas.Acquire();
            _atlasHeld = true;
        }

        /// <summary>
        /// Points this label at the shared material and asks its canvas for the
        /// vertex channels the SDF shader reads.
        ///
        /// Assigning a material marks the graphic dirty, and uGUI logs an error
        /// for anything that asks to be rebuilt while it is already rebuilding,
        /// so this runs when the label enables or changes canvas — with the
        /// atlas reference already held from <see cref="OnEnable"/>, the shared
        /// material cannot be destroyed and recreated under an enabled label,
        /// so by the time mesh generation calls this the assignment has always
        /// already happened and is skipped.
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
                // Through the shared cache, not a private parse: a hundred
                // labels handed the same bytes get one HarfBuzz face and one
                // set of atlas tiles between them. The exception is a label
                // with variations, which mutates its face and therefore must
                // own it — sharing it would embolden every other label.
                if (_variations.Count > 0)
                {
                    var loaded = FontData.Load(_fontBytesOverride);
                    _ownedFonts.Add(loaded);
                    loaded.SetVariations(_variations.ToArray());
                    _fonts.Add(loaded);
                }
                else
                {
                    var shared = SharedFontBytes.Acquire(_fontBytesOverride);
                    _sharedFonts.Add(shared);
                    _fonts.Add(shared);
                }

                if (_fallbackBytesOverride != null)
                {
                    foreach (var bytes in _fallbackBytesOverride)
                    {
                        if (bytes == null || bytes.Length == 0) continue;
                        var fallback = SharedFontBytes.Acquire(bytes);
                        _sharedFonts.Add(fallback);
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
                    // The designed bold rides in with the family it belongs to.
                    // Null for a variable font and for most static ones, and the
                    // stack treats null as "no bold here", which is what makes
                    // <b> fall through to the wght axis, then to a faked weight.
                    _fonts.Add(main.GetVariant(axes), main.BoldFace, main.Language,
                        main.LetterSpacingEm);
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

        private TextLayoutSettings BuildSettings(float maxWidth, float maxHeight) =>
            BuildSettings(maxWidth, maxHeight, EffectiveFontSize);

        // A method group converts to a NEW delegate every time it is converted,
        // and Roslyn caches that conversion for static methods only; these three
        // are instance methods by necessity. Uncached they were three heap
        // allocations on every layout that missed its cache, for every label in
        // the scene, which is the whole of what a rebuild allocated. Each one
        // reads live state when it is invoked, so a delegate built once can
        // never serve a stale font, style or sprite.
        private System.Func<int, FontData> _resolveFontOverride;
        private System.Func<int, TextStyle, TextStyle> _resolveNamedStyle;
        private System.Func<int, float> _resolveSpriteAspect;

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
                LineSpacing = EffectiveLineSpacing,
                LetterSpacingEm = EffectiveLetterSpacing,
                HasLetterSpacing = HasBaseLetterSpacing,
                BaseDirection = BidiAlgorithm.AutoDirection,
                ResolveFontOverride = _resolveFontOverride ??= NamedFont,
                ResolveNamedStyle = _resolveNamedStyle ??= ApplyNamedStyle,
                ResolveSpriteAspect = _resolveSpriteAspect ??= SpriteAspect,
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
            // Keyed by the base size, not the fitted one: the fitted size is a
            // function of everything else in the key, so keying by it would
            // make the cache chase its own output.
            var key = new LayoutKey(_markup.Length, DisplayHash, maxWidth, maxHeight, BaseFontSize,
                EffectiveLineSpacing, _alignment, _wrap, _overflow, _layoutGeneration,
                _autoSize, _autoSizeMin, _autoSizeMax);
            if (!_layoutValid || !key.Equals(_layoutKey))
            {
                if (_autoSize) _fittedSize = FitFontSize(rect);
                _engine.Layout(_markup.TextSpan, BuildSettings(maxWidth, maxHeight), _layout);
                _layoutKey = key;
                _layoutValid = true;
                _quadsValid = false;
                _layoutRuns++;
                // Vertices a caller pushed were written against tiles that no
                // longer exist. Drawing them over new text is worse than losing
                // an animation frame.
                DropVertexOverride();
                OneTextEvents.RaiseTextChanged(this);
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

        // ------------------------------------------------------------ auto-size

        /// <summary>
        /// The largest size in [min, max] at which the whole block fits the
        /// rect. Fitting is monotonic (smaller text never fits worse), so this
        /// is a bisection: try max, try min, then halve the bracket to half a
        /// point and snap down to the half-point grid, which keeps the atlas
        /// from collecting one ppem bucket per fractional answer.
        /// </summary>
        private float FitFontSize(Rect rect)
        {
            float min = Mathf.Max(1f, Mathf.Min(_autoSizeMin, _autoSizeMax));
            float max = Mathf.Max(min, _autoSizeMax);
            if (_markup.Length == 0) return max;

            if (FitsAt(max, rect)) return max;
            if (!FitsAt(min, rect)) return min;

            float fits = min, overflows = max;
            while (overflows - fits > 0.5f)
            {
                float mid = (fits + overflows) * 0.5f;
                if (FitsAt(mid, rect)) fits = mid;
                else overflows = mid;
            }
            // Snapping down keeps the answer on the fitting side of the
            // bracket: a size that fits still fits smaller.
            return Mathf.Max(min, Mathf.Floor(fits * 2f) * 0.5f);
        }

        /// <summary>
        /// Whether the text laid out at <paramref name="size"/> fits the rect.
        /// Measured with overflow disabled: truncation makes every size "fit",
        /// which would leave nothing for the search to compare. The wrap side
        /// is constrained as it will be when drawn; the block side runs free
        /// and is judged against the rect afterwards, along with the inline
        /// side, which an unbreakable word can still push past its budget.
        /// </summary>
        private bool FitsAt(float size, Rect rect)
        {
            bool vertical = IsVertical;
            var settings = BuildSettings(
                vertical ? 0f : rect.width,
                vertical ? rect.height : 0f,
                size);
            settings.Overflow = TextOverflow.Overflow;
            _engine.Layout(_markup.TextSpan, settings, _measure);

            const float slack = 0.5f;
            float inlineBudget = vertical ? rect.height : rect.width;
            float blockBudget = vertical ? rect.width : rect.height;
            return _measure.InlineExtent <= inlineBudget + slack &&
                   _measure.BlockExtent <= blockBudget + slack;
        }

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
            // The cheap way to answer "no links here": no parse has happened
            // yet and the raw text has no '<' in it, so there is nothing a
            // link could have been written as. Over-broad — <b>bold</b> fails
            // it and takes the slow path for nothing — but harmless now that
            // both paths end in the same place.
            if (_links.Count == 0 && !RichTextParser.MightHaveMarkup(_text))
            {
                BubbleUnhandledClick(eventData);
                return;
            }

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, eventData.position, eventData.pressEventCamera, out var local);
            if (TryGetLinkAtLocalPoint(local, out var link))
            {
                _linkClicked.Invoke(link.Id);
                return;
            }

            // A click that landed on the label but missed every link span is
            // not this label's click, the way a click on the padding of an <a>
            // is not the anchor's. Let it go up.
            BubbleUnhandledClick(eventData);
        }

        /// <summary>
        /// Components on this GameObject that would also have received this
        /// click. Static because a click is a per-frame event on a hot input
        /// path and a fresh list per click is a fresh list per click; the list
        /// never escapes the method that fills it.
        /// </summary>
        private static readonly List<IPointerClickHandler> s_clickHandlerScratch =
            new List<IPointerClickHandler>();

        /// <summary>
        /// Hands a click this label did nothing with to whoever is above it.
        ///
        /// The bug this exists for: Button &gt; Label is *the* uGUI hierarchy,
        /// and a label under a Button used to eat the Button's clicks.
        /// <c>StandaloneInputModule</c> resolves the click target with
        /// <c>ExecuteEvents.GetEventHandler&lt;IPointerClickHandler&gt;</c>,
        /// which walks up from the raycast hit and stops at the first object
        /// carrying the interface. This label carries it, so the walk stopped
        /// here and the Button's onClick never ran — while its pointerDown and
        /// pointerUp still arrived, so the button flashed pressed and did
        /// nothing, which is the most confusing shape that failure could take.
        /// TextMeshProUGUI implements no pointer interface at all, so labels
        /// migrated from TMP arrive with raycastTarget on and every Button
        /// under them dead, dropdown rows included: the row's item label sits
        /// over the row's Toggle.
        ///
        /// Re-dispatching upward is not a new routing, it is TMP's routing by
        /// a different road — the ancestors that GetEventHandler would have
        /// found are exactly the ancestors ExecuteHierarchy now walks, and it
        /// stops at the first one that handles the event just as GetEventHandler
        /// would have. A raycast filter was the other candidate and was
        /// rejected: a TMP label with raycastTarget on genuinely does block
        /// what is behind it, does register hover, and is the drag surface a
        /// ScrollRect full of bare text scrolls by.
        /// </summary>
        private void BubbleUnhandledClick(PointerEventData eventData)
        {
            var parent = transform.parent;
            if (parent == null) return;

            // The one way this could deliver twice. ExecuteEvents.Execute runs
            // every IPointerClickHandler on the target GameObject, so a Button
            // sharing this label's object already had its click before we were
            // called; sending it up from there would be a second, wrong
            // delivery to the same hierarchy. Every other case is safe because
            // the ancestor never got the event at all.
            //
            // Disabled components are skipped for the same reason they are
            // skipped by ExecuteEvents itself: a Button that was off did not
            // receive the click, so it is not a reason to withhold it from the
            // hierarchy above.
            GetComponents<IPointerClickHandler>(s_clickHandlerScratch);
            bool sharedWithAnother = false;
            foreach (var handler in s_clickHandlerScratch)
            {
                if (ReferenceEquals(handler, this)) continue;
                if (handler is Behaviour behaviour && !behaviour.isActiveAndEnabled) continue;
                sharedWithAnother = true;
                break;
            }
            // Cleared rather than left full: this list is static, and a click
            // is the last thing that should be keeping a destroyed component
            // reachable until the next one.
            s_clickHandlerScratch.Clear();
            if (sharedWithAnother) return;

            ExecuteEvents.ExecuteHierarchy(parent.gameObject, eventData,
                ExecuteEvents.pointerClickHandler);
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
        private int ResolveDecoration(int textIndex, in TextStyle style, bool syntheticBold)
        {
            var decoration = _style != null
                ? _style.Decoration.Over(_decoration)
                : _decoration;
            if (style.NamedStyle >= 0 && style.NamedStyle < _namedStyles.Count)
            {
                var named = _namedStyles[style.NamedStyle];
                if (named != null) decoration = named.Decoration.Over(decoration);
            }
            if (_markup.HasMarkup && _markup.Decorations.Count > 0)
                decoration = _markup.DecorationAt(textIndex).Over(decoration);

            // Last, and additively, because it is not a decoration anybody
            // wrote: it is the weight this run could not get from a font. A
            // label that also asked for a dilated face gets both, which is the
            // honest reading of "thicker than that, and bold on top".
            if (syntheticBold) decoration = decoration.WithSyntheticBold();

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
                var color = style.ResolveColor();
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
            float density = DensityFor(runSize);
            int ppem = GlyphAtlas.QuantizePixelsPerEm(density);
            // Off the bucket, not off the request: the tile really is this many
            // texels across, so the units it converts back to are the glyph's
            // own however dense the bucket turned out to be.
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
                int decoration = ResolveDecoration(glyph.Cluster, run.Style, run.SyntheticBold);
                if (TryEmitColorGlyph(font, glyph, ppem, pixelsPerUnit, runColor, colorAtlas,
                        frame, along, glyph.YOffset, run, runIndex, runTextEnd, i, decoration))
                {
                    pen += glyph.XAdvance;
                    continue;
                }

                var sdf = sdfAtlas.GetOrAdd(font, glyph.GlyphId, density);
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

            // The tile's resolution follows the quality rung with everything
            // else; the cell it is drawn into is `runSize` below, and stays
            // there. An icon does not grow because its picture got sharper.
            int ppem = GlyphAtlas.QuantizePixelsPerEm(DensityFor(runSize));
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
                    ? DecorationChannels.None
                    : _packedDecorations[quad.Decoration];
                // A caller that edited textInfo's vertices and pushed them is
                // drawn from those, corner by corner, while everything the mesh
                // needs and a vertex array cannot carry — which atlas, which
                // layer, which decoration — still comes from the tile.
                if (_vertexOverride && EmitOverrideQuad(vh, quad, decoration, _drawn.Count - 1))
                    continue;
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
            if (_punctuationDelays.Count == 0 || _markup.Length == 0) return 0f;

            var starts = _layout.GraphemeStarts;
            int from = starts[_unitStarts[unit]];
            int to = starts[_unitStarts[unit + 1]];
            float longest = 0f;
            var display = _markup.TextSpan;
            for (int i = from; i < to && i < display.Length; i++)
            {
                char c = display[i];
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

            // Re-measured at the top of every build, not only by the watcher:
            // a capture that drives Rebuild by hand has no canvas pass, and its
            // one picture must come off the measured density, not off a stale 1.
            // On a change this drops _quadsValid, so the cache check below
            // misses and the quads rebuild against the new buckets.
            RefreshPpemScale();

            // Fetched per rebuild, not cached: changing the atlas budget in
            // Project Settings replaces the atlas underneath us. A precise
            // label draws from the multi-channel atlas, which is created the
            // first time one asks for it and never otherwise.
            var atlas = _precise ? SharedGlyphAtlas.PreciseAtlas : SharedGlyphAtlas.Atlas;
            // The density, not the size: prewarm bakes what the recorder saw,
            // and a session that actually baked 96 ppem tiles is not predicted
            // by a record that says 36.
            // The span, not the string: this is off unless a session asked to
            // record its charset, and reading DisplayText to hand it an
            // argument it will not use built a string on every rebuild.
            CharsetRecorder.Record(DisplaySpan, DensityFor(EffectiveFontSize));

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
            _packedDecorations.Add(DecorationChannels.None);

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
                // Two numbers from here down, and the whole of the quality
                // setting is the gap between them: `runDensity` is what the
                // atlas is asked for and `scale` is what the text is drawn at.
                // A tile baked at twice the density is twice as many texels
                // across the same em, so the glyph comes out the same size off
                // a finer field — which is only true while `scale` keeps
                // reading `runSize`.
                float runDensity = DensityFor(runSize);
                int runPpem = GlyphAtlas.QuantizePixelsPerEm(runDensity);
                float scale = runSize / font.UnitsPerEm;
                // Only the tag's colour is baked. The label's own colour is
                // multiplied in at emit time, so tinting or fading a label
                // never invalidates these quads, and never re-bakes a colour
                // tile, which would otherwise put one tile per fade step into
                // the atlas.
                var runColor = run.Style.ResolveColor();
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
                atlas.PrepareClusters(font, runDensity, _positioned, _clusters);
                AtlasDiagnostics.Add(ref AtlasDiagnostics.LookupTicks, lookupStartedAt);

                foreach (var cluster in _clusters)
                {
                    lookupStartedAt = AtlasDiagnostics.Now;
                    var loc = atlas.GetOrAddCluster(font, runDensity,
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
                        Decoration = ResolveDecoration(cluster.TextStart, run.Style,
                            run.SyntheticBold),
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

            /// <summary>
            /// The two that do not live in a channel of their own: they ride in
            /// the spare byte of a float that was already being sent for another
            /// reason. Carried here as bytes so that <see cref="AddVert"/> can
            /// fold them into those floats without knowing what a decoration is.
            /// </summary>
            public readonly byte OutlineSoftness, FaceDilate;

            public DecorationChannels(Vector4 colors, Vector4 shape,
                byte outlineSoftness, byte faceDilate)
            {
                Colors = colors;
                Shape = shape;
                OutlineSoftness = outlineSoftness;
                FaceDilate = faceDilate;
            }

            /// <summary>
            /// What an undecorated tile writes, which is emphatically not
            /// <c>default</c>. The face dilate is signed and its zero is 128; a
            /// struct of zeroes says "thin this glyph by a whole reach", and
            /// since every plain label in every project takes this path, that
            /// is every glyph everywhere.
            /// </summary>
            public static DecorationChannels None => new DecorationChannels(
                Vector4.zero, Vector4.zero, 0, TextDecoration.QuantizeSigned(0f, 1f));
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
                    TextDecoration.PackNibbles(decoration.GlowInner, decoration.GlowRadius)));

            // Zero softness when there is no outline and a signed zero for the
            // face when nothing said anything about it, so an undecorated label
            // writes exactly the bytes it wrote before either existed.
            return new DecorationChannels(colors, shape,
                decoration.HasOutline
                    ? TextDecoration.Quantize(decoration.OutlineSoftness) : (byte)0,
                TextDecoration.QuantizeSigned(
                    decoration.HasFace ? decoration.FaceDilate : 0f, 1f));
        }

        /// <summary>
        /// The vertex-channel budget, in full, because it is a contract with
        /// <c>OneText-SDF.shader</c> and there is no compiler between the two.
        ///
        /// TEXCOORD0  xy tile uv · z layer|outline soft · w tile v-min
        /// TEXCOORD1  outline R|G · outline B|width · shadow R|G · shadow B|A
        /// TEXCOORD2  x tile v-max · y tile u-min · z tile u-max · w atlas|face dilate
        /// TEXCOORD3  glow R|G · glow B|A · shadow dx|dy · shadow soft|glow in:out
        ///
        /// TEXCOORD0.z and TEXCOORD2.w each hold two things because each was
        /// already holding almost nothing: the layer indexes a texture array of
        /// at most sixteen slices and the discriminator tells four things apart,
        /// so both were spending a whole float on four bits. The outline's
        /// softness and the face's dilate moved into the space beside them. No
        /// channel was added, so no other graphic in the canvas pays for either.
        ///
        /// TEXCOORD3.w's low byte is the one place in this file where two
        /// parameters share a byte, four bits each. They are the glow's inner and
        /// outer reach: one effect, one unit, both soft, so the worst an
        /// interpolator can do by borrowing across their boundary is change the
        /// shape of a blur by a sixteenth. That is the whole reason it is those
        /// two and not, say, a colour and a width.
        ///
        /// The list ends at TEXCOORD3 and the shader's does not: it also has a
        /// TEXCOORD4 for RectMask2D clipping. That one is <em>derived in the
        /// vertex shader</em> from the position it was already given, not
        /// carried from here, so it is not part of this contract and wants
        /// nothing added to the canvas's additionalShaderChannels. Adding it
        /// would cost every graphic in the canvas a channel to duplicate a
        /// number the GPU can work out for free.
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
            // The layer is an index into a texture array of at most sixteen
            // slices, so it has been carrying four bits in a whole float since
            // the day it was written. The outline's softness rides in the byte
            // below it. Same for the atlas discriminator's float in vmax.w,
            // which distinguishes four things and now carries the face dilate.
            //
            // Two whole bytes, found rather than made: no channel was added, so
            // nothing in this canvas pays for them, and neither one is a field
            // split inside a byte — the one place that happens is the glow's own
            // inside and outside, which can only blur each other.
            var uvA = new Vector4(u, v,
                TextDecoration.Pack((byte)layer, decoration.OutlineSoftness), uvRect.yMin);
            // vmax.w picks the atlas: single-channel field, colour picture or
            // multi-channel field. It fits in a channel the mesh already
            // carries, so emoji and precise text cost no extra vertex data and
            // no second draw call, which is the whole reason both are flags
            // rather than submeshes.
            var vmax = new Vector4(uvRect.yMax, uvRect.xMin, uvRect.xMax,
                TextDecoration.Pack((byte)atlas, decoration.FaceDilate));
            vh.AddVert(new Vector3(x, y), c, uvA, decoration.Colors, vmax, decoration.Shape,
                s_Normal, s_Tangent);
        }

        // ====================================================================
        // TMP parity — begin
        //
        // Aliases, and nothing but aliases. A project migrating off TextMesh
        // Pro has `label.text = …` written across hundreds of call sites, and
        // the difference between a package you can try in an afternoon and one
        // you cannot is whether those lines still compile. Each member below
        // forwards to the PascalCase property that is the real API; none of
        // them holds state, and none of them is in IntelliSense, so new code
        // written against this class still reads OneText's own names.
        //
        // The ones that are not straight forwards convert, and each converts
        // for the reason it was hard to add. TMP's `lineSpacing` is an offset
        // in font units and OneText's is a multiplier, so the alias does the
        // arithmetic — the same arithmetic the Onboarding migration does, out
        // of the same function, because two answers to "what does 10 mean"
        // would eventually disagree. `alignment` names an enum this package
        // has no use for, so the package declares it (see TmpCompat) rather
        // than leaving four hundred call sites uncompilable over a type name;
        // it splits into OneText's two axes on the way in and is reassembled
        // on the way out. `textWrappingMode` and `overflowMode` are the same
        // shape, narrower.
        //
        // What is deliberately *not* here: no-op stubs. A ForceMeshUpdate that
        // does nothing is a bug report about a stale mesh, filed six months
        // later, so the one below really does lay the text out and rebuild the
        // geometry before it returns — callers read the results on the next
        // line, which is the whole reason they called it. Where OneText has no
        // counterpart at all and inventing one would mean drawing something
        // other than what was asked for, the member is either absent (and the
        // Onboarding report names it as manual work) or present and [Obsolete]
        // as an error, which turns "no definition for characterSpacing" into a
        // sentence saying what to write instead. Nothing here quietly does the
        // wrong thing.
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
        /// TMP-migration parity alias for <see cref="MaxVisibleGraphemes"/>.
        /// Close but not identical: OneText counts grapheme clusters, not
        /// UTF-16 characters, so a line of Hangul or a flag emoji reveals in
        /// fewer steps here than it did there. Same behaviour for Latin, and
        /// the right behaviour everywhere else.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public int maxVisibleCharacters
        {
            get => MaxVisibleGraphemes;
            set => MaxVisibleGraphemes = value;
        }

        /// <summary>
        /// TMP-migration parity alias for the <see cref="Alignment"/> /
        /// <see cref="VerticalAlignment"/> pair, in TMP's single enum.
        ///
        /// Assigning splits the bitfield across the two axes, and the five TMP
        /// distinctions OneText does not draw (Flush, GeoAligned, Baseline,
        /// Midline, Capline) resolve to the nearest one it does — silently,
        /// because a property setter has nobody to tell; <c>Converted</c>, the
        /// one member that is not a position, has every bit set and resolves
        /// the same way the migration always resolved it. The Onboarding tab
        /// converts the same values through the same function and does name
        /// every approximation it made, which is the one to use when it
        /// matters. An approximated value never reads back as itself, so the
        /// setter swallows a re-assignment of what is already resolved rather
        /// than redrawing for it.
        ///
        /// Reading is lossy the other way: TMP has no start edge, so
        /// <see cref="TextAlignment.Start"/> and <see cref="TextAlignment.End"/>
        /// answer Left and Right. Nothing assigned through here is ever Start
        /// or End, so that only reaches a label configured in OneText's names.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TextAlignmentOptions alignment
        {
            get => TmpCompat.CombineAlignment(Alignment, VerticalAlignment);
            set
            {
                TmpCompat.SplitAlignment((int)value, out var horizontal, out var vertical,
                    out _, out _);
                // Guarded for the same reason as LineSpacing's setter: an
                // approximated value never reads back as itself (write
                // TopFlush, read TopJustified), so TMP's compare-then-assign
                // idiom re-assigns every frame, and it has to land here as a
                // no-op rather than as a redraw.
                if (Alignment == horizontal && VerticalAlignment == vertical) return;
                Alignment = horizontal;
                VerticalAlignment = vertical;
            }
        }

        /// <summary>
        /// TMP-migration parity alias for <see cref="LineSpacing"/>, in TMP's
        /// units: an offset where zero is the font's own line height, against
        /// OneText's multiplier where one is. Ten percent looser is
        /// <c>10</c> through this and <c>1.1</c> through the property.
        ///
        /// Same intent, not the same pixels: TMP applied its offset after its
        /// own ascender/descender arithmetic. And same value, not the same
        /// bits: the state is stored as the multiplier, so a read is a float
        /// round trip and <c>10f</c> can come back <c>10.000002f</c>. Assigning
        /// the readback is a no-op — the re-converted multiplier is bit-equal
        /// and <see cref="LineSpacing"/>'s setter stops it — but code comparing
        /// the alias against a literal should expect the epsilon. A project
        /// that cares should be reading OneText's units, which is what
        /// <see cref="LineSpacing"/> is.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float lineSpacing
        {
            get => TmpCompat.LineSpacingToTmp(LineSpacing);
            set => LineSpacing = TmpCompat.LineSpacingFromTmp(value);
        }

        /// <summary>
        /// TMP-migration parity alias for <see cref="Wrap"/>. The two
        /// preserving modes carry a whitespace decision OneText does not hold,
        /// so they set the wrap they imply and read back as <c>Normal</c> or
        /// <c>NoWrap</c>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TextWrappingModes textWrappingMode
        {
            get => TmpCompat.WrapToTmp(Wrap);
            set => Wrap = TmpCompat.WrapFromTmp(value);
        }

        /// <summary>
        /// TMP-migration parity alias for <see cref="Wrap"/> as the boolean it
        /// was before TMP 3.2 renamed it. Older projects are full of this one.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool enableWordWrapping
        {
            get => Wrap == TextWrap.Wrap;
            set => Wrap = TmpCompat.WrapFromWordWrapping(value);
        }

        /// <summary>
        /// TMP-migration parity alias for <see cref="Overflow"/>, in TMP's
        /// wider enum. Four of TMP's seven modes are about what clips the text
        /// or where the rest of it goes rather than about the layout, and they
        /// resolve to the nearest thing OneText does — the same resolution the
        /// Onboarding migration makes, out of the same function, and the same
        /// silence, because a property setter has nobody to tell. An
        /// approximated value never reads back as itself, so the setter
        /// swallows a re-assignment of what is already resolved.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TextOverflowModes overflowMode
        {
            get => TmpCompat.OverflowToTmp(Overflow);
            set
            {
                var resolved = TmpCompat.OverflowFromTmp(value, out _);
                if (Overflow == resolved) return;
                Overflow = resolved;
            }
        }

        /// <summary>
        /// TMP-migration parity alias for the alpha channel of
        /// <see cref="Graphic.color"/>.
        ///
        /// It holds no state of its own: reading asks the colour, and writing
        /// assigns the colour back, which is what runs the invalidation. The
        /// tint joins the mesh on the way out rather than being baked into the
        /// cached quads, so fading a label — which is what nine uses in ten of
        /// this member are — redraws without laying anything out again.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float alpha
        {
            get => color.a;
            set
            {
                var tint = color;
                tint.a = value;
                color = tint;
            }
        }

        /// <summary>
        /// TMP-migration parity for <c>ForceMeshUpdate</c>: lay the text out
        /// and rebuild the geometry now, before this returns.
        ///
        /// Synchronous on purpose, because that is the only thing callers want
        /// from it. The idiom it exists for is assign-then-measure — set the
        /// text, force the update, read the size in the next statement — and
        /// against a canvas that rebuilds at the end of the frame the reading
        /// would otherwise describe the previous string.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ForceMeshUpdate() => ForceMeshUpdate(false);

        /// <summary>
        /// TMP-migration parity for <c>ForceMeshUpdate(bool, bool)</c>.
        ///
        /// <paramref name="ignoreActiveState"/> is accepted and ignored:
        /// nothing in the path below asks whether the component is enabled, so
        /// a disabled label is rebuilt either way and the flag has nothing left
        /// to switch off. <paramref name="forceTextReparsing"/> does what it
        /// says — the markup is parsed again and the layout re-run even when
        /// neither input changed, which is the flag's one real use: a project
        /// that mutated something the cache key cannot see.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ForceMeshUpdate(bool ignoreActiveState, bool forceTextReparsing = false)
        {
            if (forceTextReparsing)
            {
                _parsedValid = false;
                _layoutValid = false;
            }

            EnsureLayout();
            // The layout is only half of it; the mesh is the half the caller
            // can see. UpdateGeometry is Graphic's own path to OnPopulateMesh
            // and the canvas renderer, so what lands is exactly what the
            // deferred rebuild would have produced, just now.
            if (canvasRenderer != null) UpdateGeometry();
        }

        /// <summary>
        /// TMP-migration parity for <c>GetParsedText</c>, which is
        /// <see cref="DisplayText"/> under TMP's name: the text as laid out,
        /// with the markup taken out of it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public string GetParsedText() => DisplayText;

        /// <summary>
        /// TMP-migration parity alias for assigning <see cref="Text"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(string value) => Text = value;

        /// <summary>
        /// TMP-migration parity alias for assigning <see cref="Text"/>.
        /// <paramref name="syncTextInputBox"/> is TMP's flag for keeping the
        /// inspector's text box in step with a runtime assignment; the
        /// inspector here reads the same serialized field either way, so there
        /// is nothing for it to switch on.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(string value, bool syncTextInputBox) => Text = value;

        /// <summary>
        /// TMP-migration parity alias for assigning <see cref="Text"/> from a
        /// builder. Worth being plain about what it does not carry over: TMP's
        /// builder overload exists to set text without allocating a string, and
        /// this one allocates, because OneText's text is a string. Same result,
        /// not the same garbage.
        ///
        /// The numeric overloads (<c>SetText("{0:2}", value)</c>) are
        /// deliberately absent rather than approximated. Their format syntax is
        /// TMP's own and not <c>string.Format</c>'s — <c>{0:2}</c> means two
        /// decimal places there and something else entirely to the BCL — so an
        /// implementation that forwarded would compile, run, and print the
        /// wrong number. The Onboarding report names them as manual work.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetText(System.Text.StringBuilder value) =>
            Text = value != null ? value.ToString() : string.Empty;

        /// <summary>
        /// TMP-migration parity for <c>isTextOverflowing</c>: whether the text
        /// as laid out does not fit the box.
        ///
        /// True when overflow handling actually dropped lines, and also when
        /// nothing was dropped but the block reaches past an edge — which is
        /// the ordinary case for the default <see cref="TextOverflow.Overflow"/>,
        /// where the text spills rather than being cut. Asking lays the text
        /// out if it is stale, so the answer describes what is on screen.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool isTextOverflowing
        {
            get
            {
                var result = EnsureLayout();
                if (result.Truncated) return true;
                var rect = GetPixelAdjustedRect();
                // A hair of slack, because a line laid out to exactly the box
                // width is not overflowing and float arithmetic disagrees.
                const float epsilon = 0.01f;
                return result.Width > rect.width + epsilon ||
                       result.Height > rect.height + epsilon;
            }
        }

        /// <summary>
        /// TMP-migration parity for <c>renderedWidth</c>: how wide the laid-out
        /// text is, as opposed to <see cref="preferredWidth"/>, which asks how
        /// wide it would be given a free hand.
        ///
        /// The laid-out block, not the drawn one, and those differ in exactly
        /// one situation: mid-typewriter, where the clusters still hidden are
        /// measured here and are not on screen. Reading a reveal's width while
        /// it runs is not something this can answer honestly, and
        /// <see cref="LayoutResult"/> is the member for code that needs the
        /// distinction.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float renderedWidth => EnsureLayout().Width;

        /// <inheritdoc cref="renderedWidth"/>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float renderedHeight => EnsureLayout().Height;

        // -------------------------------------------------- named, not faked
        //
        // Below here are members TMP has, projects use, and OneText cannot
        // honour without drawing something other than what was asked for. They
        // are declared and obsoleted as errors rather than left out, because
        // the compiler is the only place the author is guaranteed to be
        // looking: "OneTextLabel does not contain a definition for margin" sends
        // them to a search engine, and the message below sends them to the
        // member that does the job.

        [System.Obsolete("OneText has no letter spacing field on the label. Use <cspace=0.1em> " +
                         "markup for a range, a OneTextStyle asset with Letter Spacing set for " +
                         "the whole label, or the font asset's own Letter Spacing when the face " +
                         "itself is the thing that is too tight.", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float characterSpacing
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        [System.Obsolete("OneText has no word spacing. The nearest thing is letter spacing, as " +
                         "<cspace> markup, a OneTextStyle asset or the font asset's own Letter " +
                         "Spacing; there is no per-space knob.", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public float wordSpacing
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        [System.Obsolete("OneText lays text out in the RectTransform's rect and has no margin of " +
                         "its own. Inset the RectTransform, or parent the label to a rect that is " +
                         "the size you wanted the text area to be.", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Vector4 margin
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        [System.Obsolete("OneText does not resize its own RectTransform. Add a ContentSizeFitter, " +
                         "which reads preferredWidth/preferredHeight from this label like any " +
                         "other layout element.", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool autoSizeTextContainer
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        [System.Obsolete("OneText has no dirty flag to read: the layout cache is keyed by the " +
                         "values it was built from, so staleness is derived and not stored. To " +
                         "force the rebuild that setting this to true forced, call " +
                         "ForceMeshUpdate().", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool havePropertiesChanged
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        [System.Obsolete("OneText reveals from the start of the text only: MaxVisibleGraphemes " +
                         "(maxVisibleCharacters) sets where the reveal ends, and there is no " +
                         "member for where it begins. Paged text needs its own string per page.",
            true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public int firstVisibleCharacter
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        // TMP parity — end
    }
}
