using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Project-level text defaults: the font a new label starts with, and the
    /// fallback chain every label inherits. Configured once here instead of
    /// per-asset chains that silently miss a script.
    ///
    /// Lives at <c>Assets/Resources/OneTextSettings.asset</c> so it loads
    /// without any editor code; if it is missing, labels simply have no default
    /// font until one is assigned.
    /// </summary>
    // The Project window draws this rather than the default script sheet.
    [Icon("Packages/com.onetext.core/Editor/Icons/OneTextSettings.png")]
    public sealed class OneTextSettings : ScriptableObject
    {
        /// <summary>Resources path (no extension) the runtime loads from.</summary>
        public const string ResourcePath = "OneTextSettings";

        [Tooltip("Font assigned to a new label when it has none of its own.")]
        [SerializeField] private OneFontAsset _defaultFont;

        [Tooltip("Consulted, in order, for characters a label's own fonts do not cover.")]
        [SerializeField] private List<OneFontAsset> _fallbackFonts = new List<OneFontAsset>();

        [Tooltip("When no font above covers a character, draw it from a font the device the game " +
            "is running on already has — /system/fonts on Android, the system font folders on " +
            "Windows, macOS, Linux and iOS. This happens in the build, on the player's machine, " +
            "not only in the editor. On by default, because a box is the worst outcome for a " +
            "reader. Doctor still warns about every character that needs one: the face is " +
            "whatever that device happens to ship, so two devices can draw the same string " +
            "differently and a device with nothing for the character still draws a box. Web has " +
            "no font folder to look in, so the option finds nothing there.")]
        [SerializeField] private bool _systemFontFallback = true;

        // The fields a new label or a new world text starts with. They are here
        // rather than only as C# field initializers because "every label in this
        // project starts at 24 and does not take clicks" is a project decision,
        // and a project decision that can only be made by editing every object
        // after the fact is not really available. TextMesh Pro's settings page
        // taught people to look for exactly these; this is where they are.
        [Header("New text defaults")]
        [Tooltip("Em size a new label or world text starts at.")]
        [SerializeField] private float _defaultFontSize = 36f;

        [Tooltip("Smallest size auto-size may shrink to, on a new label or world text.")]
        [SerializeField] private float _defaultAutoSizeMin = 10f;

        [Tooltip("Largest size auto-size may grow to, on a new label or world text.")]
        [SerializeField] private float _defaultAutoSizeMax = 128f;

        [Tooltip("Whether a new label takes pointer clicks. On matches Unity's other graphics; " +
            "off is what a screen full of captions wants, because every one of them otherwise " +
            "sits between the pointer and the button behind it.")]
        [SerializeField] private bool _defaultRaycastTarget = true;

        [Tooltip("Whether a new label parses <b>, <color> and the rest of the markup.")]
        [SerializeField] private bool _defaultRichText = true;

        [Tooltip("Whether a new label turns the two characters \\n into a real newline.")]
        [SerializeField] private bool _defaultParseEscapes = true;

        [Tooltip("How a new label treats a line too long for its box.")]
        [SerializeField] private TextWrap _defaultWrap = TextWrap.Wrap;

        [Tooltip("Rect size a label created from the GameObject menu starts with.")]
        [SerializeField] private Vector2 _defaultCanvasSize = new Vector2(320f, 80f);

        [Tooltip("Rect size a world text created from the GameObject menu starts with, in metres.")]
        [SerializeField] private Vector2 _defaultWorldSize = new Vector2(20f, 5f);

        [Header("Glyph atlas")]
        [Tooltip("Atlas texels per em, for every label and world text set to Project — which " +
            "is all of them until somebody says otherwise. Performance (1x) bakes at the size " +
            "the text asks for. Raise it when text is magnified after it is baked: a canvas " +
            "with a scale factor above one, or a camera that gets close to a world mesh. " +
            "Each rung costs the square of itself in atlas area, so a project that raises " +
            "this usually raises the layer count below with it.")]
        [SerializeField] private TextQuality _defaultQuality = TextQuality.Performance;

        [Tooltip("Edge length of each atlas layer. Bigger holds more glyphs at once; " +
            "a CJK project at several sizes needs more than a Latin one.")]
        [SerializeField] private int _atlasTextureSize = 1024;

        [Tooltip("Layers in the atlas texture array. Memory is size x size x layers bytes.")]
        [SerializeField, Range(1, 16)] private int _atlasLayerCount = 4;

        [Header("Prewarm")]
        [Tooltip("Rasterized when the game starts, so the first frame showing a character does not hitch.")]
        [SerializeField] private OneTextCharset _prewarmCharset;

        [Tooltip("Record every character drawn during play, to save as a charset afterwards.")]
        [SerializeField] private bool _recordCharsetInPlayMode;

        [Header("Line breaking")]
        [Tooltip("Word lists installed at startup for the scripts that write no spaces " +
            "(Thai, Lao, Khmer, Burmese). Each replaces the built-in starter list for its script.")]
        [SerializeField] private List<OneTextDictionary> _dictionaries = new List<OneTextDictionary>();

        private static OneTextSettings s_instance;
        private static bool s_searched;

        /// <summary>The project's settings, or null when none has been created.</summary>
        public static OneTextSettings Instance
        {
            get
            {
                if (s_instance == null && !s_searched)
                {
                    s_searched = true;
                    s_instance = Resources.Load<OneTextSettings>(ResourcePath);
                }
                return s_instance;
            }
            set
            {
                s_instance = value;
                s_searched = true;
            }
        }

        public OneFontAsset DefaultFont => _defaultFont;

        public IReadOnlyList<OneFontAsset> FallbackFonts => _fallbackFonts;

        public float DefaultFontSize => _defaultFontSize;

        /// <summary>
        /// What a new label or world text starts as.
        ///
        /// A value type rather than a bag of properties so the caller reads the
        /// project's answer once and cannot get half of it from a settings
        /// asset that was created, or destroyed, between two lines.
        /// </summary>
        public struct TextDefaults
        {
            public float FontSize;
            public float AutoSizeMin;
            public float AutoSizeMax;
            public bool RaycastTarget;
            public bool RichText;
            public bool ParseEscapes;
            public TextWrap Wrap;

            /// <summary>Rect a label created from the GameObject menu gets.</summary>
            public Vector2 CanvasSize;

            /// <summary>Rect a world text created from the GameObject menu gets, in metres.</summary>
            public Vector2 WorldSize;

            /// <summary>
            /// What a project with no settings asset gets. The same numbers the
            /// serialized fields start at, so creating the asset changes
            /// nothing until somebody edits it.
            /// </summary>
            public static TextDefaults Default => new TextDefaults
            {
                FontSize = 36f,
                AutoSizeMin = 10f,
                AutoSizeMax = 128f,
                RaycastTarget = true,
                RichText = true,
                ParseEscapes = true,
                Wrap = TextWrap.Wrap,
                CanvasSize = new Vector2(320f, 80f),
                WorldSize = new Vector2(20f, 5f),
            };
        }

        /// <summary>This asset's answer for a new label or world text.</summary>
        public TextDefaults Defaults => new TextDefaults
        {
            FontSize = _defaultFontSize,
            AutoSizeMin = _defaultAutoSizeMin,
            AutoSizeMax = _defaultAutoSizeMax,
            RaycastTarget = _defaultRaycastTarget,
            RichText = _defaultRichText,
            ParseEscapes = _defaultParseEscapes,
            Wrap = _defaultWrap,
            CanvasSize = _defaultCanvasSize,
            WorldSize = _defaultWorldSize,
        };

        /// <summary>
        /// The project's answer, or the built-in one when no settings asset
        /// exists. What every "a new label starts as" path reads.
        /// </summary>
        public static TextDefaults ProjectDefaults =>
            Instance != null ? Instance.Defaults : TextDefaults.Default;

        /// <summary>
        /// Whether a character no font in the chain covers may be drawn from an
        /// operating-system font. See <see cref="SystemFonts"/> for what that
        /// costs and why Doctor reports it anyway.
        /// </summary>
        public bool SystemFontFallback => _systemFontFallback;

        /// <summary>
        /// The density rung text set to <see cref="TextQuality.Project"/> takes.
        ///
        /// Read at draw time rather than copied into a component when it is
        /// created, unlike the "new text defaults" above. The difference is
        /// what the knob is for: a project that has already converted six
        /// thousand labels and then finds them soft on a scaled canvas needs
        /// one field to fix all six thousand, and a creation-time default fixes
        /// none of them.
        /// </summary>
        public TextQuality DefaultQuality =>
            _defaultQuality == TextQuality.Project ? TextQuality.Performance : _defaultQuality;

        /// <summary>The atlas budget this project asks for.</summary>
        public GlyphAtlasSettings AtlasSettings => new GlyphAtlasSettings
        {
            TextureSize = _atlasTextureSize,
            LayerCount = _atlasLayerCount,
        }.Validated();

        /// <summary>Charset rasterized at startup, or null.</summary>
        public OneTextCharset PrewarmCharset => _prewarmCharset;

        public bool RecordCharsetInPlayMode => _recordCharsetInPlayMode;

        /// <summary>Word lists this project installs at startup.</summary>
        public IReadOnlyList<OneTextDictionary> Dictionaries => _dictionaries;

        /// <summary>
        /// Installs the project's word lists. Runs before the first scene so a
        /// label drawn in Awake wraps Thai the same way one drawn later does.
        /// </summary>
        public void InstallDictionaries()
        {
            foreach (var dictionary in _dictionaries)
                if (dictionary != null) dictionary.Install();
        }

        /// <summary>
        /// Applies the options that have to be in force before anything draws:
        /// the project's word lists, and charset recording, so a play session
        /// can report exactly what it drew.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyPlayModeOptions()
        {
            var settings = Instance;
            if (settings == null) return;
            settings.InstallDictionaries();
            if (settings._recordCharsetInPlayMode)
            {
                CharsetRecorder.Clear();
                CharsetRecorder.Enabled = true;
            }
        }

        /// <summary>Forgets the cached instance (editor use, after creating the asset).</summary>
        public static void Invalidate()
        {
            s_instance = null;
            s_searched = false;
        }
    }
}
