using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneText.Editor
{
    /// <summary>
    /// The screen the Hub opens on: what this project has, and the next thing
    /// to do about it.
    ///
    /// Every other section answers a question you have to know to ask. This one
    /// is for the five minutes before that: a project that has just installed
    /// the package has no font asset, no default, no idea where its strings
    /// are, and the fastest way through that is a checklist that ticks itself
    /// off as the work gets done.
    /// </summary>
    public sealed class HubOverviewTab : HubSection
    {
        public override OneTextHub.Tab Tab => OneTextHub.Tab.Overview;

        public override string Title => "Overview";

        public override string Eyebrow => "Start here";

        public override string Lede =>
            "What this project has, and what to do next. Every number here is a link " +
            "to the section that owns it.";

        public override string NavHint => "project at a glance";

        protected override void Compose(VisualElement content)
        {
            content.Add(Tiles());
            content.Add(Steps());
            content.Add(Map());
        }

        // --------------------------------------------------------------- tiles

        private VisualElement Tiles()
        {
            var settings = OneTextSettings.Instance;
            var tiles = HubUI.Box("tiles");

            int fonts = AllFonts().Count;
            tiles.Add(HubUI.Tile("Fonts", fonts.ToString("n0"),
                settings != null && settings.DefaultFont != null
                    ? $"default {settings.DefaultFont.FamilyName}"
                    : "no project default",
                fonts > 0 ? HubTone.Good : HubTone.Neutral,
                () => Hub.Go(OneTextHub.Tab.Fonts)));

            int styles = AllStyles().Count;
            tiles.Add(HubUI.Tile("Styles", styles.ToString("n0"),
                styles == 0 ? "none yet" : "named, reusable",
                styles > 0 ? HubTone.Good : HubTone.Neutral,
                () => Hub.Go(OneTextHub.Tab.Styles)));

            int charsets = AssetDatabase.FindAssets($"t:{nameof(OneTextCharset)}").Length;
            tiles.Add(HubUI.Tile("Charsets", charsets.ToString("n0"),
                settings != null && settings.PrewarmCharset != null
                    ? "one is prewarmed"
                    : "nothing prewarmed",
                charsets > 0 ? HubTone.Good : HubTone.Neutral,
                () => Hub.Go(OneTextHub.Tab.Charsets)));

            int folders = Hub.StringFolders.Count;
            tiles.Add(HubUI.Tile("String folders", folders.ToString("n0"),
                folders == 0 ? "Doctor needs these" : "scanned on demand",
                folders > 0 ? HubTone.Good : HubTone.Warn,
                () => Hub.Go(OneTextHub.Tab.Doctor)));

            tiles.Add(AtlasTile());
            tiles.Add(DoctorTile());
            return tiles;
        }

        private VisualElement AtlasTile()
        {
            if (!SharedGlyphAtlas.Exists && !SharedGlyphAtlas.PreciseAtlasExists)
                return HubUI.Tile("Atlas", "-", "not created yet", HubTone.Neutral,
                    () => Hub.Go(OneTextHub.Tab.Atlas));

            var stats = SharedGlyphAtlas.Exists
                ? SharedGlyphAtlas.Atlas.GetStats()
                : SharedGlyphAtlas.PreciseAtlas.GetStats();
            var tone = stats.Drops > 0 ? HubTone.Bad
                : stats.UsedFraction > 0.85f ? HubTone.Warn
                : HubTone.Good;
            return HubUI.Tile("Atlas", HubUI.Percent(stats.UsedFraction),
                $"{stats.TileCount:n0} tiles" + (stats.Drops > 0 ? ", dropping" : ""),
                tone, () => Hub.Go(OneTextHub.Tab.Atlas));
        }

        private VisualElement DoctorTile()
        {
            var doctor = Hub.Find(OneTextHub.Tab.Doctor) as HubDoctorTab;
            var report = doctor?.LastReport;
            if (report == null)
                return HubUI.Tile("Doctor", "-", "not run yet", HubTone.Neutral,
                    () => Hub.Go(OneTextHub.Tab.Doctor));

            int errors = 0;
            foreach (var finding in report.Findings)
                if (finding.Severity == DoctorSeverity.Error) errors++;
            return HubUI.Tile("Doctor", report.Passed ? "PASS" : errors.ToString("n0"),
                report.Passed ? "every string renders" : "cannot render",
                report.Passed ? HubTone.Good : HubTone.Bad,
                () => Hub.Go(OneTextHub.Tab.Doctor));
        }

        // --------------------------------------------------------------- steps

        private VisualElement Steps()
        {
            var settings = OneTextSettings.Instance;
            var card = HubUI.MakeCard("First steps",
                "Each of these is done once per project. They tick themselves off.").Flush();

            var fonts = AllFonts();
            card.Add(Step(fonts.Count > 0, "Import a font",
                "OneText reads .ttf and .otf itself, but Unity owns those extensions, so a font " +
                "asset is the one step between dropping a font in and drawing with it.",
                "Choose a font file…", ImportFont));

            card.Add(Step(settings != null && settings.DefaultFont != null,
                "Set the project's default font",
                "Every label with no font of its own gets this one, plus the fallback chain " +
                "under it. Without it, a new label draws nothing.",
                "Project settings", () => SettingsService.OpenProjectSettings("Project/OneText")));

            card.Add(Step(Hub.StringFolders.Count > 0, "Point OneText at your strings",
                "The folders your localisation tables and dialogue live in. Doctor, the gallery " +
                "and the coverage numbers all read them.",
                "Choose a folder…", () =>
                {
                    string picked = EditorUtility.OpenFolderPanel("Folder of strings", "Assets", "");
                    if (string.IsNullOrEmpty(picked)) return;
                    Hub.StringFolders.Add(TextSourceScanner.ToProjectPath(picked));
                    Refresh();
                }));

            var doctor = Hub.Find(OneTextHub.Tab.Doctor) as HubDoctorTab;
            card.Add(Step(doctor?.LastReport != null, "Check that every string renders",
                "Characters no font in the chain can draw, Japanese that will come out in " +
                "Chinese shapes, a locale whose line breaking needs a dictionary nobody installed.",
                "Open Doctor", () => Hub.Go(OneTextHub.Tab.Doctor)));

            card.Add(Step(settings != null && settings.PrewarmCharset != null,
                "Pre-bake the characters you already know about",
                "A charset rasterised at startup is a first frame that does not hitch when the " +
                "first line of dialogue appears.",
                "Open charsets", () => Hub.Go(OneTextHub.Tab.Charsets)));

            return card.Root;
        }

        private static VisualElement Step(bool done, string title, string why,
            string action, System.Action onAction)
        {
            var step = HubUI.Box("step");
            if (done) step.AddToClassList("step--done");

            var tick = HubUI.Text(done ? "✓" : "○", "step__tick");
            if (!done) tick.AddToClassList("step__tick--todo");
            step.Add(tick);

            var text = HubUI.Box("step__text");
            text.Add(HubUI.Text(title, "step__title"));
            text.Add(HubUI.Text(why, "step__why"));
            step.Add(text);

            step.Add(done
                ? HubUI.Quiet(action, () => onAction())
                : HubUI.Primary(action, () => onAction()));
            return step;
        }

        private void ImportFont()
        {
            string picked = EditorUtility.OpenFilePanel(
                "A .ttf, .otf or .ttc inside this project", "Assets", "ttf,otf,ttc");
            if (string.IsNullOrEmpty(picked)) return;

            string relative = TextSourceScanner.ToProjectPath(picked);
            if (!relative.StartsWith("Assets", System.StringComparison.Ordinal))
            {
                SayBadly($"{Path.GetFileName(picked)} is outside this project. " +
                    "Copy it under Assets first; the font asset is stored beside it.");
                return;
            }

            var asset = OneFontAssetCreator.CreateFromFontFile(relative);
            if (asset == null)
            {
                SayBadly($"Could not read {Path.GetFileName(picked)}.");
                return;
            }

            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            Refresh();
            Say($"Created a font asset for {asset.FamilyName}. " +
                "Set it as the project default next.");
        }

        // ----------------------------------------------------------------- map

        private VisualElement Map()
        {
            var card = HubUI.MakeCard("Sections",
                "What the rest of this window is for: nine screens, one question each.").Flush();
            foreach (var section in Hub.Sections)
            {
                if (section.Tab == OneTextHub.Tab.Overview) continue;
                var captured = section;
                var row = HubUI.Box("kv");
                var key = HubUI.Text(section.Title, "kv__key");
                key.style.color = new Color(0.494f, 0.906f, 0.706f);
                row.Add(key);
                row.Add(HubUI.Text(section.Lede, "kv__value"));
                row.RegisterCallback<ClickEvent>(_ => Hub.Go(captured.Tab));
                card.Add(row);
            }
            return card.Root;
        }
    }
}
