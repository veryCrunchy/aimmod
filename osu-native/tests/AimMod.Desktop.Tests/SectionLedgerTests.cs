using AimMod.Desktop.Coaching;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class SectionLedgerTests
{
    private static readonly AdaptiveSection[] sections = [new(0, 0, 9_000, 0, 10), new(1, 10_000, 19_000, 10, 10), new(2, 20_000, 29_000, 20, 10)];

    [Test]
    public void JoinedCombosMatchPlayingThemInOneGo()
    {
        var a = ComboRun.Empty.Add(10);
        var b = ComboRun.Empty.Add(5).Break().Add(4);
        var c = ComboRun.Empty.Add(10);
        Assert.Multiple(() =>
        {
            Assert.That(ComboRun.Join(ComboRun.Join(a, b), c).MaxCombo, Is.EqualTo(15));
            Assert.That(ComboRun.Join(a, ComboRun.Join(b, c)).MaxCombo, Is.EqualTo(15));
            Assert.That(ComboRun.Join(a, c).MaxCombo, Is.EqualTo(20));
        });
    }

    [Test]
    public void TakingBackAMissJoinsTheComboAroundIt()
    {
        var play = new Play().Hits(15).Miss().Hits(14);
        var tallies = SectionLedger.Tally(play.Judgements, sections);
        var repaired = SectionLedger.Tally(play.Judgements, sections, s => s == 1 ? SectionRepair.Breaks : SectionRepair.None);
        var (original, originalCombo) = SectionLedger.Combine(tallies);
        var (fixedPlay, fixedCombo) = SectionLedger.Combine(repaired);
        Assert.Multiple(() =>
        {
            Assert.That(tallies[1].Breaks, Is.EqualTo(1));
            Assert.That(tallies.All(t => t.Complete));
            Assert.That(originalCombo, Is.EqualTo(15));
            Assert.That(original.Miss, Is.EqualTo(1));
            Assert.That(fixedCombo, Is.EqualTo(30));
            Assert.That(fixedPlay.Miss, Is.Zero);
            Assert.That(fixedPlay.Great, Is.EqualTo(30));
        });
    }

    [Test]
    public void TakingBackHundredsKeepsTheCombo()
    {
        var play = new Play().Hits(12).Ok().Ok().Hits(16);
        var repaired = SectionLedger.Tally(play.Judgements, sections, s => s == 1 ? SectionRepair.Hits : SectionRepair.None);
        var (statistics, combo) = SectionLedger.Combine(repaired);
        Assert.Multiple(() =>
        {
            Assert.That(SectionLedger.Tally(play.Judgements, sections)[1].Ok, Is.EqualTo(2));
            Assert.That(statistics.Ok, Is.Zero);
            Assert.That(combo, Is.EqualTo(30));
        });
    }

    [Test]
    public void ADroppedSliderEndKeepsComboButCostsItsPoint()
    {
        var play = new Play().Hits(5).Slider(droppedEnd: true).Hits(24);
        var tally = SectionLedger.Tally(play.Judgements, sections);
        var repaired = SectionLedger.Tally(play.Judgements, sections, s => SectionRepair.Breaks);
        Assert.Multiple(() =>
        {
            Assert.That(tally[0].Breaks, Is.Zero, "a dropped slider end is not a combo break");
            Assert.That(tally[0].SliderTailMiss, Is.EqualTo(1));
            Assert.That(SectionLedger.Combine(tally).MaxCombo, Is.EqualTo(30));
            Assert.That(SectionLedger.Combine(repaired).MaxCombo, Is.EqualTo(31));
            Assert.That(SectionLedger.Combine(repaired).Statistics.SliderTailHit, Is.EqualTo(1));
        });
    }

    [Test]
    public void LedgerKeepsEachSectionsBestIncludingFailedPlays()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clean = new Play().Hits(12).Ok().Hits(17);
        var failed = new Play().Hits(30).Judgements.Where(j => j.ObjectIndex < 25).ToArray();
        var messy = new Play().Hits(3).Miss().Hits(26);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        var entry = SectionLedgerEntry.Create("map", sections, now)
            .Record(first, now, SectionLedger.Tally(clean.Judgements, sections), now)
            .Record(second, now, SectionLedger.Tally(failed, sections), now)
            .Record(third, now, SectionLedger.Tally(messy.Judgements, sections), now);
        var again = entry.Record(second, now, SectionLedger.Tally(failed, sections), now);
        Assert.Multiple(() =>
        {
            Assert.That(entry.Bests[0]!.ScoreId, Is.EqualTo(first), "a miss is worse than nothing");
            Assert.That(entry.Bests[1]!.ScoreId, Is.EqualTo(second), "the failed play's clean section counts");
            Assert.That(entry.Bests[2]!.ScoreId, Is.EqualTo(first), "a section the failed play never reached is not counted");
            Assert.That(entry.Bests[2]!.Plays, Is.EqualTo(2));
            Assert.That(again, Is.SameAs(entry), "a play is only counted once");
            Assert.That(SectionLedger.Combine(entry.Bests.Select(b => b!.Tally)).MaxCombo, Is.EqualTo(30));
            Assert.That(entry.Matches(sections));
        });
    }

    /// <summary>Builds judgements the way osu!lazer records them: circles, and sliders as head, end and the slider itself.</summary>
    private sealed class Play
    {
        private int index, combo;
        public List<ReplayObjectJudgement> Judgements { get; } = [];

        public Play Hits(int count)
        {
            for (int i = 0; i < count; i++) circle("Great");
            return this;
        }

        public Play Ok() { circle("Ok"); return this; }
        public Play Miss() { circle("Miss"); return this; }

        public Play Slider(bool droppedEnd)
        {
            add("SliderHeadCircle", "head", "Great", "Great", combo + 1);
            add("SliderTailCircle", "tail", droppedEnd ? "IgnoreMiss" : "SliderTailHit", "SliderTailHit", droppedEnd ? combo : combo + 1);
            add("Slider", null, "IgnoreHit", "IgnoreHit", combo);
            index++;
            return this;
        }

        private void circle(string result)
        {
            add("HitCircle", null, result, "Great", result == "Miss" ? 0 : combo + 1);
            index++;
        }

        private void add(string type, string? nested, string result, string maximum, int after)
        {
            double time = index * 1_000;
            Judgements.Add(new(index, nested, type, time, time, result, maximum, time, 0, 1, null, null, combo, after));
            combo = after;
        }
    }
}
