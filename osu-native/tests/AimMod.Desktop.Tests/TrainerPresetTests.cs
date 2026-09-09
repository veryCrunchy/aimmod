using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerPresetTests
{
    [Test]
    public void EverySkillHasValidDistinctPresetsThatPreserveThePlayersSetup()
    {
        foreach (var kind in Enum.GetValues<TrainerKind>())
        {
            var current = new TrainerSettings(Kind: kind, Bpm: 150, Keys: "D / F", OffsetMs: 31,
                Music: "song", SongIdentity: "fixture-song", SongTitle: "Fixture", SongStartSeconds: 30);
            var presets = TrainerPresets.For(kind);
            Assert.That(presets.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(presets.Select(p => p.Apply(current)).Distinct().Count(), Is.EqualTo(presets.Count));
            foreach (var preset in presets)
            {
                var selected = preset.Apply(current);
                Assert.DoesNotThrow(selected.Validate, $"{kind}: {preset.Title}");
                Assert.Multiple(() =>
                {
                    Assert.That((selected.Kind, selected.Bpm, selected.Keys, selected.OffsetMs),
                        Is.EqualTo((kind, 150, "D / F", 31)));
                    Assert.That((selected.Music, selected.SongIdentity, selected.SongTitle, selected.SongStartSeconds),
                        Is.EqualTo(("song", "fixture-song", "Fixture", 30)));
                    Assert.That(preset.Matches(selected), Is.True);
                    Assert.That(preset.Matches(selected with { CircleSize = 6 }), Is.False);
                });
            }
        }
    }

    [Test]
    public void ComparisonNeedsSixDistinctMatchingRunsAndExcludesAssistedOtherSetupsAndFutureRuns()
    {
        var runs = Enumerable.Range(0, 6).Select(i => run(i)).ToArray();
        Assert.That(TrainerProgressComparison.Build(runs[4], runs), Is.Null);
        var noise = new[] { run(10), run(1) with { Assisted = true }, run(2) with { Engine = "cue" },
            run(3) with { Settings = new(Bpm: 180) }, runs[0] };
        var result = TrainerProgressComparison.Build(runs[5], runs.Concat(noise))!;
        Assert.Multiple(() =>
        {
            Assert.That(result.FirstAccuracy, Is.EqualTo(91));
            Assert.That(result.LatestAccuracy, Is.EqualTo(94));
            Assert.That(result.FirstSpread, Is.EqualTo(19));
            Assert.That(result.LatestSpread, Is.EqualTo(16));
        });
    }

    private static TrainerResult run(int i) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(i),
        new(), 100, 98, 90, 0, 0, 0, 20 - i, 0, Engine: "osu-moving-v2", Accuracy: 90 + i);
}
