using UnityEngine;

namespace OneText.Benchmarks
{
    /// <summary>
    /// A text system under test. The scenarios drive this interface and never
    /// mention a concrete implementation, so the same scene definition measures
    /// OneText and TextMeshPro under identical conditions, and so the
    /// TextMeshPro adapter can live outside the package, where the dependency
    /// belongs.
    /// </summary>
    public interface ITextSubject
    {
        /// <summary>Name shown in the report.</summary>
        string Name { get; }

        /// <summary>Loads fonts and whatever else the system needs. Called once.</summary>
        void Setup();

        /// <summary>
        /// Creates one label. <paramref name="fontIndex"/> selects from the
        /// subject's font set (0 = Latin body, 1 = second face, 2 = CJK).
        /// </summary>
        object CreateLabel(Transform parent, Rect rect, float fontSize, int fontIndex);

        /// <summary>Sets a label's text. Cost of the resulting rebuild is what is measured.</summary>
        void SetText(object label, string text);

        /// <summary>
        /// Runs whatever the system does once per frame outside its labels:
        /// for OneText, the single shared atlas upload.
        /// </summary>
        void EndFrame();

        /// <summary>Texture memory the system currently holds, in bytes.</summary>
        long TextureMemoryBytes { get; }

        /// <summary>Extra facts for the report (atlas size, tile count, ...).</summary>
        string Describe();

        void Teardown();
    }

    /// <summary>
    /// A system that can rasterize a known character set before the run.
    /// Both systems get the same charset and the same chance to use it;
    /// prewarming one side only would be the obvious way to rig this.
    /// </summary>
    public interface IPrewarmable
    {
        void Prewarm(System.Collections.Generic.IEnumerable<int> codepoints,
            System.Collections.Generic.IReadOnlyList<float> sizes);
    }
}
