using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace OneText.Tests
{
    /// <summary>
    /// The partial upload path, checked where it matters: in the texture the
    /// GPU samples, not in the CPU copy the atlas writes first.
    ///
    /// A flush packs the frame's new tiles into a staging texture, uploads that
    /// once and blits each tile into place. An <c>Apply</c> is not ordered
    /// against the copies queued before it, so a staging texture that is
    /// uploaded twice before those copies run hands every one of them the
    /// second upload's pixels: one glyph stamped over another ("Yoji" drawn as
    /// "iYoji"). These tests read the array back from the GPU.
    ///
    /// They need a device that can do partial uploads and read textures back,
    /// so they report as ignored on a headless runner rather than passing
    /// without having looked.
    /// </summary>
    public class AtlasUploadTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private static FontData LoadFont(string packagePath) =>
            FontData.Load(File.ReadAllBytes(Path.GetFullPath(packagePath)));

        private static void RequireTheGpuThisIsAbout()
        {
            if (!GlyphAtlas.SupportsAtlasTextures || !GlyphAtlas.SupportsPartialUpload)
                Assert.Ignore("Partial atlas uploads are not available on this device " +
                              $"({SystemInfo.graphicsDeviceType}); the full upload path has no staging texture.");
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("This device cannot read a texture back from the GPU.");
        }

        /// <summary>
        /// A tile filled with a pattern no other tile shares: a stamped-over
        /// tile reads as someone else's pattern, not as a slightly wrong one.
        /// </summary>
        private static RasterizedGlyph Tile(int width, int height, int seed, bool precise)
        {
            int channels = precise ? 4 : 1;
            var pixels = new byte[width * height * channels];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(1 + (seed * 97 + i * 31 + i / (width * channels) * 7) % 254);
            return new RasterizedGlyph
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                OriginUnits = Vector2.zero,
                UnitsPerPixel = 1f,
                Channels = channels,
            };
        }

        /// <summary>The atlas's layer as the GPU has it.</summary>
        private static byte[] ReadBack(GlyphAtlas atlas, int layer)
        {
            int size = atlas.Settings.TextureSize;
            var request = AsyncGPUReadback.Request(atlas.Texture, 0, 0, size, 0, size, layer, 1);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "reading the atlas back from the GPU failed");
            return request.GetData<byte>(0).ToArray();
        }

        private static void AssertTileOnGpu(byte[] gpu, GlyphAtlas atlas, GlyphLocation location,
            RasterizedGlyph tile, string label)
        {
            int size = atlas.Settings.TextureSize;
            int bytesPerTexel = tile.BytesPerTexel;
            int x0 = Mathf.RoundToInt(location.UvRect.x * size);
            int y0 = Mathf.RoundToInt(location.UvRect.y * size);
            for (int row = 0; row < tile.Height; row++)
            for (int column = 0; column < tile.Width * bytesPerTexel; column++)
            {
                byte expected = tile.Pixels[row * tile.Width * bytesPerTexel + column];
                byte actual = gpu[((y0 + row) * size + x0) * bytesPerTexel + column];
                if (expected == actual) continue;
                Assert.Fail($"{label}: the GPU holds {actual} at row {row}, byte {column} of the tile, " +
                            $"where its own pixels say {expected}: another tile was uploaded over it");
            }
        }

        [Test]
        public void TilesThatOutgrowTheFirstStagingGuess_EachKeepTheirOwnPixels()
        {
            RequireTheGpuThisIsAbout();
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 1 },
                precise: true);

            // The sizes from the report: "Yo" as a cluster, then "j" and "i" at
            // a 128 ppem bucket. Their area asks for a 256 staging texture; the
            // first two fill its first row 152 texels tall, and "i" then needs
            // 272 rows. The old flush uploaded what it had, started again at
            // (0, 0) and wrote "i" over the left of "Yo".
            var sizes = new[] { (182, 115), (47, 152), (36, 120) };
            var tiles = new RasterizedGlyph[sizes.Length];
            var locations = new GlyphLocation[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                tiles[i] = Tile(sizes[i].Item1, sizes[i].Item2, i + 1, precise: true);
                locations[i] = atlas.CommitForTests(font, 1000 + i, 128, tiles[i]);
                Assert.IsTrue(locations[i].HasPixels, $"tile {i} found no room");
            }
            atlas.Flush();

            var stats = atlas.GetStats();
            Assert.AreEqual(1, stats.PartialUploads, "three tiles must go up as one partial upload");
            Assert.AreEqual(0, stats.FullUploads, "three tiles must not fall back to a full upload");

            var gpu = ReadBack(atlas, 0);
            for (int i = 0; i < tiles.Length; i++) AssertTileOnGpu(gpu, atlas, locations[i], tiles[i], $"tile {i}");
        }

        [Test]
        public void SeveralFlushesInOneFrame_DoNotUploadOverEachOther()
        {
            // A canvas pass and a world-space mesh each flush the shared atlas,
            // and outside play mode every label flushes as it bakes: several
            // uploads of the same staging texture inside one frame, the same
            // race across flushes that the report found inside one.
            RequireTheGpuThisIsAbout();
            using var font = LoadFont(LatinFontPath);
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 1024, LayerCount = 1 });

            const int flushes = 6;
            var tiles = new RasterizedGlyph[flushes * 3];
            var locations = new GlyphLocation[tiles.Length];
            for (int flush = 0; flush < flushes; flush++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = flush * 3 + k;
                    tiles[i] = Tile(30 + 7 * k, 40 + 5 * k, i + 1, precise: false);
                    locations[i] = atlas.CommitForTests(font, 2000 + i, 48, tiles[i]);
                    Assert.IsTrue(locations[i].HasPixels, $"tile {i} found no room");
                }
                atlas.Flush();
            }

            var stats = atlas.GetStats();
            Assert.AreEqual(flushes, stats.PartialUploads, "every flush must stay a partial upload");
            Assert.AreEqual(0, stats.FullUploads);

            var gpu = ReadBack(atlas, 0);
            for (int i = 0; i < tiles.Length; i++) AssertTileOnGpu(gpu, atlas, locations[i], tiles[i], $"tile {i}");
        }
    }
}
