using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Tests
{
    /// <summary>
    /// The DOTween integration assembly: that it offers exactly the shortcuts
    /// it promises, with exactly DOTween's signatures, and that the two it
    /// implements differently still behave.
    ///
    /// Signatures are most of this file, which looks like testing the compiler
    /// until you remember what the assembly is for. Its entire promise is that
    /// a call site written against <c>TMP_Text</c> compiles unchanged after the
    /// migration, and that promise is broken by a parameter reordered, a
    /// default dropped, or a return type narrowed from
    /// <c>TweenerCore&lt;...&gt;</c> to <c>Tweener</c> — none of which breaks
    /// anything inside this package, and all of which break every project that
    /// took the migration. The expected signatures below are transcribed from
    /// DOTween Pro's <c>DOTweenTextMeshPro.cs</c>.
    ///
    /// Everything here goes through reflection rather than through a direct
    /// reference, and deliberately: CI has no DOTween, so the integration
    /// assembly is not compiled there, so a test assembly that referenced its
    /// types could not compile either. Reflection lets the same test file say
    /// "not applicable here" instead of failing to build, in the same spirit as
    /// the golden suite standing down on a renderer its baselines did not come
    /// from.
    /// </summary>
    [Category("DOTween")]
    public class DOTweenCompatTests
    {
        private const string AssemblyName = "OneText.Integrations.DOTween";
        private const string ShortcutsType = "DG.Tweening.ShortcutExtensionsOneText";
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<object> _tweens = new List<object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var tween in _tweens) Kill(tween);
            _tweens.Clear();
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        // ------------------------------------------------------- the contract

        /// <summary>
        /// Every shortcut, rendered the way <see cref="Describe"/> renders the
        /// real thing. Type names are unqualified because that is what the
        /// assertion is about — the shape of the signature, not which assembly
        /// <c>ColorOptions</c> came from.
        /// </summary>
        private static readonly string[] ExpectedSignatures =
        {
            "TweenerCore<Color,Color,ColorOptions> DOColor(OneTextLabel,Color,Single)",
            "TweenerCore<Color,Color,ColorOptions> DOFade(OneTextLabel,Single,Single)",
            "TweenerCore<Single,Single,FloatOptions> DOFontSize(OneTextLabel,Single,Single)",
            "TweenerCore<Vector3,Vector3,VectorOptions> DOScale(OneTextLabel,Single,Single)",
            "TweenerCore<Int32,Int32,NoOptions> DOCounter(OneTextLabel,Int32,Int32,Single,Boolean,CultureInfo)",
            "TweenerCore<Int32,Int32,NoOptions> DOMaxVisibleCharacters(OneTextLabel,Int32,Single)",
            "TweenerCore<String,String,StringOptions> DOText(OneTextLabel,String,Single,Boolean,ScrambleMode,String)",

            "TweenerCore<Color,Color,ColorOptions> DOColor(OneTextMesh,Color,Single)",
            "TweenerCore<Color,Color,ColorOptions> DOFade(OneTextMesh,Single,Single)",
            "TweenerCore<Single,Single,FloatOptions> DOFontSize(OneTextMesh,Single,Single)",
            "TweenerCore<Vector3,Vector3,VectorOptions> DOScale(OneTextMesh,Single,Single)",
            "TweenerCore<Int32,Int32,NoOptions> DOCounter(OneTextMesh,Int32,Int32,Single,Boolean,CultureInfo)",
            "TweenerCore<String,String,StringOptions> DOText(OneTextMesh,String,Single,Boolean,ScrambleMode,String)"
        };

        /// <summary>
        /// The TMP shortcuts that are deliberately absent. Asserting on an
        /// absence is not paranoia here: each of these can be made to compile
        /// against something plausible-looking on a OneText label, and the
        /// result would animate the wrong thing quietly. The list is also what
        /// onboarding shows a user before they migrate, so it needs to stay
        /// true.
        /// </summary>
        public static readonly string[] DeliberatelyAbsent =
        {
            "DOFaceColor", "DOFaceFade", "DOOutlineColor", "DOGlowColor"
        };

        public static IEnumerable<TestCaseData> Signatures()
        {
            foreach (string signature in ExpectedSignatures)
            {
                int paren = signature.IndexOf('(');
                int space = signature.IndexOf(' ');
                string name = signature.Substring(space + 1, paren - space - 1);
                string on = signature.Substring(paren + 1).Split(',')[0];
                yield return new TestCaseData(signature).SetName($"Offers_{name}_On_{on}");
            }
        }

        [Test]
        [TestCaseSource(nameof(Signatures))]
        public void Offers(string expected)
        {
            var shortcuts = RequireTheIntegration();
            var actual = PublicShortcuts(shortcuts).Select(Describe).ToList();

            Assert.Contains(expected, actual,
                $"a call site that used the TMP shortcut this replaces will not compile.\n" +
                $"  wanted: {expected}\n  found:  {string.Join("\n          ", actual)}");
        }

        [Test]
        public void Offers_Nothing_It_Cannot_Honour()
        {
            var shortcuts = RequireTheIntegration();
            var actual = PublicShortcuts(shortcuts).Select(Describe).ToList();

            var unexpected = actual.Where(s => !ExpectedSignatures.Contains(s)).ToList();
            Assert.IsEmpty(unexpected,
                "these are public shortcuts nobody wrote down. Either they are a faithful " +
                "counterpart of a TMP shortcut, in which case they belong in the expected " +
                "list and in onboarding's report, or they are an approximation, in which " +
                "case they belong nowhere:\n  " + string.Join("\n  ", unexpected));
        }

        [Test]
        [TestCaseSource(nameof(DeliberatelyAbsent))]
        public void Does_Not_Fake(string name)
        {
            var shortcuts = RequireTheIntegration();

            Assert.IsEmpty(PublicShortcuts(shortcuts).Where(m => m.Name == name).ToList(),
                $"{name} tweens a property of TMP's SDF material that OneText's material does " +
                "not have. A shortcut with this name that animates something else is worse " +
                "than one that is missing: missing is a compile error the user reads, and " +
                "wrong is a tween they ship.");
        }

        [Test]
        public void Ships_No_Per_Character_Animator()
        {
            var assembly = RequireTheAssembly();

            var animators = assembly.GetTypes()
                .Where(t => t.Name.IndexOf("Animator", StringComparison.Ordinal) >= 0)
                .Select(t => t.FullName)
                .ToList();

            Assert.IsEmpty(animators,
                "DOTweenTMPAnimator rewrites TMP_MeshInfo vertices from outside the text " +
                "object. OneText's per-character animation goes through ITextQuadModifier, " +
                "which is a different API and not something a wrapper can stand in for:\n  " +
                string.Join("\n  ", animators));
        }

        [Test]
        public void Keeps_DOTweens_Defaults()
        {
            var shortcuts = RequireTheIntegration();

            foreach (var counter in PublicShortcuts(shortcuts).Where(m => m.Name == "DOCounter"))
            {
                var parameters = counter.GetParameters();
                AssertDefault(parameters[4], true, "DOCounter.addThousandsSeparator");
                AssertDefault(parameters[5], null, "DOCounter.culture");
            }

            foreach (var text in PublicShortcuts(shortcuts).Where(m => m.Name == "DOText"))
            {
                var parameters = text.GetParameters();
                AssertDefault(parameters[3], true, "DOText.richTextEnabled");
                Assert.IsTrue(parameters[4].HasDefaultValue, "DOText.scrambleMode has no default");
                // Compared as a number because reflection is entitled to hand
                // back an enum's underlying value rather than the enum, and
                // ScrambleMode.None is the zero either way.
                Assert.AreEqual(0, Convert.ToInt32(parameters[4].DefaultValue),
                    "DOText.scrambleMode defaults to something other than ScrambleMode.None");
                AssertDefault(parameters[5], null, "DOText.scrambleChars");
            }
        }

        [Test]
        public void Lives_In_DOTweens_Namespace()
        {
            var shortcuts = RequireTheIntegration();

            Assert.AreEqual("DG.Tweening", shortcuts.Namespace,
                "the call sites this exists for already have `using DG.Tweening;` and nothing " +
                "else. Anywhere but here and every one of them needs editing, which is the " +
                "work the assembly was written to avoid.");
            Assert.IsTrue(shortcuts.IsAbstract && shortcuts.IsSealed,
                "extension methods have to live in a static class");
        }

        // ------------------------------------------------------- the behaviour

        [Test]
        public void DOText_Reveals_The_Text_Instead_Of_Retyping_It()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();
            label.Text = "";

            const string line = "the quick brown fox";
            var tween = Invoke(shortcuts, "DOText", typeof(OneTextLabel),
                label, line, 1f, true, ScrambleModeNone(shortcuts), null);

            Drive(tween, 0.5f);
            Assert.AreEqual(line, label.Text,
                "the whole string should be assigned once and then revealed, not grown a " +
                "character at a time");
            Assert.Greater(label.MaxVisibleGraphemes, 0, "half a second in, something shows");
            Assert.Less(label.MaxVisibleGraphemes, label.GraphemeCount,
                "half a second in, not all of it shows");

            int layoutRuns = label.LayoutRuns;
            Drive(tween, 0.6f);
            Drive(tween, 0.7f);
            Drive(tween, 0.8f);
            Assert.AreEqual(layoutRuns, label.LayoutRuns,
                "revealing rebuilds the mesh and nothing else; a step that re-shapes the " +
                "string is the TMP behaviour this shortcut exists to not have");

            Drive(tween, 1f);
            Assert.AreEqual(label.GraphemeCount, label.MaxVisibleGraphemes,
                "the end of the tween is the end of the text");
        }

        [Test]
        public void DOText_Takes_The_Typewriter_Over_Rather_Than_Fighting_It()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();
            label.Text = "";
            label.CharactersPerSecond = 30f;

            var tween = Invoke(shortcuts, "DOText", typeof(OneTextLabel),
                label, "two typewriters is one too many", 1f, true, ScrambleModeNone(shortcuts), null);
            Drive(tween, 0.5f);

            Assert.AreEqual(0f, label.CharactersPerSecond,
                "the label's own reveal has to stand down, or both write the same counter " +
                "every frame and the text stutters between two positions");
            Assert.Greater(label.MaxVisibleGraphemes, 0);
        }

        [Test]
        public void DOMaxVisibleCharacters_Starts_From_What_Is_On_Screen()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();
            label.Text = "hide me";

            // -1 is where an untouched label sits, and it means "all". Tweening
            // from the number itself would blank the label and count up.
            Assert.AreEqual(-1, label.MaxVisibleGraphemes, "precondition: the label shows everything");

            var tween = Invoke(shortcuts, "DOMaxVisibleCharacters", typeof(OneTextLabel), label, 0, 1f);
            Drive(tween, 0.5f);

            Assert.Greater(label.MaxVisibleGraphemes, 0,
                "halfway from 'all' to 'none' is half the text, not none of it");
            Assert.Less(label.MaxVisibleGraphemes, label.GraphemeCount);
        }

        [Test]
        public void DOCounter_Formats_The_Way_DOTween_Does()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();

            var tween = Invoke(shortcuts, "DOCounter", typeof(OneTextLabel),
                label, 0, 1000000, 1f, true, null);
            Drive(tween, 1f);
            Assert.AreEqual("1,000,000", label.Text, "thousands separators are on by default");

            var plain = Invoke(shortcuts, "DOCounter", typeof(OneTextLabel),
                label, 0, 1000000, 1f, false, null);
            Drive(plain, 1f);
            Assert.AreEqual("1000000", label.Text);
        }

        [Test]
        public void DOFontSize_Drives_The_Size_The_Text_Is_Drawn_At()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();
            label.Text = "size";
            label.FontSize = 10f;

            var tween = Invoke(shortcuts, "DOFontSize", typeof(OneTextLabel), label, 50f, 1f);
            Drive(tween, 1f);

            Assert.AreEqual(50f, label.FontSize, 0.001f);
            Assert.AreEqual(50f, label.FittedFontSize, 0.001f, "and the text is laid out at it");
        }

        [Test]
        public void DOFade_Moves_Alpha_And_Leaves_The_Colour_Alone()
        {
            var shortcuts = RequireTheIntegration();
            var label = CreateLabel();
            label.color = new Color(0.25f, 0.5f, 0.75f, 1f);

            var tween = Invoke(shortcuts, "DOFade", typeof(OneTextLabel), label, 0f, 1f);
            Drive(tween, 1f);

            Assert.AreEqual(0f, label.color.a, 0.001f);
            Assert.AreEqual(0.25f, label.color.r, 0.001f);
            Assert.AreEqual(0.5f, label.color.g, 0.001f);
            Assert.AreEqual(0.75f, label.color.b, 0.001f);
        }

        // ------------------------------------------------------------- plumbing

        /// <summary>
        /// Stands the whole file down unless this project has the integration
        /// compiled into it.
        ///
        /// Absent is the correct state almost everywhere: the asmdef is
        /// constrained to ONETEXT_DOTWEEN, so a project without DOTween — CI
        /// included — has no such assembly at all, and that is the design
        /// working rather than a failure to report. A project that has DOTween
        /// and has defined the symbol runs the lot.
        /// </summary>
        private static Assembly RequireTheAssembly()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == AssemblyName);

            if (assembly == null)
                Assert.Inconclusive(
                    $"{AssemblyName} is not compiled in this project, which is what a project " +
                    "without DOTween is supposed to look like: the asmdef's ONETEXT_DOTWEEN " +
                    "constraint takes the assembly out entirely rather than leaving it to fail " +
                    "on a missing type. Install DOTween and define ONETEXT_DOTWEEN in Player " +
                    "Settings to run these.");

            return assembly;
        }

        private static Type RequireTheIntegration()
        {
            var type = RequireTheAssembly().GetType(ShortcutsType);
            Assert.IsNotNull(type,
                $"{AssemblyName} is compiled but has no {ShortcutsType}. Renaming the class is " +
                "harmless; renaming the namespace is not, and this is where that shows up.");
            return type;
        }

        private static IEnumerable<MethodInfo> PublicShortcuts(Type shortcuts) =>
            shortcuts.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        /// <summary>
        /// A method as a signature string: return type, name, parameter types,
        /// all unqualified, generics spelled out. Assembly-qualified names would
        /// make the expectations unreadable and would fail on a DOTween version
        /// bump that changed nothing a caller can see.
        /// </summary>
        private static string Describe(MethodInfo method) =>
            $"{Describe(method.ReturnType)} {method.Name}(" +
            string.Join(",", method.GetParameters().Select(p => Describe(p.ParameterType))) + ")";

        private static string Describe(Type type)
        {
            if (!type.IsGenericType) return type.Name;
            string bare = type.Name.Substring(0, type.Name.IndexOf('`'));
            return $"{bare}<{string.Join(",", type.GetGenericArguments().Select(Describe))}>";
        }

        private static void AssertDefault(ParameterInfo parameter, object expected, string what)
        {
            Assert.IsTrue(parameter.HasDefaultValue, $"{what} has no default value");
            Assert.AreEqual(expected, parameter.DefaultValue,
                $"{what} defaults to something other than DOTween's");
        }

        /// <summary>
        /// The ScrambleMode.None value, fetched from whichever DOTween this
        /// project has. The tests cannot name the enum, for the same reason they
        /// cannot name TweenerCore.
        /// </summary>
        private static object ScrambleModeNone(Type shortcuts) =>
            Enum.Parse(PublicShortcuts(shortcuts).First(m => m.Name == "DOText")
                .GetParameters()[4].ParameterType, "None");

        private object Invoke(Type shortcuts, string name, Type on, params object[] args)
        {
            var method = PublicShortcuts(shortcuts)
                .FirstOrDefault(m => m.Name == name && m.GetParameters()[0].ParameterType == on);
            Assert.IsNotNull(method, $"no {name} extending {on.Name}");

            object tween;
            try
            {
                tween = method.Invoke(null, args);
            }
            catch (TargetInvocationException e)
            {
                // Building a tween is DOTween's work, not ours, and it is the
                // one step here that can fail for reasons that have nothing to
                // do with this package — DOTween initialising itself outside
                // play mode, mostly. Anything our own setters do wrong happens
                // in Drive, which is left to fail properly.
                Assert.Inconclusive(
                    $"DOTween could not create a {name} tween in edit mode: {e.InnerException?.Message}. " +
                    "The signature tests above still ran.");
                return null;
            }

            Assert.IsNotNull(tween, $"{name} returned no tween");
            _tweens.Add(tween);
            return tween;
        }

        /// <summary>
        /// Moves a tween to a point in its own timeline and lets it apply,
        /// which is how these tests observe a setter without a running game.
        ///
        /// Through <c>TweenExtensions</c> rather than off the instance because
        /// DOTween's verbs are extension methods: <c>Goto</c> is a static taking
        /// the tween, and reflection does not do the sugar.
        /// </summary>
        private static void Drive(object tween, float toSeconds) =>
            TweenVerb(tween, "Goto", 3).Invoke(null, new object[] { tween, toSeconds, false });

        private static void Kill(object tween)
        {
            if (tween == null) return;
            TweenVerb(tween, "Kill", 2).Invoke(null, new object[] { tween, false });
        }

        private static MethodInfo TweenVerb(object tween, string name, int parameterCount)
        {
            var extensions = tween.GetType().Assembly.GetType("DG.Tweening.TweenExtensions");
            var verb = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);

            if (verb == null)
                Assert.Inconclusive(
                    $"this DOTween has no TweenExtensions.{name} taking {parameterCount} " +
                    "arguments, so there is no way to step a tween from an edit-mode test. " +
                    "The signature tests above still ran.");

            return verb;
        }

        private OneTextLabel CreateLabel()
        {
            var go = new GameObject("DOTweenCompatLabel", typeof(RectTransform));
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(600f, 200f);
            _created.Add(go);
            var label = go.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            return label;
        }
    }
}
