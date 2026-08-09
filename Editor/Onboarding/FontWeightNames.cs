using System.Collections.Generic;

namespace OneText.Editor
{
    /// <summary>
    /// What a face name says about its own weight, when it says anything at all.
    ///
    /// The absent case is the important one: <see cref="HasWeight"/> false does
    /// not mean "regular", it means the name did not tell us, and the caller
    /// must leave the font at whatever weight it opens as.
    /// </summary>
    public struct FontWeightGuess
    {
        /// <summary>True when exactly one weight word was found in the name.</summary>
        public bool HasWeight;

        /// <summary>The OpenType <c>usWeightClass</c> value: 100 through 950.</summary>
        public int Weight;

        /// <summary>The word it was read from, as the name spelled it.</summary>
        public string WeightName;

        /// <summary>True when the name also says italic or oblique.</summary>
        public bool Italic;

        /// <summary>Whether the name said anything worth acting on.</summary>
        public bool Found => HasWeight || Italic;

        public override string ToString() =>
            !Found ? "no opinion"
                : (HasWeight ? $"{WeightName} = wght {Weight}" : "upright") +
                  (Italic ? " italic" : string.Empty);
    }

    /// <summary>
    /// Reading a weight out of a face name, and turning it into axis settings a
    /// particular font file will actually accept.
    ///
    /// This exists because the recovered fonts are nearly all variable now. A
    /// project that had Pretendard-Regular, -Medium, -Bold and -ExtraBold as
    /// four TextMesh Pro assets gets one <c>PretendardVariable.ttf</c> back, and
    /// every label that used to be extra-bold renders at the file's default
    /// weight — not broken, not empty, just quietly a different design than the
    /// one that was shipped. Nobody reports that as a bug; they see it and
    /// assume it is what the migration does.
    ///
    /// The weight is written on the tin. <c>Pretendard-ExtraBold</c>,
    /// <c>NotoSansCJKsc-Black</c>, <c>Cairo_Line_Black</c> and
    /// <c>Sen-ExtraBold</c> all name their own <c>usWeightClass</c>, and reading
    /// it costs one pass over the words of a string.
    ///
    /// What it will not do is guess. Matching is on whole words, never on
    /// substrings, because a family called <c>Blackout</c> or <c>Boldoni</c>
    /// contains a weight word and is not one, and a name carrying two weight
    /// words is a name this code does not understand. Both come back as "no
    /// opinion", which leaves the font at its default weight — the same place it
    /// would have been without any of this. A wrong weight is worse than a
    /// default one, because a default weight looks like something nobody set and
    /// a wrong weight looks like something somebody chose.
    /// </summary>
    public static class FontWeightNames
    {
        /// <summary>
        /// The slant an oblique face is set to on a <c>slnt</c> axis, in
        /// degrees, matching <see cref="FontStack"/>'s synthetic italic so that
        /// a face named Italic and a <c>&lt;i&gt;</c> span do not lean by
        /// different amounts in the same paragraph. Clamped to what the font
        /// actually offers, so a face whose slant stops at -11 gets -10 and one
        /// that stops at -8 gets -8.
        /// </summary>
        public const float ItalicSlant = -10f;

        /// <summary>
        /// The OpenType weight classes, keyed by the word a face name uses for
        /// them, written as one word with the separators already removed.
        ///
        /// Deliberately absent, and each for a reason a project has already been
        /// bitten by: <c>Roman</c>, which means upright and would make
        /// "TimesNewRoman-Bold" a name with two weight words in it;
        /// <c>Text</c>, which is an optical size and not a weight;
        /// <c>Condensed</c>, <c>Expanded</c>, <c>Narrow</c> and <c>Wide</c>,
        /// which are the <c>wdth</c> axis and not this one; and
        /// <c>SemiLight</c>, which is a real weight that the standard gives no
        /// number to — answering 300 from the "Light" half of it would be
        /// inventing one.
        /// </summary>
        private static readonly Dictionary<string, int> Weights = new Dictionary<string, int>
        {
            ["thin"] = 100,
            ["hairline"] = 100,
            ["extralight"] = 200,
            ["ultralight"] = 200,
            ["light"] = 300,
            ["regular"] = 400,
            ["normal"] = 400,
            ["book"] = 400,
            ["medium"] = 500,
            ["semibold"] = 600,
            ["demibold"] = 600,
            ["demi"] = 600,
            ["bold"] = 700,
            ["extrabold"] = 800,
            ["ultrabold"] = 800,
            ["black"] = 900,
            ["heavy"] = 900,
            // usWeightClass runs to 1000 and foundries that ship a face above
            // Black conventionally number it 950. Fonts whose axis stops at 900
            // clamp back down to 900, which is the same face they would have
            // picked anyway.
            ["extrablack"] = 950,
            ["ultrablack"] = 950,
        };

        /// <summary>
        /// Words that are half of a weight and never a weight on their own.
        /// They bind to the word after them, and only when that word is itself a
        /// weight word, so "Extra Bold" is one weight and "Demi Italic" is a
        /// Demi that happens to be italic.
        /// </summary>
        private static readonly HashSet<string> Modifiers = new HashSet<string>
        {
            "extra", "ultra", "semi", "demi",
        };

        /// <summary>
        /// The weight and slope a face name claims, or a guess that admits it
        /// has none.
        ///
        /// The name is split on the same boundaries a font file's name is
        /// written on — hyphens, underscores, spaces, and the capital that
        /// starts a word inside a run-together name — and each word is matched
        /// whole. That is what separates <c>Cairo_Line_Black</c>, which is the
        /// Black of a family called Cairo Line, from <c>Blackout</c>, which is a
        /// family; and it is what stops <c>Boldoni</c> from being a Bold.
        ///
        /// Two rules make it say nothing rather than something. A name with two
        /// weight words in it is a name that has beaten the vocabulary, and a
        /// weight word in first position is part of a family name rather than a
        /// style — styles are written after the family they modify, so
        /// <c>Black Han Sans</c> is a family called Black Han Sans and
        /// <c>Black Han Sans-Bold</c> is its Bold.
        /// </summary>
        public static FontWeightGuess Infer(string faceName)
        {
            var guess = new FontWeightGuess();

            // The same tokeniser the rest of the recovery uses, so that a name
            // splits into the same words here as it does when the family and the
            // style are pulled apart.
            var tokens = FontRecovery.Tokenise(faceName);
            if (tokens.Count == 0) return guess;

            int found = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].ToLowerInvariant();

                if (token == "italic" || token == "oblique")
                {
                    guess.Italic = true;
                    continue;
                }

                // A modifier takes the weight word after it with it, whether or
                // not the pair turns out to be one this table has a number for:
                // "Semi Light" must not fall apart into a Light.
                if (i + 1 < tokens.Count && Modifiers.Contains(token) &&
                    Weights.ContainsKey(tokens[i + 1].ToLowerInvariant()))
                {
                    string joined = token + tokens[i + 1].ToLowerInvariant();
                    string spelled = tokens[i] + tokens[i + 1];
                    bool leading = i == 0;
                    i++;

                    if (leading) continue;
                    if (!Weights.TryGetValue(joined, out int paired)) continue;

                    found++;
                    guess.Weight = paired;
                    guess.WeightName = spelled;
                    continue;
                }

                if (i == 0) continue;
                if (!Weights.TryGetValue(token, out int weight)) continue;

                found++;
                guess.Weight = weight;
                guess.WeightName = tokens[i];
            }

            guess.HasWeight = found == 1;
            if (!guess.HasWeight)
            {
                guess.Weight = 0;
                guess.WeightName = null;
            }
            return guess;
        }

        /// <summary>
        /// The axis settings a guess comes to on a particular font, which is
        /// where an opinion about a name meets a file that may not share it.
        ///
        /// Nothing is set that the font cannot do. A static face has no axes and
        /// gets none of this — it is already the weight it is, and the name
        /// saying "Bold" is the file saying "Bold". A variable face gets its
        /// value clamped into the range the <c>fvar</c> table actually declares,
        /// so a name that says Black on a font that stops at 700 comes out at
        /// 700 rather than at a value HarfBuzz would silently pin there anyway.
        ///
        /// A value that lands on the axis default is dropped rather than
        /// recorded: it is the face the file already opens as, and setting it
        /// would cost every label using it a second font instance and a second
        /// set of atlas tiles for glyphs identical to the ones already there.
        /// </summary>
        public static FontVariation[] Variations(FontWeightGuess guess, IReadOnlyList<FontAxis> axes)
        {
            if (!guess.Found || axes == null || axes.Count == 0)
                return System.Array.Empty<FontVariation>();

            var set = new List<FontVariation>(2);

            if (guess.HasWeight && Axis(axes, "wght", out var weight))
            {
                float value = weight.Clamp(guess.Weight);
                if (value != weight.Default) set.Add(new FontVariation("wght", value));
            }

            if (guess.Italic)
            {
                // `ital` is a switch and `slnt` is an angle; a font offers one
                // or the other, and a font that offers neither is a font whose
                // italic is a separate file this cannot conjure.
                if (Axis(axes, "ital", out var italic))
                {
                    float value = italic.Clamp(1f);
                    if (value != italic.Default) set.Add(new FontVariation("ital", value));
                }
                else if (Axis(axes, "slnt", out var slant))
                {
                    float value = slant.Clamp(ItalicSlant);
                    if (value != slant.Default) set.Add(new FontVariation("slnt", value));
                }
            }

            return set.Count == 0 ? System.Array.Empty<FontVariation>() : set.ToArray();
        }

        /// <summary>Both halves at once: read the name, answer for this font.</summary>
        public static FontVariation[] For(string faceName, IReadOnlyList<FontAxis> axes) =>
            Variations(Infer(faceName), axes);

        /// <summary>
        /// A sentence naming what was set, for the log line and the Hub. Null
        /// when nothing was set, because there is then nothing to say.
        /// </summary>
        public static string Describe(FontVariation[] variations)
        {
            if (variations == null || variations.Length == 0) return null;

            var said = new System.Text.StringBuilder();
            for (int i = 0; i < variations.Length; i++)
            {
                if (i > 0) said.Append(", ");
                said.Append(variations[i].Tag).Append(' ').Append(variations[i].Value.ToString("0.##"));
            }
            return said.ToString();
        }

        private static bool Axis(IReadOnlyList<FontAxis> axes, string tag, out FontAxis found)
        {
            foreach (var axis in axes)
            {
                if (axis.Tag != tag) continue;
                found = axis;
                return true;
            }
            found = default;
            return false;
        }
    }
}
