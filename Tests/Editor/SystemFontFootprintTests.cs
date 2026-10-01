using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// What nickname lines drawn from the operating system's fonts cost, shown,
    /// removed and trimmed, and shown again — measured, logged, and pictured.
    ///
    /// <para>The lines are a Korean, a Chinese, a Japanese and a Korean-plus-emoji
    /// nickname over a Latin-only project font, so every one of their
    /// characters comes from this machine's fonts. The numbers are deltas from
    /// before the text: the managed heap, and on macOS the process's footprint
    /// (what jetsam and Activity Monitor count) and resident size. They go to
    /// the log and to <c>sysfont-footprint.txt</c> beside the pictures; the
    /// assertions are only the ones that do not depend on the machine.</para>
    /// </summary>
    [Category("Captures")]
    public class SystemFontFootprintTests
    {
        private static readonly string[] Nicknames =
        {
            "김민준 별빛기사", "王小明 龙之传说", "さくら ひかり", "하늘 \U0001F600\U0001F3AE",
        };

        private readonly List<Object> _created = new List<Object>();
        private readonly StringBuilder _log = new StringBuilder();
        private OneTextSettings _previousSettings;
        private long _managed0, _mono0, _footprint0, _resident0;

        private static string OutputDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("ONETEXT_CAPTURE_OUT");
                return string.IsNullOrEmpty(configured)
                    ? Path.Combine(Path.GetTempPath(), "onetext-residency")
                    : configured;
            }
        }

        [SetUp]
        public void SetUp()
        {
            FontResidency.ResetForTests();
            FontResidency.LoadOnDemand = false;
            SystemFonts.Forget();
            SystemFonts.Enabled = true;
            _previousSettings = OneTextSettings.Instance;

            var latinOnly = ScriptFontCatalog.LatinOnly(File.ReadAllBytes(Path.GetFullPath(ScriptFontCatalog.LatinFontPath)));
            if (latinOnly == null) Assert.Ignore("no subsetter: the Latin face would draw more than Latin");
            var latin = ScriptableObject.CreateInstance<OneFontAsset>();
            latin.hideFlags = HideFlags.HideAndDontSave;
            latin.Initialize(latinOnly, "Latin", "Latin");
            _created.Add(latin);
            var settings = ScriptableObject.CreateInstance<OneTextSettings>();
            settings.DefaultFont = latin;
            _created.Add(settings);
            OneTextSettings.Instance = settings;
            Directory.CreateDirectory(OutputDirectory);
            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.ResetForTests();
            SystemFonts.Forget();
            SystemFonts.UseProjectSetting();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
            OneTextSettings.Instance = _previousSettings;
            OneTextSettings.Invalidate();
        }

        [Test]
        public void Nicknames_From_System_Fonts_Cost_Pages_Not_Files_And_Leave_With_The_Text()
        {
            using var scene = new GoldenScene(900, 260);
            var labels = new List<OneTextLabel>();
            for (int i = 0; i < Nicknames.Length; i++) labels.Add(Label(scene, i));
            // Warm up first: the atlas, the shaders, the render target, and
            // the editor's own settling after a test run starts all move the
            // process numbers by more than a font does.
            for (int i = 0; i < Nicknames.Length; i++) labels[i].Text = "Warm-up " + i;
            for (int i = 0; i < 3; i++) Object.DestroyImmediate(scene.Render());
            foreach (var label in labels) label.Text = "";
            Object.DestroyImmediate(scene.Render());
            Measure("warm-up");
            System.Threading.Thread.Sleep(1500);
            Measure("baseline");

            for (int i = 0; i < Nicknames.Length; i++) labels[i].Text = Nicknames[i];
            var shown = Keep(scene.Render());
            Save(shown, "sysfont-shown.png");
            Measure("shown");
            if (SystemFonts.LoadedFaceCount == 0) Assert.Ignore("this machine has no fonts for these scripts");
            var faces = SystemFaces(labels);
            Assert.AreEqual(0, SystemFonts.ManagedBytes, "a system face was read onto the managed heap");
            foreach (var face in faces) Assert.IsTrue(face.IsMapped, SystemFonts.NameOf(face) + " was not mapped");

            foreach (var label in labels) label.Text = "";
            FontResidency.Trim();
            FontResidency.Tick();
            Object.DestroyImmediate(scene.Render());
            FontResidency.Tick();
            var released = Keep(scene.Render());
            Save(released, "sysfont-released.png");
            Measure("removed+trim");
            Assert.AreEqual(0, SystemFonts.LoadedFaceCount, "faces outlived their text: " + SystemFonts.Describe());
            foreach (var face in faces) Assert.IsFalse(face.IsValid);

            for (int i = 0; i < Nicknames.Length; i++) labels[i].Text = Nicknames[i];
            var reshown = Keep(scene.Render());
            Save(reshown, "sysfont-reshown.png");
            Measure("reshown");
            Assert.AreEqual(faces.Count, SystemFaces(labels).Count);
            int over = SystemFontReleaseTests.PixelsOverTolerance(shown, reshown);
            _log.AppendLine($"shown vs reshown: {over} pixel(s) over tolerance");

            string path = Path.Combine(OutputDirectory, "sysfont-footprint.txt");
            File.WriteAllText(path, _log.ToString());
            Debug.Log(_log.ToString());
            Assert.AreEqual(0, over, "the nicknames drew differently from reloaded faces");
        }

        private static List<FontData> SystemFaces(List<OneTextLabel> labels)
        {
            var faces = new List<FontData>();
            foreach (var label in labels)
                foreach (var run in label.EnsureLayout().Runs)
                    if (run.Font != null && SystemFonts.IsSystemFont(run.Font) && !faces.Contains(run.Font))
                        faces.Add(run.Font);
            return faces;
        }

        private void Measure(string step)
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            long managed = GC.GetTotalMemory(true);
            long mono = Profiler.GetMonoUsedSizeLong();
            var (footprint, resident) = ProcessMemory();
            if (step == "baseline")
            {
                _managed0 = managed;
                _mono0 = mono;
                _footprint0 = footprint;
                _resident0 = resident;
            }
            _log.AppendLine($"FOOTPRINT step={step} managedΔ={Mb(managed - _managed0)} " +
                            $"monoUsedΔ={Mb(mono - _mono0)} footprintΔ={Mb(footprint - _footprint0)} " +
                            $"residentΔ={Mb(resident - _resident0)} | {SystemFonts.Describe()}");
            string categories = FootprintCategories(step);
            if (categories != null) _log.AppendLine("  footprint(1): " + categories);
        }

        /// <summary>
        /// macOS's own breakdown of this process (<c>/usr/bin/footprint</c>),
        /// saved whole beside the pictures, with the rows that tell font memory
        /// apart summarised: managed heap, malloc, mapped files (clean, so not
        /// footprint), and the GPU's share. Null where the tool is not.
        /// </summary>
        private static string FootprintCategories(string step)
        {
#if UNITY_EDITOR_OSX
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/footprint",
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString())
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(info);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);
                File.WriteAllText(Path.Combine(OutputDirectory, $"sysfont-footprint-{step}.txt"), output);
                var picked = new List<string>();
                foreach (string line in output.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.EndsWith("mapped file") || trimmed.EndsWith("Mono") || trimmed.Contains("IOAccelerator") ||
                        trimmed.EndsWith("IOKit") || trimmed.EndsWith("Malloc Large") || trimmed.EndsWith("Malloc Small") ||
                        trimmed.StartsWith("phys_footprint:") || trimmed.EndsWith("TOTAL"))
                        picked.Add(System.Text.RegularExpressions.Regex.Replace(trimmed, "\\s+", " "));
                }
                return string.Join(" ; ", picked);
            }
            catch (Exception e)
            {
                return "footprint(1) failed: " + e.Message;
            }
#else
            return null;
#endif
        }

        private OneTextLabel Label(GoldenScene scene, int row)
        {
            var go = new GameObject("Nickname", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(scene.CanvasGo.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(860f, 56f);
            rect.anchoredPosition = new Vector2(20f, -10f - row * 60f);
            var label = go.AddComponent<OneTextLabel>();
            label.FontSize = 36f;
            label.color = Color.white;
            return label;
        }

        private Texture2D Keep(Texture2D texture)
        {
            _created.Add(texture);
            return texture;
        }

        private static void Save(Texture2D texture, string name) =>
            File.WriteAllBytes(Path.Combine(OutputDirectory, name), texture.EncodeToPNG());

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0") + "MB";

#if UNITY_EDITOR_OSX
        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);

        /// <summary>rusage_info_v0: ri_resident_size at 64, ri_phys_footprint at 72.</summary>
        private static (long footprint, long resident) ProcessMemory()
        {
            try
            {
                var buffer = new byte[256];
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                if (proc_pid_rusage(pid, 0, buffer) != 0) return (0, 0);
                return (BitConverter.ToInt64(buffer, 72), BitConverter.ToInt64(buffer, 64));
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }
#else
        private static (long footprint, long resident) ProcessMemory() => (0, 0);
#endif
    }
}
