using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// The two pictures the Hub draws: a ring that says what fraction of
    /// something is which, and a strip of bars that says how that fraction has
    /// been moving.
    ///
    /// Drawn with the vector API rather than assembled out of coloured boxes,
    /// because a proportion drawn as a ring is read in one glance and the same
    /// proportion written as three percentages is read in three.
    /// </summary>
    public sealed class HubDonut : VisualElement
    {
        private readonly List<(float Fraction, Color Color)> _slices =
            new List<(float, Color)>();

        /// <summary>The colour the hole is punched in.</summary>
        public Color Hole = new Color(0.047f, 0.062f, 0.075f);

        /// <summary>What the middle says, if anything.</summary>
        private readonly Label _caption = new Label();
        private readonly Label _captionNote = new Label();

        public HubDonut()
        {
            AddToClassList("donut");
            generateVisualContent += Paint;

            var centre = new VisualElement();
            centre.style.position = Position.Absolute;
            centre.style.left = 0f;
            centre.style.right = 0f;
            centre.style.top = 0f;
            centre.style.bottom = 0f;
            centre.style.alignItems = Align.Center;
            centre.style.justifyContent = Justify.Center;

            _caption.style.fontSize = 20f;
            _caption.style.unityFontStyleAndWeight = FontStyle.Bold;
            _caption.style.color = new Color(0.839f, 0.898f, 0.867f);
            _captionNote.style.fontSize = 9f;
            _captionNote.style.letterSpacing = 1.4f;
            _captionNote.style.color = new Color(0.839f, 0.898f, 0.867f, 0.32f);
            _caption.AddToClassList("mono");

            centre.Add(_caption);
            centre.Add(_captionNote);
            centre.pickingMode = PickingMode.Ignore;
            Add(centre);
        }

        /// <summary>Replaces the ring, clockwise from the top.</summary>
        public HubDonut Slices(params (float Fraction, Color Color)[] slices)
        {
            _slices.Clear();
            if (slices != null) _slices.AddRange(slices);
            MarkDirtyRepaint();
            return this;
        }

        public HubDonut Caption(string big, string small)
        {
            _caption.text = big;
            _captionNote.text = small;
            return this;
        }

        private void Paint(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (float.IsNaN(rect.width) || rect.width <= 1f || rect.height <= 1f) return;

            var painter = context.painter2D;
            var centre = new Vector2(rect.width * 0.5f, rect.height * 0.5f);
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 1f;
            float inner = radius * 0.62f;

            // Clockwise from the top, because a ring that starts anywhere else
            // reads as an arbitrary picture rather than a proportion.
            float angle = -90f;
            foreach (var slice in _slices)
            {
                if (slice.Fraction <= 0.0005f) continue;
                float sweep = Mathf.Min(360f, slice.Fraction * 360f);
                painter.fillColor = slice.Color;
                painter.BeginPath();
                painter.MoveTo(centre);
                painter.Arc(centre, radius, Angle.Degrees(angle), Angle.Degrees(angle + sweep));
                painter.ClosePath();
                painter.Fill();
                angle += sweep;
            }

            painter.fillColor = Hole;
            painter.BeginPath();
            painter.Arc(centre, inner, Angle.Degrees(0f), Angle.Degrees(360f));
            painter.ClosePath();
            painter.Fill();
        }
    }

    /// <summary>
    /// A strip of bars: one sample per column, oldest on the left.
    ///
    /// A single total that only ever goes up says nothing about pressure; the
    /// shape of it over the last two minutes says everything.
    /// </summary>
    public sealed class HubSparkline : VisualElement
    {
        private int[] _values = Array.Empty<int>();
        private readonly Label _caption = new Label();

        public Color Bar = new Color(1f, 0.8f, 0.4f);

        public HubSparkline()
        {
            AddToClassList("spark");
            generateVisualContent += Paint;
            _caption.AddToClassList("spark__caption");
            _caption.pickingMode = PickingMode.Ignore;
            Add(_caption);
        }

        public void Set(int[] values, string caption)
        {
            _values = values ?? Array.Empty<int>();
            _caption.text = caption;
            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (_values.Length == 0) return;
            if (float.IsNaN(rect.width) || rect.width <= 1f || rect.height <= 1f) return;

            int peak = 1;
            foreach (int value in _values) peak = Mathf.Max(peak, value);

            var painter = context.painter2D;
            painter.fillColor = Bar;
            float width = rect.width / _values.Length;
            for (int i = 0; i < _values.Length; i++)
            {
                int value = _values[i];
                if (value <= 0) continue;
                float height = (rect.height - 4f) * value / peak;
                float x = i * width;
                float w = Mathf.Max(1f, width - 1f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, rect.height));
                painter.LineTo(new Vector2(x, rect.height - height));
                painter.LineTo(new Vector2(x + w, rect.height - height));
                painter.LineTo(new Vector2(x + w, rect.height));
                painter.ClosePath();
                painter.Fill();
            }
        }
    }
}
