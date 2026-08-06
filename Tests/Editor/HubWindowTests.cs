using OneText.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Tests
{
    /// <summary>
    /// The window, built.
    ///
    /// The Hub's logic is tested headlessly next door; this is the other half
    /// of the risk, which is the view: a section that throws while composing
    /// itself is a section nobody can open, and it throws at build time and
    /// nowhere else.
    ///
    /// This used to be the suite's one skipped test. It was skipped because the
    /// Hub was IMGUI and batch mode loads no editor skin, so EditorStyles threw
    /// before any of this window's own code ran. A visual tree needs no skin,
    /// so the test runs everywhere now, which is as much the point of the
    /// rewrite as the look is.
    /// </summary>
    public class HubWindowTests
    {
        private OneTextHub _window;

        [SetUp]
        public void SetUp() => _window = ScriptableObject.CreateInstance<OneTextHub>();

        [TearDown]
        public void TearDown()
        {
            if (_window != null) Object.DestroyImmediate(_window);
            _window = null;
        }

        [Test]
        public void EverySection_BuildsWithoutThrowing()
        {
            Assert.That(_window.Sections.Count, Is.GreaterThan(0), "the Hub has no sections");

            foreach (var section in _window.Sections)
            {
                var root = section.Build(_window);
                Assert.NotNull(root, $"{section.Tab} built nothing");
                Assert.That(root.childCount, Is.GreaterThan(0), $"{section.Tab} is empty");

                // Twice: composing again over a live tree is what every button
                // in this window does, and the bugs live in the difference.
                Assert.DoesNotThrow(section.Rebuild, $"{section.Tab} could not rebuild");
                Assert.DoesNotThrow(section.OnShow, $"{section.Tab} could not be shown");
                Assert.DoesNotThrow(section.Tick, $"{section.Tab} could not tick");
            }
        }

        [Test]
        public void EveryTab_HasASection()
        {
            foreach (OneTextHub.Tab tab in System.Enum.GetValues(typeof(OneTextHub.Tab)))
            {
                var section = _window.Find(tab);
                Assert.NotNull(section, $"no section for {tab}");
                Assert.AreEqual(tab, section.Tab, $"{tab} resolved to {section.Tab}");
            }
        }

        [Test]
        public void EverySection_SaysWhatItIsFor()
        {
            // The usability argument in one assertion: a panel whose title is a
            // noun and whose explanation is missing is the editor window this
            // one replaced.
            foreach (var section in _window.Sections)
            {
                Assert.IsNotEmpty(section.Title, $"{section.Tab} has no title");
                Assert.IsNotEmpty(section.Eyebrow, $"{section.Tab} has no eyebrow");
                Assert.That(section.Lede, Is.Not.Null.And.Length.GreaterThan(30),
                    $"{section.Tab} does not say what it is for");
            }
        }

        [Test]
        public void Shell_BuildsAndSelectsEverySection()
        {
            var shell = new HubShell(_window);
            Assert.NotNull(shell.Root, "no shell");
            Assert.That(shell.Root.styleSheets.count, Is.GreaterThan(0),
                "the shell loaded no stylesheet: the USS asset is missing");

            foreach (var section in _window.Sections)
            {
                Assert.DoesNotThrow(() => shell.Select(section), $"could not show {section.Tab}");
                Assert.AreSame(section, shell.Current);
            }

            Assert.DoesNotThrow(() => shell.Notify("built by a test"));
            Assert.DoesNotThrow(shell.RefreshNav);
        }

        [Test]
        public void Layout_LoadsFromThePackage()
        {
            Assert.NotNull(HubUI.LoadTree("OneTextHub"), "OneTextHub.uxml did not load");
            Assert.NotNull(HubUI.LoadTree("HubCard"), "HubCard.uxml did not load");
            Assert.NotNull(HubUI.LoadStyle("OneTextHub"), "OneTextHub.uss did not load");
        }

        [Test]
        public void Card_HasATitleAndABody()
        {
            var card = HubUI.MakeCard("Title", "Note");
            Assert.NotNull(card.Root);
            Assert.NotNull(card.Body);
            Assert.NotNull(card.Actions);
            Assert.AreEqual("Title", card.TitleLabel.text);

            card.Add(new Label("body"));
            card.Act(HubUI.Ghost("act", () => { }));
            Assert.AreEqual(1, card.Body.childCount);
            Assert.AreEqual(1, card.Actions.childCount);
        }

        [Test]
        public void BundledWordLists_AreStillInThePackage()
        {
            // The Dictionaries section offers one button that installs four
            // files by name out of Samples~. Renaming one of them there would
            // turn that button into a button that quietly installs nothing:
            // the exact failure the whole section exists to make visible.
            string folder = HubDictionariesTab.BundledWordListFolder();
            Assert.NotNull(folder, "the package ships no Samples~/Dictionaries folder");

            foreach (var (file, script) in HubDictionariesTab.BundledLists)
            {
                Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(folder, file)),
                    $"{script}: {file} is missing from the bundled word lists");
            }
        }

        [Test]
        public void Charts_TakeTheirDataWithoutThrowing()
        {
            var donut = new HubDonut();
            donut.Slices((0.3f, Color.green), (0.2f, Color.yellow), (0.5f, Color.grey));
            donut.Caption("50%", "FULL");
            Assert.DoesNotThrow(donut.MarkDirtyRepaint);

            var spark = new HubSparkline();
            spark.Set(new[] { 0, 3, 9, 1, 0 }, "evictions");
            Assert.DoesNotThrow(spark.MarkDirtyRepaint);
        }
    }
}
