using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class TrainerStreamLadderTests
{
    private static TrainerResult run(TrainerSettings s, double? spread = 10, int misses = 0, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), at ?? DateTimeOffset.UtcNow.AddSeconds(-1), s, 200, 200 - misses, 150, 0, 0, 1, spread, 0,
            Engine: TrainerResult.EngineFor(s), Accuracy: 97, PlayedSeconds: s.Seconds, JudgementMisses: misses);

    [Test]
    public void StepsAreFixedFullStreamsWithoutAdaptiveDifficulty()
    {
        var ladder = new TrainerStreamLadder(180, 15, "D / F", -12);
        var s = ladder.CurrentSettings();
        Assert.That(ladder.StartBpm, Is.EqualTo(160));
        Assert.That((s.Kind, s.Pattern, s.Music, s.Seconds, s.Bpm), Is.EqualTo((TrainerKind.Alternating, TrainerPattern.Standard, "cues", 15, 160)));
        Assert.That(s.AdaptiveDifficulty || s.RandomizePatterns || s.GuidedCues, Is.False);
        Assert.That((s.Keys, s.OffsetMs, s.PatternSeed), Is.EqualTo(("D / F", -12, TrainerStreamLadder.PatternSeed)));
        var map = TrainerBeatmap.Create(s);
        // Four notes a beat for 15 seconds, nothing thinned.
        Assert.That(map.HitObjects.Count, Is.EqualTo(160).Within(4));
        Assert.That(TrainerSkillProfile.Measure(map).PeakNps, Is.EqualTo(160 / 60.0 * 4).Within(.05));
        Assert.That(new TrainerStreamLadder(70, 15, "Z / X", 0).StartBpm, Is.EqualTo(TrainerStreamLadder.MinimumBpm));
    }

    [Test]
    public void TheSpreadLimitIsAShareOfTheGapBetweenNotes()
    {
        Assert.That(TrainerStreamLadder.NoteGapMs(150), Is.EqualTo(100).Within(1e-9));
        Assert.That(TrainerStreamLadder.SpreadLimitMs(150, 15), Is.EqualTo(15).Within(1e-9));
        // 24.5 ms is uneven at 200 BPM but even at 70 BPM, where notes are 214 ms apart.
        var slow = run(TrainerStreamLadder.Rung(70, "Z / X", 0), 24.5);
        var fast = run(TrainerStreamLadder.Rung(200, "Z / X", 0), 24.5);
        Assert.That(TrainerStreamLadder.IsClean(slow, 15), Is.True);
        Assert.That(TrainerStreamLadder.IsClean(fast, 15), Is.False);
    }

    [Test]
    public void CleanStepsClimbAndTheFirstStepThatIsNotEndsTheLadder()
    {
        var ladder = new TrainerStreamLadder(180, 15, "Z / X", 0);
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 12)), Is.True);
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 15000.0 / 170 * .15)), Is.True, "Spread exactly at the limit is clean.");
        Assert.That((ladder.Finished, ladder.HighestCleanBpm, ladder.CurrentBpm, ladder.SteppingDown), Is.EqualTo((false, 170, 180, false)));
        Assert.That(ladder.Record(run(ladder.CurrentSettings(), 8, misses: 1)), Is.True);
        Assert.That(ladder.Finished, Is.True);
        Assert.That(ladder.HighestCleanBpm, Is.EqualTo(170));
        Assert.That(ladder.StopReason, Is.EqualTo("1 miss at 180 BPM"));
        Assert.That(ladder.Results.Select(r => r.LadderRun!.Clean), Is.EqualTo(new[] { true, true, false }));
        Assert.That(ladder.Results.All(r => r.LadderRun!.SessionId == ladder.Id && r.LadderRun.SpreadLimitPercent == 15), Is.True);
    }

    [Test]
    public void AFirstStepThatIsNotCleanStepsDownUntilOneIs()
    {
        var ladder = new TrainerStreamLadder(150, 15, "Z / X", 0);
        Assert.That(ladder.StartBpm, Is.EqualTo(130));
        ladder.Record(run(ladder.CurrentSettings(), 51));
        Assert.That((ladder.Finished, ladder.SteppingDown, ladder.CurrentBpm), Is.EqualTo((false, true, 120)));
        ladder.Record(run(ladder.CurrentSettings(), 20));
        Assert.That(ladder.CurrentBpm, Is.EqualTo(110), "20 ms is above 15% of the 125 ms gap at 120 BPM.");
        ladder.Record(run(ladder.CurrentSettings(), 20));
        Assert.That((ladder.Finished, ladder.HighestCleanBpm), Is.EqualTo((true, 110)), "20 ms is within 15% of the 136 ms gap at 110 BPM.");
        Assert.That(ladder.StopReason, Is.EqualTo("Clean at 110 BPM after stepping down from 130 BPM."));
    }

    [Test]
    public void SteppingDownStopsAtTheSlowestTempo()
    {
        var ladder = new TrainerStreamLadder(90, 15, "Z / X", 0);
        while (!ladder.Finished) ladder.Record(run(ladder.CurrentSettings(), 60));
        Assert.That(ladder.Results.Select(r => r.Settings.Bpm), Is.EqualTo(new[] { 70, 60 }));
        Assert.That(ladder.HighestCleanBpm, Is.Null);
        Assert.That(ladder.StopReason, Does.StartWith("No clean step down to 60 BPM."));
    }

    [Test]
    public void TheTopOfTheLadderEndsIt()
    {
        var ladder = new TrainerStreamLadder(240, 15, "Z / X", 0);
        for (int i = 0; i < 3; i++) Assert.That(ladder.Record(run(ladder.CurrentSettings(), 5)), Is.True);
        Assert.That((ladder.Finished, ladder.ReachedTop, ladder.HighestCleanBpm), Is.EqualTo((true, true, 240)));
    }

    [Test]
    public void AssistedIncompleteMismatchedAndDuplicateRunsDoNotCount()
    {
        var ladder = new TrainerStreamLadder(180, 15, "Z / X", 0);
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
        var ladder = new TrainerStreamLadder(180, 15, "Z / X", 0);
        ladder.SetControls("D / F", 10);
        Assert.That(ladder.CurrentSettings().Keys, Is.EqualTo("D / F"));
        ladder.Record(run(ladder.CurrentSettings()));
        ladder.SetControls("J / K", 0);
        Assert.That((ladder.CurrentSettings().Keys, ladder.CurrentSettings().OffsetMs), Is.EqualTo(("D / F", 10)));
    }

    [Test]
    public void OutcomesSeparateCompleteLaddersFromOnesEndedEarly()
    {
        var earlier = DateTimeOffset.UtcNow.AddDays(-2);
        var climbed = new TrainerStreamLadder(180, 15, "Z / X", 0);
        climbed.Record(run(climbed.CurrentSettings(), 11, at: earlier));
        climbed.Record(run(climbed.CurrentSettings(), 12, at: earlier));
        climbed.Record(run(climbed.CurrentSettings(), 25, at: earlier));
        var steppedDown = new TrainerStreamLadder(150, 15, "Z / X", 0);
        steppedDown.Record(run(steppedDown.CurrentSettings(), 40, at: earlier.AddHours(1)));
        steppedDown.Record(run(steppedDown.CurrentSettings(), 15, at: earlier.AddHours(1)));
        var early = new TrainerStreamLadder(180, 15, "Z / X", 0);
        early.Record(run(early.CurrentSettings(), 11));
        var downEarly = new TrainerStreamLadder(180, 15, "Z / X", 0);
        downEarly.Record(run(downEarly.CurrentSettings(), 40, at: DateTimeOffset.UtcNow.AddSeconds(-2)));
        // Steps from the first test build had a limit in milliseconds and no relative limit.
        var legacy = run(TrainerStreamLadder.Rung(70, "Z / X", 0), 24.5) with { LadderRun = new(Guid.NewGuid(), 0, 70, 0, false) };

        var outcomes = TrainerStreamLadder.Outcomes([.. climbed.Results, .. steppedDown.Results, .. early.Results, .. downEarly.Results, legacy, run(new TrainerSettings())]);
        Assert.That(outcomes, Has.Count.EqualTo(4));
        var byId = outcomes.ToDictionary(o => o.SessionId);
        Assert.That((byId[climbed.Id].Complete, byId[climbed.Id].HighestCleanBpm, byId[climbed.Id].SpreadAtHighestMs, byId[climbed.Id].SpreadAtStartMs, byId[climbed.Id].Rungs),
            Is.EqualTo((true, 170, 12.0, 11.0, 3)));
        Assert.That((byId[steppedDown.Id].Complete, byId[steppedDown.Id].HighestCleanBpm), Is.EqualTo((true, 120)));
        Assert.That((byId[early.Id].Complete, byId[early.Id].HighestCleanBpm), Is.EqualTo((false, 160)));
        Assert.That((byId[downEarly.Id].Complete, byId[downEarly.Id].HighestCleanBpm), Is.EqualTo((false, (int?)null)));
        Assert.That(outcomes[0].SessionId, Is.EqualTo(early.Id), "newest first");
    }

    [Test]
    public void DefaultsRepeatTheLastLadderOrComeFromRecentStreams()
    {
        var now = DateTimeOffset.UtcNow;
        var controls = new TrainerSettings(Kind: TrainerKind.Steady, Bpm: 200);
        Assert.That(TrainerStreamLadder.Defaults([], controls, now), Is.EqualTo(new TrainerLadderDefaults(150, 15, "Starting values. Adjust them to suit you.")));

        // 14, 16 and 18 ms at 170, 180 and 190 BPM are 15.9%, 19.2% and 22.8% of the note gap.
        var streams = new[] { 14.0, 16, 18 }.Select((spread, i) => run(new TrainerSettings(Kind: TrainerKind.Alternating, Bpm: 170 + i * 10), spread)).ToArray();
        var fromStreams = TrainerStreamLadder.Defaults(streams, controls, now);
        Assert.That((fromStreams.EdgeBpm, fromStreams.SpreadLimitPercent), Is.EqualTo((180, 24.0)));

        var ladder = new TrainerStreamLadder(210, 17, "Z / X", 0);
        ladder.Record(run(ladder.CurrentSettings(), 5));
        var repeat = TrainerStreamLadder.Defaults(streams.Concat(ladder.Results), controls, now);
        Assert.That((repeat.EdgeBpm, repeat.SpreadLimitPercent), Is.EqualTo((210, 17.0)));
    }

    [Test]
    public void LadderStepsDoNotMoveAdaptiveLimits()
    {
        var now = DateTimeOffset.UtcNow;
        var ladder = new TrainerStreamLadder(200, 15, "Z / X", 0);
        while (!ladder.Finished) ladder.Record(run(ladder.CurrentSettings(), 60, misses: 30));
        Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating, ladder.Results, [], now),
            Is.EqualTo(TrainerSkillProfile.Build(TrainerKind.Alternating, [], [], now)));
    }
}
