using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// A named text style: font, size, colour and spacing, as an asset.
    ///
    /// This is the answer to material-preset hell. In TextMesh Pro a preset is
    /// welded to a specific atlas texture, fallback glyphs ignore your outline,
    /// and swapping a font at runtime silently drops the preset, because the
    /// preset is a material, and a material is a rendering object. Nothing
    /// about our rendering is welded to an atlas, so a style can be what it
    /// should have been all along: pure data.
    ///
    /// A label stores a <em>reference</em> to a style, never a copy of its
    /// values. Editing the asset therefore updates every label using it, in the
    /// editor and at runtime, through the same registry pattern the atlas
    /// invalidation already uses. That is also what makes theming a one-line
    /// swap: change the style, every label follows.
    ///
    /// Inheritance is a single <see cref="Extends"/> level with explicit
    /// overrides, and deliberately not more. A full CSS cascade is a debugging
    /// trap ("why is this text blue" becomes an archaeology problem), and the
    /// second level buys almost nothing the first does not.
    /// </summary>
    // The Project window draws this rather than the default script sheet.
    [Icon("Packages/com.onetext.core/Editor/Icons/OneTextStyle.png")]
    [CreateAssetMenu(menuName = "OneText/Text Style", fileName = "New Text Style", order = 210)]
    public sealed class OneTextStyle : ScriptableObject
    {
        /// <summary>
        /// Which fields this style sets. A style that does not set a field
        /// inherits it from its base style, and failing that from the label.
        /// Explicit rather than sentinel values, because "size 0" and "no
        /// opinion about size" are different things and a magic number cannot
        /// tell them apart.
        /// </summary>
        [Flags]
        public enum Fields
        {
            None = 0,
            Font = 1 << 0,
            Size = 1 << 1,
            Color = 1 << 2,
            LineSpacing = 1 << 3,
            LetterSpacing = 1 << 4,
            Weight = 1 << 5,
            Slant = 1 << 6,
            Variations = 1 << 7,
            Outline = 1 << 8,
            Shadow = 1 << 9,
            Glow = 1 << 10,
        }

        [Tooltip("Style this one inherits from. One level only: a style may extend a style, " +
                 "and that style may not extend another.")]
        [SerializeField] private OneTextStyle _extends;

        [SerializeField] private Fields _overrides = Fields.None;

        [SerializeField] private OneFontAsset _font;
        [SerializeField] private float _fontSize = 32f;
        [SerializeField] private Color _color = Color.white;
        [SerializeField] private float _lineSpacing = 1f;
        [SerializeField] private float _letterSpacingEm;
        [SerializeField] private bool _bold;
        [SerializeField] private bool _italic;
        [SerializeField] private List<FontVariation> _variations = new List<FontVariation>();

        // The three decoration parts, flat rather than as a nested
        // TextDecoration, so each has its own override flag: a style that says
        // "outline" and a style that says "outline and shadow" have to be
        // different things, and one serialized struct cannot express which
        // halves of itself are meant.
        [SerializeField] private Color _outlineColor = Color.black;
        [SerializeField] private float _outlineWidth = 0.4f;
        [SerializeField] private Color _shadowColor = new Color(0f, 0f, 0f, 0.75f);
        [SerializeField] private Vector2 _shadowOffset = new Vector2(0.5f, -0.5f);
        [SerializeField] private float _shadowSoftness = 0.35f;
        [SerializeField] private Color _glowColor = Color.white;
        [SerializeField] private float _glowRadius = 0.75f;

        /// <summary>
        /// Raised whenever any style changes. Labels subscribe once and rebuild;
        /// the alternative is every label polling every style it references,
        /// which is the same work multiplied by the number of labels.
        /// </summary>
        public static event Action<OneTextStyle> Changed;

        public OneTextStyle Extends => _extends;

        public Fields Overrides => _overrides;

        public OneFontAsset Font => Resolved(Fields.Font)?._font;

        public float FontSize => Resolved(Fields.Size)?._fontSize ?? 0f;

        public Color Color => Resolved(Fields.Color)?._color ?? UnityEngine.Color.white;

        public float LineSpacing => Resolved(Fields.LineSpacing)?._lineSpacing ?? 0f;

        public float LetterSpacingEm => Resolved(Fields.LetterSpacing)?._letterSpacingEm ?? 0f;

        public bool Bold => Resolved(Fields.Weight)?._bold ?? false;

        public bool Italic => Resolved(Fields.Slant)?._italic ?? false;

        public IReadOnlyList<FontVariation> Variations
        {
            get
            {
                var owner = Resolved(Fields.Variations);
                return owner != null
                    ? (IReadOnlyList<FontVariation>)owner._variations
                    : System.Array.Empty<FontVariation>();
            }
        }

        /// <summary>
        /// The outline, shadow and glow this style asks for: only the parts it
        /// actually sets, so a style that says nothing about decorations lets
        /// the label's own base style and the markup through untouched.
        ///
        /// This is the field the M8 note parked as "later decoration defaults".
        /// It is a property rather than a material, which is the whole
        /// difference: a TMP outline preset is a material, so a heading with one
        /// and a body without it cannot batch and a fallback glyph loses the
        /// outline entirely. Here it is sixteen bytes that ride to the shader in
        /// vertex channels the mesh already carries.
        /// </summary>
        public TextDecoration Decoration
        {
            get
            {
                var decoration = TextDecoration.None;
                var outline = Resolved(Fields.Outline);
                if (outline != null)
                {
                    decoration.Set |= TextDecoration.Parts.Outline;
                    decoration.OutlineColor = outline._outlineColor;
                    decoration.OutlineWidth = outline._outlineWidth;
                }
                var shadow = Resolved(Fields.Shadow);
                if (shadow != null)
                {
                    decoration.Set |= TextDecoration.Parts.Shadow;
                    decoration.ShadowColor = shadow._shadowColor;
                    decoration.ShadowOffset = shadow._shadowOffset;
                    decoration.ShadowSoftness = shadow._shadowSoftness;
                }
                var glow = Resolved(Fields.Glow);
                if (glow != null)
                {
                    decoration.Set |= TextDecoration.Parts.Glow;
                    decoration.GlowColor = glow._glowColor;
                    decoration.GlowRadius = glow._glowRadius;
                }
                return decoration.Clamped();
            }
        }

        /// <summary>True if this style, or the one it extends, sets a field.</summary>
        public bool Sets(Fields field) => Resolved(field) != null;

        /// <summary>
        /// The style that actually decides a field: this one if it overrides it,
        /// otherwise its base. Exactly one level, so this cannot loop and does
        /// not need a visited set.
        /// </summary>
        private OneTextStyle Resolved(Fields field)
        {
            if ((_overrides & field) != 0) return this;
            if (_extends == null) return null;
            return (_extends._overrides & field) != 0 ? _extends : null;
        }

        /// <summary>
        /// Folds this style onto <paramref name="style"/>, leaving anything the
        /// style does not set alone. Markup already applied stays applied where
        /// it is more specific: <c>&lt;color=red&gt;&lt;style=title&gt;</c> is
        /// the author asking for a red title.
        /// </summary>
        public TextStyle Apply(TextStyle style)
        {
            if (Sets(Fields.Size) && style.SizeAbsolute <= 0f) style.SizeAbsolute = FontSize;
            if (Sets(Fields.Color) && !style.HasColor)
            {
                style.Color = Color;
                style.Flags |= TextStyle.Flag.HasColor;
            }
            if (Sets(Fields.LetterSpacing) && !style.HasLetterSpacing)
            {
                style.LetterSpacingEm = LetterSpacingEm;
                style.Flags |= TextStyle.Flag.HasLetterSpacing;
            }
            if (Bold) style.Flags |= TextStyle.Flag.Bold;
            if (Italic) style.Flags |= TextStyle.Flag.Italic;
            return style;
        }

        // ------------------------------------------------------------ mutation

        /// <summary>
        /// Sets the colour at runtime and tells every label. This is the theming
        /// API: dark mode and colourblind palettes are a handful of these, not a
        /// walk over every label in the scene.
        /// </summary>
        public void SetColor(Color color)
        {
            _color = color;
            _overrides |= Fields.Color;
            NotifyChanged();
        }

        public void SetFontSize(float size)
        {
            _fontSize = Mathf.Max(0.01f, size);
            _overrides |= Fields.Size;
            NotifyChanged();
        }

        public void SetFont(OneFontAsset font)
        {
            _font = font;
            _overrides |= Fields.Font;
            NotifyChanged();
        }

        /// <summary>
        /// Sets letter spacing in ems and tells every label. Not clamped away
        /// from zero the way size is: 0 here is the style saying "this text is
        /// drawn at the face's own spacing", which is a different instruction
        /// from saying nothing, and it is how a style pulls text back from a
        /// font that carries a correction of its own.
        /// </summary>
        public void SetLetterSpacing(float ems)
        {
            _letterSpacingEm = ems;
            _overrides |= Fields.LetterSpacing;
            NotifyChanged();
        }

        /// <summary>
        /// Sets the decoration at runtime and tells every label. Only the parts
        /// <paramref name="decoration"/> sets are taken, and only those become
        /// overrides, the same rule the getter reads by.
        /// </summary>
        public void SetDecoration(TextDecoration decoration)
        {
            decoration = decoration.Clamped();
            if (decoration.HasOutline)
            {
                _outlineColor = decoration.OutlineColor;
                _outlineWidth = decoration.OutlineWidth;
                _overrides |= Fields.Outline;
            }
            if (decoration.HasShadow)
            {
                _shadowColor = decoration.ShadowColor;
                _shadowOffset = decoration.ShadowOffset;
                _shadowSoftness = decoration.ShadowSoftness;
                _overrides |= Fields.Shadow;
            }
            if (decoration.HasGlow)
            {
                _glowColor = decoration.GlowColor;
                _glowRadius = decoration.GlowRadius;
                _overrides |= Fields.Glow;
            }
            NotifyChanged();
        }

        /// <summary>Stops overriding a field, so it inherits again.</summary>
        public void ClearOverride(Fields field)
        {
            _overrides &= ~field;
            NotifyChanged();
        }

        /// <summary>
        /// Announces that this style changed. Called for you by the setters and
        /// by the inspector; call it yourself only after writing the serialized
        /// fields some other way.
        /// </summary>
        public void NotifyChanged() => Changed?.Invoke(this);

#if UNITY_EDITOR
        private void OnValidate()
        {
            // One level of inheritance, enforced where it is broken rather than
            // where it is read: a chain would otherwise silently resolve to
            // whatever the second link happened to say.
            if (_extends == this)
            {
                Debug.LogWarning($"OneText: style '{name}' cannot extend itself.", this);
                _extends = null;
            }
            else if (_extends != null && _extends._extends != null)
            {
                Debug.LogWarning(
                    $"OneText: style '{name}' extends '{_extends.name}', which already extends " +
                    $"'{_extends._extends.name}'. Inheritance is one level deep; the third style " +
                    "is ignored. A full cascade makes 'why is this text blue' an archaeology " +
                    "problem, which is the trap this rule exists to avoid.", this);
            }
            if (_fontSize <= 0f) _fontSize = 0.01f;
            NotifyChanged();
        }
#endif
    }
}
