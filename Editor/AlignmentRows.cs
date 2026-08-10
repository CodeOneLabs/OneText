using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// The two alignment enums, drawn as rows of icon buttons instead of
    /// dropdowns.
    ///
    /// This is the one control everyone arriving from TextMesh Pro looks for
    /// first, and a dropdown reading "Center" is not it: alignment is a
    /// picture, and a picture is read without opening anything. The buttons
    /// are radio buttons wearing a segmented button's clothes — one of them
    /// is always pressed, and pressing the pressed one changes nothing.
    ///
    /// It lives outside the inspector that uses it because the same two
    /// fields exist on <see cref="OneTextMesh"/>, which today has no editor of
    /// its own and draws them from the default inspector. The day it gets one,
    /// the row is a call and not a paste.
    /// </summary>
    internal static class AlignmentRows
    {
        /// <summary>
        /// Placement along the inline axis: where the text sits on its line,
        /// or in its column when the writing mode is vertical.
        ///
        /// The row is relabelled rather than reordered in vertical writing
        /// mode, because the field has not changed meaning — the axis it names
        /// has turned. <c>Left</c> still means the start edge of the inline
        /// axis; in a column that edge is the top, so the row shows the icon
        /// for the top and says so in the tooltip. An inspector that kept
        /// drawing a left-aligned paragraph next to text that is stacked
        /// downward would be lying in a picture, which is worse than lying in
        /// a word.
        /// </summary>
        public static void InlineAxis(SerializedProperty alignment, bool verticalWritingMode)
        {
            var label = new GUIContent(
                verticalWritingMode ? "Along column" : "Horizontal",
                verticalWritingMode
                    ? "Where the text sits in its column: Left is the top, Right the bottom."
                    : null);
            Row(alignment, label, verticalWritingMode ? s_alongColumn : s_alongLine);
        }

        /// <summary>
        /// Placement along the block axis: where the stack of lines sits in the
        /// box, or the stack of columns when the writing mode is vertical.
        /// Columns stack right to left, so the icons for that mode run the
        /// other way: Top is the right edge.
        /// </summary>
        public static void BlockAxis(SerializedProperty verticalAlignment, bool verticalWritingMode)
        {
            var label = new GUIContent(
                verticalWritingMode ? "Across columns" : "Vertical",
                verticalWritingMode
                    ? "Where the columns sit in the box: Top is the right edge, which is " +
                      "where a right-to-left column stack starts."
                    : null);
            Row(verticalAlignment, label, verticalWritingMode ? s_acrossColumns : s_acrossLines);
        }

        // ------------------------------------------------------------ buttons

        /// <summary>
        /// One button: the enum value it sets, the built-in icon that depicts
        /// it, and the words to fall back on when there is no icon for it.
        ///
        /// <see cref="Value"/> is compared against
        /// <see cref="SerializedProperty.enumValueIndex"/>, which is a position
        /// in the enum's declaration and not the number behind the name. Both
        /// enums here are declared from zero with no gaps, so the two coincide;
        /// give either of them an explicit value and this is the line to fix.
        /// </summary>
        private readonly struct Button
        {
            public readonly int Value;
            public readonly string Icon, Words, Tooltip;

            public Button(int value, string icon, string words, string tooltip)
            {
                Value = value;
                Icon = icon;
                Words = words;
                Tooltip = tooltip;
            }
        }

        // Left, Centre and Right take Unity's own alignment icons, the ones the
        // RectTransform anchor presets are drawn with. Every name here was
        // checked against the editor's asset bundle in 6000.0: an icon name
        // that does not resolve draws a blank button, which is a worse control
        // than the dropdown this replaced, so none of them is a guess.
        //
        // Justify, Start and End are drawn instead — see DrawnIcon for what
        // the built-ins could not say for each of them.
        //
        // The rows do not run in the enum's order. Left, Centre, Right and
        // Justify come first, in that order, because that is the order TextMesh
        // Pro puts them in and it is what all but a handful of labels are ever
        // set to; Start and End follow, because they are the pair a project
        // reaches for only once it has right-to-left text to serve. It also
        // groups the pictures honestly: the first four align to the box and
        // carry no arrow, the last two follow the text and do. Order here is
        // presentation only — each button writes its own enum value, so the
        // row can be arranged for the reader without touching what it means.
        private static readonly Button[] s_alongLine =
        {
            new Button((int)TextAlignment.Left, "align_horizontally_left", "Left", "Left"),
            new Button((int)TextAlignment.Center, "align_horizontally_center", "Centre", "Center"),
            new Button((int)TextAlignment.Right, "align_horizontally_right", "Right", "Right"),
            new Button((int)TextAlignment.Justified, "onetext:justify", "Justify",
                "Justified: stretched to the full width, except on a paragraph's last line."),
            new Button((int)TextAlignment.Start, "onetext:start", "Start",
                "Start: the paragraph's own start edge — left in left-to-right text, " +
                "right in right-to-left."),
            new Button((int)TextAlignment.End, "onetext:end", "End",
                "End: the paragraph's own end edge — right in left-to-right text, " +
                "left in right-to-left."),
        };

        private static readonly Button[] s_alongColumn =
        {
            new Button((int)TextAlignment.Left, "align_vertically_top", "Left",
                "Left, which in a column is the top."),
            new Button((int)TextAlignment.Center, "align_vertically_center", "Centre",
                "Centre of the column."),
            new Button((int)TextAlignment.Right, "align_vertically_bottom", "Right",
                "Right, which in a column is the bottom."),
            new Button((int)TextAlignment.Justified, "onetext:justify-column", "Justify",
                "Justified: stretched down the whole column, except on a paragraph's last line."),
            new Button((int)TextAlignment.Start, "onetext:start-column", "Start",
                "Start: the column's own start edge, which is its top."),
            new Button((int)TextAlignment.End, "onetext:end-column", "End",
                "End: the column's own end edge, which is its bottom."),
        };

        private static readonly Button[] s_acrossLines =
        {
            new Button((int)VerticalAlignment.Top, "align_vertically_top", "Top", "Top"),
            new Button((int)VerticalAlignment.Middle, "align_vertically_center", "Middle", "Middle"),
            new Button((int)VerticalAlignment.Bottom, "align_vertically_bottom", "Bottom", "Bottom"),
        };

        private static readonly Button[] s_acrossColumns =
        {
            new Button((int)VerticalAlignment.Top, "align_horizontally_right", "Top",
                "Top, which for a stack of right-to-left columns is the right edge: " +
                "where the stack starts."),
            new Button((int)VerticalAlignment.Middle, "align_horizontally_center", "Middle",
                "Middle: the stack of columns centred in the box."),
            new Button((int)VerticalAlignment.Bottom, "align_horizontally_left", "Bottom",
                "Bottom, which for a stack of right-to-left columns is the left edge."),
        };

        private const float IconWidth = 26f, TextPadding = 10f;

        /// <summary>
        /// Draws the row and writes the clicked value through the property.
        ///
        /// Through the property and never through the target: that is what
        /// keeps undo and multi-object editing working, and it is why a mixed
        /// selection simply shows no button pressed — there is no one value to
        /// press, and clicking any of them gives every selected object that
        /// one.
        /// </summary>
        private static void Row(SerializedProperty property, GUIContent label, Button[] buttons)
        {
            var line = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(line, label, property);
            var row = EditorGUI.PrefixLabel(line, label);

            var faces = new GUIContent[buttons.Length];
            var widths = new float[buttons.Length];
            float needed = 0f;
            for (int i = 0; i < buttons.Length; i++)
            {
                faces[i] = Face(buttons[i]);
                widths[i] = faces[i].image != null
                    ? IconWidth
                    : EditorStyles.miniButtonMid.CalcSize(faces[i]).x + TextPadding;
                needed += widths[i];
            }
            // A narrow inspector shrinks the row rather than letting it run
            // out under the scrollbar: a squeezed icon is still the icon, a
            // clipped one is half a picture.
            float scale = needed > row.width && needed > 0f ? row.width / needed : 1f;

            bool mixed = property.hasMultipleDifferentValues;
            float x = row.x;
            for (int i = 0; i < buttons.Length; i++)
            {
                float width = Mathf.Floor(widths[i] * scale);
                var rect = new Rect(x, row.y, width, row.height);
                x += width;

                bool on = !mixed && property.enumValueIndex == buttons[i].Value;
                var style = buttons.Length == 1 ? EditorStyles.miniButton
                    : i == 0 ? EditorStyles.miniButtonLeft
                    : i == buttons.Length - 1 ? EditorStyles.miniButtonRight
                    : EditorStyles.miniButtonMid;
                // Toggle rather than Button so the current value stays visibly
                // pressed; the pressed one clicked returns false, and a radio
                // group has nothing to turn off, so only a click that turns a
                // button on is a change.
                if (GUI.Toggle(rect, on, faces[i], style) && !on)
                    property.enumValueIndex = buttons[i].Value;
            }

            EditorGUI.EndProperty();
        }

        /// <summary>
        /// A button's face: the icon if Unity has one under that name, the
        /// words if it does not. Resolved once per name so that a name Unity
        /// renames in some later version costs one console line at domain
        /// reload rather than one per repaint — and shows its word instead of
        /// a blank button either way.
        /// </summary>
        private static GUIContent Face(in Button button)
        {
            if (button.Icon == null) return new GUIContent(button.Words, button.Tooltip);
            var icon = Icon(button.Icon);
            return icon != null
                ? new GUIContent(icon, button.Tooltip)
                : new GUIContent(button.Words, button.Tooltip);
        }

        private static readonly Dictionary<string, Texture> s_icons = new Dictionary<string, Texture>();
        private static bool s_proSkin;
        private static float s_pixelsPerPoint;

        private static Texture Icon(string name)
        {
            // Built-in icons come in a light and a dark cut and IconContent
            // picks by the skin in force when it is asked. Cached across a
            // theme change, the row would keep the old cut's contrast until
            // the next domain reload, so the cache is thrown away with the
            // skin it was filled under. The drawn ones below are thrown away
            // with the display's scale too: dragging the window from a Retina
            // screen to an external one halves the pixels a point is worth,
            // and a texture rasterized for the old one arrives soft.
            if (s_proSkin != EditorGUIUtility.isProSkin ||
                s_pixelsPerPoint != EditorGUIUtility.pixelsPerPoint)
            {
                s_proSkin = EditorGUIUtility.isProSkin;
                s_pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
                foreach (var texture in s_icons.Values)
                    if (texture is Texture2D drawn && drawn.hideFlags == HideFlags.HideAndDontSave)
                        Object.DestroyImmediate(drawn);
                s_icons.Clear();
            }
            if (s_icons.TryGetValue(name, out var cached)) return cached;
            var icon = name.StartsWith(Drawn, System.StringComparison.Ordinal)
                ? DrawnIcon(name)
                : EditorGUIUtility.IconContent(name)?.image;
            s_icons[name] = icon;
            return icon;
        }

        // ------------------------------------------------------- drawn icons

        private const string Drawn = "onetext:";

        /// <summary>
        /// The three icons Unity has nothing honest for, drawn to the same
        /// rhythm as the built-ins beside them: thin lines of text, one grid
        /// pixel each.
        ///
        /// <para><b>Start and End.</b> The difficulty is not that Unity lacks
        /// an icon for "flush to one edge" — it has four. It is that Start
        /// <em>is</em> Left in left-to-right text, so any icon showing text
        /// against an edge would be the Left icon sitting in the row twice, and
        /// a row with the same picture on two buttons is worse than the words
        /// it replaced. What separates them is that Start follows the text
        /// rather than the box, so that is what is drawn: the same paragraph,
        /// under an arrow pointing the way the text runs. Left and Right have
        /// no arrow. The rule reads itself off the row — the ones with an arrow
        /// move with the language — which is the whole of what the two values
        /// mean.</para>
        ///
        /// <para><b>Justified.</b> This one had a built-in and gave it up.
        /// <c>align_horizontally</c> is the extent bar Unity draws for
        /// stretching a RectTransform, and at icon size it reads as a serif
        /// capital T: correct about filling a measure, silent about text. Every
        /// word processor ever shipped draws justification as lines of equal
        /// full width, and that is a picture nobody has to be taught.</para>
        ///
        /// <para>In a column the arrow turns with the text and points down, and
        /// the lines pin to the column's top or bottom — or space out down its
        /// whole length, for justified — to match the vertical icons the rest
        /// of that row is using.</para>
        /// </summary>
        private static Texture2D DrawnIcon(string name)
        {
            bool column = name.EndsWith("-column", System.StringComparison.Ordinal);
            string kind = column ? name.Substring(Drawn.Length, name.Length - Drawn.Length - 7)
                : name.Substring(Drawn.Length);
            bool atEnd = kind == "end";
            bool justified = kind == "justify";

            // A whole number of device pixels per grid cell, so every edge
            // lands on one and the result is as crisp as a hand-made bitmap.
            int scale = Mathf.Max(1, Mathf.RoundToInt(s_pixelsPerPoint));
            const int Grid = 16;
            int size = Grid * scale;

            var pixels = new Color32[size * size];
            // Unity's own icons are near-white on the dark skin and near-black
            // on the light one; matching them is what keeps this from reading
            // as a foreign object between two built-ins.
            var ink = s_proSkin ? new Color32(220, 220, 220, 255) : new Color32(45, 45, 45, 255);

            void Cell(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (x < 0 || x >= Grid || y < 0 || y >= Grid) continue;
                    // The grid is written top-down, the way the picture reads;
                    // texture rows run bottom-up.
                    int top = (Grid - 1 - y) * scale;
                    for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                        pixels[(top + dy) * size + x * scale + dx] = ink;
                }
            }

            if (justified)
            {
                // Four lines, every one the full measure, spread over the whole
                // box. No arrow: justification is a fact about the box, and the
                // arrow in this set means "follows the text".
                // Column mode keeps the lines horizontal, the way the vertical
                // icons beside it do, and spreads them over the whole height:
                // Left bunches them at the top, Right at the foot, and this is
                // the one that fills the column.
                for (int i = 0; i < 4; i++)
                {
                    if (column) Cell(3, 2 + i * 4, 12, 2 + i * 4);
                    else Cell(2, 3 + i * 3, 13, 3 + i * 3);
                }
            }
            else if (column)
            {
                // Shaft down the left, head at the bottom.
                Cell(2, 2, 2, 11);
                for (int i = 0; i < 3; i++) Cell(2 - 2 + i, 12 + i, 2 + 2 - i, 12 + i);
                // Three lines of text pinned to the column's head or its foot.
                for (int i = 0; i < 3; i++)
                {
                    int width = i == 1 ? 6 : 9;
                    int y = atEnd ? 13 - i * 3 : 3 + i * 3;
                    Cell(6, y, 6 + width - 1, y);
                }
            }
            else
            {
                // Shaft across the top, head at the right.
                Cell(2, 2, 11, 2);
                for (int i = 0; i < 3; i++) Cell(12 + i, 2 - 2 + i, 12 + i, 2 + 2 - i);
                for (int i = 0; i < 3; i++)
                {
                    int width = i == 1 ? 8 : 12;
                    int y = 7 + i * 3;
                    if (atEnd) Cell(14 - width + 1, y, 14, y);
                    else Cell(2, y, 2 + width - 1, y);
                }
            }

            // No mip chain and no linear flag: editor GUI draws these in gamma
            // space, and asking for linear would wash the ink out by exactly
            // the amount that makes it look like a disabled button.
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                // Point, because the grid was drawn at whole device pixels and
                // any smoothing would only undo that.
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
                name = name,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
