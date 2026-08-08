using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// One deterministic picture: a name, a canvas size, the fonts it needs on
    /// disk, and the scene that draws it.
    /// </summary>
    public sealed class GoldenCase
    {
        public readonly string Name;
        public readonly int Width, Height;
        public readonly string[] RequiredFonts;
        public readonly string Description;
        private readonly Action<GoldenScene> _build;

        public GoldenCase(string name, int width, int height, string description,
            string[] requiredFonts, Action<GoldenScene> build)
        {
            Name = name;
            Width = width;
            Height = height;
            Description = description;
            RequiredFonts = requiredFonts;
            _build = build;
        }

        /// <summary>The baseline PNG for this case, as an absolute path.</summary>
        public string BaselinePath => Path.Combine(GoldenCases.BaselineDirectory, Name + ".png");

        /// <summary>
        /// The first required font that is not on disk, or null when all are.
        /// The coverage faces are fetched rather than committed, so "the font
        /// is missing" is a reason to skip a case and never a reason to fail
        /// it.
        /// </summary>
        public string MissingFont()
        {
            foreach (string font in RequiredFonts)
                if (!File.Exists(Path.GetFullPath(font)))
                    return font;
            return null;
        }

        /// <summary>Renders this case. The caller owns the returned texture.</summary>
        public Texture2D Render()
        {
            using var scene = new GoldenScene(Width, Height);
            _build(scene);
            return scene.Render();
        }
    }

    /// <summary>
    /// The golden-image registry: what the renderer is asked to keep producing
    /// byte for byte.
    ///
    /// These are chosen for breadth rather than depth. Everything here is
    /// already asserted somewhere in the EditMode suite as numbers — cluster
    /// counts, run directions, break opportunities — and none of that catches
    /// the failure this tier exists for, which is the day the numbers stay
    /// right and the picture stops being. A shader that loses its outline
    /// channel, an atlas that bleeds a neighbour's tile, a vertical column that
    /// drifts half an em: each of those is invisible to a layout assertion and
    /// obvious in a diff of two PNGs.
    ///
    /// Every case is fixed: fixed canvas, fixed font, fixed string, no clock,
    /// no randomness, no dependence on what ran before it.
    /// </summary>
    public static class GoldenCases
    {
        public const string LatinFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        public const string ArabicFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        public const string VariableFont = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        private const string CoverageDir = "Packages/com.onetext.core/Tests/CoverageFonts~/";
        public const string JapaneseFont = CoverageDir + "NotoSansCJKjp-Regular.otf";
        public const string DevanagariFont = CoverageDir + "NotoSansDevanagari-Regular.ttf";
        public const string ThaiFont = CoverageDir + "NotoSansThai-Regular.ttf";
        public const string EmojiFont = CoverageDir + "NotoColorEmoji.ttf";

        /// <summary>
        /// Where baselines live. A trailing tilde keeps Unity from importing
        /// them, which is the difference between fifteen committed PNGs and
        /// fifteen PNGs plus fifteen .meta files plus fifteen imported
        /// textures nothing ever samples.
        /// </summary>
        public static string BaselineDirectory =>
            Path.Combine(PackageRoot, "Tests", "Golden~");

        /// <summary>
        /// The package's real folder on disk. Resolved through a file that
        /// certainly exists rather than through the tilde folder, which may
        /// not yet.
        /// </summary>
        public static string PackageRoot =>
            Path.GetDirectoryName(Path.GetFullPath("Packages/com.onetext.core/package.json"));

        /// <summary>
        /// The file recording which rasterizer the committed baselines came
        /// out of.
        /// </summary>
        public static string RendererStampPath =>
            Path.Combine(BaselineDirectory, "renderer.txt");

        /// <summary>
        /// Who drew these pictures: graphics API, adapter and colour space.
        ///
        /// A golden image is a claim about one rasterizer and not about text.
        /// Metal and OpenGL disagree about the last bit of an antialiased edge;
        /// so do two drivers for the same card, and so do a linear project and
        /// a gamma one. None of those differences is a bug in this package, and
        /// all of them would fail a pixel comparison, so a run on a machine the
        /// baselines were not made on has to skip rather than fail. That is
        /// also why the Unity version is deliberately NOT part of this stamp:
        /// an editor upgrade changing how uGUI rasterizes text is exactly the
        /// thing worth being told about.
        /// </summary>
        public static string RendererStamp =>
            $"{SystemInfo.graphicsDeviceType}|{SystemInfo.graphicsDeviceName}|" +
            $"{QualitySettings.activeColorSpace}";

        /// <summary>
        /// The stamp the committed baselines carry, or null when none was
        /// recorded (older baselines, or a folder assembled by hand).
        /// </summary>
        public static string CommittedRendererStamp() =>
            File.Exists(RendererStampPath)
                ? File.ReadAllText(RendererStampPath).Trim()
                : null;

        private static GoldenCase[] s_all;

        public static IReadOnlyList<GoldenCase> All => s_all ??= Build();

        public static GoldenCase Find(string name)
        {
            foreach (var golden in All)
                if (golden.Name == name) return golden;
            throw new ArgumentException($"No golden case named '{name}'.", nameof(name));
        }

        private static GoldenCase[] Build() => new[]
        {
            // ------------------------------------------------------ Latin

            new GoldenCase("latin-rich-text", 512, 256,
                "bold, italic, colour and size tags in one paragraph",
                new[] { LatinFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "<b>Bold</b> and <i>italic</i> and <b><i>both</i></b>\n" +
                        "<color=#ff8844>orange</color> <color=#44aaff>blue</color>\n" +
                        "<size=44>large</size> normal <size=18>small</size>\n" +
                        "nested <b>bold <color=#88ff88>and green</color></b> back",
                        28f, new Rect(16f, 12f, 480f, 232f));
                }),

            new GoldenCase("latin-inline-metrics", 512, 256,
                "letter spacing and baseline shift, which move glyphs rather than colour them",
                new[] { LatinFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "normal spacing here\n" +
                        "<cspace=0.25>wide tracking</cspace>\n" +
                        "<cspace=-0.06>tight tracking</cspace>\n" +
                        "base <voffset=0.4>up</voffset> <voffset=-0.3>down</voffset> base",
                        30f, new Rect(16f, 12f, 480f, 232f));
                }),

            new GoldenCase("sdf-effects", 512, 256,
                "outline, shadow and glow: the three decoration channels",
                new[] { LatinFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "<outline=#2255ccff w=0.5>Outline</outline>\n" +
                        "<shadow=#000000c0 x=0.6 y=-0.6 soft=0.3>Shadow</shadow>\n" +
                        "<glow=#66ddffff r=0.8>Glow</glow>\n" +
                        "<outline=#000000ff w=0.35><glow=#ffcc44ff r=0.6>Both</glow></outline>",
                        38f, new Rect(16f, 8f, 480f, 240f));
                }),

            new GoldenCase("latin-line-decorations", 512, 256,
                "underline and strikethrough over the text, and the <mark> wash behind it",
                new[] { LatinFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "<u>underlined words</u> plain\n" +
                        "<s>struck through</s> plain\n" +
                        "a <mark=#ffdd2266>highlighted</mark> phrase\n" +
                        "<mark=#3388ff66><u>all</u> <s>three</s> <color=#ffcc44>at once</color></mark>",
                        30f, new Rect(16f, 12f, 480f, 232f));
                }),

            new GoldenCase("latin-alignment", 512, 256,
                "left, centre and right in identical boxes",
                new[] { LatinFont }, scene =>
                {
                    var rows = new[]
                    {
                        (TextAlignment.Left, "left aligned"),
                        (TextAlignment.Center, "centred"),
                        (TextAlignment.Right, "right aligned"),
                    };
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var (alignment, text) = rows[i];
                        var label = scene.Label(LatinFont, text, 34f,
                            new Rect(16f, 16f + i * 72f, 480f, 64f));
                        label.Alignment = alignment;
                    }
                }),

            new GoldenCase("latin-justified", 512, 256,
                "justified: every line but the last stretched to the full width",
                new[] { LatinFont }, scene =>
                {
                    var label = scene.Label(LatinFont,
                        "Justification stretches the space between words so that " +
                        "each line ends exactly at the measure, and leaves the " +
                        "last line of the paragraph alone.",
                        26f, new Rect(16f, 12f, 480f, 232f));
                    label.Alignment = TextAlignment.Justified;
                }),

            new GoldenCase("latin-paragraph-wrap", 512, 256,
                "a long paragraph greedily wrapped in a narrow measure",
                new[] { LatinFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "The quick brown fox jumps over the lazy dog, and then " +
                        "keeps going for long enough that the paragraph has to " +
                        "break in several places, which is the only way to see " +
                        "whether the break positions have moved.",
                        24f, new Rect(16f, 12f, 300f, 232f));
                }),

            new GoldenCase("variable-font-axes", 512, 256,
                "one variable face at three weights",
                new[] { VariableFont }, scene =>
                {
                    var weights = new[] { 300f, 500f, 800f };
                    for (int i = 0; i < weights.Length; i++)
                    {
                        var label = scene.Label(VariableFont, $"Weight {weights[i]:0}", 40f,
                            new Rect(16f, 12f + i * 76f, 480f, 68f));
                        label.SetVariations(new FontVariation("wght", weights[i]));
                    }
                }),

            // ------------------------------------------------------ RTL

            new GoldenCase("arabic-bidi", 512, 256,
                "RTL base with embedded digits, Latin and a percentage",
                new[] { ArabicFont, LatinFont }, scene =>
                {
                    var lines = new[]
                    {
                        "العدد 123 يظهر بشكل صحيح",
                        "قال المطور Hello World ثم أكمل",
                        "نسبة النجاح 100% في الاختبار",
                    };
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var label = scene.Label(ArabicFont, lines[i], 34f,
                            new Rect(16f, 12f + i * 76f, 480f, 68f), LatinFont);
                        label.Alignment = TextAlignment.Start;
                    }
                }),

            new GoldenCase("arabic-shaping", 512, 256,
                "joining forms at a size where the seams are visible",
                new[] { ArabicFont }, scene =>
                {
                    scene.Label(ArabicFont, "ورحمة الله", 96f, new Rect(16f, 8f, 480f, 120f));
                    scene.Label(ArabicFont, "السلام عليكم ومرحبا بالعالم", 44f,
                        new Rect(16f, 140f, 480f, 108f));
                }),

            // ------------------------------------------------------ Indic and SEA

            new GoldenCase("devanagari-conjuncts", 512, 256,
                "conjuncts, matras and the reordered i-matra",
                new[] { DevanagariFont }, scene =>
                {
                    scene.Label(DevanagariFont,
                        "हिन्दी में लिखा गया\n" +
                        "क्ष त्र ज्ञ श्र द्ध\n" +
                        "विद्यालय स्वतंत्रता",
                        38f, new Rect(16f, 8f, 480f, 240f));
                }),

            new GoldenCase("thai-wrap", 512, 256,
                "Thai wrapped at dictionary word boundaries, with no space to break at",
                new[] { ThaiFont }, scene =>
                {
                    // Words the built-in list holds, written the way Thai is
                    // actually written: no spaces anywhere. Every break in the
                    // picture is one the dictionary found.
                    var label = scene.Label(ThaiFont,
                        "สวัสดีครับขอบคุณสวัสดีครับขอบคุณสวัสดีครับขอบคุณ",
                        34f, new Rect(16f, 12f, 260f, 232f));
                    label.Language = "th";
                }),

            // ------------------------------------------------------ CJK

            new GoldenCase("cjk-kinsoku", 512, 256,
                "Japanese wrapped with kinsoku: no line starts with 。 or 、",
                new[] { JapaneseFont }, scene =>
                {
                    var label = scene.Label(JapaneseFont,
                        "日本語の組版では、行頭に句読点を置かない。" +
                        "これを禁則処理と呼び、読みやすさのために欠かせない。",
                        32f, new Rect(16f, 12f, 300f, 232f));
                    label.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
                    label.Language = "ja";
                    label.CjkLatinSpacing = true;
                }),

            new GoldenCase("cjk-vertical", 512, 512,
                "縦書き: columns right to left, Latin rotated, kana upright",
                new[] { JapaneseFont, LatinFont }, scene =>
                {
                    var label = scene.Label(JapaneseFont,
                        "縦書きの文章です。\n" +
                        "ABC と 123 は回転する。\n" +
                        "かなは正立したまま。",
                        36f, new Rect(16f, 16f, 480f, 480f), LatinFont);
                    label.WritingMode = TextWritingMode.VerticalRightToLeft;
                    label.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
                    label.Language = "ja";
                }),

            new GoldenCase("cjk-vertical-decorations", 512, 384,
                "the three bars down a column: written against the em box upright, " +
                "against the face's own baseline where a run is rotated",
                new[] { JapaneseFont, LatinFont }, scene =>
                {
                    var label = scene.Label(JapaneseFont,
                        "<u>縦書きの傍線です</u>\n" +
                        "<mark=#ffdd2266><u>ABC と 123</u></mark> の帯\n" +
                        "<s>取り消し線</s>もある",
                        34f, new Rect(16f, 16f, 480f, 352f), LatinFont);
                    label.WritingMode = TextWritingMode.VerticalRightToLeft;
                    label.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
                    label.Language = "ja";
                }),

            new GoldenCase("ruby-furigana", 512, 256,
                "furigana over its base, and an annotation wider than what it reads",
                new[] { JapaneseFont }, scene =>
                {
                    var rows = new[]
                    {
                        "<ruby=かんじ>漢字</ruby>があります",
                        "この<ruby=むずかしいよみかた>難</ruby>字",
                        "<ruby=にほんご>日本語</ruby>の文章",
                    };
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var label = scene.Label(JapaneseFont, rows[i], 34f,
                            new Rect(16f, 12f + i * 76f, 480f, 68f));
                        label.Kinsoku = Unicode.AsianTypography.Kinsoku.Normal;
                        label.Language = "ja";
                    }
                }),

            // ------------------------------------------------------ colour and fallback

            new GoldenCase("emoji-sequences", 512, 256,
                "ZWJ family, flag, keycap and skin tone: one picture each",
                new[] { EmojiFont, LatinFont }, scene =>
                {
                    var rows = new[]
                    {
                        ("family", "\U0001F468‍\U0001F469‍\U0001F467"),
                        ("flag", "\U0001F1EF\U0001F1F5"),
                        ("keycap", "1️⃣"),
                        ("tone", "\U0001F44D\U0001F3FD"),
                    };
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var (name, text) = rows[i];
                        scene.Label(LatinFont, name, 22f,
                            new Rect(16f, 18f + i * 58f, 130f, 52f));
                        scene.Label(EmojiFont, text, 40f,
                            new Rect(160f, 8f + i * 58f, 336f, 56f), LatinFont);
                    }
                }),

            new GoldenCase("fallback-mixed-scripts", 512, 256,
                "one label, five scripts, resolved down the fallback chain",
                new[] { LatinFont, ArabicFont, JapaneseFont, DevanagariFont, ThaiFont }, scene =>
                {
                    scene.Label(LatinFont,
                        "Latin مرحبا 日本語\nहिन्दी ภาษาไทย 123",
                        34f, new Rect(16f, 16f, 480f, 224f),
                        ArabicFont, JapaneseFont, DevanagariFont, ThaiFont);
                }),
        };
    }
}
