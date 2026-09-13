using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Configuration;
using SixLabors.ImageSharp;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(false), TestCase(true), Explicit("Plays a warmup through the embedded osu! player and verifies the result handoff.")]
    [SupportedOSPlatform("windows")]
    public async Task WarmupUsesEmbeddedPlayerAndReturnsToNextDrill(bool successfulSliders)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "warmup", $"playback-{successfulSliders}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new WarmupPlaybackGame(host, path, successfulSliders, ok, fail), TimeSpan.FromSeconds(65));
    }

    private sealed partial class WarmupPlaybackGame(GameHost host, string path, bool successfulSliders, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private NativeTrainersWorkspace workspace = null!;
        private TrainerWarmup plan = null!;
        private bool sawPlayback;
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(1100, 760));
            Scheduler.AddDelayed(() => {
                try {
                    typeof(AimModGame).GetMethod("showTrainers", flags)!.Invoke(this, null);
                    workspace = (NativeTrainersWorkspace)typeof(AimModGame).GetField("trainersWorkspace", flags)!.GetValue(this)!;
                    plan = TrainerWarmup.Create(2, new(), [], [], DateTimeOffset.UtcNow);
                    if (successfulSliders)
                    {
                        Assert.That(plan.Record(TrainerWarmupTests.Result(plan.CurrentSettings(), 86.7)), Is.True);
                    }
                    typeof(NativeTrainersWorkspace).GetField("warmup", flags)!.SetValue(workspace, plan);
                    typeof(NativeTrainersWorkspace).GetField("warmupStore", flags)!.SetValue(workspace,
                        new TrainerHistoryStore(Storage.GetFullPath("warmup-playback/history.json", true)));
                    typeof(NativeTrainersWorkspace).GetField("warmupAccount", flags)!.SetValue(workspace, workspace.CurrentSkillAccountId?.Invoke());
                    workspace.ShowWarmup();
                    typeof(NativeTrainersWorkspace).GetMethod("launchWarmup", flags)!.Invoke(workspace, null);
                    check();
                } catch (Exception e) { fail(e); host.Exit(); }
            }, 1300);
        }
        private int lastInputNote = -1;
        protected override void Update()
        {
            // A private desktop has no foreground window; supply focus and real input.
            ((osu.Framework.Bindables.Bindable<bool>)host.IsActive).Value = true;
            if (successfulSliders && typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this) is NativeTrainerPlayer { Ready: true } player)
            {
                var objects = player.PracticeBeatmap.HitObjects;
                int index = objects.ToList().FindLastIndex(o => o.StartTime <= player.CurrentTime);
                if (index >= 0)
                {
                    var obj = (osu.Game.Rulesets.Osu.Objects.OsuHitObject)objects[index];
                    var position = obj.Position;
                    double end = obj.StartTime + 40;
                    if (obj is osu.Game.Rulesets.Osu.Objects.Slider slider)
                    {
                        end = slider.EndTime;
                        double progress = Math.Clamp((player.CurrentTime - slider.StartTime) / slider.Duration, 0, 1) * (slider.RepeatCount + 1);
                        int span = Math.Min((int)progress, slider.RepeatCount);
                        position += slider.Path.PositionAt(span % 2 == 0 ? progress - span : 1 - (progress - span));
                    }
                    var ruleset = (osu.Game.Rulesets.UI.DrawableRuleset)typeof(osu.Game.Screens.Play.Player)
                        .GetProperty("DrawableRuleset", flags)!.GetValue(player)!;
                    var input = GetContainingInputManager()!;
                    var key = TrainerSettings.ParseKeys(plan.CurrentSettings().Keys)[0];
                    if (index != lastInputNote)
                        new osu.Framework.Input.StateChanges.KeyboardKeyInput(key, false).Apply(input.CurrentState, input);
                    lastInputNote = index;
                    new osu.Framework.Input.StateChanges.MousePositionAbsoluteInput {
                        Position = ruleset.Playfield.GamefieldToScreenSpace(position)
                    }.Apply(input.CurrentState, input);
                    new osu.Framework.Input.StateChanges.KeyboardKeyInput(key, player.CurrentTime <= end).Apply(input.CurrentState, input);
                }
            }
            base.Update();
        }
        private void check() => Scheduler.AddDelayed(async () => {
            try {
                var player = (NativeTrainerPlayer?)typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this);
                if (player?.Ready == true && player.CurrentTime > 2200 && !sawPlayback)
                {
                    Assert.That(Beatmap.Value.Track.IsRunning, Is.True);
                    Assert.That(LocalConfig.Get<bool>(OsuSetting.MenuVoice), Is.False);
                    Assert.That(player.PracticeBeatmap.HitObjects.Count, Is.GreaterThanOrEqualTo(12));
                    if (successfulSliders) Assert.That(player.PracticeBeatmap.HitObjects.OfType<osu.Game.Rulesets.Osu.Objects.Slider>().Any(), Is.True);
                    using var image = await host.TakeScreenshotAsync();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); image.SaveAsPng(path);
                    sawPlayback = true;
                }
                if (sawPlayback && player is null)
                {
                    Assert.That(plan.Step, Is.EqualTo(successfulSliders ? 2 : 1), "A completed drill must advance, including successful slider runs.");
                    if (successfulSliders)
                    {
                        Assert.That(plan.Results[^1].Accuracy, Is.GreaterThan(98));
                        Assert.That(plan.Results[^1].Hits, Is.EqualTo(plan.Results[^1].Notes));
                        await Task.Delay(700); // Allow the restored workspace to render.
                        using var resultShot = await host.TakeScreenshotAsync();
                        resultShot.SaveAsPng(Path.ChangeExtension(path, ".results.png"));
                    }
                    else Assert.That(plan.Pace, Is.LessThan(1));
                    Assert.That(plan.Results[0].WarmupRun!.SessionId, Is.EqualTo(plan.Id));
                    var history = (TrainerHistoryStore)typeof(NativeTrainersWorkspace).GetField("warmupStore", flags)!.GetValue(workspace)!;
                    Assert.That(history.Load().Count, Is.EqualTo(1));
                    Assert.That(history.Load()[0].Accuracy, Is.EqualTo(plan.Results[^1].Accuracy));
                    ok(); host.Exit(); return;
                }
                check();
            } catch (Exception e) { fail(e); host.Exit(); }
        }, 250);
    }
}
