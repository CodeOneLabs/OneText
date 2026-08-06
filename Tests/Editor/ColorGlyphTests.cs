using System.Collections.Generic;
using System.IO;
using OneText.UGUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// M8: colour glyphs.
    ///
    /// This is the pure-differentiation half of the milestone. TextMesh Pro
    /// cannot render a multi-codepoint emoji sequence at all (a ZWJ family, a
    /// flag, a skin tone comes out as separate glyphs), and the community
    /// answer is a hand-maintained sprite sheet. The shaping half was already
    /// done for us by UAX #29 and HarfBuzz; what is tested here is the colour
    /// half: reading CBDT bitmaps and COLRv0 layers out of a font and getting
    /// them into an RGBA atlas the same shader and the same draw call can use.
    ///
    /// The test face is authored, not vendored (Tests/Fonts/ColorGlyphs.ttf,
    /// 1.3 KB, built by the script beside it). Noto Color Emoji would also do
    /// it, at 10.7 MB of someone else's font carried forever to exercise two
    /// code paths.
    /// </summary>
    public class ColorGlyphTests
    {
        private const string ColorFontPath = "Packages/com.onetext.core/Tests/Fonts/ColorGlyphs.ttf";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";

        private static FontData LoadFont(string path) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(path)));

        private static uint GlyphOf(FontData font, char c)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, c.ToString(), shaped);
            Assert.AreEqual(1, shaped.Count, $"'{c}' should shape to one glyph");
            return shaped[0].GlyphId;
        }

        // ------------------------------------------------------------ detection

        [Test]
        public void ColorSupport_IsDetectedPerFace()
        {
            using var color = LoadFont(ColorFontPath);
            using var mono = LoadFont(LatinFontPath);

            var support = ColorGlyphs.SupportedBy(color);
            Assert.IsTrue((support & ColorGlyphs.Support.Bitmaps) != 0, "CBDT was not detected");
            Assert.IsTrue((support & ColorGlyphs.Support.Layers) != 0, "COLRv0 + CPAL was not detected");
            Assert.IsTrue(ColorGlyphs.IsColorFont(color));

            Assert.AreEqual(ColorGlyphs.Support.None, ColorGlyphs.SupportedBy(mono),
                "an ordinary text font must not be dragged down the colour path");
            Assert.IsFalse(ColorGlyphs.IsColorFont(mono));
        }

        // --------------------------------------------------------------- COLRv0

        [Test]
        public void ColrLayers_ComposeInOrder_FromThePalette()
        {
            using var font = LoadFont(ColorFontPath);
            uint glyph = GlyphOf(font, 'A');

            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var decoded), "COLRv0 glyph did not decode");
            Assert.Greater(decoded.Width, 0);
            Assert.Greater(decoded.Height, 0);

            // 'A' is a red square (palette 0) with a blue triangle (palette 1)
            // drawn over it. Both colours have to be present, and the blue has
            // to be on top where they overlap; layer order is the thing a
            // careless implementation reverses.
            bool sawRed = false, sawBlue = false;
            foreach (var texel in decoded.Pixels)
            {
                if (texel.a < 200) continue;
                if (texel.r > 200 && texel.b < 60) sawRed = true;
                if (texel.b > 200 && texel.r < 60) sawBlue = true;
            }
            Assert.IsTrue(sawRed, "the first layer's palette colour is missing");
            Assert.IsTrue(sawBlue, "the second layer's palette colour is missing");

            // The centre of the glyph is inside both shapes, so it must be the
            // later layer's colour.
            var centre = decoded.Pixels[decoded.Height / 2 * decoded.Width + decoded.Width / 2];
            Assert.Greater(centre.b, centre.r, "layers composited in the wrong order");
        }

        [Test]
        public void ColrLayer_UsingTheTextColour_FollowsTheLabel()
        {
            // 0xFFFF is the COLR spec's "use the text colour" sentinel, and it
            // is also the index a naive palette lookup reads out of bounds on.
            using var font = LoadFont(ColorFontPath);
            uint glyph = GlyphOf(font, 'B');

            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(0, 255, 0, 255), out var green));
            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 0, 255, 255), out var magenta));

            bool sawGreen = false, sawMagenta = false;
            foreach (var texel in green.Pixels)
                if (texel.a > 200 && texel.g > 200 && texel.r < 80) sawGreen = true;
            foreach (var texel in magenta.Pixels)
                if (texel.a > 200 && texel.r > 200 && texel.b > 200) sawMagenta = true;

            Assert.IsTrue(sawGreen, "the text-colour layer did not take the text colour");
            Assert.IsTrue(sawMagenta, "the text-colour layer did not follow a colour change");
        }

        // ----------------------------------------------------------- CBDT/CBLC

        [Test]
        public void CbdtBitmap_DecodesWithItsAlpha()
        {
            using var font = LoadFont(ColorFontPath);
            uint glyph = GlyphOf(font, 'C');

            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var decoded), "CBDT glyph did not decode");
            Assert.AreEqual(32, decoded.Width);
            Assert.AreEqual(32, decoded.Height);

            // The bitmap is a green centre inside a fully transparent border.
            // A decoder that drops alpha shows a green square edge to edge.
            var corner = decoded.Pixels[0];
            Assert.AreEqual(0, corner.a, "the bitmap's transparent border was lost");

            var centre = decoded.Pixels[16 * 32 + 16];
            Assert.Greater(centre.a, 200, "the bitmap's opaque centre is missing");
            Assert.Greater(centre.g, 150, "the bitmap's colour is wrong");
        }

        [Test]
        public void MonochromeGlyphInAColourFont_IsLeftToTheSdfPath()
        {
            using var font = LoadFont(ColorFontPath);
            uint glyph = GlyphOf(font, 'D');

            Assert.IsFalse(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out _),
                "a plain outline glyph must go down the SDF path, not be turned into a bitmap");
        }

        [Test]
        public void MonochromeGlyphInAColourFont_IsStillDrawn()
        {
            // A colour font is not a font in which every glyph has colour. Noto
            // Color Emoji carries monochrome glyphs, digits and .notdef, and
            // routing the whole run down the colour path drops every one of
            // them on the floor: the glyph simply is not there, and the pen
            // advances past a gap.
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(600f, 120f);
                label.SetFont(File.ReadAllBytes(Path.GetFullPath(ColorFontPath)));
                label.FontSize = 32f;

                label.Text = "D";           // the monochrome control
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                Assert.AreEqual(1, label.Quads.Count,
                    "a monochrome glyph in a colour font was not drawn at all");
                Assert.IsFalse(label.Quads[0].IsColor,
                    "a monochrome glyph must come from the SDF atlas");

                label.Text = "AD";          // one colour glyph, one not
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                Assert.AreEqual(2, label.Quads.Count, "a mixed run lost a glyph");
                bool sawColor = false, sawMono = false;
                foreach (var quad in label.Quads)
                {
                    if (quad.IsColor) sawColor = true;
                    else sawMono = true;
                }
                Assert.IsTrue(sawColor && sawMono,
                    "colour and monochrome glyphs must be able to share a run");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(canvas);
            }
        }

        [Test]
        public void TextColourGlyphs_AreCachedPerColour()
        {
            // A COLR layer using the text-colour sentinel bakes the label's
            // colour into the tile. Cache it colour-blind and the first label
            // to draw wins: every other colour is silently wrong, including the
            // same label after a fade.
            using var font = LoadFont(ColorFontPath);
            using var atlas = new ColorGlyphAtlas(512, 1);
            uint glyph = GlyphOf(font, 'B');
            Assert.IsTrue(ColorGlyphs.UsesTextColor(font, glyph),
                "the test needs a glyph that actually uses the sentinel");

            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(0, 255, 0, 255), out var green));
            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 0, 255, 255), out var magenta));

            // Two colours, two keys, two tiles.
            var a = atlas.GetOrAdd(Key(1, new Color32(0, 255, 0, 255)), green);
            var b = atlas.GetOrAdd(Key(1, new Color32(255, 0, 255, 255)), magenta);
            Assert.IsTrue(a.HasPixels);
            Assert.IsTrue(b.HasPixels);
            Assert.AreNotEqual(a.UvRect, b.UvRect,
                "two colours of the same glyph collapsed onto one tile");
            Assert.AreEqual(2, atlas.GetStats().TileCount);

            Assert.IsFalse(ColorGlyphs.UsesTextColor(font, GlyphOf(font, 'A')),
                "a glyph with no sentinel layer must not be keyed by colour; that would " +
                "cost a cache miss per label colour for nothing");
        }

        private static long Key(long glyph, Color32 tint) =>
            glyph ^ ((long)(tint.r << 24 | tint.g << 16 | tint.b << 8 | tint.a) << 8);

        // ----------------------------------------------------- the colour atlas

        [Test]
        public void ColorAtlas_CachesAndEvicts_WithoutBlanking()
        {
            using var font = LoadFont(ColorFontPath);
            using var atlas = new ColorGlyphAtlas(256, 1);
            uint glyph = GlyphOf(font, 'A');

            Assert.IsTrue(ColorGlyphs.TryDecode(font, glyph, 32f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var decoded));

            var first = atlas.GetOrAdd(1, decoded);
            Assert.IsTrue(first.HasPixels);
            Assert.IsTrue(atlas.Contains(1));

            // A second lookup must hit, not re-pack.
            var again = atlas.GetOrAdd(1, decoded);
            Assert.AreEqual(first.UvRect, again.UvRect);
            Assert.AreEqual(1, atlas.GetStats().TileCount);

            // Overflow a small atlas, then check the survivors still resolve:
            // the same "no permanent blanking" rule the SDF atlas follows.
            for (int i = 2; i < 400; i++) atlas.GetOrAdd(i, decoded);
            var stats = atlas.GetStats();
            Debug.Log($"[color-atlas] {stats.TileCount} tiles, {stats.Evictions} evictions, " +
                $"{stats.Drops} drops, {stats.MemoryBytes / 1024} KB");
            Assert.Greater(stats.Evictions, 0, "the test needs the atlas to overflow");

            var late = atlas.GetOrAdd(399, decoded);
            Assert.IsTrue(late.HasPixels, "a tile went missing after eviction");
        }

        // ------------------------------------------------------ through a label

        [Test]
        public void Label_DrawsColourGlyphsFromTheColourAtlas()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(400f, 120f);
                label.SetFont(File.ReadAllBytes(Path.GetFullPath(ColorFontPath)));
                label.Text = "AC";
                label.FontSize = 32f;
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                Assert.Greater(label.Quads.Count, 0, "the label drew nothing");
                foreach (var quad in label.Quads)
                    Assert.IsTrue(quad.IsColor,
                        "a glyph from a colour font was drawn through the SDF atlas");

                Assert.IsTrue(SharedGlyphAtlas.ColorAtlasExists,
                    "the colour atlas was never created");
                Assert.Greater(SharedGlyphAtlas.ColorAtlas.GetStats().TileCount, 0,
                    "no colour tiles were baked");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(canvas);
            }
        }

        [Test]
        public void ColorAtlas_Eviction_ClearsThePixelsItGivesBack()
        {
            // The atlas packs tiles flush and samples bilinear, so a tile's edge
            // texel is a neighbour's. Leaving an evicted tile's pixels behind
            // means a later tap can pick up something no longer in the atlas,
            // and the uv rect is inset by half a texel for the same reason.
            using var font = LoadFont(ColorFontPath);
            using var atlas = new ColorGlyphAtlas(256, 1);
            Assert.IsTrue(ColorGlyphs.TryDecode(font, GlyphOf(font, 'A'), 32f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var decoded));

            var first = atlas.GetOrAdd(1, decoded);
            Assert.IsTrue(first.HasPixels);
            int size = atlas.Texture.width;
            int x = Mathf.RoundToInt(first.UvRect.x * size);
            int y = Mathf.RoundToInt(first.UvRect.y * size);

            for (int i = 2; i < 400; i++) atlas.GetOrAdd(i, decoded);
            Assert.IsFalse(atlas.Contains(1), "the test needs the first tile evicted");

            // Whatever is at that spot now belongs to whoever owns it; what
            // must not happen is the evicted tile's pixels surviving untouched
            // under a rect nothing references.
            Assert.Greater(atlas.GetStats().Evictions, 0);
            Assert.AreEqual(0, atlas.GetStats().Drops, "eviction should have made room");
        }

        [Test]
        public void ColorAtlas_ReusesAnEmptyShelf_WithoutOverrunningIt()
        {
            // An empty shelf may re-type itself, but only within the rows it
            // owns. Growing past them writes a tall tile over the next shelf's
            // pixels while that shelf's entries still resolve to their old UVs.
            using var font = LoadFont(ColorFontPath);
            using var atlas = new ColorGlyphAtlas(256, 1);

            Assert.IsTrue(ColorGlyphs.TryDecode(font, GlyphOf(font, 'A'), 8f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var small));
            Assert.IsTrue(ColorGlyphs.TryDecode(font, GlyphOf(font, 'A'), 64f / font.UnitsPerEm,
                new Color32(255, 255, 255, 255), out var large));
            Assert.Greater(large.Height, small.Height, "the test needs two tile heights");

            // A short shelf first, then a taller one below it.
            var shortTile = atlas.GetOrAdd(1, small);
            var tallTile = atlas.GetOrAdd(2, large);
            Assert.IsTrue(shortTile.HasPixels);
            Assert.IsTrue(tallTile.HasPixels);

            // Fill until the short shelf's tile is evicted, then ask for a tall
            // tile: it must not be placed in the short shelf's rows.
            for (int i = 3; i < 200; i++) atlas.GetOrAdd(i, large);

            Assert.AreEqual(0, atlas.GetStats().Drops);
            foreach (var key in new long[] { 199, 198 })
            {
                Assert.IsTrue(atlas.Contains(key), "the most recent tiles must still be resident");
            }
        }

        // ------------------------------------------------------- inline sprites

        private static OneTextSpriteSheet NewSheet(int width, int height, Color color,
            List<Object> created)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            created.Add(texture);

            var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero);
            sprite.name = "icon";
            created.Add(sprite);

            var sheet = ScriptableObject.CreateInstance<OneTextSpriteSheet>();
            var list = (List<Sprite>)typeof(OneTextSpriteSheet)
                .GetField("_sprites", System.Reflection.BindingFlags.NonPublic |
                                      System.Reflection.BindingFlags.Instance)
                .GetValue(sheet);
            list.Add(sprite);
            created.Add(sheet);
            return sheet;
        }

        [Test]
        public void Sprite_TakesItsOwnWidthInTheLine()
        {
            var created = new List<Object>();
            try
            {
                // A wide icon must take a wide slot. Reserving a square em for
                // every sprite is the shortcut that makes 2:1 icons overlap the
                // text after them.
                var wide = NewSheet(64, 32, Color.red, created);
                var square = NewSheet(32, 32, Color.red, created);

                using var font = LoadFont(LatinFontPath);
                using var fonts = FontStack.Single(font);
                using var engine = new TextLayoutEngine();

                float Measure(OneTextSpriteSheet sheet)
                {
                    var markup = new RichTextResult();
                    RichTextParser.Parse("a<sprite=0>b", markup);
                    var settings = TextLayoutSettings.Default(fonts, 32f);
                    settings.Wrap = TextWrap.NoWrap;
                    settings.Spans = markup.Spans;
                    settings.ResolveSpriteAspect = sheet.AspectOf;
                    var result = new TextLayoutResult();
                    engine.Layout(markup.Text, settings, result);
                    return result.Width;
                }

                float squareWidth = Measure(square);
                float wideWidth = Measure(wide);
                Assert.AreEqual(32f, wideWidth - squareWidth, 0.5f,
                    "a 2:1 sprite must take one em more room than a 1:1 one");
            }
            finally
            {
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void Sprite_IsDrawnFromTheColourAtlas_NotAsTofu()
        {
            var created = new List<Object>();
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var sheet = NewSheet(32, 32, Color.green, created);
                var label = go.GetComponent<OneTextLabel>();
                label.rectTransform.sizeDelta = new Vector2(600f, 120f);
                label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
                label.FontSize = 32f;
                label.Sprites = sheet;
                label.Text = "a<sprite=0>b";
                label.SetAllDirty();
                label.Rebuild(CanvasUpdate.PreRender);

                int colorQuads = 0, textQuads = 0;
                foreach (var quad in label.Quads)
                {
                    if (quad.IsColor) colorQuads++;
                    else textQuads++;
                }
                Assert.AreEqual(1, colorQuads, "the sprite was not drawn from the colour atlas");
                Assert.AreEqual(2, textQuads, "the letters around the sprite went missing");

                // The placeholder must never reach the font: U+FFFC has no
                // glyph in a text face and would come out as tofu.
                Assert.AreEqual(3, label.LayoutResult.Glyphs.Count);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(canvas);
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void RepeatedSprites_AreEachDrawn()
        {
            // Two <sprite=0> placeholders compare equal as styles, and the
            // itemizer merges equal styles, so a row of identical icons became
            // one icon on a short line. A sprite must never merge with its
            // neighbour, identical or not.
            var created = new List<Object>();
            try
            {
                var sheet = NewSheet(32, 32, Color.red, created);
                using var font = LoadFont(LatinFontPath);
                using var fonts = FontStack.Single(font);
                using var engine = new TextLayoutEngine();

                float Measure(string source, out int glyphs)
                {
                    var markup = new RichTextResult();
                    RichTextParser.Parse(source, markup);
                    var settings = TextLayoutSettings.Default(fonts, 32f);
                    settings.Wrap = TextWrap.NoWrap;
                    settings.Spans = markup.Spans;
                    settings.ResolveSpriteAspect = sheet.AspectOf;
                    var result = new TextLayoutResult();
                    engine.Layout(markup.Text, settings, result);
                    glyphs = result.Glyphs.Count;
                    return result.Width;
                }

                float one = Measure("<sprite=0>", out int oneGlyph);
                float three = Measure("<sprite=0><sprite=0><sprite=0>", out int threeGlyphs);

                Assert.AreEqual(1, oneGlyph);
                Assert.AreEqual(3, threeGlyphs, "repeated sprites collapsed into one");
                Assert.AreEqual(one * 3f, three, 0.5f, "three icons must take three icons' room");
            }
            finally
            {
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void Sprite_MakesItsLineTallEnoughToHoldIt()
        {
            // A sprite rises a full em from the baseline, which is taller than
            // most fonts' ascenders. Taking the line height from whatever font
            // the placeholder resolved to lets the icon poke into the line
            // above.
            var created = new List<Object>();
            try
            {
                var sheet = NewSheet(32, 32, Color.red, created);
                using var font = LoadFont(LatinFontPath);
                using var fonts = FontStack.Single(font);
                using var engine = new TextLayoutEngine();

                TextLayoutResult Layout(string source)
                {
                    var markup = new RichTextResult();
                    RichTextParser.Parse(source, markup);
                    var settings = TextLayoutSettings.Default(fonts, 32f);
                    settings.Wrap = TextWrap.NoWrap;
                    settings.Spans = markup.Spans;
                    settings.ResolveSpriteAspect = sheet.AspectOf;
                    var result = new TextLayoutResult();
                    engine.Layout(markup.Text, settings, result);
                    return result;
                }

                var text = Layout("x");
                var withSprite = Layout("x<sprite=0>");

                Assert.GreaterOrEqual(withSprite.Lines[0].Ascent, 32f - 0.01f,
                    "the line does not reach the top of the sprite it contains");
                Assert.GreaterOrEqual(withSprite.Height, text.Height,
                    "a line with a sprite cannot be shorter than one without");
            }
            finally
            {
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void Sprite_CanBeAddressedByName()
        {
            var created = new List<Object>();
            try
            {
                var sheet = NewSheet(32, 32, Color.red, created);
                var result = new RichTextResult();
                RichTextParser.Parse("a<sprite=icon>b", result, null, null, sheet.IndexOf);

                Assert.AreEqual(3, result.Text.Length);
                Assert.IsTrue(result.StyleAt(1).IsSprite, "a named sprite did not resolve");
                Assert.AreEqual(0, result.StyleAt(1).Sprite);

                // A name nothing knows stays literal, like every other tag.
                var unknown = new RichTextResult();
                RichTextParser.Parse("<sprite=nosuch>", unknown, null, null, sheet.IndexOf);
                Assert.AreEqual("<sprite=nosuch>", unknown.Text);
            }
            finally
            {
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            }
        }

        // ------------------------------------------------- variation selectors

        [Test]
        public void VariationSelectors_ChooseTheFontInTheStack()
        {
            // U+FE0E asks for the text form of a dual-purpose character and
            // U+FE0F for the emoji one. That is a choice between fonts, not
            // something one font can resolve, so it belongs to the fallback
            // walk. 'A' exists in both test faces, which is what makes it a
            // stand-in for a character with two presentations.
            using var text = LoadFont(LatinFontPath);
            using var color = LoadFont(ColorFontPath);

            using var stack = new FontStack();
            stack.Add(text);
            stack.Add(color);

            Assert.AreSame(text, stack.Resolve('A', false, false, FontStack.Presentation.Text),
                "the text presentation must pick the text font");
            Assert.AreSame(color, stack.Resolve('A', false, false, FontStack.Presentation.Emoji),
                "the emoji presentation must pick the colour font, even though the text font " +
                "covers the character and comes first");
            Assert.AreSame(text, stack.Resolve('A', false, false, FontStack.Presentation.Any),
                "with no selector, plain coverage order decides");
        }

        [Test]
        public void VariationSelector_IsHonouredThroughLayout()
        {
            using var text = LoadFont(LatinFontPath);
            using var color = LoadFont(ColorFontPath);
            using var stack = new FontStack();
            stack.Add(text);
            stack.Add(color);
            using var engine = new TextLayoutEngine();

            var settings = TextLayoutSettings.Default(stack, 32f);
            settings.Wrap = TextWrap.NoWrap;

            var plain = new TextLayoutResult();
            engine.Layout("A", settings, plain);
            Assert.AreSame(text, plain.Runs[0].Font);

            var emoji = new TextLayoutResult();
            engine.Layout("A\uFE0F", settings, emoji);
            Assert.AreSame(color, emoji.Runs[0].Font,
                "U+FE0F did not steer the character to the colour font");

            // And the selector itself must not be drawn. It is a
            // default-ignorable character: HarfBuzz should hide it, and if it
            // does not, the reader sees a box next to every emoji.
            Assert.AreEqual(1, emoji.Runs.Count, "the selector started a run of its own");
            var plainInColor = new TextLayoutResult();
            engine.Layout("A", TextLayoutSettings.Default(FontStack.Single(color), 32f), plainInColor);
            Assert.AreEqual(plainInColor.Width, emoji.Width, 0.01f,
                "the variation selector took width of its own; it must be invisible");
        }

        [Test]
        public void VariationSelector_FallsBackWhenNoFontCanHonourIt()
        {
            // Asking for the emoji form of a character no colour font has must
            // still draw the character.
            using var text = LoadFont(LatinFontPath);
            using var stack = FontStack.Single(text);
            Assert.AreSame(text, stack.Resolve('A', false, false, FontStack.Presentation.Emoji));
        }

        [Test]
        public void MulticodepointSequence_ShapesToOneGlyph_AndOneCluster()
        {
            // The claim TMP cannot make. The test font ligates A+B, standing in
            // for the ZWJ sequences a real emoji font ligates: what matters is
            // that several codepoints become one glyph and one grapheme, so a
            // typewriter reveals it whole and an effect moves it as one thing.
            using var font = LoadFont(ColorFontPath);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, "AB", shaped);

            Assert.AreEqual(1, shaped.Count,
                "two codepoints did not ligate: the font's GSUB is not being applied");

            using var fonts = FontStack.Single(font);
            using var engine = new TextLayoutEngine();
            var settings = TextLayoutSettings.Default(fonts, 32f);
            settings.Wrap = TextWrap.NoWrap;
            var layout = new TextLayoutResult();
            engine.Layout("AB", settings, layout);

            Assert.AreEqual(1, layout.Glyphs.Count, "the ligature did not survive layout");
        }
    }
}
