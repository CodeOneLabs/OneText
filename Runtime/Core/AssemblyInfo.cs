using System.Runtime.CompilerServices;

// The test assembly, so that a branch a #if keeps out of the editor can still
// be taken deliberately by a test. See OneFontAsset.DropPackedData.
[assembly: InternalsVisibleTo("OneText.Tests")]
