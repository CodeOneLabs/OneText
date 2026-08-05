using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The process-wide glyph atlas and the material that samples it.
    ///
    /// One atlas is the point: rasterized glyphs are keyed by font, size bucket
    /// and variation instance, so every label in the project shares the same
    /// cache — a second label showing the same text costs nothing to rasterize.
    /// Sharing the material matters just as much: uGUI can only batch draws
    /// that use the same material, so a per-label material instance would put
    /// every label in its own draw call.
    /// </summary>
    public static class SharedGlyphAtlas
    {
        private const string ShaderName = "OneText/SDF";

        private static GlyphAtlas s_atlas;
        private static ColorGlyphAtlas s_colorAtlas;
        private static Material s_material;
        private static int s_references;

        /// <summary>
        /// The shared RGBA atlas for colour emoji and inline sprites, created on
        /// first use — a project with no colour glyphs never pays for it.
        /// </summary>
        public static ColorGlyphAtlas ColorAtlas
        {
            get
            {
                if (s_colorAtlas != null && !s_colorAtlas.IsUsable) s_colorAtlas = null;
                if (s_colorAtlas != null) return s_colorAtlas;

                s_colorAtlas = new ColorGlyphAtlas();
                if (s_material != null) s_material.SetTexture("_ColorTex", s_colorAtlas.Texture);
                return s_colorAtlas;
            }
        }

        /// <summary>True when a colour atlas exists — asking does not create one.</summary>
        public static bool ColorAtlasExists => s_colorAtlas != null && s_colorAtlas.IsUsable;

        /// <summary>The shared atlas, creating it on first use.</summary>
        public static GlyphAtlas Atlas
        {
            get
            {
                DiscardIfUnusable();
                return s_atlas ??= Create();
            }
        }

        /// <summary>True when an atlas exists — asking does not create one.</summary>
        public static bool Exists => s_atlas != null && s_atlas.IsUsable;

        /// <summary>
        /// Drops state that belongs to the previous play session. With Domain
        /// Reload off these statics survive it; the engine objects they point at
        /// do not necessarily, and a shared atlas whose texture has been
        /// destroyed draws nothing at all.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession() => DiscardIfUnusable();

        /// <summary>
        /// Lets go of an atlas whose texture has been destroyed underneath it.
        ///
        /// The material is the subtle half. It is <c>HideAndDontSave</c>, so it
        /// outlives whatever killed the texture, and it holds <c>_GlyphTex</c>
        /// — meaning a replacement atlas alone would leave every label batching
        /// against the dead texture, which is the blank text this check exists
        /// to prevent. Repointing it is deferred to <see cref="Create"/>'s
        /// caller through <see cref="Rebind"/>: the material getter builds a
        /// material against whatever atlas exists at the time, so the two only
        /// need reconciling when the atlas changes after the material was made.
        /// </summary>
        private static void DiscardIfUnusable()
        {
            if (s_atlas == null || s_atlas.IsUsable) return;

            // Dispose, not drop: the atlas owns staging textures that are
            // themselves DontSave, and nothing else will ever destroy them.
            s_atlas.Dispose();
            s_atlas = null;
            if (s_references > 0) Rebind(Create());
        }

        /// <summary>Points the shared material at a newly built atlas.</summary>
        private static void Rebind(GlyphAtlas atlas)
        {
            if (s_material != null && atlas != null) BindGlyphTexture(atlas);
        }

        /// <summary>
        /// The atlas texture and its texel size, which always travel together.
        /// The shadow half of a text decoration turns an offset in texels into
        /// one in uv, and Unity documents the automatic <c>_TexelSize</c>
        /// companion for 2D textures rather than for arrays — so it is set here
        /// rather than assumed, where forgetting it would be a shadow sitting on
        /// top of its own glyph.
        /// </summary>
        private static void BindGlyphTexture(GlyphAtlas atlas)
        {
            s_material.SetTexture("_GlyphTex", atlas.Texture);
            float size = atlas.Texture.width;
            s_material.SetVector("_GlyphTexelSize",
                new Vector4(1f / size, 1f / atlas.Texture.height, size, atlas.Texture.height));
        }

        private static GlyphAtlas Create()
        {
            var settings = OneTextSettings.Instance;
            s_atlas = new GlyphAtlas(settings != null
                ? settings.AtlasSettings
                : GlyphAtlasSettings.Default);

            // Prewarm after the field is assigned: the charset rasterizes into
            // this atlas, and going through the property would recurse.
            if (Application.isPlaying && settings != null && settings.PrewarmCharset != null)
            {
                var report = settings.PrewarmCharset.Prewarm(s_atlas);
                if (report.Baked > 0) Debug.Log($"OneText {report}");
            }
            return s_atlas;
        }

        /// <summary>
        /// Rebuilds the atlas against the current project settings, throwing
        /// away every cached tile. Only worth calling when the budget itself
        /// changes — the editor does it when the setting is edited.
        /// </summary>
        public static void Reconfigure() => Reconfigure(force: false);

        /// <summary>
        /// Same, but <paramref name="force"/> rebuilds even when the budget is
        /// unchanged — benchmarks need every run to start from an empty atlas.
        /// </summary>
        public static void Reconfigure(bool force)
        {
            var settings = OneTextSettings.Instance;
            var wanted = settings != null ? settings.AtlasSettings : GlyphAtlasSettings.Default;
            if (!force && s_atlas != null && s_atlas.Settings.Equals(wanted)) return;

            s_atlas?.Dispose();
            s_atlas = null;
            if (s_references == 0) return;

            Rebind(Create());
        }

        /// <summary>
        /// The shared SDF material. Returns null (with an error logged once) if
        /// the shader is missing from the build.
        /// </summary>
        public static Material Material
        {
            get
            {
                if (s_material != null) return s_material;

                var shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    Debug.LogError($"{ShaderName} shader not found — add it to Always Included Shaders.");
                    return null;
                }
                s_material = new Material(shader)
                {
                    name = "OneText SDF (shared)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                BindGlyphTexture(Atlas);
                // Only if one exists: creating the colour atlas here would cost
                // 4 MB to every project that never draws an emoji.
                if (s_colorAtlas != null && s_colorAtlas.IsUsable)
                    s_material.SetTexture("_ColorTex", s_colorAtlas.Texture);
                return s_material;
            }
        }

        /// <summary>Takes a reference to the shared atlas; pair with <see cref="Release"/>.</summary>
        public static GlyphAtlas Acquire()
        {
            s_references++;
            return Atlas;
        }

        /// <summary>
        /// Drops a reference. The atlas and material are destroyed when the last
        /// user goes away, so entering and leaving play mode does not leak them.
        /// </summary>
        public static void Release()
        {
            if (--s_references > 0) return;
            s_references = 0;

            s_atlas?.Dispose();
            s_atlas = null;
            s_colorAtlas?.Dispose();
            s_colorAtlas = null;
            if (s_material == null) return;

            if (Application.isPlaying) Object.Destroy(s_material);
            else Object.DestroyImmediate(s_material);
            s_material = null;
        }
    }
}
