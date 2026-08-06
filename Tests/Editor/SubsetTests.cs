using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// Font subsetting.
    ///
    /// The interesting test is not "the file got smaller"; dropping glyphs at
    /// random does that. It is that the <em>layout tables survive</em>. GSUB and
    /// GPOS entries reference glyph ids, so a subsetter that renumbers glyphs
    /// without rewriting them silently breaks ligatures, marks and kerning, and
    /// Arabic stops joining. That is the failure that only shows up in the one
    /// language nobody on the team reads, and the field has shipped a fix for
    /// exactly it. hb-subset does this correctly by default; these are the
    /// checks that prove it rather than assume it.
    /// </summary>
    public class SubsetTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSans.ttf";
        private const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansArabic.ttf";
        private const string VariableFontPath = "Packages/com.onetext.core/Tests/Fonts/NotoSansVariable.ttf";

        private static byte[] Bytes(string packagePath) =>
            File.ReadAllBytes(Path.GetFullPath(packagePath));

        private static List<int> CodepointsOf(string text)
        {
            var codepoints = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                int cp = char.ConvertToUtf32(text, i);
                codepoints.Add(cp);
                if (char.IsHighSurrogate(text[i])) i++;
            }
            return codepoints;
        }

        /// <summary>Glyph ids and advances for a string, as shaped by a font.</summary>
        private static List<(uint Glyph, int Advance, int XOffset, int YOffset)> Shape(
            byte[] fontBytes, string text)
        {
            using var font = FontData.Load(fontBytes);
            using var shaper = new Shaper();
            var shaped = new List<ShapedGlyph>();
            shaper.Shape(font, text, shaped);

            var result = new List<(uint, int, int, int)>();
            foreach (var glyph in shaped)
                result.Add((glyph.GlyphId, glyph.XAdvance, glyph.XOffset, glyph.YOffset));
            return result;
        }

        [Test]
        public void Subsetting_IsAvailable()
        {
            Assert.IsTrue(FontSubsetter.IsAvailable,
                "the vendored HarfBuzz has no subsetting API; see Docs/NATIVES.md, which " +
                "records the symbol count verified for every platform binary");
        }

        [Test]
        public void SubsetIsSmaller_AndStillLoads()
        {
            var original = Bytes(LatinFontPath);
            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf("Hamburgefonstiv"),
                out var subset, out var report), report.Failure);

            Debug.Log($"[subset] latin: {report}");
            Assert.Less(subset.Length, original.Length, "the subset is not smaller");

            using var font = FontData.Load(subset);
            Assert.IsTrue(font.IsValid, "the subset font does not load");
            Assert.AreEqual(FontData.Load(original).UnitsPerEm, font.UnitsPerEm,
                "the subset changed the em size, which would change every measurement");
        }

        [Test]
        public void LatinLigatures_SurviveTheRenumbering()
        {
            // "fi" is one glyph in this face. If GSUB was not rewritten to the
            // new glyph ids, it comes out as two.
            var original = Bytes(LatinFontPath);
            var text = "office affair";

            var before = Shape(original, text);
            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf(text + "Hamburg"),
                out var subset, out var report), report.Failure);
            var after = Shape(subset, text);

            Assert.AreEqual(before.Count, after.Count,
                "the subset shaped a different number of glyphs; a ligature was lost, which " +
                "means GSUB was not remapped");
            for (int i = 0; i < before.Count; i++)
                Assert.AreEqual(before[i].Advance, after[i].Advance,
                    $"glyph {i} advances differently after subsetting");
        }

        [Test]
        public void ArabicJoining_SurvivesTheRenumbering()
        {
            // The one that matters. Arabic letters take initial, medial, final
            // and isolated forms through GSUB, and a subsetter that drops or
            // mis-maps those tables produces text that is technically present
            // and completely wrong to a reader.
            var original = Bytes(ArabicFontPath);
            const string text = "مرحبا بالعالم";

            var before = Shape(original, text);
            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf(text),
                out var subset, out var report), report.Failure);
            Debug.Log($"[subset] arabic: {report}");

            var after = Shape(subset, text);
            Assert.AreEqual(before.Count, after.Count,
                "the subset shaped a different number of glyphs; joining forms were lost");

            for (int i = 0; i < before.Count; i++)
            {
                Assert.AreEqual(before[i].Advance, after[i].Advance,
                    $"glyph {i}: advance changed, so a different form was selected");
                Assert.AreEqual(before[i].XOffset, after[i].XOffset,
                    $"glyph {i}: mark positioning moved, so GPOS was not remapped");
                Assert.AreEqual(before[i].YOffset, after[i].YOffset, $"glyph {i}: y offset moved");
            }
        }

        [Test]
        public void Hangul_SurvivesTheRenumbering()
        {
            var original = Bytes(LatinFontPath);
            // Jamo compose into syllables through the shaper rather than GSUB
            // in this face, but the check is the same one: the subset has to
            // shape identically to the face it came from.
            const string text = "Hangul: 한글";

            var before = Shape(original, text);
            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf(text),
                out var subset, out var report), report.Failure);

            var after = Shape(subset, text);
            Assert.AreEqual(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
                Assert.AreEqual(before[i].Advance, after[i].Advance, $"glyph {i} advance changed");
        }

        [Test]
        public void SubsetKeepsTheCharactersAsked_AndDropsTheRest()
        {
            var original = Bytes(LatinFontPath);
            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf("ABC"),
                out var subset, out var report), report.Failure);

            using var font = FontData.Load(subset);
            foreach (char kept in "ABC")
                Assert.IsTrue(font.HasGlyph(kept), $"'{kept}' was asked for and is not there");

            // And something well outside the request is gone, which is the
            // whole point, and also the cost: a subset face cannot draw what
            // nobody predicted.
            Assert.IsFalse(font.HasGlyph('Ж'),
                "the subset kept a character nobody asked for, so it saved nothing");
        }

        [Test]
        public void VariableFont_KeepsItsAxes()
        {
            // Subsetting must not flatten a variable font to a single instance:
            // a project that subsets and then asks for <b> would get regular
            // weight back with no error anywhere.
            var original = Bytes(VariableFontPath);
            using var before = FontData.Load(original);
            if (!before.IsVariable) Assert.Ignore("test font is not variable");

            Assert.IsTrue(FontSubsetter.TrySubset(original, CodepointsOf("Hamburgefonstiv"),
                out var subset, out var report), report.Failure);

            using var after = FontData.Load(subset);
            Assert.IsTrue(after.IsVariable, "subsetting flattened the variable font");
            Assert.AreEqual(before.GetVariationAxes().Length, after.GetVariationAxes().Length,
                "the subset lost a variation axis");
        }

        [Test]
        public void RefusesToSubsetToNothing()
        {
            // A font that draws nothing is never what anyone meant, and the
            // failure has to be loud rather than a zero-byte asset.
            Assert.IsFalse(FontSubsetter.TrySubset(Bytes(LatinFontPath), new List<int>(),
                out var subset, out var report));
            Assert.IsNull(subset);
            Assert.IsFalse(report.Succeeded);
            StringAssert.Contains("codepoints", report.Failure);
        }

        [Test]
        public void ReportsWhatItDid()
        {
            var original = Bytes(LatinFontPath);
            var codepoints = CodepointsOf("The quick brown fox");
            Assert.IsTrue(FontSubsetter.TrySubset(original, codepoints, out var subset, out var report));

            Assert.AreEqual(original.Length, report.OriginalBytes);
            Assert.AreEqual(subset.Length, report.SubsetBytes);
            Assert.AreEqual(codepoints.Count, report.CodepointsKept);
            Assert.Less(report.Fraction, 1f);
            StringAssert.Contains("KB", report.ToString());
        }

        [Test]
        public void CharsetOverload_UsesTheSameAssetPrewarmDoes()
        {
            // The input a subsetter needs is the one CharsetRecorder already
            // collects, which is most of why this milestone was small.
            var charset = ScriptableObject.CreateInstance<OneTextCharset>();
            try
            {
                charset.Characters = "Hamburgefonstiv";
                Assert.IsTrue(FontSubsetter.TrySubset(Bytes(LatinFontPath), charset,
                    out var subset, out var report), report.Failure);
                Assert.Greater(subset.Length, 0);

                using var font = FontData.Load(subset);
                Assert.IsTrue(font.HasGlyph('H'));
            }
            finally
            {
                Object.DestroyImmediate(charset);
            }
        }
    }
}
