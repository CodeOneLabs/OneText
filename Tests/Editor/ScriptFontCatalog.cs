using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// One line of the all-scripts sheet: what it says, which language it is
    /// in, and which on-demand font is expected to draw it (null for the
    /// lines the project's own Latin face draws).
    /// </summary>
    internal sealed class ScriptLine
    {
        public readonly string Caption;
        public readonly string Language;
        public readonly string Text;
        public readonly string Key;
        public readonly float Height;
        public readonly float Width;

        public ScriptLine(string caption, string language, string text, string key,
            float height = 46f, float width = 960f)
        {
            Caption = caption;
            Language = language;
            Text = text;
            Key = key;
            Height = height;
            Width = width;
        }
    }

    /// <summary>
    /// An on-demand font for one script: the key it is loaded by, the language
    /// it is declared for, and the font files that can stand in for it on this
    /// machine, best first.
    ///
    /// <para>The first candidate is a font the operating system ships, so the
    /// check is made against faces nobody tuned for this package — macOS
    /// collections, CFF and AAT tables, a 55 MB Korean face. The later ones
    /// are the Noto faces <c>Tools/fetch_coverage_fonts.py</c> puts in
    /// <c>Tests/CoverageFonts~</c>, so the same sheet renders on a machine
    /// without the macOS set. Nothing here is committed font data.</para>
    /// </summary>
    internal sealed class ScriptFont
    {
        public readonly string Key;
        public readonly string Language;
        public readonly (string path, string family)[] Candidates;

        public string Path;
        public string Family;

        public ScriptFont(string key, string language, params (string, string)[] candidates)
        {
            Key = key;
            Language = language;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// The scripts the residency check covers, and a font source over font
    /// files on disk that behaves the way a player's source does: every load
    /// is a fresh asset read off "disk", and a release destroys it.
    /// </summary>
    internal static class ScriptFontCatalog
    {
        public const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string PackageFonts = "Packages/com.onetext.core/Tests/Fonts~/";
        private const string Coverage = "Packages/com.onetext.core/Tests/CoverageFonts~/";
        private const string Mac = "/System/Library/Fonts/";
        private const string MacMore = "/System/Library/Fonts/Supplemental/";

        /// <summary>
        /// Settings order is fallback order, and it decides who draws a
        /// character in a line that names no language: the narrow fonts come
        /// before the broad ones so a Hebrew letter in an English line does not
        /// land in a pan-European face that happens to carry Hebrew too.
        /// </summary>
        public static ScriptFont[] Fonts() => new[]
        {
            new ScriptFont("Scripts/Cyrillic", "ru", (MacMore + "PTSans.ttc", "PT Sans"), (Coverage + "NotoSans-Regular.ttf", "Noto Sans")),
            new ScriptFont("Scripts/Greek", "el", (Mac + "Helvetica.ttc", "Helvetica"), (Coverage + "NotoSans-Regular.ttf", "Noto Sans")),
            new ScriptFont("Scripts/Arabic", "ar", (PackageFonts + "NotoSansArabic.ttf", "Noto Sans Arabic"), (Mac + "GeezaPro.ttc", "Geeza Pro")),
            new ScriptFont("Scripts/Hebrew", "he", (Mac + "ArialHB.ttc", "Arial Hebrew"), (Coverage + "NotoSansHebrew-Regular.ttf", "Noto Sans Hebrew")),
            new ScriptFont("Scripts/Devanagari", "hi", (Mac + "Kohinoor.ttc", "Kohinoor Devanagari"), (Coverage + "NotoSansDevanagari-Regular.ttf", "Noto Sans Devanagari")),
            new ScriptFont("Scripts/Bengali", "bn", (Mac + "KohinoorBangla.ttc", "Kohinoor Bangla"), (Coverage + "NotoSansBengali-Regular.ttf", "Noto Sans Bengali")),
            new ScriptFont("Scripts/Tamil", "ta", (MacMore + "Tamil Sangam MN.ttc", "Tamil Sangam MN"), (Coverage + "NotoSansTamil-Regular.ttf", "Noto Sans Tamil")),
            new ScriptFont("Scripts/Thai", "th", (MacMore + "Thonburi.ttc", "Thonburi"), (Coverage + "NotoSansThai-Regular.ttf", "Noto Sans Thai")),
            new ScriptFont("Scripts/Korean", "ko", (Mac + "AppleSDGothicNeo.ttc", "Apple SD Gothic Neo"), (Coverage + "NotoSansCJKkr-Regular.otf", "Noto Sans CJK KR")),
            new ScriptFont("Scripts/SimplifiedChinese", "zh-Hans", (Mac + "Hiragino Sans GB.ttc", "Hiragino Sans GB"), (Coverage + "NotoSansCJKsc-Regular.otf", "Noto Sans CJK SC")),
            new ScriptFont("Scripts/TraditionalChinese", "zh-Hant", (Mac + "STHeiti Light.ttc", "Heiti TC"), (Coverage + "NotoSansCJKtc-Regular.otf", "Noto Sans CJK TC")),
            new ScriptFont("Scripts/Japanese", "ja", (Mac + "ヒラギノ角ゴシック W3.ttc", "Hiragino Sans"), (Coverage + "NotoSansCJKjp-Regular.otf", "Noto Sans CJK JP")),
            // The CBDT face, so the sheet is the same on every machine; Apple
            // Color Emoji (sbix) draws too, through the system tier.
            new ScriptFont("Scripts/Emoji", null, (Coverage + "NotoColorEmoji.ttf", "Noto Color Emoji")),
        };

        public static ScriptLine[] Sheet() => new[]
        {
            new ScriptLine("fr  Latin", "fr", "Où êtes-vous ? Ça coûte déjà trop cher, garçon.", null),
            new ScriptLine("de  Latin", "de", "Größere Übungen für Äpfel und Öl, Straße.", null),
            new ScriptLine("vi  Latin", "vi", "Tiếng Việt có dấu: người, đường, phở, ở đâu?", null),
            new ScriptLine("pl  Latin", "pl", "Zażółć gęślą jaźń. Łódź, Kraków, Gdańsk.", null),
            new ScriptLine("ru  Cyrillic", "ru", "Съешь же ещё этих мягких французских булок, да выпей чаю.", "Scripts/Cyrillic"),
            new ScriptLine("el  Greek", "el", "Ξεσκεπάζω την ψυχοφθόρα βδελυγμία. Καλημέρα κόσμε!", "Scripts/Greek"),
            new ScriptLine("ar  Arabic", "ar", "العدد 123 والنسبة 45% في عام 2024 ميلادي", "Scripts/Arabic"),
            new ScriptLine("he  Hebrew", "he", "שלום עולם! המספר 42 והשנה 2024.", "Scripts/Hebrew"),
            new ScriptLine("hi  Devanagari", "hi", "हिन्दी: क्ष त्र ज्ञ श्र द्ध - विद्यालय, स्वतंत्रता", "Scripts/Devanagari"),
            new ScriptLine("bn  Bengali", "bn", "বাংলা: ক্ষ ন্ত্র স্ত্র জ্ঞ - আমার সোনার বাংলা", "Scripts/Bengali"),
            new ScriptLine("ta  Tamil", "ta", "தமிழ்: ஸ்ரீ க்ஷ கொ கோ கௌ - வணக்கம்", "Scripts/Tamil"),
            new ScriptLine("th  Thai (wrap)", "th",
                "ภาษาไทยไม่เว้นวรรคระหว่างคำผู้ใหญ่ปู่ย่าน้ำที่นี่สวัสดีครับขอบคุณสวัสดีครับขอบคุณ",
                "Scripts/Thai", 84f, 520f),
            new ScriptLine("ko  Korean", "ko", "다람쥐 헌 쳇바퀴에 타고파. 한국어 문장입니다.", "Scripts/Korean"),
            new ScriptLine("zh-Hans", "zh-Hans", "简体中文：我们说汉语，这里很漂亮。", "Scripts/SimplifiedChinese"),
            new ScriptLine("zh-Hant", "zh-Hant", "繁體中文：我們說漢語，這裡很漂亮。", "Scripts/TraditionalChinese"),
            new ScriptLine("ja  Japanese", "ja", "日本語：ひらがな、カタカナ、漢字。直す・骨", "Scripts/Japanese"),
            new ScriptLine("emoji", null, "\U0001F600 \U0001F44D\U0001F3FD \U0001F1F0\U0001F1F7 \U0001F468‍\U0001F469‍\U0001F467 ❤️ 1️⃣", "Scripts/Emoji"),
            new ScriptLine("mixed bidi", "en", "Hello עולם and مرحبا 123 - Привет, Γειά, 안녕 \U0001F600", null),
        };

        /// <summary>Points the font at the first candidate on this machine; false when there is none.</summary>
        public static bool Locate(ScriptFont font)
        {
            foreach (var (candidate, family) in font.Candidates)
            {
                string full = candidate.StartsWith("/") ? candidate : System.IO.Path.GetFullPath(candidate);
                if (!File.Exists(full)) continue;
                font.Path = full;
                font.Family = family;
                return true;
            }
            return false;
        }

        /// <summary>
        /// NotoSans cut down to Latin and the punctuation every script borrows,
        /// so that Cyrillic, Greek and Devanagari — which the full face also
        /// draws — have to come from their on-demand fonts like everything
        /// else. Null when this HarfBuzz has no subsetter.
        /// </summary>
        public static byte[] LatinOnly(byte[] notoSans)
        {
            var keep = new List<int>();
            void Range(int first, int last) { for (int c = first; c <= last; c++) keep.Add(c); }
            Range(0x20, 0x7E);
            Range(0xA0, 0x24F);
            Range(0x2B0, 0x36F);
            Range(0x1E00, 0x1EFF);
            Range(0x2000, 0x206F);
            Range(0x20A0, 0x20BF);
            return FontSubsetter.TrySubset(notoSans, keep, out var subset, out _) ? subset : null;
        }
    }

    /// <summary>
    /// Font files on disk as an <see cref="IFontSource"/>, shaped like a
    /// player's: the "disk" copy is a packed asset built once, every load hands
    /// out a fresh copy of it, and a release destroys that copy. So a font that
    /// has been released is gone, not merely unloaded, and a reload really
    /// does read it back.
    /// </summary>
    internal sealed class DiskFontSource : IFontSource, IDisposable
    {
        private readonly Dictionary<string, OneFontAsset> _disk = new Dictionary<string, OneFontAsset>();
        public readonly HashSet<OneFontAsset> Live = new HashSet<OneFontAsset>();
        public readonly List<string> LoadLog = new List<string>();
        public int Loads, Releases;

        public void Add(string key, OneFontAsset packed) => _disk[key] = packed;

        public void Load(string key, Action<OneFontAsset> done) => done(LoadNow(key));

        public OneFontAsset LoadNow(string key)
        {
            if (!_disk.TryGetValue(key, out var packed)) return null;
            var copy = Object.Instantiate(packed);
            copy.name = packed.name;
            copy.hideFlags = HideFlags.HideAndDontSave;
            Live.Add(copy);
            Loads++;
            LoadLog.Add(key);
            return copy;
        }

        public void Release(string key, OneFontAsset font)
        {
            Releases++;
            Live.Remove(font);
            if (font != null) Object.DestroyImmediate(font);
        }

        public void Dispose()
        {
            foreach (var font in Live) if (font != null) Object.DestroyImmediate(font);
            foreach (var packed in _disk.Values) if (packed != null) Object.DestroyImmediate(packed);
            Live.Clear();
            _disk.Clear();
        }
    }
}
