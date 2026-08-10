using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OneText.Editor;

namespace OneText.Tests
{
    /// <summary>
    /// The TMPro script rewriter, which edits a project's own source files.
    ///
    /// The risk here is not that it misses a rename; a missed rename is a
    /// compile error, and the compiler says exactly where. The risk is the
    /// other direction: an edit inside something that only looked like code. A
    /// dialogue line, a log message, a Windows path in a verbatim string, a
    /// commented-out block from last year — a project's text is full of the
    /// words TMP_Text and TextMeshPro, and a rewriter that touches one of them
    /// has silently changed shipped content, which nobody will notice until a
    /// localization diff months later. So most of what is asserted below is
    /// that nothing happened.
    ///
    /// The other half is the report. A file the rewriter cannot finish has to
    /// say so before the button is pressed rather than through a wall of
    /// compile errors after, so residual TMP names are checked by name and by
    /// line.
    /// </summary>
    public class TmpScriptRewriteTests
    {
        private static string Rewrite(string source) => TmpScriptRewriter.Rewrite(source).Text;

        // ------------------------------------------------------------ usings

        [Test]
        public void Using_TMPro_Becomes_The_Namespace_The_Label_Lives_In()
        {
            var result = TmpScriptRewriter.Rewrite("using TMPro;\nclass A { }\n");

            Assert.AreEqual("using OneText.UGUI;\nclass A { }\n", result.Text);
            Assert.AreEqual(1, result.Changes.Count);
            Assert.AreEqual(1, result.Changes[0].Line);
            Assert.AreEqual("using TMPro;", result.Changes[0].Original);
            Assert.AreEqual("using OneText.UGUI;", result.Changes[0].Replacement);
        }

        [Test]
        public void WorldText_Also_Needs_The_Namespace_OneTextMesh_Lives_In()
        {
            // OneTextMesh is in OneText, not OneText.UGUI, so a file that got a
            // world-text rewrite needs both usings and a file that did not gets
            // only the one. Handing every file both would be a using nobody
            // asked for in a project that runs with warnings as errors.
            var world = TmpScriptRewriter.Rewrite("using TMPro;\nclass A { TextMeshPro m; }\n");
            Assert.AreEqual("using OneText;\nusing OneText.UGUI;\nclass A { OneTextMesh m; }\n",
                world.Text);

            var ui = TmpScriptRewriter.Rewrite("using TMPro;\nclass A { TextMeshProUGUI m; }\n");
            Assert.IsFalse(ui.Text.Contains("using OneText;\n"), "an unneeded using was added");
        }

        [Test]
        public void A_Using_The_File_Already_Has_Is_Not_Added_Twice()
        {
            var result = TmpScriptRewriter.Rewrite(
                "using OneText.UGUI;\nusing TMPro;\nclass A { TMP_Text t; }\n");

            Assert.AreEqual("using OneText.UGUI;\nclass A { OneTextLabel t; }\n", result.Text);

            // The TMPro line went rather than being replaced by a copy of the
            // line above it.
            var removal = result.Changes.Find(change => change.Original == "using TMPro;");
            Assert.IsNull(removal.Replacement, "the duplicate using was written anyway");
            Assert.AreEqual(2, removal.Line);
        }

        [Test]
        public void Indent_And_Line_Endings_Survive()
        {
            var result = TmpScriptRewriter.Rewrite(
                "namespace N\r\n{\r\n    using TMPro;\r\n    class A { TextMeshPro m; }\r\n}\r\n");

            Assert.AreEqual(
                "namespace N\r\n{\r\n    using OneText;\r\n    using OneText.UGUI;\r\n" +
                "    class A { OneTextMesh m; }\r\n}\r\n",
                result.Text);
        }

        // ------------------------------------------------------------- types

        [TestCase("TextMeshProUGUI", "OneTextLabel")]
        [TestCase("TMP_Text", "OneTextLabel")]
        [TestCase("TMP_InputField", "OneTextInputField")]
        [TestCase("TextMeshPro", "OneTextMesh")]
        public void EveryMappedType_IsRenamed(string from, string to)
        {
            Assert.AreEqual($"class A {{ {to} field; }}", Rewrite($"class A {{ {from} field; }}"));
        }

        [TestCase("TMPro.TextMeshProUGUI", "OneText.UGUI.OneTextLabel")]
        [TestCase("TMPro.TMP_Text", "OneText.UGUI.OneTextLabel")]
        [TestCase("TMPro.TMP_InputField", "OneText.UGUI.OneTextInputField")]
        [TestCase("TMPro.TextMeshPro", "OneText.OneTextMesh")]
        [TestCase("TMPro.TextAlignmentOptions", "OneText.UGUI.TextAlignmentOptions")]
        [TestCase("TMPro.TextWrappingModes", "OneText.UGUI.TextWrappingModes")]
        public void QualifiedNames_AreRenamedQualified(string from, string to)
        {
            // A file that never had a using still has to migrate, and the
            // namespace it names is not the namespace the answer lives in.
            Assert.AreEqual($"class A {{ {to} field; }}", Rewrite($"class A {{ {from} field; }}"));
        }

        [Test]
        public void QualifiedEnums_KeepTheirMember()
        {
            // The member ride-along is the whole point of mapping the enums:
            // an assignment is the shape they actually occur in.
            Assert.AreEqual(
                "x.alignment = OneText.UGUI.TextAlignmentOptions.MidlineLeft;",
                Rewrite("x.alignment = TMPro.TextAlignmentOptions.MidlineLeft;"));
        }

        [Test]
        public void UnqualifiedEnums_AreLeftForTheUsingRewrite()
        {
            // OneText declares TextAlignmentOptions and TextWrappingModes under
            // the names TMP used, so once `using TMPro;` has become
            // `using OneText.UGUI;` an unqualified use is already correct.
            // Rewriting it would be changing a name to itself.
            Assert.AreEqual(
                "using OneText.UGUI;\nclass A { TextAlignmentOptions a; TextWrappingModes w; }",
                Rewrite("using TMPro;\nclass A { TextAlignmentOptions a; TextWrappingModes w; }"));
        }

        [Test]
        public void TextMeshProUGUI_Wins_Over_TextMeshPro()
        {
            // Read shortest-first, a UGUI label becomes "OneTextMeshUGUI",
            // which is not a type and is a very confusing compile error.
            Assert.AreEqual("OneTextLabel a; OneTextMesh b;",
                Rewrite("TextMeshProUGUI a; TextMeshPro b;"));
        }

        [Test]
        public void PartOfALongerName_IsNotAName()
        {
            const string source =
                "class MyTextMeshProUGUIWrapper : TextMeshProUGUIWrapper\n" +
                "{\n" +
                "    void F() { thing.TextMeshProUGUI(); other.text = 1; other.TMP_Text = 2; }\n" +
                "}\n";

            // Nothing here is a TMP type: two are longer identifiers that merely
            // start with one, and three are member accesses on somebody else's
            // object. Rewriting a member is the failure mode that silently
            // renames a field in an unrelated class.
            Assert.AreEqual(source, Rewrite(source));
            Assert.IsFalse(TmpScriptRewriter.Rewrite(source).Changed);
        }

        // ---------------------------------------------------- text, not code

        [TestCase("var s = \"TMP_Text\";")]
        [TestCase("var s = \"using TMPro;\";")]
        [TestCase("var s = @\"C:\\TMP_Text\\TextMeshProUGUI\";")]
        [TestCase("var s = $\"{count} TMP_Text and TextMeshPro\";")]
        [TestCase("var s = $@\"TMP_Text {count} TextMeshProUGUI\";")]
        [TestCase("var s = @$\"TMP_Text {count}\";")]
        [TestCase("var s = \"he said \\\"TMP_Text\\\" once\";")]
        public void StringLiterals_ComeOutByteForByte(string source)
        {
            Assert.AreEqual(source, Rewrite(source));
        }

        [Test]
        public void Comments_ComeOutByteForByte()
        {
            const string source =
                "// TextMeshProUGUI was here\n" +
                "/* and TMP_Text\n" +
                "   and using TMPro; too */\n" +
                "class A { } // TextMeshPro\n";

            Assert.AreEqual(source, Rewrite(source));
        }

        [Test]
        public void Code_After_A_String_Or_Comment_Is_Still_Code()
        {
            // The other half of the same risk: a lexer that loses track of where
            // a literal ended stops rewriting the rest of the file, and the only
            // symptom is a migration that half worked.
            Assert.AreEqual("var s = \"TMP_Text\"; OneTextLabel a;",
                Rewrite("var s = \"TMP_Text\"; TMP_Text a;"));
            Assert.AreEqual("/* TMP_Text */ OneTextLabel a;",
                Rewrite("/* TMP_Text */ TMP_Text a;"));
            Assert.AreEqual("var s = @\"TMP_Text\"; OneTextMesh a;",
                Rewrite("var s = @\"TMP_Text\"; TextMeshPro a;"));
        }

        [Test]
        public void CharLiterals_DoNotDerailTheLexer()
        {
            // A quote in a char literal, and an escaped quote in a char literal:
            // read either as the start of a string and everything after it in
            // the file is "inside a string" for ever.
            Assert.AreEqual("char q = '\"'; OneTextLabel a;",
                Rewrite("char q = '\"'; TextMeshProUGUI a;"));
            Assert.AreEqual("char e = '\\''; OneTextLabel a;",
                Rewrite("char e = '\\''; TMP_Text a;"));
            Assert.AreEqual("char b = '\\\\'; OneTextMesh a;",
                Rewrite("char b = '\\\\'; TextMeshPro a;"));
        }

        // --------------------------------------------------------- residuals

        [Test]
        public void WhatItCannotHandle_IsReportedByNameAndLine()
        {
            var result = TmpScriptRewriter.Rewrite(
                "using TMPro;\n" +          // 1
                "public class A\n" +        // 2
                "{\n" +                     // 3
                "    TextMeshProUGUI l;\n" +// 4
                "    TMP_SpriteAsset s;\n" +// 5
                "    TMP_FontAsset f;\n" +  // 6
                "}\n");                     // 7

            Assert.AreEqual(2, result.Residuals.Count, "expected exactly the two unmapped types");
            Assert.AreEqual("TMP_SpriteAsset", result.Residuals[0].Name);
            Assert.AreEqual(5, result.Residuals[0].Line);
            Assert.AreEqual("TMP_FontAsset", result.Residuals[1].Name);
            Assert.AreEqual(6, result.Residuals[1].Line);

            // And the part it does understand is not done either, because doing
            // it would take `using TMPro;` away from the two lines that still
            // need it. Half a migration does not beat none here; it is a file
            // that does not compile, and the two names are why.
            Assert.IsFalse(result.Viable);
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(result.Text.Contains("OneTextLabel l;"));
            CollectionAssert.AreEqual(new[] { "TMP_SpriteAsset", "TMP_FontAsset" },
                result.BlockingNames);
        }

        /// <summary>
        /// TextMesh Pro's dropdown, which used to be the example of a name this
        /// could not handle.
        ///
        /// It is mapped now for the reason uGUI's is: the labels a dropdown
        /// points at are converted, and a field still typed <c>TMP_Dropdown</c>
        /// would be holding a component that no longer exists. The nested types
        /// come with it — <c>OptionData</c> is where a project's own code builds
        /// the list, and it is written through the dropdown's name.
        /// </summary>
        [Test]
        public void TheTmpDropdown_AndTheTypesUnderIt_AreRewritten()
        {
            var result = TmpScriptRewriter.Rewrite(
                "using TMPro;\n" +
                "public class A\n" +
                "{\n" +
                "    TMP_Dropdown d;\n" +
                "    void Fill() { d.options.Add(new TMP_Dropdown.OptionData(\"one\")); }\n" +
                "}\n");

            Assert.IsTrue(result.Viable);
            Assert.IsEmpty(result.Residuals);
            Assert.AreEqual(
                "using OneText.UGUI;\n" +
                "public class A\n" +
                "{\n" +
                "    OneTextDropdown d;\n" +
                "    void Fill() { d.options.Add(new OneTextDropdown.OptionData(\"one\")); }\n" +
                "}\n",
                result.Text);
        }

        [Test]
        public void A_File_Whose_Every_Name_Is_Mapped_Is_Rewritten_In_Full()
        {
            // The other side of the gate: nothing is left needing TMPro, so
            // nothing stands in the way, and the file goes through whole.
            var result = TmpScriptRewriter.Rewrite(
                "using TMPro;\npublic class A\n{\n    TextMeshProUGUI l;\n    TMP_InputField f;\n}\n");

            Assert.IsTrue(result.Viable);
            Assert.IsNull(result.Blocker);
            Assert.IsEmpty(result.Residuals);
            Assert.AreEqual(
                "using OneText.UGUI;\npublic class A\n{\n    OneTextLabel l;\n    OneTextInputField f;\n}\n",
                result.Text);
        }

        [Test]
        public void A_Qualified_Residual_Reports_The_Type_Not_The_Namespace()
        {
            var result = TmpScriptRewriter.Rewrite("class A { TMPro.TMP_Settings s; }\n");

            Assert.AreEqual(1, result.Residuals.Count);
            Assert.AreEqual("TMP_Settings", result.Residuals[0].Name);
            Assert.AreEqual(1, result.Residuals[0].Line);
        }

        [Test]
        public void A_Using_Alias_Is_Left_Alone_And_Reported()
        {
            // "using TMP = TMPro;" is not the line this rewrites, and guessing
            // at it would mean rewriting every TMP.Something in the file. It
            // survives untouched and is named instead.
            const string source = "using TMP = TMPro;\nclass A { }\n";

            Assert.AreEqual(source, Rewrite(source));
            var result = TmpScriptRewriter.Rewrite(source);
            Assert.AreEqual(1, result.Residuals.Count);
            Assert.AreEqual("TMPro", result.Residuals[0].Name);
        }

        [Test]
        public void TmpNames_InText_AreNotResiduals()
        {
            // The report is read as a to-do list, so a log message must not put
            // an item on it.
            var result = TmpScriptRewriter.Rewrite(
                "// TMP_Dropdown\nvar s = \"TMP_Settings\";\n");

            Assert.IsEmpty(result.Residuals);
            Assert.IsFalse(result.Changed);
        }

        // ------------------------------------------------------- idempotence

        [Test]
        public void RewritingTwice_IsRewritingOnce()
        {
            const string source =
                "using TMPro;\n" +
                "using UnityEngine;\n" +
                "\n" +
                "public class Hud : MonoBehaviour\n" +
                "{\n" +
                "    public TextMeshProUGUI score;\n" +
                "    public TMP_InputField name;\n" +
                "    public TextMeshPro sign;\n" +
                "    void Start() { score.text = \"TMP_Text\"; }\n" +
                "}\n";

            var once = TmpScriptRewriter.Rewrite(source);
            var twice = TmpScriptRewriter.Rewrite(once.Text);

            Assert.AreEqual(once.Text, twice.Text, "a second pass changed something");
            Assert.IsFalse(twice.Changed, "a second pass found work to do");
            Assert.IsEmpty(twice.Residuals);

            Assert.IsTrue(once.Text.Contains("public OneTextLabel score;"));
            Assert.IsTrue(once.Text.Contains("public OneTextInputField name;"));
            Assert.IsTrue(once.Text.Contains("public OneTextMesh sign;"));
            Assert.IsTrue(once.Text.Contains("score.text = \"TMP_Text\";"),
                "a member access, and a string, were both left alone");
        }

        [Test]
        public void AFileWithNothingToSay_IsUnchanged()
        {
            const string source = "using UnityEngine;\nclass A : MonoBehaviour { }\n";
            var result = TmpScriptRewriter.Rewrite(source);

            Assert.AreEqual(source, result.Text);
            Assert.IsFalse(result.Changed);
            Assert.IsEmpty(result.Residuals);
        }

        // --------------------------------------------- names, not type names

        [Test]
        public void An_Enum_Member_Named_TextMeshProUGUI_Is_A_Member_And_Stays_One()
        {
            // DOTween Pro's TargetType, verbatim in shape. Every use of these is
            // TargetType.TextMeshProUGUI, which the dot rule already leaves
            // alone; renaming the declarations and not the uses is CS0117 in a
            // package the project did not write, and the old rewriter did it
            // while reporting nothing at all.
            const string source =
                "using TMPro;\n" +
                "public enum TargetType\n" +
                "{\n" +
                "    Unset,\n" +
                "    TextMeshPro,\n" +
                "    TextMeshProUGUI,\n" +
                "}\n" +
                "public class Setter { TargetType t = TargetType.TextMeshProUGUI; }\n";

            var result = TmpScriptRewriter.Rewrite(source);

            Assert.IsTrue(result.Text.Contains("    TextMeshPro,\n    TextMeshProUGUI,\n"),
                "an enum member was renamed");
            Assert.IsTrue(result.Text.Contains("TargetType.TextMeshProUGUI"));

            // Reported, so nobody wonders why those two lines are still there,
            // but reported as what they are: names, which nothing is waiting on.
            Assert.AreEqual(2, result.Residuals.Count);
            Assert.AreEqual(TmpResidualKind.Declaration, result.Residuals[0].Kind);
            Assert.AreEqual(TmpResidualKind.Declaration, result.Residuals[1].Kind);
            Assert.IsTrue(result.Viable);
        }

        [TestCase("public class A { public UnityEngine.GameObject TextMeshPro; }")]
        [TestCase("public class A { void TextMeshProUGUI() { } }")]
        [TestCase("public class A { public int[] TMP_Text; }")]
        [TestCase("public class A { public System.Action TextMeshPro; }")]
        [TestCase("public class A { void F() { int TMP_Text = 3; } }")]
        [TestCase("public class TextMeshPro { }")]
        public void AName_That_Merely_Reads_Like_AType_IsNeverRenamed(string source)
        {
            // The worst of these is the first. Unity matches serialized fields
            // by name, so renaming one does not fail to compile — it compiles,
            // and every scene and prefab that ever set that field quietly comes
            // up empty, with nothing anywhere to say so.
            Assert.AreEqual(source, Rewrite(source));
            Assert.IsFalse(TmpScriptRewriter.Rewrite(source).Changed);
        }

        [TestCase("public class A { public List<TMP_Text> labels; }")]
        [TestCase("public class A { [SerializeField] TextMeshProUGUI label; }")]
        [TestCase("public class A { void F() { var x = GetComponent<TextMeshProUGUI>(); } }")]
        [TestCase("public class A { void F(TMP_Text a, TMP_Text b) { } }")]
        [TestCase("public class A { void F() { foreach (TextMeshProUGUI l in all) { } } }")]
        [TestCase("public class A { void F() { var x = o as TMP_Text; } }")]
        [TestCase("public class A { void F() { var x = (TMP_Text)o; } }")]
        [TestCase("public class A { void F() { var t = typeof(TMP_Text); } }")]
        [TestCase("public class A : TMP_Text { }")]
        [TestCase("public class A<T> where T : TMP_Text { }")]
        [TestCase("public class A { public static void F(this TMP_Text t) { } }")]
        [TestCase("public class A { void F(out TMP_Text t) { t = null; } }")]
        [TestCase("public class A { TMP_Text F() { return null; } }")]
        [TestCase("public class A { public TMP_Text Label => label; }")]
        public void EveryPlace_AType_CanStand_IsStillRewritten(string source)
        {
            // The other direction of the same test, and the one that keeps the
            // position rule from quietly becoming "never rewrite anything".
            Assert.AreNotEqual(source, Rewrite(source), "a real type use was skipped");
        }

        [Test]
        public void An_Attribute_And_An_Array_Both_End_In_A_Bracket()
        {
            // `[SerializeField] TMP_Text label;` and `int[] TextMeshPro;` differ
            // only in what stands before the opening bracket, and they mean
            // opposite things: one introduces a type, the other has just
            // finished one and is naming it.
            Assert.AreEqual("class A { [SerializeField] OneTextLabel label; }",
                Rewrite("class A { [SerializeField] TMP_Text label; }"));
            Assert.AreEqual("class A { public int[] TextMeshPro; }",
                Rewrite("class A { public int[] TextMeshPro; }"));
        }

        [Test]
        public void APosition_ItCannotRead_IsLeftAlone_And_Reported()
        {
            // Under-rewriting is a compile error with a line number on it, so a
            // position the rule does not cover is declined rather than guessed.
            var result = TmpScriptRewriter.Rewrite("class A { void F() { if (i > TMP_Text.Zero) { } } }");

            Assert.IsFalse(result.Changed);
            Assert.AreEqual(1, result.Residuals.Count);
            Assert.AreEqual("TMP_Text", result.Residuals[0].Name);
            Assert.AreEqual(TmpResidualKind.Reference, result.Residuals[0].Kind);
        }

        // ---------------------------------------------- the using and the rest

        [Test]
        public void AUsing_With_A_Note_After_It_Is_Still_AUsing()
        {
            // A real file, comment and all. The strict version of this declined
            // the line and then let the type pass rewrite the file around it,
            // which is a dead `using TMPro;` over types that are no longer in
            // TMPro. The note belongs to whoever wrote it and comes along.
            var result = TmpScriptRewriter.Rewrite(
                "using TMPro; // TextMeshPro를 사용하는 경우\nclass A { TMP_Text t; }\n");

            Assert.AreEqual("using OneText.UGUI; // TextMeshPro를 사용하는 경우\n" +
                            "class A { OneTextLabel t; }\n", result.Text);
            Assert.IsTrue(result.Viable);
        }

        [Test]
        public void AComment_Outlives_AUsing_That_WasOnlyRemoved()
        {
            // Nothing needed writing here, so the line would have gone — but a
            // line with a comment on it is not the rewriter's to delete.
            Assert.AreEqual("using OneText.UGUI;\n// keep me\nclass A { OneTextLabel t; }\n",
                Rewrite("using OneText.UGUI;\nusing TMPro; // keep me\nclass A { TMP_Text t; }\n"));
        }

        [Test]
        public void AUsing_Line_It_CannotRead_Stops_TheWholeFile()
        {
            // Two statements on one line is not a shape this converts. What it
            // must not do is rewrite the types anyway and leave them with no
            // namespace, which is the same disagreement the comment caused.
            const string source = "using TMPro; using UnityEngine;\nclass A { TMP_Text t; }\n";
            var result = TmpScriptRewriter.Rewrite(source);

            Assert.AreEqual(source, result.Text);
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(result.Viable);
            Assert.IsNotNull(result.Blocker);
        }

        [Test]
        public void AFile_That_Would_Lose_TheUsing_It_Still_Needs_IsNot_Rewritten()
        {
            // DOTweenTextMeshPro.cs, in miniature. It reported eighteen
            // residuals and rewrote the file regardless, taking `using TMPro;`
            // with it and leaving every one of those eighteen names unresolved.
            //
            // The names here used to be TMP_MeshInfo and TMP_CharacterInfo,
            // which is what that file actually blocked on. They are no longer
            // blockers because OneText grew counterparts for them, so this
            // stands on two that still have none — the gate is what is being
            // tested, not any particular name, and it needs a name that is
            // genuinely in the way to test it with.
            const string source =
                "using TMPro;\n" +
                "public static class DOTweenTMP\n" +
                "{\n" +
                "    public static void Shake(TMP_Text target)\n" +
                "    {\n" +
                "        TMP_FontAsset font = target.font;\n" +
                "        TMP_SpriteAsset sprites = target.spriteAsset;\n" +
                "    }\n" +
                "}\n";

            var result = TmpScriptRewriter.Rewrite(source);

            Assert.AreEqual(source, result.Text, "a file that cannot compile was written anyway");
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(result.Viable);
            CollectionAssert.AreEqual(new[] { "TMP_FontAsset", "TMP_SpriteAsset" },
                result.BlockingNames);
            Assert.IsNotEmpty(result.Blocker);
        }

        [Test]
        public void APerCharacter_Animator_IsRewritten_NowThatItsTypes_HaveCounterparts()
        {
            // The other half of the test above, and the reason it had to move.
            //
            // This shape is why a real project stalls: DOTweenPro's text
            // animator reaches into per-character mesh data, nothing in OneText
            // answered to those names, and the file was refused. A refused file
            // holds back every file grouped with it — on Five-Dice that was one
            // vendored file stopping twenty-eight of the project's own scripts,
            // and 171 of 188 broken references traced back to it.
            const string source =
                "using TMPro;\n" +
                "public static class DOTweenTMP\n" +
                "{\n" +
                "    public static void Shake(TMP_Text target)\n" +
                "    {\n" +
                "        TMP_MeshInfo[] info = target.textInfo.meshInfo;\n" +
                "        TMP_CharacterInfo c = target.textInfo.characterInfo[0];\n" +
                "        target.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);\n" +
                "    }\n" +
                "}\n";

            var result = TmpScriptRewriter.Rewrite(source);

            Assert.IsTrue(result.Viable, "the file was refused: " + result.Blocker);
            Assert.IsTrue(result.Changed);
            CollectionAssert.IsEmpty(result.BlockingNames);
            StringAssert.Contains("OneTextMeshInfo[] info", result.Text);
            StringAssert.Contains("OneTextCharacterInfo c", result.Text);
            StringAssert.Contains("OneTextVertexDataUpdateFlags.Vertices", result.Text);
            StringAssert.DoesNotContain("TMPro", result.Text,
                "the using line stayed, so something in the file still needs it");
            // The prefix trap: read longest-first, TMP_Text would eat the front
            // of TMP_TextInfo and leave OneTextLabelInfo behind.
            StringAssert.DoesNotContain("OneTextLabelInfo", result.Text);
        }

        [Test]
        public void AQualified_Name_DoesNot_Depend_On_TheUsing_And_DoesNot_Block()
        {
            // TMPro.TMP_Settings resolves with or without the using line, so it
            // is work to do and not work in the way. A gate that could not tell
            // the difference would refuse half the project.
            var result = TmpScriptRewriter.Rewrite(
                "using TMPro;\nclass A { TextMeshProUGUI l; TMPro.TMP_Settings s; }\n");

            Assert.IsTrue(result.Viable);
            Assert.IsTrue(result.Text.Contains("OneTextLabel l;"));
            Assert.AreEqual(TmpResidualKind.Qualified, result.Residuals[0].Kind);
        }

        // -------------------------------------------------------------- scan

        [Test]
        public void Scan_Reports_OnlyTheFilesWithSomethingInThem()
        {
            string folder = Path.Combine(Path.GetTempPath(),
                "OneTextTmpScan" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string hit = Path.Combine(folder, "Hud.cs");
                string miss = Path.Combine(folder, "Plain.cs");
                string prose = Path.Combine(folder, "Prose.cs");
                File.WriteAllText(hit, "using TMPro;\nclass Hud { TextMeshProUGUI l; }\n");
                File.WriteAllText(miss, "class Plain { }\n");
                File.WriteAllText(prose, "class Prose { } // TextMeshProUGUI\n");

                var found = TmpScriptRewriter.Scan(new List<string> { hit, miss, prose });

                Assert.AreEqual(1, found.Count, "a comment or a plain file was reported");
                Assert.AreEqual(hit, found[0].Path);
                Assert.IsTrue(found[0].Result.Changed);

                // And the same folder through the file walker.
                var walked = TmpScriptRewriter.ScriptsUnder(folder);
                Assert.AreEqual(3, walked.Count, "the walker missed a script");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Scan_Survives_A_Path_That_IsNotThere()
        {
            Assert.IsEmpty(TmpScriptRewriter.Scan(new List<string>
            {
                Path.Combine(Path.GetTempPath(), "OneTextNoSuchFile.cs"),
            }));
            Assert.IsEmpty(TmpScriptRewriter.Scan(null));
            Assert.IsEmpty(TmpScriptRewriter.ScriptsUnder("/no/such/folder/anywhere"));
        }

        [Test]
        public void Somebody_Elses_Folder_Is_Flagged_And_Nothing_More()
        {
            // A flag, not a decision and not a vendor list. The Hub leaves these
            // unticked; rewriting a package's own source is something a person
            // chooses once and then maintains for ever.
            Assert.IsTrue(TmpScriptRewriter.IsLikelyThirdParty("Assets/Plugins/DOTween/Foo.cs"));
            Assert.IsTrue(TmpScriptRewriter.IsLikelyThirdParty("Assets/Standard Assets/Foo.cs"));
            Assert.IsFalse(TmpScriptRewriter.IsLikelyThirdParty("Assets/Scripts/Hud.cs"));
            Assert.IsFalse(TmpScriptRewriter.IsLikelyThirdParty(null));
        }

        // ---------------------------------------------------------- assembly

        [Test]
        public void AFile_Under_Somebody_Elses_Asmdef_Is_Told_What_It_Owes()
        {
            // Three rewritten files lived under an asmdef whose references were
            // spine-unity, spine-csharp and Unity.TextMeshPro. Nothing in any of
            // the three files was wrong; OneTextLabel simply was not visible
            // from that assembly, twenty-one times over. autoReferenced does not
            // reach here — it only covers the predefined assembly.
            Folder(folder =>
            {
                string asmdef = Path.Combine(folder, "LayerLab.CommonSource.asmdef");
                File.WriteAllText(asmdef,
                    "{\n    \"name\": \"LayerLab.CommonSource\",\n    \"references\": [\n" +
                    "        \"spine-unity\",\n        \"spine-csharp\",\n" +
                    "        \"Unity.TextMeshPro\"\n    ]\n}",
                    new UTF8Encoding(true));

                string script = Path.Combine(folder, "Label.cs");
                File.WriteAllText(script, "using TMPro;\nclass Label { TextMeshProUGUI l; }\n");

                var report = TmpScriptRewriter.ScanProject(new List<string> { script }, folder);

                Assert.AreEqual(1, report.Files.Count);
                var finding = report.Files[0];
                Assert.AreEqual("LayerLab.CommonSource", finding.Assembly.Name);
                Assert.IsTrue(finding.NeedsAssemblyPatch);
                CollectionAssert.AreEqual(new[] { "OneText", "OneText.UGUI" },
                    finding.MissingReferences);

                // The patch is a separate call on purpose: a scan that edited a
                // vendor's asmdef would be operating during the diagnosis.
                Assert.IsTrue(TmpAssemblyGraph.Patch(asmdef, finding.MissingReferences));

                byte[] bytes = File.ReadAllBytes(asmdef);
                Assert.AreEqual(0xEF, bytes[0], "the byte order mark did not survive");
                Assert.IsTrue(File.ReadAllText(asmdef).Contains("\"spine-unity\","),
                    "the existing references were reshaped");

                var graph = TmpAssemblyGraph.Build(folder);
                Assert.IsEmpty(graph.Missing(graph.Owner(script),
                    new[] { "OneText", "OneText.UGUI" }));
                Assert.IsFalse(TmpAssemblyGraph.Patch(asmdef, finding.MissingReferences),
                    "a second patch wrote the references twice");
            });
        }

        [Test]
        public void AReference_Already_There_By_Guid_IsNot_Added_By_Name()
        {
            // Unity writes both forms and real projects contain both, sometimes
            // in the same array. Adding the name beside the GUID would be the
            // same assembly listed twice.
            const string asmdef =
                "{\n    \"name\": \"G\",\n    \"references\": [\n" +
                "        \"GUID:f78bcd2f5c7814894929e319cfe7a2f9\"\n    ]\n}";

            Assert.AreEqual(asmdef,
                TmpAssemblyGraph.WithReferences(asmdef, new[] { "OneText.UGUI" }));
            Assert.IsTrue(TmpAssemblyGraph.WithReferences(asmdef, new[] { "OneText" })
                .Contains("\"OneText\""));
        }

        [Test]
        public void AFile_In_The_Predefined_Assembly_Owes_Nothing()
        {
            Folder(folder =>
            {
                string script = Path.Combine(folder, "Hud.cs");
                File.WriteAllText(script, "using TMPro;\nclass Hud { TextMeshProUGUI l; }\n");

                var report = TmpScriptRewriter.ScanProject(new List<string> { script }, folder);

                Assert.IsNull(report.Files[0].Assembly);
                Assert.IsFalse(report.Files[0].NeedsAssemblyPatch);
            });
        }

        // ------------------------------------------------------------ groups

        [Test]
        public void An_Extension_Method_And_Its_Callers_AreOne_Unit()
        {
            // TextMeshProExtensions declares DOTextWithCallbacks on a TMP_Text
            // and CombatDiceView calls it. Convert either alone and it does not
            // compile, and reverting the caller cannot save it, because it is
            // the provider that moved. Nothing in the call site names the class
            // that declares the method, so the method's own name is what ties
            // the two files together.
            Folder(folder =>
            {
                string provider = Path.Combine(folder, "TextMeshProExtensions.cs");
                string consumer = Path.Combine(folder, "CombatDiceView.cs");
                string alone = Path.Combine(folder, "Hud.cs");

                File.WriteAllText(provider,
                    "using TMPro;\npublic static class TextMeshProExtensions\n{\n" +
                    "    public static void DOTextWithCallbacks(this TMP_Text target) { }\n}\n");
                File.WriteAllText(consumer,
                    "using TMPro;\npublic class CombatDiceView\n{\n    public TMP_Text label;\n" +
                    "    void Play() { label.DOTextWithCallbacks(); }\n}\n");
                File.WriteAllText(alone, "using TMPro;\nclass Hud { TextMeshProUGUI l; }\n");

                var report = TmpScriptRewriter.ScanProject(
                    new List<string> { provider, consumer, alone }, folder);

                Assert.AreEqual(3, report.Files.Count);
                Assert.AreEqual(1, report.Groups.Count, "a file that owes nothing was grouped");
                CollectionAssert.AreEquivalent(new[] { provider, consumer },
                    report.Groups[0].Paths);
                Assert.AreEqual("DOTextWithCallbacks", report.Groups[0].Reason);
            });
        }

        [Test]
        public void APublic_Member_Written_In_TMP_Terms_Takes_Its_Readers_With_It()
        {
            Folder(folder =>
            {
                string provider = Path.Combine(folder, "LabelBank.cs");
                string consumer = Path.Combine(folder, "Screen.cs");
                File.WriteAllText(provider,
                    "using TMPro;\npublic class LabelBank { public TMP_Text Title; }\n");
                File.WriteAllText(consumer,
                    "using TMPro;\npublic class Screen { LabelBank bank; TMP_Text t; }\n");

                var report = TmpScriptRewriter.ScanProject(
                    new List<string> { provider, consumer }, folder);

                Assert.AreEqual(1, report.Groups.Count);
                Assert.AreEqual(2, report.Groups[0].Paths.Count);
                Assert.AreEqual("LabelBank", report.Groups[0].Reason);
            });
        }

        private static void Folder(Action<string> body)
        {
            string folder = Path.Combine(Path.GetTempPath(),
                "OneTextTmpScan" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                body(folder);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
