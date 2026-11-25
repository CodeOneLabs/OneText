using System.Reflection;
using NUnit.Framework;
using OneText.Benchmarks;
using OneText.UGUI;

namespace OneText.Tests
{
    /// <summary>
    /// The hot-path switches are test and benchmark equipment, not API: every
    /// one is internal, every one ships on, and the registry a harness uses
    /// sees all of them.
    /// </summary>
    public sealed class HotPathSwitchTests
    {
        [Test]
        public void Every_Switch_Ships_On()
        {
            Assert.That(HotPathSwitches.Names.Count, Is.GreaterThanOrEqualTo(54));
            foreach (string name in HotPathSwitches.Names)
                Assert.That(HotPathSwitches.Get(name), Is.True, name + " is off by default");
        }

        [Test]
        public void No_Switch_Is_Public()
        {
            const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (var assembly in new[] { typeof(TextLayoutEngine).Assembly, typeof(OneTextLabel).Assembly })
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var field in type.GetFields(Statics))
                        Assert.That(field.GetCustomAttribute<HotPathSwitchAttribute>(), Is.Null,
                            $"{type.Name}.{field.Name} is a public hot-path switch");
                    foreach (var property in type.GetProperties(Statics))
                        Assert.That(property.GetCustomAttribute<HotPathSwitchAttribute>(), Is.Null,
                            $"{type.Name}.{property.Name} is a public hot-path switch");
                }
            }
        }

        [Test]
        public void A_Switch_Is_Reached_By_Name_And_Put_Back()
        {
            const string Name = "GlyphAtlas.FastKeys";
            Assert.That(HotPathSwitches.Names, Does.Contain(Name));
            try
            {
                HotPathSwitches.Set(Name, false);
                Assert.That(GlyphAtlas.FastKeys, Is.False);
            }
            finally
            {
                HotPathSwitches.Set(Name, true);
            }
            Assert.That(GlyphAtlas.FastKeys, Is.True);
        }
    }
}
