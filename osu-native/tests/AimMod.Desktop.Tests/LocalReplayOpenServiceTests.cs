using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalReplayOpenServiceTests
{
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
