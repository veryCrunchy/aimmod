using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Overlays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("session", 1600, 900)]
    [TestCase("session", 800, 760)]
    [TestCase("multi-section", 800, 760)]
    [TestCase("multi-section", 1600, 900)]
    [TestCase("remaining", 800, 760)]
    [TestCase("remaining", 1600, 900)]
    [TestCase("remaining-menu", 800, 760)]
    [TestCase("remaining-menu", 1600, 900)]
    [TestCase("session-details", 1600, 900)]
    [TestCase("session-details", 800, 760)]
    [TestCase("transfer", 1600, 900)]
    [TestCase("transfer", 800, 760)]
    [TestCase("breakdown", 1600, 900)]
    [TestCase("breakdown", 800, 760)]
    [Explicit("Renders synthetic skill practice on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureSkillPractice(string scene, int width, int height)
    {
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"skill-{scene}-{width}x{height}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, failure) =>
            new CaptureSkillPracticeGame(host, output, scene, width, height, success, failure), TimeSpan.FromSeconds(30));
        using var rendered = Image.Load<Rgba32>(output);
        Assert.That(countSampledColours(rendered), Is.GreaterThan(8));
        TestContext.AddTestAttachment(output);
    }

    private sealed partial class CaptureSkillPracticeGame(GameHost host, string output, string scene,
        int width, int height, Action success, Action<Exception> failure) : OsuGameBase
    {
        [Cached] private readonly OverlayColourProvider colours = new(OverlayColourScheme.Blue);
        [Resolved] private FrameworkConfigManager frameworkConfig { get; set; } = null!;
        private const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;

        [BackgroundDependencyLoader]
        private void load()
        {
            var source = createPopulatedLibrary();
            var runs = source.SearchReplaysAsync(new LocalLibraryQuery(Limit: 200)).AsTask().GetAwaiter().GetResult().Items.ToArray();
            var run = runs[0];
            var now = DateTimeOffset.UtcNow;
            var identities = Enum.GetValues<PracticeBreakdownVariant>().Select(v => new PracticeDifficultyIdentity(
                v switch { PracticeBreakdownVariant.ReducedMovement => "Reduced movement", PracticeBreakdownVariant.AimFocus => "Aim focus",
                    PracticeBreakdownVariant.CombinedEasier => "Combined at 85%", _ => "Original section" },
                PracticeDrillType.LongJumps, "hash-" + v, "md5-" + v, 12000, 18000, true, v, "section-a",
                v == PracticeBreakdownVariant.AimFocus ? "RX" : "")).ToArray();
            var attempts = identities.SelectMany(d => Enumerable.Range(0, 3).Select(i => new PracticeAttempt(
                Guid.NewGuid(), 0, now.AddMinutes(-20 + i), d.RequiredMods == "RX" ? 1 : .96 + .002 * i,
                i, true, d.RequiredMods == "RX" ? "lazer / NF + RX / NF+RX" : "lazer / NF / NF", d.Name, false,
                d.RequiredMods == "RX", d.BreakdownVariant, d.BreakdownGroupId, d.SourceStartMs, d.SourceEndMs))).ToArray();
            var map = new SavedPracticeMap("synthetic-skill-set", run.Title, run.Difficulty, PracticeDrillType.LongJumps,
                now.AddDays(-1), 12000, 18000, 60000, 6, 120,
                Tracking: new(run.Player, 0, 0, run.BeatmapHash ?? "", run.BeatmapId, "", false, [], identities));
            var set = new PracticeSetProgress(map, new(attempts));
            var library = new PracticeMapLibrary(Path.Combine(Path.GetDirectoryName(output)!, "synthetic-library-" + scene + width));
            NativeCoachingWorkspace? workspace = null;
            var sourceSections = new[] { 12000, 30000, 50000 }.Select((time, i) => new PracticeSectionChoice(PracticeDrillType.LongJumps,
                new(PracticeDrillType.LongJumps, i * 10, i * 10 + 9, time, time + 6000, 0, [], []))).ToArray();
            var practice = new NativePracticeWorkspace((_, _) => Task.FromResult<IReadOnlyList<PracticeSectionChoice>>(sourceSections),
                (_, _) => Task.FromResult(new PracticeMapGenerationResult(false, "Unused in visual test")),
                (_, _) => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)), library, () => workspace?.ClosePractice());
            practice.StartDifficulty = (_, _) => { };
            workspace = new NativeCoachingWorkspace(source, runs.ToDictionary(r => r.ScoreId, _ => createCoachingAnalysis(0)), _ => { },
                practiceWorkspace: practice, openBeatmap: (_, _) => Task.CompletedTask, openReplayMoment: (_, _) => { });
            workspace.ConfigurePracticeSessions(new(Path.Combine(Path.GetDirectoryName(output)!, "synthetic-session-" + scene + width + ".json")), () => []);
            Add(new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(24), Child = workspace });
            Scheduler.AddDelayed(() =>
            {
                var second = new PracticeSetProgress(map with { Id = "synthetic-second-section", CreatedAt = now.AddDays(-2),
                    Tracking = map.Tracking! with { Difficulties = identities.Select(d => d with { BreakdownGroupId = "section-b", SourceStartMs = 30000, SourceEndMs = 36000 }).ToArray() } }, PracticeProgress.Empty);
                typeof(NativeCoachingWorkspace).GetField("practiceSets", flags)!.SetValue(workspace, scene == "multi-section" ? new[] { set, second } : new[] { set });
                typeof(NativeCoachingWorkspace).GetField("allReplays", flags)!.SetValue(workspace, runs);
                typeof(NativeCoachingWorkspace).GetMethod("openCoachingMap", flags)!.Invoke(workspace, [NativeCoachingWorkspace.CoachingMapKey(map), run]);
                var stages = (Dictionary<string, CoachingPracticeStage>)typeof(NativeCoachingWorkspace).GetField("viewedStages", flags)!.GetValue(workspace)!;
                stages[map.Id] = scene == "transfer" ? CoachingPracticeStage.Transfer : CoachingPracticeStage.Isolate;
                if (scene == "transfer") typeof(NativeCoachingWorkspace).GetField("selectingTransferFor", flags)!.SetValue(workspace, map.Id);
                if (scene == "session-details")
                {
                    typeof(NativeCoachingWorkspace).GetField("showPracticeSteps", flags)!.SetValue(workspace, true);
                    typeof(NativeCoachingWorkspace).GetField("showPracticeComparisonDetails", flags)!.SetValue(workspace, true);
                }
                typeof(NativeCoachingWorkspace).GetMethod("renderCoachingMap", flags)!.Invoke(workspace, null);
                if (scene == "breakdown") practice.OpenSaved(map);
                var body = (FillFlowContainer<Drawable>)typeof(NativeCoachingWorkspace).GetField("mapDetailHost", flags)!.GetValue(workspace)!;
                Assert.That(body.Count, Is.GreaterThan(3));
                if (scene.StartsWith("remaining")) practice.OpenNextBreakdown(new PracticeMapCandidate(run, [run.ScoreId], 1, 1, 1), [new(12000, 18000)]);
                if (scene == "remaining-menu") Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        IEnumerable<Drawable> descendants(Drawable drawable)
                        {
                            yield return drawable;
                            if (drawable is CompositeDrawable composite)
                                foreach (var child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(composite)!)
                                    foreach (var item in descendants(child)) yield return item;
                        }
                        var detail = (Drawable)typeof(NativePracticeWorkspace).GetField("detail", flags)!.GetValue(practice)!;
                        var dropdown = descendants(detail).OfType<AimMod.Desktop.Visuals.AimModDropdown<int>>().Single(d => d.Items.SequenceEqual(new[] { 0, 1 }));
                        var menu = (osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<int>).GetField("Menu", flags)!.GetValue(dropdown)!;
                        Drawable row = dropdown;
                        while (row.Parent != detail) row = row.Parent ?? throw new InvalidOperationException("Section menu has no form row.");
                        float oldDepth = row.Depth;
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Open;
                        Assert.That(row.Depth, Is.LessThan(oldDepth), "The open section menu must rise above the speed and length controls.");
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Closed;
                        Assert.That(row.Depth, Is.EqualTo(oldDepth));
                        menu.State = osu.Framework.Graphics.UserInterface.MenuState.Open;
                    }
                    catch (Exception error) { failure(error); host.Exit(); }
                }, 350);
                if (scene == "multi-section")
                {
                    IEnumerable<AimMod.Desktop.Visuals.AimModButton> buttons(Drawable drawable)
                    {
                        if (drawable is AimMod.Desktop.Visuals.AimModButton button) yield return button;
                        if (drawable is Container<Drawable> container)
                            foreach (var child in container.Children)
                                foreach (var descendant in buttons(child)) yield return descendant;
                    }
                    var next = buttons(body).Single(b => ((osu.Game.Graphics.Sprites.OsuSpriteText)typeof(AimMod.Desktop.Visuals.AimModButton).GetField("caption", flags)!.GetValue(b)!).Text.ToString() == "Next section");
                    next.Action.Invoke();
                    var selectedSets = (Dictionary<string, string>)typeof(NativeCoachingWorkspace).GetField("viewedPracticeSets", flags)!.GetValue(workspace)!;
                    Assert.That(selectedSets[NativeCoachingWorkspace.CoachingMapKey(map)], Is.EqualTo(second.Map.Id));
                    Assert.That(stages[second.Map.Id], Is.EqualTo(CoachingPracticeStage.Isolate), "Next section opens its tapping exercise, not another full-map baseline.");
                    Assert.That(second.Progress.Attempts, Is.Empty, "The earlier section's completed runs must not complete the next section.");
                }
            }, 1300);
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
            }, TaskScheduler.Default), 2700);
        }
    }
}
