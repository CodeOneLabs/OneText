using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// The window's chrome: a sidebar of sections, a header that says what the
    /// open one is for, and the panel itself.
    ///
    /// It is a class rather than code inside the window because the same tree
    /// has to be buildable without a window; the tests build every section
    /// this way, and so does the screenshot pass.
    /// </summary>
    public sealed class HubShell
    {
        private readonly OneTextHub _hub;
        private readonly ScrollView _nav;
        private readonly ScrollView _content;
        private readonly Label _eyebrow;
        private readonly Label _title;
        private readonly Label _lede;
        private readonly VisualElement _toast;
        private readonly Label _toastText;

        private readonly Dictionary<HubSection, Button> _navItems =
            new Dictionary<HubSection, Button>();
        private readonly Dictionary<HubSection, VisualElement> _navBadges =
            new Dictionary<HubSection, VisualElement>();

        public VisualElement Root { get; }

        public HubSection Current { get; private set; }

        public HubShell(OneTextHub hub)
        {
            _hub = hub;

            var tree = HubUI.LoadTree("OneTextHub");
            var host = new VisualElement();
            if (tree != null) tree.CloneTree(host);
            Root = host.Q("hub-root");
            if (Root == null)
            {
                // No UXML: the window still works, it just has no shell to
                // hang the sections in. Better than an empty grey rectangle.
                Root = new VisualElement { name = "hub-root" };
                Root.AddToClassList("hub-root");
                Root.Add(BuildFallbackShell(out _nav, out _content, out _eyebrow,
                    out _title, out _lede, out _toast, out _toastText));
            }
            else
            {
                Root.RemoveFromHierarchy();
                _nav = Root.Q<ScrollView>("nav");
                _content = Root.Q<ScrollView>("content");
                _eyebrow = Root.Q<Label>("eyebrow");
                _title = Root.Q<Label>("title");
                _lede = Root.Q<Label>("lede");
                _toast = Root.Q("toast");
                _toastText = Root.Q<Label>("toast-text");

                var settings = Root.Q<Button>("settings-button");
                if (settings != null)
                    settings.clicked += () => SettingsService.OpenProjectSettings("Project/OneText");

                var version = Root.Q<Label>("version");
                if (version != null) version.text = PackageVersion();
            }

            var style = HubUI.LoadStyle("OneTextHub");
            if (style != null) Root.styleSheets.Add(style);

            BuildNav();
        }

        /// <summary>Shows one section, building its tree the first time it is asked for.</summary>
        public void Select(HubSection section)
        {
            if (section == null) return;
            Current = section;

            foreach (var pair in _navItems)
                pair.Value.EnableInClassList("nav-item--on", pair.Key == section);

            if (_eyebrow != null) _eyebrow.text = section.Eyebrow.ToUpperInvariant();
            if (_title != null) _title.text = section.Title;
            if (_lede != null) _lede.text = section.Lede;

            var panel = section.Build(_hub);
            if (_content != null)
            {
                _content.Clear();
                _content.Add(panel);
                _content.scrollOffset = Vector2.zero;
            }
            section.OnShow();
            RefreshNav();
        }

        /// <summary>Re-reads every section's status pill.</summary>
        public void RefreshNav()
        {
            foreach (var pair in _navBadges)
            {
                var section = pair.Key;
                var host = pair.Value;
                host.Clear();
                string text = section.BadgeText;
                if (string.IsNullOrEmpty(text)) continue;
                host.Add(HubUI.Badge(text, section.BadgeTone));
            }
        }

        /// <summary>
        /// Says what an action just did, next to where it was clicked.
        ///
        /// Every button in this window changes something on disk or in the
        /// project, and half of them used to say so only in the console.
        /// </summary>
        public void Notify(string message, bool bad = false)
        {
            if (_toast == null || _toastText == null) return;
            _toastText.text = message;
            _toast.EnableInClassList("toast--bad", bad);
            _toast.AddToClassList("toast--on");
            _toast.schedule.Execute(() => _toast.RemoveFromClassList("toast--on"))
                .StartingIn(bad ? 6000 : 3600);
        }

        // ---------------------------------------------------------------- nav

        private void BuildNav()
        {
            if (_nav == null) return;
            _nav.Clear();
            _navItems.Clear();
            _navBadges.Clear();

            string group = null;
            foreach (var section in _hub.Sections)
            {
                if (section.NavGroup != group)
                {
                    group = section.NavGroup;
                    if (!string.IsNullOrEmpty(group))
                        _nav.Add(HubUI.Text(group.ToUpperInvariant(), "nav__group"));
                }

                var captured = section;
                var item = new Button(() => Select(captured)) { text = string.Empty };
                item.AddToClassList("nav-item");

                var text = HubUI.Box("nav-item__text");
                text.Add(HubUI.Text(section.Title, "nav-item__label"));
                if (!string.IsNullOrEmpty(section.NavHint))
                    text.Add(HubUI.Text(section.NavHint, "nav-item__hint"));
                item.Add(text);

                var badge = new VisualElement();
                badge.style.flexShrink = 0f;
                item.Add(badge);

                _nav.Add(item);
                _navItems[section] = item;
                _navBadges[section] = badge;
            }
            RefreshNav();
        }

        private static string PackageVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(HubShell).Assembly);
                return info != null ? $"v{info.version}" : "MIT · OPEN SOURCE";
            }
            catch (System.Exception)
            {
                return "MIT · OPEN SOURCE";
            }
        }

        private static VisualElement BuildFallbackShell(out ScrollView nav, out ScrollView content,
            out Label eyebrow, out Label title, out Label lede,
            out VisualElement toast, out Label toastText)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1f;

            var sidebar = HubUI.Box("sidebar");
            nav = new ScrollView();
            nav.AddToClassList("nav");
            sidebar.Add(nav);

            var main = HubUI.Box("main");
            var header = HubUI.Box("page-header");
            eyebrow = HubUI.Text("", "eyebrow");
            title = HubUI.Text("", "page-title");
            lede = HubUI.Text("", "lede");
            header.Add(eyebrow);
            header.Add(title);
            header.Add(lede);
            main.Add(header);
            content = new ScrollView();
            content.AddToClassList("content");
            main.Add(content);
            toast = HubUI.Box("toast");
            toastText = HubUI.Text("", "toast__text");
            toast.Add(toastText);
            main.Add(toast);

            row.Add(sidebar);
            row.Add(main);
            return row;
        }
    }
}
