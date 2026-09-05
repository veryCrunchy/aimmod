using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativePpTargetsWorkspaceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-pp-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void ConstructsWithUnavailableOnlineServices()
    {
        var source = new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), Array.Empty<LocalReplay>());

        Assert.DoesNotThrow(() => _ = new NativePpTargetsWorkspace(source, () => null, () => null));
    }

    [Test]
    public async Task BroadCalculationVisitsAllBatchesNotOnlyFirstFifty()
    {
        var source = new InMemoryLocalLibrarySource([], []);
        using var workspace = new NativePpTargetsWorkspace(source, () => null, () => null);
        var calculator = new RecordingCalculator();
        var requests = Enumerable.Range(1, 123).Select(id => new PpTargetExactRequest(id, null, [], .95, .8)).ToArray();
        var method = typeof(NativePpTargetsWorkspace).GetMethod("calculateExactAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(workspace, [calculator, requests, CancellationToken.None])!;
        Assert.That(calculator.BatchSizes, Is.EqualTo(new[] { 50, 50, 23 }));
        Assert.That(calculator.Ids.Distinct().Count(), Is.EqualTo(123));
    }

    private sealed class RecordingCalculator : IPpTargetExactCalculationService
    {
        public Action? OnBatch { get; init; }
        public List<int> BatchSizes { get; } = [];
        public List<int> Ids { get; } = [];
        public Task<IReadOnlyDictionary<int, PpTargetEstimate>> CalculateAsync(IReadOnlyList<PpTargetExactRequest> requests,
            CancellationToken cancellationToken = default, IProgress<PpTargetExactCalculationProgress>? progress = null)
        {
            BatchSizes.Add(requests.Count);
            Ids.AddRange(requests.Select(request => request.BeatmapId));
            OnBatch?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<int, PpTargetEstimate>>(new Dictionary<int, PpTargetEstimate>());
        }
    }

    [Test]
    public async Task CancellationAfterUncooperativeBatchPreventsFurtherRequests()
    {
        using var cancellation = new CancellationTokenSource();
        using var workspace = new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null);
        var calculator = new RecordingCalculator { OnBatch = cancellation.Cancel };
        var requests = Enumerable.Range(1, 123).Select(id => new PpTargetExactRequest(id, null, [], .95, .8)).ToArray();
        var method = typeof(NativePpTargetsWorkspace).GetMethod("calculateExactAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(workspace, [calculator, requests, cancellation.Token])!;
        Assert.That(calculator.BatchSizes, Is.EqualTo(new[] { 50 }));
    }

    [Test]
    public void CompletedProgressDropsQueuedAndLateCallbacks()
    {
        var queued = new Queue<Action>();
        var reported = new List<string>();
        Type type = typeof(NativePpTargetsWorkspace).GetNestedType("ScanProgress`1", System.Reflection.BindingFlags.NonPublic)!.MakeGenericType(typeof(string));
        var progress = (IProgress<string>)Activator.CreateInstance(type, (Action<Action>)queued.Enqueue, (Func<bool>)(() => true), (Action<string>)reported.Add)!;
        progress.Report("active");
        queued.Dequeue()();
        progress.Report("queued before completion");
        ((IDisposable)progress).Dispose();
        progress.Report("late");
        while (queued.TryDequeue(out var callback)) callback();
        Assert.That(reported, Is.EqualTo(new[] { "active" }));
    }

    [TestCase(PpTargetCatalogScanStopReason.PageLimit, OfficialBeatmapRequestStatus.Success, "page limit reached")]
    [TestCase(PpTargetCatalogScanStopReason.SetLimit, OfficialBeatmapRequestStatus.Success, "set limit reached")]
    [TestCase(PpTargetCatalogScanStopReason.RepeatedCursor, OfficialBeatmapRequestStatus.Success, "repeated page cursor")]
    [TestCase(PpTargetCatalogScanStopReason.RequestFailed, OfficialBeatmapRequestStatus.RateLimited, "osu! rate limit reached")]
    [TestCase(PpTargetCatalogScanStopReason.RequestFailed, OfficialBeatmapRequestStatus.NetworkError, "catalog request failed")]
    public void PartialScanStatusRetainsStopReason(PpTargetCatalogScanStopReason reason, OfficialBeatmapRequestStatus status, string expected)
    {
        Assert.That(NativePpTargetsWorkspace.CatalogScanSummary(new(status, [], 3, reason)), Does.Contain(expected).And.Contain("Partial catalog: 3 pages"));
    }

    [Test]
    public async Task OldSmallPoolCacheIsInvalidatedAndPartialStatusRoundTrips()
    {
        string path = Path.Combine(temporaryDirectory, "workspace.json");
        var cache = new PpTargetWorkspaceCache(path);
        await cache.SaveAsync(snapshot() with { CatalogScanStatus = "Partial catalog: page limit reached." });
        Assert.That(cache.Load()!.CatalogScanStatus, Is.EqualTo("Partial catalog: page limit reached."));
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.That(document["version"]!.GetValue<int>(), Is.EqualTo(5));
        document["version"] = 4;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        Assert.That(cache.Load(), Is.Null);
    }

    [Test]
    public async Task WorkspaceSnapshotRoundTripsAndRemainsFreshForSixHours()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var cache = new PpTargetWorkspaceCache(Path.Combine(temporaryDirectory, "workspace.json"), time);
        PpTargetWorkspaceSnapshot source = snapshot();

        await cache.SaveAsync(source);
        PpTargetWorkspaceSnapshot? loaded = cache.Load();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Profile.ValidRunCount, Is.EqualTo(24));
            Assert.That(loaded.LocalSets, Has.Count.EqualTo(1));
            Assert.That(loaded.Catalog.Single().Difficulties.Single().BeatmapId, Is.EqualTo(456));
            Assert.That(loaded.ExactEstimates[456].ExpectedPp, Is.EqualTo(280));
            Assert.That(cache.IsFresh(loaded), Is.True);
        }

        time.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
        Assert.That(cache.IsFresh(loaded!), Is.False);
    }

    [Test]
    public void CorruptWorkspaceSnapshotIsIgnored()
    {
        string path = Path.Combine(temporaryDirectory, "workspace.json");
        File.WriteAllText(path, "not-json");

        Assert.That(new PpTargetWorkspaceCache(path).Load(), Is.Null);
    }

    [Test]
    public async Task WorkspaceSnapshotWithEstimateForAnotherDifficultyIsIgnored()
    {
        string path = Path.Combine(temporaryDirectory, "workspace.json");
        var cache = new PpTargetWorkspaceCache(path);
        PpTargetWorkspaceSnapshot invalid = snapshot() with
        {
            ExactEstimates = new Dictionary<int, PpTargetEstimate>
            {
                [456] = new(280, 340, new PpTargetRange(260, 300), 1, PpTargetConfidence.High,
                    "Official osu! ruleset", BeatmapId: 999),
            },
        };

        await cache.SaveAsync(invalid);

        Assert.That(cache.Load(), Is.Null);
    }

    [Test]
    public void ExactDifficultyUsesOsuBeatmapProtocol()
    {
        Assert.That(NativePpTargetsWorkspace.BeatmapLaunchUri(456), Is.EqualTo("osu://b/456"));
    }

    [Test]
    public void DropdownsUseReadableProductLabels()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativePpTargetsWorkspace.CategoryLabel(OfficialBeatmapCategory.Ranked), Is.EqualTo("Ranked maps"));
            Assert.That(NativePpTargetsWorkspace.CategoryLabel(OfficialBeatmapCategory.Any), Is.EqualTo("Any status"));
            Assert.That(NativePpTargetsWorkspace.LengthLabel(NativePpTargetsWorkspace.TargetLength.Short), Is.EqualTo("Under 2 minutes"));
            Assert.That(NativePpTargetsWorkspace.LengthLabel(NativePpTargetsWorkspace.TargetLength.Any), Is.EqualTo("Any length"));
            Assert.That(NativePpTargetsWorkspace.SortLabel(NativePpTargetsWorkspace.TargetSort.BestFit), Is.EqualTo("Best skill fit"));
            Assert.That(NativePpTargetsWorkspace.SortLabel(NativePpTargetsWorkspace.TargetSort.MaximumPp), Is.EqualTo("Highest max PP"));
        }
    }

    private static PpTargetWorkspaceSnapshot snapshot()
    {
        var difficulty = new OfficialBeatmapDifficulty(456, "Insane", "osu", 5.2, 180, 125, 4, 9.3f, 8.7f, 6, 10_000, 3_000, 850);
        var set = new OfficialBeatmapSet(123, "Target", "", "Artist", "", "Mapper", "Anime", "ranked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            50_000, 2_000, false, false, null, null, null, null, [difficulty]);
        var localDifficulty = new LocalBeatmapDifficulty(Guid.NewGuid(), 456, "Insane", "osu", 5.2, 180, 125_000, 4, 9.3f, 8.7f, 6, 2, "hash");
        var localSet = new LocalBeatmapSet(Guid.NewGuid(), 123, "Target", "Artist", "Mapper", "Anime", DateTimeOffset.UtcNow, null, [localDifficulty], 2);
        var profile = PpTargetPreferenceProfile.Empty with
        {
            ValidRunCount = 24,
            PpSampleCount = 18,
            TypicalAccuracy = 0.975,
            Confidence = PpTargetConfidence.Medium,
        };
        var estimate = new PpTargetEstimate(280, 340, new PpTargetRange(260, 300), 18, PpTargetConfidence.High, "Official osu! ruleset");
        return new PpTargetWorkspaceSnapshot(DateTimeOffset.MinValue, profile, [localSet], [set], new Dictionary<int, PpTargetEstimate> { [456] = estimate },
            10, string.Empty, string.Empty, 4, 6, OfficialBeatmapCategory.Ranked);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
