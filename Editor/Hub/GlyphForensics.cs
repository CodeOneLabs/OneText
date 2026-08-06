using System.Collections.Generic;
using System.Text;
using UnityEngine;
using OneText.Unicode;

namespace OneText.Editor
{
    /// <summary>Everything knowable about one glyph on screen.</summary>
    public struct GlyphReport
    {
        /// <summary>Index of the glyph in the layout result.</summary>
        public int GlyphIndex;

        /// <summary>The characters this glyph came from, and where they are in the string.</summary>
        public string Characters;
        public int TextStart, TextLength;

        public uint GlyphId;

        /// <summary>Family that drew it, and the language tag it was registered under.</summary>
        public string FontFamily, FontLanguage;

        /// <summary>True when the shaper drew a glyph other than the one the cmap maps to.</summary>
        public bool Substituted;

        /// <summary>Glyph the cmap maps the first character to, for comparison.</summary>
        public uint NominalGlyphId;

        /// <summary>True when GPOS moved the glyph off its pen position.</summary>
        public bool Positioned;

        /// <summary>Line and run it belongs to, and the run's direction.</summary>
        public int LineIndex, RunIndex;
        public bool RightToLeft;

        /// <summary>Whether a line ends here, and the UAX #14 rule that decided the boundary.</summary>
        public bool EndsLine;
        public string BreakRule;

        /// <summary>Why the break rule fired, in words, where it is worth spelling out.</summary>
        public string BreakNote;

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append('\'').Append(Characters).Append("'  gid ").Append(GlyphId);
            if (Substituted) builder.Append(" (substituted from ").Append(NominalGlyphId).Append(')');
            builder.Append("  ").Append(FontFamily);
            if (!string.IsNullOrEmpty(FontLanguage)) builder.Append(" [").Append(FontLanguage).Append(']');
            builder.Append("  line ").Append(LineIndex);
            if (EndsLine && BreakRule != null) builder.Append("  break: ").Append(BreakRule);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Why a glyph looks the way it does: which font in the chain provided it,
    /// which characters it came from, whether the shaper substituted it, and,
    /// when it sits at the end of a line, which line-breaking rule put it
    /// there, by its name in UAX #14.
    ///
    /// Half the questions asked about text rendering are "why does this glyph
    /// look wrong" and "why did it break there", and the usual answer is to ask
    /// a forum. Every part of the answer is already in the engine at the moment
    /// it draws; this reads it back out.
    /// </summary>
    public static class GlyphForensics
    {
        /// <summary>Reports on every glyph of a finished layout.</summary>
        public static List<GlyphReport> Inspect(string text, TextLayoutResult layout, FontStack fonts)
        {
            var reports = new List<GlyphReport>();
            if (layout == null || string.IsNullOrEmpty(text)) return reports;

            // Line ends, so a glyph can say whether it is the last on its line,
            // which is the only place the break rule is the interesting answer.
            var lineEnds = new Dictionary<int, int>();
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                lineEnds[line.TextStart + line.TextLength] = i;
            }

            for (int runIndex = 0; runIndex < layout.Runs.Count; runIndex++)
            {
                var run = layout.Runs[runIndex];
                int lineIndex = LineOf(layout, runIndex);

                for (int g = run.GlyphStart; g < run.GlyphStart + run.GlyphCount; g++)
                {
                    if (g < 0 || g >= layout.Glyphs.Count) continue;
                    var glyph = layout.Glyphs[g];
                    int start = Mathf.Clamp(glyph.Cluster, 0, Mathf.Max(0, text.Length - 1));
                    int end = ClusterEnd(layout, run, g, text.Length);

                    var report = new GlyphReport
                    {
                        GlyphIndex = g,
                        GlyphId = glyph.GlyphId,
                        TextStart = start,
                        TextLength = Mathf.Max(1, end - start),
                        LineIndex = lineIndex,
                        RunIndex = runIndex,
                        RightToLeft = run.IsRightToLeft,
                        Positioned = glyph.XOffset != 0 || glyph.YOffset != 0,
                    };
                    report.Characters = text.Substring(report.TextStart,
                        Mathf.Min(report.TextLength, text.Length - report.TextStart));

                    if (run.Font != null)
                    {
                        report.FontFamily = FamilyOf(run.Font);
                        report.FontLanguage = fonts?.LanguageOf(run.Font);
                        int codepoint = char.ConvertToUtf32(
                            report.Characters.Length > 0 ? report.Characters : " ", 0);
                        report.NominalGlyphId = run.Font.NominalGlyph(codepoint);
                        // A cluster of several characters is a ligature or a
                        // syllable, where "substituted" is the normal state and
                        // saying so adds nothing.
                        report.Substituted = report.TextLength == 1 &&
                            report.NominalGlyphId != 0 && report.NominalGlyphId != glyph.GlyphId;
                    }

                    int boundary = report.TextStart + report.TextLength;
                    if (lineEnds.TryGetValue(boundary, out int endsLine) && endsLine == lineIndex)
                    {
                        report.EndsLine = true;
                        report.BreakRule = LineBreaker.RuleAt(text, boundary);
                        report.BreakNote = NoteFor(text, boundary, report.BreakRule);
                    }

                    reports.Add(report);
                }
            }
            return reports;
        }

        /// <summary>The features a face registers, as one line: context for a substitution.</summary>
        public static string FeatureSummary(FontData font)
        {
            if (font == null) return string.Empty;
            var substitution = font.LayoutFeatures(true);
            var positioning = font.LayoutFeatures(false);
            var builder = new StringBuilder();
            if (substitution.Length > 0)
                builder.Append("GSUB: ").Append(string.Join(", ", substitution));
            if (positioning.Length > 0)
            {
                if (builder.Length > 0) builder.Append("    ");
                builder.Append("GPOS: ").Append(string.Join(", ", positioning));
            }
            return builder.Length == 0 ? "no OpenType layout tables" : builder.ToString();
        }

        /// <summary>
        /// The rule in a sentence, for the handful of rules that get asked about.
        ///
        /// Not a table of all thirty-one: a rule number is looked up in seconds
        /// and a wrong paraphrase is worse than none. These are the ones that
        /// surprise people.
        /// </summary>
        private static string NoteFor(string text, int boundary, string rule)
        {
            switch (rule)
            {
                case "LB18": return "a space before the boundary: the ordinary case.";
                case "LB4":
                case "LB5": return "a mandatory break: the text itself asked for a new line.";
                case "LB8": return "after a zero-width space, which exists to allow exactly this.";
                case "LB21": return "an attached punctuation mark cannot start a line.";
                case "LB30a": return "regional indicators pair into flags, so breaks fall between pairs.";
                case "LB31":
                    return boundary > 0 && boundary <= text.Length &&
                           AsianTypography.IsIdeographic(text[boundary - 1])
                        ? "the default rule: between two ideographs, any boundary is a break."
                        : "the default rule: nothing forbade a break here.";
                case "LB9": return "the character is a combining mark and belongs to the one before it.";
                default: return null;
            }
        }

        private static int LineOf(TextLayoutResult layout, int runIndex)
        {
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                if (runIndex >= line.RunStart && runIndex < line.RunStart + line.RunCount) return i;
            }
            return 0;
        }

        /// <summary>
        /// Where a glyph's cluster ends: the next cluster value present in the
        /// run, or the run's end.
        ///
        /// Direction does not enter into it. Glyphs in a right-to-left run come
        /// out in visual order, but a cluster is an index into the string, and
        /// the string still runs forwards.
        /// </summary>
        private static int ClusterEnd(TextLayoutResult layout, in TextRun run, int glyphIndex, int textLength)
        {
            int cluster = layout.Glyphs[glyphIndex].Cluster;
            int runEnd = run.GlyphStart + run.GlyphCount;
            int next = run.TextStart + run.TextLength;

            for (int g = run.GlyphStart; g < runEnd; g++)
            {
                int other = layout.Glyphs[g].Cluster;
                if (other > cluster && other < next) next = other;
            }
            return Mathf.Clamp(next, cluster + 1, textLength);
        }

        private static string FamilyOf(FontData font)
        {
            foreach (var asset in AllFontAssets())
                if (asset != null && asset.Font == font) return asset.FamilyName;
            return "(font loaded from bytes)";
        }

        private static IEnumerable<OneFontAsset> AllFontAssets()
        {
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"t:{nameof(OneFontAsset)}"))
            {
                yield return UnityEditor.AssetDatabase.LoadAssetAtPath<OneFontAsset>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            }
        }
    }
}
