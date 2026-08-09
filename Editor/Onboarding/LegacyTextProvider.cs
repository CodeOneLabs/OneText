using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// The two text components Unity has always shipped: <c>UnityEngine.UI.Text</c>
    /// and <c>TextMesh</c>.
    ///
    /// They are not what most projects are leaving — TMP is — but they are what
    /// is left over in the corners of a project that left uGUI text for TMP
    /// years ago and never finished, and they cost nothing to carry here: no
    /// package gate, no optional assembly, no reason for this half of the module
    /// to be missing from any project.
    ///
    /// The one thing neither of them can give up is its font. A <c>Font</c>
    /// asset is Unity's own import of a <c>.ttf</c>, and OneText builds its atlas
    /// from the file rather than from Unity's import, so the migration follows
    /// the asset back to the file on disk. Arial and LegacyRuntime have no file
    /// on disk — they live inside the editor — and that is a finding rather than
    /// a guess.
    /// </summary>
    public sealed class LegacyTextProvider : IMigrationProvider
    {
        public string Name => "Unity UI";

        public bool TryProjectFontDefaults(out string defaultFontPath,
            out List<string> fallbackFontPaths)
        {
            defaultFontPath = null;
            fallbackFontPaths = null;
            return false;
        }

        /// <summary>Mirrors the switch in <see cref="Inspect"/>; keep the two together.</summary>
        public MigrationKind KindOf(System.Type scriptType)
        {
            if (scriptType == null) return MigrationKind.None;
            if (typeof(Text).IsAssignableFrom(scriptType)) return MigrationKind.Label;
            if (typeof(TextMesh).IsAssignableFrom(scriptType)) return MigrationKind.Mesh;
            if (typeof(Dropdown).IsAssignableFrom(scriptType)) return MigrationKind.Dropdown;
            return MigrationKind.None;
        }

        public MigrationTarget Inspect(Component component, string container, string path)
        {
            if (component is Text text) return FromUiText(text, container, path);
            if (component is TextMesh mesh) return FromTextMesh(mesh, container, path);
            if (component is Dropdown dropdown) return FromDropdown(dropdown, container, path);
            return null;
        }

        // ------------------------------------------------------ UnityEngine.UI

        /// <summary>
        /// A dropdown carries no text of its own — its caption and its rows are
        /// separate label components, each found and converted in its own right.
        /// What it carries is two fields naming those labels, and they are the
        /// reason it has to be swapped for one whose fields are wide enough.
        /// </summary>
        private static MigrationTarget FromDropdown(Dropdown dropdown, string container, string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.Dropdown,
                ComponentType = nameof(Dropdown),
                Provider = "Unity UI",
                Source = dropdown,
                Container = container,
                Path = path,
                Values = new MigrationValues(),
            };

            // Noted here, during the scan, because here is the last moment they
            // can be: the labels are converted before the dropdown is, and a
            // field naming a destroyed component reads None.
            target.Companions.Add(dropdown.captionText != null ? dropdown.captionText.gameObject : null);
            target.Companions.Add(dropdown.itemText != null ? dropdown.itemText.gameObject : null);
            return target;
        }

        private static MigrationTarget FromUiText(Text text, string container, string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.Label,
                ComponentType = nameof(Text),
                Provider = "Unity UI",
                Source = text,
                Container = container,
                Path = path,
            };

            var values = MigrationValues.Default;
            values.Text = text.text ?? string.Empty;
            values.RichText = text.supportRichText;
            values.FontSize = text.fontSize;
            values.AutoSize = text.resizeTextForBestFit;
            values.AutoSizeMin = text.resizeTextMinSize;
            values.AutoSizeMax = text.resizeTextMaxSize;
            values.LineSpacing = text.lineSpacing;
            values.Color = text.color;
            values.RaycastTarget = text.raycastTarget;

            MigrationMapping.FromTextAnchor((int)text.alignment,
                out var horizontal, out var vertical);
            values.Alignment = horizontal;
            values.VerticalAlignment = vertical;

            values.Wrap = MigrationMapping.FromHorizontalWrapMode((int)text.horizontalOverflow);
            values.Overflow = MigrationMapping.FromVerticalWrapMode((int)text.verticalOverflow);

            values.FontSourcePath = SourceFontPath(text.font, target);
            target.Values = values;

            if (text.fontStyle != FontStyle.Normal)
            {
                target.Note(DoctorSeverity.Warning, "font-style",
                    $"the font style is {text.fontStyle}, which OneText does not carry on the " +
                    "component: bold and italic are markup (<b>, <i>) or a style asset. The label " +
                    "will draw upright unless you wrap the text or assign a style.");
            }

            LintText(target, values.Text);
            return target;
        }

        // ---------------------------------------------------------- TextMesh

        private static MigrationTarget FromTextMesh(TextMesh mesh, string container, string path)
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.Mesh,
                ComponentType = nameof(TextMesh),
                Provider = "Unity UI",
                Source = mesh,
                Container = container,
                Path = path,
            };

            var values = MigrationValues.Default;
            values.Text = mesh.text ?? string.Empty;
            values.RichText = mesh.richText;
            values.Color = mesh.color;
            values.LineSpacing = mesh.lineSpacing;
            values.Alignment = MigrationMapping.FromLegacyAlignment((int)mesh.alignment);
            MigrationMapping.FromTextAnchor((int)mesh.anchor, out _, out var vertical);
            values.VerticalAlignment = vertical;
            values.Wrap = TextWrap.NoWrap; // a TextMesh never wraps.
            values.Overflow = TextOverflow.Overflow;
            values.FontSize = MigrationMapping.FromTextMeshSize(
                mesh.fontSize, mesh.characterSize, mesh.font != null ? mesh.font.fontSize : 0);
            values.FontSourcePath = SourceFontPath(mesh.font, target);
            target.Values = values;

            target.Note(DoctorSeverity.Info, "size-approximated",
                $"a TextMesh sizes by fontSize × characterSize and OneTextMesh sizes in world " +
                $"units at ten points to the unit; {values.FontSize:0.##} is the same height by " +
                "that arithmetic, not by measurement. Check it on screen.");

            target.Note(DoctorSeverity.Info, "rect-added",
                "OneTextMesh lays out inside a RectTransform and a TextMesh has none, so one " +
                "replaces the plain Transform. The object keeps its position; it gains a box.");

            LintText(target, values.Text);
            LintMeshLosses(target, values.Text);
            return target;
        }

        // ----------------------------------------------------------- shared

        /// <summary>
        /// The file behind a <c>Font</c>, or a finding saying there is not one.
        ///
        /// <c>AssetDatabase.GetAssetPath</c> answers with the editor's own
        /// resources for the built-in fonts, and those paths cannot be read as
        /// a font file, so they are named as the dead end they are rather than
        /// handed to the font asset creator to fail on.
        /// </summary>
        internal static string SourceFontPath(Font font, MigrationTarget target)
        {
            if (font == null)
            {
                target.Note(DoctorSeverity.Info, "font-default",
                    "no font was assigned, so the label will use the project default from " +
                    "Project Settings > OneText.");
                return null;
            }

            string path = AssetDatabase.GetAssetPath(font);
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", System.StringComparison.Ordinal))
            {
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".ttf" || extension == ".otf" || extension == ".ttc") return path;
            }

            target.Note(DoctorSeverity.Error, "font-source-missing",
                $"'{font.name}' is not a font file in this project" +
                (string.IsNullOrEmpty(path) ? "" : $" (it imports from {path})") +
                ". OneText builds its atlas from the .ttf/.otf itself, and the editor's built-in " +
                "fonts have no file to read. The label is left with no font, which means the " +
                "project default; put the real font file under Assets and assign it.");
            return null;
        }

        internal static void LintText(MigrationTarget target, string text)
        {
            var tags = MigrationMapping.LintTags(text);
            if (tags.Count == 0) return;
            target.Note(DoctorSeverity.Warning, "unsupported-tag",
                $"the text uses {tags.Count} tag(s) OneText has no counterpart for " +
                $"(<{string.Join(">, <", tags)}>). They are printed literally, in the middle of " +
                "the sentence. Nothing is rewritten for you: edit the string.",
                Sample(text));
        }

        internal static void LintMeshLosses(MigrationTarget target, string text)
        {
            var lost = MigrationMapping.LintMeshOnlyLosses(text);
            if (lost.Count == 0) return;
            target.Note(DoctorSeverity.Warning, "no-counterpart",
                $"the text uses <{string.Join(">, <", lost)}>, and OneTextMesh has neither " +
                "sprites nor the animation tags — world text is deliberately the smaller " +
                "component. Those tags will print literally. Use OneTextLabel on a canvas if " +
                "you need them.",
                Sample(text));
        }

        internal static string Sample(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string flat = text.Replace('\n', ' ');
            return flat.Length <= 64 ? flat : flat.Substring(0, 61) + "...";
        }
    }
}
