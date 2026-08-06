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
    /// <para><b>Web.</b> A browser has no font directory to walk, so on Web the
    /// tier finds nothing and a missing character stays tofu. That is a
    /// platform fact rather than a decision, and it is recorded in
    /// <c>Docs/NATIVES.md</c>.</para>
    ///
    /// <para><b>Colour.</b> A system face that carries CBDT or COLRv0 goes
    /// through the same colour path as a bundled one; nothing here knows the
    /// difference. Apple Color Emoji is the exception, and not because of this
    /// class: its payload is sbix, which <see cref="ColorGlyphs"/> deliberately
    /// does not read, so on macOS and iOS an emoji resolved from the system
    /// draws as an outline or as nothing. Bundle a colour emoji font for
    /// emoji.</para>
    /// </summary>
    public static class SystemFonts
    {
        private static readonly object s_sync = new object();

        // Codepoint -> the face that draws it, or null for "asked, nothing
        // has it". Negative answers are cached too: without that, a string
        // full of one unrenderable character would rescan the disk per
        // occurrence.
        private static readonly Dictionary<int, FontData> s_resolved = new Dictionary<int, FontData>();

        // Loaded faces, keyed "path#faceIndex", so two characters found in one
        // font share one parse and one set of atlas tiles.
        private static readonly Dictionary<string, FontData> s_faces =
            new Dictionary<string, FontData>(StringComparer.Ordinal);

        // Family names by FontData.CacheId, what a diagnostic prints. Keyed by
        // cache id rather than by the native pointer for the reason
        // ColorGlyphs is: a freed face's address comes straight back.
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();

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
                if (s_resolved.TryGetValue(codepoint, out var cached)) return cached;
                FontData found = null;
                try { found = Probe(codepoint); }
                catch (Exception e)
                {
                    // A fallback tier that throws would turn a missing glyph
                    // into a missing label. Whatever went wrong on this
                    // machine's disk, the answer is "no system font".
                    Debug.LogWarning($"OneText: system font fallback failed for U+{codepoint:X4}: {e.Message}");
                }
                s_resolved[codepoint] = found;
                return found;
            }
        }

        /// <summary>True if this face came from the operating system rather than the project.</summary>
        public static bool IsSystemFont(FontData font)
        {
            if (font == null) return false;
            lock (s_sync) return s_names.ContainsKey(font.CacheId);
        }

        /// <summary>
        /// The family name of a face this class supplied ("Apple SD Gothic
        /// Neo", "Segoe UI Emoji"), or null for a font it did not supply.
        /// </summary>
        public static string NameOf(FontData font)
        {
            if (font == null) return null;
            lock (s_sync) return s_names.TryGetValue(font.CacheId, out string name) ? name : null;
        }

        /// <summary>
        /// The name of the system font that would draw this character, or null.
        /// What a diagnostic asks: it wants the name, not the face.
        /// </summary>
        public static string NameFor(int codepoint) => NameOf(Resolve(codepoint));

        /// <summary>Faces loaded from the system so far. Diagnostics and tests.</summary>
        public static int LoadedFaceCount
        {
            get { lock (s_sync) return s_faces.Count; }
        }

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
        /// editor's assembly reload, not for a running game.
        /// </summary>
        public static void Forget()
        {
            lock (s_sync)
            {
                foreach (var font in s_faces.Values) font?.Dispose();
                s_faces.Clear();
                s_resolved.Clear();
                s_names.Clear();
                SystemFontIndex.Forget();
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ForgetOnAssemblyReload() =>
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Forget;
#endif

        // --------------------------------------------------------------- probing

        private static FontData Probe(int codepoint)
        {
            var files = SystemFontIndex.Files();
            if (files.Length == 0) return null;

            // A short list of the faces that are actually likely, matched on
            // file name. It is not an optimisation for its own sake: on a
            // machine with three hundred fonts, several of them cover Han, and
            // "the first file alphabetically" is not an answer anybody would
            // choose. Preference is how 한 comes out of Apple SD Gothic Neo and
            // not out of a Serif face that happens to sort earlier.
            var tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (string preferred in PreferredThenGeneric(codepoint))
            {
                foreach (string path in files)
                {
                    if (!Matches(path, preferred)) continue;
                    if (!tried.Add(path)) continue;
                    var font = TryFile(path, codepoint);
                    if (font != null) return font;
                }
            }

            foreach (string path in files)
            {
                if (!tried.Add(path)) continue;
                var font = TryFile(path, codepoint);
                if (font != null) return font;
            }
            return null;
        }

        private static bool Matches(string path, string needle)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            return name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static FontData TryFile(string path, int codepoint)
        {
            foreach (var face in SystemFontIndex.Coverage(path))
            {
                if (!face.Covers(codepoint)) continue;
                var font = Load(path, face);
                // The cmap ranges over-estimate on purpose; the loaded face is
                // where the question gets its real answer.
                if (font != null && font.HasGlyph(codepoint)) return font;
            }
            return null;
        }

        private static FontData Load(string path, SystemFontIndex.FaceCoverage face)
        {
            string key = path + "#" + face.FaceIndex;
            if (s_faces.TryGetValue(key, out var cached)) return cached;

            FontData font = null;
            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                font = FontData.Load(bytes, face.FaceIndex);
                if (!font.IsValid) { font.Dispose(); font = null; }
            }
            catch (Exception)
            {
                font = null;
            }

            s_faces[key] = font;
            if (font != null) s_names[font.CacheId] = SystemFontIndex.FamilyName(path, face);
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
