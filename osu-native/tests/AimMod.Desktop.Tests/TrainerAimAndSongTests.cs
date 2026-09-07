using AimMod.Desktop.Trainers;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osuTK;

namespace AimMod.Desktop.Tests;

public class TrainerAimAndSongTests
{
    [TestCase(TrainerAimStyle.Balanced)] [TestCase(TrainerAimStyle.WideJumps)]
    [TestCase(TrainerAimStyle.Flow)] [TestCase(TrainerAimStyle.SmallCorrections)] [TestCase(TrainerAimStyle.DirectionChanges)]
    public void AimLayoutsVaryAcrossRunsAndStayWithinPlayfield(TrainerAimStyle style)
    {
        var settings = new TrainerSettings(TrainerKind.Aim, AimStyle: style, AimSpacing: 140, PatternSeed: 31);
        var a = TrainerAimPatterns.Create(settings, 200);
        Assert.That(a, Is.EqualTo(TrainerAimPatterns.Create(settings, 200)));
        Assert.That(a, Is.Not.EqualTo(TrainerAimPatterns.Create(settings with { PatternSeed = 32 }, 200)));
        Assert.That(a.All(p => p.X is >= 64 and <= 448 && p.Y is >= 64 and <= 320), Is.True);
        Assert.That(a.Distinct().Count(), Is.EqualTo(200));
        Assert.That(a.Zip(a.Skip(1), Vector2.Distance).Min(), Is.GreaterThan(20));
        var map = TrainerBeatmap.Create(settings with { CircleSize = 6 });
        Assert.That(map.Difficulty.CircleSize, Is.EqualTo(6));
    }
    [Test]
    public void AimTypesProduceDifferentTravelDistancesAndRespectSpacing()
    {
        double distance(TrainerAimStyle style, int spacing) { var points = TrainerAimPatterns.Create(new(TrainerKind.Aim, AimStyle: style, AimSpacing: spacing), 100); return points.Zip(points.Skip(1), Vector2.Distance).Average(); }
        Assert.That(distance(TrainerAimStyle.WideJumps, 100), Is.GreaterThan(distance(TrainerAimStyle.SmallCorrections, 100) * 3));
        Assert.That(distance(TrainerAimStyle.SmallCorrections, 140), Is.GreaterThan(distance(TrainerAimStyle.SmallCorrections, 70) * 1.5));
    }
    [Test]
    public void SongPickerCollapsesDifficultiesAndUsesLongestStandardTiming()
    {
        LocalBeatmapDifficulty difficulty(string name, double duration, string mode = "osu") => new(Guid.NewGuid(), 0, name, mode, 4, 120, duration, 4, 7, 5, 0, 0);
        var set = new LocalBeatmapSet(Guid.NewGuid(), 0, "Synthetic song", "Synthetic artist", "Mapper", "", DateTimeOffset.UnixEpoch, null,
            [difficulty("Short", 30000), difficulty("Full song", 180000), difficulty("Other ruleset", 200000, "mania")], 0);
        var choices = TrainerSongChoices.FromSets([set]);
        Assert.That(choices.Length, Is.EqualTo(1)); Assert.That(choices[0].Difficulty, Is.EqualTo("Full song"));
    }
    [Test]
    public void EverySongHasAllBpmVariantsAndRandomSelectionAvoidsThePreviousSong()
    {
        foreach (string song in TrainerMusicCatalog.Songs.Keys)
        {
            for (int i = 0; i < 20; i++) Assert.That(TrainerMusicCatalog.RandomSong(song), Is.Not.EqualTo(song));
            foreach (int bpm in TrainerMusicCatalog.Tempos)
            {
                Assert.That(TrainerAudio.Asset(TrainerMusicCatalog.Asset(song, bpm)).Length, Is.GreaterThan(100000));
                Assert.That(new TrainerSession(new(Bpm: bpm, Music: song, Seconds: 180)).EndMs + 600, Is.LessThan(185000));
            }
        }
    }
}
