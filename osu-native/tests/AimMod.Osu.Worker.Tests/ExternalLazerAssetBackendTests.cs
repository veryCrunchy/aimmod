using System.Security.Cryptography;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public sealed class ExternalLazerAssetBackendTests
{
    private string testRoot = null!;
    private string libraryRoot = null!;
    private string filesRoot = null!;
    private string snapshotDirectory = null!;
    private string stagingDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"aimmod-asset-backend-{Guid.NewGuid():N}");
        libraryRoot = Path.Combine(testRoot, "lazer");
        filesRoot = Path.Combine(libraryRoot, "files");
        snapshotDirectory = Path.Combine(testRoot, "snapshots");
        stagingDirectory = Path.Combine(testRoot, "staging");
        Directory.CreateDirectory(filesRoot);
        Directory.CreateDirectory(snapshotDirectory);
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(libraryRoot, "client.realm"), "synthetic realm placeholder");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }

    [Test]
    public async Task CopiesAndVerifiesAResolvedAssetBeforeReleasingTheSnapshot()
    {
        byte[] content = "real staged beatmap bytes"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string sourceDirectory = Path.Combine(filesRoot, hash[..1], hash[..2]);
        string sourcePath = Path.Combine(sourceDirectory, hash);
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllBytesAsync(sourcePath, content);
        var reference = new LazerStoredFileReference(LazerLibraryAssetKind.Beatmap, hash, "map.osu", hash);
        var snapshotFactory = new RecordingSnapshotFactory(snapshotDirectory, filesRoot);
        var backend = createBackend(snapshotFactory, new[] { reference });

        ExternalLazerAssetResolveResult result = await backend.ResolveAsync(
            new ExternalLazerAssetResolveRequest(libraryRoot, stagingDirectory, new[] { hash }, Array.Empty<Guid>()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Has.Count.EqualTo(1));
            Assert.That(result.MissingFiles, Is.Empty);
            Assert.That(result.MissingBeatmaps, Is.Empty);
            Assert.That(result.Files[0].StagedPath, Does.StartWith(stagingDirectory + Path.DirectorySeparatorChar));
            Assert.That(File.ReadAllBytes(result.Files[0].StagedPath), Is.EqualTo(content));
            Assert.That(snapshotFactory.DeleteCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CopiesASelectedSkinFileThroughTheSameVerifiedBoundary()
    {
        byte[] content = "real skin ini"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string sourceDirectory = Path.Combine(filesRoot, hash[..1], hash[..2]);
        string sourcePath = Path.Combine(sourceDirectory, hash);
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllBytesAsync(sourcePath, content);
        Guid skinId = Guid.NewGuid();
        var reference = new LazerStoredFileReference(LazerLibraryAssetKind.Skin, skinId.ToString("D"), "skin.ini", hash);
        var backend = createBackend(new RecordingSnapshotFactory(snapshotDirectory, filesRoot), new[] { reference });

        ExternalLazerAssetResolveResult result = await backend.ResolveAsync(
            new ExternalLazerAssetResolveRequest(
                libraryRoot,
                stagingDirectory,
                Array.Empty<string>(),
                Array.Empty<Guid>(),
                new[] { skinId }),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Has.Count.EqualTo(1));
            Assert.That(result.Files[0].Kind, Is.EqualTo("Skin"));
            Assert.That(result.Files[0].LogicalName, Is.EqualTo("skin.ini"));
            Assert.That(result.MissingSkins, Is.Empty);
            Assert.That(File.ReadAllBytes(result.Files[0].StagedPath), Is.EqualTo(content));
        });
    }

    [Test]
    public async Task ReportsAReferencedFileMissingFromTheHashedStore()
    {
        const string hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var reference = new LazerStoredFileReference(LazerLibraryAssetKind.Beatmap, hash, "map.osu", hash);
        var snapshotFactory = new RecordingSnapshotFactory(snapshotDirectory, filesRoot);
        var backend = createBackend(snapshotFactory, new[] { reference });

        ExternalLazerAssetResolveResult result = await backend.ResolveAsync(
            new ExternalLazerAssetResolveRequest(libraryRoot, stagingDirectory, new[] { hash }, Array.Empty<Guid>()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.Empty);
            Assert.That(result.MissingFiles, Has.Count.EqualTo(1));
            Assert.That(result.MissingFiles[0].Code, Is.EqualTo("file_missing"));
            Assert.That(result.MissingBeatmaps, Is.EqualTo(new[] { hash }));
            Assert.That(snapshotFactory.DeleteCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void RejectsAStagingPathThroughASymbolicLinkAncestor()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Creating a junction requires platform-specific privileges on Windows.");

        string liveDirectory = Path.Combine(libraryRoot, "must-not-write");
        string alias = Path.Combine(testRoot, "lazer-alias");
        Directory.CreateDirectory(liveDirectory);
        Directory.CreateSymbolicLink(alias, libraryRoot);
        string aliasedStaging = Path.Combine(alias, "must-not-write");
        var backend = createBackend(
            new RecordingSnapshotFactory(snapshotDirectory, filesRoot),
            Array.Empty<LazerStoredFileReference>());

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ResolveAsync(
                new ExternalLazerAssetResolveRequest(libraryRoot, aliasedStaging, Array.Empty<string>(), Array.Empty<Guid>()),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("staging_path_invalid"));
    }

    private ExternalLazerAssetBackend createBackend(
        RecordingSnapshotFactory snapshotFactory,
        IReadOnlyList<LazerStoredFileReference> references)
    {
        var bridge = new ExternalLazerLibraryImportBridge(
            snapshotFactory,
            new FixedManifestReader(references),
            new ExternalLazerLibraryValidator(),
            new LazerHashedFileResolver());
        return new ExternalLazerAssetBackend(bridge);
    }

    private sealed class RecordingSnapshotFactory(string snapshots, string files) : ILazerLibrarySnapshotFactory
    {
        public int DeleteCount { get; private set; }

        public Task<LazerLibrarySnapshot> CreateSnapshotAsync(
            ValidatedExternalLazerLibraryLocation location,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LazerLibrarySnapshot(
                Guid.NewGuid(),
                Path.Combine(snapshots, "synthetic.realm"),
                files,
                DateTimeOffset.UtcNow));

        public ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedManifestReader(IReadOnlyList<LazerStoredFileReference> references) : ILazerLibraryManifestReader
    {
        public Task<LazerLibraryAssetManifest> ReadManifestAsync(
            LazerLibrarySnapshot snapshot,
            LazerLibraryAssetQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LazerLibraryAssetManifest(references, Array.Empty<string>(), Array.Empty<Guid>()));
    }
}
