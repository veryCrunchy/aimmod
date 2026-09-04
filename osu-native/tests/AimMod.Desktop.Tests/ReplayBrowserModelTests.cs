using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayBrowserModelTests
{
    [Test]
    public async Task GroupsAttemptsByExactDifficultyInsteadOfBeatmapSetText()
    {
        Guid setId = Guid.NewGuid();
        Guid hardId = Guid.NewGuid();
        Guid insaneId = Guid.NewGuid();
        LocalReplay[] replays =
        [
            replay(setId, hardId, "Shared title", "Hard", 3),
            replay(setId, hardId, "Shared title", "Hard", 2),
            replay(setId, insaneId, "Shared title", "Insane", 1),
        ];

        ReplayBrowserSnapshot result = await ReplayBrowserModel.LoadAsync(source(replays), "");

        Assert.Multiple(() =>
        {
            Assert.That(result.Maps, Has.Count.EqualTo(2));
            Assert.That(result.Maps.Single(group => group.Difficulty == "Hard").Attempts, Has.Count.EqualTo(2));
            Assert.That(result.Maps.Single(group => group.Difficulty == "Insane").Attempts, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task PaginatesAllRunsAndCapsTheBrowserAtOneHundredNewestMaps()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocalReplay[] replays = Enumerable.Range(0, 105)
            .SelectMany(map => Enumerable.Range(0, 3).Select(attempt => replay(
                Guid.NewGuid(),
                mapId(map),
                $"Map {map:000}",
                "Difficulty",
                map * 10 + attempt,
                now.AddMinutes(-(map * 10 + attempt)))))
            .ToArray();
        var recording = new RecordingReplaySource(replays);

        ReplayBrowserSnapshot result = await ReplayBrowserModel.LoadAsync(recording, "");

        Assert.Multiple(() =>
        {
            Assert.That(recording.Offsets, Is.EqualTo(new[] { 0, 200 }));
            Assert.That(result.TotalReplayCount, Is.EqualTo(315));
            Assert.That(result.TotalMapCount, Is.EqualTo(105));
            Assert.That(result.Maps, Has.Count.EqualTo(100));
            Assert.That(result.Maps.All(group => group.Attempts.Count == 3), Is.True);
            Assert.That(result.Maps.Select(group => group.Title), Does.Not.Contain("Map 104"));
        });
    }

    [Test]
    public async Task UsesBeatmapHashWhenLocalBeatmapIdIsUnavailable()
    {
        LocalReplay first = replay(Guid.Empty, Guid.Empty, "Title", "Hard", 2) with { BeatmapHash = "ABC123" };
        LocalReplay second = replay(Guid.NewGuid(), Guid.Empty, "Renamed title", "Another label", 1) with { BeatmapHash = "abc123" };

        ReplayBrowserSnapshot result = await ReplayBrowserModel.LoadAsync(source([first, second]), "");

        Assert.That(result.Maps.Single().Attempts, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task RouteStartsCollapsedAndRevealsEveryAttemptWhenExpanded()
    {
        Guid beatmapId = Guid.NewGuid();
        ReplayBrowserSnapshot snapshot = await ReplayBrowserModel.LoadAsync(source(
        [
            replay(Guid.NewGuid(), beatmapId, "Title", "Insane", 1),
            replay(Guid.NewGuid(), beatmapId, "Title", "Insane", 2),
        ]), "");
        var route = new NativeReplayRouteView();

        invoke(route, "applyReplayBrowser", snapshot);
        Assert.That(replayRows(route), Has.Count.EqualTo(1), "Only the map header should be visible while collapsed.");

        invoke(route, "toggleReplayMap", snapshot.Maps[0].Key);
        Assert.That(replayRows(route), Has.Count.EqualTo(3), "The map header and both attempts should be visible after expansion.");
    }

    private static ILocalLibrarySource source(IEnumerable<LocalReplay> replays) =>
        new InMemoryLocalLibrarySource([], replays);

    private static Guid mapId(int value)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(value + 1).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static LocalReplay replay(
        Guid setId,
        Guid beatmapId,
        string title,
        string difficulty,
        int ageMinutes,
        DateTimeOffset? playedAt = null) => new(
            Guid.NewGuid(),
            setId,
            beatmapId,
            title,
            "Artist",
            difficulty,
            "osu",
            "Player",
            playedAt ?? DateTimeOffset.UtcNow.AddMinutes(-ageMinutes),
            5,
            0.98,
            1_000_000,
            500,
            1,
            100,
            [],
            true);

    private sealed class RecordingReplaySource(IEnumerable<LocalReplay> replays) : ILocalLibrarySource
    {
        private readonly InMemoryLocalLibrarySource inner = new([], replays);

        public List<int> Offsets { get; } = [];

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            inner.SearchBeatmapSetsAsync(query, cancellationToken);

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Offsets.Add(query.Offset);
            return inner.SearchReplaysAsync(query, cancellationToken);
        }

        public void Invalidate() => inner.Invalidate();
    }

    private static IReadOnlyList<Drawable> replayRows(NativeReplayRouteView route) =>
        ((FillFlowContainer<Drawable>)field(route, "replayList")).Children;

    private static void invoke(NativeReplayRouteView route, string methodName, object argument) =>
        typeof(NativeReplayRouteView).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(route, [argument]);

    private static object field(NativeReplayRouteView route, string fieldName) =>
        typeof(NativeReplayRouteView).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(route)!;
}
