using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// Font residency across every script the package claims, with pictures.
    ///
    /// <para><see cref="FontResidencyTests"/> proves the mechanism on one
    /// Arabic face. This is the claim a general-purpose package actually makes:
    /// a project whose only loaded face is Latin can name a font per script by
    /// key, and a sheet of Cyrillic, Greek, Arabic, Hebrew, three Indic
    /// scripts, Thai, Korean, both Chinese, Japanese, colour emoji and a mixed
    /// bidi line comes out right with nothing preloaded — then gives every one
    /// of those fonts back when the text goes, and draws the same picture when
    /// it comes back.</para>
    ///
    /// <para>Each step writes a PNG to <c>ONETEXT_CAPTURE_OUT</c> (or
    /// <c>onetext-residency</c> in the temp folder) and a log of what was
    /// resident, because "the right font was loaded" and "the text looks
    /// right" are different claims and only the second one is what a reader
    /// sees. Fonts come from the operating system where it has them and from
    /// <c>Tests/CoverageFonts~</c> otherwise; a script with neither is
    /// reported and left out, not failed.</para>
    /// </summary>
    [Category("Captures")]
    public class FontResidencyScriptsTests
    {
        private const int Width = 1200;
        private const float CaptionWidth = 190f;
        private const float SampleSize = 26f;

        private static readonly Dictionary<string, (byte[] bytes, int[] coverage, bool color)> s_files =
            new Dictionary<string, (byte[], int[], bool)>(StringComparer.Ordinal);

        private readonly List<Object> _created = new List<Object>();
        private readonly Dictionary<string, ScriptFont> _fonts = new Dictionary<string, ScriptFont>();
        private readonly List<string> _missing = new List<string>();
        private readonly StringBuilder _log = new StringBuilder();

        // Collected rather than asserted on the spot: a step that goes wrong
        // should still leave the pictures of the steps after it, which are
        // what tells whether the wrong thing is visible.
        private readonly List<string> _problems = new List<string>();
        private OneTextSettings _previousSettings;
        private OneFontAsset _latin;
        private DiskFontSource _source;

        private static string OutputDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("ONETEXT_CAPTURE_OUT");
                return string.IsNullOrEmpty(configured)
                    ? Path.Combine(Path.GetTempPath(), "onetext-residency")
                    : configured;
            }
        }

        [SetUp]
        public void SetUp()
        {
            FontResidency.ResetForTests();
            SystemFonts.Enabled = false;
            // NUnit runs every test of a fixture on one instance.
            _fonts.Clear();
            _missing.Clear();
            _problems.Clear();
            _log.Clear();
            _previousSettings = OneTextSettings.Instance;

            var noto = File.ReadAllBytes(Path.GetFullPath(ScriptFontCatalog.LatinFontPath));
            var latinOnly = ScriptFontCatalog.LatinOnly(noto);
            if (latinOnly == null)
                Assert.Ignore("this HarfBuzz has no subsetter, so the Latin face would also draw " +
                              "Cyrillic, Greek and Devanagari and those would never be on demand");
            _latin = NewAsset(latinOnly, "Noto Sans (Latin subset)");

            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            settings.DefaultFont = _latin;
            OneTextSettings.Instance = settings;

            _source = new DiskFontSource();
            var references = new List<OneFontReference>();
            foreach (var font in ScriptFontCatalog.Fonts())
            {
                if (!ScriptFontCatalog.Locate(font))
                {
                    _missing.Add(font.Key);
                    continue;
                }
                var (bytes, coverage, color) = Read(font.Path);
                var packed = NewAsset(bytes, font.Family);
                packed.Language = font.Language;
                _source.Add(font.Key, packed);
                _fonts[font.Key] = font;
                references.Add(new OneFontReference(font.Key, font.Language, coverage, color));
            }
            settings.SetOnDemandFonts(references);

            FontResidency.Source = _source;
            FontResidency.LoadOnDemand = true;
            Directory.CreateDirectory(OutputDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.ResetForTests();
            _source?.Dispose();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            SystemFonts.UseProjectSetting();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
        }

        /// <summary>
        /// Bytes, cmap coverage and colour per font file, once per process: the
        /// coverage walk is the editor's job (<see cref="FontCoverage.Of"/>), a
        /// few hundred milliseconds a CJK face, and not what is under test.
        /// </summary>
        private static (byte[] bytes, int[] coverage, bool color) Read(string path)
        {
            if (s_files.TryGetValue(path, out var cached)) return cached;
            var bytes = File.ReadAllBytes(path);
            var face = FontData.Load(bytes);
            try
            {
                cached = (bytes, FontCoverage.Of(face), ColorGlyphs.IsColorFont(face));
            }
            finally
            {
                face.Dispose();
            }
            s_files[path] = cached;
            return cached;
        }

        private OneFontAsset NewAsset(byte[] bytes, string family)
        {
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.name = family;
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.Initialize(bytes, family, family);
            _created.Add(asset);
            return asset;
        }

        // ------------------------------------------------------------ the sheet

        private sealed class Row
        {
            public ScriptLine Line;
            public OneTextLabel Caption;
            public OneTextLabel Sample;
        }

        private static float SheetHeight(IEnumerable<ScriptLine> lines)
        {
            float height = 16f;
            foreach (var line in lines) height += line.Height;
            return height + 8f;
        }

        private List<Row> BuildSheet(GoldenScene scene, IReadOnlyList<ScriptLine> lines)
        {
            var rows = new List<Row>();
            float y = 16f;
            foreach (var line in lines)
            {
                var row = new Row { Line = line };
                row.Caption = Label(scene, line.Caption, 17f, new Rect(16f, y + 6f, CaptionWidth - 20f, 30f));
                row.Caption.color = new Color(0.62f, 0.66f, 0.72f, 1f);
                row.Sample = Label(scene, line.Text, SampleSize,
                    new Rect(CaptionWidth, y, line.Width, line.Height));
                row.Sample.Language = line.Language;
                rows.Add(row);
                y += line.Height;
            }
            return rows;
        }

        /// <summary>
        /// A label with no font of its own: the project's default face, then
        /// whatever <see cref="FontResidency"/> has, then the on-demand tier —
        /// the path a shipped label takes. <see cref="GoldenScene.Label"/> hands
        /// its labels font bytes, which is a different path.
        /// </summary>
        private static OneTextLabel Label(GoldenScene scene, string text, float size, Rect rect)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = rectTransform.anchorMax = rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
            rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);

            var label = go.AddComponent<OneTextLabel>();
            label.Text = text;
            label.FontSize = size;
            label.Alignment = TextAlignment.Start;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Wrap = TextWrap.Wrap;
            label.color = Color.white;
            return label;
        }

        // ------------------------------------------------------------ what drew what

        /// <summary>Every face currently loaded, by the family that owns it.</summary>
        private Dictionary<FontData, string> FaceNames()
        {
            var names = new Dictionary<FontData, string>();
            _latin.ForEachLoadedFace(face => names[face] = "Latin");
            foreach (var asset in _source.Live)
                asset.ForEachLoadedFace(face => names[face] = asset.FamilyName);
            return names;
        }

        /// <summary>
        /// Which family drew each run of the label's last layout, as a readable
        /// chain, plus the .notdef count and every (family, text) pair.
        /// </summary>
        private static string Drawn(OneTextLabel label, Dictionary<FontData, string> names,
            out int notdef, out List<(string family, string text)> runs)
        {
            var layout = label.EnsureLayout();
            string source = label.Text;
            runs = new List<(string, string)>();
            notdef = 0;
            var parts = new List<string>();
            foreach (var run in layout.Runs)
            {
                string name = run.Font == null ? "null"
                    : names.TryGetValue(run.Font, out var known) ? known
                    : run.Font.IsValid ? $"unknown#{run.Font.CacheId}" : $"DESTROYED#{run.Font.CacheId}";
                runs.Add((name, source.Substring(run.TextStart, run.TextLength)));
                for (int i = 0; i < run.GlyphCount; i++)
                    if (layout.Glyphs[run.GlyphStart + i].GlyphId == 0) notdef++;
                if (parts.Count == 0 || parts[parts.Count - 1] != name) parts.Add(name);
            }
            return string.Join(" + ", parts);
        }

        /// <summary>
        /// Logs which family drew each line, and records the things a reader
        /// would notice: a box anywhere; a script line its declared font did
        /// not draw; a Han or kana character drawn by another language's font,
        /// which is the wrong shape for this reader; an emoji drawn by a text
        /// face. A non-Han character drawn by a different on-demand font that
        /// happens to be resident and to cover it — π in a Cyrillic face that
        /// carries the Mac Roman set — is fallback order doing what it does and
        /// is logged as borrowed, not failed.
        /// </summary>
        private void CheckSheet(string step, List<Row> rows)
        {
            var names = FaceNames();
            _log.AppendLine($"[{step}] lines:");
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Sample.Text)) continue;
                string drawn = Drawn(row.Sample, names, out int notdef, out var runs);
                _log.AppendLine($"  {row.Line.Caption,-16} {drawn}  notdef={notdef}");
                string where = $"{step} {row.Line.Caption}";
                if (notdef > 0) _problems.Add($"{where}: {notdef} .notdef glyph(s), drawn by {drawn}");
                if (row.Line.Key == null || !_fonts.TryGetValue(row.Line.Key, out var font)) continue;

                bool declaredDrew = false;
                foreach (var (family, text) in runs)
                {
                    if (family == font.Family)
                    {
                        declaredDrew = true;
                        continue;
                    }
                    bool emoji = font.Language == null;
                    if (family == "Latin" && !(emoji && text.Trim().Length > 0)) continue;
                    if (!emoji && !HasIdeograph(text))
                    {
                        _log.AppendLine($"    borrowed: \"{text}\" from {family}");
                        continue;
                    }
                    _problems.Add($"{where}: \"{text}\" drawn by {family}, not {font.Family}");
                }
                if (!declaredDrew) _problems.Add($"{where}: {font.Family} drew nothing; {drawn}");
            }
        }

        private static bool HasIdeograph(string text)
        {
            foreach (char c in text)
                if (Unicode.AsianTypography.IsIdeographic(c)) return true;
            return false;
        }

        private void Expect(bool condition, string message)
        {
            if (!condition) _problems.Add(message);
        }

        private void AssertNoProblems()
        {
            CollectionAssert.IsEmpty(_problems, string.Join("\n", _problems));
        }

        private void LogResidency(string step)
        {
            _log.AppendLine($"[{step}] {FontResidency.Describe()}");
            _log.AppendLine($"[{step}] source: loads={_source.Loads} releases={_source.Releases} " +
                            $"live={_source.Live.Count} | atlas tiles: sdf={SdfTiles()} colour={ColorTiles()}");
        }

        private static int SdfTiles() =>
            (SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas.GetStats().TileCount : 0) +
            (SharedGlyphAtlas.PreciseAtlasExists ? SharedGlyphAtlas.PreciseAtlas.GetStats().TileCount : 0);

        private static int ColorTiles() =>
            SharedGlyphAtlas.ColorAtlasExists ? SharedGlyphAtlas.ColorAtlas.GetStats().TileCount : 0;

        private void WriteLog(string name)
        {
            if (_problems.Count > 0) _log.AppendLine("PROBLEMS:\n  " + string.Join("\n  ", _problems));
            if (_missing.Count > 0) _log.Insert(0, $"no font on this machine for: {string.Join(", ", _missing)}\n");
            string path = Path.Combine(OutputDirectory, name);
            File.WriteAllText(path, _log.ToString());
            TestContext.WriteLine(_log.ToString());
            Debug.Log($"OneText residency log -> {path}\n{_log}");
        }

        private static string Save(Texture2D texture, string name)
        {
            string path = Path.Combine(OutputDirectory, name);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            return path;
        }

        // ------------------------------------------------------------ tests

        [Test]
        public void EveryScript_LoadsOnDemand_GivesEveryFontBack_AndDrawsTheSameWhenItReturns()
        {
            var lines = ScriptFontCatalog.Sheet();
            using var scene = new GoldenScene(Width, Mathf.CeilToInt(SheetHeight(lines)));
            var rows = BuildSheet(scene, lines);
            Assert.AreEqual(0, FontResidency.Resident.Count, "something was resident before any text");

            // a. Cold: nothing loaded, the sheet asks.
            var cold = scene.Render();
            _created.Add(cold);
            Save(cold, "all-scripts-cold.png");
            CheckSheet("cold", rows);
            LogResidency("cold");
            Expect(_fonts.Count == FontResidency.Resident.Count,
                "cold: not every script's font was loaded on demand: " + FontResidency.Describe());

            var faces = new List<(string family, FontData face)>();
            foreach (var asset in _source.Live)
                asset.ForEachLoadedFace(face => faces.Add((asset.FamilyName, face)));

            // b. The text goes; the fonts go with it, in the two steps a
            // running game takes: out of the chain with the labels told, then
            // unloaded a frame later once nothing took them back.
            var texts = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                texts[i] = rows[i].Sample.Text;
                if (rows[i].Line.Key != null || rows[i].Line.Language == "en") rows[i].Sample.Text = "";
            }
            FontResidency.Trim();
            FontResidency.Tick();
            Object.DestroyImmediate(scene.Render());
            FontResidency.Tick();

            var released = scene.Render();
            _created.Add(released);
            Save(released, "all-scripts-released.png");
            LogResidency("released");
            CheckSheet("released", rows);

            Expect(FontResidency.Resident.Count == 0, "released: still resident: " + FontResidency.Describe());
            Expect(FontResidency.ResidentBytes == 0, $"released: {FontResidency.ResidentBytes} bytes resident");
            Expect(_source.Live.Count == 0, $"released: {_source.Live.Count} asset(s) outlived their release");
            Expect(_source.Loads == _source.Releases, $"released: {_source.Loads} loads, {_source.Releases} releases");
            var leftovers = _problems;
            foreach (var (family, face) in faces)
            {
                if (face.IsValid) leftovers.Add($"released: {family}: face still alive");
                int sdf = (SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas.Forget(face) : 0) +
                          (SharedGlyphAtlas.PreciseAtlasExists ? SharedGlyphAtlas.PreciseAtlas.Forget(face) : 0);
                if (sdf > 0) leftovers.Add($"released: {family}: {sdf} SDF tile(s) left in the atlas");
            }
            Expect(ColorTiles() == 0, $"released: {ColorTiles()} colour tile(s) of the unloaded emoji face still in the atlas");

            // c. The text comes back; so do the fonts, and the picture.
            for (int i = 0; i < rows.Count; i++) rows[i].Sample.Text = texts[i];
            var reloaded = scene.Render();
            _created.Add(reloaded);
            Save(reloaded, "all-scripts-reloaded.png");
            CheckSheet("reloaded", rows);
            LogResidency("reloaded");
            Expect(_fonts.Count == FontResidency.Resident.Count, "reloaded: " + FontResidency.Describe());

            var diff = Compare(cold, reloaded, out string summary);
            _created.Add(diff);
            Save(diff, "all-scripts-reload-diff.png");
            _log.AppendLine($"[cold vs reloaded] {summary}");
            Expect(summary.StartsWith("same"), "cold vs reloaded: " + summary);
            WriteLog("all-scripts-log.txt");
            AssertNoProblems();
        }

        [Test]
        public void SetLanguages_KeepsExactlyThatLanguagesFont()
        {
            var steps = new List<(string language, ScriptLine line)>();
            foreach (var line in ScriptFontCatalog.Sheet())
                if (line.Key != null && _fonts.ContainsKey(line.Key))
                    steps.Add((line.Language ?? "(acquire)", line));
            steps.Add(("en", new ScriptLine("en", "en", "Latin only: nothing on demand is resident.", null)));

            var strips = new List<Texture2D>();
            var problems = _problems;
            foreach (var (language, line) in steps)
            {
                string key = line.Key;
                bool acquired = line.Language == null;
                if (acquired)
                {
                    FontResidency.SetLanguages();
                    FontResidency.Acquire(key);
                }
                else
                {
                    FontResidency.SetLanguages(language);
                }

                var strip = new[] { new ScriptLine("", line.Language, line.Text, key, line.Height, 1000f) };
                using var scene = new GoldenScene(Width, Mathf.CeilToInt(SheetHeight(strip)) + 30);
                var rows = BuildSheet(scene, strip);
                // The frame that tells labels, the frame that sweeps whatever
                // the previous language left behind.
                FontResidency.Tick();
                Object.DestroyImmediate(scene.Render());
                FontResidency.Tick();

                string resident = Resident();
                string call = acquired ? $"Acquire(\"{key}\")" : $"SetLanguages(\"{language}\")";
                rows[0].Caption.Text = $"{call}   resident: {resident}";
                rows[0].Caption.rectTransform.sizeDelta = new Vector2(Width - 32f, 30f);
                rows[0].Sample.rectTransform.anchoredPosition = new Vector2(CaptionWidth, -46f);
                _log.AppendLine($"[{call}] {FontResidency.Describe()}");

                bool expectOne = key != null;
                if (FontResidency.Resident.Count != (expectOne ? 1 : 0) ||
                    (expectOne && !FontResidency.IsResident(key)))
                    problems.Add($"{call}: resident {resident}");
                CheckSheet(call, rows);

                var texture = scene.Render();
                _created.Add(texture);
                strips.Add(texture);
                Save(texture, $"per-language-{(acquired ? "emoji" : language)}.png");
                if (acquired) FontResidency.Release(key);
            }

            var sheet = Stack(strips);
            _created.Add(sheet);
            Save(sheet, "all-scripts-per-language.png");
            WriteLog("per-language-log.txt");
            AssertNoProblems();
        }

        private static string Resident()
        {
            if (FontResidency.Resident.Count == 0) return "none (0 bytes)";
            var parts = new List<string>();
            foreach (var asset in FontResidency.Resident)
                parts.Add($"{asset.FamilyName} {asset.ResidentBytes / (1024f * 1024f):0.0} MB");
            return string.Join(", ", parts);
        }

        // ------------------------------------------------------------ pictures

        /// <summary>
        /// Per-pixel comparison. "same" when nothing is over the golden suite's
        /// channel tolerance; the heatmap is red where a pixel differs at all.
        /// </summary>
        private static Texture2D Compare(Texture2D a, Texture2D b, out string summary)
        {
            var pa = a.GetPixels32();
            var pb = b.GetPixels32();
            var heat = new Color32[pa.Length];
            int any = 0, over = 0, worst = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int delta = Math.Max(Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Abs(pa[i].g - pb[i].g)),
                    Math.Max(Math.Abs(pa[i].b - pb[i].b), Math.Abs(pa[i].a - pb[i].a)));
                worst = Math.Max(worst, delta);
                if (delta > 0) any++;
                if (delta > GoldenComparer.ChannelTolerance) over++;
                byte grey = (byte)(pa[i].r / 4);
                heat[i] = delta > 0 ? new Color32(255, (byte)(255 - Math.Min(255, delta * 4)), 0, 255)
                    : new Color32(grey, grey, grey, 255);
            }
            var texture = new Texture2D(a.width, a.height, TextureFormat.RGBA32, false);
            texture.SetPixels32(heat);
            texture.Apply(false);
            summary = $"{(over == 0 ? "same" : "DIFFERENT")}: {any} pixel(s) differ at all, {over} over " +
                      $"the tolerance of {GoldenComparer.ChannelTolerance}, worst channel delta {worst}, " +
                      $"of {pa.Length}";
            return texture;
        }

        /// <summary>The strips one above the other, first at the top.</summary>
        private static Texture2D Stack(List<Texture2D> strips)
        {
            int height = 0;
            foreach (var strip in strips) height += strip.height;
            var sheet = new Texture2D(Width, height, TextureFormat.RGBA32, false);
            int top = height;
            foreach (var strip in strips)
            {
                top -= strip.height;
                sheet.SetPixels32(0, top, strip.width, strip.height, strip.GetPixels32());
            }
            sheet.Apply(false);
            return sheet;
        }
    }
}
