using System.Collections.Generic;
using UnityEngine;

namespace OneText.UGUI
{
    /// <summary>
    /// Measures how many screen pixels one canvas unit of a label actually
    /// covers, so the atlas can be asked for the density the screen will show
    /// rather than the one the font size implies.
    ///
    /// A font size is in canvas units. Between those units and the screen sit
    /// the canvas scale factor, every ancestor transform's scale, and — for a
    /// canvas drawn by a camera — the projection: an orthographic size, or a
    /// perspective divide that shrinks as the camera dollies in. None of that
    /// is visible from inside a mesh rebuild, and none of it dirties a label
    /// when it changes: a camera flying toward a world-space sign re-renders
    /// the same mesh off the same tile at a growing magnification, and nothing
    /// in uGUI tells the label so.
    ///
    /// Hence the second half of this file: labels register here, and once per
    /// canvas pass each one re-measures. The label applies the number through
    /// a hysteresis band (see <see cref="OneTextLabel.RefreshPpemScale"/>), so
    /// the per-frame cost of a label whose magnification is not changing is
    /// this measurement and a compare — no rebuild, no bake, no upload.
    /// </summary>
    public static class ScreenPpem
    {
        /// <summary>
        /// Screen pixels per canvas unit at <paramref name="label"/>, or 0 when
        /// it cannot be known this frame (no scale at all, or a perspective
        /// camera with the label at or behind its plane) — the caller keeps
        /// whatever it last applied.
        ///
        /// The lossy scale carries everything transforms contribute, including
        /// the scale factor an overlay canvas writes into its root; the camera
        /// factor converts world units to pixels for the two render modes that
        /// have a camera between the canvas and the screen. Anisotropy is
        /// resolved toward the larger axis: a label stretched 2x wide is baked
        /// for its wide axis and the narrow one is minified, which a distance
        /// field survives far better than the reverse.
        /// </summary>
        public static float Compute(OneTextLabel label) =>
            Compute(label, Context.For(label.canvas));

        /// <summary>
        /// What every label on one canvas shares: the render mode, and the
        /// camera's own numbers.
        ///
        /// Read once per poll rather than once per label. The camera reads are
        /// property calls into the engine, and two hundred nameplates asking
        /// the same camera the same six questions every frame is most of what
        /// measuring density cost.
        /// </summary>
        internal readonly struct Context
        {
            public readonly bool ScaleOnly;      // overlay, or nothing to ask
            public readonly bool Orthographic;
            public readonly float PixelsPerWorldUnit;   // orthographic only
            public readonly float PixelHeight;          // perspective only
            public readonly float TanHalfFov;
            public readonly Vector3 CameraPosition;
            public readonly Vector3 CameraForward;

            private Context(bool scaleOnly, bool orthographic, float pixelsPerWorldUnit,
                float pixelHeight, float tanHalfFov, Vector3 position, Vector3 forward)
            {
                ScaleOnly = scaleOnly;
                Orthographic = orthographic;
                PixelsPerWorldUnit = pixelsPerWorldUnit;
                PixelHeight = pixelHeight;
                TanHalfFov = tanHalfFov;
                CameraPosition = position;
                CameraForward = forward;
            }

            private static readonly Context Scale =
                new Context(true, false, 0f, 0f, 0f, Vector3.zero, Vector3.forward);

            public static Context For(Canvas canvas)
            {
                if (canvas == null) return Scale;
                var root = canvas.rootCanvas;
                if (root.renderMode == RenderMode.ScreenSpaceOverlay) return Scale;

                var camera = root.worldCamera;
                // A world-space canvas with no event camera assigned is still
                // drawn by whatever camera looks at it; the main camera is the
                // only answer available from here. A screen-space canvas with
                // no camera is defined by Unity to fall back to overlay
                // behaviour.
                if (camera == null && root.renderMode == RenderMode.WorldSpace) camera = Camera.main;
                if (camera == null) return Scale;

                if (camera.orthographic)
                    return new Context(false, true,
                        camera.pixelHeight / Mathf.Max(2f * camera.orthographicSize, 1e-6f),
                        0f, 0f, Vector3.zero, Vector3.forward);

                var t = camera.transform;
                return new Context(false, false, 0f, camera.pixelHeight,
                    Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), t.position, t.forward);
            }
        }

        /// <summary>The per-label half: a scale, and one dot product in perspective.</summary>
        internal static float Compute(OneTextLabel label, in Context context)
        {
            Vector3 lossy = label.rectTransform.lossyScale;
            float local = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y));
            if (float.IsNaN(local) || float.IsInfinity(local) || local <= 0f) return 0f;
            if (context.ScaleOnly) return local;

            if (context.Orthographic) return local * context.PixelsPerWorldUnit;

            // Depth along the view axis, not Euclidean distance: the
            // perspective divide is by the z the camera sees, and a label at
            // the edge of the frame is not larger for being further from the
            // lens.
            float depth = Vector3.Dot(label.rectTransform.position - context.CameraPosition,
                context.CameraForward);
            if (depth <= 1e-4f) return 0f;
            return local * context.PixelHeight / (2f * depth * context.TanHalfFov);
        }

        // ----------------------------------------------------------- watcher

        private static readonly List<OneTextLabel> s_labels = new List<OneTextLabel>();
        private static bool s_hooked;

        /// <summary>
        /// Clears the per-session registry. With Domain Reload off the list
        /// survives into the next play session carrying destroyed labels; the
        /// willRenderCanvases subscription is deliberately kept — it is
        /// idempotent and re-hooking would need the same flag it already has.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession() => s_labels.Clear();

        public static void Register(OneTextLabel label)
        {
            if (label == null || s_labels.Contains(label)) return;
            s_labels.Add(label);
            if (s_hooked) return;
            s_hooked = true;
            Canvas.willRenderCanvases += OnWillRenderCanvases;
        }

        public static void Unregister(OneTextLabel label) => s_labels.Remove(label);

        /// <summary>
        /// One measurement per label per canvas pass. uGUI's own rebuild loop
        /// subscribed to this event before we did (the registry exists from the
        /// first Graphic), so a label dirtied here rebuilds on the next pass,
        /// one frame later — invisible next to the hysteresis band, which is
        /// what actually decides how often anything rebuilds.
        /// </summary>
        private static void OnWillRenderCanvases() => PollNow();

        /// <summary>
        /// Re-measures every registered label immediately. The canvas pass
        /// calls this each frame; code that renders without one — a headless
        /// capture, a test — calls it by hand, the way
        /// <see cref="AtlasFlushScheduler.FlushNow"/> serves the same callers
        /// on the upload side.
        /// </summary>
        public static void PollNow()
        {
            Canvas cached = null;
            var context = default(Context);
            bool have = false;

            for (int i = s_labels.Count - 1; i >= 0; i--)
            {
                var label = s_labels[i];
                if (label == null)
                {
                    s_labels.RemoveAt(i);
                    continue;
                }

                // Labels are registered in creation order, so a scene's worth
                // of them shares one canvas and this reads it once. A scene
                // with several canvases re-reads at each boundary, which is
                // still once per canvas per frame rather than once per label.
                var canvas = label.canvas;
                if (!have || !ReferenceEquals(canvas, cached))
                {
                    cached = canvas;
                    context = Context.For(canvas);
                    have = true;
                }

                if (label.RefreshPpemScale(context)) label.SetVerticesDirty();
            }
        }
    }
}
