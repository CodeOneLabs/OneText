using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// The quad build has a straight path for the common label (no reveal,
    /// animator, modifier or tint; one or two fonts; no bands) and a set of
    /// lookups made cheaper underneath it: the ink-bounds cache, the cluster
    /// split's sort, the atlas key. Every one of those has a switch, and this
    /// is the test that the switches change nothing anyone can see: each case
    /// builds a label with every switch off and again with every switch on and
    /// holds the two meshes, the two quad lists and the two drawn lists to
    /// each other value for value.
    ///
    /// The cases are chosen to walk every branch the straight path skips or
    /// shortcuts, so that a case the shortcut gets wrong cannot be absent from
    /// here: bands, decorations by markup and by label, vertical writing,
    /// colour glyphs and sprites, reveal, a modifier, an animator, a tint,
    /// combining marks (spans out of ink order, so the sort still runs).
    /// </summary>
    public class StraightQuadsTests
    {
        private const string Fonts = "Packages/com.onetext.core/Tests/Fonts~/";
        private const string Coverage = "Packages/com.onetext.core/Tests/CoverageFonts~/";

        private static byte[] Bytes(string path) => File.ReadAllBytes(Path.GetFullPath(path));

        private static void SetAll(bool on)
        {
            FontData.FlatInkBounds = on;
            GlyphClusters.SkipOrderedSort = on;
            GlyphAtlas.FastKeys = on;
            OneTextLabel.StraightQuads = on;
            OneTextLabel.TrackedBounds = on;
        }

        [TearDown]
        public void TearDown()
        {
            SetAll(true);
            OneTextLabel.DirectMesh = true;
        }

        private sealed class Shift : ITextQuadModifier
        {
            public bool Modify(ref TextQuad quad, in TextQuadContext context)
            {
                quad.Position += new Vector2(3f, -2f);
                quad.Rotation += 10f;
                return quad.FirstGrapheme != 1;
            }
        }

        public sealed class Case
        {
            public string Name;
            public Action<OneTextLabel, List<Object>> Setup;
            public override string ToString() => Name;
        }

        private static Case Plain(string name, string text) => new Case
        {
            Name = name,
            Setup = (label, _) =>
            {
                label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                label.Text = text;
            },
        };

        private static IEnumerable<Case> Cases()
        {
            yield return Plain("one digit", "7");
            yield return Plain("digits", "12345");
            yield return Plain("words", "Score 12345, two\nlines of it");
            yield return Plain("combining marks", "é ä́ ñǫ");
            yield return Plain("rich", "<color=#ff000080>red</color> and <b>bold</b> <i>it</i> <size=40>big</size>");
            yield return Plain("bands", "<u>under</u> <s>struck</s> <mark=#ffff0080>marked</mark> plain");
            yield return Plain("decoration markup",
                "<outline=#000000>edged</outline> <shadow color=#204080ff x=0.75 y=-0.25>dropped</shadow> <glow=cyan>lit</glow>");
            yield return Plain("animator", "<wave>wavy</wave> still");
            yield return Plain("zero alpha", "<alpha=#00>gone</alpha> here");
            yield return new Case
            {
                Name = "label decoration",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Decoration = TextDecoration.DefaultOutline;
                    label.Text = "outlined <glow=red>and lit</glow>";
                },
            };
            yield return new Case
            {
                Name = "arabic",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSansArabic.ttf"), Bytes(Fonts + "NotoSans.ttf"));
                    label.Text = "بِسْمِ ٱللَّهِ and مرحبا";
                },
            };
            yield return new Case
            {
                Name = "vertical",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Coverage + "NotoSansCJKjp-Regular.otf"), Bytes(Fonts + "NotoSans.ttf"));
                    label.WritingMode = TextWritingMode.VerticalRightToLeft;
                    label.Text = "縦書きの<u>テキスト</u>abc12";
                },
            };
            yield return new Case
            {
                Name = "colour glyphs",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "ColorGlyphs.ttf"), Bytes(Fonts + "NotoSans.ttf"));
                    label.Text = "AD AD <outline=#000000>xy</outline>";
                },
            };
            yield return new Case
            {
                Name = "colour glyphs tinted",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "ColorGlyphs.ttf"), Bytes(Fonts + "NotoSans.ttf"));
                    label.TintColorGlyphs = true;
                    label.Text = "AD xy";
                },
            };
            yield return new Case
            {
                Name = "sprite",
                Setup = (label, created) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Sprites = NewSheet(created);
                    label.Text = "a<sprite=0>b";
                },
            };
            yield return new Case
            {
                Name = "reveal",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Text = "typewriter";
                    label.MaxVisibleGraphemes = 4;
                },
            };
            yield return new Case
            {
                Name = "modifier",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Text = "moved";
                    label.QuadModifier = new Shift();
                },
            };
            yield return new Case
            {
                Name = "tint",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Text = "tinted <color=#00ff00>green</color>";
                    label.color = new Color(1f, 0.5f, 0.25f, 0.75f);
                },
            };
            yield return new Case
            {
                Name = "precise",
                Setup = (label, _) =>
                {
                    label.SetFont(Bytes(Fonts + "NotoSans.ttf"));
                    label.Precise = true;
                    label.Text = "sharp 42";
                },
            };
        }

        private static OneTextSpriteSheet NewSheet(List<Object> created)
        {
            var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            var pixels = new Color[32 * 32];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.green;
            texture.SetPixels(pixels);
            texture.Apply();
            created.Add(texture);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 32, 32), Vector2.zero);
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

        /// <summary>Everything a build hands on: the mesh, the cached tiles, the drawn tiles.</summary>
        private sealed class Snapshot
        {
            public Vector3[] Positions, Normals;
            public Vector4[] Tangents;
            public Color32[] Colors;
            public List<Vector4>[] Uvs = new List<Vector4>[4];
            public int[] Triangles;
            public Bounds Bounds;
            public int SubVertices, SubIndices;
            public List<TextQuad> Quads, Drawn;
        }

        private static Snapshot Take(OneTextLabel label)
        {
            int before = OneTextLabel.DirectUploads;
            label.SetVerticesDirty();
            label.Rebuild(CanvasUpdate.PreRender);
            Assert.AreEqual(before + 1, OneTextLabel.DirectUploads, "the label did not take the direct route");
            var mesh = OneTextLabel.LastDirectMesh;
            var shot = new Snapshot
            {
                Positions = mesh.vertices,
                Normals = mesh.normals,
                Tangents = mesh.tangents,
                Colors = mesh.colors32,
                Triangles = mesh.triangles,
                Bounds = mesh.bounds,
                Quads = new List<TextQuad>(label.Quads),
                Drawn = new List<TextQuad>(label.DrawnQuads),
            };
            if (mesh.subMeshCount > 0)
            {
                var sub = mesh.GetSubMesh(0);
                shot.SubVertices = sub.vertexCount;
                shot.SubIndices = sub.indexCount;
            }
            for (int channel = 0; channel < 4; channel++)
            {
                shot.Uvs[channel] = new List<Vector4>();
                mesh.GetUVs(channel, shot.Uvs[channel]);
            }
            return shot;
        }

        /// <summary>One label, built twice: the quad build, then a cached re-emit.</summary>
        private static (Snapshot Built, Snapshot Cached) Build(Case c, bool on)
        {
            SetAll(on);
            var created = new List<Object>();
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvas.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(420f, 300f);
            try
            {
                var label = go.AddComponent<OneTextLabel>();
                label.FontSize = 28f;
                label.AnimationTime = 0.37f;
                c.Setup(label, created);
                var built = Take(label);
                var cached = Take(label);
                return (built, cached);
            }
            finally
            {
                Object.DestroyImmediate(canvas);
                foreach (var o in created) Object.DestroyImmediate(o);
            }
        }

        private static void AssertSame(Snapshot want, Snapshot got, string what)
        {
            Assert.Greater(want.Positions.Length, 0, $"{what}: nothing was drawn, so nothing was compared");
            CollectionAssert.AreEqual(want.Positions, got.Positions, $"{what}: positions");
            CollectionAssert.AreEqual(want.Colors, got.Colors, $"{what}: colours");
            CollectionAssert.AreEqual(want.Normals, got.Normals, $"{what}: normals");
            CollectionAssert.AreEqual(want.Tangents, got.Tangents, $"{what}: tangents");
            CollectionAssert.AreEqual(want.Triangles, got.Triangles, $"{what}: indices");
            for (int channel = 0; channel < 4; channel++)
                CollectionAssert.AreEqual(want.Uvs[channel], got.Uvs[channel], $"{what}: TEXCOORD{channel}");
            Assert.AreEqual(want.Bounds.min, got.Bounds.min, $"{what}: bounds min");
            Assert.AreEqual(want.Bounds.max, got.Bounds.max, $"{what}: bounds max");
            Assert.AreEqual(want.SubVertices, got.SubVertices, $"{what}: submesh vertices");
            Assert.AreEqual(want.SubIndices, got.SubIndices, $"{what}: submesh indices");
            // TextQuad has no Equals of its own; the struct default compares
            // every field, TextStyle and Rect included, which is the point.
            CollectionAssert.AreEqual(want.Quads, got.Quads, $"{what}: quads");
            CollectionAssert.AreEqual(want.Drawn, got.Drawn, $"{what}: drawn quads");
        }

        [TestCaseSource(nameof(Cases))]
        public void Every_Switch_Off_And_On_Draws_The_Same_Mesh(Case c)
        {
            // Off first and on second, then on first and off second: whichever
            // build runs first bakes the tiles the second one finds, so either
            // order alone could hide a difference that only shows when baking.
            var offFirst = Build(c, on: false);
            var onSecond = Build(c, on: true);
            var onFirst = Build(c, on: true);
            var offSecond = Build(c, on: false);

            AssertSame(offFirst.Built, onSecond.Built, "built, off then on");
            AssertSame(offFirst.Cached, onSecond.Cached, "re-emitted, off then on");
            AssertSame(offSecond.Built, onFirst.Built, "built, on then off");
            AssertSame(offSecond.Cached, onFirst.Cached, "re-emitted, on then off");
        }

        [Test]
        public void The_Drawn_List_Read_Late_Is_The_One_Drawn()
        {
            // The straight path records the drawn list rather than writing it;
            // a later read has to see the frame that was drawn, not the next
            // build's, and a write through the list has to start from it.
            SetAll(true);
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvas.transform, false);
            try
            {
                var label = go.AddComponent<OneTextLabel>();
                label.SetFont(Bytes(Fonts + "ColorGlyphs.ttf"), Bytes(Fonts + "NotoSans.ttf"));
                label.FontSize = 28f;
                label.Text = "AD <alpha=#00>x</alpha>y";
                label.SetVerticesDirty();
                label.Rebuild(CanvasUpdate.PreRender);
                Assert.AreEqual(label.Quads.Count - 1, label.DrawnQuads.Count,
                    "the zero-alpha tile was drawn, or a drawn one was lost");
                foreach (var quad in label.DrawnQuads)
                    if (quad.IsColor)
                        Assert.AreEqual(new Color32(255, 255, 255, 255), quad.Color,
                            "a picture's drawn colour is not the label's to change");
            }
            finally
            {
                Object.DestroyImmediate(canvas);
            }
        }

        // ------------------------------------------------------------ atlas

        private static List<uint> GlyphsOf(FontData font, string text)
        {
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            var ids = new List<uint>();
            foreach (char c in text)
            {
                shaped.Clear();
                shaper.Shape(font, c.ToString(), shaped);
                foreach (var glyph in shaped) ids.Add(glyph.GlyphId);
            }
            return ids;
        }

        /// <summary>
        /// The same requests into an atlas built with the fast keys and one
        /// without, under enough pressure to evict and to compact: every
        /// location handed out, every version stamp and the final statistics
        /// have to agree, because eviction follows the LRU and the LRU follows
        /// every hit.
        /// </summary>
        [Test]
        public void Fast_Keys_Leave_The_Atlas_Doing_Exactly_What_It_Did()
        {
            using var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            var text = new System.Text.StringBuilder();
            for (int cp = 'A'; cp <= 'Z'; cp++) text.Append((char)cp);
            for (int cp = 'a'; cp <= 'z'; cp++) text.Append((char)cp);
            for (int cp = 0x00C0; cp <= 0x017F; cp++) text.Append((char)cp);
            var glyphs = GlyphsOf(font, text.ToString());

            var shaped = new List<ShapedGlyph>();
            using (var shaper = new Shaper()) shaper.Shape(font, "Wave fill AVATAR 12345 office", shaped);

            List<string> Script(bool fast)
            {
                GlyphAtlas.FastKeys = fast;
                var log = new List<string>();
                using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 256, LayerCount = 1 });
                var clusters = new List<GlyphClusters.Cluster>();
                var positioned = new List<PositionedGlyph>();
                var locations = new List<GlyphLocation>();
                LogAssert.ignoreFailingMessages = true;
                try
                {
                    for (int frame = 0; frame < 4; frame++)
                    {
                        float size = frame % 2 == 0 ? 40f : 64f;
                        for (int i = 0; i < glyphs.Count; i += 1 + frame)
                        {
                            var at = atlas.GetOrAdd(font, glyphs[i], size);
                            log.Add($"g{glyphs[i]}@{size} {at.HasPixels} {at.UvRect} {at.Layer} v{atlas.Version}");
                            // The hot ones, touched again and again: the tail
                            // shortcut and the relink have to leave the same order.
                            atlas.GetOrAdd(font, glyphs[frame], size);
                        }
                        GlyphClusters.Split(font, shaped, clusters, positioned,
                            1000f * font.UnitsPerEm / 32f, GlyphClusters.DefaultMergeGapUnits(font));
                        bool located = atlas.PrepareClusters(font, 32f, positioned, clusters, locations);
                        log.Add($"prepare {located} {locations.Count} v{atlas.Version}");
                        foreach (var cluster in clusters)
                        {
                            var at = atlas.GetOrAddCluster(font, 32f, positioned, cluster.Start, cluster.Count, cluster.Hash);
                            log.Add($"c{cluster.Hash:x} {at.HasPixels} {at.UvRect} {at.Layer} v{atlas.Version}");
                        }
                        atlas.Flush();
                        if (frame == 2) atlas.Compact();
                        log.Add($"flush v{atlas.Version}");
                    }
                }
                finally
                {
                    LogAssert.ignoreFailingMessages = false;
                }
                var stats = atlas.GetStats();
                log.Add($"tiles {stats.TileCount} used {stats.UsedPixels} evictions {stats.Evictions} " +
                    $"compactions {stats.Compactions} drops {stats.Drops} shelves {stats.ShelfCount}");
                return log;
            }

            var slow = Script(false);
            var fast = Script(true);
            Assert.Greater(slow.Count, 100);
            StringAssert.DoesNotContain("evictions 0 ", slow[slow.Count - 1], "the test needs the atlas to evict");
            CollectionAssert.AreEqual(slow, fast);
        }

        [Test]
        public void The_Flat_Ink_Cache_Answers_What_The_Dictionary_Did()
        {
            using var font = FontData.Load(Bytes(Fonts + "NotoSans.ttf"));
            var answers = new List<string>();
            foreach (bool flat in new[] { false, true, true })
            {
                FontData.FlatInkBounds = flat;
                var sb = new System.Text.StringBuilder();
                // Past the end of the face too: an id with no glyph must say so
                // the same way through both.
                for (uint id = 0; id < 5000; id += 7)
                {
                    bool ink = font.TryGetInkBounds(id, out var lo, out var hi);
                    sb.Append(id).Append(ink).Append(lo).Append(hi).Append(';');
                }
                answers.Add(sb.ToString());
            }
            Assert.AreEqual(answers[0], answers[1], "first flat pass (measuring)");
            Assert.AreEqual(answers[0], answers[2], "second flat pass (cached)");
        }
    }
}
