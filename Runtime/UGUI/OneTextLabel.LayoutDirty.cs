using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace OneText.UGUI
{
    // Telling uGUI the label's size may have moved, only when something is
    // listening.
    //
    // Every text change calls SetLayoutDirty, and uGUI's answer to that is
    // LayoutRebuilder.MarkLayoutForRebuild: rent a pooled list, ask the parent
    // for ILayoutGroup components, ask the label itself for ILayoutController
    // components, and (in the editor outside play mode) ask the label and every
    // descendant for ILayoutGroup again. For a label with no layout group above
    // it and no fitter on it — a nameplate, a damage number, a HUD counter — all
    // of that concludes there is nothing to rebuild, and it costs about a
    // microsecond, which on fifty retexts a frame was most of the setter.
    //
    // So the label remembers the conclusion. The answer depends only on which
    // components sit on its parent and on itself, which are enabled, and (in the
    // editor) on its descendants, and every way that can change is something
    // the label hears about:
    //
    // - its parent changes: OnTransformParentChanged;
    // - it is enabled again (it was inactive, so nothing it skipped mattered):
    //   OnEnable;
    // - a child is added or removed: OnTransformChildrenChanged;
    // - a layout group or fitter appears or is enabled on the parent or on the
    //   label: every uGUI layout controller marks itself for rebuild when it is
    //   enabled, that rebuild walks the label, and the walk calls the label's
    //   CalculateLayoutInput* — which forgets the conclusion. Until that rebuild
    //   runs, the rebuild is itself pending and will read the label's size as
    //   it is when it runs, text change included.
    //
    // One thing the label cannot hear: someone registering a dirty-layout
    // callback on it (Graphic.RegisterDirtyLayoutCallback is not virtual). The
    // callback field is read on every skip instead, and when it is there, or
    // when this uGUI has no such field to read, the label takes the full path.
    //
    // A "somebody is listening" answer can go stale only toward the safe side:
    // a label that had a layout group above it and lost it keeps taking uGUI's
    // full path, which is what it did before this existed, until its next
    // parent change or enable re-asks.
    public sealed partial class OneTextLabel
    {
        /// <summary>
        /// Skip uGUI's layout-dirty walk when the label knows it would find
        /// nothing to rebuild. A switch so the two can be measured and held
        /// against each other; see <c>LayoutDirtyTests</c>.
        /// </summary>
        public static bool SkipUnheardLayoutDirty = true;

        // Whether the last walk found nobody, and whether that answer is still
        // good. NonSerialized: a fact about the live hierarchy, not the asset.
        [System.NonSerialized] private bool _layoutHearingKnown;
        [System.NonSerialized] private bool _layoutUnheard;
#if UNITY_EDITOR
        [System.NonSerialized] private bool _layoutHearingPlaying;
#endif

        private static readonly FieldInfo s_dirtyLayoutCallback =
            typeof(Graphic).GetField("m_OnDirtyLayoutCallback", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly List<Component> s_layoutComponents = new List<Component>();
        private static readonly List<ILayoutGroup> s_layoutGroups = new List<ILayoutGroup>();

        /// <summary>For the tests: how many times a label actually asked its hierarchy.</summary>
        internal static int LayoutHearingChecks { get; private set; }

        public override void SetLayoutDirty()
        {
            if (SkipUnheardLayoutDirty && IsActive() && NobodyListensToLayout()) return;
            base.SetLayoutDirty();
        }

        /// <summary>Forget the cached answer; the next dirty asks the hierarchy again.</summary>
        private void ForgetLayoutHearing() => _layoutHearingKnown = false;

        /// <summary>
        /// A layout rebuild walked this label. If the label had concluded that
        /// nobody listens, that is now in doubt; if it had concluded somebody
        /// does, this is the same somebody, and the answer stands — a label in
        /// a layout group is walked on every pass its own text change causes,
        /// and re-asking each time would make it pay the walk twice.
        /// </summary>
        private void LayoutControllerSeen()
        {
            if (_layoutUnheard) _layoutHearingKnown = false;
        }

        private bool NobodyListensToLayout()
        {
#if UNITY_EDITOR
            // The editor walks descendants only outside play mode, so the
            // answer is per mode.
            if (_layoutHearingKnown && _layoutHearingPlaying != UnityEditor.EditorApplication.isPlaying)
                _layoutHearingKnown = false;
#endif
            if (!_layoutHearingKnown)
            {
                _layoutUnheard = WalkFindsNoListener();
                _layoutHearingKnown = true;
#if UNITY_EDITOR
                _layoutHearingPlaying = UnityEditor.EditorApplication.isPlaying;
#endif
            }
            if (!_layoutUnheard) return false;
            // The one listener with no notification of its own.
            return s_dirtyLayoutCallback != null && s_dirtyLayoutCallback.GetValue(this) == null;
        }

        /// <summary>
        /// The questions LayoutRebuilder.MarkLayoutForRebuild asks, answering
        /// "would it rebuild anything". Mirrors uGUI's own tests exactly:
        /// a component counts if it is a Behaviour that is active and enabled.
        /// </summary>
        private bool WalkFindsNoListener()
        {
            LayoutHearingChecks++;
            var parent = transform.parent as RectTransform;
            if (parent != null)
            {
                parent.GetComponents(typeof(ILayoutGroup), s_layoutComponents);
                if (AnyActive(s_layoutComponents)) return false;
            }

            GetComponents(typeof(ILayoutController), s_layoutComponents);
            if (AnyActive(s_layoutComponents)) return false;

#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying)
            {
                // Outside play mode uGUI also marks every enabled layout group
                // below the root it found (UUM-100091), and with nothing above
                // the label that root is the label.
                GetComponentsInChildren(s_layoutGroups);
                for (int i = 0; i < s_layoutGroups.Count; i++)
                {
                    if (s_layoutGroups[i] is Behaviour behaviour && behaviour.isActiveAndEnabled)
                    {
                        s_layoutGroups.Clear();
                        return false;
                    }
                }
                s_layoutGroups.Clear();
            }
#endif
            s_layoutComponents.Clear();
            return true;
        }

        private static bool AnyActive(List<Component> components)
        {
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] != null && components[i] is Behaviour behaviour && behaviour.isActiveAndEnabled)
                {
                    components.Clear();
                    return true;
                }
            }
            return false;
        }

        // A child joining or leaving can bring a layout group with it, which
        // the editor's walk would have found.
        private void OnTransformChildrenChanged() => ForgetLayoutHearing();
    }
}
