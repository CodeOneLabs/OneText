using System;
using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// COLR version 1: the paint graph.
    ///
    /// The face is authored (Tests/Fonts~/ColrV1.ttf, built by the script
    /// beside it, which lists what every glyph is for). fontTools wrote the
    /// table and this package's own reader reads it, so a field read at the
    /// wrong offset shows up here as the wrong colour at a named point rather
    /// than agreeing with itself.
    ///
    /// Every assertion is about a point in font units, which is how the font
    /// was drawn: "the centre of the square is red". The tile is whatever size
    /// the renderer made it.
    /// </summary>
    public class ColrV1Tests
    {
        private const string FontPath = "Packages/com.onetext.core/Tests/Fonts~/ColrV1.ttf";
        private const string V0FontPath = "Packages/com.onetext.core/Tests/Fonts~/ColorGlyphs.ttf";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static readonly Color32 White = new Color32(255, 255, 255, 255);
        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Blue = new Color32(0, 0, 255, 255);
        private static readonly Color32 Nothing = new Color32(0, 0, 0, 0);

        // A hundred pixels to the em: the 800-unit square is 80 texels.
        private const float Ppem = 100f;

        private static byte[] FontBytes(string path) => File.ReadAllBytes(Path.GetFullPath(path));
        private static FontData LoadFont(string path) => FontData.Load(FontBytes(path));

        private static uint GlyphOf(FontData font, int codepoint)
        {
            uint glyph = font.NominalGlyph(codepoint);
            Assert.AreNotEqual(0u, glyph, $"U+{codepoint:X4} is not in the test face");
            return glyph;
        }

        private static ColorGlyph Decode(FontData font, int codepoint, Color32? text = null)
        {
            Assert.IsTrue(ColorGlyphs.TryDecode(font, GlyphOf(font, codepoint), Ppem / font.UnitsPerEm,
                text ?? White, out var glyph), $"'{(char)codepoint}' did not decode");
            return glyph;
        }

        /// <summary>The texel over a point given in font units; nothing, outside the tile.</summary>
        private static Color32 At(in ColorGlyph glyph, float x, float y)
        {
            int px = Mathf.FloorToInt((x - glyph.OriginUnits.x) / glyph.UnitsPerPixel);
            int py = Mathf.FloorToInt((y - glyph.OriginUnits.y) / glyph.UnitsPerPixel);
            if (px < 0 || py < 0 || px >= glyph.Width || py >= glyph.Height) return Nothing;
            return glyph.Pixels[py * glyph.Width + px];
        }

        private static void AssertColor(Color32 expected, Color32 actual, string what, int tolerance = 3)
        {
            bool close = Mathf.Abs(expected.a - actual.a) <= tolerance;
            // A transparent texel has no colour to compare.
            if (close && expected.a > 0)
                close = Mathf.Abs(expected.r - actual.r) <= tolerance &&
                        Mathf.Abs(expected.g - actual.g) <= tolerance &&
                        Mathf.Abs(expected.b - actual.b) <= tolerance;
            Assert.IsTrue(close, $"{what}: expected {expected}, got {actual}");
        }

        /// <summary>Red at 0, blue at 1: where along the test gradient a colour is.</summary>
        private static Color32 RedToBlue(float t) =>
            new Color32((byte)Mathf.RoundToInt(255f * (1f - t)), 0, (byte)Mathf.RoundToInt(255f * t), 255);

        // ------------------------------------------------------------ detection

        [Test]
        public void PaintSupport_IsDetected_AndOnlyWhereThereIsAPaintGraph()
        {
            using var v1 = LoadFont(FontPath);
            using var v0 = LoadFont(V0FontPath);
            using var mono = LoadFont(LatinFontPath);

            Assert.IsTrue((ColorGlyphs.SupportedBy(v1) & ColorGlyphs.Support.Paint) != 0,
                "COLRv1 was not detected");
            Assert.IsTrue(ColorGlyphs.IsColorFont(v1));
            Assert.IsTrue((ColorGlyphs.SupportedBy(v0) & ColorGlyphs.Support.Paint) == 0,
                "a version 0 table was taken for version 1");
            Assert.AreEqual(ColorGlyphs.Support.None, ColorGlyphs.SupportedBy(mono));
        }

        [Test]
        public void GlyphWithNoPaint_IsLeftToTheOutlinePath()
        {
            using var font = LoadFont(FontPath);
            Assert.IsFalse(ColorGlyphs.TryDecode(font, GlyphOf(font, 'z'), Ppem / font.UnitsPerEm,
                White, out _), "a glyph with no colour data was decoded as colour");
        }

        // ---------------------------------------------------------------- fills

        [Test]
        public void Solid_FillsTheOutlineItIsClippedTo()
        {
            using var font = LoadFont(FontPath);
            var a = Decode(font, 'a');
            AssertColor(Red, At(a, 500, 400), "centre of the square");
            AssertColor(Red, At(a, 120, 20), "corner of the square");
            AssertColor(Nothing, At(a, 95, 400), "left of the square");

            // Alpha on the paint is the fill's alpha, not a premultiplied colour.
            var b = Decode(font, 'b');
            AssertColor(new Color32(0, 0, 255, 128), At(b, 500, 400), "half-transparent solid");
        }

        [Test]
        public void LinearGradient_RunsAlongItsLine()
        {
            using var font = LoadFont(FontPath);
            var c = Decode(font, 'c');
            // Texel centres, so the ends are a hair inside.
            AssertColor(RedToBlue(0.0125f), At(c, 110, 400), "left end", 4);
            AssertColor(RedToBlue(0.5f), At(c, 500, 400), "middle", 4);
            AssertColor(RedToBlue(0.9875f), At(c, 890, 400), "right end", 4);
            AssertColor(At(c, 500, 100), At(c, 500, 700), "the gradient does not vary down the square", 1);
        }

        [Test]
        public void LinearGradient_IsTurnedByItsThirdPoint()
        {
            // p0→p1 is the square's diagonal; p2 sits straight above p0, which
            // turns the gradient back to horizontal. Ignoring p2 puts the
            // middle of the gradient at the top-left corner instead of its
            // start.
            using var font = LoadFont(FontPath);
            var d = Decode(font, 'd');
            AssertColor(RedToBlue(0.0125f), At(d, 110, 790), "top-left corner", 4);
            AssertColor(RedToBlue(0.5f), At(d, 500, 400), "middle", 4);
            AssertColor(RedToBlue(0.9875f), At(d, 890, 10), "bottom-right corner", 4);
        }

        [Test]
        public void RadialGradient_RunsOutFromItsCentre()
        {
            using var font = LoadFont(FontPath);
            var e = Decode(font, 'e');
            AssertColor(RedToBlue(0.0177f), At(e, 500, 400), "centre", 5);
            AssertColor(RedToBlue(0.5f), At(e, 500, 600), "half the radius up", 5);
            AssertColor(RedToBlue(0.5f), At(e, 300, 400), "half the radius left", 5);
            AssertColor(Blue, At(e, 120, 20), "past the outer circle, padded");
        }

        [Test]
        public void RadialGradient_BetweenTwoCircles_StartsAtTheInnerOne()
        {
            using var font = LoadFont(FontPath);
            var u = Decode(font, 'u');
            AssertColor(RedToBlue(0.02f), At(u, 300, 400), "the inner circle's centre", 8);
            AssertColor(RedToBlue(0.98f), At(u, 890, 400), "the outer circle's edge", 8);
            // Off the axis between the centres the colour is not what a single
            // circle about either centre would give.
            var above = At(u, 500, 700);
            Assert.Greater(above.b, 150, "above the outer centre should be most of the way to blue");
            Assert.Less(above.b, 255);
        }

        [Test]
        public void SweepGradient_TurnsCounterClockwiseFromTheXAxis()
        {
            using var font = LoadFont(FontPath);
            var f = Decode(font, 'f');
            AssertColor(RedToBlue(0.25f), At(f, 500, 600), "straight up, a quarter turn", 5);
            AssertColor(RedToBlue(0.5f), At(f, 300, 400), "left, half a turn", 5);
            AssertColor(RedToBlue(0.75f), At(f, 500, 200), "straight down, three quarters", 5);
        }

        [Test]
        public void ExtendModes_RepeatAndReflectTileTheLine()
        {
            using var font = LoadFont(FontPath);
            // The line is 200 units long, starting at x = 100.
            var r = Decode(font, 'r');
            AssertColor(RedToBlue(0.25f), At(r, 150, 400), "repeat, first period", 6);
            AssertColor(RedToBlue(0.25f), At(r, 350, 400), "repeat, second period", 6);
            AssertColor(RedToBlue(0.25f), At(r, 750, 400), "repeat, fourth period", 6);

            var s = Decode(font, 's');
            AssertColor(RedToBlue(0.25f), At(s, 150, 400), "reflect, first period", 6);
            AssertColor(RedToBlue(0.75f), At(s, 350, 400), "reflect, second period runs backwards", 6);
            AssertColor(RedToBlue(0.25f), At(s, 550, 400), "reflect, third period", 6);
        }

        // ----------------------------------------------------------- transforms

        [Test]
        public void Transforms_MoveTheOutlineAndWhatFillsIt()
        {
            using var font = LoadFont(FontPath);

            // The strip at 100..400 moved 400 to the right.
            var g = Decode(font, 'g');
            AssertColor(Green, At(g, 650, 400), "translated strip");
            AssertColor(Nothing, At(g, 250, 400), "where the strip was");

            // The bar at the right, turned a quarter turn counter-clockwise
            // about the centre: it is now at the top.
            var h = Decode(font, 'h');
            AssertColor(Red, At(h, 500, 700), "rotated bar");
            AssertColor(Nothing, At(h, 800, 400), "where the bar was");
            AssertColor(Nothing, At(h, 500, 100), "the clockwise answer");

            // The square at half width: 50..450.
            var i = Decode(font, 'i');
            AssertColor(Blue, At(i, 250, 400), "inside the halved square");
            AssertColor(Blue, At(i, 70, 400), "its left edge moved in with it");
            AssertColor(Nothing, At(i, 600, 400), "right of the halved square");
        }

        [Test]
        public void Skew_LeansTheOutline()
        {
            // A positive x skew angle leans the strip left as it rises; this
            // one is negative, so the top is to the right of the bottom.
            using var font = LoadFont(FontPath);
            var j = Decode(font, 'j');
            float lean = 800f * Mathf.Tan(20f * Mathf.Deg2Rad); // 291 units at the top
            AssertColor(Red, At(j, 250, 20), "the foot of the strip, where it was");
            AssertColor(Red, At(j, 250 + lean, 780), "the head of the strip, leaned right");
            AssertColor(Nothing, At(j, 120, 780), "where the head was");
        }

        // --------------------------------------------------- layers and reuse

        [Test]
        public void Layers_DrawInOrder_AndAColrGlyphReusesThem()
        {
            using var font = LoadFont(FontPath);
            var m = Decode(font, 'm');
            AssertColor(Blue, At(m, 500, 300), "the triangle, on top");
            AssertColor(Red, At(m, 150, 50), "the square, behind");

            // The same picture at half size about (500, 400).
            var n = Decode(font, 'n');
            AssertColor(Blue, At(n, 500, 350), "the reused triangle");
            AssertColor(Red, At(n, 320, 220), "the reused square");
            AssertColor(Nothing, At(n, 150, 50), "outside the reused picture");
        }

        [Test]
        public void VariableFormats_DrawTheirDefaultValues()
        {
            using var font = LoadFont(FontPath);
            var t = Decode(font, 't');
            AssertColor(Green, At(t, 250, 400), "PaintVarSolid");
            AssertColor(RedToBlue(0.125f), At(t, 650, 100), "PaintVarLinearGradient under PaintVarTranslate, low", 5);
            AssertColor(RedToBlue(0.875f), At(t, 650, 700), "and high", 5);
        }

        [Test]
        public void ClipBox_CutsThePictureAndSizesTheTile()
        {
            using var font = LoadFont(FontPath);
            var q = Decode(font, 'q');
            AssertColor(Red, At(q, 300, 400), "inside the clip box");
            AssertColor(Nothing, At(q, 700, 400), "ink the clip box cuts off");
            // 400 units wide at a tenth of a texel each, and a texel of padding a side.
            Assert.AreEqual(42, q.Width, "the tile should be the clip box, not the ink");
        }

        // ----------------------------------------------------------- compositing

        [Test]
        public void Composite_SourceIn_KeepsTheSourceWhereTheBackdropIs()
        {
            using var font = LoadFont(FontPath);
            var k = Decode(font, 'k');
            AssertColor(Red, At(k, 500, 300), "inside the triangle: the square's colour");
            AssertColor(Nothing, At(k, 150, 50), "inside the square only");
        }

        [Test]
        public void Composite_Multiply_MultipliesColours()
        {
            using var font = LoadFont(FontPath);
            var l = Decode(font, 'l');
            AssertColor(Green, At(l, 500, 400), "yellow times cyan");
        }

        [Test]
        public void Composite_EveryMode_LeavesTheCoverageItsDefinitionGives()
        {
            // back is 100..700 x 0..600 at alpha 0.8 and front is 300..900 x
            // 200..800 with alpha falling from 1 to 0.6 up it, so there is a
            // point in the backdrop only, one in the source only and one in
            // both. A Porter-Duff mode is two numbers, how much of each side
            // survives, and the alpha left at those three points is what the
            // numbers say; a blend mode covers what source-over covers.
            //                       clear src dst s-over d-over s-in d-in s-out d-out s-atop d-atop xor plus
            Func<float, float, float>[] ofSource =
            {
                (sa, da) => 0f, (sa, da) => 1f, (sa, da) => 0f, (sa, da) => 1f, (sa, da) => 1f - da,
                (sa, da) => da, (sa, da) => 0f, (sa, da) => 1f - da, (sa, da) => 0f, (sa, da) => da,
                (sa, da) => 1f - da, (sa, da) => 1f - da, (sa, da) => 1f,
            };
            Func<float, float, float>[] ofBackdrop =
            {
                (sa, da) => 0f, (sa, da) => 0f, (sa, da) => 1f, (sa, da) => 1f - sa, (sa, da) => 1f,
                (sa, da) => 0f, (sa, da) => sa, (sa, da) => 0f, (sa, da) => 1f - sa, (sa, da) => 1f - sa,
                (sa, da) => sa, (sa, da) => 1f - sa, (sa, da) => 1f,
            };

            // (x, y, source alpha, backdrop alpha) at the three points.
            var points = new (float X, float Y, float Sa, float Da, string Name)[]
            {
                (200, 100, 0f, 0.8f, "backdrop only"),
                (800, 700, 1f - 0.4f * (500f / 600f), 0f, "source only"),
                (500, 400, 1f - 0.4f * (200f / 600f), 0.8f, "overlap"),
            };

            using var font = LoadFont(FontPath);
            for (int mode = 0; mode < 28; mode++)
            {
                bool drawn = ColorGlyphs.TryDecode(font, GlyphOf(font, 0xE000 + mode),
                    Ppem / font.UnitsPerEm, White, out var glyph);
                if (mode == 0)
                {
                    Assert.IsFalse(drawn, "clear leaves nothing to draw");
                    continue;
                }
                Assert.IsTrue(drawn, $"mode {mode} did not decode");

                int row = mode < 13 ? mode : 3;
                foreach (var point in points)
                {
                    float expected = Mathf.Min(1f,
                        point.Sa * ofSource[row](point.Sa, point.Da) +
                        point.Da * ofBackdrop[row](point.Sa, point.Da));
                    float actual = At(glyph, point.X, point.Y).a / 255f;
                    Assert.AreEqual(expected, actual, 0.02f, $"mode {mode}, {point.Name}: alpha");
                }
            }
        }

        // ------------------------------------------------------- the text colour

        [Test]
        public void ForegroundColour_FollowsTheText_InSolidsAndInStops()
        {
            using var font = LoadFont(FontPath);
            Assert.IsTrue(ColorGlyphs.UsesTextColor(font, GlyphOf(font, 'o')),
                "a solid in the text colour was not reported");
            Assert.IsTrue(ColorGlyphs.UsesTextColor(font, GlyphOf(font, 'p')),
                "a colour stop in the text colour was not reported");
            Assert.IsFalse(ColorGlyphs.UsesTextColor(font, GlyphOf(font, 'a')),
                "a glyph drawn entirely from the palette was reported as following the text");

            AssertColor(Green, At(Decode(font, 'o', Green), 500, 400), "solid, green text");
            AssertColor(Blue, At(Decode(font, 'o', Blue), 500, 400), "solid, blue text");

            var p = Decode(font, 'p', Green);
            AssertColor(new Color32(3, 252, 0, 255), At(p, 890, 400), "the stop at the end of the line", 5);
        }

        // ------------------------------------------------- beside version 0

        [Test]
        public void VersionOne_WinsOverVersionZero_ForTheSameGlyph()
        {
            using var font = LoadFont(FontPath);
            var v = Decode(font, 'v');
            // The layers would put a blue triangle here.
            AssertColor(Green, At(v, 500, 300), "the paint graph's colour");
        }

        [Test]
        public void VersionZeroGlyph_InAVersionOneTable_StillDrawsFromItsLayers()
        {
            using var font = LoadFont(FontPath);
            var x = Decode(font, 'x');
            AssertColor(Blue, At(x, 500, 300), "the layer on top");
            AssertColor(Red, At(x, 150, 50), "the layer behind");
        }

        // ------------------------------------------------------ untrusted input

        [Test]
        public void GlyphThatReusesItself_IsAbandoned_NotFollowedForever()
        {
            using var font = LoadFont(FontPath);
            Assert.IsFalse(ColorGlyphs.TryDecode(font, GlyphOf(font, 'w'), Ppem / font.UnitsPerEm,
                White, out _), "a cyclic paint graph produced a picture");
            Assert.IsFalse(ColorGlyphs.UsesTextColor(font, GlyphOf(font, 'w')));
        }

        /// <summary>Where one table sits in a font file: the directory record and the data.</summary>
        private static (int Record, int Offset, int Length) FindTable(byte[] font, string tag)
        {
            int tables = (font[4] << 8) | font[5];
            for (int i = 0; i < tables; i++)
            {
                int record = 12 + i * 16;
                if (font[record] != tag[0] || font[record + 1] != tag[1] ||
                    font[record + 2] != tag[2] || font[record + 3] != tag[3]) continue;
                int offset = (font[record + 8] << 24) | (font[record + 9] << 16) |
                             (font[record + 10] << 8) | font[record + 11];
                int length = (font[record + 12] << 24) | (font[record + 13] << 16) |
                             (font[record + 14] << 8) | font[record + 15];
                return (record, offset, length);
            }
            Assert.Fail($"the test face has no {tag} table");
            return default;
        }

        private static IEnumerable<int> EveryColourCodepoint()
        {
            for (char c = 'a'; c <= 'z'; c++)
                if (c != 'y') yield return c;
            for (int mode = 0; mode < 28; mode++) yield return 0xE000 + mode;
        }

        [Test]
        public void TruncatedTable_DrawsNothingAndDoesNotThrow()
        {
            var original = FontBytes(FontPath);
            var (record, _, length) = FindTable(original, "COLR");

            // The table cut off at several places: inside the header, inside
            // the base glyph list, inside the paints.
            foreach (int kept in new[] { 20, 34, 60, 200, length / 2, length - 3 })
            {
                var bytes = (byte[])original.Clone();
                bytes[record + 12] = (byte)(kept >> 24);
                bytes[record + 13] = (byte)(kept >> 16);
                bytes[record + 14] = (byte)(kept >> 8);
                bytes[record + 15] = (byte)kept;

                using var font = FontData.Load(bytes);
                foreach (int codepoint in EveryColourCodepoint())
                {
                    uint glyph = font.NominalGlyph(codepoint);
                    Assert.DoesNotThrow(() =>
                    {
                        ColorGlyphs.UsesTextColor(font, glyph);
                        ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm, White, out _);
                    }, $"table cut to {kept} bytes, U+{codepoint:X4}");
                }
            }
        }

        [Test]
        public void CorruptedTable_NeverThrowsOrRunsAway()
        {
            // Not a fuzzer, but the same idea at a size a test suite can
            // afford: a few bytes of the table changed, every glyph drawn.
            // What comes out is unspecified; that it comes out, promptly and
            // without an exception, is the assertion.
            var original = FontBytes(FontPath);
            var (_, offset, length) = FindTable(original, "COLR");
            var random = new System.Random(20261124);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            for (int round = 0; round < 150; round++)
            {
                var bytes = (byte[])original.Clone();
                int edits = 1 + random.Next(4);
                for (int e = 0; e < edits; e++)
                    bytes[offset + random.Next(length)] = (byte)random.Next(256);

                using var font = FontData.Load(bytes);
                foreach (int codepoint in EveryColourCodepoint())
                {
                    uint glyph = font.NominalGlyph(codepoint);
                    try
                    {
                        ColorGlyphs.UsesTextColor(font, glyph);
                        ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm, White, out _);
                    }
                    catch (Exception exception)
                    {
                        Assert.Fail($"round {round}, U+{codepoint:X4}: {exception}");
                    }
                }
            }

            Debug.Log($"[colr-v1] 150 corrupted tables, every glyph: {watch.ElapsedMilliseconds} ms");
            Assert.Less(watch.ElapsedMilliseconds, 30000, "a corrupted table made the renderer run away");
        }

        // ------------------------------------------------------ through a label

        [Test]
        public void Label_DrawsPaintGlyphsFromTheColourAtlas()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(400f, 120f);
                label.SetFont(FontBytes(FontPath));
                label.Text = "aefn";
                label.FontSize = 32f;
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                Assert.AreEqual(4, label.Quads.Count, "one quad a glyph");
                foreach (var quad in label.Quads)
                    Assert.IsTrue(quad.IsColor,
                        "a COLRv1 glyph was drawn through the SDF atlas");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }
    }
}
