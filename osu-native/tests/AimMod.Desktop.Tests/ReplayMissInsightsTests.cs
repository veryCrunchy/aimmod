using AimMod.Desktop;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class ReplayMissInsightsTests
{
    [Test]
    public void FindsRepeatedMissesOnTheSameDifficultyOnly()
    {
        Guid beatmap = Guid.NewGuid();
        LocalReplay first = replay(beatmap, 1);
        LocalReplay second = replay(beatmap, 2);
        LocalReplay third = replay(beatmap, 3);
        LocalReplay otherDifficulty = replay(Guid.NewGuid(), 3);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(miss(17, ReplayMissReason.EarlyClick), miss(40, ReplayMissReason.Overshoot)),
            [second.ScoreId] = analysis(miss(17, ReplayMissReason.EarlyClick)),
            [third.ScoreId] = analysis(),
            [otherDifficulty.ScoreId] = analysis(miss(17, ReplayMissReason.LateClick)),
        };

        ReplayMapPatternReport report = ReplayMapPatternAnalyzer.Build(first, new[] { first, second, third, otherDifficulty }, analyses);

        Assert.Multiple(() =>
        {
            Assert.That(report.TotalAttempts, Is.EqualTo(3));
            Assert.That(report.AnalysedAttempts, Is.EqualTo(3));
            Assert.That(report.RecurringMisses, Has.Count.EqualTo(1));
            Assert.That(report.RecurringMisses[0].ObjectIndex, Is.EqualTo(17));
            Assert.That(report.RecurringMisses[0].MissedAttempts, Is.EqualTo(2));
            Assert.That(report.RecurringMisses[0].DominantReason, Is.EqualTo(ReplayMissReason.EarlyClick));
        });
    }

    [TestCase(ReplayMissReason.EarlyClick, "Early click")]
    [TestCase(ReplayMissReason.LateClick, "Late click")]
    [TestCase(ReplayMissReason.Undershoot, "Undershoot")]
    [TestCase(ReplayMissReason.Overshoot, "Overshoot")]
    [TestCase(ReplayMissReason.OnTargetNoClick, "no new click")]
    public void PresentsEvidenceInsteadOfGenericMissText(ReplayMissReason reason, string expected)
    {
        Assert.That(ReplayMissInsightPresenter.Describe(miss(3, reason)), Does.Contain(expected).IgnoreCase);
    }

    private static ReplayObjectJudgement miss(int index, ReplayMissReason reason) => new(
        index, null, "HitCircle", index * 1000, index * 1000, "Miss", "Great", index * 1000 + 150, 150, 1,
        new ReplayPoint(100, 100), null, 0, 0,
        new ReplayMissAnalysis(reason, 32, 20, 10, new ReplayPoint(90, 100), 25, 40, new ReplayPoint(140, 100), 35, true, false, true, 0.5));

    private static ReplayAnalysisResult analysis(params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion, "official", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(), judgements,
        new ReplayJudgementSummary(0, 0, 0, judgements.Length, 0, 0));

    private static LocalReplay replay(Guid beatmapId, int index) => new(
        Guid.NewGuid(), Guid.NewGuid(), beatmapId, "Map", "Artist", "Insane", "osu", "player",
        DateTimeOffset.Now.AddMinutes(-index), 5, 0.95, 1_000_000, 500, 1, 100, Array.Empty<string>(), true,
        $"hash-{beatmapId:N}");
}
