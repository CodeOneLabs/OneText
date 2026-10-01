using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// Lays labels out again when <see cref="FontResidency"/> loads or lets go
    /// of a font.
    ///
    /// <para>Both directions need it. A label that drew a box, or a face the
    /// device lent it, for want of a font has to hear that the font arrived:
    /// nothing about the label itself changed, so nothing else would dirty it.
    /// And a label drawing with a font that is leaving has to lay out without
    /// it, or take it back, before the sweep a frame later destroys the face —
    /// the two-step unload is only safe because this step happens in
    /// between.</para>
    ///
    /// <para>The notification arrives from the residency's own frame tick,
    /// after the frame's scripts and outside any canvas rebuild, so dirtying
    /// from here never trips uGUI's "already inside a graphic rebuild loop"
    /// guard. Same shape as <see cref="StyleInvalidation"/>, including the
    /// reset for a play session that starts without a domain reload.</para>
    /// </summary>
    public static class FontInvalidation
    {
        private static readonly HashSet<Graphic> s_users = new HashSet<Graphic>();
        private static readonly List<Graphic> s_scratch = new List<Graphic>();
        private static bool s_subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession() => s_users.Clear();

        public static void Register(Graphic graphic)
        {
            if (graphic == null) return;
            s_users.Add(graphic);
            if (s_subscribed) return;
            s_subscribed = true;
            FontResidency.Changed += OnFontsChanged;
        }

        public static void Unregister(Graphic graphic) => s_users.Remove(graphic);

        private static void OnFontsChanged()
        {
            s_scratch.Clear();
            s_scratch.AddRange(s_users);
            foreach (var graphic in s_scratch)
            {
                if (graphic == null)
                {
                    s_users.Remove(graphic);
                    continue;
                }
                graphic.SetAllDirty();
            }
            s_scratch.Clear();
        }
    }
}
