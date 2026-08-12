using System;
using System.Collections.Generic;

namespace OneText
{
    /// <summary>
    /// An ordered list of fonts: the first one that has a glyph for a
    /// character wins. Fallback is configured once, here, instead of being
    /// chained per font asset, the pain point this project set out to remove.
    ///
    /// Each entry may also carry bold, italic and bold-italic faces. When it
    /// does not, and the face is variable, <c>&lt;b&gt;</c> and
    /// <c>&lt;i&gt;</c> are served by instancing its axes instead. When it is
    /// neither, they do nothing at all and say so through
    /// <see cref="TryGetStyled"/>; a wrong-looking bold is a bug report, and
    /// a silently ignored one is a question on the forum.
    ///
    /// <para><b>Lifetime.</b> Instanced faces borrow the parent face they were
    /// interpolated from, so the stack outliving its fonts is not a supported
    /// arrangement even with <c>ownsFonts: false</c>: disposing a regular face
    /// while the stack is alive leaves its instanced bold pointing at a
    /// destroyed face. Dispose the stack first. For the same reason,
    /// <see cref="Clear"/> destroys instanced faces (nobody else holds a
    /// reference to create them again), so a <see cref="TextLayoutResult"/>
    /// still holding runs from this stack is stale once it is cleared, exactly
    /// as it would be after the fonts themselves went away.</para>
    /// </summary>
    public sealed class FontStack : IDisposable
    {
        /// <summary>
        /// Which form of a dual-purpose character is wanted, from a variation
        /// selector. U+FE0E asks for the text form, U+FE0F for the emoji one
        /// (✔︎ against ✔️), and the difference is which font in the stack should
        /// get the cluster, not something one font can resolve.
        /// </summary>
        public enum Presentation
        {
            Any,
            Text,
            Emoji,
        }

        /// <summary>Which face of a family a run wants.</summary>
        [Flags]
        public enum Face
        {
            Regular = 0,
            Bold = 1,
            Italic = 2,
            BoldItalic = Bold | Italic,
        }

        private sealed class Entry
        {
            public FontData Regular;

            /// <summary>
            /// BCP 47 tag this family is for, or null for "any". Han
            /// unification means one codepoint is drawn differently in Japanese
            /// and Chinese, and without this the fallback *order* decides which
            /// a reader gets, so a Japanese player sees Chinese glyph shapes
            /// because someone listed the Chinese font first.
            /// </summary>
            public string Language;

            /// <summary>
            /// Letter spacing this family is drawn with when nothing else has
            /// an opinion, in ems. Per family and not per label because a face
            /// that ships too tight is a fact about the face: a run that falls
            /// back to another family mid-line must not inherit the
            /// correction, which is exactly what a label-wide value does.
            /// </summary>
            public float LetterSpacingEm;
            public readonly FontData[] Explicit = new FontData[4];  // indexed by Face
            public readonly FontData[] Instanced = new FontData[4]; // built from variable axes
            public bool[] Attempted = new bool[4];
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<FontData> _fonts = new List<FontData>();
        private readonly Dictionary<int, int> _coverage = new Dictionary<int, int>();
        private readonly bool _ownsFonts;

        /// <summary>
        /// Weight applied when bold is instanced from a `wght` axis. Read once,
        /// when a family's bold is first asked for: changing it afterwards does
        /// not restyle faces already instanced, because their glyphs are
        /// already in the atlas under that face's identity. Set it before the
        /// first draw.
        /// </summary>
        public float BoldWeight { get; set; } = 700f;

        /// <summary>
        /// Slant applied when italic is instanced from a `slnt` axis, in
        /// degrees. Same one-shot rule as <see cref="BoldWeight"/>.
        /// </summary>
        public float ItalicSlant { get; set; } = -10f;

        /// <param name="ownsFonts">
        /// When true the stack disposes its fonts; pass false for fonts whose
        /// lifetime is owned elsewhere. Instanced faces the stack builds itself
        /// are always its own to dispose, either way.
        /// </param>
        public FontStack(bool ownsFonts = false)
        {
            _ownsFonts = ownsFonts;
        }

        public IReadOnlyList<FontData> Fonts => _fonts;

        /// <summary>
        /// The head of the stack, and what draws the box for a character
        /// neither the stack nor the operating system has a glyph for.
        ///
        /// When the stack is empty this is a face from the operating system,
        /// not null. Empty is not the exotic case it reads as: a label whose
        /// font asset was deleted, a migration that could not find a
        /// <c>.ttf</c>, a project with no default font — every one of those
        /// arrives here with nothing in <c>_fonts</c>, and a null Primary is
        /// what every caller downstream treats as "this label does not draw".
        /// The whole point of the system tier is that a reader gets letters
        /// instead of nothing, and a tier that only runs once the project
        /// already supplied a font is not a floor.
        /// </summary>
        public FontData Primary => _fonts.Count > 0 ? _fonts[0] : SystemPrimary;

        // Resolved once and remembered, including the negative: a stack with no
        // fonts is asked for its Primary on every layout pass, and walking the
        // machine's font directories per pass is not a fallback, it is a hang.
        private FontData _systemPrimary;
        private bool _systemPrimarySearched;

        /// <summary>
        /// A face from the operating system to stand at the head of an
        /// otherwise empty stack, or null when there is none.
        ///
        /// Probed with 'A' rather than with the text, because Primary is asked
        /// for before any text is known — the layout engine's own guard reads
        /// it first. What it is used for is metrics for an empty line, the
        /// ASCII fast path, and the notdef box, so a Latin face is the right
        /// answer to all three; every character that is actually drawn is still
        /// resolved on its own through <see cref="ResolveFromSystem"/>, which
        /// is where a Korean string gets a Korean face.
        /// </summary>
        private FontData SystemPrimary
        {
            get
            {
                if (_systemPrimarySearched) return _systemPrimary;
                _systemPrimarySearched = true;
                _systemPrimary = ResolveFromSystem('A');
                return _systemPrimary;
            }
        }

        /// <summary>
        /// True when nothing this stack draws with came from the project: every
        /// glyph is the operating system's, which is a diagnostic worth being
        /// able to ask for rather than inferring from a count of zero.
        /// </summary>
        public bool IsSystemOnly => _fonts.Count == 0 && Primary != null;

        public int Count => _fonts.Count;

        public void Add(FontData font) => Add(font, null, null, null);

        /// <summary>
        /// Adds a family for a specific language. When a label names the same
        /// language, this family wins over anything earlier in the stack that
        /// merely covers the character, which is how a Japanese label gets 直
        /// from the Japanese font with a Chinese font sitting above it.
        /// </summary>
        public void Add(FontData font, string language) => Add(font, language, 0f);

        /// <summary>
        /// A family with the designed bold that goes with it, plus the language
        /// and spacing the other overload takes.
        ///
        /// The bold is a separate file rather than an axis because a static
        /// font has no axis to move: a project shipping Pretendard.ttf gets its
        /// bold interpolated and needs nothing here, and one shipping
        /// NotoSans-Regular.ttf and NotoSans-Bold.ttf has two files and no way
        /// to say they are the same family until this is filled in.
        /// </summary>
        public void Add(FontData regular, FontData bold, string language, float letterSpacingEm)
        {
            int before = _entries.Count;
            Add(regular, bold, null, null);
            if (_entries.Count <= before) return;
            var entry = _entries[_entries.Count - 1];
            entry.Language = language;
            entry.LetterSpacingEm = letterSpacingEm;
        }

        /// <summary>
        /// Same, with the spacing correction this face is drawn with when
        /// neither markup nor a style nor the label says otherwise. See
        /// <see cref="LetterSpacingOf"/>.
        /// </summary>
        public void Add(FontData font, string language, float letterSpacingEm)
        {
            int before = _entries.Count;
            Add(font, null, null, null);
            // Only if the font was actually accepted: stamping the language on
            // whatever happened to be last would relabel someone else's family.
            if (_entries.Count <= before) return;
            var entry = _entries[_entries.Count - 1];
            entry.Language = language;
            entry.LetterSpacingEm = letterSpacingEm;
        }

        /// <summary>
        /// Adds a family: a regular face and, optionally, the real bold and
        /// italic faces that go with it. Real faces always beat instanced ones:
        /// a designed bold is not an interpolated one.
        /// </summary>
        public void Add(FontData regular, FontData bold, FontData italic, FontData boldItalic)
        {
            if (regular == null || !regular.IsValid) return;
            var entry = new Entry { Regular = regular };
            entry.Explicit[(int)Face.Bold] = Valid(bold);
            entry.Explicit[(int)Face.Italic] = Valid(italic);
            entry.Explicit[(int)Face.BoldItalic] = Valid(boldItalic);
            _entries.Add(entry);
            _fonts.Add(regular);
            _coverage.Clear();
            // A font that arrives after a character was answered by the
            // operating system may well cover it; the project's own font wins.
            _system?.Clear();
            // And the head of the stack is now a real font rather than the
            // system face that was standing in for one.
            _systemPrimary = null;
            _systemPrimarySearched = false;
        }

        private static FontData Valid(FontData font) => font != null && font.IsValid ? font : null;

        public void Clear()
        {
            foreach (var entry in _entries)
            {
                // Instanced faces belong to the stack whatever ownsFonts says:
                // nobody else has a reference to them.
                foreach (var instanced in entry.Instanced) instanced?.Dispose();
                if (!_ownsFonts) continue;
                entry.Regular?.Dispose();
                foreach (var face in entry.Explicit) face?.Dispose();
            }
            _entries.Clear();
            _fonts.Clear();
            _coverage.Clear();
            // Not disposed: system faces are shared process-wide and belong to
            // SystemFonts, not to whichever stack happened to ask for one. The
            // stand-in Primary is one of those and goes the same way.
            _system?.Clear();
            _systemPrimary = null;
            _systemPrimarySearched = false;
        }

        /// <summary>
        /// Throws away the bold and italic faces instanced from
        /// <paramref name="regular"/>'s axes, so the next request builds them
        /// from where its axes are now.
        ///
        /// For a caller that moved a face's axes underneath the stack — a
        /// variable-font slider on a label that owns its face outright. The
        /// instanced faces were built by laying bold or slant over whatever the
        /// regular's coordinate was at the time, and left alone they would go
        /// on drawing a <c>&lt;b&gt;</c> span at the weight the label had two
        /// drags ago.
        /// </summary>
        public void DropStyledInstances(FontData regular)
        {
            var entry = FindEntry(regular);
            if (entry == null) return;
            for (int i = 0; i < entry.Instanced.Length; i++)
            {
                entry.Instanced[i]?.Dispose();
                entry.Instanced[i] = null;
                entry.Attempted[i] = false;
            }
        }

        /// <summary>
        /// The first font covering <paramref name="codepoint"/>; failing that,
        /// a font the operating system has for it; failing that,
        /// <see cref="Primary"/>, so the caller still gets notdef boxes rather
        /// than nothing.
        ///
        /// The system tier is <see cref="SystemFonts"/> and is on unless the
        /// project turns it off. It is a last resort in the literal sense:
        /// every font the project actually ships has already said no.
        /// </summary>
        public FontData Resolve(int codepoint)
        {
            // No early return on an empty stack. It used to be here, and it is
            // the reason a label with no font drew nothing on a machine full of
            // fonts: the system tier below was written as the last rung of a
            // chain rather than as the floor under it, so the one case that
            // needed it most — no chain at all — never reached it.
            if (!_coverage.TryGetValue(codepoint, out int index))
            {
                // -1 rather than 0 for "nobody has it": the two answers used to
                // be the same value, and the system tier below needs to tell
                // them apart. Both are cached, so a character that misses the
                // whole chain asks the fonts once and not once per occurrence.
                index = -1;
                for (int i = 0; i < _fonts.Count; i++)
                {
                    if (_fonts[i].HasGlyph(codepoint)) { index = i; break; }
                }
                _coverage[codepoint] = index;
            }
            if (index >= 0) return _fonts[index];
            return ResolveFromSystem(codepoint) ?? Primary;
        }

        // Characters the chain missed and the operating system answered for.
        // Cached here as well as in SystemFonts so the steady state is one
        // dictionary lookup on this side of the lock.
        private Dictionary<int, FontData> _system;

        /// <summary>
        /// The operating system's answer for a character no font in this stack
        /// covers, or null: when the tier is switched off, when the platform
        /// has no font directory to walk, or when nothing on the machine has
        /// the character either.
        ///
        /// Public because a diagnostic has to be able to ask the same question
        /// the renderer asked, and answer it the same way: Doctor reports a
        /// character that only draws because of this, and it needs to name the
        /// face that caught it.
        /// </summary>
        public FontData ResolveFromSystem(int codepoint)
        {
            if (!SystemFonts.Enabled) return null;
            _system ??= new Dictionary<int, FontData>();
            if (_system.TryGetValue(codepoint, out var cached)) return cached;
            var font = SystemFonts.Resolve(codepoint);
            _system[codepoint] = font;
            return font;
        }

        /// <summary>
        /// Same, in the bold and/or italic face of whichever family covers the
        /// character. Coverage is decided on the regular face: a family that
        /// can draw a character in regular is the family that should draw it in
        /// bold, even if its bold face is missing a glyph or two.
        /// </summary>
        public FontData Resolve(int codepoint, bool bold, bool italic) =>
            Resolve(codepoint, bold, italic, Presentation.Any);

        /// <summary>
        /// Same, honouring a variation selector: the first font that covers the
        /// character <em>and</em> matches the requested presentation wins, and
        /// the plain coverage answer is the fallback when none does. Asking for
        /// the emoji form of a character no colour font has should still draw
        /// the character.
        /// </summary>
        public FontData Resolve(int codepoint, bool bold, bool italic, Presentation presentation) =>
            Resolve(codepoint, bold, italic, presentation, null);

        /// <summary>
        /// Same, keyed by language.
        ///
        /// This is the Han unification fix. 直 is one codepoint with different
        /// correct shapes in Japanese and Chinese, and with a plain
        /// first-font-that-covers-it walk, which one a reader sees depends on
        /// the order somebody happened to list the fallbacks in. A label that
        /// says it is Japanese gets the Japanese font.
        /// </summary>
        public FontData Resolve(int codepoint, bool bold, bool italic, Presentation presentation,
            string language)
        {
            var regular = ResolveForLanguage(codepoint, language);
            if (regular == null)
            {
                regular = presentation == Presentation.Any
                    ? Resolve(codepoint)
                    : ResolveForPresentation(codepoint, presentation);
            }
            if (!bold && !italic) return regular;

            var face = (bold ? Face.Bold : 0) | (italic ? Face.Italic : 0);
            return TryGetStyled(regular, face, out var styled) ? styled : regular;
        }

        /// <summary>
        /// Whether this family can produce a bold face at all — a designed one
        /// from a second file, or an instance off a <c>wght</c> axis.
        ///
        /// Asked of the family rather than of the face that came back, because
        /// bold-italic falls back to whichever half exists: a run that got the
        /// italic and no bold has to be able to find that out, or it is drawn
        /// slanted at the regular weight and nobody can see why.
        ///
        /// Answering it is as far as this class goes. Faking the weight is the
        /// caller's decision and the caller's business: a designed or
        /// interpolated bold is a real face and belongs in the stack, and a
        /// threshold pushed outward in a shader belongs where the drawing
        /// happens. Cheap after the first ask per family — <see cref="TryGetStyled"/>
        /// caches the instance it built and the fact that it could not build one.
        /// </summary>
        public bool HasBold(FontData font)
        {
            var regular = Family(font);
            return regular != null && TryGetStyled(regular, Face.Bold, out var bold) &&
                   bold != regular;
        }

        /// <summary>
        /// The regular face of the family a font belongs to, or the font itself
        /// when it is not one this stack knows.
        ///
        /// A resolved font is often a styled face rather than a family's
        /// regular — the italic instance of a bold-italic run that found only
        /// the italic — and asking that face whether it has a bold gets the
        /// wrong answer, because an entry is keyed on its regular.
        /// </summary>
        private FontData Family(FontData font)
        {
            if (font == null) return null;
            foreach (var entry in _entries)
            {
                if (entry.Regular == font) return entry.Regular;
                foreach (var face in entry.Explicit) if (face == font) return entry.Regular;
                foreach (var face in entry.Instanced) if (face == font) return entry.Regular;
            }
            return font;
        }

        /// <summary>
        /// The language tag a font was added under, or null.
        ///
        /// Diagnostics ask this: the Hub's forensics to say which family drew
        /// a glyph and why, Doctor to notice a Japanese string resolving
        /// through an untagged chain, which is Han unification going wrong
        /// quietly rather than loudly.
        /// </summary>
        public string LanguageOf(FontData font)
        {
            if (font == null) return null;
            foreach (var entry in _entries)
            {
                if (entry.Regular == font) return entry.Language;
                foreach (var face in entry.Explicit) if (face == font) return entry.Language;
                foreach (var face in entry.Instanced) if (face == font) return entry.Language;
            }
            return null;
        }

        /// <summary>
        /// The spacing correction a face is drawn with, in ems, or 0.
        ///
        /// Asked per run rather than per glyph, and answered for the family
        /// rather than the face: a bold or instanced face is the same design
        /// with the same metrics problem, so it gets the same correction. A
        /// face the operating system supplied is in no entry and gets 0, which
        /// is the right answer — the project never said anything about it.
        /// </summary>
        public float LetterSpacingOf(FontData font)
        {
            if (font == null) return 0f;
            foreach (var entry in _entries)
            {
                if (entry.LetterSpacingEm == 0f) continue;
                if (entry.Regular == font) return entry.LetterSpacingEm;
                foreach (var face in entry.Explicit) if (face == font) return entry.LetterSpacingEm;
                foreach (var face in entry.Instanced) if (face == font) return entry.LetterSpacingEm;
            }
            return 0f;
        }

        /// <summary>True if any font in the stack can draw this character.</summary>
        public bool Covers(int codepoint)
        {
            foreach (var font in _fonts)
                if (font.HasGlyph(codepoint)) return true;
            return false;
        }

        /// <summary>
        /// The first family declared for this language that covers the
        /// character, or null when none is, in which case ordinary coverage
        /// order decides, which is the right answer for a character the locale
        /// has no opinion about.
        /// </summary>
        private FontData ResolveForLanguage(int codepoint, string language)
        {
            if (string.IsNullOrEmpty(language)) return null;

            // Only where the locale actually has an opinion. A CJK font tagged
            // "ja" covers ASCII too, so letting the language decide every
            // codepoint would silently move a Japanese label's Latin text,
            // digits and punctuation into the CJK face, a whole-label font
            // swap dressed up as a Han-unification fix. Han and kana are the
            // characters whose correct shape depends on the reader.
            if (codepoint > char.MaxValue ||
                !Unicode.AsianTypography.IsIdeographic((char)codepoint)) return null;

            foreach (var entry in _entries)
            {
                if (entry.Language == null || !LanguageMatches(entry.Language, language)) continue;
                if (entry.Regular.HasGlyph(codepoint)) return entry.Regular;
            }
            return null;
        }

        /// <summary>
        /// Prefix matching on the primary subtag: a font declared "zh" serves
        /// "zh-Hans", and one declared "zh-Hant" does not serve "zh-Hans".
        /// Full BCP 47 negotiation is a library; this is the part that matters.
        /// </summary>
        private static bool LanguageMatches(string fontLanguage, string wanted)
        {
            if (string.Equals(fontLanguage, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return wanted.Length > fontLanguage.Length &&
                   wanted.StartsWith(fontLanguage, StringComparison.OrdinalIgnoreCase) &&
                   wanted[fontLanguage.Length] == '-';
        }

        private FontData ResolveForPresentation(int codepoint, Presentation presentation)
        {
            bool wantColor = presentation == Presentation.Emoji;
            foreach (var font in _fonts)
            {
                if (!font.HasGlyph(codepoint)) continue;
                if (ColorGlyphs.IsColorFont(font) == wantColor) return font;
            }
            // Nothing in the stack can honour the request; drawing the
            // character in the wrong presentation beats drawing tofu.
            return Resolve(codepoint);
        }

        /// <summary>
        /// The styled face for a family, if one can be had. False means the
        /// family has neither a real face nor the axes to instance one; the
        /// caller gets the regular face and may want to say so.
        /// </summary>
        public bool TryGetStyled(FontData regular, Face face, out FontData styled)
        {
            styled = regular;
            if (face == Face.Regular || regular == null) return true;

            var entry = FindEntry(regular);
            if (entry == null) return false;

            var wanted = entry.Explicit[(int)face];
            if (wanted != null) { styled = wanted; return true; }

            // Bold-italic falls back to whichever half exists before giving up.
            if (face == Face.BoldItalic)
            {
                var half = entry.Explicit[(int)Face.Bold] ?? entry.Explicit[(int)Face.Italic];
                if (half != null) { styled = half; return true; }
            }

            if (entry.Instanced[(int)face] != null) { styled = entry.Instanced[(int)face]; return true; }
            if (entry.Attempted[(int)face]) return false;
            entry.Attempted[(int)face] = true;

            var instanced = Instance(entry.Regular, face);
            if (instanced == null) return false;
            entry.Instanced[(int)face] = instanced;
            styled = instanced;
            return true;
        }

        // Linear: a stack is two or three families, and this runs once per
        // styled family per style, not per character.
        private Entry FindEntry(FontData regular)
        {
            foreach (var entry in _entries)
                if (entry.Regular == regular) return entry;
            return null;
        }

        /// <summary>
        /// Builds a styled instance from a variable font's axes, or returns
        /// null when the font has none to move.
        /// </summary>
        private FontData Instance(FontData regular, Face face)
        {
            if (!regular.IsVariable) return null;

            bool hasWeight = false, hasSlant = false, hasItalic = false;
            foreach (var axis in regular.GetVariationAxes())
            {
                if (axis.Tag == "wght") hasWeight = true;
                else if (axis.Tag == "slnt") hasSlant = true;
                else if (axis.Tag == "ital") hasItalic = true;
            }

            var variations = new List<FontVariation>(2);
            if ((face & Face.Bold) != 0 && hasWeight) variations.Add(new FontVariation("wght", BoldWeight));
            if ((face & Face.Italic) != 0)
            {
                // `ital` is a switch, `slnt` is an angle; a font offers one or
                // the other, effectively never both.
                if (hasItalic) variations.Add(new FontVariation("ital", 1f));
                else if (hasSlant) variations.Add(new FontVariation("slnt", ItalicSlant));
            }

            // Nothing moved means nothing to instance; say so rather than
            // hand back a duplicate of the regular face, which would cost an
            // hb_font and a second set of atlas tiles for identical glyphs.
            return variations.Count == 0 ? null : regular.CreateVariant(variations.ToArray());
        }

        /// <summary>Convenience: a stack holding a single font.</summary>
        public static FontStack Single(FontData font)
        {
            var stack = new FontStack();
            stack.Add(font);
            return stack;
        }

        public void Dispose()
        {
            Clear();
            GC.SuppressFinalize(this);
        }
    }
}
