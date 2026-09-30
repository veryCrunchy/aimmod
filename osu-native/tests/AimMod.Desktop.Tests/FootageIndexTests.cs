using System.Text.Json;
using AimMod.Desktop.Creator;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class FootageIndexTests
{
    private static readonly DateTimeOffset start = new(2026, 5, 1, 18, 0, 0, TimeSpan.FromHours(2));
    private static FootageRecording recording() => new(Guid.NewGuid(), "Evening stream", "https://www.twitch.tv/videos/12345", start, 0, 3600, "PracticePlayer");
    private static LocalReplay score(DateTimeOffset at) => new(Guid.NewGuid(), Guid.Empty, Guid.Empty,
        "Test song", "Test artist", "Insane", "osu", "PracticePlayer", at, 5, .98, 123456, 500, 2, 150, [], false);

    [Test]
    public void LocalRecordingMatchesScoreAndConfirmedTimeWithoutTwitch()
    {
        var local = recording() with { Location = Path.Combine(Path.GetTempPath(), "evening recording.mkv") };
        var play = score(start.AddMinutes(5));
        var library = new FootageLibrary(1, [local], []);
        FootageIndex.Validate(library);
        Assert.That(FootageIndex.Find(play, library).Single().Seconds, Is.EqualTo(300));
        var moment = new FootageMoment(Guid.NewGuid(), local.Id, "Map finish", 310, FootageIndex.ScoreKey(play));
        library = library with { Moments = [moment] };
        Assert.That(FootageIndex.Find(play, library).Single(), Is.EqualTo(new FootageMatch(local, 310, true)));
        Assert.That(FootageIndex.ExportCsv(library), Does.Not.Contain(local.Location));
    }

    [Test]
    public void MatchesUtcAcrossTimezonesWithoutRequiringReplayFile()
    {
        var r = recording(); var s = score(start.AddMinutes(25).ToUniversalTime());
        var matches = FootageIndex.Find(s, new(1, [r], []));
        Assert.That(matches.Single().Seconds, Is.EqualTo(1500));
        Assert.That(matches.Single().Confirmed, Is.False);
    }

    [Test]
    public void RejectsOutsideRecordingAndOtherPlayers()
    {
        var r = recording(); var lib = new FootageLibrary(1, [r], []);
        Assert.That(FootageIndex.Find(score(start.AddSeconds(-1)), lib), Is.Empty);
        Assert.That(FootageIndex.Find(score(start.AddHours(1)), lib), Is.Empty);
        Assert.That(FootageIndex.Find(score(start) with { Player = "AnotherPlayer" }, lib), Is.Empty);
    }

    [Test]
    public void PausedRecordingUsesSeparateSegmentsAndDoesNotFillTheGap()
    {
        var first = recording() with { VideoEndSeconds = 600 };
        var second = recording() with { WallClockStart = start.AddMinutes(20), VideoStartSeconds = 600, VideoEndSeconds = 1200 };
        var lib = new FootageLibrary(1, [first, second], []);
        Assert.That(FootageIndex.Find(score(start.AddMinutes(15)), lib), Is.Empty);
        Assert.That(FootageIndex.Find(score(start.AddMinutes(25)), lib).Single().Seconds, Is.EqualTo(900));
    }

    [Test]
    public void ConfirmedMomentOverridesClockEstimateOnlyForItsScore()
    {
        var r = recording(); var s = score(start.AddMinutes(2));
        var moment = new FootageMoment(Guid.NewGuid(), r.Id, "A play", 101, FootageIndex.ScoreKey(s));
        var lib = new FootageLibrary(1, [r], [moment]);
        Assert.That(FootageIndex.Find(s, lib).Single(), Is.EqualTo(new FootageMatch(r, 101, true)));
        Assert.That(FootageIndex.Find(score(start.AddMinutes(2)), lib).Single().Confirmed, Is.False);
    }

    [Test]
    public void OnlineScoreIdentitySurvivesMovingBetweenLocalLibraryAndPastedLink()
    {
        var local = score(start) with { OnlineScoreId = 42 };
        var online = local with { ScoreId = Guid.NewGuid(), Origin = LocalLibraryOrigin.Online };
        Assert.That(FootageIndex.ScoreKey(online), Is.EqualTo(FootageIndex.ScoreKey(local)));
        Assert.That(FootageIndex.ScoreKey(online with { LegacyScore = true }), Is.Not.EqualTo(FootageIndex.ScoreKey(local)));
    }

    [Test]
    public void AligningClockDoesNotChangeConfirmedMoments()
    {
        var r = recording(); var s = score(start.AddMinutes(10));
        var aligned = FootageIndex.Align(r, s, 500);
        Assert.That(FootageIndex.Find(s, new(1, [aligned], [])).Single().Seconds, Is.EqualTo(500));
        Assert.Throws<ArgumentException>(() => FootageIndex.Align(r, s, 3601));
    }

    [TestCase("01:02:03", 3723)]
    [TestCase("62:03", 3723)]
    [TestCase("00:00:00", 0)]
    public void ParsesVideoTime(string value, int seconds)
    { Assert.That(FootageIndex.TryTimecode(value, out double result), Is.True); Assert.That(result, Is.EqualTo(seconds)); }

    [TestCase("1:99")][TestCase("-1:00")][TestCase("NaN")][TestCase("100000:00:00")][TestCase("10")]
    public void RejectsAmbiguousOrInvalidVideoTime(string value) => Assert.That(FootageIndex.TryTimecode(value, out _), Is.False);

    [Test]
    public void RequiresTimezoneOnRecordingClock()
    {
        Assert.That(FootageIndex.TryWallClock("2026-05-01 18:00:00", out _), Is.False);
        Assert.That(FootageIndex.TryWallClock("2026-05-01 18:00:00 +02:00", out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(start));
    }

    [TestCase("https://www.twitch.tv/videos/12345?t=4h#x", "t=1h2m3s")]
    [TestCase("https://www.youtube.com/watch?v=abcdefghi01&t=1s&start=9", "t=3723s")]
    [TestCase("https://youtu.be/abcdefghi01?si=a", "t=3723s")]
    public void ReplacesTimestampWithoutLosingVideoIdentity(string source, string expected)
    {
        string link = FootageIndex.TimestampUrl(source, 3723)!.AbsoluteUri;
        Assert.That(link, Does.Contain(expected)); Assert.That(link, Does.Not.Contain("start=")); Assert.That(link, Does.Not.Contain("#"));
        if (source.Contains("watch")) Assert.That(link, Does.Contain("v=abcdefghi01"));
    }

    [TestCase("https://youtube.com.evil.example/watch?v=1")]
    [TestCase("javascript:alert(1)")][TestCase("https://user@youtube.com/watch?v=1")]
    [TestCase("https://twitch.tv/some-channel")][TestCase("https://youtube.com/watch")]
    public void RejectsUnsupportedVideoLocations(string value) => Assert.That(FootageIndex.IsSupportedLocation(value), Is.False);

    [Test]
    public void ExportOmitsLocalPathsAndNeutralisesSpreadsheetFormulas()
    {
        string path = Path.Combine(Path.GetTempPath(), "private-stream.mp4");
        var r = recording() with { Location = path, Title = "=1+1" };
        var m = new FootageMoment(Guid.NewGuid(), r.Id, "=HYPERLINK(\"x\")", 40, "private-key");
        string csv = FootageIndex.ExportCsv(new(1, [r], [m]));
        Assert.That(csv, Does.Not.Contain(path)); Assert.That(csv, Does.Not.Contain("private-key"));
        Assert.That(csv, Does.Contain("'=")); Assert.That(csv, Does.Contain("00:00:40"));
    }

    [Test]
    public async Task StoreSurvivesRestartAndRejectsBadEditsWithoutOverwriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-footage-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "footage.json");
        try
        {
            var store = new FootageLibraryStore(path);
            Assert.That((await store.LoadAsync()).Recordings, Is.Empty);
            var r = recording(); await store.SaveAsync(new(1, [r], []));
            var restored = await new FootageLibraryStore(path).LoadAsync();
            Assert.That(restored.Recordings.Single(), Is.EqualTo(r));
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(new(1, [r with { VideoEndSeconds = -2 }], [])));
            Assert.That((await store.LoadAsync()).Recordings.Single(), Is.EqualTo(r));
            await File.WriteAllTextAsync(path, "{broken");
            Assert.ThrowsAsync<JsonException>(async () => await store.LoadAsync());
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("{broken"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase("https://osu.ppy.sh/scores/12345", "scores/12345")]
    [TestCase("https://osu.ppy.sh/scores/osu/45678", "scores/osu/45678")]
    public void ScoreLinksPreserveModernAndLegacyIdentity(string link, string path)
    { Assert.That(OsuScoreAddress.TryParse(link, out var parsed), Is.True); Assert.That(parsed!.ApiPath, Is.EqualTo(path)); }

    [TestCase("https://osu.ppy.sh.evil.example/scores/123")][TestCase("https://osu.ppy.sh/scores/0")]
    [TestCase("https://osu.ppy.sh/scores/123/download")][TestCase("https://osu.ppy.sh/scores/unknown/123")]
    public void ScoreLinksCannotChooseAnArbitraryEndpoint(string link) => Assert.That(OsuScoreAddress.TryParse(link, out _), Is.False);

    [Test]
    public void ParsesModernAndLegacyScoresWithoutInventingTimestamps()
    {
        const string json = """
        {"id":42,"user_id":7,"ended_at":"2026-05-01T16:20:00Z","accuracy":0.99,"max_combo":500,"total_score":900000,
        "pp":150,"statistics":{"miss":2},"mods":[{"acronym":"HD"}],"user":{"username":"PracticePlayer"},
        "beatmap":{"id":123,"version":"Insane","difficulty_rating":5},"beatmapset":{"title":"Test song","artist":"Test artist"}}
        """;
        var score = FootageScoreLookup.Parse(JsonDocument.Parse(json).RootElement, new(42));
        Assert.That(score.PlayedAt, Is.EqualTo(start.AddMinutes(20)));
        Assert.That(score.MissCount, Is.EqualTo(2)); Assert.That(score.Mods, Is.EqualTo(new[] { "HD" }));
        Assert.That(score.TotalScore, Is.EqualTo(900000));
        Assert.Throws<InvalidDataException>(() => FootageScoreLookup.Parse(JsonDocument.Parse(json.Replace("ended_at", "missing_date")).RootElement, new(42)));
        Assert.Throws<InvalidDataException>(() => FootageScoreLookup.Parse(JsonDocument.Parse(json).RootElement, new(43)));
        var legacy = FootageScoreLookup.Parse(JsonDocument.Parse(json.Replace("ended_at", "created_at").Replace("\"miss\":2", "\"count_miss\":2")).RootElement, new(42, "osu"));
        Assert.That(legacy.LegacyScore, Is.True); Assert.That(legacy.MissCount, Is.EqualTo(2));
    }
}
