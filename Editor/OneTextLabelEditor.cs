using System.Collections.Generic;
using OneText.UGUI;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Inspector for <see cref="OneTextLabel"/>.
    ///
    /// The text area stays on top (it is what a label is) and everything
    /// else lives in four tabs: Style, Layout, Animation, Interaction. Tabs
    /// rather than one long scroll because most of what this engine can do
    /// was invisible in the flat list: named styles, sprite sheets, kinsoku,
    /// the typewriter, the whole animation system. A feature nobody can see
    /// in the inspector is a feature that does not exist.
    /// </summary>
    [CustomEditor(typeof(OneTextLabel))]
    [CanEditMultipleObjects]
    public sealed class OneTextLabelEditor : GraphicEditor
    {
        private const string TabPref = "OneText.LabelEditorTab";
        private static readonly string[] s_tabs = { "Style", "Layout", "Animation", "Interaction" };

        private SerializedProperty _text, _fontAssetProperty, _fallbackFonts, _fontSize;
        private SerializedProperty _autoSize, _autoSizeMin, _autoSizeMax;
        private SerializedProperty _style, _namedStyles, _namedFonts, _sprites, _decoration;
        private SerializedProperty _alignment, _verticalAlignment, _wrap, _overflow, _lineSpacing;
        private SerializedProperty _writingMode;
        private SerializedProperty _language, _kinsoku, _cjkLatinSpacing, _punctuationCompression;
        private SerializedProperty _rubyScale;
        private SerializedProperty _richText, _parseEscapes, _precise, _linkClicked, _graphemeRevealed;
        private SerializedProperty _quality;
        private SerializedProperty _animateProperty, _maxVisibleGraphemes;
        private SerializedProperty _revealGranularity, _charactersPerSecond, _punctuationDelays;
        private SerializedProperty _characterRevealed, _revealComplete;

        private FontAxis[] _axes;
        private float[] _axisValues;
        private OneTextLabel _label;
        private bool _showLinks;
        private bool _showMarkupHelp;

        private PreviewDriver _driver;
        private bool _previewing => _driver != null;

        protected override void OnEnable()
        {
            base.OnEnable();
            _label = target as OneTextLabel;
            _text = serializedObject.FindProperty("_text");
            _fontAssetProperty = serializedObject.FindProperty("_font");
            _fallbackFonts = serializedObject.FindProperty("_fallbackFonts");
            _fontSize = serializedObject.FindProperty("_fontSize");
            _autoSize = serializedObject.FindProperty("_autoSize");
            _autoSizeMin = serializedObject.FindProperty("_autoSizeMin");
            _autoSizeMax = serializedObject.FindProperty("_autoSizeMax");
            _style = serializedObject.FindProperty("_style");
            _namedStyles = serializedObject.FindProperty("_namedStyles");
            _namedFonts = serializedObject.FindProperty("_namedFonts");
            _sprites = serializedObject.FindProperty("_sprites");
            _decoration = serializedObject.FindProperty("_decoration");
            _writingMode = serializedObject.FindProperty("_writingMode");
            _alignment = serializedObject.FindProperty("_alignment");
            _verticalAlignment = serializedObject.FindProperty("_verticalAlignment");
            _wrap = serializedObject.FindProperty("_wrap");
            _overflow = serializedObject.FindProperty("_overflow");
            _lineSpacing = serializedObject.FindProperty("_lineSpacing");
            _language = serializedObject.FindProperty("_language");
            _kinsoku = serializedObject.FindProperty("_kinsoku");
            _cjkLatinSpacing = serializedObject.FindProperty("_cjkLatinSpacing");
            _punctuationCompression = serializedObject.FindProperty("_punctuationCompression");
            _rubyScale = serializedObject.FindProperty("_rubyScale");
            _richText = serializedObject.FindProperty("_richText");
            _parseEscapes = serializedObject.FindProperty("_parseEscapes");
            _precise = serializedObject.FindProperty("_precise");
            _quality = serializedObject.FindProperty("_quality");
            _linkClicked = serializedObject.FindProperty("_linkClicked");
            _graphemeRevealed = serializedObject.FindProperty("_graphemeRevealed");
            _animateProperty = serializedObject.FindProperty("_animate");
            _maxVisibleGraphemes = serializedObject.FindProperty("_maxVisibleGraphemes");
            _revealGranularity = serializedObject.FindProperty("_revealGranularity");
            _charactersPerSecond = serializedObject.FindProperty("_charactersPerSecond");
            _punctuationDelays = serializedObject.FindProperty("_punctuationDelays");
            _characterRevealed = serializedObject.FindProperty("_characterRevealed");
            _revealComplete = serializedObject.FindProperty("_revealComplete");
            _axes = null;
        }

        protected override void OnDisable()
        {
            StopPreview();
            base.OnDisable();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_text);
            EditorGUILayout.PropertyField(_richText, new GUIContent("Rich text",
                "Parse markup tags. Off, angle brackets are just text."));
            if (_richText.boolValue) DrawMarkupHelp();
            EditorGUILayout.PropertyField(_parseEscapes, new GUIContent("Parse escapes",
                "Turn \\n, \\t, \\uXXXX and friends into the characters they " +
                "name. Unknown escapes stay literal."));

            EditorGUILayout.Space(6f);
            int tab = EditorPrefs.GetInt(TabPref, 0);
            int newTab = GUILayout.Toolbar(tab, s_tabs);
            if (newTab != tab) EditorPrefs.SetInt(TabPref, newTab);
            EditorGUILayout.Space(4f);

            switch (newTab)
            {
                case 0: DrawStyleTab(); break;
                case 1: DrawLayoutTab(); break;
                case 2: DrawAnimationTab(); break;
                case 3: DrawInteractionTab(); break;
            }

            serializedObject.ApplyModifiedProperties();

            // Keeps the clock readout live during a preview without asking the
            // whole editor to repaint: this window only.
            if (_previewing) Repaint();
        }

        // ---------------------------------------------------------------- tabs

        private void DrawStyleTab()
        {
            EditorGUILayout.PropertyField(_fontAssetProperty, new GUIContent("Font"));
            if (_fontAssetProperty.objectReferenceValue == null)
            {
                var settings = OneTextSettingsProvider.Find();
                if (settings != null && settings.DefaultFont != null)
                {
                    EditorGUILayout.LabelField(" ", $"Using project default: {settings.DefaultFont.name}",
                        EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "No font. Right-click a .ttf/.otf in the project and choose " +
                        "OneText > Create Font Asset, then assign it here (or set a project " +
                        "default in Project Settings > OneText).", MessageType.Warning);
                }
            }
            EditorGUILayout.PropertyField(_fallbackFonts, new GUIContent("Extra fallbacks",
                "Fonts tried, in order, for characters the main font does not cover."), true);
            using (new EditorGUI.DisabledScope(_autoSize.boolValue))
                EditorGUILayout.PropertyField(_fontSize, new GUIContent("Size"));
            EditorGUILayout.PropertyField(_autoSize, new GUIContent("Auto size",
                "Pick the largest size between Min and Max at which the whole text fits " +
                "the rect. Size above is ignored while this is on."));
            if (_autoSize.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(_autoSizeMin, new GUIContent("Min"));
                    EditorGUILayout.PropertyField(_autoSizeMax, new GUIContent("Max"));
                }
            }
            DrawVariationAxes();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Styles", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_style, new GUIContent("Base style",
                "A style asset applied to the whole label. Editing the asset updates " +
                "every label that references it; themes are a style swap."));
            EditorGUILayout.PropertyField(_namedStyles, new GUIContent("Named styles",
                "Styles <style=name> can reach from markup."), true);
            EditorGUILayout.PropertyField(_namedFonts, new GUIContent("Named fonts",
                "Fonts <font=name> can reach from markup."), true);
            EditorGUILayout.PropertyField(_sprites, new GUIContent("Sprite sheet",
                "Sheet for inline <sprite=name> icons. Drawn through the same " +
                "atlas and material as text: no extra draw call."));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Rendering", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_precise, new GUIContent("Precise (MSDF)",
                "Multi-channel distance field. Use for large text or sharp corners/curves; " +
                "costs more atlas memory: four bytes a texel instead of one, in an atlas " +
                "of its own. Off, the label renders through the ordinary single-channel " +
                "SDF, which is what body text wants."));
            EditorGUILayout.PropertyField(_quality, new GUIContent("Quality",
                "Atlas texels per em, as a multiple of what the font size asks for: " +
                "Performance 1x, Medium 1.5x, High 2x, Project whichever the project sets. " +
                "Raise it when the canvas is scaled — the tile is baked for the font size " +
                "and then magnified by the scale factor, and that is the softness. Costs " +
                "the square of itself in atlas area."));
            CanvasScaleHintGUI();
            if (targets.Length == 1) DrawDecorationTable();
            AppearanceControlsGUI();
        }

        /// <summary>
        /// Says when this label is magnified more than its density pays for.
        ///
        /// Both answers are checked, because both exist: the measured screen
        /// scale, which handles this automatically and is capped, and the
        /// quality rung, which is the manual floor under it. What is left is
        /// the gap — a canvas scaled beyond
        /// <see cref="OneTextLabel.PpemCap"/>, or the measurement turned
        /// off — and only that gap is worth a line in the inspector. Nagging
        /// about magnification the label already answered is how a hint teaches
        /// people to stop reading hints.
        /// </summary>
        private void CanvasScaleHintGUI()
        {
            if (targets.Length != 1) return;
            var label = target as OneTextLabel;
            if (label == null) return;

            var canvas = label.canvas;
            if (canvas == null) return;

            // The live factor rather than the CanvasScaler's settings: the
            // scaler writes the factor, and the factor is what magnifies.
            float factor = canvas.rootCanvas != null
                ? canvas.rootCanvas.scaleFactor
                : canvas.scaleFactor;
            float rung = TextQualityScale.ForCanvas((TextQuality)_quality.intValue);
            float covered = Mathf.Max(rung, label.AppliedPpemScale);
            // A tenth of slack: a reference resolution that divides unevenly
            // lands on 1.0667 and nobody needs telling about that.
            if (factor <= covered * 1.1f) return;

            EditorGUILayout.HelpBox(
                $"This canvas scales by {factor:0.##}x, so a {label.FontSize:0.#}-point label " +
                $"is drawn at about {label.FontSize * factor:0} screen pixels off a tile baked " +
                $"for {label.FontSize * covered:0}. Raising Quality bakes a finer tile; it " +
                "costs the square of itself in atlas area.", MessageType.Info);
        }

        private void DrawLayoutTab()
        {
            EditorGUILayout.PropertyField(_writingMode, new GUIContent("Writing mode",
                "Horizontal, or 縦書き: characters top to bottom, columns right to left. " +
                "CJK stands upright and Latin turns ninety degrees (UAX #50); the font's " +
                "vertical forms and vertical advances come from its vert and vmtx tables."));
            bool vertical = _writingMode.enumValueIndex ==
                            (int)TextWritingMode.VerticalRightToLeft;
            // The two alignments turn with the text: one places it along its
            // line or column, the other places the stack of them across the
            // box. Both rows, their labels and the way they turn live in
            // AlignmentRows, which draws them as icon buttons.
            AlignmentRows.InlineAxis(_alignment, vertical);
            AlignmentRows.BlockAxis(_verticalAlignment, vertical);
            EditorGUILayout.PropertyField(_wrap);
            EditorGUILayout.PropertyField(_overflow);
            EditorGUILayout.PropertyField(_lineSpacing, new GUIContent("Line spacing"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Typography", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_language, new GUIContent("Language",
                "BCP-47 tag (ja, zh-Hans, ko…). Picks the right regional glyphs " +
                "for Han characters and locale-specific forms. Empty = neutral."));
            EditorGUILayout.PropertyField(_kinsoku, new GUIContent("Kinsoku",
                "Asian line-break prohibition rules: characters a line must not " +
                "start or end with."));
            EditorGUILayout.PropertyField(_cjkLatinSpacing, new GUIContent("CJK-Latin spacing",
                "Automatic thin space where CJK and Latin text meet."));
            EditorGUILayout.PropertyField(_punctuationCompression, new GUIContent("Punctuation compression",
                "Narrows full-width punctuation where it doubles up."));
            EditorGUILayout.PropertyField(_rubyScale, new GUIContent("Ruby size",
                "Size of a <ruby=…> annotation as a fraction of the text it annotates. " +
                "Half is what Japanese typesetting specs ask for. The line grows to make " +
                "room, and base and annotation never split across a line break."));
            if (!_richText.boolValue)
                EditorGUILayout.HelpBox("Ruby needs Rich text on.", MessageType.Info);
            if (vertical)
            {
                EditorGUILayout.HelpBox(
                    "Vertical text is read-only rendering. Tate-chu-yoko, a vertical caret " +
                    "and bidi inside a column are not implemented; an input field stays " +
                    "horizontal.", MessageType.Info);
            }
        }

        private void DrawAnimationTab()
        {
            EditorGUILayout.PropertyField(_animateProperty, new GUIContent("Animate",
                "Advance the animation clock automatically in play mode. Off, " +
                "drive AnimationTime yourself; a paused game pauses its text."));

            if (!_richText.boolValue)
            {
                EditorGUILayout.HelpBox("Effect tags need Rich text on.", MessageType.Info);
            }
            else if (targets.Length == 1)
            {
                DrawEffectToggles();
            }

            if (targets.Length == 1 && !Application.isPlaying) DrawPreviewControls();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Typewriter", EditorStyles.boldLabel);
            DrawReveal();
            EditorGUILayout.PropertyField(_characterRevealed, new GUIContent("On character revealed",
                "Fired once per revealed unit, under the granularity above: one " +
                "event for 한, not three."));
            EditorGUILayout.PropertyField(_revealComplete, new GUIContent("On reveal complete",
                "Fired once when the text is fully revealed, including after SkipToEnd()."));
            EditorGUILayout.PropertyField(_graphemeRevealed, new GUIContent("On grapheme revealed",
                "The older per-cluster event, kept for anything already listening " +
                "to it. Prefer On character revealed."));
        }

        private void DrawInteractionTab()
        {
            EditorGUILayout.PropertyField(_linkClicked, new GUIContent("On link clicked",
                "Fired with the id of the <link=id> span that was clicked."));
            DrawLinkSummary();

            EditorGUILayout.Space(4f);
            RaycastControlsGUI();
            MaskableControlsGUI();
        }

        // ----------------------------------------------------------- animation

        private const float NameWidth = 78f, CellWidth = 54f;

        /// <summary>
        /// The effect table: one row per built-in effect, one column per tag
        /// parameter. The name toggles the tag around the whole text; the
        /// cells show the tag's values: defaults greyed in until a cell is
        /// edited, dashes where an effect does not read that parameter. The
        /// tag string in the text stays the truth; this is a window onto it.
        /// </summary>
        private void DrawEffectToggles()
        {
            EditorGUILayout.LabelField("Effects (wrap whole text, or tag a span in the text yourself)",
                EditorStyles.miniBoldLabel);

            var wraps = PeelWraps(_text.stringValue, out string core);
            bool changed = false;

            var names = new List<string>(BuiltInEffects.Names);
            names.Sort();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(NameWidth));
            foreach (string column in new[] { "amp", "freq", "speed", "for" })
                GUILayout.Label(column, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(CellWidth));
            EditorGUILayout.EndHorizontal();

            foreach (string name in names)
            {
                int at = wraps.FindIndex(w => w.Name == name);
                var defaults = BuiltInEffects.DefaultsOf(name);
                var uses = BuiltInEffects.UsesOf(name);
                var current = at >= 0
                    ? RichTextParser.ParseEffectParameters(wraps[at].Args.TrimStart())
                    : new TextEffectParameters(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

                EditorGUILayout.BeginHorizontal();
                bool active = GUILayout.Toggle(at >= 0, name, EditorStyles.miniButton,
                    GUILayout.Width(NameWidth));
                if (active != at >= 0)
                {
                    if (active) { wraps.Insert(0, (name, "")); at = 0; }
                    else { wraps.RemoveAt(at); at = -1; }
                    changed = true;
                }

                bool rowChanged = false;
                using (new EditorGUI.DisabledScope(at < 0))
                {
                    float amp = ParamCell((uses & EffectParamUse.Amplitude) != 0,
                        current.Amplitude, defaults.Amplitude, ref rowChanged);
                    float freq = ParamCell((uses & EffectParamUse.Frequency) != 0,
                        current.Frequency, defaults.Frequency, ref rowChanged);
                    float speed = ParamCell((uses & EffectParamUse.Speed) != 0,
                        current.Speed, defaults.Speed, ref rowChanged);
                    // "for" applies to every effect (the animator's envelope,
                    // not the effect); no duration shows as 0 = for ever.
                    float duration = ParamCell(true, current.Duration, 0f, ref rowChanged);

                    if (rowChanged && at >= 0)
                    {
                        wraps[at] = (name, BuildArgs(new TextEffectParameters(
                            amp, freq, speed, current.Extra, duration <= 0f ? float.NaN : duration)));
                        changed = true;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!changed) return;
            WriteWraps(wraps, core);
        }

        /// <summary>
        /// Puts the peeled wraps back around the text. Both tables write through
        /// here and both peel the same set, which is what stops one of them from
        /// silently eating the other's tags on the way past.
        /// </summary>
        private void WriteWraps(List<(string Name, string Args)> wraps, string core)
        {
            // The text area may still hold keyboard focus, and a focused IMGUI
            // field writes its own buffer back over the property on the next
            // frame, silently undoing this edit. Commit and release it first.
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
            for (int i = wraps.Count - 1; i >= 0; i--)
                core = $"<{wraps[i].Name}{wraps[i].Args}>{core}</{wraps[i].Name}>";
            _text.stringValue = core;
        }

        /// <summary>
        /// One table cell. Unused parameters draw as a dash; unset ones show
        /// the default the tag will run with. Editing a cell back to exactly
        /// the default un-sets it; the tag stays as short as what it says.
        /// </summary>
        private static float ParamCell(bool used, float value, float fallback, ref bool changed)
        {
            if (!used)
            {
                GUILayout.Label("-", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(CellWidth));
                return float.NaN;
            }
            float shown = float.IsNaN(value) ? fallback : value;
            float now = EditorGUILayout.DelayedFloatField(shown, GUILayout.Width(CellWidth));
            if (Mathf.Approximately(now, shown)) return value;
            changed = true;
            return Mathf.Approximately(now, fallback) ? float.NaN : now;
        }

        /// <summary>
        /// The shortest tag argument string meaning these values. Invariant
        /// culture, because the parser reads invariant; a comma-decimal
        /// locale must not write tags the runtime cannot read back.
        /// </summary>
        private static string BuildArgs(in TextEffectParameters p)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>(4);
            if (!float.IsNaN(p.Amplitude)) parts.Add("amp=" + p.Amplitude.ToString("0.###", invariant));
            if (!float.IsNaN(p.Frequency)) parts.Add("freq=" + p.Frequency.ToString("0.###", invariant));
            if (!float.IsNaN(p.Speed)) parts.Add("speed=" + p.Speed.ToString("0.###", invariant));
            if (!float.IsNaN(p.Extra)) parts.Add("extra=" + p.Extra.ToString("0.###", invariant));
            if (!float.IsNaN(p.Duration)) parts.Add("for=" + p.Duration.ToString("0.###", invariant));
            return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
        }

        // ---------------------------------------------------------- decorations

        private static readonly (string Name, TextDecoration.Parts Part)[] s_decorations =
        {
            ("outline", TextDecoration.Parts.Outline),
            ("shadow", TextDecoration.Parts.Shadow),
            ("glow", TextDecoration.Parts.Glow),
            // The face is not a tag — there is no <face> to parse defaults out
            // of — and it was not in this table either, which meant a label the
            // migration handed a dilate of 0.25 showed nothing anywhere in the
            // inspector to say so. It draws thicker text and there was no way
            // to see the number, let alone change it.
            ("face", TextDecoration.Parts.Face),
        };

        /// <summary>
        /// What a row shows before it is switched on. Every other row reads it
        /// off the bare tag; the face has no tag, and zero dilate is exactly
        /// what "off" means for it anyway.
        /// </summary>
        private static TextDecoration RowDefaults(string name) =>
            RichTextParser.TryParseDecoration(name, null, out var parsed)
                ? parsed
                : new TextDecoration { Set = TextDecoration.Parts.Face, FaceDilate = 0f };

        /// <summary>
        /// The decoration table, editing the component's decoration field: the
        /// name toggles the part, the cells show what it draws with — the bare
        /// tag's defaults greyed in while a part is off, dashes where a part
        /// does not read that column.
        ///
        /// The component field and not tags in the text, which is where this
        /// table once wrote: the text is the one thing an external system
        /// owns — a Localize String Event, a data binding — and replacing the
        /// string took the decoration with it. Tags remain the way to decorate
        /// a span, and a tag still wins the parts it sets over what is set
        /// here.
        /// </summary>
        private void DrawDecorationTable()
        {
            EditorGUILayout.LabelField("Decorations (whole label; tag a span in the text to override)",
                EditorStyles.miniBoldLabel);

            var value = ReadDecoration();
            bool changed = false;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(NameWidth));
            GUILayout.Label("colour", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(CellWidth));
            foreach (string column in new[] { "w", "x", "y", "soft" })
                GUILayout.Label(column, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(CellWidth));
            EditorGUILayout.EndHorizontal();

            foreach (var (name, part) in s_decorations)
            {
                bool on = (value.Set & part) != 0;
                var defaults = RowDefaults(name);

                EditorGUILayout.BeginHorizontal();
                bool active = GUILayout.Toggle(on, name, EditorStyles.miniButton,
                    GUILayout.Width(NameWidth));
                if (active != on)
                {
                    // Turning a part on seeds it with the bare tag's defaults,
                    // so the first click draws something; turning it off clears
                    // only its flag, so it comes back as it was.
                    if (active) value = defaults.Over(value);
                    else value.Set &= ~part;
                    changed = true;
                    on = active;
                }
                var current = on ? value : defaults;

                bool rowChanged = false;
                using (new EditorGUI.DisabledScope(!on))
                {
                    var edited = current;
                    // The outline's alpha is not carried to the shader (the
                    // channel budget spends that byte on its width), so it is
                    // not offered here either. Showing a swatch whose alpha does
                    // nothing is worse than showing none.
                    bool alpha = name != "outline";
                    switch (name)
                    {
                        case "outline":
                            edited.OutlineColor = ColorCell(current.OutlineColor, alpha, ref rowChanged);
                            edited.OutlineWidth =
                                DecorationCell(true, current.OutlineWidth, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            // The softness has always been carried, from tags
                            // and from a TMP material's Outline Softness, and
                            // had no cell to be seen in.
                            edited.OutlineSoftness =
                                DecorationCell(true, current.OutlineSoftness, ref rowChanged);
                            break;
                        case "face":
                            // No colour: the face draws in the label's own.
                            DecorationCell(false, 0f, ref rowChanged);
                            edited.FaceDilate =
                                DecorationCell(true, current.FaceDilate, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            break;
                        case "shadow":
                            edited.ShadowColor = ColorCell(current.ShadowColor, alpha, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            edited.ShadowOffset.x =
                                DecorationCell(true, current.ShadowOffset.x, ref rowChanged);
                            edited.ShadowOffset.y =
                                DecorationCell(true, current.ShadowOffset.y, ref rowChanged);
                            edited.ShadowSoftness =
                                DecorationCell(true, current.ShadowSoftness, ref rowChanged);
                            break;
                        default:
                            edited.GlowColor = ColorCell(current.GlowColor, alpha, ref rowChanged);
                            edited.GlowRadius =
                                DecorationCell(true, current.GlowRadius, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            DecorationCell(false, 0f, ref rowChanged);
                            break;
                    }

                    if (rowChanged && on)
                    {
                        value = edited;
                        changed = true;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            if (changed) WriteDecoration(value.Clamped());
            DrawDecorationTagMigration();
        }

        private TextDecoration ReadDecoration() => new TextDecoration
        {
            Set = (TextDecoration.Parts)_decoration.FindPropertyRelative("Set").intValue,
            OutlineColor = _decoration.FindPropertyRelative("OutlineColor").colorValue,
            OutlineWidth = _decoration.FindPropertyRelative("OutlineWidth").floatValue,
            OutlineSoftness = _decoration.FindPropertyRelative("OutlineSoftness").floatValue,
            ShadowColor = _decoration.FindPropertyRelative("ShadowColor").colorValue,
            ShadowOffset = _decoration.FindPropertyRelative("ShadowOffset").vector2Value,
            ShadowSoftness = _decoration.FindPropertyRelative("ShadowSoftness").floatValue,
            GlowColor = _decoration.FindPropertyRelative("GlowColor").colorValue,
            GlowRadius = _decoration.FindPropertyRelative("GlowRadius").floatValue,
            GlowInner = _decoration.FindPropertyRelative("GlowInner").floatValue,
            FaceDilate = _decoration.FindPropertyRelative("FaceDilate").floatValue,
        };

        private void WriteDecoration(in TextDecoration value)
        {
            _decoration.FindPropertyRelative("Set").intValue = (int)value.Set;
            _decoration.FindPropertyRelative("OutlineColor").colorValue = value.OutlineColor;
            _decoration.FindPropertyRelative("OutlineWidth").floatValue = value.OutlineWidth;
            _decoration.FindPropertyRelative("OutlineSoftness").floatValue = value.OutlineSoftness;
            _decoration.FindPropertyRelative("ShadowColor").colorValue = value.ShadowColor;
            _decoration.FindPropertyRelative("ShadowOffset").vector2Value = value.ShadowOffset;
            _decoration.FindPropertyRelative("ShadowSoftness").floatValue = value.ShadowSoftness;
            _decoration.FindPropertyRelative("GlowColor").colorValue = value.GlowColor;
            _decoration.FindPropertyRelative("GlowRadius").floatValue = value.GlowRadius;
            _decoration.FindPropertyRelative("GlowInner").floatValue = value.GlowInner;
            _decoration.FindPropertyRelative("FaceDilate").floatValue = value.FaceDilate;
        }

        /// <summary>
        /// The way out for text this table's earlier version wrote: whole-text
        /// decoration wraps still render (a tag is a tag), but they live in
        /// the string, and the string is what a Localize String Event or a
        /// script replaces. One click parses them into the component field and
        /// takes them out of the text; effect wraps stay where they are.
        /// </summary>
        private void DrawDecorationTagMigration()
        {
            var wraps = PeelWraps(_text.stringValue, out string core);
            if (!wraps.Exists(w => RichTextParser.IsDecoration(w.Name))) return;

            EditorGUILayout.HelpBox(
                "This text is wrapped in decoration tags (an earlier version of this table " +
                "wrote them there). They render, but anything that sets the text — a Localize " +
                "String Event, a script — replaces them with it. Moving them onto the " +
                "component keeps them through any text change.", MessageType.Info);
            if (!GUILayout.Button("Move tags onto the component")) return;

            var value = ReadDecoration();
            var rest = new List<(string Name, string Args)>();
            foreach (var wrap in wraps)
            {
                // Forward order is outermost first, and Over gives the last
                // word to the caller, so the innermost tag lands last — the
                // same part-by-part resolution the parser gives nested tags.
                if (RichTextParser.IsDecoration(wrap.Name) &&
                    RichTextParser.TryParseDecoration(wrap.Name, wrap.Args.TrimStart(), out var tag))
                    value = tag.Over(value);
                else rest.Add(wrap);
            }
            WriteDecoration(value.Clamped());
            WriteWraps(rest, core);
        }

        private static Color32 ColorCell(Color32 value, bool alpha, ref bool changed)
        {
            Color now = EditorGUILayout.ColorField(GUIContent.none, value, false, alpha, false,
                GUILayout.Width(CellWidth));
            if (!alpha) now.a = 1f;
            var result = (Color32)now;
            if (Same(result, value)) return value;
            changed = true;
            return result;
        }

        /// <summary>
        /// One decoration cell. Unlike the effect table's, a value here is never
        /// "unset"; a decoration tag's arguments all have real defaults and a
        /// number typed back to the default simply drops out of the tag when it
        /// is rebuilt, rather than needing a NaN to say so.
        /// </summary>
        private static float DecorationCell(bool used, float value, ref bool changed)
        {
            if (!used)
            {
                GUILayout.Label("-", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(CellWidth));
                return 0f;
            }
            float now = EditorGUILayout.DelayedFloatField(value, GUILayout.Width(CellWidth));
            if (Mathf.Approximately(now, value)) return value;
            changed = true;
            return now;
        }

        private static bool Same(Color32 a, Color32 b) =>
            a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        /// <summary>
        /// Splits leading whole-text effect and decoration wraps off
        /// <paramref name="text"/>:
        /// <c>&lt;shake&gt;&lt;wave amp=3&gt;hi&lt;/wave&gt;&lt;/shake&gt;</c>
        /// becomes [shake, wave amp=3] over a core of <c>hi</c>. Only effect
        /// tags that wrap the ENTIRE text peel; anything else stays in the
        /// core untouched.
        /// </summary>
        private static List<(string Name, string Args)> PeelWraps(string text, out string core)
        {
            var wraps = new List<(string, string)>();
            while (text.Length > 3 && text[0] == '<')
            {
                int open = text.IndexOf('>');
                if (open <= 1) break;
                string token = text.Substring(1, open - 1);
                int space = token.IndexOf(' ');
                string name = space < 0 ? token : token.Substring(0, space);
                if (!BuiltInEffects.Has(name) && !RichTextParser.IsDecoration(name)) break;
                string close = $"</{name}>";
                if (!text.EndsWith(close, System.StringComparison.Ordinal)) break;
                if (open + 1 > text.Length - close.Length) break;
                wraps.Add((name, space < 0 ? "" : token.Substring(space)));
                text = text.Substring(open + 1, text.Length - close.Length - open - 1);
            }
            core = text;
            return wraps;
        }

        /// <summary>
        /// Runs the animation clock outside play mode, so seeing a wave means
        /// clicking a button rather than entering play mode. The clock rides
        /// the queued player loop inside <see cref="PreviewDriver"/> and resets
        /// to zero on stop; zero is the "show effects finished" state the label
        /// already knows.
        /// </summary>
        private void DrawPreviewControls()
        {
            EditorGUILayout.Space(2f);
            bool now = GUILayout.Toggle(_previewing, _previewing ? "■ Stop preview" : "▶ Preview animation",
                GUI.skin.button);
            // Three numbers, because a still preview has three unrelated
            // causes and one readout that cannot tell them apart is worse than
            // none: it sends you to fix the plumbing on a label that has no
            // effect tag in it. Tags say whether there is anything to animate,
            // the clock says whether the tick loop is alive, and the mesh
            // counter says whether those ticks are actually changing what is
            // drawn — leaving a still screen with all three healthy as purely
            // a repaint problem.
            if (_previewing && _label != null)
            {
                int tags = PreviewEffectTagCount();
                EditorGUILayout.LabelField("Effect tags", tags.ToString());
                EditorGUILayout.LabelField("Clock", $"{_label.AnimationTime:F2}s");
                EditorGUILayout.LabelField("Mesh", _driver.Ticks == 0
                    ? "waiting for the first tick"
                    : $"{_driver.MovedTicks} of {_driver.Ticks} ticks changed it");
                // Only when the mesh is standing still, and then everything a
                // rebuild can be stopped by, at once. A label that is not
                // dirtied never reaches the rebuild; a culled one is turned
                // away at the top of it; a rebuild that runs with no spans
                // re-emits the same tiles it emitted last time. Three
                // different repairs, and from a still label they look alike.
                if (_driver.Ticks > 0 && _driver.MovedTicks == 0)
                    EditorGUILayout.LabelField(" ", StalledPreviewReport(), EditorStyles.miniLabel);
                if (tags == 0)
                {
                    EditorGUILayout.HelpBox(_richText.boolValue
                        ? "No effect tag in this text, so there is nothing for the clock to " +
                          "move. Tag a span (<wave>like this</wave>) or use the toggles above."
                        : "Rich text is off, so any effect tag in this text is just " +
                          "characters. Turn it on for the tags to become effects.",
                        MessageType.Info);
                }
            }
            if (now == _previewing) return;
            if (now)
            {
                var go = new GameObject("OneText Preview") { hideFlags = HideFlags.HideAndDontSave };
                _driver = go.AddComponent<PreviewDriver>();
                _driver.Label = _label;
                if (_label != null) _label.RegisterDirtyVerticesCallback(_driver.NoteDirty);
                // The first kick; the driver keeps the loop alive from inside.
                EditorApplication.QueuePlayerLoopUpdate();
            }
            else
            {
                StopPreview();
            }
        }

        /// <summary>
        /// Everything that can hold a rebuild still, read off the label rather
        /// than guessed at: whether the clock's writes are reaching the dirty
        /// flag at all, whether the tags became spans, and whether the canvas
        /// is turning the rebuild away before it starts.
        /// </summary>
        private string StalledPreviewReport()
        {
            var renderer = _label.canvasRenderer;
            return $"dirtied {_driver.DirtiedTicks}  ·  spans {_label.EffectSpanCount}  ·  " +
                   $"quads {_label.DrawnQuads.Count}  ·  " +
                   (_label.isActiveAndEnabled ? "active" : "NOT ACTIVE") + "  ·  " +
                   (_label.canvas != null ? $"canvas '{_label.canvas.name}'" : "NO CANVAS") + "  ·  " +
                   (renderer != null && renderer.cull ? "CULLED" : "not culled");
        }

        private string _previewTagsFrom;
        private int _previewTagCount;

        /// <summary>
        /// Effect tags in the text as the animator will see them: counted off
        /// a real parse rather than off a search for '&lt;', so an unknown
        /// name, a decoration tag and anything inside a <c>&lt;noparse&gt;</c>
        /// all count as nothing, which is exactly what they will animate as.
        /// Cached on the string, because the inspector repaints every frame
        /// while a preview runs and the text almost never changes between two
        /// of them.
        /// </summary>
        private int PreviewEffectTagCount()
        {
            if (!_richText.boolValue) return 0;
            string text = _text.stringValue ?? "";
            if (_previewTagsFrom == text) return _previewTagCount;
            var parsed = new RichTextResult();
            RichTextParser.Parse(text, parsed);
            _previewTagsFrom = text;
            _previewTagCount = parsed.Effects.Count;
            return _previewTagCount;
        }

        private void StopPreview()
        {
            if (_driver != null && _label != null)
                _label.UnregisterDirtyVerticesCallback(_driver.NoteDirty);
            if (_driver != null) Object.DestroyImmediate(_driver.gameObject);
            _driver = null;
            if (_label != null) _label.AnimationTime = 0f;
            Canvas.ForceUpdateCanvases();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        /// <summary>
        /// The preview clock. The queued player loop keeps <c>Update</c>
        /// ticking outside play mode and repaints the Game view, but it does
        /// NOT run the canvas pass: in edit mode <c>willRenderCanvases</c>
        /// never fires from that loop, so a label dirtied by the clock stays
        /// dirty — the clock advances while every quad, and the inspector's
        /// first-quad readout with it, stands still. The rebuild has to be
        /// forced by hand each tick, the same call <see cref="StopPreview"/>
        /// already needed to snap the mesh back to zero. (The stale frames
        /// this driver was once rewritten to escape were never the update
        /// mechanism at all: they were the shared material being destroyed and
        /// re-assigned mid-rebuild, gone since labels hold the atlas from
        /// enable.) Each iteration queues the next, so the loop sustains
        /// itself until the driver is destroyed.
        /// </summary>
        [ExecuteAlways]
        private sealed class PreviewDriver : MonoBehaviour
        {
            public OneTextLabel Label;

            /// <summary>
            /// Ticks run, and how many of them changed what is drawn. The
            /// second number is the one that matters: it is the difference
            /// between a preview that is not running and a preview that is
            /// running on a label with nothing to animate.
            /// </summary>
            public int Ticks, MovedTicks;

            /// <summary>
            /// Times the label reported its vertices dirty. The clock's own
            /// writes should raise this once a tick; a label that never raises
            /// it is one whose <c>SetVerticesDirty</c> is being dropped, and no
            /// amount of forcing the canvas afterwards will draw a new frame.
            /// </summary>
            public int DirtiedTicks;

            public void NoteDirty() => DirtiedTicks++;

            private double _last;
            private int _checksum;
            private bool _checksumTaken;

            private void OnEnable() => _last = EditorApplication.timeSinceStartup;

            private void Update()
            {
                if (Application.isPlaying || Label == null) return;
                double now = EditorApplication.timeSinceStartup;
                Label.AnimationTime += (float)(now - _last);
                _last = now;
                // The rebuild the dirty flag is waiting for; nothing else runs
                // it in edit mode. Both calls, because they fail in different
                // places: the canvas pass is what a real frame does, and the
                // direct rebuild is the one every animation test drives, so a
                // label the rebuild queue never took still gets its mesh.
                // Whichever runs first leaves the other a no-op, finding the
                // vertices already clean.
                Canvas.ForceUpdateCanvases();
                Label.Rebuild(UnityEngine.UI.CanvasUpdate.PreRender);

                Ticks++;
                // Whether that rebuild changed a pixel, which is the only
                // honest answer to "is the preview running". One quad's
                // position cannot give it: a tag that starts mid-string leaves
                // the first quad still while the tagged ones move, and a
                // <fade> moves no quad anywhere — it moves alpha.
                int checksum = MeshChecksum(Label);
                if (_checksumTaken && checksum != _checksum) MovedTicks++;
                _checksum = checksum;
                _checksumTaken = true;

                EditorApplication.QueuePlayerLoopUpdate();
                // The queued loop repaints game views on its own. Scene views
                // need asking, and ONLY scene views: repainting every editor
                // window per frame (RepaintAllViews) is what turns a smooth
                // preview into a slideshow.
                SceneView.RepaintAll();
            }

            /// <summary>
            /// Everything an effect can change about a tile, in one number:
            /// position for the ambient effects, size for a scale, rotation
            /// for a spin, colour for every appearance effect there is. A
            /// checksum watching only position would call a working
            /// <c>&lt;fade&gt;</c> frozen.
            /// </summary>
            private static int MeshChecksum(OneTextLabel label)
            {
                var drawn = label.DrawnQuads;
                int hash = drawn.Count;
                for (int i = 0; i < drawn.Count; i++)
                {
                    var quad = drawn[i];
                    hash = hash * 31 + quad.Position.GetHashCode();
                    hash = hash * 31 + quad.Size.GetHashCode();
                    hash = hash * 31 + quad.Rotation.GetHashCode();
                    // Built by hand rather than through Color32's own hash,
                    // which is ValueType's: it boxes, and this runs per tile
                    // per tick.
                    hash = hash * 31 + (quad.Color.r << 24 | quad.Color.g << 16 |
                                        quad.Color.b << 8 | quad.Color.a);
                }
                return hash;
            }
        }

        /// <summary>
        /// The typewriter, scrubbable: a slider over grapheme clusters with the
        /// count read from the real layout, so an Arabic ligature or a Hangul
        /// syllable is one step, exactly what MaxVisibleGraphemes will do at
        /// runtime.
        /// </summary>
        private void DrawReveal()
        {
            EditorGUILayout.PropertyField(_revealGranularity, new GUIContent("Granularity",
                "What one step is. Grapheme is the historical behaviour; Cluster keeps a " +
                "Thai leading vowel with its consonant and a Khmer subscript with its stack; " +
                "Syllable also refuses to give 。 、 っ ゃ ー steps of their own. Korean is one " +
                "step per syllable block under all three."));
            EditorGUILayout.PropertyField(_charactersPerSecond, new GUIContent("Units per second",
                "Speed of the label's own typewriter. 0 leaves the reveal to whoever is " +
                "driving MaxVisibleGraphemes."));

            if (targets.Length == 1)
            {
                int units = _label != null ? _label.RevealUnitCount : 0;
                int graphemes = _label != null ? _label.GraphemeCount : 0;
                // The number the granularity setting is actually about. A
                // designer choosing between three modes has no way to tell what
                // they bought without seeing the step count move.
                EditorGUILayout.LabelField(" ", $"{units} units / {graphemes} clusters",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.PropertyField(_punctuationDelays, new GUIContent("Punctuation delays",
                "Extra seconds held after a unit containing one of these characters."));
            if (targets.Length == 1 && _punctuationDelays.arraySize == 0 &&
                GUILayout.Button("Fill with recommended delays"))
            {
                // Offered, never shipped as a default: a delay table is content,
                // and a package that silently retimed everyone's dialogue on
                // upgrade would be a package that broke it.
                Undo.RecordObject(_label, "Fill punctuation delays");
                PunctuationDelays.Recommended(_label.PunctuationDelays);
                EditorUtility.SetDirty(_label);
                serializedObject.Update();
            }

            bool all = _maxVisibleGraphemes.intValue < 0;
            bool nowAll = EditorGUILayout.Toggle(new GUIContent("Show all",
                "Off exposes the reveal slider: the typewriter primitive. " +
                "Drive MaxVisibleGraphemes from a script for the real thing."), all);
            if (nowAll != all)
                _maxVisibleGraphemes.intValue = nowAll ? -1 : 0;

            if (nowAll || targets.Length > 1) return;
            int count = _label != null ? _label.GraphemeCount : 0;
            _maxVisibleGraphemes.intValue = EditorGUILayout.IntSlider(
                new GUIContent("Revealed"), Mathf.Min(_maxVisibleGraphemes.intValue, count), 0, count);
        }

        // ---------------------------------------------------------------- font

        /// <summary>
        /// Variable fonts get one slider per axis, clamped to the ranges in the
        /// font's own fvar table: no guessing at valid weights.
        /// </summary>
        private void DrawVariationAxes()
        {
            if (_label == null || targets.Length > 1) return;

            if (_axes == null)
            {
                _axes = _label.GetVariationAxes();
                _axisValues = new float[_axes.Length];
                for (int i = 0; i < _axes.Length; i++) _axisValues[i] = _axes[i].Default;
            }
            if (_axes.Length == 0) return;

            EditorGUILayout.LabelField("Variable axes", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            bool changed = false;
            for (int i = 0; i < _axes.Length; i++)
            {
                float value = EditorGUILayout.Slider(
                    _axes[i].Tag, _axisValues[i], _axes[i].Minimum, _axes[i].Maximum);
                // Weight and width snap to steps of 10. Every distinct axis
                // value is its own font instance and its own set of atlas
                // tiles, so a raw drag is a bake per tick, an atlas full of
                // near-identical glyphs nobody will draw again. Snapping turns
                // a drag into a handful of instances, and the eye cannot tell
                // wght 403 from 400 anyway.
                float step = SliderStep(_axes[i].Tag);
                if (step > 0f)
                    value = Mathf.Clamp(Mathf.Round(value / step) * step,
                        _axes[i].Minimum, _axes[i].Maximum);
                // Only a change in the value that will actually be applied
                // counts; a drag inside one snap bucket must not rebuild.
                if (!Mathf.Approximately(value, _axisValues[i]))
                {
                    _axisValues[i] = value;
                    changed = true;
                }
            }
            EditorGUI.indentLevel--;

            if (!changed) return;
            var variations = new FontVariation[_axes.Length];
            for (int i = 0; i < _axes.Length; i++)
                variations[i] = new FontVariation(_axes[i].Tag, _axisValues[i]);
            _label.SetVariations(variations);
            EditorUtility.SetDirty(_label);
        }

        /// <summary>
        /// Snap size for an axis slider, 0 for continuous. Only the axes whose
        /// fine values are visually indistinguishable get a step; slant in
        /// degrees or italic 0..1 would be ruined by a step of 10.
        /// </summary>
        private static float SliderStep(string tag) =>
            tag == "wght" || tag == "wdth" ? 10f : 0f;

        // -------------------------------------------------------------- markup

        /// <summary>
        /// The tag reference, in the one place someone wondering "what can I
        /// type in here" is already looking.
        /// </summary>
        private void DrawMarkupHelp()
        {
            _showMarkupHelp = EditorGUILayout.Foldout(_showMarkupHelp, "Markup reference", true);
            if (!_showMarkupHelp) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("<b> <i> <u> <s>", "bold, italic, underline, strikethrough", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<color=#f80> <size=32> <mark=#ff0>", "colour, size, highlight", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<style=name> <font=name>", "named style / font (lists in Style tab)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<sprite=name>", "inline icon from the sprite sheet", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<link=id>…</link>", "clickable span (event in Interaction tab)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<align=center> <nobr>", "paragraph alignment, no-break span", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<josa=을>", "Korean particle picked from the previous word", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<ruby=ふりがな>漢字</ruby>", "ruby annotation (size in the Layout tab)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<wait=0.5>", "pause the typewriter here for half a second", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("<outline> <shadow> <glow>",
                "decorations (table in the Style tab)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"<{string.Join("> <", BuiltInEffects.Names)}>",
                "animation effects (try them in the Animation tab)", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private void DrawLinkSummary()
        {
            if (_label == null || targets.Length > 1) return;
            var links = _label.Links;
            if (links.Count == 0) return;

            _showLinks = EditorGUILayout.Foldout(_showLinks, $"Links ({links.Count})", true);
            if (!_showLinks) return;

            EditorGUI.indentLevel++;
            string display = _label.DisplayText;
            foreach (var link in links)
            {
                int length = Mathf.Min(link.Length, Mathf.Max(0, display.Length - link.Start));
                EditorGUILayout.LabelField(link.Id, display.Substring(link.Start, length));
            }
            EditorGUI.indentLevel--;
        }
    }
}
