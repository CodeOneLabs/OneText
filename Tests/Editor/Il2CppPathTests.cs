using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OneText.Tests
{
    /// <summary>
    /// Changes made because IL2CPP on a phone charged for things Mono in the editor does not:
    /// generic span scans that ask a reflection question per call, the array overloads of the
    /// mesh upload (same), and a reflection read of a uGUI field. Each is held to the code it
    /// replaced, both ways, in one process.
    /// </summary>
    public class Il2CppPathTests
    {
        private const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";

        [TearDown]
        public void TearDown()
        {
            SpanScan.ScalarLoops = true;
            OneTextLabel.NativeUpload = true;
            OneTextLabel.DirectCallbackRead = true;
        }

        [Test]
        public void Span_Scans_Answer_What_MemoryExtensions_Answers()
        {
            var random = new System.Random(5);
            const string alphabet = "ab<\\c한字 ";
            for (int n = 0; n < 4000; n++)
            {
                int length = random.Next(0, 24);
                var a = new char[length];
                for (int i = 0; i < length; i++) a[i] = alphabet[random.Next(alphabet.Length)];
                var b = (char[])a.Clone();
                if (length > 0 && random.Next(3) == 0) b[random.Next(length)] = 'z';
                if (random.Next(5) == 0) Array.Resize(ref b, Math.Max(0, length - 1));

                foreach (char c in new[] { '<', '\\', 'z' })
                {
                    SpanScan.ScalarLoops = false;
                    int want = SpanScan.IndexOf(a, c);
                    SpanScan.ScalarLoops = true;
                    Assert.AreEqual(want, SpanScan.IndexOf(a, c));
                }
                SpanScan.ScalarLoops = false;
                bool wantEqual = SpanScan.Equal(a, b);
                SpanScan.ScalarLoops = true;
                Assert.AreEqual(wantEqual, SpanScan.Equal(a, b));
            }
        }

        private static OneTextLabel MakeLabel(GameObject canvas)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(canvas.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 120f);
            var label = go.AddComponent<OneTextLabel>();
            label.SetFont(File.ReadAllBytes(Path.GetFullPath(LatinFontPath)));
            label.FontSize = 24f;
            return label;
        }

        private static void Rebuild(OneTextLabel label)
        {
            label.SetVerticesDirty();
            label.Rebuild(CanvasUpdate.PreRender);
        }

        private static (Vector3[] v, Color32[] c, int[] t, List<Vector4>[] uv) Capture(Mesh mesh)
        {
            var uvs = new List<Vector4>[4];
            for (int channel = 0; channel < 4; channel++)
            {
                uvs[channel] = new List<Vector4>();
                mesh.GetUVs(channel, uvs[channel]);
            }
            return (mesh.vertices, mesh.colors32, mesh.triangles, uvs);
        }

        [TestCase("7")]
        [TestCase("Score 12345")]
        [TestCase("two\nlines of it")]
        [TestCase("<color=#ff0000>red</color> and <b>bold</b>")]
        public void Native_Upload_Hands_The_Mesh_The_Same_Bytes(string text)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var label = MakeLabel(canvas);
                label.Text = text;

                OneTextLabel.NativeUpload = false;
                Rebuild(label);
                var expected = Capture(OneTextLabel.LastDirectMesh);
                Assert.Greater(expected.v.Length, 0, "nothing was drawn, so nothing was compared");

                // A different quad count first, so the index buffer is re-declared on the native route too.
                label.Text = text + "x";
                Rebuild(label);
                label.Text = text;
                OneTextLabel.NativeUpload = true;
                Rebuild(label);
                var got = Capture(OneTextLabel.LastDirectMesh);

                CollectionAssert.AreEqual(expected.v, got.v, "positions");
                CollectionAssert.AreEqual(expected.c, got.c, "colours");
                CollectionAssert.AreEqual(expected.t, got.t, "triangles");
                for (int channel = 0; channel < 4; channel++)
                    CollectionAssert.AreEqual(expected.uv[channel], got.uv[channel], "TEXCOORD" + channel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }

        [Test]
        public void Direct_Callback_Read_Still_Hears_A_Registered_Callback()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var label = MakeLabel(canvas);
                label.Text = "first";
                Rebuild(label);
                int heard = 0;
                UnityAction callback = () => heard++;
                label.RegisterDirtyLayoutCallback(callback);
                foreach (bool direct in new[] { false, true })
                {
                    OneTextLabel.DirectCallbackRead = direct;
                    int before = heard;
                    label.Text = direct ? "second" : "third";
                    Assert.Greater(heard, before, $"direct={direct}: a registered dirty-layout callback was skipped");
                }
                label.UnregisterDirtyLayoutCallback(callback);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }
    }
}
