using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>The font asset: storage, sharing and variable-font instances.</summary>
    public class FontAssetTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansVariable.ttf";

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
