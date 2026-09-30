using AimMod.Desktop.LocalLibrary;
using System.Runtime.CompilerServices;

namespace AimMod.Desktop.PpTargets;

/// <param name="PassRatios">For the next attempts, the passed score's PP relative to that map's mean passed PP, shrunk towards one.</param>
public sealed record PpTargetLearningForecast(IReadOnlyList<double> PassRatios, int SupportedTries, int Sessions, int Maps,
    int PreviousTries, PpTargetConfidence Confidence)
{
    public double Ratio(int attempt) => PassRatios.Count == 0 ? 1 : PassRatios[Math.Clamp(attempt, 0, PassRatios.Count - 1)];

    public double MeanRatio(int attempts) => Enumerable.Range(0, Math.Max(1, attempts)).Average(Ratio);
}

/// <summary>
/// Empirical retry trajectories on similar maps. Every ratio shares one passes-only denominator
/// (the map's mean passed PP), matching the PP-if-pass scale it adjusts; pass frequency is
/// modelled separately, so failed attempts are excluded here rather than counted as zero.
/// </summary>
public static class PpTargetLearningModel
{
    public const int ForecastAttempts = 5;
    private const double shrinkage = 2;
    private sealed record Session(string Map, string Setup, bool LegacyScore, DateTimeOffset End, double Stars,
        double Bpm, int Seconds, PpPatternFeatures Features, double?[] Ratios, bool Open);
    private sealed record Weighted(Session Session, double Weight);
    private static readonly ConditionalWeakTable<PpPatternProfile, ConditionalWeakTable<PpTargetOpportunityProfile, Session[]>> cache = new();

    public static PpTargetLearningForecast? Predict(PpTargetOpportunityProfile? history, PpPatternProfile? patterns,
        PpTargetEstimate? estimate, int beatmapId, double stars, double bpm, int seconds,
        IReadOnlyList<string> mods, string? modsJson = null, bool legacyScore = false)
    {
        if (history is null || patterns is null || estimate?.Features is null) return null;
        return new Predictor(history, patterns, mods, modsJson, legacyScore).Predict(estimate, beatmapId, stars, bpm, seconds);
    }

    // Predict for a fixed history and mod setup, so a ranking resolves both once.
    internal sealed class Predictor(PpTargetOpportunityProfile? history, PpPatternProfile? patterns,
        IReadOnlyList<string> mods, string? modsJson, bool legacyScore)
    {
        private readonly bool unsupported = PpTargetMods.Normalise(mods).Any(m => m is "NF" or "RX" or "AP" or "AT" or "CN" or "SD" or "PF");
        private readonly string setup = configuration(mods, modsJson);
        private Session[]? sessions;
        private Dictionary<int, PpTargetPassSample[]>? direct;

        public PpTargetLearningForecast? Predict(PpTargetEstimate? estimate, int beatmapId, double stars, double bpm, int seconds)
        {
            if (history is null || patterns is null || estimate?.Features is not { } features
                || !double.IsFinite(stars) || stars <= 0 || !double.IsFinite(bpm) || bpm <= 0 || seconds <= 0)
                return null;
            if (unsupported) return null;
            sessions ??= cache.GetValue(patterns, _ => new()).GetValue(history, h => build(h, patterns));
            direct ??= directAttempts(history);
            return predict(history, sessions, beatmapId > 0 ? direct.GetValueOrDefault(beatmapId) ?? [] : [],
                setup, features, beatmapId, stars, bpm, seconds, legacyScore);
        }

        private Dictionary<int, PpTargetPassSample[]> directAttempts(PpTargetOpportunityProfile source)
        {
            var setups = new Dictionary<(string, string), string>();
            return source.RecentAttempts.Where(a => a.LocalAttempt && a.LegacyScore == legacyScore && a.BeatmapId > 0
                    && a.PlayedAt <= source.AsOf && configurationOf(a) == setup)
                .GroupBy(a => a.BeatmapId).ToDictionary(g => g.Key, g => g.ToArray());

            string configurationOf(PpTargetPassSample attempt)
            {
                if (!setups.TryGetValue((attempt.Mods, attempt.ModsJson), out string? value))
                    setups[(attempt.Mods, attempt.ModsJson)] = value = configuration(attempt.Mods.Split(','), attempt.ModsJson);
                return value;
            }
        }
    }

    private static PpTargetLearningForecast? predict(PpTargetOpportunityProfile history, Session[] sessions, PpTargetPassSample[] attempts,
        string setup, PpPatternFeatures features, int beatmapId, double stars, double bpm, int seconds, bool legacyScore)
    {
        // A target already practised in this session starts at its next observed attempt.
        var direct = attempts.GroupBy(a => (a.LocalScoreId, a.PlayedAt)).Select(g => g.First())
            .OrderByDescending(a => a.PlayedAt).ToArray();
        int previous = 0;
        DateTimeOffset last = history.AsOf;
        foreach (var attempt in direct)
        {
            if (last - attempt.PlayedAt > TimeSpan.FromHours(6)) break;
            previous++; last = attempt.PlayedAt;
        }
        previous = Math.Min(previous, 19);
        var nearby = sessions.Where(s => s.Map != $"online:{beatmapId}" && s.LegacyScore == legacyScore && s.Setup == setup && Math.Abs(s.Stars - stars) <= .75
                && Math.Abs(Math.Log(s.Bpm / bpm)) <= Math.Log(1.25)
                && Math.Abs(Math.Log((double)s.Seconds / seconds)) <= Math.Log(1.6))
            .Select(s => new Weighted(s, similarity(features, s.Features)
                * Math.Exp(-Math.Pow((s.Stars - stars) / .5, 2))
                * Math.Pow(.5, Math.Max(0, (history.AsOf - s.End).TotalDays) / 14)
                // A session that may still be running has not shown its full trajectory yet.
                * (s.Open ? .6 : 1)))
            .Where(s => s.Weight >= .05 && s.Session.Ratios.Length > previous).ToArray();
        Weighted[] cohort = nearby.GroupBy(s => s.Session.Map).SelectMany(g =>
        {
            double total = g.Sum(s => s.Weight);
            return g.Select(s => s with { Weight = s.Weight / Math.Max(1, total) });
        }).ToArray();
        int maps = cohort.Select(s => s.Session.Map).Distinct().Count();
        bool supported = maps >= 3 && cohort.Sum(s => s.Weight) >= 1.5;
        if (!supported)
            return previous > 0 ? new([], 0, 0, 0, previous, PpTargetConfidence.Insufficient) : null;
        var ratios = new double[ForecastAttempts];
        double carried = 1;
        for (int i = 0; i < ratios.Length; i++)
        {
            double sum = 0, weight = 0;
            foreach (var item in cohort)
                if (item.Session.Ratios.Length > previous + i && item.Session.Ratios[previous + i] is { } ratio)
                {
                    sum += item.Weight * ratio;
                    weight += item.Weight;
                }
            carried = weight > 0 ? (sum + shrinkage * carried) / (weight + shrinkage) : carried;
            ratios[i] = Math.Clamp(carried, .8, 1.2);
        }
        return new(ratios, Math.Min(20 - previous, cohort.Max(s => s.Session.Ratios.Length) - previous),
            cohort.Length, maps, previous, PpTargetConfidence.Low);
    }

    private static Session[] build(PpTargetOpportunityProfile history, PpPatternProfile patterns)
    {
        var evidence = patterns.Evidence.GroupBy(e => e.ScoreId).ToDictionary(g => g.Key, g => g.First());
        // Online best/recent APIs do not provide complete retry sequences. Local known outcomes
        // alone train this model; a pass without PP is unknown for that attempt only.
        var attempts = history.RecentAttempts.Where(a => a.LocalAttempt && a.LocalScoreId is not null
                && a.PlayedAt <= history.AsOf && a.PlayedAt >= history.AsOf.AddDays(-30))
            .GroupBy(a => a.LocalScoreId).Select(g => g.First());
        var result = new List<Session>();
        foreach (var group in attempts.GroupBy(a => (Map: a.BeatmapId > 0 ? $"online:{a.BeatmapId}" : $"local:{a.LocalBeatmapId}",
                     Setup: configuration(a.Mods.Split(','), a.ModsJson), a.LegacyScore)))
        {
            var ordered = group.OrderBy(a => a.PlayedAt).ToArray();
            double[] passed = ordered.Where(a => a.Passed && a.Pp is > 0 && double.IsFinite(a.Pp.Value)).Select(a => a.Pp!.Value).ToArray();
            if (passed.Length == 0) continue;
            var shapes = ordered.Where(a => evidence.ContainsKey(a.LocalScoreId!.Value))
                .Select(a => evidence[a.LocalScoreId!.Value]).Where(e => e.PlayedAt <= history.AsOf && e.Features.PointCount >= 20).ToArray();
            if (shapes.Length == 0 || shapes.Select(e => e.MapKey).Distinct().Count() != 1) continue;
            var shape = shapes.MaxBy(e => e.Features.PointCount)!.Features;
            var sample = ordered.FirstOrDefault(a => double.IsFinite(a.Stars) && a.Stars > 0 && a.Bpm is > 0 && double.IsFinite(a.Bpm.Value) && a.LengthSeconds is > 0);
            if (sample is null) continue;
            double normal = passed.Average();
            var current = new List<PpTargetPassSample>();
            void finish()
            {
                if (current.Count > 0) result.Add(new(group.Key.Map, group.Key.Setup, group.Key.LegacyScore, current[^1].PlayedAt,
                    sample.Stars, sample.Bpm!.Value, sample.LengthSeconds!.Value, shape,
                    current.Select(a => a.Passed && a.Pp is > 0 && double.IsFinite(a.Pp.Value) ? a.Pp.Value / normal : (double?)null).ToArray(),
                    history.AsOf - current[^1].PlayedAt < TimeSpan.FromHours(6)));
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
}
