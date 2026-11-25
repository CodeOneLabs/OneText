using System;
using System.Collections.Generic;
using System.Reflection;
using OneText.UGUI;

namespace OneText.Benchmarks
{
    /// <summary>
    /// Every <see cref="HotPathSwitchAttribute"/> in the runtime, by name, for a
    /// harness that wants to hold a hot path against the code it replaced.
    ///
    /// The switches are internal so that they are not part of the package's
    /// API; this is the one door to them from outside the runtime assemblies,
    /// and it is in the dev assembly, which a project only compiles when it
    /// lists the package under <c>testables</c>. Names are
    /// <c>Type.Member</c> (<c>GlyphAtlas.FastKeys</c>). Found by reflection, so
    /// a new switch is in the list by being marked, and a harness that turns
    /// "all of them" off cannot quietly leave the newest one on.
    /// </summary>
    public static class HotPathSwitches
    {
        private readonly struct Switch
        {
            public readonly Func<bool> Get;
            public readonly Action<bool> Set;

            public Switch(Func<bool> get, Action<bool> set)
            {
                Get = get;
                Set = set;
            }
        }

        private static Dictionary<string, Switch> s_switches;
        private static string[] s_names;

        /// <summary>Sorted, <c>Type.Member</c>.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                Find();
                return s_names;
            }
        }

        public static bool Get(string name) => Named(name).Get();

        public static void Set(string name, bool on) => Named(name).Set(on);

        /// <summary>
        /// <c>true</c> is the package as it ships. <c>false</c> is every
        /// reference path at once, which is the package before the hot-path
        /// work: the "before" column of a benchmark.
        /// </summary>
        public static void SetAll(bool on)
        {
            Find();
            foreach (var entry in s_switches.Values) entry.Set(on);
        }

        private static Switch Named(string name)
        {
            Find();
            if (s_switches.TryGetValue(name, out var found)) return found;
            throw new ArgumentException($"No hot-path switch is called '{name}'.", nameof(name));
        }

        private static void Find()
        {
            if (s_switches != null) return;
            var switches = new Dictionary<string, Switch>();
            const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.DeclaredOnly;
            var assemblies = new[] { typeof(TextLayoutEngine).Assembly, typeof(OneTextLabel).Assembly };
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var field in type.GetFields(Statics))
                    {
                        if (field.GetCustomAttribute<HotPathSwitchAttribute>() == null) continue;
                        var captured = field;
                        switches.Add($"{type.Name}.{field.Name}", new Switch(
                            () => (bool)captured.GetValue(null), on => captured.SetValue(null, on)));
                    }
                    foreach (var property in type.GetProperties(Statics))
                    {
                        if (property.GetCustomAttribute<HotPathSwitchAttribute>() == null) continue;
                        var captured = property;
                        switches.Add($"{type.Name}.{property.Name}", new Switch(
                            () => (bool)captured.GetValue(null), on => captured.SetValue(null, on)));
                    }
                }
            }
            var names = new string[switches.Count];
            switches.Keys.CopyTo(names, 0);
            Array.Sort(names, StringComparer.Ordinal);
            s_names = names;
            s_switches = switches;
        }
    }
}
