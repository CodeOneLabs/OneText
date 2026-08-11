using System;
using System.Collections.Generic;
using OneText.UGUI;
using UnityEngine;

namespace OneText.Samples
{
    /// <summary>
    /// The font stack every label in the demo shares, by whichever of the two
    /// routes the host project can offer.
    ///
    /// <b>Font assets.</b> Assign <see cref="Primary"/> and the demo behaves
    /// like a normal project: the asset carries its own language, letter
    /// spacing and bold face, and per-codepoint fallbacks come from Project
    /// Settings &gt; OneText. This is the route to use in the editor.
    ///
    /// <b>Raw bytes.</b> Assign <see cref="FontFiles"/> — TTF/OTF files
    /// imported as <see cref="TextAsset"/>, which Unity does for any file
    /// renamed <c>.bytes</c> — and the demo builds the whole chain itself
    /// through <see cref="OneTextLabel.SetFont(byte[], byte[][])"/>. This is
    /// the route a published build wants, because it is the only one that
    /// carries a fallback chain without a project setting behind it, and
    /// because WebGL has no font directory to search.
    ///
    /// Bytes win when both are set. Neither set is still a running demo: the
    /// engine falls through to <c>SystemFonts</c>, which finds something for
    /// most of these scripts on a desktop editor and nothing at all in a
    /// player. The stats panel says which of the three happened, because
    /// "the demo drew boxes" and "the demo had no fonts" look identical on
    /// screen and are entirely different bugs.
    /// </summary>
    [Serializable]
    public sealed class OneTextDemoFonts
    {
        [Tooltip("Shapes Latin and sets the metrics. Fallbacks come from Project Settings > OneText.")]
        public OneFontAsset Primary;

        [Tooltip("TTF/OTF files imported as TextAsset (rename to .bytes). " +
                 "First is the primary; the rest are the fallback chain, in order. " +
                 "Overrides the font asset above. Required for a build.")]
        public List<TextAsset> FontFiles = new List<TextAsset>();

        private byte[] _primaryBytes;
        private byte[][] _fallbackBytes;
        private bool _prepared;

        public enum Source { None, Asset, Bytes }

        public Source Resolved { get; private set; }

        /// <summary>
        /// How many faces the chain has. Zero means the demo is running on
        /// whatever the operating system had, which is not a thing a build can
        /// do.
        /// </summary>
        public int ChainLength =>
            Resolved == Source.Bytes ? 1 + (_fallbackBytes?.Length ?? 0)
            : Resolved == Source.Asset ? 1
            : 0;

        /// <summary>
        /// Splits the byte route into primary and fallbacks once, so that a
        /// hundred stress labels hand <see cref="OneTextLabel.SetFont"/> the
        /// same array references. That identity is what makes them share one
        /// parsed face through <c>SharedFontBytes</c> rather than each paying
        /// for its own copy — which matters here more than anywhere, since the
        /// panel next to them is reporting memory.
        /// </summary>
        public void Prepare()
        {
            if (_prepared) return;
            _prepared = true;

            var files = new List<TextAsset>();
            if (FontFiles != null)
            {
                foreach (var file in FontFiles)
                    if (file != null && file.bytes != null && file.bytes.Length > 0)
                        files.Add(file);
            }

            if (files.Count > 0)
            {
                _primaryBytes = files[0].bytes;
                _fallbackBytes = new byte[files.Count - 1][];
                for (int i = 1; i < files.Count; i++) _fallbackBytes[i - 1] = files[i].bytes;
                Resolved = Source.Bytes;
                return;
            }

            Resolved = Primary != null ? Source.Asset : Source.None;
        }

        /// <summary>
        /// Supplies the chain from code, bypassing both inspector routes: for a
        /// harness, or for a game that fetches its fonts at runtime rather than
        /// shipping them. Must be called before the demo builds itself, which
        /// means before the component's object is activated.
        ///
        /// The arrays are held by reference on purpose. Every label is handed
        /// these exact instances, and that identity is what makes them share
        /// one parsed face through <c>SharedFontBytes</c>.
        /// </summary>
        public void UseBytes(byte[] primary, params byte[][] fallbacks)
        {
            if (primary == null || primary.Length == 0) return;
            _primaryBytes = primary;
            _fallbackBytes = fallbacks != null && fallbacks.Length > 0 ? fallbacks : null;
            Resolved = Source.Bytes;
            _prepared = true;
        }

        public void Apply(OneTextLabel label)
        {
            Prepare();
            switch (Resolved)
            {
                case Source.Bytes:
                    label.SetFont(_primaryBytes, _fallbackBytes);
                    break;
                case Source.Asset:
                    label.Font = Primary;
                    break;
            }
        }

        /// <summary>One line for the stats panel.</summary>
        public string Describe()
        {
            Prepare();
            switch (Resolved)
            {
                case Source.Bytes:
                    return ChainLength + " face(s) from bytes";
                case Source.Asset:
                    return "1 asset + project fallbacks";
                default:
                    return "system fonts (no build will have these)";
            }
        }
    }
}
