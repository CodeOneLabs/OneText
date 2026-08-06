using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Sprites a label can draw inline with <c>&lt;sprite=…&gt;</c>: the game
    /// icons that appear in dialogue, next to the emoji that come from fonts.
    ///
    /// This is a list of ordinary Unity sprites rather than a bespoke sheet
    /// format. Everything a project already has (an atlas, a slice, an
    /// importer setting) keeps working, and the one thing this needs from them
    /// (pixels and an aspect ratio) every Sprite already carries.
    ///
    /// The sprites go into the same RGBA atlas as colour emoji and draw through
    /// the same quad path, so a line of text with icons in it is still one draw
    /// call. That is also why they are copied in rather than sampled in place:
    /// a second texture would be a second draw call, which is what inline
    /// sprites exist to avoid.
    /// </summary>
    // The Project window draws this rather than the default script sheet.
    [Icon("Packages/com.onetext.core/Editor/Icons/OneTextSpriteSheet.png")]
    [CreateAssetMenu(menuName = "OneText/Sprite Sheet", fileName = "New Sprite Sheet", order = 211)]
    public sealed class OneTextSpriteSheet : ScriptableObject
    {
        [Tooltip("Sprites addressable by index (<sprite=0> is the first) or by name.")]
        [SerializeField] private List<Sprite> _sprites = new List<Sprite>();

        public IReadOnlyList<Sprite> Sprites => _sprites;

        private HashSet<int> _unreadable;

        public int Count => _sprites.Count;

        public Sprite this[int index] =>
            index >= 0 && index < _sprites.Count ? _sprites[index] : null;

        /// <summary>Index of a sprite by asset name, or -1.</summary>
        public int IndexOf(string spriteName)
        {
            for (int i = 0; i < _sprites.Count; i++)
                if (_sprites[i] != null && _sprites[i].name == spriteName) return i;
            return -1;
        }

        /// <summary>
        /// Width over height for a sprite, or 1 when there is none. The layout
        /// engine needs this before anything is drawn: a sprite occupies a
        /// character's worth of line, and how wide that is depends on its
        /// shape, not on the font.
        /// </summary>
        public float AspectOf(int index)
        {
            var sprite = this[index];
            if (sprite == null) return 1f;
            var rect = sprite.rect;
            return rect.height > 0f ? rect.width / rect.height : 1f;
        }

        /// <summary>
        /// Reads a sprite's pixels as an atlas tile, sized to
        /// <paramref name="pixelHeight"/>.
        ///
        /// Returns false when the texture is not readable, which is the common
        /// case for imported sprites and worth a clear error rather than a
        /// silent blank, because the fix ("tick Read/Write") is not guessable.
        /// </summary>
        public bool TryRead(int index, int pixelHeight, out ColorGlyph tile)
        {
            tile = default;
            var sprite = this[index];
            if (sprite == null || sprite.texture == null) return false;

            if (!sprite.texture.isReadable)
            {
                // Once per sprite, not once per rebuild: a typewriter re-runs
                // the mesh build on every revealed cluster, and an error per
                // character is how a useful message becomes noise.
                _unreadable ??= new HashSet<int>();
                if (_unreadable.Add(sprite.GetInstanceID()))
                {
                    Debug.LogError($"OneText: sprite '{sprite.name}' cannot be drawn inline " +
                        $"because its texture '{sprite.texture.name}' is not readable. Tick " +
                        "Read/Write Enabled on the texture importer.", sprite.texture);
                }
                return false;
            }

            var rect = sprite.rect;
            int sourceWidth = Mathf.RoundToInt(rect.width);
            int sourceHeight = Mathf.RoundToInt(rect.height);
            if (sourceWidth <= 0 || sourceHeight <= 0) return false;

            int height = Mathf.Clamp(pixelHeight, 1, 512);
            int width = Mathf.Max(1, Mathf.RoundToInt(height * (sourceWidth / (float)sourceHeight)));

            var source = sprite.texture.GetPixels(
                Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), sourceWidth, sourceHeight);

            // Point-sampled box resize. A sprite is baked into the atlas once
            // per size bucket, so this is not a per-frame cost, and anything
            // fancier would be a resampler this project has no reason to own.
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int sy = Mathf.Min(sourceHeight - 1, y * sourceHeight / height);
                for (int x = 0; x < width; x++)
                {
                    int sx = Mathf.Min(sourceWidth - 1, x * sourceWidth / width);
                    pixels[y * width + x] = source[sy * sourceWidth + sx];
                }
            }

            tile = new ColorGlyph
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                // Sprites sit on the baseline and rise a full em, which is what
                // makes an icon line up with the text beside it rather than
                // with some arbitrary fraction of it.
                OriginUnits = Vector2.zero,
                UnitsPerPixel = 1f,
            };
            return true;
        }
    }
}
