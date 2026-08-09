using System.Collections.Generic;
using NUnit.Framework;
using OneText.Editor;

namespace OneText.Tests
{
    /// <summary>
    /// The reader that finds the references a container's file still spells out
    /// after its loaded objects have stopped holding them.
    ///
    /// Written against text rather than against prefabs on purpose. Every fault
    /// this reader has had was a shape question — where a property path starts,
    /// when a key closes the one above it, where a number begins — and from
    /// outside, through a migration, all of them look identical: nothing found,
    /// no reason given. Here each one has its own failing line.
    ///
    /// The snippets are Unity's own output, trimmed. Where a case came from a
    /// real project the comment says which, because a shape nobody has actually
    /// seen is a shape this reader does not owe anything to.
    /// </summary>
    public sealed class ContainerFileTests
    {
        private const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        /// <summary>The stub every case points at: a Text in another prefab.</summary>
        private const string Stub =
            "--- !u!114 &5731855310793858048 stripped\n" +
            "MonoBehaviour:\n" +
            "  m_CorrespondingSourceObject: {fileID: 6153541487851920161, guid: 32bcbf47e4c3a4cb0a8ad4a6b7d8cb30, type: 3}\n" +
            "  m_PrefabInstance: {fileID: 1940595588911793953}\n" +
            "  m_GameObject: {fileID: 0}\n" +
            "  m_Script: {fileID: 11500000, guid: 5f7201a12d95ffc409449d95f23cf332, type: 3}\n" +
            "  m_Name: \n" +
            "  m_EditorClassIdentifier: \n";

        private static List<ContainerFile.StoredReference> Parse(string body) =>
            ContainerFile.Parse(Header + body + Stub);

        private static ContainerFile.StoredReference Single(string body)
        {
            var found = Parse(body);
            Assert.AreEqual(1, found.Count,
                "expected exactly one reference out of this document, got " +
                string.Join(", ", found.ConvertAll(r => r.PropertyPath)));
            return found[0];
        }

        [Test]
        public void APlainFieldIsFoundUnderItsOwnName()
        {
            var reference = Single(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  m_Script: {fileID: 11500000, guid: 9ba08140c09614b1e906c4899378d584, type: 3}\n" +
                "  Typed: {fileID: 5731855310793858048}\n");

            Assert.AreEqual("Typed", reference.PropertyPath,
                "the document's type name leaked into the property path");
            Assert.AreEqual("32bcbf47e4c3a4cb0a8ad4a6b7d8cb30", reference.TargetGuid);
            Assert.AreEqual(6153541487851920161L, reference.TargetFileId,
                "the file id was not parsed: there is a space after the colon");
            Assert.AreEqual("9ba08140c09614b1e906c4899378d584", reference.ReferrerScript);
        }

        [Test]
        public void AnEmptyStringFieldDoesNotSwallowWhatFollowsIt()
        {
            // Unity writes an empty string as a key with nothing after it, which
            // at that line is indistinguishable from a map about to be indented.
            // Every component in every prefab has two of them.
            var reference = Single(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  m_Name: \n" +
                "  m_EditorClassIdentifier: \n" +
                "  Typed: {fileID: 5731855310793858048}\n");

            Assert.AreEqual("Typed", reference.PropertyPath);
        }

        [Test]
        public void AFieldInsideAListOfASerializableClassIsReachable()
        {
            // Measured on Modern UI Pack's Pie Chart: three labels, each held by
            // an entry of one serialized list, and no other way to name them.
            var found = Parse(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  m_Script: {fileID: 11500000, guid: 5e4a4384d3c229846b0d84dc8ec948dd, type: 3}\n" +
                "  chartData:\n" +
                "  - name: Blue Item\n" +
                "    value: 25\n" +
                "    indicatorText: {fileID: 5731855310793858048}\n" +
                "  - name: Orange Item\n" +
                "    value: 50\n" +
                "    indicatorText: {fileID: 5731855310793858048}\n" +
                "  borderThickness: 0\n");

            Assert.AreEqual(2, found.Count, "one entry of the list was lost");
            Assert.AreEqual("chartData.Array.data[0].indicatorText", found[0].PropertyPath);
            Assert.AreEqual("chartData.Array.data[1].indicatorText", found[1].PropertyPath,
                "the second entry was nested inside the first instead of beside it");
        }

        [Test]
        public void AKeyAfterAListIsNotStillInsideIt()
        {
            var reference = Single(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  chartData:\n" +
                "  - name: Blue Item\n" +
                "    value: 25\n" +
                "  after: {fileID: 5731855310793858048}\n");

            Assert.AreEqual("after", reference.PropertyPath);
        }

        [Test]
        public void AFieldInsideANestedMapCarriesTheWholePath()
        {
            var reference = Single(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  settings:\n" +
                "    caption: {fileID: 5731855310793858048}\n");

            Assert.AreEqual("settings.caption", reference.PropertyPath);
        }

        [Test]
        public void AReferenceToAnAssetIsLeftAlone()
        {
            // One carrying a guid names an asset directly. Assets are not
            // renumbered by a conversion, so it was never at risk and reporting
            // it would be noise.
            var found = Parse(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  m_Sprite: {fileID: 21300000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}\n");

            Assert.AreEqual(0, found.Count);
        }

        [Test]
        public void AReferenceInsideThisFileIsLeftAlone()
        {
            // 999 is not a stub, so it names something in this same file. The
            // ordinary census reads those, and they do not cross a boundary.
            var found = Parse(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  neighbour: {fileID: 999}\n");

            Assert.AreEqual(0, found.Count);
        }

        [Test]
        public void AnEmptyFieldIsNotAReference()
        {
            var found = Parse(
                "--- !u!114 &7177242435668431763\n" +
                "MonoBehaviour:\n" +
                "  Typed: {fileID: 0}\n");

            Assert.AreEqual(0, found.Count,
                "a field the user deliberately left empty was read as a broken reference");
        }

        [Test]
        public void ABinaryProjectSaysNothingRatherThanGuessing()
        {
            Assert.AreEqual(0, ContainerFile.Parse("not yaml at all").Count);
            Assert.AreEqual(0, ContainerFile.Parse(string.Empty).Count);
            Assert.AreEqual(0, ContainerFile.Parse(null).Count);
        }
    }
}
