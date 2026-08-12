// ---------------------------------------------------------------------------
//  OneTextImeProbeInputSystem — the Input System half of OneTextImeProbe.
//  A throwaway diagnostic. It is not part of OneText, it is not shipped with
//  it, and it is meant to be deleted the moment it has answered its question.
//
//  WHAT IT IS FOR
//
//  OneText's Korean input was fixed four times against the Input Manager, on a
//  project that has the Input System package installed. So the backend in
//  Runtime/UGUI/Ime/InputSystem has never been compiled by the project the
//  fixes were tested in, let alone watched. "Korean does not work" from a
//  project using the Input System is therefore not a mystery to be reasoned
//  about; it is a measurement nobody has taken.
//
//  There is one question that decides everything else, and it is this: when a
//  Hangul syllable is committed, WHICH CHANNEL DOES IT ARRIVE ON?
//
//   * The composition channel (Keyboard.onIMECompositionChange) reports the
//     syllable while it is being built and then reports empty. OneText reads
//     this one, and this probe shows it as COMPOSE.
//   * The IMGUI channel (Event.character, read in OnGUI) is where OneText
//     expects the committed syllable to land, because that is where the
//     Input Manager backend gets it. Shown as IMGUI.
//   * The Input System's own text channel (Keyboard.onTextInput) is the other
//     candidate. OneText does not read this one at all. Shown as TEXT.
//
//  If a committed syllable shows up on IMGUI, the backend is fine and the bug
//  is elsewhere. If it shows up only on TEXT, OneText is listening to a
//  channel the platform is not using, and that is the fix — a small one, but
//  not one worth guessing at, which is why this file exists.
//
//  BEFORE YOU COPY IT IN
//
//  This file names types from the Input System package, so the package has to
//  be installed for it to compile — Active Input Handling being set to the
//  Input System is not the same thing and is not enough. If Package Manager
//  does not list "Input System", install it first. (A project in that state —
//  the setting switched, the package absent — has no working input at all,
//  which is its own answer and needs no probe.)
//
//  HOW TO USE IT
//
//   1. Copy this file into your project's Assets folder — anywhere inside it.
//      (It lives in a "~" folder in the package so that Unity never compiles
//      it there and it can never reach anybody's build. Copying it out is the
//      one step that makes it run.)
//   2. Make an empty GameObject in the scene you normally type into, and add
//      the "IME Channel Probe (Input System)" component to it
//      (Add Component > OneText > Diagnostics). It needs no wiring.
//   3. Enter play mode. The first line printed says which backend OneText
//      chose; if it says there is none, stop there and send that line — the
//      answer is already in it.
//   4. Click into your input field and type 한 — ㅎ, then ㅏ, then ㄴ. Then
//      press Enter. Then type 국 and click outside the field WITHOUT pressing
//      Enter.
//   5. Copy the console out (Console window, right-click > Copy, or the whole
//      of Editor.log) and send it back.
//   6. Delete this file.
//
//  WHAT IT WILL NOT DO
//
//  It never consumes an event. It reads IMGUI through OnGUI, which is shown
//  every event without being given it, and it never calls Event.Use or
//  Event.PopEvent. Popping is what the real input field does, and a second
//  popper would eat the keystrokes it was meant to be watching.
//
//  It switches the IME on itself, so that it reports something even in a scene
//  with no OneText field in it. That is the same value a field sets, so a
//  field being there as well changes nothing.
// ---------------------------------------------------------------------------

using System.Text;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

[AddComponentMenu("OneText/Diagnostics/IME Channel Probe (Input System)")]
[DisallowMultipleComponent]
public sealed class OneTextImeProbeInputSystem : MonoBehaviour
{
#if ENABLE_INPUT_SYSTEM

    [Tooltip("Switch the input method on from here as well as from wherever your " +
             "field does it. Harmless when a field is already doing it — both set " +
             "the same value — and necessary if you want to watch the platform " +
             "with nothing else in the scene.")]
    public bool enableImeMyself = true;

    private string _composition = string.Empty;
    private int _frame;

    private void OnEnable()
    {
        var keyboard = Keyboard.current;
        Log($"START keyboard={(keyboard == null ? "null" : keyboard.displayName)} " +
            $"backend=\"{Backend()}\" " +
            $"activeInputHandling=see Player settings; ENABLE_INPUT_SYSTEM=1, " +
#if ENABLE_LEGACY_INPUT_MANAGER
            "ENABLE_LEGACY_INPUT_MANAGER=1");
#else
            "ENABLE_LEGACY_INPUT_MANAGER=0");
#endif

        if (keyboard == null)
        {
            Log("STOP  Keyboard.current is null, so there is nothing to watch and " +
                "OneText has no backend either. That is the whole finding.");
            return;
        }

        keyboard.onIMECompositionChange += OnCompositionChange;
        keyboard.onTextInput += OnTextInput;
        if (enableImeMyself) keyboard.SetIMEEnabled(true);
    }

    private void OnDisable()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        keyboard.onIMECompositionChange -= OnCompositionChange;
        keyboard.onTextInput -= OnTextInput;
        if (enableImeMyself) keyboard.SetIMEEnabled(false);
        Log("END");
    }

    private void Update() => _frame = Time.frameCount;

    /// <summary>
    /// The composition channel. Empty is the interesting one: it is the
    /// platform saying it has let go, and everything OneText does about a
    /// commit hangs off it.
    /// </summary>
    private void OnCompositionChange(IMECompositionString composition)
    {
        var text = new StringBuilder();
        foreach (char character in composition) text.Append(character);

        string now = text.ToString();
        if (now == _composition) return;
        _composition = now;

        Log(now.Length == 0
            ? "COMPOSE (empty) — the platform says the composition is over"
            : $"COMPOSE {Quote(now)} {CodePoints(now)}");
    }

    /// <summary>
    /// The Input System's text channel. OneText does not read this one, so
    /// anything appearing here and nowhere else is text the field never sees.
    /// </summary>
    private void OnTextInput(char character) =>
        Log($"TEXT    {Quote(character.ToString())} {CodePoints(character.ToString())}" +
            (_composition.Length > 0 ? "  (while composing)" : string.Empty));

    /// <summary>
    /// The IMGUI channel, which is the one OneText reads with Event.PopEvent.
    /// Watched here without popping, so the field still gets everything.
    /// </summary>
    private void OnGUI()
    {
        var e = Event.current;
        if (e == null) return;

        if (e.type == EventType.KeyDown && (e.character != 0 || e.keyCode != KeyCode.None))
        {
            string character = e.character == 0
                ? "(no character)"
                : $"{Quote(e.character.ToString())} {CodePoints(e.character.ToString())}";
            Log($"IMGUI   keyDown key={e.keyCode} {character}" +
                (_composition.Length > 0 ? "  (while composing)" : string.Empty));
        }
    }

    private static string Backend()
    {
        // Named through the package's own answer rather than guessed at, so
        // the log says what OneText decided and not what the probe thinks it
        // should have decided.
        var type = System.Type.GetType("OneText.UGUI.ImeInput, OneText.UGUI");
        var describe = type?.GetMethod("Describe");
        return describe?.Invoke(null, null) as string ?? "unknown (OneText.UGUI not found)";
    }

    private void Log(string message) => Debug.Log($"[IME/IS f{_frame}] {message}");

    private static string CodePoints(string value)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append("U+").Append(((int)value[i]).ToString("X4"));
        }
        return text.Append(']').ToString();
    }

    private static string Quote(string value) => value.Length == 0 ? "\"\"" : $"\"{value}\"";

#else

    private void OnEnable() =>
        Debug.LogWarning(
            "[IME] This probe reads the Input System's keyboard, and this project does " +
            "not have the Input System package enabled (ENABLE_INPUT_SYSTEM is not " +
            "defined). Use OneTextImeProbe.cs instead — it watches the Input Manager.");

#endif
}
