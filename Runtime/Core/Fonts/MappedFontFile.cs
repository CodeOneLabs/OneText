using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OneText
{
    /// <summary>
    /// A font file mapped into the address space read-only, so HarfBuzz reads
    /// it in place and only the pages it touches are ever in memory.
    ///
    /// <para><b>Why a mapping.</b> Reading an operating-system font into an
    /// array costs the whole file, on the managed heap, for as long as the face
    /// lives: Apple SD Gothic Neo is 55 MB, Apple Color Emoji 192 MB, and a
    /// collection is every face in it although a label uses one. A mapping
    /// costs address space. The pages HarfBuzz actually reads — the table
    /// directory of the one face asked for, its cmap, the layout tables, the
    /// outlines or bitmaps of the glyphs on screen — come in from the file as
    /// they are touched, are clean (the file is their backing store, so the
    /// system drops them under pressure and reads them back rather than writing
    /// them to swap) and are not on the managed heap at all. macOS and iOS do
    /// not count clean file-backed pages in a process's footprint, which is
    /// the number jetsam kills by.</para>
    ///
    /// <para><b>Why managed, and not HarfBuzz's own file loader.</b>
    /// <c>hb_blob_create_from_file</c> is exported by every bundled binary but
    /// only maps the file when HarfBuzz was built with <c>HAVE_MMAP</c>, and
    /// the NuGet builds this package ships for macOS, iOS and Android were not:
    /// they import <c>fread</c> and no <c>mmap</c>, so the "file" API reads the
    /// whole file into malloc'd memory — the same cost as before, moved off the
    /// managed heap. The Linux build does not export it at all. Windows maps.
    /// <see cref="MemoryMappedFile"/> maps on every platform that has files, in
    /// the editor (Mono) and in il2cpp players, and the face is built over the
    /// view with <c>hb_blob_create</c> like any other memory.</para>
    ///
    /// <para><b>Lifetime.</b> The view must outlive the blob, face and font
    /// that read through it; <see cref="FontData"/> owns one of these and
    /// disposes it last. The file is opened with read and delete sharing, so
    /// on Windows another reader is never refused, but the file cannot be
    /// deleted or replaced while it is mapped — for operating-system fonts that
    /// is what the system does too. On POSIX a font file that is deleted or
    /// renamed over while mapped keeps its old contents for as long as the
    /// mapping lives; one truncated in place would fault on access, which no
    /// font installer does.</para>
    /// </summary>
    internal sealed unsafe class MappedFontFile : IDisposable
    {
        private MemoryMappedFile _file;
        private MemoryMappedViewAccessor _view;
        private bool _acquired;

        /// <summary>The file this maps.</summary>
        public string Path { get; }

        /// <summary>Bytes of address space the mapping takes: the file's length.</summary>
        public long Length { get; }

        /// <summary>First byte of the file, valid until <see cref="Dispose"/>.</summary>
        public IntPtr Pointer { get; private set; }

        private MappedFontFile(string path, long length)
        {
            Path = path;
            Length = length;
        }

        /// <summary>
        /// Whether this platform can map files at all. Web has no file system to
        /// map from; everywhere else il2cpp and Mono implement the API.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return false;
#else
                return !s_unsupported;
#endif
            }
        }

        // Set when a mapping failed for a reason that is the platform's rather
        // than the file's, so every later font does not pay for the exception.
        private static bool s_unsupported;

        /// <summary>
        /// Maps <paramref name="path"/>, or returns null when it cannot be: no
        /// such file, an empty one, one too big for a HarfBuzz blob, or a
        /// platform without mappings. <paramref name="error"/> says which.
        /// </summary>
        public static MappedFontFile TryOpen(string path, out string error)
        {
            error = null;
            if (!IsSupported)
            {
                error = "this platform cannot map files";
                return null;
            }
            if (string.IsNullOrEmpty(path))
            {
                error = "no path";
                return null;
            }

            FileStream stream = null;
            var mapped = (MappedFontFile)null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    1, FileOptions.None);
                long length = stream.Length;
                // hb_blob_create takes a 32-bit length. A 4 GB font is not a font.
                if (length <= 0 || length > uint.MaxValue)
                {
                    error = length <= 0 ? "empty file" : "file too large for a HarfBuzz blob";
                    stream.Dispose();
                    return null;
                }

                mapped = new MappedFontFile(path, length);
#if NET_STANDARD_2_0 || NET_STANDARD_2_1 || NET_STANDARD
                mapped._file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read,
                    HandleInheritability.None, leaveOpen: false);
#else
                mapped._file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read,
                    null, HandleInheritability.None, false);
#endif
                stream = null; // the mapping owns it now
                mapped._view = mapped._file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                mapped._view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                mapped._acquired = true;
                mapped.Pointer = (IntPtr)(pointer + mapped._view.PointerOffset);
                if (mapped.Pointer == IntPtr.Zero) throw new IOException("the view has no address");
                return mapped;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException ||
                                      e is ArgumentException || e is System.Security.SecurityException)
            {
                // The file's problem: missing, unreadable, locked. Not the platform's.
                error = e.Message;
                mapped?.Dispose();
                stream?.Dispose();
                return null;
            }
            catch (Exception e)
            {
                // NotSupportedException, a missing il2cpp icall, an entry point
                // the runtime does not have: the platform's, and permanent.
                s_unsupported = true;
                error = $"{e.GetType().Name}: {e.Message}";
                // Said once: from here every font file is read whole, which is
                // the memory this class exists to save, and nothing else shows it.
                UnityEngine.Debug.LogWarning("OneText: this platform cannot map font files (" + error +
                                             "); system and file fonts will be read whole into memory.");
                mapped?.Dispose();
                stream?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// How many bytes of the mapping are in physical memory right now, or
        /// -1 where the platform will not say. A diagnostic: it asks the kernel
        /// page by page (<c>mincore</c>) and allocates a byte per page.
        /// </summary>
        public long ResidentBytes()
        {
            if (Pointer == IntPtr.Zero) return 0;
            try
            {
                int page = Environment.SystemPageSize;
                if (page <= 0) return -1;
                long pages = (Length + page - 1) / page;
                var vector = new byte[pages];
                if (!Mincore(Pointer, (UIntPtr)Length, vector)) return -1;
                long resident = 0;
                for (long i = 0; i < pages; i++)
                    if ((vector[i] & 1) != 0) resident += page;
                return Math.Min(resident, Length);
            }
            catch (Exception)
            {
                return -1;
            }
        }

#if UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "mincore")]
        private static extern int mincore(IntPtr address, UIntPtr length, byte[] vector);

        private static bool Mincore(IntPtr address, UIntPtr length, byte[] vector) =>
            mincore(address, length, vector) == 0;
#elif UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR) || (UNITY_ANDROID && !UNITY_EDITOR)
        [DllImport("libc", EntryPoint = "mincore")]
        private static extern int mincore(IntPtr address, UIntPtr length, byte[] vector);

        private static bool Mincore(IntPtr address, UIntPtr length, byte[] vector) =>
            mincore(address, length, vector) == 0;
#else
        // Windows answers this with QueryWorkingSetEx and iOS would need
        // __Internal; neither is worth a binding for a diagnostic.
        private static bool Mincore(IntPtr address, UIntPtr length, byte[] vector) => false;
#endif

        public void Dispose()
        {
            Pointer = IntPtr.Zero;
            try
            {
                if (_acquired) _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            catch (Exception)
            {
                // Already released by its own finalizer; nothing left to undo.
            }
            _acquired = false;
            try { _view?.Dispose(); } catch (Exception) { }
            try { _file?.Dispose(); } catch (Exception) { }
            _view = null;
            _file = null;
        }
    }
}
