using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OneText.Samples.Editor
{
    /// <summary>
    /// Writes the demo scene, which is three objects.
    ///
    /// It is three objects because <see cref="OneTextDemo"/> builds its own
    /// canvas at runtime, and that is on purpose: a scene file full of
    /// hand-placed RectTransforms is a thing that rots quietly and that no
    /// reviewer can read. Everything the demo claims lives in code that can be
    /// diffed. What is left over — a camera to clear the frame, an EventSystem
    /// so the buttons take clicks, and the component itself — is small enough
    /// to generate, so it is generated rather than committed.
    ///
    /// Menu: Tools &gt; OneText &gt; Samples &gt; Build Demo Scene. Or, for a
    /// build machine that has no menus:
    ///
    ///   Unity -batchmode -quit -projectPath &lt;project&gt; -executeMethod
    ///       OneText.Samples.Editor.DemoSceneBuilder.Generate
    /// </summary>
    public static class DemoSceneBuilder
    {
        private const string Folder = "Assets/OneTextDemo";
        private const string ScenePath = Folder + "/OneTextDemo.unity";
        private const string PrinciplesPath = Folder + "/OneTextPrinciples.unity";

        /// <summary>
        /// The demo worth showing somebody: tabbed pages that each make one
        /// claim and prove it, with the feature tour as the last of them.
        /// </summary>
        [MenuItem("Tools/OneText/Samples/Build Principles Scene")]
        public static void GeneratePrinciples() => Write(PrinciplesPath, typeof(DemoShell));

        /// <summary>The tour on its own, for anybody who only wants that.</summary>
        [MenuItem("Tools/OneText/Samples/Build Demo Scene")]
        public static void Generate() => Write(ScenePath, typeof(OneTextDemo));

        private static void Write(string path, System.Type root)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera", typeof(Camera));
            var camera = cameraGo.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0x01, 0x04, 0x09, 0xFF);
            // The demo canvas is Screen Space Overlay, so this camera renders
            // nothing at all. It exists because a scene with no camera logs
            // about having no camera, and a demo whose first line of output is
            // a warning starts by looking broken.
            camera.orthographic = true;
            camera.cullingMask = 0;
            cameraGo.tag = "MainCamera";

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var host = new GameObject("OneText Demo", root);
            AssignBundledFont(host);

            Directory.CreateDirectory(Folder);
            EditorSceneManager.SaveScene(scene, path);
            AssetDatabase.Refresh();

            Debug.Log("OneText demo scene written to " + path + ".");
        }

        /// <summary>
        /// Points the new object at the font that ships beside these scripts.
        ///
        /// The demo used to come up blank for everybody except whoever had
        /// already assigned a face to it, which for a sample whose whole job is
        /// to be opened by a stranger is the same as being broken. So one face
        /// travels with the sample — Pretendard Variable, under the SIL Open
        /// Font License, carrying Latin and Hangul and a weight axis in a
        /// single file, which is enough for every page here to have something
        /// to say. Its licence sits next to it and must stay there.
        ///
        /// Found by searching rather than by a fixed path: the sample lands
        /// under <c>Assets/Samples/&lt;package&gt;/&lt;version&gt;/…</c>, and the
        /// version is in that path.
        /// </summary>
        // Primary first; the rest are the fallback chain, in order. Pretendard
        // carries Latin, Hangul, kana and the punctuation the vertical page
        // needs, and the four Noto faces carry the scripts the shaping page
        // argues with.
        //
        // The last two are subsets, and that is the whole reason they can be
        // here. A CJK ideograph face is sixteen megabytes and Noto Color Emoji
        // is ten, which is not a thing to ship so that three specimens can be
        // in Japanese, Chinese and emoji — but the demo does not need a CJK
        // face, it needs seventeen ideographs and six emoji, and those are
        // twelve and eighty kilobytes. Shipping boxes instead was the worse
        // trade: a text engine whose demo cannot draw 日本語 is arguing against
        // itself. `Tools/make_demo_font_subsets.py` regenerates both, and the
        // set it cuts is read out of these sources, so a new specimen string
        // means re-running it rather than editing a list.
        private static readonly string[] BundledFaces =
        {
            "PretendardVariable",
            "NotoSansArabic-Regular",
            "NotoSansThai-Regular",
            "NotoSansDevanagari-Regular",
            "NotoSansHebrew-Regular",
            "NotoSansJP-DemoSubset",
            "NotoColorEmoji-DemoSubset",
        };

        private static void AssignBundledFont(GameObject host)
        {
            // Whichever root the caller asked for; both hold the same
            // OneTextDemoFonts, and both build from it in Awake.
            var shell = host.GetComponent<DemoShell>();
            var tour = host.GetComponent<OneTextDemo>();
            var fonts = shell != null ? shell.Fonts : tour != null ? tour.Fonts : null;
            if (fonts == null) return;

            fonts.FontFiles.Clear();
            foreach (string face in BundledFaces)
            {
                var found = Find(face);
                if (found != null) fonts.FontFiles.Add(found);
                else Debug.LogWarning("OneText demo: bundled face " + face + " was not found; " +
                                      "the pages that need it will draw boxes.");
            }

            if (fonts.FontFiles.Count == 0)
            {
                Debug.LogWarning("OneText demo: no bundled font resolved. Assign one on the " +
                                 "OneText Demo object — a player has no system fonts to fall " +
                                 "back to, so the scene would come up empty.");
                return;
            }

            EditorUtility.SetDirty(host);
        }

        /// <summary>
        /// Finds a bundled face by its bare name.
        ///
        /// The files are named <c>Something.ttf.bytes</c> — the <c>.bytes</c>
        /// so Unity imports them as TextAssets, the <c>.ttf</c> so it is
        /// obvious what they are. Unity strips only the last extension, so the
        /// asset's name is <c>Something.ttf</c>, and an equality test against
        /// <c>Something</c> silently matches nothing and leaves the demo with
        /// no fonts at all.
        /// </summary>
        private static TextAsset Find(string name)
        {
            foreach (string guid in AssetDatabase.FindAssets(name + " t:TextAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null) continue;
                if (Path.GetFileNameWithoutExtension(asset.name) == name ||
                    asset.name == name) return asset;
            }
            return null;
        }
    }
}
