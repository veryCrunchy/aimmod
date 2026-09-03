using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayAnalysisCacheTests
{
    private string temporaryDirectory = null!;
    private string cachePath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-analysis-cache-{Guid.NewGuid():N}");
        cachePath = Path.Combine(temporaryDirectory, "replay-analysis.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task RoundTripsCompletedAnalysis()
    {
        Guid scoreId = Guid.NewGuid();
        ReplayAnalysisResult result = createResult("Miss");
        var cache = new ReplayAnalysisCache(cachePath);

        await cache.SaveAsync(new Dictionary<Guid, ReplayAnalysisResult> { [scoreId] = result });
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> loaded = cache.Load();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Keys, Is.EquivalentTo(new[] { scoreId }));
            Assert.That(loaded[scoreId].EngineVersion, Is.EqualTo(result.EngineVersion));
            Assert.That(loaded[scoreId].Summary, Is.EqualTo(result.Summary));
            Assert.That(loaded[scoreId].Judgements, Is.EqualTo(result.Judgements));
            Assert.That(loaded[scoreId].Judgements.Single().Result, Is.EqualTo("Miss"));
        });
    }

    [Test]
    public void InvalidJsonFailsClosed()
    {
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(cachePath, "not-json");

        IReadOnlyDictionary<Guid, ReplayAnalysisResult> loaded = new ReplayAnalysisCache(cachePath).Load();

        Assert.That(loaded, Is.Empty);
    }

    [Test]
    public async Task DifferentEngineVersionIsDiscarded()
    {
        Guid scoreId = Guid.NewGuid();
        ReplayAnalysisResult stale = createResult("Great") with { EngineVersion = "old-engine" };
        var cache = new ReplayAnalysisCache(cachePath);

        await cache.SaveAsync(new Dictionary<Guid, ReplayAnalysisResult> { [scoreId] = stale });

        Assert.That(cache.Load(), Is.Empty);
    }

    [Test]
    public async Task KeepsOnlyBoundedNewestEntries()
    {
        var source = new Dictionary<Guid, ReplayAnalysisResult>();
        for (int index = 0; index < ReplayAnalysisCache.MaximumEntries + 5; index++)
            source[Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}")] = createResult("Great");

        var cache = new ReplayAnalysisCache(cachePath);
        await cache.SaveAsync(source);
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> loaded = cache.Load();

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Has.Count.EqualTo(ReplayAnalysisCache.MaximumEntries));
            Assert.That(loaded.ContainsKey(Guid.Parse("00000000-0000-0000-0000-000000000001")), Is.False);
            Assert.That(loaded.ContainsKey(Guid.Parse("00000000-0000-0000-0000-000000000105")), Is.True);
        });
    }

    private static ReplayAnalysisResult createResult(string judgement) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "gameplay-clock",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        new[]
        {
            new ReplayObjectJudgement(
                42,
                null,
                "HitCircle",
                12_715,
                12_715,
                judgement,
                "Great",
                12_730,
                15,
                1,
                new ReplayPoint(256, 192),
                new ReplayPoint(250, 190),
                20,
                judgement == "Miss" ? 0 : 21),
        },
        judgement == "Miss"
            ? new ReplayJudgementSummary(0, 0, 0, 1, 0, 0)
            : new ReplayJudgementSummary(1, 0, 0, 0, 0, 0));
}
