using AimMod.Desktop.Home;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Trainers;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class HomeSkillsetsTests
{
    private static readonly IReadOnlyDictionary<ReplayMissReason, int> noReasons = new Dictionary<ReplayMissReason, int>();

    private static PpPatternEvidence play(DateTimeOffset at, int notes, IReadOnlyDictionary<string, (int Notes, double Accuracy, double Misses)> patterns,
        IReadOnlyDictionary<string, int> exposure, double weight = 1)
    {
        var outcomes = patterns.ToDictionary(p => p.Key, p => new PpPatternOutcome(p.Value.Notes, p.Value.Accuracy, p.Value.Misses, noReasons, 0, 9 + 100 * p.Value.Misses));
        return new PpPatternEvidence(Guid.NewGuid(), $"map-{Guid.NewGuid():N}", "", at,
            new PpPatternFeatures { PointCount = notes, PatternObjectCounts = exposure }, weight, outcomes);
    }

    /// <summary>
    /// An aim-first player: most notes are jumps and their accuracy there is above average; streams are rarer and
    /// clearly below average.
    /// </summary>
    internal static PpPatternProfile AimPlayer(DateTimeOffset now, int plays = 12)
    {
        var evidence = Enumerable.Range(0, plays).Select(i => play(now.AddDays(-i * 2), 500,
            new Dictionary<string, (int, double, double)>
            {
                ["Overall"] = (500, .955, .012),
                ["Jumps"] = (260, .972, .006),
                ["Streams"] = (90, .884, .041),
                ["Bursts"] = (60, .93, .02),
                ["Direction changes"] = (140, .962, .009),
                ["Speed"] = (170, .905, .03),
            },
            new Dictionary<string, int> { ["Jumps"] = 230, ["Streams"] = 90, ["Bursts"] = 60, ["Direction changes"] = 40, ["Speed"] = 20, ["Overall"] = 60 },
            weight: Math.Pow(.5, i * 2 / 15.0))).ToArray();
        return new PpPatternProfile("synthetic-aim-player", now, 30, evidence);
    }

    [Test]
    public void AimPlayerIsStrongestOnJumpsAndWeakestOnStreams()
    {
        var skills = HomeSkillsetBuilder.Build(AimPlayer(DateTimeOffset.UtcNow))!;
        Assert.That((skills.Plays, skills.Days, skills.HasComparison), Is.EqualTo((12, 30, true)));
        Assert.That(skills.Strongest!.Pattern, Is.EqualTo("Jumps"));
        Assert.That(skills.Weakest!.Pattern, Is.EqualTo("Streams"));
        Assert.That(skills.MostPlayed!.Pattern, Is.EqualTo("Jumps"));
        Assert.That(skills.Accuracy, Is.EqualTo(.955).Within(1e-9));
        var streams = skills.Skills.Single(s => s.Pattern == "Streams");
        Assert.That(streams.AccuracyVsAverage, Is.EqualTo((.884 - .955) * 100).Within(1e-6));
        Assert.That(streams.MissesPer100, Is.EqualTo(4.1).Within(1e-6));
        Assert.That(streams.UnstableRate, Is.EqualTo(10 * (9 + 4.1)).Within(1e-6));
        Assert.That(streams.PlayShare, Is.EqualTo(90 / 500.0).Within(1e-9));
        Assert.That(streams.Trainer, Is.EqualTo(TrainerKind.Alternating));
        Assert.That(skills.Skills.Single(s => s.Pattern == "Bursts").Trainer, Is.EqualTo(TrainerKind.Bursts));
        Assert.That(skills.Skills.Single(s => s.Pattern == "Jumps").Trainer, Is.EqualTo(TrainerKind.Aim));
        Assert.That(NativeHomeDashboard.HomeSkillsetPanel.versus(streams), Is.EqualTo("-7.1 points vs your average"));
    }

    [Test]
    public void PatternsComparedWithTheSamePlaysAverage()
    {
        // Streams only appear on harder maps, where the player's overall accuracy is lower too.
        var now = DateTimeOffset.UtcNow;
        var easy = Enumerable.Range(0, 4).Select(_ => play(now, 300, new Dictionary<string, (int, double, double)>
            { ["Overall"] = (300, .98, 0), ["Jumps"] = (200, .98, 0) }, new Dictionary<string, int> { ["Jumps"] = 200 }));
        var hard = Enumerable.Range(0, 4).Select(_ => play(now, 300, new Dictionary<string, (int, double, double)>
            { ["Overall"] = (300, .90, .02), ["Streams"] = (100, .90, .02), ["Jumps"] = (150, .90, .02) }, new Dictionary<string, int> { ["Streams"] = 100, ["Jumps"] = 150 }));
        var skills = HomeSkillsetBuilder.Build(new PpPatternProfile("mixed", now, 30, easy.Concat(hard).ToArray()))!;
        Assert.That(skills.Skills.Single(s => s.Pattern == "Streams").AccuracyVsAverage, Is.EqualTo(0).Within(1e-9),
            "Streams match the player's accuracy on the maps that contain them.");
    }

    [Test]
    public void ThinPatternsAreNotCalledStrongOrWeak()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = Enumerable.Range(0, 2).Select(_ => play(now, 300, new Dictionary<string, (int, double, double)>
            { ["Overall"] = (300, .95, .01), ["Streams"] = (100, .80, .1), ["Jumps"] = (150, .97, 0) }, new Dictionary<string, int> { ["Streams"] = 100, ["Jumps"] = 150 })).ToArray();
        var skills = HomeSkillsetBuilder.Build(new PpPatternProfile("thin", now, 30, evidence))!;
        Assert.That(skills.Skills.All(s => !s.Enough), Is.True);
        Assert.That((skills.HasComparison, skills.Strongest, skills.Weakest), Is.EqualTo((false, (HomeSkill?)null, (HomeSkill?)null)));
        Assert.That(skills.MostPlayed!.Pattern, Is.EqualTo("Jumps"));
        Assert.That(HomeSkillsetBuilder.Build(null), Is.Null);
        Assert.That(HomeSkillsetBuilder.Build(new PpPatternProfile("empty", now, 30, [])), Is.Null);
    }

    [Test]
    public void HomeDataCarriesSkillsets()
    {
        var now = DateTimeOffset.UtcNow;
        var inputs = new HomeDashboardInputs([], null, null, null, null, [], null) { Patterns = AimPlayer(now) };
        Assert.That(HomeDashboardBuilder.Build(inputs, now).Skillsets!.Weakest!.Label, Is.EqualTo("Streams"));
        Assert.That(HomeDashboardBuilder.Build(inputs with { Patterns = null }, now).Skillsets, Is.Null);
    }
}
