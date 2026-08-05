using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// Batches atlas uploads to one per frame.
    ///
    /// Uploading the atlas costs a full <c>Texture2DArray.Apply</c> — megabytes,
    /// however small the glyph was. Doing that inside every label's mesh rebuild
    /// meant a screenful of changing labels paid it dozens of times per frame.
    /// Instead each label asks for an upload, and the scheduler performs exactly
    /// one at <see cref="CanvasUpdate.LatePreRender"/>, after every graphic in
    /// the same canvas pass has rebuilt and before anything draws.
    /// </summary>
    public sealed class AtlasFlushScheduler : ICanvasElement
    {
        private static readonly AtlasFlushScheduler s_instance = new AtlasFlushScheduler();
        private static bool s_queued;

        /// <summary>
        /// Forces uploads to wait for an explicit <see cref="FlushNow"/>. Only
        /// useful for benchmarks that drive rebuilds without a canvas pass.
        /// </summary>
        public static bool DeferOutsidePlayMode { get; set; }

        private static bool s_hooked;

        private AtlasFlushScheduler() { }

        /// <summary>
        /// Hooks the canvas pass before the first scene loads, so the very
        /// first frame that shows text uploads its glyphs in that same frame.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void HookOnPlay() => EnsureHooked();

        /// <summary>Asks for the shared atlas to be uploaded before the next draw.</summary>
        public static void Request()
        {
            if (!SharedGlyphAtlas.Atlas.HasPendingUpload) return;

            // Outside play mode there is no reliable canvas pass to hook (editor
            // previews and batch-mode captures render on demand), so upload now.
            if (!Application.isPlaying && !DeferOutsidePlayMode)
            {
                SharedGlyphAtlas.Atlas.Flush();
                return;
            }

            EnsureHooked();
        }

        /// <summary>
        /// Subscribes to the start of the canvas pass, once.
        ///
        /// Registering from here is the whole point: tiles are baked inside
        /// <c>OnPopulateMesh</c>, which runs inside uGUI's graphic rebuild loop,
        /// and uGUI refuses — with an error, every rebuild — to accept a new
        /// canvas element while that loop is running. <c>willRenderCanvases</c>
        /// fires just before the loop, so the element is already queued when
        /// the bakes happen and its <see cref="CanvasUpdate.LatePreRender"/>
        /// pass uploads them in the same frame.
        /// </summary>
        private static void EnsureHooked()
        {
            if (s_hooked) return;
            s_hooked = true;
            Canvas.willRenderCanvases += OnWillRenderCanvases;
        }

        private static void OnWillRenderCanvases()
        {
            if (s_queued) return;
            s_queued = true;
            CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(s_instance);
        }

        /// <summary>Uploads immediately, if anything is pending.</summary>
        public static void FlushNow()
        {
            s_queued = false;
            SharedGlyphAtlas.Atlas.Flush();
        }

        void ICanvasElement.Rebuild(CanvasUpdate executing)
        {
            if (executing != CanvasUpdate.LatePreRender) return;
            FlushNow();
        }

        Transform ICanvasElement.transform => null;

        bool ICanvasElement.IsDestroyed() => false;

        void ICanvasElement.LayoutComplete() { }

        void ICanvasElement.GraphicUpdateComplete()
        {
            // The registry drops the element after the pass; a later bake has to
            // register again.
            s_queued = false;
        }
    }
}
