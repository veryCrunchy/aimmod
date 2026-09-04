using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed class PracticeMapCandidateBuilderTests
{
    [Test]
    public void RanksExactLocalMapEvidenceAndExcludesOnlineOnlyScores()
    {
        Guid map = Guid.NewGuid();
        LocalReplay first = replay(Guid.NewGuid(), map, "Map A", true);
        LocalReplay second = replay(Guid.NewGuid(), map, "Map A", true);
        LocalReplay online = replay(Guid.NewGuid(), Guid.NewGuid(), "Online", false);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(miss(2), miss(5)),
            [second.ScoreId] = analysis(miss(2)),
            [online.ScoreId] = analysis(miss(1), miss(2), miss(3), miss(4)),
        };

        IReadOnlyList<PracticeMapCandidate> candidates = PracticeMapCandidateBuilder.Build(new[] { first, second, online }, analyses);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(candidates[0].SourceReplay.BeatmapId, Is.EqualTo(map));
            Assert.That(candidates[0].AnalysedAttempts, Is.EqualTo(2));
            Assert.That(candidates[0].MissCount, Is.EqualTo(3));
            Assert.That(candidates[0].AnalysisScoreIds, Is.EquivalentTo(new[] { first.ScoreId, second.ScoreId }));
        });
    }

    [Test]
    public void DoesNotOfferAReplayWithoutExactMissEvidence()
    {
        LocalReplay run = replay(Guid.NewGuid(), Guid.NewGuid(), "Clean", true);
        Assert.That(PracticeMapCandidateBuilder.Build(new[] { run }, new Dictionary<Guid, ReplayAnalysisResult>
        {
            [run.ScoreId] = analysis(),
        }), Is.Empty);
    }

    [Test]
    public void ReturnsManyDistinctPracticeMapsAndHonoursAnExplicitLimit()
    {
        LocalReplay[] runs = Enumerable.Range(0, 30)
                                       .Select(index => replay(Guid.NewGuid(), Guid.NewGuid(), $"Map {index}", true))
                                       .ToArray();
        Dictionary<Guid, ReplayAnalysisResult> analyses = runs.ToDictionary(
            run => run.ScoreId,
            _ => analysis(miss(2)));

        Assert.Multiple(() =>
        {
            Assert.That(PracticeMapCandidateBuilder.Build(runs, analyses), Has.Count.EqualTo(30));
            Assert.That(PracticeMapCandidateBuilder.Build(runs, analyses, 12), Has.Count.EqualTo(12));
        });
    }

    private static LocalReplay replay(Guid scoreId, Guid beatmapId, string title, bool local) => new(
        scoreId, Guid.NewGuid(), beatmapId, title, "Artist", "Difficulty", "osu", "Player",
        DateTimeOffset.UtcNow, 5, 0.95, 1_000_000, 500, 1, 100, Array.Empty<string>(), true,
        new string('a', 64), IsLocallyStored: local);

    private static ReplayObjectJudgement miss(int index) => new(index, null, "HitCircle", index * 100, index * 100,
        "Miss", "Great", index * 100, 0, 1, new ReplayPoint(256, 192), new ReplayPoint(300, 192), 0, 0,
        new ReplayMissAnalysis(ReplayMissReason.Undershoot, 32, 44, 0, new ReplayPoint(300, 192), null, null, null, 44,
            false, false, false, -1, Confidence: 0.8));

    private static ReplayAnalysisResult analysis(params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion, "official", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(), judgements, new ReplayJudgementSummary(0, 0, 0, judgements.Length, 0, 0));
}
