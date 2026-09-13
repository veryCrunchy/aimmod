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
    [Test, Explicit("Plays a warmup through the embedded osu! player and verifies the result handoff.")]
    [SupportedOSPlatform("windows")]
    public async Task WarmupUsesEmbeddedPlayerAndReturnsToNextDrill()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "warmup", "playback.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new WarmupPlaybackGame(host, path, ok, fail), TimeSpan.FromSeconds(65));
    }

    private sealed partial class WarmupPlaybackGame(GameHost host, string path, Action ok, Action<Exception> fail)
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
        private void check() => Scheduler.AddDelayed(async () => {
            try {
                var player = (NativeTrainerPlayer?)typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this);
                if (player?.Ready == true && player.CurrentTime > 2200 && !sawPlayback)
                {
                    Assert.That(Beatmap.Value.Track.IsRunning, Is.True);
                    Assert.That(LocalConfig.Get<bool>(OsuSetting.MenuVoice), Is.False);
                    Assert.That(player.PracticeBeatmap.HitObjects.Count, Is.GreaterThanOrEqualTo(12));
                    using var image = await host.TakeScreenshotAsync();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); image.SaveAsPng(path);
                    sawPlayback = true;
                }
                if (sawPlayback && player is null)
                {
                    Assert.That(plan.Step, Is.EqualTo(1), "A completed difficult drill must still advance with easier demand.");
                    Assert.That(plan.Pace, Is.LessThan(1));
                    Assert.That(plan.Results[0].WarmupRun!.SessionId, Is.EqualTo(plan.Id));
                    var history = (TrainerHistoryStore)typeof(NativeTrainersWorkspace).GetField("warmupStore", flags)!.GetValue(workspace)!;
                    Assert.That(history.Load().Count, Is.EqualTo(1));
                    ok(); host.Exit(); return;
                }
                check();
            } catch (Exception e) { fail(e); host.Exit(); }
        }, 250);
    }
}
