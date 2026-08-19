using System;

namespace OneText.Unicode
{
    /// <summary>
    /// Reading UTF-16 out of a span.
    ///
    /// <c>char.ConvertToUtf32</c> has a (string, int) overload and a (char,
    /// char) one, and no span in between; every algorithm here walks a span and
    /// needs the first. This is that overload, with the same rule: a well-formed
    /// surrogate pair is one codepoint, and anything else is the unit itself,
    /// because a lone surrogate is text a font still has to be asked about
    /// rather than an exception to throw at a player.
    /// </summary>
    public static class Utf16
    {
        public static int Codepoint(ReadOnlySpan<char> text, int index)
        {
            char high = text[index];
            if (char.IsHighSurrogate(high) && index + 1 < text.Length &&
                char.IsLowSurrogate(text[index + 1]))
                return char.ConvertToUtf32(high, text[index + 1]);
            return high;
        }
    }
}
