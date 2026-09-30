using System.Reflection;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.Home;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Trainers;
using AimMod.Desktop.Updates;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

/// <summary>Visual review captures of Home with synthetic history. Writes PNGs under the test work directory.</summary>
[TestFixture]
[NonParallelizable]
public sealed partial class OffscreenHomeCaptureTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase("populated", 1920, 1080)]
    [TestCase("populated", 1280, 800)]
    [TestCase("populated", 900, 700)]
    [TestCase("month", 1920, 1080)]
    [TestCase("local", 1280, 800)]
    [TestCase("empty", 1280, 800)]
    [TestCase("empty", 900, 700)]
    [TestCase("loading", 1280, 800)]
    [TestCase("update-options", 1280, 800)]
    [TestCase("update-available", 1920, 1080)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureHome(string state, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"home-{state}-{width}x{height}.png");
        await WindowsPrivateDesktopCapture.CaptureAsync((host, ok, fail) => new HomeCaptureGame(host, state, width, height, output, ok, fail), TimeSpan.FromSeconds(40));
        using Image<Rgba32> image = Image.Load<Rgba32>(output);
        Assert.That(image.Width, Is.EqualTo(width));
        TestContext.AddTestAttachment(output, $"Home {state}");
    }

    internal static InMemoryLocalLibrarySource CreateHistory(int plays, DateTimeOffset now, string player = "SyntheticPlayer")
    {
        var random = new Random(7);
        string[] titles = ["Blue Zenith", "Hana ni Natte", "Sidetracked Day", "RE:RE:RE:START", "Parousia", "Light", "Redemption", "Freedom Dive",
            "Glorious Crown", "Snow Drive", "Crystalia", "The Big Black", "Harumachi Clover", "Kimi no Bouken", "Night of Knights", "Senbonzakura",
            "Galaxy Collapse", "Team Magma & Aqua Leader Battle Theme", "Kami no Kotoba", "Cycle Hit", "Ascension to Heaven", "Chronostasis",
            "Everything Will Freeze", "Road of Resistance", "Yomi yori Kikoyu", "Uta", "Image Material", "Can't Defeat Airman", "Airman", "Padoru"];
        string[] difficulties = ["Insane", "Extra", "Expert", "Hard", "Another", "Lunatic"];
        LocalBeatmapSet[] sets = titles.Select((title, index) => new LocalBeatmapSet(Guid.NewGuid(), 5_000 + index, title, "Synthetic Artist", "Mapper",
            string.Empty, now.AddDays(-200), now.AddDays(-1), [new LocalBeatmapDifficulty(Guid.NewGuid(), 50_000 + index, difficulties[index % difficulties.Length], "osu",
                3.8 + (index % 9) * 0.32, 160 + index * 3, 180_000, 4, 9, 8, 5, 3, $"hash-{index}")], 3)).ToArray();
        var replays = new List<LocalReplay>();
        for (int i = 0; i < plays; i++)
        {
            // Denser recent activity with gaps, like a real schedule.
            double daysAgo = Math.Pow(random.NextDouble(), 1.6) * 75;
            if ((int)daysAgo % 6 == 4) daysAgo += 1.2;
            int mapIndex = random.Next(i % 3 == 0 ? 6 : titles.Length);
            LocalBeatmapSet set = sets[mapIndex];
            LocalBeatmapDifficulty difficulty = set.Difficulties[0];
            // Accuracy improves slowly towards the present; harder maps are less accurate.
            double accuracy = Math.Clamp(0.955 - (difficulty.StarRating - 4.5) * 0.018 + (75 - daysAgo) * 0.00012 + (random.NextDouble() - 0.5) * 0.07, 0.85, 0.995);
            int misses = Math.Max(0, (int)Math.Round((0.99 - accuracy) * 110 + random.Next(-2, 4)));
            misses = Math.Min(15, misses) + (mapIndex == 2 ? 4 : 0);
            bool passed = random.NextDouble() > 0.12;
            double pp = Math.Clamp(18 + (difficulty.StarRating - 3.6) * 38 * Math.Pow(accuracy, 8) - misses * 2.2 + random.NextDouble() * 12, 20, 150);
            replays.Add(new LocalReplay(Guid.NewGuid(), set.SetId, difficulty.BeatmapId, set.Title, set.Artist, difficulty.Name, "osu", player,
                now.AddDays(-daysAgo).AddMinutes(-random.Next(0, 90)), difficulty.StarRating, accuracy, 400_000 + random.Next(600_000), 300 + random.Next(900),
                misses, passed ? Math.Round(pp, 1) : null, i % 5 == 0 ? ["HD"] : [], i % 4 != 0, difficulty.BeatmapHash, Passed: passed));
        }
        return new InMemoryLocalLibrarySource(sets, replays);
    }

    internal static OsuProfile CreateProfile() => new(12_345, "SyntheticPlayer", "NL", null,
        new OsuProfileStatistics(48_213, 1_204, 3_412.6, 97.31, 18_422, 1_100_000, 4_000_000_000, 9_000_000_000, 5_000_000, 1_450));

    internal static PpTargetWorkspaceSnapshot CreatePpTargets(DateTimeOffset now)
    {
        var difficulty = new OfficialBeatmapDifficulty(910_001, "Extra", "osu", 5.42, 186, 214, 4, 9.2f, 8.5f, 6, 120_000, 43_000, 1_600);
        var set = new OfficialBeatmapSet(920_001, "Kimi no Bouken", "", "Synthetic Artist", "", "Mapper", "", "ranked",
            now.AddDays(-30), now, 850_000, 42_000, false, false, null, null, null, null, [difficulty]);
        var estimates = new Dictionary<int, PpTargetEstimate>
        {
            [910_001] = new(168, 201, new(152, 180), 24, PpTargetConfidence.High, "fixture", BeatmapId: 910_001, ExpectedAccuracy: .968),
        };
        return new PpTargetWorkspaceSnapshot(now, PpTargetPreferenceProfile.Empty, [], [set], estimates, 100, "", "", 4, 7, OfficialBeatmapCategory.Ranked);
    }

    private sealed partial class HomeCaptureGame(GameHost host, string state, int width, int height, string output, Action ok, Action<Exception> fail)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            Dependencies.Get<FrameworkConfigManager>().SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(() =>
            {
                try
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    bool signedIn = state is "populated" or "month" or "loading" or "update-options" or "update-available";
                    bool hasPlays = state is not "empty";
                    OsuProfile? profile = signedIn ? CreateProfile() : null;
                    var library = hasPlays ? CreateHistory(420, now) : new InMemoryLocalLibrarySource([], []);
                    var gate = new TaskCompletionSource();
                    var sources = new HomeDashboardSources(
                        state == "loading" ? new BlockingLibrary(library, gate.Task) : library,
                        () => null,
                        () => profile,
                        _ => new HomeProfileChange(38.4, 612, now.AddDays(-7)),
                        () => null,
                        () => hasPlays ? new TrainerGuidedPlan(Guid.NewGuid(), TrainerGuidedFocus.Endurance, new TrainerSettings(), new TrainerSettings(), 2) : null,
                        () => [],
                        () => hasPlays && signedIn ? CreatePpTargets(now) : null,
                        () => null);
                    Action nothing = () => { };
                    var actions = new HomeDashboardActions(nothing, nothing, nothing, nothing, nothing, nothing, nothing, nothing, nothing, _ => { }, _ => { });
                    INativeUpdateService updates = new FixtureUpdateService(state == "update-available"
                        ? new NativeUpdateState(NativeUpdateStage.Available, NativeUpdateChannel.Stable, "AimMod 0.4.0 is ready", "Download the update while you keep using AimMod.", "0.4.0")
                        : new NativeUpdateState(NativeUpdateStage.Unavailable, NativeUpdateChannel.Stable, "Updates unavailable", "Install AimMod to receive updates automatically."));
                    var home = new NativeHomeDashboard(sources, actions, updates);
                    var content = (Container)typeof(AimModGame).GetField("content", flags)!.GetValue(this)!;
                    object sidebar = typeof(AimModGame).GetField("header", flags)!.GetValue(this)!;
                    if (profile is not null)
                        sidebar.GetType().GetMethod("SetPublicProfile")!.Invoke(sidebar, [profile]);
                    content.Padding = AimModVisualStyle.PagePaddingFor(AimModLayout.SidebarWidth(AimModLayout.SelectSidebarMode(width)));
                    content.Child = home;
                    Scheduler.AddDelayed(() =>
                    {
                        try
                        {
                            if (state == "update-options")
                            {
                                // Open the options the way a player would: after scrolling the row into view.
                                var surface = descendants(home).OfType<NativeUpdateSurface>().Single();
                                var scroll = descendants(home).OfType<AimModScrollContainer>().First();
                                scroll.ScrollToEnd(false);
                                Scheduler.AddDelayed(() =>
                                {
                                    ((AimModButton)typeof(NativeUpdateSurface).GetField("optionsButton", flags)!.GetValue(surface)!).Action!.Invoke();
                                    Assert.That(surface.OptionsOpen, Is.True);
                                }, 200);
                                Scheduler.AddDelayed(() =>
                                {
                                    Assert.That(surface.DrawHeight, Is.EqualTo(NativeUpdateSurface.HeightFor(true, false)).Within(.5f));
                                    scroll.ScrollToEnd(false);
                                }, 500);
                            }
                            if (state == "month")
                                descendants(home).OfType<AimModButton>().Single(button => button.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                                    .Any(text => text.Text.ToString() == "30 days")).Action!.Invoke();
                            if (state != "loading")
                                Assert.That(home.Data, Is.Not.Null, "Home must finish loading before the capture.");
                        }
                        catch (Exception error) { fail(error); host.Exit(); return; }
                        Scheduler.AddDelayed(capture, 700);
                    }, 2200);
                }
                catch (Exception error) { fail(error); host.Exit(); }
            }, 600);
        }

        private void capture() => host.TakeScreenshotAsync().ContinueWith(task =>
        {
            try
            {
                using Image<Rgba32> image = task.GetAwaiter().GetResult();
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                image.SaveAsPng(output);
                ok();
            }
            catch (Exception error) { fail(error); }
            finally { host.Exit(); }
        }, TaskScheduler.Default);

        private static IEnumerable<Drawable> descendants(Drawable item)
        {
            yield return item;
            if (item is not CompositeDrawable) yield break;
            var children = (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(item)!;
            foreach (var child in children)
                foreach (var descendant in descendants(child)) yield return descendant;
        }
    }

    /// <summary>Holds every read until released, to capture the loading state.</summary>
    private sealed class BlockingLibrary(ILocalLibrarySource inner, Task gate) : ILocalLibrarySource
    {
        public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await inner.SearchBeatmapSetsAsync(query, cancellationToken);
        }

        public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await inner.SearchReplaysAsync(query, cancellationToken);
        }

        public void Invalidate() => inner.Invalidate();
    }

    internal sealed class FixtureUpdateService(NativeUpdateState state) : INativeUpdateService
    {
        public NativeUpdateState State { get; private set; } = state;
        public event Action<NativeUpdateState>? StateChanged;
        public Task CheckAsync() => Task.CompletedTask;
        public Task SelectChannelAsync(NativeUpdateChannel channel) { State = State with { Channel = channel }; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DownloadAsync() => Task.CompletedTask;
        public void ApplyAndRestart() { }
        public void Dispose() { }
    }
}
