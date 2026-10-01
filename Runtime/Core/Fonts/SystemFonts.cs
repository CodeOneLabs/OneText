using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The last tier of fallback: a font the operating system has, for a
    /// character the project's own fonts do not cover.
    ///
    /// <para>A font stack answers first: the label's font, its fallbacks, then
    /// the project chain. Only when every one of them has missed does this get
    /// asked, and then it goes looking on disk: the platform's font
    /// directories, a short preference list for the script the character
    /// belongs to, and a scan of everything else if that misses. The face it
    /// finds is loaded through the same <see cref="FontData"/> path as any
    /// bundled font and joins the run exactly like any other fallback; the
    /// itemizer already splits runs per font, so shaping, vertical writing,
    /// the precise atlas and decorations see nothing new.</para>
    ///
    /// <para><b>Why it is on by default, and why Doctor still complains.</b> A
    /// box on screen is the worst possible outcome for a reader, and a machine
    /// that has the character should draw it. But the character was drawn by a
    /// font that is on <em>this</em> machine: another player's device may have
    /// a different face, an older one, or none. So the renderer takes the OS
    /// font and Doctor reports every character that needed one, as a warning;
    /// the build works, and it is not portable, and both of those are
    /// true.</para>
    ///
    /// <para><b>Cost.</b> Nothing happens until a character misses. The first
    /// miss lists the platform's font directories and reads the `cmap` table of
    /// the candidates (file reads of a few kilobytes each, not font parses);
    /// see <see cref="SystemFontIndex"/>. Every answer, including "nothing on
    /// this machine has it", is remembered process-wide, so the second
    /// occurrence of a character costs a dictionary lookup and allocates
    /// nothing.</para>
    ///
    /// <para><b>Memory.</b> The face that answers is loaded by mapping its
    /// file (<see cref="FontData.LoadFile"/>), not by reading it: a system
    /// font is a 55 MB Korean collection or a 192 MB emoji one, and what a
    /// nickname needs of it is a few hundred kilobytes of tables and glyphs.
    /// Those pages are file-backed and off the managed heap. And a face is
    /// held only while text uses it: <see cref="FontResidency.Trim"/> lets go
    /// of every system face nothing on screen drew with, by the same two
    /// steps it lets go of on-demand fonts (labels lay out again, then what no
    /// label took back is destroyed and its atlas tiles freed). The answers
    /// stay — which face drew which character, which files answered for a
    /// script, and which characters nothing has — so meeting the text again
    /// maps the file again and probes nothing.</para>
    ///
    /// <para><b>Web.</b> A browser has no font directory to walk, so on Web the
    /// tier finds nothing and a missing character stays tofu. That is a
    /// platform fact rather than a decision, and it is recorded in
    /// <c>Docs/NATIVES.md</c>.</para>
    ///
    /// <para><b>Colour.</b> A system face that carries CBDT, sbix or COLRv0
    /// goes through the same colour path as a bundled one; nothing here knows
    /// the difference. HarfBuzz hands over the PNG of an sbix strike the way it
    /// does a CBDT one, so Apple Color Emoji draws in colour on macOS.</para>
    /// </summary>
    public static class SystemFonts
    {
        private static readonly object s_sync = new object();

        /// <summary>
        /// One face of one file the tier has used: where it is, what it is
        /// called, and the loaded face while something wants it. The slot
        /// outlives the face. Letting go of a face nulls <see cref="Font"/>
        /// and keeps the rest, so every answer that pointed here still does,
        /// and the next character that needs it maps the file again.
        /// </summary>
        private sealed class SystemFace
        {
            public string Path;
            public uint FaceIndex;
            public string Name;
            public FontData Font;

            /// <summary>Could not be loaded (missing, unreadable, not a font). Not retried.</summary>
            public bool Failed;

            /// <summary>
            /// Trimmed: destroyed at the next sweep unless a layout asks for
            /// it first, which clears this. The two-step unload of
            /// <see cref="FontResidency"/>, for system faces.
            /// </summary>
            public bool Releasing;

            /// <summary>How many times this face has been loaded, reloads included.</summary>
            public int Loads;

            public bool IsLive => Font != null && Font.IsValid;
        }

        // Codepoint -> the face that draws it, or null for "asked, nothing
        // has it". Negative answers are cached too: without that, a string
        // full of one unrenderable character would rescan the disk per
        // occurrence. Slots, not faces: an answer survives its face being
        // let go of.
        private static readonly Dictionary<int, SystemFace> s_resolved = new Dictionary<int, SystemFace>();

        // Every face the tier has looked at, keyed "path#faceIndex", so two
        // characters found in one font share one load and one set of atlas
        // tiles.
        private static readonly Dictionary<string, SystemFace> s_faces =
            new Dictionary<string, SystemFace>(StringComparer.Ordinal);

        // Live faces by FontData.CacheId, what IsSystemFont and a diagnostic
        // ask. Keyed by cache id rather than by the native pointer for the
        // reason ColorGlyphs is: a freed face's address comes straight back.
        private static readonly Dictionary<int, SystemFace> s_live = new Dictionary<int, SystemFace>();

        /// <summary>
        /// Which files have answered for a script, most recent first.
        ///
        /// The preference list below is a guess made from file names, and it is
        /// a good one; this is the answer, which is better. The first Hangul
        /// syllable a session meets costs a walk, and every syllable after it
        /// asks the face that answered — one call instead of up to several
        /// hundred. That is the shape of the cost too: unseen characters arrive
        /// in floods, one script at a time, when a language changes or a screen
        /// full of new text opens.
        ///
        /// Promotion only, never exclusion. A face that has no glyph for this
        /// character may have one for the next in the same script, and a list
        /// that dropped it would answer worse for having learned.
        /// </summary>
        private static readonly Dictionary<int, List<string>> s_answered =
            new Dictionary<int, List<string>>();

        /// <summary>How many files a script remembers. Deep enough for a family and its fallbacks.</summary>
        private const int RememberedPerScript = 8;

        /// <summary>
        /// Whether probing remembers what answered. On, always, in a running
        /// game; off is for measuring what the memory is worth, which needs the
        /// same session to run both ways.
        /// </summary>
        public static bool RememberAnswers { get; set; } = true;

        private static bool? s_enabled;

        /// <summary>
        /// Whether a character no font in the chain covers may be drawn from an
        /// operating-system font.
        ///
        /// Unset, this follows the project's setting (Project Settings &gt;
        /// OneText), which is on. Setting it explicitly overrides that for the
        /// process, which is what a test does, and what a game that wants
        /// device-independent output can do at startup.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (s_enabled.HasValue) return s_enabled.Value;
                var settings = OneTextSettings.Instance;
                return settings == null || settings.SystemFontFallback;
            }
            set => s_enabled = value;
        }

        /// <summary>Drops an explicit <see cref="Enabled"/> override, back to the project setting.</summary>
        public static void UseProjectSetting() => s_enabled = null;

        /// <summary>
        /// The system face that draws this character, or null when the tier is
        /// off or no font on this machine has it.
        ///
        /// The returned font is owned here and shared; never dispose it.
        /// </summary>
        public static FontData Resolve(int codepoint)
        {
            if (!Enabled) return null;
            lock (s_sync)
            {
                if (s_resolved.TryGetValue(codepoint, out var cached))
                {
                    if (cached == null) return null;
                    // The face may have been let go of since it answered; this
                    // brings it back, and takes it off the list of faces about
                    // to be let go of, because a layout is using it.
                    var live = Use(cached);
                    if (live != null) return live;
                    // Its file is gone. Ask again rather than lose the
                    // character: something else on the machine may draw it.
                    s_resolved.Remove(codepoint);
                }

                SystemFace found = null;
                try { found = Probe(codepoint); }
                catch (Exception e)
                {
                    // A fallback tier that throws would turn a missing glyph
                    // into a missing label. Whatever went wrong on this
                    // machine's disk, the answer is "no system font".
                    Debug.LogWarning($"OneText: system font fallback failed for U+{codepoint:X4}: {e.Message}");
                }
                s_resolved[codepoint] = found;
                return found?.Font;
            }
        }

        // (codepoint, CJK locale) -> the face made for that locale that draws
        // it, or null for "this machine has none". Kept apart from s_resolved:
        // the language-neutral answer is still the right one for a label that
        // names no language.
        private static readonly Dictionary<long, SystemFace> s_resolvedForLanguage =
            new Dictionary<long, SystemFace>();

        /// <summary>
        /// The system face made for <paramref name="language"/> that draws this
        /// character, for the characters whose shape depends on the reader; or
        /// null when the language has no opinion, the character is not one of
        /// those, or this machine has no face for that language.
        ///
        /// <para>Han, kana and CJK punctuation are one code point each and
        /// several designs: 直 and 骨 are drawn differently for a Japanese and a
        /// Chinese reader, and a Chinese full-width comma sits in the middle of
        /// its em where a Japanese one sits at the bottom left. The neutral
        /// <see cref="Resolve(int)"/> picks one face for every reader; this
        /// looks only among the faces made for this one (Hiragino and Yu Gothic
        /// for <c>ja</c>, PingFang SC and Hiragino Sans GB for <c>zh-Hans</c>,
        /// Heiti TC and Microsoft JhengHei for <c>zh-Hant</c>, Apple SD Gothic
        /// Neo and Malgun Gothic for <c>ko</c>).</para>
        ///
        /// <para>For <c>ko</c>, punctuation only. Han in a Korean label is
        /// nearly always somebody's Chinese or Japanese name, and the Hanja a
        /// Korean face happens to carry would split that name between two
        /// fonts.</para>
        /// </summary>
        public static FontData ResolveForLanguage(int codepoint, string language)
        {
            if (!Enabled) return null;
            var locale = LocaleOf(language);
            if (locale == CjkLocale.None || !FontStack.IsReaderDependent(codepoint)) return null;
            bool punctuation = FontStack.IsCjkPunctuation(codepoint);
            if (locale == CjkLocale.Korean && !punctuation) return null;

            long key = (uint)codepoint | (long)locale << 32;
            lock (s_sync)
            {
                if (s_resolvedForLanguage.TryGetValue(key, out var cached))
                {
                    if (cached == null) return null;
                    var live = Use(cached);
                    if (live != null) return live;
                    s_resolvedForLanguage.Remove(key);
                }

                SystemFace found = null;
                try { found = ProbeForLanguage(codepoint, locale); }
                catch (Exception e)
                {
                    Debug.LogWarning($"OneText: system font fallback failed for U+{codepoint:X4} ({language}): {e.Message}");
                }
                s_resolvedForLanguage[key] = found;
                return found?.Font;
            }
        }

        /// <summary>The CJK reading a language tag asks for, by its primary subtag and script.</summary>
        internal enum CjkLocale
        {
            None,
            Japanese,
            SimplifiedChinese,
            TraditionalChinese,
            Korean,
        }

        internal static CjkLocale LocaleOf(string language)
        {
            if (string.IsNullOrEmpty(language) || language.Length < 2) return CjkLocale.None;
            string tag = language.Replace('_', '-');
            if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) && (tag.Length == 2 || tag[2] == '-'))
                return CjkLocale.Japanese;
            if (tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase) && (tag.Length == 2 || tag[2] == '-'))
                return CjkLocale.Korean;
            if (!tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || tag.Length > 2 && tag[2] != '-')
                return CjkLocale.None;
            // zh-Hant, zh-TW, zh-HK, zh-MO read traditional; everything else simplified.
            foreach (string part in tag.Split('-'))
            {
                if (part.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("MO", StringComparison.OrdinalIgnoreCase))
                    return CjkLocale.TraditionalChinese;
            }
            return CjkLocale.SimplifiedChinese;
        }

        /// <summary>True if this face came from the operating system rather than the project.</summary>
        public static bool IsSystemFont(FontData font)
        {
            if (font == null) return false;
            lock (s_sync) return s_live.ContainsKey(font.CacheId);
        }

        /// <summary>
        /// The family name of a face this class supplied ("Apple SD Gothic
        /// Neo", "Segoe UI Emoji"), or null for a font it did not supply or
        /// has since let go of.
        /// </summary>
        public static string NameOf(FontData font)
        {
            if (font == null) return null;
            lock (s_sync) return s_live.TryGetValue(font.CacheId, out var face) ? face.Name : null;
        }

        /// <summary>
        /// The name of the system font that would draw this character, or null.
        /// What a diagnostic asks: it wants the name, not the face.
        /// </summary>
        public static string NameFor(int codepoint) => NameOf(Resolve(codepoint));

        /// <summary>System faces loaded right now. Diagnostics and tests.</summary>
        public static int LoadedFaceCount
        {
            get { lock (s_sync) return s_live.Count; }
        }

        /// <summary>
        /// Bumped whenever a system face is let go of. A <see cref="FontStack"/>
        /// remembers the system's answers per character, and a face in that
        /// memory may since have been destroyed; the stack compares this with
        /// the value it remembered against and asks again when they differ.
        /// </summary>
        public static int Generation { get; private set; }

        /// <summary>
        /// Managed heap the loaded system faces hold: zero when every one is
        /// mapped, the whole file of each one that is not (Web).
        /// </summary>
        public static long ManagedBytes
        {
            get
            {
                lock (s_sync)
                {
                    long total = 0;
                    foreach (var face in s_live.Values) total += face.Font.ManagedBytes;
                    return total;
                }
            }
        }

        /// <summary>
        /// Address space the loaded system faces have mapped: the sum of their
        /// files' lengths. Reserved, not resident; see <see cref="ResidentFileBytes"/>.
        /// </summary>
        public static long MappedBytes
        {
            get
            {
                lock (s_sync)
                {
                    long total = 0;
                    foreach (var face in s_live.Values) total += face.Font.MappedBytes;
                    return total;
                }
            }
        }

        /// <summary>
        /// How much of the mapped files is in physical memory, or -1 where the
        /// platform will not say. Asks the kernel per page; a diagnostic.
        /// </summary>
        public static long ResidentFileBytes
        {
            get
            {
                lock (s_sync)
                {
                    long total = 0;
                    foreach (var face in s_live.Values)
                    {
                        if (!face.Font.IsMapped) continue;
                        long resident = face.Font.ResidentFileBytes;
                        if (resident < 0) return -1;
                        total += resident;
                    }
                    return total;
                }
            }
        }

        /// <summary>
        /// One line per loaded system face, for a log: family, file and face
        /// index, how it is held (mapped: the file's length and how much of it
        /// is resident; managed: the array), how many times it has been
        /// loaded, and whether it is about to be let go of.
        /// </summary>
        public static string Describe()
        {
            lock (s_sync)
            {
                var builder = new System.Text.StringBuilder();
                builder.Append("system faces=").Append(s_live.Count)
                    .Append(" managed=").Append(Megabytes(ManagedBytes))
                    .Append(" mapped=").Append(Megabytes(MappedBytes));
                long resident = ResidentFileBytes;
                builder.Append(" resident=").Append(resident < 0 ? "?" : Megabytes(resident))
                    .Append(" known=").Append(s_faces.Count)
                    .Append(" answers=").Append(s_resolved.Count);
                foreach (var face in s_faces.Values)
                {
                    if (!face.IsLive) continue;
                    var font = face.Font;
                    builder.Append(" | ").Append(face.Name).Append(" (")
                        .Append(System.IO.Path.GetFileName(face.Path)).Append('#').Append(face.FaceIndex).Append(") ");
                    if (font.IsMapped)
                    {
                        long faceResident = font.ResidentFileBytes;
                        builder.Append("mapped ").Append(Megabytes(font.MappedBytes)).Append(" resident ")
                            .Append(faceResident < 0 ? "?" : Megabytes(faceResident));
                    }
                    else
                    {
                        builder.Append("managed ").Append(Megabytes(font.ManagedBytes));
                    }
                    if (face.Loads > 1) builder.Append(" loads=").Append(face.Loads);
                    if (face.Releasing) builder.Append(" releasing");
                }
                return builder.ToString();
            }
        }

        private static string Megabytes(long bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "MB";

        /// <summary>
        /// Where this platform keeps its fonts. Empty on Web, which is the
        /// whole of why the tier does nothing there.
        /// </summary>
        public static IEnumerable<string> Directories() => SystemFontIndex.Directories();

        /// <summary>
        /// How many font files the platform offers. Asking builds the listing
        /// if it has not been built, which is the one cost this class has that
        /// a missing character has not already paid for.
        /// </summary>
        public static int FontFileCount
        {
            get { lock (s_sync) return SystemFontIndex.Files().Length; }
        }

        /// <summary>
        /// Drops every cached answer and destroys the loaded faces.
        ///
        /// Anything still holding a run shaped with a system face is stale
        /// afterwards, exactly as it would be if the font asset it came from
        /// had been unloaded, which is why this is for tests and for the
        /// editor's assembly reload, not for a running game. A running game
        /// lets go of faces through <see cref="FontResidency.Trim"/>, which
        /// tells the labels first and keeps the answers.
        /// </summary>
        public static void Forget()
        {
            lock (s_sync)
            {
                foreach (var face in s_faces.Values) face.Font?.Dispose();
                s_faces.Clear();
                s_resolved.Clear();
                s_resolvedForLanguage.Clear();
                s_live.Clear();
                s_answered.Clear();
                FilesProbed = 0;
                Generation++;
                SystemFontIndex.Forget();
            }
        }

        // ----------------------------------------------------------- letting go

        /// <summary>
        /// The first half of letting go: marks every loaded face as leaving.
        /// A layout that resolves a character to one of them in the meantime
        /// takes it back; <see cref="FinishRelease"/> destroys the rest. True
        /// when there was anything to mark. Called by <see cref="FontResidency.Trim"/>.
        /// </summary>
        internal static bool BeginRelease()
        {
            lock (s_sync)
            {
                bool any = false;
                foreach (var face in s_live.Values)
                {
                    face.Releasing = true;
                    any = true;
                }
                return any;
            }
        }

        /// <summary>Takes back a <see cref="BeginRelease"/> that nothing has finished.</summary>
        internal static void CancelRelease()
        {
            lock (s_sync)
                foreach (var face in s_faces.Values) face.Releasing = false;
        }

        /// <summary>
        /// The second half: destroys every face still marked as leaving, with
        /// its atlas tiles, colour included, and returns how many went. Their
        /// slots stay, so the characters they answered for still know where to
        /// look.
        /// </summary>
        internal static int FinishRelease()
        {
            List<FontData> leaving = null;
            lock (s_sync)
            {
                foreach (var face in s_faces.Values)
                {
                    if (!face.Releasing) continue;
                    face.Releasing = false;
                    if (!face.IsLive) continue;
                    (leaving ??= new List<FontData>()).Add(face.Font);
                    s_live.Remove(face.Font.CacheId);
                    face.Font = null;
                }
                if (leaving == null) return 0;
                Generation++;
            }
            // Outside the lock: the atlas is main-thread state with locks of
            // its own, and a face is only destroyed after its tiles are gone.
            foreach (var font in leaving)
            {
                SharedGlyphAtlas.Forget(font);
                font.Dispose();
            }
            return leaving.Count;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ForgetOnAssemblyReload() =>
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Forget;
#endif

        // --------------------------------------------------------------- probing

        private static SystemFace Probe(int codepoint)
        {
            var files = SystemFontIndex.Files();
            if (files.Length == 0) return null;

            // A short list of the faces that are actually likely, matched on
            // file name. It is not an optimisation for its own sake: on a
            // machine with three hundred fonts, several of them cover Han, and
            // "the first file alphabetically" is not an answer anybody would
            // choose. Preference is how 한 comes out of Apple SD Gothic Neo and
            // not out of a Serif face that happens to sort earlier.
            int script = ScriptOf(codepoint);
            var tried = new HashSet<string>(StringComparer.Ordinal);

            // What answered for this script already, before any guessing.
            if (RememberAnswers && s_answered.TryGetValue(script, out var answered))
            {
                for (int i = 0; i < answered.Count; i++)
                {
                    string path = answered[i];
                    if (!tried.Add(path)) continue;
                    var face = TryFile(path, codepoint);
                    if (face != null) { Remember(script, path); return face; }
                }
            }

            var stems = SystemFontIndex.Stems();
            foreach (string preferred in PreferredThenGeneric(codepoint))
            {
                for (int i = 0; i < files.Length; i++)
                {
                    if (!Matches(stems[i], preferred)) continue;
                    string path = files[i];
                    if (!tried.Add(path)) continue;
                    var face = TryFile(path, codepoint);
                    if (face != null) { Remember(script, path); return face; }
                }
            }

            foreach (string path in files)
            {
                if (!tried.Add(path)) continue;
                var face = TryFile(path, codepoint);
                if (face != null) { Remember(script, path); return face; }
            }
            return null;
        }

        /// <summary>
        /// The files made for this locale, in the order a reader of it would
        /// choose, and nothing else: no generic tail and no scan of the rest,
        /// because a face that merely covers the character is what the neutral
        /// answer already is.
        /// </summary>
        private static SystemFace ProbeForLanguage(int codepoint, CjkLocale locale)
        {
            var files = SystemFontIndex.Files();
            if (files.Length == 0) return null;
            int memory = 100 * (int)locale + ScriptOf(codepoint);
            var tried = new HashSet<string>(StringComparer.Ordinal);

            if (RememberAnswers && s_answered.TryGetValue(memory, out var answered))
            {
                for (int i = 0; i < answered.Count; i++)
                {
                    string path = answered[i];
                    if (!tried.Add(path)) continue;
                    var face = TryFile(path, codepoint, locale);
                    if (face != null) { Remember(memory, path); return face; }
                }
            }

            var stems = SystemFontIndex.Stems();
            foreach (string preferred in LocaleStems(locale))
            {
                for (int i = 0; i < files.Length; i++)
                {
                    if (!Matches(stems[i], preferred)) continue;
                    string path = files[i];
                    if (!tried.Add(path)) continue;
                    var face = TryFile(path, codepoint, locale);
                    if (face != null) { Remember(memory, path); return face; }
                }
            }
            return null;
        }

        /// <summary>
        /// File-name fragments of the faces made for a locale, on macOS,
        /// Windows, Linux and Android. The Japanese Hiragino files are named in
        /// Japanese on macOS, which is why the katakana is here.
        /// </summary>
        private static string[] LocaleStems(CjkLocale locale)
        {
            switch (locale)
            {
                case CjkLocale.Japanese:
                    return new[]
                    {
                        "ヒラギノ角ゴ", "HiraginoSans", "Hiragino Kaku", "YuGothic", "YuGoth", "meiryo", "msgothic",
                        "NotoSansCJKjp", "NotoSansJP", "NotoSansCJK", "DroidSansJapanese",
                    };
                case CjkLocale.SimplifiedChinese:
                    return new[]
                    {
                        "PingFang", "Hiragino Sans GB", "STHeiti", "msyh", "simsun", "NotoSansCJKsc", "NotoSansSC",
                        "NotoSansCJK", "DroidSansFallback",
                    };
                case CjkLocale.TraditionalChinese:
                    return new[]
                    {
                        "PingFang", "STHeiti", "msjh", "mingliu", "NotoSansCJKtc", "NotoSansTC", "NotoSansCJKhk",
                        "NotoSansCJK",
                    };
                case CjkLocale.Korean:
                    return new[]
                    {
                        "AppleSDGothicNeo", "AppleGothic", "malgun", "NotoSansCJKkr", "NotoSansKR", "NanumGothic",
                        "NotoSansCJK",
                    };
                default:
                    return None;
            }
        }

        /// <summary>
        /// Words in a face's family name that say which reading it was made
        /// for: in a collection that holds several (PingFang SC/TC/HK, Heiti
        /// SC/TC, Noto Sans CJK in all five), the face to load.
        /// </summary>
        private static string[] LocaleNameHints(CjkLocale locale)
        {
            switch (locale)
            {
                case CjkLocale.Japanese: return new[] { "JP", "J", "Japanese" };
                case CjkLocale.SimplifiedChinese: return new[] { "SC", "GB", "CN", "Simplified" };
                case CjkLocale.TraditionalChinese: return new[] { "TC", "HK", "TW", "Traditional" };
                case CjkLocale.Korean: return new[] { "KR", "K", "Korean" };
                default: return None;
            }
        }

        private static bool NameHints(string family, CjkLocale locale)
        {
            if (string.IsNullOrEmpty(family)) return false;
            var words = family.Split(' ', '-', '_');
            foreach (string hint in LocaleNameHints(locale))
                foreach (string word in words)
                    if (string.Equals(word, hint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Moves a file to the front of what its script has answered with.</summary>
        private static void Remember(int script, string path)
        {
            if (!RememberAnswers) return;
            if (!s_answered.TryGetValue(script, out var answered))
                s_answered[script] = answered = new List<string>(RememberedPerScript);

            int at = answered.IndexOf(path);
            if (at == 0) return;
            if (at > 0) answered.RemoveAt(at);
            answered.Insert(0, path);
            if (answered.Count > RememberedPerScript) answered.RemoveAt(answered.Count - 1);
        }

        /// <summary>
        /// Which script a codepoint belongs to, for the memory above.
        ///
        /// The same ranges <see cref="Preferred"/> sorts by, and coarse on
        /// purpose: a face that draws one Hangul syllable draws the rest, so
        /// all of them share one memory and the flood after a language change
        /// pays for the first character only. Anything unclassified shares the
        /// last bucket, which is no worse than having no memory at all.
        /// </summary>
        private static int ScriptOf(int codepoint)
        {
            if (IsEmoji(codepoint)) return 1;
            if (codepoint >= 0xAC00 && codepoint <= 0xD7AF ||
                codepoint >= 0x1100 && codepoint <= 0x11FF ||
                codepoint >= 0x3130 && codepoint <= 0x318F) return 2;   // Korean
            if (codepoint >= 0x3040 && codepoint <= 0x30FF ||
                codepoint >= 0x31F0 && codepoint <= 0x31FF) return 3;   // kana
            if (codepoint >= 0x4E00 && codepoint <= 0x9FFF ||
                codepoint >= 0x3400 && codepoint <= 0x4DBF ||
                codepoint >= 0xF900 && codepoint <= 0xFAFF ||
                codepoint >= 0x20000 && codepoint <= 0x2FA1F) return 4; // Han
            if (codepoint >= 0x0600 && codepoint <= 0x06FF ||
                codepoint >= 0x0750 && codepoint <= 0x077F ||
                codepoint >= 0xFB50 && codepoint <= 0xFEFF) return 5;   // Arabic
            if (codepoint >= 0x0590 && codepoint <= 0x05FF) return 6;   // Hebrew
            if (codepoint >= 0x0E00 && codepoint <= 0x0E7F) return 7;   // Thai
            if (codepoint >= 0x0900 && codepoint <= 0x097F) return 8;   // Devanagari
            if (codepoint >= 0x0980 && codepoint <= 0x09FF) return 9;   // Bengali
            if (codepoint >= 0x0B80 && codepoint <= 0x0BFF) return 10;  // Tamil
            if (codepoint >= 0x1200 && codepoint <= 0x137F) return 11;  // Ethiopic
            if (codepoint >= 0x0400 && codepoint <= 0x04FF ||
                codepoint >= 0x0370 && codepoint <= 0x03FF) return 12;  // Cyrillic and Greek
            return 0;
        }

        private static bool Matches(string stem, string needle)
        {
            return stem.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// How many files probing has asked about a character, this session.
        ///
        /// The number the per-script memory exists to keep down: without it,
        /// every unseen character walks the machine's font list again, and on a
        /// Mac that list is about four hundred files. A test reads this rather
        /// than a stopwatch, because "asked one file instead of four hundred"
        /// is the claim, and a millisecond is not evidence for it.
        /// </summary>
        public static int FilesProbed { get; private set; }

        private static SystemFace TryFile(string path, int codepoint, CjkLocale locale = CjkLocale.None)
        {
            FilesProbed++;
            var faces = SystemFontIndex.Coverage(path);
            // In a collection made for several readings, the face whose name
            // says this one first; then any, for files that hold one reading
            // and do not say so in the name (Hiragino, Malgun Gothic).
            for (int pass = locale == CjkLocale.None || faces.Length < 2 ? 1 : 0; pass < 2; pass++)
            {
                foreach (var coverage in faces)
                {
                    if (!coverage.Covers(codepoint)) continue;
                    var face = Slot(path, coverage);
                    if (pass == 0 && !NameHints(face.Name, locale)) continue;
                    var font = Use(face);
                    // The cmap ranges over-estimate on purpose; the loaded face is
                    // where the question gets its real answer.
                    if (font != null && font.HasGlyph(codepoint)) return face;
                }
            }
            return null;
        }

        private static SystemFace Slot(string path, SystemFontIndex.FaceCoverage coverage)
        {
            string key = path + "#" + coverage.FaceIndex;
            if (s_faces.TryGetValue(key, out var face)) return face;
            face = new SystemFace
            {
                Path = path,
                FaceIndex = coverage.FaceIndex,
                Name = SystemFontIndex.FamilyName(path, coverage),
            };
            s_faces[key] = face;
            return face;
        }

        /// <summary>
        /// The face in this slot, loading it if it was let go of, and no longer
        /// leaving if it was about to be. Null when it cannot be loaded.
        /// </summary>
        private static FontData Use(SystemFace face)
        {
            face.Releasing = false;
            if (face.IsLive) return face.Font;
            if (face.Failed) return null;

            FontData font = null;
            try
            {
                // Mapped, not read: only the pages of this one face that
                // HarfBuzz touches come into memory, and none onto the managed
                // heap. A collection's other faces cost nothing.
                font = FontData.LoadFile(face.Path, face.FaceIndex);
                if (!font.IsValid) { font.Dispose(); font = null; }
            }
            catch (Exception)
            {
                font = null;
            }

            if (font == null)
            {
                face.Failed = true;
                return null;
            }
            face.Font = font;
            face.Loads++;
            s_live[font.CacheId] = face;
            return font;
        }

        // ------------------------------------------------------------ preference

        private static readonly string[] Generic =
        {
            // Faces that cover a great deal on the platforms that have them,
            // tried before the alphabet decides.
            "Arial Unicode", "ArialUni", "Segoe UI", "seguisym", "DejaVuSans",
            "NotoSans-", "NotoSansSymbols", "Apple Symbols", "DroidSansFallback",
        };

        private static readonly string[] None = Array.Empty<string>();

        /// <summary>
        /// File-name fragments worth trying first for a character, by the block
        /// it lives in.
        ///
        /// A full script-to-font policy is fontconfig, and fontconfig is a
        /// library. This is the part that pays: the scripts a game actually
        /// ships in, on the three desktop platforms and Android, in the order a
        /// native reader would want them.
        /// </summary>
        private static string[] Preferred(int codepoint)
        {
            // Emoji before script: a codepoint in the emoji blocks wants a
            // colour face, whatever else on the machine has an outline for it.
            if (IsEmoji(codepoint))
                return new[] { "Apple Color Emoji", "seguiemj", "NotoColorEmoji", "EmojiOne", "Symbola" };

            if (codepoint >= 0xAC00 && codepoint <= 0xD7AF || // Hangul syllables
                codepoint >= 0x1100 && codepoint <= 0x11FF || // Jamo
                codepoint >= 0x3130 && codepoint <= 0x318F)   // compatibility jamo
                return new[]
                {
                    "AppleSDGothicNeo", "AppleGothic", "malgun", "NotoSansKR", "NotoSansCJKkr",
                    "NanumGothic", "gulim", "batang", "NotoSansCJK",
                };

            if (codepoint >= 0x3040 && codepoint <= 0x30FF || // kana
                codepoint >= 0x31F0 && codepoint <= 0x31FF)
                return new[]
                {
                    "Hiragino", "YuGothic", "meiryo", "msgothic", "NotoSansJP", "NotoSansCJKjp",
                    "NotoSansCJK", "AquaKana",
                };

            if (codepoint >= 0x4E00 && codepoint <= 0x9FFF ||   // unified ideographs
                codepoint >= 0x3400 && codepoint <= 0x4DBF ||   // extension A
                codepoint >= 0xF900 && codepoint <= 0xFAFF ||   // compatibility
                codepoint >= 0x20000 && codepoint <= 0x2FA1F)   // the supplementary planes
                return new[]
                {
                    "PingFang", "Hiragino", "msyh", "simsun", "msjh", "NotoSansSC", "NotoSansTC",
                    "NotoSansCJK", "NotoSerifCJK", "DroidSansFallback", "Songti", "Kaiti",
                };

            if (codepoint >= 0x0600 && codepoint <= 0x06FF ||
                codepoint >= 0x0750 && codepoint <= 0x077F ||
                codepoint >= 0xFB50 && codepoint <= 0xFEFF)
                return new[] { "GeezaPro", "NotoNaskhArabic", "NotoSansArabic", "segoeui", "tahoma", "DroidSansArabic", "Amiri" };

            if (codepoint >= 0x0590 && codepoint <= 0x05FF)
                return new[] { "ArialHB", "NotoSansHebrew", "NotoRashiHebrew", "david", "DroidSansHebrew" };

            if (codepoint >= 0x0E00 && codepoint <= 0x0E7F)
                return new[] { "Thonburi", "NotoSansThai", "leelawad", "tahoma", "DroidSansThai" };

            if (codepoint >= 0x0900 && codepoint <= 0x097F)
                return new[] { "DevanagariSangamMN", "Kohinoor", "NotoSansDevanagari", "mangal", "nirmala" };

            if (codepoint >= 0x0980 && codepoint <= 0x09FF)
                return new[] { "BanglaSangamMN", "KohinoorBangla", "NotoSansBengali", "vrinda", "nirmala" };

            if (codepoint >= 0x0B80 && codepoint <= 0x0BFF)
                return new[] { "TamilSangamMN", "NotoSansTamil", "latha", "nirmala" };

            if (codepoint >= 0x1200 && codepoint <= 0x137F)
                return new[] { "Kefa", "NotoSansEthiopic", "ebrima" };

            if (codepoint >= 0x0400 && codepoint <= 0x04FF ||
                codepoint >= 0x0370 && codepoint <= 0x03FF)
                return new[] { "Helvetica", "Arial", "segoeui", "NotoSans-", "DejaVuSans" };

            return None;
        }

        /// <summary>
        /// The blocks that are emoji rather than symbols. Rough on purpose:
        /// getting this wrong sends a symbol to a colour font that does not
        /// have it, which costs one wasted probe and then falls through.
        /// </summary>
        private static bool IsEmoji(int codepoint) =>
            codepoint >= 0x1F000 && codepoint <= 0x1FAFF ||
            codepoint >= 0x2600 && codepoint <= 0x27BF ||
            codepoint >= 0x1F1E6 && codepoint <= 0x1F1FF;

        /// <summary>
        /// Preference names, generic tail included: the order
        /// <see cref="Probe"/> walks.
        /// </summary>
        private static IEnumerable<string> PreferredThenGeneric(int codepoint)
        {
            foreach (string name in Preferred(codepoint)) yield return name;
            foreach (string name in Generic) yield return name;
        }
    }
}
