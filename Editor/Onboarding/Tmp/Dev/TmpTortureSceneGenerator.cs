using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OneText.Editor
{
    /// <summary>
    /// A TextMesh Pro project's bad day, built on purpose, so the migration can
    /// be run against something that argues back.
    ///
    /// The proof generator next door builds three components and photographs
    /// them, which answers "does the swap preserve the look". This answers a
    /// different question — "what does the report say about a screen nobody
    /// wrote carefully" — and so it is built the other way round: every label
    /// here exists because it is the one that makes some rule in the scan fire.
    /// Tags OneText prints rather than obeys, an alignment with no counterpart,
    /// an overflow mode that used to flow into a second box, a margin that is
    /// about to stop existing, effects living on a material instead of a
    /// component, a font asset whose source file is nowhere, a field in a
    /// ScriptableObject naming a component two prefabs down.
    ///
    /// It writes a scene, two prefabs and an asset, and converts none of them:
    /// the point is to press Scan yourself and read what comes back. Nothing
    /// outside <see cref="WorkFolder"/> is touched, and deleting that folder
    /// undoes the whole thing.
    ///
    /// Menu: Tools > OneText > Dev > Build TMP Torture Scene. Or:
    /// Unity -batchmode -quit -projectPath &lt;dev&gt; -executeMethod
    ///     OneText.Editor.TmpTortureSceneGenerator.Generate
    /// </summary>
    public static class TmpTortureSceneGenerator
    {
        private const string WorkFolder = "Assets/OneTextTmpTorture";
        private const string ScenePath = WorkFolder + "/TmpTorture.unity";
        private const string LeafPath = WorkFolder + "/Leaf.prefab";
        private const string HostPath = WorkFolder + "/Host.prefab";
        private const string PointerPath = WorkFolder + "/Pointer.asset";
        private const string OrphanFontPath = WorkFolder + "/OrphanFont SDF.asset";

        private const string ReferrerType = "TmpTortureReferrer";
        private const string SettingsType = "TmpTortureSettings";
        private const string ReferrerPath = WorkFolder + "/" + ReferrerType + ".cs";
        private const string SettingsPath = WorkFolder + "/" + SettingsType + ".cs";

        /// <summary>Where the next label goes. One column, laid out downwards.</summary>
        private const float Left = 40f;
        private const float Width = 900f;

        [MenuItem("Tools/OneText/Dev/Build TMP Torture Scene")]
        public static void Generate()
        {
            if (!Ready()) return;

            Directory.CreateDirectory(WorkFolder);
            AssetDatabase.Refresh();

            if (!Scripts()) return;

            var materials = Materials();
            var orphan = OrphanFont();

            string leaf = BuildLeafPrefab();
            string host = BuildHostPrefab(leaf);
            BuildPointerAsset(leaf);
            BuildScene(host, materials, orphan);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "OneText: TMP torture fixture written to " + WorkFolder + ".\n" +
                "The scene is not in Build Settings, so the Onboarding scan will skip it until " +
                "you tick 'Also scan scenes that are not in Build Settings' — or add it to Build " +
                "Settings yourself.\n" +
                "Delete " + WorkFolder + " to undo everything this made.");

            var scene = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ScenePath);
            if (scene != null) EditorGUIUtility.PingObject(scene);
        }

        [MenuItem("Tools/OneText/Dev/Build TMP Torture Scene", true)]
        private static bool CanGenerate() => !Application.isPlaying;

        /// <summary>
        /// Whether TMP has anything to build with.
        ///
        /// A project can have the package and not yet have pressed "Import TMP
        /// Essentials", in which case there is no default font asset, every
        /// label here would come out with a null font, and the scan would report
        /// on a fixture rather than on TextMesh Pro. Better to say so.
        /// </summary>
        private static bool Ready()
        {
            // Asked through the settings object rather than straight at the
            // property, because on a project that never imported the resources
            // there is no settings object and the property dereferences it
            // without looking — the check for "is TMP set up" is itself a
            // NullReferenceException, which is a poor way to tell somebody to
            // press Import.
            if (TMP_Settings.instance != null && TMP_Settings.defaultFontAsset != null) return true;

            Debug.LogError("OneText: TextMesh Pro has no default font asset in this project, so " +
                           "there is nothing to build a TMP scene out of. Window > TextMeshPro > " +
                           "Import TMP Essential Resources, then run this again.");
            return false;
        }

        // ------------------------------------------------------------ scripts

        /// <summary>
        /// The two fixture scripts, written into the project the first time and
        /// found there every time after.
        ///
        /// They cannot ship inside the package. A MonoBehaviour compiled into an
        /// editor-only assembly is one Unity refuses to put on a GameObject —
        /// <c>AddComponent</c> hands back null and logs nothing useful — and
        /// this assembly is editor-only for the good reason that it exists to
        /// build fixtures. So they are generated as ordinary project scripts,
        /// which is also where a script holding a <c>TMP_Text</c> field lives in
        /// every project this migration will ever be pointed at.
        ///
        /// Returns false on the run that writes them: the types do not exist
        /// until the compile that follows, and there is no way to wait for a
        /// domain reload from inside the call that triggered it. Running the
        /// menu item again is the second half.
        /// </summary>
        private static bool Scripts()
        {
            if (Find(ReferrerType) != null && Find(SettingsType) != null) return true;

            File.WriteAllText(ReferrerPath,
                "using TMPro;\n" +
                "using UnityEngine;\n" +
                "\n" +
                "/// <summary>\n" +
                "/// Two fields naming text components, one wide and one narrow.\n" +
                "///\n" +
                "/// TMP_Text widens to OneTextLabel once the script rewrite has run, so a field\n" +
                "/// like the first can be re-pointed by the migration and is. The second is the\n" +
                "/// same story under a different name. Written by OneText's TMP torture fixture;\n" +
                "/// delete the folder to be rid of it.\n" +
                "/// </summary>\n" +
                "public sealed class " + ReferrerType + " : MonoBehaviour\n" +
                "{\n" +
                "    public TMP_Text Wide;\n" +
                "    public TextMeshProUGUI Narrow;\n" +
                "}\n");

            File.WriteAllText(SettingsPath,
                "using TMPro;\n" +
                "using UnityEngine;\n" +
                "\n" +
                "/// <summary>\n" +
                "/// A settings asset with text fields on it: the referrer nothing in a migration\n" +
                "/// opens for its own sake, and an ordinary thing for a project to have.\n" +
                "/// </summary>\n" +
                "public sealed class " + SettingsType + " : ScriptableObject\n" +
                "{\n" +
                "    public TMP_Text Wide;\n" +
                "    public TextMeshProUGUI Narrow;\n" +
                "}\n");

            AssetDatabase.Refresh();
            Debug.Log("OneText: wrote the two fixture scripts to " + WorkFolder + ". They have to " +
                      "compile before anything can hold one, so run this again once the editor " +
                      "has finished reloading — the second run builds the scene.");
            return false;
        }

        /// <summary>The fixture type by name, or null before the compile that makes it.</summary>
        private static Type Find(string name)
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<UnityEngine.Object>())
                if (type.Name == name) return type;
            return null;
        }

        // ---------------------------------------------------------- materials

        /// <summary>
        /// The four material presets a real project has, because a real project
        /// keeps its look on the material and this migration has to go and get
        /// it.
        ///
        /// Measured on Five-Dice: 69 of 70 TMP materials carry an effect and 67
        /// of them an outline. A fixture whose labels all share the default
        /// material is a fixture that never exercises the half of the reader
        /// that matters most.
        /// </summary>
        private sealed class Presets
        {
            public Material Outline, Shadow, Glow, Everything;
        }

        private static Presets Materials()
        {
            var basis = TMP_Settings.defaultFontAsset.material;
            var presets = new Presets
            {
                Outline = Preset(basis, "Outline"),
                Shadow = Preset(basis, "Shadow"),
                Glow = Preset(basis, "Glow"),
                Everything = Preset(basis, "Everything"),
            };

            Set(presets.Outline, "_OutlineWidth", 0.25f);
            SetColor(presets.Outline, "_OutlineColor", new Color(0.05f, 0.05f, 0.1f, 1f));
            Set(presets.Outline, "_OutlineSoftness", 0.05f);

            SetColor(presets.Shadow, "_UnderlayColor", new Color(0f, 0f, 0f, 0.75f));
            Set(presets.Shadow, "_UnderlayOffsetX", 0.6f);
            Set(presets.Shadow, "_UnderlayOffsetY", -0.6f);
            Set(presets.Shadow, "_UnderlaySoftness", 0.3f);
            Set(presets.Shadow, "_UnderlayDilate", 0.1f);

            SetColor(presets.Glow, "_GlowColor", new Color(0.4f, 0.8f, 1f, 1f));
            Set(presets.Glow, "_GlowOuter", 0.4f);
            Set(presets.Glow, "_GlowInner", 0.05f);
            Set(presets.Glow, "_GlowPower", 0.75f);

            Set(presets.Everything, "_OutlineWidth", 0.2f);
            SetColor(presets.Everything, "_OutlineColor", new Color(0.6f, 0f, 0.3f, 1f));
            SetColor(presets.Everything, "_UnderlayColor", new Color(0f, 0f, 0f, 0.6f));
            Set(presets.Everything, "_UnderlayOffsetX", 0.4f);
            Set(presets.Everything, "_UnderlayOffsetY", -0.4f);
            SetColor(presets.Everything, "_GlowColor", new Color(1f, 0.9f, 0.4f, 1f));
            Set(presets.Everything, "_GlowOuter", 0.3f);
            // A face colour that disagrees with the vertex colour, which is the
            // TMP habit that makes "what colour is this label" a two-place
            // question.
            SetColor(presets.Everything, "_FaceColor", new Color(1f, 0.85f, 0.5f, 1f));
            Set(presets.Everything, "_FaceDilate", 0.1f);

            foreach (var material in new[] { presets.Outline, presets.Shadow, presets.Glow,
                         presets.Everything })
                EditorUtility.SetDirty(material);
            return presets;
        }

        private static Material Preset(Material basis, string name)
        {
            string path = $"{WorkFolder}/{name} SDF.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var material = new Material(basis) { name = name + " SDF" };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void Set(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        /// <summary>
        /// A font asset made from a font with no file under Assets, which is the
        /// commonest error a real migration reports and the one nobody can fix
        /// without being told which typeface to go and find.
        /// </summary>
        private static TMP_FontAsset OrphanFont()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OrphanFontPath);
            if (existing != null) return existing;

            // A copy of a working font asset with the source reference cut, not
            // a font asset built from scratch out of something unreadable.
            //
            // Building one is the obvious way and it does not work: the only
            // font guaranteed to be present without a file under Assets is the
            // builtin, and the builtin ships without font data, so TMP declines
            // to make an asset from it. The copy lands in exactly the state a
            // real project reaches by a different road — somebody deleted the
            // .ttf and kept the font asset — which is the state being fixtured.
            string source = AssetDatabase.GetAssetPath(TMP_Settings.defaultFontAsset);
            if (string.IsNullOrEmpty(source)) return null;
            if (!AssetDatabase.CopyAsset(source, OrphanFontPath)) return null;

            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OrphanFontPath);
            if (font == null) return null;

            var serialized = new SerializedObject(font);
            var property = serialized.FindProperty("m_SourceFontFile");
            var guid = serialized.FindProperty("m_SourceFontFileGUID");
            if (property != null) property.objectReferenceValue = null;
            if (guid != null) guid.stringValue = string.Empty;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(font);
            return font;
        }

        // ------------------------------------------------------------ prefabs

        /// <summary>
        /// A prefab with the label one level down: the shape that breaks a
        /// partial migration, because anything pointing at it is naming a
        /// component in a file of its own.
        /// </summary>
        private static string BuildLeafPrefab()
        {
            var root = new GameObject("Leaf", typeof(RectTransform));
            var label = Child(root.transform, "Leaf Label", new Rect(0f, 0f, 400f, 60f));

            var text = label.AddComponent<TextMeshProUGUI>();
            text.text = "a label that lives in its own file";
            text.fontSize = 28f;
            text.color = Color.white;

            PrefabUtility.SaveAsPrefabAsset(root, LeafPath);
            UnityEngine.Object.DestroyImmediate(root);
            return LeafPath;
        }

        /// <summary>
        /// A prefab that nests the leaf and holds a field naming the label
        /// inside it — a reference that crosses two files, which is the one the
        /// module was measured to have been silent about.
        /// </summary>
        private static string BuildHostPrefab(string leafPath)
        {
            var root = new GameObject("Host", typeof(RectTransform));
            root.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.26f, 1f);

            var nested = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(leafPath));
            nested.transform.SetParent(root.transform, false);

            var label = nested.GetComponentInChildren<TextMeshProUGUI>(true);
            root.AddComponent<Button>().targetGraphic = label;
            PointAt(root.AddComponent(Find(ReferrerType)), label);

            PrefabUtility.SaveAsPrefabAsset(root, HostPath);
            UnityEngine.Object.DestroyImmediate(root);
            return HostPath;
        }

        /// <summary>
        /// The half of the reference problem that is not a scene and not a
        /// prefab, and that nothing in a migration ever opens for its own sake.
        /// </summary>
        private static void BuildPointerAsset(string leafPath)
        {
            var label = AssetDatabase.LoadAssetAtPath<GameObject>(leafPath)
                .GetComponentInChildren<TextMeshProUGUI>(true);

            var pointer = AssetDatabase.LoadAssetAtPath<ScriptableObject>(PointerPath);
            if (pointer == null)
            {
                pointer = ScriptableObject.CreateInstance(Find(SettingsType));
                AssetDatabase.CreateAsset(pointer, PointerPath);
            }

            PointAt(pointer, label);
        }

        /// <summary>
        /// Fills the fixture's two fields, through the serialized object rather
        /// than through the type.
        ///
        /// The types are compiled from source this same class wrote, in the
        /// project rather than in this assembly, so there is nothing here to
        /// cast to. Which is fine — the migration reads these fields the same
        /// way, and a fixture wired through <c>SerializedObject</c> is wired
        /// exactly as the inspector would have wired it.
        /// </summary>
        private static void PointAt(UnityEngine.Object holder, TextMeshProUGUI label)
        {
            if (holder == null) return;

            var serialized = new SerializedObject(holder);
            var wide = serialized.FindProperty("Wide");
            var narrow = serialized.FindProperty("Narrow");
            if (wide != null) wide.objectReferenceValue = label;
            if (narrow != null) narrow.objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(holder);
        }

        // -------------------------------------------------------------- scene

        private static void BuildScene(string hostPath, Presets materials, TMP_FontAsset orphan)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Camera", typeof(Camera));
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            var camera = cameraGo.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.09f, 0.12f, 1f);

            var canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvasGo.GetComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;

            float y = 20f;
            Tags(canvasGo.transform, ref y);
            Geometry(canvasGo.transform, ref y);
            Effects(canvasGo.transform, materials, ref y);
            Colour(canvasGo.transform, orphan, ref y);
            Layouts(canvasGo.transform, ref y);
            Controls(canvasGo.transform, ref y);
            Nested(canvasGo.transform, hostPath, ref y);
            World();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        // ---- the tags OneText obeys, and the ones it prints

        private static void Tags(Transform parent, ref float y)
        {
            Heading(parent, "Rich text", ref y);

            var supported = Label(parent, "Tags · supported", ref y, 70f);
            supported.text =
                "<b>bold</b> <i>italic</i> <u>under</u> <s>struck</s> " +
                "<color=#7fd1ff>colour</color> <size=140%>size</size> " +
                "<mark=#ffff0040>mark</mark> <nobr>no break</nobr> " +
                "<link=\"id\">link</link> <cspace=0.2em>spaced</cspace> " +
                "x<sup>2</sup> H<sub>2</sub>O <alpha=#80>faded</alpha> " +
                "<mspace=0.6em>111</mspace> <noparse><b>raw</b></noparse> one<br>two";

            // Every one of these is a tag this package prints as text rather
            // than obeys, and each has to turn up by name in the report.
            var unsupported = Label(parent, "Tags · no counterpart", ref y, 70f);
            unsupported.text =
                "<indent=10%>indented</indent> " +
                "<line-height=80%>tight</line-height> <smallcaps>small caps</smallcaps> " +
                "<rotate=8>tilted</rotate> " +
                "<margin=20>margined</margin> " +
                "<gradient=\"Yellow to Orange\">gradient</gradient> " +
                "<pos=200>positioned</pos>";

            var mixed = Label(parent, "Tags · rich text off", ref y, 44f);
            mixed.text = "<b>this label has richText off, so the tags are the text</b>";
            mixed.richText = false;
        }

        // ---- alignment, overflow, spacing, margins: the mapping decisions

        private static void Geometry(Transform parent, ref float y)
        {
            Heading(parent, "Geometry and mapping", ref y);

            var justified = Label(parent, "Alignment · Justified", ref y, 70f);
            justified.text = "Justified alignment stretches every line but the last, which is a " +
                             "thing this package does not do at all.";
            justified.alignment = TextAlignmentOptions.Justified;

            var flush = Label(parent, "Alignment · Flush", ref y, 70f);
            flush.text = "Flush stretches the last line too, and there is no counterpart for " +
                         "that either.";
            flush.alignment = TextAlignmentOptions.Flush;

            var capline = Label(parent, "Alignment · CaplineRight", ref y, 44f);
            capline.text = "capline, right";
            capline.alignment = TextAlignmentOptions.CaplineRight;

            var ellipsis = Label(parent, "Overflow · Ellipsis", ref y, 44f);
            ellipsis.text = "This line is longer than the box it is in and TMP ends it with an " +
                            "ellipsis rather than clipping it.";
            ellipsis.overflowMode = TextOverflowModes.Ellipsis;
            NoWrap(ellipsis);

            var page = Label(parent, "Overflow · Page", ref y, 44f);
            page.text = "Page mode paginates the text and shows one page at a time. Page two is " +
                        "here somewhere and will not be after the conversion.";
            page.overflowMode = TextOverflowModes.Page;
            page.pageToDisplay = 1;

            var truncate = Label(parent, "Overflow · Truncate", ref y, 44f);
            truncate.text = "Truncate simply stops drawing at the edge of the box.";
            truncate.overflowMode = TextOverflowModes.Truncate;

            var margin = Label(parent, "Margin", ref y, 60f);
            margin.text = "This label carries a margin of 24, 12, 24, 12. OneText has no margin: " +
                          "the rect is the box.";
            margin.margin = new Vector4(24f, 12f, 24f, 12f);

            var spacing = Label(parent, "Spacing", ref y, 70f);
            spacing.text = "Line spacing here is an offset of -12, character spacing 4, word " +
                           "spacing 8, paragraph spacing 20.\nSecond line, so the offset shows.";
            spacing.lineSpacing = -12f;
            spacing.characterSpacing = 4f;
            spacing.wordSpacing = 8f;
            spacing.paragraphSpacing = 20f;

            var styled = Label(parent, "Font style on the component", ref y, 44f);
            styled.text = "bold, italic, underlined and small-capped by the component";
            styled.fontStyle = FontStyles.Bold | FontStyles.Italic | FontStyles.Underline |
                               FontStyles.SmallCaps;

            var auto = Label(parent, "Auto size", ref y, 80f);
            auto.text = "Auto-sizing between 18 and 64, which is what every headline in every " +
                        "project that installed TMP is set to.";
            auto.enableAutoSizing = true;
            auto.fontSizeMin = 18f;
            auto.fontSizeMax = 64f;
        }

        // ---- the look that lives on the material rather than the component

        private static void Effects(Transform parent, Presets materials, ref float y)
        {
            Heading(parent, "Effects, which live on the material", ref y);

            var outline = Label(parent, "Material · outline", ref y, 50f);
            outline.text = "An outline, on the material, which is where 67 of 70 real ones were.";
            outline.fontSize = 34f;
            outline.fontSharedMaterial = materials.Outline;

            var shadow = Label(parent, "Material · shadow", ref y, 50f);
            shadow.text = "An underlay, which everybody calls a shadow.";
            shadow.fontSize = 34f;
            shadow.fontSharedMaterial = materials.Shadow;

            var glow = Label(parent, "Material · glow", ref y, 50f);
            glow.text = "A glow.";
            glow.fontSize = 34f;
            glow.fontSharedMaterial = materials.Glow;

            var everything = Label(parent, "Material · all of it", ref y, 50f);
            everything.text = "Outline, underlay, glow, a dilated face and a face colour that " +
                              "disagrees with the vertex colour.";
            everything.fontSize = 34f;
            everything.fontSharedMaterial = materials.Everything;
        }

        // ---- colour, fonts, and the things that are not really text state

        private static void Colour(Transform parent, TMP_FontAsset orphan, ref float y)
        {
            Heading(parent, "Colour, fonts, visibility", ref y);

            var gradient = Label(parent, "Vertex gradient", ref y, 50f);
            gradient.text = "A four-corner vertex gradient, which is per-quad and not per-label.";
            gradient.fontSize = 32f;
            gradient.enableVertexGradient = true;
            gradient.colorGradient = new VertexGradient(
                new Color(1f, 0.9f, 0.3f), new Color(1f, 0.9f, 0.3f),
                new Color(1f, 0.3f, 0.5f), new Color(1f, 0.3f, 0.5f));

            var faded = Label(parent, "Alpha on the colour", ref y, 44f);
            faded.text = "This one is drawn at 35% alpha by the component's colour.";
            faded.color = new Color(1f, 1f, 1f, 0.35f);

            var typing = Label(parent, "maxVisibleCharacters", ref y, 44f);
            typing.text = "Only the first twenty characters of this are visible, which is how a " +
                          "typewriter effect is usually built.";
            typing.maxVisibleCharacters = 20;

            var quiet = Label(parent, "Raycast target off", ref y, 44f);
            quiet.text = "This label does not take raycasts.";
            quiet.raycastTarget = false;

            var korean = Label(parent, "Not Latin", ref y, 60f);
            korean.text = "한국어 텍스트와 日本語, plus عربى and a few emoji 🙂🎲. The default " +
                          "font has none of these — which is itself the point.";

            if (orphan == null) return;

            var orphaned = Label(parent, "Font with no source file", ref y, 50f);
            orphaned.text = "This label's font asset was built from a font with no file under " +
                            "Assets. The scan cannot make a OneText font out of nothing.";
            orphaned.font = orphan;
        }

        // ---- text inside the layout machinery it usually sits in

        private static void Layouts(Transform parent, ref float y)
        {
            Heading(parent, "Inside the layout machinery", ref y);

            var column = Child(parent, "Vertical layout", new Rect(Left, y, Width, 120f));
            var group = column.AddComponent<VerticalLayoutGroup>();
            group.spacing = 6f;
            group.childControlHeight = true;
            group.childForceExpandHeight = false;
            column.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            for (int i = 0; i < 2; i++)
            {
                var row = Child(column.transform, $"Row {i}", new Rect(0f, 0f, Width, 40f));
                var text = row.AddComponent<TextMeshProUGUI>();
                text.text = $"Row {i} in a vertical layout group with a content size fitter " +
                            "above it, which is where a preferred height comes from.";
                text.fontSize = 24f;
                text.color = Color.white;
                row.AddComponent<LayoutElement>().minHeight = 30f;
            }
            y += 130f;

            var masked = Child(parent, "Masked", new Rect(Left, y, Width, 60f));
            masked.AddComponent<RectMask2D>();
            var inner = Child(masked.transform, "Long", new Rect(0f, 0f, Width * 2f, 60f));
            var wide = inner.AddComponent<TextMeshProUGUI>();
            wide.text = "This label is twice as wide as the RectMask2D above it, so half of it " +
                        "is clipped by the mask rather than by any text setting.";
            wide.fontSize = 24f;
            wide.color = Color.white;
            NoWrap(wide);
            y += 70f;
        }

        // ---- input field and dropdown, with their wiring

        private static void Controls(Transform parent, ref float y)
        {
            Heading(parent, "Controls, with their wiring", ref y);

            var fieldGo = Child(parent, "Input field", new Rect(Left, y, 520f, 56f));
            fieldGo.AddComponent<Image>().color = new Color(0.18f, 0.18f, 0.22f, 1f);

            var textGo = Child(fieldGo.transform, "Text", new Rect(10f, 6f, 500f, 44f));
            var fieldText = textGo.AddComponent<TextMeshProUGUI>();
            fieldText.text = "typed by a player";
            fieldText.fontSize = 26f;
            fieldText.color = Color.white;

            var placeholderGo = Child(fieldGo.transform, "Placeholder",
                new Rect(10f, 6f, 500f, 44f));
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Enter text…";
            placeholder.fontSize = 26f;
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);

            var field = fieldGo.AddComponent<TMP_InputField>();
            field.textComponent = fieldText;
            field.placeholder = placeholder;
            field.targetGraphic = fieldGo.GetComponent<Image>();
            field.text = "typed by a player";
            field.characterLimit = 40;
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
            field.caretWidth = 3;
            field.caretColor = new Color(1f, 0.8f, 0.2f, 1f);
            field.customCaretColor = true;

            // Three listeners on three events, each naming a component that is
            // itself about to be destroyed and replaced. Wired the way a person
            // wires them — in the inspector, where nothing but the migration can
            // move them.
            UnityEventTools.AddPersistentListener(field.onValueChanged, Sink(placeholder));
            UnityEventTools.AddPersistentListener(field.onEndEdit, Sink(fieldText));
            UnityEventTools.AddPersistentListener(field.onSubmit, Sink(placeholder));
            y += 66f;

            var dropdownGo = Child(parent, "Dropdown", new Rect(Left, y, 320f, 48f));
            dropdownGo.AddComponent<Image>().color = new Color(0.18f, 0.18f, 0.22f, 1f);

            var captionGo = Child(dropdownGo.transform, "Label", new Rect(12f, 6f, 260f, 36f));
            var caption = captionGo.AddComponent<TextMeshProUGUI>();
            caption.text = "Choose one";
            caption.fontSize = 24f;
            caption.color = Color.white;

            // The template, minimal but real: a dropdown whose template is null
            // is a dropdown that throws the moment anybody clicks it, and a
            // fixture that cannot be clicked is one nobody trusts afterwards.
            var template = Child(dropdownGo.transform, "Template", new Rect(0f, 48f, 320f, 120f));
            template.SetActive(false);
            template.AddComponent<Image>().color = new Color(0.14f, 0.14f, 0.18f, 1f);
            var viewport = Child(template.transform, "Viewport", new Rect(0f, 0f, 320f, 120f));
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            var content = Child(viewport.transform, "Content", new Rect(0f, 0f, 320f, 40f));
            var item = Child(content.transform, "Item", new Rect(0f, 0f, 320f, 32f));
            item.AddComponent<Toggle>();
            var itemLabelGo = Child(item.transform, "Item Label", new Rect(12f, 2f, 296f, 28f));
            var itemLabel = itemLabelGo.AddComponent<TextMeshProUGUI>();
            itemLabel.text = "Option";
            itemLabel.fontSize = 22f;
            itemLabel.color = Color.white;
            template.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 1f);
            var scroll = template.AddComponent<ScrollRect>();
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content.GetComponent<RectTransform>();

            var dropdown = dropdownGo.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = dropdownGo.GetComponent<Image>();
            dropdown.template = template.GetComponent<RectTransform>();
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;
            dropdown.options = new List<TMP_Dropdown.OptionData>
            {
                new TMP_Dropdown.OptionData("Choose one"),
                new TMP_Dropdown.OptionData("한국어 항목"),
                new TMP_Dropdown.OptionData("<b>a tag in an option</b>"),
                new TMP_Dropdown.OptionData("the last one"),
            };
            y += 58f;

            var buttonGo = Child(parent, "Button", new Rect(Left, y, 240f, 44f));
            var buttonLabelGo = Child(buttonGo.transform, "Button Label",
                new Rect(0f, 0f, 240f, 44f));
            var buttonLabel = buttonLabelGo.AddComponent<TextMeshProUGUI>();
            buttonLabel.text = "targetGraphic is this label";
            buttonLabel.fontSize = 22f;
            buttonLabel.alignment = TextAlignmentOptions.Center;
            buttonLabel.color = Color.white;
            buttonGo.AddComponent<Button>().targetGraphic = buttonLabel;
            y += 54f;
        }

        // ---- the prefab instance, and a field that reaches out of the scene

        private static void Nested(Transform parent, string hostPath, ref float y)
        {
            Heading(parent, "Nested prefabs and references", ref y);

            var host = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(hostPath), parent);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(420f, 60f);
            rect.anchoredPosition = new Vector2(Left, -y);

            // An override on the instance: the same label, said differently in
            // the scene than in the file it comes from. Converting the outer
            // container without the inner one is exactly what this is here to
            // catch.
            var instanceLabel = host.GetComponentInChildren<TextMeshProUGUI>(true);
            instanceLabel.text = "overridden in the scene";
            y += 70f;

            var referrerGo = Child(parent, "Scene referrer", new Rect(Left, y, Width, 44f));
            var note = referrerGo.AddComponent<TextMeshProUGUI>();
            note.text = "A script on this object names the label above, in the prefab, from the " +
                        "scene.";
            note.fontSize = 22f;
            note.color = new Color(0.55f, 0.85f, 1f, 1f);

            PointAt(referrerGo.AddComponent(Find(ReferrerType)), instanceLabel);
            y += 54f;
        }

        /// <summary>World-space text, which is the same swap through a different door.</summary>
        private static void World()
        {
            var plain = new GameObject("World Text").AddComponent<TextMeshPro>();
            plain.transform.position = new Vector3(-4f, 2f, 0f);
            plain.text = "world space, plain";
            plain.fontSize = 6f;

            var tilted = new GameObject("World Text Tilted").AddComponent<TextMeshPro>();
            tilted.transform.position = new Vector3(-4f, 0f, 0f);
            tilted.transform.rotation = Quaternion.Euler(0f, 25f, -8f);
            tilted.text = "world space, turned away from the camera";
            tilted.fontSize = 5f;
            tilted.alignment = TextAlignmentOptions.Midline;
        }

        // ------------------------------------------------------------ plumbing

        private static void Heading(Transform parent, string title, ref float y)
        {
            var go = Child(parent, "— " + title, new Rect(Left, y, Width, 34f));
            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = title.ToUpperInvariant();
            text.fontSize = 18f;
            text.characterSpacing = 6f;
            text.color = new Color(0.55f, 0.85f, 1f, 1f);
            y += 44f;
        }

        /// <summary>One label, named, placed, and stepped past.</summary>
        private static TextMeshProUGUI Label(Transform parent, string name, ref float y,
            float height)
        {
            var go = Child(parent, name, new Rect(Left, y, Width, height));
            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = 24f;
            text.color = Color.white;
            y += height + 10f;
            return text;
        }

        /// <summary>
        /// Wrapping off, through whichever door this TMP has.
        ///
        /// <c>enableWordWrapping</c> is obsolete in the TextMesh Pro inside
        /// Unity 6 and is the only spelling that exists in the 3.0.7 that 2022.3
        /// resolves, so the choice is made by the same define the provider next
        /// door uses rather than by picking one and warning on half the editors
        /// this package supports.
        /// </summary>
        private static void NoWrap(TMP_Text text)
        {
#if ONETEXT_TMP_WRAPMODE
            text.textWrappingMode = TextWrappingModes.NoWrap;
#else
            text.enableWordWrapping = false;
#endif
        }

        private static GameObject Child(Transform parent, string name, Rect rect)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var transform = go.GetComponent<RectTransform>();
            transform.anchorMin = new Vector2(0f, 1f);
            transform.anchorMax = new Vector2(0f, 1f);
            transform.pivot = new Vector2(0f, 1f);
            transform.sizeDelta = new Vector2(rect.width, rect.height);
            transform.anchoredPosition = new Vector2(rect.x, -rect.y);
            return go;
        }

        /// <summary>
        /// A <c>UnityAction&lt;string&gt;</c> pointed at the label's own
        /// one-string method, asked for by signature rather than written as a
        /// method group — the same reason the proof generator does it this way.
        /// TMP 3.0.7, which 2022.3 resolves, has only the two-argument
        /// <c>SetText</c>, and a method group whose only candidate takes an
        /// optional parameter does not convert.
        /// </summary>
        private static UnityAction<string> Sink(Component target)
        {
            MethodInfo method = target.GetType().GetMethod("SetText", new[] { typeof(string) })
                                ?? target.GetType().GetProperty("text").GetSetMethod();
            return (UnityAction<string>)Delegate.CreateDelegate(
                typeof(UnityAction<string>), target, method);
        }
    }
}
