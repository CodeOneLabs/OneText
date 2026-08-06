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

        [Tooltip("Default em size for new labels.")]
        [SerializeField] private float _defaultFontSize = 36f;

        [Tooltip("When no font above covers a character, draw it from a font the operating system " +
            "has instead of a box. On by default, because a box is the worst outcome for a reader. " +
            "Doctor still warns about every character that needs one: the face is the one on this " +
            "machine, and another device may have a different one or none. Web has no font " +
            "directory to look in, so the option does nothing there.")]
        [SerializeField] private bool _systemFontFallback = true;

        [Header("Glyph atlas")]
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
        /// Whether a character no font in the chain covers may be drawn from an
        /// operating-system font. See <see cref="SystemFonts"/> for what that
        /// costs and why Doctor reports it anyway.
        /// </summary>
        public bool SystemFontFallback => _systemFontFallback;

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
