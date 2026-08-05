using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    /// <summary>
    /// Rebuilds labels when a style asset they reference changes.
    ///
    /// This is what "labels store the reference, not baked values" costs: one
    /// subscription and a list. The payoff is the whole point of named styles —
    /// editing a style updates every label using it, in the editor and at
    /// runtime, and theming becomes a style swap rather than a walk over every
    /// label in every loaded scene.
    ///
    /// It is deliberately the same shape as <see cref="AtlasInvalidation"/>,
    /// including the reset at <c>SubsystemRegistration</c>: with Domain Reload
    /// off, a static list of graphics outlives the play session that filled it,
    /// and the entries are all destroyed objects.
    /// </summary>
    public static class StyleInvalidation
    {
        private static readonly List<IStyleUser> s_users = new List<IStyleUser>();
        private static bool s_subscribed;

        /// <summary>Implemented by anything that draws through a style asset.</summary>
        public interface IStyleUser
        {
            /// <summary>True if this user's appearance depends on that style.</summary>
            bool UsesStyle(OneTextStyle style);

            /// <summary>Called when it changed.</summary>
            void OnStyleChanged();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            s_users.Clear();
            // The subscription is to a static C# event, which survives the
            // session exactly as this class does — so it is not re-taken here,
            // only re-taken after a real domain reload.
        }

        public static void Register(IStyleUser user)
        {
            if (user == null || s_users.Contains(user)) return;
            s_users.Add(user);
            EnsureSubscribed();
        }

        public static void Unregister(IStyleUser user) => s_users.Remove(user);

        private static void EnsureSubscribed()
        {
            if (s_subscribed) return;
            s_subscribed = true;
            OneTextStyle.Changed += OnChanged;
        }

        private static void OnChanged(OneTextStyle style)
        {
            for (int i = s_users.Count - 1; i >= 0; i--)
            {
                var user = s_users[i];
                // Destroyed MonoBehaviours compare false to null through the
                // interface only if the reference is cast back; do that rather
                // than trusting the interface reference itself.
                if (user is Object obj && obj == null)
                {
                    s_users.RemoveAt(i);
                    continue;
                }
                if (user.UsesStyle(style)) user.OnStyleChanged();
            }
        }
    }
}
