using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>The font asset: storage, sharing and variable-font instances.</summary>
    public class FontAssetTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansVariable.ttf";

        private static OneFontAsset CreateAsset(string packagePath)
        {
            var bytes = File.ReadAllBytes(Path.GetFullPath(packagePath));
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.Initialize(bytes, "Test Family", packagePath);
            return asset;
        }

        [Test]
        public void Font_File_Round_Trips_Through_Compression()
        {
            var original = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            var asset = CreateAsset(LatinFontPath);
            try
            {
                var restored = asset.GetFontBytes();
                Assert.AreEqual(original.Length, restored.Length);
                Assert.AreEqual(original, restored, "decompressed bytes must match the font file");
                Assert.Less(asset.StoredSize, original.Length, "deflate should shrink a TTF");
                Debug.Log($"[asset] {original.Length / 1024} KB font stored as " +
                          $"{asset.StoredSize / 1024} KB ({asset.StoredSize / (float)original.Length:P0})");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_Shares_One_Parsed_Face()
        {
            var asset = CreateAsset(LatinFontPath);
            try
            {
                var first = asset.Font;
                var second = asset.Font;
                Assert.AreSame(first, second, "the asset owns one parsed font");
                Assert.IsTrue(first.IsValid);
                Assert.AreEqual("Test Family", asset.FamilyName);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Variants_Are_Cached_Per_Axis_Combination()
        {
            var asset = CreateAsset(VariableFontPath);
            try
            {
                var bold = asset.GetVariant(new[] { new FontVariation("wght", 700f) });
                var boldAgain = asset.GetVariant(new[] { new FontVariation("wght", 700f) });
                var light = asset.GetVariant(new[] { new FontVariation("wght", 200f) });

                Assert.AreSame(bold, boldAgain, "same axes must reuse one instance (and its atlas entries)");
                Assert.AreNotSame(bold, light);
                Assert.AreNotSame(asset.Font, bold, "the base font keeps its own default instance");
                Assert.AreNotEqual(bold.Generation, asset.Font.Generation);

                // A variant borrows the parsed face, so disposing it must not
                // take the shared face down with it.
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(bold.IsValid);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        // The packed copy of the font file, which a player lets go of once the
        // unpacked one exists. The editor must not, because the object under
        // test is the asset on disk; these take the branch deliberately.

        [Test]
        public void The_Editor_Keeps_The_Packed_Font_After_Unpacking_It()
        {
            var asset = CreateAsset(LatinFontPath);
            try
            {
                int packed = asset.StoredSize;
                Assert.IsTrue(asset.Font.IsValid);
                Assert.AreEqual(packed, asset.StoredSize,
                    "the editor must keep the bytes it would otherwise save to disk without");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Dropping_The_Packed_Font_Leaves_It_Drawing_And_Readable()
        {
            var original = File.ReadAllBytes(Path.GetFullPath(LatinFontPath));
            var asset = CreateAsset(LatinFontPath);
            try
            {
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(asset.DropPackedData(), "there was a packed copy to drop");
                Assert.AreEqual(0, asset.StoredSize);
                Assert.IsFalse(asset.DropPackedData(), "and only the once");

                Assert.IsTrue(asset.Font.IsValid, "the face reads the unpacked array, not the packed one");
                Assert.IsTrue(asset.Font.HasGlyph('A'));
                Assert.AreEqual(original, asset.GetFontBytes(),
                    "and the font file is still there to hand out");
                Assert.IsFalse(asset.IsPlaceholder, "a dropped packed copy is not a missing font");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void A_Dropped_Asset_Survives_Having_Its_Face_Rebuilt()
        {
            var asset = CreateAsset(VariableFontPath);
            try
            {
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(asset.DropPackedData());

                // Public, and it releases the face so the variants can be built
                // against the new one. Without the unpacked array kept, there
                // is nothing left to build from and every label on this font
                // goes blank for the rest of the process.
                asset.SetBaseVariations(new[] { new FontVariation("wght", 800f) });

                Assert.IsNotNull(asset.Font, "the asset must be able to reload its own face");
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(asset.Font.HasGlyph('A'));
                Assert.IsNotNull(asset.GetVariant(System.Array.Empty<FontVariation>()));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Repacking_A_Dropped_Asset_Puts_The_Packed_Copy_Back()
        {
            var asset = CreateAsset(LatinFontPath);
            try
            {
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(asset.DropPackedData());

                Assert.IsTrue(asset.Repack(OneFontAsset.FontPacking.Smallest));
                Assert.Greater(asset.StoredSize, 0, "repacking rebuilds the packed copy from the unpacked one");
                Assert.IsTrue(asset.Font.IsValid);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Emptying_A_Dropped_Asset_Still_Takes_Its_Font_Away()
        {
            var asset = CreateAsset(LatinFontPath);
            try
            {
                Assert.IsTrue(asset.Font.IsValid);
                Assert.IsTrue(asset.DropPackedData());

                asset.InitializePlaceholder("Waiting", default);

                Assert.IsTrue(asset.IsPlaceholder);
                Assert.IsNull(asset.GetFontBytes(), "the kept array must not outlive the font it was");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Nothing_Is_Dropped_From_A_Font_That_Was_Never_Packed()
        {
            // Bytes that brotli cannot shrink are stored as they are, and then
            // the stored copy is the one the face reads.
            var random = new System.Random(7);
            var incompressible = new byte[64 * 1024];
            random.NextBytes(incompressible);

            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            try
            {
                asset.Initialize(incompressible, "Incompressible", null);
                Assert.AreEqual(incompressible.Length, asset.StoredSize, "nothing was packed");

                Assert.AreEqual(incompressible, asset.GetFontBytes());
                Assert.IsFalse(asset.DropPackedData(), "these bytes are the font, not a copy of it");
                Assert.AreEqual(incompressible.Length, asset.StoredSize);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Variant_Disposal_Leaves_The_Shared_Face_Usable()
        {
            var asset = CreateAsset(VariableFontPath);
            try
            {
                var basis = asset.Font;
                var variant = basis.CreateVariant(new FontVariation("wght", 900f));
                variant.Dispose();

                Assert.IsTrue(basis.IsValid, "disposing a variant must not destroy the shared face");
                Assert.IsTrue(basis.HasGlyph('A'));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
