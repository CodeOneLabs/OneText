using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// Applies effect spans to a label's tiles, as an
    /// <see cref="ITextQuadModifier"/>.
    ///
    /// It writes quad positions and colours in place and never re-runs shaping
    /// or layout; that is the M8 hook's contract, and it is what makes
    /// animated text cost vertex-write time instead of rebuild time. Nothing
    /// here allocates per frame: the effect list is built when the text is
    /// parsed and evaluated in place afterwards.
    ///
    /// Effects are addressed by grapheme cluster, so a shaking Arabic word
    /// shakes as joined letters and a wave through Hangul moves syllables. That
    /// is the part no animator built on top of a text engine can do, because
    /// from outside there is no way to know which character became which glyph.
    /// </summary>
    public sealed class TextAnimator : ITextQuadModifier
    {
        private readonly List<TextEffectSpan> _spans = new List<TextEffectSpan>();

        /// <summary>Seconds each cluster was revealed at; -1 for not yet.</summary>
        private float[] _revealedAt = System.Array.Empty<float>();

        /// <summary>
        /// The latest stamp any cluster carries, and whether any cluster is
        /// still waiting for one. Both are the reveal's answer to "when can an
        /// appearance effect be over", and both are recomputed whole by
        /// <see cref="NoteReveal"/> rather than nudged, because reveal is not
        /// monotonic: MaxVisibleGraphemes can be set backwards, and a stamp that
        /// is re-taken later has to push the finish out, not be forgotten.
        ///
        /// Kept here, once per reveal, so the per-frame question costs a compare
        /// rather than a walk over every cluster in the label.
        /// </summary>
        private float _latestReveal = float.NegativeInfinity;
        private bool _revealPending = true;

        /// <summary>The effect spans in force. Rebuilt when the text changes.</summary>
        public IReadOnlyList<TextEffectSpan> Spans => _spans;

        /// <summary>True if there is anything to animate.</summary>
        public bool IsEmpty => _spans.Count == 0;

        /// <summary>
        /// The last clock time at which any span can still change a pixel:
        /// negative infinity when there is nothing left to animate, positive
        /// infinity while any span never ends.
        ///
        /// Span count cannot answer this, and a caller that asks it instead pays
        /// for the bug it names: spans are only ever dropped by a rebuild, so a
        /// <c>&lt;pop for=0.3&gt;</c> damage number still holds its span a minute
        /// later, and anything that ticks while spans exist re-emits that whole
        /// mesh every frame for the rest of the label's life to write back
        /// identical vertices.
        ///
        /// A span with a <c>for=</c> ends when its ENVELOPE does, not when the
        /// effect's own <c>speed=</c> would: the envelope's ease-out is itself
        /// motion, so an effect that settled early still has vertices to write
        /// while it is being faded away.
        ///
        /// A span without one ends only if its effect says it settles; see
        /// <see cref="ISettlingTextEffect"/>. Ambient effects say nothing and
        /// are unbounded, which is the whole point: stopping a
        /// <c>&lt;wave&gt;</c>'s clock freezes it mid-swing.
        ///
        /// The maximum wins across spans, so a cluster under both
        /// <c>&lt;fade&gt;</c> and <c>&lt;wave&gt;</c> follows the one that
        /// never stops.
        /// </summary>
        public float WorkEndsAt
        {
            get
            {
                float latest = float.NegativeInfinity;
                for (int i = 0; i < _spans.Count; i++)
                {
                    // Null-effect spans are skipped by Modify too; counting one
                    // as unbounded work would keep a clock running for a tag
                    // that draws nothing.
                    if (_spans[i].Effect == null) continue;
                    float duration = _spans[i].Parameters.Duration;
                    // Exactly EnvelopeWeight's reading of "no duration". Any
                    // disagreement here stops the clock under an effect that is
                    // still being drawn, which is a frozen animation.
                    float endsAt = float.IsNaN(duration) || duration <= 0f
                        ? SettlesAt(_spans[i].Effect)
                        : duration;
                    if (float.IsPositiveInfinity(endsAt)) return float.PositiveInfinity;
                    if (endsAt > latest) latest = endsAt;
                }
                return latest;
            }
        }

        /// <summary>
        /// When an un-enveloped effect stops changing anything: the last reveal
        /// stamp plus what the effect declares it needs after one, or positive
        /// infinity for an effect that never settles.
        ///
        /// Measured off the REVEAL, never off the label clock. A typewriter
        /// still running is unbounded whatever its effects declare: the cluster
        /// revealed ten seconds from now has its whole appearance still to play,
        /// and answering with a time computed from the stamps taken so far would
        /// stop the clock on text that has not arrived yet.
        ///
        /// Conservative by construction: <see cref="_latestReveal"/> is the
        /// label's latest stamp rather than this span's, so a span may be held
        /// open by a reveal outside it. That costs a few extra frames of ticking
        /// on a label that was already ticking for the reveal, where the other
        /// direction, a span released early, is a fade frozen half-drawn.
        /// </summary>
        private float SettlesAt(ITextEffect effect)
        {
            if (!(effect is ISettlingTextEffect settling)) return float.PositiveInfinity;
            float settle = settling.SettleSeconds;
            if (float.IsNaN(settle) || float.IsInfinity(settle)) return float.PositiveInfinity;
            if (_revealPending) return float.PositiveInfinity;
            // Negative infinity is "revealed with the clock stopped", which
            // Modify already draws as finished; adding to it keeps it finished
            // rather than making an editor preview tick for nothing.
            return _latestReveal + Mathf.Max(0f, settle);
        }

        public void Clear()
        {
            _spans.Clear();
            for (int i = 0; i < _revealedAt.Length; i++) _revealedAt[i] = -1f;
            // Un-stamped, so no appearance effect can be called finished until
            // the next draw says which clusters are showing and when. Claiming
            // "settled" here would silence the tick between a text change and
            // the emit that follows it, which is an effect that never plays.
            _latestReveal = float.NegativeInfinity;
            _revealPending = true;
        }

        public void Add(in TextEffectSpan span) => _spans.Add(span);

        /// <summary>
        /// Clears the "shown finished because nothing was advancing the clock"
        /// stamps, so effects play once a clock does start. Without this, the
        /// first frame drawn before anyone set AnimationTime would freeze every
        /// appearance effect as already over.
        /// </summary>
        public void UnlatchFrozenStamps()
        {
            for (int i = 0; i < _revealedAt.Length; i++)
                if (float.IsNegativeInfinity(_revealedAt[i]))
                {
                    _revealedAt[i] = -1f;
                    // A cluster that was being shown finished is now waiting for
                    // a real stamp, so nothing keyed off the reveal is over. The
                    // NoteReveal that follows recomputes this; saying it here as
                    // well is what keeps the two safe to call apart.
                    _revealPending = true;
                }
        }

        /// <summary>
        /// Records that clusters up to <paramref name="revealed"/> are visible
        /// as of <paramref name="time"/>.
        ///
        /// Appearance effects key off when a cluster's own turn came, not off
        /// when the label started, so the moment has to be remembered per
        /// cluster. Called by the frontend as the reveal advances.
        /// </summary>
        public void NoteReveal(int revealed, int clusterCount, float time)
        {
            if (_revealedAt.Length < clusterCount)
            {
                var grown = new float[Mathf.Max(clusterCount, _revealedAt.Length * 2 + 8)];
                for (int i = 0; i < grown.Length; i++)
                    grown[i] = i < _revealedAt.Length ? _revealedAt[i] : -1f;
                _revealedAt = grown;
            }

            // Everything up to the reveal point that has no time yet is stamped
            // now. A label that starts fully revealed therefore stamps every
            // cluster at the current clock, which is what makes an appearance
            // effect play once on a label nobody is typing rather than never.
            int limit = revealed < 0 ? clusterCount : Mathf.Min(revealed, clusterCount);
            for (int i = 0; i < limit; i++)
                if (_revealedAt[i] < 0f && !float.IsNegativeInfinity(_revealedAt[i]))
                    _revealedAt[i] = time;

            // Clusters past the reveal point are un-revealed again: rewinding a
            // typewriter has to rewind its fades too.
            for (int i = limit; i < clusterCount && i < _revealedAt.Length; i++)
                _revealedAt[i] = -1f;

            // What an appearance effect's finish is measured from, recomputed
            // whole because reveal runs backwards as readily as forwards. A
            // rewound typewriter re-stamps its clusters at a LATER time than the
            // ones it kept, so a running maximum that was only ever raised would
            // be right by luck and a running one that was lowered would end a
            // fade that is still to play.
            _revealPending = limit < clusterCount;
            float latest = float.NegativeInfinity;
            for (int i = 0; i < limit; i++)
                if (_revealedAt[i] > latest) latest = _revealedAt[i];
            _latestReveal = latest;
        }

        public bool Modify(ref TextQuad quad, in TextQuadContext context)
        {
            if (_spans.Count == 0) return true;

            int cluster = quad.FirstGrapheme;
            float revealedAt = cluster >= 0 && cluster < _revealedAt.Length ? _revealedAt[cluster] : 0f;
            var input = new TextEffectInput(
                context.Time,
                cluster,
                context.GraphemeCount,
                revealedAt > float.NegativeInfinity && revealedAt < 0f ? 0f : 1f,
                // A stamp of -infinity means "revealed, and the clock is not
                // running": appearance effects see infinite elapsed time and
                // show themselves finished. Without it a still label with a
                // fade on it is a label with no text.
                float.IsNegativeInfinity(revealedAt) ? float.MaxValue
                    : revealedAt >= 0f ? Mathf.Max(0f, context.Time - revealedAt)
                    : 0f,
                quad.Center);

            var combined = TextEffectOutput.Identity;
            bool any = false;
            for (int i = 0; i < _spans.Count; i++)
            {
                var span = _spans[i];
                if (span.Effect == null || !span.Covers(cluster)) continue;
                float weight = EnvelopeWeight(span.Parameters.Duration, context.Time);
                if (weight <= 0f) continue;
                var output = span.Effect.Evaluate(input);
                combined = combined.Combine(weight < 1f ? output.Faded(weight) : output);
                any = true;
            }
            if (!any) return true;

            Apply(ref quad, combined);
            return quad.Color.a != 0;
        }

        /// <summary>
        /// How much of a time-limited effect (<c>for=</c> on the tag) is still
        /// in force at <paramref name="time"/>: 1 while it runs, easing to 0
        /// over the last stretch, 0 after. No duration (NaN or non-positive)
        /// is always 1. The ease-out is the point: a sine hard-stopped
        /// mid-swing snaps its letter home; one damped to zero settles.
        /// </summary>
        public static float EnvelopeWeight(float duration, float time)
        {
            if (float.IsNaN(duration) || duration <= 0f) return 1f;
            if (time >= duration) return 0f;
            float fade = Mathf.Min(0.15f, duration * 0.25f);
            float weight = Mathf.Clamp01((duration - time) / fade);
            // Smoothstep: the settle decelerates instead of ending on a corner.
            return weight * weight * (3f - 2f * weight);
        }

        /// <summary>
        /// Writes a transform and tint onto a tile.
        ///
        /// Rotation and scale are about the tile's centre, which is the only
        /// pivot that does not make a rotating letter drift off its line.
        /// </summary>
        public static void Apply(ref TextQuad quad, in TextEffectOutput output)
        {
            var centre = quad.Center;

            if (output.Scale != Vector2.one)
            {
                quad.Size = new Vector2(quad.Size.x * output.Scale.x, quad.Size.y * output.Scale.y);
                quad.Position = centre - quad.Size * 0.5f;
            }

            quad.Position += output.Translate;

            // Carried to the emitter, which rotates the four corners about the
            // tile's centre. Displacing the centre about itself, the obvious
            // thing to try when the emitter is axis-aligned, is identically
            // zero, which is a rotation effect that does nothing at all.
            quad.Rotation += output.RotationDegrees;

            if (output.Tint != Color.white)
            {
                var tint = output.Tint;
                quad.Color = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(quad.Color.r * tint.r), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(quad.Color.g * tint.g), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(quad.Color.b * tint.b), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(quad.Color.a * tint.a), 0, 255));
            }
        }
    }
}
