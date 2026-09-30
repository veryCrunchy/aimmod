using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerSessionTests
{
    [TestCase(TrainerKind.Steady, 60)]
    [TestCase(TrainerKind.Alternating, 120)]
    [TestCase(TrainerKind.Bursts, 45)]
    [TestCase(TrainerKind.Rhythm, 88)]
    public void PatternsHaveCountInAndIntendedRhythms(TrainerKind kind, int expected)
    {
        var s = new TrainerSession(new(kind, 120, 15));
        Assert.That(s.StartMs, Is.EqualTo(2500));
        Assert.That(s.Notes.Count, Is.EqualTo(expected));
        Assert.That(s.Notes.Select(n => n.TimeMs), Is.Ordered.Ascending);
        Assert.That(s.Notes[^1].TimeMs, Is.LessThan(s.EndMs));
    }

    [Test]
    public void CountInDoesNotScoreAndDuplicateCannotHitSameNote()
    {
        var s = new TrainerSession(new());
        s.Tap(500, 0); Assert.That(s.Extras, Is.Zero);
        s.Tap(s.StartMs, 0); s.Tap(s.StartMs, 1);
        Assert.That(s.Hits.Count, Is.EqualTo(1)); Assert.That(s.Extras, Is.EqualTo(1));
        var r = s.Result(DateTimeOffset.UtcNow);
        Assert.That(r.Misses, Is.EqualTo(s.Notes.Count - 1));
        Assert.That(r.OnTimePercent, Is.LessThan(1));
    }

    [Test]
    public void SteadyOffsetAndUnevenTappingProduceDifferentMetrics()
    {
        var even = new TrainerSession(new(Seconds:15));
        foreach (var n in even.Notes) even.Tap(n.TimeMs + 20, 0);
        var r = even.Result(DateTimeOffset.UtcNow);
        Assert.That(r.MeanMs, Is.EqualTo(20)); Assert.That(r.SpreadMs, Is.Zero);
        Assert.That(r.OnTimePercent, Is.EqualTo(100)); Assert.That(r.DriftMs, Is.Zero);
        var uneven = new TrainerSession(new(Seconds:15));
        foreach (var (n, i) in uneven.Notes.Select((n, i) => (n, i))) uneven.Tap(n.TimeMs + (i % 2 == 0 ? -35 : 35), i % 2);
        var u = uneven.Result(DateTimeOffset.UtcNow);
        Assert.That(u.MeanMs, Is.Zero); Assert.That(u.SpreadMs, Is.EqualTo(35));
        Assert.That(u.OnTimePercent, Is.Zero);
    }

    [Test]
    public void OffsetCompensatesForDelayedAudioAndMissesRemainInDenominator()
    {
        var s = new TrainerSession(new(Seconds:15, OffsetMs:40));
        foreach (var n in s.Notes.Take(s.Notes.Count / 2)) s.Tap(n.TimeMs - 40, 0);
        var r = s.Result(DateTimeOffset.UtcNow);
        Assert.That(r.MeanMs, Is.Zero); Assert.That(r.OnTimePercent, Is.EqualTo(50));
        Assert.That(r.DriftMs, Is.Null);
    }

    [Test]
    public void AlternationCountsRepeatedKeysButAllowsEitherStartingKey()
    {
        var s = new TrainerSession(new(TrainerKind.Alternating));
        s.Tap(s.Notes[0].TimeMs, 1); s.Tap(s.Notes[1].TimeMs, 0); s.Tap(s.Notes[2].TimeMs, 0);
        Assert.That(s.RepeatedKeys, Is.EqualTo(1));
    }

    [Test]
    public void NoTapsHaveMissingStatisticsRatherThanPerfectSpread()
    {
        var r = new TrainerSession(new()).Result(DateTimeOffset.UtcNow);
        Assert.That(r.MeanMs, Is.Null); Assert.That(r.SpreadMs, Is.Null); Assert.That(r.OnTimePercent, Is.Zero);
    }

    [Test]
    public void AudioCuesShareTheScoringTimeline()
    {
        var s = new TrainerSession(new(Seconds:15));
        var wave = TrainerAudio.Render(s);
        Assert.That(System.Text.Encoding.ASCII.GetString(wave, 0, 4), Is.EqualTo("RIFF"));
        int sample = (int)Math.Round(s.StartMs * 44.1);
        Assert.That(BitConverter.ToInt16(wave, 44 + (sample - 1) * 2), Is.Zero);
        Assert.That(Enumerable.Range(sample, 50).Any(i => BitConverter.ToInt16(wave, 44 + i * 2) != 0), Is.True);
        Assert.That(wave.Length, Is.EqualTo(44 + (int)((s.EndMs + 600) * 44.1) * 2));
    }

    [Test]
    public void ReadingRequiresSequenceAndGeneratesAnotherSet()
    {
        var s = new PointerTrainerSession(new(TrainerKind.Reading), 1);
        Assert.That(s.Tap(300, 2), Is.False);
        for (int i = 1; i <= 4; i++) Assert.That(s.Tap(i * 500, i), Is.True);
        Assert.That(s.Targets.Count, Is.EqualTo(4)); Assert.That(s.NextNumber, Is.EqualTo(1));
        Assert.That(s.Extras, Is.EqualTo(1)); Assert.That(s.Result(DateTimeOffset.UtcNow).ResponseMs, Is.EqualTo(500));
    }

    [Test]
    public void ReactionFalseStartsDoNotCreateFastResponses()
    {
        var s = new PointerTrainerSession(new(TrainerKind.Reaction), 2);
        Assert.That(s.Tap(100), Is.False); Assert.That(s.Responses, Is.Empty);
        Assert.That(s.Tap(s.CueTime + 240), Is.True);
        Assert.That(s.Result(DateTimeOffset.UtcNow).ResponseMs, Is.EqualTo(240));
        Assert.That(s.Tap(s.EndMs + 1), Is.False);
    }

    [Test]
    public void HistoryRoundTripsAndDeduplicatesCompletedSessions()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trainer-test-" + Guid.NewGuid().ToString("N"));
        var store = new TrainerHistoryStore(Path.Combine(directory, "history.json"));
        var result = new TrainerSession(new()).Result(DateTimeOffset.UtcNow);
        try
        {
            store.Add(result); store.Add(result);
            Assert.That(store.Load(), Has.Count.EqualTo(1)); Assert.That(store.Load()[0], Is.EqualTo(result));
        }
        finally { Directory.Delete(directory, true); }
    }
}
