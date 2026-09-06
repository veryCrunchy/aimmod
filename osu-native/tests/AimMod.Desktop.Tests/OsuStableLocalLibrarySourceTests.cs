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
    public async Task FindsRenamedAndExportedReplaysWithoutDuplicateScores()
    {
        var database = createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "map.osu");
        Directory.CreateDirectory(Path.Combine(songs, "42 Artist - Title"));
        File.WriteAllText(Path.Combine(songs, "42 Artist - Title", "map.osu"), minimalBeatmap());
        database.Save(Path.Combine(root, "osu!.db"));
        var scores = createScoresDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        scores.Save(Path.Combine(root, "scores.db"));
        var score = scores.Scores.Single().Item2.Single();
        string replay = writeReplay(score, "Replays", "Player - Artist [Insane].osr");
        writeReplay(score, Path.Combine("Data", "r"), "different-filename.osr");
        var result = await new OsuStableLocalLibrarySource(root, songs).SearchReplaysAsync(new());
        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.EqualTo(1));
            Assert.That(result.Items.Single().HasReplayFile, Is.True);
            Assert.That(result.Items.Single().Mods, Is.EqualTo(new[] { "HD" }));
            Assert.That(result.Items.Single().OnlineBeatmapId, Is.EqualTo(84));
            Assert.That(result.Items.Single().ReplayPath, Does.EndWith("different-filename.osr"));
        });
    }

    [Test]
    public async Task ExportedReplayWithoutScoreDatabaseOrInstalledMapRemainsAvailable()
    {
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "missing.osu").Save(Path.Combine(root, "osu!.db"));
        var score = createScoresDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").Scores.Single().Item2.Single();
        var source = new OsuStableLocalLibrarySource(root, songs);
        Assert.That((await source.SearchReplaysAsync(new())).Items, Is.Empty);
        string path = writeReplay(score, "Replays", "export.OSR");
        File.WriteAllText(Path.Combine(root, "Replays", "broken.osr"), "broken");
        var result = await source.SearchReplaysAsync(new());
        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.EqualTo(1));
            Assert.That(result.Items.Single().ReplayPath, Is.EqualTo(path));
            Assert.That(result.Items.Single().BeatmapPath, Is.Empty);
            Assert.That(result.Items.Single().Title, Is.EqualTo("Title"));
            Assert.That(result.Items.Single().TotalScore, Is.EqualTo(score.ReplayScore));
        });
    }

    [Test]
    public async Task DamagedScoreDatabaseDoesNotHideExportedReplays()
    {
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "missing.osu").Save(Path.Combine(root, "osu!.db"));
        File.WriteAllBytes(Path.Combine(root, "scores.db"), [1, 2]);
        var score = createScoresDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").Scores.Single().Item2.Single();
        writeReplay(score, "Replays", "retained.osr");
        var result = await new OsuStableLocalLibrarySource(root, songs).SearchReplaysAsync(new());
        Assert.That(result.Total, Is.EqualTo(1));
        Assert.That(result.Items.Single().HasReplayFile, Is.True);
    }

    [Test]
    public void OversizedReplayMetadataIsRejectedBeforeAllocation()
    {
        string path = Path.Combine(root, "invalid.osr");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write((byte)Ruleset.Standard);
            writer.Write(20260711);
            writer.Write((byte)0x0b);
            writer.Write7BitEncodedInt(int.MaxValue);
        }
        Assert.That(StableReplayHeaders.Read(path), Is.Null);
    }

    [Test]
    public void TruncatedReplayPayloadIsNotIndexed()
    {
        var score = createScoresDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").Scores.Single().Item2.Single();
        string path = writeReplay(score, "Replays", "truncated.osr");
        using (var file = File.OpenWrite(path)) file.SetLength(file.Length - 10);
        Assert.That(StableReplayHeaders.Read(path), Is.Null);
    }

    [Test]
    public async Task MissingMapDoesNotDiscardScoreOnlyHistory()
    {
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "missing.osu").Save(Path.Combine(root, "osu!.db"));
        createScoresDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").Save(Path.Combine(root, "scores.db"));
        var result = await new OsuStableLocalLibrarySource(root, songs).SearchReplaysAsync(new());
        Assert.That(result.Total, Is.EqualTo(1));
        Assert.That(result.Items.Single().HasReplayFile, Is.False);
    }

    private string writeReplay(Score score, string folder, string name)
    {
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, folder)).FullName, name);
        using var writer = new BinaryWriter(File.Create(path));
        void text(string value) { writer.Write((byte)0x0b); writer.Write(value); }
        writer.Write((byte)score.Ruleset);
        writer.Write(score.OsuVersion);
        text(score.BeatmapMD5Hash); text(score.PlayerName); text(score.ReplayMD5Hash);
        writer.Write(score.Count300); writer.Write(score.Count100); writer.Write(score.Count50);
        writer.Write(score.CountGeki); writer.Write(score.CountKatu); writer.Write(score.CountMiss);
        writer.Write(score.ReplayScore); writer.Write(score.Combo); writer.Write(score.PerfectCombo);
        writer.Write((int)score.Mods); text(""); writer.Write(score.ScoreTimestamp.Ticks);
        // Header indexing must not try to decompress cursor frames.
        writer.Write(3); writer.Write(new byte[] { 1, 2, 3 }); writer.Write(score.ScoreId);
        return path;
    }

    [Test]
    public async Task IgnoresEntriesThatEscapeTheSongsDirectory()
    {
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "..\\outside.osu").Save(Path.Combine(root, "osu!.db"));
        LocalLibraryPage<LocalBeatmapSet> maps = await new OsuStableLocalLibrarySource(root, songs)
            .SearchBeatmapSetsAsync(new LocalLibraryQuery());
        Assert.That(maps.Items, Is.Empty);
    }

    [Test]
    public async Task LibraryThumbnailsDoNotDecodeHitObjects()
    {
        string folder = Directory.CreateDirectory(Path.Combine(songs, "42 Artist - Title")).FullName;
        File.WriteAllText(Path.Combine(folder, "map.osu"), minimalBeatmap().Replace(
            "256,192,1000,1,0,0:0:0:0:", "invalid hit object which must not be decoded"));
        File.WriteAllText(Path.Combine(folder, "background.jpg"), "image");
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "map.osu").Save(Path.Combine(root, "osu!.db"));
        var source = new OsuStableLocalLibrarySource(root, songs);
        var maps = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        Assert.That(maps.Items.Single().BackgroundPath, Does.EndWith("background.jpg"));
        Assert.That(source.Progress, Is.Null);
    }

    [Test]
    public async Task FailedSnapshotCanBeRetriedWithoutChangingDatabaseStamp()
    {
        string folder = Directory.CreateDirectory(Path.Combine(songs, "42 Artist - Title")).FullName;
        File.WriteAllText(Path.Combine(folder, "map.osu"), minimalBeatmap());
        string database = Path.Combine(root, "osu!.db");
        createOsuDatabase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "map.osu").Save(database);
        var source = new OsuStableLocalLibrarySource(root, songs);
        using (var locked = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAsync<IOException>(async () => await source.SearchBeatmapSetsAsync(new LocalLibraryQuery()));
        Assert.That((await source.SearchBeatmapSetsAsync(new LocalLibraryQuery())).Total, Is.EqualTo(1));
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
