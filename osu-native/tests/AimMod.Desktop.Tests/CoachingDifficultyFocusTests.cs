using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

/// <summary>Practice from the beatmap browser coaches the selected difficulty, not whichever difficulties of the set have evidence.</summary>
public sealed class CoachingDifficultyFocusTests
{
    private static readonly DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly LocalBeatmapDifficulty extra = new(Guid.NewGuid(), 4_001, "Synthetic Extra", "osu", 5.05, 185, 33_000, 4, 9, 8, 5, 87, "hash-extra");
    private static readonly LocalBeatmapDifficulty expert = new(Guid.NewGuid(), 4_002, "Synthetic Expert", "osu", 5.01, 185, 33_000, 4, 9, 8, 5, 7, "hash-expert");

    private static LocalReplay play(LocalBeatmapDifficulty difficulty, double hoursAgo, bool passed = true, string[]? mods = null, string? hash = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), difficulty.BeatmapId, "Synthetic Song", "Synthetic Artist", difficulty.Name, "osu", "SyntheticPlayer",
            now.AddHours(-hoursAgo), difficulty.StarRating, .97, 900_000, 400, 2, null, mods ?? [], true, hash ?? difficulty.BeatmapHash, Passed: passed);

    [Test]
    public void TheNewestPassedPlayOnThatDifficultyIsChosen()
    {
        var older = play(extra, 5);
        var newer = play(extra, 1);
        var history = new[] { play(expert, 0.1), older, newer, play(extra, 0.5, passed: false) };
        Assert.That(NativeCoachingWorkspace.CoachingRunFor(history, extra), Is.SameAs(newer));
    }

    [Test]
    public void AFailedPlayIsUsedWhenTheDifficultyWasNeverPassed()
    {
        var failed = play(extra, 2, passed: false);
        Assert.That(NativeCoachingWorkspace.CoachingRunFor([play(expert, 1), failed], extra), Is.SameAs(failed));
    }

    [Test]
    public void AutoplayAndOtherDifficultiesNeverCount()
    {
        Assert.That(NativeCoachingWorkspace.CoachingRunFor([play(extra, 1, mods: ["AT"]), play(expert, 1)], extra), Is.Null);
        // A play recorded before the map was updated still belongs to the same installed difficulty by its id.
        var updated = play(extra, 3, hash: "hash-before-update");
        Assert.That(NativeCoachingWorkspace.CoachingRunFor([updated], extra), Is.SameAs(updated));
    }
}
