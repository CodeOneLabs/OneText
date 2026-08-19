using System;
using System.Collections.Generic;
using System.IO;

namespace OneText
{
    /// <summary>
    /// The font files the operating system has, and what each of them covers,
    /// read from the files themselves, without parsing a font.
    ///
    /// <para>The point of this class is that asking "does any font on this
    /// machine have U+D55C" must not mean loading every font on this machine.
    /// A system font directory is a gigabyte of faces; loading them through
    /// HarfBuzz to ask a one-bit question would cost seconds and hold the lot
    /// in memory afterwards. So coverage is read straight out of the `cmap`
    /// table: open the file, seek to the table directory, read the one table
    /// that answers the question (a few kilobytes per face) and keep the
    /// character ranges. Only the face that wins is ever handed to
    /// <see cref="FontData"/>.</para>
    ///
    /// <para>The ranges are an over-estimate by design. A `cmap` format 4
    /// segment can map a character inside its range to glyph 0, and this reader
    /// does not walk the glyph arrays to find out. That is deliberate: the
    /// answer here decides which face is worth loading, and
    /// <see cref="FontData.HasGlyph"/> gives the real answer a moment later on
    /// the face that was loaded. An over-estimate costs one wasted load; an
    /// under-estimate would lose a font that could have drawn the
    /// character.</para>
    ///
    /// <para>Format 13 (many-to-one) is not read, and that is also deliberate.
    /// The one font that uses it on macOS is <c>LastResort.otf</c>, whose
    /// glyphs are decorated boxes standing in for a script, tofu with a
    /// hint. A fallback tier that resolved to it would have replaced every
    /// missing character with a different-looking missing character.</para>
    /// </summary>
    internal static class SystemFontIndex
    {
        /// <summary>Coverage of one face inside one font file.</summary>
        internal sealed class FaceCoverage
        {
            /// <summary>Index of this face in the file, non-zero only for collections.</summary>
            public uint FaceIndex;

            /// <summary>Offset of this face's table directory, for the name table.</summary>
            public long TableDirectory;

            private int[] _starts = Array.Empty<int>();
            private int[] _ends = Array.Empty<int>();

            public int RangeCount => _starts.Length;

            public void SetRanges(List<int> starts, List<int> ends)
            {
                _starts = starts.ToArray();
                _ends = ends.ToArray();
            }

            /// <summary>True if the face's cmap claims this character.</summary>
            public bool Covers(int codepoint)
            {
                int lo = 0, hi = _starts.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (codepoint < _starts[mid]) hi = mid - 1;
                    else if (codepoint > _ends[mid]) lo = mid + 1;
                    else return true;
                }
                return false;
            }
        }

        private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };

        /// <summary>
        /// Files whose coverage is a lie for this purpose. LastResort answers
        /// for nearly all of Unicode with boxes; see the class comment.
        /// </summary>
        private static readonly string[] Excluded = { "lastresort", "adobeblank" };

        private static string[] s_files;

        /// <summary>
        /// Each file's name without its directory or extension, computed with
        /// the listing.
        ///
        /// The preference walk matches names, and it does so for every file
        /// against every preferred name — several thousand comparisons for one
        /// character that missed the project's fonts. Cutting the name out of
        /// the path each time made a string for every one of them; the name
        /// does not change after the listing, so it is taken once.
        /// </summary>
        private static string[] s_stems;
        private static readonly Dictionary<string, FaceCoverage[]> s_coverage =
            new Dictionary<string, FaceCoverage[]>(StringComparer.Ordinal);

        /// <summary>
        /// Every font file the platform keeps, found once and remembered.
        ///
        /// This is a directory listing, not a parse: it costs milliseconds even
        /// where the answer runs to hundreds of files, and it happens the first
        /// time a character misses the project's own fonts, never before.
        /// </summary>
        internal static string[] Files()
        {
            if (s_files != null) return s_files;

            var found = new List<string>(256);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in Directories())
            {
                if (string.IsNullOrEmpty(directory)) continue;
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    // Recursive because the Linux and Windows layouts nest and
                    // the macOS one does not; one rule is cheaper than three.
                    foreach (string path in Directory.EnumerateFiles(directory, "*",
                                 SearchOption.AllDirectories))
                    {
                        if (!IsFontFile(path)) continue;
                        if (!seen.Add(path)) continue;
                        found.Add(path);
                    }
                }
                catch (Exception)
                {
                    // A directory that cannot be listed (sandboxed, or gone
                    // between the check and the walk) is one fewer source of
                    // fallbacks, not a failure. The next one may work.
                }
            }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            s_files = found.ToArray();
            s_stems = new string[s_files.Length];
            for (int i = 0; i < s_files.Length; i++)
                s_stems[i] = Path.GetFileNameWithoutExtension(s_files[i]);
            return s_files;
        }

        /// <summary>
        /// The file names behind <see cref="Files"/>, index for index: what the
        /// preference walk compares against, without cutting a string out of a
        /// path to do it.
        /// </summary>
        internal static string[] Stems()
        {
            if (s_stems == null) Files();
            return s_stems;
        }

        private static bool IsFontFile(string path)
        {
            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;
            bool known = false;
            foreach (string candidate in Extensions)
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                { known = true; break; }
            if (!known) return false;

            string name = Path.GetFileNameWithoutExtension(path).Replace(" ", string.Empty)
                .ToLowerInvariant();
            foreach (string excluded in Excluded)
                if (name.Contains(excluded)) return false;
            return true;
        }

        /// <summary>
        /// Where the platform keeps its fonts.
        ///
        /// Web is the empty case and has to be: a browser has no file system to
        /// walk, so the whole tier is a no-op there and a character no bundled
        /// font covers stays tofu, exactly as it did before this existed.
        /// </summary>
        internal static IEnumerable<string> Directories()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            yield break;
#else
            string home = null;
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch (Exception) { /* not every platform has one */ }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Supplemental";
            yield return "/Library/Fonts";
            if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Library/Fonts");
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string windows = null;
            try { windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
            catch (Exception) { /* fall through to the literal path */ }
            yield return string.IsNullOrEmpty(windows) ? @"C:\Windows\Fonts" : Path.Combine(windows, "Fonts");
            // Fonts installed for one user, which is what "Install for me" does
            // since Windows 10 and what a font a designer added will be.
            string local = null;
            try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch (Exception) { /* ignored */ }
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, @"Microsoft\Windows\Fonts");
#elif UNITY_ANDROID
            yield return "/system/fonts";
            yield return "/system/font";
            yield return "/data/fonts";
            yield return "/product/fonts";
#elif UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS
            // The sandbox may refuse these; Directories() is allowed to name a
            // path that cannot be read, and Files() swallows the refusal.
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Core";
            yield return "/System/Library/Fonts/Cache";
            yield return "/System/Library/Fonts/AppFonts";
            yield return "/System/Library/Fonts/CoreAddition";
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".local/share/fonts");
                yield return Path.Combine(home, ".fonts");
            }
#else
            yield break;
#endif
#endif
        }

        /// <summary>
        /// The faces in one font file and what each covers, parsed once and
        /// remembered. Empty for a file that cannot be read or holds no usable
        /// cmap (including, on purpose, a file whose only cmap is format 13).
        /// </summary>
        internal static FaceCoverage[] Coverage(string path)
        {
            if (s_coverage.TryGetValue(path, out var cached)) return cached;
            FaceCoverage[] faces;
            try { faces = ReadCoverage(path); }
            catch (Exception) { faces = Array.Empty<FaceCoverage>(); }
            s_coverage[path] = faces;
            return faces;
        }

        /// <summary>Forgets the listing and every parsed cmap (tests, and editor reloads).</summary>
        internal static void Forget()
        {
            s_files = null;
            s_stems = null;
            s_coverage.Clear();
        }

        // --------------------------------------------------------- sfnt reading

        private static FaceCoverage[] ReadCoverage(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            var offsets = FaceOffsets(stream);
            if (offsets.Count == 0) return Array.Empty<FaceCoverage>();

            var faces = new List<FaceCoverage>(offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                var face = ReadFace(stream, offsets[i], (uint)i);
                if (face != null) faces.Add(face);
            }
            return faces.Count == 0 ? Array.Empty<FaceCoverage>() : faces.ToArray();
        }

        /// <summary>
        /// Where each face's table directory starts. One entry for a plain
        /// font; one per member for a collection, which is how macOS ships most
        /// of its families.
        /// </summary>
        private static List<long> FaceOffsets(FileStream stream)
        {
            var offsets = new List<long>(1);
            var header = new byte[12];
            if (!Fill(stream, header, 12)) return offsets;

            uint tag = BE32(header, 0);
            const uint Ttcf = 0x74746366; // 'ttcf'
            if (tag != Ttcf)
            {
                // 0x00010000 (TrueType), 'OTTO' (CFF), 'true' (older Apple).
                if (tag != 0x00010000 && tag != 0x4F54544F && tag != 0x74727565) return offsets;
                offsets.Add(0);
                return offsets;
            }

            uint count = BE32(header, 8);
            if (count == 0 || count > 512) return offsets;
            var table = new byte[count * 4];
            if (!Fill(stream, table, table.Length)) return offsets;
            for (uint i = 0; i < count; i++) offsets.Add(BE32(table, (int)(i * 4)));
            return offsets;
        }

        private static FaceCoverage ReadFace(FileStream stream, long directory, uint index)
        {
            if (!Seek(stream, directory)) return null;
            var header = new byte[12];
            if (!Fill(stream, header, 12)) return null;
            int tables = (int)BE16(header, 4);
            if (tables <= 0 || tables > 512) return null;

            var records = new byte[tables * 16];
            if (!Fill(stream, records, records.Length)) return null;

            long cmapOffset = -1;
            uint cmapLength = 0;
            for (int i = 0; i < tables; i++)
            {
                if (BE32(records, i * 16) != 0x636D6170) continue; // 'cmap'
                cmapOffset = BE32(records, i * 16 + 8);
                cmapLength = BE32(records, i * 16 + 12);
                break;
            }
            if (cmapOffset < 0 || cmapLength < 4 || cmapLength > 16 * 1024 * 1024) return null;

            var cmap = new byte[cmapLength];
            if (!Seek(stream, cmapOffset) || !Fill(stream, cmap, cmap.Length)) return null;

            var face = new FaceCoverage { FaceIndex = index, TableDirectory = directory };
            return ParseCmap(cmap, face) ? face : null;
        }

        /// <summary>
        /// Picks the best subtable in a cmap and turns it into ranges. "Best"
        /// is the one that reaches the most of Unicode: a format 12 table over
        /// a format 4 one, because only the former can name anything above the
        /// BMP.
        /// </summary>
        private static bool ParseCmap(byte[] cmap, FaceCoverage face)
        {
            int count = (int)BE16(cmap, 2);
            if (count <= 0 || 4 + count * 8 > cmap.Length) return false;

            int bestScore = 0, bestOffset = -1, bestFormat = 0;
            for (int i = 0; i < count; i++)
            {
                int record = 4 + i * 8;
                uint platform = BE16(cmap, record);
                uint encoding = BE16(cmap, record + 2);
                long offset = BE32(cmap, record + 4);
                if (offset < 0 || offset + 2 > cmap.Length) continue;
                int format = (int)BE16(cmap, (int)offset);

                int score = Score(platform, encoding, format);
                if (score <= bestScore) continue;
                bestScore = score;
                bestOffset = (int)offset;
                bestFormat = format;
            }
            if (bestOffset < 0) return false;

            var starts = new List<int>(256);
            var ends = new List<int>(256);
            bool ok = bestFormat switch
            {
                0 => ParseFormat0(cmap, bestOffset, starts, ends),
                4 => ParseFormat4(cmap, bestOffset, starts, ends),
                6 => ParseFormat6(cmap, bestOffset, starts, ends),
                12 => ParseFormat12(cmap, bestOffset, starts, ends),
                _ => false,
            };
            if (!ok || starts.Count == 0) return false;
            face.SetRanges(starts, ends);
            return true;
        }

        private static int Score(uint platform, uint encoding, int format)
        {
            // Unicode beyond the BMP first, then the BMP, then the legacy
            // single-byte tables that only an old Apple font still carries.
            if (format == 12 && (platform == 3 && encoding == 10 ||
                                 platform == 0 && (encoding == 4 || encoding == 6))) return 4;
            if (format == 4 && (platform == 3 && encoding == 1 ||
                                platform == 0 && encoding <= 3)) return 3;
            if (format == 6 && platform == 0) return 2;
            if (format == 0 && (platform == 1 || platform == 0)) return 1;
            return 0;
        }

        private static bool ParseFormat0(byte[] cmap, int offset, List<int> starts, List<int> ends)
        {
            if (offset + 262 > cmap.Length) return false;
            int run = -1;
            for (int c = 0; c < 256; c++)
            {
                bool mapped = cmap[offset + 6 + c] != 0;
                if (mapped && run < 0) run = c;
                else if (!mapped && run >= 0) { starts.Add(run); ends.Add(c - 1); run = -1; }
            }
            if (run >= 0) { starts.Add(run); ends.Add(255); }
            return starts.Count > 0;
        }

        private static bool ParseFormat4(byte[] cmap, int offset, List<int> starts, List<int> ends)
        {
            if (offset + 14 > cmap.Length) return false;
            int segments = (int)BE16(cmap, offset + 6) / 2;
            if (segments <= 0) return false;
            int endsAt = offset + 14;
            int startsAt = endsAt + segments * 2 + 2;
            if (startsAt + segments * 2 > cmap.Length) return false;

            for (int i = 0; i < segments; i++)
            {
                int end = (int)BE16(cmap, endsAt + i * 2);
                int start = (int)BE16(cmap, startsAt + i * 2);
                // The table always closes with the 0xFFFF sentinel segment;
                // taking it at face value would claim the noncharacter.
                if (start > end || start == 0xFFFF) continue;
                starts.Add(start);
                ends.Add(end);
            }
            return starts.Count > 0;
        }

        private static bool ParseFormat6(byte[] cmap, int offset, List<int> starts, List<int> ends)
        {
            if (offset + 10 > cmap.Length) return false;
            int first = (int)BE16(cmap, offset + 6);
            int entries = (int)BE16(cmap, offset + 8);
            if (entries <= 0) return false;
            starts.Add(first);
            ends.Add(first + entries - 1);
            return true;
        }

        private static bool ParseFormat12(byte[] cmap, int offset, List<int> starts, List<int> ends)
        {
            if (offset + 16 > cmap.Length) return false;
            long groups = BE32(cmap, offset + 12);
            if (groups <= 0 || offset + 16 + groups * 12 > cmap.Length) return false;

            for (long i = 0; i < groups; i++)
            {
                int record = offset + 16 + (int)(i * 12);
                long start = BE32(cmap, record);
                long end = BE32(cmap, record + 4);
                if (start > end || end > 0x10FFFF) continue;
                starts.Add((int)start);
                ends.Add((int)end);
            }
            return starts.Count > 0;
        }

        // ------------------------------------------------------------ name table

        /// <summary>
        /// The family name a face calls itself, for a diagnostic that has to
        /// name the font a machine happened to supply. Falls back to the file
        /// name, which is never wrong and is sometimes ugly.
        /// </summary>
        internal static string FamilyName(string path, FaceCoverage face)
        {
            try
            {
                string name = ReadFamilyName(path, face.TableDirectory);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception)
            {
                // A face with no readable name table still draws; it just has
                // to be reported by file name.
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        private static string ReadFamilyName(string path, long directory)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            if (!Seek(stream, directory)) return null;
            var header = new byte[12];
            if (!Fill(stream, header, 12)) return null;
            int tables = (int)BE16(header, 4);
            if (tables <= 0 || tables > 512) return null;

            var records = new byte[tables * 16];
            if (!Fill(stream, records, records.Length)) return null;

            long offset = -1;
            uint length = 0;
            for (int i = 0; i < tables; i++)
            {
                if (BE32(records, i * 16) != 0x6E616D65) continue; // 'name'
                offset = BE32(records, i * 16 + 8);
                length = BE32(records, i * 16 + 12);
                break;
            }
            if (offset < 0 || length < 6 || length > 4 * 1024 * 1024) return null;

            var table = new byte[length];
            if (!Seek(stream, offset) || !Fill(stream, table, table.Length)) return null;

            int count = (int)BE16(table, 2);
            int strings = (int)BE16(table, 4);
            if (count <= 0 || 6 + count * 12 > table.Length) return null;

            string best = null;
            int bestScore = 0;
            for (int i = 0; i < count; i++)
            {
                int record = 6 + i * 12;
                uint platform = BE16(table, record);
                uint encoding = BE16(table, record + 2);
                uint language = BE16(table, record + 4);
                uint nameId = BE16(table, record + 6);
                int stringLength = (int)BE16(table, record + 8);
                int stringOffset = strings + (int)BE16(table, record + 10);
                // 16 is the typographic family: "Apple SD Gothic Neo" where
                // name 1 says "Apple SD Gothic Neo Regular" on some faces.
                if (nameId != 1 && nameId != 16) continue;
                if (stringLength <= 0 || stringOffset + stringLength > table.Length) continue;

                bool wide = platform == 3 || platform == 0;
                // English first, and by a wide margin. A face carries its name
                // in every language it was localized into, and Hiragino Sans GB
                // calls itself 冬青黑體簡體中文, a true name and a poor one to
                // put in a warning addressed to whoever is reading the log.
                bool english = platform == 3 && language == 0x0409 ||
                               platform == 1 && language == 0 ||
                               platform == 0;
                int score = (english ? 8 : 0) +
                            (nameId == 16 ? 2 : 1) +
                            (platform == 3 ? 2 : platform == 0 ? 1 : 0);
                if (score <= bestScore) continue;

                string value = wide
                    ? Utf16Be(table, stringOffset, stringLength)
                    : Ascii(table, stringOffset, stringLength);
                if (string.IsNullOrEmpty(value)) continue;
                best = value;
                bestScore = score;
            }
            return best;
        }

        private static string Utf16Be(byte[] data, int offset, int length)
        {
            var chars = new char[length / 2];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = (char)((data[offset + i * 2] << 8) | data[offset + i * 2 + 1]);
            return new string(chars).Trim();
        }

        private static string Ascii(byte[] data, int offset, int length)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)data[offset + i];
            return new string(chars).Trim();
        }

        // ---------------------------------------------------------------- bytes

        private static bool Seek(FileStream stream, long position)
        {
            if (position < 0 || position >= stream.Length) return false;
            stream.Position = position;
            return true;
        }

        private static bool Fill(FileStream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0) return false;
                read += chunk;
            }
            return true;
        }

        private static uint BE16(byte[] data, int offset) =>
            (uint)((data[offset] << 8) | data[offset + 1]);

        private static uint BE32(byte[] data, int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3]);
    }
}
