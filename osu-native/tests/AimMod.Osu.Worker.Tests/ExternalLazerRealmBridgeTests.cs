using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;
using Realms;
using System.Runtime.Versioning;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public sealed class ExternalLazerRealmBridgeTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-lazer-snapshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task DeletesOnlyTheExactRealmSnapshotAndItsSidecars()
    {
        (RealmLazerLibrarySnapshotFactory factory, LazerLibrarySnapshot snapshot) = await createSnapshot();

        await factory.DeleteSnapshotAsync(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(snapshot.DatabasePath), Is.False);
            Assert.That(File.Exists($"{snapshot.DatabasePath}.lock"), Is.False);
            Assert.That(File.Exists($"{snapshot.DatabasePath}.note"), Is.False);
            Assert.That(Directory.Exists($"{snapshot.DatabasePath}.management"), Is.False);
        });
    }

    [Test]
    public void RefusesToDeleteARecognisablePathNotCreatedByTheFactory()
    {
        Guid snapshotId = Guid.NewGuid();
        string unrelatedPath = Path.Combine(temporaryDirectory, $"lazer-{snapshotId:N}.realm");
        File.WriteAllText(unrelatedPath, "synthetic data");
        var snapshot = new LazerLibrarySnapshot(snapshotId, unrelatedPath, temporaryDirectory, DateTimeOffset.UtcNow);

        ExternalLazerLibraryException exception = Assert.ThrowsAsync<ExternalLazerLibraryException>(async () =>
            await new RealmLazerLibrarySnapshotFactory().DeleteSnapshotAsync(snapshot))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("snapshot_path_invalid"));
            Assert.That(File.Exists(unrelatedPath), Is.True);
        });
    }

    [Test]
    public async Task RefusesAPathSubstitutionForAnOwnedSnapshotId()
    {
        (RealmLazerLibrarySnapshotFactory factory, LazerLibrarySnapshot snapshot) = await createSnapshot();
        string unrelatedDirectory = Path.Combine(temporaryDirectory, "unrelated");
        Directory.CreateDirectory(unrelatedDirectory);
        string unrelatedPath = Path.Combine(unrelatedDirectory, Path.GetFileName(snapshot.DatabasePath));
        File.WriteAllText(unrelatedPath, "synthetic data");

        ExternalLazerLibraryException exception = Assert.ThrowsAsync<ExternalLazerLibraryException>(async () =>
            await factory.DeleteSnapshotAsync(snapshot with { DatabasePath = unrelatedPath }))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("snapshot_path_invalid"));
            Assert.That(File.Exists(unrelatedPath), Is.True);
            Assert.That(File.Exists(snapshot.DatabasePath), Is.True);
        });

        await factory.DeleteSnapshotAsync(snapshot);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task UsesPrivateSnapshotPermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Unix file modes are not available on Windows.");

        (RealmLazerLibrarySnapshotFactory factory, LazerLibrarySnapshot snapshot) = await createSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(snapshot.DatabasePath)!), Is.EqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            Assert.That(File.GetUnixFileMode(snapshot.DatabasePath), Is.EqualTo(UnixFileMode.UserRead));
        });

        await factory.DeleteSnapshotAsync(snapshot);
    }

    [Test]
    public void UsesTheSchemaVersionFromThePinnedPpyRelease()
    {
        // ppy.osu.Game 2026.730.0 source commit 1032a7c31581513c8be751e46f0940e1c95ed252
        // declares RealmAccess.schema_version = 51.
        Assert.Multiple(() =>
        {
            Assert.That(ReplayAnalysisProtocol.EngineVersion, Is.EqualTo("ppy.osu.Game/2026.730.0"));
            Assert.That(RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion, Is.EqualTo(51));
        });
    }

    private async Task<(RealmLazerLibrarySnapshotFactory Factory, LazerLibrarySnapshot Snapshot)> createSnapshot()
    {
        string libraryRoot = Path.Combine(temporaryDirectory, "library");
        string filesRoot = Path.Combine(libraryRoot, "files");
        string snapshotDirectory = Path.Combine(temporaryDirectory, "snapshots");
        string sourcePath = Path.Combine(libraryRoot, "client.realm");
        Directory.CreateDirectory(filesRoot);
        Directory.CreateDirectory(snapshotDirectory);

        using (Realm.GetInstance(new RealmConfiguration(sourcePath)
               {
                   IsDynamic = true,
                   SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
               }))
        {
        }

        var factory = new RealmLazerLibrarySnapshotFactory();
        LazerLibrarySnapshot snapshot = await factory.CreateSnapshotAsync(new ValidatedExternalLazerLibraryLocation(
            libraryRoot,
            sourcePath,
            filesRoot,
            snapshotDirectory));
        return (factory, snapshot);
    }
}
