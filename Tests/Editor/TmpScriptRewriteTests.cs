using System.Collections.Generic;
using System.IO;
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
        public void QualifiedNames_AreRenamedQualified(string from, string to)
        {
            // A file that never had a using still has to migrate, and the
            // namespace it names is not the namespace the answer lives in.
            Assert.AreEqual($"class A {{ {to} field; }}", Rewrite($"class A {{ {from} field; }}"));
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
                "    TMP_Dropdown d;\n" +   // 5
                "    TMP_FontAsset f;\n" +  // 6
                "}\n");                     // 7

            Assert.AreEqual(2, result.Residuals.Count, "expected exactly the two unmapped types");
            Assert.AreEqual("TMP_Dropdown", result.Residuals[0].Name);
            Assert.AreEqual(5, result.Residuals[0].Line);
            Assert.AreEqual("TMP_FontAsset", result.Residuals[1].Name);
            Assert.AreEqual(6, result.Residuals[1].Line);

            // The part it does understand is still done: half a migration
            // beats none, and the compiler will point at the other half.
            Assert.IsTrue(result.Text.Contains("OneTextLabel l;"));
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
    }
}
