using System.Runtime.Versioning;
using AimMod.Desktop.Creator;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Game;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(800, 760, "score")]
    [TestCase(1600, 900, "score")]
    [TestCase(800, 760, "editor")]
    [TestCase(1600, 900, "library")]
    [TestCase(800, 760, "entry")]
    [TestCase(800, 760, "return")]
    [TestCase(800, 760, "twitch-search")]
    [TestCase(1600, 900, "twitch-search")]
    [TestCase(800, 760, "twitch-connect")]
    [TestCase(800, 760, "twitch-auto")]
    [TestCase(800, 760, "scrolled")]
    [TestCase(1600, 900, "scrolled")]
    [TestCase(800, 760, "own-connect")]
    [TestCase(800, 760, "own-linked")]
    [TestCase(1600, 900, "own-linked")]
    [TestCase(800, 760, "creator-disabled")]
    [TestCase(800, 760, "local-editor")]
    [TestCase(1600, 900, "local-editor")]
    [Explicit("Uses a private graphics desktop for native footage UI verification.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureFootageWorkspace(int width, int height, string mode)
    {
        string outputPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"footage-{mode}-{width}.png");
        string libraryPath = Path.ChangeExtension(outputPath, ".fixture.json");
        var at = new DateTimeOffset(2026, 5, 1, 18, 20, 0, TimeSpan.FromHours(2));
        var score = new LocalReplay(Guid.NewGuid(), Guid.Empty, Guid.Empty, "Blue Horizon", "Test Artist", "Insane", "osu", "PracticePlayer", at, 5.2, .9876, 912345, 854, 1, 245, ["HD"], false, OnlineScoreId: 456);
        var recording = new FootageRecording(Guid.NewGuid(), "Evening stream", "https://www.twitch.tv/videos/12345", at.AddMinutes(-20), 0, 7200, score.Player);
        FootageRecording[] recordings = mode == "scrolled"
            ? Enumerable.Range(0, 4).Select(i => recording with { Id = Guid.NewGuid(), Location = $"https://www.twitch.tv/videos/{12345 + i}" }).ToArray()
            : [recording];
        await new FootageLibraryStore(libraryPath).SaveAsync(new(1, recordings, []));
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new CaptureFootageGame(host, outputPath, libraryPath, score, width, height, mode, ok, fail), TimeSpan.FromSeconds(45));
        Assert.That(File.Exists(outputPath), Is.True);
        TestContext.AddTestAttachment(outputPath);
    }

    private sealed partial class CaptureFootageGame(GameHost host, string outputPath, string libraryPath,
        LocalReplay score, int width, int height, string mode, Action ok, Action<Exception> fail) : OsuGameBase
    {
        [Resolved] private FrameworkConfigManager frameworkConfig { get; set; } = null!;
        private NativeFootageWorkspace workspace = null!;
        private NativeReplayRouteView? entry;
        private Image<Rgba32>? headerBeforeScroll;
        private int fixedControlsBottom;
        private TwitchCaptureSource? twitchSource;
        [BackgroundDependencyLoader]
        private void load()
        {
            var source = new InMemoryLocalLibrarySource([], [score]);
            var profile = new OsuProfile(42, "PracticePlayer", "", null, null);
            var coverage = new OnlineScoreCoverage(OsuBestScoresFetchStatus.Success, false, DateTimeOffset.UtcNow, "public window", 100, false);
            Drawable screen = workspace = new NativeFootageWorkspace(source, new(libraryPath), () => { }, _ => { }, _ => { },
                (_, _) => Task.FromResult(score), mode == "library" || mode.StartsWith("own-") ? null : score,
                mode.StartsWith("twitch", StringComparison.Ordinal) || mode.StartsWith("own-") ? twitchSource = new TwitchCaptureSource(score, mode is "twitch-connect" or "own-connect") : null,
                mode == "twitch-auto" ? new FootageChannel(score.Player, "streamer") : null,
                accountAccess: mode.StartsWith("own-") ? new CreatorAccountAccess(() => profile,
                    _ => Task.FromResult(new OnlineAccountScoreHistoryResult(profile, [], coverage, coverage)), () => { }) : null);
            if (mode == "creator-disabled")
            {
                workspace.Dispose(); screen = new NativeReplayRouteView(source);
                ((NativeReplayRouteView)screen).OpenFootage();
                Assert.That(typeof(NativeReplayRouteView).GetField("footageButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen), Is.Null);
            }
            if (mode is "entry" or "return")
            {
                workspace.Dispose();
                screen = entry = new NativeReplayRouteView(source, footageFactory: (_, back) => workspace = new NativeFootageWorkspace(
                    source, new(libraryPath), back, _ => { }, _ => { }, (_, _) => Task.FromResult(score), score));
            }
            Add(new Container
            {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(24),
                Child = screen,
            });
        }
        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            if (entry is not null) Scheduler.AddDelayed(() =>
            {
                entry.Children.OfType<AimModButton>().Last().Action!.Invoke();
                Assert.That(entry.Children.Where(c => c is not NativeFootageWorkspace).All(c => c.Alpha == 0), Is.True);
            }, 1000);
            if (mode == "return") Scheduler.AddDelayed(() =>
            {
                workspace.Children.OfType<FillFlowContainer<Drawable>>().Single().Children.OfType<AimModButton>().First().Action!.Invoke();
                Assert.That(entry!.Children.OfType<NativeFootageWorkspace>(), Is.Empty);
                Assert.That(entry.Children.OfType<AimModSectionHeader>().Single().Alpha, Is.EqualTo(1));
            }, 2300);
            if (mode is "editor" or "local-editor") Scheduler.AddDelayed(() => typeof(NativeFootageWorkspace).GetMethod("renderRecordingEditor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(workspace, [null, mode == "local-editor"]), 2000);
            if (mode == "twitch-connect") Scheduler.AddDelayed(() => typeof(NativeFootageWorkspace).GetMethod("connectTwitch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(workspace, null), 1200);
            if (mode == "own-linked")
            {
                Scheduler.AddDelayed(() => typeof(NativeFootageWorkspace).GetMethod("linkOwnAccounts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(workspace, null), 1200);
                Scheduler.AddDelayed(() =>
                {
                    var saved = new FootageLibraryStore(libraryPath).LoadAsync().GetAwaiter().GetResult();
                    Assert.That(saved.Channels.Single(), Is.EqualTo(new FootageChannel("PracticePlayer", "viewer", 42, "7")));
                    Assert.That(saved.Recordings.Length, Is.EqualTo(2));
                    Assert.That(twitchSource!.LastArchiveChannel, Is.EqualTo("viewer"));
                }, 2800);
            }
            if (mode is "twitch-search" or "twitch-auto")
            {
                if (mode == "twitch-search") Scheduler.AddDelayed(() => typeof(NativeFootageWorkspace).GetMethod("findTwitch",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(workspace, [score, "streamer"]), 1200);
                Scheduler.AddDelayed(() =>
                {
                    var saved = new FootageLibraryStore(libraryPath).LoadAsync().GetAwaiter().GetResult();
                    Assert.That(saved.Recordings.Length, Is.EqualTo(2), "An existing manually aligned VOD must not be imported again.");
                    Assert.That(saved.Channels.Single(), Is.EqualTo(new FootageChannel(score.Player, "streamer")));
                    Assert.That(saved.Recordings.Single(r => r.Location.EndsWith("12345")).WallClockStart, Is.EqualTo(score.PlayedAt.AddMinutes(-20)));
                }, 2600);
            }
            if (mode == "scrolled") Scheduler.AddDelayed(async () =>
            {
                var viewport = (AimModScrollContainer)typeof(NativeFootageWorkspace).GetField("scroll",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(workspace)!;
                fixedControlsBottom = (int)Math.Floor(viewport.ScreenSpaceDrawQuad.TopLeft.Y) - 1;
                headerBeforeScroll = await host.TakeScreenshotAsync();
                Schedule(() =>
                {
                    viewport.ScrollToEnd(false);
                    Scheduler.AddDelayed(() => Assert.That(viewport.Current, Is.GreaterThan(0), "The regression capture must actually scroll."), 200);
                });
            }, 1600);
            Scheduler.AddDelayed(() => host.TakeScreenshotAsync().ContinueWith(task =>
            {
                try
                {
                    using Image<Rgba32> image = task.GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); image.SaveAsPng(outputPath);
                    if (mode == "scrolled")
                    {
                        Assert.That(headerBeforeScroll, Is.Not.Null);
                        using var before = headerBeforeScroll!;
                        // The entire fixed header/filter region must remain identical
                        // after moving content underneath it, at both window sizes.
                        Assert.That(fixedControlsBottom, Is.GreaterThan(100));
                        for (int y = 24; y < fixedControlsBottom; y++)
                            for (int x = 24; x < width - 24; x++)
                                Assert.That(image[x, y], Is.EqualTo(before[x, y]), $"Scroll leaked into fixed controls at {x},{y}");
                    }
                    ok();
                }
                catch (Exception e) { fail(e); }
                finally { host.Exit(); }
            }, TaskScheduler.Default), 3500);
        }
    }

    private sealed class TwitchCaptureSource(LocalReplay score, bool disconnected) : ITwitchVodDiscovery
    {
        public string? LastArchiveChannel;
        public bool IsConfigured => true;
        public Task<TwitchAccount?> SavedAccountAsync(CancellationToken token) => Task.FromResult<TwitchAccount?>(disconnected ? null : new("7", "viewer"));
        public Task<TwitchDeviceCode> BeginAsync(CancellationToken token) => Task.FromResult(new TwitchDeviceCode("private-code", "ABCD1234", new("https://www.twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5));
        public async Task<TwitchAccount> CompleteAsync(TwitchDeviceCode code, CancellationToken token) { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<TwitchVodSearch> ListArchivesAsync(string channel, CancellationToken token) { LastArchiveChannel = channel; return FindAsync(channel, score.PlayedAt, token); }
        public Task<TwitchVodSearch> FindAsync(string channel, DateTimeOffset at, CancellationToken token) => Task.FromResult(new TwitchVodSearch(
            [new("12345", "123", "Already aligned stream", score.PlayedAt.AddMinutes(-25), 7200),
             new("67890", "123", "Another broadcast around this play", score.PlayedAt.AddMinutes(-20), 7200)], 1, true, false));
    }
}
