using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingReplayObservationsTests
{
    private static readonly DateTimeOffset epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static LocalReplay run(int minute = 0) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "Practice song", "Artist", "Hard", "osu", "Practice Player", epoch.AddMinutes(minute), 4, .94,
        100000, 100, 1, null, [], true, BeatmapHash: "matching-map-hash");

    private static ReplayAnalysisResult analysis(params ReplayObjectJudgement[] notes) => new(
        ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
        [], notes, new(0, 0, 0, notes.Count(j => j.Result == "Miss"), 0, 0));

    private static ReplayObjectJudgement miss(int index = 17, double time = 18750, ReplayMissReason reason = ReplayMissReason.Overshoot) => new(
        index, null, "HitCircle", time, time, "Miss", "Great", time + 120, 120, 1,
        new(256, 192), new(300, 192), 20, 0,
        new(reason, 32, 40, 10, new(290, 192), 120, reason == ReplayMissReason.Overshoot ? 45 : 20,
            new(296, 192), 45, true, false, true, .5, Confidence: .9));

    [Test]
    public void CountsOnlyMatchingPlayerSetupMapAndAnalysisEngineAndDeduplicatesScores()
    {
        var selected = run();
        var matching = run(-1) with { Player = "practice player" };
        var otherPlayer = run(-2) with { Player = "Another Player" };
        var otherMap = run(-3) with { BeatmapHash = "other-map-hash" };
        var otherMods = run(-4) with { Mods = ["HD"] };
        var customRate = run(-5) with { ModsJson = "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.2}}]" };
        var stable = run(-6) with { Origin = LocalLibraryOrigin.Stable };
        var assisted = run(-7) with { Mods = ["RX"] };
        var oldEngine = run(-8);
        var missingAnalysis = run(-9);
        var future = run() with { PlayedAt = DateTimeOffset.UtcNow.AddDays(1) };
        var history = new[] { selected, selected, matching, matching, otherPlayer, otherMap, otherMods, customRate,
            stable, assisted, oldEngine, missingAnalysis, future };
        var analyses = history.Where(r => r != missingAnalysis).DistinctBy(r => r.ScoreId)
            .ToDictionary(r => r.ScoreId, r => analysis(miss()));
        analyses[oldEngine.ScoreId] = analyses[oldEngine.ScoreId] with { EngineVersion = "old-engine" };
        var observation = CoachingReplayObservations.Build(selected, history, analyses).Single();
        Assert.Multiple(() =>
        {
            Assert.That(observation.Plays, Is.EqualTo(2));
            Assert.That(observation.ScoreIds, Is.EquivalentTo(new[] { selected.ScoreId, matching.ScoreId }));
            Assert.That(observation.Notes, Is.EqualTo(1));
        });
    }

    [TestCase("RX")]
    [TestCase("AP")]
    [TestCase("AT")]
    [TestCase("CN")]
    public void DoesNotBuildCoachingObservationsFromAnAssistedSelectedReplay(string mod)
    {
        var selected = run() with { Mods = [mod] };
        Assert.That(CoachingReplayObservations.Build(selected, [selected],
            new Dictionary<Guid, ReplayAnalysisResult> { [selected.ScoreId] = analysis(miss()) }), Is.Empty);
    }

    [Test]
    public void RequiresCurrentAnalysisForSelectedReplay()
    {
        var selected = run();
        Assert.That(CoachingReplayObservations.Build(selected, [], new Dictionary<Guid, ReplayAnalysisResult>()), Is.Empty);
        Assert.That(CoachingReplayObservations.Build(selected, [], new Dictionary<Guid, ReplayAnalysisResult>
            { [selected.ScoreId] = analysis(miss()) with { EngineVersion = "unknown" } }), Is.Empty);
    }

    [Test]
    public void SelectedMissesRequireRootObjectConfidenceAndUsableTimestamp()
    {
        var selected = run();
        var boundary = miss() with { MissAnalysis = miss().MissAnalysis! with { Confidence = .7 } };
        var lowConfidence = miss(18) with { MissAnalysis = miss().MissAnalysis! with { Confidence = .699 } };
        var nested = miss(19) with { NestedPath = "0" };
        var noIndex = miss(20) with { ObjectIndex = null };
        var negativeIndex = miss(-1);
        var negativeTime = miss(21, -1);
        var nonfiniteTime = miss(22, double.NaN);
        var noRadius = miss(23) with { MissAnalysis = miss().MissAnalysis! with { HitRadius = 0 } };
        var observation = CoachingReplayObservations.Build(selected, [], new Dictionary<Guid, ReplayAnalysisResult>
            { [selected.ScoreId] = analysis(boundary, lowConfidence, nested, noIndex, negativeIndex, negativeTime, nonfiniteTime, noRadius) }).Single();
        Assert.Multiple(() =>
        {
            Assert.That(observation.Notes, Is.EqualTo(1));
            Assert.That(observation.FirstObjectIndex, Is.EqualTo(17));
            Assert.That(observation.TimeMs, Is.EqualTo(18750));
            Assert.That(observation.ScoreIds, Is.EqualTo(new[] { selected.ScoreId }));
        });
    }

    [Test]
    public void ComparableMissEvidenceMustMeetTheSameRootConfidenceAndRadiusBoundary()
    {
        var selected = run();
        var valid = run(-1);
        var nested = run(-2);
        var weak = run(-3);
        var differentObject = run(-4);
        var invalidRadius = run(-5);
        var invalidTime = run(-6);
        var timing = miss(reason: ReplayMissReason.LateClick);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [selected.ScoreId] = analysis(timing),
            [valid.ScoreId] = analysis(timing with { MissAnalysis = timing.MissAnalysis! with { Confidence = .7 } }),
            [nested.ScoreId] = analysis(timing with { NestedPath = "0" }),
            [weak.ScoreId] = analysis(timing with { MissAnalysis = timing.MissAnalysis! with { Confidence = .699 } }),
            [differentObject.ScoreId] = analysis(timing with { ObjectIndex = 99 }),
            [invalidRadius.ScoreId] = analysis(timing with { MissAnalysis = timing.MissAnalysis! with { HitRadius = 0 } }),
            [invalidTime.ScoreId] = analysis(timing with { StartTimeMs = double.NaN })
        };
        var observation = CoachingReplayObservations.Build(selected,
            [valid, nested, weak, differentObject, invalidRadius, invalidTime], analyses).Single();
        Assert.Multiple(() =>
        {
            Assert.That(observation.Plays, Is.EqualTo(2));
            Assert.That(observation.ScoreIds, Is.EquivalentTo(new[] { selected.ScoreId, valid.ScoreId }));
        });
    }

    [Test]
    public void ReplayLinkUsesTheSelectedRealObjectAndTimestampWhileRetainingSupportingScoreIds()
    {
        var selected = run();
        var repeated = run(-1);
        var unrelated = run(-2);
        var observation = CoachingReplayObservations.Build(selected, [repeated, unrelated],
            new Dictionary<Guid, ReplayAnalysisResult>
            {
                [selected.ScoreId] = analysis(miss(17, 18750), miss(24, 26300)),
                [repeated.ScoreId] = analysis(miss(24, 26300)),
                [unrelated.ScoreId] = analysis(miss(88, 81000))
            }).Single();
        Assert.Multiple(() =>
        {
            Assert.That(observation.FirstObjectIndex, Is.EqualTo(17));
            Assert.That(observation.TimeMs, Is.EqualTo(18750));
            Assert.That(observation.Notes, Is.EqualTo(2));
            Assert.That(observation.Plays, Is.EqualTo(2));
            Assert.That(observation.ScoreIds, Is.EquivalentTo(new[] { selected.ScoreId, repeated.ScoreId }));
        });
    }

    [Test]
    public void TappingObservationMatchesTheSamePhraseAndLinksItsActualStart()
    {
        var selected = run();
        var samePhrase = run(-1);
        var differentPhrase = run(-2);
        var steady = run(-3);
        ReplayAnalysisResult phrase(int first, bool rush) => analysis(Enumerable.Range(0, 8).Select(i =>
            new ReplayObjectJudgement(first + i, null, "HitCircle", 12000 + i * 100, 12000 + i * 100,
                "Great", "Great", 12000 + i * 100 + (rush ? -i * 12 : 0), rush ? -i * 12 : 0,
                1, null, null, i, i + 1)).ToArray());
        var observation = CoachingReplayObservations.Build(selected, [samePhrase, differentPhrase, steady],
            new Dictionary<Guid, ReplayAnalysisResult>
            {
                [selected.ScoreId] = phrase(40, true), [samePhrase.ScoreId] = phrase(40, true),
                [differentPhrase.ScoreId] = phrase(80, true), [steady.ScoreId] = phrase(40, false)
            }).Single();
        Assert.Multiple(() =>
        {
            Assert.That(observation.Label, Is.EqualTo("Rushing through the phrase"));
            Assert.That(observation.FirstObjectIndex, Is.EqualTo(40));
            Assert.That(observation.TimeMs, Is.EqualTo(12000));
            Assert.That(observation.Notes, Is.EqualTo(8));
            Assert.That(observation.ScoreIds, Is.EquivalentTo(new[] { selected.ScoreId, samePhrase.ScoreId }));
        });
    }
}
