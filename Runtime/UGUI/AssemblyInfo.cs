using System.Runtime.CompilerServices;

// The test assembly, so a test can ask a label which faces it actually
// resolved. See VariableSweepTests: whether a slider drag reparses the font
// file or moves the axes of the face already loaded is invisible from outside
// — same glyphs, same tiles, same pixels — and is the whole difference
// between a drag that costs nothing and one that reparses six megabytes a
// frame.
[assembly: InternalsVisibleTo("OneText.Tests")]
