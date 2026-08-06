using NUnit.Framework;
using OneText.Editor;
using UnityEditor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The shader has to be in the player, and nothing in a scene says so.
    ///
    /// Every label shares one material this package builds at runtime, so the
    /// build's dependency walk never meets the SDF shader and strips it: a
    /// player in which every label lays out correctly and draws nothing. The
    /// fix is a folder name: the shader lives under <c>Resources</c>, which
    /// ships it whether or not anything visibly asks for it. That is an
    /// invariant of where a file sits, so it is worth a test that fails the
    /// day somebody tidies the file back into a folder that reads better.
    /// </summary>
    public class ShaderShippingTests
    {
        [Test]
        public void Shader_LoadsFromResources_RatherThanByName()
        {
            var fromResources = Resources.Load<Shader>(SharedGlyphAtlas.ShaderResourcePath);
            Assert.IsNotNull(fromResources,
                $"Resources.Load('{SharedGlyphAtlas.ShaderResourcePath}') found nothing; the shader " +
                "is no longer under a Resources folder, and a player build would strip it");
            Assert.AreEqual(SharedGlyphAtlas.ShaderName, fromResources.name);
            Assert.AreSame(fromResources, SharedGlyphAtlas.LoadShader(),
                "the loader fell through to Shader.Find, which only works in the editor");
        }

        [Test]
        public void ShaderAsset_SitsUnderAResourcesFolderInThePackage()
        {
            string path = AssetDatabase.GetAssetPath(
                Resources.Load<Shader>(SharedGlyphAtlas.ShaderResourcePath));

            // The whole mechanism is this substring. Asserting the full path
            // would also pin the folder above it, which is free to move.
            StringAssert.Contains("/Resources/", path);
            Assert.IsTrue(path.EndsWith($"/{SharedGlyphAtlas.ShaderResourcePath}.shader"),
                $"the resource name and the file name have to agree; path was '{path}'");
        }

        [Test]
        public void SharedMaterial_UsesTheShaderThatShips()
        {
            var material = SharedGlyphAtlas.Material;
            Assert.IsNotNull(material);
            Assert.AreSame(Resources.Load<Shader>(SharedGlyphAtlas.ShaderResourcePath), material.shader,
                "the shared material is built from some other copy of the shader than the one " +
                "the build will include");
        }

        [Test]
        public void Doctor_IsQuietWhenTheShaderWillShip()
        {
            var report = TextDoctor.Run(null, null);
            foreach (var finding in report.Findings)
                Assert.AreNotEqual("sdf-shader", finding.Rule, finding.Message);
        }
    }
}
