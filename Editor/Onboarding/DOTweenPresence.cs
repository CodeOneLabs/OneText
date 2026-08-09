using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Keeps <c>ONETEXT_DOTWEEN</c> in step with whether DOTween is actually in
    /// the project.
    ///
    /// The integration assembly is constrained on that symbol, and the symbol
    /// cannot come from the asmdef alone: <c>versionDefines</c> matches a package
    /// manifest, and most projects install DOTween from the Asset Store, which
    /// is a DLL under <c>Assets/Plugins</c> with no manifest to match. Left to
    /// the asmdef, those projects get no shortcuts, no error, and no reason —
    /// the extension methods simply are not there.
    ///
    /// Both directions matter, and the second one more. A symbol left behind
    /// after DOTween is removed does not degrade quietly: the integration
    /// assembly compiles against types that are gone, and the project stops
    /// building until somebody finds a define they never set by hand. So this
    /// runs on every reload and removes as readily as it adds.
    ///
    /// It writes a project setting, which is not something a package should do
    /// lightly. It is done here because the alternative is worse in both
    /// directions and because there is nothing to decide: the symbol is not a
    /// preference, it is a statement about what is installed, and it is only
    /// ever written when it disagrees with that.
    /// </summary>
    [InitializeOnLoad]
    public static class DOTweenPresence
    {
        private const string Symbol = "ONETEXT_DOTWEEN";

        // Deferred rather than run here: this constructor is on the import path,
        // and writing a project setting from inside it is asking to be told the
        // asset database is busy.
        static DOTweenPresence() => EditorApplication.delayCall += () => Sync();

        /// <summary>Whether DOTween's own assembly is loaded in this project.</summary>
        public static bool Installed
        {
            get
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // By type rather than by assembly name: DOTween ships as
                    // DOTween.dll from the Asset Store and as the same code
                    // inside a package from OpenUPM, and the assembly is not
                    // always called the same thing.
                    Type found;
                    try { found = assembly.GetType("DG.Tweening.DOTween", false); }
                    catch (Exception) { continue; }
                    if (found != null) return true;
                }
                return false;
            }
        }

        /// <summary>Whether the symbol is set for the target being built for now.</summary>
        public static bool Enabled => Array.IndexOf(Symbols(), Symbol) >= 0;

        /// <summary>
        /// Puts the symbol where the truth is. Returns true if it changed
        /// anything, which means a recompile is about to happen.
        /// </summary>
        public static bool Sync()
        {
            bool installed = Installed;
            if (installed == Enabled) return false;

            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            var symbols = new System.Collections.Generic.List<string>(Symbols());

            if (installed)
            {
                symbols.Add(Symbol);
                Debug.Log("OneText: DOTween is in this project, so its tween shortcuts are " +
                          "switched on (" + Symbol + " added to Scripting Define Symbols).");
            }
            else
            {
                symbols.Remove(Symbol);
                Debug.Log("OneText: DOTween is no longer in this project, so " + Symbol +
                          " has been removed from Scripting Define Symbols. Leaving it would " +
                          "have stopped the project compiling.");
            }

            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
            return true;
        }

        private static string[] Symbols()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            string defined = PlayerSettings.GetScriptingDefineSymbols(target) ?? string.Empty;
            return defined.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
