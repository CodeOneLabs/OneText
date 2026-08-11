using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Registers the TextMesh Pro reader with the migration, once per domain
    /// reload.
    ///
    /// This whole assembly compiles only where TMP does — the define constraint
    /// on its asmdef sees to that — so this constructor running at all is the
    /// signal the rest of the module keys off. When TMP is not in the project,
    /// nothing registers, the Hub says as much in a sentence, and the legacy
    /// half of the migration carries on working.
    /// </summary>
    [InitializeOnLoad]
    public static class TmpMigrationBootstrap
    {
        static TmpMigrationBootstrap() => MigrationProviders.Register(new TmpMigrationProvider());
    }

    /// <summary>
    /// Reads TextMesh Pro components and says what they were set to, in
    /// OneText's own terms.
    ///
    /// It reads and nothing else. No component is destroyed here, none is added,
    /// no serialized field is written: <see cref="ComponentMigration"/> owns all
    /// of that for every provider. The split is what keeps the surgery testable
    /// on a machine that has never had TMP installed, and it keeps this file
    /// honest about what a clean-room port is allowed to know — the public API
    /// and the public serialized behaviour of the components, which is what a
    /// project's own scenes already depend on.
    /// </summary>
    public sealed class TmpMigrationProvider : IMigrationProvider
    {
        public string Name => MigrationProviders.TextMeshProName;

        /// <summary>
        /// Mirrors the switch in <see cref="Inspect"/>; keep the two together.
        ///
        /// Assignability rather than equality, because a project that subclassed
        /// <c>TextMeshProUGUI</c> has a script guid of its own and the reference
        /// read off the file names that one.
        /// </summary>
        public MigrationKind KindOf(System.Type scriptType)
        {
            if (scriptType == null) return MigrationKind.None;
            if (typeof(TextMeshProUGUI).IsAssignableFrom(scriptType)) return MigrationKind.Label;
            if (typeof(TextMeshPro).IsAssignableFrom(scriptType)) return MigrationKind.Mesh;
            if (typeof(TMP_InputField).IsAssignableFrom(scriptType)) return MigrationKind.InputField;
            if (typeof(TMP_Dropdown).IsAssignableFrom(scriptType)) return MigrationKind.Dropdown;
            return MigrationKind.None;
        }

        public MigrationTarget Inspect(Component component, string container, string path)
        {
            switch (component)
            {
                case TextMeshProUGUI ugui:
                    return FromText(ugui, MigrationKind.Label, nameof(TextMeshProUGUI),
                        container, path);
                case TextMeshPro mesh:
                    return FromText(mesh, MigrationKind.Mesh, nameof(TextMeshPro), container, path);
                case TMP_InputField field:
                    return FromInputField(field, container, path);
                case TMP_Dropdown dropdown:
                    return FromDropdown(dropdown, container, path);
                default:
                    return null;
            }
        }

        // --------------------------------------------------------------- text

        private static MigrationTarget FromText(TMP_Text text, MigrationKind kind,
            string typeName, string container, string path)
        {
            var target = new MigrationTarget
            {
                Kind = kind,
                ComponentType = typeName,
                Provider = MigrationProviders.TextMeshProName,
                Source = text,
                Container = container,
                Path = path,
            };

            target.Values = ReadText(text, target, kind);
            return target;
        }

        private static MigrationValues ReadText(TMP_Text text, MigrationTarget target,
            MigrationKind kind)
        {
            var values = MigrationValues.Default;
            values.Text = text.text ?? string.Empty;
            values.RichText = text.richText;
            values.FontSize = text.fontSize;
            values.AutoSize = text.enableAutoSizing;
            values.AutoSizeMin = text.fontSizeMin;
            values.AutoSizeMax = text.fontSizeMax;
            values.Color = text.color;
            values.RaycastTarget = text.raycastTarget;
            values.Decoration = DecorationOf(text, target);

            MigrationMapping.FromTmpAlignment((int)text.alignment,
                out var horizontal, out var vertical, out bool approximated, out string what);
            values.Alignment = horizontal;
            values.VerticalAlignment = vertical;
            if (approximated)
            {
                target.Note(DoctorSeverity.Info, "alignment-approximated",
                    $"{what} alignment has no OneText equivalent; the nearest is " +
                    $"{horizontal}/{vertical}.");
            }

            values.Overflow = MigrationMapping.FromTmpOverflow((int)text.overflowMode,
                out string unsupportedOverflow);
            if (unsupportedOverflow != null)
            {
                target.Note(DoctorSeverity.Warning, "overflow-approximated",
                    $"overflow mode {unsupportedOverflow} has no OneText equivalent; the label " +
                    $"is set to {values.Overflow}. " +
                    (unsupportedOverflow == "ScrollRect" || unsupportedOverflow == "Linked"
                        ? "Text that used to flow into another box will not."
                        : "Check what the box looks like when the text is long."));
            }

#if ONETEXT_TMP_WRAPMODE
            values.Wrap = MigrationMapping.FromTmpWrappingMode((int)text.textWrappingMode);
#else
            values.Wrap = MigrationMapping.FromTmpWordWrapping(text.enableWordWrapping);
#endif

            values.LineSpacing = MigrationMapping.LineSpacingFromTmp(text.lineSpacing);
            if (!Mathf.Approximately(text.lineSpacing, 0f))
            {
                target.Note(DoctorSeverity.Info, "line-spacing-approximated",
                    $"line spacing {text.lineSpacing:0.##} is an offset in TMP and a multiplier " +
                    $"here; {values.LineSpacing:0.###} is the same intent, not necessarily the " +
                    "same pixels.");
            }

            var margin = text.margin;
            if (!Mathf.Approximately(margin.sqrMagnitude, 0f))
            {
                target.Note(DoctorSeverity.Warning, "margin-lost",
                    $"this label has a margin of ({margin.x:0.#}, {margin.y:0.#}, {margin.z:0.#}, " +
                    $"{margin.w:0.#}). OneText has no margin: the RectTransform is the text box. " +
                    "Inset the rect by the same amounts, or the text will start where the margin " +
                    "used to end.");
            }

            if (text.fontStyle != FontStyles.Normal)
            {
                target.Note(DoctorSeverity.Warning, "font-style",
                    $"the font style is {text.fontStyle}, which OneText does not hold on the " +
                    "component: bold, italic and the rest are markup (<b>, <i>) or a style asset.");
            }

            ReadFonts(text.font, ref values, target);

            var tags = MigrationMapping.LintTags(values.Text);
            if (tags.Count > 0)
            {
                target.Note(DoctorSeverity.Warning, "unsupported-tag",
                    $"the text uses {tags.Count} TMP tag(s) OneText has no counterpart for " +
                    $"(<{string.Join(">, <", tags)}>). They are printed literally, in the middle " +
                    "of the sentence. v1 reports them and rewrites nothing: edit the string.",
                    Sample(values.Text));
            }

            if (kind == MigrationKind.Mesh)
            {
                var lost = MigrationMapping.LintMeshOnlyLosses(values.Text);
                if (lost.Count > 0)
                {
                    target.Note(DoctorSeverity.Warning, "no-counterpart",
                        $"the text uses <{string.Join(">, <", lost)}>, and OneTextMesh has neither " +
                        "sprites nor animation — world text is deliberately the smaller component. " +
                        "Put this on a canvas with OneTextLabel if you need them.",
                        Sample(values.Text));
                }
            }

            return values;
        }

        /// <summary>
        /// The font file behind a TMP font asset, plus the per-label fallback
        /// table flattened into a list.
        ///
        /// OneText builds its atlas from the <c>.ttf</c> itself rather than from
        /// somebody's baked atlas, so a TMP font asset is only useful here for
        /// the file it points at. A font asset created from a dynamic OS font,
        /// or one whose source file was deleted after baking, points at nothing
        /// — and that is a hard stop for that label rather than something to
        /// paper over.
        /// </summary>
        private static void ReadFonts(TMP_FontAsset font, ref MigrationValues values,
            MigrationTarget target)
        {
            if (font == null)
            {
                target.Note(DoctorSeverity.Info, "font-default",
                    "no font asset was assigned, so the label falls back to the project default " +
                    "in Project Settings > OneText.");
                return;
            }

            values.FontSourcePath = SourcePath(font, target);

            var table = font.fallbackFontAssetTable;
            if (table == null || table.Count == 0) return;

            values.FallbackFontSourcePaths = new List<string>();
            foreach (var fallback in table)
            {
                if (fallback == null) continue;
                string path = SourcePath(fallback, target);
                if (!string.IsNullOrEmpty(path) && !values.FallbackFontSourcePaths.Contains(path))
                    values.FallbackFontSourcePaths.Add(path);
            }
        }

        private static string SourcePath(TMP_FontAsset font, MigrationTarget target)
        {
            string path = FontFilePath(font);
            if (path != null) return path;

            target.Note(DoctorSeverity.Error, "font-source-missing",
                $"the font asset '{font.name}' has no usable source font file. OneText rasterises " +
                "from the .ttf/.otf, not from a baked atlas, so there is nothing to convert. Put " +
                "the original font file under Assets and set it as the asset's Source Font File, " +
                "or assign a OneText font to this label by hand.");
            return null;
        }

        /// <summary>
        /// The font file a TMP font asset was built from, or null.
        ///
        /// Three places to look, because <c>sourceFontFile</c> answers null for
        /// every font asset baked to a static atlas — that is what baking means,
        /// and it is true of the LiberationSans that every project which ever
        /// clicked "Import TMP Essentials" is using. What survives baking is the
        /// GUID of the file it was baked from, written in plain text in the
        /// asset next to the atlas, and following it is the difference between
        /// converting a real project and telling it every font it owns is
        /// missing. The older editor-only reference is checked after it, for
        /// assets written before the GUID existed.
        /// </summary>
        private static string FontFilePath(TMP_FontAsset font)
        {
            if (font == null) return null;

            string path = Usable(AssetDatabase.GetAssetPath(font.sourceFontFile));
            if (path != null) return path;

            var serialized = new SerializedObject(font);

            var guid = serialized.FindProperty("m_SourceFontFileGUID");
            if (guid != null && guid.propertyType == SerializedPropertyType.String &&
                !string.IsNullOrEmpty(guid.stringValue))
            {
                path = Usable(AssetDatabase.GUIDToAssetPath(guid.stringValue));
                if (path != null) return path;
            }

            var editorReference = serialized.FindProperty("m_SourceFontFile_EditorRef");
            if (editorReference != null &&
                editorReference.propertyType == SerializedPropertyType.ObjectReference)
            {
                path = Usable(AssetDatabase.GetAssetPath(editorReference.objectReferenceValue));
                if (path != null) return path;
            }

            var stored = serialized.FindProperty("m_SourceFontFilePath");
            return stored != null && stored.propertyType == SerializedPropertyType.String
                ? Usable(stored.stringValue)
                : null;
        }

        private static string Usable(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return extension == ".ttf" || extension == ".otf" || extension == ".ttc" ? path : null;
        }

        // -------------------------------------------------------- input field

        private static MigrationTarget FromInputField(TMP_InputField field, string container,
            string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.InputField,
                ComponentType = nameof(TMP_InputField),
                Provider = MigrationProviders.TextMeshProName,
                Source = field,
                Container = container,
                Path = path,
            };

            var values = MigrationValues.Default;
            values.Text = field.text ?? string.Empty;
            values.Multiline = field.lineType != TMP_InputField.LineType.SingleLine;
            values.ReadOnly = field.readOnly;
            values.CharacterLimit = field.characterLimit;
            values.CaretWidth = field.caretWidth;
            values.CaretBlinkRate = field.caretBlinkRate;
            values.Interactable = field.interactable;
            values.CaretColor = CaretColour(field);

            values.TextComponentId = InstanceId(field.textComponent);
            values.PlaceholderId = InstanceId(field.placeholder);
            values.TargetGraphicId = InstanceId(field.targetGraphic);
            values.ViewportId = InstanceId(field.textViewport);

            // TMP's onValueChanged and onSubmit are both UnityEvent<string>,
            // and so are OneText's, which is the only reason the persistent
            // call list can be copied across at all. onEndEdit has no OneText
            // counterpart, so its listeners are named rather than moved.
            var serialized = new SerializedObject(field);
            values.ValueChangedCalls = UnityEventTransfer.Read(serialized, "m_OnValueChanged");
            values.SubmitCalls = UnityEventTransfer.Read(serialized, "m_OnSubmit");
            values.EndEditCalls = UnityEventTransfer.Read(serialized, "m_OnEndEdit");

            target.Values = values;

            if (field.contentType != TMP_InputField.ContentType.Standard)
            {
                target.Note(DoctorSeverity.Warning, "no-counterpart",
                    $"content type {field.contentType} is not carried: OneTextInputField has no " +
                    "content type and no character validation, so a password field becomes a " +
                    "plain one and an email field stops validating. Filter the value in your own " +
                    "code, on the value-changed event.");
            }

            if (values.TextComponentId == 0)
            {
                target.Note(DoctorSeverity.Warning, "no-counterpart",
                    "this input field has no text component assigned, so there is nothing for the " +
                    "converted field to draw into.");
            }

            return target;
        }

        private static Color CaretColour(TMP_InputField field)
        {
            // The getter answers with the text component's colour when the
            // field has no custom one, and a field with no text component at
            // all would throw on the way past.
            try { return field.caretColor; }
            catch (System.Exception) { return Color.white; }
        }

        private static int InstanceId(Object component) =>
            component == null ? 0 : component.GetInstanceID();

        // ------------------------------------------------------- decoration

        /// <summary>
        /// The material a label actually draws with, without making one.
        ///
        /// Never through <c>TMP_Text.fontMaterial</c>: that getter calls
        /// <c>GetMaterial(m_sharedMaterial)</c>, which <em>instantiates</em>.
        /// Reading it during a scan would leave a new material asset behind for
        /// every label in the project, from a pass whose whole promise is that
        /// it writes nothing.
        ///
        /// The instance wins when there is one, because that is what somebody
        /// made when they changed the outline on one label rather than on the
        /// preset every label shares.
        /// </summary>
        private static Material MaterialOf(TMP_Text text)
        {
            SerializedObject serialized;
            try { serialized = new SerializedObject(text); }
            catch (System.Exception) { return null; }

            if (serialized.FindProperty("m_fontMaterial")?.objectReferenceValue is Material own)
                return own;
            return serialized.FindProperty("m_sharedMaterial")?.objectReferenceValue as Material;
        }

        /// <summary>
        /// Outline, shadow and glow as OneText holds them, read off the material
        /// TextMesh Pro holds them on.
        ///
        /// The four numbers that come across do so unscaled, because the two
        /// definitions agree: TMP's <c>_OutlineWidth</c>, <c>_UnderlaySoftness</c>
        /// and <c>_GlowOuter</c> are 0..1 and OneText quantises 0..1;
        /// <c>_UnderlayOffsetX/Y</c> are -1..1 and OneText quantises -1..1.
        /// Checked against a real asset rather than against the documentation:
        /// a shipped material's underlay offset of (0.32, -0.4) sits next to
        /// OneText's own default of (0.5, -0.5).
        ///
        /// What has no counterpart is named rather than approximated. A face
        /// dilate silently dropped is a label that is the wrong weight and says
        /// nothing about it, and this module has spent long enough being wrong
        /// quietly.
        /// </summary>
        private static TextDecoration DecorationOf(TMP_Text text, MigrationTarget target)
        {
            var decoration = TextDecoration.None;
            var material = MaterialOf(text);
            if (material == null) return decoration;

            float reach = ReachFactor(text, material, target);

            // Keywords, not values — where the shader has the keyword. TextMesh
            // Pro's SDF shaders gate the underlay behind UNDERLAY_ON and the
            // glow behind GLOW_ON, so a material can carry a border colour and
            // draw nothing, which most of them do: the values are whatever the
            // preset was made from and the keyword is what the inspector's
            // checkbox writes. Reading the values alone put a drop shadow on
            // some two thousand eight hundred labels that never had one.
            //
            // The outline is the trap. `OUTLINE_ON` exists only in the *Mobile*
            // SDF shaders; `TMP_SDF.shader` and its SSD, Overlay and Surface
            // siblings have no such keyword and draw the outline from the value
            // alone (`TMP_SDF.shader`: `float outline = (_OutlineWidth *
            // _ScaleRatioA) * scale;`, no #if around it). Asking a material for
            // a keyword its shader never declared answers false, so gating on
            // it dropped the outline from every label using the desktop shader
            // — measured on a real project: 166 labels came out with the face
            // dilate carried and the outline silently gone.
            //
            // The face dilate is gated by nothing on either shader: it is
            // folded into the weight unconditionally, which is why text that
            // looked right in TMP came out thin when it was dropped.
            bool outlined = Drawn(material, "OUTLINE_ON", Number(material, "_OutlineWidth")) &&
                            Colour(material, "_OutlineColor").a > 0f;
            float thickness = outlined
                ? Number(material, "_OutlineWidth") * RatioA(material) * reach : 0f;

            // `weight = lerp(_WeightNormal, _WeightBold, bold) / 4.0;`
            // `weight = (weight + _FaceDilate) * _ScaleRatioA * 0.5;`
            // — the bold half of that lerp is the fake bold, which is a font
            // style question and reported as one; this is the normal weight,
            // which is a permanent part of how thick the material draws and
            // has nowhere else to go.
            //
            // Less half the outline, because the two renderers put the outline
            // in different places. TextMesh Pro straddles the face's edge with
            // it — `faceAlpha` is read at `d - outline*0.5` and `outlineAlpha`
            // at `d + outline*0.5` — so half the outline is drawn *inside* the
            // face and the dilate under it never shows. Here the outline is
            // wholly outside a face its width does not touch. Copied straight
            // across, a real material's 0.25 under an outline of 0.23 drew a
            // face 45% heavier in ink than the same material in TMP.
            float faceDilate = (Number(material, "_FaceDilate") +
                                Number(material, "_WeightNormal") / 4f) * RatioA(material) * reach
                               - thickness * 0.5f;

            if (faceDilate != 0f)
            {
                decoration.Set |= TextDecoration.Parts.Face;
                decoration.FaceDilate = Mathf.Clamp(faceDilate, -1f, 1f);
            }

            if (outlined)
            {
                decoration.Set |= TextDecoration.Parts.Outline;
                decoration.OutlineColor = Colour(material, "_OutlineColor");

                // The outer edge, not the thickness. Both of this shader's
                // thresholds are measured from the undilated glyph edge —
                // `faceT = 0.5 - faceDilate * REACH_FIELD` and
                // `outlineT = 0.5 - outlineWidth * REACH_FIELD` — so a width
                // handed the ring's thickness has the dilated face eat into it,
                // and a face dilated further than the width swallows it whole.
                // Which is what happened: 0.207 of outline over 0.225 of face
                // put the outline's threshold *inside* the face's and drew no
                // outline at all, on 166 labels of a real project. The width
                // that draws TMP's ring is where its outer edge is, which is
                // the face plus the thickness.
                float outer = faceDilate + thickness;
                if (outer > 1f)
                {
                    target.Note(DoctorSeverity.Warning, "material-effect",
                        $"the outline reaches {outer:0.##} of the distance field and the field " +
                        "knows one, so it is drawn as thick as it can be and comes out thinner " +
                        "than TextMesh Pro drew it. A thicker outline needs a wider field than " +
                        "this renderer bakes.");
                }
                decoration.OutlineWidth = Mathf.Clamp01(outer);
                decoration.OutlineSoftness =
                    Mathf.Clamp01(Number(material, "_OutlineSoftness") * RatioA(material) * reach);
            }

            // The underlay's own ratio, and the glow's. Every one of these four
            // numbers is multiplied by its ratio in the shader before it means
            // anything — `(_UnderlayOffsetX * _ScaleRatioC) * _GradientScale /
            // _TextureWidth`, `_GlowOuter * _ScaleRatioB` — and the ratios are
            // how TextMesh Pro compensates for the font asset's own padding and
            // point size. Two materials with the same 0.5 on the same slider
            // draw different thicknesses on two font assets, which is exactly
            // what a migration copying the raw slider cannot know.
            //
            // `_GlowInner` is deliberately not scaled: TMP's own shader leaves
            // it out of the lerp's ratio (`lerp(_GlowInner, (_GlowOuter *
            // _ScaleRatioB), ...)`), so scaling it here would draw an inner glow
            // TextMesh Pro does not.
            float ratioC = Ratio(material, "_ScaleRatioC") * reach;
            var underlay = Colour(material, "_UnderlayColor");
            float dx = Number(material, "_UnderlayOffsetX") * ratioC;
            float dy = Number(material, "_UnderlayOffsetY") * ratioC;
            float softness = Number(material, "_UnderlaySoftness") * ratioC;
            if (material.IsKeywordEnabled("UNDERLAY_ON") && underlay.a > 0f)
            {
                decoration.Set |= TextDecoration.Parts.Shadow;
                decoration.ShadowColor = underlay;
                decoration.ShadowOffset = new Vector2(Mathf.Clamp(dx, -1f, 1f),
                    Mathf.Clamp(dy, -1f, 1f));
                decoration.ShadowSoftness = Mathf.Clamp01(softness);
            }

            var glow = Colour(material, "_GlowColor");
            if (material.IsKeywordEnabled("GLOW_ON") && glow.a > 0f)
            {
                decoration.Set |= TextDecoration.Parts.Glow;
                decoration.GlowColor = glow;
                decoration.GlowRadius = Mathf.Clamp01(
                    Number(material, "_GlowOuter") * Ratio(material, "_ScaleRatioB") * reach);
                decoration.GlowInner = Mathf.Clamp01(Number(material, "_GlowInner") * reach);
            }

            LintMaterial(material, decoration, target);
            return decoration;
        }

        /// <summary>Everything on the material that OneText has no field for.</summary>
        private static void LintMaterial(Material material, in TextDecoration decoration,
            MigrationTarget target)
        {
            var lost = new List<string>();

            if (material.IsKeywordEnabled("UNDERLAY_INNER"))
            {
                lost.Add("an inner underlay — TextMesh Pro draws that one inside the letter and " +
                         "OneText's shadow is always outside, so carrying it would have put the " +
                         "shadow on the wrong side of the ink");
            }

            float underlayDilate = Number(material, "_UnderlayDilate");
            if (decoration.HasShadow && underlayDilate != 0f)
                lost.Add($"underlay dilate {underlayDilate:0.##} (the shadow comes across at the " +
                         "face's own weight)");

            bool shapedGlow = material.HasProperty("_GlowPower") &&
                              material.GetFloat("_GlowPower") != 1f;
            if (decoration.HasGlow && shapedGlow)
                lost.Add("the glow's power curve (OneText's glow falls off linearly from the edge)");

            if (material.HasProperty("_FaceColor"))
            {
                Color face = material.GetColor("_FaceColor");
                if (face != Color.white)
                {
                    lost.Add($"face colour ({face.r:0.##}, {face.g:0.##}, {face.b:0.##}, " +
                             $"{face.a:0.##}) — TextMesh Pro multiplies it into the label's own " +
                             "colour and OneText has no second colour to multiply by. It is not " +
                             "folded into the colour here because that has not been checked on " +
                             "screen; look at this label and set its colour to what you see");
                }
            }

            if (lost.Count == 0) return;
            target.Note(DoctorSeverity.Warning, "material-effect",
                $"'{material.name}' carries {lost.Count:n0} thing(s) the label cannot: " +
                string.Join("; ", lost) + ".");
        }

        private static float Number(Material material, string property) =>
            material.HasProperty(property) ? material.GetFloat(property) : 0f;

        private static Color Colour(Material material, string property) =>
            material.HasProperty(property) ? material.GetColor(property) : new Color(0, 0, 0, 0);

        /// <summary>
        /// One of TextMesh Pro's three scale ratios, defaulting to 1 where the
        /// shader has no such property.
        ///
        /// They are not a setting anybody types: TMP's material inspector
        /// computes them from the font asset's gradient scale, atlas padding and
        /// sampling point size, and every effect value is multiplied by one of
        /// them in the shader. Which is to say the sliders are in units of that
        /// font asset, and the same 0.5 on two font assets is two thicknesses.
        /// A migration that copies the slider and not the ratio is copying a
        /// number whose unit it left behind.
        /// </summary>
        private static float Ratio(Material material, string property) =>
            material.HasProperty(property) ? material.GetFloat(property) : 1f;

        /// <summary>A shorthand for the one ratio two effects share.</summary>
        private static float RatioA(Material material) => Ratio(material, "_ScaleRatioA");

        /// <summary>
        /// What one unit of a TextMesh Pro effect is worth here.
        ///
        /// Both renderers measure their effects in the width of their own
        /// distance field and both call it 1, and the two are not the same
        /// distance. TMP's is the atlas's gradient scale over the size the
        /// atlas was sampled at — 10 texels over 60 points is a sixth of an em.
        /// OneText's is <see cref="GlyphRasterizer.SpreadPixels"/> texels at the
        /// density the tile is baked at, which for a 40-point label is four
        /// over forty, a tenth. The same 0.23 is a third thinner here, and on
        /// the label that reported this it came out as a hairline where TMP had
        /// a two-pixel edge.
        ///
        /// So the numbers are converted rather than copied, per label, because
        /// the denominator is the label's own baked density and two labels in
        /// different sizes do not share it. That is exactly as far as this goes:
        /// it makes the conversion faithful at the size the label is now. A
        /// label whose size changes afterwards — by hand, by code, by
        /// auto-sizing — drifts, because a reach is texels and not ems. Said
        /// out loud for the labels where that is likely rather than left to be
        /// discovered.
        /// </summary>
        private static float ReachFactor(TMP_Text text, Material material, MigrationTarget target)
        {
            float gradient = Number(material, "_GradientScale");
            float points = text.font != null ? text.font.faceInfo.pointSize : 0f;
            if (gradient <= 0f || points <= 0f) return 1f;

            float size = text.fontSize > 0f ? text.fontSize : points;
            int ppem = GlyphAtlas.QuantizePixelsPerEm(size);
            if (ppem <= 0) return 1f;

            float tmpSpread = gradient / points;              // in ems
            float reach = GlyphRasterizer.SpreadPixels / ppem; // in ems
            float factor = tmpSpread / reach;

            if (text.enableAutoSizing && !Mathf.Approximately(factor, 1f))
            {
                target.Note(DoctorSeverity.Info, "material-effect",
                    $"its outline and dilate are converted for {size:0.#} points, the size it is " +
                    "at now. This label auto-sizes, and a reach is texels rather than ems, so at " +
                    "a very different size they will not be in quite the same proportion.");
            }
            return factor;
        }

        /// <summary>
        /// Whether an effect is actually drawn: the keyword decides where the
        /// shader has one, and the value decides where it has not.
        ///
        /// A material answers false to <c>IsKeywordEnabled</c> for a keyword its
        /// shader never declared, which is indistinguishable from a checkbox
        /// somebody left off — and TextMesh Pro's desktop SDF shaders declare
        /// no <c>OUTLINE_ON</c> at all while drawing the outline from the value.
        /// So the keyword is only evidence when the shader admits to knowing it.
        /// </summary>
        private static bool Drawn(Material material, string keyword, float value)
        {
            if (value <= 0f) return false;
            var shader = material.shader;
            if (shader == null) return true;

            switch (Declares(shader, keyword))
            {
                case true: return material.IsKeywordEnabled(keyword);
                case false: return true; // The value is the whole story.
                // Unreadable: keep the older, quieter answer rather than put an
                // effect on a project we could not check.
                default: return material.IsKeywordEnabled(keyword);
            }
        }

        /// <summary>
        /// Whether a shader declares a keyword, read out of its source, or null
        /// where the source cannot be read.
        ///
        /// Not <c>Shader.keywordSpace</c>, which was the obvious way and is the
        /// wrong one: a keyword space holds every global keyword the project
        /// declares anywhere, so the Mobile shader's <c>OUTLINE_ON</c> is in the
        /// desktop shader's space too and the space answers yes for a shader
        /// that has never heard of it. Not <c>ShaderUtil</c> either — it has no
        /// public way to ask. The source file is the only thing that knows, and
        /// a shader's source is an asset like any other.
        ///
        /// Cached per shader, because this is asked once per material on a
        /// project with thousands of them.
        /// </summary>
        private static bool? Declares(Shader shader, string keyword)
        {
            if (!s_shaderSource.TryGetValue(shader, out string source))
            {
                source = null;
                string path = AssetDatabase.GetAssetPath(shader);
                if (!string.IsNullOrEmpty(path))
                {
                    try { source = System.IO.File.ReadAllText(path); }
                    catch (System.Exception) { source = null; }
                }
                s_shaderSource[shader] = source;
            }
            if (source == null) return null;

            // A whole word: OUTLINE_ON must not match OUTLINE_ONLY.
            int at = source.IndexOf(keyword, System.StringComparison.Ordinal);
            while (at >= 0)
            {
                int end = at + keyword.Length;
                bool before = at == 0 || !IsWordCharacter(source[at - 1]);
                bool after = end >= source.Length || !IsWordCharacter(source[end]);
                if (before && after) return true;
                at = source.IndexOf(keyword, end, System.StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsWordCharacter(char c) => c == '_' || char.IsLetterOrDigit(c);

        private static readonly Dictionary<Shader, string> s_shaderSource =
            new Dictionary<Shader, string>();

        // ----------------------------------------------------------- dropdown

        private static MigrationTarget FromDropdown(TMP_Dropdown dropdown, string container,
            string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.Dropdown,
                ComponentType = nameof(TMP_Dropdown),
                Provider = MigrationProviders.TextMeshProName,
                Source = dropdown,
                Container = container,
                Path = path,
                Values = new MigrationValues(),
            };

            // Noted here, during the scan, because here is the last moment they
            // can be: the labels are converted before the dropdown is, and a
            // field naming a destroyed component reads None. Same reason as the
            // uGUI dropdown, and the same two fields.
            target.Companions.Add(dropdown.captionText != null
                ? dropdown.captionText.gameObject : null);
            target.Companions.Add(dropdown.itemText != null
                ? dropdown.itemText.gameObject : null);

            // The two members OneTextDropdown does not have. Both are TMP's own
            // additions to Unity's dropdown, and both are the kind of thing that
            // would otherwise be discovered by a dropdown behaving differently
            // rather than by anything saying so.
            //
            // Read off the serialized object rather than through the properties
            // because both arrived late in TextMesh Pro's life, and this
            // assembly compiles against every TMP from 3.0.0 up: a property
            // that is not there yet is a build error, not a missing feature.
            // The field names are what the file holds on every version that has
            // them at all.
            var serialized = new SerializedObject(dropdown);
            var multiSelect = serialized.FindProperty("m_MultiSelect");
            var placeholder = serialized.FindProperty("m_Placeholder");

            if (multiSelect != null && multiSelect.boolValue)
            {
                target.Note(DoctorSeverity.Warning, "no-counterpart",
                    "multi-select is on, and OneTextDropdown selects one option like Unity's " +
                    "dropdown does. The converted dropdown keeps the options and the wiring, and " +
                    "selects one at a time.");
            }
            if (placeholder != null && placeholder.objectReferenceValue is Component graphic)
            {
                target.Note(DoctorSeverity.Warning, "no-counterpart",
                    $"its placeholder graphic ('{graphic.gameObject.name}') is not carried: " +
                    "OneTextDropdown has no placeholder, so the first option shows where the " +
                    "placeholder used to when nothing is selected. The object itself is left " +
                    "alone.");
            }
            return target;
        }

        // ------------------------------------------------------ project-wide

        /// <summary>
        /// TMP's project settings: the default font asset and the global
        /// fallback chain, as source font file paths.
        /// </summary>
        public bool TryProjectFontDefaults(out string defaultFontPath,
            out List<string> fallbackFontPaths)
        {
            defaultFontPath = null;
            fallbackFontPaths = new List<string>();

            try
            {
                var settings = TMP_Settings.instance;
                if (settings == null) return false;

                defaultFontPath = FontFilePath(TMP_Settings.defaultFontAsset);

                var fallbacks = TMP_Settings.fallbackFontAssets;
                if (fallbacks != null)
                {
                    foreach (var fallback in fallbacks)
                    {
                        string path = FontFilePath(fallback);
                        if (!string.IsNullOrEmpty(path) && !fallbackFontPaths.Contains(path))
                            fallbackFontPaths.Add(path);
                    }
                }
            }
            catch (System.Exception)
            {
                // No TMP Settings asset in the project: a fair state for a
                // project that only ever had TMP as a transitive dependency.
                return false;
            }

            return !string.IsNullOrEmpty(defaultFontPath) || fallbackFontPaths.Count > 0;
        }

        private static string Sample(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string flat = text.Replace('\n', ' ');
            return flat.Length <= 64 ? flat : flat.Substring(0, 61) + "...";
        }
    }
}
