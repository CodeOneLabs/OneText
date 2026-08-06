using System;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// What an effect is told about the cluster it is animating.
    ///
    /// Deliberately small and deliberately a value: an effect runs per cluster
    /// per frame, and anything that allocates here allocates thousands of times
    /// a second.
    /// </summary>
    public readonly struct TextEffectInput
    {
        /// <summary>Seconds the label has been animating.</summary>
        public readonly float Time;

        /// <summary>Index of the first grapheme cluster this tile covers.</summary>
        public readonly int Cluster;

        /// <summary>Total grapheme clusters in the text.</summary>
        public readonly int ClusterCount;

        /// <summary>
        /// How far this cluster has been revealed, 0 to 1.
        ///
        /// This is what lets an appearance effect key off the typewriter rather
        /// than off wall time: a character that fades in as it is revealed has
        /// to know when its own turn came, not when the label started.
        /// </summary>
        public readonly float Reveal;

        /// <summary>Seconds since this cluster was revealed; 0 if it has not been.</summary>
        public readonly float TimeSinceReveal;

        /// <summary>The tile's own centre, for effects that need a pivot.</summary>
        public readonly Vector2 Center;

        public TextEffectInput(float time, int cluster, int clusterCount,
            float reveal, float timeSinceReveal, Vector2 center)
        {
            Time = time;
            Cluster = cluster;
            ClusterCount = clusterCount;
            Reveal = reveal;
            TimeSinceReveal = timeSinceReveal;
            Center = center;
        }
    }

    /// <summary>What an effect does to a cluster: a transform and a tint.</summary>
    public struct TextEffectOutput
    {
        public Vector2 Translate;
        public float RotationDegrees;
        public Vector2 Scale;
        public Color Tint;

        /// <summary>Identity: no movement, no tint.</summary>
        public static TextEffectOutput Identity => new TextEffectOutput
        {
            Translate = Vector2.zero,
            RotationDegrees = 0f,
            Scale = Vector2.one,
            Tint = Color.white,
        };

        /// <summary>
        /// Combines two effects. Translations and rotations add, scales and
        /// tints multiply, which is what makes <c>&lt;wave&gt;&lt;rainbow&gt;</c>
        /// mean both rather than whichever the parser saw last.
        /// </summary>
        public TextEffectOutput Combine(in TextEffectOutput other) => new TextEffectOutput
        {
            Translate = Translate + other.Translate,
            RotationDegrees = RotationDegrees + other.RotationDegrees,
            Scale = new Vector2(Scale.x * other.Scale.x, Scale.y * other.Scale.y),
            Tint = Tint * other.Tint,
        };

        /// <summary>
        /// This output blended toward identity: 1 is the full effect, 0 is
        /// none. How a time-limited effect ends: a sine stopped mid-swing
        /// teleports its letter home, a sine faded to zero amplitude settles
        /// there.
        /// </summary>
        public TextEffectOutput Faded(float weight) => new TextEffectOutput
        {
            Translate = Translate * weight,
            RotationDegrees = RotationDegrees * weight,
            Scale = Vector2.Lerp(Vector2.one, Scale, weight),
            Tint = Color.Lerp(Color.white, Tint, weight),
        };
    }

    /// <summary>
    /// One animation effect: a function from (time, cluster, reveal) to a
    /// transform and a tint.
    ///
    /// Effects animate <em>grapheme clusters</em>, which is the whole reason
    /// this can live in the engine and could not live on top of one. A shaking
    /// Arabic word shakes as joined letters because the joined letters are one
    /// tile; a wave through Hangul moves syllables because a syllable is one
    /// cluster. An animator working from outside the engine has to guess which
    /// character is where, and stops being able to the moment a ligature
    /// appears.
    /// </summary>
    public interface ITextEffect
    {
        /// <summary>Evaluated once per cluster per frame. Must not allocate.</summary>
        TextEffectOutput Evaluate(in TextEffectInput input);
    }

    /// <summary>
    /// An effect that finishes by itself: it stops changing a cluster a fixed
    /// time after that cluster is revealed, whether or not the tag carried a
    /// <c>for=</c>.
    ///
    /// This is what separates the appearance effects from the ambient ones.
    /// <c>&lt;wave&gt;</c> with no <c>for=</c> genuinely never ends and its
    /// label must tick for ever; <c>&lt;fade&gt;</c> with no <c>for=</c> is over
    /// once the last cluster has faded in, and without this declaration a
    /// dialogue box (a long string, an appearance effect, no <c>for=</c>,
    /// which is the common case and not an exotic one) re-emits its whole mesh
    /// every frame for the rest of its life to write back identical vertices.
    ///
    /// Deliberately a separate interface rather than a member on
    /// <see cref="ITextEffect"/>: every effect written against this package,
    /// including a one-line <see cref="DelegateTextEffect"/>, has to keep
    /// compiling and keep its present behaviour, and an effect that says
    /// nothing is correctly read as one that never settles. A default interface
    /// member would say the same thing in the source and still demand runtime
    /// support this package does not otherwise require.
    /// </summary>
    public interface ISettlingTextEffect : ITextEffect
    {
        /// <summary>
        /// Seconds after a cluster's reveal beyond which this effect no longer
        /// changes that cluster. NaN or infinite means never, the same reading
        /// as an effect that does not implement this at all.
        ///
        /// Measured from each cluster's own reveal stamp, never from the label
        /// clock: a typewriter that reveals its last cluster a minute in has a
        /// fade still to play a minute in.
        /// </summary>
        float SettleSeconds { get; }
    }

    /// <summary>
    /// Parameters an effect tag carries: <c>&lt;wave amp=2 freq=1.5&gt;</c>.
    ///
    /// A fixed set of named floats rather than a dictionary, because a
    /// dictionary per tag per rebuild is exactly the allocation the animation
    /// pass exists to avoid, and because four is more than any effect in the
    /// box needs.
    /// </summary>
    public readonly struct TextEffectParameters
    {
        public readonly float Amplitude, Frequency, Speed, Extra;

        /// <summary>
        /// Seconds the effect runs for (<c>&lt;wave for=0.5&gt;</c>); NaN or
        /// non-positive means for ever. Effects never see this; the animator
        /// applies it as an envelope over their output, which is what makes
        /// every effect, including user-registered ones, time-limitable
        /// without knowing it.
        /// </summary>
        public readonly float Duration;

        public TextEffectParameters(float amplitude, float frequency, float speed, float extra)
            : this(amplitude, frequency, speed, extra, float.NaN) { }

        public TextEffectParameters(float amplitude, float frequency, float speed, float extra,
            float duration)
        {
            Amplitude = amplitude;
            Frequency = frequency;
            Speed = speed;
            Extra = extra;
            Duration = duration;
        }

        public static TextEffectParameters Default => new TextEffectParameters(1f, 1f, 1f, 0f);

        /// <summary>This, with any value the tag did not set taken from defaults.</summary>
        public TextEffectParameters Or(in TextEffectParameters fallback) => new TextEffectParameters(
            float.IsNaN(Amplitude) ? fallback.Amplitude : Amplitude,
            float.IsNaN(Frequency) ? fallback.Frequency : Frequency,
            float.IsNaN(Speed) ? fallback.Speed : Speed,
            float.IsNaN(Extra) ? fallback.Extra : Extra,
            float.IsNaN(Duration) ? fallback.Duration : Duration);

        public override string ToString() =>
            $"amp={Amplitude} freq={Frequency} speed={Speed} extra={Extra} for={Duration}";
    }

    /// <summary>
    /// An effect plus the parameters and the text range it applies to. This is
    /// what a tag becomes.
    /// </summary>
    public readonly struct TextEffectSpan
    {
        public readonly ITextEffect Effect;
        public readonly TextEffectParameters Parameters;

        /// <summary>Grapheme clusters this effect covers, inclusive of First.</summary>
        public readonly int FirstCluster, LastCluster;

        public TextEffectSpan(ITextEffect effect, in TextEffectParameters parameters,
            int firstCluster, int lastCluster)
        {
            Effect = effect;
            Parameters = parameters;
            FirstCluster = firstCluster;
            LastCluster = lastCluster;
        }

        public bool Covers(int cluster) => cluster >= FirstCluster && cluster <= LastCluster;
    }

    /// <summary>
    /// An effect written as a delegate, for callers who do not want a type.
    /// The registry accepts these, so a project can add an effect in one line.
    /// </summary>
    public sealed class DelegateTextEffect : ITextEffect
    {
        private readonly Func<TextEffectInput, TextEffectParameters, TextEffectOutput> _evaluate;
        private readonly TextEffectParameters _parameters;

        public DelegateTextEffect(
            Func<TextEffectInput, TextEffectParameters, TextEffectOutput> evaluate,
            in TextEffectParameters parameters)
        {
            _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
            _parameters = parameters;
        }

        public TextEffectOutput Evaluate(in TextEffectInput input) => _evaluate(input, _parameters);
    }
}
