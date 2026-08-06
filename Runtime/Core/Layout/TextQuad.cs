using UnityEngine;

namespace OneText
{
    /// <summary>
    /// One drawn tile, after layout and atlas lookup but before it reaches a
    /// mesh: where it goes, what it samples, what colour it is, and which
    /// grapheme clusters of the source text it belongs to.
    ///
    /// This is the seam the engine exposes to anything that wants to move text
    /// without re-laying it out. Animation, reveal and per-character effects
    /// all want the same thing (the finished geometry, addressable by unit of
    /// text), and every animation layer built on TMP had to reconstruct it from
    /// the outside, which stopped working the moment a ligature appeared.
    /// </summary>
    public struct TextQuad
    {
        /// <summary>Bottom-left corner in the frontend's local space.</summary>
        public Vector2 Position;

        /// <summary>Width and height in local units.</summary>
        public Vector2 Size;

        /// <summary>
        /// Rotation about the tile's centre, in degrees. The emitter rotates
        /// the four corners; it is not a shear and not a texture rotation.
        /// </summary>
        public float Rotation;

        /// <summary>Atlas rect and layer this tile samples.</summary>
        public Rect UvRect;
        public int Layer;

        public Color32 Color;

        /// <summary>
        /// Grapheme clusters this tile covers, inclusive. A tile is usually one
        /// cluster; connected scripts bake several into one seam-free field, and
        /// then it is all of them.
        /// </summary>
        public int FirstGrapheme, LastGrapheme;

        /// <summary>Index of the run this tile came from.</summary>
        public int RunIndex;

        /// <summary>Baseline this tile sits on, in the same space as <see cref="Position"/>.</summary>
        public float Baseline;

        /// <summary>The style in force where this tile came from.</summary>
        public TextStyle Style;

        /// <summary>
        /// Index into the frontend's decoration table; see
        /// <c>OneTextLabel.DecorationOf</c>.
        ///
        /// An index rather than the nine numbers themselves, because a label has
        /// at most a handful of distinct decorations and every one of its
        /// thousands of tiles would otherwise carry a copy through every
        /// modifier and every frame. Slot 0 is always
        /// <see cref="TextDecoration.None"/>, so a zero-initialized quad is
        /// undecorated; a -1 sentinel would need every construction site to
        /// remember, and one that forgot would draw an outline nobody asked for.
        /// </summary>
        public int Decoration;

        /// <summary>
        /// True if this tile lives in the RGBA colour atlas rather than the SDF
        /// one: a colour emoji or an inline sprite. It picks the sampler, not
        /// a second draw call.
        /// </summary>
        public bool IsColor;

        /// <summary>
        /// True if this tile is a multi-channel field, from the precise atlas.
        /// Same story as <see cref="IsColor"/>: a third sampler in the same
        /// material, not a second draw call. Never set together with it; a
        /// colour tile is a picture and has no distance to be precise about.
        /// </summary>
        public bool IsPrecise;

        /// <summary>Centre of the tile, the sane pivot for rotation and scaling.</summary>
        public Vector2 Center => Position + Size * 0.5f;
    }

    /// <summary>
    /// A post-layout pass over the drawn tiles.
    ///
    /// Implementations may translate, rotate, scale and tint; they may not
    /// change what text says or where it wraps, because they run after those
    /// decisions and re-running them per frame is exactly the cost this hook
    /// exists to avoid. A modifier that only moves quads costs vertex-write
    /// time, not rebuild time.
    /// </summary>
    public interface ITextQuadModifier
    {
        /// <summary>
        /// Called once per drawn tile, in draw order.
        ///
        /// <paramref name="quad"/> may be edited in place. Return false to drop
        /// the tile entirely, which is how a reveal that has not reached this
        /// cluster yet, or an effect that fades a character out completely,
        /// avoids paying for geometry nobody sees.
        /// </summary>
        bool Modify(ref TextQuad quad, in TextQuadContext context);
    }

    /// <summary>What a modifier is told about the text it is modifying.</summary>
    public readonly struct TextQuadContext
    {
        /// <summary>Total grapheme clusters in the laid-out text.</summary>
        public readonly int GraphemeCount;

        /// <summary>Seconds since the frontend started animating; 0 if it does not.</summary>
        public readonly float Time;

        /// <summary>The layout these quads came from, for anything else needed.</summary>
        public readonly TextLayoutResult Layout;

        public TextQuadContext(TextLayoutResult layout, int graphemeCount, float time)
        {
            Layout = layout;
            GraphemeCount = graphemeCount;
            Time = time;
        }
    }
}
