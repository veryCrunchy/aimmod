using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Overlays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("find", 1920, 1080)]
    [TestCase("find", 1280, 800)]
    [TestCase("find", 900, 700)]
    [TestCase("find-analysing", 1280, 800)]
    [TestCase("detail", 1920, 1080)]
    [TestCase("detail", 1280, 800)]
    [TestCase("detail", 900, 700)]
    [TestCase("detail-hover", 1280, 800)]
    [TestCase("detail-practising", 1280, 800)]
    [TestCase("detail-practising", 900, 700)]
    [TestCase("find-menu", 1280, 800)]
    [TestCase("find-menu", 900, 700)]
    [Explicit("Renders the coaching screen with 1,000+ synthetic plays on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureCoachingScreen(string scene, int width, int height)
    {
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"coaching-screen-{scene}-{width}x{height}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, failure) =>
            new CaptureCoachingScreenGame(host, output, scene, width, height, success, failure), TimeSpan.FromSeconds(45));
        using var rendered = Image.Load<Rgba32>(output);
        Assert.That(rendered.Width, Is.EqualTo(width));
        Assert.That(countSampledColours(rendered), Is.GreaterThan(8));
        TestContext.AddTestAttachment(output);
    }

    /// <summary>Synthetic, deterministic history: many attempts per difficulty with recurring problem sections.</summary>
    internal static class CoachingScreenFixture
    {
        private static readonly string[] titles =
        [
            "Neon Cascade", "Paper Satellites", "Glass Harbour", "Midnight Relay", "Violet Engine", "Tidal Clockwork",
            "Signal Garden", "Aurora Freight", "Hollow Comet", "Silver Lattice", "Crimson Orbit", "Static Meadow",
            "Lantern Drift", "Quiet Voltage", "Northbound Echo", "Copper Skyline", "Velvet Circuit", "Ember Parade",
        ];
        private static readonly string[] difficulties = ["Hard", "Insane", "Extra", "Expert"];

        public static (LocalReplay[] Runs, Dictionary<Guid, ReplayAnalysisResult> Analyses) Build(DateTimeOffset now)
        {
            var random = new Random(7);
            var runs = new List<LocalReplay>();
            var analyses = new Dictionary<Guid, ReplayAnalysisResult>();
            int map = 0;
            foreach (string title in titles)
            {
                Guid setId = Guid.NewGuid();
                int difficultyCount = 1 + map % 3;
                for (int d = 0; d < difficultyCount; d++)
                {
                    Guid beatmapId = Guid.NewGuid();
                    double stars = 4.1 + map * 0.13 + d * 0.55;
                    double lengthMs = 150_000 + map % 5 * 21_000;
                    double skill = 0.955 - (stars - 4) * 0.012 + random.NextDouble() * 0.01;
                    int attempts = 12 + (map * 7 + d * 11) % 70;
                    string[] mods = map % 5 == 3 ? ["HD"] : map % 7 == 5 ? ["DT"] : [];
                    // Problem type per difficulty keeps rows distinct in the ranked list.
                    int problem = (map + d) % 4;
                    for (int a = 0; a < attempts; a++)
                    {
                        double daysAgo = Math.Pow(random.NextDouble(), 1.6) * 58 + (map % 4) * 0.5;
                        double accuracy = Math.Clamp(skill + (random.NextDouble() - 0.5) * 0.05 + a * 0.0002, 0.71, 0.995);
                        int misses = Math.Max(0, (int)Math.Round((1 - accuracy) * 70 + (random.NextDouble() - 0.4) * 5));
                        bool replay = a % 3 != 1;
                        var run = new LocalReplay(Guid.NewGuid(), setId, beatmapId, title, "Synthetic Artist", difficulties[(d + map) % difficulties.Length],
                            "osu", "Synthetic Player", now.AddDays(-daysAgo).AddMinutes(-a), stars, accuracy,
                            600_000 + (long)(accuracy * 400_000), 300 + a, misses, 120 + stars * 30 * accuracy + random.NextDouble() * 20,
                            mods, replay, $"synthetic-hash-{map}-{d}", OnlineBeatmapId: 90_000 + map * 10 + d);
                        runs.Add(run);
                        if (replay && daysAgo < 40)
                            analyses[run.ScoreId] = analysis(random, lengthMs, misses, problem, accuracy);
                    }
                }
                map++;
            }
            return (runs.OrderByDescending(r => r.PlayedAt).ToArray(), analyses);
        }

        private static ReplayAnalysisResult analysis(Random random, double lengthMs, int misses, int problem, double accuracy)
        {
            var judgements = new List<ReplayObjectJudgement>();
            double time = 2_000;
            int index = 0;
            double streamStart = lengthMs * 0.38, jumpStart = lengthMs * 0.62, lateStart = lengthMs * 0.84;
            var missAt = new HashSet<int>();
            while (time < lengthMs)
            {
                bool stream = time >= streamStart && time < streamStart + 1_600;
                double gap = stream ? 110 : 330;
                double offset = (random.NextDouble() - 0.5) * 14;
                // Problem 0: rushing stream; 1: overshooting jumps; 2: early presses late in the map; 3: undershoots and missed presses.
                if (stream && problem == 0 && random.NextDouble() < 0.8)
                    offset = 6 - (time - streamStart) / 110 * 6.5 + (random.NextDouble() - 0.5) * 3;
                string result = Math.Abs(offset) > 45 ? "Ok" : "Great";
                ReplayMissAnalysis? miss = null;
                bool inJump = time >= jumpStart && time < jumpStart + 2_400;
                bool inLate = time >= lateStart && time < lateStart + 2_000;
                bool section = (inJump && problem is 1 or 3) || (inLate && problem == 2);
                bool wantsMiss = misses > missAt.Count && ((section && random.NextDouble() < 0.5) || random.NextDouble() < 0.0015);
                var objectPosition = new ReplayPoint(120 + index * 37 % 280, 90 + index * 53 % 200);
                if (wantsMiss)
                {
                    missAt.Add(index);
                    result = "Miss";
                    int kind = section ? problem : random.Next(1, 4);
                    float dx = (float)(random.NextDouble() - 0.5) * 16, dy = (float)(random.NextDouble() - 0.5) * 16;
                    miss = kind switch
                    {
                        1 => new ReplayMissAnalysis(ReplayMissReason.Overshoot, 32, 30, -8, new(objectPosition.X + 46 + dx, objectPosition.Y + 16 + dy), 12, 50,
                            new ReplayPoint(objectPosition.X + 46 + dx, objectPosition.Y + 16 + dy), 50, true, false, true, 0.4, Confidence: 0.86),
                        2 => new ReplayMissAnalysis(ReplayMissReason.EarlyClick, 32, 6, -4, new(objectPosition.X + 4, objectPosition.Y + 2), -118 - random.Next(40), 7,
                            new ReplayPoint(objectPosition.X + 4, objectPosition.Y + 2), 8, true, false, false, 0.1, Confidence: 0.82),
                        _ when random.NextDouble() < 0.6 => new ReplayMissAnalysis(ReplayMissReason.Undershoot, 32, 36, -6, new(objectPosition.X - 40 + dx, objectPosition.Y - 14 + dy), 8, 44,
                            new ReplayPoint(objectPosition.X - 40 + dx, objectPosition.Y - 14 + dy), 44, false, true, false, -0.3, Confidence: 0.84),
                        _ => new ReplayMissAnalysis(ReplayMissReason.OnTargetNoClick, 32, 8, 0, new(objectPosition.X + 5, objectPosition.Y), null, null,
                            null, 8, true, false, false, 0, Confidence: 0.8),
                    };
                    offset = miss.PressTimeOffsetMs ?? 150;
                }
                judgements.Add(new ReplayObjectJudgement(index, null, "HitCircle", time, time, result, "Great", time + offset, offset, 1,
                    objectPosition, new ReplayPoint(objectPosition.X + 3, objectPosition.Y - 2), index, result == "Miss" ? 0 : index + 1, miss));
                time += gap;
                index++;
            }
            int great = judgements.Count(j => j.Result == "Great"), ok = judgements.Count(j => j.Result == "Ok");
            return new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true,
                ReplayAnalysisProtocol.WallClockTimeoutMs, Array.Empty<int>(), judgements.ToArray(),
                new ReplayJudgementSummary(great, ok, 0, missAt.Count, 0, 0));
        }
    }

    private sealed partial class CaptureCoachingScreenGame(GameHost host, string output, string scene,
        int width, int height, Action success, Action<Exception> failure) : OsuGameBase
    {
        [Cached] private readonly OverlayColourProvider colours = new(OverlayColourScheme.Blue);
        [Resolved] private FrameworkConfigManager frameworkConfig { get; set; } = null!;
        private const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;

        [BackgroundDependencyLoader]
        private void load()
        {
            var now = DateTimeOffset.UtcNow;
            var (runs, analyses) = CoachingScreenFixture.Build(now);
            Assert.That(runs.Length, Is.GreaterThan(1_000));
            var source = new InMemoryLocalLibrarySource([], runs);
            string storage = Path.Combine(Path.GetDirectoryName(output)!, "coaching-screen-" + scene + width);
            var library = new PracticeMapLibrary(Path.Combine(storage, "library"));
            NativeCoachingWorkspace? workspace = null;
            var sections = new[] { 60_000, 110_000, 150_000 }.Select((time, i) => new PracticeSectionChoice(PracticeDrillType.Streams,
                new(PracticeDrillType.Streams, i * 10, i * 10 + 9, time, time + 6_000, 0, [], []))).ToArray();
            var practice = new NativePracticeWorkspace((_, _) => Task.FromResult<IReadOnlyList<PracticeSectionChoice>>(sections),
                (_, _) => Task.FromResult(new PracticeMapGenerationResult(false, "Unused in visual test")),
                (_, _) => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)), library, () => workspace?.ClosePractice());
            practice.StartDifficulty = (_, _) => { };
            workspace = new NativeCoachingWorkspace(source, analyses, _ => { }, practiceWorkspace: practice,
                openBeatmap: (_, _) => Task.CompletedTask, openReplayMoment: (_, _) => { }, openTrainers: () => { }, practiceLibrary: library);
            workspace.ConfigurePracticeSessions(new(Path.Combine(storage, "session.json")), () => []);
            float sidebar = AimModLayout.SidebarWidth(AimModLayout.SelectSidebarMode(width));
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas });
            Add(new Box { RelativeSizeAxes = Axes.Y, Width = sidebar, Colour = AimModPalette.Header });
            Add(new Container { RelativeSizeAxes = Axes.Both, Padding = AimModVisualStyle.PagePaddingFor(sidebar), Child = workspace });

            if (scene == "find-analysing")
                Scheduler.AddDelayed(() =>
                {
                    workspace.BeginAnalysisProgress();
                    workspace.SetAnalysisProgress(38, 120, "Glass Harbour [Insane]");
                }, 1_600);

            if (scene.StartsWith("detail", StringComparison.Ordinal))
                Scheduler.AddDelayed(() =>
                {
                    var all = (IReadOnlyList<LocalReplay>)typeof(NativeCoachingWorkspace).GetField("allReplays", flags)!.GetValue(workspace)!;
                    // The most-played analysed difficulty mirrors a player's usual first pick from the ranked list.
                    var run = all.Where(r => analyses.ContainsKey(r.ScoreId)).GroupBy(r => r.BeatmapId).OrderByDescending(g => g.Count()).First().First();
                    if (scene == "detail-practising")
                    {
                        var identities = Enum.GetValues<PracticeBreakdownVariant>().Select(v => new PracticeDifficultyIdentity(
                            v.ToString(), PracticeDrillType.Streams, "hash-" + v, "md5-" + v, 60_000, 66_000, true, v, "section-a", "")).ToArray();
                        var attempts = identities.Take(2).SelectMany(d => Enumerable.Range(0, 3).Select(i => new PracticeAttempt(
                            Guid.NewGuid(), 0, now.AddMinutes(-30 + i), .93 + .01 * i, 2 - i, true, "lazer / NM / NM", d.Name, false, false,
                            d.BreakdownVariant, d.BreakdownGroupId, d.SourceStartMs, d.SourceEndMs))).ToArray();
                        var saved = new SavedPracticeMap("synthetic-coaching-set", run.Title, run.Difficulty, PracticeDrillType.Streams,
                            now.AddDays(-1), 60_000, 66_000, 180_000, 4, 120,
                            Tracking: new(run.Player, 0, run.OnlineBeatmapId, run.BeatmapHash, run.BeatmapId, ScoreMods.Configuration(run), false, [], identities));
                        typeof(NativeCoachingWorkspace).GetField("practiceSets", flags)!.SetValue(workspace,
                            new[] { new PracticeSetProgress(saved, new(attempts)) });
                    }
                    typeof(NativeCoachingWorkspace).GetMethod("openCoachingRun", flags)!.Invoke(workspace, [run]);
                    if (scene == "detail-hover")
                        Scheduler.AddDelayed(() =>
                        {
                            try
                            {
                                // Hover the tallest timeline slice so the tooltip and its per-play counts are reviewed too.
                                var detailHost = (Drawable)typeof(NativeCoachingWorkspace).GetField("mapDetailHost", flags)!.GetValue(workspace)!;
                                var timeline = descendants(detailHost).OfType<AimModCoachTimeline>().First();
                                var bins = (Container)typeof(AimModCoachTimeline).GetField("bins", flags)!.GetValue(timeline)!;
                                var tallest = bins.Children.MaxBy(bin => descendants(bin).OfType<FillFlowContainer>().First().Height)!;
                                var hover = (Action<bool, float>)tallest.GetType().GetField("hover", flags)!.GetValue(tallest)!;
                                hover(true, tallest.X + tallest.DrawWidth / 2);
                            }
                            catch (Exception error) { failure(error); host.Exit(); }
                        }, 600);
                }, scene == "detail-practising" ? 2_800 : 1_900);

            if (scene == "find-menu")
                Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        var filters = (Drawable)typeof(NativeCoachingWorkspace).GetField("coachingFilters", flags)!.GetValue(workspace)!;
                        var dropdown = descendants(filters).OfType<AimModDropdown<CoachingTimeRange>>().Single();
                        var menu = (osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<CoachingTimeRange>)
                            .GetField("Menu", flags)!.GetValue(dropdown)!;
                        // The open menu must lift the whole filter row above the profile and ranked list, then restore it.
                        float depth = filters.Depth;
                        var runList = (Drawable)typeof(NativeCoachingWorkspace).GetField("findLayout", flags)!.GetValue(workspace)!;
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Open;
                        Assert.That(filters.Depth, Is.LessThan(runList.Depth), "The open period menu must draw above the map list.");
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Closed;
                        Assert.That(filters.Depth, Is.EqualTo(depth));
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Open;
                    }
                    catch (Exception error) { failure(error); host.Exit(); }
                }, 1_900);
        }

        private static IEnumerable<Drawable> descendants(Drawable drawable)
        {
            yield return drawable;
            if (drawable is CompositeDrawable composite)
                foreach (var child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(composite)!)
                    foreach (var item in descendants(child)) yield return item;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(() => host.TakeScreenshotAsync().ContinueWith(task =>
            {
                try
                {
                    using var screenshot = task.GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    screenshot.SaveAsPng(output); success();
                }
                catch (Exception error) { failure(error); }
                finally { host.Exit(); }
            }, TaskScheduler.Default), 4_200);
        }
    }
}
