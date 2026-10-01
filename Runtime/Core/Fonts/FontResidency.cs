using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Which fonts are in memory, and the means to put them there and take
    /// them out again.
    ///
    /// <para><b>The problem.</b> A localised game lists a fallback font per
    /// language in the project settings, and the settings asset is loaded at
    /// startup, so every font it references is loaded with it: a player
    /// reading Korean carries the Chinese and Japanese faces too, at about
    /// sixteen megabytes each once unpacked, plus the packed copy until the
    /// first label unpacks it. Nothing ever lets go of them.</para>
    ///
    /// <para><b>The answer</b> is to name those fonts by key
    /// (<see cref="OneTextSettings.OnDemandFonts"/>) rather than by reference,
    /// and to load them through an <see cref="IFontSource"/> only while
    /// something wants them. Three things can want one:</para>
    /// <list type="bullet">
    /// <item><see cref="Acquire"/> / <see cref="Release"/>: explicit and
    /// reference-counted, for code that knows what it is about to show.</item>
    /// <item><see cref="SetLanguages"/>: the fonts declared for the languages
    /// the game is in. Switching language loads the new ones and lets go of the
    /// old.</item>
    /// <item>A label meeting a character none of the loaded fonts draws, which a
    /// font's recorded coverage says an unloaded one does: it is loaded on the
    /// spot (<see cref="LoadOnDemand"/>), so a Chinese nickname in a Korean game
    /// is drawn in the project's own face instead of the device's.</item>
    /// </list>
    ///
    /// <para><b>Letting go</b> is two steps a frame apart, because a label
    /// holds the faces it last laid out with. When nothing wants a font any
    /// more it leaves the fallback chain, every label is told to lay out again,
    /// and a label that still needs it takes it back through the on-demand path
    /// above; whatever no label took back is unloaded on the following frame:
    /// its face destroyed, its unpacked bytes dropped, its atlas tiles freed and
    /// the asset handed back to the source. <see cref="Trim"/> runs the same
    /// sweep over fonts that were loaded on demand, so a screen that showed a
    /// Japanese word once does not keep the Japanese face for the session.
    /// <see cref="Collect"/> does it all now, for tests and teardown.</para>
    ///
    /// <para>Faces the operating system lent (<see cref="SystemFonts"/>) are
    /// in it too. They are never wanted by key or by language, only by text,
    /// so <see cref="Trim"/> offers every one of them up the same way: the
    /// labels lay out again, a label still drawing a nickname in Thai takes
    /// its face back, and the rest are destroyed on the following frame with
    /// their atlas tiles. The answers stay, so the next Thai nickname maps the
    /// file again without probing.</para>
    ///
    /// <para>Fonts referenced directly — the default font, the settings'
    /// fallback list, a label's Font field — are outside all of this: they are
    /// loaded with whatever references them, as before.</para>
    ///
    /// <para>Main thread only, like the label lifecycle that drives it.</para>
    /// </summary>
    public static class FontResidency
    {
        private sealed class Entry
        {
            public string Key;
            public OneFontAsset Asset;
            public int Refs;
            public bool Required;
            public bool Candidate;
            public bool Loading;
            public bool Demanded;
            public int Order;
            public List<Action<OneFontAsset>> Waiters;
        }

        private static readonly Dictionary<string, Entry> s_entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly List<Entry> s_chain = new List<Entry>();
        private static readonly List<OneFontAsset> s_resident = new List<OneFontAsset>();
        private static readonly List<string> s_languages = new List<string>();
        private static readonly HashSet<string> s_missing = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<OneFontSourceKind, Func<IFontSource>> s_factories =
            new Dictionary<OneFontSourceKind, Func<IFontSource>>();

        private static IFontSource s_source;
        private static bool s_sourceExplicit;
        private static bool? s_loadOnDemand;
        private static int s_nextOrder = 1 << 20;
        private static bool s_changePending;
        private static bool s_sweepPending;
        private static int s_tick;
        private static int s_notifiedTick;

        /// <summary>
        /// Raised, on a later frame and never inside a canvas rebuild, when fonts
        /// joined or left the chain in a way text already on screen should be
        /// laid out again for. The canvas label subscribes; a component that
        /// keeps its own font stack can too.
        /// </summary>
        public static event Action Changed;

        /// <summary>
        /// Where fonts are loaded from. Unset, it is whichever source the project
        /// settings name; the Addressables one exists only in a project that has
        /// the Addressables package. Setting it lets go of nothing already loaded:
        /// each font goes back to the source that gave it.
        /// </summary>
        public static IFontSource Source
        {
            get
            {
                if (s_source != null) return s_source;
                var settings = OneTextSettings.Instance;
                var kind = settings != null ? settings.FontSource : OneFontSourceKind.Resources;
                if (kind != OneFontSourceKind.Resources &&
                    s_factories.TryGetValue(kind, out var factory) && factory != null)
                    s_source = factory();
                if (s_source == null)
                {
                    if (kind != OneFontSourceKind.Resources)
                        Debug.LogWarning($"OneText: the project settings load fonts through {kind}, " +
                            "but that source is not in this build (is the package installed?). " +
                            "Loading from Resources instead.");
                    s_source = new ResourcesFontSource();
                }
                return s_source;
            }
            set
            {
                s_source = value;
                s_sourceExplicit = value != null;
            }
        }

        /// <summary>
        /// Makes a built-in source kind available. The optional integration
        /// assemblies call this at startup; nothing else needs to.
        /// </summary>
        public static void RegisterSource(OneFontSourceKind kind, Func<IFontSource> factory)
        {
            s_factories[kind] = factory;
            if (!s_sourceExplicit) s_source = null;
        }

        /// <summary>
        /// Whether a label may load an on-demand font for a character nothing
        /// loaded draws. Unset, the project setting decides (on by default).
        /// </summary>
        public static bool LoadOnDemand
        {
            get
            {
                if (s_loadOnDemand.HasValue) return s_loadOnDemand.Value;
                var settings = OneTextSettings.Instance;
                return settings == null || settings.LoadOnDemand;
            }
            set => s_loadOnDemand = value;
        }

        /// <summary>
        /// The fonts this class has loaded, in fallback order: the order the
        /// settings list them, then anything acquired by a key the settings do
        /// not name, in the order it arrived. Labels add these after the
        /// settings' own fallback list.
        /// </summary>
        public static IReadOnlyList<OneFontAsset> Resident => s_resident;

        /// <summary>The languages last passed to <see cref="SetLanguages"/>.</summary>
        public static IReadOnlyList<string> Languages => s_languages;

        /// <summary>Whether the font behind <paramref name="key"/> is in the chain right now.</summary>
        public static bool IsResident(string key) =>
            key != null && s_entries.TryGetValue(key, out var entry) && entry.Asset != null &&
            !entry.Candidate;

        /// <summary>Bytes the resident fonts hold: packed and unpacked font files.</summary>
        public static long ResidentBytes
        {
            get
            {
                long total = 0;
                foreach (var asset in s_resident) total += asset != null ? asset.ResidentBytes : 0;
                return total;
            }
        }

        // ------------------------------------------------------------ explicit

        /// <summary>
        /// Loads the font behind <paramref name="key"/> now, if it is not in
        /// memory already, and holds it until a matching <see cref="Release"/>.
        /// Null when the source has nothing under that key.
        /// </summary>
        public static OneFontAsset Acquire(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var entry = EntryFor(key);
            entry.Refs++;
            LoadNow(entry, notify: true);
            return entry.Asset;
        }

        /// <summary>
        /// Same, without blocking: <paramref name="done"/> gets the asset, or
        /// null, once it is in. Owed one <see cref="Release"/> either way.
        /// </summary>
        public static void AcquireAsync(string key, Action<OneFontAsset> done)
        {
            if (string.IsNullOrEmpty(key))
            {
                done?.Invoke(null);
                return;
            }
            var entry = EntryFor(key);
            entry.Refs++;
            if (entry.Asset != null)
            {
                if (entry.Candidate) Revive(entry, notify: true);
                done?.Invoke(entry.Asset);
                return;
            }
            if (done != null) (entry.Waiters ??= new List<Action<OneFontAsset>>()).Add(done);
            if (entry.Loading) return;
            entry.Loading = true;
            Source.Load(key, asset => Loaded(entry, asset));
        }

        /// <summary>
        /// Gives back one <see cref="Acquire"/>. When nothing else wants the
        /// font it leaves the chain at once and is unloaded a frame later,
        /// unless text on screen takes it back first.
        /// </summary>
        public static void Release(string key)
        {
            if (string.IsNullOrEmpty(key) || !s_entries.TryGetValue(key, out var entry)) return;
            if (entry.Refs <= 0)
            {
                Debug.LogWarning($"OneText: FontResidency.Release(\"{key}\") without a matching Acquire.");
                return;
            }
            entry.Refs--;
            if (!Wanted(entry)) Retire(entry);
        }

        // ------------------------------------------------------------ languages

        /// <summary>
        /// The languages the game is showing now. The on-demand fonts declared
        /// for them are loaded, synchronously; the ones declared for other
        /// languages are let go of, and so is everything that was only loaded on
        /// demand (<see cref="Trim"/>). Tags match on the primary subtag, the way
        /// the font stack matches them: <c>zh</c> serves <c>zh-Hans</c>.
        /// </summary>
        public static void SetLanguages(params string[] languages)
        {
            s_languages.Clear();
            if (languages != null)
                foreach (var language in languages)
                    if (!string.IsNullOrEmpty(language)) s_languages.Add(language);

            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references != null)
            {
                for (int i = 0; i < references.Count; i++)
                {
                    var reference = references[i];
                    if (string.IsNullOrEmpty(reference.Key)) continue;
                    bool wanted = false;
                    foreach (var language in s_languages)
                        if (reference.Serves(language)) { wanted = true; break; }

                    var entry = EntryFor(reference.Key);
                    if (wanted)
                    {
                        entry.Required = true;
                        LoadNow(entry, notify: true);
                    }
                    else if (entry.Required)
                    {
                        entry.Required = false;
                    }
                }
            }
            Trim();
        }

        /// <summary>
        /// Lets go of every font that is resident only because a label once
        /// needed it: not acquired, not required by a language, and every face
        /// the operating system lent. A label that still shows one of its
        /// characters takes it back on its next layout, so what is on screen
        /// keeps drawing; the rest is unloaded next frame. A good thing to call
        /// on a scene change.
        /// </summary>
        public static void Trim()
        {
            foreach (var entry in s_entries.Values)
                if (entry.Asset != null && !entry.Candidate && !Wanted(entry)) Retire(entry);

            if (SystemFonts.BeginRelease())
            {
                s_sweepPending = true;
                MarkChanged(notify: true);
            }
        }

        /// <summary>
        /// Unloads now whatever is waiting to be, without giving text on screen
        /// a frame to take it back. Text that still needs a font loads it again
        /// on its next layout. For tests, teardown and memory warnings.
        /// </summary>
        public static void Collect()
        {
            if (s_changePending)
            {
                s_changePending = false;
                Changed?.Invoke();
            }
            Sweep();
        }

        // ------------------------------------------------------------ on demand

        /// <summary>
        /// The on-demand font for a character no font in a stack has, loading it
        /// if it has to, or null. Called by <see cref="FontStack"/> after its own
        /// fonts have all missed and before the operating system is asked.
        /// </summary>
        internal static FontData ResolveOnDemand(int codepoint, string language) =>
            ResolveOnDemand(codepoint, language, Want.Any);

        /// <summary>
        /// Which on-demand fonts a stack is asking among. <see cref="Want.Any"/>
        /// is the plain miss; the other two are for the cases where a font
        /// that is merely loaded is not good enough: a Han or kana character in
        /// a label that says its language, which a resident font of another
        /// language would draw in the wrong shape, and a character asked for
        /// in its emoji presentation, which a text face would draw in black.
        /// </summary>
        internal enum Want
        {
            Any,
            Language,
            Color,
        }

        internal static FontData ResolveOnDemand(int codepoint, string language, Want want)
        {
            if (!LoadOnDemand) return null;
            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references == null || references.Count == 0) return null;
            if (want == Want.Language && string.IsNullOrEmpty(language)) return null;

            int pick = -1;
            // The label's language first, for the same reason the stack prefers
            // it: 直 has a Japanese and a Chinese shape, and the first font in
            // the list to cover it is not necessarily the one the reader expects.
            if (!string.IsNullOrEmpty(language))
            {
                for (int i = 0; i < references.Count && pick < 0; i++)
                    if (references[i].Serves(language) && Fits(references[i], codepoint, want)) pick = i;
            }
            if (want != Want.Language)
            {
                for (int i = 0; i < references.Count && pick < 0; i++)
                    if (Fits(references[i], codepoint, want)) pick = i;
            }
            if (pick < 0) return null;

            string key = references[pick].Key;
            if (string.IsNullOrEmpty(key) || s_missing.Contains(key)) return null;

            var entry = EntryFor(key);
            if (entry.Asset == null)
            {
                // Not notifying: this runs inside a layout pass, and the label
                // asking is the one that needs it. Others pick it up from the
                // chain the next time they lay out.
                LoadNow(entry, notify: false);
                if (entry.Asset == null) return null;
            }
            else if (entry.Candidate)
            {
                Revive(entry, notify: false);
            }
            entry.Demanded = true;
            var font = entry.Asset.Font;
            return font != null && font.IsValid ? font : null;
        }

        /// <summary>
        /// Whether <paramref name="face"/> is the face of a font this class
        /// loaded, and if so the language it was declared for (null when none
        /// was). Nothing is loaded to answer.
        /// </summary>
        internal static bool IsOnDemandFace(FontData face, out string language)
        {
            language = null;
            if (face == null || s_entries.Count == 0) return false;
            foreach (var entry in s_entries.Values)
            {
                var asset = entry.Asset;
                if (asset == null || !asset.IsLoaded || !ReferenceEquals(asset.Font, face)) continue;
                language = DeclaredLanguage(entry.Key) ?? asset.Language;
                return true;
            }
            return false;
        }

        private static string DeclaredLanguage(string key)
        {
            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references != null)
                for (int i = 0; i < references.Count; i++)
                    if (string.Equals(references[i].Key, key, StringComparison.Ordinal))
                        return references[i].Language;
            return null;
        }

        private static bool Fits(in OneFontReference reference, int codepoint, Want want) =>
            reference.Covers(codepoint) && (want != Want.Color || reference.IsColor);

        /// <summary>
        /// Whether an on-demand font in the project settings draws this
        /// character, loaded or not. Diagnostics ask it so that a character the
        /// build draws from a font it loads on demand is not reported as one
        /// nothing draws.
        /// </summary>
        public static bool CoversOnDemand(int codepoint)
        {
            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references == null) return false;
            for (int i = 0; i < references.Count; i++)
                if (references[i].Covers(codepoint)) return true;
            return false;
        }

        // ------------------------------------------------------------ internals

        private static bool Wanted(Entry entry) => entry.Refs > 0 || entry.Required;

        private static Entry EntryFor(string key)
        {
            if (s_entries.TryGetValue(key, out var entry)) return entry;
            entry = new Entry { Key = key, Order = OrderOf(key) };
            s_entries[key] = entry;
            return entry;
        }

        private static int OrderOf(string key)
        {
            var settings = OneTextSettings.Instance;
            var references = settings != null ? settings.OnDemandFonts : null;
            if (references != null)
                for (int i = 0; i < references.Count; i++)
                    if (string.Equals(references[i].Key, key, StringComparison.Ordinal)) return i;
            return s_nextOrder++;
        }

        private static void LoadNow(Entry entry, bool notify)
        {
            if (entry.Asset != null)
            {
                if (entry.Candidate) Revive(entry, notify);
                return;
            }
            if (s_missing.Contains(entry.Key)) return;

            var asset = Source.LoadNow(entry.Key);
            if (asset == null)
            {
                s_missing.Add(entry.Key);
                Debug.LogWarning($"OneText: no font asset under \"{entry.Key}\" in {Source.GetType().Name}. " +
                    "Check the key in Project Settings > OneText > On-demand fonts.");
                return;
            }
            Admit(entry, asset, notify);
        }

        private static void Loaded(Entry entry, OneFontAsset asset)
        {
            entry.Loading = false;
            if (asset != null)
            {
                // A synchronous load got there first; this second copy is the
                // source's to take back, or an Addressables handle leaks.
                if (entry.Asset != null) Source.Release(entry.Key, asset);
                else Admit(entry, asset, notify: true);
            }
            else
            {
                s_missing.Add(entry.Key);
            }

            var waiters = entry.Waiters;
            entry.Waiters = null;
            if (waiters != null)
                foreach (var waiter in waiters) waiter?.Invoke(entry.Asset);

            // Everyone who asked may have released while it was loading.
            if (entry.Asset != null && !Wanted(entry) && !entry.Demanded) Retire(entry);
        }

        private static void Admit(Entry entry, OneFontAsset asset, bool notify)
        {
            entry.Asset = asset;
            entry.Candidate = false;
            Insert(entry);
            MarkChanged(notify);
        }

        private static void Revive(Entry entry, bool notify)
        {
            entry.Candidate = false;
            Insert(entry);
            MarkChanged(notify);
        }

        /// <summary>Out of the chain now; unloaded by the sweep unless revived first.</summary>
        private static void Retire(Entry entry)
        {
            if (entry.Asset == null || entry.Candidate) return;
            entry.Candidate = true;
            entry.Demanded = false;
            s_chain.Remove(entry);
            RebuildResident();
            s_sweepPending = true;
            MarkChanged(notify: true);
        }

        private static void Insert(Entry entry)
        {
            if (s_chain.Contains(entry)) return;
            int at = 0;
            while (at < s_chain.Count && s_chain[at].Order <= entry.Order) at++;
            s_chain.Insert(at, entry);
            RebuildResident();
        }

        private static void RebuildResident()
        {
            s_resident.Clear();
            foreach (var entry in s_chain) s_resident.Add(entry.Asset);
        }

        private static void MarkChanged(bool notify)
        {
            // Every stack built against the old chain is stale from here, which
            // the labels notice on their next layout without being told.
            OneTextSettings.NotifyFontsChanged();
            if (!notify) return;
            s_changePending = true;
            Driver.Ensure();
        }

        private static void Sweep()
        {
            s_sweepPending = false;
            bool any = false;
            foreach (var entry in s_entries.Values)
            {
                if (!entry.Candidate || entry.Asset == null) continue;
                var asset = entry.Asset;
                entry.Asset = null;
                entry.Candidate = false;
                entry.Demanded = false;
                any = true;

                if (asset != null)
                {
                    asset.ForEachLoadedFace(SharedGlyphAtlas.Forget);
                    asset.Unload();
                }
                Source.Release(entry.Key, asset);
            }
            // The system faces no layout took back since Trim marked them.
            if (SystemFonts.FinishRelease() > 0) any = true;
            if (any) OneTextSettings.NotifyFontsChanged();
        }

        /// <summary>
        /// Advances the two-step unload. Driven once a frame, after the frame's
        /// scripts and before its canvases rebuild; see <see cref="FontResidency"/>.
        /// </summary>
        internal static void Tick()
        {
            s_tick++;
            if (s_changePending)
            {
                // Labels hear about it now and lay out in this frame's canvas
                // pass; the sweep waits for the next tick so that pass can
                // take back what is still on screen.
                s_changePending = false;
                s_notifiedTick = s_tick;
                Changed?.Invoke();
                return;
            }
            if (s_sweepPending && s_tick > s_notifiedTick) Sweep();
        }

        internal static bool HasPendingWork => s_changePending || s_sweepPending;

        /// <summary>
        /// One line per resident font, for a log: key, language, bytes, and why
        /// it is here.
        /// </summary>
        public static string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("resident=").Append(s_resident.Count)
                .Append(" bytes=").Append(ResidentBytes)
                .Append(" languages=[").Append(string.Join(",", s_languages)).Append(']');
            foreach (var entry in s_chain)
            {
                builder.Append(" | ").Append(entry.Key);
                if (entry.Asset != null && !string.IsNullOrEmpty(entry.Asset.Language))
                    builder.Append(" (").Append(entry.Asset.Language).Append(')');
                builder.Append(' ').Append(entry.Asset != null ? entry.Asset.ResidentBytes : 0).Append('B');
                if (entry.Refs > 0) builder.Append(" refs=").Append(entry.Refs);
                if (entry.Required) builder.Append(" language");
                if (entry.Demanded) builder.Append(" demand");
            }
            int waiting = 0;
            foreach (var entry in s_entries.Values) if (entry.Candidate) waiting++;
            if (waiting > 0) builder.Append(" | unloading=").Append(waiting);
            if (SystemFonts.LoadedFaceCount > 0)
                builder.Append(" | ").Append(SystemFonts.Describe());
            return builder.ToString();
        }

        /// <summary>
        /// Forgets everything that belongs to a play session. With Domain Reload
        /// off these statics outlive it, holding assets the editor has since
        /// unloaded.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession() => Reset();

        /// <summary>
        /// Unloads everything this class loaded and forgets it all, overrides
        /// included. For tests, which need each case to start from nothing.
        /// </summary>
        internal static void ResetForTests()
        {
            foreach (var entry in s_entries.Values)
                if (entry.Asset != null) entry.Candidate = true;
            Sweep();
            s_loadOnDemand = null;
            s_source = null;
            s_sourceExplicit = false;
            Reset();
        }

        private static void Reset()
        {
            s_entries.Clear();
            s_chain.Clear();
            s_resident.Clear();
            s_languages.Clear();
            s_missing.Clear();
            s_changePending = false;
            s_sweepPending = false;
            // A trim nobody finished belongs to the session that started it.
            SystemFonts.CancelRelease();
            s_nextOrder = 1 << 20;
            if (!s_sourceExplicit) s_source = null;
        }

        /// <summary>
        /// Ticks <see cref="FontResidency"/> once a frame: a hidden object in play
        /// mode, the editor update loop otherwise. Created the first time there is
        /// something to do, never before, so a project that never names a font by
        /// key never has one.
        /// </summary>
        private sealed class Driver : MonoBehaviour
        {
            private static Driver s_instance;
    #if UNITY_EDITOR
            private static bool s_editorDriving;
    #endif

            internal static void Ensure()
            {
    #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    if (s_editorDriving) return;
                    s_editorDriving = true;
                    UnityEditor.EditorApplication.update += EditorTick;
                    return;
                }
    #endif
                if (s_instance != null) return;
                var host = new GameObject("OneText Font Residency") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                s_instance = host.AddComponent<Driver>();
            }

    #if UNITY_EDITOR
            private static void EditorTick()
            {
                if (Application.isPlaying) return;
                Tick();
            }
    #endif

            private void LateUpdate() => Tick();

            private void OnDestroy()
            {
                if (s_instance == this) s_instance = null;
            }
        }
    }
}
