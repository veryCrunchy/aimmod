using AimMod.Desktop.Coaching;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TappingCoachingTests
{
    [TestCase("Rushing through the phrase", 0, -12, -24, -36, -48, -60, -72, -84)]
    [TestCase("Even taps, early start", -65, -65, -65, -65, -65, -65, -65, -65)]
    [TestCase("Uneven tap spacing", 0, -55, 0, -55, 0, -55, 0, -55)]
    public void DistinguishesTimingPatterns(string expected, params double[] offsets)
    {
        Assert.That(TappingCoaching.Build(analysis(offsets))?.Pattern, Is.EqualTo(expected));
    }

    [Test]
    public void DoesNotCallChangingMapRhythmUnevenTapping()
    {
        var value = analysis([0, -55, 0, -55, 0, -55, 0, -55]);
        value = value with { Judgements = value.Judgements.Select((j, i) => j with { StartTimeMs = j.StartTimeMs + (i % 2) * 50 }).ToArray() };
        Assert.That(TappingCoaching.Build(value), Is.Null);
    }

    [Test]
    public void RejectsMissesNestedObjectsAndIncompleteRuns()
    {
        var value = analysis([0, -12, -24, -36, -48, -60, -72, -84]);
        Assert.That(TappingCoaching.Build(value with { EngineVersion = "unknown" }), Is.Null);
        Assert.That(TappingCoaching.Build(value with { Judgements = value.Judgements.Select(j => j with { NestedPath = "0" }).ToArray() }), Is.Null);
        Assert.That(TappingCoaching.Build(value with { Judgements = value.Judgements.Select(j => j with { Result = "Miss" }).ToArray() }), Is.Null);
        Assert.That(TappingCoaching.Build(analysis([0, 3, -3, 2, -2, 3, -3, 0])), Is.Null);
    }

    static ReplayAnalysisResult analysis(double[] offsets) => new(ReplayAnalysisProtocol.EngineVersion,
        "officialRulesetPlayback", true, ReplayAnalysisProtocol.WallClockTimeoutMs, [],
        offsets.Select((offset, i) => new ReplayObjectJudgement(i, null, "HitCircle", 1000 + i * 100,
            1000 + i * 100, "Great", "Great", 1000 + i * 100 + offset, offset, 1, null, null, i, i + 1)).ToArray(),
        new ReplayJudgementSummary(8, 0, 0, 0, 0, 0));
}
