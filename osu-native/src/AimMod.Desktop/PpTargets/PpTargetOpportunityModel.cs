using AimMod.Desktop.ScoreHistory;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetBestPlay(int BeatmapId, double Pp);
public sealed record PpTargetPassSample(int BeatmapId, DateTimeOffset PlayedAt, double Stars,
    double? Bpm, int? LengthSeconds, string Mods, bool Passed, Guid? LocalBeatmapId = null);
public sealed record PpTargetOpportunityProfile(DateTimeOffset AsOf, IReadOnlyList<PpTargetBestPlay> BestPlays,
    IReadOnlyList<PpTargetPassSample> RecentAttempts);
public sealed record PpTargetPassEstimate(double Probability, double Lower, double Upper, int Attempts, int Maps,
    bool BroaderComparison = false, PpTargetConfidence Confidence = PpTargetConfidence.Low,
    bool DurationAdjusted = false);

public static class PpTargetOpportunityModel
{
    public static PpTargetOpportunityProfile Build(IEnumerable<ScoreHistoryEntry> scores, DateTimeOffset? now = null)
    {
        DateTimeOffset reference = now ?? DateTimeOffset.UtcNow;
        var entries = scores.Where(s => s.OnlineBeatmapId > 0 || s.LocalBeatmapId is { } map && map != Guid.Empty)
            .GroupBy(s => s.OnlineScoreId > 0 ? $"online:{s.OnlineScoreId}" : s.Identity)
            .Select(g => g.OrderByDescending(s => s.Passed is not null).First()).ToArray();
        var best = entries.Where(s => s.Provenance.HasFlag(ScoreHistoryProvenance.OnlineBest)
                && s.OnlineBeatmapId > 0 && s.OnlineScoreId > 0
                && s.PerformancePoints is >= 0 && double.IsFinite(s.PerformancePoints.Value))
            .GroupBy(s => s.OnlineBeatmapId)
            .Select(g => new PpTargetBestPlay(g.Key, g.Max(s => s.PerformancePoints!.Value)))
            .OrderByDescending(s => s.Pp).ThenBy(s => s.BeatmapId).ToArray();
        // Best-only listings are selected successes, not observations of pass frequency.
        var attempts = entries.Where(s => (s.Provenance.HasFlag(ScoreHistoryProvenance.OnlineRecent) || s.IsLocal)
                && s.Passed is not null && s.PlayedAt <= reference && s.PlayedAt >= reference.AddDays(-30)
                && double.IsFinite(s.StarRating) && s.StarRating > 0)
            .Select(s => new PpTargetPassSample(s.OnlineBeatmapId, s.PlayedAt, s.StarRating, s.Bpm,
                s.LengthSeconds, modKey(s.Mods), s.Passed!.Value, s.OnlineBeatmapId > 0 ? null : s.LocalBeatmapId)).ToArray();
        return new(reference, best, attempts);
    }

    public static double? AccountGain(PpTargetOpportunityProfile? profile, int beatmapId, double expectedPp)
    {
        if (profile is null || profile.BestPlays.Count == 0 || !double.IsFinite(expectedPp) || expectedPp < 0)
            return null;
        var best = profile.BestPlays.GroupBy(p => p.BeatmapId).ToDictionary(g => g.Key, g => g.Max(p => p.Pp));
        double previous = best.GetValueOrDefault(beatmapId);
        if (expectedPp <= previous) return 0;
        int count = best.Count;
        double before = weighted(best.Values, count);
        best[beatmapId] = expectedPp;
        // Retain displaced known plays at their new weights; omit unknown tail and bonus PP.
        return Math.Max(0, weighted(best.Values, best.Count) - before);
    }

    public static PpTargetPassEstimate? EstimatePass(PpTargetOpportunityProfile? profile,
        double stars, double bpm, int seconds, IReadOnlyList<string> mods)
    {
        if (profile is null || !double.IsFinite(stars) || stars <= 0 || seconds <= 0) return null;
        string key = modKey(mods);
        if (key.Split(',').Any(m => m is "NF" or "SD" or "PF" or "RX" or "AP" or "AT")) return null;
        var eligible = profile.RecentAttempts.Where(s => s.Mods == key && s.PlayedAt <= profile.AsOf
            && s.PlayedAt >= profile.AsOf.AddDays(-30) && double.IsFinite(s.Stars) && s.Stars > 0).ToArray();
        var nearby = eligible.Where(s => Math.Abs(s.Stars - stars) <= 0.75
                && s.LengthSeconds is > 0 && s.Bpm is > 0 && double.IsFinite(s.Bpm.Value)
                && Math.Abs(Math.Log((double)seconds / s.LengthSeconds.Value)) <= Math.Log(1.6)
                && bpm > 0 && Math.Abs(Math.Log(bpm / s.Bpm.Value)) <= Math.Log(1.25))
            .Select(s => (Sample: s, Weight: Math.Exp(-Math.Pow((s.Stars - stars) / 0.5, 2)
                - Math.Pow(Math.Log((double)seconds / s.LengthSeconds!.Value) / 0.45, 2)
                - Math.Pow(Math.Log(bpm / s.Bpm!.Value) / 0.2, 2))
                * Math.Pow(0.5, Math.Max(0, (profile.AsOf - s.PlayedAt).TotalDays) / 14)))
            .ToArray();
        string mapKey(PpTargetPassSample s) => s.LocalBeatmapId is { } id ? $"local:{id}" : $"online:{s.BeatmapId}";
        bool broader = false;
        if (nearby.Length < 5 || nearby.Select(s => mapKey(s.Sample)).Distinct().Count() < 3)
        {
            broader = true;
            // Missing metadata reduces support; it never becomes an exact match. Keep mods fixed.
            nearby = eligible.Where(s => Math.Abs(s.Stars - stars) <= 1.25
                    && (s.LengthSeconds is not > 0 || Math.Abs(Math.Log((double)seconds / s.LengthSeconds.Value)) <= Math.Log(2.2))
                    && (s.Bpm is not > 0 || !double.IsFinite(s.Bpm.Value) || bpm <= 0
                        || Math.Abs(Math.Log(bpm / s.Bpm.Value)) <= Math.Log(1.5)))
                .Select(s => (Sample: s, Weight: Math.Exp(-Math.Pow((s.Stars - stars) / .9, 2))
                    * (s.LengthSeconds is > 0 ? Math.Exp(-Math.Pow(Math.Log((double)seconds / s.LengthSeconds.Value) / .8, 2)) : .5)
                    * (s.Bpm is > 0 && double.IsFinite(s.Bpm.Value) && bpm > 0 ? Math.Exp(-Math.Pow(Math.Log(bpm / s.Bpm.Value) / .4, 2)) : .5)
                    * Math.Pow(.5, Math.Max(0, (profile.AsOf - s.PlayedAt).TotalDays) / 14))).ToArray();
        }
        int maps = nearby.Select(s => mapKey(s.Sample)).Distinct().Count();
        bool durationAdjusted = false;
        if (nearby.Length < 5 || maps < 3)
        {
            // Longer maps can use shorter, otherwise similar attempts. This is a survival
            // approximation, not evidence that the player has demonstrated marathon stamina.
            var shorter = eligible.Where(s => Math.Abs(s.Stars - stars) <= .75
                    && s.LengthSeconds is >= 45 && seconds > s.LengthSeconds.Value
                    && seconds <= s.LengthSeconds.Value * 4d
                    && s.Bpm is > 0 && double.IsFinite(s.Bpm.Value) && double.IsFinite(bpm) && bpm > 0
                    && Math.Abs(Math.Log(bpm / s.Bpm.Value)) <= Math.Log(1.25))
                .Select(s => (Sample: s, Weight: Math.Exp(-Math.Pow((s.Stars - stars) / .65, 2)
                    - Math.Pow(Math.Log(bpm / s.Bpm!.Value) / .25, 2))
                    * Math.Pow(.5, Math.Max(0, (profile.AsOf - s.PlayedAt).TotalDays) / 14))).ToArray();
            int shorterMaps = shorter.Select(s => mapKey(s.Sample)).Distinct().Count();
            if (shorter.Length >= 8 && shorterMaps >= 5)
            {
                nearby = shorter;
                maps = shorterMaps;
                durationAdjusted = broader = true;
            }
        }
        if (nearby.Length < 5 || maps < 3) return null;
        // Repeated retries of one map cannot outweigh broad evidence across other maps.
        var balanced = nearby.GroupBy(s => mapKey(s.Sample)).SelectMany(g =>
        {
            double total = g.Sum(s => s.Weight);
            return g.Select(s => (s.Sample, Weight: s.Weight / Math.Max(1, total)));
        }).ToArray();
        double weight = balanced.Sum(s => s.Weight);
        if (weight < 1) return null;
        double successes = balanced.Where(s => s.Sample.Passed).Sum(s => s.Weight);
        double probability = (successes + 1) / (weight + 2);
        // Conservative Wilson interval uses map-balanced evidence, not raw retry count.
        const double z = 1.96;
        double n = weight + 2, denominator = 1 + z * z / n;
        double centre = (probability + z * z / (2 * n)) / denominator;
        double margin = z * Math.Sqrt(probability * (1 - probability) / n + z * z / (4 * n * n)) / denominator;
        double lower = Math.Max(0, centre - margin), upper = Math.Min(1, centre + margin);
        if (durationAdjusted)
        {
            double observedSeconds = balanced.Sum(s => s.Weight * s.Sample.LengthSeconds!.Value) / weight;
            double durationRatio = seconds / observedSeconds;
            probability = Math.Pow(probability, durationRatio);
            // Widen uncertainty for the unobserved stamina requirement.
            lower = Math.Pow(lower, durationRatio * 1.5);
        }
        return new(probability, lower, upper, nearby.Length, maps,
            broader, !broader && weight >= 6 ? PpTargetConfidence.Medium : PpTargetConfidence.Low, durationAdjusted);
    }

    private static double weighted(IEnumerable<double> pp, int count) => pp.OrderDescending().Take(count)
        .Select((value, index) => value * Math.Pow(0.95, index)).Sum();
    private static string modKey(IEnumerable<string> mods) => string.Join(',', PpTargetMods.Normalise(mods));
}
