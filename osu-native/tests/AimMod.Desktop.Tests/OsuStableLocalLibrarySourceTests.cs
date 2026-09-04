using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using OsuParsers.Database;
using OsuParsers.Database.Objects;
using OsuParsers.Enums;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuStableLocalLibrarySourceTests
{
    private string root = null!;
    private string songs = null!;

    [SetUp]
    public void SetUp()
    {
        root = Directory.CreateTempSubdirectory("aimmod-stable-library-").FullName;
        songs = Directory.CreateDirectory(Path.Combine(root, "Songs")).FullName;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public async Task ReadsStableBeatmapsScoresAndReplayPaths()
    {
        const string beatmapHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string replayHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string setFolder = Directory.CreateDirectory(Path.Combine(songs, "42 Artist - Title")).FullName;
        string beatmapFile = "Artist - Title (Mapper) [Insane].osu";
        File.WriteAllText(Path.Combine(setFolder, beatmapFile), minimalBeatmap());
        File.WriteAllText(Path.Combine(setFolder, "background.jpg"), "image");
        string replayDirectory = Directory.CreateDirectory(Path.Combine(root, "Data", "r")).FullName;
        File.WriteAllText(Path.Combine(replayDirectory, replayHash + ".osr"), "replay");

        createOsuDatabase(beatmapHash, beatmapFile).Save(Path.Combine(root, "osu!.db"));
        createScoresDatabase(beatmapHash, replayHash).Save(Path.Combine(root, "scores.db"));
        var source = new OsuStableLocalLibrarySource(root, songs);

        LocalLibraryPage<LocalBeatmapSet> maps = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        LocalLibraryPage<LocalReplay> replays = await source.SearchReplaysAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(maps.Total, Is.EqualTo(1));
            Assert.That(maps.Items[0].OnlineId, Is.EqualTo(42));
            Assert.That(maps.Items[0].Difficulties[0].StarRating, Is.EqualTo(5.25));
            Assert.That(maps.Items[0].Difficulties[0].Bpm, Is.EqualTo(180));
            Assert.That(maps.Items[0].BackgroundPath, Does.EndWith("background.jpg"));
            Assert.That(replays.Total, Is.EqualTo(1));
            Assert.That(replays.Items[0].Origin, Is.EqualTo(LocalLibraryOrigin.Stable));
            Assert.That(replays.Items[0].Accuracy, Is.EqualTo(0.9333).Within(0.001));
            Assert.That(replays.Items[0].BeatmapPath, Does.EndWith(beatmapFile));
            Assert.That(replays.Items[0].ReplayPath, Does.EndWith(replayHash + ".osr"));
            Assert.That(replays.Items[0].HasReplayFile, Is.True);
        });
    }

    [Test]
    public async Task IgnoresEntriesThatEscapeTheSongsDirectory()
    {
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "..\\outside.osu").Save(Path.Combine(root, "osu!.db"));
        LocalLibraryPage<LocalBeatmapSet> maps = await new OsuStableLocalLibrarySource(root, songs)
            .SearchBeatmapSetsAsync(new LocalLibraryQuery());
        Assert.That(maps.Items, Is.Empty);
    }

    private static OsuDatabase createOsuDatabase(string hash, string fileName)
    {
        var beatmap = new DbBeatmap
        {
            Artist = "Artist",
            ArtistUnicode = "Artist",
            Title = "Title",
            TitleUnicode = "Title",
            Creator = "Mapper",
            Difficulty = "Insane",
            AudioFileName = "audio.mp3",
            MD5Hash = hash,
            FileName = fileName,
            FolderName = "42 Artist - Title",
            Ruleset = Ruleset.Standard,
            StandardStarRating = new Dictionary<Mods, double> { [Mods.None] = 5.25 },
            TaikoStarRating = [],
            CatchStarRating = [],
            ManiaStarRating = [],
            TotalTime = 120_000,
            DrainTime = 110,
            BeatmapId = 84,
            BeatmapSetId = 42,
            ApproachRate = 9,
            CircleSize = 4,
            OverallDifficulty = 8,
            HPDrain = 6,
        };
        beatmap.TimingPoints.Add(new DbTimingPoint { BPM = 180, Offset = 0, Inherited = false });
        return new OsuDatabase
        {
            OsuVersion = 20260711,
            FolderCount = 1,
            AccountUnlocked = true,
            UnlockDate = DateTime.MinValue,
            PlayerName = "player",
            BeatmapCount = 1,
            Beatmaps = [beatmap],
        };
    }

    private static ScoresDatabase createScoresDatabase(string beatmapHash, string replayHash) => new()
    {
        OsuVersion = 20260711,
        Scores =
        [
            Tuple.Create(beatmapHash, new List<Score>
            {
                new()
                {
                    Ruleset = Ruleset.Standard,
                    OsuVersion = 20260711,
                    BeatmapMD5Hash = beatmapHash,
                    PlayerName = "player",
                    ReplayMD5Hash = replayHash,
                    Count300 = 90,
                    Count100 = 10,
                    Count50 = 0,
                    CountMiss = 0,
                    ReplayScore = 1_000_000,
                    Combo = 500,
                    Mods = Mods.Hidden,
                    ScoreTimestamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                    ScoreId = 123,
                },
            }),
        ],
    };

    private static string minimalBeatmap() => """
        osu file format v14

        [General]
        AudioFilename: audio.mp3
        Mode: 0

        [Metadata]
        Title:Title
        Artist:Artist
        Creator:Mapper
        Version:Insane

        [Difficulty]
        HPDrainRate:6
        CircleSize:4
        OverallDifficulty:8
        ApproachRate:9

        [Events]
        0,0,"background.jpg",0,0

        [TimingPoints]
        0,333.333333333,4,2,1,100,1,0

        [HitObjects]
        256,192,1000,1,0,0:0:0:0:
        """;
}
