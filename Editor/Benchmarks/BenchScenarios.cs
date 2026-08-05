using System.Collections.Generic;
using UnityEngine;

namespace OneText.Benchmarks
{
    /// <summary>
    /// The compound scenarios: scenes that stress several things at once,
    /// because that is how the differences between text systems actually
    /// compound in a game. Each returns the same report card.
    /// </summary>
    public static class BenchScenarios
    {
        // ------------------------------------------------------------ C2: chat

        /// <summary>
        /// C2 — a chat/log window under permanent churn: 30 visible lines, one
        /// new line every frame, mixed Korean/Chinese/English with 60% of the
        /// vocabulary repeating. This is the only scenario where the atlas runs
        /// *permanently full*, which is what per-tile eviction was built for:
        /// the number to look at is the p99 curve over time, not the median.
        /// </summary>
        public static BenchRun ChatStream(ITextSubject subject, int frames = 2000)
        {
            const int lines = 30;
            var corpus = new BenchCorpus(seed: 1234, BenchCorpus.Language.Mixed);
            var run = new BenchRun { Scenario = "C2 chat stream (CJK churn)", Subject = subject.Name };

            using var scene = new BenchScene();
            subject.Setup();
            (subject as IPrewarmable)?.Prewarm(corpus.CommonCodepoints(), new[] { 18f, 24f });

            var labels = new List<object>(lines);
            for (int i = 0; i < lines; i++)
            {
                var rect = new Rect(20f, 20f + i * 24f, 1200f, 24f);
                // Two sizes, as a chat window with system lines and user lines has.
                labels.Add(subject.CreateLabel(scene.Canvas.transform, rect,
                    i % 3 == 0 ? 18f : 24f, fontIndex: 2));
            }

            // Fill the window first; those frames are startup, not steady state.
            for (int i = 0; i < lines; i++)
            {
                int index = i;
                scene.Frame(subject, () => subject.SetText(labels[index], corpus.Line()));
            }

            // Everything above is setup: prewarming the atlas and filling the
            // scene have already done most of the session's glyph work, and
            // counting it into totals that are then divided by the frame count
            // reports it as a per-frame cost it never was.
            run.SetupNotes = AtlasDiagnostics.TakeSetupSummary();

            for (int frame = 0; frame < frames; frame++)
            {
                int slot = frame % lines;
                run.Samples.Add(scene.Frame(subject, () =>
                    subject.SetText(labels[slot], corpus.Line())));
            }

            run.TextureBytes = subject.TextureMemoryBytes;
            run.Notes = subject.Describe();
            subject.Teardown();
            return run;
        }

        // -------------------------------------------------------- C1: global UI

        /// <summary>
        /// C1 — a live-service HUD: 40 static elements and 20 that change every
        /// frame (counters, timers, resources), three faces, three sizes, and a
        /// language switch a third of the way in. The switch is the interesting
        /// part: it floods the atlas with glyphs nobody has seen, which is
        /// exactly the case a dynamic atlas is supposed to handle and the one
        /// where OneText used to be slower than a pre-baked atlas.
        /// </summary>
        public static BenchRun GlobalUi(ITextSubject subject, int frames = 600)
        {
            const int staticLabels = 40, changing = 20;
            var korean = new BenchCorpus(seed: 11, BenchCorpus.Language.Korean);
            var japanese = new BenchCorpus(seed: 22, BenchCorpus.Language.Japanese);
            var english = new BenchCorpus(seed: 33, BenchCorpus.Language.English);
            var run = new BenchRun { Scenario = "C1 global UI", Subject = subject.Name };

            using var scene = new BenchScene();
            subject.Setup();
            (subject as IPrewarmable)?.Prewarm(korean.CommonCodepoints(), new[] { 18f, 24f, 36f });

            var all = new List<object>();
            for (int i = 0; i < staticLabels + changing; i++)
            {
                float size = i % 3 == 0 ? 36f : i % 3 == 1 ? 24f : 18f;
                var rect = new Rect(20f + (i % 4) * 300f, 20f + (i / 4) * 40f, 280f, 36f);
                all.Add(subject.CreateLabel(scene.Canvas.transform, rect, size, fontIndex: i % 3));
            }

            var language = korean;
            for (int i = 0; i < all.Count; i++)
            {
                int index = i;
                scene.Frame(subject, () => subject.SetText(all[index], language.Short(index)));
            }

            // Everything above is setup: prewarming the atlas and filling the
            // scene have already done most of the session's glyph work, and
            // counting it into totals that are then divided by the frame count
            // reports it as a per-frame cost it never was.
            run.SetupNotes = AtlasDiagnostics.TakeSetupSummary();

            for (int frame = 0; frame < frames; frame++)
            {
                // Two switches: a fresh script at frame 300, then back to Latin
                // at 450, so both the flood and the return to cached glyphs show.
                if (frame == frames / 2) language = japanese;
                else if (frame == frames * 3 / 4) language = english;

                var current = language;
                int tick = frame;
                run.Samples.Add(scene.Frame(subject, () =>
                {
                    for (int i = 0; i < changing; i++)
                        subject.SetText(all[staticLabels + i], current.Short(tick * 31 + i));

                    // A language switch retexts everything, static labels too.
                    if (tick == frames / 2 || tick == frames * 3 / 4)
                        for (int i = 0; i < staticLabels; i++)
                            subject.SetText(all[i], current.Short(i));
                }));
            }

            run.TextureBytes = subject.TextureMemoryBytes;
            run.Notes = subject.Describe();
            subject.Teardown();
            return run;
        }

        // ------------------------------------------------- C4: the workload matrix

        /// <summary>
        /// One cell of the workload matrix: N labels, R of them retexted every
        /// frame, drawing from a corpus whose novelty is a parameter.
        ///
        /// C2 and C3 each move two variables at once — C2 has few long labels
        /// AND a stream of unseen glyphs, C3 has many short labels AND a warm
        /// atlas — so neither can say which of the two a change acted on. This
        /// varies rebuild count and glyph novelty independently, at a fixed
        /// string length, which is what makes "the fix moved the cell it was
        /// aimed at and left the others alone" a statement the numbers can
        /// support.
        ///
        /// <paramref name="reuse"/> 1 draws only from the prewarmed vocabulary,
        /// so the atlas stays warm and the frame is layout and mesh work; 0
        /// draws new characters every time, so the frame is glyph baking.
        /// </summary>
        public static BenchRun Churn(ITextSubject subject, string name, int frames,
            int labels, int rebuildsPerFrame, float reuse)
        {
            var corpus = new BenchCorpus(seed: 4321, BenchCorpus.Language.Mixed, reuse);
            var run = new BenchRun { Scenario = name, Subject = subject.Name };

            using var scene = new BenchScene();
            subject.Setup();
            // Both novelty settings prewarm the same vocabulary, so the only
            // difference between the warm and novel cells is whether the text
            // stays inside it.
            (subject as IPrewarmable)?.Prewarm(corpus.CommonCodepoints(), new[] { 24f });

            var all = new List<object>(labels);
            for (int i = 0; i < labels; i++)
            {
                var rect = new Rect((i % 20) * 60f, (i / 20) * 26f, 240f, 26f);
                all.Add(subject.CreateLabel(scene.Canvas.transform, rect, 24f, fontIndex: 2));
            }
            for (int i = 0; i < labels; i++)
            {
                int index = i;
                scene.Frame(subject, () => subject.SetText(all[index], corpus.Line(3)));
            }

            run.SetupNotes = AtlasDiagnostics.TakeSetupSummary();

            int cursor = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                run.Samples.Add(scene.Frame(subject, () =>
                {
                    for (int i = 0; i < rebuildsPerFrame; i++)
                    {
                        subject.SetText(all[cursor], corpus.Line(3));
                        cursor = (cursor + 1) % labels;
                    }
                }));
            }

            run.TextureBytes = subject.TextureMemoryBytes;
            run.Notes = subject.Describe();
            subject.Teardown();
            return run;
        }

        // ------------------------------------------------------- C3: worldspace

        /// <summary>
        /// C3 — 200 world-space labels: 150 nameplates that never change and 50
        /// damage numbers that change every frame. World space is where a
        /// material per font asset hurts most, because there is no screen-space
        /// canvas batching to hide behind, so the number to read here is
        /// batches and SetPass calls rather than milliseconds.
        /// </summary>
        public static BenchRun WorldSpaceLabels(ITextSubject subject, int frames = 600)
        {
            const int nameplates = 150, damage = 50;
            var names = new BenchCorpus(seed: 7, BenchCorpus.Language.Korean);
            var run = new BenchRun { Scenario = "C3 world-space labels", Subject = subject.Name };

            using var scene = new BenchScene(worldSpace: true);
            subject.Setup();
            (subject as IPrewarmable)?.Prewarm(names.CommonCodepoints(), new[] { 20f, 28f });

            var plates = new List<object>();
            for (int i = 0; i < nameplates + damage; i++)
            {
                var rect = new Rect((i % 20) * 60f, (i / 20) * 60f, 200f, 30f);
                plates.Add(subject.CreateLabel(scene.Canvas.transform, rect,
                    i < nameplates ? 20f : 28f, fontIndex: i % 2 == 0 ? 0 : 2));
            }

            for (int i = 0; i < plates.Count; i++)
            {
                int index = i;
                scene.Frame(subject, () => subject.SetText(plates[index], names.Short(index)));
            }

            // Everything above is setup: prewarming the atlas and filling the
            // scene have already done most of the session's glyph work, and
            // counting it into totals that are then divided by the frame count
            // reports it as a per-frame cost it never was.
            run.SetupNotes = AtlasDiagnostics.TakeSetupSummary();

            for (int frame = 0; frame < frames; frame++)
            {
                int tick = frame;
                run.Samples.Add(scene.Frame(subject, () =>
                {
                    for (int i = 0; i < damage; i++)
                        subject.SetText(plates[nameplates + i], (tick * 7 + i * 13 % 900).ToString());
                }));
            }

            run.TextureBytes = subject.TextureMemoryBytes;
            run.Notes = subject.Describe();
            subject.Teardown();
            return run;
        }
    }
}
