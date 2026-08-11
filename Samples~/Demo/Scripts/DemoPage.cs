using UnityEngine;

namespace OneText.Samples
{
    /// <summary>
    /// One argument, on one screen.
    ///
    /// The first version of this demo was a feature list — Arabic here,
    /// vertical text there, ruby below — and a feature list cannot persuade
    /// anybody, because every text library's feature list says the same words.
    /// Someone reading "Arabic: yes" learns nothing they could not have
    /// guessed, and nothing that distinguishes one implementation from another.
    ///
    /// A page here makes a claim instead, and it has to earn it with two things
    /// a feature list does not have:
    ///
    /// <b>The wrong answer, next to the right one.</b> Not a description of
    /// what goes wrong without shaping — the actual broken rendering, produced
    /// live by the same engine with the step switched off. Arabic letters that
    /// do not join are obviously broken to somebody who cannot read Arabic;
    /// a sentence claiming they would be is not.
    ///
    /// <b>The mechanism, visible.</b> The glyph stream under the string, the
    /// atlas tile a letter came out of, the three channels of a multi-channel
    /// field. What separates implementations is what they do inside, so what is
    /// inside is what the demo has to show.
    ///
    /// Pages are plain objects rather than MonoBehaviours: they are built into
    /// a rect handed to them and ticked by the shell, and none of them wants an
    /// independent lifetime.
    /// </summary>
    internal abstract class DemoPage
    {
        /// <summary>Tab label. Short — it sits in a row of them.</summary>
        internal abstract string Title { get; }

        /// <summary>
        /// The claim, in one line, shown under the tab bar. This is the thing
        /// the page has to prove; if a page cannot state one, it is a feature
        /// list again and should not be a page.
        /// </summary>
        internal abstract string Claim { get; }

        protected OneTextDemoFonts Fonts { get; private set; }

        protected RectTransform Host { get; private set; }

        internal void Attach(RectTransform host, OneTextDemoFonts fonts)
        {
            Host = host;
            Fonts = fonts;
            Build(host);
        }

        protected abstract void Build(RectTransform host);

        /// <summary>
        /// Called every frame while this page is the visible one. Pages that
        /// only draw once leave it empty; the shell does not tick hidden pages,
        /// so a page reading the atlas every frame costs nothing while it is
        /// behind another tab.
        /// </summary>
        internal virtual void Tick() { }
    }
}
