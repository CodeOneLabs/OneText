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
    /// Faces the operating system lends are held while text uses them and no
    /// longer: mapped rather than read, let go of by <see cref="FontResidency.Trim"/>
    /// through the same two steps as on-demand fonts, and found again from
    /// what the tier remembers rather than by probing.
    ///
    /// <para>The "system" here is a temporary folder the tier is pointed at,
    /// holding copies of the package's own test fonts, so every case runs the
    /// same on every machine and a test can take a font away.</para>
    /// </summary>
    public class SystemFontReleaseTests
    {
        private const int Beh = 0x0628;
        private const string Arabic = "مرحبا بالعالم";

        private readonly List<Object> _created = new List<Object>();
        private string _directory;
        private OneTextSettings _previousSettings;

        [SetUp]
        public void SetUp()
        {
            FontResidency.ResetForTests();
            FontResidency.LoadOnDemand = false;
            _directory = Path.Combine(Path.GetTempPath(), "onetext-system-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            File.Copy(Path.GetFullPath(MappedFontTests.ArabicFontPath), Path.Combine(_directory, "NotoSansArabic.ttf"));
            SystemFontIndex.DirectoriesOverride = new[] { _directory };
            SystemFonts.Forget();
            SystemFonts.Enabled = true;

            // Latin only in the project, so Arabic has to come from "the system".
            _previousSettings = OneTextSettings.Instance;
            var latin = ScriptableObject.CreateInstance<OneFontAsset>();
            latin.hideFlags = HideFlags.HideAndDontSave;
            latin.Initialize(File.ReadAllBytes(Path.GetFullPath(MappedFontTests.LatinFontPath)), "Latin", "Latin");
            _created.Add(latin);
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            settings.DefaultFont = latin;
            _created.Add(settings);
            OneTextSettings.Instance = settings;
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.ResetForTests();
            SystemFonts.Forget();
            SystemFontIndex.DirectoriesOverride = null;
            SystemFontIndex.Forget();
            SystemFonts.UseProjectSetting();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
            try { Directory.Delete(_directory, true); }
            catch (Exception) { /* left for the OS */ }
        }

        [Test]
        public void A_System_Face_Is_Mapped_Not_Read()
        {
            var face = SystemFonts.Resolve(Beh);
            Assert.NotNull(face);
            Assert.IsTrue(SystemFonts.IsSystemFont(face));
            Assert.IsTrue(face.IsMapped);
            Assert.AreEqual(0, SystemFonts.ManagedBytes, "a system face put its file on the managed heap");
            Assert.AreEqual(new FileInfo(Path.Combine(_directory, "NotoSansArabic.ttf")).Length, SystemFonts.MappedBytes);
            StringAssert.Contains("NotoSansArabic.ttf#0", SystemFonts.Describe());
        }

        [Test]
        public void Trim_Lets_Go_Of_A_Face_Nothing_Uses_And_The_Answer_Brings_It_Back()
        {
            var first = SystemFonts.Resolve(Beh);
            int probed = SystemFonts.FilesProbed;
            int generation = SystemFonts.Generation;

            FontResidency.Trim();
            FontResidency.Collect();

            Assert.IsFalse(first.IsValid, "the face outlived a trim nothing held it through");
            Assert.AreEqual(0, SystemFonts.LoadedFaceCount);
            Assert.IsNull(SystemFonts.NameOf(first));
            Assert.AreNotEqual(generation, SystemFonts.Generation);

            var again = SystemFonts.Resolve(Beh);
            Assert.NotNull(again);
            Assert.IsTrue(again.IsValid && again.HasGlyph(Beh));
            Assert.AreEqual(probed, SystemFonts.FilesProbed, "finding it again probed files instead of asking the answer");
            StringAssert.Contains("loads=2", SystemFonts.Describe());
        }

        [Test]
        public void Negative_Answers_Survive_A_Trim()
        {
            Assert.IsNull(SystemFonts.Resolve(0x10FFFD));
            SystemFonts.Resolve(Beh);
            FontResidency.Trim();
            FontResidency.Collect();

            int before = SystemFonts.FilesProbed;
            Assert.IsNull(SystemFonts.Resolve(0x10FFFD));
            Assert.AreEqual(before, SystemFonts.FilesProbed, "a remembered 'nothing has it' walked the disk again");
        }

        [Test]
        public void A_Stack_That_Outlives_A_Trim_Does_Not_Hand_Out_A_Destroyed_Face()
        {
            var stack = new FontStack();
            var first = stack.ResolveFromSystem(Beh);
            Assert.NotNull(first);

            FontResidency.Trim();
            FontResidency.Collect();
            Assert.IsFalse(first.IsValid);

            var again = stack.ResolveFromSystem(Beh);
            Assert.NotNull(again);
            Assert.IsTrue(again.IsValid, "the stack's own memory handed back the destroyed face");
            stack.Dispose();
        }

        [Test]
        public void A_File_That_Disappears_Is_Found_Elsewhere()
        {
            File.Copy(Path.Combine(_directory, "NotoSansArabic.ttf"), Path.Combine(_directory, "ZBackupArabic.ttf"));
            SystemFontIndex.Forget();

            var first = SystemFonts.Resolve(Beh);
            StringAssert.EndsWith("NotoSansArabic.ttf", first.SourcePath);
            FontResidency.Trim();
            FontResidency.Collect();

            // Unmapped by the trim, so even Windows lets it go.
            File.Delete(Path.Combine(_directory, "NotoSansArabic.ttf"));

            var again = SystemFonts.Resolve(Beh);
            Assert.NotNull(again, "a vanished file took the character with it");
            StringAssert.EndsWith("ZBackupArabic.ttf", again.SourcePath);
        }

        [Test]
        public void A_Collection_Loads_Only_The_Face_That_Answers()
        {
            File.Delete(Path.Combine(_directory, "NotoSansArabic.ttf"));
            File.WriteAllBytes(Path.Combine(_directory, "pair.ttc"), MappedFontTests.Collection(
                File.ReadAllBytes(Path.GetFullPath(MappedFontTests.LatinFontPath)),
                File.ReadAllBytes(Path.GetFullPath(MappedFontTests.ArabicFontPath))));
            SystemFontIndex.Forget();

            var face = SystemFonts.Resolve(Beh);
            Assert.NotNull(face);
            Assert.AreEqual(1, SystemFonts.LoadedFaceCount, SystemFonts.Describe());
            StringAssert.Contains("pair.ttc#1", SystemFonts.Describe());
            Assert.AreEqual(0, SystemFonts.ManagedBytes);
        }

        [Test]
        public void A_Label_Keeps_Its_System_Face_Through_A_Trim_And_Gives_It_Back_When_The_Text_Goes()
        {
            using var scene = new GoldenScene(600, 120);
            var label = Label(scene, Arabic);
            var shown = scene.Render();
            _created.Add(shown);
            var face = SystemFace(label);
            Assert.NotNull(face, "the Arabic was not drawn by a system face");

            // On screen: the trim offers it up, the layout takes it back.
            FontResidency.Trim();
            FontResidency.Tick();
            Object.DestroyImmediate(scene.Render());
            FontResidency.Tick();
            Assert.IsTrue(face.IsValid, "a face the label was drawing with was destroyed");
            Assert.AreSame(face, SystemFace(label));

            // Off screen: gone, tiles and all.
            label.Text = "";
            FontResidency.Trim();
            FontResidency.Tick();
            Object.DestroyImmediate(scene.Render());
            FontResidency.Tick();
            Assert.IsFalse(face.IsValid, "the face outlived the text: " + SystemFonts.Describe());
            Assert.AreEqual(0, SystemFonts.LoadedFaceCount);
            int tiles = (SharedGlyphAtlas.Exists ? SharedGlyphAtlas.Atlas.Forget(face) : 0) +
                        (SharedGlyphAtlas.PreciseAtlasExists ? SharedGlyphAtlas.PreciseAtlas.Forget(face) : 0);
            Assert.AreEqual(0, tiles, "the released face left tiles in the atlas");

            // Back: the same picture from a fresh mapping.
            label.Text = Arabic;
            var reshown = scene.Render();
            _created.Add(reshown);
            var back = SystemFace(label);
            Assert.NotNull(back);
            Assert.IsTrue(back.IsValid);
            Assert.AreNotSame(face, back);
            Assert.AreEqual(0, PixelsOverTolerance(shown, reshown),
                "the text drew differently from a reloaded face");
        }

        internal static int PixelsOverTolerance(Texture2D a, Texture2D b)
        {
            var pa = a.GetPixels32();
            var pb = b.GetPixels32();
            int over = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int delta = Math.Max(Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Abs(pa[i].g - pb[i].g)),
                    Math.Max(Math.Abs(pa[i].b - pb[i].b), Math.Abs(pa[i].a - pb[i].a)));
                if (delta > GoldenComparer.ChannelTolerance) over++;
            }
            return over;
        }

        private static FontData SystemFace(OneTextLabel label)
        {
            foreach (var run in label.EnsureLayout().Runs)
                if (run.Font != null && SystemFonts.IsSystemFont(run.Font)) return run.Font;
            return null;
        }

        private static OneTextLabel Label(GoldenScene scene, string text)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(560f, 80f);
            rect.anchoredPosition = new Vector2(20f, -20f);
            var label = go.AddComponent<OneTextLabel>();
            label.FontSize = 36f;
            label.color = Color.white;
            label.Text = text;
            return label;
        }
    }
}
