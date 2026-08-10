using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// What a component says when the project gave it nothing to draw with.
    ///
    /// <para>It said nothing at all until now. Neither <c>OneTextLabel</c> nor
    /// <c>OneTextMesh</c> contained a single <c>LogWarning</c>: a label whose
    /// font asset had no file in it failed its native-state check and returned,
    /// which on screen is a label that is simply not there. The one warning
    /// that existed was <c>OneFontAsset</c>'s, and it only fires for a
    /// migration placeholder — an asset that knows it is waiting for a file.
    /// Every other way of arriving with no font (an empty Font field and no
    /// project default, an asset the migration could not make, a font file
    /// deleted from the project afterwards) was silent.</para>
    ///
    /// <para><b>Once per missing font, not once per component.</b> The
    /// projects this matters to are the converted ones, where one absent
    /// <c>.ttf</c> is six thousand labels; six thousand identical lines is not
    /// a diagnostic, it is why people turn the console off. Same rule
    /// <c>OneFontAsset.StandIn</c> already follows for the same
    /// reason.</para>
    /// </summary>
    public static class MissingFonts
    {
        // Not cleared as it grows: it is bounded by the number of distinct
        // fonts a project is missing, which is small enough to be a list of
        // things to go and find. A domain reload empties it, which is the
        // right moment to say it all again.
        private static readonly HashSet<string> s_said = new HashSet<string>();

        /// <summary>
        /// Says, once, that <paramref name="font"/> is not there.
        ///
        /// <paramref name="drawing"/> is the difference between the two
        /// outcomes, and it is a real difference: with the system tier on and a
        /// font on the machine, the text is legible but in a face the project
        /// did not choose and another device may not have. Without it the text
        /// is not on screen at all.
        /// </summary>
        public static void Warn(Object context, OneFontAsset font, bool drawing)
        {
            string reason = Reason(font);
            // Keyed on the reason rather than on the object, so the second
            // label with the same missing font is silent and a different
            // missing font still speaks.
            if (!s_said.Add(reason + (drawing ? "|drawing" : "|blank"))) return;

            Debug.LogWarning("OneText: " + reason + ". " + (drawing
                    ? "Text is being drawn in a font from this device instead, so it will not " +
                      "look the same on every device — and on one that has nothing for the " +
                      "characters, it will not draw at all."
                    : "Text using it does not draw. The device's own fonts are not standing in " +
                      "either" + (SystemFonts.Enabled
                          ? ": nothing on this machine has the characters, or there is no font " +
                            "folder to look in (Web has none)."
                          : ", because system font fallback is off in Project Settings > OneText."))
                + " Assign a font on the component, or set a project default in " +
                "Project Settings > OneText.", context);
        }

        private static string Reason(OneFontAsset font)
        {
            if (font == null)
            {
                var settings = OneTextSettings.Instance;
                if (settings == null)
                    return "no font is assigned and this project has no OneText settings asset " +
                           "to take a default from";
                return settings.DefaultFont == null
                    ? "no font is assigned and the project has no default font"
                    : $"no font is assigned and the project default '{settings.DefaultFont.name}' " +
                      "has no font file in it";
            }

            if (font.IsPlaceholder)
            {
                string expected = font.Recovery.ExpectedFileName;
                return $"the font '{font.FamilyName}' is a placeholder the TextMesh Pro " +
                       "migration left behind, still waiting for " +
                       (string.IsNullOrEmpty(expected) ? "its source .ttf/.otf" : expected);
            }

            return $"the font asset '{font.name}' has no font file in it";
        }

        /// <summary>Forgets what has been said. For tests, which assert on it.</summary>
        public static void Forget() => s_said.Clear();
    }
}
