using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace OneText.Tests
{
    /// <summary>
    /// The version, said in two places, held to one answer.
    ///
    /// <see cref="OneTextInfo.Version"/> read 0.1.0 while the package shipped
    /// 0.2.0, and had done for two releases, because nothing at runtime reads
    /// it and nothing checked it. It cannot simply be deleted in favour of the
    /// manifest: the editor can ask the package manager what version it is
    /// running and a player build cannot, which is exactly the build whose
    /// crash report most needs to say.
    ///
    /// So the constant stays and this keeps it honest. A release that bumps
    /// one and forgets the other fails here rather than a year later in
    /// somebody's bug report.
    /// </summary>
    public sealed class PackageVersionTests
    {
        private const string ManifestPath = "Packages/com.onetext.core/package.json";

        private static string ManifestVersion()
        {
            string full = Path.GetFullPath(ManifestPath);
            if (!File.Exists(full))
                Assert.Ignore($"{ManifestPath} is not resolvable from this project.");

            var match = Regex.Match(File.ReadAllText(full),
                "\"version\"\\s*:\\s*\"([^\"]+)\"");
            Assert.IsTrue(match.Success, "package.json declares no version");
            return match.Groups[1].Value;
        }

        [Test]
        public void TheConstantAndTheManifest_AgreeOnTheVersion()
        {
            Assert.AreEqual(ManifestVersion(), OneTextInfo.Version,
                "package.json and OneTextInfo.Version disagree. Whichever was bumped, bump the " +
                "other: the manifest is what the editor and OpenUPM read, and the constant is " +
                "what a shipped player build has instead of a package manager.");
        }

        [Test]
        public void TheVersion_IsThreeNumbers()
        {
            // Not a style rule: OpenUPM and the package manager both resolve on
            // semver, and a version they cannot parse is a package that does not
            // list rather than one that lists wrongly.
            Assert.IsTrue(Regex.IsMatch(OneTextInfo.Version, @"^\d+\.\d+\.\d+([-+].*)?$"),
                $"'{OneTextInfo.Version}' is not a semantic version");
        }

        [Test]
        public void ThePackageName_IsTheOneTheManifestDeclares()
        {
            string full = Path.GetFullPath(ManifestPath);
            if (!File.Exists(full))
                Assert.Ignore($"{ManifestPath} is not resolvable from this project.");

            var match = Regex.Match(File.ReadAllText(full), "\"name\"\\s*:\\s*\"([^\"]+)\"");
            Assert.IsTrue(match.Success, "package.json declares no name");
            Assert.AreEqual(match.Groups[1].Value, OneTextInfo.PackageName);
        }
    }
}
