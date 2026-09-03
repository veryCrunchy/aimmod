using AimMod.Desktop;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Game;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeReplayRouteTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-native-replay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void NativeRouteUsesOfficialPlayerWithoutFullOsuClient()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(AimModGame).IsSubclassOf(typeof(OsuGameBase)), Is.True);
            Assert.That(typeof(NativeReplayPlayer).IsSubclassOf(typeof(ReplayPlayer)), Is.True);
            Assert.That(typeof(ImportedBeatmapReplayDecoder).IsSubclassOf(typeof(LegacyScoreDecoder)), Is.True);
            Assert.That(typeof(AimModGame).Assembly.GetReferencedAssemblies().Select(reference => reference.Name),
                Does.Not.Contain("osu.Desktop"));
        });
    }

    [Test]
    public void ReplayTransportRejectsCommandsUntilOfficialPlayerIsReady()
    {
        using var player = new NativeReplayPlayer(new Score(), () => { }, _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(player.IsTransportReady.Value, Is.False);
            Assert.That(player.CurrentTime.Value, Is.Zero);
            Assert.That(player.Duration.Value, Is.Zero);
            Assert.That(player.IsPaused.Value, Is.True);
            Assert.That(player.PlaybackRate.Value, Is.EqualTo(1));
            Assert.That(player.TogglePause(), Is.False);
            Assert.That(player.SetPaused(false), Is.False);
            Assert.That(player.SeekTo(5000), Is.False);
            Assert.That(player.SeekTo(double.NaN), Is.False);
            Assert.That(player.SetPlaybackRate(1.5), Is.False);
            Assert.That(player.SetPlaybackRate(double.PositiveInfinity), Is.False);
        });
    }

    [Test]
    public void TimelineIsBoundedButNeverDropsLateExactMisses()
    {
        ReplayObjectJudgement[] ordinary = Enumerable.Range(0, 2000)
                                                     .Select(index => judgement(index, index * 10, "Great"))
                                                     .Append(judgement(2000, 25_000, "Miss"))
                                                     .ToArray();
        var result = new ReplayAnalysisResult(
            "test",
            "beatmap",
            true,
            1000,
            Array.Empty<int>(),
            ordinary,
            new ReplayJudgementSummary(2000, 0, 0, 1, 0, 0));

        IReadOnlyList<ReplayTimelineMark> marks = ReplayTimelineSampler.Sample(result, 100);

        Assert.Multiple(() =>
        {
            Assert.That(marks, Has.Count.LessThanOrEqualTo(100));
            Assert.That(marks.Any(mark => mark.Tone == ReplayTimelineTone.Miss && mark.TimeMilliseconds == 25_000), Is.True);
            Assert.That(marks.Select(mark => mark.TimeMilliseconds), Is.Ordered);
        });
    }

    [Test]
    public void ParsesPairedBeatmapAndReplayFiles()
    {
        string beatmap = createFile("set.osz");
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--beatmap", beatmap, "--replay", replay });

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Replay?.BeatmapPath, Is.EqualTo(Path.GetFullPath(beatmap)));
            Assert.That(result.Replay?.ReplayPath, Is.EqualTo(Path.GetFullPath(replay)));
        });
    }

    [Test]
    public void RejectsAnIncompleteReplayRequest()
    {
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--replay", replay });

        Assert.Multiple(() =>
        {
            Assert.That(result.Replay, Is.Null);
            Assert.That(result.Error, Is.EqualTo("Opening a replay needs both --beatmap and --replay."));
        });
    }

    [Test]
    public void RejectsUnsupportedFileTypesBeforeStartingTheGame()
    {
        string beatmap = createFile("set.zip");
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--beatmap", beatmap, "--replay", replay });

        Assert.That(result.Error, Does.Contain(".osz"));
    }

    private string createFile(string name)
    {
        string path = Path.Combine(temporaryDirectory, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static ReplayObjectJudgement judgement(int index, double time, string result) => new(
        index,
        null,
        "HitCircle",
        time,
        time,
        result,
        "Great",
        time,
        0,
        1,
        null,
        null,
        0,
        0);
}
