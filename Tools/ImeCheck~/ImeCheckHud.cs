using System;
using System.IO;
using System.Text;
using UnityEngine;
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

        Debug.Log($"[ime-check] {Application.unityVersion} on {Application.platform}, package build {BuildStamp.Value}");
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

public static class BuildStamp
{
    public static string Value = "unknown";
}
