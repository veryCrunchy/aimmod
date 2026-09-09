using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetLearningModelTests
{
    private static readonly DateTimeOffset now = new(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PpPatternFeatures jumps = new() { PointCount = 200, JumpFraction = .8, StreamFraction = .05, BurstFraction = .1, SharpTurnFraction = .2, NotesPerSecond = 5, JumpDistance = 200 };
    private static readonly PpPatternFeatures streams = jumps with { JumpFraction = .05, StreamFraction = .8 };
    private static PpTargetEstimate estimate(PpPatternFeatures? features = null) => new(200, 400, new(150, 250), 20,
        PpTargetConfidence.Low, "synthetic", Features: features ?? jumps);

    private static (PpTargetOpportunityProfile History, PpPatternProfile Patterns) history(double[] pp, PpPatternFeatures? shape = null, string mods = "")
    {
        var attempts = new List<PpTargetPassSample>();
        var evidence = new List<PpPatternEvidence>();
        for (int map = 1; map <= 3; map++) for (int session = 0; session < 2; session++) for (int i = 0; i < pp.Length; i++)
        {
            Guid id = Guid.NewGuid();
            var time = now.AddDays(-5 + session).AddHours(map).AddMinutes(i * 3);
            attempts.Add(new(map, time, 5, 180, 120, mods, pp[i] > 0, LocalScoreId: id, Pp: pp[i], LocalAttempt: true));
            evidence.Add(new(id, $"map-{map}", mods, time, shape ?? jumps, 1, new Dictionary<string, PpPatternOutcome>()));
        }
        return (new(now, [], attempts), new("synthetic", now, 30, evidence));
    }

    private static PpTargetLearningForecast? predict(PpTargetOpportunityProfile h, PpPatternProfile p, PpTargetEstimate? e = null) =>
        PpTargetLearningModel.Predict(h, p, e ?? estimate(), 99, 5, 180, 120, []);

    [Test]
    public void LearnsOrderedRetriesAndIncludesFailuresInFirstTry()
    {
        var data = history([0, 80, 100, 140]);
        var result = predict(data.History, data.Patterns)!;
        Assert.That(result, Is.Not.Null);
        Assert.That(result.FirstTryPp, Is.Zero);
        Assert.That(result.TargetPp, Is.EqualTo(262));
        Assert.That(result.LikelyTries, Is.EqualTo(4));
        Assert.That(result.SupportedTries, Is.EqualTo(4));
        Assert.That(result.Maps, Is.EqualTo(3));
        Assert.That(result.Confidence, Is.EqualTo(PpTargetConfidence.Low));
        var reverse = history([140, 100, 80, 0]);
        var early = predict(reverse.History, reverse.Patterns)!;
        Assert.That(early.TargetPp, Is.EqualTo(result.TargetPp));
        Assert.That(early.LikelyTries, Is.EqualTo(1), "Same distribution but different chronology changes time to target.");
    }

    [Test]
    public void DoesNotInventLearningForFlatHistoryOrExceedTheFcCeiling()
    {
        var flat = history([100, 100, 100]);
        var result = predict(flat.History, flat.Patterns)!;
        Assert.That(result.FirstTryPp, Is.EqualTo(200).Within(.001));
        Assert.That(result.TargetPp, Is.EqualTo(200));
        Assert.That(result.LikelyTries, Is.EqualTo(1));
        var improving = history([0, 80, 100, 140]);
        Assert.That(predict(improving.History, improving.Patterns, estimate() with { RealisticMaximumPp = 220 })!.TargetPp, Is.EqualTo(220));
    }

    [Test]
    public void RejectsUnmatchedPatternsModsAndSparseOrSelectedHistory()
    {
        var data = history([0, 80, 100]);
        Assert.That(predict(data.History, data.Patterns, estimate(streams)), Is.Null);
        Assert.That(predict(data.History, data.Patterns, estimate(jumps with { JumpDistance = 600 })), Is.Null);
        var dt = history([0, 80, 100], mods: "DT");
        Assert.That(predict(dt.History, dt.Patterns), Is.Null);
        Assert.That(predict(data.History with { RecentAttempts = data.History.RecentAttempts.Where(a => a.BeatmapId == 1).ToArray() }, data.Patterns), Is.Null);
        Assert.That(predict(data.History with { RecentAttempts = data.History.RecentAttempts.Select(a => a with { LocalAttempt = false }).ToArray() }, data.Patterns), Is.Null);
        Assert.That(predict(data.History with { RecentAttempts = data.History.RecentAttempts.Select(a => a with { Pp = null }).ToArray() }, data.Patterns), Is.Null);
    }

    [Test]
    public void UsesNextAttemptForAnAlreadyPractisedTargetAndDoesNotExtrapolate()
    {
        var data = history([0, 80, 100, 140]);
        var current = new PpTargetPassSample(99, now.AddMinutes(-3), 5, 180, 120, "", false, LocalScoreId: Guid.NewGuid(), Pp: 0, LocalAttempt: true);
        var result = predict(data.History with { RecentAttempts = data.History.RecentAttempts.Append(current).ToArray() }, data.Patterns)!;
        Assert.That(result.PreviousTries, Is.EqualTo(1));
        Assert.That(result.FirstTryPp, Is.EqualTo(150).Within(.001));
        Assert.That(result.LikelyTries, Is.EqualTo(3));
        Assert.That(result.SupportedTries, Is.EqualTo(3));
        var many = Enumerable.Range(0, 20).Select(i => current with { LocalScoreId = Guid.NewGuid(), PlayedAt = now.AddMinutes(-i) });
        Assert.That(predict(data.History with { RecentAttempts = data.History.RecentAttempts.Concat(many).ToArray() }, data.Patterns), Is.Null);
    }

    [Test]
    public void EarlySuccessesAndAbandonmentsRemainInTheForecast()
    {
        var data = history([0, 80, 100, 140]);
        var original = predict(data.History, data.Patterns)!;
        var successes = new List<PpTargetPassSample>();
        for (int map = 1; map <= 3; map++)
            successes.Add(new(map, now.AddDays(-2).AddHours(map), 5, 180, 120, "", true,
                LocalScoreId: Guid.NewGuid(), Pp: 140, LocalAttempt: true));
        var improved = predict(data.History with { RecentAttempts = data.History.RecentAttempts.Concat(successes).ToArray() }, data.Patterns)!;
        Assert.That(improved.Sessions, Is.EqualTo(9));
        Assert.That(improved.FirstTryPp, Is.GreaterThan(original.FirstTryPp));
        var failures = successes.Select(a => a with { Passed = false, Pp = 0 });
        var abandoned = predict(data.History with { RecentAttempts = data.History.RecentAttempts.Concat(failures).ToArray() }, data.Patterns)!;
        Assert.That(abandoned.Sessions, Is.EqualTo(9));
        Assert.That(abandoned.ReachProbability, Is.LessThan(original.ReachProbability));
    }

    [Test]
    public void TargetMapAndStillOpenSessionsDoNotTrainTheirOwnForecast()
    {
        var data = history([0, 80, 100, 140]);
        Assert.That(PpTargetLearningModel.Predict(data.History, data.Patterns, estimate(), 1, 5, 180, 120, []), Is.Null,
            "Excluding the target leaves fewer than three independent maps.");
        var current = data.History.RecentAttempts.Select(a => a with { PlayedAt = now.AddMinutes(-10) }).ToArray();
        Assert.That(predict(data.History with { RecentAttempts = current }, data.Patterns), Is.Null);
    }

    [Test]
    public void FutureAttemptsCannotChangeForecastAndDuplicateScoresAreNotExtraPractice()
    {
        var data = history([0, 80, 100, 140]);
        var original = predict(data.History, data.Patterns);
        var augmented = data.History with { RecentAttempts = data.History.RecentAttempts.Concat(data.History.RecentAttempts)
            .Concat(data.History.RecentAttempts.Select(a => a with { LocalScoreId = Guid.NewGuid(), PlayedAt = now.AddDays(1), Pp = 10000 })).ToArray() };
        Assert.That(predict(augmented, data.Patterns), Is.EqualTo(original));
    }
}
