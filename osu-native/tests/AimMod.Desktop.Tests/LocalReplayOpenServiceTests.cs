using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalReplayOpenServiceTests
{
    [Test]
    public async Task StableReplayCanBeSharedWithoutItsBeatmapAndIsNeverDeletedByTheLease()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-share-").FullName;
        try
        {
            string path = Path.Combine(root, "export.osr");
            File.WriteAllBytes(path, [1, 2, 3]);
            var row = new LocalReplay(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "Missing map", "", "", "osu", "Synthetic player",
                DateTimeOffset.UtcNow, 0, 0.95, 1000, 10, 1, null, [], true, ReplayPath: path, Origin: LocalLibraryOrigin.Stable);
            var service = new CompositeLocalReplayOpenService();
            await using (var lease = await service.OpenReplayFileAsync(row))
                Assert.That(lease.ReplayPath, Is.EqualTo(path));
            Assert.That(File.Exists(path), Is.True);
            Assert.ThrowsAsync<ExternalLazerReplayOpenException>(async () => await service.OpenAsync(row));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task OpensStableReplayFilesWithoutLazerStaging()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-replay-").FullName;
        try
        {
            string beatmap = Path.Combine(root, "map.osu");
            string replay = Path.Combine(root, "play.osr");
            File.WriteAllText(beatmap, "beatmap");
            File.WriteAllText(replay, "replay");
            LocalReplay row = new(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", "Artist", "Insane", "osu", "player",
                DateTimeOffset.UtcNow, 5, 0.98, 1_000_000, 500, 0, null, [], true,
                BeatmapPath: beatmap, ReplayPath: replay, Origin: LocalLibraryOrigin.Stable);

            await using IPlayableReplayBundle bundle = await new CompositeLocalReplayOpenService().OpenAsync(row);

            Assert.Multiple(() =>
            {
                Assert.That(bundle.BeatmapPath, Is.EqualTo(beatmap));
                Assert.That(bundle.ReplayPath, Is.EqualTo(replay));
                Assert.That(bundle.OpenRequest.BeatmapPath, Is.EqualTo(beatmap));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
