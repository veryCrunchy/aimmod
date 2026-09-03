using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ExternalLazerLibraryBridgeTests
{
    private string testRoot = null!;
    private string libraryRoot = null!;
    private string snapshotDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"aimmod-lazer-bridge-{Guid.NewGuid():N}");
        libraryRoot = Path.Combine(testRoot, "lazer");
        snapshotDirectory = Path.Combine(testRoot, "snapshots");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "files"));
        Directory.CreateDirectory(snapshotDirectory);
        File.WriteAllText(Path.Combine(libraryRoot, "client.realm"), "synthetic realm placeholder");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }

    [Test]
    public void ValidatesAnExternalReadOnlyBoundary()
    {
        var validator = new ExternalLazerLibraryValidator();

        ValidatedExternalLazerLibraryLocation actual = validator.Validate(new ExternalLazerLibraryLocation(libraryRoot, snapshotDirectory));

        Assert.Multiple(() =>
        {
            Assert.That(actual.DatabasePath, Is.EqualTo(Path.Combine(libraryRoot, "client.realm")));
            Assert.That(actual.FilesRoot, Is.EqualTo(Path.Combine(libraryRoot, "files")));
            Assert.That(actual.SnapshotDirectory, Is.EqualTo(snapshotDirectory));
        });
    }

    [Test]
    public void RejectsSnapshotsInsideTheLazerLibrary()
    {
        string unsafeSnapshots = Path.Combine(libraryRoot, "snapshots");
        Directory.CreateDirectory(unsafeSnapshots);
        var validator = new ExternalLazerLibraryValidator();

        ExternalLazerLibraryException exception = Assert.Throws<ExternalLazerLibraryException>(() =>
            validator.Validate(new ExternalLazerLibraryLocation(libraryRoot, unsafeSnapshots)))!;

        Assert.That(exception.Code, Is.EqualTo("snapshot_location_invalid"));
    }

    [Test]
    public void ResolvesOfficialHashedStorePathsWithoutScanningDirectories()
    {
        const string hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        string storedDirectory = Path.Combine(libraryRoot, "files", "a", "ab");
        string storedPath = Path.Combine(storedDirectory, hash);
        Directory.CreateDirectory(storedDirectory);
        File.WriteAllText(storedPath, "asset");
        var references = new[]
        {
            new LazerStoredFileReference(LazerLibraryAssetKind.Audio, "map-1", "audio.mp3", hash.ToUpperInvariant()),
            new LazerStoredFileReference(LazerLibraryAssetKind.Audio, "map-2", "audio.mp3", hash),
        };

        IReadOnlyList<ResolvedLazerStoredFile> resolved = new LazerHashedFileResolver().Resolve(
            Path.Combine(libraryRoot, "files"),
            references);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Has.Count.EqualTo(2));
            Assert.That(resolved.All(file => file.SourcePath == storedPath), Is.True);
            Assert.That(resolved.All(file => file.Length == 5), Is.True);
            Assert.That(resolved.All(file => file.Reference.Sha256Hash == hash), Is.True);
        });
    }

    [Test]
    public void RejectsNonSha256FileReferences()
    {
        var reference = new LazerStoredFileReference(LazerLibraryAssetKind.Replay, "score", "play.osr", "../client.realm");

        ExternalLazerLibraryException exception = Assert.Throws<ExternalLazerLibraryException>(() =>
            new LazerHashedFileResolver().Resolve(Path.Combine(libraryRoot, "files"), new[] { reference }))!;

        Assert.That(exception.Code, Is.EqualTo("asset_hash_invalid"));
    }

    [Test]
    public void AllowsEmptySkinFilesButRejectsEmptyGameplayAssets()
    {
        const string emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        string storedDirectory = Path.Combine(libraryRoot, "files", "e", "e3");
        Directory.CreateDirectory(storedDirectory);
        File.WriteAllBytes(Path.Combine(storedDirectory, emptyHash), Array.Empty<byte>());
        var resolver = new LazerHashedFileResolver();

        IReadOnlyList<ResolvedLazerStoredFile> skin = resolver.Resolve(
            Path.Combine(libraryRoot, "files"),
            new[] { new LazerStoredFileReference(LazerLibraryAssetKind.Skin, Guid.NewGuid().ToString(), "empty.txt", emptyHash) });
        ExternalLazerLibraryException error = Assert.Throws<ExternalLazerLibraryException>(() => resolver.Resolve(
            Path.Combine(libraryRoot, "files"),
            new[] { new LazerStoredFileReference(LazerLibraryAssetKind.Audio, "map", "audio.mp3", emptyHash) }))!;

        Assert.Multiple(() =>
        {
            Assert.That(skin.Single().Length, Is.Zero);
            Assert.That(error.Code, Is.EqualTo("asset_size_invalid"));
        });
    }

    [Test]
    public void FailedManifestReadsDeleteThePrivateSnapshot()
    {
        var snapshotFactory = new RecordingSnapshotFactory(snapshotDirectory, Path.Combine(libraryRoot, "files"));
        var bridge = new ExternalLazerLibraryImportBridge(
            snapshotFactory,
            new FailingManifestReader(),
            new ExternalLazerLibraryValidator(),
            new LazerHashedFileResolver());

        Assert.ThrowsAsync<InvalidOperationException>(async () => await bridge.ResolveAssetsAsync(
            new ExternalLazerLibraryLocation(libraryRoot, snapshotDirectory),
            new LazerLibraryAssetQuery(Array.Empty<string>(), Array.Empty<Guid>())));

        Assert.That(snapshotFactory.DeleteCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SuccessfulImportsReturnAnIdempotentSnapshotLease()
    {
        var snapshotFactory = new RecordingSnapshotFactory(snapshotDirectory, Path.Combine(libraryRoot, "files"));
        var bridge = new ExternalLazerLibraryImportBridge(
            snapshotFactory,
            new EmptyManifestReader(),
            new ExternalLazerLibraryValidator(),
            new LazerHashedFileResolver());

        ExternalLazerLibraryAssetLease lease = await bridge.ResolveAssetsAsync(
            new ExternalLazerLibraryLocation(libraryRoot, snapshotDirectory),
            new LazerLibraryAssetQuery(Array.Empty<string>(), Array.Empty<Guid>()));
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.That(snapshotFactory.DeleteCount, Is.EqualTo(1));
    }

    private sealed class RecordingSnapshotFactory(string snapshots, string filesRoot) : ILazerLibrarySnapshotFactory
    {
        public int DeleteCount { get; private set; }

        public Task<LazerLibrarySnapshot> CreateSnapshotAsync(
            ValidatedExternalLazerLibraryLocation location,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new LazerLibrarySnapshot(Guid.NewGuid(), Path.Combine(snapshots, "copy.realm"), filesRoot, DateTimeOffset.UtcNow);
            return Task.FromResult(snapshot);
        }

        public ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyManifestReader : ILazerLibraryManifestReader
    {
        public Task<LazerLibraryAssetManifest> ReadManifestAsync(
            LazerLibrarySnapshot snapshot,
            LazerLibraryAssetQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LazerLibraryAssetManifest(Array.Empty<LazerStoredFileReference>(), Array.Empty<string>(), Array.Empty<Guid>()));
    }

    private sealed class FailingManifestReader : ILazerLibraryManifestReader
    {
        public Task<LazerLibraryAssetManifest> ReadManifestAsync(
            LazerLibrarySnapshot snapshot,
            LazerLibraryAssetQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LazerLibraryAssetManifest>(new InvalidOperationException("synthetic failure"));
    }
}
