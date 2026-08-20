using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using OneText.UGUI;

/// <summary>
/// The two 2026-08-20 IME reports, put in front of a person on the platform
/// that reported them. Everything it draws is read live off the field, and
/// everything Unity logs is mirrored to ime-check.log beside the executable so
/// a run that still misbehaves can be sent back as evidence rather than as a
/// description.
/// </summary>
public sealed class ImeCheckHud : MonoBehaviour
{
    public OneTextInputField Field;
    public string FontFileName = "ImeCheckFont.ttf";

    private string _logPath;
    private StreamWriter _log;
    private GUIStyle _mono;
    private readonly StringBuilder _sb = new StringBuilder();

    private void Awake()
    {
        _logPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "ime-check.log");
        try
        {
            _log = new StreamWriter(_logPath, false) { AutoFlush = true };
            Application.logMessageReceived += Mirror;
        }
        catch (Exception e) { Debug.LogWarning("no log file: " + e.Message); }

        string font = Path.Combine(Application.streamingAssetsPath, FontFileName);
        if (File.Exists(font) && Field != null && Field.textComponent != null)
            Field.textComponent.SetFont(File.ReadAllBytes(font));

        // Without this the log this whole player exists to produce is empty:
        // the field's own account of what it did is off by default and costs
        // nothing when off, which is right everywhere except here.
        OneTextInputField.LogEditing = true;

        Debug.Log($"[ime-check] {Application.unityVersion} on {Application.platform}, package build {BuildStamp.Value}");
    }

    /// <summary>
    /// A person opens this player and clicks the field, which is the right
    /// thing to make them do — the click is part of what is being checked.
    /// A machine driving the same player has no reason to aim a mouse at a
    /// rectangle it has to compute, so <c>-imeAutoFocus</c> puts the caret in
    /// the field at startup and nothing else changes.
    /// </summary>
    private void Start()
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (!string.Equals(arg, "-imeAutoFocus", StringComparison.Ordinal)) continue;
            if (Field == null) break;
            Field.Select();
            Field.ActivateInputField();
            Debug.Log("[ime-check] -imeAutoFocus: the field is focused without a click");
            break;
        }
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= Mirror;
        _log?.Dispose();
    }

    private void Mirror(string message, string stack, LogType type)
    {
        try { _log?.WriteLine($"{Time.frameCount}\t{type}\t{message}"); } catch { }
    }

    private static string Points(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var sb = new StringBuilder();
        foreach (char c in value) sb.Append($"U+{(int)c:X4} ");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The driver's way of clicking. A machine has no reason to aim a mouse
    /// at a rectangle it has to compute, and the focus-loss half of the
    /// 2026-08-21 report is a click on empty canvas: the EventSystem deselects
    /// the field and nothing else happens. So the driver writes one command
    /// to ime-drive.txt beside the executable — "away" deselects the way that
    /// click does, "back" selects the field again, "mark &lt;text&gt;" puts a
    /// line in the log so the verdict can cut it into scenarios — and the file
    /// is deleted once obeyed, which is how the driver knows it landed.
    /// </summary>
    private string _drivePath;

    private void Update()
    {
        if (_drivePath == null)
            _drivePath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "ime-drive.txt");
        if (!File.Exists(_drivePath)) return;

        string command;
        try
        {
            command = File.ReadAllText(_drivePath).Trim();
            File.Delete(_drivePath);
        }
        catch (IOException) { return; } // mid-write; it will still be there next frame

        if (string.Equals(command, "away", StringComparison.Ordinal))
        {
            Debug.Log("[drive ] away");
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            else if (Field != null) Field.DeactivateInputField();
        }
        else if (string.Equals(command, "back", StringComparison.Ordinal))
        {
            Debug.Log("[drive ] back");
            if (Field != null) { Field.Select(); Field.ActivateInputField(); }
        }
        else if (command.StartsWith("mark ", StringComparison.Ordinal))
        {
            Debug.Log($"[mark  ] {command.Substring(5)}");
        }
        else
        {
            Debug.Log($"[drive ] unknown command '{command}'");
        }
    }

    private string _lastComposition = "\u0000";
    private string _lastValue = "\u0000";

    /// <summary>
    /// The one thing the field's own trace does not say and every diagnosis so
    /// far has turned on: whether the report moved this frame, and in what
    /// code points. ㅇ as U+3147 and ㅇ as U+110B read the same on screen and
    /// are not the same to the discriminator that decides whether a syllable
    /// was deleted or committed.
    /// </summary>
    private void LateUpdate()
    {
        if (Field == null) return;

        string composing = Field.compositionString ?? string.Empty;
        if (!string.Equals(composing, _lastComposition, StringComparison.Ordinal))
        {
            Debug.Log($"[report] {Quoted(_lastComposition)} -> {Quoted(composing)}");
            _lastComposition = composing;
        }

        string value = Field.text ?? string.Empty;
        if (!string.Equals(value, _lastValue, StringComparison.Ordinal))
        {
            Debug.Log($"[value ] {Quoted(_lastValue)} -> {Quoted(value)}");
            _lastValue = value;
        }
    }

    private static string Quoted(string value) =>
        value == "\u0000" ? "(start)" : $"'{value}' [{Points(value)}]";

    private void OnGUI()
    {
        _mono ??= new GUIStyle(GUI.skin.label) { fontSize = 16, richText = false };

        _sb.Clear();
        _sb.AppendLine("OneText IME check — 2026-08-20 리포트 두 건");
        _sb.AppendLine();
        _sb.AppendLine("1) 위 칸을 클릭하고 한글로 ㅁ 을 다섯 번 누르세요.");
        _sb.AppendLine("   기대: ㅁㅁㅁㅁㅁ  (다섯 개, 눌린 만큼)");
        _sb.AppendLine();
        _sb.AppendLine("2) 칸을 비우고 '아' 를 친 뒤 백스페이스를 두 번 누르세요.");
        _sb.AppendLine("   기대: 첫 번째에 ㅇ 만 남고, 두 번째에 완전히 사라짐");
        _sb.AppendLine("   (두 번째가 씹히고 세 번 눌러야 지워지면 아직 버그입니다)");
        _sb.AppendLine();

        if (Field != null)
        {
            _sb.AppendLine($"value      : '{Field.text}'  ({Field.text.Length}자)");
            _sb.AppendLine($"             {Points(Field.text)}");
            _sb.AppendLine($"composing  : '{Field.compositionString}'  {Points(Field.compositionString)}");
            _sb.AppendLine($"displayed  : '{Field.displayText}'");
            _sb.AppendLine($"isComposing: {Field.isComposing}");
        }
        _sb.AppendLine();
        _sb.AppendLine("로그: " + _logPath);

        GUI.Label(new Rect(24f, 120f, Screen.width - 48f, Screen.height - 140f), _sb.ToString(), _mono);
    }
}

