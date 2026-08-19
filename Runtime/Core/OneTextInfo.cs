namespace OneText
{
    /// <summary>
    /// Package identity constants.
    /// </summary>
    public static class OneTextInfo
    {
        /// <summary>
        /// The package version, as <c>package.json</c> declares it.
        ///
        /// Two copies of one fact, and this one had been wrong since 0.1.0 —
        /// through two releases, because nothing reads it at runtime and
        /// nothing checked it. It exists because a player build has no package
        /// manager to ask: the editor reads the real version off the manifest,
        /// and a crash report from a shipped game cannot. A test holds the two
        /// together now, which is the only reason this is safe to believe.
        /// </summary>
        public const string Version = "0.3.2";

        public const string PackageName = "com.onetext.core";
    }
}
