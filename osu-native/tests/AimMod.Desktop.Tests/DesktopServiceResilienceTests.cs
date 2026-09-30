using System.ComponentModel;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Updates;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class DesktopServiceResilienceTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("aimmod-service-resilience-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task ReplayAnalysisCacheCoalescesScheduledSavesAndFlushes()
    {
        string path = Path.Combine(directory, "analysis.json");
        var cache = new ReplayAnalysisCache(path, TimeSpan.FromMinutes(5));
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        cache.ScheduleSave(new Dictionary<Guid, ReplayAnalysisResult> { [first] = analysisResult() });
        cache.ScheduleSave(new Dictionary<Guid, ReplayAnalysisResult> { [second] = analysisResult() });
        Assert.That(File.Exists(path), Is.False, "saves wait for a quiet period");
        await cache.FlushAsync();

        IReadOnlyDictionary<Guid, ReplayAnalysisResult> loaded = await cache.LoadAsync();
        Assert.That(loaded.Keys, Is.EquivalentTo(new[] { second }));
    }

    [Test]
    public async Task ReplayAnalysisCacheScheduledSaveEventuallyWritesTheLatestSnapshot()
    {
        string path = Path.Combine(directory, "analysis.json");
        var cache = new ReplayAnalysisCache(path, TimeSpan.FromMilliseconds(20));
        Guid id = Guid.NewGuid();

        cache.ScheduleSave(new Dictionary<Guid, ReplayAnalysisResult>());
        cache.ScheduleSave(new Dictionary<Guid, ReplayAnalysisResult> { [id] = analysisResult() });

        Assert.That(SpinWait.SpinUntil(() => new ReplayAnalysisCache(path).Load().ContainsKey(id), TimeSpan.FromSeconds(5)), Is.True);
        await cache.FlushAsync();
    }

    [Test]
    public async Task ReplayAnalysisCacheLoadToleratesDuplicateEntries()
    {
        string path = Path.Combine(directory, "analysis.json");
        Guid duplicate = Guid.NewGuid();
        var cache = new ReplayAnalysisCache(path);
        await cache.SaveAsync(new Dictionary<Guid, ReplayAnalysisResult> { [duplicate] = analysisResult() });
        string json = await File.ReadAllTextAsync(path);
        int entryStart = json.IndexOf("{\"scoreId\"", StringComparison.Ordinal);
        int entryEnd = json.LastIndexOf("]}", StringComparison.Ordinal);
        string entry = json[entryStart..entryEnd];
        await File.WriteAllTextAsync(path, json[..entryEnd] + "," + entry + json[entryEnd..]);

        Assert.That((await cache.LoadAsync()).Keys, Is.EquivalentTo(new[] { duplicate }));
    }

    [Test]
    public async Task UpdateServiceCallsAfterDisposeAreIgnored()
    {
        var service = new NativeUpdateService(new MemoryUpdatePreferences(), new UnavailableBackendFactory());
        service.Dispose();

        await service.CheckAsync();
        await service.SelectChannelAsync(NativeUpdateChannel.Preview);

        Assert.That(service.State.Stage, Is.EqualTo(NativeUpdateStage.Idle));
    }

    [Test]
    public void DestinationSetterSurvivesPreferenceWriteFailures()
    {
        var service = new OsuBeatmapDestinationService(new UnusedInstaller(), new ThrowingDestinationPreferences(), Path.Combine(directory, "handoff"));
        OsuClientDestination? raised = null;
        service.DestinationChanged += destination => raised = destination;

        Assert.DoesNotThrow(() => service.Destination = OsuClientDestination.Stable);

        Assert.Multiple(() =>
        {
            Assert.That(service.Destination, Is.EqualTo(OsuClientDestination.Stable));
            Assert.That(raised, Is.EqualTo(OsuClientDestination.Stable));
        });
    }

    [Test]
    public async Task StableLaunchWin32FailureBecomesLaunchFailed()
    {
        string executable = Path.Combine(directory, "osu!.exe");
        await File.WriteAllTextAsync(executable, "binary");
        string handoff = Directory.CreateDirectory(Path.Combine(directory, "handoff")).FullName;
        var archive = new LazerBeatmapArchive(7, Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(handoff, $"beatmapset-7-{archive.Id:N}.osz"), "archive");
        var service = new OsuBeatmapDestinationService(new UnusedInstaller(), new MemoryDestinationPreferences(OsuClientDestination.Stable), handoff, executable,
            (_, _, _) => throw new Win32Exception(193));

        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.LaunchFailed));
    }

    [Test]
    public async Task HydrationCacheWithDuplicateKeysStillLoads()
    {
        string cachePath = Path.Combine(directory, "pp-cache.json");
        string key = new('a', 64);
        await File.WriteAllTextAsync(cachePath,
            $"{{\"version\":3,\"entries\":[{{\"key\":\"{key}\",\"performancePoints\":10,\"calculatedAt\":\"2026-01-01T00:00:00+00:00\"}},"
            + $"{{\"key\":\"{key}\",\"performancePoints\":12,\"calculatedAt\":\"2026-01-02T00:00:00+00:00\"}}]}}");

        Assert.DoesNotThrow(() => new LocalScorePpHydrationService(directory, cachePath, (_, _) => Task.FromResult<double?>(null)));
    }

    [Test]
    public void MalformedTimingPointsAreSkippedInsteadOfFailingThePlan()
    {
        string path = Path.Combine(directory, "source.osu");
        File.WriteAllText(path, """
            osu file format v14

            [General]
            AudioFilename: audio.ogg
            Mode: 0

            [Metadata]
            Title:Source Song
            Artist:Artist
            Creator:Mapper
            Version:Original

            [Difficulty]
            CircleSize:4
            SliderMultiplier:1.4

            [TimingPoints]
            0,500,4,2,1,50,1,0
            oops
            1000,notanumber,4,2,1,50,1,0
            2000,0,4,2,1,50,1,0
            3000,-100,4,2,1,50,0,0

            [HitObjects]
            256,192,1000,1,0,0:0:0:0:
            256,192,1200,1,0,0:0:0:0:
            """);

        PracticeSourceBeatmap source = OsuPracticeBeatmapReader.Read(path);

        Assert.That(source.TimingPoints.Select(point => point.TimeMs), Is.EqualTo(new[] { 0d, 3000d }));
    }

    [Test]
    public void BackgroundQueryKeepsRunningAfterAFailureHandlerThrows()
    {
        using var query = new LatestBackgroundQuery<int>();
        using var failed = new ManualResetEventSlim();
        query.Submit(_ => throw new InvalidOperationException("query"), _ => { }, _ =>
        {
            failed.Set();
            throw new InvalidOperationException("handler");
        });
        Assert.That(failed.Wait(TimeSpan.FromSeconds(5)), Is.True);

        int? result = null;
        using var completed = new ManualResetEventSlim();
        Assert.That(SpinWait.SpinUntil(() =>
        {
            query.Submit(_ => 42, value => { result = value; completed.Set(); }, _ => { });
            return completed.Wait(TimeSpan.FromMilliseconds(200));
        }, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void StableCollectionJournalIsBounded()
    {
        const string first = "11111111111111111111111111111111", second = "22222222222222222222222222222222";
        string journal = Path.Combine(directory, "journal");
        for (int index = 0; index < 12; index++)
            PracticeCollectionSync.Stable(directory, journal, index % 2 == 0 ? [first] : [second], index % 2 == 0 ? [second] : [first], () => false);

        Assert.That(Directory.EnumerateFiles(journal).Count(), Is.LessThanOrEqualTo(8));
    }

    [Test, NonParallelizable]
    public void FfmpegLocatorIgnoresRelativePathEntries()
    {
        string tools = Directory.CreateDirectory(Path.Combine(directory, "tools")).FullName;
        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "binary");
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(FfmpegExecutableLocator.Find("tools", null), Is.Null);
                Assert.That(FfmpegExecutableLocator.Find(tools, null), Is.Not.Null);
            });
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Test]
    public void DiskCacheBudgetRejectsAnEntryThatCannotFit()
    {
        string cache = Directory.CreateDirectory(Path.Combine(directory, "cache")).FullName;
        string kept = Path.Combine(cache, new string('c', 64) + ".png");
        File.WriteAllBytes(kept, new byte[16]);

        Assert.Throws<IOException>(() => DiskCacheBudget.Trim(cache, ".png", 0, 1, TimeSpan.FromDays(1), kept));
        Assert.That(File.Exists(kept), Is.False, "an over-budget new entry is rejected");
    }

    private static ReplayAnalysisResult analysisResult() => new(
        ReplayAnalysisProtocol.EngineVersion, "gameplay-clock", true, ReplayAnalysisProtocol.WallClockTimeoutMs, Array.Empty<int>(),
        [new ReplayObjectJudgement(1, null, "HitCircle", 1_000, 1_000, "Great", "Great", 1_000, 0, 1,
            new ReplayPoint(256, 192), new ReplayPoint(250, 190), 0, 0)],
        new ReplayJudgementSummary(1, 0, 0, 0, 0, 0),
        new ReplayAnalysisContentIdentity(new string('a', 64), new string('b', 64)));

    private sealed class MemoryUpdatePreferences : INativeUpdatePreferenceStore
    {
        public NativeUpdateChannel Load() => NativeUpdateChannel.Stable;
        public void Save(NativeUpdateChannel channel) { }
    }

    private sealed class UnavailableBackendFactory : INativeUpdateBackendFactory
    {
        public INativeUpdateBackend Create(NativeUpdateChannel channel) => new UnavailableBackend();
    }

    private sealed class UnavailableBackend : INativeUpdateBackend
    {
        public bool IsInstalled => false;
        public string? CurrentVersion => null;
        public Task<NativeUpdateRelease?> CheckForUpdatesAsync() => Task.FromResult<NativeUpdateRelease?>(null);
        public Task DownloadAsync(NativeUpdateRelease release, Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public void ApplyAndRestart(NativeUpdateRelease release) { }
    }

    private sealed class ThrowingDestinationPreferences : IOsuClientDestinationPreferenceStore
    {
        public OsuClientDestination Load() => OsuClientDestination.Auto;
        public void Save(OsuClientDestination destination) => throw new IOException("disk full");
    }

    private sealed class MemoryDestinationPreferences(OsuClientDestination value) : IOsuClientDestinationPreferenceStore
    {
        public OsuClientDestination Load() => value;
        public void Save(OsuClientDestination destination) { }
    }

    private sealed class UnusedInstaller : ILazerBeatmapInstallService
    {
        public Task<LazerBeatmapArchive> PreserveAsync(string sourceArchive, int beatmapSetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LazerBeatmapInstallResult> InstallAsync(LazerBeatmapArchive archive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Discard(LazerBeatmapArchive archive) { }
    }
}
