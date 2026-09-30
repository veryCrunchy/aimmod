using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
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

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("beatmaps-installed", 1920, 1080)]
    [TestCase("beatmaps-installed", 1280, 800)]
    [TestCase("beatmaps-installed", 900, 700)]
    [TestCase("beatmaps-online", 1920, 1080)]
    [TestCase("beatmaps-online", 1280, 800)]
    [TestCase("beatmaps-online", 900, 700)]
    [TestCase("beatmaps-menu", 1280, 800)]
    [TestCase("beatmaps-menu", 900, 700)]
    [TestCase("beatmaps-filtered", 1280, 800)]
    [TestCase("beatmaps-popover", 1280, 800)]
    [TestCase("beatmaps-popover", 900, 700)]
    [TestCase("beatmaps-details", 900, 700)]
    [TestCase("beatmaps-inspector", 1920, 1080)]
    [TestCase("beatmaps-inspector", 1280, 800)]
    [TestCase("beatmaps-online-expanded", 900, 700)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureBeatmapsWorkspace(string route, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures");
        string outputPath = Path.Combine(root, $"aimmod-{route}-{width}x{height}.png");
        InMemoryLocalLibrarySource library = BeatmapCaptureFixture.CreateLibrary(Path.Combine(root, "beatmaps-fixture"));

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => new CaptureBeatmapsWorkspaceGame(host, library, outputPath, route, width, height, succeeded, failed),
            TimeSpan.FromSeconds(40));

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);
        Assert.That(image.Width, Is.EqualTo(width));
        TestContext.AddTestAttachment(outputPath, $"AimMod {route} captured on a private Windows desktop");
    }

    private sealed partial class CaptureBeatmapsWorkspaceGame(
        GameHost host, ILocalLibrarySource source, string outputPath, string route, int width, int height,
        Action succeeded, Action<Exception> failed) : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private NativeBeatmapDiscoveryScreen screen = null!;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            // Mirror the application shell: the sidebar width follows the window width.
            float sidebar = AimModLayout.SidebarWidth(AimModLayout.SelectSidebarMode(width));
            screen = new NativeBeatmapDiscoveryScreen(source, () => new BeatmapCaptureFixture.Catalog(), () => null,
                () => new BeatmapCaptureFixture.Calculator(), () => null, (_, _) => Task.CompletedTask, _ => { })
            {
                RelativeSizeAxes = Axes.Both,
            };
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas });
            Add(new Box { RelativeSizeAxes = Axes.Y, Width = sidebar, Colour = AimModPalette.Header });
            Add(new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = AimModVisualStyle.PagePaddingFor(sidebar),
                Child = screen,
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (route == "beatmaps-inspector")
                Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        var browser = (NativeInstalledBeatmapBrowser)screen.GetActiveScreenForTesting()!;
                        object inspector = typeof(NativeInstalledBeatmapBrowser).GetField("inspector", flags)!.GetValue(browser)!;
                        var scroll = (AimModScrollContainer)inspector.GetType().GetField("scroll", flags)!.GetValue(inspector)!;
                        scroll.ScrollTo(width > 1600 ? 360 : 440, false);
                    }
                    catch (Exception error) { failed(error); host.Exit(); }
                }, 3300);
            if (route == "beatmaps-online-expanded")
                Scheduler.AddDelayed(() => screen.OpenSet(700_002), 400);
            if (route == "beatmaps-online")
                Scheduler.AddDelayed(() => screen.SelectTab(NativeBeatmapDiscoveryScreen.BeatmapDiscoveryTab.Online), 400);
            if (route == "beatmaps-filtered")
                Scheduler.AddDelayed(() =>
                {
                    var browser = (NativeInstalledBeatmapBrowser)screen.GetActiveScreenForTesting()!;
                    browser.ApplyFiltersForTesting("xi", 4, 8, NativeInstalledBeatmapBrowser.BpmFilter.Above200);
                }, 1200);
            if (route == "beatmaps-popover")
                Scheduler.AddDelayed(() =>
                {
                    var browser = (NativeInstalledBeatmapBrowser)screen.GetActiveScreenForTesting()!;
                    browser.ApplyFiltersForTesting(string.Empty, 0, 10, NativeInstalledBeatmapBrowser.BpmFilter.From160To200);
                    browser.SetFilterPopoverForTesting(true);
                }, 1600);
            if (route == "beatmaps-details")
                Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        var browser = (NativeInstalledBeatmapBrowser)screen.GetActiveScreenForTesting()!;
                        var rows = (FillFlowContainer<Drawable>)typeof(NativeInstalledBeatmapBrowser).GetField("setRows", flags)!.GetValue(browser)!;
                        ((ClickableContainer)rows.Children[2]).TriggerClick();
                        Assert.That(browser.DetailsOpenForTesting, Is.True, "Clicking a row in a narrow window opens its details.");
                    }
                    catch (Exception error) { failed(error); host.Exit(); }
                }, 1800);
            if (route == "beatmaps-menu")
                Scheduler.AddDelayed(() =>
                {
                    try
                    {
                        var browser = (NativeInstalledBeatmapBrowser)screen.GetActiveScreenForTesting()!;
                        var dropdown = (osu.Framework.Graphics.UserInterface.Dropdown<LocalLibrarySort>)typeof(NativeInstalledBeatmapBrowser)
                            .GetField("sortDropdown", flags)!.GetValue(browser)!;
                        var menu = (osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<LocalLibrarySort>)
                            .GetField("Menu", flags)!.GetValue(dropdown)!;
                        menu.Open();
                    }
                    catch (Exception error) { failed(error); host.Exit(); }
                }, 2600);
            Scheduler.AddDelayed(capture, 4200);
        }

        private int captureAttempts;

        private void capture()
        {
            // The window can apply its requested size a little late; never capture the default size.
            if (host.Window is { } window && window.ClientSize.Width != width && captureAttempts++ < 10)
            {
                Scheduler.AddDelayed(capture, 400);
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
                catch (Exception error) { failed(error); }
                finally { host.Exit(); }
            }, TaskScheduler.Default);
        }
    }
}

/// <summary>Synthetic library, catalog and PP calculator for beatmap workspace captures.</summary>
internal static class BeatmapCaptureFixture
{
    private static readonly (string Title, string Artist, string Mapper, double Bpm, int Seconds, double[] Stars)[] sets =
    [
        ("Blue Zenith", "xi", "Asphyxia", 200, 245, [3.1, 4.4, 5.6, 6.9, 7.8]),
        ("Hana ni Natte", "Miyuki Nakajima", "Delis", 176, 212, [2.2, 3.6, 4.9, 5.7]),
        ("Light", "Camellia", "Fuycho", 180, 330, [4.1, 5.3, 6.4]),
        ("RE:RE:RE:START", "Camellia", "Mir", 192, 188, [1.8, 2.9, 3.9, 4.8, 5.9, 6.6, 7.4]),
        ("Redemption", "LeaF", "Nevo", 172, 164, [5.1]),
        ("Parousia", "xi", "Ashaasoki", 230, 359, [6.2, 7.1, 8.2]),
        ("Sidetracked Day", "VINXIS", "Sotarks", 180, 432, [3.3, 4.2, 5.4, 6.3]),
        ("Kimi no Bouken", "Ikimonogakari", "Sotarks", 150, 86, [1.6, 2.4, 3.1]),
        ("Galaxy Collapse", "Kurokotei", "Mismagius", 270, 226, [4.8, 6.1, 7.2, 8.6]),
        ("Freedom Dive", "xi", "Nakagawa-Kanon", 222, 263, [3.4, 5.1, 6.0, 7.0]),
        ("Snow Drive", "Tanchiky", "Kenterz", 186, 138, [2.6, 3.8, 4.6, 5.3]),
        ("Ascension to Heaven", "xi", "Nakagawa-Kanon", 158, 380, [4.4, 5.6]),
    ];

    public static InMemoryLocalLibrarySource CreateLibrary(string artworkDirectory)
    {
        Directory.CreateDirectory(artworkDirectory);
        var beatmapSets = new List<LocalBeatmapSet>();
        var replays = new List<LocalReplay>();
        DateTimeOffset now = DateTimeOffset.Now;
        string[] names = ["Easy", "Normal", "Hard", "Insane", "Extra", "Expert", "Extreme", "Overdose", "Another"];

        for (int index = 0; index < sets.Length; index++)
        {
            var entry = sets[index];
            string background = index % 4 == 3 ? string.Empty : artwork(artworkDirectory, index);
            LocalBeatmapDifficulty[] difficulties = entry.Stars.Select((stars, difficultyIndex) => new LocalBeatmapDifficulty(
                Guid.NewGuid(),
                300_000 + index * 20 + difficultyIndex,
                entry.Stars.Length == 1 ? "Insane" : names[Math.Min(names.Length - 1, (int)Math.Clamp(stars - 1.2, 0, 8))],
                "osu",
                stars,
                entry.Bpm,
                entry.Seconds * 1000d,
                3.6f + (float)stars * 0.1f,
                Math.Min(10, 6.5f + (float)stars * 0.45f),
                Math.Min(10, 6f + (float)stars * 0.45f),
                5,
                null,
                $"fixture-{index}-{difficultyIndex}")).ToArray();
            bool played = index % 3 != 1;
            DateTimeOffset? lastPlayed = played ? now.AddHours(-(index * 17 + 3)) : null;
            beatmapSets.Add(new LocalBeatmapSet(Guid.NewGuid(), 100_000 + index, entry.Title, entry.Artist, entry.Mapper, string.Empty,
                now.AddDays(-index * 3 - 1), lastPlayed, difficulties, played ? 2 + index % 5 : 0, background));

            if (!played)
                continue;
            LocalBeatmapDifficulty target = difficulties[Math.Min(difficulties.Length - 1, difficulties.Length / 2)];
            for (int play = 0; play < 5; play++)
            {
                double accuracy = 0.915 + play * 0.011 + index % 3 * 0.006;
                replays.Add(new LocalReplay(Guid.NewGuid(), beatmapSets[^1].SetId, target.BeatmapId, entry.Title, entry.Artist, target.Name, "osu",
                    "SyntheticPlayer", now.AddDays(-play * 4 - index), target.StarRating, accuracy, 600_000 + play * 40_000, 400 + play * 20,
                    Math.Max(0, 7 - play * 2), 60 + target.StarRating * 34 * accuracy + play * 3, Array.Empty<string>(), true, target.BeatmapHash));
            }
        }

        return new InMemoryLocalLibrarySource(beatmapSets, replays);
    }

    private static string artwork(string directory, int index)
    {
        string path = Path.Combine(directory, $"background-{index}.png");
        if (File.Exists(path))
            return path;
        // Abstract gradients stand in for real backgrounds without shipping third-party art.
        float hue = index * 37 % 360 / 360f;
        using var image = new Image<Rgba32>(480, 270);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            float t = (x + y * 0.6f) / (image.Width + image.Height * 0.6f);
            float wave = MathF.Sin(x * 0.03f + index) * 0.08f + MathF.Cos(y * 0.05f) * 0.06f;
            image[x, y] = hsv(hue + t * 0.18f, 0.55f, Math.Clamp(0.35f + t * 0.45f + wave, 0, 1));
        }
        image.SaveAsPng(path);
        return path;
    }

    private static Rgba32 hsv(float h, float s, float v)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (float r, float g, float b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new Rgba32(r, g, b);
    }

    internal sealed class Calculator : IPpTargetExactCalculationService
    {
        public async Task<IReadOnlyDictionary<int, PpTargetEstimate>> CalculateAsync(IReadOnlyList<PpTargetExactRequest> requests,
            CancellationToken cancellationToken = default, IProgress<PpTargetExactCalculationProgress>? progress = null)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            return requests.ToDictionary(request => request.BeatmapId, request =>
            {
                double stars = 3 + request.BeatmapId % 20 * 0.7;
                double full = Math.Pow(stars, 2.6) * 3.2;
                double expected = full * Math.Pow(request.ExpectedAccuracy, 12);
                return new PpTargetEstimate(expected, full, new(expected * 0.95, expected * 1.05), 1, PpTargetConfidence.High,
                    "fixture", request.BeatmapId);
            });
        }
    }

    internal sealed class Catalog : IOfficialBeatmapDiscoveryClient
    {
        public async Task<OfficialBeatmapSearchResult> GetSetAsync(int beatmapSetId, CancellationToken cancellationToken = default)
        {
            OfficialBeatmapSearchResult all = await SearchAsync(new OfficialBeatmapSearchQuery(), cancellationToken);
            return all with { BeatmapSets = all.BeatmapSets.Where(set => set.BeatmapSetId == beatmapSetId).ToArray(), ServerTotal = 1 };
        }

        public Task<OfficialBeatmapSearchResult> SearchAsync(OfficialBeatmapSearchQuery query, CancellationToken cancellationToken = default)
        {
            string[] statuses = ["ranked", "loved", "ranked", "pending", "ranked", "graveyard", "qualified", "ranked"];
            OfficialBeatmapSet[] results = sets.Take(8).Select((entry, index) => new OfficialBeatmapSet(
                700_000 + index, entry.Title, entry.Title, entry.Artist, entry.Artist, entry.Mapper, string.Empty, statuses[index],
                DateTimeOffset.Now.AddDays(-index * 40), DateTimeOffset.Now.AddDays(-index), 1_200_000 / (index + 1), 9_000 / (index + 1),
                false, index == 5, null, null, null, null,
                entry.Stars.Select((stars, difficultyIndex) => new OfficialBeatmapDifficulty(800_000 + index * 20 + difficultyIndex,
                    difficultyIndex == entry.Stars.Length - 1 ? "Extra" : $"Diff {difficultyIndex + 1}", "osu", stars, entry.Bpm, entry.Seconds,
                    4, 9, 8.5f, 5, 400_000 / (difficultyIndex + 1), 90_000 / (difficultyIndex + 2), 1200)).ToArray())).ToArray();
            return Task.FromResult(new OfficialBeatmapSearchResult(OfficialBeatmapRequestStatus.Success, results, 1_842));
        }

        public Task<OfficialBeatmapDownloadResult> DownloadAsync(int beatmapSetId, string destinationDirectory, bool noVideo = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.NetworkError));
    }
}
