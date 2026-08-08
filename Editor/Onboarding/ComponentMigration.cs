using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// The scenes and prefabs of a project, read for text components, reported
    /// on, and — when asked twice — converted in place.
    ///
    /// The script rewriter next door handles the half of leaving TextMesh Pro
    /// that is text; this handles the half that is data, and the two halves fail
    /// differently. A script that still says <c>TextMeshProUGUI</c> is a compile
    /// error and the compiler will not let anyone forget it. A scene that still
    /// holds one is a scene that opens, plays, and looks exactly right, until
    /// the day the package is removed and every label in it becomes a missing
    /// script with no name attached. Nothing in Unity will tell you how many of
    /// those you have. This does, before you decide.
    ///
    /// Two passes, deliberately. <see cref="Scan"/> opens everything, writes
    /// nothing, and produces a report you can read; <see cref="Apply"/> does the
    /// whole thing again from scratch and only then destroys anything. Apply
    /// never trusts the scan's findings, because the scan's component
    /// references stopped being valid the moment its scene closed, and a
    /// migration that acted on a stale reference would act on the wrong object.
    /// </summary>
    public static class ComponentMigration
    {
        public sealed class Options
        {
            /// <summary>Every scene under Assets rather than only the build's.</summary>
            public bool AllScenes;

            public bool IncludeScenes = true;

            public bool IncludePrefabs = true;

            /// <summary>When set, only these container paths are touched.</summary>
            public List<string> OnlyContainers;

            /// <summary>Whether Apply may set the project's default font from TMP's.</summary>
            public bool AdoptProjectFontDefaults;

            public bool Wants(string container) =>
                OnlyContainers == null || OnlyContainers.Count == 0 ||
                OnlyContainers.Contains(container);
        }

        // ------------------------------------------------------------ entries

        /// <summary>Reads the project and reports. Writes nothing, dirties nothing.</summary>
        public static MigrationReport Scan(Options options = null) =>
            Run(options ?? new Options(), convert: false);

        /// <summary>
        /// Does the scan again and then performs it: font assets, prefabs in
        /// dependency order, then scenes.
        /// </summary>
        public static MigrationReport Apply(Options options = null) =>
            Run(options ?? new Options(), convert: true);

        /// <summary>
        /// Reports on a hierarchy that is already open, without going near the
        /// asset database. What a test and the Hub's own preview both want, and
        /// the only entry point that does not decide for itself which scenes to
        /// open.
        /// </summary>
        public static MigrationReport ScanInPlace(GameObject[] roots,
            string container = "(in memory)")
        {
            var report = new MigrationReport { ContainersScanned = 1 };
            Process(roots, container, report, convert: false, new FontAssetCache(report, true),
                undo: false);
            return report;
        }

        /// <summary>
        /// Converts a hierarchy that is already open. <paramref name="createFontAssets"/>
        /// is the one thing a test wants off: making a OneText font asset means
        /// reading a whole font file and writing an asset beside it, which is a
        /// real effect on a real project and not what an assertion about
        /// alignment is asking for.
        /// </summary>
        public static MigrationReport ConvertInPlace(GameObject[] roots,
            string container = "(in memory)", bool createFontAssets = true)
        {
            var report = new MigrationReport { ContainersScanned = 1 };
            Process(roots, container, report, convert: true,
                new FontAssetCache(report, createFontAssets), undo: false);
            return report;
        }

        // ------------------------------------------------------------ the run

        private static MigrationReport Run(Options options, bool convert)
        {
            var report = new MigrationReport();

            // Every container is opened in Single mode, so anything unsaved in
            // the editor right now is about to be closed. Asking is the whole
            // of the protection; batch mode has nobody to ask and nothing to
            // lose.
            if (!Application.isBatchMode &&
                !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = "cancelled",
                    Message = "the open scenes hold unsaved changes and were not saved, so " +
                              "nothing was scanned.",
                });
                return report;
            }

            var fonts = new FontAssetCache(report);
            if (convert && options.AdoptProjectFontDefaults) AdoptDefaults(fonts, report);

            try
            {
                if (options.IncludePrefabs) RunPrefabs(options, convert, report, fonts);
                if (options.IncludeScenes) RunScenes(options, convert, report, fonts);
            }
            finally
            {
                if (convert)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
            }
            return report;
        }

        /// <summary>
        /// Prefab assets, base before variant and nested before host.
        ///
        /// The ordering is the difference between a migration and a mess. A
        /// prefab variant loaded for editing shows its base's components as its
        /// own; convert the variant first and the swap is recorded as an
        /// override on top of a base that still holds the old component, so when
        /// the base is converted too the object ends up with both. Converting
        /// the base first means the variant, when it opens, already holds a
        /// OneText label and there is simply nothing there to convert — the
        /// same reason the whole module has to be idempotent.
        /// </summary>
        private static void RunPrefabs(Options options, bool convert, MigrationReport report,
            FontAssetCache fonts)
        {
            foreach (string path in OrderedPrefabPaths())
            {
                if (!options.Wants(path)) continue;
                report.ContainersScanned++;

                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                }
                catch (Exception error)
                {
                    report.Add(new MigrationFinding
                    {
                        Severity = DoctorSeverity.Warning,
                        Rule = "unreadable-container",
                        Message = $"this prefab could not be opened: {error.Message}",
                        Container = path,
                    });
                    continue;
                }

                try
                {
                    bool changed = Process(new[] { root }, path, report, convert, fonts, undo: false);
                    if (convert && changed) PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void RunScenes(Options options, bool convert, MigrationReport report,
            FontAssetCache fonts)
        {
            var paths = ScenePaths(options.AllScenes);
            if (paths.Count == 0) return;

            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string path in paths)
                {
                    if (!options.Wants(path)) continue;
                    report.ContainersScanned++;

                    var scene = default(UnityEngine.SceneManagement.Scene);
                    try
                    {
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    }
                    catch (Exception error)
                    {
                        report.Add(new MigrationFinding
                        {
                            Severity = DoctorSeverity.Warning,
                            Rule = "unreadable-container",
                            Message = $"this scene could not be opened: {error.Message}",
                            Container = path,
                        });
                        continue;
                    }

                    bool changed = Process(scene.GetRootGameObjects(), path, report, convert,
                        fonts, undo: convert);
                    if (convert && changed) EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                // An untitled scene has no setup to restore, and asking for one
                // back throws rather than shrugging.
                if (setup != null && setup.Length > 0)
                {
                    try { EditorSceneManager.RestoreSceneManagerSetup(setup); }
                    catch (Exception) { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene); }
                }
            }
        }

        // ---------------------------------------------------------- containers

        /// <summary>
        /// The scenes to look at: the build's, which is what ships, or every
        /// one under Assets, which is what a project actually has.
        /// </summary>
        public static List<string> ScenePaths(bool all)
        {
            var paths = new List<string>();
            if (all)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!paths.Contains(path)) paths.Add(path);
                }
                paths.Sort(StringComparer.Ordinal);
                return paths;
            }

            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene == null || string.IsNullOrEmpty(scene.path)) continue;
                if (!paths.Contains(scene.path)) paths.Add(scene.path);
            }
            return paths;
        }

        /// <summary>
        /// Every prefab under Assets, sorted so that nothing is converted before
        /// something it is built out of. Depth is the longest chain of prefab
        /// dependencies below a prefab, and equal depths keep path order so two
        /// runs produce the same report.
        /// </summary>
        public static List<string> OrderedPrefabPaths()
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path);
            }
            paths.Sort(StringComparer.Ordinal);

            var known = new HashSet<string>(paths);
            var depths = new Dictionary<string, int>();
            foreach (string path in paths) Depth(path, known, depths, new HashSet<string>());

            paths.Sort((a, b) =>
            {
                int byDepth = depths[a].CompareTo(depths[b]);
                return byDepth != 0 ? byDepth : string.CompareOrdinal(a, b);
            });
            return paths;
        }

        private static int Depth(string path, HashSet<string> known,
            Dictionary<string, int> depths, HashSet<string> visiting)
        {
            if (depths.TryGetValue(path, out int cached)) return cached;
            if (!visiting.Add(path)) return 0; // prefabs cannot really cycle; do not hang if they do

            int deepest = 0;
            foreach (string dependency in AssetDatabase.GetDependencies(path, false))
            {
                if (dependency == path || !known.Contains(dependency)) continue;
                deepest = Mathf.Max(deepest, Depth(dependency, known, depths, visiting) + 1);
            }

            visiting.Remove(path);
            depths[path] = deepest;
            return deepest;
        }

        // -------------------------------------------------------- one container

        private static bool Process(GameObject[] roots, string container, MigrationReport report,
            bool convert, FontAssetCache fonts, bool undo)
        {
            var targets = Collect(roots, container);
            if (targets.Count == 0) return false;

            var convertible = new List<MigrationTarget>();
            foreach (var target in targets) if (target.Convertible) convertible.Add(target);

            var references = convertible.Count == 0
                ? new List<Referrer>()
                : CollectReferences(roots, convertible);

            foreach (var reference in references)
            {
                reference.Target.Note(DoctorSeverity.Warning, "dangling-reference",
                    $"{reference.ReferrerType} at '{reference.ReferrerPath}' points at this " +
                    $"component through '{reference.PropertyPath}'. The migration will re-point it " +
                    "at the OneText component that replaces it; a field declared as a TextMesh Pro " +
                    "type cannot hold one, so run the script rewrite first if this is your code.");
            }

            foreach (var target in targets) report.Add(target);
            if (!convert) return false;

            // Labels and meshes first: an input field's text and placeholder
            // have to exist as OneText components before the field that names
            // them is built, or the field arrives pointing at nothing.
            convertible.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));

            var replaced = new Dictionary<int, Component>();
            var made = new List<(MigrationTarget Target, Component Component)>();

            foreach (var target in convertible)
            {
                int oldId = target.Source.GetInstanceID();
                try
                {
                    var component = Replace(target, fonts, undo);
                    if (component == null) continue;
                    replaced[oldId] = component;
                    made.Add((target, component));
                }
                catch (Exception error)
                {
                    var finding = target.Note(DoctorSeverity.Error, "convert-failed",
                        $"this component could not be replaced: {error.Message}");
                    report.Add(finding);
                }
            }

            // Second pass for the fields that name other components, now that
            // every one of them has an answer.
            foreach (var (target, component) in made)
            {
                if (target.Kind != MigrationKind.InputField) continue;
                WireInputField(component, target, replaced, report);
            }

            Relink(references, replaced, report);

            report.Converted += made.Count;
            return made.Count > 0;
        }

        private static int Rank(MigrationKind kind) => kind == MigrationKind.InputField ? 1 : 0;

        // ------------------------------------------------------------ collect

        private static List<MigrationTarget> Collect(GameObject[] roots, string container)
        {
            var targets = new List<MigrationTarget>();
            var providers = MigrationProviders.All;

            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue; // a missing script
                    string path = PathOf(component.transform);
                    foreach (var provider in providers)
                    {
                        MigrationTarget target;
                        try
                        {
                            target = provider.Inspect(component, container, path);
                        }
                        catch (Exception error)
                        {
                            Debug.LogWarning($"OneText migration: {provider.Name} could not read " +
                                             $"{component.GetType().Name} at {path}: {error.Message}");
                            continue;
                        }
                        if (target == null) continue;
                        targets.Add(target);
                        break;
                    }
                }
            }
            return targets;
        }

        private sealed class Referrer
        {
            public Component Component;
            public string ReferrerPath;
            public string ReferrerType;
            public string PropertyPath;
            public int OldId;
            public MigrationTarget Target;
        }

        /// <summary>
        /// Every serialized field anywhere in this container that points at a
        /// component about to be destroyed.
        ///
        /// Every component, not the likely ones: a reference to a label is
        /// exactly as likely to live on a bespoke <c>DialogueBox</c> as on a
        /// button, and the whole failure this prevents — a field that reads
        /// "None (Text Mesh Pro UGUI)" after the migration, in a scene nobody
        /// reopens until release — is invisible unless you look everywhere.
        /// Components that are themselves being destroyed are skipped: their
        /// fields have no future to protect.
        /// </summary>
        private static List<Referrer> CollectReferences(GameObject[] roots,
            List<MigrationTarget> targets)
        {
            var byId = new Dictionary<int, MigrationTarget>();
            foreach (var target in targets) byId[target.Source.GetInstanceID()] = target;

            var found = new List<Referrer>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    if (byId.ContainsKey(component.GetInstanceID())) continue;

                    SerializedObject serialized;
                    try { serialized = new SerializedObject(component); }
                    catch (Exception) { continue; }

                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        int id = iterator.objectReferenceInstanceIDValue;
                        if (id == 0 || !byId.TryGetValue(id, out var target)) continue;

                        found.Add(new Referrer
                        {
                            Component = component,
                            ReferrerPath = PathOf(component.transform),
                            ReferrerType = component.GetType().Name,
                            PropertyPath = iterator.propertyPath,
                            OldId = id,
                            Target = target,
                        });
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Points every recorded reference at the component that replaced the
        /// one it named, and reads it back.
        ///
        /// The readback is not paranoia: a field declared as <c>TMP_Text</c>
        /// silently refuses a <c>OneTextLabel</c>, and the refusal looks exactly
        /// like success from the writing side. That is the case this whole
        /// module exists downstream of the script rewriter for, and the finding
        /// says so by name.
        /// </summary>
        private static void Relink(List<Referrer> references, Dictionary<int, Component> replaced,
            MigrationReport report)
        {
            foreach (var reference in references)
            {
                if (reference.Component == null) continue;
                if (!replaced.TryGetValue(reference.OldId, out var made) || made == null) continue;

                var serialized = new SerializedObject(reference.Component);
                var property = serialized.FindProperty(reference.PropertyPath);
                if (property == null) continue;

                property.objectReferenceValue = made;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                serialized.Update();
                var written = serialized.FindProperty(reference.PropertyPath);
                if (written != null && written.objectReferenceValue == made)
                {
                    report.Relinked++;
                    continue;
                }

                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = "dangling-reference",
                    Message = $"'{reference.PropertyPath}' on {reference.ReferrerType} would not " +
                              $"take the {made.GetType().Name} that replaced its target: the field " +
                              "is still declared as a TextMesh Pro type. Run the script rewrite in " +
                              "this tab, let Unity recompile, and convert again.",
                    Container = reference.Target.Container,
                    Path = reference.ReferrerPath,
                    Component = reference.ReferrerType,
                });
            }
        }

        // ------------------------------------------------------------ replace

        /// <summary>
        /// The swap itself: the old component off the GameObject, the OneText
        /// one on, and every captured value written through
        /// <c>SerializedObject</c> because the fields that hold them are private.
        /// </summary>
        private static Component Replace(MigrationTarget target, FontAssetCache fonts, bool undo)
        {
            var go = target.Source.gameObject;

            if (undo) Undo.DestroyObjectImmediate(target.Source);
            else UnityEngine.Object.DestroyImmediate(target.Source);

            Component made;
            switch (target.Kind)
            {
                case MigrationKind.Label:
                    EnsureGraphicParts(go, undo);
                    made = Add<OneTextLabel>(go, undo);
                    break;
                case MigrationKind.InputField:
                    EnsureGraphicParts(go, undo);
                    made = Add<OneTextInputField>(go, undo);
                    break;
                case MigrationKind.Mesh:
                    EnsureRect(go, undo);
                    made = Add<OneTextMesh>(go, undo);
                    break;
                default:
                    return null;
            }

            ApplyValues(made, target, fonts, undo);
            return made;
        }

        private static T Add<T>(GameObject go, bool undo) where T : Component =>
            undo ? Undo.AddComponent<T>(go) : go.AddComponent<T>();

        /// <summary>
        /// A <c>Graphic</c> needs its RectTransform and CanvasRenderer up front:
        /// <c>AddComponent</c> does not honour a <c>RequireComponent</c> declared
        /// on a base class, which is a thing you find out once.
        /// </summary>
        private static void EnsureGraphicParts(GameObject go, bool undo)
        {
            EnsureRect(go, undo);
            if (go.GetComponent<CanvasRenderer>() == null) Add<CanvasRenderer>(go, undo);
        }

        private static void EnsureRect(GameObject go, bool undo)
        {
            if (go.GetComponent<RectTransform>() != null) return;
            // AddComponent<RectTransform> on an object with a plain Transform
            // replaces it rather than failing, which is the only way a legacy
            // TextMesh can become a laid-out box.
            if (undo) Undo.AddComponent<RectTransform>(go);
            else go.AddComponent<RectTransform>();
        }

        private static void ApplyValues(Component made, MigrationTarget target,
            FontAssetCache fonts, bool undo)
        {
            var values = target.Values;
            var serialized = new SerializedObject(made);

            var font = fonts.Get(values.FontSourcePath, target);
            SetObject(serialized, "_font", font);
            SetFontList(serialized, "_fallbackFonts", values.FallbackFontSourcePaths, fonts, target);

            SetString(serialized, "_text", values.Text);
            SetBool(serialized, "_richText", values.RichText);
            SetFloat(serialized, "_fontSize", values.FontSize);
            SetBool(serialized, "_autoSize", values.AutoSize);
            SetFloat(serialized, "_autoSizeMin", values.AutoSizeMin);
            SetFloat(serialized, "_autoSizeMax", values.AutoSizeMax);
            SetInt(serialized, "_alignment", (int)values.Alignment);
            SetInt(serialized, "_verticalAlignment", (int)values.VerticalAlignment);
            SetInt(serialized, "_wrap", (int)values.Wrap);
            SetInt(serialized, "_overflow", (int)values.Overflow);
            SetFloat(serialized, "_lineSpacing", values.LineSpacing);

            // The label's colour is the Graphic's; the mesh's is its own.
            SetColor(serialized, "m_Color", values.Color);
            SetColor(serialized, "_color", values.Color);
            SetBool(serialized, "m_RaycastTarget", values.RaycastTarget);

            if (target.Kind == MigrationKind.InputField)
            {
                SetString(serialized, "_text", values.Text);
                SetBool(serialized, "_multiline", values.Multiline);
                SetBool(serialized, "_readOnly", values.ReadOnly);
                SetInt(serialized, "_characterLimit", values.CharacterLimit);
                SetColor(serialized, "_caretColor", values.CaretColor);
                SetFloat(serialized, "_caretWidth", values.CaretWidth);
                SetFloat(serialized, "_caretBlinkRate", values.CaretBlinkRate);
                SetBool(serialized, "m_Interactable", values.Interactable);
            }

            if (undo) serialized.ApplyModifiedProperties();
            else serialized.ApplyModifiedPropertiesWithoutUndo();

            // The component was added, ran OnEnable against its defaults, and
            // only then had its fields written from outside. Anything that
            // copies a serialized field into runtime state on enable — the
            // input field's editing model does exactly that — is now holding
            // the default rather than the value. Loading the saved scene would
            // fix it; the person watching the conversion happen should not have
            // to reload to see it.
            Reenable(made);
        }

        /// <summary>
        /// Makes a component read its own serialized fields again.
        ///
        /// Everything here writes fields on a component that has already run
        /// <c>OnEnable</c>, so anything a component copies out of a serialized
        /// field on enable — the input field's editing model, its placeholder
        /// visibility — is still holding the default. Reopening the saved scene
        /// would fix it. The person watching the conversion happen should not
        /// have to.
        /// </summary>
        private static void Reenable(Component component)
        {
            if (component is Behaviour behaviour && behaviour.enabled &&
                behaviour.gameObject.activeInHierarchy)
            {
                behaviour.enabled = false;
                behaviour.enabled = true;
            }
        }

        /// <summary>
        /// The references and listeners only an input field has, wired after
        /// every label in the container has become a OneText one.
        /// </summary>
        private static void WireInputField(Component field, MigrationTarget target,
            Dictionary<int, Component> replaced, MigrationReport report)
        {
            var values = target.Values;
            var serialized = new SerializedObject(field);

            SetObject(serialized, "_textComponent", Resolve(values.TextComponentId, replaced));
            SetObject(serialized, "_placeholder", Resolve(values.PlaceholderId, replaced));
            if (values.TargetGraphicId != 0)
            {
                var graphic = EditorUtility.InstanceIDToObject(values.TargetGraphicId) as Component;
                if (graphic != null) SetObject(serialized, "m_TargetGraphic", graphic);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            serialized.Update();
            if (values.TextComponentId != 0 &&
                serialized.FindProperty("_textComponent")?.objectReferenceValue == null)
            {
                report.Add(target.Note(DoctorSeverity.Warning, "no-counterpart",
                    "the input field's text component did not survive the swap — it was not one " +
                    "of the components this migration converted. Assign a OneTextLabel to Text " +
                    "Component by hand."));
            }

            CarryListeners(serialized, "_onValueChanged", values.ValueChangedCalls, replaced,
                target, report);
            CarryListeners(serialized, "_onSubmit", values.SubmitCalls, replaced, target, report);

            // The labels arrived after the field did, so the field has never
            // looked at them. Pushing its visuals once now is not cosmetic: the
            // placeholder is hidden by disabling that label, and a component's
            // enabled flag is serialized, so doing it here is what makes the
            // saved scene open with the placeholder already out of the way
            // rather than printed over the field's own text.
            Reenable(field);
            (field as OneTextInputField)?.UpdateVisuals();
        }

        private static void CarryListeners(SerializedObject serialized, string path,
            List<MigrationPersistentCall> calls, Dictionary<int, Component> replaced,
            MigrationTarget target, MigrationReport report)
        {
            if (calls == null || calls.Count == 0) return;

            int landed = UnityEventTransfer.Write(serialized, path, calls, replaced);
            var finding = landed == calls.Count
                ? target.Note(DoctorSeverity.Info, "event-listeners",
                    $"{landed:n0} persistent listener(s) carried over to {path.TrimStart('_')}.")
                : target.Note(DoctorSeverity.Warning, "event-listeners",
                    $"{calls.Count - landed:n0} of {calls.Count:n0} persistent listener(s) on " +
                    $"{path.TrimStart('_')} did not survive: the method is not on the new type, or " +
                    "its target is gone. Re-wire them in the inspector.");
            report.Add(finding);
        }

        private static Component Resolve(int id, Dictionary<int, Component> replaced)
        {
            if (id == 0) return null;
            return replaced.TryGetValue(id, out var made) ? made : null;
        }

        // ------------------------------------------------------------- fonts

        /// <summary>
        /// One <see cref="OneFontAsset"/> per source font file, however many
        /// labels asked for it.
        ///
        /// A project with two hundred labels on one font has one font, and a
        /// migration that made two hundred copies of a four-megabyte asset would
        /// be worse than the thing it replaced. An asset that already exists
        /// beside the font file is reused untouched: re-importing it would put
        /// every font in the project in the diff for no reason.
        /// </summary>
        private sealed class FontAssetCache
        {
            private readonly Dictionary<string, OneFontAsset> _byPath =
                new Dictionary<string, OneFontAsset>();

            private readonly MigrationReport _report;
            private readonly bool _enabled;

            public FontAssetCache(MigrationReport report, bool enabled = true)
            {
                _report = report;
                _enabled = enabled;
            }

            public OneFontAsset Get(string sourcePath, MigrationTarget target)
            {
                if (!_enabled || string.IsNullOrEmpty(sourcePath)) return null;
                if (_byPath.TryGetValue(sourcePath, out var cached))
                {
                    if (cached == null) NoteMissing(sourcePath, target);
                    return cached;
                }

                string directory = System.IO.Path.GetDirectoryName(sourcePath) ?? "Assets";
                string baseName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
                string assetPath = $"{directory.Replace('\\', '/')}/{baseName} Font.asset";

                var asset = AssetDatabase.LoadAssetAtPath<OneFontAsset>(assetPath);
                if (asset == null)
                {
                    asset = OneFontAssetCreator.CreateFromFontFile(sourcePath);
                    if (asset != null) _report.FontsCreated++;
                }

                if (asset == null) NoteMissing(sourcePath, target);

                _byPath[sourcePath] = asset;
                return asset;
            }

            /// <summary>
            /// Said through the report as well as the target: by the time a
            /// font is being made, the target's own findings were already
            /// folded in, so a note added only there would never reach the
            /// report's error count — and this is an error.
            /// </summary>
            private void NoteMissing(string sourcePath, MigrationTarget target)
            {
                const string rule = "font-source-missing";
                string message = $"no OneText font asset could be made from {sourcePath}. The " +
                                 "label is left with no font, which means the project default.";
                _report.Add(target != null
                    ? target.Note(DoctorSeverity.Error, rule, message)
                    : new MigrationFinding
                    {
                        Severity = DoctorSeverity.Error,
                        Rule = rule,
                        Message = message,
                    });
            }
        }

        private static void SetFontList(SerializedObject serialized, string path,
            List<string> sourcePaths, FontAssetCache fonts, MigrationTarget target)
        {
            var property = serialized.FindProperty(path);
            if (property == null || !property.isArray) return;

            var assets = new List<OneFontAsset>();
            if (sourcePaths != null)
            {
                foreach (string source in sourcePaths)
                {
                    var asset = fonts.Get(source, target);
                    if (asset != null && !assets.Contains(asset)) assets.Add(asset);
                }
            }

            property.arraySize = assets.Count;
            for (int i = 0; i < assets.Count; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
        }

        /// <summary>
        /// TMP's project-wide default font and global fallbacks, offered to
        /// OneText's own settings. Only ever fills a blank: a project that has
        /// already chosen a OneText default has chosen it.
        /// </summary>
        private static void AdoptDefaults(FontAssetCache fonts, MigrationReport report)
        {
            foreach (var provider in MigrationProviders.All)
            {
                if (!provider.TryProjectFontDefaults(out string defaultPath, out var fallbacks))
                    continue;
                if (string.IsNullOrEmpty(defaultPath) && (fallbacks == null || fallbacks.Count == 0))
                    continue;

                var settings = OneTextSettingsProvider.GetOrCreate();
                if (settings == null) return;

                var serialized = new SerializedObject(settings);
                var current = serialized.FindProperty("_defaultFont");
                if (current != null && current.objectReferenceValue == null)
                {
                    var asset = fonts.Get(defaultPath, null);
                    if (asset != null)
                    {
                        current.objectReferenceValue = asset;
                        report.Add(new MigrationFinding
                        {
                            Severity = DoctorSeverity.Info,
                            Rule = "project-default",
                            Message = $"the project default font is now '{asset.name}', taken from " +
                                      $"{provider.Name}'s default font asset.",
                        });
                    }
                }

                var list = serialized.FindProperty("_fallbackFonts");
                if (list != null && list.isArray && list.arraySize == 0 && fallbacks != null)
                {
                    var assets = new List<OneFontAsset>();
                    foreach (string path in fallbacks)
                    {
                        var asset = fonts.Get(path, null);
                        if (asset != null && !assets.Contains(asset)) assets.Add(asset);
                    }
                    list.arraySize = assets.Count;
                    for (int i = 0; i < assets.Count; i++)
                        list.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
                    if (assets.Count > 0)
                    {
                        report.Add(new MigrationFinding
                        {
                            Severity = DoctorSeverity.Info,
                            Rule = "project-default",
                            Message = $"{assets.Count:n0} global fallback font(s) carried over from " +
                                      $"{provider.Name}.",
                        });
                    }
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
                OneTextSettings.Invalidate();
            }
        }

        // -------------------------------------------------------- small tools

        public static string PathOf(Transform transform)
        {
            if (transform == null) return string.Empty;
            var builder = new StringBuilder(transform.name);
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                builder.Insert(0, parent.name + "/");
            return builder.ToString();
        }

        private static void SetString(SerializedObject serialized, string path, string value)
        {
            var property = serialized.FindProperty(path);
            if (property != null) property.stringValue = value ?? string.Empty;
        }

        private static void SetBool(SerializedObject serialized, string path, bool value)
        {
            var property = serialized.FindProperty(path);
            if (property != null) property.boolValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string path, float value)
        {
            var property = serialized.FindProperty(path);
            if (property != null) property.floatValue = value;
        }

        private static void SetInt(SerializedObject serialized, string path, int value)
        {
            var property = serialized.FindProperty(path);
            if (property != null) property.intValue = value;
        }

        private static void SetColor(SerializedObject serialized, string path, Color value)
        {
            var property = serialized.FindProperty(path);
            if (property != null && property.propertyType == SerializedPropertyType.Color)
                property.colorValue = value;
        }

        private static void SetObject(SerializedObject serialized, string path,
            UnityEngine.Object value)
        {
            var property = serialized.FindProperty(path);
            if (property != null && property.propertyType == SerializedPropertyType.ObjectReference)
                property.objectReferenceValue = value;
        }

        // ---------------------------------------------------------------- CI

        /// <summary>
        /// Batch-mode entry point: scan and report, never convert. Exits 1 when
        /// anything in the project would not survive the migration, so a team
        /// mid-migration can put the question in a pipeline rather than in
        /// somebody's memory.
        ///
        /// Unity -batchmode -projectPath &lt;p&gt; -executeMethod
        ///     OneText.Editor.ComponentMigration.RunFromCommandLine [-oneAllScenes]
        /// </summary>
        public static void RunFromCommandLine()
        {
            var options = new Options
            {
                AllScenes = HasArg("-oneAllScenes"),
                IncludePrefabs = !HasArg("-oneNoPrefabs"),
            };

            MigrationReport report;
            try
            {
                report = Scan(options);
            }
            catch (Exception error)
            {
                Debug.LogError($"OneText migration: the scan itself failed: {error}");
                EditorApplication.Exit(2);
                return;
            }

            foreach (var finding in report.Findings)
            {
                string line = $"OneText migration {finding}";
                switch (finding.Severity)
                {
                    case DoctorSeverity.Error: Debug.LogError(line); break;
                    case DoctorSeverity.Warning: Debug.LogWarning(line); break;
                    default: Debug.Log(line); break;
                }
            }

            if (!MigrationProviders.HasTextMeshPro)
            {
                Debug.Log("OneText migration: TextMesh Pro is not in this project, so only " +
                          "UnityEngine.UI.Text and TextMesh were looked for.");
            }

            Debug.Log($"OneText migration: {report.Summary()}");
            EditorApplication.Exit(report.Passed ? 0 : 1);
        }

        private static bool HasArg(string name)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument == name) return true;
            return false;
        }
    }
}
