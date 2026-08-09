using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using OneText.Editor;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OneText.Tests
{
    /// <summary>
    /// The TMP-parity members that a real migration turned out to need, and
    /// that <see cref="TmpParityAliasTests"/> does not already cover.
    ///
    /// The distinction between the two files is where they came from.
    /// TmpParityAliasTests covers the aliases that were designed in; everything
    /// below was found by converting a seven-hundred-file game and reading the
    /// compiler errors — <c>alpha</c> in four files, <c>ForceMeshUpdate</c> in
    /// two, <c>onEndEdit</c> and <c>SetTextWithoutNotify</c> on the one input
    /// field the project has. So the assertions are less about round trips than
    /// about the member doing the real thing: that ForceMeshUpdate has actually
    /// laid the text out by the time it returns, that alpha moves the colour
    /// the mesh is built from, and that SetTextWithoutNotify is silent where
    /// assigning is not. A member that compiles and does nothing is the failure
    /// mode this file exists to catch, and it is not one a round trip would
    /// notice.
    /// </summary>
    public class TmpApiParityTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        private static byte[] LatinFont() => File.ReadAllBytes(Path.GetFullPath(LatinFontPath));

        private OneTextLabel NewLabel(float width = 400f, float height = 120f)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var go = new GameObject("Label",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(OneTextLabel));
            _created.Add(go);
            go.transform.SetParent(canvas.transform, false);

            var label = go.GetComponent<OneTextLabel>();
            label.rectTransform.sizeDelta = new Vector2(width, height);
            label.SetFont(LatinFont());
            label.FontSize = 24f;
            return label;
        }

        private OneTextInputField NewField(out OneTextLabel label, out OneTextLabel placeholder)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            _created.Add(canvas);
            var root = new GameObject("Field", typeof(RectTransform), typeof(CanvasRenderer));
            root.transform.SetParent(canvas.transform, false);
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 60f);

            label = NewChildLabel(root, "Text");
            placeholder = NewChildLabel(root, "Placeholder");

            var field = root.AddComponent<OneTextInputField>();
            var serialized = new UnityEditor.SerializedObject(field);
            serialized.FindProperty("_textComponent").objectReferenceValue = label;
            serialized.FindProperty("_placeholder").objectReferenceValue = placeholder;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return field;
        }

        private static OneTextLabel NewChildLabel(GameObject parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var label = go.AddComponent<OneTextLabel>();
            label.SetFont(LatinFont());
            label.FontSize = 24f;
            label.Wrap = TextWrap.NoWrap;
            return label;
        }

        private OneTextMesh NewMesh(float width = 10f, float height = 4f)
        {
            var go = new GameObject("WorldText", typeof(RectTransform));
            _created.Add(go);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
            var text = go.AddComponent<OneTextMesh>();
            text.SetFont(LatinFont());
            text.FontSize = 10f;
            return text;
        }

        private static BaseEventData NewEventData() => new BaseEventData(EventSystem.current);

        private static UnityEngine.Event ReturnKey() => new UnityEngine.Event
        {
            type = EventType.KeyDown,
            keyCode = KeyCode.Return,
        };

        // ------------------------------------------------------------- alpha

        [Test]
        public void Alpha_IsTheColoursAlphaChannel()
        {
            var label = NewLabel();

            label.alpha = 0.25f;
            Assert.AreEqual(0.25f, label.color.a, 1e-5f, "the alias wrote the colour");
            Assert.AreEqual(0.25f, label.alpha, 1e-5f);

            label.color = new Color(1f, 0f, 0f, 0.75f);
            Assert.AreEqual(0.75f, label.alpha, 1e-5f, "and reads it back");
        }

        [Test]
        public void Alpha_LeavesTheOtherThreeChannelsAlone()
        {
            // The reason this is worth asserting: the obvious wrong
            // implementation is a field of its own, and the second obvious one
            // assigns a whole colour built from the wrong parts. Fading a label
            // must not turn it white.
            var label = NewLabel();
            label.color = new Color(0.2f, 0.4f, 0.6f, 1f);

            label.alpha = 0f;

            Assert.AreEqual(0.2f, label.color.r, 1e-5f);
            Assert.AreEqual(0.4f, label.color.g, 1e-5f);
            Assert.AreEqual(0.6f, label.color.b, 1e-5f);
            Assert.AreEqual(0f, label.color.a, 1e-5f);
        }

        // --------------------------------------------------- ForceMeshUpdate

        [Test]
        public void ForceMeshUpdate_HasLaidTheTextOutBeforeItReturns()
        {
            // The idiom the member exists for: assign, force, measure — all in
            // three consecutive statements, with no canvas update in between.
            var label = NewLabel();
            label.Text = "one";
            label.ForceMeshUpdate();
            float shortWidth = label.LayoutResult.Width;

            label.Text = "a considerably longer line of text";
            label.ForceMeshUpdate();

            Assert.Greater(label.LayoutResult.Width, shortWidth,
                "the layout describes the new string, not the one it replaced");
            Assert.Greater(label.LayoutResult.GraphemeCount, 3);
        }

        [Test]
        public void ForceMeshUpdate_DoesNotRelayOutWhenNothingMoved()
        {
            // Forcing is not the same as wasting. The layout cache is keyed by
            // value, so a second force over identical input has nothing to do,
            // and a project calling this in Update (which is what projects do)
            // must not pay for shaping every frame.
            var label = NewLabel();
            label.Text = "unchanged";
            label.ForceMeshUpdate();

            int runs = label.LayoutRuns;
            label.ForceMeshUpdate();
            Assert.AreEqual(runs, label.LayoutRuns, "identical input, no second shaping pass");
        }

        [Test]
        public void ForceMeshUpdate_WithReparsingAsked_LaysOutAgainAnyway()
        {
            // Which is the whole difference between the two overloads, and the
            // reason the flag is not swallowed: it is the escape hatch for a
            // project that changed something the cache key cannot see.
            var label = NewLabel();
            label.Text = "unchanged";
            label.ForceMeshUpdate();

            int runs = label.LayoutRuns;
            label.ForceMeshUpdate(true, true);
            Assert.AreEqual(runs + 1, label.LayoutRuns);
        }

        // ---------------------------------------------------- text accessors

        [Test]
        public void GetParsedText_IsTheTextWithoutItsMarkup()
        {
            var label = NewLabel();
            label.RichText = true;
            label.Text = "a <b>bold</b> claim";

            Assert.AreEqual("a bold claim", label.GetParsedText());
            Assert.AreEqual(label.DisplayText, label.GetParsedText(),
                "it is DisplayText under TMP's name, not a second parse");
        }

        [Test]
        public void GetParsedText_IsTheWholeStringWhenMarkupIsOff()
        {
            var label = NewLabel();
            label.RichText = false;
            label.Text = "a <b>bold</b> claim";

            Assert.AreEqual("a <b>bold</b> claim", label.GetParsedText());
        }

        [Test]
        public void SetText_Overloads_AreAllAssignments()
        {
            var label = NewLabel();

            label.SetText("plain");
            Assert.AreEqual("plain", label.Text);

            label.SetText("with the sync flag", true);
            Assert.AreEqual("with the sync flag", label.Text);

            label.SetText(new System.Text.StringBuilder("from a builder"));
            Assert.AreEqual("from a builder", label.Text);

            label.SetText((System.Text.StringBuilder)null);
            Assert.AreEqual(string.Empty, label.Text, "a null builder is an empty string, not a throw");
        }

        [Test]
        public void SetText_DoesNotOfferTheNumericOverloads()
        {
            // Deliberately absent, and asserted so that nobody adds them as a
            // forward to string.Format: TMP's "{0:2}" means two decimal places
            // and the BCL's means something else, so the helpful-looking
            // overload would compile, run, and print the wrong number.
            foreach (var method in typeof(OneTextLabel).GetMethods())
            {
                if (method.Name != "SetText") continue;
                var parameters = method.GetParameters();
                Assert.IsFalse(
                    parameters.Length > 1 && parameters[1].ParameterType == typeof(float),
                    "SetText(string, float …) cannot be implemented faithfully and must stay out");
            }
        }

        // ------------------------------------------------------ overflowMode

        [Test]
        public void OverflowMode_RoundTripsTheThreeOneTextDraws()
        {
            var label = NewLabel();

            foreach (TextOverflowModes value in new[]
            {
                TextOverflowModes.Overflow, TextOverflowModes.Ellipsis, TextOverflowModes.Truncate,
            })
            {
                label.overflowMode = value;
                Assert.AreEqual(value, label.overflowMode, $"{value} did not survive the round trip");
            }

            label.overflowMode = TextOverflowModes.Ellipsis;
            Assert.AreEqual(TextOverflow.Ellipsis, label.Overflow, "the alias wrote the property");

            label.Overflow = TextOverflow.Truncate;
            Assert.AreEqual(TextOverflowModes.Truncate, label.overflowMode);
        }

        [Test]
        public void OverflowMode_ResolvesTheFourThatAreNotAboutTheLayout()
        {
            var label = NewLabel();

            label.overflowMode = TextOverflowModes.Masking;
            Assert.AreEqual(TextOverflow.Truncate, label.Overflow,
                "a mask hiding the overflow and a layout dropping it read the same");

            label.overflowMode = TextOverflowModes.Page;
            Assert.AreEqual(TextOverflow.Truncate, label.Overflow);

            label.overflowMode = TextOverflowModes.ScrollRect;
            Assert.AreEqual(TextOverflow.Overflow, label.Overflow,
                "the scroll view wants the whole block to exist");

            label.overflowMode = TextOverflowModes.Linked;
            Assert.AreEqual(TextOverflow.Overflow, label.Overflow);
        }

        [Test]
        public void OverflowMode_AndTheMigration_AreTheSameArithmetic()
        {
            // The same guarantee TmpParityAliasTests makes for alignment and
            // line spacing, for the mode that joined them: a value assigned
            // through the alias and a value carried by the Onboarding migration
            // cannot disagree about what Masking means. The two implementations
            // are still separate — MigrationMapping has its own copy, written
            // before TmpCompat had one — so this is the assertion that keeps
            // them honest until it forwards.
            foreach (TextOverflowModes mode in Enum.GetValues(typeof(TextOverflowModes)))
            {
                var throughAlias = TmpCompat.OverflowFromTmp(mode, out string aliasSaid);
                var throughMigration = MigrationMapping.FromTmpOverflow((int)mode,
                    out string migrationSaid);

                Assert.AreEqual(throughMigration, throughAlias, $"{mode} maps two ways");
                Assert.AreEqual(migrationSaid, aliasSaid,
                    $"{mode} is named as an approximation by one and not the other");
            }
        }

        // ------------------------------------------------------- measurement

        [Test]
        public void IsTextOverflowing_AnswersForBothWaysTextCanFailToFit()
        {
            var label = NewLabel(width: 200f, height: 40f);
            label.Wrap = TextWrap.Wrap;

            label.Text = "short";
            Assert.IsFalse(label.isTextOverflowing, "one word in a roomy box fits");

            label.Text = "a line long enough that it has to wrap several times before it " +
                         "runs out of things to say, which it eventually does";
            Assert.IsTrue(label.isTextOverflowing,
                "the block reaches past the box even though nothing was dropped");

            // And the other way in: overflow handling that actually cut lines.
            label.Overflow = TextOverflow.Truncate;
            Assert.IsTrue(label.isTextOverflowing);
        }

        [Test]
        public void RenderedSize_IsTheLaidOutBlock()
        {
            var label = NewLabel();
            label.Wrap = TextWrap.NoWrap;
            label.Text = "measured";

            var layout = label.EnsureLayout();
            Assert.AreEqual(layout.Width, label.renderedWidth, 1e-4f);
            Assert.AreEqual(layout.Height, label.renderedHeight, 1e-4f);
            Assert.Greater(label.renderedWidth, 0f);
        }

        // ------------------------------------------------- shape of the region

        [Test]
        public void TheNewAliases_AreHiddenFromCompletion()
        {
            // Same rule as the older aliases: they are a migration ramp, not
            // API, and new code written against this class should never be
            // offered them.
            foreach (string name in new[]
            {
                "alpha", "overflowMode", "isTextOverflowing", "renderedWidth", "renderedHeight",
            })
            {
                var member = typeof(OneTextLabel).GetProperty(name);
                Assert.NotNull(member, $"the parity alias {name} is gone");
                Assert.IsNotEmpty(
                    member.GetCustomAttributes(typeof(EditorBrowsableAttribute), false),
                    $"{name} is not hidden from completion");
            }

            foreach (string name in new[] { "ForceMeshUpdate", "GetParsedText" })
            {
                var member = typeof(OneTextLabel).GetMethod(name, Type.EmptyTypes);
                Assert.NotNull(member, $"the parity member {name} is gone");
                Assert.IsNotEmpty(
                    member.GetCustomAttributes(typeof(EditorBrowsableAttribute), false),
                    $"{name} is not hidden from completion");
            }
        }

        [Test]
        public void TheMembersWithNoCounterpart_AreCompileErrorsWithInstructions()
        {
            // These exist only so the compiler can say something useful. If one
            // ever loses its error flag it becomes a member that compiles and
            // silently does nothing, which is the exact failure the region was
            // written to avoid — a label whose characterSpacing assignment is
            // accepted and ignored is a bug report about kerning, six months
            // later.
            foreach (string name in new[]
            {
                "characterSpacing", "wordSpacing", "margin", "autoSizeTextContainer",
                "havePropertiesChanged", "firstVisibleCharacter",
            })
            {
                var member = typeof(OneTextLabel).GetProperty(name);
                Assert.NotNull(member, $"{name} should be declared so the compiler can explain it");

                var obsolete = (ObsoleteAttribute)Attribute.GetCustomAttribute(
                    member, typeof(ObsoleteAttribute));
                Assert.NotNull(obsolete, $"{name} must be obsolete, not quietly present");
                Assert.IsTrue(obsolete.IsError, $"{name} must not compile");
                Assert.IsNotEmpty(obsolete.Message, $"{name} must say what to write instead");
            }
        }

        // ------------------------------------------- input field: silent set

        [Test]
        public void SetTextWithoutNotify_SetsTheValue()
        {
            var field = NewField(out _, out _);

            field.SetTextWithoutNotify("assigned quietly");

            Assert.AreEqual("assigned quietly", field.text);
            Assert.AreEqual("assigned quietly", field.editingModel.Text);
        }

        [Test]
        public void SetTextWithoutNotify_IsSilentWhereAssigningTextIsNot()
        {
            // The whole point of the member, and the one assertion that would
            // catch it being aliased to the ordinary setter: a field that
            // listens to its own event has to be refillable without the refill
            // reading as an edit.
            var field = NewField(out _, out _);

            var reported = new List<string>();
            field.onValueChanged.AddListener(reported.Add);

            field.SetTextWithoutNotify("quiet");
            Assert.IsEmpty(reported, "SetTextWithoutNotify must not raise onValueChanged");

            field.text = "loud";
            Assert.AreEqual(new[] { "loud" }, reported, "assigning text still raises it");
        }

        [Test]
        public void SetTextWithoutNotify_ShowsTheNewValue()
        {
            // Silent about the event, not about the screen.
            var field = NewField(out var label, out _);

            field.SetTextWithoutNotify("drawn");
            field.UpdateVisuals();

            Assert.AreEqual("drawn", label.Text);
        }

        // ------------------------------------------ input field: end of edit

        [Test]
        public void OnEndEdit_FiresWhenFocusLeaves()
        {
            var field = NewField(out _, out _);
            field.ActivateInputField();
            field.text = "a name";

            var reported = new List<string>();
            field.onEndEdit.AddListener(reported.Add);

            field.DeactivateInputField();

            Assert.AreEqual(new[] { "a name" }, reported,
                "losing focus is the moment the value is final");
        }

        [Test]
        public void OnEndEdit_FiresOnSubmit()
        {
            var field = NewField(out _, out _);
            field.ActivateInputField();
            field.text = "typed";

            var endEdit = new List<string>();
            var submit = new List<string>();
            field.onEndEdit.AddListener(endEdit.Add);
            field.onSubmit.AddListener(submit.Add);

            field.ProcessKeyEvent(ReturnKey());

            Assert.AreEqual(new[] { "typed" }, submit);
            Assert.AreEqual(new[] { "typed" }, endEdit,
                "committing with Return ends the edit as well as submitting it");
        }

        [Test]
        public void OnEndEdit_FiresOnceForASubmitThatIsThenFollowedByLosingFocus()
        {
            // OneText's Return leaves the caret in the field where TMP's
            // deactivates it, so the two moments that TMP collapses into one
            // are still two here. The value only ended once, so it is only
            // reported once.
            var field = NewField(out _, out _);
            field.ActivateInputField();
            field.text = "typed";

            var reported = new List<string>();
            field.onEndEdit.AddListener(reported.Add);

            field.ProcessKeyEvent(ReturnKey());
            field.DeactivateInputField();

            Assert.AreEqual(1, reported.Count, "one end of edit, not two");
        }

        [Test]
        public void OnEndEdit_FiresAgainOnceTheValueHasMovedOn()
        {
            var field = NewField(out _, out _);
            field.ActivateInputField();
            field.text = "first";

            var reported = new List<string>();
            field.onEndEdit.AddListener(reported.Add);

            field.ProcessKeyEvent(ReturnKey());
            field.text = "second";
            field.DeactivateInputField();

            Assert.AreEqual(new[] { "first", "second" }, reported,
                "a new value is a new thing to report the end of");
        }

        [Test]
        public void OnEndEdit_DoesNotFireForAFieldThatNeverHadFocus()
        {
            var field = NewField(out _, out _);
            field.text = "set from code";

            var reported = new List<string>();
            field.onEndEdit.AddListener(reported.Add);

            field.DeactivateInputField();

            Assert.IsEmpty(reported, "nothing was being edited, so no edit ended");
        }

        [Test]
        public void OnSelectAndOnDeselect_FireWithTheValue()
        {
            var field = NewField(out _, out _);
            field.text = "value";

            var selected = new List<string>();
            var deselected = new List<string>();
            field.onSelect.AddListener(selected.Add);
            field.onDeselect.AddListener(deselected.Add);

            field.OnSelect(NewEventData());
            Assert.AreEqual(new[] { "value" }, selected);
            Assert.IsTrue(field.isFocused);

            field.OnDeselect(NewEventData());
            Assert.AreEqual(new[] { "value" }, deselected);
            Assert.IsFalse(field.isFocused);
        }

        // ------------------------------------------ input field: the rest of it

        [Test]
        public void LineType_IsTheMultilineAxis()
        {
            var field = NewField(out _, out _);

            field.lineType = OneTextInputField.LineType.MultiLineNewline;
            Assert.IsTrue(field.multiline);
            Assert.AreEqual(OneTextInputField.LineType.MultiLineNewline, field.lineType);

            field.lineType = OneTextInputField.LineType.SingleLine;
            Assert.IsFalse(field.multiline);
            Assert.AreEqual(OneTextInputField.LineType.SingleLine, field.lineType);

            field.multiline = true;
            Assert.AreEqual(OneTextInputField.LineType.MultiLineNewline, field.lineType);
        }

        [Test]
        public void LineType_ReadsMultiLineSubmitBackAsTheHalfItKept()
        {
            // The lossy direction, asserted rather than left to be discovered:
            // MultiLineSubmit asks for several lines and a committing Return,
            // and this field spends one bit on both.
            var field = NewField(out _, out _);

            field.lineType = OneTextInputField.LineType.MultiLineSubmit;
            Assert.IsTrue(field.multiline, "the lines are the half that is kept");
            Assert.AreEqual(OneTextInputField.LineType.MultiLineNewline, field.lineType);
        }

        [Test]
        public void Placeholder_IsTheLabelTheFieldOwns()
        {
            var field = NewField(out _, out var placeholder);

            Assert.AreSame(placeholder, field.placeholder);

            field.text = string.Empty;
            field.UpdateVisuals();
            Assert.IsTrue(field.placeholder.enabled, "an empty field shows it");

            field.text = "something";
            field.UpdateVisuals();
            Assert.IsFalse(field.placeholder.enabled, "and a filled one does not");
        }

        [Test]
        public void StringPosition_IsTheSameIndexAsCaretPosition()
        {
            var field = NewField(out _, out _);
            field.text = "abcdef";

            field.stringPosition = 4;
            Assert.AreEqual(4, field.caretPosition);
            Assert.AreEqual(4, field.stringPosition);

            field.caretPosition = 2;
            Assert.AreEqual(2, field.stringPosition);
        }

        [Test]
        public void TheSelectionEnds_SelectARange()
        {
            var field = NewField(out _, out _);
            field.text = "abcdef";

            field.selectionAnchorPosition = 1;
            field.selectionFocusPosition = 4;

            Assert.AreEqual(1, field.selectionAnchorPosition);
            Assert.AreEqual(4, field.selectionFocusPosition);
            Assert.AreEqual(4, field.caretPosition, "the focus end is the caret");
            Assert.AreEqual("bcd", field.editingModel.SelectedText);
        }

        [Test]
        public void MoveTextEndAndStart_PutTheCaretAtTheEnds()
        {
            var field = NewField(out _, out _);
            field.text = "abcdef";
            field.caretPosition = 3;

            field.MoveTextEnd(false);
            Assert.AreEqual(6, field.caretPosition);
            Assert.IsFalse(field.editingModel.HasSelection, "no shift, no selection");

            field.MoveTextStart(true);
            Assert.AreEqual(0, field.caretPosition);
            Assert.AreEqual("abcdef", field.editingModel.SelectedText,
                "shift extended the selection back over the whole string");
        }

        [Test]
        public void TheInputFieldParityMembers_AreNotHiddenFromCompletion()
        {
            // The opposite rule to the label's, and deliberately so: this class
            // already speaks the input-field vocabulary, so onEndEdit and
            // SetTextWithoutNotify are its own API rather than a ramp off
            // somebody else's. Hiding them would hide the right way to use it.
            foreach (var member in new MemberInfo[]
            {
                typeof(OneTextInputField).GetProperty("onEndEdit"),
                typeof(OneTextInputField).GetProperty("placeholder"),
                typeof(OneTextInputField).GetProperty("lineType"),
                typeof(OneTextInputField).GetMethod("SetTextWithoutNotify"),
            })
            {
                Assert.NotNull(member, "a parity member is gone");
                Assert.IsEmpty(
                    member.GetCustomAttributes(typeof(EditorBrowsableAttribute), false),
                    $"{member.Name} should be offered, not hidden");
            }
        }

        // -------------------------------------------------------- world text

        [Test]
        public void MeshAliases_ForwardToTheProperties()
        {
            var text = NewMesh();

            text.text = "world";
            Assert.AreEqual("world", text.Text);
            text.Text = "back";
            Assert.AreEqual("back", text.text);

            text.fontSize = 18f;
            Assert.AreEqual(18f, text.FontSize);

            text.richText = false;
            Assert.IsFalse(text.RichText);

            text.enableAutoSizing = true;
            text.fontSizeMin = 6f;
            text.fontSizeMax = 30f;
            Assert.IsTrue(text.AutoSize);
            Assert.AreEqual(6f, text.AutoSizeMin);
            Assert.AreEqual(30f, text.AutoSizeMax);
        }

        [Test]
        public void MeshColourAndAlpha_AreTheSameColour()
        {
            var text = NewMesh();

            text.color = new Color(0.2f, 0.4f, 0.6f, 1f);
            Assert.AreEqual(text.Color, text.color);

            text.alpha = 0.5f;
            Assert.AreEqual(0.5f, text.Color.a, 1e-5f);
            Assert.AreEqual(0.2f, text.Color.r, 1e-5f, "the other channels are untouched");
            Assert.AreEqual(0.5f, text.alpha, 1e-5f);
        }

        [Test]
        public void MeshForceMeshUpdate_HasBuiltTheMeshBeforeItReturns()
        {
            var text = NewMesh();
            text.text = "world text";
            text.ForceMeshUpdate();

            var mesh = text.GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(mesh.vertexCount, 0, "the geometry is there, not queued");
            Assert.Greater(text.Layout.Width, 0f);
        }

        [Test]
        public void MeshGetParsedText_IsTheTextWithoutItsMarkup()
        {
            var text = NewMesh();
            text.richText = true;
            text.text = "a <b>bold</b> claim";

            Assert.AreEqual("a bold claim", text.GetParsedText());
        }

        [Test]
        public void MeshSetText_Overloads_AreAllAssignments()
        {
            var text = NewMesh();

            text.SetText("plain");
            Assert.AreEqual("plain", text.Text);

            text.SetText("with the sync flag", true);
            Assert.AreEqual("with the sync flag", text.Text);

            text.SetText(new System.Text.StringBuilder("from a builder"));
            Assert.AreEqual("from a builder", text.Text);
        }

        [Test]
        public void TheMeshAliases_AreHiddenFromCompletion()
        {
            foreach (string name in new[] { "text", "fontSize", "color", "alpha", "richText" })
            {
                var member = typeof(OneTextMesh).GetProperty(name);
                Assert.NotNull(member, $"the parity alias {name} is gone");
                Assert.IsNotEmpty(
                    member.GetCustomAttributes(typeof(EditorBrowsableAttribute), false),
                    $"{name} is not hidden from completion");
            }
        }
    }
}
