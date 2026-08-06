using System;
using System.Collections;
using System.Collections.Generic;
using OneText;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tier-3 build smoke test: the checks that only a real player build on a real
/// device can answer.
///
/// Editor tests run against the mono runtime, the editor's copy of the native
/// plugins, and shaders that were never stripped. A player build changes all
/// three at once, and each change has its own way of failing silently:
/// IL2CPP strips the managed code that only reflection reaches, the platform
/// packager decides which native binaries make it into the app, and the shader
/// compiler drops variants no scene appears to reference. This component runs
/// once at startup, exercises those three seams, and prints a single line the
/// runner scripts grep for.
///
/// The scene that holds it is created procedurally at build time by
/// SmokeBuild, so the harness project's own scenes stay untouched. Nothing here
/// runs in the editor unless that scene is opened.
/// </summary>
public sealed class SmokeSelfTest : MonoBehaviour
{
    /// <summary>The one line the runner scripts look for. Keep it in sync with Tools/run_*_smoke.sh.</summary>
    public const string Marker = "ONETEXT-SMOKE:";

    private const string FontRoot = "SmokeFonts/";

    /// <summary>How bright a pixel has to be before it counts as "something was drawn".</summary>
    private const float InkThreshold = 0.25f;

    /// <summary>Below this many bright pixels the render is treated as blank.</summary>
    private const int MinInkPixels = 64;

    /// <summary>
    /// How long the finished scene stays on screen before the app quits, so
    /// the runner scripts have a window in which to capture it.
    /// </summary>
    private const float HoldSeconds = 25f;

    private readonly List<string> _failures = new List<string>();
    private int _passed;

    private GameObject _camGo;
    private GameObject _canvasGo;
    private OneTextLabel _label;

    private void Start()
    {
        Debug.Log("ONETEXT-SMOKE-BEGIN platform=" + Application.platform +
                  " unity=" + Application.unityVersion);
        StartCoroutine(RunAll());
    }

    private IEnumerator RunAll()
    {
        // One frame so the graphics device and the canvas system are live before
        // anything asks them for a picture.
        yield return null;

        Run("native-alive", CheckNativeAlive);
        Run("script-arabic", () => CheckShapes("Arabic", "NotoSansArabic", "السلام عليكم"));
        Run("script-devanagari", () => CheckShapes("Devanagari", "NotoSansDevanagari", "नमस्ते दुनिया"));
        Run("script-thai", () => CheckShapes("Thai", "NotoSansThai", "สวัสดีชาวโลก"));
        Run("script-korean", () => CheckShapes("Korean", "NotoSansKorean", "안녕하세요"));
        Run("script-emoji", () => CheckShapes("Emoji", "NotoColorEmoji",
            "\U0001F44B\U0001F469‍\U0001F4BB\U0001F1F0\U0001F1F7"));
        Run("sdf-shader-present", CheckShaderPresent);

        // The render check has to straddle a frame boundary, so it cannot live
        // inside Run's try/catch: C# forbids yielding inside a try that has a
        // catch clause. It carries its own guards instead.
        yield return RenderCheck();

        Report();
        ShowVerdictOnScreen();

        // Stay up long enough for the runner to photograph the result. The app
        // still quits on its own, so a run nobody is watching does not hang.
        float hold = HoldSeconds;
        Debug.Log("ONETEXT-SMOKE-INFO holding " + hold + "s for screenshot");
        yield return new WaitForSecondsRealtime(hold);

        Application.Quit(_failures.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// Puts the verdict into the picture as well as the log, so the screenshot
    /// is self-describing rather than just a wall of text that someone has to
    /// take on trust.
    /// </summary>
    private void ShowVerdictOnScreen()
    {
        if (_canvasGo == null) return;
        try
        {
            var go = new GameObject("Verdict", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_canvasGo.transform, false);
            var verdict = go.AddComponent<OneTextLabel>();
            var rt = verdict.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0.16f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            verdict.SetFont(FontBytes("NotoSans"));
            verdict.Text = _failures.Count == 0
                ? "PASS - " + _passed + " checks"
                : "FAIL - " + _failures[0];
            verdict.FontSize = 44f;
            verdict.color = _failures.Count == 0 ? Color.green : Color.red;
        }
        catch (Exception e)
        {
            Debug.Log("ONETEXT-SMOKE-INFO could not draw the verdict overlay: " + e.Message);
        }
    }

    private void Run(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Debug.Log("ONETEXT-SMOKE-CHECK ok " + name);
        }
        catch (Exception e)
        {
            Fail(name, e.GetType().Name + ": " + e.Message);
        }
    }

    private void Fail(string name, string detail)
    {
        _failures.Add(name + " -> " + detail);
        Debug.LogError("ONETEXT-SMOKE-CHECK FAIL " + name + " -> " + detail);
    }

    private void Report()
    {
        if (_failures.Count == 0)
            Debug.Log(Marker + " PASS (" + _passed + " checks)");
        else
            Debug.LogError(Marker + " FAIL - " + _failures[0]);
    }

    // ---------------------------------------------------------------- checks

    /// <summary>
    /// The native half is alive at all: HarfBuzz answers with a version, and a
    /// plain Latin string comes back as glyphs. A missing or mispackaged
    /// libHarfBuzzSharp throws DllNotFoundException here, which is the single
    /// most likely way a player build differs from the editor.
    /// </summary>
    private void CheckNativeAlive()
    {
        Debug.Log("ONETEXT-SMOKE-INFO harfbuzz=" + Shaper.HarfBuzzVersion);

        using (var font = LoadFont("NotoSans"))
        using (var shaper = new Shaper())
        {
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, "Hello OneText 0123", glyphs);
            if (glyphs.Count == 0)
                throw new Exception("Latin shaping produced no glyphs.");
            Debug.Log("ONETEXT-SMOKE-INFO latin glyphs=" + glyphs.Count);
        }
    }

    /// <summary>
    /// One complex script, shaped with the face that covers it. Anything past
    /// Latin needs the OpenType tables HarfBuzz reads at runtime, so a build
    /// that shipped a stub or a stripped-down native library passes the Latin
    /// check and fails here.
    /// </summary>
    private void CheckShapes(string label, string fontName, string text)
    {
        using (var font = LoadFont(fontName))
        using (var shaper = new Shaper())
        {
            var glyphs = new List<ShapedGlyph>();
            shaper.Shape(font, text, glyphs);
            if (glyphs.Count == 0)
                throw new Exception(label + " shaping produced no glyphs.");

            int notdef = 0;
            foreach (var g in glyphs)
                if (g.GlyphId == 0) notdef++;
            if (notdef == glyphs.Count)
                throw new Exception(label + " shaped to " + glyphs.Count + " glyphs, all .notdef.");

            Debug.Log("ONETEXT-SMOKE-INFO " + label + " glyphs=" + glyphs.Count + " notdef=" + notdef);
        }
    }

    /// <summary>
    /// The SDF shader survived the build. It reaches the player through
    /// Resources.Load, which the shader stripper does not always follow, and a
    /// null here is the difference between text and nothing at all.
    /// </summary>
    private void CheckShaderPresent()
    {
        var shader = SharedGlyphAtlas.LoadShader();
        if (shader == null)
            throw new Exception("SharedGlyphAtlas.LoadShader() returned null - the SDF shader was stripped from the build.");
        if (!shader.isSupported)
            throw new Exception("SDF shader '" + shader.name + "' is not supported on this device/graphics API.");
        Debug.Log("ONETEXT-SMOKE-INFO shader=" + shader.name + " gfx=" + SystemInfo.graphicsDeviceType);
    }

    /// <summary>
    /// End to end: a real canvas, a real label, a real frame, read back from
    /// the backbuffer. This is the only check that exercises the rasterizer,
    /// the atlas upload and the mobile shader together, and the only one that
    /// notices when text lays out perfectly and draws nothing.
    /// </summary>
    private IEnumerator RenderCheck()
    {
        const string Name = "live-render";

        try
        {
            _camGo = new GameObject("SmokeCamera");
            var cam = _camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.orthographic = true;

            _canvasGo = new GameObject("SmokeCanvas", typeof(RectTransform));
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvasGo.AddComponent<CanvasScaler>();

            // A black backdrop under the text, so "blank" means blank rather
            // than "whatever the driver left in the buffer".
            var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer))
                .AddComponent<Image>();
            backdrop.transform.SetParent(_canvasGo.transform, false);
            backdrop.color = Color.black;
            Stretch(backdrop.rectTransform);

            var labelGo = new GameObject("SmokeLabel", typeof(RectTransform), typeof(CanvasRenderer));
            labelGo.transform.SetParent(_canvasGo.transform, false);
            var label = labelGo.AddComponent<OneTextLabel>();
            _label = label;
            Stretch(label.rectTransform);

            // Every script the shaping checks covered, in one label, resolved
            // through the fallback chain. Shaping them in isolation proves the
            // native library works; drawing them together is what proves the
            // rasterizer, the atlas and the mobile shader do too.
            label.SetFont(
                FontBytes("NotoSans"),
                FontBytes("NotoSansArabic"),
                FontBytes("NotoSansDevanagari"),
                FontBytes("NotoSansThai"),
                FontBytes("NotoSansKorean"),
                FontBytes("NotoColorEmoji"));
            label.Text =
                "OneText smoke 0123\n" +
                "السلام عليكم\n" +
                "नमस्ते दुनिया\n" +
                "สวัสดีชาวโลก\n" +
                "안녕하세요\n" +
                "\U0001F44B\U0001F30D";
            label.FontSize = 56f;
            label.color = Color.white;

            Canvas.ForceUpdateCanvases();
        }
        catch (Exception e)
        {
            Fail(Name, "scene setup: " + e.GetType().Name + ": " + e.Message);
            yield break;
        }

        // Two frames: the first builds the mesh and uploads the atlas, the
        // second is the one guaranteed to have the glyphs in it.
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();

        Texture2D shot = null;
        RenderTexture target = null;
        try
        {
            // Not read off the screen: on iOS, Metal's drawable is write-only
            // and ReadPixels from it comes back one flat colour while the same
            // glyphs are plainly visible on the device. A RenderTexture is
            // readable everywhere, so point a camera at this same canvas and
            // read the texture instead, the way the editor proof generators do.
            const int w = 1024, h = 1024;
            var cam = _camGo.GetComponent<Camera>();
            var canvas = _canvasGo.GetComponent<Canvas>();
            target = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            Shader.SetGlobalFloat("unity_GUIZTestMode",
                (float)UnityEngine.Rendering.CompareFunction.Always);
            cam.targetTexture = target;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            Canvas.ForceUpdateCanvases();
            cam.Render();

            RenderTexture.active = target;
            shot = new Texture2D(w, h, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
            shot.Apply(false);
            RenderTexture.active = null;

            // Back to the screen, so the runner's device screenshot still has
            // something to photograph.
            cam.targetTexture = null;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var pixels = shot.GetPixels32();
            int ink = 0;
            var first = pixels.Length > 0 ? pixels[0] : default;
            bool uniform = true;
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (uniform && (p.r != first.r || p.g != first.g || p.b != first.b)) uniform = false;
                float luma = (0.299f * p.r + 0.587f * p.g + 0.114f * p.b) / 255f;
                if (luma >= InkThreshold) ink++;
            }

            // Lit pixels alone are not evidence that OneText drew anything: when
            // the native library is missing, Unity paints its own error console
            // over the screen in red and the frame comes back far from blank.
            // That is a false pass this check has already produced once, so ask
            // the label what it actually emitted as well as asking the screen.
            int quads = _label != null && _label.DrawnQuads != null ? _label.DrawnQuads.Count : 0;

            Debug.Log("ONETEXT-SMOKE-INFO readback=" + w + "x" + h +
                      " ink=" + ink + " uniform=" + uniform + " drawnQuads=" + quads);

            if (quads == 0)
                throw new Exception("The label produced no drawn quads - OneText laid out nothing, whatever else is on screen.");
            if (uniform)
                throw new Exception("Frame is a single flat colour (" + first.r + "," + first.g + "," + first.b + ") - nothing rendered.");
            if (ink < MinInkPixels)
                throw new Exception("Only " + ink + " lit pixels in " + w + "x" + h + " (need " + MinInkPixels + ") - the label drew nothing.");

            _passed++;
            Debug.Log("ONETEXT-SMOKE-CHECK ok " + Name);
        }
        catch (Exception e)
        {
            Fail(Name, e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            if (shot != null) Destroy(shot);
            if (target != null)
            {
                if (RenderTexture.active == target) RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(target);
            }
        }
        // The canvas deliberately stays up: the runner scripts grab a device
        // screenshot of it once they see the verdict line.
    }

    // ---------------------------------------------------------------- helpers

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>
    /// Font bytes out of Resources. The .ttf files are copied in as .bytes so
    /// Unity keeps them whole instead of importing them as font assets, which
    /// is also what makes them reachable from a player build.
    /// </summary>
    private static byte[] FontBytes(string name)
    {
        var asset = Resources.Load<TextAsset>(FontRoot + name);
        if (asset == null)
            throw new Exception("Resources/" + FontRoot + name + ".bytes is missing from the build.");
        var bytes = asset.bytes;
        if (bytes == null || bytes.Length == 0)
            throw new Exception("Resources/" + FontRoot + name + ".bytes is empty.");
        return bytes;
    }

    private static OneText.FontData LoadFont(string name) =>
        OneText.FontData.Load(FontBytes(name));
}
