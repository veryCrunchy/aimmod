using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetPatternModelTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void ExtractsActualNormalizedGeometryAndRateWithoutStars()
    {
        PpPatternPoint[] points = [new(0, 0, 0), new(200, 100, 0), new(400, 100, 100)];
        var f = PpTargetPatternModel.ExtractFeatures(points, 50, 2);
        Assert.Multiple(() =>
        {
            Assert.That(f.MeanSpacing, Is.EqualTo(100));
            Assert.That(f.PeakSpacing, Is.EqualTo(100));
            Assert.That(f.NotesPerSecond, Is.EqualTo(10));
            Assert.That(f.NormalizedSpeed, Is.EqualTo(1000));
            Assert.That(f.MeanDirectionChangeDegrees, Is.EqualTo(90).Within(1e-8));
            Assert.That(f.SharpTurnFraction, Is.EqualTo(1));
            Assert.That(f.BurstFraction, Is.EqualTo(1));
            Assert.That(f.StreamFraction, Is.Zero);
            Assert.That(f.DurationSeconds, Is.EqualTo(.2));
            Assert.That(PpTargetPatternModel.ExtractFeatures(points, 25, 1).MeanSpacing, Is.EqualTo(200));
            Assert.That(JsonSerializer.Deserialize<PpPatternFeatures>(JsonSerializer.Serialize(f)), Is.EqualTo(f));
        });
    }

    [Test]
    public void DistinguishesBurstsStreamsJumpsAndBreaks()
    {
        var stream = PpTargetPatternModel.ExtractFeatures(streamPoints(), 32, 1);
        var jump = PpTargetPatternModel.ExtractFeatures(jumpPoints(), 32, 1);
        PpPatternPoint[] broken = Enumerable.Range(0, 6).Select(i => new PpPatternPoint(i * 100, i * 10, 0, i == 3)).ToArray();
        var bursts = PpTargetPatternModel.ExtractFeatures(broken, 32, 1);
        var noBridge = PpTargetPatternModel.ExtractFeatures([new(0, 0, 0), new(100, 10, 0), new(200, 500, 0, true), new(300, 510, 0)], 32, 1);
        Assert.Multiple(() =>
        {
            Assert.That(stream.StreamFraction, Is.EqualTo(1));
            Assert.That(stream.JumpFraction, Is.Zero);
            Assert.That(jump.JumpFraction, Is.EqualTo(1));
            Assert.That(jump.StreamFraction, Is.Zero);
            Assert.That(jump.MeanDirectionChangeDegrees, Is.EqualTo(180).Within(1e-8));
            Assert.That(bursts.BurstFraction, Is.EqualTo(1));
            Assert.That(bursts.StreamFraction, Is.Zero);
            Assert.That(noBridge.TransitionCount, Is.EqualTo(2));
            Assert.That(noBridge.JumpFraction, Is.Zero);
            Assert.That(noBridge.MeanDirectionChangeDegrees, Is.Null);
        });
    }

    [Test]
    public void MissingMeasurementsRemainNullRatherThanInventingFeatures()
    {
        var missing = PpTargetPatternModel.ExtractFeatures(streamPoints(), null, null);
        var empty = PpTargetPatternModel.ExtractFeatures([], 32, 1);
        var stationary = PpTargetPatternModel.ExtractFeatures([new(0, 10, 10), new(100, 10, 10), new(200, 10, 10)], 32, 1);
        Assert.Multiple(() =>
        {
            Assert.That(missing.MeanSpacing, Is.Null);
            Assert.That(missing.NotesPerSecond, Is.Null);
            Assert.That(missing.StreamFraction, Is.Null);
            Assert.That(missing.NormalizedSpeed, Is.Null);
            Assert.That(empty.JumpFraction, Is.Null);
            Assert.That(stationary.MeanDirectionChangeDegrees, Is.Null);
            Assert.That(stationary.MeanSpacing, Is.Zero);
            Assert.That(PpTargetPatternModel.ExtractFeatures([new(0, double.NaN, 0)], 32, 1).InvalidPointCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void SameStarStreamAndJumpPlayersReceiveDifferentMeasuredFits()
    {
        var streamPlayer = profile(Enumerable.Range(1, 4).Select(i => (replay(i), analysis(i <= 2 ? streamPoints() : jumpPoints(), i > 2 ? index => index % 4 == 0 : null))).ToArray());
        var jumpPlayer = profile(Enumerable.Range(1, 4).Select(i => (replay(i), analysis(i <= 2 ? streamPoints() : jumpPoints(), i <= 2 ? index => index % 4 == 0 : null))).ToArray());
        var stream = PpTargetPatternModel.ExtractFeatures(streamPoints(), 32, 1);
        var jump = PpTargetPatternModel.ExtractFeatures(jumpPoints(), 32, 1);
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetPatternModel.Predict(stream, streamPlayer).Fit, Is.GreaterThan(PpTargetPatternModel.Predict(stream, jumpPlayer).Fit!.Value));
            Assert.That(PpTargetPatternModel.Predict(jump, jumpPlayer).Fit, Is.GreaterThan(PpTargetPatternModel.Predict(jump, streamPlayer).Fit!.Value));
            Assert.That(PpTargetPatternModel.Predict(jump, streamPlayer).ExpectedAccuracy, Is.LessThan(.9));
            Assert.That(PpTargetPatternModel.Predict(stream, streamPlayer).Strengths, Is.Not.Empty);
        });
    }

    [Test]
    public void KeepsMissesAndReasonsDespiteHighLocalScoreAccuracy()
    {
        var p = profile([(replay(1) with { Accuracy = 1 }, analysis(jumpPoints(), i => i % 3 == 0)), (replay(2) with { Accuracy = 1 }, analysis(jumpPoints(), i => i % 3 == 0))]);
        var prediction = PpTargetPatternModel.Predict(PpTargetPatternModel.ExtractFeatures(jumpPoints(), 32, 1), p);
        Assert.Multiple(() =>
        {
            Assert.That(p.Evidence.All(e => e.Outcomes["Overall"].MissRate > .3), Is.True);
            Assert.That(prediction.ExpectedAccuracy, Is.LessThan(.8));
            Assert.That(prediction.ExpectedMissRate, Is.EqualTo(prediction.PatternFits.Max(f => f.ExpectedMissRate!.Value)));
            Assert.That(prediction.ExpectedMissRate, Is.GreaterThan(.3));
            Assert.That(prediction.Risks.Any(r => r.Contains("overshoots")), Is.True);
            Assert.That(prediction.CoverageNotes, Is.Not.Empty);
            Assert.That(prediction.Risks.Any(r => r.Contains("completion")), Is.False);
        });
    }

    [Test]
    public void WeakJumpSectionLimitsFitDespiteEasyStreamMajority()
    {
        var points = streamPoints(80).Concat(jumpPoints(16).Select(p => p with { TimeMs = p.TimeMs + 12000 })).ToArray();
        var p = profile([(replay(1), analysis(points, i => i >= 80)), (replay(2), analysis(points, i => i >= 80))]);
        var prediction = PpTargetPatternModel.Predict(PpTargetPatternModel.ExtractFeatures(points, 32, 1), p);
        Assert.Multiple(() =>
        {
            Assert.That(p.Evidence[0].Outcomes["Overall"].Accuracy, Is.GreaterThan(.8));
            Assert.That(prediction.PatternFits.Single(f => f.Pattern == "Jumps").Fit, Is.Zero);
            Assert.That(prediction.Fit, Is.Zero);
            Assert.That(prediction.ExpectedAccuracy, Is.LessThan(.1));
            Assert.That(prediction.ExpectedMissRate, Is.EqualTo(1));
        });
    }

    [Test]
    public void BalancesMapRetriesAndReportsSparseCoverage()
    {
        var attempts = Enumerable.Range(1, 60).Select(i => (replay(i) with { BeatmapId = id(1), BeatmapHash = "same-map" }, analysis(jumpPoints()))).ToArray();
        var p = profile(attempts);
        var candidate = PpTargetPatternModel.ExtractFeatures(jumpPoints(), 32, 1);
        var prediction = PpTargetPatternModel.Predict(candidate, p);
        Assert.Multiple(() =>
        {
            Assert.That(p.Evidence.Sum(e => e.Weight), Is.EqualTo(1).Within(1e-8));
            Assert.That(prediction.Fit, Is.Null);
            Assert.That(prediction.ExpectedAccuracy, Is.Null);
            Assert.That(prediction.ExpectedMissRate, Is.Null);
            Assert.That(prediction.EvidenceConfidence, Is.Zero);
            Assert.That(prediction.PatternFits.All(f => f.DistinctMaps == 1), Is.True);
        });
        var balanced = profile(attempts.Concat([(replay(100), analysis(jumpPoints(), _ => true))]).ToArray());
        Assert.That(PpTargetPatternModel.Predict(candidate, balanced).ExpectedAccuracy, Is.LessThan(.6), "Retries must not drown out one independently failed map.");
    }

    [Test]
    public void AppliesRecencyAndStableDailyIdentity()
    {
        var rows = new[] { (replay(1), analysis(streamPoints())), (replay(2), analysis(streamPoints())), (replay(3) with { PlayedAt = now.AddDays(-31) }, analysis(streamPoints(), _ => true)), (replay(4) with { PlayedAt = now.AddHours(1) }, analysis(streamPoints(), _ => true)) };
        var p = profile(rows);
        Assert.That(p.Evidence, Has.Count.EqualTo(2));
        var stableRows = rows.Take(2).ToArray();
        var morning = profile(stableRows, now);
        var afternoon = profile(stableRows.Reverse().ToArray(), now.AddMinutes(5));
        Assert.That(morning.Identity, Is.EqualTo(afternoon.Identity));
        Assert.That(profile(stableRows, now.AddDays(1)).Identity, Is.Not.EqualTo(morning.Identity));
        Assert.That(profile([(replay(1), analysis(streamPoints(), _ => true)), stableRows[1]]).Identity, Is.Not.EqualTo(morning.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => PpTargetPatternModel.BuildProfile([], new Dictionary<Guid, ReplayAnalysisResult>(), recencyDays: 0));
    }

    [Test]
    public void RequiresCompatibleModsAndActualClockRate()
    {
        var p = profile([(replay(1) with { Mods = ["NC"] }, analysis(streamPoints())), (replay(2) with { Mods = ["DoubleTime"] }, analysis(streamPoints()))]);
        var candidate = PpTargetPatternModel.ExtractFeatures(streamPoints(), 32, 1);
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetPatternModel.Predict(candidate, p, ["DT"]).Fit, Is.Not.Null);
            Assert.That(PpTargetPatternModel.Predict(candidate, p, ["HD"]).Fit, Is.Null);
            Assert.That(PpTargetPatternModel.Predict(candidate, p).Fit, Is.Null);
            Assert.That(PpTargetPatternModel.Predict(candidate with { ClockRate = 1.5 }, p, ["DT"]).Fit, Is.Null);
        });
    }

    [Test]
    public void UsesOnlyCircleAndSliderHeadOnceNeverSliderTailMiss()
    {
        var baseline = analysis(streamPoints());
        var first = baseline.Judgements[0];
        ReplayObjectJudgement[] records = [
            first with { ObjectType = "Slider", Result = "Miss" },
            first with { ObjectType = "SliderHeadCircle", NestedPath = "0" },
            first with { ObjectType = "SliderHeadCircle", NestedPath = "0" },
            first with { ObjectType = "SliderTailCircle", NestedPath = "1", Result = "Miss" },
            first with { ObjectType = "SliderTick", NestedPath = "2", Result = "Miss" },
            .. baseline.Judgements.Skip(1),
        ];
        var p = profile([(replay(1), baseline with { Judgements = records, Summary = new(0, 0, 0, 999, 999, 0) })]);
        Assert.Multiple(() =>
        {
            Assert.That(p.Evidence[0].Features.PointCount, Is.EqualTo(32));
            Assert.That(p.Evidence[0].Outcomes["Overall"].ObjectCount, Is.EqualTo(32));
            Assert.That(p.Evidence[0].Outcomes["Overall"].Accuracy, Is.EqualTo(1));
            Assert.That(p.Evidence[0].Outcomes["Overall"].MissRate, Is.Zero);
        });
    }

    [Test]
    public void LongGapsResetGeometryAndStreams()
    {
        var points = Enumerable.Range(0, 12).Select(i => new PpPatternPoint(
            i * 100 + (i >= 6 ? 3000 : 0), i * 4 + (i >= 6 ? 400 : 0), 0)).ToArray();
        var f = PpTargetPatternModel.ExtractFeatures(points, 32, 1);
        var directions = PpTargetPatternModel.ExtractFeatures([new(0, 0, 0), new(100, 10, 0), new(3100, 0, 100), new(3200, 0, 110)], 32, 1);
        Assert.Multiple(() =>
        {
            Assert.That(f.TransitionCount, Is.EqualTo(10));
            Assert.That(f.JumpFraction, Is.Zero);
            Assert.That(f.StreamFraction, Is.Zero);
            Assert.That(f.BurstFraction, Is.EqualTo(1));
            Assert.That(directions.MeanDirectionChangeDegrees, Is.Null);
        });
    }

    [Test]
    public void PeakDistanceRateAndSpeedAffectSimilarity()
    {
        var p = profile([(replay(1), analysis(jumpPoints())), (replay(2), analysis(jumpPoints()))]);
        var f = PpTargetPatternModel.ExtractFeatures(jumpPoints(), 32, 1);
        double confidence = PpTargetPatternModel.Predict(f, p).EvidenceConfidence;
        foreach (var changed in new[] { f with { PeakSpacing = f.PeakSpacing + 90 }, f with { JumpDistance = f.JumpDistance + 90 },
                     f with { PeakNotesPerSecond = f.PeakNotesPerSecond + 1.5 }, f with { NormalizedSpeed = f.NormalizedSpeed + 400 } })
            Assert.That(PpTargetPatternModel.Predict(changed, p).EvidenceConfidence, Is.LessThan(confidence));
    }

    [Test]
    public void DefaultSettingsAndClassicAllowKnownCsButCustomCsStaysUnknown()
    {
        var r = replay(1) with { Mods = ["Classic"], ModsJson = "[{\"acronym\":\"CL\",\"settings\":{}}]" };
        var difficulty = new LocalBeatmapDifficulty(r.BeatmapId, 1, "Test", "osu", 5, 180, 10000, 5, 9, 8, 5, 1);
        LocalBeatmapSet[] sets = [new(r.SetId, 1, "Map", "Artist", "Mapper", "", now, null, [difficulty], 1)];
        var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [r.ScoreId] = analysis(streamPoints()) };
        var p = PpTargetPatternModel.BuildProfile([r], analyses, now: now, localSets: sets);
        Assert.That(p.Evidence[0].Features.HitRadius, Is.GreaterThan(0));
        var custom = PpTargetPatternModel.BuildProfile([r with { ModsJson = "[{\"acronym\":\"CL\",\"settings\":{\"circle_size\":8}}]" }], analyses, now: now, localSets: sets);
        Assert.That(custom.Evidence[0].Features.HitRadius, Is.Null);
        var noContext = PpTargetPatternModel.BuildProfile([r], analyses, now: now);
        Assert.That(noContext.Evidence[0].Features.MeanSpacing, Is.Null);
    }

    private static PpPatternPoint[] streamPoints(int count = 32) => Enumerable.Range(0, count).Select(i => new PpPatternPoint(i * 125, i * 4, 100)).ToArray();
    private static PpPatternPoint[] jumpPoints(int count = 32) => Enumerable.Range(0, count).Select(i => new PpPatternPoint(i * 300, i % 2 == 0 ? 50 : 450, 100)).ToArray();
    private static Guid id(int number) => new(number, 0, 0, new byte[8]);
    private static LocalReplay replay(int number) => new(id(number), id(number + 1000), id(number + 2000), "Same stars", "Artist", "Difficulty", "osu", "Player", now, 5, .99, 1000000, 1000, 0, 200, [], true, $"map-{number}");
    private static ReplayAnalysisResult analysis(PpPatternPoint[] points, Func<int, bool>? miss = null) => new("test", "officialRulesetPlayback", true, 0, [], points.Select((p, i) =>
        new ReplayObjectJudgement(i, null, "HitCircle", p.TimeMs, p.TimeMs, miss?.Invoke(i) == true ? "Miss" : "Great", "Great", p.TimeMs, 0, 1, new((float)p.X, (float)p.Y), null, i, i + 1,
            miss?.Invoke(i) == true ? new ReplayMissAnalysis(ReplayMissReason.Overshoot, 32, 80, 0, new(0, 0), null, null, null, 80, false, false, false, 0) : null)).ToArray(), ReplayJudgementSummary.Empty);
    private static PpPatternProfile profile((LocalReplay Replay, ReplayAnalysisResult Analysis)[] rows, DateTimeOffset? reference = null) => PpTargetPatternModel.BuildProfile(rows.Select(r => r.Replay),
        rows.ToDictionary(r => r.Replay.ScoreId, r => r.Analysis), rows.ToDictionary(r => r.Replay.ScoreId, _ => new PpPatternContext(32, 1)), reference ?? now);
}
