using AimMod.Desktop.PpTargets;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetSessionFormModelTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] patterns = ["Overall", "Jumps", "Streams", "Bursts", "Speed", "Direction changes"];
    private static readonly PpPatternFeatures shape = PpTargetPatternModel.ExtractFeatures(
        Enumerable.Range(0, 40).Select(i => new PpPatternPoint(i * 250, (i % 2) * 200, 100)), 32, 1);

    [TestCase(.90, -1)]
    [TestCase(.99, 1)]
    public void DetectsFormAgainstOwnMapBaselinesAndAdjustsPatternPrediction(double accuracy, int direction)
    {
        var evidence = history(_ => accuracy);
        var form = PpTargetSessionFormModel.Build(evidence, now);
        Assert.That(form.Patterns, Has.Count.EqualTo(6));
        Assert.That(Math.Sign(form.Patterns[0].AccuracyDelta), Is.EqualTo(direction));
        Assert.That(form.Patterns[0].AccuracyDelta, Is.InRange(-.015, .01));
        var profile = new PpPatternProfile("synthetic", now, 30, evidence);
        var normal = PpTargetPatternModel.Predict(shape, profile);
        var adjusted = PpTargetPatternModel.Predict(shape, profile with { SessionForm = form });
        Assert.That(Math.Sign(adjusted.ExpectedAccuracy!.Value - normal.ExpectedAccuracy!.Value), Is.EqualTo(direction));
        Assert.That(Math.Sign(adjusted.Fit!.Value - normal.Fit!.Value), Is.EqualTo(direction));
        Assert.That(form.Summary, Does.Contain(direction > 0 ? "above" : "below"));
    }

    [Test]
    public void DetectsSessionTrendWithoutClaimingPhysicalWarmup()
    {
        var form = PpTargetSessionFormModel.Build(history(i => i < 3 ? .92 : .98), now);
        Assert.That(form.Patterns[0].Observation, Does.Contain("improving through this session"));
        var dip = PpTargetSessionFormModel.Build(history(i => i < 3 ? .98 : .92), now);
        Assert.That(dip.Patterns[0].Observation, Does.Contain("recent plays have dipped"));
    }

    [Test]
    public void NewMapsChangedModsSparseHistoryAndInactivityDoNotProduceOffDayClaims()
    {
        var history = PpTargetSessionFormModelTests.history(_ => .8);
        bool recent(PpPatternEvidence e) => e.PlayedAt.Date == now.Date;
        foreach (var changed in new[] {
                     history.Select(e => recent(e) ? e with { SetupKey = "different-settings" } : e).ToArray(),
                     history.Where(e => !recent(e) || e.MapKey == "map-0").ToArray(),
                     history.Where(e => e.PlayedAt > now.AddDays(-2)).ToArray(),
                     history.Select(e => recent(e) ? e with { Features = shape with { PointCount = 5 } } : e).ToArray() })
            Assert.That(PpTargetSessionFormModel.Build(changed, now).Patterns, Is.Empty);
        Assert.That(PpTargetSessionFormModel.Build(history, now.AddHours(2)).Patterns, Is.Empty);
        Assert.That(PpTargetSessionFormModel.Build(history.Concat(history), now).Patterns, Is.EqualTo(PpTargetSessionFormModel.Build(history, now).Patterns));
        var future = history.Select(e => e with { ScoreId = Guid.NewGuid(), PlayedAt = now.AddHours(1) });
        Assert.That(PpTargetSessionFormModel.Build(history.Concat(future), now).Patterns, Is.EqualTo(PpTargetSessionFormModel.Build(history, now).Patterns));
    }

    [Test]
    public void NewMapsUseHistoricalPatternNeighboursWithSmallerAdjustments()
    {
        var original = history(_ => .90);
        var sameMap = PpTargetSessionFormModel.Build(original, now);
        var newMaps = original.Select(e => e.PlayedAt.Date == now.Date ? e with { MapKey = "new-" + e.MapKey } : e).ToArray();
        var form = PpTargetSessionFormModel.Build(newMaps, now);
        Assert.That(form.Patterns, Is.Not.Empty);
        Assert.That(form.Patterns.All(p => p.UsesSimilarMaps), Is.True);
        Assert.That(form.Patterns[0].AccuracyDelta, Is.LessThan(0));
        Assert.That(Math.Abs(form.Patterns[0].MissRateDelta), Is.LessThanOrEqualTo(Math.Abs(sameMap.Patterns[0].MissRateDelta)));
        Assert.That(form.Support!.Single().ComparablePlays, Is.EqualTo(6));
        foreach (var changed in new[] {
            newMaps.Select(e => e.PlayedAt.Date == now.Date ? e with { Features = shape with { MeanSpacing = 2000, PeakSpacing = 2500 } } : e).ToArray(),
            newMaps.Select(e => e.PlayedAt.Date == now.Date ? e with { LegacyScore = true } : e).ToArray(),
            newMaps.Select(e => e.PlayedAt.Date == now.Date ? e with { Features = shape with { ClockRate = 1.5 } } : e).ToArray(),
            newMaps.Select(e => e.PlayedAt.Date != now.Date ? e with { PlayedAt = now.AddHours(-4) } : e).ToArray()
        }) Assert.That(PpTargetSessionFormModel.Build(changed, now).Patterns, Is.Empty);
    }

    [Test]
    public void ExplainsMissingBaselineSeparatelyFromPlayCountAndExpiry()
    {
        var evidence = history(_ => .9).Where(e => e.PlayedAt.Date == now.Date).ToArray();
        var form = PpTargetSessionFormModel.Build(evidence, now);
        Assert.That(form.StatusFor("", false, now), Does.Contain("6 recent plays; 0/5 comparable"));
        Assert.That(form.StatusFor("", false, now.AddHours(2)), Does.Contain("no active session"));
        Assert.That(form.StatusFor("DT", false, now), Does.Contain("this mod setup"));
    }

    [Test]
    public void StreamDipDoesNotReduceJumpSpecificForm()
    {
        var evidence = history(_ => .97).Select(e => e.PlayedAt.Date == now.Date
            ? e with { Outcomes = e.Outcomes.ToDictionary(p => p.Key, p => p.Key == "Streams" ? p.Value with { Accuracy = .90 } : p.Value) } : e).ToArray();
        var form = PpTargetSessionFormModel.Build(evidence, now);
        Assert.That(form.Patterns.Single(p => p.Pattern == "Jumps").AccuracyDelta, Is.Zero.Within(1e-10));
        Assert.That(form.Patterns.Single(p => p.Pattern == "Streams").AccuracyDelta, Is.LessThan(0));
    }

    private static PpPatternEvidence[] history(Func<int, double> current)
    {
        var result = new List<PpPatternEvidence>();
        PpPatternEvidence play(int map, DateTimeOffset time, double accuracy) => new(Guid.NewGuid(), $"map-{map}", "", time, shape, 1,
            patterns.ToDictionary(p => p, _ => new PpPatternOutcome(40, accuracy, .01, new Dictionary<AimMod.Osu.Runtime.Contracts.ReplayMissReason, int>())),
            ScoreMods.Configuration([], "", PpTargetMods.NormaliseOne));
        for (int day = 1; day <= 2; day++) for (int map = 0; map < 3; map++) result.Add(play(map, now.AddDays(-day), .97));
        for (int i = 0; i < 6; i++) result.Add(play(i % 3, now.AddMinutes(-30 + i * 5), current(i)));
        return result.ToArray();
    }
}
