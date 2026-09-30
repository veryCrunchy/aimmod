using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AimMod.Desktop.Skins;
using AimMod.Desktop.Skins.Online;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
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
    [TestCase("skins-installed", 1920, 1080)]
    [TestCase("skins-installed", 1280, 800)]
    [TestCase("skins-installed", 900, 700)]
    [TestCase("skins-stable-selected", 1920, 1080)]
    [TestCase("skins-stable-selected", 1280, 800)]
    [TestCase("skins-no-results", 1280, 800)]
    [TestCase("skins-list", 1280, 800)]
    [TestCase("skins-list", 900, 700)]
    [TestCase("skins-sort-menu", 1280, 800)]
    [TestCase("skins-sort-menu", 900, 700)]
    [TestCase("skins-online", 1920, 1080)]
    [TestCase("skins-online", 1280, 800)]
    [TestCase("skins-online", 900, 700)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureSkinsOnPrivateDesktop(string route, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string outputPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"aimmod-{route}-{width}x{height}.png");
        string fixtureRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", "skins-fixture");
        SkinCaptureFixture fixture = SkinCaptureFixture.Create(fixtureRoot);

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => new CaptureSkinsGame(host, fixture, route, outputPath, width, height, succeeded, failed),
            TimeSpan.FromSeconds(40));

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
            Assert.That(countSampledColours(image), Is.GreaterThan(8), "The capture should contain rendered UI, not a blank frame.");
        });
        TestContext.AddTestAttachment(outputPath, $"AimMod {route} captured on a private Windows desktop");
    }

    private sealed partial class CaptureSkinsGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private readonly GameHost host;
        private readonly SkinCaptureFixture fixture;
        private readonly string route;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;
        private NativeSkinsScreen screen = null!;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureSkinsGame(GameHost host, SkinCaptureFixture fixture, string route, string outputPath, int width, int height, Action succeeded, Action<Exception> failed)
        {
            this.host = host;
            this.fixture = fixture;
            this.route = route;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        private Box sidebarBox = null!;
        private Container page = null!;

        protected override void Update()
        {
            base.Update();
            // Match the app shell: the sidebar mode follows the scaled (logical) width, not window pixels.
            float sidebar = AimModLayout.SidebarWidth(AimModLayout.SelectSidebarMode(page.Parent?.DrawWidth ?? width));
            sidebarBox.Width = sidebar;
            page.Padding = AimModVisualStyle.PagePaddingFor(sidebar);
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            float sidebar = AimModLayout.SidebarWidth(AimModLayout.SelectSidebarMode(width));
            screen = new NativeSkinsScreen(
                fixture.Source,
                fixture.LazerSelectedSkinId,
                fixture.AppliedSkinId,
                (_, token) => Task.Delay(50, token),
                fixture.CreateOnlineBackend(),
                null,
                fixture.SaveDirectory)
            {
                RelativeSizeAxes = Axes.Both,
            };
            Add(new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                    sidebarBox = new Box { RelativeSizeAxes = Axes.Y, Width = sidebar, Colour = AimModPalette.Header },
                    page = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = AimModVisualStyle.PagePaddingFor(sidebar),
                        Child = screen,
                    },
                },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            if (route == "skins-online")
                Scheduler.AddDelayed(() => screen.SelectTabForTesting(NativeSkinsScreen.SkinsWorkspaceTab.Online), 300);
            if (route == "skins-sort-menu")
                Scheduler.AddDelayed(() => SkinCaptureFixture.OpenFirstMenu(screen), 2500);
            if (route == "skins-stable-selected")
                Scheduler.AddDelayed(() => SkinCaptureFixture.SelectCard(screen, "idke"), 2500);
            if (route == "skins-list")
                Scheduler.AddDelayed(() => ((osu.Framework.Bindables.Bindable<NativeSkinsScreen.SkinListDensity>)typeof(NativeSkinsScreen)
                    .GetField("density", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!).Value = NativeSkinsScreen.SkinListDensity.List, 1500);
            if (route == "skins-no-results")
                Scheduler.AddDelayed(() => SkinCaptureFixture.Search(screen, "no such skin"), 1500);
            Scheduler.AddDelayed(capture, 5000);
        }

        private int resizeAttempts;

        private void capture()
        {
            // The window resize is applied asynchronously and can lag on a busy machine; wait for it.
            if (host.Window is { } window && (window.ClientSize.Width != width || window.ClientSize.Height != height) && resizeAttempts++ < 10)
            {
                frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
                Scheduler.AddDelayed(capture, 1000);
                return;
            }

            host.TakeScreenshotAsync().ContinueWith(task =>
            {
                bool retry = false;
                try
                {
                    using Image<Rgba32> image = task.GetAwaiter().GetResult();
                    // The framebuffer can still be the default size right after a resize; capture again.
                    if ((image.Width != width || image.Height != height) && resizeAttempts++ < 10)
                    {
                        retry = true;
                        Schedule(() =>
                        {
                            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
                            Scheduler.AddDelayed(capture, 1000);
                        });
                        return;
                    }
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
                    if (!retry)
                        host.Exit();
                }
            }, TaskScheduler.Default);
        }
    }
}

/// <summary>
/// Synthetic skins with generated artwork: messy real-world names, both lazer (hashed store)
/// and stable (folder) layouts, @2x and 1x elements, and skins that lack most elements.
/// </summary>
internal sealed class SkinCaptureFixture
{
    private sealed record Spec(
        string Name,
        string Author,
        bool Lazer,
        Rgba32[] Combo,
        Rgba32 Accent,
        bool HighResolution = false,
        bool Elements = true,
        bool Background = true,
        string Version = "2.7",
        bool BuiltIn = false);

    private static readonly Spec[] specs =
    [
        new("-       《CK》 WhiteCat 3.0 ~ DT [-       《CK》 WhiteCat 3.0 ~ DT]", "Cookiezi & WhiteCat", true,
            [rgb(255, 255, 255), rgb(255, 170, 200)], rgb(255, 120, 170), HighResolution: true),
        new("- # BTMC |  ?Freedom Dive  ↓? [- # BTMC ?Freedom Dive ↓?]", "BTMC", true,
            [rgb(70, 140, 255), rgb(40, 210, 255), rgb(130, 90, 255)], rgb(70, 170, 255)),
        new("《CK》 Bacon boi 1.0 『blue』 [- 《CK》 Bacon boi 1.0 『blue』 (2)]", "Bacon", true,
            [rgb(40, 120, 255), rgb(20, 60, 190)], rgb(90, 160, 255), HighResolution: true),
        new("osu! \"argon\" (2022)", "team osu!", true, [rgb(255, 102, 170)], rgb(255, 102, 170), Elements: false, Background: false, BuiltIn: true),
        new("osu! \"classic\" (2013)", "team osu!", true, [rgb(255, 192, 0), rgb(0, 202, 0), rgb(18, 124, 255), rgb(242, 24, 57)], rgb(255, 192, 0), Elements: false, Background: false, BuiltIn: true),
        new("!!! Aristia(Edit) [!!! Aristia(Edit)]", "Aristia, edited by Shigetora", true,
            [rgb(255, 128, 64), rgb(255, 200, 80)], rgb(255, 150, 60)),
        new("Rafis HDDT [rafis_hddt_v3]", "Rafis", true, [rgb(230, 230, 230), rgb(255, 60, 60)], rgb(255, 60, 60), Background: false),
        new("- idke 1.2 -", "idke", false, [rgb(120, 255, 170), rgb(60, 200, 120)], rgb(80, 230, 150), HighResolution: true),
        new("_Cookiezi 32 (Chocomint)", "Unknown creator", false, [rgb(255, 180, 90)], rgb(255, 180, 90)),
        new("#  seoul v10  #", "seoul", false, [rgb(200, 200, 255), rgb(150, 110, 255)], rgb(170, 140, 255)),
        new("Garin 2020 ~ HR", "Garin", false, [rgb(255, 70, 90), rgb(255, 255, 255)], rgb(255, 70, 90), Version: "latest"),
        new("- Vaxei 3.0 -", "Vaxei", false, [rgb(255, 230, 120), rgb(120, 200, 255)], rgb(255, 220, 120)),
        new("Mrekk v4.2", "mrekk", false, [rgb(255, 255, 255), rgb(90, 90, 90)], rgb(220, 220, 220), Elements: false),
        new("- ⌈ Kindle ⌋ -", "Unknown creator", false, [rgb(255, 150, 40)], rgb(255, 150, 40), Background: false),
        new("WhiteCat 2.1 (Normal) [WhiteCat 2.1 (Normal)]", "WhiteCat", true, [rgb(255, 255, 255), rgb(200, 200, 200)], rgb(240, 240, 240)),
        new("Yugen [Yugen]", "Yugen", true, [rgb(255, 110, 200), rgb(110, 200, 255)], rgb(255, 110, 200)),
    ];

    private SkinCaptureFixture(IInstalledSkinSource source, Guid? lazerSelected, Guid? applied, string root)
    {
        Source = source;
        LazerSelectedSkinId = lazerSelected;
        AppliedSkinId = applied;
        Root = root;
        SaveDirectory = Path.Combine(root, "saved");
    }

    public IInstalledSkinSource Source { get; }
    public Guid? LazerSelectedSkinId { get; }
    public Guid? AppliedSkinId { get; }
    public string Root { get; }
    public string SaveDirectory { get; }

    public static SkinCaptureFixture Create(string root)
    {
        string lazerRoot = Path.Combine(root, "lazer");
        string stableRoot = Path.Combine(root, "stable", "Skins");
        Directory.CreateDirectory(Path.Combine(lazerRoot, "files"));
        Directory.CreateDirectory(stableRoot);
        var lazerSkins = new List<ExternalLazerSkinSummary>();
        int index = 0;
        foreach (Spec spec in specs)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["skin.ini"] = System.Text.Encoding.UTF8.GetBytes(skinIni(spec)),
            };
            if (spec.Elements)
                addElements(files, spec);
            if (spec.Background)
                files["menu-background.jpg"] = SkinFixtureArt.Background(1366, 768, spec.Accent, index);

            if (spec.Lazer)
            {
                Guid id = new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture-lazer:" + spec.Name)).AsSpan(0, 16));
                var hashed = spec.BuiltIn ? new Dictionary<string, string>() : files.ToDictionary(file => file.Key, file => store(lazerRoot, file.Value), StringComparer.OrdinalIgnoreCase);
                string previewName = hashed.Keys.FirstOrDefault(name => name.StartsWith("menu-background", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                lazerSkins.Add(new ExternalLazerSkinSummary(
                    id, spec.Name, spec.Author, spec.BuiltIn ? string.Empty : new string((char)('a' + index % 6), 64), spec.BuiltIn,
                    spec.BuiltIn ? 0 : files.Count + 180 + index * 37,
                    previewName.Length == 0 ? string.Empty : hashed[previewName], previewName)
                {
                    PreviewFiles = hashed.Where(file => !file.Key.StartsWith("menu-background", StringComparison.OrdinalIgnoreCase))
                        .Select(file => new ExternalLazerSkinFile(file.Key, file.Value)).ToArray(),
                });
            }
            else
            {
                string directory = Path.Combine(stableRoot, spec.Name.Trim());
                Directory.CreateDirectory(directory);
                foreach ((string name, byte[] bytes) in files)
                {
                    string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
                Directory.SetCreationTimeUtc(directory, DateTime.UtcNow.AddDays(-index * 3));
            }
            index++;
        }

        ExternalLazerSkinSummary[] catalog = lazerSkins.ToArray();
        var lazer = new ExternalLazerInstalledSkinSource(lazerRoot, (request, _) =>
        {
            IEnumerable<ExternalLazerSkinSummary> matching = catalog.Where(skin =>
                (request.SkinId is null || skin.SkinId == request.SkinId)
                && (request.SearchText.Length == 0
                    || skin.Name.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase)
                    || skin.Creator.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase)));
            ExternalLazerSkinSummary[] all = matching.ToArray();
            return Task.FromResult(new ExternalLazerSkinCatalogSearchResult(all.Skip(request.Offset).Take(request.Limit).ToArray(), all.Length, request.Offset, request.Limit));
        });
        var source = new CompositeInstalledSkinSource(lazer, new OsuStableInstalledSkinSource(stableRoot));
        return new SkinCaptureFixture(source, catalog[0].SkinId, catalog[2].SkinId, root);
    }

    public OnlineSkinCatalogBackend CreateOnlineBackend()
    {
        var http = new FixtureScreenshotHttp();
        var providers = new IOnlineSkinCatalogProvider[]
        {
            new FixtureOnlineProvider("osuskins-net", "osuskins.net", new Uri("https://osuskins.net/"), 0),
            new FixtureOnlineProvider("skins-osuck-net", "skins.osuck.net", new Uri("https://skins.osuck.net/"), 1),
        };
        return new OnlineSkinCatalogBackend(Path.Combine(Root, "online-cache"), Path.Combine(Root, "online-previews"), http, providers, new FixtureBrowser());
    }

    /// <summary>Opens the first dropdown in the installed tab so a capture shows it over its neighbours.</summary>
    public static void OpenFirstMenu(Drawable root)
    {
        var dropdown = osu.Framework.Testing.TestingExtensions.ChildrenOfType<osu.Framework.Graphics.UserInterface.Dropdown<InstalledSkinSort>>((CompositeDrawable)root).FirstOrDefault();
        if (dropdown is null)
            return;
        var menu = (osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<InstalledSkinSort>)
            .GetField("Menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dropdown)!;
        menu.Open();
    }

    /// <summary>Clicks the card whose readable name contains <paramref name="name"/>, as a player would.</summary>
    public static void SelectCard(NativeSkinsScreen screen, string name)
    {
        SkinCardView? card = osu.Framework.Testing.TestingExtensions.ChildrenOfType<SkinCardView>(screen)
            .FirstOrDefault(candidate => osu.Framework.Testing.TestingExtensions.ChildrenOfType<osu.Game.Graphics.Sprites.TruncatingSpriteText>(candidate)
                .Any(text => text.Text.ToString().Contains(name, StringComparison.OrdinalIgnoreCase)));
        card?.TriggerClick();
    }

    public static void Search(NativeSkinsScreen screen, string text)
    {
        var box = (AimModTextBox)typeof(NativeSkinsScreen).GetField("searchBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!;
        box.Current.Value = text;
    }

    private static void addElements(Dictionary<string, byte[]> files, Spec spec)
    {
        string suffix = spec.HighResolution ? "@2x" : string.Empty;
        int scale = spec.HighResolution ? 2 : 1;
        files[$"hitcircle{suffix}.png"] = SkinFixtureArt.Circle(128 * scale, 0.9f, rgb(255, 255, 255));
        files[$"hitcircleoverlay{suffix}.png"] = SkinFixtureArt.Ring(128 * scale, 0.94f, 0.12f, rgb(255, 255, 255));
        files[$"approachcircle{suffix}.png"] = SkinFixtureArt.Ring(128 * scale, 0.96f, 0.05f, rgb(255, 255, 255));
        files[$"cursor{suffix}.png"] = SkinFixtureArt.Circle(56 * scale, 0.8f, spec.Accent, glow: true);
        files[$"cursortrail{suffix}.png"] = SkinFixtureArt.Circle(24 * scale, 0.7f, spec.Accent);
        files[$"sliderb0{suffix}.png"] = SkinFixtureArt.Circle(118 * scale, 0.85f, rgb(255, 255, 255));
        files[$"sliderfollowcircle{suffix}.png"] = SkinFixtureArt.Ring(256 * scale, 0.95f, 0.05f, spec.Accent);
        files[$"reversearrow{suffix}.png"] = SkinFixtureArt.Arrow(96 * scale, rgb(255, 255, 255));
        for (int digit = 0; digit <= 9; digit++)
        {
            files[$"default-{digit}{suffix}.png"] = SkinFixtureArt.Digits(digit.ToString(), 24 * scale, 36 * scale, rgb(255, 255, 255));
            files[$"score-{digit}{suffix}.png"] = SkinFixtureArt.Digits(digit.ToString(), 26 * scale, 40 * scale, rgb(255, 255, 255));
        }
        files[$"score-x{suffix}.png"] = SkinFixtureArt.Cross(26 * scale, rgb(255, 255, 255));
        files[$"hit300{suffix}.png"] = SkinFixtureArt.Digits("300", 84 * scale, 40 * scale, rgb(110, 190, 255));
        files[$"hit100{suffix}.png"] = SkinFixtureArt.Digits("100", 84 * scale, 40 * scale, rgb(120, 255, 120));
        files[$"hit50{suffix}.png"] = SkinFixtureArt.Digits("50", 56 * scale, 40 * scale, rgb(255, 190, 80));
        files[$"hit0{suffix}.png"] = SkinFixtureArt.Cross(56 * scale, rgb(255, 60, 60));
        files[$"scorebar-bg{suffix}.png"] = SkinFixtureArt.Bar(640 * scale, 34 * scale, rgb(40, 40, 50), 1);
        files[$"scorebar-colour{suffix}.png"] = SkinFixtureArt.Bar(600 * scale, 14 * scale, spec.Accent, 0.9f);
    }

    private static string skinIni(Spec spec)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("[General]");
        string plain = spec.Name.Contains('[') ? spec.Name[..spec.Name.IndexOf('[')].Trim() : spec.Name.Trim();
        builder.AppendLine($"Name: {plain}");
        builder.AppendLine($"Author: {spec.Author}");
        builder.AppendLine($"Version: {spec.Version}");
        builder.AppendLine();
        builder.AppendLine("[Colours]");
        for (int i = 0; i < spec.Combo.Length; i++)
            builder.AppendLine($"Combo{i + 1}: {spec.Combo[i].R},{spec.Combo[i].G},{spec.Combo[i].B}");
        builder.AppendLine($"SliderBorder: 255,255,255");
        builder.AppendLine($"SliderTrackOverride: {spec.Accent.R / 5},{spec.Accent.G / 5},{spec.Accent.B / 5}");
        return builder.ToString();
    }

    private static string store(string lazerRoot, byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string path = Path.Combine(lazerRoot, "files", hash[..1], hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return hash;
    }

    private static Rgba32 rgb(byte r, byte g, byte b) => new(r, g, b, 255);

    private sealed class FixtureOnlineProvider(string id, string name, Uri home, int seed) : IOnlineSkinCatalogProvider
    {
        private static readonly string[] names =
        [
            "Rafis 2023 ~ Blue", "- # Whitecat 2.1 # -", "Seoul v10 [Edit]", "Aristia (Edit)", "Garin 2020", "- Freedom Dive ↓ -",
            "Yugen", "Mrekk v4.2", "《CK》 Bacon boi 1.0", "idke 1.2", "Vaxei 3.0", "Emilia ~ Re:Zero",
        ];

        public string Id => id;
        public string DisplayName => name;
        public Uri HomePage => home;

        public Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
        {
            OnlineSkinCatalogEntry[] entries = Enumerable.Range(0, 6).Select(i => entry(i * 2 + seed)).ToArray();
            return Task.FromResult(new OnlineSkinCatalogPage(OnlineSkinCatalogStatus.Success, entries, 1, 30, false));
        }

        public Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OnlineSkinCatalogEntry?>(entry(int.Parse(entryId, System.Globalization.CultureInfo.InvariantCulture)));

        private OnlineSkinCatalogEntry entry(int i) => new(
            id, i.ToString(System.Globalization.CultureInfo.InvariantCulture), names[i % names.Length], new[] { "Rafis", "WhiteCat", "seoul", "Aristia", "Garin", "BTMC" }[i % 6],
            new Uri(home, $"skins/{i}"),
            [new Uri($"https://cdn.osuskins.net/fixture/{i}-a.png"), new Uri($"https://cdn.osuskins.net/fixture/{i}-b.png")],
            new OnlineSkinSourceAttribution(id, name, home, $"Listed by {name}"),
            new OnlineSkinDownloadTarget(new Uri(home, $"download/{i}.osk"), OnlineSkinDownloadKind.DirectHttps, [home.Host]),
            [OnlineSkinRuleset.Standard],
            DownloadCount: 1_200 + i * 3_517,
            FileSizeBytes: (12 + i * 3) * 1024L * 1024,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-i * 9));
    }

    private sealed class FixtureScreenshotHttp : ISecureSkinHttpClient
    {
        public Task<SkinHttpPayload> GetBytesAsync(Uri uri, SkinHttpFetchOptions options, CancellationToken cancellationToken = default)
        {
            int seed = uri.AbsolutePath.Aggregate(17, (value, character) => value * 31 + character) & 0x7fff;
            byte[] bytes = SkinFixtureArt.Screenshot(960, 540, seed);
            return Task.FromResult(new SkinHttpPayload(bytes, uri, "image/png"));
        }

        public Task<SkinHttpFile> DownloadAsync(Uri uri, string destinationPath, SkinHttpFetchOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offline fixture");
    }

    private sealed class FixtureBrowser : ISkinDownloadBrowser
    {
        public bool CanOpen(Uri page) => false;

        public Task<OnlineSkinResolvedDownload> DownloadAsync(Uri page, string destinationPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offline fixture");
    }
}

/// <summary>Tiny rasteriser for skin-like PNG elements; no fonts or drawing libraries needed.</summary>
internal static class SkinFixtureArt
{
    public static byte[] Circle(int size, float radius, Rgba32 colour, bool glow = false) => draw(size, size, (x, y) =>
    {
        float d = distance(x, y, size);
        if (glow && d > radius && d < 1)
            return colour with { A = (byte)(90 * (1 - (d - radius) / (1 - radius))) };
        if (d > radius)
            return default;
        float shade = 0.72f + 0.28f * (1 - d / radius);
        float edge = Math.Clamp((radius - d) * size / 2, 0, 1);
        return new Rgba32((byte)(colour.R * shade), (byte)(colour.G * shade), (byte)(colour.B * shade), (byte)(255 * edge));
    });

    public static byte[] Ring(int size, float outer, float thickness, Rgba32 colour) => draw(size, size, (x, y) =>
    {
        float d = distance(x, y, size);
        return d <= outer && d >= outer - thickness ? colour : default;
    });

    public static byte[] Arrow(int size, Rgba32 colour) => draw(size, size, (x, y) =>
    {
        float u = x / (float)size, v = y / (float)size;
        bool head = u > 0.45f && u < 0.8f && Math.Abs(v - 0.5f) < (0.8f - u);
        bool shaft = u > 0.2f && u <= 0.5f && Math.Abs(v - 0.5f) < 0.08f;
        return head || shaft ? colour : default;
    });

    public static byte[] Cross(int size, Rgba32 colour) => draw(size, size, (x, y) =>
    {
        float u = x / (float)size, v = y / (float)size;
        bool on = (Math.Abs(u - v) < 0.1f || Math.Abs(u + v - 1) < 0.1f) && u > 0.12f && u < 0.88f;
        return on ? colour : default;
    });

    public static byte[] Bar(int width, int height, Rgba32 colour, float alpha) => draw(width, height, (x, y) =>
    {
        float shade = 0.8f + 0.2f * (1 - y / (float)height);
        return new Rgba32((byte)(colour.R * shade), (byte)(colour.G * shade), (byte)(colour.B * shade), (byte)(255 * alpha));
    });

    /// <summary>Seven-segment digits, so number sprites look like numbers without a font.</summary>
    public static byte[] Digits(string text, int width, int height, Rgba32 colour)
    {
        int[] masks = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];
        float cell = width / (float)text.Length;
        return draw(width, height, (x, y) =>
        {
            int index = Math.Min(text.Length - 1, (int)(x / cell));
            float u = (x - index * cell) / cell, v = y / (float)height;
            int mask = masks[text[index] - '0'];
            const float t = 0.14f, l = 0.18f, r = 0.82f;
            bool on = (mask & 1) != 0 && v < t + 0.04f && u > l && u < r
                      || (mask & 2) != 0 && u > r - t && u < r && v > 0.04f && v < 0.5f
                      || (mask & 4) != 0 && u > r - t && u < r && v > 0.5f && v < 0.96f
                      || (mask & 8) != 0 && v > 0.96f - t && v < 0.96f + 0.04f && u > l && u < r
                      || (mask & 16) != 0 && u > l && u < l + t && v > 0.5f && v < 0.96f
                      || (mask & 32) != 0 && u > l && u < l + t && v > 0.04f && v < 0.5f
                      || (mask & 64) != 0 && Math.Abs(v - 0.5f) < t / 2 && u > l && u < r;
            if (on)
                return colour;
            // A soft dark outline keeps the digits readable over tinted circles.
            return default;
        });
    }

    public static byte[] Background(int width, int height, Rgba32 accent, int seed)
    {
        using var image = new Image<Rgba32>(width, height);
        var random = new Random(seed * 7919 + 13);
        (float X, float Y, float R)[] blobs = Enumerable.Range(0, 6).Select(_ => (random.NextSingle() * width, random.NextSingle() * height, 80 + random.NextSingle() * 260)).ToArray();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float g = (x + y) / (float)(width + height);
            float glow = blobs.Sum(blob => Math.Max(0, 1 - MathF.Sqrt((x - blob.X) * (x - blob.X) + (y - blob.Y) * (y - blob.Y)) / blob.R)) * 0.55f;
            float k = Math.Clamp(0.12f + 0.25f * (1 - g) + glow, 0, 1);
            image[x, y] = new Rgba32((byte)(accent.R * k + 12 * (1 - k)), (byte)(accent.G * k + 14 * (1 - k)), (byte)(accent.B * k + 22 * (1 - k)), 255);
        }
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    public static byte[] Screenshot(int width, int height, int seed)
    {
        var random = new Random(seed);
        var accent = new Rgba32((byte)random.Next(60, 255), (byte)random.Next(60, 255), (byte)random.Next(60, 255), 255);
        using var image = new Image<Rgba32>(width, height);
        (float X, float Y)[] circles = Enumerable.Range(0, 5).Select(i => (160f + i * 150, 200f + (float)Math.Sin(i + seed) * 90)).ToArray();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float k = 0.1f + 0.15f * y / height;
            var pixel = new Rgba32((byte)(accent.R * k), (byte)(accent.G * k), (byte)(accent.B * k), 255);
            foreach ((float cx, float cy) in circles)
            {
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < 44) pixel = new Rgba32((byte)(accent.R * 0.9f), (byte)(accent.G * 0.9f), (byte)(accent.B * 0.9f), 255);
                else if (d < 50) pixel = new Rgba32(255, 255, 255, 255);
            }
            if (y > height - 30 && x < width * 0.6f) pixel = accent;
            image[x, y] = pixel;
        }
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static float distance(int x, int y, int size)
    {
        float cx = (x + 0.5f) / size * 2 - 1, cy = (y + 0.5f) / size * 2 - 1;
        return MathF.Sqrt(cx * cx + cy * cy);
    }

    private static byte[] draw(int width, int height, Func<int, int, Rgba32> pixel)
    {
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            image[x, y] = pixel(x, y);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
