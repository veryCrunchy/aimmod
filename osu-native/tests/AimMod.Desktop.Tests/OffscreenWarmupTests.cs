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
using osu.Game.Graphics.Sprites;
using SixLabors.ImageSharp;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(800, 760)] [TestCase(1600, 900)]
    [Explicit("Checks warmup navigation and results on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task WarmupNavigationAndResults(int width, int height)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "warmup", $"warmup-{width}");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new WarmupGame(host, width, height, path, ok, fail), TimeSpan.FromSeconds(40));
    }

    private sealed partial class WarmupGame(GameHost host, int width, int height, string path, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private NativeTrainersWorkspace workspace = null!;
        private TrainerSettings? launched;
        private int account = 1;
        private int synced;
        private TrainerHistoryStore history = null!;
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(async () => {
                try
                {
                    history = new TrainerHistoryStore(Storage.GetFullPath("warmup-fixture/history.json", true));
                    workspace = new NativeTrainersWorkspace(() => history, () => { }) {
                        CurrentSkillAccountId = () => account,
                        LaunchOsuSession = (s, mouse, volume) => launched = s,
                        BeginTrainingSync = () => _ => synced++ };
                    var content = (Container)typeof(AimModGame).GetField("content", flags)!.GetValue(this)!;
                    content.Padding = AimModVisualStyle.PagePadding; content.Child = workspace;
                    await Task.Delay(700);
                    Schedule(async () => {
                        try {
                            workspace.ApplyOsuSettings(new("D / F", -25, false, "osu!"));
                            workspace.ShowWarmup();
                            await capture("setup");
                            invoke("prepareWarmup");
                            pollPlan();
                        } catch (Exception e) { fail(e); host.Exit(); }
                    });
                } catch (Exception e) { fail(e); host.Exit(); }
            }, 1000);
        }
        private object? field(string name) => typeof(NativeTrainersWorkspace).GetField(name, flags)!.GetValue(workspace);
        private void invoke(string name) => typeof(NativeTrainersWorkspace).GetMethod(name, flags)!.Invoke(workspace, null);
        private static void setTrainerMenu(Drawable dropdown, bool open) => typeof(CaptureAimModGame)
            .GetMethod("setTrainerMenu", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [dropdown, open]);
        private void pollPlan() => Scheduler.AddDelayed(async () => {
            try {
                if (field("warmup") is not TrainerWarmup plan) { pollPlan(); return; }
                await capture("plan");
                invoke("launchWarmup");
                Assert.That(launched!.Keys, Is.EqualTo("D / F"));
                Assert.That(launched.OffsetMs, Is.EqualTo(-25));
                workspace.CompleteOsuSession(null);
                Assert.That(plan.Step, Is.Zero); Assert.That(history.Load(), Is.Empty);
                for (int i = 0; i < 4; i++) {
                    invoke("launchWarmup");
                    workspace.CompleteOsuSession(TrainerWarmupTests.Result(launched!, i == 0 ? 70 : 94, i == 0 ? 15 : 0));
                    if (i == 0) { Assert.That(plan.Pace, Is.LessThan(1)); await capture("after-drill"); }
                }
                Assert.That(plan.Finished, Is.True);
                Assert.That(history.Load().Count(r => r.WarmupRun?.SessionId == plan.Id), Is.EqualTo(4));
                Assert.That(synced, Is.EqualTo(4));
                await capture("complete");
                var original = (TrainerSettings)field("settings")!;
                Assert.That(original.Kind, Is.EqualTo(TrainerKind.Steady));
                invoke("showSkillTrainers");
                var adaptive = (TrainerSettings)field("settings")!;
                Assert.That(adaptive.AdaptiveDifficulty, Is.True);
                for (int i = 1; i <= 3; i++) history.Add(TrainerWarmupTests.Result(adaptive, 88) with { CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-i) });
                workspace.RefreshHistory();
                await capture("adaptive-setup");
                workspace.Start();
                Assert.That(launched!.OverallDifficulty, Is.EqualTo(5.25));
                Assert.That(launched.SkillLimits, Is.Not.Null);
                Assert.That(((Drawable)field("objectControls")!).Alpha, Is.Zero);
                Assert.That(((Drawable)field("timingControls")!).Alpha, Is.Zero);
                Assert.That(((Drawable)field("practiceOptionsToggle")!).Alpha, Is.Zero);
                var retained = (TrainerSettings)field("settings")!;
                Assert.That(retained.Sliders, Is.EqualTo(adaptive.Sliders));
                Assert.That(retained.Bpm, Is.EqualTo(adaptive.Bpm));
                Assert.That(retained.ApproachRate, Is.EqualTo(adaptive.ApproachRate));
                workspace.CompleteOsuSession(TrainerWarmupTests.Result(launched, 88));
                await capture("adaptive-result");
                invoke("returnToPracticeSettings");
                ((AimModButton)field("adaptiveToggle")!).Action!();
                Assert.That(history.LoadPreferences().AdaptiveDifficulty, Is.False);
                Assert.That(((Drawable)field("objectControls")!).Alpha, Is.EqualTo(1));
                Assert.That(((Drawable)field("timingControls")!).Alpha, Is.EqualTo(1));
                await capture("manual-setup");
                invoke("TogglePracticeOptions");
                Assert.That(((Drawable)field("practiceOptions")!).Alpha, Is.EqualTo(1));
                var patterns = (Drawable)field("patternControls")!;
                var geometry = (Drawable)field("geometryControls")!;
                var scroll = (AimModScrollContainer)field("contentScroll")!;
                ((AimModButton)field("starTargetToggle")!).Action!();
                ((osu.Framework.Bindables.BindableDouble)field("targetMinimum")!).Value = 4;
                ((osu.Framework.Bindables.BindableDouble)field("targetMaximum")!).Value = 5;
                Assert.That(((TrainerSettings)field("settings")!).MinimumStars, Is.EqualTo(4));
                var starControls = (Drawable)field("starControls")!;
                await Task.Delay(350);
                scroll.ScrollTo(scroll.Current + scroll.ToLocalSpace(starControls.ToScreenSpace(osuTK.Vector2.Zero)).Y - 24, false);
                await capture("star-range");
                Assert.That(history.LoadPreferences().MinimumStars, Is.EqualTo(4));
                var rejected = new TrainerStarPlan(new TrainerSettings(MinimumStars:4.1, MaximumStars:5.1, AdaptiveDifficulty:true), new(), .83, false);
                var rejection = Assert.Throws<InvalidOperationException>(() => TrainerStarTarget.RequireTarget(rejected));
                workspace.SetPreparationStatus(false, rejection!.Message, reveal: true);
                var preparationStatus = (osu.Game.Graphics.Containers.OsuTextFlowContainer)field("status")!;
                await Task.Delay(200);
                await capture("star-target-unavailable");
                Assert.That(preparationStatus.DrawHeight, Is.GreaterThan(width == 800 ? 20 : 0));
                Assert.That(preparationStatus.ScreenSpaceDrawQuad.BottomRight.Y, Is.LessThanOrEqualTo(height));
                ((AimModButton)field("starTargetToggle")!).Action!();
                ((AimModDropdown<TrainerSliderStyle>)field("sliderSelector")!).Current.Value = TrainerSliderStyle.Mixed;
                ((AimModDropdown<TrainerSliderShape>)field("sliderShapeSelector")!).Current.Value = TrainerSliderShape.Mixed;
                var objects = (Drawable)field("objectControls")!;
                await Task.Delay(200);
                scroll.ScrollTo(scroll.Current + scroll.ToLocalSpace(objects.ToScreenSpace(osuTK.Vector2.Zero)).Y - 24, false);
                await Task.Delay(200);
                float objectDepth = objects.Depth;
                setTrainerMenu((Drawable)field("sliderShapeSelector")!, true);
                Assert.That(objects.Depth, Is.LessThan(objectDepth));
                await capture("slider-shape-menu");
                setTrainerMenu((Drawable)field("sliderShapeSelector")!, false);
                Assert.That(objects.Depth, Is.EqualTo(objectDepth));
                await Task.Delay(200);
                scroll.ScrollTo(scroll.Current + scroll.ToLocalSpace(patterns.ToScreenSpace(osuTK.Vector2.Zero)).Y - 24, false);
                await Task.Delay(200);
                float patternDepth = patterns.Depth;
                setTrainerMenu((Drawable)field("patternSelector")!, true);
                Assert.That(patterns.Depth, Is.LessThan(geometry.Depth));
                await capture("manual-pattern-menu");
                setTrainerMenu((Drawable)field("patternSelector")!, false);
                Assert.That(patterns.Depth, Is.EqualTo(patternDepth));
                scroll.ScrollTo(0, false);
                workspace.Start();
                Assert.That(launched!.AdaptiveDifficulty, Is.False);
                Assert.That(launched.SkillLimits, Is.Null);
                workspace.CompleteOsuSession(null);
                ((AimModButton)field("adaptiveToggle")!).Action!();
                Assert.That(history.LoadPreferences().AdaptiveDifficulty, Is.True);
                var controls = (Drawable)field("controls")!;
                var music = (Drawable)field("musicControls")!;
                float depth = controls.Depth;
                setTrainerMenu((Drawable)field("durationSelector")!, true);
                Assert.That(controls.Depth, Is.LessThan(music.Depth));
                await capture("menu");
                setTrainerMenu((Drawable)field("durationSelector")!, false);
                Assert.That(controls.Depth, Is.EqualTo(depth));
                workspace.ShowWarmup(); account = 2; workspace.ShowWarmup();
                Assert.That(field("warmup"), Is.Null);
                ok(); host.Exit();
            } catch (Exception e) { fail(e); host.Exit(); }
        }, 150);
        private async Task capture(string stage)
        {
            await Task.Delay(350);
            using var shot = await host.TakeScreenshotAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            shot.SaveAsPng($"{path}-{stage}.png");
        }
    }
}
