using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osuTK;

namespace AimMod.Desktop.Tests;

public class ReadingReactionTests
{
    [Test]
    public void ChoiceRejectsWrongKeysAndEarlyTapsWithoutFastResponses()
    {
        var session = new ReactionSession(new(Kind: TrainerKind.Reaction, ReactionMode: ReactionMode.Choice), 12);
        Assert.That(session.Tap(100, 0), Is.False);
        Assert.That(session.Tap(session.CueTime + 220, 1 - session.RequiredKey), Is.False);
        Assert.That(session.Tap(session.CueTime + 340, session.RequiredKey), Is.True);
        var summary = session.Summary();
        Assert.That(summary.Early, Is.EqualTo(1));
        Assert.That(summary.WrongKey, Is.EqualTo(1));
        Assert.That(summary.Correct, Is.EqualTo(1));
        Assert.That(summary.MedianMs, Is.EqualTo(340));
    }

    [Test]
    public void StopCuesRewardWithholdingAndDoNotContributeReactionTimes()
    {
        var session = new ReactionSession(new(Kind: TrainerKind.Reaction, Seconds: 180, ReactionMode: ReactionMode.GoNoGo), 3);
        while (!session.NoGo) session.Tap(session.CueTime + 250, 0);
        session.Advance(session.CueTime + session.Settings.ReactionWindowMs);
        Assert.That(session.Summary().Withheld, Is.EqualTo(1));
        while (!session.NoGo) session.Tap(session.CueTime + 250, 0);
        Assert.That(session.Tap(session.CueTime + 5, 0), Is.False);
        Assert.That(session.Summary().FalseAlarms, Is.EqualTo(1));
        Assert.That(session.Summary().MedianMs, Is.EqualTo(250));
    }

    [Test]
    public void ExpiredCueCountsOnceAndLastCueGetsItsFullWindow()
    {
        var session = new ReactionSession(new(Kind: TrainerKind.Reaction, Seconds: 15, ReactionDelay: TrainerReactionDelay.Short, ReactionWindowMs: 2000), 4);
        Assert.That(session.Tap(session.CueTime + 2001, 0), Is.False);
        Assert.That(session.Summary().Missed, Is.EqualTo(1));
        Assert.That(session.Summary().Early, Is.Zero);
        while (session.CueTime + 2000 < 15000) session.Tap(session.CueTime + 300, 0);
        Assert.That(session.CueTime, Is.LessThan(15000));
        Assert.That(session.EndMs, Is.GreaterThan(15000));
        Assert.That(session.Tap(15001, 0), Is.True);
        Assert.That(session.Tap(double.NaN, 0), Is.False);
        Assert.That(session.Tap(session.EndMs + 1, 0), Is.False);
    }

    [Test]
    public void ReadingPatternsAreSeededBoundedAndRespectSequenceLength()
    {
        foreach (var pattern in TrainerPatterns.Choices(TrainerKind.Reading).Values)
        foreach (int length in new[] { 4, 6, 8, 12 })
        {
            var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: pattern, ReadingGroupSize: length, PatternSeed: 52);
            var map = TrainerBeatmap.Create(settings);
            var repeat = TrainerBeatmap.Create(settings);
            Assert.That(map.HitObjects.Select(h => h.Position), Is.EqualTo(repeat.HitObjects.Select(h => h.Position)));
            Assert.That(map.HitObjects.All(h => h.Position.X is >= 64 and <= 448 && h.Position.Y is >= 64 and <= 320), Is.True);
            Assert.That(map.HitObjects.Select((h, i) => h.NewCombo == (i % length == 0)).All(x => x), Is.True);
            Assert.That(map.HitObjects.Select(h => h.Position), Is.Not.EqualTo(TrainerBeatmap.Create(settings with { PatternSeed = 53 }).HitObjects.Select(h => h.Position)));
        }
    }

    [Test]
    public void HiddenUsesOsuModAndDifferentSetupsStaySeparate()
    {
        var reading = new TrainerSettings(Kind: TrainerKind.Reading);
        Assert.That(TrainerReadingPatterns.Mods(reading), Is.Empty);
        Assert.That(TrainerReadingPatterns.Mods(reading with { ReadingHidden = true }).Single(), Is.TypeOf<OsuModHidden>());
        Assert.That(reading.ComparisonKey(), Is.Not.EqualTo((reading with { ReadingHidden = true }).ComparisonKey()));
        Assert.That(reading.ComparisonKey(), Is.Not.EqualTo((reading with { ReadingGroupSize = 8 }).ComparisonKey()));
        Assert.That(reading.ComparisonKey(), Is.EqualTo((reading with { ReactionMode = ReactionMode.Choice }).ComparisonKey()));
        var reaction = new TrainerSettings(Kind: TrainerKind.Reaction);
        Assert.That(reaction.ComparisonKey(), Is.Not.EqualTo((reaction with { ReactionMode = ReactionMode.Choice }).ComparisonKey()));
        Assert.That(reaction.ComparisonKey(), Is.Not.EqualTo((reaction with { ReactionWindowMs = 600 }).ComparisonKey()));
    }
    [Test]
    public void SuccessivePhrasesChangeTheirShapeNotJustRotation()
    {
        var settings = new TrainerSettings(Kind: TrainerKind.Reading, ReadingGroupSize: 8, PatternSeed: 81, Seconds: 60);
        var notes = new TrainerSession(settings).Notes;
        var positions = TrainerReadingPatterns.Create(settings, notes);
        var ratios = Enumerable.Range(0, positions.Length / 8).Select(g =>
            Math.Round(Vector2.Distance(positions[g * 8], positions[g * 8 + 1]) /
                       Vector2.Distance(positions[g * 8 + 1], positions[g * 8 + 2]), 2)).Distinct().Count();
        Assert.That(ratios, Is.GreaterThan(4), "Rotating or scaling a repeated phrase preserves its distance ratios.");
    }

    [Test]
    public void MixedReadingChangesMotifsRhythmsAndDensity()
    {
        var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: TrainerPattern.ReadingMix, PatternSeed: 42, Seconds: 60);
        var clear = new TrainerSession(settings);
        var dense = new TrainerSession(settings with { ReadingComplexity = 2 });
        Assert.That(dense.Notes.Select(n => n.Pattern).Distinct().Count(), Is.GreaterThanOrEqualTo(5));
        Assert.That(dense.Notes.Count, Is.LessThanOrEqualTo(clear.Notes.Count * 2));
        var gaps = dense.Notes.Zip(dense.Notes.Skip(1), (a, b) => b.TimeMs - a.TimeMs).ToArray();
        Assert.That(gaps.Min(), Is.GreaterThan(0));
        Assert.That(gaps.Distinct().Count(), Is.GreaterThanOrEqualTo(3));
        Assert.That(dense.Notes, Is.EqualTo(new TrainerSession(settings with { ReadingComplexity = 2 }).Notes));
        Assert.That(dense.Notes, Is.Not.EqualTo(new TrainerSession(settings with { ReadingComplexity = 2, PatternSeed = 43 }).Notes));
        Assert.That(settings.ComparisonKey(), Is.Not.EqualTo((settings with { ReadingComplexity = 2 }).ComparisonKey()));
        Assert.That(TrainerResult.EngineFor(settings), Is.EqualTo("osu-reading-v4"));
    }

    [Test]
    public void AdvancedReadingStaysOnThePlayfieldAtWideSpacing()
    {
        foreach (var pattern in TrainerPatterns.Choices(TrainerKind.Reading).Values)
        for (int seed = 1; seed <= 5; seed++)
        {
            var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: pattern, ReadingComplexity: 2,
                PatternSeed: seed, AimSpacing: 140, ReadingGroupSize: 12);
            var map = TrainerBeatmap.Create(settings);
            Assert.That(map.HitObjects.All(h => h.Position.X is >= 64 and <= 448 && h.Position.Y is >= 64 and <= 320), Is.True);
        }
    }

    [Test]
    public void DenseMixedReadingCannotBypassSkillLimits()
    {
        var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: TrainerPattern.ReadingMix, Bpm: 240,
            ReadingComplexity: 2, ReadingGroupSize: 12, RandomizePatterns: true, PatternSeed: 81, AimSpacing: 140);
        var limited = TrainerSkillProfile.Apply(settings, new());
        Assert.That(limited.ReadingComplexity, Is.Zero);
        var demand = TrainerSkillProfile.Measure(TrainerBeatmap.Create(limited));
        Assert.That(demand.PeakNps, Is.LessThanOrEqualTo(2.501));
        Assert.That(demand.JumpDistance, Is.LessThanOrEqualTo(120.001));
        Assert.That(demand.AimVelocity, Is.LessThanOrEqualTo(300.001));
    }

    [Test]
    public void ReadingVisibilityAndPhraseBreaksHoldAtEveryApproachRate()
    {
        for (int ar = 3; ar <= 10; ar++)
        for (int level = 0; level <= 2; level++)
        {
            var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: TrainerPattern.ReadingMix,
                Bpm: 240, NoteSpeed: TrainerNoteSpeed.FourPerBeat, ReadingComplexity: level, ApproachRate: ar, ReadingGroupSize: 6);
            var notes = new TrainerSession(settings).Notes;
            double visibility = TrainerReadingPatterns.VisibilityMs(settings);
            Assert.That(notes, Is.EqualTo(TrainerReadingPatterns.ConstrainTiming(settings, notes)), "Filtering twice must not erase more notes.");
            foreach (var note in notes)
                Assert.That(notes.Count(n => n.TimeMs >= note.TimeMs && n.TimeMs < note.TimeMs + visibility), Is.LessThanOrEqualTo(3 + level));
            for (int i = 1; i < notes.Count; i++)
            {
                double gap = notes[i].TimeMs - notes[i - 1].TimeMs;
                Assert.That(gap, Is.GreaterThanOrEqualTo(250));
                if (i % settings.ReadingGroupSize == 0) Assert.That(gap, Is.GreaterThanOrEqualTo(visibility));
            }
        }
    }

    [Test]
    public void OrdinaryReadingNotesDoNotObscureOtherActiveTargets()
    {
        foreach (var pattern in new[] { TrainerPattern.Crossings, TrainerPattern.Scattered, TrainerPattern.ReadingPolygons, TrainerPattern.ReadingWeave })
        for (int seed = 0; seed < 5; seed++)
        {
            var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: pattern, ReadingComplexity: 2,
                Bpm: 180, NoteSpeed: TrainerNoteSpeed.FourPerBeat, ApproachRate: 5, ReadingGroupSize: 8, PatternSeed: seed);
            var map = TrainerBeatmap.Create(settings);
            double visibility = TrainerReadingPatterns.VisibilityMs(settings);
            for (int i = 0; i < map.HitObjects.Count; i++)
            for (int j = i - 1; j >= 0 && map.HitObjects[i].StartTime - map.HitObjects[j].StartTime < visibility; j--)
                Assert.That(Vector2.Distance(map.HitObjects[i].Position, map.HitObjects[j].Position), Is.GreaterThanOrEqualTo(75.99));
        }
    }

    [Test]
    public void ImportedSongTimingCannotBypassReadingVisibilityBudget()
    {
        var settings = new TrainerSettings(Kind: TrainerKind.Reading, Pattern: TrainerPattern.Crossings, ReadingComplexity: 2, ApproachRate: 3);
        var source = Enumerable.Range(0, 400).Select(i => new TrainerNote(i * 50, 0, TrainerPattern.Crossings)).ToArray();
        var map = TrainerBeatmap.Create(settings, source);
        Assert.That(map.HitObjects.Count, Is.LessThan(60));
        double visibility = TrainerReadingPatterns.VisibilityMs(settings);
        foreach (var note in map.HitObjects)
            Assert.That(map.HitObjects.Count(n => n.StartTime >= note.StartTime && n.StartTime < note.StartTime + visibility), Is.LessThanOrEqualTo(5));
    }

}
