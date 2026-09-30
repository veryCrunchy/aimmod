using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Testing;
using AimMod.Desktop.Replays;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Models;
using osu.Game.Replays;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
using osu.Game.Scoring;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vector2 = osuTK.Vector2;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("playback", 1600, 900)]
    [TestCase("playback", 1100, 760)]
    [TestCase("playback", 800, 760)]
    [TestCase("playback", 800, 600)]
    [TestCase("settings", 1600, 900)]
    [TestCase("settings", 800, 760)]
    [TestCase("speed-menu", 1600, 900)]
    [TestCase("speed-menu", 800, 760)]
    [TestCase("mistakes", 1600, 900)]
    [TestCase("mistakes", 800, 760)]
    [TestCase("library", 1100, 760)]
    [TestCase("filter-menu", 1600, 900)]
    [TestCase("filter-menu", 800, 760)]
    [Explicit("Runs the official replay player with a synthetic beatmap on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureReplayPlayback(string scene, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string directory = Environment.GetEnvironmentVariable("AIMMOD_CAPTURE_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures");
        string outputPath = Path.Combine(directory, $"aimmod-replay-{scene}-{width}x{height}.png");
        InMemoryLocalLibrarySource source = await createReplayCaptureLibrary();

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => new CaptureReplayPlaybackGame(host, source, outputPath, scene, width, height, succeeded, failed),
            TimeSpan.FromSeconds(45));

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
            Assert.That(countSampledColours(image), Is.GreaterThan(8));
        });
        TestContext.AddTestAttachment(outputPath, $"AimMod replay {scene}");
    }

    /// <summary>
    /// The populated library with the newest attempt's score statistics matching the synthetic replay
    /// (10 misses of 160 objects), so the summary, analysis and gameplay HUD agree.
    /// </summary>
    private static async Task<InMemoryLocalLibrarySource> createReplayCaptureLibrary()
    {
        InMemoryLocalLibrarySource populated = createPopulatedLibrary();
        LocalBeatmapSet[] sets = (await populated.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 200))).Items.ToArray();
        LocalReplay[] replays = (await populated.SearchReplaysAsync(new LocalLibraryQuery(Limit: 200))).Items.ToArray();
        replays[0] = replays[0] with { MissCount = SyntheticMissIndices.Count, Accuracy = 0.9437, MaxCombo = SyntheticMaxCombo, PerformancePoints = 142.6 };
        return new InMemoryLocalLibrarySource(sets, replays);
    }

    /// <summary>Objects the synthetic replay aims wide of. The longest clean run (objects 62-120) gives a 59x combo.</summary>
    internal static readonly IReadOnlySet<int> SyntheticMissIndices = new HashSet<int> { 5, 12, 19, 26, 33, 40, 47, 54, 61, 121 };

    internal const int SyntheticMaxCombo = 59;

    /// <summary>Synthetic osu!standard map and replay used for capture and headless player tests.</summary>
    internal static (IBeatmap Beatmap, Score Score) CreateSyntheticReplay(osu.Game.Rulesets.RulesetInfo ruleset)
    {
        var info = new BeatmapInfo(ruleset, new BeatmapDifficulty { CircleSize = 4, ApproachRate = 9, OverallDifficulty = 8, DrainRate = 5 },
            new BeatmapMetadata { Title = "Synthetic Capture Stream", Artist = "AimMod", Author = new RealmUser { Username = "AimMod" } })
        {
            DifficultyName = "Capture",
        };
        var beatmap = new osu.Game.Beatmaps.Beatmap { BeatmapInfo = info };
        beatmap.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 400 });
        var frames = new List<ReplayFrame> { new OsuReplayFrame(0, new Vector2(256, 192)) };
        for (int index = 0; index < 160; index++)
        {
            double time = 1_500 + index * 400;
            var position = new Vector2(64 + index * 97 % 384, 48 + index * 61 % 288);
            beatmap.HitObjects.Add(new HitCircle { StartTime = time, Position = position, NewCombo = index % 8 == 0 });
            // A few objects are aimed wide so the replay contains real misses.
            Vector2 aim = SyntheticMissIndices.Contains(index) ? position + new Vector2(90, 0) : position;
            frames.Add(new OsuReplayFrame(time - 180, aim));
            frames.Add(new OsuReplayFrame(time, aim, OsuAction.LeftButton));
            frames.Add(new OsuReplayFrame(time + 60, aim));
        }

        var score = new Score
        {
            ScoreInfo = new ScoreInfo(info, ruleset, new RealmUser { Username = "Synthetic player" }) { Date = DateTimeOffset.Now.AddDays(-1) },
            Replay = new Replay { Frames = frames },
        };
        return (beatmap, score);
    }

    /// <summary>Opens or closes any framework dropdown's menu (the menu property is protected).</summary>
    internal static void SetDropdownMenuOpen(Drawable dropdown, bool open)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly;
        for (Type? type = dropdown.GetType(); type is not null; type = type.BaseType)
        {
            object? value = type.GetProperty("Menu", flags)?.GetValue(dropdown) ?? type.GetField("Menu", flags)?.GetValue(dropdown);
            if (value is osu.Framework.Graphics.UserInterface.Menu menu)
            {
                menu.State = open ? osu.Framework.Graphics.UserInterface.MenuState.Open : osu.Framework.Graphics.UserInterface.MenuState.Closed;
                return;
            }
        }

        throw new AssertionException("Dropdown menu was not found.");
    }

    internal sealed class SyntheticWorkingBeatmap(IBeatmap beatmap, osu.Framework.Audio.AudioManager? audio)
        : WorkingBeatmap(beatmap.BeatmapInfo, audio)
    {
        protected override IBeatmap GetBeatmap() => beatmap;
        protected override osu.Framework.Audio.Track.Track GetBeatmapTrack() => GetVirtualTrack();
        public override osu.Framework.Graphics.Textures.Texture GetBackground() => null!;
        protected override osu.Game.Skinning.ISkin GetSkin() => null!;
        public override Stream GetStream(string storagePath) => Stream.Null;
    }

    private sealed partial class CaptureReplayPlaybackGame : OsuGameBase
    {
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly string scene;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;
        private NativeReplayRouteView route = null!;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureReplayPlaybackGame(GameHost host, ILocalLibrarySource source, string outputPath, string scene, int width, int height, Action succeeded, Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.scene = scene;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            LocalReplay[] replays = source.SearchReplaysAsync(new LocalLibraryQuery(Limit: 200)).AsTask().GetAwaiter().GetResult().Items.ToArray();
            var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [replays[0].ScoreId] = syntheticAnalysis() };
            route = new NativeReplayRouteView(source, analyses, _ => { }, openPractice: _ => { }, openBeatmap: (_, _) => Task.CompletedTask)
            {
                RelativeSizeAxes = Axes.Both,
            };
            // Mirror the application shell: sidebar and the shared page inset.
            Add(new osu.Framework.Graphics.Shapes.Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas });
            Add(new osu.Framework.Graphics.Shapes.Box { RelativeSizeAxes = Axes.Y, Width = Visuals.AimModVisualStyle.SidebarWidth, Colour = AimModPalette.Header });
            Add(new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = Visuals.AimModVisualStyle.PagePadding,
                Child = route,
            });
            route.SetReplaySummary(replays[0]);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(startPlayer, 400);
        }

        private void startPlayer()
        {
            try
            {
                var ruleset = RulesetStore.GetRuleset(0)!;
                (IBeatmap beatmap, Score score) = CreateSyntheticReplay(ruleset);
                Ruleset.Value = ruleset;
                Beatmap.Value = new SyntheticWorkingBeatmap(beatmap, Audio);
                NativeReplayPlayer player = null!;
                player = new NativeReplayPlayer(score, () =>
                {
                    route.ShowReady();
                    route.SeekToMoment(8_100);
                    player.SetPaused(false);
                    Scheduler.AddDelayed(prepareScene, 1_200);
                }, message => failed(new InvalidOperationException(message)));
                route.AttachPlayer(player);
                route.ScreenStack.Push(player);
            }
            catch (Exception error)
            {
                failed(error);
                host.Exit();
            }
        }

        private void prepareScene()
        {
            try
            {
                applyScene();
            }
            catch (Exception error)
            {
                failed(error);
                host.Exit();
                return;
            }

            Scheduler.AddDelayed(() =>
            {
                try
                {
                    verifyOpenPopup();
                }
                catch (Exception error)
                {
                    failed(error);
                    host.Exit();
                    return;
                }

                capture();
            }, 700);
        }

        /// <summary>
        /// With a menu or popover open, every ancestor branch up to the route must be frontmost among its
        /// siblings and unmasked, so the popup draws over the neighbouring rows instead of behind them.
        /// </summary>
        private void verifyOpenPopup()
        {
            (Drawable? owner, Drawable? maskBoundary) = scene switch
            {
                "settings" => ((Drawable?)route.DisplaySettings, (Drawable?)route),
                "speed-menu" => (route.TransportBar.SpeedDropdown, route),
                // The side panel keeps its rounded mask; the menu stays within it and is bounded by the window.
                "filter-menu" => (route.ModFilter, route.SidePanel),
                _ => (null, null),
            };
            if (owner is null)
                return;

            var problems = new List<string>();
            bool masked = false;
            for (Drawable child = owner; child.Parent is { } parent && child != route; child = parent)
            {
                if (parent == maskBoundary)
                    masked = true;
                if (!masked && parent != route && parent is osu.Framework.Graphics.Containers.CompositeDrawable { Masking: true })
                    problems.Add($"{parent.GetType().Name} masks the open popup");
                if (parent is osu.Framework.Graphics.Containers.Container<Drawable> container && container.Children.Contains(child))
                {
                    Drawable? front = container.Children.LastOrDefault(sibling => sibling.IsPresent);
                    if (front != child)
                        problems.Add($"{child.GetType().Name} is drawn behind {front?.GetType().Name} in {parent.GetType().Name}");
                }

                if (parent == route)
                    break;
            }

            if (problems.Count > 0)
                throw new AssertionException("Open popup layering: " + string.Join("; ", problems));
        }

        /// <summary>
        /// Checks the rendered embedded player: osu!'s replay chrome is gone, the gameplay HUD is
        /// shown, and the game surface is a whole 16:9 surface at osu!'s reference size or larger.
        /// </summary>
        private void verifyEmbeddedPlayer()
        {
            NativeReplayPlayer player = route.ScreenStack.CurrentScreen as NativeReplayPlayer
                                        ?? throw new InvalidOperationException("The replay player is not the current screen.");
            var problems = new List<string>();
            if (!player.ReplayChromeRemoved || player.ReplayOverlay.Parent is not null)
                problems.Add("osu!'s replay overlay is still attached");
            if (route.Viewport.ChildrenOfType<osu.Game.Screens.Play.HUD.ReplaySettingsOverlay>().Any(overlay => overlay.IsPresent))
                problems.Add("a replay settings overlay is visible");
            if (route.Viewport.ChildrenOfType<osu.Game.Screens.Play.HUD.SongProgress>().Any(progress => progress.State.Value == osu.Framework.Graphics.Containers.Visibility.Visible))
                problems.Add("osu!'s song progress is visible");
            osu.Game.Screens.Play.HUDOverlay hud = route.Viewport.ChildrenOfType<osu.Game.Screens.Play.HUDOverlay>().Single();
            if (hud.BottomRightElements.Alpha > 0)
                problems.Add("the hold-to-quit button is visible");
            if (!hud.ShowHud.Value)
                problems.Add("the gameplay HUD is hidden");
            if (player.Configuration.ShowLeaderboard)
                problems.Add("the solo leaderboard is enabled");

            osuTK.Vector2 surface = route.ScreenStack.DrawSize;
            if (surface.X < ReplayViewport.ReferenceSize.X - 1 || surface.Y < ReplayViewport.ReferenceSize.Y - 1)
                problems.Add($"the game surface {surface} is smaller than osu!'s reference size");
            if (Math.Abs(surface.X / surface.Y - ReplayViewport.SurfaceAspect) > 0.01f)
                problems.Add($"the game surface {surface} is not 16:9");
            var frame = route.Viewport.SurfaceFrame.ScreenSpaceDrawQuad.AABBFloat;
            var viewportBounds = route.Viewport.ScreenSpaceDrawQuad.AABBFloat;
            if (frame.Left < viewportBounds.Left - 1 || frame.Right > viewportBounds.Right + 1 || frame.Top < viewportBounds.Top - 1 || frame.Bottom > viewportBounds.Bottom + 1)
                problems.Add("the game surface extends beyond the viewport frame");
            var playerBounds = player.ScreenSpaceDrawQuad.AABBFloat;
            if (Math.Abs(playerBounds.Width - frame.Width) > 2 || Math.Abs(playerBounds.Height - frame.Height) > 2)
                problems.Add($"the player ({playerBounds.Size}) does not fill the visible surface ({frame.Size})");

            if (problems.Count > 0)
                throw new AssertionException("Embedded replay player: " + string.Join("; ", problems));
        }

        private void applyScene()
        {
            verifyEmbeddedPlayer();
            switch (scene)
            {
                case "settings":
                    route.OpenPlaybackSettingsForCapture();
                    break;
                case "speed-menu":
                    route.OpenSpeedMenuForCapture();
                    break;
                case "mistakes":
                    route.ShowTabForCapture(ReplaySidePanelTab.Mistakes);
                    break;
                case "library":
                    route.ShowTabForCapture(ReplaySidePanelTab.Library);
                    break;
                case "filter-menu":
                    route.ShowTabForCapture(ReplaySidePanelTab.Library);
                    SetDropdownMenuOpen(route.ModFilter, true);
                    break;
            }
        }

        private static ReplayAnalysisResult syntheticAnalysis()
        {
            ReplayObjectJudgement judgement(int index)
            {
                double time = 1_500 + index * 400;
                bool miss = SyntheticMissIndices.Contains(index);
                string result = miss ? "Miss" : index % 11 == 3 ? "Ok" : index % 23 == 7 ? "Meh" : "Great";
                return new ReplayObjectJudgement(index, null, "HitCircle", time, time, result, "Great", time + (miss ? 150 : 5),
                    miss ? 150 : 5, 1, new ReplayPoint(256, 192), new ReplayPoint(258, 190), index, miss ? 0 : index + 1,
                    miss ? new ReplayMissAnalysis(ReplayMissReason.Overshoot, 90, 40, -10, new ReplayPoint(346, 192), 50, 55,
                        new ReplayPoint(311, 192), 45, true, false, true, 0.4, Confidence: 0.85) : null);
            }

            ReplayObjectJudgement[] judgements = Enumerable.Range(0, 160).Select(judgement).ToArray();
            return new ReplayAnalysisResult(
                ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true,
                ReplayAnalysisProtocol.WallClockTimeoutMs, Array.Empty<int>(), judgements,
                new ReplayJudgementSummary(
                    judgements.Count(j => j.Result == "Great"), judgements.Count(j => j.Result == "Ok"),
                    judgements.Count(j => j.Result == "Meh"), judgements.Count(j => j.Result == "Miss"), 0, 0));
        }

        private void capture()
        {
            host.TakeScreenshotAsync().ContinueWith(task =>
            {
                try
                {
                    using Image<Rgba32> image = task.GetAwaiter().GetResult();
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    image.SaveAsPng(outputPath);
                    succeeded();
                }
                catch (Exception error)
                {
                    failed(error);
                }
                finally
                {
                    host.Exit();
                }
            }, TaskScheduler.Default);
        }
    }
}
