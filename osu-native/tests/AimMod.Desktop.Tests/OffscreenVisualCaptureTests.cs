using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Overlays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

[TestFixture]
[NonParallelizable]
public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase("home", 1600, 900)]
    [TestCase("home", 1100, 760)]
    [TestCase("beatmaps", 1100, 760)]
    [TestCase("beatmaps-populated", 1100, 760)]
    [TestCase("beatmaps-populated", 800, 760)]
    [TestCase("beatmaps-populated", 1600, 900)]
    [TestCase("skins", 1100, 760)]
    [TestCase("settings", 1100, 760)]
    [TestCase("settings", 1600, 900)]
    [TestCase("replays", 1100, 760)]
    [TestCase("replays-analysis", 1600, 900)]
    [TestCase("statistics", 1100, 760)]
    [TestCase("statistics-populated", 1100, 760)]
    [TestCase("statistics-populated", 1600, 900)]
    [TestCase("coaching-populated", 1100, 760)]
    [TestCase("coaching-populated", 1600, 900)]
    [TestCase("coaching-complete", 1100, 760)]
    [TestCase("coaching-complete", 1600, 900)]
    [TestCase("coaching-practice-many", 1100, 760)]
    [TestCase("coaching-practice-many", 1600, 900)]
    [TestCase("coaching-error", 1100, 760)]
    [TestCase("coaching-empty", 1100, 760)]
    [TestCase("ppTargets", 1100, 760)]
    [TestCase("ppTargets", 1600, 900)]
    [TestCase("ppTargets-populated", 1100, 760)]
    [TestCase("ppTargets-populated", 1600, 900)]
    [TestCase("loading", 1100, 760)]
    [Explicit("Creates a real graphics device and writes a visual-review artifact.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureWorkspaceOnPrivateDesktop(string route, int width, int height)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Private-desktop captures are only supported on Windows.");

        string outputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "visual-captures",
            $"aimmod-{route}-{width}x{height}.png");

        InMemoryLocalLibrarySource source = route.EndsWith("-populated", StringComparison.Ordinal)
                                            || route.StartsWith("coaching-", StringComparison.Ordinal)
                                            && !string.Equals(route, "coaching-empty", StringComparison.Ordinal)
                                            || string.Equals(route, "replays-analysis", StringComparison.Ordinal)
            ? createPopulatedLibrary()
            : new InMemoryLocalLibrarySource([], []);

        PpTargetWorkspaceCache? ppCache = route.StartsWith("ppTargets", StringComparison.Ordinal)
            ? await createPpTargetCaptureCache(outputPath)
            : null;

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => route switch
            {
                "ppTargets" or "ppTargets-populated" => new CapturePpTargetsGame(host, ppCache!, outputPath, width, height, succeeded, failed),
                "beatmaps-populated" => new CaptureBeatmapGame(host, source, outputPath, width, height, succeeded, failed),
                "statistics-populated" => new CaptureStatisticsGame(host, source, outputPath, width, height, succeeded, failed),
                "coaching-populated" => new CaptureCoachingGame(host, source, outputPath, width, height, CoachingCaptureState.Analysing, succeeded, failed),
                "coaching-complete" => new CaptureCoachingGame(host, source, outputPath, width, height, CoachingCaptureState.Complete, succeeded, failed),
                "coaching-practice-many" => new CaptureCoachingGame(host, source, outputPath, width, height, CoachingCaptureState.PracticeMany, succeeded, failed),
                "coaching-error" => new CaptureCoachingGame(host, source, outputPath, width, height, CoachingCaptureState.Error, succeeded, failed),
                "coaching-empty" => new CaptureCoachingGame(host, source, outputPath, width, height, CoachingCaptureState.Empty, succeeded, failed),
                "replays-analysis" => new CaptureReplayAnalysisGame(host, source, outputPath, width, height, succeeded, failed),
                _ => new CaptureAimModGame(host, source, outputPath, route, width, height, succeeded, failed),
            },
            TimeSpan.FromSeconds(30));

        using Image<Rgba32> image = Image.Load<Rgba32>(outputPath);

        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
            Assert.That(countSampledColours(image), Is.GreaterThan(8), "The capture should contain rendered UI, not a blank frame.");
        });

        TestContext.AddTestAttachment(outputPath, $"AimMod {route} captured on a private Windows desktop");
    }

    private static InMemoryLocalLibrarySource createPopulatedLibrary()
    {
        LocalBeatmapSet[] sets = Enumerable.Range(0, 7).Select(setIndex =>
        {
            Guid setId = Guid.NewGuid();
            LocalBeatmapDifficulty[] difficulties = Enumerable.Range(0, 6).Select(difficultyIndex => new LocalBeatmapDifficulty(
                Guid.NewGuid(),
                10_000 + setIndex * 10 + difficultyIndex,
                new[] { "Bloom", "Petal", "Fleur", "Blossom", "Full Bloom", "Transcend" }[difficultyIndex],
                "osu",
                3.8 + setIndex * 0.08 + difficultyIndex * 0.68,
                172 + setIndex * 4,
                195_000 + setIndex * 13_000,
                4 + difficultyIndex * 0.08f,
                8.3f + difficultyIndex * 0.25f,
                7.8f + difficultyIndex * 0.3f,
                5,
                difficultyIndex < 4 ? 3 + difficultyIndex : null,
                $"hash-{setIndex}-{difficultyIndex}"))
                .ToArray();

            return new LocalBeatmapSet(
                setId,
                2_000 + setIndex,
                new[] { "Hana ni Natte", "Light", "RE:RE:RE:START", "Redemption", "Parousia", "Blue Zenith", "Sidetracked Day" }[setIndex],
                new[] { "Miyuki Nakajima", "Camellia", "Camellia", "LeaF", "xi", "xi", "VINXIS" }[setIndex],
                new[] { "Delis", "Fuycho", "Mir", "Nevo", "Ashaasoki", "Asphyxia", "Sotarks" }[setIndex],
                string.Empty,
                DateTimeOffset.Now.AddDays(-setIndex - 1),
                DateTimeOffset.Now.AddHours(-setIndex * 8),
                difficulties,
                8 + setIndex,
                string.Empty);
        }).ToArray();

        LocalReplay[] replays = Enumerable.Range(0, 7).Select(index =>
        {
            LocalBeatmapSet set = sets[index];
            LocalBeatmapDifficulty selected = set.Difficulties[0];
            return new LocalReplay(
                Guid.NewGuid(),
                set.SetId,
                selected.BeatmapId,
                set.Title,
                set.Artist,
                selected.Name,
                "osu",
                "verycrunchy",
                DateTimeOffset.Now.AddDays(-7 + index),
                selected.StarRating,
                0.91 + index * 0.011,
                850_000 + index * 43_000,
                420 + index * 31,
                Math.Max(0, 6 - index),
                110 + index * 12,
                Array.Empty<string>(),
                true,
                selected.BeatmapHash);
        }).ToArray();

        return new InMemoryLocalLibrarySource(sets, replays);
    }

    private static ReplayAnalysisResult createCoachingAnalysis(int replayIndex)
    {
        ReplayObjectJudgement[] hits = Enumerable.Range(0, 12).Select(index => new ReplayObjectJudgement(
            index,
            null,
            "HitCircle",
            10_000 + index * 500,
            10_000 + index * 500,
            "Great",
            "Great",
            10_018 + index * 500 + index % 4,
            18 + index % 4,
            1,
            new ReplayPoint(256, 192),
            new ReplayPoint(262 + index % 3, 195 + index % 2),
            12,
            0)).ToArray();
        ReplayObjectJudgement miss = new(
            24, null, "HitCircle", 24_000, 24_000, "Miss", "Great", 24_150, 150, 1,
            new ReplayPoint(256, 192), null, 120, 0,
            new ReplayMissAnalysis(
                replayIndex == 2 ? ReplayMissReason.EarlyClick : ReplayMissReason.Overshoot,
                32, 40, -10, new ReplayPoint(280, 192), 50, 55,
                new ReplayPoint(311, 192), 45, true, false, true, 0.4, Confidence: 0.85));

        return new ReplayAnalysisResult(
            ReplayAnalysisProtocol.EngineVersion,
            "officialRulesetPlayback",
            true,
            ReplayAnalysisProtocol.WallClockTimeoutMs,
            Array.Empty<int>(),
            hits.Append(miss).ToArray(),
            new ReplayJudgementSummary(hits.Length, 0, 0, 1, 0, 0));
    }

    private static async Task<PpTargetWorkspaceCache> createPpTargetCaptureCache(string outputPath)
    {
        var now = DateTimeOffset.UtcNow;
        const string identity = "private-desktop-pp-pattern-fixture";
        var features = new PpPatternFeatures { PointCount = 600, TransitionCount = 599, JumpDistance = 210, StreamFraction = 0.3, HitRadius = 32, ClockRate = 1 };
        PpPatternEvidence[] evidence = Enumerable.Range(0, 12).Select(i => new PpPatternEvidence(
            Guid.NewGuid(), $"fixture-map-{i}", "NM", now.AddDays(-i), features, 1,
            new Dictionary<string, PpPatternOutcome>
            {
                ["Jumps"] = new(240, 0.986, 0.01, new Dictionary<ReplayMissReason, int> { [ReplayMissReason.Overshoot] = 2 }),
                ["Streams"] = new(180, 0.924, 0.04, new Dictionary<ReplayMissReason, int> { [ReplayMissReason.LateClick] = 7 }),
            })).ToArray();
        var profile = PpTargetPreferenceProfile.Empty with
        {
            ValidRunCount = 148,
            DistinctSetupCount = 24,
            PpSampleCount = 112,
            PreferredStarRange = new(4.5, 6.5),
            PreferredBpmRange = new(170, 220),
            TypicalAccuracy = 0.972,
            HistoricalBestPp = 325,
            Confidence = PpTargetConfidence.High,
            PerformanceSamples = Enumerable.Range(0, 24).Select(i => new PpTargetPerformanceSample(4.5 + i * 0.07, 120 + i * 5, 0.975)).ToArray(),
            PatternProfile = new(identity, now, 30, evidence),
        };
        var estimates = new Dictionary<int, PpTargetEstimate>();
        OfficialBeatmapSet[] catalog = Enumerable.Range(0, 8).Select(i =>
        {
            int beatmapId = 910_000 + i;
            var difficulty = new OfficialBeatmapDifficulty(beatmapId,
                i == 0 ? "Extra: Beyond the Horizon of the Endless Night" : $"Insane {i + 1}",
                "osu", 4.9 + i * 0.15, 180 + i * 5, 160 + i * 17, 4.2f, 9.3f, 8.8f, 6, 120_000, 43_000, 1250 + i * 110);
            var prediction = new PpPatternPrediction(0.94 - i * 0.06, 0.976 - i * 0.006, 0.82,
                ["Jumps: 98.6% accuracy across 12 maps; controlled spacing and consistent cursor placement"],
                ["Streams: 92.4% accuracy across 8 maps; late clicks after sustained high-speed tapping sequences", "Sharp turns: 94.1% accuracy across 6 maps; repeated overshoot on direction changes"],
                [new("Jumps", 0.94, 0.986, 0.86, 12), new("Streams", 0.62, 0.924, 0.72, 8), new("Sharp turns", 0.71, 0.941, 0.65, 6)],
                ["Slider tracking is not measured by head geometry"]);
            estimates[beatmapId] = new(185 + i * 21, 276 + i * 29, new(170 + i * 21, 202 + i * 21), 24,
                PpTargetConfidence.High, "Official osu! ruleset / fixture", BeatmapId: beatmapId,
                PatternPrediction: prediction, PatternProfileIdentity: identity);
            return new OfficialBeatmapSet(920_000 + i,
                new[] { "A Long Journey Beyond the Horizon (Extended Version)", "Blue Zenith", "Hana ni Natte", "Sidetracked Day", "RE:RE:RE:START", "Parousia", "Light", "Redemption" }[i],
                "", "Camellia featuring a deliberately long guest artist credit", "", "Mapper with a long display name", "Original", "ranked",
                now.AddDays(-i), now, 850_000, 42_000, false, false, null, null, null, null, [difficulty]);
        }).ToArray();
        var cache = new PpTargetWorkspaceCache(Path.ChangeExtension(outputPath, ".fixture.json"));
        await cache.SaveAsync(new(now, profile, [], catalog, estimates, 100, "", "", 4, 7, OfficialBeatmapCategory.Ranked));
        Assert.That(cache.Load()?.ExactEstimates.Count, Is.EqualTo(8), "The populated snapshot must survive persistence.");
        return cache;
    }

    private static int countSampledColours(Image<Rgba32> image)
    {
        var colours = new HashSet<Rgba32>();
        int stepX = Math.Max(1, image.Width / 128);
        int stepY = Math.Max(1, image.Height / 72);

        for (int y = 0; y < image.Height; y += stepY)
        {
            for (int x = 0; x < image.Width; x += stepX)
                colours.Add(image[x, y]);
        }

        return colours.Count;
    }

    private sealed partial class CaptureAimModGame : AimModGame
    {
        private readonly GameHost host;
        private readonly string outputPath;
        private readonly string route;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureAimModGame(
            GameHost host,
            ILocalLibrarySource source,
            string outputPath,
            string route,
            int width,
            int height,
            Action succeeded,
            Action<Exception> failed)
            : base(AimModLaunchOptions.Home, source)
        {
            this.host = host;
            this.outputPath = outputPath;
            this.route = route;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));

            if (string.Equals(route, "loading", StringComparison.Ordinal))
            {
                var overlay = new AimModLoadingOverlay();
                LoadComponentAsync(overlay, loaded =>
                {
                    Add(loaded);
                    loaded.ShowLoading("Calculating beatmap PP", "Difficulty 12 of 24", 12, 24);
                });
            }
            else if (!string.Equals(route, "home", StringComparison.Ordinal))
            {
                if (route == "settings")
                {
                    Scheduler.AddDelayed(() =>
                    {
                        string fixtureRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", "settings-fixture");
                        typeof(AimModGame).GetField("beatmapDestinationService", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(this, new OsuBeatmapDestinationService(
                                new LazerBeatmapInstallService(Path.Combine(fixtureRoot, "archives")),
                                new FileOsuClientDestinationPreferenceStore(Path.Combine(fixtureRoot, "destination")),
                                Path.Combine(fixtureRoot, "handoff")));
                    }, 1100);
                }
                string routeName = route.Split('-', 2)[0];
                MethodInfo routeMethod = typeof(AimModGame).GetMethod($"show{char.ToUpperInvariant(routeName[0])}{routeName[1..]}", BindingFlags.Instance | BindingFlags.NonPublic)
                                         ?? throw new InvalidOperationException($"Unknown capture route '{route}'.");
                Scheduler.AddDelayed(() => routeMethod.Invoke(this, null), route == "settings" ? 1200 : 300);
            }

            Scheduler.AddDelayed(capture, route == "settings" ? 3500 : 1800);
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

    private sealed partial class CapturePpTargetsGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private readonly GameHost host;
        private readonly PpTargetWorkspaceCache cache;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;
        private NativePpTargetsWorkspace workspace = null!;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CapturePpTargetsGame(GameHost host, PpTargetWorkspaceCache cache, string outputPath, int width, int height, Action succeeded, Action<Exception> failed)
        {
            this.host = host;
            this.cache = cache;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // Fresh persisted estimates plus empty live history keep this capture offline
            // and prevent a background profile rebuild from replacing the measured fixture.
            workspace = new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null,
                workspaceCache: cache, openBeatmap: (_, _) => Task.CompletedTask);
            Add(new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                Padding = new osu.Framework.Graphics.MarginPadding { Top = 88, Horizontal = 52, Bottom = 24 },
                Child = workspace,
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 2200);
        }

        private void capture()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var rows = (osu.Framework.Graphics.Containers.FillFlowContainer<osu.Framework.Graphics.Drawable>)
                    typeof(NativePpTargetsWorkspace).GetField("results", flags)!.GetValue(workspace)!;
                Assert.That(rows.Count, Is.EqualTo(8), "Capture must show PP target rows, never Home or a loading placeholder.");
                foreach (var row in rows.Children)
                {
                    var tooltip = (osu.Framework.Graphics.Cursor.IHasTooltip)row;
                    Assert.That(tooltip.TooltipText.ToString(), Does.Contain("Jumps: 98.6%").And.Contain("Streams: 92.4%").And.Contain("Sharp turns:"));
                    float previousBottom = 0;
                    foreach (string field in new[] { "title", "artist", "mapDetails", "mechanicsDetails", "confidenceDetails", "patternDetails" })
                    {
                        var line = (osu.Framework.Graphics.Sprites.SpriteText)row.GetType().GetField(field, flags)!.GetValue(row)!;
                        var top = row.ToLocalSpace(line.ToScreenSpace(osuTK.Vector2.Zero));
                        var bottom = row.ToLocalSpace(line.ToScreenSpace(line.DrawSize));
                        Assert.That(line.Text.ToString(), Is.Not.Empty, field);
                        Assert.That(top.Y, Is.GreaterThanOrEqualTo(previousBottom - 0.5f), $"{field} overlaps the previous line");
                        Assert.That(bottom.Y, Is.LessThanOrEqualTo(row.DrawHeight), $"{field} escapes its PP row");
                        Assert.That(bottom.X, Is.LessThanOrEqualTo(row.DrawWidth), $"{field} escapes horizontally");
                        previousBottom = bottom.Y;
                    }
                    var skill = (osu.Framework.Graphics.Sprites.SpriteText)row.GetType().GetField("confidenceDetails", flags)!.GetValue(row)!;
                    Assert.That(skill.Text.ToString(), Does.Contain("skill fit").And.Not.Contain("unmeasured"));
                }
            }
            catch (Exception error)
            {
                failed(error);
                host.Exit();
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

    private sealed partial class CaptureBeatmapGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureBeatmapGame(
            GameHost host,
            ILocalLibrarySource source,
            string outputPath,
            int width,
            int height,
            Action succeeded,
            Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                Padding = new osu.Framework.Graphics.MarginPadding(18),
                Child = new NativeBeatmapDiscoveryScreen(source, () => null, () => null)
                {
                    RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 5000);
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

    private sealed partial class CaptureStatisticsGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureStatisticsGame(
            GameHost host,
            ILocalLibrarySource source,
            string outputPath,
            int width,
            int height,
            Action succeeded,
            Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                Padding = new osu.Framework.Graphics.MarginPadding(18),
                Child = new NativeStatisticsWorkspace(source, _ => { })
                {
                    RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 1500);
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

    private sealed partial class CaptureCoachingGame : OsuGameBase
    {
        [Cached]
        private readonly OverlayColourProvider overlayColours = new(OverlayColourScheme.Blue);
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly CoachingCaptureState state;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureCoachingGame(
            GameHost host,
            ILocalLibrarySource source,
            string outputPath,
            int width,
            int height,
            CoachingCaptureState state,
            Action succeeded,
            Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.state = state;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            LocalReplay[] replays = source.SearchReplaysAsync(new LocalLibraryQuery(Limit: 200)).AsTask().GetAwaiter().GetResult().Items.ToArray();
            int analysisCount = state == CoachingCaptureState.PracticeMany ? replays.Length : 3;
            var analyses = replays.Take(analysisCount)
                                  .Select((replay, index) => (replay, index))
                                  .ToDictionary(item => item.replay.ScoreId, item => createCoachingAnalysis(item.index));

            var workspace = new NativeCoachingWorkspace(
                source,
                analyses,
                _ => { },
                () => null,
                (_, _) => Task.FromResult(new PracticeMapGenerationResult(
                    true,
                    "Practice map ready",
                    LazerArchive: new LazerBeatmapArchive(0, Guid.NewGuid()))),
                (_, _) => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)));
            workspace.RelativeSizeAxes = osu.Framework.Graphics.Axes.Both;

            Add(new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = osu.Framework.Graphics.Axes.Both,
                Padding = new osu.Framework.Graphics.MarginPadding
                {
                    Left = 52,
                    Right = 52,
                    Top = 92,
                    Bottom = 18,
                },
                Child = workspace,
            });

            Scheduler.AddDelayed(() =>
            {
                if (state == CoachingCaptureState.Empty)
                    return;

                workspace.BeginAnalysisProgress();
                bool complete = state is CoachingCaptureState.Complete or CoachingCaptureState.PracticeMany;
                workspace.SetAnalysisProgress(complete ? 7 : 3, 7, "Blue Zenith [Another]");
                if (complete)
                    workspace.ApplyNewAnalyses(3, 0);
                else if (state == CoachingCaptureState.Error)
                    workspace.SetAnalysisError();
            }, 1200);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 2500);
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

    private enum CoachingCaptureState
    {
        Analysing,
        Complete,
        PracticeMany,
        Error,
        Empty,
    }

    private sealed partial class CaptureReplayAnalysisGame : OsuGameBase
    {
        private readonly GameHost host;
        private readonly ILocalLibrarySource source;
        private readonly string outputPath;
        private readonly int width;
        private readonly int height;
        private readonly Action succeeded;
        private readonly Action<Exception> failed;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public CaptureReplayAnalysisGame(GameHost host, ILocalLibrarySource source, string outputPath, int width, int height, Action succeeded, Action<Exception> failed)
        {
            this.host = host;
            this.source = source;
            this.outputPath = outputPath;
            this.width = width;
            this.height = height;
            this.succeeded = succeeded;
            this.failed = failed;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            LocalReplay[] replays = source.SearchReplaysAsync(new LocalLibraryQuery(Limit: 200)).AsTask().GetAwaiter().GetResult().Items.ToArray();
            var analyses = replays.Take(3).ToDictionary(replay => replay.ScoreId, replay => replayAnalysis());
            var route = new NativeReplayRouteView(source, analyses, _ => { }) { RelativeSizeAxes = osu.Framework.Graphics.Axes.Both };
            Add(route);
            route.SetReplaySummary(replays[0]);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
            Scheduler.AddDelayed(capture, 1800);
        }

        private static ReplayAnalysisResult replayAnalysis()
        {
            ReplayObjectJudgement judgement(int index, double time, string result, ReplayMissAnalysis? miss = null) => new(
                index, null, "HitCircle", time, time, result, "Great", time + (result == "Miss" ? 150 : 5),
                result == "Miss" ? 150 : 5, 1, new ReplayPoint(256, 192), new ReplayPoint(258, 190), index, result == "Miss" ? 0 : index + 1, miss);
            var evidence = new ReplayMissAnalysis(
                ReplayMissReason.LateClick, 32, 4, -18, new ReplayPoint(258, 190), 82, 70,
                new ReplayPoint(326, 192), 8, true, false, true, 0.45, Confidence: 0.9);
            ReplayObjectJudgement[] judgements =
            {
                judgement(0, 1_000, "Great"),
                judgement(1, 2_000, "Ok"),
                judgement(2, 3_000, "Meh"),
                judgement(24, 24_000, "Miss", evidence),
            };
            return new ReplayAnalysisResult(
                ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true,
                ReplayAnalysisProtocol.WallClockTimeoutMs, Array.Empty<int>(), judgements,
                new ReplayJudgementSummary(1, 1, 1, 1, 0, 0));
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

[SupportedOSPlatform("windows")]
internal static class WindowsPrivateDesktopCapture
{
    private const uint generic_all = 0x10000000;

    public static async Task CaptureAsync(
        Func<GameHost, Action, Action<Exception>, Game> createGame,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(createGame);

        string desktopName = $"AimModCapture-{Guid.NewGuid():N}";
        nint desktop = CreateDesktop(desktopName, nint.Zero, nint.Zero, 0, generic_all, nint.Zero);

        if (desktop == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the private capture desktop.");

        Exception? captureError = null;
        Exception? workerError = null;
        var captureFinished = new ManualResetEventSlim();
        GameHost? runningHost = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the renderer thread to the private desktop.");

                var options = new HostOptions
                {
                    FriendlyGameName = "AimMod visual capture",
                    IPCPipeName = null,
                };

                using DesktopGameHost host = Host.GetSuitableDesktopHost($"aimmod-capture-{Guid.NewGuid():N}", options);
                runningHost = host;

                Game game = createGame(
                    host,
                    () => captureFinished.Set(),
                    error =>
                    {
                        captureError = error;
                        captureFinished.Set();
                    });

                host.Run(game);
            }
            catch (Exception error)
            {
                workerError = error;
                captureFinished.Set();
            }
            finally
            {
                runningHost = null;
            }
        })
        {
            IsBackground = true,
            Name = "AimMod private-desktop capture",
        };

        thread.Start();

        try
        {
            bool completed = await Task.Run(() => captureFinished.Wait(timeout)).ConfigureAwait(false);

            if (!completed)
            {
                runningHost?.Exit();
                throw new TimeoutException($"The off-screen capture did not complete within {timeout}.");
            }

            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                runningHost?.Exit();
                throw new TimeoutException("The off-screen graphics host did not stop after the capture completed.");
            }

            if (workerError is not null)
                throw new InvalidOperationException("The off-screen graphics host failed.", workerError);

            if (captureError is not null)
                throw new InvalidOperationException("The renderer could not capture the frame.", captureError);
        }
        finally
        {
            captureFinished.Dispose();

            if (!thread.IsAlive)
            {
                // Graphics backends can briefly retain helper-thread references to the
                // desktop after the host exits. Cleanup is best-effort; Windows releases
                // the isolated desktop when this short-lived test process terminates.
                for (int attempt = 0; attempt < 5 && !CloseDesktop(desktop); attempt++)
                    Thread.Sleep(100);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateDesktop(
        string desktop,
        nint device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}
