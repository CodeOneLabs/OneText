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
            options = Widened(options, report);

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

            var covered = Covered(options, prefabs, scenes);
            var references = Watch(options, convert, covered, prefabs, scenes, report);

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
                    // The rest of the project, before the assets: a field in a
                    // prefab nobody selected is broken by this run just as
                    // thoroughly as one in a prefab that was, and this is the
                    // pass that goes and gets it.
                    Mend(references, covered, report);

                    // ScriptableObjects last, because they are the one referrer
                    // that is never opened for its own sake, and by now every
                    // component they could name has a replacement.
                    references.RelinkAssets(report);
                    Narrowed(options, references, report, references.Settle(report));
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
            HashSet<string> covered, List<string> prefabs, List<string> scenes,
            MigrationReport report)
        {
            if (!options.CrossContainerReferences || covered.Count == 0) return null;

            // Only an apply pays for the candidate sweep. The list exists to be
            // censused, a scan censuses nothing — it reads what it opens anyway —
            // and asking the asset database about the dependencies of every
            // prefab in a project is not a thing to do for a report.
            if (!convert) return new ContainerReferences(covered);

            var references = new ContainerReferences(
                ContainerReferences.Candidates(covered, Everywhere(options, prefabs, scenes)));
            references.Census(report);
            return references;
        }

        /// <summary>The containers this run will actually open and convert.</summary>
        private static HashSet<string> Covered(Options options, List<string> prefabs,
            List<string> scenes)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in prefabs) if (options.Wants(path)) covered.Add(path);
            foreach (string path in scenes) if (options.Wants(path)) covered.Add(path);
            return covered;
        }

        /// <summary>
        /// Every container a referrer could be sitting in, which is every
        /// container there is.
        ///
        /// Not the same list as the one being converted, and deliberately: what
        /// empties a field is its target being replaced, and the field has no
        /// opinion about whether its own file was ticked. Searching only the
        /// selection is how converting four prefabs used to leave the other four
        /// hundred quietly holding None. Nothing here is opened — this is the
        /// list <see cref="ContainerReferences.Candidates"/> asks the asset
        /// database about, and the answer for most projects is "almost none of
        /// them depend on the selection", cheaply.
        /// </summary>
        private static List<string> Everywhere(Options options, List<string> prefabs,
            List<string> scenes)
        {
            var everywhere = new List<string>(options.IncludePrefabs ? prefabs
                                                                    : OrderedPrefabPaths());
            everywhere.AddRange(options.IncludeScenes && options.AllScenes ? scenes
                                                                          : ScenePaths(all: true));
            return everywhere;
        }

        /// <summary>
        /// Opens the containers that were not converted but were pointing at
        /// something that was, and re-points them.
        ///
        /// This is what makes "convert this prefab" a thing a person can do on a
        /// Tuesday. Before it, a narrow run was a trade: fewer files touched
        /// now, a scatter of fields reading None in the files you did not pick,
        /// and a warning saying so. The list is bounded by what actually
        /// changed — a run that converted nothing opens nothing here — and each
        /// file is written only if a field in it really was re-pointed, so a
        /// migration still shows up in a diff as the work it did.
        ///
        /// It gets a progress bar of its own rather than sharing the run's,
        /// because it also has to happen after the run's was cancelled. Stopping
        /// a conversion half way leaves containers converted, and a converted
        /// container with nothing sent to mend the references into it is the one
        /// state this module refuses to leave a project in.
        /// </summary>
        private static void Mend(ContainerReferences references, HashSet<string> covered,
            MigrationReport report)
        {
            var outsiders = references.Outsiders(covered);
            if (outsiders.Count == 0) return;

            var prefabs = new List<string>();
            var scenes = new List<string>();
            foreach (string path in outsiders)
            {
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) scenes.Add(path);
                else prefabs.Add(path);
            }

            using (var progress = new Progress("Mending references", outsiders.Count))
            {
                MendPrefabs(prefabs, references, report, progress);
                if (scenes.Count == 0 || progress.Cancelled) return;

                var setup = EditorSceneManager.GetSceneManagerSetup();
                try { MendScenes(scenes, references, report, progress); }
                finally
                {
                    if (setup != null && setup.Length > 0)
                    {
                        try { EditorSceneManager.RestoreSceneManagerSetup(setup); }
                        catch (Exception) { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene); }
                    }
                }
            }
        }

        private static void MendPrefabs(List<string> paths, ContainerReferences references,
            MigrationReport report, Progress progress)
        {
            foreach (string path in paths)
            {
                if (!progress.Step(path)) return;

                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                }
                catch (Exception error)
                {
                    Unreadable(report, path, "prefab", error);
                    continue;
                }

                try
                {
                    var roots = new[] { root };

                    // Same rule as the main pass, for the same reason: Unity
                    // will not save a prefab holding a script it cannot resolve,
                    // so mending one in memory is work thrown away with a
                    // reference reported as settled.
                    if (MissingScripts(roots).Count > 0)
                    {
                        references.Refused(roots, path);
                        continue;
                    }

                    int relinked = references.Relink(roots, path, report);
                    if (relinked > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
                        if (!saved) Unsaved(report, path, 0, relinked, references);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void MendScenes(List<string> paths, ContainerReferences references,
            MigrationReport report, Progress progress)
        {
            foreach (string path in paths)
            {
                if (!progress.Step(path)) return;

                var scene = default(UnityEngine.SceneManagement.Scene);
                try
                {
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                }
                catch (Exception error)
                {
                    Unreadable(report, path, "scene", error);
                    continue;
                }

                var roots = scene.GetRootGameObjects();
                if (MissingScripts(roots).Count > 0)
                {
                    references.Refused(roots, path);
                    continue;
                }

                int relinked = references.Relink(roots, path, report);
                if (relinked <= 0) continue;
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    Unsaved(report, path, 0, relinked, references);
            }
        }

        private static void Unreadable(MigrationReport report, string path, string kind,
            Exception error)
        {
            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Warning,
                Rule = "unreadable-container",
                Message = $"this {kind} holds a field pointing at something this run converted " +
                          $"and could not be opened to mend it: {error.Message}",
                Container = path,
            });
        }

        /// <summary>
        /// Says, once, which part of the project was converted and which fields
        /// reaching into it are still empty.
        ///
        /// This used to be the warning that a narrow run leaves a scatter of
        /// dead references behind it, because it did: only the selection was
        /// searched for referrers, so a prefab left out of the run was never
        /// asked whether it pointed at anything that changed. <see cref="Mend"/>
        /// is that question being asked project-wide, and what is left here is
        /// the far smaller thing — a run that was narrow <em>and</em> could not
        /// mend everything, where the two facts belong in one sentence because
        /// the reader is about to wonder whether they are connected.
        /// </summary>
        private static void Narrowed(Options options, ContainerReferences references,
            MigrationReport report, int unmended)
        {
            if (references == null || references.Changes == 0 || unmended == 0) return;

            var left = new List<string>();
            if (!options.IncludePrefabs) left.Add("prefabs were excluded");
            if (!options.IncludeScenes) left.Add("scenes were excluded");
            if (options.OnlyContainers != null && options.OnlyContainers.Count > 0)
                left.Add($"only {options.OnlyContainers.Count:n0} container(s) were selected");
            if (left.Count == 0) return;

            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Warning,
                Rule = ContainerReferences.Rule,
                Message = $"{references.Changes:n0} container(s) were converted, and " +
                          string.Join(", ", left) + $". References into them were followed across " +
                          $"the whole project, not just the selection, and {unmended:n0} of them " +
                          "could not be re-pointed — those are the errors above, each naming the " +
                          "field and why. Converting the rest of the project will not fix them on " +
                          "its own.",
            });
        }

        /// <summary>
        /// A selection, plus the prefabs the selection is built out of.
        ///
        /// The same hazard the ordering below exists for, arriving by a
        /// different road. A prefab or scene opened for editing shows the
        /// components of everything nested inside it as its own, and converting
        /// one of those records the swap as an override on top of a prefab asset
        /// that still holds the old component — so when that asset is converted
        /// in a later run, the object ends up carrying both. Ordering solves it
        /// for a run that holds the whole chain; this is what solves it for a run
        /// that was handed the top of one.
        ///
        /// Public because the person confirming a conversion should be reading
        /// the same list the conversion is about to act on, which means the Hub
        /// has to be able to ask this before the dialog rather than after.
        /// </summary>
        public static List<string> WithWhatTheyNest(IEnumerable<string> containers)
        {
            var closure = new List<string>();
            if (containers == null) return closure;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in containers)
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) closure.Add(path);
            if (closure.Count == 0) return closure;

            // Recursive: a scene nests a prefab that nests the one holding the
            // label, and the middle link is as unconvertible on its own as the
            // top one. The asset database answers this in one call and the
            // narrowing is already done for us — a dependency of a prefab is a
            // very short list next to a project.
            foreach (string dependency in AssetDatabase.GetDependencies(closure.ToArray(), true))
            {
                if (!dependency.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(dependency)) closure.Add(dependency);
            }
            return closure;
        }

        /// <summary>
        /// The options a narrowed run is really asking for, with the widening
        /// said out loud.
        ///
        /// The caller's own <see cref="Options"/> is left alone: it is theirs,
        /// they may run it twice, and a method that quietly grows a list it was
        /// lent is a method that makes the second run different from the first.
        /// </summary>
        private static Options Widened(Options options, MigrationReport report)
        {
            if (options.OnlyContainers == null || options.OnlyContainers.Count == 0) return options;

            var closure = WithWhatTheyNest(options.OnlyContainers);
            if (closure.Count == options.OnlyContainers.Count) return options;

            var added = closure.GetRange(options.OnlyContainers.Count,
                closure.Count - options.OnlyContainers.Count);
            added.Sort(StringComparer.Ordinal);

            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Info,
                Rule = "nested-selection",
                Message = $"{added.Count:n0} prefab(s) were added to this run because the ones you " +
                          "chose are built out of them: " +
                          string.Join(", ", added.GetRange(0, Math.Min(5, added.Count))) +
                          (added.Count > 5 ? $" and {added.Count - 5:n0} more" : string.Empty) +
                          ". Converting a label that lives in a nested prefab from the outside " +
                          "writes it as an override, and the object ends up holding both " +
                          "components the day that prefab is converted for itself.",
            });

            return new Options
            {
                AllScenes = options.AllScenes,
                IncludeScenes = options.IncludeScenes,
                IncludePrefabs = options.IncludePrefabs,
                OnlyContainers = closure,
                AdoptProjectFontDefaults = options.AdoptProjectFontDefaults,
                CrossContainerReferences = options.CrossContainerReferences,
            };
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

            // Both halves of the census: what the loaded objects still hold, and
            // what only the file still says. Gathered together because the
            // withholding below can send us round again, and a second round that
            // forgot the severed half would convert exactly what the first round
            // held back for.
            var unreadable = new List<string>();
            List<Referrer> Gather()
            {
                var found = CollectReferences(roots, convertible);
                unreadable.Clear();
                Severed(roots, container, convertible, found, unreadable);
                return found;
            }

            var references = convertible.Count == 0 ? new List<Referrer>() : Gather();

            // Anything whose conversion would leave a field unable to name it is
            // taken off the list before the list is used. Once it is off, the
            // references have to be gathered again: a component that was skipped
            // as a referrer because it was itself going to be converted is a
            // referrer again the moment it is not — and its own fields are
            // exactly the narrow kind that started this, so it can hold back the
            // next component along.
            //
            // Which is why this is a loop rather than a second pass. A uGUI
            // InputField named by an 'InputField' field is held back; that makes
            // its 'Text'-declared m_TextComponent live again; that has to hold
            // back the label inside it. One round found the first, reported it,
            // and broke the second.
            while (convertible.Count > 0 && Withhold(references, convertible))
                references = Gather();

            foreach (string lost in unreadable)
            {
                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = "severed-reference",
                    Message = $"{lost} named a text component in this file, and the field is not " +
                              "on the component any more — the script was rewritten into something " +
                              "without it. Nothing can put that reference back; it is here so that " +
                              "the loss is known rather than found at runtime.",
                    Container = container,
                });
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
                    var component = Replace(target, fonts, undo, report);
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

            /// <summary>The type the field is declared as, e.g. <c>Text</c>.</summary>
            public string DeclaredType;

            /// <summary>Whether that type can hold what the target is about to become.</summary>
            public bool Holds;
        }

        /// <summary>
        /// The references this container's file still spells out and its loaded
        /// objects no longer hold, for targets inside this same file.
        ///
        /// <see cref="CollectReferences"/> asks the loaded components, and a
        /// field whose declared type changed under it has nothing to say: Unity
        /// discarded the pointer when the script rewrite widened the field, so
        /// the walk sees 0 and records nothing. Nothing records it, so nothing
        /// mends it, so nothing reports it — the field simply reads None the next
        /// time somebody runs the game, and the report says the container was
        /// converted cleanly.
        ///
        /// <see cref="ContainerFile"/> was written for the cross-file half of
        /// exactly this and returns both halves now. This is the half where there
        /// is no other container to open: the controller and the label it drives
        /// are usually in one prefab, which makes this the commoner case and the
        /// quieter one.
        /// </summary>
        private static void Severed(GameObject[] roots, string container,
            List<MigrationTarget> convertible, List<Referrer> into, List<string> unreadable)
        {
            var stored = ContainerFile.Read(container);
            if (stored.Count == 0) return;

            var byTarget = new Dictionary<int, MigrationTarget>();
            foreach (var target in convertible)
                if (target.Source != null) byTarget[target.Source.GetInstanceID()] = target;

            Dictionary<long, Component> byId = null;
            foreach (var entry in stored)
            {
                if (!ContainerFile.IsInside(entry)) continue;
                if (Replacement(ContainerFile.KindOfScript(entry.TargetScript)) == null) continue;

                if (byId == null) byId = Inside(roots, container);
                if (!byId.TryGetValue(entry.Referrer, out var referrer) || referrer == null) continue;
                if (!byId.TryGetValue(entry.TargetFileId, out var found) || found == null) continue;
                if (!byTarget.TryGetValue(found.GetInstanceID(), out var target)) continue;

                var serialized = new SerializedObject(referrer);
                var property = serialized.FindProperty(entry.PropertyPath);
                if (property == null ||
                    property.propertyType != SerializedPropertyType.ObjectReference)
                {
                    // The file names a field the component no longer has. Said
                    // out loud rather than skipped: it means the script was
                    // rewritten into something that dropped the field, and the
                    // reference is gone for good.
                    unreadable.Add($"'{entry.PropertyPath}' on " +
                                   $"{referrer.GetType().Name} at '{PathOf(referrer.transform)}'");
                    continue;
                }

                // Still holding it, so the ordinary census already has this one.
                if (property.objectReferenceInstanceIDValue != 0) continue;

                string declared = DeclaredTypeName(property);
                into.Add(new Referrer
                {
                    Component = referrer,
                    ReferrerPath = PathOf(referrer.transform),
                    ReferrerType = referrer.GetType().Name,
                    PropertyPath = entry.PropertyPath,
                    OldId = found.GetInstanceID(),
                    Target = target,
                    DeclaredType = declared,
                    Holds = WouldHold(declared, Replacement(target.Kind)),
                });
            }
        }

        /// <summary>
        /// Every component in this container, by the id its file gives it.
        ///
        /// Two ways in, because the two kinds of container are open in different
        /// senses. A scene is open — its objects are the scene's own, and
        /// <c>GlobalObjectId</c> hands back the id the file uses. A prefab is
        /// open as a copy in a preview scene, and a copy carries no asset ids at
        /// all; the asset does, and the two hierarchies are the same shape, so
        /// the ids come off the asset and land on the copy by where they sit.
        /// </summary>
        private static Dictionary<long, Component> Inside(GameObject[] roots, string container)
        {
            var byId = new Dictionary<long, Component>();
            if (roots == null || string.IsNullOrEmpty(container)) return byId;

            if (container.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null) continue;
                        var id = GlobalObjectId.GetGlobalObjectIdSlow(component);
                        if (id.targetObjectId != 0) byId[(long)id.targetObjectId] = component;
                    }
                }
                return byId;
            }

            // Where each component sits, and what it is. Two of one type on one
            // object cannot be told apart this way, so neither is claimed: a
            // reference mended onto the wrong component of the right type is
            // worse than one reported as unmended.
            var byPlace = new Dictionary<string, Component>();
            var ambiguous = new HashSet<string>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    string key = PathOf(component.transform) + "|" + component.GetType().FullName;
                    if (byPlace.ContainsKey(key)) ambiguous.Add(key);
                    else byPlace[key] = component;
                }
            }

            UnityEngine.Object[] assets;
            try { assets = AssetDatabase.LoadAllAssetsAtPath(container); }
            catch (Exception) { return byId; }
            if (assets == null) return byId;

            foreach (var asset in assets)
            {
                if (!(asset is Component component)) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long id))
                    continue;
                string key = PathOf(component.transform) + "|" + component.GetType().FullName;
                if (ambiguous.Contains(key)) continue;
                if (byPlace.TryGetValue(key, out var live)) byId[id] = live;
            }
            return byId;
        }

        /// <summary>
        /// Takes off the list every component whose conversion would leave a
        /// field naming nothing, and says so where the component is.
        ///
        /// This is the difference between a migration and a wrecking ball. A
        /// field declared <c>Text</c> cannot hold a <c>OneTextLabel</c>, and
        /// unlike the TextMesh Pro case there is no rewrite coming to widen it:
        /// this module deliberately leaves <c>Text</c> alone in source, because
        /// the name is too common to rewrite by name. So converting the label
        /// that field names would break the field, permanently, in exchange for
        /// nothing the project asked for. The label stays as it is, the report
        /// says which field is the reason, and the choice of what to do about it
        /// stays with the person who wrote the field.
        ///
        /// Returns whether anything was withheld, because if it was, the list of
        /// references was gathered against a list that no longer holds.
        /// </summary>
        private static bool Withhold(List<Referrer> references, List<MigrationTarget> convertible)
        {
            var blocked = new Dictionary<MigrationTarget, Referrer>();
            foreach (var reference in references)
            {
                if (reference.Holds || blocked.ContainsKey(reference.Target)) continue;
                blocked[reference.Target] = reference;
            }
            if (blocked.Count == 0) return false;

            foreach (var pair in blocked)
            {
                var reference = pair.Value;
                pair.Key.Note(DoctorSeverity.Error, "reference-would-break",
                    $"this component was left as it is. '{reference.PropertyPath}' on " +
                    $"{reference.ReferrerType} at '{reference.ReferrerPath}' names it and is " +
                    $"declared {reference.DeclaredType}, which cannot hold the " +
                    $"{Replacement(pair.Key.Kind)?.Name} it would become — converting it would " +
                    $"empty that field with nothing able to fill it again. {Remedy(reference)}");
                convertible.Remove(pair.Key);
            }
            return true;
        }

        /// <summary>What the person reading the finding can actually do about it.</summary>
        private static string Remedy(Referrer reference)
        {
            if (IsTmpType(reference.DeclaredType))
                return "That field is a TextMesh Pro type, which the script rewrite in this tab " +
                       "does widen: run it, let Unity recompile, and convert again.";

            return $"The script rewrite does not touch {reference.DeclaredType} — the name is too " +
                   "common in source to rewrite safely — so nothing this tab can do will widen " +
                   "that field. If the script is yours, declare it as the OneText type and convert " +
                   "again. If it is not, leaving this component alone is the right answer.";
        }

        private static bool IsTmpType(string name) =>
            !string.IsNullOrEmpty(name) &&
            (name.StartsWith("TMP_", StringComparison.Ordinal) ||
             name.StartsWith("TMPro_", StringComparison.Ordinal) ||
             name == "TextMeshProUGUI" || name == "TextMeshPro");

        /// <summary>The OneText type a kind is replaced by, which is what a field has to hold.</summary>
        private static Type Replacement(MigrationKind kind)
        {
            switch (kind)
            {
                case MigrationKind.Label: return typeof(OneTextLabel);
                case MigrationKind.Mesh: return typeof(OneTextMesh);
                case MigrationKind.InputField: return typeof(OneTextInputField);
                case MigrationKind.Dropdown: return typeof(OneTextDropdown);
                default: return null;
            }
        }

        /// <summary>
        /// The type a serialized object reference is declared as.
        ///
        /// <c>SerializedProperty.type</c> answers <c>PPtr&lt;$Text&gt;</c> for a
        /// field declared <c>Text</c>, which is the declared type and not the
        /// type of whatever happens to be in it — the distinction this whole
        /// check rests on, since the thing in it is about to be destroyed.
        /// </summary>
        private static string DeclaredTypeName(SerializedProperty property)
        {
            string type = property.type;
            if (string.IsNullOrEmpty(type)) return null;
            int start = type.IndexOf('$');
            if (start < 0) return type;
            int end = type.IndexOf('>', start);
            return end < 0
                ? type.Substring(start + 1)
                : type.Substring(start + 1, end - start - 1);
        }

        /// <summary>
        /// Whether a field declared as <paramref name="declared"/> can hold a
        /// <paramref name="replacement"/>, by walking what the replacement is
        /// rather than by resolving the name to a type.
        ///
        /// Resolving would mean searching every loaded assembly for a simple
        /// name that several of them may have. Walking cannot answer "yes"
        /// wrongly in a way that matters: the worst case is two unrelated types
        /// sharing a simple name, and the answer to that is the readback in
        /// <see cref="Relink"/>, which measures rather than predicts.
        /// </summary>
        private static bool WouldHold(string declared, Type replacement)
        {
            if (string.IsNullOrEmpty(declared) || replacement == null) return true;
            for (var type = replacement; type != null; type = type.BaseType)
                if (type.Name == declared) return true;
            return false;
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

                        string declared = DeclaredTypeName(iterator);
                        found.Add(new Referrer
                        {
                            Component = component,
                            ReferrerPath = PathOf(component.transform),
                            ReferrerType = component.GetType().Name,
                            PropertyPath = iterator.propertyPath,
                            OldId = id,
                            Target = target,
                            DeclaredType = declared,
                            Holds = WouldHold(declared, Replacement(target.Kind)),
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
                              $"take the {made.GetType().Name} that replaced its target, and reads " +
                              $"None now. The field is declared {reference.DeclaredType}, which " +
                              "this migration read as wide enough — so either two types share that " +
                              "name or the field is not what it says. " + Remedy(reference),
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
        private static Component Replace(MigrationTarget target, FontAssetCache fonts, bool undo,
            MigrationReport report)
        {
            var go = target.Source.gameObject;

            // A dropdown is the one thing here whose values cannot be captured
            // in advance: options, the event's persistent calls and the whole of
            // Selectable's state are serialized structures, not the handful of
            // scalars a label carries, and a SerializedObject dies with the
            // object it reads. So this one is built before the old one goes.
            if (target.Kind == MigrationKind.Dropdown) return ReplaceDropdown(target, go, undo, report);

            // An input field is a Selectable too, and a Selectable's colour
            // block, transition and navigation are structures rather than the
            // scalars MigrationValues can hold. Without this they are silently
            // replaced by the defaults, which is a field that converts, reports
            // nothing, and stops highlighting when the pointer is over it.
            if (target.Kind == MigrationKind.InputField)
                return ReplaceInputField(target, go, fonts, undo);

            if (undo) Undo.DestroyObjectImmediate(target.Source);
            else UnityEngine.Object.DestroyImmediate(target.Source);

            Component made;
            switch (target.Kind)
            {
                case MigrationKind.Label:
                    EnsureGraphicParts(go, undo);
                    made = Add<OneTextLabel>(go, undo);
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
        private static Component ReplaceDropdown(MigrationTarget target, GameObject go, bool undo,
            MigrationReport report)
        {
            // Not typed as Unity's dropdown: TMP's is the other one that lands
            // here, and everything below reads the old component through a
            // SerializedObject by field name rather than through its API. The
            // two agree on every one of those names, because TMP's dropdown is
            // Unity's with the label types changed.
            var old = target.Source;

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
                // The options first, and then everything else, because
                // m_Value is in "everything else" and the component clamps it
                // against the options it can see when the write is applied. Set
                // the other way round, a dropdown sitting on its third option
                // comes back sitting on its first — quietly, since a value of
                // zero is what an untouched dropdown has.
                CarryOptions(new SerializedObject(old), new SerializedObject(carrier));
                Carry(new SerializedObject(old), new SerializedObject(carrier),
                    Both(SelectableFields, DropdownFields));

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
                CarryOptions(new SerializedObject(carrier), new SerializedObject(made));
                Carry(new SerializedObject(carrier), new SerializedObject(made),
                    Both(SelectableFields, DropdownFields));
            }
            finally { UnityEngine.Object.DestroyImmediate(scratch); }

            var to = new SerializedObject(made);
            SetObject(to, "m_CaptionText", Label(captionObject));
            SetObject(to, "m_ItemText", Label(itemObject));

            // A TMP dropdown with a placeholder holds -1 for "nothing chosen",
            // and there is no placeholder to come back to here. Left alone it
            // would be a value the component clamps every time it draws and
            // never writes down, which is a field that disagrees with what is
            // on screen for the rest of the prefab's life.
            var value = to.FindProperty("m_Value");
            if (value != null && value.intValue < 0) value.intValue = 0;

            if (undo) to.ApplyModifiedProperties();
            else to.ApplyModifiedPropertiesWithoutUndo();

            // A label the dropdown named and that is still a Text is a label
            // something else stopped this run from converting. The dropdown has
            // just been handed nothing for it, and a dropdown with no caption is
            // exactly the silent failure everything here is arranged against.
            Missing(report, target, captionObject, "Caption Text");
            Missing(report, target, itemObject, "Item Text");

            Reenable(made);
            return made;
        }

        private static void Missing(MigrationReport report, MigrationTarget target,
            GameObject named, string field)
        {
            if (named == null || Label(named) != null) return;
            report.Add(target.Note(DoctorSeverity.Warning, "no-counterpart",
                $"{field} named '{named.name}', which this run did not convert, so the new " +
                "dropdown has none. Assign a OneTextLabel to it by hand, or look for the finding " +
                "on that object saying why it was left alone."));
        }

        /// <summary>
        /// The input field swap, which is the dropdown's for the same reason and
        /// a smaller one: everything an input field carries in its own right was
        /// read at scan time into <see cref="MigrationValues"/>, but Selectable's
        /// state was not and could not be — a colour block is a structure.
        /// </summary>
        private static Component ReplaceInputField(MigrationTarget target, GameObject go,
            FontAssetCache fonts, bool undo)
        {
            var old = target.Source;

            var scratch = new GameObject("OneText input field carrier", typeof(RectTransform))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            OneTextInputField made;
            try
            {
                var carrier = scratch.AddComponent<OneTextInputField>();
                if (carrier == null)
                    throw new InvalidOperationException("the carrier would not take a OneTextInputField");
                Carry(new SerializedObject(old), new SerializedObject(carrier), SelectableFields);

                if (undo) Undo.DestroyObjectImmediate(old);
                else UnityEngine.Object.DestroyImmediate(old);

                EnsureGraphicParts(go, undo);
                made = Add<OneTextInputField>(go, undo);
                if (made == null)
                    throw new InvalidOperationException(
                        "the object would not take a OneTextInputField after the old one was removed");
                Carry(new SerializedObject(carrier), new SerializedObject(made), SelectableFields);
            }
            finally { UnityEngine.Object.DestroyImmediate(scratch); }

            ApplyValues(made, target, fonts, undo);
            return made;
        }

        /// <summary>
        /// Selectable's own state, which a dropdown or an input field is as much
        /// as it is either of those: lose it and the thing stops highlighting,
        /// stops navigating and may stop taking clicks at all.
        /// </summary>
        private static readonly string[] SelectableFields =
        {
            "m_Navigation", "m_Transition", "m_Colors", "m_SpriteState",
            "m_AnimationTriggers", "m_Interactable", "m_TargetGraphic",
        };

        /// <summary>
        /// What a dropdown carries on top of Selectable's own state.
        ///
        /// The options are not in here: they are the one field whose shape is
        /// not the same on both sides — see <see cref="CarryOptions"/>.
        /// </summary>
        private static readonly string[] DropdownFields =
        {
            "m_Template", "m_CaptionImage", "m_ItemImage", "m_Value",
            "m_OnValueChanged", "m_AlphaFadeSpeed",
        };

        /// <summary>
        /// The options list, element by element, because the element is where
        /// the two dropdowns stop agreeing.
        ///
        /// Unity's <c>OptionData</c> holds a string and a sprite; TextMesh
        /// Pro's holds those and a colour. <c>CopyFromSerializedProperty</c>
        /// moves a structure by walking it, so a source with a field the
        /// destination has not got is not a copy this can be trusted to make —
        /// and the options are the field with the least to spare: a dropdown
        /// that converts, reports nothing and comes back empty is the failure
        /// this whole path is arranged against.
        ///
        /// The colour is written on every element rather than left to the
        /// resize, which fills a new element with whatever the last one held or
        /// with zeroes. Zero is a transparent black, and a caption image tinted
        /// with it is invisible — a converted dropdown losing its image for a
        /// field the source never had.
        /// </summary>
        private static void CarryOptions(SerializedObject from, SerializedObject to)
        {
            var source = from.FindProperty("m_Options.m_Options");
            var destination = to.FindProperty("m_Options.m_Options");
            if (source == null || destination == null || !source.isArray || !destination.isArray)
                return;

            destination.arraySize = source.arraySize;
            for (int i = 0; i < source.arraySize; i++)
            {
                var one = source.GetArrayElementAtIndex(i);
                var other = destination.GetArrayElementAtIndex(i);
                if (one == null || other == null) continue;

                var text = one.FindPropertyRelative("m_Text");
                var into = other.FindPropertyRelative("m_Text");
                if (text != null && into != null) into.stringValue = text.stringValue;

                var image = one.FindPropertyRelative("m_Image");
                var intoImage = other.FindPropertyRelative("m_Image");
                if (image != null && intoImage != null)
                    intoImage.objectReferenceValue = image.objectReferenceValue;

                var colour = one.FindPropertyRelative("m_Color");
                var intoColour = other.FindPropertyRelative("m_Color");
                if (intoColour != null)
                    intoColour.colorValue = colour != null ? colour.colorValue : Color.white;
            }
            to.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Every named field that exists on both, moved whole.</summary>
        private static void Carry(SerializedObject from, SerializedObject to,
            params string[] paths)
        {
            foreach (string path in paths)
            {
                var property = from.FindProperty(path);
                if (property != null) to.CopyFromSerializedProperty(property);
            }
            to.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string[] Both(string[] first, string[] second)
        {
            var all = new string[first.Length + second.Length];
            first.CopyTo(all, 0);
            second.CopyTo(all, first.Length);
            return all;
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

            SetDecoration(serialized, "_decoration", values.Decoration);

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
            CarryListeners(serialized, "_onEndEdit", values.EndEditCalls, replaced, target, report);

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

                if (asset == null)
                {
                    NoteMissing(sourcePath, target);
                    // And then a placeholder, the same answer the no-source-path
                    // branch above has always given. This branch used to return
                    // the null: the font asset named a file, the file was not
                    // there (deleted, or in a package the project no longer
                    // has, or unreadable), and every label that wanted it was
                    // converted pointing at nothing. That is the case with the
                    // least excuse for it — the file name is known exactly, so
                    // the placeholder can say what to go and find, and dropping
                    // it in fixes every label at once.
                    asset = FontRecovery.PlaceholderForFile(_report, sourcePath);
                    if (asset != null) NoteRecoveredFile(sourcePath, asset, target);
                }

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
            /// The same note for a font known by its file rather than by the
            /// name of the asset that wanted it, and tolerant of having no
            /// target: <see cref="AdoptDefaults"/> resolves the project's
            /// default font through this cache with nothing to hang a finding
            /// on.
            /// </summary>
            private void NoteRecoveredFile(string sourcePath, OneFontAsset placeholder,
                MigrationTarget target)
            {
                string message =
                    $"{sourcePath} is not there, so the migration made a placeholder font at " +
                    $"{AssetDatabase.GetAssetPath(placeholder)} and pointed everything that " +
                    "wanted that file at it instead of at nothing. Put " +
                    $"{System.IO.Path.GetFileName(sourcePath)} anywhere in the project and drop " +
                    "it into the placeholder, and they all have their font back.";
                _report.Add(target != null
                    ? target.Note(DoctorSeverity.Info, FontRecovery.RecoveredRule, message)
                    : new MigrationFinding
                    {
                        Severity = DoctorSeverity.Info,
                        Rule = FontRecovery.RecoveredRule,
                        Message = message,
                    });
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
                string message = $"no OneText font asset could be made from {sourcePath}. " +
                                 "A placeholder stands in for it, so the reference survives; " +
                                 "until the file is supplied the text draws in the project " +
                                 "default, or in one of the device's own fonts, or not at all.";
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

        /// <summary>
        /// The outline, shadow and glow, written field by field because the
        /// whole struct cannot be handed over at once.
        /// </summary>
        private static void SetDecoration(SerializedObject serialized, string path,
            in TextDecoration decoration)
        {
            if (serialized.FindProperty(path) == null) return; // a mesh, which has none

            SetInt(serialized, path + ".Set", (int)decoration.Set);
            SetColor32(serialized, path + ".OutlineColor", decoration.OutlineColor);
            SetFloat(serialized, path + ".OutlineWidth", decoration.OutlineWidth);
            SetColor32(serialized, path + ".ShadowColor", decoration.ShadowColor);
            SetVector2(serialized, path + ".ShadowOffset", decoration.ShadowOffset);
            SetFloat(serialized, path + ".ShadowSoftness", decoration.ShadowSoftness);
            SetColor32(serialized, path + ".GlowColor", decoration.GlowColor);
            SetFloat(serialized, path + ".GlowRadius", decoration.GlowRadius);
            SetFloat(serialized, path + ".GlowInner", decoration.GlowInner);
            SetFloat(serialized, path + ".OutlineSoftness", decoration.OutlineSoftness);
            SetFloat(serialized, path + ".FaceDilate", decoration.FaceDilate);
        }

        /// <summary>
        /// A <c>Color32</c>, which Unity may hand back as a colour or as four
        /// bytes depending on how it decided to serialize the struct.
        ///
        /// Both are written rather than one being assumed, and a round-trip test
        /// says which branch is the live one — the alternative is a comment
        /// asserting something nobody measured, and a migration that quietly
        /// leaves every outline black.
        /// </summary>
        private static void SetColor32(SerializedObject serialized, string path, Color32 value)
        {
            var property = serialized.FindProperty(path);
            if (property == null) return;

            if (property.propertyType == SerializedPropertyType.Color)
            {
                property.colorValue = value;
                return;
            }

            SetInt(serialized, path + ".r", value.r);
            SetInt(serialized, path + ".g", value.g);
            SetInt(serialized, path + ".b", value.b);
            SetInt(serialized, path + ".a", value.a);
        }

        private static void SetVector2(SerializedObject serialized, string path, Vector2 value)
        {
            var property = serialized.FindProperty(path);
            if (property != null && property.propertyType == SerializedPropertyType.Vector2)
                property.vector2Value = value;
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
