// ---------------------------------------------------------------------------
//  OneTextSessionProbe — a throwaway diagnostic for one bug report, and meant
//  to be deleted the moment it has answered its question. It is not part of
//  OneText and it is never compiled inside the package.
//
//  THE REPORT IT IS FOR
//
//      1. Type 아아아아 into a field. It looks right.
//      2. Leave the field without pressing Enter (click somewhere else).
//      3. Click back into the same field and type 아아아아 again.
//
//  What happens instead: the text stops accumulating. The composition cycles
//  아 → 앙 → 아 → 앙 in place, and only one syllable ever reaches the value.
//  The same happens with four different syllables (가나다라), which is what
//  rules out every guard in OneText that compares one syllable against
//  another: none of them can match 가, 나 and 다 in turn. So the question is
//  no longer "which guard swallowed it" but "did the committed character ever
//  arrive at all" — and this answers exactly that.
//
//  Two things it separates that nothing else can:
//
//    * A character event that OneText's field popped is gone before OnGUI
//      runs. So a KEY line below is a keystroke the field did NOT take. In
//      session 1 there should be none; if session 2 is full of them, the field
//      stopped draining the queue and the fix is in the field.
//    * If session 2 has no KEY lines either, and the composition still cycles,
//      then the platform stopped delivering commits after the input method was
//      switched off and on again — and the fix is in how a field ends its IME
//      session, not in anything downstream of it.
//
//  HOW TO USE IT
//
//   1. Copy this file into your project's Assets folder — anywhere inside it.
//   2. Drop the component on any GameObject in the scene with the input field
//      (Add Component > "OneText Session Probe"). No wiring: it follows
//      whichever field has focus. Optionally drag the field into Field to
//      watch that one even while it is not focused.
//   3. Enter play mode and do the three steps above. Type slowly, and after
//      each syllable give it a moment.
//   4. Copy the console out (Console window, right-click > Copy, or the whole
//      of Editor.log) and send it back. Then delete this file.
//
//  It never calls Event.Use or Event.PopEvent: popping is what the real field
//  does, and a second popper would eat the keystrokes it is meant to watch.
// ---------------------------------------------------------------------------

using System.Text;
using OneText;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.EventSystems;

[AddComponentMenu("OneText/OneText Session Probe")]
public sealed class OneTextSessionProbe : MonoBehaviour
{
    [Tooltip("The field to watch. Leave empty to follow whichever one has focus.")]
    public OneTextInputField Field;

    [Tooltip("Writes a MARK line, so two runs can be told apart in one log.")]
    public KeyCode MarkKey = KeyCode.F9;

    private string _last = string.Empty;
    private readonly StringBuilder _line = new StringBuilder();

    private void OnGUI()
    {
        var e = Event.current;
        if (e == null) return;

        if (e.type == EventType.KeyDown && e.keyCode == MarkKey)
        {
            Debug.Log($"[probe] ------------------------------ MARK f={Time.frameCount}");
            return;
        }

        // Reached OnGUI, which means the input field did not pop it. That is
        // the whole point of reading the queue from here.
        if (e.type == EventType.KeyDown || e.rawType == EventType.KeyDown)
        {
            Debug.Log($"[probe] f={Time.frameCount} KEY-NOT-TAKEN code={e.keyCode} " +
                      $"char={Describe(e.character.ToString())} mods={e.modifiers}");
        }
    }

    private void LateUpdate()
    {
        var field = Field != null ? Field : FocusedField();
        var arbiter = ImeCommitArbiter.Shared;

        _line.Clear();
        _line.Append("platform=").Append(Describe(PlatformComposition()));
        _line.Append(" imeOn=").Append(PlatformImeIsSelected());
        _line.Append(" | selected=").Append(SelectedName());
        if (field != null)
        {
            _line.Append(" focused=").Append(field.isFocused);
            _line.Append(" value=").Append(Describe(field.text));
            _line.Append(" composing=").Append(Describe(field.compositionString));
            _line.Append(" caret=").Append(field.caretPosition);
        }
        else
        {
            _line.Append(" field=none");
        }

        _line.Append(" | arbiter=");
        if (arbiter.IsAwaitingPlatform) _line.Append("awaiting");
        else if (arbiter.IsSuppressingEcho) _line.Append("suppressing");
        else _line.Append("idle");
        _line.Append(" pending=").Append(Describe(arbiter.PendingText));
        _line.Append(" replay=").Append(Describe(arbiter.ReplayedComposition));
        _line.Append(" lastCommit=").Append(Describe(arbiter.AcceptedPlatformCommit));

        string now = _line.ToString();
        if (now == _last) return; // only when something moved
        _last = now;
        Debug.Log($"[probe] f={Time.frameCount} {now}");
    }

    private static OneTextInputField FocusedField()
    {
        var selected = EventSystem.current != null
            ? EventSystem.current.currentSelectedGameObject
            : null;
        return selected != null ? selected.GetComponent<OneTextInputField>() : null;
    }

    private static string SelectedName()
    {
        if (EventSystem.current == null) return "no EventSystem";
        var selected = EventSystem.current.currentSelectedGameObject;
        return selected != null ? selected.name : "none";
    }

    // Both guarded: a project on the Input System throws from the device
    // members of UnityEngine.Input, and a probe that crashes the build it was
    // added to instrument is worse than no probe.
    private static string PlatformComposition()
    {
        try { return Input.compositionString; }
        catch (System.Exception error) { return "<threw: " + error.GetType().Name + ">"; }
    }

    private static string PlatformImeIsSelected()
    {
        try { return Input.imeIsSelected.ToString(); }
        catch (System.Exception error) { return "<threw: " + error.GetType().Name + ">"; }
    }

    /// <summary>
    /// The text, and every code point in it, because half of this report is
    /// two strings that look identical in a console and are not: 한 is one
    /// character or three depending on which channel it came from.
    /// </summary>
    private static string Describe(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var text = new StringBuilder("\"").Append(value).Append("\"[");
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append("U+").Append(((int)value[i]).ToString("X4"));
        }
        return text.Append(']').ToString();
    }
}
