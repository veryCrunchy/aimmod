using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
[NonParallelizable]
public sealed class WorkerResilienceTests
{
    private string testRoot = null!;
    private string libraryRoot = null!;

    [SetUp]
    public void SetUp()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"aimmod-worker-resilience-{Guid.NewGuid():N}");
        libraryRoot = Path.Combine(testRoot, "lazer");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "files"));
        File.WriteAllText(Path.Combine(libraryRoot, "client.realm"), "synthetic realm placeholder");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }

    [Test]
    public void CatalogSearchTimesOutInsteadOfHangingTheWorker()
    {
        var backend = new ReplayAnalysisBackend(
            new NeverEngine(),
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1),
            new UnusedAssetBackend(),
            new HangingCatalogBackend())
        {
            OperationTimeoutOverride = TimeSpan.FromMilliseconds(50),
        };

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.SearchExternalLazerCatalog,
                JsonSerializer.SerializeToElement(
                    new ExternalLazerCatalogSearchRequest("/lazer", ExternalLazerCatalogEntryKind.BeatmapSets),
                    RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("operation_timeout"));
    }

    [Test]
    public void PpCalculationThatIgnoresCancellationStillTimesOut()
    {
        string beatmap = Path.Combine(testRoot, "map.osu");
        File.WriteAllText(beatmap, "osu file format v14");
        var backend = new ReplayAnalysisBackend(
            new NeverEngine(),
            new BlockingPpCalculator(),
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1))
        {
            OperationTimeoutOverride = TimeSpan.FromMilliseconds(50),
        };

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.CalculatePp,
                JsonSerializer.SerializeToElement(
                    new PpWhatIfRequest(testRoot, beatmap, Array.Empty<string>(), 0.98),
                    RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("operation_timeout"));
    }

    [Test]
    public void CallerCancellationIsNotReportedAsATimeout()
    {
        var backend = new ReplayAnalysisBackend(
            new NeverEngine(),
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1),
            new UnusedAssetBackend(),
            new HangingCatalogBackend());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.SearchExternalLazerCatalog,
                JsonSerializer.SerializeToElement(
                    new ExternalLazerCatalogSearchRequest("/lazer", ExternalLazerCatalogEntryKind.BeatmapSets),
                    RuntimeProtocol.JsonOptions),
                cancellation.Token));
    }

    [Test]
    public async Task CatalogCleanupFailureNeverReplacesTheResult()
    {
        var factory = new FakeSnapshotFactory { ThrowOnDelete = true };
        var backend = new ExternalLazerCatalogBackend(factory, new FixedCatalogReader(), new ExternalLazerLibraryValidator());

        ExternalLazerCatalogSearchResult result = await backend.SearchAsync(
            new ExternalLazerCatalogSearchRequest(libraryRoot, ExternalLazerCatalogEntryKind.BeatmapSets),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.EqualTo(7));
            Assert.That(factory.DeleteCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void CatalogCleanupFailureNeverMasksTheOriginalError()
    {
        var factory = new FakeSnapshotFactory { ThrowOnDelete = true };
        var backend = new ExternalLazerCatalogBackend(factory, new FixedCatalogReader { Failure = new ExternalLazerLibraryException("catalog_too_large", "too large") }, new ExternalLazerLibraryValidator());

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.SearchAsync(
                new ExternalLazerCatalogSearchRequest(libraryRoot, ExternalLazerCatalogEntryKind.BeatmapSets),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("catalog_too_large"));
    }

    [Test]
    public async Task SnapshotCacheReusesAnUnchangedDatabaseAndRefreshesAChangedOne()
    {
        var inner = new FakeSnapshotFactory();
        using var cache = new CachedLazerLibrarySnapshotFactory(inner, TimeSpan.FromMinutes(5));
        ValidatedExternalLazerLibraryLocation location = validatedLocation();

        LazerLibrarySnapshot first = await cache.CreateSnapshotAsync(location);
        LazerLibrarySnapshot second = await cache.CreateSnapshotAsync(location);
        await File.WriteAllTextAsync(location.DatabasePath, "a much longer synthetic realm placeholder");
        LazerLibrarySnapshot third = await cache.CreateSnapshotAsync(location);

        Assert.Multiple(() =>
        {
            Assert.That(second.SnapshotId, Is.EqualTo(first.SnapshotId));
            Assert.That(third.SnapshotId, Is.Not.EqualTo(first.SnapshotId));
            Assert.That(inner.CreateCalls, Is.EqualTo(2));
            Assert.That(inner.DeleteCalls, Is.Zero);
            Assert.That(File.Exists(first.DatabasePath), Is.True);
        });

        await cache.DeleteSnapshotAsync(first);
        Assert.That(inner.DeleteCalls, Is.Zero);
        await cache.DeleteSnapshotAsync(second);
        Assert.Multiple(() =>
        {
            Assert.That(inner.DeleteCalls, Is.EqualTo(1));
            Assert.That(Directory.Exists(Path.GetDirectoryName(first.DatabasePath)), Is.False);
            Assert.That(File.Exists(third.DatabasePath), Is.True);
        });
    }

    [Test]
    public async Task SnapshotCacheExpiresSnapshotsByAge()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var inner = new FakeSnapshotFactory();
        using var cache = new CachedLazerLibrarySnapshotFactory(inner, TimeSpan.FromSeconds(30), clock);
        ValidatedExternalLazerLibraryLocation location = validatedLocation();

        LazerLibrarySnapshot first = await cache.CreateSnapshotAsync(location);
        await cache.DeleteSnapshotAsync(first);
        clock.Advance(TimeSpan.FromSeconds(31));
        LazerLibrarySnapshot second = await cache.CreateSnapshotAsync(location);

        Assert.Multiple(() =>
        {
            Assert.That(second.SnapshotId, Is.Not.EqualTo(first.SnapshotId));
            Assert.That(inner.CreateCalls, Is.EqualTo(2));
            Assert.That(inner.DeleteCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposingTheSnapshotCacheRemovesItsSnapshots()
    {
        var inner = new FakeSnapshotFactory();
        var cache = new CachedLazerLibrarySnapshotFactory(inner, TimeSpan.FromMinutes(5));
        LazerLibrarySnapshot snapshot = await cache.CreateSnapshotAsync(validatedLocation());
        string directory = Path.GetDirectoryName(snapshot.DatabasePath)!;

        cache.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(inner.DeleteCalls, Is.EqualTo(1));
            Assert.That(Directory.Exists(directory), Is.False);
        });
    }

    [Test]
    public void SnapshotCacheRemovesItsDirectoryWhenCreationFails()
    {
        var inner = new FakeSnapshotFactory { ThrowOnCreate = true };
        using var cache = new CachedLazerLibrarySnapshotFactory(inner, TimeSpan.FromMinutes(5));

        Assert.ThrowsAsync<IOException>(async () => await cache.CreateSnapshotAsync(validatedLocation()));

        Assert.That(inner.LastSnapshotDirectory is null || !Directory.Exists(inner.LastSnapshotDirectory), Is.True);
    }

    private ValidatedExternalLazerLibraryLocation validatedLocation() => new(
        libraryRoot,
        Path.Combine(libraryRoot, "client.realm"),
        Path.Combine(libraryRoot, "files"),
        testRoot);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private sealed class FakeSnapshotFactory : ILazerLibrarySnapshotFactory
    {
        public bool ThrowOnDelete { get; init; }
        public bool ThrowOnCreate { get; init; }
        public int CreateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string? LastSnapshotDirectory { get; private set; }

        public Task<LazerLibrarySnapshot> CreateSnapshotAsync(
            ValidatedExternalLazerLibraryLocation location,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastSnapshotDirectory = location.SnapshotDirectory;
            if (ThrowOnCreate)
                throw new IOException("synthetic create failure");

            string path = Path.Combine(location.SnapshotDirectory, $"snapshot-{Guid.NewGuid():N}.realm");
            File.WriteAllText(path, "snapshot");
            return Task.FromResult(new LazerLibrarySnapshot(Guid.NewGuid(), path, location.FilesRoot, DateTimeOffset.UtcNow));
        }

        public ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot)
        {
            DeleteCalls++;
            if (ThrowOnDelete)
                throw new ExternalLazerLibraryException("snapshot_path_invalid", "synthetic delete failure");

            File.Delete(snapshot.DatabasePath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedCatalogReader : ILazerLibraryCatalogReader
    {
        public Exception? Failure { get; init; }

        public Task<ExternalLazerCatalogSearchResult> ReadCatalogAsync(
            LazerLibrarySnapshot snapshot,
            ExternalLazerCatalogSearchRequest query,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new ExternalLazerCatalogSearchResult(
                query.Kind,
                Array.Empty<ExternalLazerBeatmapSet>(),
                Array.Empty<ExternalLazerReplaySummary>(),
                7,
                query.Offset,
                query.Limit));
        }
    }

    private sealed class NeverEngine : IReplayAnalysisEngine
    {
        public ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay analysis is not expected.");
    }

    private sealed class UnusedAssetBackend : IExternalLazerAssetBackend
    {
        public ValueTask<ExternalLazerAssetResolveResult> ResolveAsync(
            ExternalLazerAssetResolveRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asset resolution is not expected.");
    }

    private sealed class HangingCatalogBackend : IExternalLazerCatalogBackend
    {
        public async ValueTask<ExternalLazerCatalogSearchResult> SearchAsync(
            ExternalLazerCatalogSearchRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BlockingPpCalculator : IPpWhatIfCalculator
    {
        public ValueTask<PpWhatIfResult> CalculateAsync(ValidatedPpInput input, CancellationToken cancellationToken)
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
