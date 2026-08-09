using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// The one question this package asks unprompted, once per project: you
    /// have just installed a text engine into a project that already draws
    /// text — do you want to see what moving it over would involve?
    ///
    /// It is a dialog rather than a window that opens itself because opening a
    /// window nobody asked for is how a package makes its first impression a
    /// bad one. The question is asked once and remembered, whatever the answer;
    /// Onboarding stays where it always was, in Project Settings &gt; OneText,
    /// for the person who says no today and yes next week.
    /// </summary>
    [InitializeOnLoad]
    internal static class OnboardingWelcome
    {
        // EditorPrefs is per machine, not per project, so the project's own
        // path is part of the key: two projects on one desk each get asked.
        private static string Key =>
            "OneText.Onboarding.Asked." + Application.dataPath.GetHashCode().ToString("X8");

        static OnboardingWelcome()
        {
            // Not during the reload itself: opening a dialog while the domain
            // is still being set up hangs the editor, and a compile error
            // anywhere else in the project would surface as ours.
            EditorApplication.delayCall += Ask;
        }

        private static void Ask()
        {
            // Batch mode has nobody to ask, and a test run must never stop on a
            // modal dialog.
            if (Application.isBatchMode || EditorPrefs.GetBool(Key, false)) return;
            EditorPrefs.SetBool(Key, true);

            bool tmp = HasTextMeshPro();
            string body = tmp
                ? "This project uses TextMesh Pro.\n\n" +
                  "OneText can take it over for you: it scans every scene, prefab and script, " +
                  "tells you exactly what will not survive the swap before it touches anything, " +
                  "and can undo the whole thing with one button.\n\n" +
                  "Open the Onboarding screen now?"
                : "OneText draws text in Unity: one draw call, every script, no atlas to bake.\n\n" +
                  "The Onboarding screen sets the project up — import a font, make it the " +
                  "default — and converts any existing Text or TextMeshPro objects if you add " +
                  "them later.\n\n" +
                  "Open it now?";

            bool open = EditorUtility.DisplayDialog("OneText is installed", body,
                "Open Onboarding", "Not now");

            if (open) OneTextHub.Open(OneTextHub.Tab.Onboarding);
            else Debug.Log("OneText: the Onboarding screen is in Project Settings > OneText " +
                "whenever you want it.");
        }

        /// <summary>
        /// Whether TextMesh Pro is in this project, asked of the type system
        /// rather than the package list: a project that vendored TMP into
        /// Assets has it just as much as one that installed the package.
        /// </summary>
        private static bool HasTextMeshPro()
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType("TMPro.TMP_Text", false) != null) return true;
            return false;
        }
    }
}
