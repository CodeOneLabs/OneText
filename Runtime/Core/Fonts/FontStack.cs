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
        /// </summary>
        public FontData Primary => _fonts.Count > 0 ? _fonts[0] : null;

        public int Count => _fonts.Count;

        public void Add(FontData font) => Add(font, null, null, null);

        /// <summary>
        /// Adds a family for a specific language. When a label names the same
        /// language, this family wins over anything earlier in the stack that
        /// merely covers the character, which is how a Japanese label gets 直
        /// from the Japanese font with a Chinese font sitting above it.
        /// </summary>
        public void Add(FontData font, string language)
        {
            int before = _entries.Count;
            Add(font, null, null, null);
            // Only if the font was actually accepted: stamping the language on
            // whatever happened to be last would relabel someone else's family.
            if (_entries.Count > before) _entries[_entries.Count - 1].Language = language;
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
            // SystemFonts, not to whichever stack happened to ask for one.
            _system?.Clear();
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
            if (_fonts.Count == 0) return null;
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
