using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        /// <summary>The text with markup removed — what indices refer to.</summary>
        public string Text = string.Empty;

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
        /// that has to be markup: everything else about a reveal — how fast,
        /// what a step is, how long to hold after a full stop — is project
        /// policy that belongs on the label, but "pause HERE, in this
        /// sentence" has nowhere else it can be said.
        /// </summary>
        public readonly List<(int Index, float Seconds)> Waits = new List<(int, float)>();

        public void Clear()
        {
            Text = string.Empty;
            Spans.Clear();
            Links.Clear();
            Alignments.Clear();
            Effects.Clear();
            Decorations.Clear();
            Waits.Clear();
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
    /// all-or-nothing — name, argument and closing '&gt;' all have to be there
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
        }

        public static void Parse(string source, RichTextResult result) =>
            Parse(source, result, null, null);

        /// <summary>
        /// Parses <paramref name="source"/> into <paramref name="result"/>.
        ///
        /// <paramref name="styleNames"/> and <paramref name="fontNames"/> map
        /// the arguments of <c>&lt;style=…&gt;</c> and <c>&lt;font=…&gt;</c> to
        /// indices; either may be null, in which case those tags do not parse
        /// and stay literal — which is the honest outcome, because a style the
        /// label cannot resolve is not a style.
        /// </summary>
        public static void Parse(string source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames) =>
            Parse(source, result, styleNames, fontNames, null);

        /// <summary>
        /// Same, with a resolver for <c>&lt;sprite=name&gt;</c>. An index still
        /// works; a name is what survives someone reordering the sheet.
        /// </summary>
        public static void Parse(string source, RichTextResult result,
            Func<string, int> styleNames, Func<string, int> fontNames, Func<string, int> spriteNames)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.Clear();
            if (string.IsNullOrEmpty(source)) return;

            if (!MightHaveMarkup(source))
            {
                result.Text = source;
                result.Spans.Add(new TextStyleSpan(0, source.Length, TextStyle.Default));
                return;
            }

            var output = new StringBuilder(source.Length);
            var stack = new List<Open>();
            var style = TextStyle.Default;
            var spanStart = 0;
            var spanStyle = style;

            for (int i = 0; i < source.Length;)
            {
                if (source[i] != '<')
                {
                    output.Append(source[i++]);
                    continue;
                }

                if (!TryReadTag(source, i, out string name, out string argument,
                        out bool closing, out int after))
                {
                    output.Append(source[i++]);
                    continue;
                }

                var next = style;
                int sprite = -1;
                string josa = null;
                bool consumed;
                if (closing)
                    consumed = ApplyClose(name, stack, result, output.Length, ref next);
                else
                    consumed = ApplyOpen(name, argument, stack, result, output.Length, style,
                        styleNames, fontNames, spriteNames, ref next, ref sprite, ref josa);

                if (!consumed)
                {
                    // Not a tag we understand, or not a well-formed use of one.
                    output.Append(source[i++]);
                    continue;
                }

                result.HasMarkup = true;

                if (josa != null)
                {
                    // Written straight into the output, styled like the text it
                    // follows: a particle is part of the sentence, not a tag
                    // that leaves a mark.
                    output.Append(Unicode.KoreanJosa.Resolve(output.ToString(), josa));
                    i = after;
                    continue;
                }

                if (!next.Equals(spanStyle))
                {
                    if (output.Length > spanStart)
                        result.Spans.Add(new TextStyleSpan(spanStart, output.Length - spanStart, spanStyle));
                    spanStart = output.Length;
                    spanStyle = next;
                }

                if (sprite >= 0)
                {
                    // A sprite is one placeholder character in a span of its
                    // own: it takes an index so carets, selection and reveal can
                    // count it, and it is not a pair, so the span opens and
                    // closes here rather than going on the stack.
                    if (output.Length > spanStart)
                        result.Spans.Add(new TextStyleSpan(spanStart, output.Length - spanStart, spanStyle));
                    var spriteStyle = next;
                    spriteStyle.Sprite = sprite;
                    spriteStyle.Flags |= TextStyle.Flag.Sprite;
                    output.Append(SpritePlaceholder);
                    result.Spans.Add(new TextStyleSpan(output.Length - 1, 1, spriteStyle));
                    spanStart = output.Length;
                }

                style = next;
                i = after;
            }

            if (output.Length > spanStart || result.Spans.Count == 0)
                result.Spans.Add(new TextStyleSpan(spanStart, output.Length - spanStart, spanStyle));

            // Anything still open at the end closes at the end. Unterminated
            // markup styling the rest of the text is what every other engine
            // does, and it is far less surprising than dropping the style.
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].Name == "link")
                    result.Links.Add(new TextLink(stack[i].LinkId, stack[i].LinkStart,
                        output.Length - stack[i].LinkStart));
                else if (IsDecoration(stack[i].Name))
                    CloseDecoration(result, stack[i].DecorationIndex, output.Length);
                else if (BuiltInEffects.Has(stack[i].Name))
                    result.Effects.Add((stack[i].Name, stack[i].EffectParameters,
                        stack[i].EffectStart, output.Length));
            }

            result.Text = output.ToString();
        }

        /// <summary>
        /// Recorded by <c>&lt;/align&gt;</c> when nothing was overriding
        /// alignment before it: "go back to whatever the label says", which is
        /// not any particular alignment.
        /// </summary>
        internal const TextAlignment DefaultAlignmentMarker = (TextAlignment)(-1);

        // --------------------------------------------------------------- tags

        /// <summary>
        /// Reads a tag at <paramref name="at"/>. Returns false — consuming
        /// nothing — unless the whole thing is well formed.
        /// </summary>
        private static bool TryReadTag(string s, int at, out string name, out string argument,
            out bool closing, out int after)
        {
            name = null;
            argument = null;
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

            name = s.Substring(nameStart, i - nameStart).ToLowerInvariant();

            // `<wave amp=2 freq=1.5>`: a space after the name introduces an
            // attribute list, which runs to the '>' and is handed to the tag to
            // interpret. Only effect tags use it, but the reader has to allow
            // it or the tag is malformed before anyone gets to look at it.
            if (i < s.Length && s[i] == ' ')
            {
                int listStart = ++i;
                while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                if (i >= s.Length || s[i] != '>') return false;
                argument = s.Substring(listStart, i - listStart).Trim();
                after = i + 1;
                return argument.Length > 0;
            }

            if (i < s.Length && s[i] == '=')
            {
                i++;
                int argStart = i;
                // A quoted argument may contain anything but its quote; an
                // unquoted one runs to '>'. Newlines end a tag either way — an
                // unterminated '<' should not swallow a paragraph.
                if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                {
                    char quote = s[i++];
                    argStart = i;
                    while (i < s.Length && s[i] != quote && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != quote) return false;
                    argument = s.Substring(argStart, i - argStart);
                    i++;
                }
                else
                {
                    while (i < s.Length && s[i] != '>' && s[i] != '<' && s[i] != '\n') i++;
                    if (i >= s.Length || s[i] != '>') return false;
                    argument = s.Substring(argStart, i - argStart);
                }
            }

            if (i >= s.Length || s[i] != '>') return false;
            after = i + 1;
            return true;
        }

        private static bool ApplyOpen(string name, string argument, List<Open> stack,
            RichTextResult result, int position, TextStyle current,
            Func<string, int> styleNames, Func<string, int> fontNames, Func<string, int> spriteNames,
            ref TextStyle style, ref int sprite, ref string josa)
        {
            var previous = current;
            switch (name)
            {
                // These take no argument, so one makes the tag malformed —
                // the same all-or-nothing rule the rest of the parser follows.
                // <b=7> is more likely a typo than a bold.
                case "b": if (argument != null) return false; style.Flags |= TextStyle.Flag.Bold; break;
                case "i": if (argument != null) return false; style.Flags |= TextStyle.Flag.Italic; break;
                case "u": if (argument != null) return false; style.Flags |= TextStyle.Flag.Underline; break;
                case "s": if (argument != null) return false; style.Flags |= TextStyle.Flag.Strikethrough; break;
                case "nobr": if (argument != null) return false; style.Flags |= TextStyle.Flag.NoBreak; break;

                case "color":
                    if (!TryParseColor(argument, out var color)) return false;
                    style.Color = color;
                    style.Flags |= TextStyle.Flag.HasColor;
                    break;

                case "mark":
                    // <mark> with no argument is a conventional yellow wash.
                    if (string.IsNullOrEmpty(argument)) style.MarkColor = new Color32(255, 235, 0, 80);
                    else if (!TryParseColor(argument, out style.MarkColor)) return false;
                    style.Flags |= TextStyle.Flag.HasMark;
                    break;

                case "size":
                    if (!TryParseSize(argument, current, ref style)) return false;
                    break;

                case "voffset":
                    if (!TryParseEms(argument, out style.BaselineShiftEm)) return false;
                    break;

                case "cspace":
                    if (!TryParseEms(argument, out style.LetterSpacingEm)) return false;
                    break;

                case "align":
                {
                    if (!TryParseAlignment(argument, out var alignment)) return false;
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
                    if (styleNames == null || string.IsNullOrEmpty(argument)) return false;
                    int index = styleNames(argument);
                    if (index < 0) return false;
                    style.NamedStyle = index;
                    break;
                }

                case "font":
                {
                    if (fontNames == null || string.IsNullOrEmpty(argument)) return false;
                    int index = fontNames(argument);
                    if (index < 0) return false;
                    style.FontOverride = index;
                    break;
                }

                case "outline":
                case "shadow":
                case "glow":
                {
                    if (!TryParseDecoration(name, argument, out var decoration)) return false;
                    // Recorded at OPEN, not at close, so the list stays in the
                    // order the author wrote: DecorationAt lays later spans over
                    // earlier ones, and closing order is the reverse of that —
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

                case "link":
                    if (string.IsNullOrEmpty(argument)) return false;
                    stack.Add(new Open
                    {
                        Name = "link",
                        Previous = previous,
                        LinkStart = position,
                        LinkId = argument,
                    });
                    return true; // pushed with its own bookkeeping

                case "josa":
                {
                    // Resolved here, at parse time, against the text already
                    // written — which is what makes it work on a string that
                    // was interpolated at runtime, with no C# call anywhere.
                    // A formatter cannot help a string that arrives from a
                    // localisation table already assembled.
                    if (!Unicode.KoreanJosa.IsJosa(argument)) return false;
                    josa = argument;
                    return true;
                }

                case "wait":
                {
                    // Recorded and nothing else: a pause writes no text, styles
                    // nothing and closes nothing, so it never reaches the stack
                    // and </wait> is not a thing. Seconds must parse and must be
                    // finite and non-negative — <wait=soon> is a typo, and the
                    // house rule for a typo is that it stays visible rather than
                    // silently doing nothing.
                    if (!float.TryParse(argument, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float seconds)) return false;
                    if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return false;
                    result.Waits.Add((position, seconds));
                    return true;
                }

                case "sprite":
                {
                    // Reported back rather than emitted here: a sprite is a
                    // character, and the caller owns the text and its spans.
                    if (string.IsNullOrEmpty(argument)) return false;
                    if (!int.TryParse(argument, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int index))
                    {
                        // A name, then — which is what survives someone
                        // reordering the sheet, and is why IndexOf exists.
                        index = spriteNames?.Invoke(argument) ?? -1;
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
                        EffectParameters = ParseEffectParameters(argument),
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
            // emitted here never gets another chance — it is off the stack
            // before the end-of-input flush — and a link that silently vanishes
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

        private static bool TryParseSize(string argument, TextStyle current, ref TextStyle style)
        {
            if (string.IsNullOrEmpty(argument)) return false;

            if (argument[argument.Length - 1] == '%')
            {
                if (!float.TryParse(argument.Substring(0, argument.Length - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out float percent) ||
                    percent <= 0f) return false;
                style.SizeScale = current.SizeScale * (percent / 100f);
                return true;
            }

            if (argument[0] == '+' || argument[0] == '-')
            {
                // Relative to the inherited size, which the parser does not
                // know — so it is recorded as an absolute delta the label
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

        private static bool TryParseEms(string argument, out float ems)
        {
            ems = 0f;
            if (string.IsNullOrEmpty(argument)) return false;
            string value = argument.EndsWith("em", StringComparison.OrdinalIgnoreCase)
                ? argument.Substring(0, argument.Length - 2)
                : argument;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ems);
        }

        private static bool TryParseAlignment(string argument, out TextAlignment alignment)
        {
            alignment = TextAlignment.Start;
            if (string.IsNullOrEmpty(argument)) return false;
            switch (argument.ToLowerInvariant())
            {
                case "left": alignment = TextAlignment.Left; return true;
                case "right": alignment = TextAlignment.Right; return true;
                case "center": case "centre": alignment = TextAlignment.Center; return true;
                case "justified": case "justify": alignment = TextAlignment.Justified; return true;
                case "start": alignment = TextAlignment.Start; return true;
                case "end": alignment = TextAlignment.End; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Reads an effect tag's arguments: <c>&lt;wave amp=2 freq=1.5&gt;</c>.
        ///
        /// Unset values come back as NaN rather than zero, so an effect can
        /// tell "the author did not say" from "the author said none" — an
        /// amplitude of 0 is a legitimate request for a still effect, and a
        /// default of 0 would make every unparameterised tag do nothing.
        ///
        /// Public because the inspector's effect table reads a tag's arguments
        /// through the same rules the runtime does — two parsers is one bug.
        /// </summary>
        public static TextEffectParameters ParseEffectParameters(string argument)
        {
            float amplitude = float.NaN, frequency = float.NaN, speed = float.NaN, extra = float.NaN;
            float duration = float.NaN;
            if (string.IsNullOrEmpty(argument))
                return new TextEffectParameters(amplitude, frequency, speed, extra, duration);

            // A bare number is the amplitude: <shake=3> is what people write.
            if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float bare))
                return new TextEffectParameters(bare, frequency, speed, extra, duration);

            foreach (var pair in argument.Split(new[] { ' ', ',' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                if (!float.TryParse(pair.Substring(equals + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value)) continue;

                switch (pair.Substring(0, equals).ToLowerInvariant())
                {
                    case "amp": case "amplitude": amplitude = value; break;
                    case "freq": case "frequency": frequency = value; break;
                    case "speed": case "time": speed = value; break;
                    case "extra": case "arg": extra = value; break;
                    // Seconds the effect runs before settling; the animation
                    // clock starts when the label enables, so <wave for=0.5>
                    // is "wave for half a second on appear".
                    case "for": case "dur": case "duration": duration = value; break;
                }
            }
            return new TextEffectParameters(amplitude, frequency, speed, extra, duration);
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
        /// parameters. They mix — <c>&lt;outline=black w=0.6&gt;</c> is one
        /// unquoted argument that happens to contain both.
        ///
        /// A token that is neither a colour nor a parameter this tag reads
        /// fails the whole tag, which leaves it in the text as literal
        /// characters. That is the house rule, and it is what makes a typo
        /// visible instead of silently drawing the default.
        ///
        /// Public because the inspector's decoration table reads a tag's
        /// arguments through the same rules the runtime does — two parsers is
        /// one bug.
        /// </summary>
        public static bool TryParseDecoration(string name, string argument, out TextDecoration decoration)
        {
            switch (name)
            {
                case "outline": decoration = TextDecoration.DefaultOutline; break;
                case "shadow": decoration = TextDecoration.DefaultShadow; break;
                case "glow": decoration = TextDecoration.DefaultGlow; break;
                default: decoration = TextDecoration.None; return false;
            }
            if (string.IsNullOrEmpty(argument)) return true;

            foreach (var token in argument.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
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

                string key = token.Substring(0, equals).ToLowerInvariant();
                string value = token.Substring(equals + 1);
                if (key == "color" || key == "colour" || key == "c")
                {
                    if (!TryParseColor(value, out var named)) return false;
                    SetDecorationColor(ref decoration, named);
                    continue;
                }

                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float number) || float.IsNaN(number) || float.IsInfinity(number))
                    return false;

                switch (name)
                {
                    case "outline" when key == "w" || key == "width":
                        decoration.OutlineWidth = number; break;
                    case "shadow" when key == "x":
                        decoration.ShadowOffset.x = number; break;
                    case "shadow" when key == "y":
                        decoration.ShadowOffset.y = number; break;
                    case "shadow" when key == "soft" || key == "softness":
                        decoration.ShadowSoftness = number; break;
                    case "glow" when key == "r" || key == "radius" || key == "w" || key == "width":
                        decoration.GlowRadius = number; break;
                    default: return false;
                }
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
        public static bool TryParseColor(string argument, out Color32 color)
        {
            color = default;
            if (string.IsNullOrEmpty(argument)) return false;

            if (argument[0] == '#')
            {
                if (!ColorUtility.TryParseHtmlString(argument, out var parsed)) return false;
                color = parsed;
                return true;
            }

            switch (argument.ToLowerInvariant())
            {
                case "black": color = new Color32(0, 0, 0, 255); return true;
                case "white": color = new Color32(255, 255, 255, 255); return true;
                case "red": color = new Color32(255, 0, 0, 255); return true;
                case "green": color = new Color32(0, 255, 0, 255); return true;
                case "blue": color = new Color32(0, 0, 255, 255); return true;
                case "yellow": color = new Color32(255, 255, 0, 255); return true;
                case "orange": color = new Color32(255, 128, 0, 255); return true;
                case "purple": color = new Color32(160, 32, 240, 255); return true;
                case "grey": case "gray": color = new Color32(128, 128, 128, 255); return true;
                case "cyan": color = new Color32(0, 255, 255, 255); return true;
                case "magenta": color = new Color32(255, 0, 255, 255); return true;
                case "brown": color = new Color32(165, 42, 42, 255); return true;
                case "pink": color = new Color32(255, 192, 203, 255); return true;
                case "clear": color = new Color32(0, 0, 0, 0); return true;
                default: return false;
            }
        }
    }
}
