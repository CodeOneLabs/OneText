using UnityEngine;

namespace OneText.Samples
{
    /// <summary>
    /// The original feature tour, kept as the last tab.
    ///
    /// It is last on purpose. A tour is what you look at once you already
    /// believe the engine is doing something hard — before that it reads as a
    /// list of words every library prints. The pages ahead of it earn the
    /// belief; this one shows the breadth once it has been earned.
    ///
    /// It is also the page that carries the draw-call claim, because that claim
    /// only means anything with a screen this busy behind it: fourteen animated
    /// effects, six scripts and a few hundred labels, and the batch counter
    /// still reading what it reads.
    /// </summary>
    internal sealed class OverviewPage : DemoPage
    {
        private readonly DemoShell _shell;

        internal OverviewPage(DemoShell shell)
        {
            _shell = shell;
        }

        internal override string Title => "Tour";

        internal override string Claim =>
            "Effects, markup, six scripts and several hundred labels — " +
            "and the batch counter beside them.";

        protected override void Build(RectTransform host)
        {
            // Built on an inactive object so the tour's Awake runs after it has
            // been told where to build. Adding the component to a live object
            // would run Awake immediately and stand up a second canvas over the
            // shell's own.
            var go = new GameObject("tour");
            go.SetActive(false);
            go.transform.SetParent(host, false);

            var demo = go.AddComponent<OneTextDemo>();
            demo.HostIn(host, _shell != null ? _shell.SharedFonts : Fonts);
            go.SetActive(true);
        }
    }
}
