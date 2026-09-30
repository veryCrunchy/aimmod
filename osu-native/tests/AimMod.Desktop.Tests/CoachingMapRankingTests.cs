using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingMapRankingTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void GroupsPlaysByDifficultyAndRanksTheLargestRoomToImproveFirst()
    {
        var steady = Enumerable.Range(0, 8).Select(i => play("Steady", "hash-a", .95 + i * .0005, 1, now.AddHours(-i))).ToArray();
        var uneven = Enumerable.Range(0, 8).Select(i => play("Uneven", "hash-b", i == 0 ? .985 : .93, 6, now.AddHours(-i))).ToArray();
        var ranking = CoachingMapRanker.Rank([.. steady, .. uneven], new Dictionary<Guid, ReplayAnalysisResult>(), now);

        Assert.Multiple(() =>
        {
            Assert.That(ranking.Maps, Has.Count.EqualTo(2));
            Assert.That(ranking.Maps[0].Latest.Title, Is.EqualTo("Uneven"));
            Assert.That(ranking.Maps[0].Attempts, Is.EqualTo(8));
            Assert.That(ranking.Maps[0].BestAccuracy, Is.EqualTo(.985).Within(1e-9));
            Assert.That(ranking.Maps[0].TypicalAccuracy, Is.EqualTo(.93).Within(1e-9));
            Assert.That(ranking.Maps[0].TopIssue, Is.Null, "Without replay analysis there is no issue to name.");
        });
    }

    [Test]
    public void MainIssueIsWhereConfidentMissesClusterAcrossAnalysedPlays()
    {
        var runs = Enumerable.Range(0, 5).Select(i => play("Jumps", "hash-c", .94, 3, now.AddHours(-i))).ToArray();
        var analyses = runs.ToDictionary(r => r.ScoreId, _ => analysis(
            (40, 60_000, ReplayMissReason.Overshoot), (41, 60_300, ReplayMissReason.Overshoot), (90, 120_000, ReplayMissReason.EarlyClick)));
        var map = CoachingMapRanker.Rank(runs, analyses, now).Maps.Single();

        Assert.Multiple(() =>
        {
            Assert.That(map.AnalysedAttempts, Is.EqualTo(5));
            Assert.That(map.TopIssue!.Kind, Is.EqualTo(CoachingIssueKind.Overshoot));
            Assert.That(map.TopIssue.TimeMs, Is.EqualTo(60_000));
            Assert.That(map.TopIssue.FirstObjectIndex, Is.EqualTo(40), "Practice starts at the first object of the clustered section.");
            Assert.That(map.TopIssue.PerPlay, Is.EqualTo(2).Within(1e-9));
            Assert.That(map.Target.HasReplayFile, Is.True);
        });
    }

    [Test]
    public void SkillSummarySharesMissesByFamilyAndTimelineCoversTheMap()
    {
        var runs = Enumerable.Range(0, 4).Select(i => play("Mixed", "hash-d", .93, 3, now.AddHours(-i))).ToArray();
        var analyses = runs.ToDictionary(r => r.ScoreId, _ => analysis(
            (10, 10_000, ReplayMissReason.Overshoot), (20, 20_000, ReplayMissReason.EarlyClick), (30, 30_000, ReplayMissReason.EarlyClick)));
        var skills = CoachingMapRanker.Rank(runs, analyses, now).Skills;
        var timeline = CoachingMapTimeline.Build(runs[0], runs, analyses);

        Assert.Multiple(() =>
        {
            Assert.That(skills.AnalysedPlays, Is.EqualTo(4));
            Assert.That(skills.Lines.Select(l => l.Family), Is.EqualTo(new[] { CoachingIssueFamily.Timing, CoachingIssueFamily.Aim }));
            Assert.That(skills.Lines[0].Share, Is.EqualTo(2 / 3.0).Within(1e-9));
            Assert.That(timeline.AnalysedPlays, Is.EqualTo(4));
            Assert.That(timeline.Markers, Has.Count.EqualTo(12));
            Assert.That(timeline.LengthMs, Is.GreaterThanOrEqualTo(30_000));
        });
    }

    [Test]
    public void SectionsMatchingTheMainIssueComeFirst()
    {
        var run = play("Sections", "hash-e", .93, 3, now);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [run.ScoreId] = analysis((10, 10_000, ReplayMissReason.EarlyClick), (11, 10_300, ReplayMissReason.EarlyClick), (50, 50_000, ReplayMissReason.Overshoot)),
        };
        var observations = CoachingReplayObservations.Build(run, [run], analyses);
        var main = new CoachingIssue(CoachingIssueKind.Overshoot, 5, 3, 50_000, 51_000, 50, 1);
        var sections = CoachingSections.Build(run, observations, analyses, main);

        Assert.That(sections.Select(s => CoachingIssues.Family(s.Kind)), Is.EqualTo(new[] { CoachingIssueFamily.Aim, CoachingIssueFamily.Timing }));
        Assert.That(sections[1].PressOffsetsMs, Has.Count.EqualTo(2));
    }

    private static LocalReplay play(string title, string hash, double accuracy, int misses, DateTimeOffset at) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), title, "Synthetic Artist", "Insane", "osu", "Synthetic Player", at, 5.2, accuracy,
        900_000, 400, misses, 200, [], true, hash);

    private static ReplayAnalysisResult analysis(params (int Index, double Time, ReplayMissReason Reason)[] misses)
    {
        var judgements = new List<ReplayObjectJudgement>();
        for (int index = 0; index < 100; index++)
        {
            double time = 1_000 + index * 1_000.0;
            var miss = misses.FirstOrDefault(m => m.Index == index);
            if (miss.Time > 0)
            {
                bool aim = miss.Reason == ReplayMissReason.Overshoot;
                var cursor = new ReplayPoint(aim ? 300 : 258, 192);
                judgements.Add(new ReplayObjectJudgement(index, null, "HitCircle", miss.Time, miss.Time, "Miss", "Great", miss.Time + 150, 150, 1,
                    new ReplayPoint(256, 192), cursor, index, 0,
                    new ReplayMissAnalysis(miss.Reason, 32, aim ? 40 : 4, 0, cursor, aim ? 10 : -130, aim ? 44 : 2, cursor, 10, true, false, false, 0, Confidence: .9)));
            }
            else
                judgements.Add(new ReplayObjectJudgement(index, null, "HitCircle", time, time, "Great", "Great", time + 4, 4, 1,
                    new ReplayPoint(256, 192), new ReplayPoint(257, 192), index, index + 1));
        }
        return new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion, "officialRulesetPlayback", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
            [], judgements.OrderBy(j => j.StartTimeMs).ToArray(), new ReplayJudgementSummary(100 - misses.Length, 0, 0, misses.Length, 0, 0));
    }
}
