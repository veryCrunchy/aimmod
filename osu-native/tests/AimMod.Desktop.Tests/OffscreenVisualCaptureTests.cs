using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game;
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
    [TestCase("beatmaps-populated", 1600, 900)]
    [TestCase("skins", 1100, 760)]
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

        await WindowsPrivateDesktopCapture.CaptureAsync(
            (host, succeeded, failed) => route switch
            {
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
                string routeName = route.Split('-', 2)[0];
                MethodInfo routeMethod = typeof(AimModGame).GetMethod($"show{char.ToUpperInvariant(routeName[0])}{routeName[1..]}", BindingFlags.Instance | BindingFlags.NonPublic)
                                         ?? throw new InvalidOperationException($"Unknown capture route '{route}'.");
                Scheduler.AddDelayed(() => routeMethod.Invoke(this, null), 300);
            }

            Scheduler.AddDelayed(capture, 1800);
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

    private sealed partial class CaptureBeatmapGame : OsuGameBase
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

    private sealed partial class CaptureStatisticsGame : OsuGameBase
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
                (_, _) => Task.FromResult(new PracticeMapGenerationResult(true, "Practice map imported", "C:\\AimMod\\Practice")));
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
