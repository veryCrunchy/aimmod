using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

/// <summary>
/// The pattern cache avoids writing files it does not need to: on Windows every new file is checked again by real-time
/// malware scanning.
/// </summary>
public sealed class PpTargetBeatmapCacheTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(directory = Path.Combine(Path.GetTempPath(), $"aimmod-pattern-cache-{Guid.NewGuid():N}"));

    [TearDown]
    public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Test]
    public async Task AnIdenticalCachedMapIsNotRewritten()
    {
        string cache = Path.Combine(directory, "cache");
        var reader = new PpTargetBeatmapPatternReader(cache);
        string scratch = Path.Combine(directory, "a.osu");
        File.WriteAllText(scratch, "osu file format v14\n");
        var file = await PpTargetBeatmapPatternReader.IdentifyAsync(scratch, null, CancellationToken.None);
        await reader.RetainAsync(file, 1, null, CancellationToken.None);
        string kept = Directory.GetFiles(cache, "*.osu").Single();
        var earlier = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(kept, earlier);

        await reader.RetainAsync(file, 1, null, CancellationToken.None);
        Assert.That(File.GetLastWriteTimeUtc(kept), Is.EqualTo(earlier), "an identical copy must not be written again");

        File.WriteAllText(scratch, "osu file format v14\n[General]\n");
        await reader.RetainAsync(await PpTargetBeatmapPatternReader.IdentifyAsync(scratch, null, CancellationToken.None), 1, null, CancellationToken.None);
        Assert.That(File.ReadAllText(kept), Does.Contain("[General]"), "changed content is still written");
    }
}
