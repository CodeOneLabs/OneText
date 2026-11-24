using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using OneText.Native;

namespace OneText.Tests
{
    /// <summary>
    /// <see cref="Shaper.InertShortcut"/> answers some runs without HarfBuzz.
    /// The claim is that nobody can tell: for every run it answers, the glyph
    /// ids, clusters, advances and offsets are exactly what <c>hb_shape</c>
    /// returns. <see cref="InertGlyphs"/> argues why; these tests hold it to
    /// that, on every font this repository carries (and every system font of
    /// the machine running them), by shaping both ways:
    ///
    /// - every codepoint the shortcut admits, alone;
    /// - every ordered pair of the admitted printable ASCII, which is where
    ///   kerning and ligatures live;
    /// - thousands of random runs of admitted characters, cut out of a longer
    ///   text with random context on both sides, in random languages (locl);
    /// - the same runs with one character the shortcut must refuse inserted,
    ///   which must leave the output untouched and go to HarfBuzz;
    /// - a variable font at many axis settings, where the advances move.
    /// </summary>
    public class InertShapingTests
    {
        private const string Fonts = "Packages/com.onetext.core/Tests/Fonts~";
        private const string CoverageFonts = "Packages/com.onetext.core/Tests/CoverageFonts~";

        private static readonly string[] Languages =
        {
            null, "en", "tr", "nl", "ro", "sr", "bg", "ru", "pl", "ca", "de", "el",
            "ja", "ko", "zh-Hans", "zh-Hant", "zh-HK", "vi", "az", "mk", "kk", "x-test",
        };

        // Characters that must never be answered by the shortcut, whatever the
        // font: marks, joiners, selectors, ignorables, the fraction slash, jamo,
        // tone marks, a Thai vowel, a lone surrogate, a Coptic letter.
        private static readonly string[] Poison =
        {
            "\u0301", "\u0308", "\u200D", "\u200C", "\u00AD", "\uFE0F", "\uFE00", "\u2044", "\u2060",
            "\u1100", "\u1161", "\u11A8", "\u302E", "\u3099", "\uFF9E", "\u0E33", "\uD800", "\uDC00",
            "\u03E2", "\u200B", "\u2028", "\u202A", "\u2066", "\uFEFF", "\u0009", "\u0627", "\u05D0",
            "\u0915", "\U0001F3FB", "\U0001F1E6", "\U000E0041", "\u3164", "\uFFA0", "\u115F",
        };

        [TearDown]
        public void Restore() => Shaper.InertShortcut = true;

        private static IEnumerable<string> RepositoryFonts()
        {
            foreach (var dir in new[] { Fonts, CoverageFonts })
            {
                var full = Path.GetFullPath(dir);
                if (!Directory.Exists(full)) continue;
                var files = new List<string>(Directory.GetFiles(full));
                files.Sort(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".ttf" || ext == ".otf" || ext == ".ttc") yield return file;
                }
            }
        }

        [Test]
        public void Every_Repository_Font_Shapes_Identically_Both_Ways()
        {
            var report = new StringBuilder();
            int fonts = 0, eligible = 0;
            long singles = 0, runs = 0, hits = 0;
            foreach (var path in RepositoryFonts())
            {
                using var font = FontData.LoadFile(path);
                var stats = Exercise(font, Path.GetFileName(path), runsPerFont: 1500, everySingle: true);
                fonts++;
                if (stats.Eligible) eligible++;
                singles += stats.Singles;
                runs += stats.Runs;
                hits += stats.Hits;
                report.AppendLine($"{Path.GetFileName(path)}: {stats.Summary}");
            }
            UnityEngine.Debug.Log($"[inert] {fonts} fonts, {eligible} analysed, {singles} single codepoints and " +
                $"{runs} runs compared, {hits} answered by the shortcut\n{report}");
            Assert.Greater(fonts, 200, "the coverage fonts are missing");
            Assert.Greater(eligible, 150);
            Assert.Greater(hits, 100000, "the shortcut fired too rarely for this to prove much");
        }

        [Test]
        public void Every_System_Font_Shapes_Identically_Both_Ways()
        {
            var dirs = new[] { "/System/Library/Fonts", "/Library/Fonts", "C:/Windows/Fonts", "/usr/share/fonts" };
            var report = new StringBuilder();
            int faces = 0, eligible = 0;
            long hits = 0;
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                var files = new List<string>(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
                files.Sort(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".ttf" && ext != ".otf" && ext != ".ttc") continue;
                    for (uint face = 0; face < 32; face++)
                    {
                        FontData font;
                        try { font = FontData.LoadFile(file, face); }
                        catch (Exception) { break; }
                        using (font)
                        {
                            if (!font.IsValid || font.UnitsPerEm == 0 || (face > 0 && !HasGlyphs(font))) break;
                            var stats = Exercise(font, $"{Path.GetFileName(file)}#{face}", runsPerFont: 300, everySingle: false);
                            faces++;
                            if (stats.Eligible) eligible++;
                            hits += stats.Hits;
                            report.AppendLine($"{Path.GetFileName(file)}#{face}: {stats.Summary}");
                        }
                        if (ext != ".ttc") break;
                    }
                }
            }
            UnityEngine.Debug.Log($"[inert] system: {faces} faces, {eligible} analysed, {hits} runs answered\n{report}");
            if (faces == 0) Assert.Ignore("no system fonts on this machine");
        }

        // A face index past the end of a collection loads as an empty face.
        private static bool HasGlyphs(FontData font) =>
            HarfBuzzApi.hb_font_get_nominal_glyph(font.Font, 'a', out _) != 0 ||
            HarfBuzzApi.hb_font_get_nominal_glyph(font.Font, 0x4E00, out _) != 0 ||
            HarfBuzzApi.hb_font_get_nominal_glyph(font.Font, 0x30, out _) != 0;

        [Test]
        public void A_Variable_Font_Shapes_Identically_At_Every_Setting()
        {
            foreach (var name in new[] { "NotoSansVariable.ttf", "NotoSans.ttf" })
            {
                using var font = FontData.LoadFile(Path.GetFullPath($"{Fonts}/{name}"));
                var axes = font.GetVariationAxes();
                Assert.Greater(axes.Length, 0, $"{name} is expected to be variable");
                var random = new Random(name.GetHashCode());
                long hits = 0;
                int before = font.Generation;
                for (int setting = 0; setting < 24; setting++)
                {
                    var variations = new FontVariation[axes.Length];
                    for (int a = 0; a < axes.Length; a++)
                    {
                        float t = setting == 0 ? 0f : setting == 1 ? 1f : (float)random.NextDouble();
                        variations[a] = new FontVariation(axes[a].Tag, axes[a].Minimum + t * (axes[a].Maximum - axes[a].Minimum));
                    }
                    font.SetVariations(variations);
                    var stats = Exercise(font, $"{name} setting {setting}", runsPerFont: 400, everySingle: true);
                    hits += stats.Hits;
                }
                Assert.Greater(font.Generation, before);
                Assert.Greater(hits, 1000, $"{name}: the shortcut never fired on a variable font");
            }
        }

        [Test]
        public void Advances_Follow_A_Variation_Change()
        {
            using var font = FontData.LoadFile(Path.GetFullPath($"{Fonts}/NotoSansVariable.ttf"));
            using var shaper = new Shaper();
            var light = new List<ShapedGlyph>();
            var heavy = new List<ShapedGlyph>();
            const string text = "mmm";
            var axes = font.GetVariationAxes();
            int wght = Array.FindIndex(axes, a => a.Tag == "wght");
            Assert.GreaterOrEqual(wght, 0);
            font.SetVariations(new FontVariation("wght", axes[wght].Minimum));
            shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, light);
            Assert.IsTrue(font.InertGlyphs.Eligible);
            font.SetVariations(new FontVariation("wght", axes[wght].Maximum));
            shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, heavy);
            Assert.AreNotEqual(light[0].XAdvance, heavy[0].XAdvance,
                "the advance of 'm' did not move with the weight axis: the analysis outlived its generation");
            var expected = new List<ShapedGlyph>();
            Shaper.InertShortcut = false;
            shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, expected);
            CollectionAssert.AreEqual(expected, heavy);
        }

        [Test]
        public void Every_Digit_Of_The_Benchmark_Latin_Font_Is_Inert()
        {
            // Noto Sans's '0' begins a ccmp ligature, zero + U+FE00 (its slashed
            // form). U+FE00 is never admitted, so the ligature cannot complete in
            // an answered run, and '0' stays a plain digit; without that
            // refinement every number with a zero in it would go to HarfBuzz.
            using var font = FontData.LoadFile(Path.GetFullPath($"{Fonts}/NotoSans.ttf"));
            var probe = new List<ShapedGlyph>();
            Assert.IsTrue(font.InertGlyphs.TryShape("0123456789".AsSpan(), 0, 10, probe));
            Assert.AreEqual(10, probe.Count);
            // ...and the ligature itself still goes to HarfBuzz and still forms.
            probe.Clear();
            Assert.IsFalse(font.InertGlyphs.TryShape("0\uFE00".AsSpan(), 0, 2, probe));
            using var shaper = new Shaper();
            shaper.Shape(font, "0\uFE00", 0, 2, Shaper.Direction.LeftToRight, probe);
            Assert.AreEqual(1, probe.Count, "zero + VS1 should ligate to the slashed zero");
        }

        [Test]
        public void Only_Left_To_Right_Runs_Are_Answered()
        {
            using var font = FontData.LoadFile(Path.GetFullPath($"{CoverageFonts}/NotoSansCJKkr-Regular.otf"));
            using var shaper = new Shaper();
            const string text = "한국어";
            foreach (Shaper.Direction direction in Enum.GetValues(typeof(Shaper.Direction)))
            {
                var expected = new List<ShapedGlyph>();
                var actual = new List<ShapedGlyph>();
                Shaper.InertShortcut = false;
                shaper.Shape(font, text, 0, text.Length, direction, expected, null);
                Shaper.InertShortcut = true;
                shaper.Shape(font, text, 0, text.Length, direction, actual, null);
                CollectionAssert.AreEqual(expected, actual, direction.ToString());
            }
            var probe = new List<ShapedGlyph>();
            Assert.IsTrue(font.InertGlyphs.TryShape(text.AsSpan(), 0, text.Length, probe),
                "precomposed Hangul is expected to be inert in Noto Sans CJK KR");
        }

        [Test]
        public void Refused_Characters_Are_Never_Answered()
        {
            foreach (var s in Poison)
            {
                if (s.Length == 1 && char.IsSurrogate(s[0])) continue;
                int cp = char.ConvertToUtf32(s, 0);
                Assert.IsFalse(InertGlyphs.IsPlainCharacter(cp), $"U+{cp:X4} is admitted");
            }
            // The digits and Hangul the benchmarks are made of are admitted; whether
            // they are inert is the font's business.
            foreach (int cp in new[] { '0', '9', 'A', ' ', 0xAC00, 0xD7A3, 0x4E00, 0x3042, 0x30A2, 0x00E9 })
                Assert.IsTrue(InertGlyphs.IsPlainCharacter(cp), $"U+{cp:X4}");
        }

        [Test]
        public void The_Shortcut_Allocates_Nothing_Once_Warm()
        {
            using var font = FontData.LoadFile(Path.GetFullPath($"{CoverageFonts}/NotoSansCJKkr-Regular.otf"));
            using var shaper = new Shaper();
            var output = new List<ShapedGlyph>(64);
            const string text = "안녕하세요세계";
            for (int i = 0; i < 10; i++) { output.Clear(); shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, output, "ko"); }
            Assert.IsTrue(font.InertGlyphs.TryShape(text.AsSpan(), 0, text.Length, new List<ShapedGlyph>()));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { output.Clear(); shaper.Shape(font, text, 0, text.Length, Shaper.Direction.LeftToRight, output, "ko"); }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - before, "the warm shortcut allocated");
        }

        // ------------------------------------------------------------------

        private struct Stats
        {
            public bool Eligible;
            public long Singles, Runs, Hits;
            public string Summary;
        }

        private static Stats Exercise(FontData font, string name, int runsPerFont, bool everySingle)
        {
            var stats = new Stats();
            var table = font.InertGlyphs;
            stats.Eligible = table.Eligible;
            if (!table.Eligible)
            {
                stats.Summary = "not analysed: " + table.Refusal;
                return stats;
            }

            using var shaper = new Shaper();
            var expected = new List<ShapedGlyph>(64);
            var actual = new List<ShapedGlyph>(64);
            var probe = new List<ShapedGlyph>(4);
            var buffer = new char[2];

            // Every admitted codepoint, alone.
            var inert = new List<int>();
            var refused = new List<int>();
            var random = new Random(name.GetHashCode());
            for (int cp = 0; cp < 0x323B0; cp++)
            {
                if (!InertGlyphs.IsPlainCharacter(cp)) continue;
                int n = Encode(cp, buffer);
                probe.Clear();
                bool hit = table.TryShape(new ReadOnlySpan<char>(buffer, 0, n), 0, n, probe);
                if (!hit)
                {
                    Assert.AreEqual(0, probe.Count, $"{name}: a refusal wrote output");
                    if (HarfBuzzApi.hb_font_get_nominal_glyph(font.Font, (uint)cp, out uint g) != 0 && g != 0)
                        refused.Add(cp);
                    continue;
                }
                inert.Add(cp);
                if (!everySingle && random.Next(16) != 0) continue;
                var text = new string(buffer, 0, n);
                Compare(shaper, font, text, 0, n, null, expected, actual, name);
                stats.Singles++;
                stats.Hits++;
            }

            var ascii = inert.FindAll(cp => cp < 0x80);
            foreach (int a in ascii)
            foreach (int b in ascii)
            {
                string text = $"{(char)a}{(char)b}";
                Compare(shaper, font, text, 0, 2, null, expected, actual, name);
                stats.Runs++;
                stats.Hits++;
            }

            if (inert.Count == 0)
            {
                stats.Summary = $"analysed, nothing inert ({refused.Count} mapped characters refused)";
                return stats;
            }

            // Random runs inside random context, in random languages; and the
            // same with a refused character inserted.
            var builder = new StringBuilder();
            for (int r = 0; r < runsPerFont; r++)
            {
                builder.Clear();
                int prefix = random.Next(4);
                for (int i = 0; i < prefix; i++) AppendContext(builder, random, inert, refused);
                int start = builder.Length;
                int length = 1 + random.Next(random.Next(4) == 0 ? 64 : 12);
                // Favour the characters real text repeats, so pairs recur.
                var pool = ascii.Count > 0 && random.Next(3) == 0 ? ascii : inert;
                int window = Math.Min(pool.Count, 1 + random.Next(Math.Max(1, Math.Min(pool.Count, 40))));
                int offset = random.Next(pool.Count - window + 1);
                for (int i = 0; i < length; i++) AppendCodepoint(builder, pool[offset + random.Next(window)]);
                int end = builder.Length;
                int suffix = random.Next(4);
                for (int i = 0; i < suffix; i++) AppendContext(builder, random, inert, refused);
                string text = builder.ToString();
                string language = Languages[random.Next(Languages.Length)];

                Compare(shaper, font, text, start, end - start, language, expected, actual, name);
                stats.Runs++;
                stats.Hits++;

                // One refused character anywhere in the run.
                int at = start + random.Next(end - start + 1);
                while (at > start && at < end && char.IsLowSurrogate(text[at])) at--;
                string poison = refused.Count > 0 && random.Next(2) == 0
                    ? char.ConvertFromUtf32(refused[random.Next(refused.Count)])
                    : Poison[random.Next(Poison.Length)];
                string mixed = text.Insert(at, poison);
                probe.Clear();
                Assert.IsFalse(table.TryShape(mixed.AsSpan(), start, end - start + poison.Length, probe),
                    $"{name}: a run containing {Describe(poison, 0, poison.Length)} was answered");
                Assert.AreEqual(0, probe.Count);
                CompareAnyway(shaper, font, mixed, start, end - start + poison.Length, language, expected, actual, name);
                stats.Runs++;
            }

            stats.Summary = $"{inert.Count} inert, {refused.Count} mapped but refused, {stats.Runs} runs";
            return stats;
        }

        private static void AppendContext(StringBuilder builder, Random random, List<int> inert, List<int> refused)
        {
            int roll = random.Next(3);
            if (roll == 0) AppendCodepoint(builder, inert[random.Next(inert.Count)]);
            else if (roll == 1 && refused.Count > 0) AppendCodepoint(builder, refused[random.Next(refused.Count)]);
            else builder.Append(Poison[random.Next(Poison.Length)]);
        }

        private static void AppendCodepoint(StringBuilder builder, int cp)
        {
            if (cp < 0x10000) builder.Append((char)cp);
            else builder.Append(char.ConvertFromUtf32(cp));
        }

        private static int Encode(int cp, char[] buffer)
        {
            if (cp < 0x10000) { buffer[0] = (char)cp; return 1; }
            cp -= 0x10000;
            buffer[0] = (char)(0xD800 + (cp >> 10));
            buffer[1] = (char)(0xDC00 + (cp & 0x3FF));
            return 2;
        }

        /// <summary>Shapes both ways and requires the shortcut to have answered.</summary>
        private static void Compare(Shaper shaper, FontData font, string text, int start, int length, string language,
            List<ShapedGlyph> expected, List<ShapedGlyph> actual, string name)
        {
            var probe = new List<ShapedGlyph>();
            Assert.IsTrue(font.InertGlyphs.TryShape(text.AsSpan(), start, length, probe),
                $"{name}: {Describe(text, start, length)} built from inert characters was refused");
            CompareAnyway(shaper, font, text, start, length, language, expected, actual, name);
        }

        private static void CompareAnyway(Shaper shaper, FontData font, string text, int start, int length,
            string language, List<ShapedGlyph> expected, List<ShapedGlyph> actual, string name)
        {
            expected.Clear();
            actual.Clear();
            // Something already in the list: the shortcut appends, as HarfBuzz's copy-out does.
            expected.Add(new ShapedGlyph(1, 2, 3, 4, 5, 6));
            actual.Add(new ShapedGlyph(1, 2, 3, 4, 5, 6));
            Shaper.InertShortcut = false;
            shaper.Shape(font, text, start, length, Shaper.Direction.LeftToRight, expected, language);
            Shaper.InertShortcut = true;
            shaper.Shape(font, text, start, length, Shaper.Direction.LeftToRight, actual, language);
            if (expected.Count != actual.Count)
                Assert.Fail($"{name}: {Describe(text, start, length)} lang {language}: {expected.Count} glyphs from HarfBuzz, {actual.Count} from the shortcut");
            for (int i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var a = actual[i];
                if (e.GlyphId != a.GlyphId || e.Cluster != a.Cluster || e.XAdvance != a.XAdvance ||
                    e.YAdvance != a.YAdvance || e.XOffset != a.XOffset || e.YOffset != a.YOffset)
                    Assert.Fail($"{name}: {Describe(text, start, length)} lang {language} glyph {i}: HarfBuzz {e}, shortcut {a}");
            }
        }

        private static string Describe(string text, int start, int length)
        {
            var b = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (i == start) b.Append('[');
                b.Append($"{(int)text[i]:X4} ");
                if (i == start + length - 1) b.Append(']');
            }
            return b.ToString();
        }
    }
}
