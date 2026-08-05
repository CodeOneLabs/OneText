using System.Collections.Generic;
using UnityEngine;

namespace OneText.Unicode
{
    /// <summary>
    /// Full UAX #9 Unicode Bidirectional Algorithm: explicit embeddings and
    /// isolates (X1-X10), weak types (W1-W7), paired brackets (N0/BD16),
    /// neutrals (N1-N2), implicit levels (I1-I2) and reordering (L1-L2).
    /// Validated against the complete BidiCharacterTest.txt (UCD 17.0.0).
    /// </summary>
    public static class BidiAlgorithm
    {
        /// <summary>Paragraph direction: determine from first strong character (P2/P3).</summary>
        public const byte AutoDirection = 2;

        private const int MaxDepth = 125;
        private const BidiClass NoOverride = (BidiClass)255;

        private struct Status
        {
            public byte Level;
            public BidiClass Override;
            public bool Isolate;
        }

        /// <summary>
        /// Resolves embedding levels and visual order for one paragraph.
        /// </summary>
        /// <param name="codepoints">Paragraph text as codepoints.</param>
        /// <param name="paragraphDirection">0 = LTR, 1 = RTL, 2 = auto (P2/P3).</param>
        /// <param name="levels">Out: resolved level per character.</param>
        /// <param name="removed">Out: true for characters removed by X9 (embedding controls, BN).</param>
        /// <param name="visualOrder">Out: logical indices in visual order (removed chars excluded).</param>
        /// <returns>The resolved paragraph embedding level.</returns>
        public static byte Resolve(IReadOnlyList<int> codepoints, byte paragraphDirection,
            byte[] levels, bool[] removed, List<int> visualOrder)
        {
            int n = codepoints.Count;
            var types = Scratch(ref s_types, n);
            var orig = Scratch(ref s_orig, n);
            bool plain = true;
            for (int i = 0; i < n; i++)
            {
                var cls = BidiData.GetClass(codepoints[i]);
                orig[i] = types[i] = cls;
                plain &= !IsDirectionallyHard(cls);
            }

            // P2/P3
            byte para = paragraphDirection == AutoDirection
                ? (byte)(FirstStrong(orig, 0, n) == 1 ? 1 : 0)
                : paragraphDirection;

            // Text with nothing right-to-left in it, in a left-to-right
            // paragraph, resolves to level 0 everywhere, and the algorithm
            // below is an expensive way to write that down. Without R, AL or
            // AN there are no odd levels and I1 never lifts a number to level
            // 2 (W7 turns every EN into L first), so the whole resolution is a
            // constant. Latin, CJK, digits and punctuation — most game text —
            // take this path and allocate nothing.
            if (plain && para == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    levels[i] = 0;
                    removed[i] = false;
                }
                visualOrder.Clear();
                for (int i = 0; i < n; i++) visualOrder.Add(i);
                return 0;
            }

            // X1-X8
            var stack = new List<Status> { new Status { Level = para, Override = NoOverride } };
            int overflowIso = 0, overflowEmb = 0, validIso = 0;

            for (int i = 0; i < n; i++)
            {
                var t = orig[i];
                switch (t)
                {
                    case BidiClass.RLE:
                    case BidiClass.LRE:
                    case BidiClass.RLO:
                    case BidiClass.LRO:
                    {
                        removed[i] = true;
                        levels[i] = Top(stack).Level;
                        bool odd = t == BidiClass.RLE || t == BidiClass.RLO;
                        int lvl = NextLevel(Top(stack).Level, odd);
                        if (lvl <= MaxDepth && overflowIso == 0 && overflowEmb == 0)
                        {
                            var over = t == BidiClass.RLO ? BidiClass.R
                                : t == BidiClass.LRO ? BidiClass.L : NoOverride;
                            stack.Add(new Status { Level = (byte)lvl, Override = over });
                        }
                        else if (overflowIso == 0)
                        {
                            overflowEmb++;
                        }
                        break;
                    }
                    case BidiClass.LRI:
                    case BidiClass.RLI:
                    case BidiClass.FSI:
                    {
                        bool odd = t == BidiClass.RLI ||
                            (t == BidiClass.FSI && FirstStrong(orig, i + 1, MatchingPdi(orig, i)) == 1);
                        levels[i] = Top(stack).Level;
                        if (Top(stack).Override != NoOverride) types[i] = Top(stack).Override;
                        int lvl = NextLevel(Top(stack).Level, odd);
                        if (lvl <= MaxDepth && overflowIso == 0 && overflowEmb == 0)
                        {
                            validIso++;
                            stack.Add(new Status { Level = (byte)lvl, Override = NoOverride, Isolate = true });
                        }
                        else
                        {
                            overflowIso++;
                        }
                        break;
                    }
                    case BidiClass.PDI:
                    {
                        if (overflowIso > 0)
                        {
                            overflowIso--;
                        }
                        else if (validIso > 0)
                        {
                            overflowEmb = 0;
                            while (!Top(stack).Isolate) stack.RemoveAt(stack.Count - 1);
                            stack.RemoveAt(stack.Count - 1);
                            validIso--;
                        }
                        levels[i] = Top(stack).Level;
                        if (Top(stack).Override != NoOverride) types[i] = Top(stack).Override;
                        break;
                    }
                    case BidiClass.PDF:
                    {
                        removed[i] = true;
                        levels[i] = Top(stack).Level;
                        if (overflowIso > 0) { }
                        else if (overflowEmb > 0) overflowEmb--;
                        else if (!Top(stack).Isolate && stack.Count >= 2) stack.RemoveAt(stack.Count - 1);
                        break;
                    }
                    case BidiClass.B:
                        levels[i] = para;
                        break;
                    case BidiClass.BN:
                        removed[i] = true;
                        levels[i] = Top(stack).Level;
                        break;
                    default:
                        levels[i] = Top(stack).Level;
                        if (Top(stack).Override != NoOverride) types[i] = Top(stack).Override;
                        break;
                }
            }

            // Embedding levels after the X phase; sos/eos and I-rule parity
            // must not observe I1/I2 adjustments made to earlier sequences.
            var xlevels = Scratch(ref s_xlevels, n);
            System.Array.Copy(levels, xlevels, n);

            // X10 / BD13: isolating run sequences
            var runs = new List<List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (removed[i]) continue;
                if (runs.Count > 0 && levels[runs[^1][^1]] == levels[i]) runs[^1].Add(i);
                else runs.Add(new List<int> { i });
            }

            var runByStart = new Dictionary<int, int>();
            for (int ri = 0; ri < runs.Count; ri++) runByStart[runs[ri][0]] = ri;

            var used = new bool[runs.Count];
            var sequences = new List<List<int>>();
            for (int ri = 0; ri < runs.Count; ri++)
            {
                if (used[ri]) continue;
                var seq = new List<int>(runs[ri]);
                used[ri] = true;
                while (IsIsolateInitiator(orig[seq[^1]]))
                {
                    int pdi = MatchingPdi(orig, seq[^1]);
                    if (pdi >= n || removed[pdi]) break;
                    if (!runByStart.TryGetValue(pdi, out int nri) || used[nri]) break;
                    seq.AddRange(runs[nri]);
                    used[nri] = true;
                }
                sequences.Add(seq);
            }

            foreach (var seq in sequences)
                ResolveSequence(codepoints, seq, orig, types, xlevels, levels, removed, para, n);

            // L1
            for (int i = 0; i < n; i++)
            {
                if (orig[i] == BidiClass.B || orig[i] == BidiClass.S)
                {
                    levels[i] = para;
                    for (int j = i - 1; j >= 0 && IsResetWhitespace(orig[j], removed[j]); j--)
                        levels[j] = para;
                }
            }
            for (int j = n - 1; j >= 0 && IsResetWhitespace(orig[j], removed[j]); j--)
                levels[j] = para;

            // L2
            visualOrder.Clear();
            for (int i = 0; i < n; i++)
                if (!removed[i]) visualOrder.Add(i);
            if (visualOrder.Count > 0)
            {
                int maxLvl = 0, minOdd = MaxDepth + 2;
                foreach (int i in visualOrder)
                {
                    if (levels[i] > maxLvl) maxLvl = levels[i];
                    int odd = levels[i] | 1;
                    if (odd < minOdd) minOdd = odd;
                }
                for (int lvl = maxLvl; lvl >= minOdd; lvl--)
                {
                    for (int k = 0; k < visualOrder.Count;)
                    {
                        if (levels[visualOrder[k]] >= lvl)
                        {
                            int j = k;
                            while (j < visualOrder.Count && levels[visualOrder[j]] >= lvl) j++;
                            visualOrder.Reverse(k, j - k);
                            k = j;
                        }
                        else
                        {
                            k++;
                        }
                    }
                }
            }

            return para;
        }

        private static void ResolveSequence(IReadOnlyList<int> cps, List<int> seq,
            BidiClass[] orig, BidiClass[] types, byte[] xlevels, byte[] levels,
            bool[] removed, byte para, int n)
        {
            byte lvl = xlevels[seq[0]];

            int prev = -1;
            for (int j = seq[0] - 1; j >= 0; j--)
                if (!removed[j]) { prev = j; break; }
            var sos = DirOf(System.Math.Max(lvl, prev >= 0 ? xlevels[prev] : para));

            BidiClass eos;
            int last = seq[^1];
            if (IsIsolateInitiator(orig[last]) && MatchingPdi(orig, last) >= n)
            {
                eos = DirOf(System.Math.Max(lvl, para));
            }
            else
            {
                int nxt = -1;
                for (int j = last + 1; j < n; j++)
                    if (!removed[j]) { nxt = j; break; }
                eos = DirOf(System.Math.Max(lvl, nxt >= 0 ? xlevels[nxt] : para));
            }

            // W1
            var prevType = sos;
            foreach (int i in seq)
            {
                if (types[i] == BidiClass.NSM)
                    types[i] = (IsIsolateInitiator(prevType) || prevType == BidiClass.PDI)
                        ? BidiClass.ON : prevType;
                prevType = (IsIsolateInitiator(orig[i]) || orig[i] == BidiClass.PDI)
                    ? orig[i] : types[i];
            }

            // W2
            var strong = sos;
            foreach (int i in seq)
            {
                if (types[i] == BidiClass.L || types[i] == BidiClass.R || types[i] == BidiClass.AL)
                    strong = types[i];
                else if (types[i] == BidiClass.EN && strong == BidiClass.AL)
                    types[i] = BidiClass.AN;
            }

            // W3
            foreach (int i in seq)
                if (types[i] == BidiClass.AL) types[i] = BidiClass.R;

            // W4
            for (int k = 1; k < seq.Count - 1; k++)
            {
                var a = types[seq[k - 1]];
                var b = types[seq[k]];
                var c = types[seq[k + 1]];
                if (b == BidiClass.ES && a == BidiClass.EN && c == BidiClass.EN)
                    types[seq[k]] = BidiClass.EN;
                else if (b == BidiClass.CS && a == c && (a == BidiClass.EN || a == BidiClass.AN))
                    types[seq[k]] = a;
            }

            // W5
            for (int k = 0; k < seq.Count;)
            {
                if (types[seq[k]] == BidiClass.ET)
                {
                    int j = k;
                    while (j < seq.Count && types[seq[j]] == BidiClass.ET) j++;
                    var before = k > 0 ? types[seq[k - 1]] : sos;
                    var after = j < seq.Count ? types[seq[j]] : eos;
                    if (before == BidiClass.EN || after == BidiClass.EN)
                        for (int m = k; m < j; m++) types[seq[m]] = BidiClass.EN;
                    k = j;
                }
                else
                {
                    k++;
                }
            }

            // W6
            foreach (int i in seq)
                if (types[i] == BidiClass.ES || types[i] == BidiClass.ET || types[i] == BidiClass.CS)
                    types[i] = BidiClass.ON;

            // W7
            strong = sos;
            foreach (int i in seq)
            {
                if (types[i] == BidiClass.L || types[i] == BidiClass.R)
                    strong = types[i];
                else if (types[i] == BidiClass.EN && strong == BidiClass.L)
                    types[i] = BidiClass.L;
            }

            ResolveBrackets(cps, seq, orig, types, lvl, sos);

            // N1/N2
            var e = DirOf(lvl);
            for (int k = 0; k < seq.Count;)
            {
                if (IsNeutralOrIsolate(types[seq[k]]))
                {
                    int j = k;
                    while (j < seq.Count && IsNeutralOrIsolate(types[seq[j]])) j++;
                    var before = k > 0 ? types[seq[k - 1]] : sos;
                    var after = j < seq.Count ? types[seq[j]] : eos;
                    var bs = StrongDir(before);
                    var asd = StrongDir(after);
                    var fill = (bs != NoOverride && bs == asd) ? bs : e;
                    for (int m = k; m < j; m++) types[seq[m]] = fill;
                    k = j;
                }
                else
                {
                    k++;
                }
            }

            // I1/I2
            foreach (int i in seq)
            {
                var t = types[i];
                if ((xlevels[i] & 1) == 0)
                {
                    if (t == BidiClass.R) levels[i] += 1;
                    else if (t == BidiClass.AN || t == BidiClass.EN) levels[i] += 2;
                }
                else
                {
                    if (t == BidiClass.L || t == BidiClass.EN || t == BidiClass.AN) levels[i] += 1;
                }
            }
        }

        // N0 / BD16
        private static void ResolveBrackets(IReadOnlyList<int> cps, List<int> seq,
            BidiClass[] orig, BidiClass[] types, byte lvl, BidiClass sos)
        {
            var e = DirOf(lvl);
            var o = e == BidiClass.L ? BidiClass.R : BidiClass.L;

            var stackClose = new List<int>();   // expected canonical closing cp
            var stackIndex = new List<int>();   // seq index of the opener
            var pairs = new List<(int open, int close)>();

            for (int si = 0; si < seq.Count; si++)
            {
                int cp = cps[seq[si]];
                if (types[seq[si]] != BidiClass.ON) continue;
                if (!BidiData.TryGetBracket(cp, out int paired, out bool isOpen)) continue;
                if (isOpen)
                {
                    if (stackClose.Count >= 63) break; // BD16: stop on overflow
                    stackClose.Add(BidiData.CanonicalBracket(paired));
                    stackIndex.Add(si);
                }
                else
                {
                    int canon = BidiData.CanonicalBracket(cp);
                    for (int d = stackClose.Count - 1; d >= 0; d--)
                    {
                        if (stackClose[d] == canon)
                        {
                            pairs.Add((stackIndex[d], si));
                            stackClose.RemoveRange(d, stackClose.Count - d);
                            stackIndex.RemoveRange(d, stackIndex.Count - d);
                            break;
                        }
                    }
                }
            }

            pairs.Sort((x, y) => x.open.CompareTo(y.open));

            foreach (var (open, close) in pairs)
            {
                bool foundE = false, foundO = false;
                for (int m = open + 1; m < close; m++)
                {
                    var st = StrongDir(types[seq[m]]);
                    if (st == e) { foundE = true; break; }
                    if (st == o) foundO = true;
                }

                BidiClass resolved;
                if (foundE)
                {
                    resolved = e;
                }
                else if (foundO)
                {
                    var context = sos;
                    for (int m = open - 1; m >= 0; m--)
                    {
                        var st = StrongDir(types[seq[m]]);
                        if (st != NoOverride) { context = st; break; }
                    }
                    resolved = context == o ? o : e;
                }
                else
                {
                    continue;
                }

                types[seq[open]] = resolved;
                types[seq[close]] = resolved;
                foreach (int b in new[] { open, close })
                {
                    for (int m = b + 1; m < seq.Count && orig[seq[m]] == BidiClass.NSM; m++)
                        types[seq[m]] = resolved;
                }
            }
        }

        private static Status Top(List<Status> stack) => stack[^1];

        private static int NextLevel(int current, bool odd) =>
            odd ? (current + 1) | 1 : (current + 2) & ~1;

        private static BidiClass DirOf(int level) => (level & 1) != 0 ? BidiClass.R : BidiClass.L;

        /// <summary>
        /// True for a class whose presence means the full resolution has to
        /// run: anything right-to-left, Arabic numbers (which take level 2
        /// even beside Latin), and the explicit embedding and isolate controls.
        /// </summary>
        private static bool IsDirectionallyHard(BidiClass cls)
        {
            switch (cls)
            {
                case BidiClass.R:
                case BidiClass.AL:
                case BidiClass.AN:
                case BidiClass.BN:
                case BidiClass.LRE:
                case BidiClass.RLE:
                case BidiClass.LRO:
                case BidiClass.RLO:
                case BidiClass.PDF:
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI:
                case BidiClass.PDI:
                    return true;
                default:
                    return false;
            }
        }

        // Per-call scratch, reused: laying out a screen of labels calls this
        // once per label per frame, and three arrays each time is a collection
        // pause waiting to happen.
        [System.ThreadStatic] private static BidiClass[] s_types;
        [System.ThreadStatic] private static BidiClass[] s_orig;
        [System.ThreadStatic] private static byte[] s_xlevels;

        private static T[] Scratch<T>(ref T[] array, int length)
        {
            if (array == null || array.Length < length)
                array = new T[Mathf.NextPowerOfTwo(Mathf.Max(length, 32))];
            return array;
        }

        private static bool IsIsolateInitiator(BidiClass t) =>
            t == BidiClass.LRI || t == BidiClass.RLI || t == BidiClass.FSI;

        private static bool IsNeutralOrIsolate(BidiClass t) =>
            t == BidiClass.B || t == BidiClass.S || t == BidiClass.WS || t == BidiClass.ON ||
            t == BidiClass.LRI || t == BidiClass.RLI || t == BidiClass.FSI || t == BidiClass.PDI;

        private static bool IsResetWhitespace(BidiClass origType, bool isRemoved) =>
            isRemoved || origType == BidiClass.WS ||
            origType == BidiClass.LRI || origType == BidiClass.RLI ||
            origType == BidiClass.FSI || origType == BidiClass.PDI;

        /// <summary>L for strong L; R for R/EN/AN; NoOverride sentinel otherwise.</summary>
        private static BidiClass StrongDir(BidiClass t)
        {
            if (t == BidiClass.L) return BidiClass.L;
            if (t == BidiClass.R || t == BidiClass.EN || t == BidiClass.AN) return BidiClass.R;
            return NoOverride;
        }

        /// <summary>P2: first strong class skipping isolate runs; 0 = L, 1 = R/AL, -1 = none.</summary>
        private static int FirstStrong(BidiClass[] orig, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                var t = orig[i];
                if (depth == 0)
                {
                    if (t == BidiClass.L) return 0;
                    if (t == BidiClass.R || t == BidiClass.AL) return 1;
                }
                if (IsIsolateInitiator(t)) depth++;
                else if (t == BidiClass.PDI && depth > 0) depth--;
            }
            return -1;
        }

        private static int MatchingPdi(BidiClass[] orig, int i)
        {
            int depth = 1;
            for (int j = i + 1; j < orig.Length; j++)
            {
                if (IsIsolateInitiator(orig[j])) depth++;
                else if (orig[j] == BidiClass.PDI && --depth == 0) return j;
            }
            return orig.Length;
        }
    }
}
