namespace OneText.Unicode
{
    /// <summary>
    /// Korean postposition selection: 조사.
    ///
    /// Korean particles come in pairs, and which one is correct depends on
    /// whether the preceding syllable ends in a consonant: 사과<b>를</b> but
    /// 수박<b>을</b>. Interpolate an item name into "{item}을" and it is wrong
    /// half the time, so Korean teams write a custom formatter for this in
    /// every project, and a formatter is a C# call, which does not help a
    /// string that arrives from a localisation table.
    ///
    /// So this ships as a markup tag as well: <c>사과&lt;josa=을&gt;</c> resolves
    /// to 를 at parse time by reading the grapheme before it, which means it
    /// works on runtime-interpolated strings with no code at all, inside the
    /// same pipeline as every other tag.
    ///
    /// Either member of a pair is accepted and both resolve to the correct one,
    /// so nobody has to remember which spelling the engine wants.
    /// </summary>
    public static class KoreanJosa
    {
        private const char SyllableFirst = '가'; // 가
        private const char SyllableLast = '힣';  // 힣

        /// <summary>
        /// A particle pair: the form after a consonant (받침 있음) and the form
        /// after a vowel (받침 없음).
        /// </summary>
        private readonly struct Pair
        {
            public readonly string AfterConsonant, AfterVowel;

            public Pair(string afterConsonant, string afterVowel)
            {
                AfterConsonant = afterConsonant;
                AfterVowel = afterVowel;
            }
        }

        private static readonly Pair[] Pairs =
        {
            new Pair("을", "를"),
            new Pair("은", "는"),
            new Pair("이", "가"),
            new Pair("과", "와"),
            new Pair("아", "야"),
            new Pair("이여", "여"),
            new Pair("이라", "라"),
            new Pair("으로", "로"),
            new Pair("으로서", "로서"),
            new Pair("으로써", "로써"),
        };

        /// <summary>True if this is a particle this can resolve, either spelling.</summary>
        public static bool IsJosa(string particle)
        {
            if (string.IsNullOrEmpty(particle)) return false;
            foreach (var pair in Pairs)
                if (particle == pair.AfterConsonant || particle == pair.AfterVowel) return true;
            return false;
        }

        /// <summary>
        /// The correct form of <paramref name="particle"/> after
        /// <paramref name="preceding"/>.
        ///
        /// Returns the particle unchanged when it is not a pair we know or the
        /// preceding text is not Korean; a Latin name before a Korean particle
        /// is a real case, and guessing at it produces something worse than
        /// leaving the author's choice alone.
        /// </summary>
        public static string Resolve(string preceding, string particle)
        {
            if (string.IsNullOrEmpty(particle)) return particle;

            foreach (var pair in Pairs)
            {
                if (particle != pair.AfterConsonant && particle != pair.AfterVowel) continue;
                if (!TryGetFinalConsonant(preceding, out int jongseong)) return particle;

                // (으)로 is the exception every Korean speaker knows and every
                // naive implementation gets wrong: after ㄹ it takes the
                // after-vowel form. 서울로, not 서울으로.
                bool isRo = pair.AfterConsonant.StartsWith("으로", System.StringComparison.Ordinal);
                if (isRo && jongseong == RieulJongseong) return pair.AfterVowel;

                return jongseong != 0 ? pair.AfterConsonant : pair.AfterVowel;
            }
            return particle;
        }

        /// <summary>Index of ㄹ in the jongseong (final consonant) table.</summary>
        private const int RieulJongseong = 8;

        /// <summary>
        /// The final-consonant index of the last Korean syllable in
        /// <paramref name="text"/>: 0 for none, 1-27 for one.
        ///
        /// Hangul syllables are algorithmically composed, so this is arithmetic
        /// rather than a table: the block is laid out as
        /// (initial × 21 + medial) × 28 + final.
        /// </summary>
        public static bool TryGetFinalConsonant(string text, out int jongseong)
        {
            jongseong = 0;
            if (string.IsNullOrEmpty(text)) return false;

            // Trailing punctuation and spaces are skipped: "사과 " before a
            // particle is still 사과, and a closing bracket after a name does
            // not change how the name sounds.
            int i = text.Length - 1;
            while (i >= 0 && !IsSyllable(text[i]) && !IsDigit(text[i]))
            {
                if (!char.IsWhiteSpace(text[i]) && !char.IsPunctuation(text[i])) return false;
                i--;
            }
            if (i < 0) return false;

            if (IsDigit(text[i]))
            {
                // Numbers are read aloud, so the particle follows the sound of
                // the last digit: 1(일) ends in ㄹ, 3(삼) in ㅁ, 6(육) in ㄱ.
                jongseong = DigitJongseong(text[i]);
                return true;
            }

            jongseong = (text[i] - SyllableFirst) % 28;
            return true;
        }

        private static bool IsSyllable(char c) => c >= SyllableFirst && c <= SyllableLast;

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>
        /// Final consonant of a digit's Korean reading. Only whether it has one
        /// matters, except for 1: 일 ends in ㄹ, which the (으)로 exception
        /// needs to know about.
        /// </summary>
        private static int DigitJongseong(char digit) => digit switch
        {
            '0' => IeungJongseong,  // 영
            '1' => RieulJongseong,  // 일
            '3' => MieumJongseong,  // 삼
            '6' => KiyeokJongseong, // 육
            '7' => RieulJongseong,  // 칠
            '8' => RieulJongseong,  // 팔
            _ => 0,                 // 2 이, 4 사, 5 오, 9 구: all end in a vowel
        };

        private const int KiyeokJongseong = 1;  // ㄱ
        private const int MieumJongseong = 16;  // ㅁ
        private const int IeungJongseong = 21;  // ㅇ
    }
}
