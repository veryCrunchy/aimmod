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

    private static (PpTargetOpportunityProfile History, PpPatternProfile Patterns) history(double?[] pp, PpPatternFeatures? shape = null, string mods = "",
        Func<int, int, int, DateTimeOffset>? at = null)
    {
        var attempts = new List<PpTargetPassSample>();
        var evidence = new List<PpPatternEvidence>();
        for (int map = 1; map <= 3; map++) for (int session = 0; session < 2; session++) for (int i = 0; i < pp.Length; i++)
        {
            Guid id = Guid.NewGuid();
            var time = at?.Invoke(map, session, i) ?? now.AddDays(-5 + session).AddHours(map).AddMinutes(i * 3);
            attempts.Add(new(map, time, 5, 180, 120, mods, pp[i] != 0, LocalScoreId: id, Pp: pp[i], LocalAttempt: true));
            evidence.Add(new(id, $"map-{map}", mods, time, shape ?? jumps, 1, new Dictionary<string, PpPatternOutcome>()));
        }
        return (new(now, [], attempts), new("synthetic", now, 30, evidence));
    }

    private static PpTargetLearningForecast? predict(PpTargetOpportunityProfile h, PpPatternProfile p, PpTargetEstimate? e = null) =>
        PpTargetLearningModel.Predict(h, p, e ?? estimate(), 99, 5, 180, 120, []);

    [Test]
    public void RatiosSharePassesOnlyDenominatorAndFailuresAreNotZeroPp()
    {
        // Mean passed PP is 100; a failed first attempt must not turn the first-try ratio into zero.
        var data = history([0, 80, 100, 120]);
        var result = predict(data.History, data.Patterns)!;
        Assert.That(result.Sessions, Is.EqualTo(6));
        Assert.That(result.Maps, Is.EqualTo(3));
        Assert.That(result.Ratio(0), Is.EqualTo(1), "No passed first attempts leaves the neutral ratio.");
        Assert.That(result.Ratio(1), Is.LessThan(1).And.GreaterThanOrEqualTo(.8));
        Assert.That(result.Ratio(3), Is.GreaterThan(result.Ratio(2)));
        Assert.That(result.Confidence, Is.EqualTo(PpTargetConfidence.Low));
    }

    [Test]
    public void FlatHistoryIsNeutralAndImprovementIsShrunkAndBounded()
    {
        var flat = history([100, 100, 100]);
        var result = predict(flat.History, flat.Patterns)!;
        Assert.That(result.PassRatios, Is.All.EqualTo(1).Within(1e-9));
        var improving = history([50, 150, 400]);
        var bounded = predict(improving.History, improving.Patterns)!;
        Assert.That(bounded.Ratio(0), Is.EqualTo(.8));
        Assert.That(bounded.Ratio(2), Is.LessThanOrEqualTo(1.2));
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
    public void OnePassWithoutPpSkipsOnlyThatAttempt()
    {
        var data = history([90, 100, 110]);
        var attempts = data.History.RecentAttempts.ToArray();
        attempts[1] = attempts[1] with { Pp = null };
        var result = predict(data.History with { RecentAttempts = attempts }, data.Patterns);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Sessions, Is.EqualTo(6));
    }

    [Test]
    public void UsesNextAttemptForAnAlreadyPractisedTarget()
    {
        var data = history([80, 100, 120, 140]);
        var current = new PpTargetPassSample(99, now.AddMinutes(-3), 5, 180, 120, "", false, LocalScoreId: Guid.NewGuid(), Pp: 0, LocalAttempt: true);
        var result = predict(data.History with { RecentAttempts = data.History.RecentAttempts.Append(current).ToArray() }, data.Patterns)!;
        Assert.That(result.PreviousTries, Is.EqualTo(1));
        Assert.That(result.SupportedTries, Is.EqualTo(3));
        var fresh = predict(data.History, data.Patterns)!;
        Assert.That(result.Ratio(0), Is.GreaterThan(fresh.Ratio(0)), "Later attempts in comparable sessions scored higher.");
        var many = Enumerable.Range(0, 25).Select(i => current with { LocalScoreId = Guid.NewGuid(), PlayedAt = now.AddMinutes(-i) });
        Assert.That(predict(data.History with { RecentAttempts = data.History.RecentAttempts.Concat(many).ToArray() }, data.Patterns)!.PreviousTries, Is.EqualTo(19));
    }

    [Test]
    public void CurrentSessionAttemptsTrainOtherTargetsAtLowerWeight()
    {
        var closed = history([80, 100, 120]);
        var open = history([80, 100, 120], at: (map, session, i) => now.AddMinutes(-60 + map * 12 + session * 4 + i));
        Assert.That(predict(open.History, open.Patterns), Is.Not.Null, "Sessions ended less than six hours ago are still evidence.");
        Assert.That(PpTargetLearningModel.Predict(closed.History, closed.Patterns, estimate(), 1, 5, 180, 120, []), Is.Null,
            "Excluding the target leaves fewer than three independent maps.");
    }

    [Test]
    public void FutureAttemptsCannotChangeForecastAndDuplicateScoresAreNotExtraPractice()
    {
        var data = history([0, 80, 100, 140]);
        var original = predict(data.History, data.Patterns)!;
        var augmented = data.History with { RecentAttempts = data.History.RecentAttempts.Concat(data.History.RecentAttempts)
            .Concat(data.History.RecentAttempts.Select(a => a with { LocalScoreId = Guid.NewGuid(), PlayedAt = now.AddDays(1), Pp = 10000 })).ToArray() };
        var repeated = predict(augmented, data.Patterns)!;
        Assert.That(repeated.PassRatios, Is.EqualTo(original.PassRatios));
        Assert.That(repeated.Sessions, Is.EqualTo(original.Sessions));
    }
}
