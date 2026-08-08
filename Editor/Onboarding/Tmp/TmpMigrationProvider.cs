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

            // TMP's onValueChanged and onSubmit are both UnityEvent<string>,
            // and so are OneText's, which is the only reason the persistent
            // call list can be copied across at all. onEndEdit has no OneText
            // counterpart, so its listeners are named rather than moved.
            var serialized = new SerializedObject(field);
            values.ValueChangedCalls = UnityEventTransfer.Read(serialized, "m_OnValueChanged");
            values.SubmitCalls = UnityEventTransfer.Read(serialized, "m_OnSubmit");
            var endEdit = UnityEventTransfer.Read(serialized, "m_OnEndEdit");

            target.Values = values;

            if (endEdit.Count > 0)
            {
                target.Note(DoctorSeverity.Warning, "event-listeners",
                    $"{endEdit.Count:n0} persistent listener(s) on On End Edit cannot be carried: " +
                    "OneTextInputField has no such event. On Submit fires when the user commits " +
                    "with Return; there is no event for losing focus. Re-wire these by hand.");
            }

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

        // ----------------------------------------------------------- dropdown

        private static MigrationTarget FromDropdown(TMP_Dropdown dropdown, string container,
            string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.ReportOnly,
                ComponentType = nameof(TMP_Dropdown),
                Provider = MigrationProviders.TextMeshProName,
                Source = dropdown,
                Container = container,
                Path = path,
            };

            target.Note(DoctorSeverity.Info, "no-counterpart",
                "OneText has no dropdown, so this component is left exactly as it is and keeps " +
                "needing TextMesh Pro.");

            // The caption and item labels are TMP_Text components, and this
            // migration converts every TMP_Text it can see. Saying so here is
            // the difference between a dropdown that visibly loses its caption
            // and one whose owner was warned.
            if (dropdown.captionText != null || dropdown.itemText != null)
            {
                target.Note(DoctorSeverity.Warning, "dangling-reference",
                    "its caption and item labels are TextMesh Pro components that this migration " +
                    "will convert, and a TMP_Dropdown cannot hold a OneTextLabel. Exclude this " +
                    "container, or keep the dropdown and accept re-wiring it by hand.");
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
