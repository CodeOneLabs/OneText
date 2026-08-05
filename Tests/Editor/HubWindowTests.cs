using OneText.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Tests
{
    /// <summary>
    /// The window, opened and drawn.
    ///
    /// The Hub's logic is tested headlessly next door; this is the other half
    /// of the risk, which is IMGUI itself — a mismatched layout scope or a
    /// style built during a layout pass throws at draw time and nowhere else,
    /// and a tooling window that throws on its second repaint is a tooling
    /// window nobody uses.
    /// </summary>
    public class HubWindowTests
    {
        [Test]
        public void EveryTab_DrawsWithoutThrowing()
        {
            // Batch mode loads no editor skin, so EditorStyles is null and every
            // IMGUI call throws for a reason that has nothing to do with this
            // window. The test is then only meaningful from an editor that has
            // one — which is where a person about to open the Hub is anyway.
            // Probed rather than compared: with no skin loaded the property
            // does not return null, it throws inside itself.
            if (Application.isBatchMode || !HasEditorStyles())
                Assert.Ignore("no editor GUI skin in this session (batch mode)");

            var window = ScriptableObject.CreateInstance<OneTextHub>();
            try
            {
                foreach (OneTextHub.Tab tab in System.Enum.GetValues(typeof(OneTextHub.Tab)))
                {
                    // Twice: a layout pass and a repaint pass see different
                    // things, and the bugs live in the difference.
                    Draw(window, tab);
                    Draw(window, tab);
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static bool HasEditorStyles()
        {
            try
            {
                return EditorStyles.toolbarButton != null;
            }
            catch (System.NullReferenceException)
            {
                return false;
            }
        }

        private static void Draw(OneTextHub window, OneTextHub.Tab tab)
        {
            var method = typeof(OneTextHub).GetMethod("OnGUI",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var field = typeof(OneTextHub).GetField("_tab",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(window, tab);

            // A window that was never shown has no GUI context of its own, so
            // one is borrowed: this is what an EditorWindow repaint does.
            var container = new IMGUIContainer(() => method.Invoke(window, null));
            try
            {
                container.style.width = 800f;
                container.style.height = 600f;
                container.MarkDirtyRepaint();
                container.onGUIHandler();
            }
            catch (System.Exception e) when (e.InnerException is ExitGUIException)
            {
                // GUIUtility.ExitGUI is control flow, not a failure.
            }
        }
    }
}
