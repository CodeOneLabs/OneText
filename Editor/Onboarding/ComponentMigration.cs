using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
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
    ///
    /// One container at a time is not the whole job, though, and
    /// <see cref="ContainerReferences"/> is the part that is not: a field in one
    /// prefab that names a component in another is broken by converting the
    /// second and can only be mended while the first is open. That pass is
    /// arranged around this one and runs from inside it.
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

            /// <summary>
            /// Whether to follow the references that leave one container and
            /// land in another — see <see cref="ContainerReferences"/>.
            ///
            /// On by default, and the default is the recommendation: off is a
            /// migration that silently blanks a field in a prefab it never
            /// reported on. It is a flag at all because it is the one part of
            /// this that is not free. A scan pays nothing for it, because the
            /// containers are open anyway and nothing has been destroyed yet; an
            /// apply pays one extra open of every asset that depends on a
            /// container this run will touch, because the only moment those
            /// references still exist is before the first save. On a project
            /// whose prefabs nest nothing that is no assets at all, and on one
            /// where they nest heavily it is most of them.
            /// </summary>
            public bool CrossContainerReferences = true;

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

            var prefabs = options.IncludePrefabs ? OrderedPrefabPaths() : new List<string>();
            var scenes = options.IncludeScenes ? ScenePaths(options.AllScenes) : new List<string>();

            // Every scene this touches is opened in Single mode, so anything
            // unsaved in the editor right now is about to be closed. Asking is
            // the whole of the protection; batch mode has nobody to ask and
            // nothing to lose, and a run that opens no scene at all has nothing
            // to protect against.
            if (scenes.Count > 0 && !Application.isBatchMode &&
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

            var references = Watch(options, convert, prefabs, scenes, report);

            var fonts = new FontAssetCache(report);
            if (convert && options.AdoptProjectFontDefaults) AdoptDefaults(fonts, report);

            // One bar over both loops, because to the person waiting this is one
            // job. A migration of a real project takes minutes with the editor
            // frozen for all of them, and an editor that is frozen and silent is
            // one a first-time user force-quits — halfway through, which is the
            // one state this module works hardest to avoid.
            using (var progress = new Progress(convert ? "Converting text components"
                                                       : "Scanning text components",
                       Wanted(prefabs, options) + Wanted(scenes, options)))
            try
            {
                RunPrefabs(prefabs, options, convert, report, fonts, references, progress);
                if (!progress.Cancelled)
                    RunScenes(scenes, options, convert, report, fonts, references, progress);
                if (progress.Cancelled)
                    report.Add(new MigrationFinding
                    {
                        Severity = DoctorSeverity.Warning,
                        Rule = "cancelled",
                        Message = "you stopped this part way through. Everything already " +
                                  "converted is converted and saved; nothing was left half " +
                                  "written. Run it again to finish — it picks up where the " +
                                  "project is, not where it left off.",
                    });

                if (references != null && !convert) references.Report(report);
                else if (references != null)
                {
                    // ScriptableObjects last, because they are the one referrer
                    // that is never opened for its own sake, and by now every
                    // component they could name has a replacement.
                    references.RelinkAssets(report);
                    references.Settle(report);
                    Narrowed(options, references, report);
                }
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

        private static int Wanted(List<string> paths, Options options)
        {
            int count = 0;
            foreach (string path in paths) if (options.Wants(path)) count++;
            return count;
        }

        /// <summary>
        /// The bar, and the cancel button on it.
        ///
        /// Cancelling is offered because the alternative is not "it finishes" —
        /// it is the user killing the editor, which they will, because a window
        /// that has not moved in four minutes looks broken whatever it is doing.
        /// The check happens between containers, where a prefab is either fully
        /// converted and written or not opened at all, so stopping there leaves
        /// the project in a state the next run can simply carry on from.
        ///
        /// Silent in batch mode: there is nobody to show it to, it cannot be
        /// cancelled, and drawing it per container costs a project-sized number
        /// of no-ops in CI.
        /// </summary>
        private sealed class Progress : IDisposable
        {
            private readonly string _title;
            private readonly int _total;
            private readonly bool _shown;
            private int _done;

            public bool Cancelled { get; private set; }

            public Progress(string title, int total)
            {
                _title = title;
                _total = Mathf.Max(1, total);
                _shown = !Application.isBatchMode && total > 0;
            }

            /// <summary>
            /// Announces the container about to be opened. Returns false when the
            /// user has asked to stop, which every loop treats as "do no more".
            /// </summary>
            public bool Step(string container)
            {
                _done++;
                if (!_shown || Cancelled) return !Cancelled;

                // The path rather than a count, because the useful question
                // while waiting is "is it stuck?", and a name that keeps
                // changing answers it.
                if (EditorUtility.DisplayCancelableProgressBar(_title,
                        $"{_done}/{_total}  {container}", (float)_done / _total))
                    Cancelled = true;
                return !Cancelled;
            }

            public void Dispose()
            {
                if (_shown) EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Sets up the cross-container reference pass, and — for an apply — takes
        /// its census before anything is destroyed.
        ///
        /// The census has to happen here, in front of everything, because it is
        /// the only moment those references still exist. A scan needs no census:
        /// it destroys nothing, so every container it opens on its ordinary walk
        /// still holds whole references, and <see cref="ContainerReferences.Record"/>
        /// is called there instead for nothing.
        /// </summary>
        private static ContainerReferences Watch(Options options, bool convert,
            List<string> prefabs, List<string> scenes, MigrationReport report)
        {
            if (!options.CrossContainerReferences) return null;

            var scope = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in prefabs) if (options.Wants(path)) scope.Add(path);
            foreach (string path in scenes) if (options.Wants(path)) scope.Add(path);
            if (scope.Count == 0) return null;

            var references = new ContainerReferences(ContainerReferences.Candidates(scope));
            if (convert) references.Census(report);
            return references;
        }

        /// <summary>
        /// Says, once, that a run over part of a project can only mend the
        /// references held by the part it was given.
        ///
        /// The census reads the assets in the run. A prefab left out of it, or a
        /// scene outside the build when only the build's scenes were opened, is
        /// never opened and so is never even asked whether it points at anything
        /// that changed — and if it does, that field is reading None from now on
        /// with nothing anywhere saying so. This is the one place that can say
        /// it, because this is the only place that knows the run was narrow.
        /// </summary>
        private static void Narrowed(Options options, ContainerReferences references,
            MigrationReport report)
        {
            if (references == null || references.Changes == 0) return;

            var left = new List<string>();
            if (!options.IncludePrefabs) left.Add("prefabs were excluded");
            if (!options.IncludeScenes) left.Add("scenes were excluded");
            else if (!options.AllScenes) left.Add("only the build's scenes were opened");
            if (options.OnlyContainers != null && options.OnlyContainers.Count > 0)
                left.Add($"only {options.OnlyContainers.Count:n0} container(s) were selected");
            if (left.Count == 0) return;

            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Warning,
                Rule = ContainerReferences.Rule,
                Message = $"{references.Changes:n0} container(s) were converted, and " +
                          string.Join(", ", left) + ". A field in a prefab, scene or asset that " +
                          "was not part of this run and pointed at a component in one that was is " +
                          "reading None now, and nothing here has looked at it. Convert the whole " +
                          "project — All Scenes included — and every one of those is found and " +
                          "re-pointed.",
            });
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
        private static void RunPrefabs(List<string> paths, Options options, bool convert,
            MigrationReport report, FontAssetCache fonts, ContainerReferences references,
            Progress progress)
        {
            foreach (string path in paths)
            {
                if (!options.Wants(path)) continue;
                if (!progress.Step(path)) return;
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
                    var roots = new[] { root };
                    var broken = MissingScripts(roots);
                    if (convert && broken.Count > 0)
                    {
                        report.Add(new MigrationFinding
                        {
                            Severity = DoctorSeverity.Error,
                            Rule = "unsaveable-container",
                            Message = "this prefab holds a script Unity cannot resolve, and Unity " +
                                      "refuses to save a prefab in that state — so nothing here " +
                                      "was converted, because a conversion that cannot be written " +
                                      "is worse than none. Remove or restore the missing script " +
                                      $"on {string.Join(", ", broken.GetRange(0, Math.Min(3, broken.Count)))}" +
                                      (broken.Count > 3 ? $" and {broken.Count - 3} more" : string.Empty) +
                                      ", then convert again.",
                            Container = path,
                        });
                    }

                    int made = Process(roots, path, report, convert, fonts,
                        undo: false, saveable: broken.Count == 0);

                    // A prefab with nothing of its own to convert can still be a
                    // prefab holding a field that pointed into one that did, so
                    // this is asked whether or not anything was swapped here —
                    // and not at all when the file could not be written, because
                    // then nothing under it changed either.
                    int relinked = 0;
                    if (references != null)
                    {
                        if (!convert) references.Record(roots, path);
                        else if (broken.Count == 0) relinked = references.Relink(roots, path, report);
                        else references.Refused(roots, path);
                    }

                    if (convert && (made > 0 || relinked > 0))
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
                        if (!saved) Unsaved(report, path, made, relinked, references);
                        else if (made > 0) references?.Changed(path);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void RunScenes(List<string> paths, Options options, bool convert,
            MigrationReport report, FontAssetCache fonts, ContainerReferences references,
            Progress progress)
        {
            if (paths.Count == 0) return;

            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string path in paths)
                {
                    if (!options.Wants(path)) continue;
                    // Stopping here still restores the scene setup below, which
                    // is the whole reason the check is inside the try.
                    if (!progress.Step(path)) return;
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

                    var roots = scene.GetRootGameObjects();
                    var broken = MissingScripts(roots);
                    if (convert && broken.Count > 0)
                    {
                        report.Add(new MigrationFinding
                        {
                            Severity = DoctorSeverity.Error,
                            Rule = "unsaveable-container",
                            Message = "this scene holds a script Unity cannot resolve. Saving a " +
                                      "scene in that state drops the broken component rather than " +
                                      "preserving it, so nothing here was converted. Remove or " +
                                      "restore the missing script, then convert again.",
                            Container = path,
                        });
                    }

                    int made = Process(roots, path, report, convert,
                        fonts, undo: convert, saveable: broken.Count == 0);

                    // Scenes are converted last, so a field here that pointed
                    // into a prefab converted much earlier in the run has been
                    // reading None ever since, and this is the only visit it
                    // gets in which to be given the replacement.
                    int relinked = 0;
                    if (references != null)
                    {
                        if (!convert) references.Record(roots, path);
                        else if (broken.Count == 0) relinked = references.Relink(roots, path, report);
                        else references.Refused(roots, path);
                    }
                    if (relinked > 0) EditorSceneManager.MarkSceneDirty(scene);

                    if (convert && (made > 0 || relinked > 0) && !EditorSceneManager.SaveScene(scene))
                        Unsaved(report, path, made, relinked, references);
                    else if (convert && made > 0) references?.Changed(path);
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

        /// <summary>Returns how many components were swapped, so a caller whose save fails can take them back.</summary>
        private static int Process(GameObject[] roots, string container, MigrationReport report,
            bool convert, FontAssetCache fonts, bool undo, bool saveable = true)
        {
            var targets = Collect(roots, container);
            if (targets.Count == 0) return 0;

            // A container that cannot be written is reported and left exactly as
            // it is. Swapping its components first would be the same mistake the
            // script rewriter makes when it starts a file it cannot finish: the
            // work is real, the report says so, and the disk disagrees.
            if (convert && !saveable)
            {
                foreach (var target in targets) report.Add(target);
                return 0;
            }

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
            if (!convert) return 0;

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
            return made.Count;
        }

        /// <summary>
        /// What order the components on one container are converted in.
        ///
        /// A dropdown goes last of all because the swap has to hand its new
        /// component the labels it points at, and those labels are only
        /// <c>OneTextLabel</c> once they have been converted themselves.
        /// </summary>
        private static int Rank(MigrationKind kind) =>
            kind == MigrationKind.Dropdown ? 2 : kind == MigrationKind.InputField ? 1 : 0;

        // ------------------------------------------------------------ collect

        /// <summary>
        /// A container whose components were swapped and whose file then refused
        /// to be written.
        ///
        /// The count has to come back out of the report. Unity's save failures
        /// arrive as console errors rather than exceptions, so without this the
        /// run ends with a large, encouraging number that includes components
        /// still sitting on disk exactly as they were — and a second run finds
        /// them, swaps them, fails to save them, and reports them converted
        /// again, for as long as anybody is willing to press the button.
        /// </summary>
        private static void Unsaved(MigrationReport report, string container, int made,
            int relinked, ContainerReferences references)
        {
            report.Converted -= made;
            report.Relinked -= relinked;
            references?.Unwind(container);

            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Error,
                Rule = "save-failed",
                Message = $"{made:n0} component(s) here were swapped" +
                          (relinked > 0
                              ? $" and {relinked:n0} reference(s) into other containers re-pointed"
                              : string.Empty) +
                          ", and then Unity refused to write the file, so this container is " +
                          "unchanged on disk. The console holds Unity's reason; a missing script " +
                          "somewhere in it is much the commonest one.",
                Container = container,
            });
        }

        /// <summary>
        /// The GameObjects in this hierarchy carrying a script Unity can no
        /// longer resolve.
        ///
        /// This matters for one blunt reason: Unity refuses to save a prefab
        /// that holds a missing script, and refuses it after the components in
        /// memory have already been swapped. Converting such a prefab and then
        /// asking for it back produces an error in the console, an unchanged
        /// file on disk, and — if nobody is checking — a report that counts the
        /// swap as done. Asset packs are full of these, left behind when a
        /// dependency was removed, so this is not a rare shape.
        /// </summary>
        private static List<string> MissingScripts(GameObject[] roots)
        {
            var broken = new List<string>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        if (component != null) continue;
                        broken.Add(PathOf(transform));
                        break;
                    }
                }
            }
            return broken;
        }

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

            // A dropdown is the one thing here whose values cannot be captured
            // in advance: options, the event's persistent calls and the whole of
            // Selectable's state are serialized structures, not the handful of
            // scalars a label carries, and a SerializedObject dies with the
            // object it reads. So this one is built before the old one goes.
            if (target.Kind == MigrationKind.Dropdown) return ReplaceDropdown(target, go, undo);

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

        /// <summary>
        /// The dropdown swap: the new component alongside the old, every shared
        /// field copied across by path, the two labels re-pointed, and only then
        /// the old one destroyed.
        ///
        /// The fields are copied rather than read and re-set because two of them
        /// cannot be read and re-set. <c>m_Options</c> is a list of a serializable
        /// class and <c>m_OnValueChanged</c> is a UnityEvent whose persistent
        /// calls are what a designer wired in the inspector; losing either would
        /// leave a dropdown that converts cleanly, reports nothing, and is empty
        /// or dead when the game runs. <c>SerializedObject.CopyFromSerializedProperty</c>
        /// moves them whole, which is the reason this component's fields are
        /// named exactly as Unity's are.
        /// </summary>
        private static Component ReplaceDropdown(MigrationTarget target, GameObject go, bool undo)
        {
            var old = (Dropdown)target.Source;

            // Read off the target rather than off the component: by now the
            // labels have been converted and the old dropdown's own fields, which
            // named the components that were destroyed to do it, read None. The
            // scan wrote the objects down while it still could.
            var captionObject = target.Companions.Count > 0 ? target.Companions[0] : null;
            var itemObject = target.Companions.Count > 1 ? target.Companions[1] : null;

            // Both cannot sit on one object — a Selectable is exclusive — so the
            // values go through a component on a scratch object: copied off the
            // old one while it is still there, then onto the new one once it is
            // not. Two copies rather than one, and no moment where the values
            // exist nowhere.
            // The carrier needs the RectTransform OneTextDropdown asks for before
            // it can hold one at all: a GameObject made with a plain Transform
            // takes the component and hands back null.
            var scratch = new GameObject("OneText dropdown carrier", typeof(RectTransform))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            OneTextDropdown made;
            try
            {
                var carrier = scratch.AddComponent<OneTextDropdown>();
                if (carrier == null)
                    throw new InvalidOperationException("the carrier would not take a OneTextDropdown");
                Carry(new SerializedObject(old), new SerializedObject(carrier));

                if (undo) Undo.DestroyObjectImmediate(old);
                else UnityEngine.Object.DestroyImmediate(old);

                EnsureRect(go, undo);
                made = Add<OneTextDropdown>(go, undo);
                if (made == null)
                {
                    var present = new List<string>();
                    foreach (var each in go.GetComponents<Component>())
                        present.Add(each == null ? "(missing script)" : each.GetType().Name);
                    throw new InvalidOperationException(
                        "the object would not take a OneTextDropdown after the old one was " +
                        "removed. What is on it: " + string.Join(", ", present));
                }
                Carry(new SerializedObject(carrier), new SerializedObject(made));
            }
            finally { UnityEngine.Object.DestroyImmediate(scratch); }

            var to = new SerializedObject(made);
            SetObject(to, "m_CaptionText", Label(captionObject));
            SetObject(to, "m_ItemText", Label(itemObject));
            if (undo) to.ApplyModifiedProperties();
            else to.ApplyModifiedPropertiesWithoutUndo();

            Reenable(made);
            return made;
        }

        /// <summary>Every field the two dropdowns share, moved whole.</summary>
        private static void Carry(SerializedObject from, SerializedObject to)
        {
            foreach (string path in new[]
            {
                // Selectable's own state, which a dropdown is as much as it is a
                // dropdown: lose it and the thing stops highlighting, stops
                // navigating and may stop taking clicks at all.
                "m_Navigation", "m_Transition", "m_Colors", "m_SpriteState",
                "m_AnimationTriggers", "m_Interactable", "m_TargetGraphic",
                // And the dropdown's own.
                "m_Template", "m_CaptionImage", "m_ItemImage", "m_Value",
                "m_Options", "m_OnValueChanged", "m_AlphaFadeSpeed",
            })
            {
                var property = from.FindProperty(path);
                if (property != null) to.CopyFromSerializedProperty(property);
            }
            to.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>The OneText label on an object the old dropdown named, if it has one yet.</summary>
        private static OneTextLabel Label(GameObject go) =>
            go == null ? null : go.GetComponent<OneTextLabel>();

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
        ///
        /// The same rule applies to the fonts there is no file for. Those get a
        /// placeholder — see <see cref="FontRecovery"/> — one per source font,
        /// however many labels and however many baked font assets asked for it.
        /// </summary>
        private sealed class FontAssetCache
        {
            private readonly Dictionary<string, OneFontAsset> _byPath =
                new Dictionary<string, OneFontAsset>();

            /// <summary>Placeholders, by the name of the font asset that lacked a file.</summary>
            private readonly Dictionary<string, OneFontAsset> _recovered =
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
                if (!_enabled) return null;
                if (string.IsNullOrEmpty(sourcePath)) return Recovered(target);
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
            /// The font for a label whose font asset had no file behind it.
            ///
            /// This used to be a null, and a null is a correct answer that ends
            /// the conversation: the label loses its font, the reference graph
            /// loses an edge, and the only record of which font it wanted is a
            /// line in a report. On a project whose font packs shipped atlases
            /// and no <c>.ttf</c> that is most of the labels in it.
            ///
            /// So the answer is a placeholder instead — a real OneFontAsset
            /// carrying everything recovered from the old font asset except the
            /// bytes. Every label that used that font points at the same one, so
            /// finding the file once fixes all of them, and until somebody does
            /// the placeholder reports itself as unfilled and draws in the
            /// project default, which is exactly what the null did.
            ///
            /// Which font it was comes from the finding the provider already
            /// raised: providers are gated adapters that report in prose, and
            /// the first <c>font-source-missing</c> on a target is its primary
            /// font, because that is the order they read a component in.
            /// </summary>
            private OneFontAsset Recovered(MigrationTarget target)
            {
                if (target == null) return null;

                string name = null;
                foreach (var finding in target.Findings)
                {
                    if (finding.Rule != FontRecovery.Rule) continue;
                    name = FontRecovery.NamedFont(finding.Message);
                    if (name != null) break;
                }
                if (name == null) return null;

                if (_recovered.TryGetValue(name, out var known)) return known;

                var placeholder = FontRecovery.PlaceholderFor(_report, name);
                _recovered[name] = placeholder;
                if (placeholder != null) NoteRecovered(name, placeholder, target);
                return placeholder;
            }

            /// <summary>
            /// Said once per font, not once per label, and as a note rather than
            /// an error: the error is still there, above this, saying the font
            /// asset had nothing to convert. This says what was done about it.
            /// </summary>
            private void NoteRecovered(string fontAssetName, OneFontAsset placeholder,
                MigrationTarget target)
            {
                string expected = placeholder.Recovery.ExpectedFileName;
                _report.Add(target.Note(DoctorSeverity.Info, FontRecovery.RecoveredRule,
                    $"'{fontAssetName}' has no font file, so the migration made a placeholder " +
                    $"font at {AssetDatabase.GetAssetPath(placeholder)} and pointed this label — " +
                    "and every other label that used it — at that instead of at nothing. Drop " +
                    $"{(string.IsNullOrEmpty(expected) ? "the source font file" : expected)} " +
                    "into the project and all of them have their font back; until then they draw " +
                    "in the project default."));
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
