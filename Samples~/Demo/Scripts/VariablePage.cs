using System.Collections.Generic;
using System.Text;
using OneText.UGUI;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.Samples
{
    /// <summary>
    /// One file, and a continuum inside it.
    ///
    /// A static family ships a file per weight, and the weights it did not ship
    /// do not exist. Wanting 550 between Medium and SemiBold means either
    /// picking one of them or asking a foundry. A variable font stores the
    /// outlines once with rules for deforming them along named axes, so 550 is
    /// there, and so is 551.
    ///
    /// The reason to put sliders on a page rather than a paragraph is that the
    /// interpolation is the point. Nine static weights animated between them
    /// would pop nine times; a variable axis does not, because the outline
    /// itself is being moved before it is ever rasterised. Dragging the slider
    /// is the only way to feel the difference between "we support variable
    /// fonts" and what variable fonts are for.
    ///
    /// The axes are not hard-coded here. <see cref="OneTextLabel.GetVariationAxes"/>
    /// asks the face what it exposes and returns each axis's real range, so a
    /// font with an optical-size or a slant axis grows the right sliders on its
    /// own — and a font with none says so instead of pretending.
    /// </summary>
    internal sealed class VariablePage : DemoPage
    {
        private const string Sample = "Handgloves 한글 42";

        private readonly List<FontVariation> _values = new List<FontVariation>();
        private readonly List<OneTextLabel> _readouts = new List<OneTextLabel>();
        private readonly StringBuilder _scratch = new StringBuilder(512);

        private const int Rungs = 5;

        private readonly List<OneTextLabel> _rungLabels = new List<OneTextLabel>();

        private FontAxis[] _axes;
        private OneTextLabel _specimen;
        private OneTextLabel _explain;
        private RectTransform _sliderColumn;
        private RectTransform _ladderColumn;

        internal override string Title => "Variable";

        internal override string Claim =>
            "One file holds a continuum, not a handful of weights — and the outline is " +
            "interpolated before it is rasterised, so nothing pops.";

        protected override void Build(RectTransform host)
        {
            var left = DemoUi.Rect("left", host);
            left.anchorMin = new Vector2(0f, 0f);
            left.anchorMax = new Vector2(0.58f, 1f);
            left.offsetMin = new Vector2(4f, 4f);
            left.offsetMax = new Vector2(-4f, -4f);

            var specimenBody = DemoUi.PanelWithTitle("specimen", left,
                "the face, live", Fonts);
            var specimenPanel = (RectTransform)specimenBody.parent;
            specimenPanel.anchorMin = new Vector2(0f, 0.52f);
            specimenPanel.anchorMax = new Vector2(1f, 1f);
            specimenPanel.offsetMin = Vector2.zero;
            specimenPanel.offsetMax = Vector2.zero;

            _specimen = DemoUi.Label("text", specimenBody, Sample, 64f, DemoUi.Ink, Fonts);
            DemoUi.Fill((RectTransform)_specimen.transform, 12f);
            _specimen.Alignment = TextAlignment.Center;
            _specimen.VerticalAlignment = VerticalAlignment.Middle;
            _specimen.Wrap = TextWrap.NoWrap;

            // A ladder of the same string at several points along the first
            // axis, so the continuum is visible at a glance and not only while
            // a slider is moving.
            var ladderBody = DemoUi.PanelWithTitle("ladder", left,
                "the same axis, sampled — every step is a real instance, not a chosen weight",
                Fonts);
            var ladderPanel = (RectTransform)ladderBody.parent;
            ladderPanel.anchorMin = new Vector2(0f, 0f);
            ladderPanel.anchorMax = new Vector2(1f, 0.5f);
            ladderPanel.offsetMin = Vector2.zero;
            ladderPanel.offsetMax = Vector2.zero;

            _ladderColumn = DemoUi.Fill(DemoUi.Rect("rungs", ladderBody), 12f);
            var rungs = _ladderColumn.gameObject.AddComponent<VerticalLayoutGroup>();
            rungs.childControlHeight = true;
            rungs.childControlWidth = true;
            rungs.childForceExpandHeight = true;
            rungs.spacing = 2f;

            var right = DemoUi.Rect("right", host);
            right.anchorMin = new Vector2(0.58f, 0f);
            right.anchorMax = new Vector2(1f, 1f);
            right.offsetMin = new Vector2(4f, 4f);
            right.offsetMax = new Vector2(-4f, -4f);

            var axesBody = DemoUi.PanelWithTitle("axes", right,
                "axes this face exposes", Fonts);
            var axesPanel = (RectTransform)axesBody.parent;
            axesPanel.anchorMin = new Vector2(0f, 0.34f);
            axesPanel.anchorMax = new Vector2(1f, 1f);
            axesPanel.offsetMin = Vector2.zero;
            axesPanel.offsetMax = Vector2.zero;

            _sliderColumn = DemoUi.Rect("sliders", axesBody);
            DemoUi.Fill(_sliderColumn, 10f);
            var column = _sliderColumn.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childControlHeight = true;
            column.childControlWidth = true;
            column.childForceExpandHeight = false;
            column.childForceExpandWidth = true;
            column.spacing = 10f;

            var explainBody = DemoUi.PanelWithTitle("cost", right,
                "what this replaces", Fonts);
            var explainPanel = (RectTransform)explainBody.parent;
            explainPanel.anchorMin = new Vector2(0f, 0f);
            explainPanel.anchorMax = new Vector2(1f, 0.32f);
            explainPanel.offsetMin = Vector2.zero;
            explainPanel.offsetMax = Vector2.zero;

            _explain = DemoUi.Label("text", explainBody, string.Empty, DemoUi.Caption, DemoUi.Ink, Fonts);
            DemoUi.Fill((RectTransform)_explain.transform, 10f);

            BuildAxes();
        }

        private void BuildAxes()
        {
            _axes = _specimen.GetVariationAxes();
            if (_axes == null || _axes.Length == 0)
            {
                // An honest empty state. The page has nothing to demonstrate
                // with a static face, and saying so is better than showing
                // sliders that move nothing.
                DemoUi.Label("none", _sliderColumn,
                    "<color=#D29922>This face has no variation axes.</color>\n\n" +
                    "Assign a variable font — one with an <b>fvar</b> table, such as a " +
                    "Noto Sans variable build — to the demo's font stack and this page " +
                    "grows one slider per axis the file declares.",
                    DemoUi.Body, DemoUi.Ink, Fonts);
                DemoUi.Label("flat", _ladderColumn,
                    Sample + "   <size=14><color=" + DemoUi.DimHex + ">the only instance this file has" +
                    "</color></size>", 26f, DemoUi.Ink, Fonts).Wrap = TextWrap.NoWrap;
                _explain.Text = "<color=" + DemoUi.DimHex + ">Nothing to compare: a static face is one " +
                                "instance per file by definition.</color>";
                return;
            }

            _values.Clear();
            for (int i = 0; i < _axes.Length; i++)
            {
                var axis = _axes[i];
                _values.Add(new FontVariation(axis.Tag, axis.Default));
                BuildSlider(i, axis);
            }

            Apply();
        }

        private void BuildSlider(int index, FontAxis axis)
        {
            var row = DemoUi.Rect(axis.Tag, _sliderColumn);
            var stack = row.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.childControlHeight = true;
            stack.childControlWidth = true;
            stack.childForceExpandHeight = false;
            stack.spacing = 2f;
            row.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var readout = DemoUi.Label("label", row,
                Describe(axis, axis.Default), DemoUi.Caption, DemoUi.Ink, Fonts);
            readout.Wrap = TextWrap.NoWrap;
            var readoutElement = readout.gameObject.AddComponent<LayoutElement>();
            readoutElement.preferredHeight = 22f;
            _readouts.Add(readout);

            var slider = DemoUi.Slider(row, axis.Minimum, axis.Maximum, axis.Default,
                value => OnAxis(index, value));
            var sliderElement = slider.gameObject.AddComponent<LayoutElement>();
            sliderElement.preferredHeight = 20f;
        }

        private static string Describe(FontAxis axis, float value) =>
            "<b>" + axis.Tag + "</b>  " + value.ToString("0.#") +
            "   <color=" + DemoUi.DimHex + ">" + axis.Minimum.ToString("0.#") + " … " +
            axis.Maximum.ToString("0.#") + "</color>";

        private void OnAxis(int index, float value)
        {
            if (index < 0 || index >= _values.Count) return;

            // Quantised, and this is not cosmetic. Every distinct coordinate is
            // a distinct set of outlines and therefore a distinct set of atlas
            // tiles: a drag that emits a new value every frame mints thousands
            // of tiles a second into a cache that holds a few hundred, the
            // atlas starts evicting tiles that are still on screen, and the
            // labels holding their UVs draw whatever replaced them. Forty steps
            // across an axis is finer than an eye can follow and bounded.
            value = Quantise(_axes[index], value);
            if (Mathf.Approximately(_values[index].Value, value)) return;

            _values[index] = new FontVariation(_axes[index].Tag, value);
            if (index < _readouts.Count)
                _readouts[index].Text = Describe(_axes[index], value);

            _specimen.SetVariations(_values.ToArray());
            // The rungs pin axis zero to their own coordinate, so moving axis
            // zero cannot change them — and rebuilding them anyway was six
            // labels' worth of glyphs re-baked per frame of a drag.
            if (index != 0) BuildLadder();
            BuildExplain();
        }

        private static float Quantise(FontAxis axis, float value)
        {
            // Twenty-four, not forty: the specimen is set at 64 px, so one
            // instance of it is fourteen of the largest tiles in the sheet, and
            // a full sweep at forty steps asks for more atlas than exists.
            const int steps = 24;
            float span = axis.Maximum - axis.Minimum;
            if (span <= 0f) return axis.Minimum;
            float step = span / steps;
            return Mathf.Clamp(
                axis.Minimum + Mathf.Round((value - axis.Minimum) / step) * step,
                axis.Minimum, axis.Maximum);
        }

        private void Apply()
        {
            _specimen.SetVariations(_values.ToArray());
            BuildLadder();
            BuildExplain();
        }

        /// <summary>
        /// Five real instances along the first axis, one label each.
        ///
        /// One label with markup could not do this: markup varies size and
        /// colour, not axis coordinates, so five rungs written into one string
        /// would be five copies of the same instance with different captions —
        /// which is exactly the sort of demo this page exists to argue against.
        /// Each rung therefore carries its own <see cref="OneTextLabel.SetVariations"/>
        /// call, and they still batch into one draw, because varying an axis
        /// changes the outlines and not the material.
        /// </summary>
        private void BuildLadder()
        {
            if (_axes == null || _axes.Length == 0) return;
            var axis = _axes[0];

            while (_rungLabels.Count < Rungs)
            {
                var rung = DemoUi.Label("rung" + _rungLabels.Count, _ladderColumn,
                    Sample, 34f, DemoUi.Ink, Fonts);
                // The thin end of a weight axis is where a distance field runs
                // out: at wght 45 a stem is well under a texel of a field baked
                // one texel per pixel, and strokes drop out of the bottom rung.
                // High bakes twice as dense on a canvas, which is enough to
                // hold them — so this ladder doubles as the argument for the
                // quality rung existing at all.
                rung.Quality = TextQuality.High;
                rung.Wrap = TextWrap.NoWrap;
                rung.VerticalAlignment = VerticalAlignment.Middle;
                _rungLabels.Add(rung);
            }

            for (int i = 0; i < _rungLabels.Count; i++)
            {
                float t = Rungs == 1 ? 0f : i / (float)(Rungs - 1);
                float value = Mathf.Lerp(axis.Minimum, axis.Maximum, t);

                // Every other axis stays where the sliders left it, so the
                // ladder shows this one axis moving rather than a jumble.
                var instance = new FontVariation[_values.Count];
                for (int a = 0; a < _values.Count; a++) instance[a] = _values[a];
                instance[0] = new FontVariation(axis.Tag, value);

                _rungLabels[i].Text = Sample + "   <size=14><color=" + DemoUi.DimHex + ">" +
                                      axis.Tag + " " + value.ToString("0") + "</color></size>";
                _rungLabels[i].SetVariations(instance);
            }
        }

        private void BuildExplain()
        {
            if (_axes == null || _axes.Length == 0) return;

            _scratch.Clear();
            _scratch.Append("This face declares <b>").Append(_axes.Length)
                .Append(_axes.Length == 1 ? " axis</b>: " : " axes</b>: ");
            for (int i = 0; i < _axes.Length; i++)
            {
                if (i > 0) _scratch.Append(", ");
                _scratch.Append(_axes[i].Tag);
            }
            _scratch.Append(".\n\n");

            // The combinatorial point, which is the one that actually lands: a
            // static family covering the same ground needs a file per named
            // instance, and the count multiplies with every axis.
            long instances = 1;
            for (int i = 0; i < _axes.Length; i++) instances *= 9;
            _scratch.Append("<color=" + DemoUi.DimHex + ">A static family covering the same ground at nine " +
                            "steps per axis would be <b>").Append(instances)
                .Append("</b> separate files — and would still have nothing between the " +
                        "steps. Here the steps do not exist: there is one file and a " +
                        "coordinate.</color>");
            _explain.Text = _scratch.ToString();
        }
    }
}
