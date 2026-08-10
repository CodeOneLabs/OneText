namespace OneText
{
    /// <summary>
    /// How many atlas texels a piece of text asks for per em, as a multiple of
    /// what its size implies.
    ///
    /// <para>The rungs are named rather than numbered because the multiplier
    /// they stand for is not the same on both sides. World text is magnified by
    /// a camera whose distance the component cannot see, so its ladder is
    /// 1, 2 and 4 and the member's own value is that multiplier
    /// (<see cref="TextQualityScale.ForWorld"/>). A canvas label is magnified
    /// by a canvas scale factor, which on the screens that exist is between one
    /// and about three, so its ladder is 1, 1.5 and 2
    /// (<see cref="TextQualityScale.ForCanvas"/>): four times the density on a
    /// label would cost sixteen times the atlas area for texels no display can
    /// show.</para>
    ///
    /// <para>The 0.2.0 changelog said a UI label needed nothing like this,
    /// because "its font size is in screen pixels, so the density it asks for
    /// is the density it gets". That is only true on an unscaled canvas. A
    /// CanvasScaler set to Scale With Screen Size — the setting most projects
    /// ship — draws a 36-point label at 108 screen pixels, and nothing in this
    /// package reads the scale factor, so the label is still baked at the 32
    /// bucket and blown up three times. This is the knob that says so.</para>
    /// </summary>
    public enum TextQuality
    {
        /// <summary>
        /// Whatever the project says, from Project Settings &gt; OneText.
        ///
        /// The zero, deliberately: a field added to a component that is already
        /// serialized in six thousand prefabs reads back as zero on every one
        /// of them, and "ask the project" is the only answer that is right for
        /// all six thousand at once. It is also what makes the project setting
        /// worth having — a knob nothing inherits from is a knob nobody can
        /// use after the fact.
        /// </summary>
        Project = 0,

        /// <summary>The size as-is: one texel per pixel the size asks for.</summary>
        Performance = 1,

        /// <summary>
        /// Denser: twice for world text, one and a half times on a canvas.
        /// </summary>
        Medium = 2,

        /// <summary>
        /// Denser still — four times for world text, twice on a canvas — and
        /// the last rung there is.
        ///
        /// Nothing above it would reliably do anything: the atlas density
        /// ladder stops at 256 pixels per em, so past about 64 points this
        /// already asks for more than exists and a larger multiplier would
        /// change the setting without changing the picture. If four times is
        /// not enough for a world mesh, the variable is the magnification
        /// rather than the multiplier — no fixed number can follow a camera —
        /// and the answer is a denser ladder or a density chosen from the
        /// screen, not another member here.
        /// </summary>
        High = 4,
    }

    /// <summary>
    /// What a <see cref="TextQuality"/> rung is worth, on each of the two
    /// ladders, with <see cref="TextQuality.Project"/> already resolved.
    ///
    /// One place, so a component never has to remember which ladder it is on
    /// and the project default is read the same way from both.
    /// </summary>
    public static class TextQualityScale
    {
        /// <summary>
        /// The project's rung, from Project Settings &gt; OneText, or
        /// <see cref="TextQuality.Performance"/> when there is no settings
        /// asset — the same answer the settings asset ships with, so creating
        /// one changes nothing until somebody edits it.
        /// </summary>
        public static TextQuality Project
        {
            get
            {
                var settings = OneTextSettings.Instance;
                return settings != null ? settings.DefaultQuality : TextQuality.Performance;
            }
        }

        /// <summary>The rung itself, with Project resolved against the project.</summary>
        public static TextQuality Resolve(TextQuality quality)
        {
            if (quality != TextQuality.Project) return quality;
            var project = Project;
            // A settings asset whose own field somehow says Project would
            // otherwise loop. It cannot through the inspector; it can through
            // a hand-edited YAML or an older asset.
            return project == TextQuality.Project ? TextQuality.Performance : project;
        }

        /// <summary>
        /// Texels per em per point of size, for text in the world: 1, 2 or 4.
        /// The member's value is the multiplier, which is why the enum has the
        /// numbers it has.
        /// </summary>
        public static float ForWorld(TextQuality quality) => (int)Resolve(quality);

        /// <summary>
        /// Texels per em per pixel of size, for text on a canvas: 1, 1.5 or 2.
        ///
        /// Half the world ladder above Performance, because what magnifies a
        /// label is a canvas scale factor rather than a camera, and that is a
        /// small number with a ceiling. Two covers a 2x reference-resolution
        /// scale outright; the cost is four times the atlas area for the tiles
        /// a label at that setting uses, and a project full of them will want
        /// the atlas budget raised to match.
        /// </summary>
        public static float ForCanvas(TextQuality quality)
        {
            switch (Resolve(quality))
            {
                case TextQuality.Medium: return 1.5f;
                case TextQuality.High: return 2f;
                default: return 1f;
            }
        }
    }
}
