using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

/// <summary>Played difficulties without drills are listed with a reason instead of silently missing.</summary>
public sealed class PlayedWithoutDrillsTests
{
    private static readonly DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static LocalReplay play(Guid beatmap, string difficulty, double hoursAgo, bool replay = true, bool local = true, bool passed = true, double stars = 5) =>
        new(Guid.NewGuid(), Guid.NewGuid(), beatmap, "Synthetic Song", "Synthetic Artist", difficulty, "osu", "SyntheticPlayer", now.AddHours(-hoursAgo),
            stars, .97, 900_000, 400, 0, null, [], replay, "hash-" + difficulty, IsLocallyStored: local, Passed: passed);

    private static ReplayAnalysisResult clean() => new(ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true, ReplayAnalysisProtocol.WallClockTimeoutMs, [],
        [new ReplayObjectJudgement(0, null, "HitCircle", 1_000, 1_000, "Great", "Great", 1_004, 4, 1, new ReplayPoint(256, 192), new ReplayPoint(256, 192), 0, 1)],
        new ReplayJudgementSummary(1, 0, 0, 0, 0, 0));

    [Test]
    public void EachPlayedDifficultyGetsItsReason()
    {
        var drilled = play(Guid.NewGuid(), "Drilled", 1);
        var noReplay = play(Guid.NewGuid(), "No replay", 2, replay: false);
        var notAnalysed = play(Guid.NewGuid(), "Not analysed", 3);
        var failedId = Guid.NewGuid();
        var failed = play(failedId, "Failed", 4);
        var cleanPlay = play(Guid.NewGuid(), "Clean", 5);
        var onlineOnly = play(Guid.NewGuid(), "Online only", 6, local: false);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult> { [cleanPlay.ScoreId] = clean() };
        var drills = new[] { new PracticeMapCandidate(drilled, [drilled.ScoreId], 1, 3, 4) };

        var result = PracticeMapCandidateBuilder.PlayedWithoutDrills([drilled, noReplay, notAnalysed, failed, cleanPlay, onlineOnly], analyses, drills,
            scoreId => scoreId == failed.ScoreId);

        Assert.That(result.Select(r => (r.Play.Difficulty, r.Reason)), Is.EqualTo(new[]
        {
            ("No replay", PlayedWithoutDrillsReason.NoReplayFile),
            ("Not analysed", PlayedWithoutDrillsReason.NotAnalysed),
            ("Failed", PlayedWithoutDrillsReason.AnalysisFailed),
            ("Clean", PlayedWithoutDrillsReason.NoProblemsFound),
        }), "newest first; the drilled and the online-only difficulty are left out");
        Assert.That(result.Select(r => r.ReasonLabel), Is.EqualTo(new[] { "no replay saved", "not analysed yet", "analysis failed", "no misses or timing problems found" }));
    }

    [Test]
    public void ADifficultyOpensFromItsNewestPassedPlayAndCountsEveryPlay()
    {
        var beatmap = Guid.NewGuid();
        var passed = play(beatmap, "Extra", 5);
        var newerFail = play(beatmap, "Extra", 1, passed: false);
        var result = PracticeMapCandidateBuilder.PlayedWithoutDrills([newerFail, passed, play(beatmap, "Extra", 9)], new Dictionary<Guid, ReplayAnalysisResult>(), []).Single();
        Assert.That((result.Play, result.Plays), Is.EqualTo((passed, 3)));
        Assert.That(result.Explanation, Does.Contain("Opening its coaching page analyses them now"));
    }

    [Test]
    public void SearchAndStarsApplyToPlayedDifficulties()
    {
        var replay = play(Guid.NewGuid(), "Kumocha's Extra", 1, stars: 5.05);
        Assert.Multiple(() =>
        {
            Assert.That(PracticeMapCandidateSearch.MatchesPlay(replay, new PracticeCandidateQuery("kumocha")), Is.True);
            Assert.That(PracticeMapCandidateSearch.MatchesPlay(replay, new PracticeCandidateQuery("synthetic song")), Is.True);
            Assert.That(PracticeMapCandidateSearch.MatchesPlay(replay, new PracticeCandidateQuery("other map")), Is.False);
            Assert.That(PracticeMapCandidateSearch.MatchesPlay(replay, new PracticeCandidateQuery(MinimumStars: 5.5)), Is.False);
            Assert.That(PracticeMapCandidateSearch.MatchesPlay(replay, new PracticeCandidateQuery(Evidence: PracticeEvidenceFilter.FivePlusMisses)), Is.True,
                "the evidence filter is applied by the page, which hides played difficulties entirely");
        });
    }
}
