// ---------------------------------------------------------------------------
//  OneTextImeProbe — a throwaway diagnostic. It is not part of OneText, it is
//  not shipped with it, and it is meant to be deleted the moment it has
//  answered its question.
//
//  WHAT IT IS FOR
//
//  Every fix OneText has made to Korean input rests on a model of what the
//  platform sends and when: that the composition arrives on one channel, the
//  committed text on another, and that a printable character event during a
//  composition is always committed text and never a jamo still being composed.
//  That model was read out of the Windows IME documentation and verified
//  against it. Nobody has ever watched macOS do it. Four rounds of fixes have
//  not stopped a character appearing that the user did not type, which is what
//  it looks like when the model is wrong rather than the code.
//
//  So this prints what the two channels actually say, frame by frame, and the
//  next change gets made against a recording instead of a guess.
//
//  HOW TO USE IT
//
//   1. Copy this file into your project's Assets folder — anywhere inside it.
//      (It lives in a "~" folder in the package so that Unity never compiles
//      it there and it can never reach anybody's build. Copying it out is the
//      one step that makes it run.)
//   2. Make an empty GameObject in the scene you normally type into, and add
//      the "IME Channel Probe" component to it (Add Component > OneText >
//      Diagnostics). It needs no wiring and it does not touch OneText: what it
//      reports is the platform, not the package.
//   3. Enter play mode.
//
//  Then two runs, back to back, in this order. They differ by one keystroke,
//  and that keystroke is the whole question: pressing Enter is the case you
//  say works, and not pressing it is the case that adds a character.
//
//   RUN A — the good case.
//      Press F9 (this writes a MARK line so the two runs can be told apart;
//      press it when nothing is being composed, or the input method will eat
//      it). Click into your input field. Type 한 — ㅎ, then ㅏ, then ㄴ.
//      Press Enter. Then click into another field, or anywhere outside.
//
//   RUN B — the case that duplicates.
//      Press F9 again. Click into the same field. Type 한 the same way.
//      Do NOT press Enter. Click straight into another field. Now wait two
//      full seconds without touching anything — that pause is what lets the
//      probe say whether the platform is still holding the syllable. Then
//      click back into the first field and type 국.
//
//   Press F9 once more to close the recording.
//
//   4. Copy the console out (Console window, right-click > Copy, or the whole
//      of Editor.log) and send it back, along with one sentence: where did the
//      extra character appear — in the field you left, or the field you moved
//      to? That answer alone rules out half of what is on the table.
//   5. Delete this file.
//
//  WHAT IT WILL NOT DO
//
//  It never consumes an event. It reads them through OnGUI, which is shown
//  every event without being given it, and it never calls Event.Use or
//  Event.PopEvent. Popping is what the real input field does, and a second
//  popper would eat the keystrokes it was meant to be watching.
//
//  It also logs only when something changes. Typing 한국 should be a dozen
//  lines, not a screenful a frame.
// ---------------------------------------------------------------------------

using System;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

[AddComponentMenu("OneText/Diagnostics/IME Channel Probe")]
[DisallowMultipleComponent]
public sealed class OneTextImeProbe : MonoBehaviour
{
    [Tooltip("Switch the input method on from here as well as from wherever your " +
             "field does it. Harmless when a field is already doing it — both set " +
             "the same value — and necessary if you want to watch the platform " +
             "with nothing else in the scene.")]
    public bool enableImeMyself = true;

    [Tooltip("Stop logging after this many lines, so that an input method stuck in " +
             "a loop cannot fill the console before you can read it.")]
    public int maxLines = 400;

    [Tooltip("Report a composition that has not changed for this many frames, once. " +
             "That line is the one that says the platform is still holding a syllable " +
             "the field believes it has already finished with.")]
    public int holdFrames = 90;

    [Tooltip("Writes a MARK line into the log, so that two runs recorded one after " +
             "the other can be told apart in a single paste. It carries no character, " +
             "so an input field ignores it — but press it between runs rather than " +
             "mid-syllable, because a live input method may eat it before Unity sees " +
             "it. Change it if this key is bound to something in your game.")]
    public KeyCode markKey = KeyCode.F9;

#if ENABLE_LEGACY_INPUT_MANAGER

    private string _composition = string.Empty;
    private int _compositionChangedFrame = -1;
    private int _compositionSinceFrame;
    private bool _holdReported;
    private IMECompositionMode _mode;
    private bool _imeSelected;
    private int _selectedId;
    private int _marks;
    private int _lines;

    private void OnEnable()
    {
        _composition = Input.compositionString ?? string.Empty;
        _compositionSinceFrame = Time.frameCount;
        _mode = Input.imeCompositionMode;
        _imeSelected = Input.imeIsSelected;

        if (enableImeMyself) Input.imeCompositionMode = IMECompositionMode.On;

        Log($"probe started. platform={Application.platform} unity={Application.unityVersion} " +
            $"compositionMode={Input.imeCompositionMode} imeIsSelected={Input.imeIsSelected} " +
            $"composition={Quote(_composition)}");
        Log("legend: COMP = Input.compositionString changed, with its code points. " +
            "KEY = a key event, watched without being consumed. SEL = the EventSystem " +
            "moved the keyboard to another object. HOLD = the composition has stood " +
            "still. MARK = you pressed the marker key. The question every KEY line " +
            "answers is what the composition said at that moment, whether it had just " +
            "moved, and how long ago it last did.");
        Log($"the recording wants two runs: 한 then Enter then away, and 한 then away " +
            $"with no Enter. Press {markKey} between them.");
    }

    private void OnDisable()
    {
        // Auto rather than Off, for the reason LegacyImeInput gives: Off would
        // leave the input method disabled for everything else in the editor.
        if (enableImeMyself) Input.imeCompositionMode = IMECompositionMode.Auto;
    }

    private void Update() => PollComposition();

    private void OnGUI()
    {
        // Layout and Repaint come through here too and say nothing about input.
        // The composition is polled first anyway, because it can move between
        // two events inside one frame and this is the only place that would see
        // it happen.
        PollComposition();

        var keyEvent = Event.current;
        if (keyEvent == null) return;

        bool down = keyEvent.type == EventType.KeyDown || keyEvent.rawType == EventType.KeyDown;
        if (!down && !(keyEvent.isKey && keyEvent.character != '\0')) return;

        if (down && keyEvent.keyCode == markKey)
        {
            Log($"---------------- MARK #{++_marks} ----------------");
            return;
        }

        // Read, never taken. No Use(), no PopEvent: the field this is watching
        // needs every one of these.
        //
        // sinceComp is the number the whole recording turns on. A character
        // that arrives on the same frame the composition moved is the platform
        // handing over and moving on in one step; one that arrives many frames
        // after the composition last said anything is the platform finishing
        // something it had been sitting on — which is the shape the defocus
        // case is expected to have.
        Log($"KEY  {Format(keyEvent)} | comp={Quote(_composition)} {Points(_composition)} " +
            $"compChangedThisFrame={(_compositionChangedFrame == Time.frameCount ? "YES" : "no")} " +
            $"sinceComp=+{Time.frameCount - _compositionSinceFrame}f");
    }

    /// <summary>
    /// Who the EventSystem currently has selected, which for a uGUI field is
    /// the same thing as who has the keyboard.
    ///
    /// It is here because the question this recording exists to answer is about
    /// the moment focus leaves: a character that arrives after that line is a
    /// character the field it belonged to can no longer see, because the queue
    /// is drained by whoever is selected now. Reading it costs a reference
    /// compare and it is the only line in the log that says where the keyboard
    /// went.
    /// </summary>
    private void PollSelection()
    {
        var system = EventSystem.current;
        var selected = system != null ? system.currentSelectedGameObject : null;
        int id = selected != null ? selected.GetInstanceID() : 0;
        if (id == _selectedId) return;

        _selectedId = id;
        Log($"SEL  the keyboard is now on {(selected != null ? Quote(selected.name) : "nothing")}");
    }

    private void PollComposition()
    {
        PollSelection();

        string now = Input.compositionString ?? string.Empty;

        if (!string.Equals(now, _composition, StringComparison.Ordinal))
        {
            Log($"COMP {Quote(_composition)} -> {Quote(now)} {Points(now)}");
            _composition = now;
            _compositionChangedFrame = Time.frameCount;
            _compositionSinceFrame = Time.frameCount;
            _holdReported = false;
        }
        else if (!_holdReported && now.Length > 0 &&
                 Time.frameCount - _compositionSinceFrame >= holdFrames)
        {
            _holdReported = true;
            Log($"HOLD {Quote(now)} {Points(now)} unchanged for {holdFrames} frames — " +
                "the platform is still composing this");
        }

        if (Input.imeCompositionMode != _mode || Input.imeIsSelected != _imeSelected)
        {
            _mode = Input.imeCompositionMode;
            _imeSelected = Input.imeIsSelected;
            Log($"IME  compositionMode={_mode} imeIsSelected={_imeSelected}");
        }
    }

    private void Log(string message)
    {
        if (_lines > maxLines) return;

        _lines++;
        if (_lines > maxLines)
        {
            Debug.Log("[IME] line budget reached; the probe is silent from here. Raise " +
                      "maxLines on the component and enter play mode again.");
            return;
        }

        Debug.Log($"[IME] f={Time.frameCount,-7} {message}");
    }

    /// <summary>
    /// One key event, with the character as a code point rather than only as a
    /// glyph. Which of those it is matters more than anything else in this log:
    /// 'ㅎ' and '하' and '한' are one keystroke apart on screen and nowhere near
    /// each other in Unicode, and a syllable that arrives decomposed (U+1112
    /// U+1161 U+11AB) looks identical in a console to one that arrives composed
    /// (U+D55C) while behaving nothing like it.
    /// </summary>
    private static string Format(Event keyEvent)
    {
        string character = keyEvent.character == '\0'
            ? "none        "
            : $"U+{(int)keyEvent.character:X4} {Glyph(keyEvent.character)}";
        string type = keyEvent.type == keyEvent.rawType
            ? keyEvent.type.ToString()
            : $"{keyEvent.type}/raw:{keyEvent.rawType}";
        return $"{type,-8} char={character} key={keyEvent.keyCode} mods={keyEvent.modifiers}";
    }

    private static string Glyph(char character) =>
        char.IsControl(character) ? "(control)" : $"'{character}'";

    /// <summary>Every UTF-16 unit of a string, which is the unit the field indexes by.</summary>
    private static string Points(string value)
    {
        if (value.Length == 0) return "[]";

        var text = new StringBuilder(value.Length * 7 + 2).Append('[');
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
            "[IME] This probe reads Input.compositionString, and this project has " +
            "Active Input Handling set to \"Input System Package (New)\", where that " +
            "property throws instead of answering. Set Project Settings > Player > " +
            "Active Input Handling to \"Both\" for the length of this test — it changes " +
            "nothing else about how the project runs — or say so and a probe for the " +
            "other backend can be written instead.");

#endif
}
