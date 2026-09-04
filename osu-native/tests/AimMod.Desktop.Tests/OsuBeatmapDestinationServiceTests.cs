using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuBeatmapDestinationServiceTests
{
    private string root = null!;
    private string executable = null!;

    [SetUp]
    public void SetUp()
    {
        root = Directory.CreateTempSubdirectory("aimmod-destination-").FullName;
        executable = Path.Combine(root, "osu!.exe");
        File.WriteAllText(executable, "fixture");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public async Task StablePreferenceLaunchesStableWithPreservedArchive()
    {
        var lazer = new StubInstallService(root);
        var store = new MemoryPreferenceStore(OsuClientDestination.Stable);
        System.Diagnostics.ProcessStartInfo? observed = null;
        var service = new OsuBeatmapDestinationService(
            lazer, store, root, executable,
            (start, _, _) =>
            {
                observed = start;
                return Task.FromResult(new OsuBeatmapDestinationService.DestinationLaunchOutcome(true, false, 0));
            });
        LazerBeatmapArchive archive = await lazer.PreserveAsync(createArchive(), 42);

        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.LazerStarted));
            Assert.That(result.LauncherSource, Is.EqualTo("osu!stable"));
            Assert.That(observed?.FileName, Is.EqualTo(executable));
            Assert.That(observed?.ArgumentList.Single(), Does.EndWith($"beatmapset-42-{archive.Id:N}.osz"));
            Assert.That(lazer.InstallCalls, Is.Zero);
        });
    }

    [Test]
    public async Task AutoFallsBackToStableWhenLazerIsUnavailable()
    {
        var lazer = new StubInstallService(root) { Result = new(LazerBeatmapInstallStatus.LazerNotFound) };
        var service = new OsuBeatmapDestinationService(
            lazer, new MemoryPreferenceStore(OsuClientDestination.Auto), root, executable,
            (_, _, _) => Task.FromResult(new OsuBeatmapDestinationService.DestinationLaunchOutcome(true, false, 0)));
        LazerBeatmapArchive archive = await lazer.PreserveAsync(createArchive(), 0);

        LazerBeatmapInstallResult result = await service.InstallAsync(archive);

        Assert.Multiple(() =>
        {
            Assert.That(lazer.InstallCalls, Is.EqualTo(1));
            Assert.That(result.LauncherSource, Is.EqualTo("osu!stable"));
        });
    }

    [Test]
    public void PreferenceIsPersistedWhenChanged()
    {
        var store = new MemoryPreferenceStore(OsuClientDestination.Auto);
        var service = new OsuBeatmapDestinationService(new StubInstallService(root), store, root, executable,
            (_, _, _) => Task.FromResult(new OsuBeatmapDestinationService.DestinationLaunchOutcome(true, false, 0)));
        service.Destination = OsuClientDestination.Lazer;
        Assert.That(store.Value, Is.EqualTo(OsuClientDestination.Lazer));
    }

    private string createArchive()
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".osz");
        File.WriteAllText(path, "archive");
        return path;
    }

    private sealed class MemoryPreferenceStore(OsuClientDestination value) : IOsuClientDestinationPreferenceStore
    {
        public OsuClientDestination Value { get; private set; } = value;
        public OsuClientDestination Load() => Value;
        public void Save(OsuClientDestination destination) => Value = destination;
    }

    private sealed class StubInstallService(string root) : ILazerBeatmapInstallService
    {
        public int InstallCalls { get; private set; }
        public LazerBeatmapInstallResult Result { get; init; } = new(LazerBeatmapInstallStatus.Sent);

        public Task<LazerBeatmapArchive> PreserveAsync(string sourceArchive, int beatmapSetId, CancellationToken cancellationToken = default)
        {
            var archive = new LazerBeatmapArchive(beatmapSetId, Guid.NewGuid());
            string target = Path.Combine(root, beatmapSetId == 0
                ? $"practice-{archive.Id:N}.osz"
                : $"beatmapset-{beatmapSetId}-{archive.Id:N}.osz");
            File.Copy(sourceArchive, target);
            return Task.FromResult(archive);
        }

        public Task<LazerBeatmapInstallResult> InstallAsync(LazerBeatmapArchive archive, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            return Task.FromResult(Result);
        }

        public void Discard(LazerBeatmapArchive archive)
        {
        }
    }
}
