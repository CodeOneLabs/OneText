using System;

namespace OneText.UGUI
{
    /// <summary>
    /// The one place to hear that some label, anywhere, has been laid out again.
    ///
    /// Per-label events cover the case where you know which label you care
    /// about. This covers the other one: a component that is handed a label at
    /// runtime and has to keep something in step with it — a per-character
    /// animator holding cached vertex positions, a layout watcher, an effect
    /// driver — and does not own the label or its lifetime.
    ///
    /// It exists in this shape because that is the shape the code arriving from
    /// TextMesh Pro is written in. <c>TMPro_EventManager.TEXT_CHANGED_EVENT</c>
    /// is a global any component may subscribe to, and DOTweenPro's text
    /// animator is one of the components that does; a per-label event would have
    /// meant every one of those call sites being rewritten by hand, which is
    /// exactly the manual work this package exists to remove.
    ///
    /// Subscribers are called after the layout has been rebuilt and before the
    /// mesh is emitted, so reading <c>textInfo</c> from inside one gives the new
    /// text. Unsubscribe in OnDisable or OnDestroy: this is a static event and
    /// it will hold a dead component alive otherwise.
    /// </summary>
    public static class OneTextEvents
    {
        /// <summary>
        /// Raised when a label has re-laid its text out. The argument is the
        /// label it happened to.
        /// </summary>
        public static event Action<OneTextLabel> TextChanged;

        /// <summary>
        /// The same event, in the shape TextMesh Pro's callers subscribe to:
        /// <c>TEXT_CHANGED_EVENT.Add(handler)</c> and <c>.Remove(handler)</c>,
        /// with a handler taking a plain <c>Object</c>.
        ///
        /// Kept because renaming the type is not enough on its own — a call
        /// written against <c>TMPro_EventManager</c> is a call to
        /// <c>.Add(…)</c>, and a rename onto an ordinary C# event would leave
        /// code that does not compile, which is the one outcome the rewrite is
        /// not allowed to produce.
        /// </summary>
        public static readonly Subscription TEXT_CHANGED_EVENT = new Subscription();

        /// <summary>Add/Remove over the same subscriber list, for the callers that expect it.</summary>
        public sealed class Subscription
        {
            internal Action<UnityEngine.Object> Handlers;

            public void Add(Action<UnityEngine.Object> handler) => Handlers += handler;

            public void Remove(Action<UnityEngine.Object> handler) => Handlers -= handler;
        }

        internal static void RaiseTextChanged(OneTextLabel label)
        {
            var subscribers = TEXT_CHANGED_EVENT.Handlers;
            if (subscribers != null)
            {
                foreach (var each in subscribers.GetInvocationList())
                {
                    try { ((Action<UnityEngine.Object>)each)(label); }
                    catch (Exception error) { UnityEngine.Debug.LogException(error); }
                }
            }

            var handler = TextChanged;
            if (handler == null) return;

            // One subscriber throwing must not stop the rest being told, and
            // must not take the label's own rebuild down with it: this is a
            // notification, not part of laying text out.
            foreach (var each in handler.GetInvocationList())
            {
                try { ((Action<OneTextLabel>)each)(label); }
                catch (Exception error) { UnityEngine.Debug.LogException(error); }
            }
        }
    }
}
