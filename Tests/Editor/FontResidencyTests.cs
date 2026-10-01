using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// Fonts named by key: loaded while something wants them, unloaded when
    /// nothing does, and never destroyed under a label that is still drawing
    /// with them. The project default here is Latin only and the keyed font is
    /// Arabic only, so which one drew a string is never ambiguous.
    /// </summary>
    public class FontResidencyTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";
        private const string ArabicKey = "Fonts/Arabic";
        private const string Arabic = "سلام";

        /// <summary>A source over in-memory assets that counts what it was asked.</summary>
        private sealed class CountingSource : IFontSource
        {
            public readonly Dictionary<string, OneFontAsset> Assets = new Dictionary<string, OneFontAsset>();
            public int Loads;
            public int Releases;

            public void Load(string key, Action<OneFontAsset> done) => done(LoadNow(key));

            public OneFontAsset LoadNow(string key)
            {
                if (!Assets.TryGetValue(key, out var asset)) return null;
                Loads++;
                return asset;
            }

            public void Release(string key, OneFontAsset font) => Releases++;
        }

        private readonly List<Object> _created = new List<Object>();
        private CountingSource _source;
        private OneTextSettings _settings;
        private OneFontAsset _arabic;
        private OneTextSettings _previousSettings;

        [SetUp]
        public void SetUp()
        {
            FontResidency.ResetForTests();
            SystemFonts.Enabled = false;

            var latin = MakeAsset(LatinFontPath, "Latin");
            _arabic = MakeAsset(ArabicFontPath, "Arabic");
            _arabic.Language = "ar";

            _previousSettings = OneTextSettings.Instance;
            _settings = ScriptableObject.CreateInstance<OneTextSettings>();
            _created.Add(_settings);
            _settings.DefaultFont = latin;
            OneTextSettings.Instance = _settings;

            _source = new CountingSource();
            _source.Assets[ArabicKey] = _arabic;
            FontResidency.Source = _source;
            FontResidency.LoadOnDemand = false;
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.ResetForTests();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            SystemFonts.UseProjectSetting();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
        }

        private OneFontAsset MakeAsset(string path, string family)
        {
            var asset = ScriptableObject.CreateInstance<OneFontAsset>();
            asset.Initialize(File.ReadAllBytes(Path.GetFullPath(path)), family, path);
            _created.Add(asset);
            return asset;
        }

        private void DeclareArabic(bool withCoverage)
        {
            var coverage = withCoverage ? FontCoverage.Of(_arabic.Font) : null;
            // Recording the coverage loaded the face; start from unloaded, the
            // state a player is in before anything asks.
            _arabic.Unload();
            _settings.SetOnDemandFonts(new[] { new OneFontReference(ArabicKey, "ar", coverage) });
        }

        private OneTextLabel NewLabel(string text)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvasGo);
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(600f, 100f);
            label.FontSize = 32f;
            label.Text = text;
            return label;
        }

        private static void Draw(OneTextLabel label)
        {
            label.SetAllDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        /// <summary>Whether any run of the label's last layout was shaped with this face.</summary>
        private static bool DrewWith(OneTextLabel label, FontData font)
        {
            foreach (var run in label.EnsureLayout().Runs)
                if (ReferenceEquals(run.Font, font)) return true;
            return false;
        }

        private static void AssertEveryRunLive(OneTextLabel label)
        {
            foreach (var run in label.EnsureLayout().Runs)
                Assert.IsTrue(run.Font != null && run.Font.IsValid,
                    "a run kept a face that has been destroyed");
        }

        [Test]
        public void Acquire_LoadsOnce_AndTheLastReleaseUnloadsOnTheSweep()
        {
            var first = FontResidency.Acquire(ArabicKey);
            var second = FontResidency.Acquire(ArabicKey);

            Assert.AreSame(_arabic, first);
            Assert.AreSame(first, second);
            Assert.AreEqual(1, _source.Loads, "a second acquire loaded again");
            Assert.IsTrue(FontResidency.IsResident(ArabicKey));
            CollectionAssert.Contains(FontResidency.Resident, _arabic);

            FontResidency.Release(ArabicKey);
            Assert.IsTrue(FontResidency.IsResident(ArabicKey), "released while still held once");

            FontResidency.Release(ArabicKey);
            Assert.IsFalse(FontResidency.IsResident(ArabicKey), "still in the chain after the last release");
            Assert.AreEqual(0, _source.Releases, "unloaded before labels were told");

            FontResidency.Tick(); // labels hear about it
            Assert.AreEqual(0, _source.Releases, "swept in the same tick that told the labels");
            FontResidency.Tick(); // nobody took it back
            Assert.AreEqual(1, _source.Releases);
            Assert.IsFalse(_arabic.IsLoaded, "the face outlived the unload");
            Assert.AreEqual(0, _arabic.ResidentBytes - _arabic.StoredSize,
                "the unpacked copy outlived the unload");
        }

        [Test]
        public void ALabel_DrawsWithAnAcquiredFont_AndLaysOutWithoutItAfterTheUnload()
        {
            var label = NewLabel(Arabic);
            Draw(label);
            var face = _arabic.Font;
            Assert.IsFalse(DrewWith(label, face), "drew Arabic before the font was loaded");

            FontResidency.Acquire(ArabicKey);
            Draw(label);
            Assert.IsTrue(DrewWith(label, _arabic.Font), "an acquired font did not reach the label");

            FontResidency.Release(ArabicKey);
            FontResidency.Collect();
            Assert.IsFalse(_arabic.IsLoaded);

            Assert.DoesNotThrow(() => Draw(label));
            AssertEveryRunLive(label);
        }

        [Test]
        public void SetLanguages_LoadsTheLanguagesFonts_AndLetsGoOfTheOthers()
        {
            DeclareArabic(withCoverage: false);

            FontResidency.SetLanguages("ar-EG");
            Assert.IsTrue(FontResidency.IsResident(ArabicKey), "ar did not serve ar-EG");

            FontResidency.SetLanguages("en");
            Assert.IsFalse(FontResidency.IsResident(ArabicKey));
            FontResidency.Collect();
            Assert.AreEqual(1, _source.Releases);
        }

        [Test]
        public void ACharacterOnlyAnUnloadedFontDraws_LoadsItOnDemand()
        {
            DeclareArabic(withCoverage: true);
            FontResidency.LoadOnDemand = true;

            var label = NewLabel(Arabic);
            Draw(label);

            Assert.AreEqual(1, _source.Loads);
            Assert.IsTrue(FontResidency.IsResident(ArabicKey));
            Assert.IsTrue(DrewWith(label, _arabic.Font));
        }

        [Test]
        public void Trim_KeepsAFontTextOnScreenStillShows_AndDropsItOnceNothingDoes()
        {
            DeclareArabic(withCoverage: true);
            FontResidency.LoadOnDemand = true;
            var label = NewLabel(Arabic);
            Draw(label);

            FontResidency.Trim();
            FontResidency.Tick(); // what the canvas pass is told
            Draw(label);          // the canvas pass: the label takes the font back
            FontResidency.Tick(); // the sweep finds nothing to do
            Assert.AreEqual(0, _source.Releases, "unloaded a font on screen");
            Assert.IsTrue(FontResidency.IsResident(ArabicKey));

            label.Text = "plain";
            Draw(label);
            FontResidency.Trim();
            FontResidency.Tick();
            Draw(label);
            FontResidency.Tick();
            Assert.AreEqual(1, _source.Releases);
            Assert.IsFalse(_arabic.IsLoaded);
            AssertEveryRunLive(label);
        }

        [Test]
        public void ALabelStillHoldingAnUnloadedFace_RebuildsBeforeUsingIt()
        {
            // Disabled while the font left: it hears nothing, and must still
            // not reach for the destroyed face when it comes back.
            FontResidency.Acquire(ArabicKey);
            var label = NewLabel(Arabic);
            Draw(label);
            label.enabled = false;

            FontResidency.Release(ArabicKey);
            FontResidency.Collect();

            label.enabled = true;
            Assert.DoesNotThrow(() => Draw(label));
            AssertEveryRunLive(label);
        }

        [Test]
        public void AnUnknownKey_LoadsNothing_AndSaysSoOnce()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("no font asset under \"Fonts/Missing\""));
            Assert.IsNull(FontResidency.Acquire("Fonts/Missing"));
            Assert.IsNull(FontResidency.Acquire("Fonts/Missing"));
        }

        [Test]
        public void Coverage_IsTheFontsCmap_AsSortedInclusiveRanges()
        {
            var ranges = FontCoverage.Of(_arabic.Font);
            Assert.That(ranges.Length, Is.GreaterThan(1));
            Assert.AreEqual(0, ranges.Length % 2);
            for (int i = 2; i < ranges.Length; i += 2)
                Assert.Greater(ranges[i], ranges[i - 1] + 1, "ranges overlap or touch");

            Assert.IsTrue(FontCoverage.Contains(ranges, 'س'));
            Assert.IsFalse(FontCoverage.Contains(ranges, 0x4E00));
            foreach (int codepoint in new[] { (int)'س', 0x4E00, 'A', 0x10FFFF })
                Assert.AreEqual(_arabic.Font.HasGlyph(codepoint), FontCoverage.Contains(ranges, codepoint),
                    $"U+{codepoint:X4}");
        }

        [Test]
        public void ForgettingAFace_FreesItsAtlasTiles()
        {
            using var atlas = new GlyphAtlas(new GlyphAtlasSettings { TextureSize = 512, LayerCount = 1 });
            Assume.That(atlas.IsUsable, "no array textures on this device");
            var font = _arabic.Font;
            atlas.GetOrAdd(font, font.NominalGlyph('س'), 32f);
            atlas.GetOrAdd(font, font.NominalGlyph('ل'), 32f);
            int version = atlas.Version;

            Assert.AreEqual(2, atlas.Forget(font));
            Assert.AreEqual(0, atlas.GetStats().TileCount);
            Assert.AreNotEqual(version, atlas.Version, "meshes were not told tiles went away");
        }
    }
}
