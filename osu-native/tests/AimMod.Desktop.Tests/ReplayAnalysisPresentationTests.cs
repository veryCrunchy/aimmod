using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class ReplayAnalysisPresentationTests
{
    [Test]
    public void PresentsExactMissTimesAndObjectNumbers()
    {
        ReplayAnalysisPresentation presentation = ReplayAnalysisPresenter.Present(result(
            new ReplayJudgementSummary(20, 2, 1, 2, 0, 0),
            judgement(7, 12_345, "Miss"),
            judgement(19, 65_020, "Miss")));

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Summary, Does.Contain("2 miss"));
            Assert.That(presentation.NotableMoments, Does.Contain("0:12.345 (object 8)"));
            Assert.That(presentation.NotableMoments, Does.Contain("1:05.020 (object 20)"));
            Assert.That(presentation.NextPlay, Does.Contain("0:12.345"));
        });
    }

    [Test]
    public void CleanRunDoesNotInventProblemLocation()
    {
        ReplayAnalysisPresentation presentation = ReplayAnalysisPresenter.Present(result(
            new ReplayJudgementSummary(20, 0, 0, 0, 0, 0),
            judgement(0, 1_000, "Great")));

        Assert.That(presentation.NotableMoments, Is.EqualTo("No misses or slider breaks in this run"));
        Assert.That(presentation.NextPlay, Does.Contain("repeatability"));
    }

    [Test]
    public void NotableJumpTargetsPreferObjectMissesAndIgnoreSuccessfulSliderParts()
    {
        ReplayAnalysisResult analysis = result(
            new ReplayJudgementSummary(20, 0, 0, 1, 1, 0),
            judgement(0, 500, "SliderTailHit"),
            judgement(1, 800, "SliderTailMiss"),
            judgement(42, 12_715, "Miss"));

        IReadOnlyList<ReplayObjectJudgement> notable = ReplayAnalysisPresenter.SelectNotableJudgements(analysis);

        Assert.That(notable.Select(judgement => judgement.StartTimeMs), Is.EqualTo(new[] { 12_715d }));
    }

    private static ReplayAnalysisResult result(ReplayJudgementSummary summary, params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "official-clock",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        judgements,
        summary);

    private static ReplayObjectJudgement judgement(int objectIndex, double time, string result) => new(
        objectIndex,
        null,
        "HitCircle",
        time,
        time,
        result,
        "Great",
        time,
        0,
        1,
        null,
        null,
        objectIndex,
        objectIndex + 1);
}
