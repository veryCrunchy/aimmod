using AimMod.Desktop.LocalLibrary;
using System.Runtime.CompilerServices;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetLearningForecast(double FirstTryPp, double TargetPp, int LikelyTries,
    int SupportedTries, double ReachProbability, int Sessions, int Maps, int PreviousTries,
    PpTargetRange FirstTryRange, PpTargetConfidence Confidence);

/// <summary>
/// Empirical retry trajectories, not an independent-roll success formula. Failed attempts
/// remain zero; chronological score changes retain learning, fatigue and correlated retries.
/// PP ratios transfer between maps relative to each map's typical completed score, using
/// the target's ruleset-calculated projected PP as the scale. These are estimates, not exact PP.
/// </summary>
public static class PpTargetLearningModel
{
    private sealed record Session(string Map, string Setup, bool LegacyScore, DateTimeOffset End, double Stars,
        double Bpm, int Seconds, PpPatternFeatures Features, double[] Ratios);
    private sealed record Weighted(Session Session, double Weight);
    private static readonly ConditionalWeakTable<PpPatternProfile, ConditionalWeakTable<PpTargetOpportunityProfile, Session[]>> cache = new();

    public static PpTargetLearningForecast? Predict(PpTargetOpportunityProfile? history, PpPatternProfile? patterns,
        PpTargetEstimate? estimate, int beatmapId, double stars, double bpm, int seconds,
        IReadOnlyList<string> mods, string? modsJson = null, bool legacyScore = false)
    {
        if (history is null || patterns is null || estimate?.Features is not { } features
            || !double.IsFinite(estimate.ExpectedPp) || estimate.ExpectedPp <= 0
            || !double.IsFinite(estimate.RealisticMaximumPp) || estimate.RealisticMaximumPp <= 0
            || !double.IsFinite(stars) || stars <= 0 || !double.IsFinite(bpm) || bpm <= 0 || seconds <= 0)
            return null;
        if (PpTargetMods.Normalise(mods).Any(m => m is "NF" or "RX" or "AP" or "AT" or "CN" or "SD" or "PF")) return null;
        string setup = configuration(mods, modsJson);
        var sessions = cache.GetValue(patterns, _ => new()).GetValue(history, h => build(h, patterns));
        // A target already practised in this session starts at its next observed attempt.
        var direct = history.RecentAttempts.Where(a => a.LocalAttempt && a.LegacyScore == legacyScore && a.BeatmapId == beatmapId && beatmapId > 0
                && a.PlayedAt <= history.AsOf && configuration(a.Mods.Split(','), a.ModsJson) == setup)
            .GroupBy(a => (a.LocalScoreId, a.PlayedAt)).Select(g => g.First())
            .OrderByDescending(a => a.PlayedAt).ToArray();
        int previous = 0;
        DateTimeOffset last = history.AsOf;
        foreach (var attempt in direct)
        {
            if (last - attempt.PlayedAt > TimeSpan.FromHours(6)) break;
            previous++; last = attempt.PlayedAt;
        }
        if (previous >= 20) return null;
        var nearby = sessions.Where(s => s.Map != $"online:{beatmapId}" && s.LegacyScore == legacyScore && s.Setup == setup && Math.Abs(s.Stars - stars) <= .75
                && Math.Abs(Math.Log(s.Bpm / bpm)) <= Math.Log(1.25)
                && Math.Abs(Math.Log((double)s.Seconds / seconds)) <= Math.Log(1.6))
            .Select(s => new Weighted(s, similarity(features, s.Features)
                * Math.Exp(-Math.Pow((s.Stars - stars) / .5, 2))
                * Math.Pow(.5, Math.Max(0, (history.AsOf - s.End).TotalDays) / 14)))
            .Where(s => s.Weight >= .1 && s.Session.Ratios.Length > previous).ToArray();
        // Keep early successes AND early abandonments. Restricting the cohort to long
        // retry sessions selects for persistence and biases even the first-attempt PP.
        // Later bests carry forward the last observed best after a session ends: a
        // conservative observed result, not invented improvement on unplayed attempts.
        int horizon = Math.Min(20 - previous, nearby.Select(s => s.Session.Ratios.Length - previous).DefaultIfEmpty(0).Max());
        for (; horizon >= 2; horizon--)
        {
            var observed = nearby.Where(s => s.Session.Ratios.Length >= previous + horizon).ToArray();
            if (observed.Length >= 5 && observed.Select(s => s.Session.Map).Distinct().Count() >= 3) break;
        }
        if (horizon < 2) return null;
        Weighted[] cohort = nearby.GroupBy(s => s.Session.Map).SelectMany(g =>
        {
            double total = g.Sum(s => s.Weight);
            return g.Select(s => s with { Weight = s.Weight / Math.Max(1, total) });
        }).ToArray();
        double weight = cohort.Sum(s => s.Weight);
        if (weight < 1.5) return null;
        double pp(double ratio) => Math.Clamp(ratio * estimate.ExpectedPp, 0, estimate.RealisticMaximumPp);
        var first = cohort.Select(s => (Value: pp(s.Session.Ratios[previous]), s.Weight)).ToArray();
        var best = cohort.Select(s => (Value: pp(s.Session.Ratios.Skip(previous).Take(horizon).Max()), s.Weight)).ToArray();
        double target = Math.Floor(quantile(best, .5));
        if (target <= 0) return null;
        int tries = 1;
        double probability = 0;
        for (; tries <= horizon; tries++)
        {
            probability = cohort.Where(s => pp(s.Session.Ratios.Skip(previous).Take(tries).Max()) >= target).Sum(s => s.Weight) / weight;
            if (probability >= .5) break;
        }
        return new(first.Sum(s => s.Value * s.Weight) / weight, target, Math.Min(tries, horizon), horizon,
            probability, cohort.Length, cohort.Select(s => s.Session.Map).Distinct().Count(), previous,
            new(quantile(first, .2), quantile(first, .8)), PpTargetConfidence.Low);
    }

    private static Session[] build(PpTargetOpportunityProfile history, PpPatternProfile patterns)
    {
        var evidence = patterns.Evidence.GroupBy(e => e.ScoreId).ToDictionary(g => g.Key, g => g.First());
        // Online best/recent APIs do not provide complete retry sequences. Local known
        // outcomes alone train this model; missing PP on a pass is unknown, never zero.
        var attempts = history.RecentAttempts.Where(a => a.LocalAttempt && a.LocalScoreId is not null
                && a.PlayedAt <= history.AsOf && a.PlayedAt >= history.AsOf.AddDays(-30))
            .GroupBy(a => a.LocalScoreId).Select(g => g.First());
        var result = new List<Session>();
        foreach (var group in attempts.GroupBy(a => (Map: a.BeatmapId > 0 ? $"online:{a.BeatmapId}" : $"local:{a.LocalBeatmapId}",
                     Setup: configuration(a.Mods.Split(','), a.ModsJson), a.LegacyScore)))
        {
            var ordered = group.OrderBy(a => a.PlayedAt).ToArray();
            if (ordered.Any(a => a.Passed && (a.Pp is null || !double.IsFinite(a.Pp.Value) || a.Pp < 0))) continue;
            var shapes = ordered.Where(a => evidence.ContainsKey(a.LocalScoreId!.Value))
                .Select(a => evidence[a.LocalScoreId!.Value]).Where(e => e.PlayedAt <= history.AsOf && e.Features.PointCount >= 20).ToArray();
            if (shapes.Length == 0 || shapes.Select(e => e.MapKey).Distinct().Count() != 1) continue;
            var shape = shapes.MaxBy(e => e.Features.PointCount)!.Features;
            var sample = ordered.FirstOrDefault(a => double.IsFinite(a.Stars) && a.Stars > 0 && a.Bpm is > 0 && double.IsFinite(a.Bpm.Value) && a.LengthSeconds is > 0);
            if (sample is null) continue;
            double normal = ordered.Where(a => a.Passed).Select(a => a.Pp!.Value).DefaultIfEmpty(0).Average();
            var current = new List<PpTargetPassSample>();
            void finish()
            {
                if (current.Count > 0 && history.AsOf - current[^1].PlayedAt >= TimeSpan.FromHours(6)) result.Add(new(group.Key.Map, group.Key.Setup, group.Key.LegacyScore, current[^1].PlayedAt,
                    sample.Stars, sample.Bpm!.Value, sample.LengthSeconds!.Value, shape,
                    current.Select(a => a.Passed && normal > 0 ? a.Pp!.Value / normal : 0).ToArray()));
                current.Clear();
            }
            foreach (var attempt in ordered)
            {
                if (current.Count > 0 && attempt.PlayedAt - current[^1].PlayedAt > TimeSpan.FromHours(6)) finish();
                current.Add(attempt);
            }
            finish();
        }
        return result.ToArray();
    }

    private static string configuration(IEnumerable<string> mods, string? json) =>
        ScoreMods.Configuration(mods.ToArray(), json ?? "", PpTargetMods.NormaliseForSkill);

    private static double similarity(PpPatternFeatures a, PpPatternFeatures b)
    {
        double distance = 0;
        foreach (var pair in new[] { (a.JumpFraction, b.JumpFraction), (a.StreamFraction, b.StreamFraction), (a.BurstFraction, b.BurstFraction), (a.SharpTurnFraction, b.SharpTurnFraction) })
        {
            if (pair.Item1 is not { } x || pair.Item2 is not { } y || !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1) return 0;
            distance += Math.Pow((x - y) / .25, 2);
        }
        if (a.NotesPerSecond is not > 0 || b.NotesPerSecond is not > 0) return 0;
        distance += Math.Pow(Math.Log(a.NotesPerSecond.Value / b.NotesPerSecond.Value) / .3, 2);
        if (a.JumpFraction > .1)
        {
            if (a.JumpDistance is not > 0 || b.JumpDistance is not > 0) return 0;
            distance += Math.Pow(Math.Log(a.JumpDistance.Value / b.JumpDistance.Value) / .35, 2);
        }
        return Math.Exp(-distance);
    }

    private static double quantile((double Value, double Weight)[] samples, double fraction)
    {
        double threshold = samples.Sum(s => s.Weight) * fraction, total = 0;
        foreach (var item in samples.OrderBy(s => s.Value)) { total += item.Weight; if (total >= threshold) return item.Value; }
        return samples.Max(s => s.Value);
    }
}
