using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// Bundled fonts read from files on disk: mapped where the platform has
    /// files, and from the fallback source wherever it does not — a key with no
    /// file, or a root that is inside an archive the way Android's
    /// StreamingAssets is.
    /// </summary>
    public class FileFontSourceTests
    {
        private const string Key = "Fonts/Arabic Font";
        private const string Arabic = "سلام";

        /// <summary>A fallback that hands out one in-memory asset and counts.</summary>
        private sealed class Fallback : IFontSource
        {
            public OneFontAsset Asset;
            public int Loads, Releases;

            public void Load(string key, Action<OneFontAsset> done) => done(LoadNow(key));

            public OneFontAsset LoadNow(string key)
            {
                Loads++;
                return Asset;
            }

            public void Release(string key, OneFontAsset font) => Releases++;
        }

        private readonly List<Object> _created = new List<Object>();
        private string _root;
        private Fallback _fallback;
        private OneTextSettings _previousSettings;

        [SetUp]
        public void SetUp()
        {
            FontResidency.ResetForTests();
            SystemFonts.Enabled = false;
            _root = Path.Combine(Path.GetTempPath(), "onetext-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Fonts"));
            File.Copy(Path.GetFullPath(MappedFontTests.ArabicFontPath), Path.Combine(_root, Key + ".ttf"));

            var packed = ScriptableObject.CreateInstance<OneFontAsset>();
            packed.hideFlags = HideFlags.HideAndDontSave;
            packed.Initialize(File.ReadAllBytes(Path.GetFullPath(MappedFontTests.ArabicFontPath)), "Packed", "Packed");
            _created.Add(packed);
            _fallback = new Fallback { Asset = packed };
            _previousSettings = OneTextSettings.Instance;
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.ResetForTests();
            SystemFonts.UseProjectSetting();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
            try { Directory.Delete(_root, true); }
            catch (Exception) { /* left for the OS */ }
        }

        [Test]
        public void A_Key_With_A_File_Is_Mapped_And_Holds_Nothing_On_The_Heap()
        {
            var source = new FileFontSource(_root, _fallback);
            Assert.IsTrue(source.ReadsFiles);

            var asset = source.LoadNow(Key);
            Assert.NotNull(asset);
            Assert.IsTrue(asset.IsFileBacked);
            Assert.IsTrue(asset.Font.IsMapped);
            Assert.IsTrue(asset.Font.HasGlyph(0x0628));
            Assert.AreEqual(0, asset.ResidentBytes, "a mapped font counted managed bytes");
            Assert.AreEqual(new FileInfo(Path.Combine(_root, Key + ".ttf")).Length, asset.MappedBytes);
            Assert.AreEqual(0, _fallback.Loads);
            Assert.AreEqual(1, source.Live);

            var face = asset.Font;
            source.Release(Key, asset);
            Assert.IsFalse(face.IsValid, "releasing left the face (and the mapping) alive");
            Assert.IsTrue(asset == null, "the runtime asset was not destroyed");
            Assert.AreEqual(0, source.Live);
            Assert.AreEqual(0, _fallback.Releases);
        }

        [Test]
        public void A_Key_Without_A_File_Goes_To_The_Fallback()
        {
            var source = new FileFontSource(_root, _fallback);
            var asset = source.LoadNow("Fonts/Nothing Here");
            Assert.AreSame(_fallback.Asset, asset);
            Assert.AreEqual(1, _fallback.Loads);
            source.Release("Fonts/Nothing Here", asset);
            Assert.AreEqual(1, _fallback.Releases);

            OneFontAsset async = null;
            source.Load("Fonts/Nothing Here", a => async = a);
            Assert.AreSame(_fallback.Asset, async);
        }

        [Test]
        public void An_Android_Style_Root_Sends_Every_Key_To_The_Fallback()
        {
            // What Application.streamingAssetsPath is on Android: a URL into the
            // APK, which nothing can map. No setting has to say so.
            var source = new FileFontSource("jar:file:///data/app/com.example/base.apk!/assets/OneTextFonts", _fallback);
            Assert.IsFalse(source.ReadsFiles);
            Assert.AreSame(_fallback.Asset, source.LoadNow(Key));
            Assert.AreEqual(1, _fallback.Loads);

            var web = new FileFontSource("https://example.com/StreamingAssets/OneTextFonts", _fallback);
            Assert.IsFalse(web.ReadsFiles);
            Assert.AreSame(_fallback.Asset, web.LoadNow(Key));
        }

        [Test]
        public void A_Key_Cannot_Leave_The_Root()
        {
            var source = new FileFontSource(Path.Combine(_root, "Fonts"), null);
            File.Copy(Path.Combine(_root, Key + ".ttf"), Path.Combine(_root, "Outside.ttf"));
            Assert.IsNull(source.PathFor("../Outside"));
            Assert.IsNull(source.PathFor(Path.Combine(_root, "Outside")));
            Assert.IsNotNull(source.PathFor("Arabic Font"));
            Assert.IsNotNull(source.PathFor("Arabic Font.ttf"), "a key may carry its extension");
        }

        [Test]
        public void The_StreamingAssets_Setting_Reads_Files_Over_Resources()
        {
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            settings.FontSource = OneFontSourceKind.StreamingAssets;
            OneTextSettings.Instance = settings;
            FontResidency.ResetForTests();

            var source = FontResidency.Source as FileFontSource;
            Assert.NotNull(source, $"got {FontResidency.Source?.GetType().Name}");
            Assert.IsInstanceOf<ResourcesFontSource>(source.Fallback);
            StringAssert.EndsWith(FileFontSource.DefaultFolder, source.Root);
        }

        [Test]
        public void Residency_Through_Files_Loads_On_Demand_Unloads_And_Reloads()
        {
            var latin = ScriptableObject.CreateInstance<OneFontAsset>();
            latin.hideFlags = HideFlags.HideAndDontSave;
            latin.Initialize(File.ReadAllBytes(Path.GetFullPath(MappedFontTests.LatinFontPath)), "Latin", "Latin");
            _created.Add(latin);
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(settings);
            settings.DefaultFont = latin;
            using (var probe = FontData.Load(File.ReadAllBytes(Path.GetFullPath(MappedFontTests.ArabicFontPath))))
                settings.SetOnDemandFonts(new[] { new OneFontReference(Key, "ar", FontCoverage.Of(probe)) });
            OneTextSettings.Instance = settings;
            var source = new FileFontSource(_root, _fallback);
            FontResidency.Source = source;
            FontResidency.LoadOnDemand = true;

            using var scene = new GoldenScene(400, 100);
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(scene.CanvasGo.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(360f, 80f);
            var label = go.AddComponent<OneTextLabel>();
            label.FontSize = 36f;
            label.Text = Arabic;
            Object.DestroyImmediate(scene.Render());

            Assert.IsTrue(FontResidency.IsResident(Key), FontResidency.Describe());
            var asset = FontResidency.Resident[0];
            Assert.IsTrue(asset.IsFileBacked && asset.Font.IsMapped);
            Assert.AreEqual("ar", asset.Language, "the language came from the settings entry");
            Assert.AreEqual(0, FontResidency.ResidentBytes, "a mapped on-demand font counted heap bytes");
            Assert.AreEqual(0, _fallback.Loads);

            label.Text = "";
            FontResidency.Trim();
            FontResidency.Tick();
            Object.DestroyImmediate(scene.Render());
            FontResidency.Tick();
            Assert.IsFalse(FontResidency.IsResident(Key));
            Assert.AreEqual(0, source.Live);

            label.Text = Arabic;
            Object.DestroyImmediate(scene.Render());
            Assert.IsTrue(FontResidency.IsResident(Key));
            Assert.AreEqual(1, source.Live);
        }

        [Test]
        public void The_Build_Writes_Each_On_Demand_Font_With_Its_Own_Extension()
        {
            Assert.AreEqual(".otf", OneFontFiles.ExtensionOf(new byte[] { (byte)'O', (byte)'T', (byte)'T', (byte)'O' }));
            Assert.AreEqual(".ttc", OneFontFiles.ExtensionOf(new byte[] { (byte)'t', (byte)'t', (byte)'c', (byte)'f' }));
            Assert.AreEqual(".ttf", OneFontFiles.ExtensionOf(new byte[] { 0, 1, 0, 0 }));
            Assert.IsFalse(OneFontFiles.TargetReadsFiles(UnityEditor.BuildTarget.Android));
            Assert.IsFalse(OneFontFiles.TargetReadsFiles(UnityEditor.BuildTarget.WebGL));
            Assert.IsTrue(OneFontFiles.TargetReadsFiles(UnityEditor.BuildTarget.StandaloneOSX));
            Assert.IsTrue(OneFontFiles.TargetReadsFiles(UnityEditor.BuildTarget.StandaloneWindows64));
        }
    }
}
