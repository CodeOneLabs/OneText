using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Everything a parsed rich-text string produces: the text that actually
    /// gets laid out, the style spans over it, the clickable ranges, and the
    /// per-paragraph alignment overrides.
    /// </summary>
    public sealed class RichTextResult
    {
        /// <summary>
        /// The text with markup removed: what indices refer to.
        ///
        /// Held as a buffer this result owns and turned into a string only when
        /// something asks for one. A label lays out the span and never asks;
        /// the string exists for callers (an input field's model, a caret's
        /// hit test, anyone reading DisplayText) and is built once per parse.
        /// </summary>
        public string Text
        {
            get => _text ??= Length == 0 ? string.Empty : new string(Buffer, 0, Length);
            set
            {
                _text = value ?? string.Empty;
                Length = _text.Length;
                if (Buffer == null || Buffer.Length < Length) Buffer = new char[Mathf.Max(16, Length)];
                _text.CopyTo(0, Buffer, 0, Length);
            }
        }

        /// <summary>The same text without building a string for it.</summary>
        public ReadOnlySpan<char> TextSpan => new ReadOnlySpan<char>(Buffer, 0, Length);

        /// <summary>The buffer behind <see cref="TextSpan"/>, grown by the parser.</summary>
        internal char[] Buffer = new char[64];

        /// <summary>How much of <see cref="Buffer"/> is text: its length in chars.</summary>
        public int Length;

        private string _text = string.Empty;

        /// <summary>Called by the parser when it has written a new text.</summary>
        internal void TextWritten() => _text = null;

        /// <summary>
        /// Holds text that has no markup in it, as one default-styled span.
        /// What the parser's own early-out does, for a caller that already
        /// knows there is nothing to parse.
        /// </summary>
        public void SetPlain(ReadOnlySpan<char> text)
        {
            Clear();
            if (Buffer.Length < text.Length)
                Buffer = new char[Mathf.Max(16, Mathf.NextPowerOfTwo(text.Length))];
            text.CopyTo(Buffer);
            Length = text.Length;
            TextWritten();
            Spans.Add(new TextStyleSpan(0, text.Length, TextStyle.Default));
        }

        /// <summary>Contiguous, non-overlapping, covering the whole of <see cref="Text"/>.</summary>
        public readonly List<TextStyleSpan> Spans = new List<TextStyleSpan>();

        /// <summary><c>&lt;link=id&gt;</c> ranges, in <see cref="Text"/> indices.</summary>
        public readonly List<TextLink> Links = new List<TextLink>();

        /// <summary>
        /// Alignment overrides from <c>&lt;align&gt;</c>, as (start index,
        /// alignment). Alignment is a property of a line, not of a character,
        /// so it is kept out of <see cref="TextStyle"/>: putting it there would
        /// split runs for something no run cares about.
        /// </summary>
        public readonly List<(int Start, TextAlignment Alignment)> Alignments =
            new List<(int, TextAlignment)>();

        /// <summary>True if any markup was actually applied.</summary>
        public bool HasMarkup;

        /// <summary>
        /// Effect tags, as (name, parameters, text range). Effects are kept out
        /// of <see cref="TextStyle"/> for the same reason alignment is: they do
        /// not change how text is laid out, so making them split runs would
        /// re-shape the text for something only the vertex pass reads.
        /// </summary>
        public readonly List<(string Name, TextEffectParameters Parameters, int Start, int End)>
            Effects = new List<(string, TextEffectParameters, int, int)>();

        /// <summary>
        /// <c>&lt;outline&gt;</c>, <c>&lt;shadow&gt;</c> and <c>&lt;glow&gt;</c>
        /// ranges, in the order they were opened. Out of
        /// <see cref="TextStyle"/> for the reason effects are: a decoration
        /// changes no advance and no line break, so putting it there would
        /// re-shape the text to say something only the fragment shader reads.
        ///
        /// Spans may overlap and nest; <see cref="DecorationAt"/> is what
        /// resolves them, so an outline written around a shadow gives both.
        /// </summary>
        public readonly List<TextDecorationSpan> Decorations = new List<TextDecorationSpan>();

        /// <summary>
        /// <c>&lt;wait=0.5&gt;</c> pauses, as (index into <see cref="Text"/>,
        /// seconds), in the order they were written.
        ///
        /// A point, not a range, which is why it is the one typewriter control
        /// that has to be markup: everything else about a reveal (how fast,
        /// what a step is, how long to hold after a full stop) is project
        /// policy that belongs on the label, but "pause HERE, in this
        /// sentence" has nowhere else it can be said.
        /// </summary>
        public readonly List<(int Index, float Seconds)> Waits = new List<(int, float)>();

        /// <summary>
        /// <c>&lt;ruby=ふりがな&gt;漢字&lt;/ruby&gt;</c> annotations, in
        /// <see cref="Text"/> indices.
        ///
        /// Out of <see cref="Text"/> and out of <see cref="TextStyle"/> both:
        /// the annotation is a second string the layout engine shapes on its
        /// own, and the base is ordinary text that keeps its ordinary indices.
        /// </summary>
        public readonly List<TextRubySpan> Rubies = new List<TextRubySpan>();

        public void Clear()
        {
            Length = 0;
            TextWritten();
            Spans.Clear();
            Links.Clear();
            Alignments.Clear();
            Effects.Clear();
            Decorations.Clear();
            Waits.Clear();
            Rubies.Clear();
            HasMarkup = false;
        }

        /// <summary>
        /// The decoration in force at <paramref name="index"/>: every span
        /// covering it, laid over each other in the order they were opened, so
        /// the innermost tag wins the parts it sets and the outer ones keep the
        /// parts it does not.
        /// </summary>
        public TextDecoration DecorationAt(int index)
        {
            var decoration = TextDecoration.None;
            for (int i = 0; i < Decorations.Count; i++)
            {
                if (!Decorations[i].Covers(index)) continue;
                decoration = Decorations[i].Decoration.Over(decoration);
            }
            return decoration;
        }

        /// <summary>The style covering <paramref name="index"/>.</summary>
        public TextStyle StyleAt(int index)
        {
            // Spans are ordered and contiguous, so a binary search is exact.
            int lo = 0, hi = Spans.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (index < Spans[mid].Start) hi = mid - 1;
                else if (index >= Spans[mid].End) lo = mid + 1;
                else return Spans[mid].Style;
            }
            return TextStyle.Default;
        }

        /// <summary>The alignment in force at <paramref name="index"/>, if any.</summary>
        public bool TryGetAlignment(int index, out TextAlignment alignment)
        {
            alignment = default;
            bool found = false;
            foreach (var entry in Alignments)
            {
                if (entry.Start > index) break;
                if ((int)entry.Alignment < 0) { found = false; continue; } // </align>
                alignment = entry.Alignment;
                found = true;
            }
            return found;
        }
    }

    /// <summary>
    /// Turns markup into text plus styles.
    ///
    /// The rule that decides every ambiguous case: <b>a tag that is not
    /// well-formed stays in the text, verbatim</b>. A stray '&lt;' is far more
    /// often a less-than sign than a broken tag, and text that silently
    /// disappears is the worst failure a text engine has. So parsing a tag is
    /// all-or-nothing: name, argument and closing '&gt;' all have to be there
    /// and make sense, or nothing is consumed and the '&lt;' is just a
    /// character.
    ///
    /// Nesting is a stack, and a close tag that does not match the top of the
    /// stack is also left literal rather than guessed at. `&lt;/&gt;` closes
    /// whatever is open, which is the one convenience worth having.
    /// </summary>
    public static class RichTextParser
    {
        /// <summary>Cheap pre-check: no '&lt;' means nothing to do.</summary>
        public static bool MightHaveMarkup(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf('<') >= 0;

        /// <inheritdoc cref="MightHaveMarkup(string)"/>
        public static bool MightHaveMarkup(ReadOnlySpan<char> text) =>
            text.IndexOf('<') >= 0;

        /// <summary>Placeholder character standing in for an inline sprite.</summary>
        public const char SpritePlaceholder = '￼'; // OBJECT REPLACEMENT CHARACTER

        private struct Open
        {
            public string Name;
            public TextStyle Previous;        // style to restore when this closes
            public int LinkStart;             // link only
            public string LinkId;             // link only
            public bool HadAlignment;         // align only: was one in force before?
            public TextAlignment PreviousAlignment;
            public TextEffectParameters EffectParameters; // effect only
            public int EffectStart;
            public int DecorationIndex;       // decoration tags only
            public int RubyStart;             // ruby only
            public string RubyText;
        }

        public static void Parse(string source, RichTextResult result) =>
            Parse(source.AsSpan(), result, null, null);

        /// <inheritdoc cref="Parse(string, RichTextResult)"/>
        public static void Parse(ReadOnlySpan<char> source, RichTextResult result) =>
            Parse(source, result, null, null);

        /// <summary>
        /// Parses <paramref name="source"/> into <paramref name="result"/>.
        ///
        /// <paramref name="styleNames"/> and <paramref name="fontNames"/> map
        /// the arguments of <c>&lt;style=…&gt;</c> and <c>&lt;font=…&gt;</c> to
        /// indices; either may be null, in which case those tags do not parse
        /// and stay literal, which is the honest outcome, because a style the
        /// label cannot resolve is not a style.
        /// </summary>
        public static void Parse(string source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames) =>
            Parse(source.AsSpan(), result, styleNames, fontNames, null);

        /// <inheritdoc cref="Parse(string, RichTextResult, Func{string, int}, Func{string, int})"/>
        public static void Parse(ReadOnlySpan<char> source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames) =>
            Parse(source, result, styleNames, fontNames, null);

        /// <summary>
        /// Same, with a resolver for <c>&lt;sprite=name&gt;</c>. An index still
        /// works; a name is what survives someone reordering the sheet.
        /// </summary>
        /// <summary>
        /// The text being built, and the open-tag stack, reused between calls.
        ///
        /// A parse used to allocate a StringBuilder, a list and a string per
        /// tag; on a label whose markup changes every frame that is about two
        /// kilobytes a rebuild, which is the largest thing a warmed steady
        /// state allocated. A char buffer rather than a StringBuilder because
        /// the josa resolver and the tag readers want to look at what has been
        /// written as a span, and a StringBuilder cannot show them one.
        ///
        /// Thread-static rather than plain static: layout is single-threaded
        /// today, and a field that quietly stops being safe when it is not is
        /// worse than one that was never shared.
        /// </summary>
        [ThreadStatic] private static List<Open> s_stack;

        /// <summary>
        /// The result being written to. A field rather than a parameter because
        /// every Write below would otherwise carry it, and the parser is not
        /// reentrant either way: the name resolvers it calls out to look names
        /// up in tables, they do not parse.
        /// </summary>
        [ThreadStatic] private static RichTextResult s_result;

        private static void Write(char value)
        {
            var result = s_result;
            if (result.Length == result.Buffer.Length)
                Array.Resize(ref result.Buffer, result.Buffer.Length * 2);
            result.Buffer[result.Length++] = value;
        }

        private static void Write(ReadOnlySpan<char> value)
        {
            var result = s_result;
            int needed = result.Length + value.Length;
            if (needed > result.Buffer.Length)
            {
                int size = result.Buffer.Length;
                while (size < needed) size *= 2;
                Array.Resize(ref result.Buffer, size);
            }
            value.CopyTo(new Span<char>(result.Buffer, result.Length, value.Length));
            result.Length += value.Length;
        }

        private static void Write(string source, int start, int count) =>
            Write(source.AsSpan(start, count));

        /// <summary>
        /// The same, from a string. Everything inside works on the span, so a
        /// caller that already has characters does not have to make one.
        /// </summary>
        public static void Parse(string source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames, Func<string, int> spriteNames) =>
            Parse(source.AsSpan(), result, styleNames, fontNames, spriteNames);

        /// <inheritdoc cref="Parse(string, RichTextResult, Func{string, int}, Func{string, int}, Func{string, int})"/>
        public static void Parse(ReadOnlySpan<char> source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames, Func<string, int> spriteNames)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.Clear();
            if (source.IsEmpty)
            {
                result.Spans.Add(new TextStyleSpan(0, 0, TextStyle.Default));
                return;
            }

            if (!MightHaveMarkup(source))
            {
                result.SetPlain(source);
                return;
            }

            var stack = s_stack ??= new List<Open>();
            stack.Clear();
            s_result = result;
            var style = TextStyle.Default;
            var spanStart = 0;
            var spanStyle = style;

            for (int i = 0; i < source.Length;)
            {
                if (source[i] != '<')
                {
                    Write(source[i++]);
                    continue;
                }

                if (!TryReadTag(source, i, out string name, out int argStart, out int argLength,
                        out bool hasArgument, out bool closing, out int after))
                {
                    Write(source[i++]);
                    continue;
                }

                // <noparse> is settled here rather than in ApplyOpen, because it
                // is not a style: it is an instruction about how to read what
                // comes next, and the only place that can obey it is the loop
                // doing the reading. Everything up to </noparse> is copied out
                // character for character, tags and all, which is the whole
                // point — it is what a chat line or a player's name has to be
                // shown through if a user typing <size=500> is not to resize the
                // window.
                if (!closing && !hasArgument && name == "noparse")
                {
                    result.HasMarkup = true;
                    int literalStart = after;
                    int end = source.Slice(literalStart).IndexOf("</noparse>".AsSpan(),
                        StringComparison.OrdinalIgnoreCase);
                    if (end >= 0) end += literalStart;

                    // Unterminated, so the rest of the text is literal. The
                    // house rule everywhere else is that unclosed markup runs to
                    // the end, and this is that rule with nothing to close.
                    if (end < 0) end = source.Length;
                    Write(source.Slice(literalStart, end - literalStart));
                    i = end < source.Length ? end + "</noparse>".Length : source.Length;
                    continue;
                }

                // <br> writes a character and changes no style, so it belongs
                // here beside <noparse> rather than in the style switch. It
                // exists for the text a project does not get to type: a
                // localisation table cell, a CSV column, an XML attribute —
                // places where a literal newline is somebody else's escaping
                // problem and a tag is not.
                if (!closing && !hasArgument && name == "br")
                {
                    result.HasMarkup = true;
                    Write('\n');
                    i = after;
                    continue;
                }

                var next = style;
                int sprite = -1;
                string josa = null;
                bool consumed;
                if (closing)
                    consumed = ApplyClose(name, stack, result, s_result.Length, ref next);
                else
                    consumed = ApplyOpen(name, source, argStart, argLength, hasArgument, stack,
                        result, s_result.Length, style,
                        styleNames, fontNames, spriteNames, ref next, ref sprite, ref josa);

                if (!consumed)
                {
                    // Not a tag we understand, or not a well-formed use of one.
                    Write(source[i++]);
                    continue;
                }

                result.HasMarkup = true;

                if (josa != null)
                {
                    // Written straight into the output, styled like the text it
                    // follows: a particle is part of the sentence, not a tag
                    // that leaves a mark.
                    // Against the text written so far, read as a span: the
                    // resolver only walks back to the last syllable, and
                    // materialising the whole paragraph to show it one
                    // character was the second-largest allocation here.
                    Write(Unicode.KoreanJosa.Resolve(result.TextSpan, josa));
                    i = after;
                    continue;
                }

                if (!next.Equals(spanStyle))
                {
                    if (s_result.Length > spanStart)
                        result.Spans.Add(new TextStyleSpan(spanStart, s_result.Length - spanStart, spanStyle));
                    spanStart = s_result.Length;
                    spanStyle = next;
                }

                if (sprite >= 0)
                {
                    // A sprite is one placeholder character in a span of its
                    // own: it takes an index so carets, selection and reveal can
                    // count it, and it is not a pair, so the span opens and
                    // closes here rather than going on the stack.
                    if (s_result.Length > spanStart)
                        result.Spans.Add(new TextStyleSpan(spanStart, s_result.Length - spanStart, spanStyle));
                    var spriteStyle = next;
                    spriteStyle.Sprite = sprite;
                    spriteStyle.Flags |= TextStyle.Flag.Sprite;
                    Write(SpritePlaceholder);
                    result.Spans.Add(new TextStyleSpan(s_result.Length - 1, 1, spriteStyle));
                    spanStart = s_result.Length;
                }

                style = next;
                i = after;
            }

            if (s_result.Length > spanStart || result.Spans.Count == 0)
                result.Spans.Add(new TextStyleSpan(spanStart, s_result.Length - spanStart, spanStyle));

            // Anything still open at the end closes at the end. Unterminated
            // markup styling the rest of the text is what every other engine
            // does, and it is far less surprising than dropping the style.
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].Name == "link")
                    result.Links.Add(new TextLink(stack[i].LinkId, stack[i].LinkStart,
                        s_result.Length - stack[i].LinkStart));
                else if (stack[i].Name == "ruby")
                    CloseRuby(result, stack[i], s_result.Length);
                else if (IsDecoration(stack[i].Name))
                    CloseDecoration(result, stack[i].DecorationIndex, s_result.Length);
                else if (BuiltInEffects.Has(stack[i].Name))
                    result.Effects.Add((stack[i].Name, stack[i].EffectParameters,
                        stack[i].EffectStart, s_result.Length));
            }

            // Written, not built: the string behind it is made only if someone
            // asks for one.
            result.TextWritten();
        }

        /// <summary>
        /// Recorded by <c>&lt;/align&gt;</c> when nothing was overriding
        /// alignment before it: "go back to whatever the label says", which is
        /// not any particular alignment.
        /// </summary>
        internal const TextAlignment DefaultAlignmentMarker = (TextAlignment)(-1);

        // --------------------------------------------------------------- tags

        /// <summary>
        /// Reads a tag at <paramref name="at"/>. Returns false (consuming
        /// nothing) unless the whole thing is well formed.
        /// </summary>
        /// <summary>
        /// Every tag name this parser answers to, as the exact string instances
        /// the switch below compares against.
        ///
        /// A name used to be cut out of the source and lower-cased, which is
        /// two strings per tag, thrown away a frame later. Matching the source
        /// against these instead costs a case-insensitive span compare and
        /// hands the switch a string it can compare by reference.
        /// </summary>
        private static readonly string[] KnownNames =
        {
            "b", "i", "u", "s", "nobr", "color", "mark", "size", "voffset", "cspace", "mspace",
            "sup", "sub", "alpha", "align", "style", "font", "outline", "shadow", "glow",
            "ruby", "link", "josa", "wait", "sprite", "noparse", "br",
        };

        /// <summary>
        /// The canonical instance of the tag name at <paramref name="start"/>,
        /// or a fresh lower-cased string when nothing recognises it — which is
        /// a tag that stays literal, so the allocation is on a path that is
        /// already the exceptional one.
        /// </summary>
        private static string CanonicalName(ReadOnlySpan<char> s, int start, int length)
        {
            var span = s.Slice(start, length);
            foreach (string candidate in KnownNames)
                if (span.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return candidate;

            // Effects are registered rather than listed, so a project can add
            // one from user code and have markup find it.
            string effect = BuiltInEffects.CanonicalName(span);
            if (effect != null) return effect;

            return new string(span).ToLowerInvariant();
        }

        /// <summary>
        /// Reads a tag at <paramref name="at"/>. Returns false (consuming
        /// nothing) unless the whole thing is well formed.
        ///
        /// The argument comes back as a range into the source rather than a
        /// string: most tags parse a number or a colour out of it and never
        /// need one, and the handful that do (a style, font or sprite name, a
        /// link id, a particle) cut it themselves.
        /// </summary>
        private static bool TryReadTag(ReadOnlySpan<char> s, int at, out string name,
            out int argStart, out int argLength, out bool hasArgument,
            out bool closing, out int after)
        {
            name = null;
            argStart = 0;
            argLength = 0;
            hasArgument = false;
            closing = false;
            after = at;

            int i = at + 1;
            if (i >= s.Length) return false;
            if (s[i] == '/')
            {
                closing = true;
                i++;
            }

            int nameStart = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
            if (i == nameStart && !(closing && i < s.Length && s[i] == '>')) return false;

            name = i == nameStart ? string.Empty : CanonicalName(s, nameStart, i - nameStart);

            // `<wave amp=2 freq=1.5>`: a space after the name introduces an
            // attribute list, which runs to the '>' and is handed to the tag to
            // interpret. Only effect tags use it, but the reader has to allow
            // it or the tag is malformed before anyone gets to look at it.
            if (i < s.Length && s[i] == ' ')
            {
                int listStart = ++i;
                while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                if (i >= s.Length || s[i] != '>') return false;
                Trim(s, listStart, i - listStart, out argStart, out argLength);
                hasArgument = true;
                after = i + 1;
                return argLength > 0;
            }

            if (i < s.Length && s[i] == '=')
            {
                i++;
                int start = i;
                // A quoted argument may contain anything but its quote; an
                // unquoted one runs to '>'. Newlines end a tag either way; an
                // unterminated '<' should not swallow a paragraph.
                if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                {
                    char quote = s[i++];
                    start = i;
                    while (i < s.Length && s[i] != quote && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != quote) return false;
                    argStart = start;
                    argLength = i - start;
                    hasArgument = true;
                    i++;
                }
                else
                {
                    while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != '>') return false;
                    argStart = start;
                    argLength = i - start;
                    hasArgument = true;
                }
            }

            if (i >= s.Length || s[i] != '>') return false;
            after = i + 1;
            return true;
        }

        /// <summary>The range with leading and trailing whitespace taken off.</summary>
        private static void Trim(ReadOnlySpan<char> s, int start, int length,
            out int trimmedStart, out int trimmedLength)
        {
            int end = start + length;
            while (start < end && char.IsWhiteSpace(s[start])) start++;
            while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
            trimmedStart = start;
            trimmedLength = end - start;
        }

        /// <summary>
        /// The argument arrives as a range into the source rather than as a
        /// string. Most tags read a number or a colour out of it and never need
        /// one; the five that keep what they read — a style, font or sprite
        /// name, a link id, a particle — cut a string themselves, and are the
        /// only tags that still allocate.
        /// </summary>
        private static bool ApplyOpen(string name, ReadOnlySpan<char> source, int argStart, int argLength,
            bool hasArgument, List<Open> stack,
            RichTextResult result, int position, TextStyle current,
            Func<string, int> styleNames, Func<string, int> fontNames, Func<string, int> spriteNames,
            ref TextStyle style, ref int sprite, ref string josa)
        {
            var previous = current;
            var arg = hasArgument ? source.Slice(argStart, argLength) : default;
            switch (name)
            {
                // These take no argument, so one makes the tag malformed:
                // the same all-or-nothing rule the rest of the parser follows.
                // <b=7> is more likely a typo than a bold.
                case "b": if (hasArgument) return false; style.Flags |= TextStyle.Flag.Bold; break;
                case "i": if (hasArgument) return false; style.Flags |= TextStyle.Flag.Italic; break;
                case "u": if (hasArgument) return false; style.Flags |= TextStyle.Flag.Underline; break;
                case "s": if (hasArgument) return false; style.Flags |= TextStyle.Flag.Strikethrough; break;
                case "nobr": if (hasArgument) return false; style.Flags |= TextStyle.Flag.NoBreak; break;

                case "color":
                    if (!TryParseColor(arg, out var color)) return false;
                    style.Color = color;
                    style.Flags |= TextStyle.Flag.HasColor;
                    break;

                case "mark":
                    // <mark> with no argument is a conventional yellow wash.
                    if (arg.IsEmpty) style.MarkColor = new Color32(255, 235, 0, 80);
                    else if (!TryParseColor(arg, out style.MarkColor)) return false;
                    style.Flags |= TextStyle.Flag.HasMark;
                    break;

                case "size":
                    if (!TryParseSize(arg, current, ref style)) return false;
                    break;

                case "voffset":
                    if (!TryParseEms(arg, out style.BaselineShiftEm)) return false;
                    break;

                case "cspace":
                    if (!TryParseEms(arg, out style.LetterSpacingEm)) return false;
                    // Flagged, not inferred from the number: <cspace=0> is an
                    // author pulling a run back to the face's own metrics over
                    // a label, a style asset or a font that widened it, and
                    // reading 0 as "said nothing" would hand it straight back.
                    style.Flags |= TextStyle.Flag.HasLetterSpacing;
                    break;

                case "mspace":
                    if (!TryParseEms(arg, out style.MonoAdvanceEm)) return false;
                    // A cell of zero or less is not a cell. Unlike <cspace=0>,
                    // which is a real request, <mspace=0> asks for every glyph
                    // to be drawn on top of the last, and the house rule for a
                    // tag that cannot mean what it says is that it stays visible.
                    if (style.MonoAdvanceEm <= 0f) return false;
                    style.Flags |= TextStyle.Flag.HasMonoAdvance;
                    break;

                // Superscript and subscript, which are not a new kind of state:
                // they are the size and the baseline shift this parser already
                // had, set together. The numbers are TMP's own defaults —
                // OneText cannot read the face's superscriptOffset here because
                // the parser has no font, and a constant that matches what
                // everybody's text already looks like beats a lookup that
                // arrives one layer too late.
                case "sup":
                    if (hasArgument) return false;
                    style.SizeScale = current.SizeScale * SuperscriptSize;
                    style.BaselineShiftEm = Lift(current.BaselineShiftEm, SuperscriptOffsetEm,
                        SuperscriptSize);
                    break;

                case "sub":
                    if (hasArgument) return false;
                    style.SizeScale = current.SizeScale * SubscriptSize;
                    style.BaselineShiftEm = Lift(current.BaselineShiftEm, SubscriptOffsetEm,
                        SubscriptSize);
                    break;

                case "alpha":
                {
                    if (!TryParseAlpha(arg, out style.AlphaOverride)) return false;
                    style.Flags |= TextStyle.Flag.HasAlpha;
                    break;
                }

                case "align":
                {
                    if (!TryParseAlignment(arg, out var alignment)) return false;
                    // Remember what was in force, so </align> can put it back.
                    // Without this the tag is one-way: the rest of the text
                    // keeps an alignment the author explicitly ended.
                    bool had = result.Alignments.Count > 0;
                    var previousAlignment = had
                        ? result.Alignments[result.Alignments.Count - 1].Alignment
                        : default;
                    result.Alignments.Add((position, alignment));
                    stack.Add(new Open
                    {
                        Name = name,
                        Previous = previous,
                        HadAlignment = had,
                        PreviousAlignment = previousAlignment,
                    });
                    return true;
                }

                case "style":
                {
                    if (styleNames == null || arg.IsEmpty) return false;
                    int index = styleNames(new string(source.Slice(argStart, argLength)));
                    if (index < 0) return false;
                    style.NamedStyle = index;
                    break;
                }

                case "font":
                {
                    if (fontNames == null || arg.IsEmpty) return false;
                    int index = fontNames(new string(source.Slice(argStart, argLength)));
                    if (index < 0) return false;
                    style.FontOverride = index;
                    break;
                }

                case "outline":
                case "shadow":
                case "glow":
                {
                    if (!TryParseDecoration(name, arg, out var decoration)) return false;
                    // Recorded at OPEN, not at close, so the list stays in the
                    // order the author wrote: DecorationAt lays later spans over
                    // earlier ones, and closing order is the reverse of that;
                    // resolving from it would give the outer tag the last word.
                    stack.Add(new Open
                    {
                        Name = name,
                        Previous = previous,
                        DecorationIndex = result.Decorations.Count,
                    });
                    result.Decorations.Add(new TextDecorationSpan(decoration, position, int.MaxValue));
                    return true;
                }

                case "ruby":
                {
                    if (arg.IsEmpty) return false;
                    // Ruby inside ruby is refused rather than guessed at. Two
                    // annotations over one base is double-sided ruby (両側ルビ),
                    // which is a placement problem of its own and not what
                    // someone who nested the tag by accident meant; the house
                    // rule for a tag this parser will not honour is that it
                    // stays visible.
                    for (int i = 0; i < stack.Count; i++)
                        if (stack[i].Name == "ruby") return false;
                    stack.Add(new Open
                    {
                        Name = "ruby",
                        Previous = previous,
                        RubyStart = position,
                        RubyText = new string(source.Slice(argStart, argLength)),
                    });
                    return true;
                }

                case "link":
                    if (arg.IsEmpty) return false;
                    stack.Add(new Open
                    {
                        Name = "link",
                        Previous = previous,
                        LinkStart = position,
                        LinkId = new string(source.Slice(argStart, argLength)),
                    });
                    return true; // pushed with its own bookkeeping

                case "josa":
                {
                    // Resolved here, at parse time, against the text already
                    // written, which is what makes it work on a string that
                    // was interpolated at runtime, with no C# call anywhere.
                    // A formatter cannot help a string that arrives from a
                    // localisation table already assembled.
                    if (!Unicode.KoreanJosa.IsJosa(arg)) return false;
                    josa = new string(source.Slice(argStart, argLength));
                    return true;
                }

                case "wait":
                {
                    // Recorded and nothing else: a pause writes no text, styles
                    // nothing and closes nothing, so it never reaches the stack
                    // and </wait> is not a thing. Seconds must parse and must be
                    // finite and non-negative; <wait=soon> is a typo, and the
                    // house rule for a typo is that it stays visible rather than
                    // silently doing nothing.
                    if (!float.TryParse(arg, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float seconds)) return false;
                    if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return false;
                    result.Waits.Add((position, seconds));
                    return true;
                }

                case "sprite":
                {
                    // Reported back rather than emitted here: a sprite is a
                    // character, and the caller owns the text and its spans.
                    if (arg.IsEmpty) return false;
                    if (!int.TryParse(arg, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int index))
                    {
                        // A name, then, which is what survives someone
                        // reordering the sheet, and is why IndexOf exists.
                        index = spriteNames?.Invoke(new string(source.Slice(argStart, argLength))) ?? -1;
                    }
                    if (index < 0) return false;
                    sprite = index;
                    return true;
                }

                default:
                {
                    // Effect tags are looked up in the registry rather than
                    // listed here, so a project can add one from user code and
                    // have markup find it. A name nothing recognises stays
                    // literal, exactly like any other unknown tag.
                    if (!BuiltInEffects.Has(name)) return false;
                    stack.Add(new Open
                    {
                        Name = name,
                        Previous = previous,
                        EffectParameters = ParseEffectParameters(arg),
                        EffectStart = position,
                    });
                    return true;
                }
            }

            stack.Add(new Open { Name = name, Previous = previous });
            return true;
        }

        private static bool ApplyClose(string name, List<Open> stack, RichTextResult result,
            int position, ref TextStyle style)
        {
            if (stack.Count == 0) return false;

            int index = stack.Count - 1;
            if (name.Length > 0)
            {
                // Close the matching tag if it is on the stack at all. Closing
                // out of order is sloppy markup rather than broken markup, and
                // dropping the text would be the worse answer.
                while (index >= 0 && stack[index].Name != name) index--;
                if (index < 0) return false;
            }

            var open = stack[index];

            // Everything above the match is being closed too, implicitly. Their
            // ranges still have to be reported: a link popped this way and not
            // emitted here never gets another chance (it is off the stack
            // before the end-of-input flush), and a link that silently vanishes
            // is the failure this parser exists to avoid.
            for (int i = stack.Count - 1; i >= index; i--)
            {
                if (stack[i].Name == "link")
                    result.Links.Add(new TextLink(stack[i].LinkId, stack[i].LinkStart,
                        position - stack[i].LinkStart));
                if (stack[i].Name == "align")
                    result.Alignments.Add((position, stack[i].HadAlignment
                        ? stack[i].PreviousAlignment
                        : DefaultAlignmentMarker));
                if (stack[i].Name == "ruby")
                    CloseRuby(result, stack[i], position);
                if (IsDecoration(stack[i].Name))
                    CloseDecoration(result, stack[i].DecorationIndex, position);
                if (BuiltInEffects.Has(stack[i].Name))
                    result.Effects.Add((stack[i].Name, stack[i].EffectParameters,
                        stack[i].EffectStart, position));
            }

            style = open.Previous;
            stack.RemoveRange(index, stack.Count - index);
            return true;
        }

        // ------------------------------------------------------------ parsing

        private static bool TryParseSize(ReadOnlySpan<char> argument, TextStyle current,
            ref TextStyle style)
        {
            if (argument.IsEmpty) return false;

            if (argument[argument.Length - 1] == '%')
            {
                if (!float.TryParse(argument.Slice(0, argument.Length - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out float percent) ||
                    percent <= 0f) return false;
                style.SizeScale = current.SizeScale * (percent / 100f);
                return true;
            }

            if (argument[0] == '+' || argument[0] == '-')
            {
                // Relative to the inherited size, which the parser does not
                // know, so it is recorded as an absolute delta the label
                // resolves. Only meaningful against an absolute base.
                if (!float.TryParse(argument, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float delta)) return false;
                if (current.SizeAbsolute <= 0f) return false;
                style.SizeAbsolute = Mathf.Max(0.01f, current.SizeAbsolute + delta);
                return true;
            }

            if (!float.TryParse(argument, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float absolute) || absolute <= 0f) return false;
            style.SizeAbsolute = absolute;
            return true;
        }

        /// <summary>
        /// TextMesh Pro's own superscript and subscript defaults, in ems.
        ///
        /// TMP reads these off the face — <c>superscriptOffset</c>,
        /// <c>subscriptSize</c> and their pair — and a face may disagree with
        /// them. Nothing here can ask: the parser is handed a string and no
        /// font, deliberately, because it runs once per text change while the
        /// font can change under it without the text changing at all. So the
        /// defaults are the numbers, and text migrated from TMP lands where it
        /// was for every face that never overrode them, which is nearly all.
        /// </summary>
        private const float SuperscriptSize = 0.5f;
        private const float SuperscriptOffsetEm = 0.35f;
        private const float SubscriptSize = 0.5f;
        private const float SubscriptOffsetEm = -0.25f;

        /// <summary>
        /// The baseline shift a shrunk run needs to sit where the offset says,
        /// in the run's own ems.
        ///
        /// <see cref="TextStyle.BaselineShiftEm"/> is resolved against the size
        /// of the run holding it, and a superscript run is half size, so
        /// writing the offset in unchanged buys half the lift — which reads,
        /// correctly, as a superscript sitting too low. Dividing by the shrink
        /// says the offset in the size the text had before it shrank, which is
        /// what "a third of an em above the baseline" is meant to be measured
        /// against, and is where TextMesh Pro puts it.
        ///
        /// The shift already in force is carried through the same division, so
        /// a <c>&lt;sup&gt;</c> inside a <c>&lt;voffset&gt;</c> keeps the raise
        /// it inherited instead of halving it.
        /// </summary>
        private static float Lift(float inherited, float offsetEm, float size) =>
            (inherited + offsetEm) / size;

        /// <summary>
        /// <c>&lt;alpha=#80&gt;</c>: two hex digits, and nothing else.
        ///
        /// Deliberately not a percentage or a 0–1 float, both of which would be
        /// reasonable designs and neither of which is what any text arriving
        /// from TextMesh Pro says. A tag that silently accepted
        /// <c>&lt;alpha=0.5&gt;</c> would read a migrated <c>&lt;alpha=#80&gt;</c>
        /// one way and a hand-written one another.
        /// </summary>
        private static bool TryParseAlpha(ReadOnlySpan<char> argument, out byte alpha)
        {
            alpha = 255;
            if (argument.IsEmpty || argument[0] != '#') return false;
            if (argument.Length != 3) return false;
            return byte.TryParse(argument.Slice(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out alpha);
        }

        private static bool TryParseEms(ReadOnlySpan<char> argument, out float ems)
        {
            ems = 0f;
            if (argument.IsEmpty) return false;
            var value = argument.EndsWith("em".AsSpan(), StringComparison.OrdinalIgnoreCase)
                ? argument.Slice(0, argument.Length - 2)
                : argument;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ems);
        }

        private static bool TryParseAlignment(ReadOnlySpan<char> argument, out TextAlignment alignment)
        {
            alignment = TextAlignment.Start;
            if (argument.IsEmpty) return false;
            if (Is(argument, "left")) { alignment = TextAlignment.Left; return true; }
            if (Is(argument, "right")) { alignment = TextAlignment.Right; return true; }
            if (Is(argument, "center") || Is(argument, "centre"))
            { alignment = TextAlignment.Center; return true; }
            if (Is(argument, "justified") || Is(argument, "justify"))
            { alignment = TextAlignment.Justified; return true; }
            if (Is(argument, "start")) { alignment = TextAlignment.Start; return true; }
            if (Is(argument, "end")) { alignment = TextAlignment.End; return true; }
            return false;
        }

        /// <summary>Case-insensitive compare against a literal, allocating nothing.</summary>
        private static bool Is(ReadOnlySpan<char> value, string literal) =>
            value.Equals(literal.AsSpan(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The next token of an attribute list, split on spaces and commas.
        /// The list form is what <c>&lt;wave amp=2 freq=1.5&gt;</c> and
        /// <c>&lt;outline=black w=0.6&gt;</c> both use, and splitting it with
        /// string.Split allocated an array and a string per token.
        /// </summary>
        private static bool NextToken(ReadOnlySpan<char> s, ref int index, out ReadOnlySpan<char> token)
        {
            while (index < s.Length && (s[index] == ' ' || s[index] == ',')) index++;
            int start = index;
            while (index < s.Length && s[index] != ' ' && s[index] != ',') index++;
            token = s.Slice(start, index - start);
            return token.Length > 0;
        }

        /// <summary>
        /// Reads an effect tag's arguments: <c>&lt;wave amp=2 freq=1.5&gt;</c>.
        ///
        /// Unset values come back as NaN rather than zero, so an effect can
        /// tell "the author did not say" from "the author said none"; an
        /// amplitude of 0 is a legitimate request for a still effect, and a
        /// default of 0 would make every unparameterised tag do nothing.
        ///
        /// Public because the inspector's effect table reads a tag's arguments
        /// through the same rules the runtime does; two parsers is one bug.
        /// </summary>
        public static TextEffectParameters ParseEffectParameters(string argument) =>
            ParseEffectParameters(argument.AsSpan());

        /// <inheritdoc cref="ParseEffectParameters(string)"/>
        public static TextEffectParameters ParseEffectParameters(ReadOnlySpan<char> argument)
        {
            float amplitude = float.NaN, frequency = float.NaN, speed = float.NaN, extra = float.NaN;
            float duration = float.NaN;
            if (argument.IsEmpty)
                return new TextEffectParameters(amplitude, frequency, speed, extra, duration);

            // A bare number is the amplitude: <shake=3> is what people write.
            if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float bare))
                return new TextEffectParameters(bare, frequency, speed, extra, duration);

            int index = 0;
            while (NextToken(argument, ref index, out var pair))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                if (!float.TryParse(pair.Slice(equals + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value)) continue;

                var key = pair.Slice(0, equals);
                if (Is(key, "amp") || Is(key, "amplitude")) amplitude = value;
                else if (Is(key, "freq") || Is(key, "frequency")) frequency = value;
                else if (Is(key, "speed") || Is(key, "time")) speed = value;
                else if (Is(key, "extra") || Is(key, "arg")) extra = value;
                // Seconds the effect runs before settling; the animation
                // clock starts when the label enables, so <wave for=0.5>
                // is "wave for half a second on appear".
                else if (Is(key, "for") || Is(key, "dur") || Is(key, "duration")) duration = value;
            }
            return new TextEffectParameters(amplitude, frequency, speed, extra, duration);
        }

        /// <summary>
        /// Records a finished ruby annotation.
        ///
        /// An empty base is dropped: <c>&lt;ruby=かん&gt;&lt;/ruby&gt;</c> is an
        /// annotation of nothing, and there is no advance to centre it over.
        /// </summary>
        private static void CloseRuby(RichTextResult result, in Open open, int end)
        {
            if (end > open.RubyStart)
                result.Rubies.Add(new TextRubySpan(open.RubyText, open.RubyStart, end - open.RubyStart));
        }

        /// <summary>True for the three tags that carry a <see cref="TextDecoration"/>.</summary>
        public static bool IsDecoration(string name) =>
            name == "outline" || name == "shadow" || name == "glow";

        /// <summary>Writes the end position into a span opened earlier.</summary>
        private static void CloseDecoration(RichTextResult result, int index, int end)
        {
            if (index < 0 || index >= result.Decorations.Count) return;
            var span = result.Decorations[index];
            result.Decorations[index] = new TextDecorationSpan(span.Decoration, span.Start, end);
        }

        /// <summary>
        /// Reads a decoration tag's arguments.
        ///
        /// Three spellings, all of which people write:
        /// <c>&lt;shadow&gt;</c> takes the defaults, <c>&lt;shadow=red&gt;</c>
        /// names the colour, and <c>&lt;shadow x=0.4 soft=0.6&gt;</c> names
        /// parameters. They mix: <c>&lt;outline=black w=0.6&gt;</c> is one
        /// unquoted argument that happens to contain both.
        ///
        /// A token that is neither a colour nor a parameter this tag reads
        /// fails the whole tag, which leaves it in the text as literal
        /// characters. That is the house rule, and it is what makes a typo
        /// visible instead of silently drawing the default.
        ///
        /// Public because the inspector's decoration table reads a tag's
        /// arguments through the same rules the runtime does; two parsers is
        /// one bug.
        /// </summary>
        public static bool TryParseDecoration(string name, string argument, out TextDecoration decoration) =>
            TryParseDecoration(name, argument.AsSpan(), out decoration);

        /// <inheritdoc cref="TryParseDecoration(string, string, out TextDecoration)"/>
        public static bool TryParseDecoration(string name, ReadOnlySpan<char> argument,
            out TextDecoration decoration)
        {
            switch (name)
            {
                case "outline": decoration = TextDecoration.DefaultOutline; break;
                case "shadow": decoration = TextDecoration.DefaultShadow; break;
                case "glow": decoration = TextDecoration.DefaultGlow; break;
                default: decoration = TextDecoration.None; return false;
            }
            if (argument.IsEmpty) return true;

            int index = 0;
            while (NextToken(argument, ref index, out var token))
            {
                int equals = token.IndexOf('=');
                if (equals <= 0)
                {
                    // A bare token is the colour: <glow=cyan> is what gets
                    // written, and no other parameter is worth a bare form.
                    if (!TryParseColor(token, out var bare)) return false;
                    SetDecorationColor(ref decoration, bare);
                    continue;
                }

                var key = token.Slice(0, equals);
                var value = token.Slice(equals + 1);
                if (Is(key, "color") || Is(key, "colour") || Is(key, "c"))
                {
                    if (!TryParseColor(value, out var named)) return false;
                    SetDecorationColor(ref decoration, named);
                    continue;
                }

                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float number) || float.IsNaN(number) || float.IsInfinity(number))
                    return false;

                bool known;
                switch (name)
                {
                    case "outline":
                        known = Is(key, "w") || Is(key, "width");
                        if (known) decoration.OutlineWidth = number;
                        break;
                    case "shadow":
                        if (Is(key, "x")) { decoration.ShadowOffset.x = number; known = true; }
                        else if (Is(key, "y")) { decoration.ShadowOffset.y = number; known = true; }
                        else if (Is(key, "soft") || Is(key, "softness"))
                        { decoration.ShadowSoftness = number; known = true; }
                        else known = false;
                        break;
                    default:
                        known = Is(key, "r") || Is(key, "radius") || Is(key, "w") || Is(key, "width");
                        if (known) decoration.GlowRadius = number;
                        break;
                }
                if (!known) return false;
            }

            decoration = decoration.Clamped();
            return true;
        }

        private static void SetDecorationColor(ref TextDecoration decoration, Color32 color)
        {
            if (decoration.HasOutline) decoration.OutlineColor = color;
            else if (decoration.HasShadow) decoration.ShadowColor = color;
            else decoration.GlowColor = color;
        }

        /// <summary>
        /// `#rgb`, `#rrggbb`, `#rrggbbaa` or one of the handful of names that
        /// markup in the wild actually uses. Unity's own ColorUtility handles
        /// the hex forms; the names are here because `<color=red>` is what
        /// people write.
        /// </summary>
        public static bool TryParseColor(string argument, out Color32 color) =>
            TryParseColor(argument.AsSpan(), out color);

        /// <inheritdoc cref="TryParseColor(string, out Color32)"/>
        public static bool TryParseColor(ReadOnlySpan<char> argument, out Color32 color)
        {
            color = default;
            if (argument.IsEmpty) return false;

            // The hex forms are read here rather than through
            // ColorUtility.TryParseHtmlString, which takes a string and would
            // make <color=#ff8800> cost one on every parse. The four lengths
            // are the four that function accepts after a '#'.
            if (argument[0] == '#') return TryParseHex(argument.Slice(1), out color);

            if (Is(argument, "black")) { color = new Color32(0, 0, 0, 255); return true; }
            if (Is(argument, "white")) { color = new Color32(255, 255, 255, 255); return true; }
            if (Is(argument, "red")) { color = new Color32(255, 0, 0, 255); return true; }
            if (Is(argument, "green")) { color = new Color32(0, 255, 0, 255); return true; }
            if (Is(argument, "blue")) { color = new Color32(0, 0, 255, 255); return true; }
            if (Is(argument, "yellow")) { color = new Color32(255, 255, 0, 255); return true; }
            if (Is(argument, "orange")) { color = new Color32(255, 128, 0, 255); return true; }
            if (Is(argument, "purple")) { color = new Color32(160, 32, 240, 255); return true; }
            if (Is(argument, "grey") || Is(argument, "gray"))
            { color = new Color32(128, 128, 128, 255); return true; }
            if (Is(argument, "cyan")) { color = new Color32(0, 255, 255, 255); return true; }
            if (Is(argument, "magenta")) { color = new Color32(255, 0, 255, 255); return true; }
            if (Is(argument, "brown")) { color = new Color32(165, 42, 42, 255); return true; }
            if (Is(argument, "pink")) { color = new Color32(255, 192, 203, 255); return true; }
            if (Is(argument, "clear")) { color = new Color32(0, 0, 0, 0); return true; }
            return false;
        }

        /// <summary>`rgb`, `rgba`, `rrggbb` or `rrggbbaa`, without the hash.</summary>
        private static bool TryParseHex(ReadOnlySpan<char> hex, out Color32 color)
        {
            color = default;
            bool shorthand = hex.Length == 3 || hex.Length == 4;
            if (!shorthand && hex.Length != 6 && hex.Length != 8) return false;

            Span<byte> channels = stackalloc byte[4] { 255, 255, 255, 255 };
            int count = shorthand ? hex.Length : hex.Length / 2;
            for (int i = 0; i < count; i++)
            {
                if (shorthand)
                {
                    if (!TryHexDigit(hex[i], out int digit)) return false;
                    channels[i] = (byte)(digit * 16 + digit);
                    continue;
                }
                if (!TryHexDigit(hex[i * 2], out int high)) return false;
                if (!TryHexDigit(hex[i * 2 + 1], out int low)) return false;
                channels[i] = (byte)(high * 16 + low);
            }
            color = new Color32(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        private static bool TryHexDigit(char c, out int value)
        {
            if (c >= '0' && c <= '9') { value = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
            if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
            value = 0;
            return false;
        }
    }
}
