using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Models;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using Realms;
using System.Runtime.CompilerServices;

namespace AimMod.Osu.Worker.Tests;

[SetUpFixture]
public sealed class ExternalLazerModelBootstrap
{
    [OneTimeSetUp]
    public void LoadPinnedPpyRealmModelsBeforeAnyRealmOpens() =>
        RuntimeHelpers.RunModuleConstructor(typeof(BeatmapInfo).Module.ModuleHandle);
}

[TestFixture]
[NonParallelizable]
public sealed class ExternalLazerCatalogReaderTests
{
    private string temporaryDirectory = null!;
    private string realmPath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-catalog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        realmPath = Path.Combine(temporaryDirectory, "catalog.realm");
        createSyntheticRealm();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task GroupsDifficultiesAndUsesPinnedRealmMetadataFields()
    {
        var reader = new DynamicRealmLazerLibraryCatalogReader();

        ExternalLazerCatalogSearchResult result = await reader.ReadCatalogAsync(
            snapshot(),
            new ExternalLazerCatalogSearchRequest(
                temporaryDirectory,
                ExternalLazerCatalogEntryKind.BeatmapSets,
                SearchText: "artist mapper hard",
                MinimumStars: 4,
                Limit: 20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Total, Is.EqualTo(1));
            Assert.That(result.BeatmapSets, Has.Count.EqualTo(1));
            Assert.That(result.Replays, Is.Empty);
            Assert.That(result.BeatmapSets[0].Title, Is.EqualTo("Fixture Remix"));
            Assert.That(result.BeatmapSets[0].Creator, Is.EqualTo("Fixture Mapper"));
            Assert.That(result.BeatmapSets[0].Difficulties.Select(difficulty => difficulty.Name), Is.EqualTo(new[] { "Hard" }));
            Assert.That(result.BeatmapSets[0].Difficulties[0].LocalScoreCount, Is.EqualTo(1));
            Assert.That(result.BeatmapSets[0].LocalReplayCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ReturnsBoundedReplaySummariesAndPagesThem()
    {
        var reader = new DynamicRealmLazerLibraryCatalogReader();
        var query = new ExternalLazerCatalogSearchRequest(
            temporaryDirectory,
            ExternalLazerCatalogEntryKind.Replays,
            SearchText: "fixture hd",
            Sort: ExternalLazerCatalogSort.Accuracy,
            Limit: 1);

        ExternalLazerCatalogSearchResult first = await reader.ReadCatalogAsync(snapshot(), query);
        ExternalLazerCatalogSearchResult second = await reader.ReadCatalogAsync(snapshot(), query with { Offset = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(first.Total, Is.EqualTo(2));
            Assert.That(first.Replays, Has.Count.EqualTo(1));
            Assert.That(first.HasMore, Is.True);
            Assert.That(first.Replays[0].Accuracy, Is.EqualTo(0.98));
            Assert.That(first.Replays[0].MissCount, Is.EqualTo(2));
            Assert.That(first.Replays[0].Mods, Is.EqualTo(new[] { "HD" }));
            Assert.That(first.Replays[0].HasReplayFile, Is.True);
            Assert.That(second.Replays, Has.Count.EqualTo(1));
            Assert.That(second.Replays[0].ScoreId, Is.Not.EqualTo(first.Replays[0].ScoreId));
        });
    }

    [TestCase(ExternalLazerCatalogProtocol.MaximumPageSize + 1, 0)]
    [TestCase(1, ExternalLazerCatalogProtocol.MaximumOffset + 1)]
    public void RejectsPagingOutsideProtocolBounds(int limit, int offset)
    {
        var reader = new DynamicRealmLazerLibraryCatalogReader();
        var query = new ExternalLazerCatalogSearchRequest(
            temporaryDirectory,
            ExternalLazerCatalogEntryKind.BeatmapSets,
            Offset: offset,
            Limit: limit);

        ExternalLazerLibraryException exception = Assert.ThrowsAsync<ExternalLazerLibraryException>(async () =>
            await reader.ReadCatalogAsync(snapshot(), query))!;

        Assert.That(exception.Code, Is.EqualTo("catalog_query_invalid"));
    }

    private LazerLibrarySnapshot snapshot() =>
        new(Guid.NewGuid(), realmPath, Path.Combine(temporaryDirectory, "files"), DateTimeOffset.UtcNow);

    private void createSyntheticRealm()
    {
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "files"));
        var configuration = new RealmConfiguration(realmPath)
        {
            SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
        };
        using Realm realm = Realm.GetInstance(configuration);
        realm.Write(() =>
        {
            RulesetInfo ruleset = realm.Add(new RulesetInfo("osu", "osu!", "fixture", 0));
            BeatmapSetInfo firstSet = createSet(ruleset, "Fixture Title", "Fixture Artist", "Easy", 2.1, 'a');
            BeatmapSetInfo secondSet = createSet(ruleset, "Fixture Remix", "Second Artist", "Hard", 5.2, 'b');
            realm.Add(firstSet);
            realm.Add(secondSet);

            realm.Add(createScore(firstSet.Beatmaps[0], ruleset, 0.92, 900_000, false));
            realm.Add(createScore(secondSet.Beatmaps[0], ruleset, 0.98, 1_200_000, true));
        });
    }

    private static BeatmapSetInfo createSet(
        RulesetInfo ruleset,
        string title,
        string artist,
        string difficultyName,
        double stars,
        char hashCharacter)
    {
        string hash = new(hashCharacter, 64);
        var beatmap = new BeatmapInfo(
            ruleset,
            new BeatmapDifficulty
            {
                CircleSize = 4,
                ApproachRate = 9,
                OverallDifficulty = 8,
                DrainRate = 6,
            },
            new BeatmapMetadata(new RealmUser { OnlineID = 42, Username = "Fixture Mapper" })
            {
                Title = title,
                Artist = artist,
                Source = "Fixture Source",
                Tags = "fixture tags",
            })
        {
            DifficultyName = difficultyName,
            Hash = hash,
            MD5Hash = new string(hashCharacter, 32),
            StarRating = stars,
            BPM = 180,
            Length = 120_000,
            LastPlayed = new DateTimeOffset(2026, 2, hashCharacter == 'a' ? 1 : 2, 0, 0, 0, TimeSpan.Zero),
        };
        var set = new BeatmapSetInfo(new[] { beatmap })
        {
            DateAdded = new DateTimeOffset(2026, 1, hashCharacter == 'a' ? 1 : 2, 0, 0, 0, TimeSpan.Zero),
        };
        beatmap.BeatmapSet = set;
        return set;
    }

    private static ScoreInfo createScore(
        BeatmapInfo beatmap,
        RulesetInfo ruleset,
        double accuracy,
        long totalScore,
        bool replayFile)
    {
        var score = new ScoreInfo(beatmap, ruleset, new RealmUser { OnlineID = 7, Username = "Fixture Player" })
        {
            BeatmapHash = beatmap.Hash,
            Accuracy = accuracy,
            TotalScore = totalScore,
            MaxCombo = 500,
            Date = new DateTimeOffset(2026, 3, replayFile ? 2 : 1, 0, 0, 0, TimeSpan.Zero),
            PP = replayFile ? 220.5 : 100,
            ModsJson = "[{\"acronym\":\"HD\"}]",
            StatisticsJson = "{\"Miss\":2}",
        };
        if (replayFile)
        {
            score.Files.Add(new RealmNamedFileUsage(
                new RealmFile { Hash = new string('c', 64) },
                "fixture.osr"));
        }

        return score;
    }
}
