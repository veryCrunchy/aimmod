using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using SixLabors.ImageSharp;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(100, 800, 760)]
    [TestCase(150, 1600, 900)]
    [Explicit("Plays a short synthetic DT map on a private desktop and verifies persisted recovery.")]
    [SupportedOSPlatform("windows")]
    public async Task DtTrainerPlaysOriginalMapAndSavesSpeed(int speed, int width, int height)
    {
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "dt-trainer", $"result-{speed}-{width}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new DtLaunchGame(host, speed, width, height, output, ok, fail), TimeSpan.FromSeconds(50));
        TestContext.AddTestAttachment(output);
    }

    private sealed partial class DtLaunchGame(GameHost host, int speed, int width, int height, string output, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private bool sawPlayer;
        private int polls;
        private DtProgressStore progressStore = null!;
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(async () =>
            {
                try
                {
                    string root = Storage.GetFullPath("dt-fixture", true); Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, $"osu!.{Environment.UserName}.cfg"), "keyOsuLeft = A\nkeyOsuRight = S\nOffset = -37\nMouseDisableButtons = 1");
                    File.WriteAllBytes(Path.Combine(root, "music.ogg"), TrainerAudio.Asset("training-midnight-pulse.ogg"));
                    string path = Path.Combine(root, "original.osu");
                    string map = "osu file format v14\n[General]\nAudioFilename: music.ogg\nMode: 0\n[Metadata]\nTitle: DT practice fixture\nArtist: AimMod\nCreator: Test\nVersion: Original objects\n[Difficulty]\nHPDrainRate:0\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,500,4,1,0,100,1,0\n[HitObjects]\n"
                        + string.Join("\n", Enumerable.Range(0, 22).Select(i => $"{100 + i % 3 * 130},192,{2000 + i * 200},1,0,0:0:0:0:"))
                        + "\n128,192,6600,2,0,L|384:192,1,140\n256,192,7400,8,0,8400\n";
                    File.WriteAllText(path, map);
                    var score = new LocalReplay(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "DT practice fixture", "AimMod", "Original objects", "osu", "", DateTimeOffset.UnixEpoch,
                        3, 0, 0, 0, 0, null, [], false, BeatmapHash: "dt-fixture", BeatmapPath: path, Origin: LocalLibraryOrigin.Stable);
                    var difficulty = new LocalBeatmapDifficulty(score.BeatmapId, 1, score.Difficulty, "osu", 3, 120, 8400, 4, 5, 5, 0, 0,
                        BeatmapHash: score.BeatmapHash, BeatmapPath: path, Origin: LocalLibraryOrigin.Stable);
                    var set = new LocalBeatmapSet(score.SetId, 1, score.Title, score.Artist, "Test", "", DateTimeOffset.UtcNow, null,
                        [difficulty, difficulty with { BeatmapId = Guid.NewGuid(), Name = "Another difficulty", BeatmapHash = "other-fixture", StarRating = 4 }], 0);
                    typeof(AimModGame).GetField("localLibrary", flags)!.SetValue(this, new InMemoryLocalLibrarySource([set], []));
                    typeof(AimModGame).GetField("trainerStableRoot", flags)!.SetValue(this, root);
                    typeof(AimModGame).GetField("trainerLazerRoot", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("beatmapDestinationService", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("replayOpenService", flags)!.SetValue(this, new DtSource(path));
                    progressStore = new DtProgressStore(Storage.GetFullPath("trainers/dt-0.json", true));
                    await progressStore.SaveAsync(new("dt-fixture", speed));
                    Schedule(() =>
                    {
                        typeof(AimModGame).GetMethod("showTrainers", flags)!.Invoke(this, null);
                        var trainers = (NativeTrainersWorkspace)typeof(AimModGame).GetField("trainersWorkspace", flags)!.GetValue(this)!;
                        var entry = (AimMod.Desktop.Visuals.AimModButton)typeof(NativeTrainersWorkspace).GetField("dtEntry", flags)!.GetValue(trainers)!;
                        entry.Action!.Invoke();
                        var workspace = (NativeDtTrainerWorkspace)typeof(AimModGame).GetField("dtTrainerWorkspace", flags)!.GetValue(this)!;
                        int retries = 0, searchStage = 0;
                        void chooseWhenReady()
                        {
                            try
                            {
                            var rows = (osu.Framework.Graphics.Containers.FillFlowContainer<osu.Framework.Graphics.Drawable>)typeof(NativeDtTrainerWorkspace).GetField("maps", flags)!.GetValue(workspace)!;
                            var choices = rows.Children.OfType<AimMod.Desktop.Visuals.AimModChoiceGroup>().SelectMany(group => group.Choices.Children).OfType<AimMod.Desktop.Visuals.AimModButton>().ToArray();
                            var search = (AimMod.Desktop.Visuals.AimModSearchBox)typeof(NativeDtTrainerWorkspace).GetField("search", flags)!.GetValue(workspace)!;
                            if (searchStage == 1)
                            {
                                if (choices.Length > 0 && retries++ < 40) { Scheduler.AddDelayed(chooseWhenReady, 100); return; }
                                Assert.That(choices, Is.Empty, "Search must filter the installed library.");
                                search.Current.Value = ""; searchStage = 2; retries = 0;
                                Scheduler.AddDelayed(chooseWhenReady, 500); return;
                            }
                            if (choices.Length == 0 && retries++ < 40) { Scheduler.AddDelayed(chooseWhenReady, 100); return; }
                            if (searchStage == 0)
                            {
                                search.Current.Value = "nonexistent-search-fixture"; searchStage = 1; retries = 0;
                                Scheduler.AddDelayed(chooseWhenReady, 500); return;
                            }
                            Assert.That(choices.Length, Is.EqualTo(2), "Both map difficulties must be selectable.");
                            typeof(NativeTrainersWorkspace).GetMethod("showSkillTrainers", flags)!.Invoke(trainers, null);
                            entry.Action!.Invoke();
                            Assert.That(typeof(AimModGame).GetField("dtTrainerWorkspace", flags)!.GetValue(this), Is.SameAs(workspace), "Tab switches retain the workspace.");
                            _ = captureStage("browse");
                            choices[0].Action!.Invoke();
                            Scheduler.AddDelayed(async () => {
                                try
                                {
                                    var selected = typeof(NativeDtTrainerWorkspace).GetField("selected", flags)!.GetValue(workspace);
                                    typeof(NativeTrainersWorkspace).GetMethod("showSkillTrainers", flags)!.Invoke(trainers, null);
                                    entry.Action!.Invoke();
                                    Assert.That(typeof(NativeDtTrainerWorkspace).GetField("selected", flags)!.GetValue(workspace), Is.SameAs(selected));
                                    var change = (AimMod.Desktop.Visuals.AimModButton)typeof(NativeDtTrainerWorkspace).GetField("changeMap", flags)!.GetValue(workspace)!;
                                    var resume = (AimMod.Desktop.Visuals.AimModButton)typeof(NativeDtTrainerWorkspace).GetField("returnToSession", flags)!.GetValue(workspace)!;
                                    change.Action!.Invoke(); resume.Action!.Invoke();
                                    await captureStage("selected");
                                    typeof(NativeDtTrainerWorkspace).GetMethod("start", flags)!.Invoke(workspace, null);
                                }
                                catch (Exception error) { fail(error); host.Exit(); }
                            }, 800);
                            Scheduler.AddDelayed(check, 900);
                            }
                            catch (Exception error) { fail(error); host.Exit(); }
                        }
                        Scheduler.AddDelayed(chooseWhenReady, 700);
                    });
                }
                catch (Exception error) { fail(error); host.Exit(); }
            }, 1200);
        }
        private async Task captureStage(string stage)
        {
            using var image = await host.TakeScreenshotAsync();
            string path = Path.Combine(Path.GetDirectoryName(output)!, $"{stage}-{width}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); image.SaveAsPng(path);
        }
        private async void check()
        {
            try
            {
                if (++polls > 110) throw new TimeoutException("DT gameplay did not finish.");
                var player = (NativeTrainerPlayer?)typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this);
                if (player?.Ready == true)
                {
                    sawPlayer = true;
                    Assert.That(Beatmap.Value.Beatmap.HitObjects.Count, Is.EqualTo(24));
                    Assert.That(player.PracticeBeatmap.HitObjects.OfType<Slider>().Count(), Is.EqualTo(1));
                    Assert.That(player.PracticeBeatmap.HitObjects.OfType<Spinner>().Count(), Is.EqualTo(1));
                    if (speed > 100) Assert.That(SelectedMods.Value.OfType<OsuModDoubleTime>().Single().SpeedChange.Value, Is.EqualTo(speed / 100d));
                }
                if (sawPlayer && player is null)
                {
                    var saved = await progressStore.LoadAsync("dt-fixture");
                    if (saved.History.Length > 0)
                    {
                        Assert.That(saved.Speed, Is.EqualTo(Math.Max(100, speed - 5)));
                        Assert.That(saved.History.Single().Speed, Is.EqualTo(speed));
                        using var image = await host.TakeScreenshotAsync();
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!); image.SaveAsPng(output);
                        ok(); host.Exit(); return;
                    }
                }
                Scheduler.AddDelayed(check, 250);
            }
            catch (Exception error) { fail(error); host.Exit(); }
        }
    }
    private sealed class DtSource(string path) : ILocalReplayOpenService
    {
        public Task<IPlayableReplayBundle> OpenAsync(LocalReplay replay, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IBeatmapSourceLease> OpenBeatmapSourceAsync(LocalReplay replay, CancellationToken cancellationToken = default) => Task.FromResult<IBeatmapSourceLease>(new DtLease(path));
    }
    private sealed record DtLease(string BeatmapPath) : IBeatmapSourceLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
