using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class ReplayAnalysisStagingTests
{
    [Test]
    public async Task CopiesInputsIntoPrivateTemporaryDirectory()
    {
        await using ReplayAnalysisStaging staging = await ReplayAnalysisStaging.CreateAsync(
            new MemoryStream("osu file"u8.ToArray()),
            new MemoryStream("replay file"u8.ToArray()));

        Assert.Multiple(() =>
        {
            Assert.That(Path.GetExtension(staging.BeatmapPath), Is.EqualTo(".osu"));
            Assert.That(Path.GetExtension(staging.ReplayPath), Is.EqualTo(".osr"));
            Assert.That(File.ReadAllText(staging.BeatmapPath), Is.EqualTo("osu file"));
            Assert.That(File.ReadAllText(staging.ReplayPath), Is.EqualTo("replay file"));
            Assert.That(Path.GetDirectoryName(staging.BeatmapPath), Is.EqualTo(staging.DirectoryPath));
        });
    }

    [Test]
    public async Task DisposeRemovesStagedFiles()
    {
        ReplayAnalysisStaging staging = await ReplayAnalysisStaging.CreateAsync(
            new MemoryStream("osu file"u8.ToArray()),
            new MemoryStream("replay file"u8.ToArray()));
        string directory = staging.DirectoryPath;

        await staging.DisposeAsync();

        Assert.That(Directory.Exists(directory), Is.False);
    }

    [Test]
    public async Task CopiesDirectDifficultyAndReplayFiles()
    {
        string sourceDirectory = Directory.CreateTempSubdirectory("aimmod-staging-source-").FullName;
        string beatmapPath = Path.Combine(sourceDirectory, "map.osu");
        string replayPath = Path.Combine(sourceDirectory, "play.osr");

        try
        {
            await File.WriteAllTextAsync(beatmapPath, "osu file");
            await File.WriteAllTextAsync(replayPath, "replay file");

            await using ReplayAnalysisStaging staging = await ReplayAnalysisStaging.CreateAsync(beatmapPath, replayPath);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(staging.BeatmapPath), Is.EqualTo("osu file"));
                Assert.That(File.ReadAllText(staging.ReplayPath), Is.EqualTo("replay file"));
            });
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Test]
    public void RejectsOversizedBeatmap()
    {
        using var oversized = new MemoryStream(new byte[ReplayAnalysisProtocol.MaximumBeatmapBytes + 1]);
        using var replay = new MemoryStream("replay"u8.ToArray());

        Assert.That(
            async () => await ReplayAnalysisStaging.CreateAsync(oversized, replay),
            Throws.TypeOf<InvalidOperationException>());
    }
}
