// No `using System;` here on purpose: this file says `Object.DestroyImmediate`,
// and pulling System in makes that ambiguous against System.Object. The two
// names the helpers below need are written out instead.
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// Real TextMesh Pro components, built in memory, converted, and read back.
    ///
    /// Everything up to here is testable without TMP on purpose, which leaves
    /// exactly one thing that is not: whether the adapter reads the right
    /// property off the real component. A mapping table that is correct about
    /// <c>TextAlignmentOptions</c> and reads it out of <c>fontStyle</c> passes
    /// every other test in this repository.
    ///
    /// This assembly compiles only where TMP does, so it disappears rather than
    /// fails in a project that has already removed it — which is the state
    /// every project that finishes this migration ends up in.
    /// </summary>
    public class TmpMigrationTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // The provider registers from an InitializeOnLoad constructor,
            // which has already run by the time a test does; registering again
            // is a no-op and makes the test independent of that ordering.
            MigrationProviders.Register(new TmpMigrationProvider());
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made) if (go != null) Object.DestroyImmediate(go);
            _made.Clear();
        }

        private GameObject NewObject(string name, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            if (parent != null) go.transform.SetParent(parent, false);
            else _made.Add(go);
            return go;
        }

        private static int CountOf(MigrationReport report, string rule)
        {
            int n = 0;
            foreach (var finding in report.Findings) if (finding.Rule == rule) n++;
            return n;
        }

        /// <summary>
        /// The one-string method a persistent listener can be pointed at, asked
        /// for by signature rather than written as a method group.
        ///
        /// Writing <c>placeholder.SetText</c> compiles against the TextMesh Pro
        /// inside Unity 6 and does not compile at all against TMP 3.0.7, which
        /// is what 2022.3 resolves. 3.0.7 has only
        /// <c>SetText(string, bool syncTextInputBox = true)</c>; the
        /// one-argument overload arrived later. A method group whose only
        /// candidate has an optional parameter does not convert to
        /// <c>UnityAction&lt;string&gt;</c> — optional parameters are not filled
        /// in by a method group conversion — so the generic
        /// <c>AddPersistentListener</c> stops being applicable, overload
        /// resolution falls back to the non-generic one, and the file fails to
        /// build with a pair of CS1503s that name neither cause.
        ///
        /// Reflection names the signature it wants instead of letting overload
        /// resolution choose, so the same source builds on both. Where the
        /// one-argument <c>SetText</c> exists this is exactly what the method
        /// group used to resolve to, and the test goes on proving what it did.
        /// Where it does not, the <c>text</c> setter is the other method both
        /// TMP_Text and OneTextLabel carry, and the case being tested — a
        /// persistent listener whose target is itself about to be replaced — is
        /// the same one either way. The caller asserts against
        /// <see cref="MemberInfo.Name"/> rather than a literal for that reason.
        /// </summary>
        private static MethodInfo StringSink(System.Type type)
        {
            return type.GetMethod("SetText", new[] { typeof(string) })
                   ?? type.GetProperty("text").GetSetMethod();
        }

        /// Resolved on TMP_Text rather than on the component's own type, so the
        /// delegate wired here and the method name asserted afterwards are the
        /// same MethodInfo and cannot drift apart.
        private static UnityAction<string> ListenerOn(Component target)
        {
            return (UnityAction<string>)System.Delegate.CreateDelegate(
                typeof(UnityAction<string>), target, StringSink(typeof(TMP_Text)));
        }

        // --------------------------------------------------------------- label

        [Test]
        public void TextMeshProUGUI_BecomesALabelCarryingItsValues()
        {
            var go = NewObject("Headline");
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "carried across";
            tmp.fontSize = 41f;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 13f;
            tmp.fontSizeMax = 55f;
            tmp.alignment = TMPro.TextAlignmentOptions.BottomRight;
            tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            tmp.lineSpacing = 25f;
            tmp.richText = true;
            tmp.color = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            tmp.raycastTarget = false;

            var report = ComponentMigration.ConvertInPlace(new[] { go }, "(test)", false);

            Assert.AreEqual(1, report.Converted, "the TMP label was not swapped");
            Assert.IsNull(go.GetComponent<TextMeshProUGUI>(), "the TMP component survived");

            var label = go.GetComponent<OneTextLabel>();
            Assert.NotNull(label, "no OneTextLabel arrived");
            Assert.AreEqual("carried across", label.Text);
            Assert.AreEqual(41f, label.FontSize, 1e-3f);
            Assert.IsTrue(label.AutoSize);
            Assert.AreEqual(13f, label.AutoSizeMin, 1e-3f);
            Assert.AreEqual(55f, label.AutoSizeMax, 1e-3f);
            Assert.AreEqual(TextAlignment.Right, label.Alignment);
            Assert.AreEqual(VerticalAlignment.Bottom, label.VerticalAlignment);
            Assert.AreEqual(TextOverflow.Ellipsis, label.Overflow);
            Assert.AreEqual(1.25f, label.LineSpacing, 1e-3f,
                "TMP's +25 offset is a 1.25 multiplier here");
            Assert.IsTrue(label.RichText);
            Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), label.color);
            Assert.IsFalse(label.raycastTarget);
        }

        [Test]
        public void AMarginIsReportedRatherThanSilentlyDropped()
        {
            // OneText has no margin: the rect is the text box. A label whose
            // text used to start forty pixels in and now starts at the edge is
            // the most common way a migration looks broken, so it is a warning
            // with the numbers in it.
            var go = NewObject("Margined");
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "inset";
            tmp.margin = new Vector4(40f, 8f, 40f, 8f);

            var report = ComponentMigration.ScanInPlace(new[] { go }, "(test)");

            Assert.AreEqual(1, CountOf(report, "margin-lost"));
            foreach (var finding in report.Findings)
            {
                if (finding.Rule != "margin-lost") continue;
                Assert.AreEqual(DoctorSeverity.Warning, finding.Severity);
                StringAssert.Contains("40", finding.Message);
            }
        }

        [Test]
        public void TheAlignmentsOneTextDoesNotHave_ArriveAsNotes()
        {
            var go = NewObject("Flush");
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "flush";
            tmp.alignment = TMPro.TextAlignmentOptions.TopFlush;

            var report = ComponentMigration.ScanInPlace(new[] { go }, "(test)");
            Assert.AreEqual(1, CountOf(report, "alignment-approximated"));
        }

        [Test]
        public void TheOverflowModesOneTextDoesNotHave_ArriveAsWarnings()
        {
            var go = NewObject("Paged");
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "paged";
            tmp.overflowMode = TMPro.TextOverflowModes.Page;

            var report = ComponentMigration.ScanInPlace(new[] { go }, "(test)");
            Assert.AreEqual(1, CountOf(report, "overflow-approximated"));
        }

        // --------------------------------------------------------- world text

        [Test]
        public void TextMeshPro_BecomesWorldTextAtTheSameSize()
        {
            var go = new GameObject("World", typeof(RectTransform));
            _made.Add(go);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "world";
            tmp.fontSize = 36f;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;

            var report = ComponentMigration.ConvertInPlace(new[] { go }, "(test)", false);

            Assert.AreEqual(1, report.Converted);
            var mesh = go.GetComponent<OneTextMesh>();
            Assert.NotNull(mesh, "no OneTextMesh arrived");
            Assert.AreEqual("world", mesh.Text);
            Assert.AreEqual(36f, mesh.FontSize, 1e-3f,
                "TMP world sizes port verbatim: ten points to the unit on both sides");
            Assert.AreEqual(TextAlignment.Center, mesh.Alignment);
        }

        [Test]
        public void WorldText_SaysWhatItCannotDo()
        {
            var go = new GameObject("World", typeof(RectTransform));
            _made.Add(go);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "a <wave>wobble</wave> and a <sprite=1>";

            var report = ComponentMigration.ScanInPlace(new[] { go }, "(test)");
            Assert.GreaterOrEqual(CountOf(report, "no-counterpart"), 1,
                "OneTextMesh has neither sprites nor effects and did not say so");
        }

        // -------------------------------------------------------- input field

        [Test]
        public void InputField_KeepsItsLabelsAndItsListeners()
        {
            var root = NewObject("Field");
            var background = root.AddComponent<Image>();

            var textGo = NewObject("Text", root.transform);
            var fieldText = textGo.AddComponent<TextMeshProUGUI>();
            fieldText.text = "typed";

            var placeholderGo = NewObject("Placeholder", root.transform);
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Enter text…";

            var field = root.AddComponent<TMP_InputField>();
            field.textComponent = fieldText;
            field.placeholder = placeholder;
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
            field.characterLimit = 42;
            field.text = "typed";
            field.readOnly = true;

            // SetText(string) exists on both TMP_Text and OneTextLabel, so this
            // listener has to survive both the retarget and the type change.
            UnityEventTools.AddPersistentListener(field.onValueChanged, ListenerOn(placeholder));
            Assert.AreEqual(1, field.onValueChanged.GetPersistentEventCount(),
                "the test did not manage to wire a listener");

            var report = ComponentMigration.ConvertInPlace(new[] { root }, "(test)", false);

            Assert.AreEqual(3, report.Converted, "field and both labels should all convert");
            Assert.IsNull(root.GetComponent<TMP_InputField>());

            var made = root.GetComponent<OneTextInputField>();
            Assert.NotNull(made, "no OneTextInputField arrived");
            Assert.AreEqual("typed", made.text);
            Assert.IsTrue(made.multiline);
            Assert.IsTrue(made.readOnly);
            Assert.AreEqual(42, made.characterLimit);

            var serialized = new SerializedObject(made);
            Assert.AreSame(textGo.GetComponent<OneTextLabel>(),
                serialized.FindProperty("_textComponent").objectReferenceValue,
                "the text component was not re-pointed at its replacement");
            Assert.AreSame(placeholderGo.GetComponent<OneTextLabel>(),
                serialized.FindProperty("_placeholder").objectReferenceValue,
                "the placeholder was not re-pointed at its replacement");
            Assert.AreSame(background, made.targetGraphic,
                "the field lost the graphic it highlights");

            Assert.AreEqual(1, made.onValueChanged.GetPersistentEventCount(),
                "the persistent listener did not survive the swap");
            Assert.AreSame(placeholderGo.GetComponent<OneTextLabel>(),
                made.onValueChanged.GetPersistentTarget(0),
                "the listener still points at the component that was destroyed");
            Assert.AreEqual(StringSink(typeof(TMP_Text)).Name,
                made.onValueChanged.GetPersistentMethodName(0));
            Assert.AreEqual(1, CountOf(report, "event-listeners"));
        }

        [Test]
        public void ContentType_IsNamed_AndEndEditListeners_AreCarried()
        {
            var root = NewObject("Password");
            var textGo = NewObject("Text", root.transform);
            var fieldText = textGo.AddComponent<TextMeshProUGUI>();

            var field = root.AddComponent<TMP_InputField>();
            field.textComponent = fieldText;
            field.contentType = TMP_InputField.ContentType.Password;
            UnityEventTools.AddPersistentListener(field.onEndEdit, ListenerOn(fieldText));

            var scan = ComponentMigration.ScanInPlace(new[] { root }, "(test)");
            Assert.GreaterOrEqual(CountOf(scan, "no-counterpart"), 1,
                "a password field silently became a plain one");

            var report = ComponentMigration.ConvertInPlace(new[] { root }, "(test)", false);
            var made = root.GetComponent<OneTextInputField>();
            Assert.NotNull(made, "no OneTextInputField arrived");

            // OneTextInputField grew an onEndEdit, so these listeners stopped
            // being something to apologise for and became something to move.
            Assert.AreEqual(1, made.onEndEdit.GetPersistentEventCount(),
                "the On End Edit wiring was dropped, and a designer's inspector wiring is not " +
                "recoverable from anywhere else");
            Assert.AreSame(textGo.GetComponent<OneTextLabel>(),
                made.onEndEdit.GetPersistentTarget(0),
                "the listener still points at the component that was destroyed");
            Assert.GreaterOrEqual(CountOf(report, "event-listeners"), 1);
        }

        // ----------------------------------------------------------- dropdown

        [Test]
        public void Dropdown_IsReportedAndLeftExactlyWhereItIs()
        {
            var root = NewObject("Dropdown");
            var dropdown = root.AddComponent<TMP_Dropdown>();

            var report = ComponentMigration.ConvertInPlace(new[] { root }, "(test)", false);

            Assert.NotNull(root.GetComponent<TMP_Dropdown>(),
                "a component with no counterpart was destroyed anyway");
            Assert.AreEqual(1, report.CountOfKind(MigrationKind.ReportOnly));
            Assert.GreaterOrEqual(CountOf(report, "no-counterpart"), 1);
            Assert.NotNull(dropdown);
        }

        // -------------------------------------------------------- idempotence

        [Test]
        public void ConvertingTwice_FindsNothingTheSecondTime()
        {
            var go = NewObject("Twice");
            go.AddComponent<TextMeshProUGUI>().text = "once";

            ComponentMigration.ConvertInPlace(new[] { go }, "(test)", false);
            var second = ComponentMigration.ScanInPlace(new[] { go }, "(test)");

            Assert.AreEqual(0, second.Targets.Count,
                "a converted object still looks like something to convert");
            Assert.AreEqual(1, go.GetComponents<OneTextLabel>().Length);
        }

        // ------------------------------------------------------------ provider

        [Test]
        public void TheProviderAnnouncesItselfUnderTheNameTheHubLooksFor()
        {
            Assert.IsTrue(MigrationProviders.HasTextMeshPro,
                "the gated assembly compiled but nothing registered: the Hub would say TMP is absent");
        }
    }
}
