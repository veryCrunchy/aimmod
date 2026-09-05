using System.Security.Cryptography;
using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetBeatmapPatternReaderTests
{
    private string directory = null!;
    private string beatmapPath = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aimmod-pattern-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        beatmapPath = Path.Combine(directory, "test.osu");
        File.WriteAllText(beatmapPath, beatmap);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [TestCase("NM", 4, 1)]
    [TestCase("HR", 5.2, 1)]
    [TestCase("EZ", 2, 1)]
    [TestCase("DT", 4, 1.5)]
    [TestCase("NC", 4, 1.5)]
    [TestCase("HT", 4, 0.75)]
    public void UsesOfficialModdedRadiusAndRawTimestampsWithSeparateRate(string mod, double cs, double rate)
    {
        PpTargetBeatmapPatternGeometry geometry = PpTargetBeatmapPatternReader.Read(beatmapPath, [mod]);
        double expectedRadius = 32 * (1 - 0.14 * (cs - 5)) * 1.00041;
        Assert.Multiple(() =>
        {
            Assert.That(geometry.HitRadius, Is.EqualTo(expectedRadius).Within(0.0001));
            Assert.That(geometry.ClockRate, Is.EqualTo(rate));
            Assert.That(geometry.Points.Select(point => point.TimeMs), Is.EqualTo(new[] { 1000d, 1500, 2500, 3000 }));
            Assert.That(geometry.Points[0].X, Is.EqualTo(64));
            Assert.That(geometry.Points[0].Y, Is.EqualTo(mod == "HR" ? 320 : 64));
        });
    }

    [Test]
    public void ExcludesSpinnerAndNestedSliderTicksButIncludesSliderHeadOnce()
    {
        PpTargetBeatmapPatternGeometry geometry = PpTargetBeatmapPatternReader.Read(beatmapPath, []);
        Assert.That(geometry.Points.Count, Is.EqualTo(4));
        Assert.That(geometry.Points.Count(point => point.TimeMs == 1500), Is.EqualTo(1));
        Assert.That(geometry.Points.Any(point => point.TimeMs == 2000), Is.False);
        Assert.That(geometry.Points.Single(point => point.TimeMs == 2500).BreakBefore, Is.True);
        Assert.That(PpTargetPatternModel.ExtractFeatures(geometry.Points, geometry.HitRadius, geometry.ClockRate).TransitionCount, Is.EqualTo(2));
    }

    [Test]
    public async Task ContentHashAndRequestedChecksumAreVerified()
    {
        string hash = Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(beatmapPath)));
        PpTargetBeatmapFile original = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, hash, default);
        await File.AppendAllTextAsync(beatmapPath, "\n// updated map\n");
        PpTargetBeatmapFile changed = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, null, default);
        Assert.That(changed.ContentHash, Is.Not.EqualTo(original.ContentHash));
        Assert.ThrowsAsync<InvalidDataException>(async () => await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, hash, default));
    }

    [Test]
    public async Task PersistedGeometryAndSourceAreIndependentOfProfileAndSeparatedByMods()
    {
        string cache = Path.Combine(directory, "cache");
        var reader = new PpTargetBeatmapPatternReader(cache);
        PpTargetBeatmapFile original = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, null, default);
        PpTargetBeatmapPatternGeometry nm = await reader.ReadAsync(original, [], default);
        PpTargetBeatmapPatternGeometry dt = await reader.ReadAsync(original, ["DT"], default);
        await reader.RetainAsync(original, 456, null, default);
        File.Delete(beatmapPath);
        var reopened = new PpTargetBeatmapPatternReader(cache);
        PpTargetBeatmapFile? cached = await reopened.TryGetCachedFileAsync(456, null, default);
        Assert.That(cached, Is.Not.Null);
        Assert.That(cached!.ContentHash, Is.EqualTo(original.ContentHash));
        PpTargetBeatmapPatternGeometry reused = await reopened.ReadAsync(cached, [], default);
        Assert.Multiple(() =>
        {
            Assert.That(reused.Points, Is.EqualTo(nm.Points));
            Assert.That(reused.HitRadius, Is.EqualTo(nm.HitRadius));
            Assert.That(reused.ClockRate, Is.EqualTo(1));
            Assert.That(dt.ClockRate, Is.EqualTo(1.5));
            Assert.That(Directory.GetFiles(cache, "*.json").Length, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task InvalidOrOutdatedGeometryCacheIsRebuilt()
    {
        string cache = Path.Combine(directory, "cache");
        var reader = new PpTargetBeatmapPatternReader(cache);
        PpTargetBeatmapFile file = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, null, default);
        await reader.ReadAsync(file, [], default);
        string geometryPath = Directory.GetFiles(cache, "*.json").Single();
        await File.WriteAllTextAsync(geometryPath, "{invalid json");
        Assert.That((await reader.ReadAsync(file, [], default)).Points.Count, Is.EqualTo(4));
        string json = await File.ReadAllTextAsync(geometryPath);
        await File.WriteAllTextAsync(geometryPath, json.Replace(PpTargetBeatmapPatternReader.Version, "outdated-version"));
        Assert.That((await reader.ReadAsync(file, [], default)).Points.Count, Is.EqualTo(4));
        Assert.That(await File.ReadAllTextAsync(geometryPath), Does.Contain(PpTargetBeatmapPatternReader.Version));
    }

    [Test]
    public async Task UnversionedRemoteSourceExpiresButChecksumAddressedSourceDoesNot()
    {
        var reader = new PpTargetBeatmapPatternReader(Path.Combine(directory, "cache"));
        PpTargetBeatmapFile file = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, null, default);
        await reader.RetainAsync(file, 456, null, default);
        await reader.RetainAsync(file, 456, file.ContentHash, default);
        foreach (string cached in Directory.GetFiles(Path.Combine(directory, "cache"), "*.osu"))
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddDays(-2));
        Assert.That(await reader.TryGetCachedFileAsync(456, null, default), Is.Null);
        Assert.That(await reader.TryGetCachedFileAsync(456, file.ContentHash, default), Is.Not.Null);
    }

    [TestCase("../outside")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\")]
    public void CacheRejectsNonHexadecimalChecksums(string hash)
    {
        var reader = new PpTargetBeatmapPatternReader(Path.Combine(directory, "cache"));
        Assert.ThrowsAsync<ArgumentException>(async () => await reader.TryGetCachedFileAsync(456, hash, default));
        Assert.That(Directory.Exists(Path.Combine(directory, "cache")), Is.False);
    }

    [Test]
    public async Task DiskCacheEvictsOldFilesWithinCountAndByteBudgets()
    {
        string cache = Path.Combine(directory, "cache");
        var reader = new PpTargetBeatmapPatternReader(cache, maximumFiles: 3, maximumBytes: 100_000);
        PpTargetBeatmapFile file = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, null, default);
        for (int id = 1; id <= 6; id++) await reader.RetainAsync(file, id, null, default);
        Assert.That(Directory.GetFiles(cache).Length, Is.EqualTo(3));
        Assert.That(await reader.TryGetCachedFileAsync(6, null, default), Is.Not.Null);
        var tight = new PpTargetBeatmapPatternReader(cache, maximumFiles: 20, maximumBytes: new FileInfo(beatmapPath).Length + 1);
        await tight.RetainAsync(file, 7, null, default);
        Assert.That(Directory.GetFiles(cache).Length, Is.EqualTo(1));
        Assert.That(await tight.TryGetCachedFileAsync(7, null, default), Is.Not.Null);
    }

    [Test]
    public void UnsupportedModeIsNotPretendedToBeStandardGeometry()
    {
        File.WriteAllText(beatmapPath, beatmap.Replace("Mode: 0", "Mode: 3"));
        Assert.Throws<InvalidDataException>(() => PpTargetBeatmapPatternReader.Read(beatmapPath, []));
    }

    private const string beatmap = """
        osu file format v14

        [General]
        AudioFilename: audio.mp3
        Mode: 0

        [Metadata]
        Title:Geometry test
        Artist:AimMod
        Creator:AimMod
        Version:Test
        BeatmapID:456
        BeatmapSetID:123

        [Difficulty]
        HPDrainRate:6
        CircleSize:4
        OverallDifficulty:8
        ApproachRate:9
        SliderMultiplier:1.4
        SliderTickRate:1

        [TimingPoints]
        0,500,4,2,1,50,1,0

        [HitObjects]
        64,64,1000,1,0,0:0:0:0:
        448,320,1500,2,0,L|308:320,1,140
        256,192,2000,8,0,2400
        64,320,2500,1,0,0:0:0:0:
        448,64,3000,1,0,0:0:0:0:
        """;
}
