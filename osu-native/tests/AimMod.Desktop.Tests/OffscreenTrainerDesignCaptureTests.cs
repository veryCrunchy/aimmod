using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("overview", 1920, 1080)] [TestCase("overview", 1280, 800)] [TestCase("overview", 900, 700)]
    [TestCase("overview-scrolled", 1280, 800)] [TestCase("overview-scrolled", 900, 700)]
    [TestCase("manual", 1920, 1080)] [TestCase("manual", 900, 700)]
    [TestCase("aim", 1280, 800)]
    [TestCase("empty", 1280, 800)]
    [TestCase("results", 1920, 1080)] [TestCase("results", 1280, 800)] [TestCase("results", 900, 700)]
    [TestCase("menu-length", 1280, 800)] [TestCase("menu-length", 900, 700)]
    [TestCase("menu-music", 1920, 1080)] [TestCase("menu-music", 900, 700)]
    [TestCase("warmup", 1920, 1080)] [TestCase("warmup", 900, 700)]
    [TestCase("warmup-plan", 1280, 800)] [TestCase("warmup-plan", 900, 700)]
    [TestCase("mods", 1920, 1080)] [TestCase("mods", 900, 700)]
    [TestCase("guided", 1280, 800)] [TestCase("reaction-results", 1280, 800)] [TestCase("warmup-done", 1280, 800)]
    [Explicit("Creates a real graphics device and writes trainer design-review captures.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureTrainerDesign(string route, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Private-desktop captures are only supported on Windows.");
        string outputPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", "trainers-design", $"{route}-{width}x{height}.png");
        var source = route.StartsWith("mods", StringComparison.Ordinal) ? createPopulatedLibrary() : new InMemoryLocalLibrarySource([], []);
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new TrainerDesignGame(host, source, route, width, height, outputPath, ok, fail), TimeSpan.FromSeconds(45));
        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);
        Assert.That(image.Width, Is.EqualTo(width));
        Assert.That(image.Height, Is.EqualTo(height));
        TestContext.AddTestAttachment(outputPath, $"Trainers {route}");
    }

    /// <summary>Synthetic practice over several weeks: accuracy 85–95%, spread 20–40 ms, varying tempo.</summary>
    internal static TrainerResult[] SyntheticTrainerHistory(TrainerKind kind, DateTimeOffset now, int count = 36, int seed = 7, int offsetMs = 16, double? fixedSpread = null, double? fixedMean = null)
    {
        var random = new Random(seed + (int)kind);
        var runs = new List<TrainerResult>();
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Math.Max(1, count - 1);
            double accuracy = Math.Clamp(85.5 + 8 * t + (random.NextDouble() - .5) * 3.2, 85, 95.4);
            double spread = fixedSpread ?? Math.Clamp(38 - 15 * t + (random.NextDouble() - .5) * 6, 20, 40);
            int bpm = 140 + 10 * random.Next(0, 6);
            int notes = 110 + random.Next(0, 90);
            int misses = (int)Math.Round((100 - accuracy) / 100 * notes * .3);
            double mean = fixedMean ?? (random.NextDouble() - .55) * 12;
            var settings = new TrainerSettings(Kind: kind, Bpm: bpm, Seconds: i % 5 == 0 ? 120 : 60, AdaptiveDifficulty: true, OffsetMs: offsetMs,
                OverallDifficulty: Math.Round((4.5 + t * 1.25) * 4) / 4, Music: "cues", ApproachRate: 6 + (int)(t * 2), CircleSize: 3 + (int)(t * 1.5));
            // Recorded hit offsets: roughly normal around the run's mean with its spread.
            var offsets = Enumerable.Range(0, notes - misses).Select(_ =>
                mean + spread * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())).ToArray();
            runs.Add(new TrainerResult(Guid.NewGuid(), now.AddDays(-42 * (1 - t)).AddHours(-random.Next(1, 10)), settings,
                notes, notes - misses, offsets.Count(o => Math.Abs(o) <= 25), 0, 0, mean, spread,
                (random.NextDouble() - .5) * 8, Engine: TrainerResult.EngineFor(settings), Accuracy: accuracy, PlayedSeconds: settings.Seconds,
                Demand: new TrainerDemand(bpm / 60.0 * (kind == TrainerKind.Aim ? 1 : 2) * (.9 + .2 * t), kind == TrainerKind.Aim ? 150 + 60 * t : 90 + 30 * t,
                    kind == TrainerKind.Aim ? 420 + 120 * t : 260, 8 + (int)(8 * t)),
                JudgementMisses: misses, TapTargets: notes, OffsetHistogram: TrainerResult.Histogram(offsets)));
        }
        return runs.ToArray();
    }

    private sealed partial class TrainerDesignGame(GameHost host, ILocalLibrarySource source, string route, int width, int height,
        string outputPath, Action ok, Action<Exception> fail) : AimModGame(AimModLaunchOptions.Home, source)
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        [Resolved] private FrameworkConfigManager frameworkConfig { get; set; } = null!;
        private NativeTrainersWorkspace workspace = null!;

        private object? field(string name) => typeof(NativeTrainersWorkspace).GetField(name, flags)!.GetValue(workspace);
        private void invoke(string name, params object[] args) => typeof(NativeTrainersWorkspace).GetMethod(name, flags)!.Invoke(workspace, args);
        private static void setMenu(Drawable dropdown, bool open) => typeof(CaptureAimModGame)
            .GetMethod("setTrainerMenu", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [dropdown, open]);

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(() => typeof(AimModGame).GetMethod("showTrainers", flags)!.Invoke(this, null), 300);
            Scheduler.AddDelayed(() =>
            {
                try { arrange(); }
                catch (Exception error) { fail(error); host.Exit(); }
            }, 1200);
        }

        private void arrange()
        {
            workspace = (NativeTrainersWorkspace)typeof(AimModGame).GetField("trainersWorkspace", flags)!.GetValue(this)!;
            var store = ((Func<TrainerHistoryStore>)field("history")!)();
            var now = DateTimeOffset.UtcNow;
            if (route != "empty")
            {
                foreach (var run in SyntheticTrainerHistory(TrainerKind.Steady, now).Concat(SyntheticTrainerHistory(TrainerKind.Aim, now, 18, 3))) store.Add(run);
            }
            workspace.ApplyOsuSettings(new TrainerOsuSettings("Z / X", 16, false, "osu!lazer"));
            if (route == "aim") workspace.SelectTrainer(TrainerKind.Aim);
            workspace.RefreshHistory();
            double delay = 1500;
            switch (route)
            {
                case "overview-scrolled":
                    Scheduler.AddDelayed(() => ((AimModScrollContainer)field("contentScroll")!).ScrollToEnd(false), 1200);
                    delay = 2200;
                    break;
                case "manual":
                    ((ClickableContainer)field("adaptiveToggle")!).Action!();
                    workspace.TogglePracticeOptions();
                    break;
                case "results":
                {
                    var latest = SyntheticTrainerHistory(TrainerKind.Steady, now, 1, 99, fixedSpread: 19.5, fixedMean: -3.2)[0];
                    latest = latest with { CompletedAt = now, Accuracy = 94.1 };
                    workspace.CompleteOsuSession(latest);
                    Assert.That((bool)field("showingResults")!, Is.True, "The result must replace the setup.");
                    break;
                }
                case "menu-length":
                    Scheduler.AddDelayed(() => checkMenuLayering("durationSelector"), 400);
                    break;
                case "menu-music":
                    Scheduler.AddDelayed(() =>
                    {
                        var menu = (Drawable)field("musicSelector")!;
                        var scroll = (AimModScrollContainer)field("contentScroll")!;
                        scroll.ScrollTo(scroll.Current + scroll.ToLocalSpace(menu.ScreenSpaceDrawQuad.TopLeft).Y - 120, false);
                        Scheduler.AddDelayed(() => checkMenuLayering("musicSelector"), 300);
                    }, 400);
                    delay = 2000;
                    break;
                case "warmup":
                    workspace.ShowWarmup();
                    break;
                case "warmup-plan":
                    workspace.ShowWarmup();
                    invoke("prepareWarmup");
                    delay = 3000;
                    break;
                case "guided":
                    workspace.StartGuidedPractice(TrainerGuidedFocus.MovementComparison);
                    break;
                case "reaction-results":
                {
                    workspace.SelectTrainer(TrainerKind.Reaction);
                    var session = new ReactionSession(new(Kind: TrainerKind.Reaction, Seconds: 30, ReactionMode: ReactionMode.ChoiceGoNoGo), 3);
                    while (session.CueTime < 30000)
                    {
                        if (session.NoGo) session.Advance(session.CueTime + 1200);
                        else session.Tap(session.CueTime + 260 + session.Trials.Count * 7, session.Trials.Count % 4 == 0 ? 1 - session.RequiredKey : session.RequiredKey);
                    }
                    workspace.CompleteOsuSession(session.Result(DateTimeOffset.UtcNow));
                    break;
                }
                case "warmup-done":
                {
                    workspace.ShowWarmup();
                    var plan = TrainerWarmup.Create(4, new TrainerSettings(Keys: "Z / X", OffsetMs: 16), store.Load(), [], now);
                    typeof(NativeTrainersWorkspace).GetField("warmup", flags)!.SetValue(workspace, plan);
                    typeof(NativeTrainersWorkspace).GetField("warmupStore", flags)!.SetValue(workspace, store);
                    typeof(NativeTrainersWorkspace).GetField("warmupAccount", flags)!.SetValue(workspace, workspace.CurrentSkillAccountId?.Invoke());
                    for (int i = 0; i < 2; i++)
                        plan.Record(TrainerWarmupTests.Result(plan.CurrentSettings(), i == 0 ? 91.5 : 94.2));
                    workspace.ShowWarmup();
                    break;
                }
                case "mods":
                    ((AimModButton)field("dtEntry")!).Action!();
                    delay = 2500;
                    break;
            }
            Scheduler.AddDelayed(capture, delay);
        }

        /// <summary>An open card menu must lift its rows above every neighbour, hide the pinned actions, and restore on close.</summary>
        private void checkMenuLayering(string dropdownField)
        {
            try
            {
                var menu = (Drawable)field(dropdownField)!;
                var card = (Drawable)field("sessionCard")!;
                var choices = (Drawable)field("choices")!;
                var progress = (Drawable)field("progress")!;
                var actions = (Drawable)field("actionsHost")!;
                float depth = card.Depth;
                setMenu(menu, true);
                Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        Assert.That(card.Depth, Is.LessThan(Math.Min(choices.Depth, progress.Depth)), "The open menu's card must draw above the choices and progress.");
                        Assert.That(actions.Alpha, Is.Zero, "Pinned actions must not cover an open menu.");
                        setMenu(menu, false);
                        Scheduler.AddDelayed(() =>
                        {
                            try
                            {
                                Assert.That(card.Depth, Is.EqualTo(depth), "Closing the menu must restore the card depth.");
                                Assert.That(actions.Alpha, Is.EqualTo(1), "Actions return once the menu closes.");
                                setMenu(menu, true);
                            }
                            catch (Exception error) { fail(error); host.Exit(); }
                        }, 100);
                    }
                    catch (Exception error) { fail(error); host.Exit(); }
                }, 100);
            }
            catch (Exception error) { fail(error); host.Exit(); }
        }

        private int sizeChecks;
        private void capture()
        {
            // The first window of a run can report its new size a few frames late.
            if (host.Window?.ClientSize.Width != width && sizeChecks++ < 20) { Scheduler.AddDelayed(capture, 250); return; }
            if (workspace.Parent is null) { fail(new AssertionException("Trainers must be visible before capturing.")); host.Exit(); return; }
            if (route == "results" && !(bool)field("showingResults")!) { fail(new AssertionException("The result view was replaced before capture.")); host.Exit(); return; }
            Scheduler.AddDelayed(screenshot, sizeChecks > 0 ? 600 : 0);
        }

        private int attempts;
        private void screenshot() => host.TakeScreenshotAsync().ContinueWith(task =>
        {
            bool retry = false;
            try
            {
                using Image<Rgba32> image = task.GetAwaiter().GetResult();
                // A frame from before the resize leaves an unrendered black strip along the bottom edge.
                bool stale = image.Width != width || image.Height != height || image[4, image.Height - 4] is { R: 0, G: 0, B: 0 };
                if (stale && attempts++ < 8) { retry = true; return; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                image.SaveAsPng(outputPath);
                ok();
            }
            catch (Exception error) { fail(error); }
            finally
            {
                if (retry) Schedule(() => Scheduler.AddDelayed(screenshot, 500));
                else host.Exit();
            }
        }, TaskScheduler.Default);
    }
}
