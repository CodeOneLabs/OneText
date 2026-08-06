using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>How a piece of the Hub is feeling about itself.</summary>
    public enum HubTone
    {
        Neutral,
        Good,
        Warn,
        Bad,
        Info,
    }

    /// <summary>
    /// The Hub's vocabulary of parts: cards, notices, tiles, pills, pickers.
    ///
    /// Everything here is built from the runtime half of UI Toolkit (Label,
    /// Button, TextField, Slider) and never from the editor's own controls.
    /// That is not purism. An ObjectField drags the editor's chrome into a
    /// window whose whole point is not to look like one, and a tree made only
    /// of runtime elements is also a tree that can be rendered to a texture
    /// outside an editor window, which is how this thing is screenshotted and
    /// how the tests build every section without a GUI skin.
    /// </summary>
    public static class HubUI
    {
        private const string UiFolder = "Packages/com.onetext.core/Editor/Hub/UI/";

        // ------------------------------------------------------------ assets

        /// <summary>The shell and card layouts, by file name without extension.</summary>
        public static VisualTreeAsset LoadTree(string name) =>
            Load<VisualTreeAsset>(name, "uxml");

        /// <summary>The stylesheet.</summary>
        public static StyleSheet LoadStyle(string name) =>
            Load<StyleSheet>(name, "uss");

        private static T Load<T>(string name, string extension) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>($"{UiFolder}{name}.{extension}");
            if (asset != null) return asset;

            // A project that copied the package under Assets rather than
            // referencing it still has the files, somewhere.
            foreach (string guid in AssetDatabase.FindAssets($"{name} t:{typeof(T).Name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith($"/{name}.{extension}", StringComparison.Ordinal)) continue;
                var found = AssetDatabase.LoadAssetAtPath<T>(path);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Marks an element as data rather than prose: a number, a path, a
        /// glyph id.
        ///
        /// The site sets these in SF Mono. This does not, and the reason is
        /// worth writing down: a dynamic OS font handed to UI Toolkit through
        /// FontDefinition.FromFont renders from a font atlas the panel rebuilds
        /// on its own schedule, and every label wearing it came out drawn with
        /// another label's glyphs. Tracking, size and colour carry the same
        /// distinction without betting the readability of every number in the
        /// window on that path.
        /// </summary>
        public static T Mono<T>(T element) where T : VisualElement
        {
            element.AddToClassList("mono");
            return element;
        }

        // ------------------------------------------------------------- cards

        /// <summary>
        /// One card: a title, a line under it saying what the card is for, the
        /// actions it offers, and a body.
        /// </summary>
        public sealed class Card
        {
            public VisualElement Root { get; internal set; }
            public VisualElement Body { get; internal set; }
            public VisualElement Actions { get; internal set; }
            public Label TitleLabel { get; internal set; }
            public Label NoteLabel { get; internal set; }

            public Card Add(VisualElement element)
            {
                Body.Add(element);
                return this;
            }

            public Card Act(Button button)
            {
                Actions.Add(button);
                return this;
            }

            /// <summary>No padding in the body, for cards that are a list.</summary>
            public Card Flush()
            {
                Root.AddToClassList("card--flush");
                return this;
            }
        }

        private static VisualTreeAsset _cardTree;
        private static bool _cardTreeResolved;

        public static Card MakeCard(string title, string note = null)
        {
            if (!_cardTreeResolved)
            {
                _cardTree = LoadTree("HubCard");
                _cardTreeResolved = true;
            }

            var card = new Card();
            if (_cardTree != null)
            {
                var host = new VisualElement();
                _cardTree.CloneTree(host);
                card.Root = host.Q("card") ?? host;
                card.Body = card.Root.Q("card-body");
                card.Actions = card.Root.Q("card-actions");
                card.TitleLabel = card.Root.Q<Label>("card-title");
                card.NoteLabel = card.Root.Q<Label>("card-note");
                card.Root.RemoveFromHierarchy();
            }

            if (card.Root == null || card.Body == null) BuildCardByHand(card);

            card.TitleLabel.text = title ?? string.Empty;
            if (string.IsNullOrEmpty(note)) card.NoteLabel.style.display = DisplayStyle.None;
            else card.NoteLabel.text = note;
            return card;
        }

        private static void BuildCardByHand(Card card)
        {
            var root = new VisualElement();
            root.AddToClassList("card");
            var head = new VisualElement();
            head.AddToClassList("card__head");
            var heading = new VisualElement();
            heading.AddToClassList("card__heading");
            var title = new Label { name = "card-title" };
            title.AddToClassList("card__title");
            var note = new Label { name = "card-note" };
            note.AddToClassList("card__note");
            heading.Add(title);
            heading.Add(note);
            var actions = new VisualElement { name = "card-actions" };
            actions.AddToClassList("card__actions");
            head.Add(heading);
            head.Add(actions);
            var body = new VisualElement { name = "card-body" };
            body.AddToClassList("card__body");
            root.Add(head);
            root.Add(body);

            card.Root = root;
            card.Body = body;
            card.Actions = actions;
            card.TitleLabel = title;
            card.NoteLabel = note;
        }

        // ------------------------------------------------------------- parts

        public static Label Text(string text, params string[] classes)
        {
            var label = new Label(text ?? string.Empty);
            foreach (string name in classes) label.AddToClassList(name);
            return label;
        }

        public static VisualElement Box(params string[] classes)
        {
            var element = new VisualElement();
            foreach (string name in classes) element.AddToClassList(name);
            return element;
        }

        /// <summary>A labelled control, with an optional line of help under it.</summary>
        public static VisualElement Field(string label, VisualElement control, string hint = null)
        {
            var field = Box("field");
            field.Add(Text(label, "field__label"));
            var host = Box("field__control");
            host.Add(control);
            field.Add(host);
            if (string.IsNullOrEmpty(hint)) return field;

            var wrap = new VisualElement();
            wrap.Add(field);
            var help = Text(hint, "hint");
            help.style.marginLeft = 128f;
            help.style.marginTop = -4f;
            help.style.marginBottom = 8f;
            wrap.Add(help);
            return wrap;
        }

        /// <summary>A row of a table that is not a table.</summary>
        public static VisualElement KeyValue(string key, string value, HubTone tone = HubTone.Neutral)
        {
            var row = Box("kv");
            row.Add(Text(key, "kv__key"));
            var text = Text(value, "kv__value");
            switch (tone)
            {
                case HubTone.Good: text.AddToClassList("kv__value--good"); break;
                case HubTone.Warn: text.AddToClassList("kv__value--warn"); break;
                case HubTone.Bad: text.AddToClassList("kv__value--bad"); break;
            }
            Mono(text);
            row.Add(text);
            return row;
        }

        public static Button Primary(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn--primary");
            return button;
        }

        public static Button Ghost(string text, Action onClick) => new Button(onClick) { text = text };

        public static Button Quiet(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn--quiet");
            button.AddToClassList("btn--tiny");
            return button;
        }

        public static Button Danger(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn--danger");
            return button;
        }

        /// <summary>An on/off control drawn as a pill, so its state is one glance.</summary>
        public static Button Pill(string text, bool on, Action<bool> onChange)
        {
            Button button = null;
            bool state = on;
            button = new Button(() =>
            {
                state = !state;
                button.EnableInClassList("pill--on", state);
                onChange?.Invoke(state);
            })
            { text = text };
            button.AddToClassList("pill");
            button.EnableInClassList("pill--on", state);
            return button;
        }

        /// <summary>A row of mutually exclusive choices: the site's tab strip.</summary>
        public static VisualElement Segments(string[] labels, int selected, Action<int> onChange)
        {
            var row = Box("segments");
            var buttons = new List<Button>();
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var button = new Button(() =>
                {
                    for (int b = 0; b < buttons.Count; b++)
                        buttons[b].EnableInClassList("segment--on", b == index);
                    onChange?.Invoke(index);
                })
                { text = labels[i] };
                button.AddToClassList("segment");
                button.EnableInClassList("segment--on", i == selected);
                buttons.Add(button);
                row.Add(button);
            }
            return row;
        }

        public static Label Badge(string text, HubTone tone = HubTone.Neutral)
        {
            var label = Text(text, "badge");
            switch (tone)
            {
                case HubTone.Good: label.AddToClassList("badge--good"); break;
                case HubTone.Warn: label.AddToClassList("badge--warn"); break;
                case HubTone.Bad: label.AddToClassList("badge--bad"); break;
                case HubTone.Info: label.AddToClassList("badge--info"); break;
            }
            return label;
        }

        public static VisualElement Notice(string text, HubTone tone = HubTone.Neutral)
        {
            var notice = Box("notice");
            switch (tone)
            {
                case HubTone.Good: notice.AddToClassList("notice--good"); break;
                case HubTone.Warn: notice.AddToClassList("notice--warn"); break;
                case HubTone.Bad: notice.AddToClassList("notice--bad"); break;
            }
            notice.Add(Text(text, "notice__text"));
            return notice;
        }

        /// <summary>
        /// What to do when there is nothing here yet.
        ///
        /// The state every project is in on the day it installs the package, and
        /// the one an editor window usually answers with a blank panel. It gets
        /// the first step instead, with the button that takes it.
        /// </summary>
        public static VisualElement Empty(string title, string body,
            string actionLabel = null, Action action = null, string glyph = "◇")
        {
            var empty = Box("empty");
            empty.Add(Text(glyph, "empty__glyph"));
            empty.Add(Text(title, "empty__title"));
            empty.Add(Text(body, "empty__body"));
            if (!string.IsNullOrEmpty(actionLabel) && action != null)
                empty.Add(Primary(actionLabel, action));
            return empty;
        }

        public static VisualElement Tile(string label, string value, string note,
            HubTone tone = HubTone.Neutral, Action onClick = null)
        {
            var tile = Box("tile");
            tile.Add(Text(label.ToUpperInvariant(), "tile__label"));
            var number = Text(value, "tile__value");
            switch (tone)
            {
                case HubTone.Warn: number.AddToClassList("tile__value--warn"); break;
                case HubTone.Bad: number.AddToClassList("tile__value--bad"); break;
                case HubTone.Neutral: number.AddToClassList("tile__value--idle"); break;
            }
            tile.Add(number);
            if (!string.IsNullOrEmpty(note)) tile.Add(Text(note, "tile__note"));
            if (onClick != null)
                tile.RegisterCallback<ClickEvent>(_ => onClick());
            return tile;
        }

        public static VisualElement Meter(float fraction, HubTone tone = HubTone.Good)
        {
            var meter = Box("meter");
            var fill = Box("meter__fill");
            switch (tone)
            {
                case HubTone.Warn: fill.AddToClassList("meter__fill--warn"); break;
                case HubTone.Bad: fill.AddToClassList("meter__fill--bad"); break;
            }
            fill.style.width = new Length(Mathf.Clamp01(fraction) * 100f, LengthUnit.Percent);
            meter.Add(fill);
            return meter;
        }

        /// <summary>
        /// A knob that is not on the first screen. Everything a first run does
        /// not need is behind one of these, closed.
        /// </summary>
        public static VisualElement Disclose(string title, VisualElement body, bool open = false)
        {
            var wrap = new VisualElement();
            var arrow = Text(open ? "▼" : "▶", "disclose__arrow");
            var label = Text(title.ToUpperInvariant(), "disclose__title");
            bool state = open;

            var head = new Button(() =>
            {
                state = !state;
                arrow.text = state ? "▼" : "▶";
                body.style.display = state ? DisplayStyle.Flex : DisplayStyle.None;
            })
            { text = string.Empty };
            head.AddToClassList("disclose__head");
            head.Add(arrow);
            head.Add(label);

            body.AddToClassList("disclose__body");
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

            wrap.Add(head);
            wrap.Add(body);
            return wrap;
        }

        public static TextField Input(string value, string placeholder = null,
            Action<string> onChange = null)
        {
            var field = new TextField { value = value ?? string.Empty };
            field.isDelayed = false;
            if (onChange != null)
                field.RegisterValueChangedCallback(evt => onChange(evt.newValue));
            if (!string.IsNullOrEmpty(placeholder)) field.tooltip = placeholder;
            return field;
        }

        /// <summary>A slider with a number beside it, both in the house style.</summary>
        public static VisualElement Knob(float value, float min, float max, string format,
            Action<float> onChange)
        {
            var slider = new Slider(min, max) { value = value };
            slider.AddToClassList("knob");
            var readout = Mono(Text(value.ToString(format), "kv__value"));
            readout.style.width = 56f;
            readout.style.marginLeft = 12f;
            readout.style.flexShrink = 0f;
            slider.RegisterValueChangedCallback(evt =>
            {
                readout.text = evt.newValue.ToString(format);
                onChange?.Invoke(evt.newValue);
            });
            var row = Box("row");
            row.Add(slider);
            row.Add(readout);
            return row;
        }

        // ------------------------------------------------------- asset picker

        /// <summary>
        /// The object field, rebuilt out of a button and a menu.
        ///
        /// A menu of the project's assets of that type is a better answer than
        /// a slot to drag into anyway: a project has three charsets, not three
        /// hundred, and picking from a list is one click where dragging is a
        /// hunt through the Project window. Dropping still works.
        /// </summary>
        public static Button AssetPicker<T>(Func<T> get, Action<T> set, string emptyText,
            string createLabel = null, Func<T> create = null) where T : UnityEngine.Object
        {
            Button button = null;
            var name = Text(string.Empty, "picker__name");
            var caret = Text("▾", "picker__caret");

            void Refresh()
            {
                var current = get();
                name.text = current != null ? current.name : emptyText;
                button.EnableInClassList("picker--empty", current == null);
            }

            button = new Button(() =>
            {
                var menu = new GenericMenu();
                var current = get();
                menu.AddItem(new GUIContent("(none)"), current == null, () =>
                {
                    set(null);
                    Refresh();
                });

                var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
                if (guids.Length > 0) menu.AddSeparator("");
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                    if (asset == null) continue;
                    var captured = asset;
                    menu.AddItem(new GUIContent(asset.name), captured == current, () =>
                    {
                        set(captured);
                        Refresh();
                    });
                }

                if (createLabel != null && create != null)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent(createLabel), false, () =>
                    {
                        var made = create();
                        if (made != null) set(made);
                        Refresh();
                    });
                }

                menu.DropDown(button.worldBound);
            })
            { text = string.Empty };

            button.AddToClassList("picker");
            button.Add(name);
            button.Add(caret);
            Refresh();

            button.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                if (Dragged<T>() != null)
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            });
            button.RegisterCallback<DragPerformEvent>(_ =>
            {
                var dragged = Dragged<T>();
                if (dragged == null) return;
                DragAndDrop.AcceptDrag();
                set(dragged);
                Refresh();
            });
            return button;
        }

        private static T Dragged<T>() where T : UnityEngine.Object
        {
            foreach (var reference in DragAndDrop.objectReferences)
                if (reference is T typed) return typed;
            return null;
        }

        // ------------------------------------------------------------- misc

        /// <summary>Nothing irreversible happens without this.</summary>
        public static bool Confirm(string title, string question, string yes) =>
            EditorUtility.DisplayDialog(title, question, yes, "Cancel");

        /// <summary>
        /// A fraction as a percentage, with no space before the sign.
        ///
        /// .NET's "P" format puts one there, which is correct for several
        /// locales and wrong for every screen in this window; "1 %" reads as
        /// two things.
        /// </summary>
        public static string Percent(float fraction, int decimals = 0) =>
            (fraction * 100f).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals)) + "%";
    }
}
