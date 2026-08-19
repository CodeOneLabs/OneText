using System;
using System.Text;

namespace OneText
{
    /// <summary>
    /// Turns backslash escapes into the characters they name: \n, \t, \v,
    /// \r, \\, \u + four hex digits, \U + eight.
    ///
    /// A file has no way to hold a newline inside a CSV cell or a JSON-ish
    /// localization table except as the two characters "\n", so that is what
    /// pipelines store and what a label gets handed. Printed literally it is
    /// never what the translator meant, and TextMesh Pro resolves these by
    /// default, so strings migrated from it arrive expecting the same.
    ///
    /// Anything unrecognized stays exactly as written, backslash and all: a
    /// Windows path pasted into a label must not lose its separators just
    /// because "\U" happened to start a folder name — which is also why the
    /// hex forms only apply when every digit is present.
    /// </summary>
    public static class EscapeParser
    {
        /// <summary>Cheap pre-check: no backslash means nothing to do.</summary>
        public static bool MightHaveEscapes(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf('\\') >= 0;

        /// <inheritdoc cref="MightHaveEscapes(string)"/>
        public static bool MightHaveEscapes(ReadOnlySpan<char> text) => text.IndexOf('\\') >= 0;

        /// <summary>
        /// The unescaped string. Returns the instance it was given when there
        /// is nothing to change, so a caller can compare by reference and a
        /// string without escapes costs nothing.
        /// </summary>
        public static string Unescape(string text)
        {
            if (!MightHaveEscapes(text)) return text;

            var output = new StringBuilder(text.Length);
            bool changed = false;
            for (int i = 0; i < text.Length;)
            {
                char c = text[i];
                if (c != '\\' || i + 1 == text.Length)
                {
                    output.Append(c);
                    i++;
                    continue;
                }

                switch (text[i + 1])
                {
                    case 'n': output.Append('\n'); i += 2; changed = true; continue;
                    case 't': output.Append('\t'); i += 2; changed = true; continue;
                    case 'v': output.Append('\v'); i += 2; changed = true; continue;
                    case 'r': output.Append('\r'); i += 2; changed = true; continue;
                    case '\\': output.Append('\\'); i += 2; changed = true; continue;

                    case 'u':
                        // A lone surrogate is allowed through: it is what the
                        // same escape means in a C# literal, and the shaper
                        // already turns invalid UTF-16 into U+FFFD.
                        if (TryReadHex(text, i + 2, 4, out uint unit))
                        {
                            output.Append((char)unit);
                            i += 6;
                            changed = true;
                            continue;
                        }
                        break;

                    case 'U':
                        if (TryReadHex(text, i + 2, 8, out uint scalar) &&
                            scalar <= 0x10FFFF && (scalar < 0xD800 || scalar > 0xDFFF))
                        {
                            if (scalar >= 0x10000)
                            {
                                output.Append((char)(0xD800u + ((scalar - 0x10000u) >> 10)));
                                output.Append((char)(0xDC00u + ((scalar - 0x10000u) & 0x3FF)));
                            }
                            else
                            {
                                output.Append((char)scalar);
                            }
                            i += 10;
                            changed = true;
                            continue;
                        }
                        break;
                }

                output.Append(c);
                i++;
            }
            return changed ? output.ToString() : text;
        }

        // Unsigned on purpose: eight digits of hex overflow a signed int, and
        // \UFFFFFFFF wrapping to -1 would sail under the U+10FFFF ceiling.
        private static bool TryReadHex(string text, int start, int count, out uint value)
        {
            value = 0;
            if (start + count > text.Length) return false;
            for (int i = 0; i < count; i++)
            {
                int digit = HexDigit(text[start + i]);
                if (digit < 0) return false;
                value = (value << 4) | (uint)digit;
            }
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
