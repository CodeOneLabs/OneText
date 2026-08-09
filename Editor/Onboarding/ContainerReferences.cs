using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using OneText.UGUI;

namespace OneText.Editor
{
    /// <summary>
    /// The references that point out of one container and into another, written
    /// down before anything is converted and re-pointed once it has been.
    ///
    /// <see cref="ComponentMigration"/> already protects the references inside a
    /// container: it notes every field that names a component it is about to
    /// destroy and points that field at the replacement before the file is
    /// written. That works because the field and the component are alive in one
    /// open hierarchy at one moment. Across containers they never are. A prefab
    /// that nests another one names the nested prefab's components by the nested
    /// file's own identity, and converting that file gives the component a new
    /// identity; the host is not open when it happens, and by the time the host
    /// is opened in its own right the field has already resolved to nothing and
    /// there is nothing left to trace. The result is a field that reads "None
    /// (Text Mesh Pro UGUI)" in a prefab the migration reported as clean —
    /// exactly the failure the in-container pass exists to prevent, one file
    /// over.
    ///
    /// So the identity cannot be a live object. It is three strings that survive
    /// the file being rewritten: the target container's asset path, the
    /// hierarchy path of the GameObject inside it, and which OneText component
    /// the thing on it will become. The census reads them while the reference is
    /// still whole; the re-point looks them up again afterwards, in the
    /// referrer's own hierarchy, where the nested instance now carries a OneText
    /// component in place of the old one.
    ///
    /// The container in that identity is the file the component is <em>written
    /// in</em>, not the one it was reached through — see <see cref="Origin"/>.
    /// Prefabs nest several deep in a real project, and the files in the middle
    /// of such a chain hold no components of their own, convert nothing and are
    /// never written; an identity that stopped at one of those would be waiting
    /// for a change that is never announced.
    ///
    /// Two things keep this affordable on a project of four thousand prefabs.
    /// <c>AssetDatabase.GetDependencies(path, false)</c> says which assets could
    /// possibly point into a container this run will touch, and only those are
    /// opened for the census — an asset that depends on nothing being converted
    /// cannot be broken by the conversion. And the re-point itself costs no
    /// opens at all: a container is always converted after everything it depends
    /// on, so by the time it is opened for its own conversion every reference it
    /// holds into another container has already been broken and can be mended
    /// in the same visit.
    /// </summary>
    public sealed class ContainerReferences
    {
        /// <summary>
        /// The one rule this module reports under.
        ///
        /// Deliberately not <c>dangling-reference</c>. That rule means "a field
        /// in this container names a component in this container, and the
        /// migration handled it" — it is raised, acted on, and closed inside a
        /// single visit, and a person reading it has nothing to do. This one
        /// means the opposite: the field and the component that broke it are in
        /// different files, the fix happens somewhere other than where the
        /// damage did, and whether it happens at all depends on whether the
        /// referring asset is in the same run. Those are different things to
        /// know and they deserve different names in a report you can filter.
        /// </summary>
        public const string Rule = "cross-container-reference";

        /// <summary>
        /// One serialized field, in one asset, that names a component living in
        /// another one.
        ///
        /// Every field here is a string or an enum on purpose. A live
        /// <c>Component</c> would be the obvious thing to hold and it is the one
        /// thing that cannot work: the component this describes exists only
        /// while its container is loaded, and the whole point of recording it is
        /// to still know what it was after that container has been closed,
        /// rewritten, and reopened with different file identifiers inside it.
        /// </summary>
        public sealed class CrossReference
        {
            /// <summary>Asset path of the prefab, scene or asset holding the field.</summary>
            public string Referrer;

            /// <summary>Hierarchy path of the object holding it; empty for an asset.</summary>
            public string ReferrerPath;

            /// <summary>Type name of the component or ScriptableObject holding it.</summary>
            public string ReferrerType;

            /// <summary>
            /// Which one, when the object carries more than one component of
            /// that type.
            ///
            /// Nothing stops a GameObject holding two of the same script, and
            /// picking whichever came first would mean reading one component's
            /// field and writing another's. That was survivable while a write
            /// needed a container to have been marked changed; now that an empty
            /// field is itself the reason to write, guessing wrong would put a
            /// reference into a field that was deliberately left empty.
            /// </summary>
            public int ReferrerIndex;

            /// <summary>The serialized property path, as the inspector would name it.</summary>
            public string PropertyPath;

            /// <summary>Asset path of the container the named component lives in.</summary>
            public string Target;

            /// <summary>Hierarchy path of the GameObject inside that container.</summary>
            public string TargetPath;

            /// <summary>The type as a person would name it: <c>TextMeshProUGUI</c>.</summary>
            public string TargetComponent;

            /// <summary>Which OneText component will stand in its place.</summary>
            public MigrationKind TargetKind;

            /// <summary>
            /// Whether the component was reached through an instance of its
            /// container sitting inside the referrer, rather than named across
            /// files.
            ///
            /// The difference decides what the field is given back. A host
            /// prefab that nests another one has to be handed the component on
            /// its own copy; handing it the one in the nested asset instead
            /// would look identical in the inspector and serialize as a
            /// reference to a different object than the one on screen.
            /// </summary>
            public bool Nested;

            /// <summary>Whether the field was actually pointed at the replacement.</summary>
            public bool Relinked;

            /// <summary>
            /// Whether the field was seen holding nothing after the run had
            /// started converting.
            ///
            /// This is the only fact in here that is observed rather than
            /// deduced, and it outranks everything that is deduced. The census
            /// only ever records a field that was holding a live component, so a
            /// field reading None later in the same run was emptied by this run
            /// — whatever the bookkeeping believes about which container it
            /// named or whether that container was written. Reporting used to
            /// hang off the bookkeeping and went silent whenever the bookkeeping
            /// was wrong, which is the worst possible failure for a module whose
            /// entire subject is silence.
            /// </summary>
            public bool Broken;

            /// <summary>
            /// Whether this reference has been dealt with, one way or another:
            /// re-pointed, reported as refused, or found to be unbroken. What is
            /// left over when the run ends is what nobody managed to fix, and
            /// that is what has to be said out loud.
            /// </summary>
            public bool Settled;

            /// <summary>Why it could not be re-pointed, when something specific went wrong.</summary>
            public string Trouble;

            /// <summary>
            /// True when this was read off the file rather than out of the
            /// hierarchy, because the hierarchy no longer holds it.
            ///
            /// Two things put a reference here, and neither is the user's doing:
            /// the script rewrite gave the field a type the stored pointer
            /// cannot satisfy, so Unity dropped the pointer on import; or the
            /// component holding it has no script in the project and never
            /// loaded at all. Both are invisible to a census that reads live
            /// objects, and both leave the file saying exactly what was meant.
            ///
            /// It is never reported on its own evidence. A severed reference is
            /// only this run's business if this run converted the container it
            /// named — otherwise it was already broken when we arrived, and
            /// saying so would be blaming the migration for the state of the
            /// project.
            /// </summary>
            public bool Severed;

            /// <summary>
            /// True when nothing in the hierarchy could be found to hold this —
            /// a component whose script is missing. It can be named but not
            /// mended: there is no field to write to until the script comes back.
            /// </summary>
            public bool Unreachable;

            public override string ToString() =>
                $"{Referrer}:{ReferrerPath}.{PropertyPath} -> {Target}:{TargetPath}";
        }

        private readonly List<CrossReference> _records = new List<CrossReference>();

        private readonly Dictionary<string, List<CrossReference>> _byReferrer =
            new Dictionary<string, List<CrossReference>>(StringComparer.Ordinal);

        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Containers whose components were swapped and whose file was written.</summary>
        private readonly HashSet<string> _changed = new HashSet<string>(StringComparer.Ordinal);

        private readonly HashSet<string> _watched;

        public ContainerReferences(HashSet<string> watched)
        {
            _watched = watched ?? new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Every cross-container reference found, in the order they were found.</summary>
        public IReadOnlyList<CrossReference> All => _records;

        /// <summary>Containers that really were converted and written, so far.</summary>
        public int Changes => _changed.Count;

        /// <summary>
        /// Says that this container's components were swapped and the file was
        /// written.
        ///
        /// Nothing is re-pointed at a container that did not say this. A prefab
        /// refused for holding a missing script, or one whose save failed, is
        /// unchanged on disk, which means every reference into it is still
        /// perfectly good — and re-pointing a field that was never broken would
        /// be a write nobody asked for, in a file nobody expected to be in the
        /// diff.
        /// </summary>
        public void Changed(string container)
        {
            if (!string.IsNullOrEmpty(container)) _changed.Add(container);
        }

        // ------------------------------------------------------------ narrowing

        /// <summary>
        /// Every asset that could hold a field pointing into one of these
        /// containers: the containers themselves, plus the ScriptableObjects.
        ///
        /// The dependency list is the narrowing and it is the reason this is
        /// affordable. A reference into a prefab is a reference to that prefab's
        /// guid, so an asset that does not depend on a container cannot name
        /// anything inside it, and the cheap question rules out most of a
        /// project before anything is opened.
        ///
        /// ScriptableObjects are in the universe because they are the half of
        /// this hole nothing else covers. A settings asset with a
        /// <c>TMP_Text</c> field on it is an ordinary thing to have, it is not a
        /// scene and it is not a prefab, and nothing in the migration has ever
        /// looked at one.
        /// </summary>
        public static HashSet<string> Candidates(IEnumerable<string> containers)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (containers != null) foreach (string path in containers) known.Add(path);

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            if (known.Count == 0) return candidates;

            var universe = new List<string>(known);
            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) universe.Add(path);
            }

            foreach (string path in universe)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                {
                    if (dependency == path || !known.Contains(dependency)) continue;
                    candidates.Add(path);
                    break;
                }
            }
            return candidates;
        }

        // -------------------------------------------------------------- census

        /// <summary>
        /// Opens every candidate once, before anything is converted, and writes
        /// down what it points at.
        ///
        /// This is the one pass that costs something the migration was not
        /// already paying, and it is unavoidable: the information it collects
        /// stops existing the moment the first container is saved. It is bounded
        /// by the dependency narrowing above rather than by the size of the
        /// project, so a project whose prefabs nest nothing pays nothing.
        /// </summary>
        public void Census(MigrationReport report)
        {
            var ordered = new List<string>(_watched);
            ordered.Sort(StringComparer.Ordinal);

            var scenes = new List<string>();
            foreach (string path in ordered)
            {
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".unity") scenes.Add(path);
                else if (extension == ".prefab") CensusPrefab(path, report);
                else CensusAsset(path);
            }
            if (scenes.Count == 0) return;

            // Every scene is opened in Single mode here exactly as the main pass
            // opens them, so the editor's own open scenes have to be put back
            // before the main pass starts and captures what it thinks they are.
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string path in scenes) CensusScene(path, report);
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    try { EditorSceneManager.RestoreSceneManagerSetup(setup); }
                    catch (Exception) { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene); }
                }
            }
        }

        private void CensusPrefab(string path, MigrationReport report)
        {
            GameObject root;
            try { root = PrefabUtility.LoadPrefabContents(path); }
            catch (Exception error)
            {
                Unreadable(report, path, error);
                return;
            }

            try { Record(new[] { root }, path); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private void CensusScene(string path, MigrationReport report)
        {
            var scene = default(UnityEngine.SceneManagement.Scene);
            try { scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single); }
            catch (Exception error)
            {
                Unreadable(report, path, error);
                return;
            }
            Record(scene.GetRootGameObjects(), path);
        }

        private void CensusAsset(string path)
        {
            UnityEngine.Object[] objects;
            try { objects = AssetDatabase.LoadAllAssetsAtPath(path); }
            catch (Exception) { return; }
            if (objects == null) return;

            var cache = new Cache();
            foreach (var asset in objects)
            {
                if (!(asset is ScriptableObject)) continue;
                Walk(asset, path, asset.name, asset.GetType().Name, cache);
            }
        }

        /// <summary>
        /// Said as a warning rather than swallowed: a candidate that will not
        /// open is a candidate whose references cannot be recorded, and the
        /// consequence of that is silence in exactly the place this class exists
        /// to stop being silent.
        /// </summary>
        private static void Unreadable(MigrationReport report, string path, Exception error)
        {
            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Warning,
                Rule = Rule,
                Message = "this could not be opened to read the references it holds into other " +
                          "containers, so if it holds any they will be left pointing at nothing " +
                          $"once those containers are converted: {error.Message}",
                Container = path,
            });
        }

        // ------------------------------------------------------------ recording

        /// <summary>
        /// Writes down every field in this open hierarchy that names a
        /// convertible component belonging to a different container.
        ///
        /// Called from the census before an Apply, and from the ordinary walk
        /// during a Scan — a scan converts nothing, so every reference is still
        /// whole while the container it lives in is open anyway, and the report
        /// costs no second visit.
        /// </summary>
        public int Record(GameObject[] roots, string container)
        {
            if (roots == null || string.IsNullOrEmpty(container)) return 0;
            if (!_watched.Contains(container)) return 0;

            var cache = new Cache();
            int found = 0;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue; // a missing script
                    if (component is Transform) continue;
                    found += Walk(component, container, ComponentMigration.PathOf(component.transform),
                        component.GetType().Name, cache);
                }
            }

            // Everything the hierarchy could not be asked about. Second, because
            // a reference the walk above found is the better record of the two:
            // it knows the component it is on, and it can be mended.
            found += RecordSevered(roots, container);
            return found;
        }

        /// <summary>
        /// The references this container's file still spells out and its loaded
        /// objects no longer hold — see <see cref="ContainerFile"/> for why they
        /// exist and why the file is the only place left to read them.
        /// </summary>
        private int RecordSevered(GameObject[] roots, string container)
        {
            var stored = ContainerFile.Read(container);
            if (stored.Count == 0) return 0;

            Dictionary<string, List<Component>> byScript = null;
            int found = 0;

            foreach (var entry in stored)
            {
                var kind = ContainerFile.KindOfScript(entry.TargetScript);
                if (!Convertible(kind)) continue;

                string target = AssetDatabase.GUIDToAssetPath(entry.TargetGuid);
                if (string.IsNullOrEmpty(target) || target == container) continue;

                // Asked of the target's own file, which has not been converted
                // yet at census time, so the object the id names is still there
                // to be measured rather than guessed at.
                string targetPath = PathInside(target, entry.TargetFileId);
                if (targetPath == null) continue;

                if (byScript == null) byScript = ByScript(roots);
                var referrer = Holder(byScript, entry);

                var reference = new CrossReference
                {
                    Referrer = container,
                    ReferrerPath = referrer == null
                        ? null
                        : ComponentMigration.PathOf(referrer.transform),
                    ReferrerType = referrer == null
                        ? ScriptName(entry.ReferrerScript)
                        : referrer.GetType().Name,
                    ReferrerIndex = IndexOf(referrer),
                    PropertyPath = entry.PropertyPath,
                    Target = target,
                    TargetPath = targetPath,
                    TargetComponent = ScriptName(entry.TargetScript),
                    TargetKind = kind,
                    Nested = true,
                    Severed = true,
                    Unreachable = referrer == null,
                };
                if (Add(reference)) found++;
            }
            return found;
        }

        /// <summary>
        /// The component the file says holds this field, matched by the script
        /// it runs. Null when the project has no such script, which is the whole
        /// of why some of these were never read in the first place.
        /// </summary>
        private static Component Holder(Dictionary<string, List<Component>> byScript, ContainerFile.StoredReference entry)
        {
            if (string.IsNullOrEmpty(entry.ReferrerScript)) return null;
            if (!byScript.TryGetValue(entry.ReferrerScript, out var candidates)) return null;
            if (candidates.Count == 1) return candidates[0];

            // Several components run the same script. The one this belongs to is
            // the one that has the field and is not holding anything in it —
            // which is what "severed" means and what tells it from a sibling
            // that is perfectly fine.
            foreach (var candidate in candidates)
            {
                var property = new SerializedObject(candidate).FindProperty(entry.PropertyPath);
                if (property != null &&
                    property.propertyType == SerializedPropertyType.ObjectReference &&
                    property.objectReferenceValue == null)
                    return candidate;
            }
            return null;
        }

        private static Dictionary<string, List<Component>> ByScript(GameObject[] roots)
        {
            var byScript = new Dictionary<string, List<Component>>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (!(component is MonoBehaviour behaviour)) continue;

                    var script = MonoScript.FromMonoBehaviour(behaviour);
                    if (script == null) continue;
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
                    if (string.IsNullOrEmpty(guid)) continue;

                    if (!byScript.TryGetValue(guid, out var list))
                        byScript[guid] = list = new List<Component>();
                    list.Add(component);
                }
            }
            return byScript;
        }

        /// <summary>Where the object with this id sits inside its own container.</summary>
        private static string PathInside(string container, long fileId)
        {
            UnityEngine.Object[] objects;
            try { objects = AssetDatabase.LoadAllAssetsAtPath(container); }
            catch (Exception) { return null; }
            if (objects == null) return null;

            foreach (var asset in objects)
            {
                if (!(asset is Component component)) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long id)) continue;
                if (id != fileId) continue;
                return ComponentMigration.PathOf(component.transform);
            }
            return null;
        }

        private static string ScriptName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "a missing script";
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path)
                ? "a missing script"
                : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private int Walk(UnityEngine.Object subject, string container, string referrerPath,
            string referrerType, Cache cache)
        {
            SerializedObject serialized;
            try { serialized = new SerializedObject(subject); }
            catch (Exception) { return 0; }

            int found = 0;
            int index = -1; // worked out only if this component turns out to hold one
            var iterator = serialized.GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                var component = iterator.objectReferenceValue as Component;
                if (component == null) continue;

                // A Transform is never converted and a GameObject reference is
                // never broken by this migration — only the component is
                // destroyed, and the object it sat on keeps its identity. Both
                // are ruled out before the expensive questions are asked,
                // because between them they are most of the object references in
                // any hierarchy.
                if (component is Transform) continue;

                if (!cache.Identify(component, out string target, out string path, out bool nested))
                    continue;
                if (target == container) continue;

                var kind = cache.KindOf(component);
                if (!Convertible(kind)) continue;

                // A field on a component that is itself about to be destroyed
                // has no future to protect, and asking the question only here
                // means the providers are never run over a whole project's worth
                // of components that turned out to reference nothing.
                if (subject is Component self && Convertible(cache.KindOf(self))) break;

                if (index < 0) index = IndexOf(subject as Component);

                var reference = new CrossReference
                {
                    Referrer = container,
                    ReferrerPath = referrerPath,
                    ReferrerType = referrerType,
                    ReferrerIndex = index,
                    PropertyPath = iterator.propertyPath,
                    Target = target,
                    TargetPath = path,
                    TargetComponent = component.GetType().Name,
                    TargetKind = kind,
                    Nested = nested,
                };
                if (Add(reference)) found++;
            }
            return found;
        }

        private bool Add(CrossReference reference)
        {
            string key = string.Join("|", reference.Referrer, reference.ReferrerPath,
                reference.ReferrerType, reference.PropertyPath);
            if (!_seen.Add(key)) return false;

            _records.Add(reference);
            if (!_byReferrer.TryGetValue(reference.Referrer, out var list))
                _byReferrer[reference.Referrer] = list = new List<CrossReference>();
            list.Add(reference);
            return true;
        }

        private static bool Convertible(MigrationKind kind) =>
            kind == MigrationKind.Label || kind == MigrationKind.Mesh ||
            kind == MigrationKind.InputField;

        // ------------------------------------------------------------ re-point

        /// <summary>
        /// Points this container's recorded references at the components that
        /// replaced the ones they named, in the visit the container was going to
        /// get anyway.
        ///
        /// The field itself decides. A field that still holds something was not
        /// broken by anything and is left exactly alone — which is how a target
        /// that was refused, or whose save failed, ends up with its referrers
        /// untouched, and how a second run costs nothing. A field that now holds
        /// nothing was emptied by this run, because the census only wrote it
        /// down while it was holding a live component, and that is true whether
        /// or not this class correctly worked out which container to blame.
        ///
        /// The read-back is the same one the in-container pass does and for the
        /// same reason: a field still declared as a TextMesh Pro type refuses a
        /// <c>OneTextLabel</c> silently, and a refusal that looks like a success
        /// is how a migration reports work it did not do.
        /// </summary>
        public int Relink(GameObject[] roots, string container, MigrationReport report) =>
            Visit(roots, container, report, mend: true);

        /// <summary>
        /// Reads this container's references without mending any of them,
        /// because this container is not going to be written.
        ///
        /// A prefab refused for a missing script is refused over its own
        /// components; it says nothing about the prefabs it points at, which may
        /// have been converted an hour ago. Those fields are broken and will
        /// stay broken until the missing script is dealt with. Looking is what
        /// separates that from the far commoner case of a refused prefab that
        /// pointed at nothing which changed, and it costs one property read.
        /// </summary>
        public void Refused(GameObject[] roots, string container) =>
            Visit(roots, container, null, mend: false);

        private int Visit(GameObject[] roots, string container, MigrationReport report, bool mend)
        {
            if (roots == null || !_byReferrer.TryGetValue(container ?? string.Empty, out var pending))
                return 0;

            Dictionary<string, GameObject> instances = null;
            int relinked = 0;

            foreach (var reference in pending)
            {
                if (reference.Settled) continue;

                var referrer = FindComponent(roots, reference);
                if (referrer == null)
                {
                    reference.Trouble = "the component that held it is no longer in this container";
                    continue;
                }

                var serialized = new SerializedObject(referrer);
                var property = Broken(serialized, reference);
                if (property == null) continue;

                if (!mend)
                {
                    reference.Trouble = "the file holding it was refused for a missing script, and " +
                                        "a file Unity will not save is a file nothing can be " +
                                        "mended in";
                    continue;
                }

                if (instances == null) instances = Instances(roots);
                var made = Resolve(reference, instances);
                if (made == null)
                {
                    reference.Trouble = "the component that replaced it could not be found in " +
                                        reference.Target;
                    continue;
                }

                if (Write(serialized, property, reference, made, report)) relinked++;
            }

            if (report != null) report.Relinked += relinked;
            return relinked;
        }

        /// <summary>
        /// The property behind a recorded reference when it is empty and
        /// therefore something this run broke, and null when there is nothing to
        /// do about it.
        ///
        /// The three answers are all different and only one of them is work.
        /// </summary>
        private static SerializedProperty Broken(SerializedObject serialized, CrossReference reference)
        {
            var property = serialized.FindProperty(reference.PropertyPath);
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
            {
                reference.Trouble = $"'{reference.PropertyPath}' is no longer a field on " +
                                    reference.ReferrerType;
                return null;
            }

            if (property.objectReferenceValue != null)
            {
                // For a reference the hierarchy told us about, a field holding
                // something is the end of it: nothing broke.
                //
                // For one read off the file it proves nothing, because the file
                // and the loaded object can disagree. A component that fills its
                // own serialized fields when it loads — "if (!countText)
                // countText = GetComponentInChildren<…>()", which is an ordinary
                // thing for a project to write — hands back a value that was
                // never written down. Believe it and the field is marked settled
                // while the file still names a component that no longer exists,
                // and the file is what ships. Measured on a real project: two
                // prefabs went silent for exactly this reason while their
                // neighbours, whose scripts do not self-heal, were mended.
                if (!reference.Severed)
                {
                    reference.Settled = true;
                    return null;
                }
                return property;
            }

            reference.Broken = true;
            return property;
        }

        /// <summary>
        /// The same again for the assets that have no hierarchy to be opened in.
        ///
        /// A ScriptableObject is never a container, so nothing else in the
        /// migration ever visits one; this runs after every prefab and scene has
        /// been converted, which is the earliest moment every component it could
        /// name has a replacement.
        /// </summary>
        public int RelinkAssets(MigrationReport report)
        {
            int relinked = 0;
            foreach (var reference in _records)
            {
                if (reference.Settled || IsContainer(reference.Referrer)) continue;

                var owner = FindAsset(reference);
                if (owner == null)
                {
                    reference.Trouble = $"no {reference.ReferrerType} could be found in this asset";
                    continue;
                }

                var serialized = new SerializedObject(owner);
                var property = Broken(serialized, reference);
                if (property == null) continue;

                var made = FromAsset(reference);
                if (made == null)
                {
                    reference.Trouble = "the component that replaced it could not be found in " +
                                        reference.Target;
                    continue;
                }

                if (!Write(serialized, property, reference, made, report)) continue;
                EditorUtility.SetDirty(owner);
                relinked++;
            }

            report.Relinked += relinked;
            return relinked;
        }

        /// <summary>The assignment, the read-back, and whichever finding the answer was.</summary>
        private static bool Write(SerializedObject serialized, SerializedProperty property,
            CrossReference reference, Component made, MigrationReport report)
        {
            property.objectReferenceValue = made;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            serialized.Update();
            var written = serialized.FindProperty(reference.PropertyPath);
            reference.Settled = true;

            if (written != null && written.objectReferenceValue == made)
            {
                reference.Relinked = true;
                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Info,
                    Rule = Rule,
                    Message = $"'{reference.PropertyPath}' on {reference.ReferrerType} named the " +
                              $"{reference.TargetComponent} at '{reference.TargetPath}' in " +
                              $"{reference.Target}, which changed under it, and now names the " +
                              $"{made.GetType().Name} that replaced it. The field is in this file " +
                              "and the component is in that one, so nothing that happened while " +
                              "that file was open could have fixed it.",
                    Container = reference.Referrer,
                    Path = reference.ReferrerPath,
                    Component = reference.ReferrerType,
                });
                return true;
            }

            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Error,
                Rule = Rule,
                Message = $"'{reference.PropertyPath}' on {reference.ReferrerType} would not take " +
                          $"the {made.GetType().Name} that replaced the {reference.TargetComponent} " +
                          $"at '{reference.TargetPath}' in {reference.Target}: the field is still " +
                          "declared as a TextMesh Pro type. It reads None now, because that " +
                          "container was converted under it. Run the script rewrite in this tab, " +
                          "let Unity recompile, and convert again.",
                Container = reference.Referrer,
                Path = reference.ReferrerPath,
                Component = reference.ReferrerType,
            });
            return false;
        }

        /// <summary>
        /// Takes back the re-points made in a container whose save then failed.
        ///
        /// Same arithmetic as the swap count, for the same reason: the writes
        /// happened in memory and the file on disk does not have them, so a
        /// report that counted them would be describing a project that does not
        /// exist. Putting the references back into the unsettled pile means the
        /// end of the run names them out loud instead.
        /// </summary>
        public int Unwind(string container)
        {
            if (!_byReferrer.TryGetValue(container ?? string.Empty, out var pending)) return 0;

            int taken = 0;
            foreach (var reference in pending)
            {
                if (!reference.Relinked) continue;
                reference.Relinked = false;
                reference.Settled = false;
                reference.Broken = true;
                reference.Trouble = "the file it lives in was written to in memory and then Unity " +
                                    "refused to save it";
                taken++;
            }
            return taken;
        }

        // ----------------------------------------------------------- resolution

        /// <summary>
        /// The object this one is a copy of, followed all the way down to the
        /// file that actually defines it.
        ///
        /// One hop is not enough and that was the bug.
        /// <c>GetCorrespondingObjectFromSource</c> answers with the
        /// <em>immediate</em> source, so a prefab holding an instance of a
        /// prefab that itself nests a third one gets back the middle file, not
        /// the one the component is written in. The middle file is not what
        /// converts: it holds no component of its own, so opening it swaps
        /// nothing and it is never written — and a reference recorded against it
        /// is waiting for a change that will never be announced, while the file
        /// that really did change goes unmentioned. Every hop until the answers
        /// stop moving lands on the file whose identifiers the reference is
        /// actually spelled in, which is the file whose conversion breaks it.
        ///
        /// The cap is there because this walks a chain Unity built and a cycle
        /// in it would be Unity's bug, not something worth hanging on.
        /// </summary>
        private static Component Origin(Component component)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            if (source == null) return null;

            for (int hop = 0; hop < 16; hop++)
            {
                var deeper = PrefabUtility.GetCorrespondingObjectFromSource(source);
                if (deeper == null || deeper == source) break;
                source = deeper;
            }
            return source;
        }

        /// <summary>
        /// Every GameObject in this hierarchy that came from another container,
        /// keyed by which one and where in it.
        ///
        /// This is the other half of the stable identity. The census recorded a
        /// target by asking the referenced component's GameObject where it came
        /// from; this asks every GameObject here the same question and builds the
        /// answer into a lookup, so the two halves agree by construction rather
        /// than by two pieces of code being written to match.
        /// </summary>
        private static Dictionary<string, GameObject> Instances(GameObject[] roots)
        {
            var found = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    var source = Origin(transform);
                    if (source == null) continue;

                    string owner = AssetDatabase.GetAssetPath(source);
                    if (string.IsNullOrEmpty(owner)) continue;

                    string key = Key(owner, ComponentMigration.PathOf(source.transform));
                    if (!found.ContainsKey(key)) found[key] = transform.gameObject;
                }
            }
            return found;
        }

        /// <summary>
        /// The component to point the field at, found the same way the reference
        /// reached it in the first place.
        ///
        /// Which way that was is recorded rather than guessed at. A field that
        /// named a component in an instance nested here gets the component on
        /// this hierarchy's own copy; one that named a component across files
        /// gets the one in the file. Trying the first and falling back to the
        /// second would quietly turn a reference to the object on screen into a
        /// reference to the asset it came from, which looks the same in the
        /// inspector and is not.
        /// </summary>
        private static Component Resolve(CrossReference reference,
            Dictionary<string, GameObject> instances)
        {
            if (!reference.Nested) return FromAsset(reference);
            return instances.TryGetValue(Key(reference.Target, reference.TargetPath), out var live)
                ? Made(live, reference.TargetKind)
                : null;
        }

        private static Component FromAsset(CrossReference reference)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(reference.Target);
            if (root == null) return null;

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                if (ComponentMigration.PathOf(transform) == reference.TargetPath)
                    return Made(transform.gameObject, reference.TargetKind);
            return null;
        }

        private static Component Made(GameObject go, MigrationKind kind)
        {
            if (go == null) return null;
            switch (kind)
            {
                case MigrationKind.InputField: return go.GetComponent<OneTextInputField>();
                case MigrationKind.Mesh: return go.GetComponent<OneTextMesh>();
                default: return go.GetComponent<OneTextLabel>();
            }
        }

        /// <summary>Where a component sits among the ones of its own type on its object.</summary>
        private static int IndexOf(Component component)
        {
            if (component == null) return 0;

            int index = 0;
            foreach (var sibling in component.gameObject.GetComponents<Component>())
            {
                if (sibling == component) return index;
                if (sibling != null && sibling.GetType() == component.GetType()) index++;
            }
            return 0;
        }

        private static Component FindComponent(GameObject[] roots, CrossReference reference)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (ComponentMigration.PathOf(transform) != reference.ReferrerPath) continue;

                    int index = 0;
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        if (component == null) continue;
                        if (component.GetType().Name != reference.ReferrerType) continue;
                        if (index == reference.ReferrerIndex) return component;
                        index++;
                    }
                }
            }
            return null;
        }

        private static UnityEngine.Object FindAsset(CrossReference reference)
        {
            var objects = AssetDatabase.LoadAllAssetsAtPath(reference.Referrer);
            if (objects == null) return null;

            foreach (var asset in objects)
            {
                if (asset == null || asset.GetType().Name != reference.ReferrerType) continue;
                // The name is the only thing telling two sub-assets of one type
                // apart, and it is what the census wrote down for exactly that.
                if (string.IsNullOrEmpty(reference.ReferrerPath) ||
                    asset.name == reference.ReferrerPath) return asset;
            }
            return null;
        }

        private static bool IsContainer(string path)
        {
            string extension = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension == ".prefab" || extension == ".unity";
        }

        private static string Key(string container, string path) => container + "|" + path;

        // ------------------------------------------------------------ reporting

        /// <summary>
        /// What a scan has to say: every one of these will break, and the
        /// migration can only mend the ones whose referring asset is in the same
        /// run.
        /// </summary>
        public void Report(MigrationReport report)
        {
            foreach (var reference in _records)
            {
                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Warning,
                    Rule = Rule,
                    Message = $"'{reference.PropertyPath}' on {reference.ReferrerType} names the " +
                              $"{reference.TargetComponent} at '{reference.TargetPath}' in " +
                              $"{reference.Target}. Converting that container gives the component a " +
                              "new identity in its own file, and this field is in a different file, " +
                              "so nothing that happens while that one is open can mend it. Convert " +
                              "both in the same run and it is re-pointed here; convert only that " +
                              "one and this field reads None.",
                    Container = reference.Referrer,
                    Path = reference.ReferrerPath,
                    Component = reference.ReferrerType,
                });
            }
        }

        /// <summary>
        /// What an apply has to say at the end: the references into a container
        /// that really did change, which nothing managed to re-point.
        ///
        /// Every one of these is a field that reads None in a project that
        /// otherwise migrated cleanly, which is the whole failure this module was
        /// written against. Reporting it is the floor; the re-point above is what
        /// we would rather do, and this is what is said when we could not.
        /// </summary>
        public void Settle(MigrationReport report)
        {
            foreach (var reference in _records)
            {
                if (reference.Settled) continue;

                // Two ways to know this one matters, and the first outranks the
                // second. Either the field was looked at and found empty, which
                // is a fact; or the field was never looked at because its asset
                // was not in the run, and the container it named was converted,
                // which is an inference — the best one available about a file
                // nobody opened.
                if (!reference.Broken && !_changed.Contains(reference.Target)) continue;

                report.Add(new MigrationFinding
                {
                    Severity = DoctorSeverity.Error,
                    Rule = Rule,
                    Message = $"'{reference.PropertyPath}' on {reference.ReferrerType} named the " +
                              $"{reference.TargetComponent} at '{reference.TargetPath}' in " +
                              $"{reference.Target} and reads None now" +
                              (reference.Broken
                                  ? ". It was not re-pointed because " +
                                    (string.IsNullOrEmpty(reference.Trouble)
                                        ? "the replacement could not be identified"
                                        : reference.Trouble) +
                                    ". Deal with that and convert again, or set the field by hand."
                                  : reference.Severed
                                      ? Severance(reference)
                                      : ", because that container was converted and the asset " +
                                        "holding this field was not part of this run. Include it " +
                                        "and convert again, or set the field by hand."),
                    Container = reference.Referrer,
                    Path = reference.ReferrerPath,
                    Component = reference.ReferrerType,
                });
            }
        }

        /// <summary>
        /// What to say about a reference that was gone before the census could
        /// see it. The two causes need different sentences because they need
        /// different things done about them.
        /// </summary>
        private static string Severance(CrossReference reference) =>
            reference.Unreachable
                ? ". The component holding it, " + reference.ReferrerType + ", has no script in " +
                  "this project, so nothing could read the field, let alone write to it. Restore " +
                  "the script and convert again, or set the field by hand afterwards."
                : ". The field had already been emptied before this run reached it: the script " +
                  "rewrite gave it a OneText type while the file still named a TextMesh Pro " +
                  "component, and Unity drops a pointer it cannot bind. It is named here because " +
                  "the file still says what it meant. Set it by hand, or undo the conversion of " +
                  reference.Target + " and convert both together.";

        // ---------------------------------------------------------------- cache

        /// <summary>
        /// The two questions asked of every object reference in a container,
        /// answered once per component rather than once per field.
        ///
        /// Both are cheap on their own and neither is cheap four thousand times:
        /// a hierarchy asks where its parent is on every component it holds, and
        /// a provider reading a text component to say what kind it is does real
        /// work. The cache lives for one container, because instance ids do not
        /// mean anything once it closes.
        /// </summary>
        private sealed class Cache
        {
            private readonly Dictionary<int, MigrationKind> _kinds =
                new Dictionary<int, MigrationKind>();

            private readonly Dictionary<int, string[]> _identities =
                new Dictionary<int, string[]>();

            /// <summary>
            /// Which container a component really belongs to and where in it.
            ///
            /// Two shapes reach the same answer. A component sitting in a nested
            /// prefab instance is alive in this hierarchy and has no asset path
            /// of its own, so the question goes through
            /// <c>GetCorrespondingObjectFromSource</c> to the object it was
            /// copied from. A component named directly across files — what a
            /// ScriptableObject holds — is already the asset object, and its own
            /// path is the answer.
            /// </summary>
            public bool Identify(Component component, out string container, out string path,
                out bool nested)
            {
                container = null;
                path = null;
                nested = false;
                if (component == null) return false;

                int id = component.GetInstanceID();
                if (_identities.TryGetValue(id, out var known))
                {
                    if (known == null) return false;
                    container = known[0];
                    path = known[1];
                    nested = known[2] != null;
                    return true;
                }

                var source = component;
                string owner = AssetDatabase.GetAssetPath(component);
                if (string.IsNullOrEmpty(owner))
                {
                    source = Origin(component);
                    owner = source == null ? null : AssetDatabase.GetAssetPath(source);
                    nested = true;
                }

                if (string.IsNullOrEmpty(owner) || source == null)
                {
                    _identities[id] = null;
                    return false;
                }

                container = owner;
                path = ComponentMigration.PathOf(source.transform);
                _identities[id] = new[] { container, path, nested ? "nested" : null };
                return true;
            }

            public MigrationKind KindOf(Component component)
            {
                if (component == null) return MigrationKind.None;

                int id = component.GetInstanceID();
                if (_kinds.TryGetValue(id, out var known)) return known;

                var kind = MigrationKind.None;
                foreach (var provider in MigrationProviders.All)
                {
                    MigrationTarget target;
                    try { target = provider.Inspect(component, string.Empty, string.Empty); }
                    catch (Exception) { continue; }
                    if (target == null) continue;
                    kind = target.Kind;
                    break;
                }

                _kinds[id] = kind;
                return kind;
            }
        }
    }
}
