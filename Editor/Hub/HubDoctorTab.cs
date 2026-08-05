using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Doctor: the project's strings against the fonts and break data it ships,
    /// with the same verdict CI gets.
    ///
    /// Nothing in or around Unity answers "will every string in this build
    /// render", and every part of the answer is decidable before the build.
    /// </summary>
    public sealed class HubDoctorTab
    {
        private DoctorReport _report;
        private bool _showInfo = true;

        public void Draw(OneTextHub hub)
        {
            OneTextHub.Header("Doctor",
                "Static renderability analysis: characters no font can draw, Japanese text that " +
                "will come out in Chinese shapes, locales whose line breaking needs a dictionary " +
                "that was never installed.");

            hub.DrawStringFolders();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run"))
                    _report = TextDoctor.Run(hub.StringFolders);
                _showInfo = GUILayout.Toggle(_showInfo, "Show notes",
                    EditorStyles.miniButton, GUILayout.Width(100f));
            }

            EditorGUILayout.Space();
            if (_report == null)
            {
                EditorGUILayout.LabelField("Not run yet.", EditorStyles.miniLabel);
                DrawCiHint();
                return;
            }

            EditorGUILayout.LabelField(_report.Summary(),
                _report.Passed ? EditorStyles.boldLabel : ErrorStyle());

            if (_report.Findings.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing to report — every string renders.", MessageType.Info);
                DrawCiHint();
                return;
            }

            foreach (var finding in _report.Findings)
            {
                if (finding.Severity == DoctorSeverity.Info && !_showInfo) continue;
                DrawFinding(finding);
            }

            DrawCiHint();
        }

        private static void DrawFinding(in DoctorFinding finding)
        {
            var type = finding.Severity switch
            {
                DoctorSeverity.Error => MessageType.Error,
                DoctorSeverity.Warning => MessageType.Warning,
                _ => MessageType.Info,
            };

            EditorGUILayout.HelpBox($"[{finding.Rule}] {finding.Message}", type);
            if (string.IsNullOrEmpty(finding.Source) && string.IsNullOrEmpty(finding.Sample)) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f);
                using (new EditorGUILayout.VerticalScope())
                {
                    if (!string.IsNullOrEmpty(finding.Sample))
                        EditorGUILayout.LabelField("sample", finding.Sample, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(finding.Source))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("in", finding.Source, EditorStyles.miniLabel);
                            var asset = AssetDatabase.LoadAssetAtPath<Object>(finding.Source);
                            using (new EditorGUI.DisabledScope(asset == null))
                            {
                                if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(50f)))
                                    EditorGUIUtility.PingObject(asset);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The command line, spelled out. A lint that only runs when somebody
        /// opens a window is a document; the point of this one is the exit code.
        /// </summary>
        private static void DrawCiHint()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("On CI", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                "Unity -batchmode -projectPath . -executeMethod " +
                "OneText.Editor.TextDoctor.RunFromCommandLine -oneStrings Assets/Localization",
                EditorStyles.textArea, GUILayout.Height(34f));
            EditorGUILayout.LabelField(
                "Exits 1 when a string cannot render, so an unrenderable string fails the merge " +
                "rather than the release.", EditorStyles.wordWrappedMiniLabel);
        }

        private static GUIStyle ErrorStyle()
        {
            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = new Color(0.95f, 0.45f, 0.45f);
            return style;
        }
    }
}
