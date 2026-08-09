using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The process-wide glyph atlas and the material that samples it.
    ///
    /// One atlas is the point: rasterized glyphs are keyed by font, size bucket
    /// and variation instance, so every label in the project shares the same
    /// cache: a second label showing the same text costs nothing to rasterize.
    /// Sharing the material matters just as much: uGUI can only batch draws
    /// that use the same material, so a per-label material instance would put
    /// every label in its own draw call.
    /// </summary>
    public static class SharedGlyphAtlas
    {
        /// <summary>The shader every label draws through.</summary>
        public const string ShaderName = "OneText/SDF";

        /// <summary>
        /// Where the shader is loaded from, relative to the package's
        /// <c>Runtime/Shaders/Resources</c> folder.
        ///
        /// The folder is the whole point. Nothing in a scene references the SDF
        /// shader (every label shares one material this class builds at
        /// runtime), so a build has no reason to believe the shader is used and
        /// strips it, which is a player where every label draws nothing.
        /// Anything under a <c>Resources</c> folder ships whether or not the
        /// dependency analyser can see who wants it, so the folder name is what
        /// carries the dependency, and it does so without asking the project to
        /// add a line to Always Included Shaders.
        /// </summary>
        public const string ShaderResourcePath = "OneText-SDF";

        private static GlyphAtlas s_atlas;
        private static GlyphAtlas s_preciseAtlas;
        private static ColorGlyphAtlas s_colorAtlas;
        private static Material s_material;
        private static int s_references;

        /// <summary>
        /// The shared RGBA atlas for colour emoji and inline sprites, created on
        /// first use: a project with no colour glyphs never pays for it.
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

        /// <summary>True when a colour atlas exists; asking does not create one.</summary>
        public static bool ColorAtlasExists => s_colorAtlas != null && s_colorAtlas.IsUsable;

        /// <summary>
        /// The shared multi-channel atlas, for labels with <c>precise</c> on.
        /// Created on first use like the colour one: a project that never asks
        /// for MSDF never allocates four bytes a texel for it.
        ///
        /// Same size and layer count as the ordinary atlas, from the same
        /// project setting: it holds the same kind of tiles for the same
        /// glyphs, just wider ones, and a second budget nobody would find in
        /// the inspector is worse than a predictable multiple of the first.
        /// </summary>
        public static GlyphAtlas PreciseAtlas
        {
            get
            {
                if (s_preciseAtlas != null && !s_preciseAtlas.IsUsable) s_preciseAtlas = null;
                if (s_preciseAtlas != null) return s_preciseAtlas;

                var settings = OneTextSettings.Instance;
                s_preciseAtlas = new GlyphAtlas(
                    settings != null ? settings.AtlasSettings : GlyphAtlasSettings.Default,
                    precise: true);
                if (s_material != null) BindPreciseTexture(s_preciseAtlas);
                return s_preciseAtlas;
            }
        }

        /// <summary>True when a precise atlas exists; asking does not create one.</summary>
        public static bool PreciseAtlasExists => s_preciseAtlas != null && s_preciseAtlas.IsUsable;

        /// <summary>The shared atlas, creating it on first use.</summary>
        public static GlyphAtlas Atlas
        {
            get
            {
                DiscardIfUnusable();
                return s_atlas ??= Create();
            }
        }

        /// <summary>True when an atlas exists; asking does not create one.</summary>
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
        /// outlives whatever killed the texture, and it holds <c>_GlyphTex</c>,
        /// meaning a replacement atlas alone would leave every label batching
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
        /// companion for 2D textures rather than for arrays, so it is set here
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

        /// <summary>
        /// Same pairing for the multi-channel atlas. Its own texel size, not the
        /// ordinary atlas's: the two are configured alike today and a shadow
        /// offset computed from the wrong one would only be visible on the day
        /// they stop being.
        /// </summary>
        private static void BindPreciseTexture(GlyphAtlas atlas)
        {
            s_material.SetTexture("_MsdfTex", atlas.Texture);
            float size = atlas.Texture.width;
            s_material.SetVector("_MsdfTexelSize",
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
        /// changes; the editor does it when the setting is edited.
        /// </summary>
        public static void Reconfigure() => Reconfigure(force: false);

        /// <summary>
        /// Same, but <paramref name="force"/> rebuilds even when the budget is
        /// unchanged: benchmarks need every run to start from an empty atlas.
        /// </summary>
        public static void Reconfigure(bool force)
        {
            var settings = OneTextSettings.Instance;
            var wanted = settings != null ? settings.AtlasSettings : GlyphAtlasSettings.Default;
            if (!force && s_atlas != null && s_atlas.Settings.Equals(wanted)) return;

            s_atlas?.Dispose();
            s_atlas = null;
            // The precise atlas takes its size from the same setting, so it is
            // as stale as the other one and is rebuilt on demand.
            s_preciseAtlas?.Dispose();
            s_preciseAtlas = null;
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

                var shader = LoadShader();
                if (shader == null)
                {
                    ReportMissingShader();
                    return null;
                }
                s_material = new Material(shader)
                {
                    name = "OneText SDF (shared)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                BindGlyphTexture(Atlas);
                // Only if one exists: creating the colour atlas here would cost
                // 4 MB to every project that never draws an emoji, and the
                // precise one four times that to every project that never asks
                // for MSDF.
                if (s_colorAtlas != null && s_colorAtlas.IsUsable)
                    s_material.SetTexture("_ColorTex", s_colorAtlas.Texture);
                if (s_preciseAtlas != null && s_preciseAtlas.IsUsable)
                    BindPreciseTexture(s_preciseAtlas);
                return s_material;
            }
        }

        /// Said once per domain, not once per label. See <see cref="ReportMissingShader"/>.
        private static bool s_shaderReported;

        /// <summary>
        /// Says the shader is missing, once, and says the right thing about why.
        ///
        /// <para>Two situations produce a null shader and they are not the same
        /// problem. A project whose package did not import properly has a fault
        /// to fix, and gets an error. A process with no graphics device — a
        /// headless CI container, an editor started without one — loads no
        /// shaders at all, by design: nothing is wrong, text still lays out and
        /// measures, and only drawing is unavailable. That one gets a warning
        /// naming the device, because an error there fails every test that so
        /// much as adds a label, which is exactly what it did on this project's
        /// CI: roughly a hundred EditMode failures, every one of them the words
        /// "Unhandled log message" and none of them a fault in the code under
        /// test.</para>
        ///
        /// <para>Once, either way. The old message came out of a property every
        /// label touches on enable, which turned one missing file into eight
        /// thousand identical lines in a CI log.</para>
        /// </summary>
        private static void ReportMissingShader()
        {
            if (s_shaderReported) return;
            s_shaderReported = true;

            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.LogWarning($"{ShaderName} is unavailable because this process has no " +
                    "graphics device, so no shaders are loaded at all. Layout, measurement and " +
                    "the atlas still work; nothing will be drawn. This is the expected state in " +
                    "a headless CI container and not a fault in the package.");
                return;
            }

            Debug.LogError($"{ShaderName} shader not found (graphics device: " +
                $"{SystemInfo.graphicsDeviceType}{EditorShaderDiagnosis()}): it should have " +
                "shipped from the package's Runtime/Shaders/Resources folder. Reimport OneText, " +
                "or add the shader to Always Included Shaders.");
        }

        /// <summary>
        /// What the editor can see that the runtime cannot, appended to the
        /// message above.
        ///
        /// This exists because of a failure that only ever happened on CI: the
        /// shader loads on every developer machine and on none of the three CI
        /// runners, which have a working OpenGL device and import the rest of
        /// the package fine. Whether the asset is absent from the database, or
        /// present and unloadable, is the difference between an import problem
        /// and a compilation one, and it takes a line in a log to tell them
        /// apart — a line nobody could get by asking a machine that works.
        /// </summary>
        private static string EditorShaderDiagnosis()
        {
#if UNITY_EDITOR
            string path = "Packages/com.onetext.core/Runtime/Shaders/Resources/OneText-SDF.shader";
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path);
            int inDatabase = UnityEditor.AssetDatabase.FindAssets("t:Shader OneText-SDF").Length;
            return $"; asset at the package path: {(asset != null ? "loads" : "does not load")}" +
                $"; shaders named OneText-SDF in the database: {inDatabase}" +
                $"; Shader.Find: {(Shader.Find(ShaderName) != null ? "found" : "null")}";
#else
            return string.Empty;
#endif
        }

        /// <summary>
        /// The SDF shader every label draws through, or null when it did not
        /// make it into the build.
        ///
        /// The load, not the find, is what a player depends on:
        /// <c>Resources.Load</c> both names the asset and is the reason it is
        /// there to be named. <see cref="Shader.Find"/> stays behind it for the
        /// project that has already put the shader in Always Included Shaders
        /// (the instruction this package used to give) and for one that
        /// vendors the source somewhere else in its Assets folder.
        /// </summary>
        public static Shader LoadShader()
        {
            var shader = Resources.Load<Shader>(ShaderResourcePath);
            return shader != null ? shader : Shader.Find(ShaderName);
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
            s_preciseAtlas?.Dispose();
            s_preciseAtlas = null;
            s_colorAtlas?.Dispose();
            s_colorAtlas = null;
            if (s_material == null) return;

            if (Application.isPlaying) Object.Destroy(s_material);
            else Object.DestroyImmediate(s_material);
            s_material = null;
        }
    }
}
