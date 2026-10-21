using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class TrainerStreamLadderTests
{
    private static TrainerResult run(TrainerSettings s, double? spread = 14, int misses = 0, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), at ?? DateTimeOffset.UtcNow.AddSeconds(-1), s, 200, 200 - misses, 150, 0, 0, 1, spread, 0,
            Engine: TrainerResult.EngineFor(s), Accuracy: 97, PlayedSeconds: s.Seconds, JudgementMisses: misses);

    [Test]
    public void StepsAreFixedFullStreamsWithoutAdaptiveDifficulty()
    {
        var ladder = new TrainerStreamLadder(180, 18, "D / F", -12);
        var s = ladder.CurrentSettings();
        Assert.That(ladder.StartBpm, Is.EqualTo(160));
        Assert.That((s.Kind, s.Pattern, s.Music, s.Seconds, s.Bpm), Is.EqualTo((TrainerKind.Alternating, TrainerPattern.Standard, "cues", 15, 160)));
        Assert.That(s.AdaptiveDifficulty || s.RandomizePatterns || s.GuidedCues, Is.False);
        Assert.That((s.Keys, s.OffsetMs, s.PatternSeed), Is.EqualTo(("D / F", -12, TrainerStreamLadder.PatternSeed)));
        var map = TrainerBeatmap.Create(s);
        // Four notes a beat for 15 seconds, nothing thinned.
        Assert.That(map.HitObjects.Count, Is.EqualTo(160 * 4 / 4).Within(4));
        Assert.That(TrainerSkillProfile.Measure(map).PeakNps, Is.EqualTo(160 / 60.0 * 4).Within(.05));
        Assert.That(new TrainerStreamLadder(70, 18, "Z / X", 0).StartBpm, Is.EqualTo(TrainerStreamLadder.MinimumBpm));
    }

    [Test]
    public void CleanStepsClimbAndTheFirstMissEndsTheLadder()
    {
        var ladder = new TrainerStreamLadder(180, 18, "Z / X", 0);
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 12)), Is.True);
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 18)), Is.True, "Spread exactly at the limit is clean.");
        Assert.That((ladder.Finished, ladder.HighestCleanBpm, ladder.CurrentBpm), Is.EqualTo((false, 170, 180)));
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 11, misses: 1)), Is.True);
        Assert.That(ladder.Finished, Is.True);
        Assert.That(ladder.HighestCleanBpm, Is.EqualTo(170));
        Assert.That(ladder.StopReason, Is.EqualTo("1 miss at 180 BPM"));
        Assert.That(ladder.Results.Select(r => r.LadderRun!.Clean), Is.EqualTo(new[] { true, true, false }));
        Assert.That(ladder.Results.All(r => r.LadderRun!.SessionId == ladder.Id && r.LadderRun.SpreadLimitMs == 18), Is.True);
    }

    [Test]
    public void SpreadAboveTheLimitEndsTheLadderWithoutMisses()
    {
        var ladder = new TrainerStreamLadder(150, 15, "Z / X", 0);
        ladder.Record(run(ladder.CurrentSettings(), 15.4));
        Assert.That(ladder.Finished, Is.True);
        Assert.That(ladder.HighestCleanBpm, Is.Null);
        Assert.That(ladder.StopReason, Does.StartWith("Timing spread 15.4 ms at 130 BPM"));
    }

    [Test]
    public void TheTopOfTheLadderEndsIt()
    {
        var ladder = new TrainerStreamLadder(240, 20, "Z / X", 0);
        for (int i = 0; i < 3; i++) Assert.That(ladder.Record(run(ladder.CurrentSettings())), Is.True);
        Assert.That((ladder.Finished, ladder.ReachedTop, ladder.HighestCleanBpm), Is.EqualTo((true, true, 240)));
    }

    [Test]
    public void AssistedIncompleteMismatchedAndDuplicateRunsDoNotCount()
    {
        var ladder = new TrainerStreamLadder(180, 18, "Z / X", 0);
        var result = run(ladder.CurrentSettings());
        Assert.That(ladder.Record(result with { Assisted = true }), Is.False);
        Assert.That(ladder.Record(result with { PlayedSeconds = 5 }), Is.False);
        Assert.That(ladder.Record(result with { Settings = result.Settings with { AdaptiveDifficulty = true } }), Is.False);
        Assert.That(ladder.Record(result with { Settings = result.Settings with { Bpm = 170 } }), Is.False);
        Assert.That(ladder.Record(result), Is.True);
        Assert.That(ladder.Record(result), Is.False);
        Assert.That(ladder.Results, Has.Count.EqualTo(1));
    }

    [Test]
    public void ControlsFollowOsuUntilTheFirstStep()
    {
        var ladder = new TrainerStreamLadder(180, 18, "Z / X", 0);
        ladder.SetControls("D / F", 10);
        Assert.That(ladder.CurrentSettings().Keys, Is.EqualTo("D / F"));
        ladder.Record(run(ladder.CurrentSettings()));
        ladder.SetControls("J / K", 0);
        Assert.That((ladder.CurrentSettings().Keys, ladder.CurrentSettings().OffsetMs), Is.EqualTo(("D / F", 10)));
    }

    [Test]
    public void OutcomesSeparateCompleteLaddersFromOnesEndedEarly()
    {
        var finished = new TrainerStreamLadder(180, 18, "Z / X", 0);
        finished.Record(run(finished.CurrentSettings(), 13, at: DateTimeOffset.UtcNow.AddDays(-2)));
        finished.Record(run(finished.CurrentSettings(), 16, at: DateTimeOffset.UtcNow.AddDays(-2)));
        finished.Record(run(finished.CurrentSettings(), 25, at: DateTimeOffset.UtcNow.AddDays(-2)));
        var early = new TrainerStreamLadder(180, 18, "Z / X", 0);
        early.Record(run(early.CurrentSettings(), 12));
        var outcomes = TrainerStreamLadder.Outcomes(finished.Results.Concat(early.Results).Append(run(new TrainerSettings())));
        Assert.That(outcomes, Has.Count.EqualTo(2));
        Assert.That((outcomes[0].SessionId, outcomes[0].Complete, outcomes[0].HighestCleanBpm), Is.EqualTo((early.Id, false, 160)));
        Assert.That((outcomes[1].Complete, outcomes[1].HighestCleanBpm, outcomes[1].SpreadAtHighestMs, outcomes[1].Rungs), Is.EqualTo((true, 170, 16.0, 3)));
    }

    [Test]
    public void DefaultsRepeatTheLastLadderOrComeFromRecentStreams()
    {
        var now = DateTimeOffset.UtcNow;
        var controls = new TrainerSettings(Kind: TrainerKind.Steady, Bpm: 200);
        Assert.That(TrainerStreamLadder.Defaults([], controls, now), Is.EqualTo(new TrainerLadderDefaults(150, 20, "Starting values. Adjust them to suit you.")));

        var streams = new[] { 14.0, 16, 18 }.Select((spread, i) => run(new TrainerSettings(Kind: TrainerKind.Alternating, Bpm: 170 + i * 10), spread)).ToArray();
        var fromStreams = TrainerStreamLadder.Defaults(streams, controls, now);
        Assert.That((fromStreams.EdgeBpm, fromStreams.SpreadLimitMs), Is.EqualTo((180, 20.0)));

        var ladder = new TrainerStreamLadder(210, 17, "Z / X", 0);
        ladder.Record(run(ladder.CurrentSettings(), 30));
        var repeat = TrainerStreamLadder.Defaults(streams.Concat(ladder.Results), controls, now);
        Assert.That((repeat.EdgeBpm, repeat.SpreadLimitMs), Is.EqualTo((210, 17.0)));
    }

    [Test]
    public void LadderStepsDoNotMoveAdaptiveLimits()
    {
        var now = DateTimeOffset.UtcNow;
        var ladder = new TrainerStreamLadder(200, 18, "Z / X", 0);
        while (!ladder.Finished) ladder.Record(run(ladder.CurrentSettings(), 40, misses: 30));
        Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating, ladder.Results, [], now),
            Is.EqualTo(TrainerSkillProfile.Build(TrainerKind.Alternating, [], [], now)));
    }
}
