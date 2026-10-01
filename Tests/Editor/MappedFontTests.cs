using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OneText.Tests
{
    /// <summary>
    /// Fonts read by mapping their file: the face is the same face, the
    /// managed heap holds none of it, and a collection gives up the one face
    /// asked for.
    /// </summary>
    public class MappedFontTests
    {
        internal const string LatinFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSans.ttf";
        internal const string ArabicFontPath = "Packages/com.onetext.core/Tests/Fonts~/NotoSansArabic.ttf";

        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "onetext-mapped-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_directory, true); }
            catch (Exception) { /* a mapping a failed test left open; the OS cleans temp */ }
        }

        [Test]
        public void A_Mapped_Font_Is_The_Same_Face_And_Holds_No_Managed_Bytes()
        {
            string path = Path.GetFullPath(LatinFontPath);
            using var mapped = FontData.LoadFile(path);
            using var read = FontData.Load(File.ReadAllBytes(path));

            Assert.IsTrue(mapped.IsValid);
            Assert.IsTrue(mapped.IsMapped, "the file was read rather than mapped on a platform that can map");
            Assert.AreEqual(0, mapped.ManagedBytes, "a mapped face pins no managed array");
            Assert.AreEqual(new FileInfo(path).Length, mapped.MappedBytes);
            Assert.AreEqual(path, mapped.SourcePath);
            Assert.AreEqual(new FileInfo(path).Length, read.ManagedBytes);

            Assert.AreEqual(read.UnitsPerEm, mapped.UnitsPerEm);
            Assert.AreEqual(read.Ascender, mapped.Ascender);
            Assert.AreEqual(read.Descender, mapped.Descender);
            foreach (char c in "Hello, Ωμέγα — Привет 123")
            {
                Assert.AreEqual(read.NominalGlyph(c), mapped.NominalGlyph(c), $"glyph for '{c}'");
                uint glyph = read.NominalGlyph(c);
                bool a = read.TryGetInkBounds(glyph, out var minA, out var maxA);
                bool b = mapped.TryGetInkBounds(glyph, out var minB, out var maxB);
                Assert.AreEqual(a, b);
                Assert.AreEqual(minA, minB);
                Assert.AreEqual(maxA, maxB);
            }
        }

        [Test]
        public void A_Collection_Gives_The_Face_Asked_For_And_Only_That_One()
        {
            string collection = Path.Combine(_directory, "pair.ttc");
            File.WriteAllBytes(collection, Collection(
                File.ReadAllBytes(Path.GetFullPath(LatinFontPath)),
                File.ReadAllBytes(Path.GetFullPath(ArabicFontPath))));

            using var latin = FontData.LoadFile(collection, 0);
            using var arabic = FontData.LoadFile(collection, 1);

            Assert.IsTrue(latin.IsMapped && arabic.IsMapped);
            Assert.IsTrue(latin.HasGlyph('A'));
            Assert.IsFalse(latin.HasGlyph(0x0628), "face 0 is the Latin one");
            Assert.IsTrue(arabic.HasGlyph(0x0628), "face 1 is the Arabic one");
            Assert.AreEqual(0, latin.ManagedBytes + arabic.ManagedBytes);
        }

        [Test]
        public void A_Missing_File_Throws_Like_Reading_It_Would()
        {
            Assert.Throws<FileNotFoundException>(() => FontData.LoadFile(Path.Combine(_directory, "none.ttf")));
        }

        [Test]
        public void Disposing_Unmaps_So_The_File_Can_Be_Deleted()
        {
            // Windows refuses to delete a mapped file; this is the test that
            // would say so if the view outlived the face.
            string copy = Path.Combine(_directory, "copy.ttf");
            File.Copy(Path.GetFullPath(ArabicFontPath), copy);
            var font = FontData.LoadFile(copy);
            Assert.IsTrue(font.HasGlyph(0x0628));
            font.Dispose();
            File.Delete(copy);
            Assert.IsFalse(File.Exists(copy));
        }

        [Test]
        public void A_Large_System_Collection_Is_Not_Read_Whole()
        {
            // The claim this whole path exists for, on a real 20+ MB
            // collection where the machine has one: loading one face and
            // drawing a few characters brings in a small part of the file.
            string path = LargestCollection();
            if (path == null) Assert.Ignore("no font collection of 10 MB or more on this machine");

            using var font = FontData.LoadFile(path, 0);
            Assert.IsTrue(font.IsMapped);
            Assert.AreEqual(0, font.ManagedBytes);
            long resident = font.ResidentFileBytes;
            TestContext.WriteLine($"{Path.GetFileName(path)}: {font.MappedBytes / 1048576.0:0.0} MB mapped, " +
                                  $"{(resident < 0 ? "?" : (resident / 1048576.0).ToString("0.0"))} MB in memory");
            if (resident < 0) Assert.Ignore("this platform does not report resident pages");
            // Page-cache residency is shared with every other process reading
            // the file, so this is an upper bound on what the load brought in,
            // and a loose one is all that can be asserted.
            Assert.Less(resident, font.MappedBytes, "every page of the collection is in memory");
        }

        private static string LargestCollection()
        {
            string best = null;
            long size = 10L * 1024 * 1024;
            foreach (string directory in SystemFonts.Directories())
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.GetFiles(directory, "*.ttc"))
                {
                    long length = new FileInfo(file).Length;
                    // Not the emoji one: it is the font the system itself keeps hottest.
                    if (length > size && !file.Contains("Emoji")) { best = file; size = length; }
                }
            }
            return best;
        }

        /// <summary>
        /// A TrueType collection of whole sfnt files: the <c>ttcf</c> header,
        /// then each font as it was, its table offsets moved by where it now
        /// starts. Enough of the format for HarfBuzz and the cmap reader.
        /// </summary>
        internal static byte[] Collection(params byte[][] fonts)
        {
            int header = 12 + 4 * fonts.Length;
            var starts = new int[fonts.Length];
            int at = header;
            for (int i = 0; i < fonts.Length; i++)
            {
                at = (at + 3) & ~3;
                starts[i] = at;
                at += fonts[i].Length;
            }

            var output = new byte[at];
            Write32(output, 0, 0x74746366); // 'ttcf'
            Write32(output, 4, 0x00010000);
            Write32(output, 8, (uint)fonts.Length);
            for (int i = 0; i < fonts.Length; i++)
            {
                Write32(output, 12 + 4 * i, (uint)starts[i]);
                Buffer.BlockCopy(fonts[i], 0, output, starts[i], fonts[i].Length);
                int tables = output[starts[i] + 4] << 8 | output[starts[i] + 5];
                for (int t = 0; t < tables; t++)
                {
                    int record = starts[i] + 12 + 16 * t;
                    Write32(output, record + 8, Read32(output, record + 8) + (uint)starts[i]);
                }
            }
            return output;
        }

        private static uint Read32(byte[] b, int at) =>
            (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static void Write32(byte[] b, int at, uint value)
        {
            b[at] = (byte)(value >> 24);
            b[at + 1] = (byte)(value >> 16);
            b[at + 2] = (byte)(value >> 8);
            b[at + 3] = (byte)value;
        }
    }
}
