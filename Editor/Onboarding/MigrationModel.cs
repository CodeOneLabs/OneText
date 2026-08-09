using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>Which OneText component a found component turns into, if any.</summary>
    public enum MigrationKind
    {
        /// <summary>Nothing to do: the component is not one this module knows.</summary>
        None,

        /// <summary>Becomes <c>OneTextLabel</c>.</summary>
        Label,

        /// <summary>Becomes <c>OneTextMesh</c>.</summary>
        Mesh,

        /// <summary>Becomes <c>OneTextInputField</c>.</summary>
        InputField,

        /// <summary>
        /// Becomes <c>OneTextDropdown</c>.
        ///
        /// A dropdown is here for its two labels rather than for itself:
        /// <c>UnityEngine.UI.Dropdown</c> declares them as <c>Text</c>, nothing
        /// can widen that, and converting the labels it points at leaves it with
        /// a blank caption and empty rows. It is converted last of everything on
        /// its container, because it has to be handed labels that already are
        /// what they are going to be.
        /// </summary>
        Dropdown,

        /// <summary>
        /// Found, named in the report, and left alone: there is no OneText
        /// counterpart to swap in. A dropdown is the whole of this category
        /// today, and pretending otherwise would be worse than saying so.
        /// </summary>
        ReportOnly,
    }

    /// <summary>
    /// One thing worth saying about one component, named by the rule that found
    /// it.
    ///
    /// Deliberately the same shape as <see cref="DoctorFinding"/> — severity,
    /// rule name, message, where — because the two reports are read by the same
    /// person on the same day and one of them already taught the Hub how to draw
    /// a finding. Only the "where" differs: Doctor locates a string by locale and
    /// key, this locates a component by container and hierarchy path.
    /// </summary>
    public struct MigrationFinding
    {
        public DoctorSeverity Severity;

        /// <summary>Rule name, so a finding can be looked up rather than interpreted.</summary>
        public string Rule;

        public string Message;

        /// <summary>The scene or prefab asset path the component lives in.</summary>
        public string Container;

        /// <summary>The hierarchy path inside that container.</summary>
        public string Path;

        /// <summary>The component type this is about, e.g. <c>TextMeshProUGUI</c>.</summary>
        public string Component;

        /// <summary>The offending text, trimmed to something a log line can hold.</summary>
        public string Sample;

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(Severity.ToString().ToLowerInvariant()).Append(": [")
                   .Append(Rule).Append("] ").Append(Message);
            if (!string.IsNullOrEmpty(Component)) builder.Append("  on ").Append(Component);
            if (!string.IsNullOrEmpty(Path)) builder.Append("  at ").Append(Path);
            if (!string.IsNullOrEmpty(Container)) builder.Append("  in ").Append(Container);
            return builder.ToString();
        }
    }

    /// <summary>
    /// One persistent listener, lifted out of a UnityEvent as plain data.
    ///
    /// The component it was serialized on is about to be destroyed, so nothing
    /// that reads through it survives the swap. This is what does.
    /// </summary>
    public struct MigrationPersistentCall
    {
        public Object Target;

        /// <summary>
        /// The target's instance id, read while it was still alive.
        ///
        /// By the time this is written back, the component it names may have
        /// been destroyed, and a destroyed Unity object compares equal to null
        /// while still being a perfectly good dictionary key — if you took the
        /// key first. This is that key.
        /// </summary>
        public int TargetId;

        public string TargetAssemblyTypeName;
        public string MethodName;
        public int Mode;
        public int CallState;
        public Object ObjectArgument;
        public int ObjectArgumentId;
        public string ObjectArgumentAssemblyTypeName;
        public int IntArgument;
        public float FloatArgument;
        public string StringArgument;
        public bool BoolArgument;
    }

    /// <summary>
    /// Everything one component was set to, in OneText's own vocabulary and in
    /// primitives.
    ///
    /// This is the whole reason the TMP half of this module can be a thin,
    /// gated adapter: the adapter reads TextMesh Pro and fills one of these,
    /// and the engine that destroys components and writes serialized fields
    /// never sees a TMP type. Every mapping decision has already happened by
    /// the time a value lands here, which is also why the mapping is testable
    /// without TMP installed.
    /// </summary>
    public struct MigrationValues
    {
        public string Text;
        public bool RichText;

        public float FontSize;
        public bool AutoSize;
        public float AutoSizeMin, AutoSizeMax;

        public TextAlignment Alignment;
        public VerticalAlignment VerticalAlignment;
        public TextWrap Wrap;
        public TextOverflow Overflow;

        /// <summary>A multiplier, the way OneText means it — never TMP's offset.</summary>
        public float LineSpacing;

        public Color Color;
        public bool RaycastTarget;

        /// <summary>
        /// Asset path of the <c>.ttf</c>/<c>.otf</c> behind the source font, or
        /// null when there is none to be had. Not a font asset: the engine owns
        /// creating those, once per source file, however many labels share it.
        /// </summary>
        public string FontSourcePath;

        public List<string> FallbackFontSourcePaths;

        // ------------------------------------------------------- input fields

        public bool Multiline;
        public bool ReadOnly;
        public int CharacterLimit;
        public Color CaretColor;
        public float CaretWidth;
        public float CaretBlinkRate;
        public bool Interactable;
        public List<MigrationPersistentCall> ValueChangedCalls;
        public List<MigrationPersistentCall> SubmitCalls;

        /// <summary>
        /// Instance ids of the components the old field pointed at, captured
        /// before anything is destroyed.
        ///
        /// Ids rather than references because by the time the new input field
        /// needs them, the labels they name have themselves been replaced, and
        /// the id is the only thing that survives long enough to be looked up
        /// in the map of what became what.
        /// </summary>
        public int TextComponentId;
        public int PlaceholderId;
        public int TargetGraphicId;

        /// <summary>The defaults a label starts life with, so a partial read is still sane.</summary>
        public static MigrationValues Default => new MigrationValues
        {
            Text = string.Empty,
            RichText = true,
            FontSize = 36f,
            AutoSizeMin = 10f,
            AutoSizeMax = 128f,
            Alignment = TextAlignment.Start,
            VerticalAlignment = VerticalAlignment.Middle,
            Wrap = TextWrap.Wrap,
            Overflow = TextOverflow.Overflow,
            LineSpacing = 1f,
            Color = UnityEngine.Color.white,
            RaycastTarget = true,
            CaretColor = UnityEngine.Color.white,
            CaretWidth = 2f,
            CaretBlinkRate = 1.7f,
            Interactable = true,
        };
    }

    /// <summary>One component the scan found, and what will become of it.</summary>
    public sealed class MigrationTarget
    {
        public MigrationKind Kind;

        /// <summary>The type as a person would name it: <c>TextMeshProUGUI</c>.</summary>
        public string ComponentType;

        /// <summary>Which provider claimed it: "TextMesh Pro", "Unity UI".</summary>
        public string Provider;

        /// <summary>
        /// The live component. Valid only while its container is open, which is
        /// why nothing outside one scan pass may hold on to it.
        /// </summary>
        public Component Source;

        /// <summary>The scene or prefab asset path.</summary>
        public string Container;

        /// <summary>Hierarchy path inside the container, roots included.</summary>
        public string Path;

        public MigrationValues Values;

        /// <summary>
        /// Objects this target names and will have to name again afterwards,
        /// noted while it is still able to name them.
        ///
        /// A dropdown's caption and item labels are the case. It is converted
        /// last, after those labels have been swapped, and by then its own
        /// fields read None — the components they named were destroyed. The
        /// objects survive; the references to the components do not.
        /// </summary>
        public readonly List<GameObject> Companions = new List<GameObject>();

        public readonly List<MigrationFinding> Findings = new List<MigrationFinding>();

        public bool Convertible => Kind == MigrationKind.Label ||
                                   Kind == MigrationKind.Mesh ||
                                   Kind == MigrationKind.InputField ||
                                   Kind == MigrationKind.Dropdown;

        public MigrationFinding Note(DoctorSeverity severity, string rule, string message,
            string sample = null)
        {
            var finding = new MigrationFinding
            {
                Severity = severity,
                Rule = rule,
                Message = message,
                Container = Container,
                Path = Path,
                Component = ComponentType,
                Sample = sample,
            };
            Findings.Add(finding);
            return finding;
        }
    }

    /// <summary>
    /// What one pass over the project found, and — after Apply — what it did.
    ///
    /// Same contract as <see cref="DoctorReport"/>: warnings are advice,
    /// errors are things that will not work, and <see cref="Passed"/> is what
    /// a build should key off.
    /// </summary>
    public sealed class MigrationReport
    {
        public readonly List<MigrationFinding> Findings = new List<MigrationFinding>();

        /// <summary>Every convertible or report-only component found, in scan order.</summary>
        public readonly List<MigrationTarget> Targets = new List<MigrationTarget>();

        /// <summary>Scenes and prefabs opened.</summary>
        public int ContainersScanned;

        /// <summary>Containers that held at least one target.</summary>
        public readonly List<string> Containers = new List<string>();

        /// <summary>Components actually swapped, by kind. Zero after a scan.</summary>
        public int Converted;

        /// <summary>Font assets created from source font files during Apply.</summary>
        public int FontsCreated;

        /// <summary>
        /// Placeholder font assets created during Apply for fonts whose source
        /// file is missing — one per source font, not per label.
        /// </summary>
        public int PlaceholdersCreated;

        /// <summary>
        /// The fonts this project needs files for, deduplicated by source font.
        ///
        /// Filled by <see cref="FontRecovery.Collect"/>, which reads it out of
        /// the findings below rather than out of anything the conversion did, so
        /// a scan produces the same list as an apply. Empty on a project whose
        /// fonts all converted, which is the project this list wishes it were
        /// describing.
        /// </summary>
        public readonly FontRecoveryManifest Recovery = new FontRecoveryManifest();

        /// <summary>References to a converted component that were pointed at the new one.</summary>
        public int Relinked;

        public int Count(DoctorSeverity severity)
        {
            int n = 0;
            foreach (var finding in Findings) if (finding.Severity == severity) n++;
            return n;
        }

        public int CountOfKind(MigrationKind kind)
        {
            int n = 0;
            foreach (var target in Targets) if (target.Kind == kind) n++;
            return n;
        }

        public int Errors => Count(DoctorSeverity.Error);
        public int Warnings => Count(DoctorSeverity.Warning);

        public bool Passed => Errors == 0;

        public void Add(MigrationFinding finding) => Findings.Add(finding);

        /// <summary>Adds a target and folds its own findings into the report.</summary>
        public void Add(MigrationTarget target)
        {
            Targets.Add(target);
            if (!Containers.Contains(target.Container)) Containers.Add(target.Container);
            foreach (var finding in target.Findings) Findings.Add(finding);
        }

        public string Summary() =>
            $"{Targets.Count:n0} component(s) in {Containers.Count:n0} of {ContainersScanned:n0} " +
            $"container(s): {Errors} error(s), {Warnings} warning(s), " +
            $"{Count(DoctorSeverity.Info)} note(s)";
    }
}
