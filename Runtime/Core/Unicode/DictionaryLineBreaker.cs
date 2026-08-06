using System;
using System.Collections.Generic;

namespace OneText.Unicode
{
    /// <summary>
    /// Word breaking for the scripts that do not write spaces: Thai, Lao,
    /// Khmer and Burmese.
    ///
    /// These shape correctly today and wrap in the wrong places, which is the
    /// gap between "ships Thai shaping" and "ships Thai" that almost everyone
    /// leaves quietly open. UAX #14 is explicit about why: it gives these
    /// scripts class SA and defers them to dictionary analysis, because there
    /// is no way to find a word boundary in a run of Thai letters without
    /// knowing the words.
    ///
    /// So: a trie, longest match, and the standard fallback. The algorithm is
    /// small; the data is the work, and the data is deliberately separable:
    /// <see cref="WordList"/> takes any newline-separated list, so the ICU
    /// dictionaries (permissively licensed, ~200 KB for Thai) drop straight in.
    /// What ships built in is a starter list, and a project that needs correct
    /// Thai wrapping should load the full one; <see cref="Coverage"/> exists so
    /// that can be checked rather than assumed.
    /// </summary>
    public sealed class WordList
    {
        /// <summary>
        /// A trie over UTF-16 code units, stored as a dictionary of dictionaries.
        ///
        /// Not the most compact structure available (a packed DAWG would be
        /// far smaller), but the lookup is what matters here and this one is
        /// obviously correct. Compaction is worth doing when someone has
        /// measured a real dictionary in a real build.
        /// </summary>
        private sealed class Node
        {
            public Dictionary<char, Node> Next;
            public bool IsWord;
        }

        private readonly Node _root = new Node();

        public int WordCount { get; private set; }

        /// <summary>Longest word in the list, which bounds every lookup.</summary>
        public int LongestWord { get; private set; }

        public void Add(string word)
        {
            if (string.IsNullOrEmpty(word)) return;

            var node = _root;
            foreach (char c in word)
            {
                node.Next ??= new Dictionary<char, Node>();
                if (!node.Next.TryGetValue(c, out var next))
                {
                    next = new Node();
                    node.Next[c] = next;
                }
                node = next;
            }
            if (!node.IsWord)
            {
                node.IsWord = true;
                WordCount++;
            }
            if (word.Length > LongestWord) LongestWord = word.Length;
        }

        /// <summary>
        /// Loads a newline-separated word list, the format ICU's dictionaries
        /// ship in, so theirs can be used unmodified. Lines starting with '#'
        /// are comments and anything after a tab is ignored, which is how those
        /// files carry per-word weights.
        /// </summary>
        public void AddAll(string wordList)
        {
            if (string.IsNullOrEmpty(wordList)) return;
            foreach (var line in wordList.Split('\n'))
            {
                var word = line.Trim();
                if (word.Length == 0 || word[0] == '#') continue;
                int tab = word.IndexOf('\t');
                if (tab > 0) word = word.Substring(0, tab);
                Add(word);
            }
        }

        /// <summary>
        /// Length of the longest word starting at <paramref name="start"/>, or
        /// 0 if none.
        /// </summary>
        public int LongestMatch(string text, int start, int limit)
        {
            var node = _root;
            int best = 0;
            int end = Math.Min(limit, start + LongestWord);
            for (int i = start; i < end; i++)
            {
                if (node.Next == null || !node.Next.TryGetValue(text[i], out node)) break;
                if (node.IsWord) best = i - start + 1;
            }
            return best;
        }

        /// <summary>
        /// What fraction of a sample this list can actually segment, as
        /// characters matched over characters tried.
        ///
        /// Worth having because the honest failure mode of a dictionary
        /// breaker is not a crash; it is wrapping that is subtly wrong in a
        /// language the team does not read. A number makes that checkable.
        /// </summary>
        public float Coverage(string sample)
        {
            if (string.IsNullOrEmpty(sample)) return 0f;
            int matched = 0, i = 0;
            while (i < sample.Length)
            {
                int length = LongestMatch(sample, i, sample.Length);
                if (length > 0) { matched += length; i += length; }
                else i++;
            }
            return matched / (float)sample.Length;
        }
    }

    /// <summary>
    /// Turns a word list into break opportunities, for the scripts UAX #14
    /// hands over.
    /// </summary>
    public static class DictionaryLineBreaker
    {
        // Not synchronised: layout is main-thread, and a dictionary is
        // installed at startup rather than mid-frame. If shaping ever goes
        // parallel this needs the same treatment FontData got.

        private static readonly Dictionary<string, WordList> s_lists =
            new Dictionary<string, WordList>(StringComparer.OrdinalIgnoreCase);

        private static bool s_installedDefaults;

        /// <summary>
        /// Installs the built-in lists on first use.
        ///
        /// Called from <see cref="Apply"/> rather than a static constructor, so
        /// a project that replaces the Thai list before drawing anything is not
        /// paying to build one it throws away, and so that "Thai works out of
        /// the box" is true rather than an instruction in a README.
        /// </summary>
        public static void EnsureDefaults()
        {
            if (s_installedDefaults) return;
            s_installedDefaults = true;
            if (!s_lists.ContainsKey("Thai")) s_lists["Thai"] = BuiltInThai();
        }

        /// <summary>
        /// Forgets every list, including the built-in ones, and stops them
        /// being reinstalled. For tests, and for a project that would rather
        /// leave Thai unbroken than break it with a partial dictionary.
        /// </summary>
        public static void ClearAll()
        {
            s_lists.Clear();
            s_installedDefaults = true;
        }

        /// <summary>Back to just the built-in lists. For tests.</summary>
        public static void ResetToDefaults()
        {
            s_lists.Clear();
            s_installedDefaults = false;
            EnsureDefaults();
        }

        /// <summary>
        /// Registers the word list for a script: "Thai", "Lao", "Khmer",
        /// "Myanmar". Replaces any previous list, so loading the full ICU
        /// dictionary over the built-in starter is one call.
        /// </summary>
        public static void SetWordList(string script, WordList words)
        {
            if (string.IsNullOrEmpty(script)) return;
            s_installedDefaults = true; // an explicit list is never overwritten by a default
            if (words == null) s_lists.Remove(script);
            else s_lists[script] = words;
        }

        /// <summary>The list in force for a script, or null.</summary>
        public static WordList GetWordList(string script) =>
            s_lists.TryGetValue(script, out var words) ? words : null;

        /// <summary>True if any script has a dictionary loaded.</summary>
        public static bool HasAnyWordList => s_lists.Count > 0;

        /// <summary>Scripts this handles at all, dictionary or not.</summary>
        public static string ScriptOf(char c)
        {
            if (c >= '฀' && c <= '๿') return "Thai";
            if (c >= '຀' && c <= '໿') return "Lao";
            if (c >= 'ក' && c <= '៿') return "Khmer";
            if (c >= 'က' && c <= '႟') return "Myanmar";
            return null;
        }

        /// <summary>True for the scripts UAX #14 defers (class SA).</summary>
        public static bool NeedsDictionary(char c) => ScriptOf(c) != null;

        /// <summary>
        /// Adds break opportunities inside runs of dictionary-needing script.
        ///
        /// This only ever <em>adds</em> opportunities, and only inside a run of
        /// one such script: UAX #14 already decided everything else, and a
        /// tailoring that overrode it would break text far away from Thai.
        ///
        /// A run the dictionary cannot segment is left alone rather than broken
        /// arbitrarily. Wrapping Thai in the wrong place is what this exists to
        /// fix; inventing a wrong place of our own would be no better.
        /// </summary>
        public static void Apply(string text, LineBreaker.Opportunity[] opportunities)
        {
            EnsureDefaults();
            if (string.IsNullOrEmpty(text) || s_lists.Count == 0) return;

            int n = Math.Min(text.Length, opportunities.Length - 1);
            for (int start = 0; start < n;)
            {
                string script = ScriptOf(text[start]);
                if (script == null) { start++; continue; }

                int end = start;
                while (end < n && ScriptOf(text[end]) == script) end++;

                var words = GetWordList(script);
                if (words != null) Segment(text, start, end, words, opportunities);
                start = end;
            }
        }

        /// <summary>
        /// Longest match, left to right, with the standard fallback: when
        /// nothing matches, skip one character and try again.
        ///
        /// The fallback matters more than it looks. A real sentence contains
        /// names, numbers and loanwords no dictionary has, and a segmenter that
        /// gives up at the first unknown word produces one unbreakable line,
        /// which is exactly the failure being fixed.
        /// </summary>
        private static void Segment(string text, int start, int end, WordList words,
            LineBreaker.Opportunity[] opportunities)
        {
            int i = start;
            while (i < end)
            {
                int length = words.LongestMatch(text, i, end);
                if (length <= 0) { i++; continue; }

                i += length;
                // A break after the word, never before the run's first character:
                // that boundary belongs to whatever came before it.
                if (i > start && i < end && opportunities[i] == LineBreaker.Opportunity.None)
                    opportunities[i] = LineBreaker.Opportunity.Allowed;
            }
        }

        /// <summary>
        /// A small built-in Thai list, so Thai wraps better than not at all out
        /// of the box.
        ///
        /// It is not a substitute for ICU's ~200 KB dictionary and does not
        /// pretend to be: <see cref="WordList.Coverage"/> on real text will say
        /// so plainly. It exists because "install a dictionary before Thai
        /// works" is a worse first experience than "Thai works, and works better
        /// when you load the full list".
        /// </summary>
        public static WordList BuiltInThai()
        {
            var words = new WordList();
            words.AddAll(string.Join("\n", ThaiStarterWords));
            return words;
        }

        private static readonly string[] ThaiStarterWords =
        {
            "สวัสดี", "ครับ", "ค่ะ", "ขอบคุณ", "ขอโทษ", "ไม่", "ใช่", "และ", "หรือ",
            "แต่", "ที่", "นี้", "นั้น", "เป็น", "อยู่", "คือ", "มี", "ได้", "ไป", "มา",
            "ทำ", "ให้", "กับ", "จาก", "ของ", "ใน", "บน", "ก็", "จะ", "ว่า", "คน",
            "เรา", "คุณ", "เขา", "ฉัน", "ผม", "มัน", "เวลา", "วัน", "ปี", "เดือน",
            "ภาษา", "ไทย", "ประเทศ", "เมือง", "บ้าน", "โรงเรียน", "หนังสือ", "เกม",
            "เล่น", "ดู", "กิน", "นอน", "พูด", "อ่าน", "เขียน", "เรียน", "รู้", "คิด",
            "ชอบ", "รัก", "ดี", "ใหม่", "เก่า", "ใหญ่", "เล็ก", "มาก", "น้อย", "เร็ว",
            "ช้า", "สูง", "ต่ำ", "ยาว", "สั้น", "เริ่ม", "จบ", "ต่อ", "แล้ว", "ยัง",
            "ตอน", "เมื่อ", "ถ้า", "เพราะ", "เพื่อ", "โดย", "ตาม", "ทุก", "บาง", "อื่น",
        };
    }
}
