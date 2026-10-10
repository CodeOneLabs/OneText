using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// Rebuilds text meshes when the atlas moves tiles under them.
    ///
    /// A mesh bakes the UV rect of every tile it draws, so a tile that is
    /// evicted or relocated by a compaction leaves stale UVs behind: the
    /// wrong glyph, or none. The atlas reports this as a version change, and
    /// graphics registered here rebuild once per change.
    ///
    /// Version changes can happen inside a canvas rebuild, where uGUI refuses
    /// to queue another graphic. Check again after that pass and complete any
    /// repairs before drawing, so old UVs never sample newly relocated tiles.
    /// </summary>
    public static class AtlasInvalidation
    {
        // A rebuild can itself evict, so a starved atlas could rebuild forever.
        // After this many consecutive invalidating frames, back off and say so.
        private const int ThrashLimit = 30;
        private const int BackoffFrames = 120;

        private static readonly List<Graphic> s_users = new List<Graphic>();
        private static GlyphAtlas s_atlas;
        private static int s_version;
        private static int s_colorVersion;
        private static int s_preciseVersion;
        private static int s_consecutive;
        private static int s_backoff;
        private static bool s_warned;
        private static bool s_editorDriving;
        private static AtlasWatcher s_watcher;
        private static bool s_canvasHooked;
        private static bool s_repairing;
        private const int MaxRepairPasses = 4;

        /// <summary>
        /// Clears everything that belongs to a play session. With Domain Reload
        /// turned off these statics survive from the previous session, carrying
        /// destroyed graphics, a disposed atlas and a spent backoff into a run
        /// that has nothing to do with them. This attribute fires on every entry
        /// into play mode, reload or not, which is exactly the scope wanted.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            Canvas.willRenderCanvases -= RepairBeforeDraw;
            s_canvasHooked = false;
            s_repairing = false;
            s_users.Clear();
            s_atlas = null;
            s_version = 0;
            s_colorVersion = 0;
            s_preciseVersion = 0;
            s_consecutive = 0;
            s_backoff = 0;
            s_warned = false;
            // The watcher was a scene object; leaving play mode destroyed it.
            // Holding the stale reference would suppress the next one, and the
            // symptom is text that never rebuilds after a compaction: blank
            // labels and ghost quads, in the second play session only.
            s_watcher = null;
        }

        public static void Register(Graphic graphic)
        {
            if (graphic == null || s_users.Contains(graphic)) return;
            s_users.Add(graphic);
            EnsureDriver();
        }

        public static void Unregister(Graphic graphic) => s_users.Remove(graphic);

        /// <summary>Checks the atlas once and rebuilds registered graphics if it changed.</summary>
        public static void Poll() => QueueChangedMeshes();

        private static bool QueueChangedMeshes()
        {
            if (!SharedGlyphAtlas.Exists) return false;

            var atlas = SharedGlyphAtlas.Atlas;
            // The colour atlas evicts too, and a mesh drawing an emoji has that
            // tile's uv rect baked into it exactly as a text mesh does. Watching
            // only the SDF atlas leaves an evicted emoji sampling whatever was
            // written over it: the very failure this class exists to prevent,
            // on the other atlas.
            int colorVersion = SharedGlyphAtlas.ColorAtlasExists
                ? SharedGlyphAtlas.ColorAtlas.Version
                : 0;
            // And the precise atlas, for the same reason again: a label with
            // `precise` on bakes its uv rects from that one.
            int preciseVersion = SharedGlyphAtlas.PreciseAtlasExists
                ? SharedGlyphAtlas.PreciseAtlas.Version
                : 0;

            if (atlas == s_atlas && atlas.Version == s_version && colorVersion == s_colorVersion &&
                preciseVersion == s_preciseVersion)
            {
                s_consecutive = 0;
                return false;
            }

            s_atlas = atlas;
            s_version = atlas.Version;
            s_colorVersion = colorVersion;
            s_preciseVersion = preciseVersion;

            if (s_backoff > 0)
            {
                s_backoff--;
                return false;
            }

            if (++s_consecutive > ThrashLimit)
            {
                s_backoff = BackoffFrames;
                s_consecutive = 0;
                if (!s_warned)
                {
                    s_warned = true;
                    var stats = atlas.GetStats();
                    Debug.LogWarning("OneText: the glyph atlas is evicting tiles that are still on " +
                        $"screen ({stats.TileCount} tiles, {stats.UsedFraction * 100f:0}% full, " +
                        $"{stats.Evictions} evictions). Raise the atlas budget in " +
                        "Project Settings > OneText, or draw fewer distinct glyphs and sizes.");
                }
                return false;
            }

            for (int i = s_users.Count - 1; i >= 0; i--)
            {
                if (s_users[i] == null) s_users.RemoveAt(i);
                else s_users[i].SetVerticesDirty();
            }
            return true;
        }

        private static void RepairBeforeDraw()
        {
            if (s_repairing || CanvasUpdateRegistry.IsRebuildingGraphics() ||
                CanvasUpdateRegistry.IsRebuildingLayout()) return;
            s_repairing = true;
            try
            {
                // This callback follows the registry's complete graphic pass.
                // Recursive canvas updates re-enter this event, hence the guard.
                for (int pass = 0; pass < MaxRepairPasses; pass++)
                {
                    // Settled, or backing off. Clearing labels during the
                    // back-off would rebuild them anyway and keep the atlas
                    // evicting, and a label with no font to build with would
                    // look stale on every frame.
                    if (!QueueChangedMeshes()) return;
                    Canvas.ForceUpdateCanvases();
                }
                // The passes are spent: an undersized atlas may never hold
                // every visible glyph. Do not display another character in
                // that case; retry next frame. A culled label draws nothing and
                // uGUI skips its rebuild, so it is left dirty for when it unculls.
                foreach (var graphic in s_users)
                {
                    if (graphic is OneTextLabel label && label.isActiveAndEnabled &&
                        !label.HasCurrentAtlasQuads && !label.canvasRenderer.cull)
                    {
                        label.canvasRenderer.Clear();
                        label.SetVerticesDirty();
                        // Clear also removes the material bindings.
                        label.SetMaterialDirty();
                    }
                }
            }
            finally { s_repairing = false; }
        }

        private static void EnsureDriver()
        {
            // The editor update loop outlives play sessions, so this subscription
            // is taken once and kept; the watcher does not, so it is checked by
            // reference every time rather than by a "did we already do this" flag.
#if UNITY_EDITOR
            if (!s_editorDriving)
            {
                s_editorDriving = true;
                UnityEditor.EditorApplication.update += Poll;
            }
#endif
            if (!Application.isPlaying) return;
            if (!s_canvasHooked)
            {
                // Construct the registry before subscribing: repairs must run
                // after PerformUpdate, never inside its graphic rebuild loop.
                _ = CanvasUpdateRegistry.instance;
                Canvas.willRenderCanvases += RepairBeforeDraw;
                s_canvasHooked = true;
            }
            if (s_watcher != null) return;

            var host = new GameObject("OneText Atlas Watcher") { hideFlags = HideFlags.HideAndDontSave };
            s_watcher = host.AddComponent<AtlasWatcher>();
            Object.DontDestroyOnLoad(host);
        }

        /// <summary>Ticks <see cref="Poll"/> once per frame in play mode.</summary>
        private sealed class AtlasWatcher : MonoBehaviour
        {
            private void LateUpdate() => Poll();
        }
    }
}
