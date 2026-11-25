using System;

namespace OneText
{
    /// <summary>
    /// Marks a static <c>bool</c> that chooses between a hot path and the code
    /// it replaced.
    ///
    /// <c>true</c>, the default, is what ships. <c>false</c> runs the earlier
    /// implementation, which is kept for two reasons: a test can run both and
    /// assert the same layout, quads, atlas bytes or mesh (the equivalence
    /// tests under <c>Tests/Editor</c> each name the switch they hold), and a
    /// benchmark can flip one inside a single Unity process, which is the only
    /// A/B on this code that has ever been trustworthy. None of them changes
    /// what is drawn, so none of them is API: they are <c>internal</c>, and a
    /// harness reaches them by name through
    /// <c>OneText.Benchmarks.HotPathSwitches</c> in the dev assembly.
    ///
    /// Some are read once (an atlas reads the ones that shape its tables when it
    /// is constructed), so flip them before building what is measured.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class HotPathSwitchAttribute : Attribute
    {
    }
}
