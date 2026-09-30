using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Overlays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

/// <summary>Synthetic six-month history shaped like a real player's score list.</summary>
internal static class StatisticsSyntheticHistory
{
    private static readonly string[] titles =
    [
        "Blue Zenith", "Sidetracked Day", "Parousia", "Redemption", "Light", "Hana ni Natte", "Freedom Dive",
        "Harumachi Clover", "Night of Knights", "Senbonzakura", "Glory Days", "Kimi no Bouken", "Snow Drive",
        "Chronostasis", "Galaxy Collapse", "Cycle Hit", "Ascension to Heaven", "Brain Power", "Crystallized",
        "Everything Will Freeze", "Team Magma", "Sayonara Heaven", "Yomi yori", "Daydream Cafe", "Flower Dance",
        "Remote Control", "Reol - No title", "Lemon", "Unwelcome School", "Save Me",
    ];

    private static readonly string[] artists =
    [
        "xi", "VINXIS", "LeaF", "Camellia", "Nekomata Master", "Miyuki Nakajima", "UNDEAD CORPORATION",
        "DJ Genki", "ZUN", "Kurousa-P", "Hige Driver", "Hanatan", "Sakuzyo", "Dirtywaltz", "Wake Up",
    ];

    private static readonly string[] difficulties = ["Normal", "Hard", "Insane", "Extra", "Expert", "Another", "Hyper"];

    private static readonly string[][] modSets = [[], [], [], [], ["HD"], ["HD"], ["HR"], ["DT"], ["HD", "DT"], ["HD", "HR"], ["NF"]];

    public static IReadOnlyList<LocalReplay> Create(int count = 2_600, int seed = 1734)
    {
        var random = new Random(seed);
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset start = now.AddDays(-182);
        var maps = Enumerable.Range(0, 140).Select(index => new
        {
            SetId = Guid.NewGuid(),
            BeatmapId = Guid.NewGuid(),
            OnlineBeatmapId = 400_000 + index,
            Title = titles[index % titles.Length],
            Artist = artists[index % artists.Length],
            Difficulty = difficulties[index % difficulties.Length],
            Stars = Math.Round(3 + random.NextDouble() * 2.5, 2),
            Ranked = random.NextDouble() > 0.12,
        }).ToArray();

        var result = new List<LocalReplay>(count);
        for (int index = 0; index < count; index++)
        {
            double progress = index / (double)count;
            // Sessions cluster in the evenings with some idle weeks.
            double dayOffset = progress * 182 + random.NextDouble() * 0.9;
            if (progress is > 0.41 and < 0.46)
                dayOffset += 9;
            DateTimeOffset playedAt = start.AddDays(Math.Min(181.9, dayOffset)).AddHours(random.Next(0, 5));
            var map = maps[Math.Min(maps.Length - 1, (int)(Math.Pow(random.NextDouble(), 1.4) * maps.Length))];
            string[] mods = modSets[random.Next(modSets.Length)];
            double stars = map.Stars;
            double skill = 0.885 + progress * 0.045;
            double accuracy = Math.Clamp(skill - (stars - 4.2) * 0.02 + gaussian(random) * 0.035, 0.80, 0.995);
            bool failed = random.NextDouble() < 0.06;
            int misses = Math.Clamp((int)Math.Round(Math.Abs(gaussian(random)) * (1.3 + (0.97 - accuracy) * 70) + (failed ? 8 : 0)), 0, 20);
            double? pp = map.Ranked && !failed && !mods.Contains("NF")
                ? Math.Clamp(Math.Pow(stars, 2.25) * 5.4 * Math.Pow(accuracy, 9) * (1 - misses * 0.025), 20, 140)
                : null;
            bool onlineOnly = random.NextDouble() < 0.2;
            bool submitted = onlineOnly || random.NextDouble() < 0.35;
            result.Add(new LocalReplay(
                Guid.NewGuid(),
                map.SetId,
                map.BeatmapId,
                map.Title,
                map.Artist,
                map.Difficulty,
                "osu",
                "Example Player",
                playedAt,
                Math.Round(stars, 2),
                accuracy,
                (long)(accuracy * 900_000 + random.Next(0, 90_000)),
                (int)(300 + random.NextDouble() * 900 * (1 - misses / 25d)),
                misses,
                pp is { } value ? Math.Round(value, 2) : null,
                mods,
                HasReplayFile: !onlineOnly,
                OnlineScoreId: submitted ? 5_000_000 + index : 0,
                IsLocallyStored: !onlineOnly,
                Origin: onlineOnly ? LocalLibraryOrigin.Online : LocalLibraryOrigin.Lazer,
                OnlineBeatmapId: map.OnlineBeatmapId,
                Passed: !failed));
        }

        return result;
    }

    private static double gaussian(Random random)
    {
        double u1 = 1 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2);
    }
}

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("overview", 1920, 1080)]
    [TestCase("overview", 1280, 800)]
    [TestCase("overview", 900, 700)]
    [TestCase("filters", 1280, 800)]
    [TestCase("filters", 900, 700)]
    [TestCase("filters-menu", 1280, 800)]
    [TestCase("filters-menu", 900, 700)]
    [TestCase("sort-menu", 1280, 800)]
    [TestCase("hover", 1920, 1080)]
    [TestCase("metric-pp", 1280, 800)]
    [TestCase("metric-misses", 1920, 1080)]
    [TestCase("period-30", 1280, 800)]
    [TestCase("details", 900, 700)]
    [TestCase("plays", 1280, 800)]
    [TestCase("empty", 1280, 800)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureStatisticsRedesign(string state, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string outputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "visual-captures",
            $"aimmod-statistics-{state}-{width}x{height}.png");
        var source = new InMemoryLocalLibrarySource([], state == "empty" ? [] : StatisticsSyntheticHistory.Create());

        // A contended graphics device occasionally presents a blank first frame; retry before judging the capture.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await WindowsPrivateDesktopCapture.CaptureAsync(
                (host, succeeded, failed) => new CaptureStatisticsRedesignGame(host, source, outputPath, state, width, height, succeeded, failed),
                TimeSpan.FromSeconds(40));
            using Image<Rgba32> attemptImage = Image.Load<Rgba32>(outputPath);
            if (attemptImage.Width == width && attemptImage.Height == height && countSampledColours(attemptImage) > 8)
                break;
        }

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
            Assert.That(countSampledColours(image), Is.GreaterThan(8));
        });
        TestContext.AddTestAttachment(outputPath, $"AimMod statistics {state}");
    }

    private sealed partial class CaptureStatisticsRedesignGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private NativeStatisticsWorkspace workspace = null!;
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly string state;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureStatisticsRedesignGame(GameHost host, ILocalLibrarySource source, string outputPath, string state,
                                             int width, int height, Action succeeded, Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.state = state;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // Mirror the application shell: sidebar plus the shared page inset.
            float sidebar = Visuals.AimModLayout.SidebarWidth(Visuals.AimModLayout.SelectSidebarMode(width));
            Add(new Box { RelativeSizeAxes = Axes.Y, Width = sidebar, Colour = AimModPalette.Header });
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Depth = 10 });
            Add(new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = Visuals.AimModVisualStyle.PagePaddingFor(sidebar),
                Child = workspace = new NativeStatisticsWorkspace(source, _ => { }, openBeatmap: (_, _) => Task.CompletedTask) { RelativeSizeAxes = Axes.Both },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            // A full-display capture cannot fit a bordered window, so use the borderless mode instead.
            bool fullDisplay = width >= 1920 && height >= 1080;
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, fullDisplay ? WindowMode.Borderless : WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(() =>
            {
                MethodInfo? hook = typeof(NativeStatisticsWorkspace).GetMethod("ApplyCaptureState", BindingFlags.Instance | BindingFlags.NonPublic);
                hook?.Invoke(workspace, [state]);
            }, 1800);
            if (state == "filters-menu")
                Scheduler.AddDelayed(verifyOpenMenuLayering, 2300);
            Scheduler.AddDelayed(capture, 2600);
        }

        /// <summary>An open mods menu must lift its row above the star slider and results, then restore the depth on close.</summary>
        private void verifyOpenMenuLayering()
        {
            try
            {
                var dropdown = (ScoreModFilterDropdown)typeof(NativeStatisticsWorkspace)
                    .GetField("modDropdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                var menu = (osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<string>)
                    .GetField("Menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dropdown)!;
                var popover = (Container)typeof(NativeStatisticsWorkspace)
                    .GetField("filterPopover", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                Drawable row = popover.Children.OfType<Container>().Single(child => child.Children.Any(item => ReferenceEquals(item, dropdown)));
                var siblings = popover.Children.Where(child => !ReferenceEquals(child, row)).ToArray();
                Assert.That(menu.State, Is.EqualTo(osu.Framework.Graphics.UserInterface.MenuState.Open));
                Assert.That(siblings.All(sibling => row.Depth < sibling.Depth), Is.True, "The open menu's row must be above every popover row.");
                menu.Close();
                Assert.That(row.Depth, Is.EqualTo(-2), "Closing the menu restores the row depth.");
                menu.Open();
            }
            catch (Exception error)
            {
                failed(error);
                host.Exit();
            }
        }

        private int captureAttempts;

        private void capture()
        {
            // A contended graphics device can apply the window size late; wait for the real layout.
            if (((int)Math.Round(DrawWidth) != width || (int)Math.Round(DrawHeight) != height) && ++captureAttempts < 20)
            {
                frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
                Scheduler.AddDelayed(capture, 500);
                return;
            }

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
