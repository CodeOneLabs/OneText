using System.Globalization;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using OneText;
using OneText.UGUI;
using UnityEngine;

// DOTween's own namespace, not OneText's, and deliberately: every call site
// this assembly exists to rescue already has `using DG.Tweening;` at the top,
// because that is where DOTween Pro put the TMP shortcuts. Putting the
// replacements anywhere else would mean editing every file that tweens text,
// which is the cost this assembly exists to avoid.
namespace DG.Tweening
{
    /// <summary>
    /// DOTween Pro's TextMesh Pro shortcuts, re-pointed at OneText.
    ///
    /// <c>ShortcutExtensionsTMPText</c> extends <c>TMP_Text</c>, and only
    /// <c>TMP_Text</c>. So a project that migrates off TMP loses every text
    /// tween it has at compile time, with nothing wrong at the call sites: the
    /// code is fine, the extension methods simply stopped applying to the type.
    /// These are the same methods — same names, same argument order, same
    /// defaults, same return types, same <c>SetTarget</c> and <c>SetOptions</c>
    /// conventions — extending <see cref="OneTextLabel"/> and
    /// <see cref="OneTextMesh"/>. A migrated call site compiles unchanged.
    ///
    /// This is the only third-party integration OneText ships. DOTween was by
    /// a wide margin the largest thing standing in the way of a real migration,
    /// so it gets an assembly; that is a decision about DOTween specifically
    /// and not the first entry in a plugin framework.
    ///
    /// <para><b>Turning it on.</b> The assembly is constrained to
    /// <c>ONETEXT_DOTWEEN</c> and compiles to nothing without it, so a project
    /// with no DOTween carries no reference to a type it does not have.
    /// Installing DOTween from OpenUPM (<c>com.demigiant.dotween</c>) defines
    /// the symbol through this asmdef's <c>versionDefines</c> and needs nothing
    /// further. The Asset Store build — which is what most projects have — is a
    /// DLL under <c>Assets/Plugins/Demigiant</c> with no package manifest for a
    /// version expression to match, so there the symbol goes in by hand:
    /// Project Settings &gt; Player &gt; Scripting Define Symbols, once per
    /// build target group. The Hub offers the same thing as a button.</para>
    ///
    /// <para>The asmdef names no DOTween assembly in its references, and must
    /// not: the Asset Store install has no asmdef to name, and an asmdef
    /// reference that resolves to nothing is a hard error rather than a
    /// silently dropped one. Both installs are auto-referenced instead — the
    /// plugin DLL by its importer, the OpenUPM package by its own asmdefs —
    /// which is why <c>overrideReferences</c> stays false here.</para>
    ///
    /// <para><b>What is missing, and why.</b> Four TMP shortcuts have no
    /// honest counterpart and are left out rather than approximated:
    /// <c>DOFaceColor</c>, <c>DOFaceFade</c>, <c>DOOutlineColor</c> and
    /// <c>DOGlowColor</c> tween named properties of TMP's SDF material
    /// (<c>_FaceColor</c>, <c>_OutlineColor</c>, <c>_GlowColor</c>), and
    /// OneText's material is a different shader with a different face, outline
    /// and glow model — the outline and glow are per-quad decoration packed
    /// into vertex data, not material floats. <c>DOTweenTMPAnimator</c> is out
    /// for the related reason that it reaches into <c>TMP_MeshInfo</c> and
    /// rewrites vertices behind the text object's back; OneText's per-character
    /// animation goes through <c>ITextQuadModifier</c>, which is a different
    /// shape of API and not something a wrapper can fake.</para>
    /// </summary>
    public static class ShortcutExtensionsOneText
    {
        #region OneTextLabel

        /// <summary>Tweens a OneTextLabel's color to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOColor(this OneTextLabel target, Color endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.To(() => target.color, x => target.color = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's alpha to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOFade(this OneTextLabel target, float endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.ToAlpha(() => target.color, x => target.color = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's font size to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations.
        /// <para>Does nothing visible while <see cref="OneTextLabel.AutoSize"/> is on, for the
        /// same reason assigning the property does: auto-size is the thing choosing the size.</para></summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<float, float, FloatOptions> DOFontSize(this OneTextLabel target, float endValue, float duration)
        {
            TweenerCore<float, float, FloatOptions> t = DOTween.To(() => target.FontSize, x => target.FontSize = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's scale to the given value (uniform, as the TMP shortcut did).
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Vector3, Vector3, VectorOptions> DOScale(this OneTextLabel target, float endValue, float duration)
        {
            Transform trans = target.transform;
            Vector3 endValueV3 = new Vector3(endValue, endValue, endValue);
            TweenerCore<Vector3, Vector3, VectorOptions> t = DOTween.To(() => trans.localScale, x => trans.localScale = x, endValueV3, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>
        /// Tweens a OneTextLabel's text from one integer to another, with options for thousands separators
        /// </summary>
        /// <param name="fromValue">The value to start from</param>
        /// <param name="endValue">The end value to reach</param>
        /// <param name="duration">The duration of the tween</param>
        /// <param name="addThousandsSeparator">If TRUE (default) also adds thousands separators</param>
        /// <param name="culture">The <see cref="CultureInfo"/> to use (InvariantCulture if NULL)</param>
        public static TweenerCore<int, int, NoOptions> DOCounter(
            this OneTextLabel target, int fromValue, int endValue, float duration, bool addThousandsSeparator = true, CultureInfo culture = null
        ){
            int v = fromValue;
            CultureInfo cInfo = !addThousandsSeparator ? null : culture ?? CultureInfo.InvariantCulture;
            // A counter really does change its string every step, so this one
            // does lay the text out again every step and there is no way around
            // it. It is a handful of digits; the reason to say so is that every
            // other shortcut here was written to avoid exactly that, and this
            // is the one place where the cost is the feature.
            TweenerCore<int, int, NoOptions> t = DOTween.To(() => v, x => {
                v = x;
                target.Text = addThousandsSeparator
                    ? v.ToString("N0", cInfo)
                    : v.ToString();
            }, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's reveal to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations.
        /// <para>Named for TMP's <c>maxVisibleCharacters</c> so call sites carry over, but the
        /// unit is OneText's: grapheme clusters, not UTF-16 characters. A line of Hangul or a
        /// flag emoji therefore reveals in fewer steps here than it did there, and correctly.</para></summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<int, int, NoOptions> DOMaxVisibleCharacters(this OneTextLabel target, int endValue, float duration)
        {
            // -1 is OneText's "nothing is holding text back", and it is where an
            // untouched label sits; TMP's equivalent resting value was a large
            // positive number. Tweening from a literal -1 would blank the label
            // for a frame and then count up from nothing, so the start value is
            // read as the number of clusters that -1 is actually showing.
            TweenerCore<int, int, NoOptions> t = DOTween.To(
                () => target.MaxVisibleGraphemes < 0 ? target.GraphemeCount : target.MaxVisibleGraphemes,
                x => target.MaxVisibleGraphemes = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's text to the given value by revealing it.
        /// Also stores the label as the tween's target so it can be used for filtered operations.
        ///
        /// <para>The one shortcut here that is not a transcription of DOTween's. TMP's
        /// <c>DOText</c> assigns a longer prefix of the string every frame, and doing that to a
        /// OneTextLabel would be wrong three times over: every assignment re-shapes and re-wraps
        /// the whole string, a prefix cut at a UTF-16 boundary shows half a cluster and flips
        /// Arabic joining forms as the next letter arrives, and — the one that is not a matter of
        /// degree — assigning <c>Text</c> rewinds a running reveal, so a label with a typewriter
        /// on it would restart every frame and display nothing at all, for ever.</para>
        ///
        /// <para>So this sets the text once and drives
        /// <see cref="OneTextLabel.MaxVisibleGraphemes"/>, which is the counter OneText has for
        /// exactly this and which rebuilds only the mesh. The text is laid out once, clusters
        /// arrive whole, and the reveal steps in the label's own
        /// <see cref="OneTextLabel.RevealGranularity"/> — so a label set to reveal by syllable
        /// still reveals by syllable when DOTween is the one driving it. The mapping from
        /// DOTween's string position to a reveal unit is proportional rather than exact, because
        /// what DOTween hands over is UTF-16 with markup in it and the reveal counts laid-out
        /// clusters; both ends line up exactly, which is what a caller can see.</para>
        ///
        /// <para><paramref name="richTextEnabled"/> is honoured by construction: the reveal
        /// counts glyphs, and a markup tag never was one. <paramref name="scrambleMode"/> and
        /// <paramref name="scrambleChars"/> are passed to DOTween unchanged and still shape the
        /// string it computes, but a reveal shows the real text or nothing, so scrambled
        /// characters do not appear on screen. Code that needs the scramble should tween
        /// <see cref="OneTextLabel.Text"/> itself.</para>
        ///
        /// <para>The label's own typewriter is switched off when this tween first runs. Two
        /// typewriters on one label is not a blend, it is a fight over one counter, and asking
        /// DOTween to type the line is a statement about which one should win.</para></summary>
        /// <param name="endValue">The end string to tween to</param><param name="duration">The duration of the tween</param>
        /// <param name="richTextEnabled">If TRUE (default), rich text will be interpreted correctly while animated,
        /// otherwise all tags will be considered as normal text</param>
        /// <param name="scrambleMode">The type of scramble mode to use, if any</param>
        /// <param name="scrambleChars">A string containing the characters to use for scrambling.
        /// Use as many characters as possible (minimum 10) because DOTween uses a fast scramble mode which gives better results with more characters.
        /// Leave it to NULL (default) to use default ones</param>
        public static TweenerCore<string, string, StringOptions> DOText(this OneTextLabel target, string endValue, float duration, bool richTextEnabled = true, ScrambleMode scrambleMode = ScrambleMode.None, string scrambleChars = null)
        {
            string final = endValue ?? string.Empty;
            string typed = string.Empty;
            bool armed = false;

            TweenerCore<string, string, StringOptions> t = DOTween.To(() => typed, x => {
                typed = x;
                // On the first step rather than at creation, so a tween held by
                // SetDelay or sitting in a Sequence leaves the label showing
                // what it was showing until its turn comes — which is what the
                // TMP shortcut did, and the only part of its behaviour that
                // depended on assigning the text late.
                if (!armed)
                {
                    armed = true;
                    target.CharactersPerSecond = 0f;
                    target.Text = final;
                    target.MaxVisibleGraphemes = 0;
                }

                int units = target.RevealUnitCount;
                if (units <= 0) return;
                // How far DOTween thinks it has typed, measured against the
                // string it is typing towards. Under ScrambleMode the tail is
                // random characters and under rich text the tags arrive whole,
                // and a leading-match count survives both: the prefix is real
                // text in every mode, and the count only ever goes forward.
                float progress = final.Length == 0 ? 1f : (float)MatchingPrefix(x, final) / final.Length;
                int unit = Mathf.Clamp(Mathf.FloorToInt(progress * units), 0, units);
                // Floor, not round: a unit appears when its characters have been
                // typed, not when half of them have.
                target.MaxVisibleGraphemes = target.GraphemeOfRevealUnit(unit);
            }, final, duration);

            t.SetOptions(richTextEnabled, scrambleMode, scrambleChars)
                .SetTarget(target);
            return t;
        }

        #endregion

        #region OneTextMesh

        /// <summary>Tweens a OneTextMesh's color to the given value.
        /// Also stores the mesh as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOColor(this OneTextMesh target, Color endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.To(() => target.Color, x => target.Color = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextMesh's alpha to the given value.
        /// Also stores the mesh as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOFade(this OneTextMesh target, float endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.ToAlpha(() => target.Color, x => target.Color = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextMesh's font size to the given value.
        /// Also stores the mesh as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<float, float, FloatOptions> DOFontSize(this OneTextMesh target, float endValue, float duration)
        {
            TweenerCore<float, float, FloatOptions> t = DOTween.To(() => target.FontSize, x => target.FontSize = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextMesh's scale to the given value (uniform, as the TMP shortcut did).
        /// Also stores the mesh as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Vector3, Vector3, VectorOptions> DOScale(this OneTextMesh target, float endValue, float duration)
        {
            Transform trans = target.transform;
            Vector3 endValueV3 = new Vector3(endValue, endValue, endValue);
            TweenerCore<Vector3, Vector3, VectorOptions> t = DOTween.To(() => trans.localScale, x => trans.localScale = x, endValueV3, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>
        /// Tweens a OneTextMesh's text from one integer to another, with options for thousands separators
        /// </summary>
        /// <param name="fromValue">The value to start from</param>
        /// <param name="endValue">The end value to reach</param>
        /// <param name="duration">The duration of the tween</param>
        /// <param name="addThousandsSeparator">If TRUE (default) also adds thousands separators</param>
        /// <param name="culture">The <see cref="CultureInfo"/> to use (InvariantCulture if NULL)</param>
        public static TweenerCore<int, int, NoOptions> DOCounter(
            this OneTextMesh target, int fromValue, int endValue, float duration, bool addThousandsSeparator = true, CultureInfo culture = null
        ){
            int v = fromValue;
            CultureInfo cInfo = !addThousandsSeparator ? null : culture ?? CultureInfo.InvariantCulture;
            TweenerCore<int, int, NoOptions> t = DOTween.To(() => v, x => {
                v = x;
                target.Text = addThousandsSeparator
                    ? v.ToString("N0", cInfo)
                    : v.ToString();
            }, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextMesh's text to the given value.
        /// Also stores the mesh as the tween's target so it can be used for filtered operations.
        ///
        /// <para>This one is DOTween's implementation verbatim — the string is assigned every
        /// step — because <see cref="OneTextMesh"/> has no reveal counter to drive instead. It
        /// therefore carries TMP's costs as well as TMP's behaviour: the text is laid out again
        /// on every step, and a prefix cut mid-cluster can show for a frame. A world-space label
        /// that types a lot of text, and especially one typing a script that shapes, wants
        /// <see cref="OneTextLabel"/> or its own reveal loop rather than this.</para></summary>
        /// <param name="endValue">The end string to tween to</param><param name="duration">The duration of the tween</param>
        /// <param name="richTextEnabled">If TRUE (default), rich text will be interpreted correctly while animated,
        /// otherwise all tags will be considered as normal text</param>
        /// <param name="scrambleMode">The type of scramble mode to use, if any</param>
        /// <param name="scrambleChars">A string containing the characters to use for scrambling.
        /// Use as many characters as possible (minimum 10) because DOTween uses a fast scramble mode which gives better results with more characters.
        /// Leave it to NULL (default) to use default ones</param>
        public static TweenerCore<string, string, StringOptions> DOText(this OneTextMesh target, string endValue, float duration, bool richTextEnabled = true, ScrambleMode scrambleMode = ScrambleMode.None, string scrambleChars = null)
        {
            TweenerCore<string, string, StringOptions> t = DOTween.To(() => target.Text, x => target.Text = x, endValue, duration);
            t.SetOptions(richTextEnabled, scrambleMode, scrambleChars)
                .SetTarget(target);
            return t;
        }

        #endregion

        /// <summary>
        /// How many characters <paramref name="typed"/> shares with the front of
        /// <paramref name="final"/>: the length of the text DOTween has actually
        /// typed, whatever it has appended behind it.
        /// </summary>
        // ------------------------------------------------- DOTween Pro parity
        //
        // DOTween Pro's TMP module defines these on TMP_Text and nothing else
        // does. They are here for the same reason as everything above — a
        // migrated call site has to compile unchanged — and they matter for one
        // more: while OneText offers a method by name, the migration knows a
        // caller of it can convert without dragging DOTween Pro's own source
        // along, and a vendored file that never moves is a vendored file that
        // never conflicts with these.

        /// <summary>Tweens a OneTextLabel's face colour to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOFaceColor(this OneTextLabel target, Color32 endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.To(
                () => target.faceColor, x => target.faceColor = x, (Color)endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's face alpha to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOFaceFade(this OneTextLabel target, float endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.ToAlpha(
                () => target.faceColor, x => target.faceColor = x, endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's outline colour to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOOutlineColor(this OneTextLabel target, Color32 endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.To(
                () => target.outlineColor, x => target.outlineColor = x, (Color)endValue, duration);
            t.SetTarget(target);
            return t;
        }

        /// <summary>Tweens a OneTextLabel's glow colour to the given value.
        /// Also stores the label as the tween's target so it can be used for filtered operations</summary>
        /// <param name="endValue">The end value to reach</param><param name="duration">The duration of the tween</param>
        public static TweenerCore<Color, Color, ColorOptions> DOGlowColor(this OneTextLabel target, Color32 endValue, float duration)
        {
            TweenerCore<Color, Color, ColorOptions> t = DOTween.To(
                () => target.glowColor, x => target.glowColor = x, (Color)endValue, duration);
            t.SetTarget(target);
            return t;
        }

        private static int MatchingPrefix(string typed, string final)
        {
            if (string.IsNullOrEmpty(typed)) return 0;
            int n = typed.Length < final.Length ? typed.Length : final.Length;
            int i = 0;
            while (i < n && typed[i] == final[i]) i++;
            return i;
        }
    }
}
