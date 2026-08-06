using System;
using System.Collections.Generic;
using UnityEngine;

namespace OneText
{
    /// <summary>
    /// The effects that ship in the box, and the registry markup looks them up
    /// in.
    ///
    /// Shipping these at all is the position: in the TextMesh Pro world a
    /// dialogue box that types, shakes and fades is the engine plus a paid
    /// animation asset stacked on top, each keeping its own model of which
    /// character is where. Here they are ten small structs over the engine's
    /// own cluster mapping, and adding an eleventh from user code is one
    /// <see cref="Register"/> call.
    ///
    /// Every name here is ours. The clean-room rule in CONTRIBUTING.md applies
    /// to animation assets as much as to text engines: feature-level
    /// inspiration is fine, borrowed vocabulary is not.
    /// </summary>
    /// <summary>Which of the four tag parameters an effect reads.</summary>
    [Flags]
    public enum EffectParamUse
    {
        None = 0,
        Amplitude = 1,
        Frequency = 2,
        Speed = 4,
        Extra = 8,
    }

    public static class BuiltInEffects
    {
        private static readonly Dictionary<string, Func<TextEffectParameters, ITextEffect>> s_registry =
            new Dictionary<string, Func<TextEffectParameters, ITextEffect>>(StringComparer.Ordinal);

        private static readonly Dictionary<string, (TextEffectParameters Defaults, EffectParamUse Uses)>
            s_info = new Dictionary<string, (TextEffectParameters, EffectParamUse)>(StringComparer.Ordinal);

        static BuiltInEffects()
        {
            // The defaults here mirror each effect struct's constructor; they
            // exist so tooling (the inspector's effect table) can SHOW what an
            // unparameterised tag will do, which a NaN cannot.
            Register("wave", p => new Wave(p),
                new TextEffectParameters(4f, 0.6f, 4f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("shake", p => new Shake(p),
                new TextEffectParameters(2f, float.NaN, 24f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Speed);
            Register("wobble", p => new Wobble(p),
                new TextEffectParameters(8f, 0.8f, 3f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("bounce", p => new Bounce(p),
                new TextEffectParameters(6f, 0.5f, 3f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("rainbow", p => new Rainbow(p),
                new TextEffectParameters(float.NaN, 0.08f, 0.35f, 0.85f),
                EffectParamUse.Frequency | EffectParamUse.Speed | EffectParamUse.Extra);
            Register("pulse", p => new Pulse(p),
                new TextEffectParameters(0.12f, 0.4f, 4f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("fade", p => new FadeIn(p),
                new TextEffectParameters(float.NaN, float.NaN, 0.25f, float.NaN),
                EffectParamUse.Speed);
            Register("rise", p => new RiseIn(p),
                new TextEffectParameters(10f, float.NaN, 0.3f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Speed);
            Register("swell", p => new SwellIn(p),
                new TextEffectParameters(0.6f, float.NaN, 0.25f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Speed);
            Register("glitch", p => new Glitch(p),
                new TextEffectParameters(3f, 0.15f, 12f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("stretch", p => new Stretch(p),
                new TextEffectParameters(0.15f, 0.5f, 6f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("flash", p => new Flash(p),
                new TextEffectParameters(0.6f, 0f, 8f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed);
            Register("pop", p => new PopIn(p),
                new TextEffectParameters(1f, float.NaN, 0.25f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Speed);
            Register("drop", p => new DropIn(p),
                new TextEffectParameters(14f, float.NaN, 0.35f, float.NaN),
                EffectParamUse.Amplitude | EffectParamUse.Speed);
        }

        /// <summary>
        /// Adds or replaces an effect. The name is what
        /// <c>&lt;name amp=… freq=…&gt;</c> looks up, and a name markup cannot
        /// resolve stays literal text, the same rule every other tag follows.
        /// </summary>
        public static void Register(string name, Func<TextEffectParameters, ITextEffect> factory) =>
            Register(name, factory, TextEffectParameters.Default,
                EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed |
                EffectParamUse.Extra);

        /// <summary>
        /// Same, declaring the defaults an unparameterised tag gets and which
        /// parameters the effect reads at all: what lets tooling present the
        /// effect honestly instead of offering knobs that do nothing.
        /// </summary>
        public static void Register(string name, Func<TextEffectParameters, ITextEffect> factory,
            in TextEffectParameters defaults, EffectParamUse uses)
        {
            if (string.IsNullOrEmpty(name) || factory == null) return;
            s_registry[name] = factory;
            s_info[name] = (defaults, uses);
        }

        /// <summary>True if markup should treat this name as an effect tag.</summary>
        public static bool Has(string name) => s_registry.ContainsKey(name);

        /// <summary>Builds an effect by name, or null.</summary>
        public static ITextEffect Create(string name, in TextEffectParameters parameters) =>
            s_registry.TryGetValue(name, out var factory) ? factory(parameters) : null;

        /// <summary>Every registered name, for tooling and error messages.</summary>
        public static IEnumerable<string> Names => s_registry.Keys;

        /// <summary>The values an unparameterised tag runs with.</summary>
        public static TextEffectParameters DefaultsOf(string name) =>
            s_info.TryGetValue(name, out var info) ? info.Defaults : TextEffectParameters.Default;

        /// <summary>Which parameters the effect actually reads.</summary>
        public static EffectParamUse UsesOf(string name) =>
            s_info.TryGetValue(name, out var info)
                ? info.Uses
                : EffectParamUse.Amplitude | EffectParamUse.Frequency | EffectParamUse.Speed |
                  EffectParamUse.Extra;

        // ------------------------------------------------------------ movement

        /// <summary>A travelling sine: the classic wave along a line of text.</summary>
        private readonly struct Wave : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Wave(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 4f : p.Amplitude;
                _frequency = float.IsNaN(p.Frequency) ? 0.6f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 4f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                // Phase from the cluster index, not from the x position: that
                // is what makes the wave travel through the text in reading
                // order, and it is the same in a right-to-left line.
                output.Translate = new Vector2(0f,
                    Mathf.Sin(input.Time * _speed + input.Cluster * _frequency) * _amplitude);
                return output;
            }
        }

        /// <summary>Per-cluster jitter, uncorrelated between neighbours.</summary>
        private readonly struct Shake : ITextEffect
        {
            private readonly float _amplitude, _speed;

            public Shake(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 2f : p.Amplitude;
                _speed = float.IsNaN(p.Speed) ? 24f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                // Value noise rather than Random: an effect must be a pure
                // function of its input, or two labels showing the same text
                // shake differently and a paused game keeps twitching.
                float t = input.Time * _speed;
                output.Translate = new Vector2(
                    Noise(input.Cluster * 7.13f + t) * _amplitude,
                    Noise(input.Cluster * 3.71f + t + 41.7f) * _amplitude);
                return output;
            }
        }

        /// <summary>A slow rotation back and forth.</summary>
        private readonly struct Wobble : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Wobble(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 8f : p.Amplitude;
                _frequency = float.IsNaN(p.Frequency) ? 0.8f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 3f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                output.RotationDegrees =
                    Mathf.Sin(input.Time * _speed + input.Cluster * _frequency) * _amplitude;
                return output;
            }
        }

        /// <summary>A hop that runs along the text and lands.</summary>
        private readonly struct Bounce : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Bounce(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 6f : p.Amplitude;
                _frequency = float.IsNaN(p.Frequency) ? 0.5f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 3f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float phase = input.Time * _speed - input.Cluster * _frequency;
                // abs(sin) rather than sin: a bounce touches the floor and comes
                // back up, it does not sink through it.
                output.Translate = new Vector2(0f, Mathf.Abs(Mathf.Sin(phase)) * _amplitude);
                return output;
            }
        }

        /// <summary>
        /// Corrupted-signal bursts: a cluster sits still most of the time and
        /// occasionally tears sideways for a slot or two, dimming as it does.
        /// Time is cut into slots and each (slot, cluster) pair rolls the same
        /// dice every frame, deterministic like everything here, so two labels
        /// with the same text glitch identically and a paused game stays torn
        /// exactly as it was.
        /// </summary>
        private readonly struct Glitch : ITextEffect
        {
            private readonly float _amplitude, _fraction, _speed;

            public Glitch(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 3f : p.Amplitude;
                _fraction = float.IsNaN(p.Frequency) ? 0.15f : Mathf.Clamp01(p.Frequency);
                _speed = float.IsNaN(p.Speed) ? 12f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float slot = Mathf.Floor(input.Time * _speed) + input.Cluster * 17f;
                // The gate: |noise| is uniform-ish in [0,1], so comparing it to
                // the fraction makes freq= literally "how much of the time this
                // cluster is torn".
                if (Mathf.Abs(Noise(slot * 0.731f)) > _fraction) return output;
                output.Translate = new Vector2(Noise(slot * 1.937f) * _amplitude, 0f);
                output.Tint = new Color(1f, 1f, 1f, 0.6f + 0.4f * Mathf.Abs(Noise(slot * 2.417f)));
                return output;
            }
        }

        /// <summary>
        /// Squash and stretch: wide when it is flat, tall when it is thin, in
        /// antiphase so the letter reads as elastic rather than merely scaled.
        /// </summary>
        private readonly struct Stretch : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Stretch(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 0.15f : p.Amplitude;
                _frequency = float.IsNaN(p.Frequency) ? 0.5f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 6f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float s = Mathf.Sin(input.Time * _speed + input.Cluster * _frequency) * _amplitude;
                output.Scale = new Vector2(1f + s, 1f - s);
                return output;
            }
        }

        // -------------------------------------------------------------- colour

        /// <summary>
        /// A blink. freq=0 (the default) blinks the whole span in unison,
        /// which is what a warning wants; a non-zero freq staggers clusters.
        /// </summary>
        private readonly struct Flash : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Flash(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 0.6f : Mathf.Clamp01(p.Amplitude);
                _frequency = float.IsNaN(p.Frequency) ? 0f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 8f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float dip = 0.5f + 0.5f * Mathf.Sin(input.Time * _speed + input.Cluster * _frequency);
                output.Tint = new Color(1f, 1f, 1f, 1f - _amplitude * dip);
                return output;
            }
        }

        /// <summary>A hue sweep along the text.</summary>
        private readonly struct Rainbow : ITextEffect
        {
            private readonly float _frequency, _speed, _saturation;

            public Rainbow(in TextEffectParameters p)
            {
                _frequency = float.IsNaN(p.Frequency) ? 0.08f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 0.35f : p.Speed;
                _saturation = float.IsNaN(p.Extra) ? 0.85f : Mathf.Clamp01(p.Extra);
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float hue = Mathf.Repeat(input.Cluster * _frequency + input.Time * _speed, 1f);
                output.Tint = Color.HSVToRGB(hue, _saturation, 1f);
                return output;
            }
        }

        /// <summary>Breathing scale.</summary>
        private readonly struct Pulse : ITextEffect
        {
            private readonly float _amplitude, _frequency, _speed;

            public Pulse(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 0.12f : p.Amplitude;
                _frequency = float.IsNaN(p.Frequency) ? 0.4f : p.Frequency;
                _speed = float.IsNaN(p.Speed) ? 4f : p.Speed;
            }

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float s = 1f + Mathf.Sin(input.Time * _speed + input.Cluster * _frequency) * _amplitude;
                output.Scale = new Vector2(s, s);
                return output;
            }
        }

        // ---------------------------------------------------------- appearance
        //
        // These key off reveal progress rather than wall time, which is the
        // difference between "the label has been alive for two seconds" and
        // "this character's turn just came". A typewriter with a fade has to
        // mean the second one.
        //
        // Every one of them is also ISettlingTextEffect, reporting the same
        // duration it already clamps its own t against. That is what lets a
        // dialogue box (an appearance effect over a long string with no for=)
        // stop ticking once the text has arrived, instead of re-emitting the
        // mesh for the rest of the label's life to redraw a finished fade. The
        // ambient effects above deliberately do not implement it: they never end
        // and a label carrying one must never stop.

        private readonly struct FadeIn : ITextEffect, ISettlingTextEffect
        {
            private readonly float _duration;

            public FadeIn(in TextEffectParameters p) =>
                _duration = float.IsNaN(p.Speed) ? 0.25f : Mathf.Max(0.0001f, p.Speed);

            // Clamp01 above: at TimeSinceReveal == _duration the alpha is 1 and
            // nothing past it moves.
            public float SettleSeconds => _duration;

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float t = Mathf.Clamp01(input.TimeSinceReveal / _duration);
                output.Tint = new Color(1f, 1f, 1f, t);
                return output;
            }
        }

        private readonly struct RiseIn : ITextEffect, ISettlingTextEffect
        {
            private readonly float _amplitude, _duration;

            public RiseIn(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 10f : p.Amplitude;
                _duration = float.IsNaN(p.Speed) ? 0.3f : Mathf.Max(0.0001f, p.Speed);
            }

            public float SettleSeconds => _duration;

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float t = Mathf.Clamp01(input.TimeSinceReveal / _duration);
                float eased = 1f - (1f - t) * (1f - t);
                output.Translate = new Vector2(0f, -(1f - eased) * _amplitude);
                output.Tint = new Color(1f, 1f, 1f, eased);
                return output;
            }
        }

        /// <summary>
        /// Appears with an overshoot: scales up past full size and springs
        /// back. The reason people reach past the built-in eases: swell
        /// arrives politely, pop arrives with a "toc". amp scales the
        /// overshoot, speed is the duration, like every appearance effect.
        /// </summary>
        private readonly struct PopIn : ITextEffect, ISettlingTextEffect
        {
            private readonly float _overshoot, _duration;

            public PopIn(in TextEffectParameters p)
            {
                _overshoot = 1.70158f * (float.IsNaN(p.Amplitude) ? 1f : Mathf.Max(0f, p.Amplitude));
                _duration = float.IsNaN(p.Speed) ? 0.25f : Mathf.Max(0.0001f, p.Speed);
            }

            // The overshoot is inside the duration: the back-out ease has
            // already crossed 1, peaked and settled by the time t reaches it.
            public float SettleSeconds => _duration;

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float t = Mathf.Clamp01(input.TimeSinceReveal / _duration);
                // Back-out ease: starts at 0, crosses 1, peaks, settles at 1.
                float u = t - 1f;
                float s = 1f + u * u * ((_overshoot + 1f) * u + _overshoot);
                output.Scale = new Vector2(Mathf.Max(0f, s), Mathf.Max(0f, s));
                output.Tint = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 3f));
                return output;
            }
        }

        /// <summary>
        /// Falls in from above and lands with one small bounce, rise's
        /// heavier sibling. amp is the drop height, speed the duration.
        /// </summary>
        private readonly struct DropIn : ITextEffect, ISettlingTextEffect
        {
            private readonly float _height, _duration;

            public DropIn(in TextEffectParameters p)
            {
                _height = float.IsNaN(p.Amplitude) ? 14f : p.Amplitude;
                _duration = float.IsNaN(p.Speed) ? 0.35f : Mathf.Max(0.0001f, p.Speed);
            }

            // The rebound is inside the duration: the second arc dies on the
            // floor exactly at t=1, so there is nothing left after it.
            public float SettleSeconds => _duration;

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float t = Mathf.Clamp01(input.TimeSinceReveal / _duration);
                // Two-arc bounce: the fall lands at 70% of the time, the
                // remainder is one rebound that dies on the floor.
                float progress = t < 0.7f
                    ? (t / 0.7f) * (t / 0.7f)
                    : 1f - 0.18f * Mathf.Sin(Mathf.PI * (t - 0.7f) / 0.3f);
                output.Translate = new Vector2(0f, (1f - progress) * _height);
                output.Tint = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 4f));
                return output;
            }
        }

        private readonly struct SwellIn : ITextEffect, ISettlingTextEffect
        {
            private readonly float _amplitude, _duration;

            public SwellIn(in TextEffectParameters p)
            {
                _amplitude = float.IsNaN(p.Amplitude) ? 0.6f : p.Amplitude;
                _duration = float.IsNaN(p.Speed) ? 0.25f : Mathf.Max(0.0001f, p.Speed);
            }

            public float SettleSeconds => _duration;

            public TextEffectOutput Evaluate(in TextEffectInput input)
            {
                var output = TextEffectOutput.Identity;
                float t = Mathf.Clamp01(input.TimeSinceReveal / _duration);
                float eased = 1f - (1f - t) * (1f - t);
                float s = Mathf.Lerp(1f - _amplitude, 1f, eased);
                output.Scale = new Vector2(s, s);
                output.Tint = new Color(1f, 1f, 1f, eased);
                return output;
            }
        }

        /// <summary>
        /// Deterministic value noise in [-1, 1]. Not a good noise function; a
        /// good enough one, with no state and no allocation, which is what an
        /// effect running per cluster per frame actually needs.
        /// </summary>
        private static float Noise(float x)
        {
            float s = Mathf.Sin(x) * 43758.5453f;
            return (s - Mathf.Floor(s)) * 2f - 1f;
        }
    }
}
