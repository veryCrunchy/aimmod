using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [Test, Explicit("Plays a tapping drill with Autopilot through the embedded osu! player using key presses only.")]
    [SupportedOSPlatform("windows")]
    public async Task AutopilotTappingDrillHitsNotesWithoutMouseMovement()
    {
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new AutopilotPlaybackGame(host, ok, fail), TimeSpan.FromSeconds(60));
    }

    private sealed partial class AutopilotPlaybackGame(GameHost host, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private NativeTrainersWorkspace workspace = null!;
        private bool sawPlayback;
        private int lastNote = -1;
        private int polls;

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(1100, 760));
            Scheduler.AddDelayed(() =>
            {
                try
                {
                    // Synthetic stable controls keep the developer's own osu! configuration out of the run.
                    string root = Storage.GetFullPath("autopilot-fixture", true); Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, $"osu!.{Environment.UserName}.cfg"), "keyOsuLeft = A\nkeyOsuRight = S\nOffset = 0\nMouseDisableButtons = 1\nFullscreen=0");
                    typeof(AimModGame).GetField("trainerStableRoot", flags)!.SetValue(this, root);
                    typeof(AimModGame).GetField("trainerLazerRoot", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("beatmapDestinationService", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetMethod("showTrainers", flags)!.Invoke(this, null);
                    workspace = (NativeTrainersWorkspace)typeof(AimModGame).GetField("trainersWorkspace", flags)!.GetValue(this)!;
                    typeof(NativeTrainersWorkspace).GetField("preferences", flags)!.SetValue(workspace, new TrainerWorkspacePreferences(AdaptiveDifficulty: false, Autopilot: true));
                    typeof(NativeTrainersWorkspace).GetField("settings", flags)!.SetValue(workspace,
                        new TrainerSettings(TrainerKind.Steady, Bpm: 100, Seconds: 15, Autopilot: true));
                    Scheduler.AddDelayed(() =>
                    {
                        try { workspace.Start(); check(); }
                        catch (Exception e) { fail(e); host.Exit(); }
                    }, 800);
                }
                catch (Exception e) { fail(e); host.Exit(); }
            }, 1300);
        }

        protected override void Update()
        {
            // A private desktop has no foreground window; supply focus and real key input. The mouse is never moved.
            ((osu.Framework.Bindables.Bindable<bool>)host.IsActive).Value = true;
            // Hover, and with it hit detection, only runs while the OS cursor is inside the window.
            if (host.Window?.CursorInWindow is osu.Framework.Bindables.Bindable<bool> inWindow) inWindow.Value = true;
            if (typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this) is NativeTrainerPlayer { Ready: true } player)
            {
                var objects = player.PracticeBeatmap.HitObjects;
                int index = objects.ToList().FindLastIndex(o => o.StartTime <= player.CurrentTime);
                if (index >= 0)
                {
                    var input = GetContainingInputManager()!;
                    var settings = (TrainerSettings)typeof(NativeTrainersWorkspace).GetField("settings", flags)!.GetValue(workspace)!;
                    var key = TrainerSettings.ParseKeys(settings.Keys)[index % 2];
                    if (index != lastNote)
                    {
                        foreach (var k in TrainerSettings.ParseKeys(settings.Keys))
                            new osu.Framework.Input.StateChanges.KeyboardKeyInput(k, false).Apply(input.CurrentState, input);
                        lastNote = index;
                    }
                    new osu.Framework.Input.StateChanges.KeyboardKeyInput(key, player.CurrentTime <= objects[index].StartTime + 40).Apply(input.CurrentState, input);
                }
            }
            base.Update();
        }

        private void check() => Scheduler.AddDelayed(() =>
        {
            try
            {
                if (++polls > 200) throw new TimeoutException("Autopilot gameplay did not finish.");
                var player = (NativeTrainerPlayer?)typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this);
                if (player?.Ready == true && !sawPlayback)
                {
                    Assert.That(SelectedMods.Value.OfType<OsuModAutopilot>(), Is.Not.Empty, "Autopilot must reach the embedded player.");
                    sawPlayback = true;
                }
                if (sawPlayback && player is null)
                {
                    var result = new TrainerHistoryStore(Storage.GetFullPath("trainers/history-0.json", true)).Load()
                        .OrderByDescending(r => r.CompletedAt).First();
                    Assert.That(result.Assisted, Is.True);
                    Assert.That(result.Settings.Autopilot, Is.True);
                    Assert.That(result.Hits, Is.GreaterThanOrEqualTo((int)(result.Notes * .9)),
                        $"Key presses alone should hit the notes: {result.Hits}/{result.Notes}.");
                    Assert.That(SelectedMods.Value.OfType<OsuModAutopilot>(), Is.Empty, "Mods are restored after practice.");
                    ok(); host.Exit(); return;
                }
                check();
            }
            catch (Exception e) { fail(e); host.Exit(); }
        }, 250);
    }
}
