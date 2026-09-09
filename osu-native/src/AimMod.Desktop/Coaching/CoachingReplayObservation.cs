using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public sealed record CoachingReplayObservation(string Label, string Detail, string Cue,
    int FirstObjectIndex, double TimeMs, int Notes, int Plays, IReadOnlyList<Guid> ScoreIds);

public static class CoachingReplayObservations
{
    public static IReadOnlyList<CoachingReplayObservation> Build(LocalReplay selected,
        IEnumerable<LocalReplay> history, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        if (!ScoreMods.IsManualPlay(selected) || !analyses.TryGetValue(selected.ScoreId, out var analysis)
            || analysis.EngineVersion != ReplayAnalysisProtocol.EngineVersion) return [];
        var runs = history.Append(selected).Where(r => ScoreMods.IsManualPlay(r)
                && string.Equals(r.Player, selected.Player, StringComparison.OrdinalIgnoreCase)
                && ScoreMods.SetupKey(r) == ScoreMods.SetupKey(selected)
                && r.PlayedAt <= DateTimeOffset.UtcNow)
            .DistinctBy(r => r.ScoreId).OrderByDescending(r => r.PlayedAt).Take(100)
            .Where(r => analyses.TryGetValue(r.ScoreId, out var a) && a.EngineVersion == ReplayAnalysisProtocol.EngineVersion).ToArray();
        var observations = new List<CoachingReplayObservation>();
        if (TappingCoaching.Build(analysis) is { } tapping)
        {
            var matching = runs.Where(r => TappingCoaching.Build(analyses[r.ScoreId]) is { } other
                && other.FirstObjectIndex == tapping.FirstObjectIndex && other.Pattern == tapping.Pattern).ToArray();
            observations.Add(new(tapping.Pattern, tapping.Observation, tapping.Cue, tapping.FirstObjectIndex,
                tapping.StartTimeMs, 8, matching.Length, matching.Select(r => r.ScoreId).ToArray()));
        }
        var misses = analysis.Judgements.Where(validMiss).ToArray();
        foreach (var group in misses.GroupBy(j => category(j.MissAnalysis!)).Where(g => g.Key is not null)
                     .OrderByDescending(g => g.Count()).Take(3))
        {
            var first = group.First();
            var indices = group.Select(j => j.ObjectIndex).ToHashSet();
            var matching = runs.Where(r => analyses[r.ScoreId].Judgements.Any(j => validMiss(j) && indices.Contains(j.ObjectIndex)
                && category(j.MissAnalysis!) == group.Key)).ToArray();
            var (label, cue) = group.Key switch
            {
                "outside" => ("Cursor outside at the press", "Compare this rhythm with reduced movement, then add the original spacing."),
                "timing" => ("Press outside the timing window", "Practise the rhythm with less movement, then check the original section."),
                _ => ("Reached the circle without a matching press", "Check the replay, then practise the movement and tapping together.")
            };
            observations.Add(new(label, $"{group.Count()} missed notes in this play. The same observation appears in {matching.Length} comparable plays.",
                cue, first.ObjectIndex!.Value, first.StartTimeMs, group.Count(), matching.Length, matching.Select(r => r.ScoreId).ToArray()));
        }
        return observations.Take(3).ToArray();
    }

    private static bool validMiss(ReplayObjectJudgement j) => j.Result == "Miss" && j.ObjectIndex is >= 0
        && string.IsNullOrEmpty(j.NestedPath) && double.IsFinite(j.StartTimeMs) && j.StartTimeMs >= 0
        && j.MissAnalysis is { Confidence: >= .7, HitRadius: > 0 } m && double.IsFinite(m.HitRadius);

    private static string? category(ReplayMissAnalysis m)
    {
        if (m.DistanceAtPress is { } distance && double.IsFinite(distance) && double.IsFinite(m.HitRadius)
            && m.HitRadius > 0 && distance > m.HitRadius) return "outside";
        if (m.Reason is ReplayMissReason.EarlyClick or ReplayMissReason.LateClick
            && m.PressTimeOffsetMs is { } offset && double.IsFinite(offset)) return "timing";
        return m.Reason == ReplayMissReason.OnTargetNoClick ? "no-press" : null;
    }
}
