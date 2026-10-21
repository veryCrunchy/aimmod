using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class ReplayFolderAnalysisTests
{
    private static readonly DateTimeOffset now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void AnEstimateCarriesItsIntervalAndPlayCount()
    {
        var e = Estimate.Of([10, 12, 14])!;
        Assert.Multiple(() =>
        {
            Assert.That(e.Mean, Is.EqualTo(12).Within(1e-9));
            Assert.That(e.High - e.Mean, Is.EqualTo(4.303 * 2 / Math.Sqrt(3)).Within(1e-3), "t interval for three plays");
            Assert.That(e.Plays, Is.EqualTo(3));
            Assert.That(Estimate.Of([5]), Is.Null, "one play has no interval");
        });
    }

    [Test]
    public void KeysShowTheirOwnBiasAndFailedPlaysCount()
    {
        var runs = Enumerable.Range(0, 4).Select(i => run(i, passed: i != 3)).ToArray();
        var analyses = runs.Select((r, i) => (r.ScoreId, analysis(withKeys: i > 0, missAt: i == 1 ? 7 : null)))
            .ToDictionary(p => p.ScoreId, p => p.Item2);

        var summary = ReplayFolderAnalysis.Build(runs, analyses, now);

        var left = summary.Keys.Single(k => k.Key == ReplayPressedKey.Left);
        var right = summary.Keys.Single(k => k.Key == ReplayPressedKey.Right);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Plays, Is.EqualTo(4));
            Assert.That(summary.FailedPlays, Is.EqualTo(1));
            Assert.That(summary.PlaysWithKeys, Is.EqualTo(3), "the oldest analysis has no key data");
            Assert.That(left.MeanOffsetMs!.Mean, Is.LessThan(0), "left presses are early");
            Assert.That(right.MeanOffsetMs!.Mean, Is.GreaterThan(0), "right presses are late");
            Assert.That(left.MeanOffsetMs.Plays, Is.EqualTo(3));
            Assert.That(left.Share + right.Share, Is.EqualTo(1).Within(1e-9));
            Assert.That(summary.Timing.UnstableRate!.Plays, Is.EqualTo(4));
            Assert.That(summary.RecentMisses.Single().Run.ScoreId, Is.EqualTo(runs[1].ScoreId));
        });
    }

    [Test]
    public void AutoplayAndOtherModesAreLeftOut()
    {
        var auto = run(0, passed: true) with { Mods = ["AT"] };
        var taiko = run(1, passed: true) with { RulesetShortName = "taiko" };
        var summary = ReplayFolderAnalysis.Build([auto, taiko],
            new Dictionary<Guid, ReplayAnalysisResult> { [auto.ScoreId] = analysis(true, null), [taiko.ScoreId] = analysis(true, null) }, now);
        Assert.That(summary.Plays, Is.Zero);
    }

    [Test]
    public void ADateSplitsTimingKeysAndPatternsIntoBeforeAndSince()
    {
        var runs = Enumerable.Range(0, 6).Select(i => run(i, passed: true)).ToArray();
        var analyses = runs.ToDictionary(r => r.ScoreId, _ => analysis(withKeys: true, missAt: null));
        var since = now.AddDays(-7);

        var summary = ReplayFolderAnalysis.Build(runs, analyses, now, since: since);

        var comparison = summary.Comparison!;
        Assert.Multiple(() =>
        {
            Assert.That(comparison.Since, Is.EqualTo(since));
            Assert.That((comparison.PlaysBefore, comparison.PlaysSince), Is.EqualTo((3, 3)), "plays at the cut belong to the later side");
            Assert.That(comparison.Before.UnstableRate!.Plays, Is.EqualTo(3));
            Assert.That(comparison.After.UnstableRate!.Plays, Is.EqualTo(3));
            Assert.That(comparison.KeysBefore.Select(k => k.Key), Is.EquivalentTo(new[] { ReplayPressedKey.Left, ReplayPressedKey.Right }));
            Assert.That(summary.Patterns.Single(p => p.Pattern == "Overall").Plays, Is.EqualTo(6));
            Assert.That(summary.Patterns.Single(p => p.Pattern == "Overall").EarlierAccuracy!.Plays, Is.EqualTo(3));
            Assert.That(summary.Timing.UnstableRate!.Plays, Is.EqualTo(6), "the overall summary still covers every play");
            Assert.That(ReplayFolderAnalysis.Build(runs, analyses, now).Comparison, Is.Null);
        });
    }

    [Test]
    public void InstalledCircleSizesLetStreamsBeRecognised()
    {
        var runs = Enumerable.Range(0, 4).Select(i => run(i, passed: true)).ToArray();
        var analyses = runs.ToDictionary(r => r.ScoreId, _ => streams());
        var difficulty = new LocalBeatmapDifficulty(Guid.NewGuid(), 1, "Test", "osu", 5, 180, 60_000, 4, 9, 8, 5, null, BeatmapHash: "synthetic");
        var set = new LocalBeatmapSet(Guid.NewGuid(), 1, "Synthetic", "AimMod", "Mapper", "", now, now, [difficulty], 4);

        Assert.That(ReplayFolderAnalysis.Build(runs, analyses, now).Patterns.Select(p => p.Pattern), Does.Not.Contain("Streams"),
            "without a circle size, stream spacing cannot be judged");
        Assert.That(ReplayFolderAnalysis.Build(runs, analyses, now, [set]).Patterns.Select(p => p.Pattern), Does.Contain("Streams"));
        var measuredMiss = runs.ToDictionary(r => r.ScoreId, _ => streams(missRadius: 32));
        Assert.That(ReplayFolderAnalysis.Build(runs, measuredMiss, now).Patterns.Select(p => p.Pattern), Does.Contain("Streams"),
            "a miss's measured hit radius is enough");
    }

    /// <summary>Thirty-two circles 90 ms apart and 12 px apart: one long stream.</summary>
    private static ReplayAnalysisResult streams(double? missRadius = null)
    {
        var judgements = new List<ReplayObjectJudgement>();
        for (int i = 0; i < 32; i++)
        {
            double time = 1_000 + i * 90;
            bool miss = missRadius is not null && i == 20;
            judgements.Add(new(i, null, "HitCircle", time, time, miss ? "Miss" : "Great", "Great", time + 3, 3, 1,
                new ReplayPoint(200 + i * 12, 200), null, i, i + 1,
                miss ? new ReplayMissAnalysis(ReplayMissReason.LateClick, missRadius!.Value, 4, 0, new ReplayPoint(0, 0), 40, 4, null, 4, false, false, false, 0, Confidence: .9) : null));
        }
        return new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion, "map", true, 0, [], judgements, ReplayJudgementSummary.Empty);
    }

    private static LocalReplay run(int day, bool passed) => new(Guid.NewGuid(), Guid.Empty, Guid.Empty, "Synthetic", "AimMod", "Test", "osu", "player",
        now.AddDays(day - 10), 5, .95, 1_000_000, 40, 0, null, [], true, BeatmapHash: "synthetic", Passed: passed);

    /// <summary>Forty circles alternating keys: left presses land early, right presses late, with a little spread.</summary>
    private static ReplayAnalysisResult analysis(bool withKeys, int? missAt)
    {
        var judgements = new List<ReplayObjectJudgement>();
        for (int i = 0; i < 40; i++)
        {
            bool left = i % 2 == 0;
            double offset = (left ? -8 : 6) + (i % 5) - 2;
            double time = 1_000 + i * 200;
            string result = i == missAt ? "Miss" : "Great";
            judgements.Add(new(i, null, "HitCircle", time, time, result, "Great", time + offset, offset, 1,
                new ReplayPoint(100 + i % 4 * 80, 100 + i % 3 * 60), null, i, i + 1,
                PressedKey: withKeys && result != "Miss" ? left ? ReplayPressedKey.Left : ReplayPressedKey.Right : null));
        }
        return new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion, "map", true, 0, [], judgements, ReplayJudgementSummary.Empty);
    }
}
