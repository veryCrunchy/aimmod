using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class PracticeMapLibraryTests
{
    private string root = null!;
    [SetUp] public void SetUp() => root = Directory.CreateTempSubdirectory("aimmod-practice-library-").FullName;
    [TearDown] public void TearDown() => Directory.Delete(root, true);

    [Test]
    public void SavedDrillsSurviveRestartAndCanBeRenamedAndRemoved()
    {
        var library = new PracticeMapLibrary(root);
        SavedPracticeMap map = create(library);
        library.Save(map with { Title = "Jump control", Favourite = true });
        var reopened = new PracticeMapLibrary(root);
        Assert.That(reopened.List().Single().Title, Is.EqualTo("Jump control"));
        Assert.That(reopened.List().Single().Favourite, Is.True);
        reopened.Delete(map.Id);
        Assert.That(reopened.List(), Is.Empty);
    }

    [Test]
    public void FiltersAndSortingUseSavedMetadata()
    {
        var library = new PracticeMapLibrary(root);
        SavedPracticeMap first = create(library);
        SavedPracticeMap second = create(library) with { Title = "Streams", Scenario = PracticeDrillType.Streams, Favourite = true };
        library.Save(second);
        Assert.That(PracticeMapLibrary.Search(library.List(), "stream", PracticeDrillType.Streams, true, PracticeLibrarySort.Title), Is.EqualTo(new[] { second }));
        Assert.That(PracticeMapLibrary.Search(library.List(), "", PracticeDrillType.LongJumps, false, PracticeLibrarySort.Newest), Is.EqualTo(new[] { first }));
    }

    [Test]
    public void WorkspaceSettingsSurviveRestart()
    {
        var settings = new PracticeWorkspaceSettings("jump", PracticeCandidateSort.RecentlyPlayed,
            PracticeEvidenceFilter.RepeatedAcrossAttempts, 3, 6, 90, 8, 6, 4, 85);
        new PracticeMapLibrary(root).SaveSettings(settings);
        Assert.That(new PracticeMapLibrary(root).LoadSettings(), Is.EqualTo(settings));
    }

    [Test]
    public async Task BackgroundSettingsWritesAreOrderedAndLeaveValidJson()
    {
        var library = new PracticeMapLibrary(root);
        var writes = Enumerable.Range(0, 40)
            .Select(index => library.SaveSettingsAsync(new(Search: $"query-{index}"))).ToArray();
        await Task.WhenAll(writes);
        Assert.That(library.LoadSettings().Search, Is.EqualTo("query-39"));
        Assert.That(File.Exists(Path.Combine(root, "workspace.json.tmp")), Is.False);
    }

    [Test]
    public async Task BackgroundLibraryReadReturnsSavedMetadata()
    {
        var library = new PracticeMapLibrary(root);
        var map = create(library);
        Assert.That(await library.ListAsync(), Is.EqualTo(new[] { map }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await library.ListAsync(cancelled.Token));
        Assert.That(await library.ListAsync(), Has.Count.EqualTo(1));
    }

    [TestCase("../outside")]
    [TestCase(".")]
    [TestCase("map")]
    public void RejectsUnownedPaths(string id)
    {
        var library = new PracticeMapLibrary(root);
        Assert.Throws<ArgumentException>(() => library.ArchivePath(id));
        Assert.Throws<ArgumentException>(() => library.Delete(id));
    }

    [Test]
    public void MissingOrCorruptEntriesDoNotBreakLibrary()
    {
        var library = new PracticeMapLibrary(root);
        SavedPracticeMap map = create(library);
        File.WriteAllText(Path.Combine(root, map.Id, "practice.json"), "not json");
        Assert.That(library.List(), Is.Empty);
        File.Delete(library.ArchivePath(map.Id));
        Assert.Throws<FileNotFoundException>(() => library.Save(map));
    }

    private SavedPracticeMap create(PracticeMapLibrary library)
    {
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, id));
        File.WriteAllBytes(library.ArchivePath(id), [1, 2, 3]);
        var map = new SavedPracticeMap(id, "Practice song", "Hard", PracticeDrillType.LongJumps,
            DateTimeOffset.UtcNow, 10000, 15000, 60000, 6, 120);
        library.Save(map);
        return map;
    }
}
