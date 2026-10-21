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
