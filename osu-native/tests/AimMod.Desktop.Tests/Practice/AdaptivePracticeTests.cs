using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Utils;

namespace AimMod.Desktop.Tests.Practice;

public class AdaptivePracticeTests
{
    private static readonly DateTimeOffset now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Evenly spaced circles, one every <paramref name="spacing"/> ms, with a new combo every <paramref name="comboEvery"/> objects.</summary>
    private static AdaptiveObject[] stream(int count, double spacing = 250, int comboEvery = 8, double start = 1_000) =>
        Enumerable.Range(0, count).Select(i => new AdaptiveObject(start + i * spacing, start + i * spacing, i % comboEvery == 0)).ToArray();

    private static AdaptivePracticeProgress progress(int sections, double rate = 1) =>
        AdaptivePracticeCoach.Create("map", "Title", "Hard", rate, sections, now);

    /// <summary>Four ten-second sections with one-second gaps.</summary>
    private static readonly AdaptiveSection[] sections = Enumerable.Range(0, 4)
        .Select(i => new AdaptiveSection(i, 1_000 + i * 10_000, 10_000 + i * 10_000, i * 20, 20)).ToArray();

    private static AdaptivePracticeProgress fail(AdaptivePracticeProgress p, int section, double? at = null) =>
        AdaptivePracticeCoach.Fail(p, sections, section, at ?? sections[section].StartMs + 5_000, now);

    /// <summary>Completes whatever the coach schedules next, cleanly or with the given misses.</summary>
    private static AdaptivePracticeProgress clear(AdaptivePracticeProgress p, params double[] misses)
    {
        var drill = AdaptivePracticeCoach.Next(p, sections);
        return AdaptivePracticeCoach.Complete(p, sections, drill, result(misses.Length, 0, misses), now);
    }

    /// <summary>A twenty-note section or spot with the given misses and 100s.</summary>
    private static AdaptiveResult result(int misses = 0, int offbeat = 0, double[]? missTimes = null, int notes = 20, double? accuracy = null) =>
        new(misses, notes, offbeat, accuracy ?? 1 - (misses + offbeat * 2 / 3d) / notes, missTimes ?? [], notes);

    private static AdaptivePracticeProgress mastered(params int[] which)
    {
        var p = progress(4);
        var all = p.Sections.ToArray();
        foreach (int i in which) all[i] = all[i] with { Tried = true, ClearedAtFullSpeed = true };
        return p with { Sections = all };
    }

    [Test]
    public void SectionsCoverEveryObjectOnceAndCutAtTheFirstNewComboAfterTheTargetLength()
    {
        var objects = stream(240);
        var split = AdaptivePracticeSections.Split(objects);

        Assert.That(split.Sum(s => s.ObjectCount), Is.EqualTo(objects.Length));
        Assert.That(split.Select(s => s.FirstObject), Is.Ordered.Ascending);
        for (int i = 1; i < split.Count; i++)
        {
            Assert.That(split[i].FirstObject, Is.EqualTo(split[i - 1].LastObject + 1));
            Assert.That(objects[split[i].FirstObject].NewCombo, Is.True, "a phrase cut lands on a new combo");
            Assert.That(split[i - 1].EndMs - split[i - 1].StartMs, Is.InRange(AdaptivePracticeSections.TargetMs - 250, AdaptivePracticeSections.MaximumMs));
        }
    }

    [Test]
    public void BreaksAndLongGapsAlwaysStartANewSectionAndLongPhrasesAreCapped()
    {
        // No new combos at all: only the maximum length can cut the first phrase.
        var phrase = stream(100, comboEvery: 1000);
        var afterGap = stream(40, start: phrase[^1].StartMs + 4_000, comboEvery: 1000);
        var split = AdaptivePracticeSections.Split([.. phrase, .. afterGap]);

        Assert.That(split.Any(s => s.FirstObject == phrase.Length), Is.True, "the 4 s gap starts a section");
        Assert.That(split.All(s => s.EndMs - s.StartMs <= AdaptivePracticeSections.MaximumMs), Is.True);

        var broken = AdaptivePracticeSections.Split([.. stream(30), .. stream(30, start: 9_000)], [(8_300, 8_900)]);
        Assert.That(broken.Select(s => s.FirstObject), Does.Contain(30), "a beatmap break starts a section");
    }

    [Test]
    public void AShortTailJoinsThePreviousSection()
    {
        var split = AdaptivePracticeSections.Split(stream(52, comboEvery: 4));
        Assert.That(split[^1].EndMs - split[^1].StartMs, Is.GreaterThanOrEqualTo(AdaptivePracticeSections.MinimumMs));
        Assert.That(split[^1].LastObject, Is.EqualTo(51));
    }

    [Test]
    public void TheFirstDrillIsTheFirstSectionAloneAtOneSpeed()
    {
        var drill = AdaptivePracticeCoach.Next(progress(4), sections);

        Assert.That(drill, Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 0, 0, 1_000, 10_000, 1)));
        Assert.That(new AdaptiveAttemptPlan(sections, progress(4, rate: 1.5), drill).Rate, Is.EqualTo(1.5), "one rate for the whole attempt");
        Assert.That(AdaptivePracticeCoach.Next(progress(4), sections, start: 2).Focus, Is.EqualTo(2), "coaching can start on a chosen section");
        Assert.That(AdaptivePracticeCoach.Next(mastered(2), sections, start: 2).Focus, Is.Zero, "a mastered section is not drilled again");
    }

    [Test]
    public void EachConsecutiveFailSlowsTheSectionFurtherUntilTheFloor()
    {
        var p = progress(3);
        var speeds = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            p = AdaptivePracticeCoach.Fail(p, sections, 1, 12_000 + i * 3_000, now);
            speeds.Add(p.Sections[1].Speed);
        }

        Assert.That(speeds, Is.EqualTo(new[] { .95, .85, .70, .55, .50, .50 }));
        Assert.That(p.Sections[1].ConsecutiveFails, Is.EqualTo(6));
        Assert.That(p.Fails, Is.EqualTo(6));
        Assert.That(p.Sections[0].Speed, Is.EqualTo(1), "other sections keep their speed");
    }

    [Test]
    public void ASlowedSectionSpeedsUpOnlyOnCleanClears()
    {
        var p = fail(fail(progress(4), 0, 2_000), 0, 8_000);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.85));

        p = clear(p, 4_000, 9_500);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.85), "a scrappy clear holds the speed");
        Assert.That(p.Sections[0].ConsecutiveFails, Is.Zero);

        p = clear(p);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.90));
        Assert.That(AdaptivePracticeCoach.Next(p, sections), Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 0, 0, 1_000, 10_000, .9)),
            "the same section again, nothing after it");
    }

    [Test]
    public void TheMoreASectionFailsTheMoreCleanFullSpeedClearsItNeeds()
    {
        Assert.That(AdaptivePracticeCoach.RequiredStreak(new()), Is.EqualTo(1));
        Assert.That(AdaptivePracticeCoach.RequiredStreak(new(Fails: 3)), Is.EqualTo(2));
        Assert.That(AdaptivePracticeCoach.RequiredStreak(new(Fails: 20)), Is.EqualTo(4));

        var p = progress(4);
        var all = p.Sections.ToArray();
        all[0] = all[0] with { Fails = 4, Tried = true };
        p = p with { Sections = all };
        p = clear(clear(p));
        Assert.That(p.Sections[0].CleanStreak, Is.EqualTo(2));
        Assert.That(p.Sections[0].ClearedAtFullSpeed, Is.False, "four fails need three clean clears");

        p = clear(p, 2_000, 8_000);
        Assert.That(p.Sections[0].CleanStreak, Is.Zero, "a clear with misses restarts the streak");

        p = clear(clear(clear(p)));
        Assert.That(p.Sections[0].ClearedAtFullSpeed, Is.True);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(1), "then the coach moves on");
    }

    [Test]
    public void ANewlyMasteredSectionIsConnectedToThePreviousOne()
    {
        var p = clear(mastered(0));
        Assert.That(p.Sections[1].ClearedAtFullSpeed, Is.True);
        Assert.That(AdaptivePracticeCoach.Next(p, sections), Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Connect, 0, 1, 1_000, 20_000, 1)));

        p = clear(p);
        Assert.That(p.ConnectTo, Is.Null);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(2));

        var broken = fail(clear(mastered(0)), 1);
        Assert.That(broken.Sections[1].ClearedAtFullSpeed, Is.False, "failing the joined section sends it back to drills");
        Assert.That(AdaptivePracticeCoach.Next(broken, sections), Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 1, 1, 11_000, 20_000, .95)));
    }

    [Test]
    public void MissesInOnePlaceTurnIntoASpotDrill()
    {
        var p = clear(progress(4), 5_000, 6_200);
        var spot = AdaptivePracticeCoach.Next(p, sections);

        Assert.That(spot.Kind, Is.EqualTo(AdaptiveDrillKind.Spot));
        Assert.That(spot.StartMs, Is.EqualTo(3_500), "a short run-up before the first miss");
        Assert.That(spot.EndMs, Is.EqualTo(7_200));
        Assert.That(spot.Speed, Is.EqualTo(1));

        p = AdaptivePracticeCoach.Complete(p, sections, spot, result(notes: 8), now);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Kind, Is.EqualTo(AdaptiveDrillKind.Spot), "one clean repeat is not enough");
        p = AdaptivePracticeCoach.Complete(p, sections, spot, result(notes: 8), now);
        Assert.That(AdaptivePracticeCoach.Next(p, sections), Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Section, 0, 0, 1_000, 10_000, 1)),
            "the learned spot goes back into the whole section");
        Assert.That(p.Sections[0].ClearedAtFullSpeed, Is.False, "spot clears do not master the section");
    }

    [Test]
    public void ScatteredMissesStayAWholeSectionDrill()
    {
        Assert.That(AdaptivePracticeCoach.Next(clear(progress(4), 2_000, 8_000), sections).Kind, Is.EqualTo(AdaptiveDrillKind.Section));
    }

    [Test]
    public void ADrillThatFellApartCountsAsAFailEvenWithoutOsuFailing()
    {
        // A short drill starts at full health, so osu! may never fail it. Missing this much is still a fail.
        var p = clear(progress(4), 5_000, 5_400, 5_800);
        Assert.That(p.Fails, Is.EqualTo(1));
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.95));
        Assert.That(p.Sections[0].Clears, Is.Zero);

        p = clear(p, 5_100, 5_500, 9_000);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.85), "repeated struggles keep slowing it down");
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Kind, Is.EqualTo(AdaptiveDrillKind.Spot), "struggling twice in the same place drills that spot");

        var nearlyAll50s = AdaptivePracticeCoach.Complete(progress(4), sections, AdaptivePracticeCoach.Next(progress(4), sections), result(accuracy: .55), now);
        Assert.That(nearlyAll50s.Fails, Is.EqualTo(1), "a run of nearly all 50s is a struggle too");

        Assert.That(AdaptivePracticeCoach.Grade(result(4, notes: 40), .15), Is.Not.EqualTo(AdaptiveGrade.Struggled), "a long section allows a few more breaks");
        Assert.That(AdaptivePracticeCoach.Grade(result(5, notes: 40), .15), Is.EqualTo(AdaptiveGrade.Struggled));

        var connect = AdaptivePracticeCoach.Next(clear(mastered(0)), sections);
        Assert.That(AdaptivePracticeCoach.Complete(clear(mastered(0)), sections, connect, result(6, missTimes: [12_000, 13_000], notes: 40), now).Fails,
            Is.Zero, "getting through a connect or full run is what counts there");
    }

    [Test]
    public void A100sNeverMakeAStruggleAndCleanAllowsThePlayersUsualShare()
    {
        // Twelve notes with four 100s is 78% accuracy, but no part of it fell apart.
        var short100s = new AdaptiveResult(0, 12, 4, .78, [], 12);
        Assert.That(AdaptivePracticeCoach.Grade(short100s, .2), Is.EqualTo(AdaptiveGrade.Shaky));
        Assert.That(AdaptivePracticeCoach.Grade(short100s with { Offbeat = 3, Accuracy = .83 }, .2), Is.EqualTo(AdaptiveGrade.Clean),
            "a player who usually hits one in five as a 100 may do so on a clean run");
        Assert.That(AdaptivePracticeCoach.Grade(short100s with { Offbeat = 1, ComboBreaks = 1 }, .4), Is.EqualTo(AdaptiveGrade.Shaky), "a miss is never clean");

        Assert.That(AdaptivePracticeCoach.Allowance(12, .2), Is.EqualTo(3));
        Assert.That(AdaptivePracticeCoach.Allowance(12, .02), Is.EqualTo(2), "at least a tenth of the notes");
        Assert.That(AdaptivePracticeCoach.Allowance(4, .1), Is.EqualTo(1), "at least one");
    }

    [Test]
    public void LeniencyComesFromThePractisedPlay()
    {
        Assert.That(AdaptivePracticeCoach.LeniencyFrom(160, 30, 10, .9), Is.EqualTo(.2).Within(1e-9));
        Assert.That(AdaptivePracticeCoach.LeniencyFrom(0, 0, 0, .86), Is.EqualTo(.21).Within(1e-9), "estimated from accuracy without hit counts");
        Assert.That(AdaptivePracticeCoach.LeniencyFrom(0, 0, 0, .3), Is.EqualTo(.4), "bounded");
        Assert.That(AdaptivePracticeCoach.Describe(new(1, 12, 2, .8, [5_000], 12), .2), Is.EqualTo("1 miss, 2 × 100/50 (3 allowed for a clean run)."));
    }

    [Test]
    public void TwoSteadyRunsBelowFullSpeedAlsoSpeedUp()
    {
        var p = fail(fail(fail(progress(4), 0, 2_000), 0, 6_000), 0, 9_000);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.70));
        p = AdaptivePracticeCoach.Complete(p, sections, AdaptivePracticeCoach.Next(p, sections), result(offbeat: 8), now);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.70), "one steady run holds the speed");
        p = AdaptivePracticeCoach.Complete(p, sections, AdaptivePracticeCoach.Next(p, sections), result(offbeat: 8), now);
        Assert.That(p.Sections[0].Speed, Is.EqualTo(.75), "two in a row speed it up");
    }

    [Test]
    public void FailingTwiceAtTheSamePlaceDrillsThatSpot()
    {
        var p = fail(progress(4), 2, 26_000);
        Assert.That(p.Sections[2].HasSpot, Is.False);
        p = fail(p, 2, 26_800);

        var spot = AdaptivePracticeCoach.Next(p, sections, start: 2);
        Assert.That(spot.Kind, Is.EqualTo(AdaptiveDrillKind.Spot));
        Assert.That(spot.StartMs, Is.EqualTo(24_500));
        Assert.That(spot.Speed, Is.EqualTo(.85), "the spot plays at the section's slowed speed");

        Assert.That(fail(fail(progress(4), 2, 22_000), 2, 29_000).Sections[2].HasSpot, Is.False, "fails in different places");
    }

    [Test]
    public void SkippingMarksThePartDoneButNeverSkipsTheFullRun()
    {
        var p = fail(fail(progress(4), 0, 4_000), 0, 4_300);
        var spot = AdaptivePracticeCoach.Next(p, sections);
        Assert.That(spot.Kind, Is.EqualTo(AdaptiveDrillKind.Spot));

        p = AdaptivePracticeCoach.Skip(p, spot, now);
        Assert.That(p.Sections[0], Has.Property("ClearedAtFullSpeed").True.And.Property("Speed").EqualTo(1).And.Property("HasSpot").False);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(1), "on to the next section");

        var connecting = clear(mastered(0));
        var connect = AdaptivePracticeCoach.Next(connecting, sections);
        Assert.That(AdaptivePracticeCoach.Next(AdaptivePracticeCoach.Skip(connecting, connect, now), sections).Focus, Is.EqualTo(2));

        var full = mastered(0, 1, 2, 3);
        var run = AdaptivePracticeCoach.Next(full, sections);
        Assert.That(AdaptivePracticeCoach.CanSkip(run), Is.False);
        Assert.That(AdaptivePracticeCoach.Skip(full, run, now), Is.SameAs(full));
        Assert.That(AdaptiveBreather.AfterSkip(spot, AdaptivePracticeCoach.Next(p, sections)).Detail, Is.EqualTo("Section 1 marked as done."));
    }

    [Test]
    public void AFullRunFromTheStartFinishesTheMap()
    {
        var p = mastered(0, 1, 2, 3);
        var run = AdaptivePracticeCoach.Next(p, sections);
        Assert.That(run, Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.FullRun, 0, 3, 1_000, 40_000, 1)));

        var failed = fail(p, 2);
        Assert.That(failed.Completed, Is.False);
        Assert.That(AdaptivePracticeCoach.Next(failed, sections).Focus, Is.EqualTo(2), "a fail in the full run drills that section");

        var done = AdaptivePracticeCoach.Complete(p, sections, run, result(1, missTimes: [12_000], notes: 80), now);
        Assert.That(done.CompletedAt, Is.EqualTo(now), "a full run without failing completes the map");
    }

    [Test]
    public void FailsBelongToTheLatestSectionOfTheDrillUnderWay()
    {
        var connect = new AdaptiveDrill(AdaptiveDrillKind.Connect, 1, 2, 11_000, 30_000, 1);
        Assert.That(AdaptivePracticeCoach.SectionAt(sections, connect, 20_500), Is.EqualTo(1), "a late miss after the section ends");
        Assert.That(AdaptivePracticeCoach.SectionAt(sections, connect, 21_000), Is.EqualTo(2));
        Assert.That(AdaptivePracticeCoach.SectionAt(sections, connect, 0), Is.EqualTo(1), "the lead-in belongs to the first section");
        Assert.That(AdaptivePracticeCoach.SectionAt(sections, connect, 39_000), Is.EqualTo(2), "never beyond the drill");
    }

    [Test]
    public void SavedProgressResumesOnlyForTheSameSplitAndTarget()
    {
        var saved = fail(progress(5), 2);
        Assert.That(AdaptivePracticeCoach.Resume(saved, "map", "Title", "Hard", 1, 5, now), Is.EqualTo(saved));
        Assert.That(AdaptivePracticeCoach.Resume(saved, "map", "Title", "Hard", 1, 6, now).Fails, Is.Zero);
        Assert.That(AdaptivePracticeCoach.Resume(saved, "map", "Title", "Hard", 1.5, 5, now).Fails, Is.Zero);
        Assert.That(AdaptivePracticeCoach.Resume(null, "map", "Title", "Hard", 1, 5, now).Sections, Has.Length.EqualTo(5));
    }

    [Test]
    public void StoreRoundTripsAndIgnoresUnreadableFiles()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-adaptive-").FullName;
        try
        {
            string path = Path.Combine(root, "adaptive.json");
            var store = new AdaptivePracticeStore(path);
            var saved = clear(fail(fail(progress(4), 3, 35_000), 3, 35_500), 2_000, 2_400) with { ConnectTo = 2 };
            store.Save(saved);
            store.Save(progress(2) with { MapKey = "other" });

            var loaded = store.Load("map");
            Assert.That(loaded?.Sections, Is.EqualTo(saved.Sections));
            Assert.That(loaded?.ConnectTo, Is.EqualTo(2));
            Assert.That(store.List(), Has.Count.EqualTo(2));

            File.WriteAllText(path, "{ not json");
            Assert.That(store.Load("map"), Is.Null);
            Assert.That(store.List(), Is.Empty);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void ProgressSavedBeforeDrillsStillLoads()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-adaptive-").FullName;
        try
        {
            // The first release saved passes instead of drills; its section speeds carry over.
            string path = Path.Combine(root, "adaptive.json");
            File.WriteAllText(path, """[{"MapKey":"map","Title":"T","Difficulty":"D","TargetRate":1,"UpdatedAt":"2026-10-02T12:00:00+00:00","Attempts":3,"Fails":2,"PassStart":1,"PassClean":false,"Sections":[{"Speed":1,"Tried":true,"ClearedAtFullSpeed":true},{"Speed":0.85,"Fails":2,"ConsecutiveFails":2,"Tried":true}]}]""");

            var loaded = new AdaptivePracticeStore(path).Load("map");
            Assert.That(loaded?.Sections[1].Speed, Is.EqualTo(.85));
            Assert.That(AdaptivePracticeCoach.Next(loaded!, sections.Take(2).ToArray()).Focus, Is.EqualTo(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void SetupKeepsDifficultyModsAndTurnsRateModsIntoTheTargetSpeed()
    {
        static LocalReplay run(params string[] mods) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Song", "Artist", "Insane",
            "osu", "Player", now, 5, .95, 1_000_000, 500, 3, null, mods, true, BeatmapHash: "ABC");

        var setup = AdaptivePracticeSetup.For(run("HD", "DT", "HR", "NF"));
        Assert.That(setup.Mods, Is.EqualTo(new[] { "HD", "HR" }));
        Assert.That(setup.TargetRate, Is.EqualTo(1.5));
        Assert.That(setup.Key, Is.EqualTo("abc|HD+HR|1.50"));
        Assert.That(AdaptivePracticeSetup.For(run("HR", "HD", "DT")).Key, Is.EqualTo(setup.Key), "mod order does not split progress");
        Assert.That(AdaptivePracticeSetup.For(run()).Key, Is.Not.EqualTo(setup.Key));
        Assert.That(AdaptivePracticeSetup.For(run("HT")).TargetRate, Is.EqualTo(.75));
    }

    [Test]
    public void SpeedModIsAcceptedByTheRulesetTrimsToTheDrillAndSharesItsPlanWithClones()
    {
        var state = progress(4, rate: 1.5);
        var plan = new AdaptiveAttemptPlan(sections, state, new AdaptiveDrill(AdaptiveDrillKind.Section, 1, 1, 11_000, 20_000, .7));
        var mod = new AdaptiveSpeedMod { Plan = plan };

        Assert.That(ModUtils.CheckModsBelongToRuleset(new OsuRuleset(), new Mod[] { mod }), Is.True);
        var clone = (AdaptiveSpeedMod)mod.DeepClone();
        Assert.That(clone.Plan, Is.SameAs(plan));

        var beatmap = new Beatmap<OsuHitObject>();
        for (int i = 0; i < 40; i++) beatmap.HitObjects.Add(new HitCircle { StartTime = 1_000 + i * 1_000 });
        clone.ApplyToBeatmap(beatmap);

        Assert.That(beatmap.HitObjects.Select(h => h.StartTime), Is.EqualTo(Enumerable.Range(11, 10).Select(s => s * 1_000d)),
            "only the drilled section is played");
        Assert.That(clone.SpeedChange.Value, Is.EqualTo(1.05).Within(.005), "the drill's speed of the target rate");

        clone.SpeedChange.Value = 1.5;
        Assert.That(clone.SpeedChange.Value, Is.EqualTo(1.5), "full DT speed is within range");
    }

    [Test]
    public void BreatherExplainsEachStepHonestly()
    {
        var first = AdaptivePracticeCoach.Next(progress(4), sections);
        var p = fail(progress(4), 0, 4_000);
        var next = AdaptivePracticeCoach.Next(p, sections);
        Assert.That(AdaptiveBreather.AfterFail(p, 0, next).Title, Is.EqualTo("Section 1 at 95%"));

        p = fail(p, 0, 4_300);
        next = AdaptivePracticeCoach.Next(p, sections);
        Assert.That(AdaptiveBreather.AfterFail(p, 0, next), Is.EqualTo(new AdaptiveBreather("Spot in section 1 at 85%",
            "Failed at the same place again. Drilling just that part.")));

        var cleaned = clear(progress(4));
        var connect = AdaptivePracticeCoach.Next(clear(mastered(0)), sections);
        Assert.That(AdaptiveBreather.AfterClear(first, cleaned, AdaptivePracticeCoach.Next(cleaned, sections), false).Detail,
            Is.EqualTo("Section 1 is done. On to section 2."));
        Assert.That(AdaptiveBreather.AfterClear(first, clear(mastered(0)), connect, false).Title, Is.EqualTo("Connect sections 1 and 2"));
        Assert.That(AdaptiveBreather.AfterClear(first, cleaned, first, true).Finished, Is.True);
        Assert.That(AdaptiveBreather.Label(connect, 4), Is.EqualTo("Sections 1–2"));
    }
}
