using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Scoring;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalLibrarySourceTests
{
    [Test]
    public async Task KeepsDifficultiesGroupedUnderTheirBeatmapSet()
    {
        InMemoryLocalLibrarySource source = createSource();

        LocalLibraryPage<LocalBeatmapSet> page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(
            SearchText: "stream Mapper",
            MinimumStars: 4,
            MaximumStars: 6));

        Assert.Multiple(() =>
        {
            Assert.That(page.Total, Is.EqualTo(1));
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Title, Is.EqualTo("Stream Practice"));
            Assert.That(page.Items[0].Difficulties.Select(difficulty => difficulty.Name), Is.EqualTo(new[] { "Insane" }));
        });
    }

    [Test]
    public async Task PagesLargeLibrariesWithoutReturningEveryRow()
    {
        LocalBeatmapSet seed = createSource().SearchBeatmapSetsAsync(new LocalLibraryQuery()).Result.Items[0];
        LocalBeatmapSet[] sets = Enumerable.Range(0, 10_000)
                                           .Select(index => seed with
                                           {
                                               SetId = Guid.NewGuid(),
                                               Title = $"Map {index:D3}",
                                               DateAdded = seed.DateAdded.AddMinutes(index),
                                           })
                                           .ToArray();
        var source = new InMemoryLocalLibrarySource(sets, Array.Empty<LocalReplay>());

        LocalLibraryPage<LocalBeatmapSet> first = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(Offset: 0, Limit: 60));
        LocalLibraryPage<LocalBeatmapSet> second = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(Offset: 60, Limit: 60));

        Assert.Multiple(() =>
        {
            Assert.That(first.Items, Has.Count.EqualTo(60));
            Assert.That(first.Total, Is.EqualTo(10_000));
            Assert.That(first.HasMore, Is.True);
            Assert.That(second.Items, Has.Count.EqualTo(60));
            Assert.That(second.Items.Select(item => item.SetId), Is.Not.EquivalentTo(first.Items.Select(item => item.SetId)));
        });
    }

    [Test]
    public async Task SearchesAndSortsOfflineReplayMetadata()
    {
        InMemoryLocalLibrarySource source = createSource();

        LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
            SearchText: "hidden hardrock",
            Sort: LocalLibrarySort.Accuracy));

        Assert.Multiple(() =>
        {
            Assert.That(page.Total, Is.EqualTo(1));
            Assert.That(page.Items[0].Player, Is.EqualTo("LocalPlayer"));
            Assert.That(page.Items[0].Accuracy, Is.EqualTo(0.982));
            Assert.That(page.Items[0].HasReplayFile, Is.True);
        });
    }

    [Test]
    public async Task BeatmapSortControlsMapToRealLibraryOrdering()
    {
        LocalBeatmapSet seed = (await createSource().SearchBeatmapSetsAsync(new LocalLibraryQuery())).Items[0];
        LocalBeatmapSet alpha = seed with
        {
            SetId = Guid.NewGuid(),
            Title = "Alpha",
            DateAdded = seed.DateAdded.AddDays(-1),
            Difficulties = new[] { difficulty(Guid.NewGuid(), "Hard", 4.2) },
        };
        LocalBeatmapSet zulu = seed with
        {
            SetId = Guid.NewGuid(),
            Title = "Zulu",
            DateAdded = seed.DateAdded.AddDays(1),
            Difficulties = new[] { difficulty(Guid.NewGuid(), "Expert", 6.4) },
        };
        var source = new InMemoryLocalLibrarySource(new[] { zulu, alpha }, Array.Empty<LocalReplay>());

        LocalLibraryPage<LocalBeatmapSet> alphabetical = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(Sort: LocalLibrarySort.Title));
        LocalLibraryPage<LocalBeatmapSet> hardest = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(Sort: LocalLibrarySort.StarRating));

        Assert.Multiple(() =>
        {
            Assert.That(alphabetical.Items.Select(set => set.Title), Is.EqualTo(new[] { "Alpha", "Zulu" }));
            Assert.That(hardest.Items.Select(set => set.Title), Is.EqualTo(new[] { "Zulu", "Alpha" }));
        });
    }

    [Test]
    public void QueryBoundsWorkBeforeAnyManagerAccess()
    {
        LocalLibraryQuery query = new LocalLibraryQuery(MinimumStars: -2, MaximumStars: -1, Offset: -20, Limit: 500).Normalised();

        Assert.Multiple(() =>
        {
            Assert.That(query.MinimumStars, Is.Null);
            Assert.That(query.MaximumStars, Is.Null);
            Assert.That(query.Offset, Is.Zero);
            Assert.That(query.Limit, Is.EqualTo(200));
            Assert.That(typeof(NativeLocalLibraryScreen).IsSubclassOf(typeof(CompositeDrawable)), Is.True);
        });
    }

    [Test]
    public void ProductionSourceUsesOfficialStoresWithoutOwningRealmAccess()
    {
        Type[] fieldTypes = typeof(OsuManagerLocalLibrarySource)
                            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            .Select(field => field.FieldType)
                            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(fieldTypes, Does.Contain(typeof(BeatmapStore)));
            Assert.That(fieldTypes, Does.Contain(typeof(ScoreManager)));
            Assert.That(fieldTypes, Does.Not.Contain(typeof(RealmAccess)));
            Assert.That(fieldTypes, Does.Not.Contain(typeof(BeatmapManager)));
        });
    }

    private static InMemoryLocalLibrarySource createSource()
    {
        Guid setId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid easyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid insaneId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var difficulties = new[]
        {
            difficulty(easyId, "Easy", 2.1),
            difficulty(insaneId, "Insane", 5.2),
        };
        var set = new LocalBeatmapSet(
            setId,
            42,
            "Stream Practice",
            "Synthetic Artist",
            "Mapper",
            "Test fixture",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 2, 3, 4, 5, TimeSpan.Zero),
            difficulties,
            1);
        var replay = new LocalReplay(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            setId,
            insaneId,
            set.Title,
            set.Artist,
            "Hidden Insane",
            "osu",
            "LocalPlayer",
            new DateTimeOffset(2026, 3, 2, 3, 4, 5, TimeSpan.Zero),
            5.2,
            0.982,
            1_000_000,
            900,
            2,
            220.5,
            new[] { "HardRock" },
            true);

        return new InMemoryLocalLibrarySource(new[] { set }, new[] { replay });
    }

    private static LocalBeatmapDifficulty difficulty(Guid id, string name, double stars) => new(
        id,
        -1,
        name,
        "osu",
        stars,
        180,
        120_000,
        4,
        9,
        8,
        6,
        0);
}
