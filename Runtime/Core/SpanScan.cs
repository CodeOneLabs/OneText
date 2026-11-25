using System;

namespace OneText
{
    /// <summary>
    /// Character scans the hot path runs on every label, written as plain loops.
    ///
    /// <c>MemoryExtensions.IndexOf&lt;char&gt;</c> and <c>SequenceEqual&lt;char&gt;</c> are
    /// generic, and under IL2CPP their first step asks the runtime whether <c>char</c> is a
    /// bitwise-comparable type: <c>typeof(T)</c> turned into a type object, which is a hash
    /// lookup behind a mutex, every call. On a phone (Exynos 2500, IL2CPP release) that lookup
    /// was 8-19 % of the main thread in the warm benchmark scenes, most of it from the two
    /// "might this have markup / escapes?" pre-checks and the text setter's "is it the same
    /// text?" check, all on strings a dozen characters long, where a loop is the whole job.
    /// Mono in the editor answers the same question at JIT time, which is why the editor never
    /// showed it. Same answers either way.
    /// </summary>
    public static class SpanScan
    {
        /// <summary>Off = the <c>MemoryExtensions</c> calls, as before.</summary>
        [HotPathSwitch]
        internal static bool ScalarLoops = true;

        public static int IndexOf(ReadOnlySpan<char> text, char value)
        {
            if (!ScalarLoops) return text.IndexOf(value);
            for (int i = 0; i < text.Length; i++)
                if (text[i] == value) return i;
            return -1;
        }

        public static bool Equal(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            if (!ScalarLoops) return a.SequenceEqual(b);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
