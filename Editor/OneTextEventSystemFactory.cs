using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OneText.Editor
{
    /// <summary>
    /// Puts the input module on an EventSystem OneText is creating, choosing
    /// the one the project's input backend can actually run.
    ///
    /// The bug this exists for is not subtle and it is not OneText's alone.
    /// <c>StandaloneInputModule</c> reads <c>UnityEngine.Input</c>, and
    /// <c>UnityEngine.Input</c> throws outright in a project whose Active Input
    /// Handling is "Input System Package (New)". So a menu item that hard-codes
    /// that module creates an EventSystem which throws on every frame it
    /// updates: no clicks reach anything, no field can be focused, and the
    /// first thing the project's author sees is a console full of exceptions
    /// from a component they did not add. OneText's own
    /// <c>GameObject &gt; UI &gt; OneText</c> entries were doing exactly this.
    ///
    /// The module for the other backend cannot be named here — its assembly
    /// only exists when the package is installed, and an assembly definition
    /// that references a package which is not there does not fail to find it,
    /// it fails to compile. The IME backend solves that with a whole assembly
    /// of its own; one AddComponent does not earn one, so the type is looked up
    /// by name.
    ///
    /// Which module, and why that test. The define is asked first because the
    /// package being installed is not the same as its backend being switched
    /// on: a project can have the Input System package sitting there with
    /// Active Input Handling still set to the old one, and an
    /// InputSystemUIInputModule there would be a module fed by nothing. So the
    /// backend has to be enabled AND the type has to be findable. This is the
    /// same answer uGUI's own menu reaches by a different road — it calls
    /// <c>InputModuleComponentFactory.AddInputModule</c>, which the Input
    /// System package overrides when its backend is on — and it is done here
    /// rather than through that factory because that factory is not in every
    /// editor version this package supports.
    /// </summary>
    public static class OneTextEventSystemFactory
    {
        /// <summary>
        /// Adds the input module that works here, and returns it. Never returns
        /// null: with no working backend at all it adds the standalone module
        /// anyway and says why, because an EventSystem with no module is a
        /// harder thing to diagnose than one that names its own problem.
        /// </summary>
        public static BaseInputModule AddInputModule(GameObject eventSystem)
        {
#if ENABLE_INPUT_SYSTEM
            var module = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null) return (BaseInputModule)eventSystem.AddComponent(module);
#endif

#if !ENABLE_LEGACY_INPUT_MANAGER
            // Neither backend. Active Input Handling says the Input System, and
            // the Input System package is not installed to answer for it, so
            // there is no module in this project that can read a mouse.
            Debug.LogWarning(
                "[OneText] This project's Active Input Handling is \"Input System Package " +
                "(New)\", but the Input System package is not installed, so there is no " +
                "input module that can run here: the EventSystem just created carries a " +
                "StandaloneInputModule, and that reads UnityEngine.Input, which throws " +
                "under this setting. Install com.unity.inputsystem, or set Active Input " +
                "Handling to \"Both\". The same setting is why OneText cannot compose " +
                "Korean, Japanese or Chinese in this project.");
#endif
            return eventSystem.AddComponent<StandaloneInputModule>();
        }
    }
}
