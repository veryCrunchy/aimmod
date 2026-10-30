using AimMod.Desktop.PpTargets;
using AimMod.Desktop.Trainers;

namespace AimMod.Desktop.Home;

/// <summary>
/// One pattern class in the player's recent analysed plays: how much of what they play it makes up, and how their
/// accuracy on it compares with their accuracy on every note of the same plays.
/// </summary>
/// <param name="PlayShare">Share of recent notes that belong to this pattern first (streams before bursts before jumps).</param>
/// <param name="AccuracyVsAverage">Accuracy on this pattern minus accuracy on all notes of the same plays, in percentage points.</param>
internal sealed record HomeSkill(string Pattern, string Label, string Description, double PlayShare, int Plays, int Notes,
    double? Accuracy, double? AccuracyVsAverage, double? MissesPer100, double? UnstableRate, bool Enough)
{
    /// <summary>The trainer that practises this pattern on its own.</summary>
    public TrainerKind Trainer => Pattern switch
    {
        "Streams" or "Speed" => TrainerKind.Alternating,
        "Bursts" => TrainerKind.Bursts,
        _ => TrainerKind.Aim,
    };
}

internal sealed record HomeSkillsets(int Plays, int Days, double? Accuracy, double? UnstableRate, IReadOnlyList<HomeSkill> Skills)
{
    public HomeSkill? Strongest => Skills.Where(s => s.Enough).MaxBy(s => s.AccuracyVsAverage);

    public HomeSkill? Weakest => Skills.Where(s => s.Enough && s != Strongest).MinBy(s => s.AccuracyVsAverage);

    public HomeSkill? MostPlayed => Skills.Where(s => s.PlayShare > 0).MaxBy(s => s.PlayShare);

    public bool HasComparison => Skills.Count(s => s.Enough) >= 2;
}

internal static class HomeSkillsetBuilder
{
    /// <summary>A pattern needs this many plays and notes before it is called strong or weak.</summary>
    public const int MinimumPlays = 3, MinimumNotes = 60, MinimumNotesPerPlay = 5;

    private static readonly (string Pattern, string Label, string Description)[] patterns =
    [
        ("Jumps", "Jumps", "Wide spacing between notes"),
        ("Streams", "Streams", "8 or more fast notes in a row"),
        ("Bursts", "Bursts", "3 to 7 fast notes in a row"),
        ("Direction changes", "Sharp angles", "Changes of direction of 60° or more"),
        ("Speed", "Speed", "Notes 200 ms apart or closer"),
    ];

    /// <summary>Recency- and map-balanced pooling of the profile's replay evidence. Null without analysed plays.</summary>
    public static HomeSkillsets? Build(PpPatternProfile? profile)
    {
        var evidence = profile?.Evidence.Where(e => e.Weight > 0 && double.IsFinite(e.Weight)
            && e.Outcomes.TryGetValue("Overall", out var overall) && overall.ObjectCount > 0).ToArray() ?? [];
        if (evidence.Length == 0) return null;

        double totalNotes = evidence.Sum(e => e.Weight * e.Features.PointCount);
        var overall = pool(evidence, "Overall");
        var skills = patterns.Select(p =>
        {
            var plays = evidence.Where(e => e.Outcomes.TryGetValue(p.Pattern, out var o) && o.ObjectCount >= MinimumNotesPerPlay).ToArray();
            var pooled = pool(plays, p.Pattern);
            // Overall accuracy of the same plays, so a pattern is compared with the player's own average on those maps.
            var baseline = pool(plays, "Overall");
            double share = totalNotes <= 0 ? 0 : evidence.Sum(e => e.Weight * (e.Features.PatternObjectCounts?.GetValueOrDefault(p.Pattern) ?? 0)) / totalNotes;
            int notes = plays.Sum(e => e.Outcomes[p.Pattern].ObjectCount);
            return new HomeSkill(p.Pattern, p.Label, p.Description, share, plays.Length, notes, pooled.Accuracy,
                pooled.Accuracy - baseline.Accuracy is { } gap ? gap * 100 : null,
                pooled.MissRate * 100, pooled.UnstableRate, plays.Length >= MinimumPlays && notes >= MinimumNotes);
        }).ToArray();
        return new HomeSkillsets(evidence.Length, profile!.RecencyDays, overall.Accuracy, overall.UnstableRate, skills);
    }

    private static (double? Accuracy, double? MissRate, double? UnstableRate) pool(IReadOnlyList<PpPatternEvidence> plays, string pattern)
    {
        double weight = 0, accuracy = 0, misses = 0, urWeight = 0, ur = 0;
        foreach (var play in plays)
        {
            if (!play.Outcomes.TryGetValue(pattern, out var outcome) || outcome.ObjectCount <= 0) continue;
            double w = play.Weight * outcome.ObjectCount;
            weight += w; accuracy += w * outcome.Accuracy; misses += w * outcome.MissRate;
            if (outcome.UnstableRate is { } value && double.IsFinite(value)) { urWeight += w; ur += w * value; }
        }
        return weight <= 0 ? (null, null, null) : (accuracy / weight, misses / weight, urWeight <= 0 ? null : ur / urWeight);
    }
}
