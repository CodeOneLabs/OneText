using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The natives are laid out the way the loader expects.
    ///
    /// A missing or mis-tagged binary is not a compile error and not a test
    /// failure anywhere else; it is a build that succeeds and an app that
    /// shows no text, discovered on a device. Everything here is checkable from
    /// the editor on any host, so the whole matrix is covered by one CI run
    /// even though only one platform can actually be exercised.
    /// </summary>
    public class NativesTests
    {
        private const string PluginRoot = "Packages/com.onetext.core/Runtime/Plugins";

        // Path, the one BuildTarget it may be enabled for, its CPU tag, and the
        // editor OS it serves (null = player only).
        private static readonly (string path, BuildTarget target, string cpu, string editorOs)[] Expected =
        {
            ("macOS/libHarfBuzzSharp.dylib", BuildTarget.StandaloneOSX, "AnyCPU", "OSX"),
            ("Windows/x86_64/libHarfBuzzSharp.dll", BuildTarget.StandaloneWindows64, "x86_64", "Windows"),
            ("Windows/x86/libHarfBuzzSharp.dll", BuildTarget.StandaloneWindows, "x86", null),
            ("Windows/ARM64/libHarfBuzzSharp.dll", BuildTarget.StandaloneWindows64, "ARM64", "Windows"),
            ("Linux/x86_64/libHarfBuzzSharp.so", BuildTarget.StandaloneLinux64, "x86_64", "Linux"),
            ("Android/arm64-v8a/libHarfBuzzSharp.so", BuildTarget.Android, "ARM64", null),
            ("Android/armeabi-v7a/libHarfBuzzSharp.so", BuildTarget.Android, "ARMv7", null),
            ("Android/x86_64/libHarfBuzzSharp.so", BuildTarget.Android, "X86_64", null),
            ("iOS/libHarfBuzzSharp.xcframework", BuildTarget.iOS, "AnyCPU", null),
        };

        [Test]
        public void EveryPlatformHasABinary()
        {
            var missing = new List<string>();
            foreach (var (path, _, _, _) in Expected)
            {
                string full = Path.GetFullPath($"{PluginRoot}/{path}");
                if (!File.Exists(full) && !Directory.Exists(full)) missing.Add(path);
            }
            CollectionAssert.IsEmpty(missing,
                "a platform in the shipping matrix has no HarfBuzz binary; that platform builds " +
                "and then draws nothing. See Docs/NATIVES.md to re-vendor.");
        }

        [Test]
        public void EveryBinaryIsTaggedForExactlyItsOwnPlatform()
        {
            // Every one of these files is called libHarfBuzzSharp. Two enabled
            // for one platform is a build error; one enabled for none is silent.
            // So this asserts both directions: enabled where it belongs, and
            // enabled nowhere else. Only the second half catches the failure
            // that actually happens, which is a binary quietly inheriting a
            // platform it has no business being on.
            foreach (var (path, target, cpu, editorOs) in Expected)
            {
                string assetPath = $"{PluginRoot}/{path}";
                var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                Assert.IsNotNull(importer, $"{path} is not imported as a plugin; its .meta is missing " +
                    "or wrong, and Unity will not ship it");

                Assert.IsFalse(importer.GetCompatibleWithAnyPlatform(),
                    $"{path} is marked Any Platform; it collides with every other libHarfBuzzSharp");

                Assert.IsTrue(importer.GetCompatibleWithPlatform(target),
                    $"{path} is not enabled for {target}");
                Assert.AreEqual(cpu, importer.GetPlatformData(target, "CPU"),
                    $"{path} is tagged for the wrong CPU; Unity would ship it to the wrong ABI");

                foreach (var other in (BuildTarget[])System.Enum.GetValues(typeof(BuildTarget)))
                {
                    if (other == target || (int)other < 0) continue;
                    bool enabled;
                    try { enabled = importer.GetCompatibleWithPlatform(other); }
                    catch (System.ArgumentException) { continue; } // platform module not installed
                    Assert.IsFalse(enabled,
                        $"{path} is also enabled for {other}; two binaries of the same name on one " +
                        "platform is a build Unity refuses");
                }

                // The editor loads a native plugin for the machine it runs on,
                // and picks by OS and CPU. Get either wrong and two plugins
                // claim the same host, or none does and the editor has no
                // HarfBuzz at all.
                Assert.AreEqual(editorOs != null, importer.GetCompatibleWithEditor(),
                    $"{path}: editor compatibility does not match what this binary is for");
                if (editorOs == null) continue;
                Assert.AreEqual(editorOs, importer.GetEditorData("OS"), $"{path}: wrong editor OS");
                Assert.AreEqual(cpu, importer.GetEditorData("CPU"), $"{path}: wrong editor CPU");
            }
        }

        [Test]
        public void AndroidSixtyFourBitBinariesAre16KbAligned()
        {
            // Google requires 16 KB page alignment for 64-bit Android libraries.
            // Unity records what it found in the importer, so the check is free
            // and does not need an ELF parser or an Android host.
            foreach (var abi in new[] { "arm64-v8a", "x86_64" })
            {
                string assetPath = $"{PluginRoot}/Android/{abi}/libHarfBuzzSharp.so";
                var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                Assert.IsNotNull(importer, assetPath);
                Assert.AreEqual("true", importer.GetPlatformData(BuildTarget.Android, "Is16KbAligned"),
                    $"Android/{abi} is not 16 KB page aligned; Google Play rejects 64-bit " +
                    "libraries that are not, and the failure is at submission, not at build");
            }
        }

        [Test]
        public void IosShipsOneXcframeworkCarryingDeviceAndSimulator()
        {
            string root = $"{PluginRoot}/iOS/libHarfBuzzSharp.xcframework";
            var importer = AssetImporter.GetAtPath(root) as PluginImporter;
            Assert.IsNotNull(importer, "the iOS xcframework is not imported as a plugin");

            // A dynamic framework that is not embedded is in the Xcode project
            // and absent from the app that ships.
            Assert.AreEqual("true", importer.GetPlatformData(BuildTarget.iOS, "AddToEmbeddedBinaries"),
                "the iOS framework is not embedded; it links and then is not there at runtime");

            // Device and simulator binaries have the same name, and the plugin
            // importer has no switch to keep two of them apart. One xcframework
            // carrying both slices is the format that solves that, so the test
            // is that both slices are actually in there.
            foreach (var slice in new[] { "ios-arm64", "ios-arm64_x86_64-simulator" })
            {
                string binary = Path.GetFullPath(
                    $"{root}/{slice}/libHarfBuzzSharp.framework/libHarfBuzzSharp");
                Assert.IsTrue(File.Exists(binary), $"the xcframework has no {slice} binary");
            }

            // Microsoft's device framework ships a *simulator* Info.plist, and
            // left alone it fails device install and App Store validation. The
            // plists are binary, so this looks for bytes rather than text.
            var devicePlist = File.ReadAllBytes(Path.GetFullPath(
                $"{root}/ios-arm64/libHarfBuzzSharp.framework/Info.plist"));
            Assert.IsTrue(Contains(devicePlist, "iPhoneOS"),
                "the iOS device slice still claims to be a simulator build");
            Assert.IsFalse(Contains(devicePlist, "iPhoneSimulator"),
                "the iOS device slice still claims to be a simulator build");

            var manifest = File.ReadAllText(Path.GetFullPath($"{root}/Info.plist"));
            StringAssert.Contains("XFWK", manifest, "the xcframework manifest is not an xcframework");
            StringAssert.Contains("SupportedPlatformVariant", manifest,
                "the xcframework manifest does not distinguish the simulator slice");
        }

        private static bool Contains(byte[] haystack, string needle)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i + bytes.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < bytes.Length && haystack[i + j] == bytes[j]) j++;
                if (j == bytes.Length) return true;
            }
            return false;
        }

        [Test]
        public void SubsettingIsAvailable_OnThisPlatform()
        {
            // Subsetting runs in the editor, so strictly it is only needed where
            // the editor runs, but a platform build that omits harfbuzz-subset
            // makes the feature platform-dependent, which is worse than not
            // having it. This asserts it for the host; Docs/NATIVES.md records
            // the symbol count verified for every other binary at vendor time.
            Assert.IsTrue(HarfBuzzSubset.IsAvailable,
                "the vendored HarfBuzz has no subsetting API; the binary was built without " +
                "harfbuzz-subset, which is a separate library in HarfBuzz's build");
        }

        [Test]
        public void HarfBuzzLoadsAndReportsItsVersion()
        {
            string version = Shaper.HarfBuzzVersion;
            Assert.IsFalse(string.IsNullOrEmpty(version), "HarfBuzz did not load");
            Debug.Log($"[natives] HarfBuzz {version} on {Application.platform}");
        }
    }
}
