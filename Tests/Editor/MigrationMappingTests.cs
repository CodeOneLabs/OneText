using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The arithmetic of leaving TextMesh Pro, checked without TextMesh Pro.
    ///
    /// Every number in a migration is a chance to be quietly wrong: an
    /// alignment bit read from the wrong byte turns every centred label in a
    /// project left-aligned, and a line spacing read as a multiplier when it was
    /// an offset turns 10 into 10× rather than 1.1×. None of that throws, none
    /// of it fails a build, and all of it is only visible to somebody who opens
    /// the scene and looks.
    ///
    /// These are also the only tests in this module that can run in a project
    /// with no TMP installed, which is most CI, so the conversions live behind
    /// primitives specifically so this file can exist.
    /// </summary>
    public class MigrationMappingTests
    {
        // ------------------------------------------------------------ alignment

        [Test]
        public void TmpAlignment_SplitsTheBitfieldIntoTwoAxes()
        {
            // TopLeft is Left | Top, which is how every combined name in the
            // public enum is built.
            MigrationMapping.FromTmpAlignment(
                MigrationMapping.TmpLeft | MigrationMapping.TmpTop,
                out var horizontal, out var vertical, out bool approximated, out _);
            Assert.AreEqual(TextAlignment.Left, horizontal);
            Assert.AreEqual(VerticalAlignment.Top, vertical);
            Assert.IsFalse(approximated, "Left/Top is exact");

            MigrationMapping.FromTmpAlignment(
                MigrationMapping.TmpCenter | MigrationMapping.TmpMiddle,
                out horizontal, out vertical, out approximated, out _);
            Assert.AreEqual(TextAlignment.Center, horizontal);
            Assert.AreEqual(VerticalAlignment.Middle, vertical);
            Assert.IsFalse(approximated);

            MigrationMapping.FromTmpAlignment(
                MigrationMapping.TmpRight | MigrationMapping.TmpBottom,
                out horizontal, out vertical, out approximated, out _);
            Assert.AreEqual(TextAlignment.Right, horizontal);
            Assert.AreEqual(VerticalAlignment.Bottom, vertical);
            Assert.IsFalse(approximated);

            MigrationMapping.FromTmpAlignment(
                MigrationMapping.TmpJustified | MigrationMapping.TmpTop,
                out horizontal, out _, out approximated, out _);
            Assert.AreEqual(TextAlignment.Justified, horizontal);
            Assert.IsFalse(approximated);
        }

        [Test]
        public void TmpAlignment_NamesTheDistinctionsOneTextDoesNotHave()
        {
            // The four TMP alignments with no OneText equivalent all have to
            // arrive as "the nearest thing, and here is what you lost": a
            // silent nearest thing is how a migration earns its reputation.
            foreach (int flag in new[]
                     {
                         MigrationMapping.TmpFlush,
                         MigrationMapping.TmpGeometryCenter,
                     })
            {
                MigrationMapping.FromTmpAlignment(flag | MigrationMapping.TmpTop,
                    out _, out _, out bool approximated, out string what);
                Assert.IsTrue(approximated, $"0x{flag:X} should be flagged");
                Assert.IsNotEmpty(what, $"0x{flag:X} did not say what was lost");
            }

            foreach (int flag in new[]
                     {
                         MigrationMapping.TmpBaseline,
                         MigrationMapping.TmpMidline,
                         MigrationMapping.TmpCapline,
                     })
            {
                MigrationMapping.FromTmpAlignment(MigrationMapping.TmpLeft | flag,
                    out _, out _, out bool approximated, out string what);
                Assert.IsTrue(approximated, $"0x{flag:X} should be flagged");
                Assert.IsNotEmpty(what, $"0x{flag:X} did not say what was lost");
            }

            MigrationMapping.FromTmpAlignment(
                MigrationMapping.TmpLeft | MigrationMapping.TmpCapline,
                out _, out var vertical, out _, out _);
            Assert.AreEqual(VerticalAlignment.Top, vertical, "capline sits at the top, not middle");
        }

        // --------------------------------------------------------- line spacing

        [Test]
        public void TmpLineSpacing_IsAnOffsetAndBecomesAMultiplier()
        {
            Assert.AreEqual(1f, MigrationMapping.LineSpacingFromTmp(0f), 1e-5f,
                "zero offset is single spacing");
            Assert.AreEqual(1.1f, MigrationMapping.LineSpacingFromTmp(10f), 1e-5f);
            Assert.AreEqual(0.5f, MigrationMapping.LineSpacingFromTmp(-50f), 1e-5f);
            Assert.AreEqual(2f, MigrationMapping.LineSpacingFromTmp(100f), 1e-5f);
        }

        // -------------------------------------------------------- overflow/wrap

        [Test]
        public void TmpOverflow_MapsWhatItCanAndNamesWhatItCannot()
        {
            Assert.AreEqual(TextOverflow.Overflow,
                MigrationMapping.FromTmpOverflow(MigrationMapping.TmpOverflowOverflow, out var lost));
            Assert.IsNull(lost);

            Assert.AreEqual(TextOverflow.Ellipsis,
                MigrationMapping.FromTmpOverflow(MigrationMapping.TmpOverflowEllipsis, out lost));
            Assert.IsNull(lost);

            Assert.AreEqual(TextOverflow.Truncate,
                MigrationMapping.FromTmpOverflow(MigrationMapping.TmpOverflowTruncate, out lost));
            Assert.IsNull(lost);

            foreach (int mode in new[]
                     {
                         MigrationMapping.TmpOverflowMasking,
                         MigrationMapping.TmpOverflowScrollRect,
                         MigrationMapping.TmpOverflowPage,
                         MigrationMapping.TmpOverflowLinked,
                     })
            {
                MigrationMapping.FromTmpOverflow(mode, out string unsupported);
                Assert.IsNotEmpty(unsupported, $"overflow mode {mode} passed silently");
            }
        }

        [Test]
        public void TmpWrapping_TreatsBothNoWrapModesAsNoWrap()
        {
            Assert.AreEqual(TextWrap.NoWrap,
                MigrationMapping.FromTmpWrappingMode(MigrationMapping.TmpWrapNoWrap));
            Assert.AreEqual(TextWrap.Wrap,
                MigrationMapping.FromTmpWrappingMode(MigrationMapping.TmpWrapNormal));
            Assert.AreEqual(TextWrap.Wrap,
                MigrationMapping.FromTmpWrappingMode(MigrationMapping.TmpWrapPreserveWhitespace));
            Assert.AreEqual(TextWrap.NoWrap, MigrationMapping.FromTmpWrappingMode(
                MigrationMapping.TmpWrapPreserveWhitespaceNoWrap));

            Assert.AreEqual(TextWrap.Wrap, MigrationMapping.FromTmpWordWrapping(true));
            Assert.AreEqual(TextWrap.NoWrap, MigrationMapping.FromTmpWordWrapping(false));
        }

        // ---------------------------------------------------------- legacy uGUI

        [Test]
        public void TextAnchor_DecomposesAllNineValues()
        {
            // Asserted against the enum itself rather than against remembered
            // integers: the whole risk is reading the wrong row or column.
            var expected = new (TextAnchor Anchor, TextAlignment H, VerticalAlignment V)[]
            {
                (TextAnchor.UpperLeft, TextAlignment.Left, VerticalAlignment.Top),
                (TextAnchor.UpperCenter, TextAlignment.Center, VerticalAlignment.Top),
                (TextAnchor.UpperRight, TextAlignment.Right, VerticalAlignment.Top),
                (TextAnchor.MiddleLeft, TextAlignment.Left, VerticalAlignment.Middle),
                (TextAnchor.MiddleCenter, TextAlignment.Center, VerticalAlignment.Middle),
                (TextAnchor.MiddleRight, TextAlignment.Right, VerticalAlignment.Middle),
                (TextAnchor.LowerLeft, TextAlignment.Left, VerticalAlignment.Bottom),
                (TextAnchor.LowerCenter, TextAlignment.Center, VerticalAlignment.Bottom),
                (TextAnchor.LowerRight, TextAlignment.Right, VerticalAlignment.Bottom),
            };

            foreach (var (anchor, h, v) in expected)
            {
                MigrationMapping.FromTextAnchor((int)anchor, out var horizontal, out var vertical);
                Assert.AreEqual(h, horizontal, $"{anchor} horizontal");
                Assert.AreEqual(v, vertical, $"{anchor} vertical");
            }
        }

        [Test]
        public void LegacyWrapModes_MapToTheirOneTextNames()
        {
            Assert.AreEqual(TextWrap.Wrap,
                MigrationMapping.FromHorizontalWrapMode((int)HorizontalWrapMode.Wrap));
            Assert.AreEqual(TextWrap.NoWrap,
                MigrationMapping.FromHorizontalWrapMode((int)HorizontalWrapMode.Overflow));

            Assert.AreEqual(TextOverflow.Truncate,
                MigrationMapping.FromVerticalWrapMode((int)VerticalWrapMode.Truncate));
            Assert.AreEqual(TextOverflow.Overflow,
                MigrationMapping.FromVerticalWrapMode((int)VerticalWrapMode.Overflow));

            Assert.AreEqual(TextAlignment.Left,
                MigrationMapping.FromLegacyAlignment((int)UnityEngine.TextAlignment.Left));
            Assert.AreEqual(TextAlignment.Center,
                MigrationMapping.FromLegacyAlignment((int)UnityEngine.TextAlignment.Center));
            Assert.AreEqual(TextAlignment.Right,
                MigrationMapping.FromLegacyAlignment((int)UnityEngine.TextAlignment.Right));
        }

        [Test]
        public void TextMeshSize_MeetsOneTextsWorldScale()
        {
            // The default 3D TextMesh — fontSize 0 (meaning the font's own),
            // characterSize 0.1 — against a font imported at 100.
            Assert.AreEqual(10f, MigrationMapping.FromTextMeshSize(0, 0.1f, 100), 1e-4f);
            Assert.AreEqual(10f, MigrationMapping.FromTextMeshSize(100, 0.1f, 0), 1e-4f);
            Assert.AreEqual(64f, MigrationMapping.FromTextMeshSize(64, 1f, 0), 1e-4f);

            // A characterSize of zero would collapse every label to nothing,
            // which is worse than ignoring it.
            Assert.Greater(MigrationMapping.FromTextMeshSize(32, 0f, 0), 0f);
        }

        // ----------------------------------------------------------- tag lint

        [Test]
        public void TagLint_FindsWhatOneTextWillPrintLiterally()
        {
            var found = MigrationMapping.LintTags(
                "x<sup>2</sup> and <noparse>raw</noparse> with <margin=2em>space");
            CollectionAssert.Contains(found, "sup");
            CollectionAssert.Contains(found, "noparse");
            CollectionAssert.Contains(found, "margin");
        }

        [Test]
        public void TagLint_LeavesAloneEverythingOneTextActuallyParses()
        {
            var found = MigrationMapping.LintTags(
                "<b>bold</b> <i>italic</i> <color=#ff0000>red</color> <size=20>big</size> " +
                "<align=center>mid</align> <nobr>x y</nobr> <voffset=2>up</voffset> " +
                "<cspace=1>wide</cspace> <mark=#ff0>hi</mark> <link=\"id\">go</link> " +
                "<wave amp=2 freq=1.5>shaky</wave> <sprite=3> <sprite name=\"star\">");
            CollectionAssert.IsEmpty(found,
                $"the lint invented tags: {string.Join(", ", found)}");
        }

        [Test]
        public void TagLint_SeparatesTheSpriteFormsThatWorkFromTheOnesThatDoNot()
        {
            CollectionAssert.IsEmpty(MigrationMapping.LintTags("<sprite=0>"));
            CollectionAssert.IsEmpty(MigrationMapping.LintTags("<sprite name=\"star\">"));

            var indexed = MigrationMapping.LintTags("<sprite index=3>");
            Assert.AreEqual(1, indexed.Count, "sprite index= should be reported once");
            StringAssert.Contains("index", indexed[0]);

            var animated = MigrationMapping.LintTags("<sprite anim=\"0,4,10\">");
            Assert.AreEqual(1, animated.Count);
            StringAssert.Contains("anim", animated[0]);
        }

        [Test]
        public void TagLint_DoesNotChokeOnTextThatIsNotMarkupAtAll()
        {
            Assert.IsEmpty(MigrationMapping.LintTags(null));
            Assert.IsEmpty(MigrationMapping.LintTags(string.Empty));
            Assert.IsEmpty(MigrationMapping.LintTags("2 < 3 and 4 > 1"));
            Assert.IsEmpty(MigrationMapping.LintTags("<unterminated"));
            Assert.IsEmpty(MigrationMapping.LintTags("<>"));
            // A tag name is reported once however often it appears.
            Assert.AreEqual(1, MigrationMapping.LintTags("<sup>a</sup><sup>b</sup>").Count);
        }

        [Test]
        public void MeshLint_NamesWhatWorldTextDeliberatelyLacks()
        {
            var lost = MigrationMapping.LintMeshOnlyLosses("hello <wave>there</wave> <sprite=2>");
            CollectionAssert.Contains(lost, "wave");
            CollectionAssert.Contains(lost, "sprite");

            CollectionAssert.IsEmpty(MigrationMapping.LintMeshOnlyLosses("<b>plain</b> <color=red>x</color>"),
                "bold and colour work fine on world text");
        }

        // -------------------------------------------------------- report shape

        [Test]
        public void Report_CountsBySeverityAndByKind()
        {
            var report = new MigrationReport { ContainersScanned = 3 };

            var label = new MigrationTarget
            {
                Kind = MigrationKind.Label,
                ComponentType = "TextMeshProUGUI",
                Container = "Assets/A.unity",
                Path = "Canvas/Label",
            };
            label.Note(DoctorSeverity.Warning, "margin-lost", "a margin went away");

            var dropdown = new MigrationTarget
            {
                Kind = MigrationKind.ReportOnly,
                ComponentType = "TMP_Dropdown",
                Container = "Assets/A.unity",
                Path = "Canvas/Dropdown",
            };
            dropdown.Note(DoctorSeverity.Info, "no-counterpart", "no dropdown here");

            report.Add(label);
            report.Add(dropdown);
            report.Add(new MigrationFinding
            {
                Severity = DoctorSeverity.Error,
                Rule = "font-source-missing",
                Message = "no font file",
            });

            Assert.AreEqual(2, report.Targets.Count);
            Assert.AreEqual(1, report.CountOfKind(MigrationKind.Label));
            Assert.AreEqual(1, report.CountOfKind(MigrationKind.ReportOnly));
            Assert.AreEqual(0, report.CountOfKind(MigrationKind.Mesh));
            Assert.AreEqual(1, report.Errors);
            Assert.AreEqual(1, report.Warnings);
            Assert.AreEqual(1, report.Count(DoctorSeverity.Info));
            Assert.IsFalse(report.Passed, "an error has to fail the report");

            // One container, seen twice, is still one container.
            Assert.AreEqual(1, report.Containers.Count);
            Assert.IsNotEmpty(report.Summary());
        }

        [Test]
        public void Finding_SaysWhereItIsWithoutBeingRead()
        {
            var target = new MigrationTarget
            {
                Kind = MigrationKind.Label,
                ComponentType = "TextMeshProUGUI",
                Container = "Assets/Menu.unity",
                Path = "Canvas/Title",
            };
            var finding = target.Note(DoctorSeverity.Warning, "unsupported-tag", "a tag will print");

            string line = finding.ToString();
            StringAssert.Contains("unsupported-tag", line);
            StringAssert.Contains("TextMeshProUGUI", line);
            StringAssert.Contains("Canvas/Title", line);
            StringAssert.Contains("Assets/Menu.unity", line);
        }

        [Test]
        public void Values_StartFromSomethingSane()
        {
            var values = MigrationValues.Default;
            Assert.AreEqual(1f, values.LineSpacing, "a default multiplier of zero would erase every line");
            Assert.AreEqual(TextWrap.Wrap, values.Wrap);
            Assert.AreEqual(string.Empty, values.Text, "never the label's own placeholder string");
            Assert.IsTrue(values.RichText);
        }

        // ------------------------------------------------------------ providers

        [Test]
        public void Providers_AlwaysHoldTheLegacyOneAndNeverRegisterTwice()
        {
            var all = MigrationProviders.All;
            bool legacy = false;
            var names = new List<string>();
            foreach (var provider in all)
            {
                Assert.IsNotEmpty(provider.Name, "a provider with no name cannot be reported");
                CollectionAssert.DoesNotContain(names, provider.Name,
                    $"{provider.Name} registered twice: every finding would appear twice");
                names.Add(provider.Name);
                if (provider is LegacyTextProvider) legacy = true;
            }
            Assert.IsTrue(legacy, "UnityEngine.UI.Text and TextMesh must always be migratable");

            // Registering the same provider again is a no-op, which is what
            // keeps a second InitializeOnLoad entry point harmless.
            int before = all.Count;
            MigrationProviders.Register(new LegacyTextProvider());
            Assert.AreEqual(before, MigrationProviders.All.Count);
        }
    }
}
