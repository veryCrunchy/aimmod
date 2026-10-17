using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [Test]
    [Explicit("Plays a synthetic map through adaptive practice on a private desktop and writes visual-review artifacts.")]
    [SupportedOSPlatform("windows")]
    public async Task AdaptivePracticeFailsASectionThenRetriesItSlower()
    {
        string captures = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, failure) =>
            new AdaptivePracticeGame(host, captures, false, success, failure), TimeSpan.FromSeconds(60));
    }

    [Test]
    [Explicit("Plays a synthetic map through adaptive practice on a private desktop and writes visual-review artifacts.")]
    [SupportedOSPlatform("windows")]
    public async Task AdaptivePracticeEndsADrillAfterItsSectionAndMovesOn()
    {
        string captures = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, failure) =>
            new AdaptivePracticeGame(host, captures, true, success, failure), TimeSpan.FromSeconds(60));
    }

    /// <param name="autoplay">Aims at and taps every circle on time instead of missing every circle.</param>
    private sealed partial class AdaptivePracticeGame(GameHost host, string captures, bool autoplay, Action success, Action<Exception> failure)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private LocalReplay selection = null!;
        private int stage, polls;

        protected override void LoadComplete()
        {
            base.LoadComplete();
            LocalConfig.SetValue(OsuSetting.AudioOffset, 0d);
            Scheduler.AddDelayed(() =>
            {
                try
                {
                    var stableRoot = Storage.GetFullPath("synthetic-controls", true);
                    Directory.CreateDirectory(stableRoot);
                    File.WriteAllText(Path.Combine(stableRoot, "osu!." + Environment.UserName + ".cfg"), "keyOsuLeft = A\nkeyOsuRight = S\nOffset = 0");
                    typeof(AimModGame).GetField("trainerStableRoot", flags)!.SetValue(this, stableRoot);
                    typeof(AimModGame).GetField("trainerLazerRoot", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("beatmapDestinationService", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("replayOpenService", flags)!.SetValue(this, new CompositeLocalReplayOpenService());

                    // No input reaches the map, so every circle is missed and osu!'s high drain fails the first section quickly.
                    string directory = Storage.GetFullPath("synthetic-adaptive", true);
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, "music.ogg"), TrainerAudio.Asset("training-midnight-pulse.ogg"));
                    string path = Path.Combine(directory, "adaptive.osu");
                    File.WriteAllText(path, "osu file format v14\n[General]\nAudioFilename: music.ogg\nMode: 0\n[Metadata]\nTitle: Synthetic adaptive\nArtist: AimMod\nCreator: Test\nVersion: Sections\n"
                        + "[Difficulty]\nHPDrainRate:10\nCircleSize:4\nOverallDifficulty:5\nApproachRate:7\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,500,4,1,0,100,1,0\n[HitObjects]\n"
                        + string.Join("\n", Enumerable.Range(0, 120).Select(i => $"{128 + i % 4 * 64},192,{4000 + i * 300},{(i % 8 == 0 ? 5 : 1)},0,0:0:0:0:")) + "\n");
                    selection = new LocalReplay(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Synthetic adaptive", "AimMod", "Sections", "osu", "Practice Player",
                        DateTimeOffset.UtcNow.AddMinutes(-5), 3, .9, 100_000, 50, 4, null, [], false, BeatmapHash: "synthetic-adaptive", BeatmapPath: path, Origin: LocalLibraryOrigin.Stable);

                    typeof(AimModGame).GetMethod("showCoaching", flags)!.Invoke(this, null);
                    typeof(AimModGame).GetMethod("startAdaptivePractice", flags)!.Invoke(this, [selection, null]);
                    Scheduler.AddDelayed(check, 250);
                }
                catch (Exception error) { failure(error); host.Exit(); }
            }, 1500);
        }

        protected override void Update()
        {
            base.Update();
            // A private desktop has no foreground window; gameplay pauses when the game is inactive.
            ((osu.Framework.Bindables.Bindable<bool>)host.IsActive).Value = true;
            if (autoplay) tap();
        }

        private readonly HashSet<double> tapped = [];

        /// <summary>Aims at each circle and taps it on its hit time, alternating keys.</summary>
        private void tap()
        {
            if (typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this) is not AdaptivePracticeScreen screen
                || typeof(AdaptivePracticeScreen).GetField("player", flags)!.GetValue(screen) is not AdaptivePracticePlayer { Ready: true } player) return;
            double now = player.GameplayTime();
            var next = player.Objects().OfType<osu.Game.Rulesets.Osu.Objects.OsuHitObject>()
                .FirstOrDefault(o => !tapped.Contains(o.StartTime) && o.StartTime <= now + 4 && o.StartTime >= now - 40);
            if (next is null) return;
            tapped.Add(next.StartTime);
            var ruleset = (osu.Game.Rulesets.UI.DrawableRuleset)typeof(osu.Game.Screens.Play.Player).GetProperty("DrawableRuleset", flags)!.GetValue(player)!;
            var input = GetContainingInputManager()!;
            var key = tapped.Count % 2 == 0 ? osuTK.Input.Key.A : osuTK.Input.Key.S;
            new osu.Framework.Input.StateChanges.MousePositionAbsoluteInput { Position = ruleset.Playfield.GamefieldToScreenSpace(next.StackedPosition) }.Apply(input.CurrentState, input);
            new osu.Framework.Input.StateChanges.KeyboardKeyInput(key, true).Apply(input.CurrentState, input);
            Scheduler.AddDelayed(() => new osu.Framework.Input.StateChanges.KeyboardKeyInput(key, false).Apply(input.CurrentState, input), 40);
        }

        private void check()
        {
            try
            {
                var screen = typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this) as AdaptivePracticeScreen;
                var plan = screen is null ? null : (AdaptiveAttemptPlan)typeof(AdaptivePracticeScreen).GetField("plan", flags)!.GetValue(screen)!;
                var player = screen is null ? null : typeof(AdaptivePracticeScreen).GetField("player", flags)!.GetValue(screen) as AdaptivePracticePlayer;
                if (autoplay)
                {
                    clean(screen, plan);
                    return;
                }
                switch (stage)
                {
                    case 0 when plan is { Progress.Fails: > 0 } && screen!.IsCurrentScreen():
                        Assert.Multiple(() =>
                        {
                            Assert.That(plan.Sections, Has.Count.GreaterThan(2), "a 36 s map splits into several sections");
                            Assert.That(plan.Progress.Sections[0].Speed, Is.EqualTo(.95), "the failed first section repeats a little slower");
                            Assert.That(plan.Drill, Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 0, 0, plan.Sections[0].StartMs, plan.Sections[0].EndMs, .95)),
                                "the retry is the failed section alone, at one slower speed");
                            Assert.That(player, Is.Null, "the breather replaces the failed attempt");
                        });
                        stage = 1;
                        // Capture after the failed attempt's exit fade and the card's entrance.
                        Scheduler.AddDelayed(() => screenshot("aimmod-adaptive-breather.png", () =>
                            typeof(AdaptivePracticeScreen).GetMethod("startAttempt", flags)!.Invoke(screen, null)), 900);
                        return;
                    case 1 when player is { Ready: true } && player.CurrentTimeAfterStart() > 2_200:
                        var mod = player.GameplayMods().OfType<AdaptiveSpeedMod>().Single();
                        Assert.Multiple(() =>
                        {
                            Assert.That(player.FirstObjectTime(), Is.EqualTo(plan!.Sections[0].StartMs), "the retry starts at the failed section");
                            Assert.That(player.LastObjectTime(), Is.LessThanOrEqualTo(plan.Sections[0].EndMs), "and stops after it");
                            Assert.That(mod.Plan, Is.SameAs(plan));
                            Assert.That(mod.SpeedChange.Value, Is.EqualTo(.95).Within(.011), "the retry plays at the reduced speed");
                            Assert.That(Beatmap.Value.Track.AggregateTempo.Value, Is.EqualTo(.95).Within(.011), "the music slows with the map");
                        });
                        stage = 2;
                        screenshot("aimmod-adaptive-retry.png", () => screen!.StopSession());
                        return;
                    case 2 when screen is null:
                        var saved = new AdaptivePracticeStore(Storage.GetFullPath($"coaching/adaptive-0.json", true)).Load(AdaptivePracticeSetup.For(selection).Key);
                        Assert.Multiple(() =>
                        {
                            Assert.That(saved?.Fails, Is.GreaterThanOrEqualTo(1), "progress survives the session");
                            Assert.That(saved?.Sections[0].Speed, Is.LessThan(1));
                            Assert.That(LocalConfig.Get<double>(OsuSetting.AudioOffset), Is.Zero, "session settings are restored");
                        });
                        success(); host.Exit();
                        return;
                }
                if (++polls > 200)
                {
                    var coaching = typeof(AimModGame).GetField("coachingWorkspace", flags)!.GetValue(this);
                    string status = coaching is null ? "no coaching" : (string)coaching.GetType().GetField("practiceRunStatus", flags)!.GetValue(coaching)!;
                    string playing = player is null ? "no player" : $"ready={player.Ready} t={(player.Ready ? player.CurrentTimeAfterStart() : 0):0} health={player.Health():0.00}";
                    throw new TimeoutException($"Adaptive practice stalled at stage {stage}. session={(screen is null ? "none" : "running")} current={screen?.IsCurrentScreen()} "
                        + $"fails={plan?.Progress.Fails} status='{status}' {playing}");
                }
                Scheduler.AddDelayed(check, 250);
            }
            catch (Exception error) { failure(error); host.Exit(); }
        }

        private void clean(AdaptivePracticeScreen? screen, AdaptiveAttemptPlan? plan)
        {
            switch (stage)
            {
                case 0 when plan is { Progress.Attempts: > 0 } && screen!.IsCurrentScreen():
                    var title = (osu.Game.Graphics.Sprites.OsuSpriteText)typeof(AdaptivePracticeScreen).GetField("title", flags)!.GetValue(screen)!;
                    Assert.Multiple(() =>
                    {
                        Assert.That(plan.Progress.Fails, Is.Zero);
                        Assert.That(plan.Progress.Sections[0].ClearedAtFullSpeed, Is.True, "a clean full-speed clear masters an unfailed section");
                        Assert.That(plan.Progress.Sections.Skip(1).All(s => !s.Tried), Is.True, "the drill stopped after its section");
                        Assert.That(plan.Drill, Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 1, 1, plan.Sections[1].StartMs, plan.Sections[1].EndMs, 1)));
                        Assert.That(title.Text.ToString(), Is.EqualTo("Section 2 at 100%"));
                    });
                    stage = 1;
                    Scheduler.AddDelayed(() => screenshot("aimmod-adaptive-cleared.png", () => screen!.StopSession()), 700);
                    return;
                case 1 when screen is null:
                    success(); host.Exit();
                    return;
            }
            if (++polls > 200) throw new TimeoutException($"Adaptive practice stalled at clean stage {stage}.");
            Scheduler.AddDelayed(check, 250);
        }

        private void screenshot(string name, Action then) => host.TakeScreenshotAsync().ContinueWith(task =>
        {
            try
            {
                using Image<Rgba32> image = task.GetAwaiter().GetResult();
                Directory.CreateDirectory(captures);
                image.SaveAsPng(Path.Combine(captures, name));
                Schedule(() => { then(); Scheduler.AddDelayed(check, 250); });
            }
            catch (Exception error) { failure(error); host.Exit(); }
        }, TaskScheduler.Default);
    }
}

internal static class AdaptivePracticePlayerProbe
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static osu.Game.Screens.Play.GameplayState state(AdaptivePracticePlayer player) =>
        (osu.Game.Screens.Play.GameplayState)typeof(osu.Game.Screens.Play.Player).GetProperty("GameplayState", flags)!.GetValue(player)!;

    public static IReadOnlyList<osu.Game.Rulesets.Mods.Mod> GameplayMods(this AdaptivePracticePlayer player) => state(player).Mods;
    public static IReadOnlyList<osu.Game.Rulesets.Objects.HitObject> Objects(this AdaptivePracticePlayer player) => state(player).Beatmap.HitObjects;

    public static double GameplayTime(this AdaptivePracticePlayer player) =>
        ((osu.Game.Screens.Play.GameplayClockContainer)typeof(osu.Game.Screens.Play.Player).GetProperty("GameplayClockContainer", flags)!.GetValue(player)!).CurrentTime;

    public static double Health(this AdaptivePracticePlayer player) => state(player).HealthProcessor.Health.Value;
    public static double LastObjectTime(this AdaptivePracticePlayer player) => state(player).Beatmap.HitObjects[^1].StartTime;
    public static double FirstObjectTime(this AdaptivePracticePlayer player) => state(player).Beatmap.HitObjects[0].StartTime;

    public static double CurrentTimeAfterStart(this AdaptivePracticePlayer player)
    {
        var clock = (osu.Game.Screens.Play.GameplayClockContainer)typeof(osu.Game.Screens.Play.Player).GetProperty("GameplayClockContainer", flags)!.GetValue(player)!;
        return clock.CurrentTime - state(player).Beatmap.HitObjects[0].StartTime + AdaptiveAttemptPlan.LeadInMs;
    }
}
