using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativePpTargetsWorkspaceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-pp-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void ConstructsWithUnavailableOnlineServices()
    {
        var source = new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), Array.Empty<LocalReplay>());

        Assert.DoesNotThrow(() => _ = new NativePpTargetsWorkspace(source, () => null, () => null));
    }

    [Test]
    public async Task WorkspaceSnapshotRoundTripsAndRemainsFreshForSixHours()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var cache = new PpTargetWorkspaceCache(Path.Combine(temporaryDirectory, "workspace.json"), time);
        PpTargetWorkspaceSnapshot source = snapshot();

        await cache.SaveAsync(source);
        PpTargetWorkspaceSnapshot? loaded = cache.Load();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Profile.ValidRunCount, Is.EqualTo(24));
            Assert.That(loaded.LocalSets, Has.Count.EqualTo(1));
            Assert.That(loaded.Catalog.Single().Difficulties.Single().BeatmapId, Is.EqualTo(456));
            Assert.That(loaded.ExactEstimates[456].ExpectedPp, Is.EqualTo(280));
            Assert.That(cache.IsFresh(loaded), Is.True);
        }

        time.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
        Assert.That(cache.IsFresh(loaded!), Is.False);
    }

    [Test]
    public void CorruptWorkspaceSnapshotIsIgnored()
    {
        string path = Path.Combine(temporaryDirectory, "workspace.json");
        File.WriteAllText(path, "not-json");

        Assert.That(new PpTargetWorkspaceCache(path).Load(), Is.Null);
    }

    [Test]
    public async Task WorkspaceSnapshotWithEstimateForAnotherDifficultyIsIgnored()
    {
        string path = Path.Combine(temporaryDirectory, "workspace.json");
        var cache = new PpTargetWorkspaceCache(path);
        PpTargetWorkspaceSnapshot invalid = snapshot() with
        {
            ExactEstimates = new Dictionary<int, PpTargetEstimate>
            {
                [456] = new(280, 340, new PpTargetRange(260, 300), 1, PpTargetConfidence.High,
                    "Official osu! ruleset", BeatmapId: 999),
            },
        };

        await cache.SaveAsync(invalid);

        Assert.That(cache.Load(), Is.Null);
    }

    [Test]
    public void ExactDifficultyUsesOsuBeatmapProtocol()
    {
        Assert.That(NativePpTargetsWorkspace.BeatmapLaunchUri(456), Is.EqualTo("osu://b/456"));
    }

    [Test]
    public void DropdownsUseReadableProductLabels()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativePpTargetsWorkspace.CategoryLabel(OfficialBeatmapCategory.Ranked), Is.EqualTo("Ranked maps"));
            Assert.That(NativePpTargetsWorkspace.CategoryLabel(OfficialBeatmapCategory.Any), Is.EqualTo("Any status"));
            Assert.That(NativePpTargetsWorkspace.LengthLabel(NativePpTargetsWorkspace.TargetLength.Short), Is.EqualTo("Under 2 minutes"));
            Assert.That(NativePpTargetsWorkspace.LengthLabel(NativePpTargetsWorkspace.TargetLength.Any), Is.EqualTo("Any length"));
            Assert.That(NativePpTargetsWorkspace.SortLabel(NativePpTargetsWorkspace.TargetSort.BestFit), Is.EqualTo("Best personal fit"));
            Assert.That(NativePpTargetsWorkspace.SortLabel(NativePpTargetsWorkspace.TargetSort.MaximumPp), Is.EqualTo("Highest max PP"));
        }
    }

    private static PpTargetWorkspaceSnapshot snapshot()
    {
        var difficulty = new OfficialBeatmapDifficulty(456, "Insane", "osu", 5.2, 180, 125, 4, 9.3f, 8.7f, 6, 10_000, 3_000, 850);
        var set = new OfficialBeatmapSet(123, "Target", "", "Artist", "", "Mapper", "Anime", "ranked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            50_000, 2_000, false, false, null, null, null, null, [difficulty]);
        var localDifficulty = new LocalBeatmapDifficulty(Guid.NewGuid(), 456, "Insane", "osu", 5.2, 180, 125_000, 4, 9.3f, 8.7f, 6, 2, "hash");
        var localSet = new LocalBeatmapSet(Guid.NewGuid(), 123, "Target", "Artist", "Mapper", "Anime", DateTimeOffset.UtcNow, null, [localDifficulty], 2);
        var profile = PpTargetPreferenceProfile.Empty with
        {
            ValidRunCount = 24,
            PpSampleCount = 18,
            TypicalAccuracy = 0.975,
            Confidence = PpTargetConfidence.Medium,
        };
        var estimate = new PpTargetEstimate(280, 340, new PpTargetRange(260, 300), 18, PpTargetConfidence.High, "Official osu! ruleset");
        return new PpTargetWorkspaceSnapshot(DateTimeOffset.MinValue, profile, [localSet], [set], new Dictionary<int, PpTargetEstimate> { [456] = estimate },
            10, string.Empty, string.Empty, 4, 6, OfficialBeatmapCategory.Ranked);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
