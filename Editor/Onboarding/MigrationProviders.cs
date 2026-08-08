using System.Collections.Generic;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Reads one family of text components and says, in OneText's own terms,
    /// what it found.
    ///
    /// A provider only ever <em>reads</em>. It never destroys a component, never
    /// adds one, never writes a serialized field — <see cref="ComponentMigration"/>
    /// does all of that, for every provider, in one place. That split is the
    /// whole point of the seam: the TextMesh Pro provider lives in an assembly
    /// that only compiles when TMP is installed, so if the surgery lived there
    /// it would be untestable on a machine without TMP, which is most CI. As it
    /// stands the risky half is ordinary code in the ordinary editor assembly
    /// and the gated half is a value reader.
    /// </summary>
    public interface IMigrationProvider
    {
        /// <summary>What to call this in the report: "TextMesh Pro", "Unity UI".</summary>
        string Name { get; }

        /// <summary>
        /// Inspects one component. Returns null when it is not one of this
        /// provider's, which is the answer for almost every component in a
        /// project and must therefore be cheap.
        /// </summary>
        MigrationTarget Inspect(Component component, string container, string path);

        /// <summary>
        /// The project-wide font defaults this provider knows about, if any:
        /// TMP's default font asset and global fallbacks, as source font file
        /// paths. False when the provider has no such notion.
        /// </summary>
        bool TryProjectFontDefaults(out string defaultFontPath, out List<string> fallbackFontPaths);
    }

    /// <summary>
    /// Where the providers announce themselves.
    ///
    /// <see cref="ComponentMigration"/> lives in <c>OneText.Editor</c>, which
    /// cannot reference the TMP-gated assembly — an assembly that does not
    /// compile in half the projects that install this package is not something
    /// anything else may depend on. So the dependency runs the other way: the
    /// gated assembly references this one and registers itself from an
    /// <c>InitializeOnLoad</c> constructor, and when TMP is absent the registry
    /// simply has one fewer provider in it and the Hub says so out loud instead
    /// of failing to compile.
    /// </summary>
    public static class MigrationProviders
    {
        private static readonly List<IMigrationProvider> Registered = new List<IMigrationProvider>();

        /// <summary>The name the TMP provider registers under. Checked, not guessed at.</summary>
        public const string TextMeshProName = "TextMesh Pro";

        /// <summary>
        /// Adds a provider, once. Domain reloads re-run every
        /// <c>InitializeOnLoad</c> constructor and the static list survives
        /// nothing, but a provider registered twice from two entry points would
        /// double every finding, so the name is the key.
        /// </summary>
        public static void Register(IMigrationProvider provider)
        {
            if (provider == null) return;
            foreach (var existing in Registered)
                if (existing.Name == provider.Name) return;
            Registered.Add(provider);
        }

        public static IReadOnlyList<IMigrationProvider> All
        {
            get
            {
                EnsureBuiltIns();
                return Registered;
            }
        }

        /// <summary>Whether anything registered for TextMesh Pro: the Hub says different things.</summary>
        public static bool HasTextMeshPro
        {
            get
            {
                foreach (var provider in All)
                    if (provider.Name == TextMeshProName) return true;
                return false;
            }
        }

        /// <summary>
        /// The legacy provider is not gated on anything and is not worth an
        /// <c>InitializeOnLoad</c> of its own; it is simply always there.
        /// </summary>
        private static void EnsureBuiltIns()
        {
            foreach (var provider in Registered)
                if (provider is LegacyTextProvider) return;
            Registered.Insert(0, new LegacyTextProvider());
        }
    }
}
