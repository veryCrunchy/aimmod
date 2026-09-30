using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.LocalLibrary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetBestPlay(int BeatmapId, double Pp);
public sealed record PpTargetPassSample(int BeatmapId, DateTimeOffset PlayedAt, double Stars,
    double? Bpm, int? LengthSeconds, string Mods, bool Passed, Guid? LocalBeatmapId = null,
    double? Accuracy = null, string ModsJson = "", Guid? LocalScoreId = null, double? Pp = null, bool LocalAttempt = false, bool LegacyScore = false);
public sealed record PpTargetOpportunityProfile(DateTimeOffset AsOf, IReadOnlyList<PpTargetBestPlay> BestPlays,
    IReadOnlyList<PpTargetPassSample> RecentAttempts);
public sealed record PpTargetPassEstimate(double Probability, double Lower, double Upper, int Attempts, int Maps,
    bool BroaderComparison = false, PpTargetConfidence Confidence = PpTargetConfidence.Low,
    bool DurationAdjusted = false, bool SameMap = false, double? ConditionalAccuracy = null, bool CrossMode = false);

public static class PpTargetOpportunityModel
{
    private static readonly ConditionalWeakTable<PpTargetOpportunityProfile, AttemptIndex> indexes = new();
    private sealed class AttemptIndex
    {
        private readonly Dictionary<string, PpTargetPassSample[]> bySetup;
        private readonly ConcurrentDictionary<(string Setup, bool LegacyScore), EligibleAttempts> eligible = new();
        private readonly DateTimeOffset asOf;
        public AttemptIndex(PpTargetOpportunityProfile profile)
        {
            asOf = profile.AsOf;
            bySetup = profile.RecentAttempts.Where(s => s.PlayedAt <= profile.AsOf
                    && s.PlayedAt >= profile.AsOf.AddDays(-30) && double.IsFinite(s.Stars) && s.Stars > 0)
                .GroupBy(s => ScoreMods.Configuration(s.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries), s.ModsJson, PpTargetMods.NormaliseForSkill))
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        }
        public EligibleAttempts For(IReadOnlyList<string> mods, string? json, bool legacyScore) =>
            eligible.GetOrAdd((ScoreMods.Configuration(mods, json, PpTargetMods.NormaliseForSkill), legacyScore), key =>
                new((bySetup.GetValueOrDefault(key.Setup) ?? []).Where(s => s.LegacyScore == key.LegacyScore).ToArray(), asOf));
        public EligibleAttempts ForBothModes(IReadOnlyList<string> mods, string? json) =>
            both.GetOrAdd(ScoreMods.Configuration(mods, json, PpTargetMods.NormaliseForSkill), key =>
                new(bySetup.GetValueOrDefault(key) ?? [], asOf));
        private readonly ConcurrentDictionary<string, EligibleAttempts> both = new();
    }

    // Attempts for one setup, indexed by beatmap and by star rating. Queries return indices in the
    // original order so every downstream sum is evaluated exactly as a full scan would.
    internal sealed class EligibleAttempts
    {
        private readonly Dictionary<int, PpTargetPassSample[]> byBeatmap;
        private readonly double[] stars;

        public EligibleAttempts(PpTargetPassSample[] samples, DateTimeOffset asOf)
        {
            Samples = samples;
            byBeatmap = samples.GroupBy(s => s.BeatmapId).ToDictionary(g => g.Key, g => g.ToArray());
            stars = samples.Select(s => s.Stars).ToArray();
            Recency = samples.Select(s => Math.Pow(0.5, Math.Max(0, (asOf - s.PlayedAt).TotalDays) / 14)).ToArray();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            Maps = samples.Select(s =>
            {
                string key = s.LocalBeatmapId is { } id ? $"local:{id}" : $"online:{s.BeatmapId}";
                if (!ids.TryGetValue(key, out int map)) ids[key] = map = ids.Count;
                return map;
            }).ToArray();
        }

        public PpTargetPassSample[] Samples { get; }
        public double[] Recency { get; }
        public int[] Maps { get; }

        public PpTargetPassSample[] ForBeatmap(int beatmapId) => byBeatmap.GetValueOrDefault(beatmapId) ?? [];

        // A superset of the samples within the distance; callers still apply the exact comparison.
        public List<int> Near(double target, double distance)
        {
            double minimum = target - distance - 1e-6, maximum = target + distance + 1e-6;
            var window = new List<int>();
            for (int i = 0; i < stars.Length; i++)
                if (stars[i] >= minimum && stars[i] <= maximum)
                    window.Add(i);
            return window;
        }
    }

    // EstimatePass for a fixed mod setup, so a ranking resolves the setup and its attempts once.
    internal sealed class PassEstimator
    {
        private readonly EligibleAttempts? eligible;
        private readonly EligibleAttempts? both;

        public PassEstimator(PpTargetOpportunityProfile profile, IReadOnlyList<string> mods, string? modsJson, bool legacyScore)
        {
            if (modKey(mods).Split(',').Any(m => m is "NF" or "SD" or "PF" or "RX" or "AP" or "AT" or "CN")) return;
            AttemptIndex index = indexes.GetValue(profile, p => new AttemptIndex(p));
            eligible = index.For(mods, modsJson, legacyScore);
            both = index.ForBothModes(mods, modsJson);
        }

        public PpTargetPassEstimate? Estimate(double stars, double bpm, int seconds, int beatmapId)
        {
            if (!double.IsFinite(stars) || stars <= 0 || seconds <= 0 || eligible is null) return null;
            // Pass/fail barely depends on the scoring system. A player who switched clients keeps
            // their other-mode history instead of silently losing it, at low confidence.
            return estimate(eligible, stars, bpm, seconds, beatmapId)
                ?? (both!.Samples.Length > eligible.Samples.Length && estimate(both, stars, bpm, seconds, beatmapId) is { } crossed
                    ? crossed with { CrossMode = true, Confidence = PpTargetConfidence.Low } : null);
        }
    }

    // AccountGain with the best-play table and its current weighted total computed once.
    internal sealed class AccountGainIndex
    {
        private readonly Dictionary<int, double>? best;
        private readonly double[] descending = [];
        private readonly double before;

        public AccountGainIndex(PpTargetOpportunityProfile? profile)
        {
            if (profile is null || profile.BestPlays.Count == 0) return;
            best = profile.BestPlays.GroupBy(p => p.BeatmapId).ToDictionary(g => g.Key, g => g.Max(p => p.Pp));
            descending = best.Values.OrderDescending().ToArray();
            for (int i = 0; i < descending.Length; i++) before += descending[i] * Math.Pow(0.95, i);
        }

        public double? Gain(int beatmapId, double expectedPp)
        {
            if (best is null || !double.IsFinite(expectedPp) || expectedPp < 0)
                return null;
            bool known = best.TryGetValue(beatmapId, out double previous);
            if (expectedPp <= previous) return 0;
            // Retain displaced known plays at their new weights; omit unknown tail and bonus PP.
            double after = 0;
            int index = 0;
            bool removed = !known, inserted = false;
            foreach (double value in descending)
            {
                if (!removed && value.Equals(previous))
                {
                    removed = true;
                    continue;
                }
                if (!inserted && expectedPp.CompareTo(value) > 0)
                {
                    after += expectedPp * Math.Pow(0.95, index++);
                    inserted = true;
                }
                after += value * Math.Pow(0.95, index++);
            }
            if (!inserted) after += expectedPp * Math.Pow(0.95, index);
            return Math.Max(0, after - before);
        }
    }
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
                s.LengthSeconds, modKey(s.Mods), s.Passed!.Value, s.OnlineBeatmapId > 0 ? null : s.LocalBeatmapId,
                double.IsFinite(s.Accuracy) && s.Accuracy is >= 0 and <= 1 ? s.Accuracy : null, s.ModsJson,
                s.LocalScoreId, s.PerformancePoints, s.IsLocal, s.LegacyScore)).ToArray();
        return new(reference, best, attempts);
    }

    public static double? AccountGain(PpTargetOpportunityProfile? profile, int beatmapId, double expectedPp) =>
        new AccountGainIndex(profile).Gain(beatmapId, expectedPp);

    public static PpTargetPassEstimate? EstimatePass(PpTargetOpportunityProfile? profile,
        double stars, double bpm, int seconds, IReadOnlyList<string> mods, int beatmapId = 0, string? modsJson = null, bool legacyScore = false)
    {
        if (profile is null || !double.IsFinite(stars) || stars <= 0 || seconds <= 0) return null;
        return new PassEstimator(profile, mods, modsJson, legacyScore).Estimate(stars, bpm, seconds, beatmapId);
    }

    private static readonly double log_1_25 = Math.Log(1.25), log_1_5 = Math.Log(1.5), log_1_6 = Math.Log(1.6), log_2_2 = Math.Log(2.2);

    // True only when |log(ratio)| certainly exceeds log(limit); the margin dwarfs Math.Log's rounding error,
    // so the exact logarithm comparison decides every ratio near the boundary.
    private static bool outside(double ratio, double limit) => ratio > limit * (1 + 1e-9) || ratio < 1 / limit * (1 - 1e-9);

    private static PpTargetPassEstimate? estimate(EligibleAttempts attempts, double stars, double bpm, int seconds, int beatmapId)
    {
        // Multiple attempts of this difficulty are more relevant than pooled neighbouring maps.
        // Best-score feeds remain excluded: they cannot establish attempt frequency.
        var direct = beatmapId > 0 ? attempts.ForBeatmap(beatmapId) : [];
        if (direct.Length >= 3)
        {
            double directSuccesses = direct.Count(s => s.Passed);
            double directProbability = (directSuccesses + 1) / (direct.Length + 2);
            var interval = wilson(directProbability, direct.Length + 2);
            double[] accuracy = direct.Where(s => s.Passed && s.Accuracy is not null).Select(s => s.Accuracy!.Value).Order().ToArray();
            return new(directProbability, interval.Lower, interval.Upper, direct.Length, 1,
                Confidence: PpTargetConfidence.Low, SameMap: true,
                ConditionalAccuracy: accuracy.Length >= 2 ? PpTargetPreferenceProfiler.percentile(accuracy, .5) : null);
        }
        // Every pooled comparison below is bounded to within 1.25 stars. Samples are visited in their
        // original order and each weight is the same expression as the full scan it replaces.
        PpTargetPassSample[] all = attempts.Samples;
        List<int> eligible = attempts.Near(stars, 1.25);
        var nearby = new List<(int Index, double Weight)>();
        foreach (int i in eligible)
        {
            PpTargetPassSample s = all[i];
            if (!(Math.Abs(s.Stars - stars) <= 0.75 && s.LengthSeconds is > 0 && s.Bpm is > 0 && double.IsFinite(s.Bpm.Value))) continue;
            double lengthRatio = (double)seconds / s.LengthSeconds.Value;
            if (outside(lengthRatio, 1.6) || !(bpm > 0)) continue;
            double length = Math.Log(lengthRatio);
            if (!(Math.Abs(length) <= log_1_6)) continue;
            double tempoRatio = bpm / s.Bpm.Value;
            if (outside(tempoRatio, 1.25)) continue;
            double tempo = Math.Log(tempoRatio);
            if (!(Math.Abs(tempo) <= log_1_25)) continue;
            nearby.Add((i, Math.Exp(-Math.Pow((s.Stars - stars) / 0.5, 2) - Math.Pow(length / 0.45, 2) - Math.Pow(tempo / 0.2, 2))
                * attempts.Recency[i]));
        }
        int mapCount(List<(int Index, double Weight)> items) => items.Select(s => attempts.Maps[s.Index]).Distinct().Count();
        bool broader = false;
        if (nearby.Count < 5 || mapCount(nearby) < 3)
        {
            broader = true;
            // Missing metadata reduces support; it never becomes an exact match. Keep mods fixed.
            nearby = new List<(int Index, double Weight)>();
            foreach (int i in eligible)
            {
                PpTargetPassSample s = all[i];
                if (!(Math.Abs(s.Stars - stars) <= 1.25)) continue;
                double length = 0;
                if (s.LengthSeconds is > 0)
                {
                    double lengthRatio = (double)seconds / s.LengthSeconds.Value;
                    if (outside(lengthRatio, 2.2)) continue;
                    length = Math.Log(lengthRatio);
                    if (!(Math.Abs(length) <= log_2_2)) continue;
                }
                double tempo = 0;
                if (!(s.Bpm is not > 0 || !double.IsFinite(s.Bpm.Value) || bpm <= 0))
                {
                    double tempoRatio = bpm / s.Bpm.Value;
                    if (outside(tempoRatio, 1.5)) continue;
                    tempo = Math.Log(tempoRatio);
                    if (!(Math.Abs(tempo) <= log_1_5)) continue;
                }
                nearby.Add((i, Math.Exp(-Math.Pow((s.Stars - stars) / .9, 2))
                    * (s.LengthSeconds is > 0 ? Math.Exp(-Math.Pow(length / .8, 2)) : .5)
                    * (s.Bpm is > 0 && double.IsFinite(s.Bpm.Value) && bpm > 0 ? Math.Exp(-Math.Pow(tempo / .4, 2)) : .5)
                    * attempts.Recency[i]));
            }
        }
        int maps = mapCount(nearby);
        bool durationAdjusted = false;
        if (nearby.Count < 5 || maps < 3)
        {
            // Longer maps can use shorter, otherwise similar attempts. This is a survival
            // approximation, not evidence that the player has demonstrated marathon stamina.
            var shorter = new List<(int Index, double Weight)>();
            foreach (int i in eligible)
            {
                PpTargetPassSample s = all[i];
                if (!(Math.Abs(s.Stars - stars) <= .75
                      && s.LengthSeconds is >= 45 && seconds > s.LengthSeconds.Value
                      && seconds <= s.LengthSeconds.Value * 4d
                      && s.Bpm is > 0 && double.IsFinite(s.Bpm.Value) && double.IsFinite(bpm) && bpm > 0)) continue;
                double tempoRatio = bpm / s.Bpm.Value;
                if (outside(tempoRatio, 1.25)) continue;
                double tempo = Math.Log(tempoRatio);
                if (!(Math.Abs(tempo) <= log_1_25)) continue;
                shorter.Add((i, Math.Exp(-Math.Pow((s.Stars - stars) / .65, 2) - Math.Pow(tempo / .25, 2)) * attempts.Recency[i]));
            }
            int shorterMaps = mapCount(shorter);
            if (shorter.Count >= 8 && shorterMaps >= 5)
            {
                nearby = shorter;
                maps = shorterMaps;
                durationAdjusted = broader = true;
            }
        }
        if (nearby.Count < 5 || maps < 3) return null;
        // Repeated retries of one map cannot outweigh broad evidence across other maps.
        var balanced = nearby.GroupBy(s => attempts.Maps[s.Index]).SelectMany(g =>
        {
            double total = g.Sum(s => s.Weight);
            return g.Select(s => (Sample: all[s.Index], s.Index, Weight: s.Weight / Math.Max(1, total)));
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
        var passedMaps = balanced.Where(s => s.Sample.Passed && s.Sample.Accuracy is not null)
            .GroupBy(s => attempts.Maps[s.Index]).Select(g => g.OrderByDescending(s => s.Sample.PlayedAt).First().Sample.Accuracy!.Value)
            .Order().ToArray();
        return new(probability, lower, upper, nearby.Count, maps,
            broader, !broader && weight >= 6 ? PpTargetConfidence.Medium : PpTargetConfidence.Low, durationAdjusted,
            ConditionalAccuracy: passedMaps.Length >= 3 ? PpTargetPreferenceProfiler.percentile(passedMaps, .5) : null);
    }

    private static (double Lower, double Upper) wilson(double probability, double n)
    {
        const double z = 1.96;
        double denominator = 1 + z * z / n;
        double centre = (probability + z * z / (2 * n)) / denominator;
        double margin = z * Math.Sqrt(probability * (1 - probability) / n + z * z / (4 * n * n)) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    private static string modKey(IEnumerable<string> mods) => string.Join(',', PpTargetMods.Normalise(mods));
}
