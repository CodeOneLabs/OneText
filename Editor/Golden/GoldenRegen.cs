using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// The approve-new-goldens workflow, and the only thing that is allowed to
    /// overwrite a baseline.
    ///
    /// Keeping it in one place is the point. A golden suite fails the moment
    /// approving a change is easier than understanding it, so the tests never
    /// write baselines and this never asserts anything: whoever runs it is
    /// saying, in the commit, that they looked at the new pictures.
    ///
    /// Batch: Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///        OneText.Editor.GoldenRegen.RegenerateAll
    /// </summary>
    public static class GoldenRegen
    {
        [MenuItem("Tools/OneText/Golden Images/Regenerate All Baselines")]
        public static void RegenerateAll()
        {
            var log = new StringBuilder();
            string directory = GoldenCases.BaselineDirectory;
            Directory.CreateDirectory(directory);

            int written = 0, skipped = 0, failed = 0;
            foreach (var golden in GoldenCases.All)
            {
                string missing = golden.MissingFont();
                if (missing != null)
                {
                    skipped++;
                    log.AppendLine($"  skip  {golden.Name}: needs {missing}");
                    continue;
                }

                Texture2D texture = null;
                try
                {
                    texture = golden.Render();
                    File.WriteAllBytes(golden.BaselinePath, texture.EncodeToPNG());
                    written++;
                    log.AppendLine($"  write {golden.Name} ({golden.Width}x{golden.Height})");
                }
                catch (Exception exception)
                {
                    failed++;
                    log.AppendLine($"  FAIL  {golden.Name}: {exception.Message}");
                    Debug.LogException(exception);
                }
                finally
                {
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            // Last, and only when something was actually written: the stamp is
            // what tells the next machine whether these pictures are about its
            // rasterizer or somebody else's.
            if (written > 0)
                File.WriteAllText(GoldenCases.RendererStampPath, GoldenCases.RendererStamp + "\n");

            Debug.Log($"OneText golden baselines -> {directory}\n" +
                      $"{written} written, {skipped} skipped, {failed} failed\n" +
                      $"renderer: {GoldenCases.RendererStamp}\n{log}");

            if (failed > 0)
                throw new InvalidOperationException(
                    $"{failed} golden case(s) failed to render; baselines were not written for them.");
        }

        /// <summary>
        /// Renders every case into <c>ONETEXT_GOLDEN_OUT</c> (or the temp
        /// directory) without touching a baseline: for looking at what the
        /// engine currently draws before deciding whether to approve it.
        /// </summary>
        [MenuItem("Tools/OneText/Golden Images/Render All Without Approving")]
        public static void RenderAll()
        {
            string directory = GoldenComparer.OutputDirectory;
            Directory.CreateDirectory(directory);

            foreach (var golden in GoldenCases.All)
            {
                if (golden.MissingFont() != null) continue;
                var texture = golden.Render();
                File.WriteAllBytes(Path.Combine(directory, golden.Name + "_actual.png"),
                    texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
            }

            Debug.Log($"OneText golden renders -> {directory}");
        }

        [MenuItem("Tools/OneText/Golden Images/Reveal Baseline Folder")]
        public static void RevealBaselines()
        {
            Directory.CreateDirectory(GoldenCases.BaselineDirectory);
            EditorUtility.RevealInFinder(GoldenCases.BaselineDirectory);
        }
    }
}
