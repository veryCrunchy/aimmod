using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Tests;

// Verbatim copy of the aimmod-osu-v0.3.1 ranking and pattern-profile algorithms. The optimised
// production code must reproduce these results exactly; do not "fix" this copy.
internal static class ReferencePpTargetEngine
{
    public static PpTargetRankingResult Rank(
        PpTargetPreferenceProfile profile,
        IEnumerable<OfficialBeatmapSet> beatmapSets,
        PpTargetFilters? filters = null,
        IReadOnlyDictionary<int, PpTargetEstimate>? exactEstimates = null)
    {
        NormalisedFilters query = normalise(filters ?? new PpTargetFilters());
        FlatCandidate[] flattened = beatmapSets.Where(set => set is not null)
            .OrderBy(set => set.BeatmapSetId)
            .SelectMany(set => (set.Difficulties ?? []).Select(difficulty => new FlatCandidate(set, difficulty)))
            .Where(candidate => validDifficulty(candidate.Difficulty))
            .GroupBy(candidate => candidate.Difficulty.BeatmapId)
            .Select(group => group.OrderBy(candidate => candidate.Set.BeatmapSetId).ThenBy(candidate => candidate.Difficulty.Name, StringComparer.Ordinal).First())
            .ToArray();

        PpTargetCandidate[] matching = flattened.Where(candidate => matchesMetadata(candidate, query)).Select(candidate => score(profile, candidate, exactEstimates))
            .Where(candidate => matches(candidate, query))
            .OrderByDescending(candidate => candidate.EvidenceTier)
            .ThenByDescending(candidate => candidate.RankScore)
            .ThenByDescending(candidate => candidate.EstimatedAttainableGainPp)
            .ThenByDescending(candidate => candidate.Estimate == null ? (double?)null : candidate.Estimate.ExpectedPp)
            .ThenBy(candidate => candidate.BeatmapId)
            .ToArray();

        return new PpTargetRankingResult(profile, matching.Take(query.Limit).ToArray(), flattened.Length, matching.Length);
    }

    private static PpTargetCandidate score(
        PpTargetPreferenceProfile profile,
        FlatCandidate candidate,
        IReadOnlyDictionary<int, PpTargetEstimate>? exactEstimates)
    {
        OfficialBeatmapSet set = candidate.Set;
        OfficialBeatmapDifficulty difficulty = candidate.Difficulty;
        IReadOnlyList<string> mods = profile.PreferredModSetup ?? PpTargetMods.SelectCompatible(profile.CommonMods);
        double preference = preferenceFit(profile, set, difficulty);
        (double attainability, double scoreEvidence, int nearbySampleCount) = performanceFit(profile, difficulty.StarRating);
        PpTargetPassEstimate? passEstimate = EstimatePass(profile.Opportunities,
            difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds, mods, difficulty.BeatmapId, profile.PreferredModsJson, profile.LegacyScore);
        double? expectedAccuracy = passEstimate?.ConditionalAccuracy ?? profile.TypicalAccuracy;
        PpTargetEstimate? estimate = matchingEstimate(
            exactEstimates?.GetValueOrDefault(difficulty.BeatmapId) is {} exact && exact.LegacyScore == profile.LegacyScore && (exact.ModsJson ?? "") == (profile.PreferredModsJson ?? "") ? exact : null,
            difficulty.BeatmapId,
            mods,
            expectedAccuracy,
            attainability);
        if (estimate?.PatternProfileIdentity is { } identity && identity != profile.PatternProfile?.Identity)
            estimate = null;
        if (estimate?.PatternPrediction is { Fit: { } patternFit })
            attainability = Math.Clamp(patternFit, 0, 1);
        PpTargetConfidence recommendation = recommendationConfidence(nearbySampleCount, profile.Confidence);
        if (profile.PatternProfile?.ScoreEvidence is not null)
            recommendation = nearbySampleCount >= 2 ? PpTargetConfidence.Low : PpTargetConfidence.Insufficient;
        if (estimate?.PatternPrediction is { } pattern)
        {
            bool scoreSupported = profile.PatternProfile?.ScoreEvidence is not null && nearbySampleCount >= 2;
            attainability = pattern.Fit ?? Math.Min(scoreSupported ? attainability : 0.5, pattern.PatternFits
                .Where(item => item.Fit is not null).Select(item => item.Fit!.Value).DefaultIfEmpty(1).Min());
            scoreEvidence = pattern.Fit is null && scoreSupported ? scoreEvidence : pattern.EvidenceConfidence;
            recommendation = pattern.Fit is null ? (scoreSupported ? PpTargetConfidence.Low : PpTargetConfidence.Insufficient) : pattern.EvidenceConfidence switch
            {
                >= 0.65 => PpTargetConfidence.High,
                >= 0.35 => PpTargetConfidence.Medium,
                > 0 => PpTargetConfidence.Low,
                _ => PpTargetConfidence.Insufficient,
            };
        }
        double? baseline = difficultyBaseline(profile.PerformanceSamples, difficulty.StarRating);
        bool supportedScore = passEstimate is not null && (passEstimate.ConditionalAccuracy is not null
            || estimate?.PatternPrediction is { Fit: not null, ExpectedAccuracy: not null });
        bool awardsPp = string.Equals(set.Status, "ranked", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(set.Status, "approved", StringComparison.OrdinalIgnoreCase);
        double? gain = estimate is null || baseline is null || !awardsPp || !supportedScore
            ? null
            : Math.Max(0, estimate.ExpectedPp - baseline.Value) * passEstimate!.Probability;
        double? accountGain = awardsPp && estimate is not null && supportedScore
            ? AccountGain(profile.Opportunities, difficulty.BeatmapId, estimate.ExpectedPp) * passEstimate!.Probability : null;
        double? gainPerMinute = accountGain is { } account && difficulty.TotalLengthSeconds > 0
            ? account / (difficulty.TotalLengthSeconds / 60d) : null;
        double gainScore = accountGain is null
            ? 0
            : Math.Clamp(accountGain.Value / 20, 0, 1);
        double modCompatibility = modFit(profile.CommonMods, mods);
        double confidenceScore = (int)recommendation / 3d;
        double rank = 100 * (0.60 * attainability + 0.15 * (passEstimate?.Lower ?? attainability) + 0.10 * preference
                            + 0.08 * gainScore * attainability
                            + 0.04 * modCompatibility + 0.03 * confidenceScore);
        if (accountGain is { } attainableAccountGain)
        {
            double gainScale = Math.Max(5, (profile.CompetitivePpFloor ?? 100) * .1);
            rank *= .45 + .55 * attainableAccountGain / (attainableAccountGain + gainScale);
        }

        return new PpTargetCandidate(
            set.BeatmapSetId, difficulty.BeatmapId, set.Title, set.Artist, set.Creator, set.Source, set.Status,
            difficulty.Name, difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds, difficulty.MaximumCombo,
            set.CoverUrl, preference, attainability, rank, baseline, gain, estimate, mods,
            scoreEvidence, modCompatibility, recommendation, passEstimate, accountGain, gainPerMinute, expectedAccuracy,
            awardsPp ? LearningPredict(profile.Opportunities, profile.PatternProfile, estimate,
                difficulty.BeatmapId, difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds, mods, profile.PreferredModsJson, profile.LegacyScore) : null);
    }

    private static PpTargetEstimate? matchingEstimate(
        PpTargetEstimate? estimate,
        int beatmapId,
        IReadOnlyList<string> mods,
        double? expectedAccuracy,
        double attainability)
    {
        if (estimate is null || estimate.BeatmapId is { } estimateBeatmapId && estimateBeatmapId != beatmapId)
            return null;
        if (estimate.Mods is not null && !PpTargetMods.Normalise(estimate.Mods).SequenceEqual(PpTargetMods.Normalise(mods)))
            return null;
        if (estimate.ExpectedAccuracy is { } accuracy
            && (expectedAccuracy is null || Math.Abs(accuracy - expectedAccuracy.Value) > 0.000_001))
            return null;
        if (estimate.Attainability is { } estimatedAttainability
            && Math.Abs(estimatedAttainability - attainability) > 0.000_001)
            return null;
        return estimate;
    }

    private static (double Attainability, double Evidence, int SampleCount) performanceFit(
        PpTargetPreferenceProfile profile,
        double starRating)
    {
        if (ScoreMods.HasCustomSettings(profile.PreferredModsJson)) return (.5, 0, 0);
        if (profile.PatternProfile is { ScoreEvidence: not null } recent)
        {
            var support = ScoreFit(recent, starRating,
                profile.PreferredModSetup ?? PpTargetMods.SelectCompatible(profile.CommonMods), profile.LegacyScore);
            return (support.Fit, support.Confidence, support.Maps);
        }
        PpTargetPerformanceSample[] nearby = profile.PerformanceSamples
            .Where(sample => double.IsFinite(sample.StarRating) && sample.StarRating > 0
                             && double.IsFinite(sample.Accuracy) && sample.Accuracy is >= 0 and <= 1)
            .OrderBy(sample => Math.Abs(sample.StarRating - starRating))
            .ThenByDescending(sample => sample.Accuracy)
            .Take(24)
            .ToArray();
        if (nearby.Length == 0)
            return (rangeFit(profile.PreferredStarRange, starRating, 1.5), 0, 0);

        double weightedFit = 0;
        double totalWeight = 0;
        foreach (PpTargetPerformanceSample sample in nearby)
        {
            double distance = Math.Abs(sample.StarRating - starRating);
            double weight = Math.Exp(-distance / 0.9);
            double demonstratedStars = sample.StarRating + Math.Clamp((sample.Accuracy - 0.95) * 5, -0.5, 0.25);
            double fit = 1 / (1 + Math.Exp((starRating - demonstratedStars - 0.4) / 0.35));
            weightedFit += fit * weight;
            totalWeight += weight;
        }

        if (totalWeight <= double.Epsilon)
            return (0, 0, 0);

        double evidence = Math.Clamp(totalWeight / 8, 0, 1);
        double evidenceFit = weightedFit / totalWeight;
        double preferenceFit = rangeFit(profile.PreferredStarRange, starRating, 1.5);
        int evidenceSampleCount = nearby.Count(sample => Math.Abs(sample.StarRating - starRating) <= 1.25);
        return (Math.Clamp(evidenceFit * (0.7 + 0.3 * evidence) + preferenceFit * 0.3 * (1 - evidence), 0, 1), evidence, evidenceSampleCount);
    }

    private static double? difficultyBaseline(IReadOnlyList<PpTargetPerformanceSample> samples, double starRating)
    {
        double[] nearbyPp = samples.Where(sample => double.IsFinite(sample.StarRating)
                                                    && Math.Abs(sample.StarRating - starRating) <= 1.25
                                                    && double.IsFinite(sample.PerformancePoints)
                                                    && sample.PerformancePoints > 0)
                                   .OrderBy(sample => Math.Abs(sample.StarRating - starRating))
                                   .Take(24)
                                   .Select(sample => sample.PerformancePoints)
                                   .Order()
                                   .ToArray();
        return nearbyPp.Length == 0
            ? null
            : PpTargetPreferenceProfiler.percentile(nearbyPp, nearbyPp.Length >= 8 ? 0.65 : 0.5);
    }

    private static double modFit(IReadOnlyList<PpTargetPreference> preferences, IReadOnlyList<string> selected)
    {
        if (preferences.Count == 0)
            return 1;
        if (selected.Count == 0)
            return 0;
        return Math.Clamp(selected.Select(mod => preferences
            .Where(preference => PpTargetMods.NormaliseOne(preference.Value) == mod)
            .Select(preference => preference.Weight)
            .DefaultIfEmpty(0)
            .Max()).Average(), 0, 1);
    }

    private static PpTargetConfidence recommendationConfidence(int nearbySamples, PpTargetConfidence profileConfidence)
    {
        PpTargetConfidence evidence = nearbySamples switch
        {
            >= 12 => PpTargetConfidence.High,
            >= 5 => PpTargetConfidence.Medium,
            >= 2 => PpTargetConfidence.Low,
            _ => PpTargetConfidence.Insufficient,
        };
        return (PpTargetConfidence)Math.Min((int)evidence, (int)profileConfidence);
    }

    private static double preferenceFit(PpTargetPreferenceProfile profile, OfficialBeatmapSet set, OfficialBeatmapDifficulty difficulty)
    {
        double star = rangeFit(profile.PreferredStarRange, difficulty.StarRating, 2);
        double bpm = rangeFit(profile.PreferredBpmRange, difficulty.Bpm, 80);
        double length = rangeFit(profile.PreferredLengthSecondsRange, difficulty.TotalLengthSeconds, 240);
        double creator = signalFit(profile.PreferredCreators, set.Creator);
        double source = signalFit(profile.PreferredSources, set.Source);
        double artist = signalFit(profile.PreferredArtists, set.Artist);
        HashSet<string> tokens = PpTargetPreferenceProfiler.tokenise($"{set.Title} {difficulty.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        double title = profile.PreferredTitleSignals.Where(signal => tokens.Contains(signal.Value)).Select(signal => signal.Weight).DefaultIfEmpty(0).Max();
        double metadata = Math.Max(Math.Max(creator, source), Math.Max(artist, title));
        return Math.Clamp(0.5 * star + 0.15 * bpm + 0.15 * length + 0.2 * metadata, 0, 1);
    }

    private static double signalFit(IReadOnlyList<PpTargetPreference> preferences, string value) =>
        preferences.FirstOrDefault(item => string.Equals(item.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase))?.Weight ?? 0;

    private static double rangeFit(PpTargetRange? range, double value, double falloff)
    {
        if (range is null || !double.IsFinite(value) || value < 0)
            return 0;
        if (range.Contains(value))
            return 1;
        double distance = value < range.Minimum ? range.Minimum - value : value - range.Maximum;
        return Math.Clamp(1 - distance / Math.Max(0.001, falloff), 0, 1);
    }

    private static bool matchesMetadata(FlatCandidate candidate, NormalisedFilters filters)
    {
        var d = candidate.Difficulty; var set = candidate.Set;
        if (!between(d.StarRating, filters.MinimumStars, filters.MaximumStars)
            || !between(d.Bpm, filters.MinimumBpm, filters.MaximumBpm)
            || !between(d.TotalLengthSeconds, filters.MinimumLengthSeconds, filters.MaximumLengthSeconds)
            || filters.Statuses.Count > 0 && !filters.Statuses.Contains(set.Status)) return false;
        if (filters.SearchTokens.Length == 0) return true;
        string searchable = $"{set.Title} {set.Artist} {set.Creator} {set.Source} {d.Name} {set.Status}";
        return filters.SearchTokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool matches(PpTargetCandidate candidate, NormalisedFilters filters)
    {
        if (!between(candidate.StarRating, filters.MinimumStars, filters.MaximumStars)
            || !between(candidate.Bpm, filters.MinimumBpm, filters.MaximumBpm)
            || !between(candidate.TotalLengthSeconds, filters.MinimumLengthSeconds, filters.MaximumLengthSeconds))
            return false;
        if (filters.Statuses.Count > 0 && !filters.Statuses.Contains(candidate.Status))
            return false;
        if (filters.SearchTokens.Length > 0)
        {
            string searchable = $"{candidate.Title} {candidate.Artist} {candidate.Creator} {candidate.Source} {candidate.Difficulty} {candidate.Status}";
            if (filters.SearchTokens.Any(token => !searchable.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (filters.HasExpectedFilter && (candidate.FirstAttemptPp is not { } earned || !between(earned, filters.MinimumExpectedPp, filters.MaximumExpectedPp)))
            return false;
        return !filters.HasMaximumFilter
               || candidate.Estimate is not null && between(candidate.Estimate.RealisticMaximumPp, filters.MinimumRealisticMaximumPp, filters.MaximumRealisticMaximumPp);
    }

    private static bool between(double value, double? minimum, double? maximum) =>
        (minimum is null || value >= minimum) && (maximum is null || value <= maximum);

    private static NormalisedFilters normalise(PpTargetFilters filters)
    {
        (double? minStars, double? maxStars) = range(filters.MinimumStars, filters.MaximumStars, 0);
        (double? minExpected, double? maxExpected) = range(filters.MinimumExpectedPp, filters.MaximumExpectedPp, 0);
        (double? minMaximum, double? maxMaximum) = range(filters.MinimumRealisticMaximumPp, filters.MaximumRealisticMaximumPp, 0);
        (double? minBpm, double? maxBpm) = range(filters.MinimumBpm, filters.MaximumBpm, 0);
        (double? minLength, double? maxLength) = range(filters.MinimumLengthSeconds, filters.MaximumLengthSeconds, 0);
        HashSet<string> statuses = (filters.Statuses ?? []).Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] search = PpTargetPreferenceProfiler.tokenise((filters.SearchText ?? string.Empty)[..Math.Min(filters.SearchText?.Length ?? 0, 256)]).ToArray();
        return new NormalisedFilters(search, minStars, maxStars, minExpected, maxExpected, minMaximum, maxMaximum,
            minLength, maxLength, minBpm, maxBpm, statuses, Math.Clamp(filters.Limit, 1, 50_000));
    }

    private static (double? Minimum, double? Maximum) range(double? minimum, double? maximum, double floor)
    {
        minimum = validBound(minimum, floor) ? minimum : null;
        maximum = validBound(maximum, floor) ? maximum : null;
        return minimum > maximum ? (maximum, minimum) : (minimum, maximum);
    }

    private static bool validBound(double? value, double minimum) =>
        value is not null && double.IsFinite(value.Value) && value >= minimum;

    private static bool validDifficulty(OfficialBeatmapDifficulty difficulty) =>
        difficulty.BeatmapId > 0 && string.Equals(difficulty.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase)
        && difficulty.StarRating > 0 && double.IsFinite(difficulty.StarRating)
        && difficulty.Bpm >= 0 && double.IsFinite(difficulty.Bpm) && difficulty.TotalLengthSeconds >= 0;

    private sealed record FlatCandidate(OfficialBeatmapSet Set, OfficialBeatmapDifficulty Difficulty);

    private sealed record NormalisedFilters(
        string[] SearchTokens, double? MinimumStars, double? MaximumStars,
        double? MinimumExpectedPp, double? MaximumExpectedPp,
        double? MinimumRealisticMaximumPp, double? MaximumRealisticMaximumPp,
        double? MinimumLengthSeconds, double? MaximumLengthSeconds,
        double? MinimumBpm, double? MaximumBpm, HashSet<string> Statuses, int Limit)
    {
        public bool HasExpectedFilter => MinimumExpectedPp is not null || MaximumExpectedPp is not null;
        public bool HasMaximumFilter => MinimumRealisticMaximumPp is not null || MaximumRealisticMaximumPp is not null;
    }

    // PpTargetPatternModel.ScoreFit
    public static (double Fit, double Confidence, int Maps) ScoreFit(PpPatternProfile profile, double stars, IEnumerable<string> mods, bool legacyScore = false)
    {
        string key = ReferencePpTargetPatternModel.modsKey(mods);
        var nearby = (profile.ScoreEvidence ?? []).Where(e => e.LegacyScore == legacyScore && e.ModsKey == key && Math.Abs(e.StarRating - stars) <= 1.25
                && e.Weight > 0 && double.IsFinite(e.Weight))
            .GroupBy(e => e.MapKey).OrderBy(g => g.Min(e => Math.Abs(e.StarRating - stars)))
            .ThenBy(g => g.Key, StringComparer.Ordinal).Take(24).SelectMany(g => g).ToArray();
        double total = 0, fit = 0;
        foreach (var sample in nearby)
        {
            double weight = sample.Weight * Math.Exp(-Math.Abs(sample.StarRating - stars) / .9);
            double demonstrated = sample.StarRating + Math.Clamp((sample.Accuracy - .95) * 5, -.5, .25);
            fit += weight / (1 + Math.Exp((stars - demonstrated - .4) / .35));
            total += weight;
        }
        int maps = nearby.Select(e => e.MapKey).Distinct().Count();
        return (total > 0 ? fit / total : .5, Math.Min(.3, total / 8 * .3), maps);
    }

    // PpTargetOpportunityModel
    private static readonly ConditionalWeakTable<PpTargetOpportunityProfile, AttemptIndex> indexes = new();
    private sealed class AttemptIndex
    {
        private readonly Dictionary<string, PpTargetPassSample[]> bySetup;
        public AttemptIndex(PpTargetOpportunityProfile profile)
        {
            bySetup = profile.RecentAttempts.Where(s => s.PlayedAt <= profile.AsOf
                    && s.PlayedAt >= profile.AsOf.AddDays(-30) && double.IsFinite(s.Stars) && s.Stars > 0)
                .GroupBy(s => ScoreMods.Configuration(s.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries), s.ModsJson, PpTargetMods.NormaliseForSkill))
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        }
        public PpTargetPassSample[] For(IReadOnlyList<string> mods, string? json) =>
            bySetup.GetValueOrDefault(ScoreMods.Configuration(mods, json, PpTargetMods.NormaliseForSkill)) ?? [];
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
        return Math.Max(0, weighted(best.Values, best.Count) - before);
    }

    public static PpTargetPassEstimate? EstimatePass(PpTargetOpportunityProfile? profile,
        double stars, double bpm, int seconds, IReadOnlyList<string> mods, int beatmapId = 0, string? modsJson = null, bool legacyScore = false)
    {
        if (profile is null || !double.IsFinite(stars) || stars <= 0 || seconds <= 0) return null;
        string key = modKey(mods);
        if (key.Split(',').Any(m => m is "NF" or "SD" or "PF" or "RX" or "AP" or "AT" or "CN")) return null;
        var eligible = indexes.GetValue(profile, p => new AttemptIndex(p)).For(mods, modsJson).Where(s => s.LegacyScore == legacyScore).ToArray();
        var direct = eligible.Where(s => beatmapId > 0 && s.BeatmapId == beatmapId).ToArray();
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
        var balanced = nearby.GroupBy(s => mapKey(s.Sample)).SelectMany(g =>
        {
            double total = g.Sum(s => s.Weight);
            return g.Select(s => (s.Sample, Weight: s.Weight / Math.Max(1, total)));
        }).ToArray();
        double weight = balanced.Sum(s => s.Weight);
        if (weight < 1) return null;
        double successes = balanced.Where(s => s.Sample.Passed).Sum(s => s.Weight);
        double probability = (successes + 1) / (weight + 2);
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
            lower = Math.Pow(lower, durationRatio * 1.5);
        }
        var passedMaps = balanced.Where(s => s.Sample.Passed && s.Sample.Accuracy is not null)
            .GroupBy(s => mapKey(s.Sample)).Select(g => g.OrderByDescending(s => s.Sample.PlayedAt).First().Sample.Accuracy!.Value)
            .Order().ToArray();
        return new(probability, lower, upper, nearby.Length, maps,
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

    private static double weighted(IEnumerable<double> pp, int count) => pp.OrderDescending().Take(count)
        .Select((value, index) => value * Math.Pow(0.95, index)).Sum();
    private static string modKey(IEnumerable<string> mods) => string.Join(',', PpTargetMods.Normalise(mods));

    // PpTargetLearningModel
    private sealed record Session(string Map, string Setup, bool LegacyScore, DateTimeOffset End, double Stars,
        double Bpm, int Seconds, PpPatternFeatures Features, double[] Ratios);
    private sealed record Weighted(Session Session, double Weight);
    private static readonly ConditionalWeakTable<PpPatternProfile, ConditionalWeakTable<PpTargetOpportunityProfile, Session[]>> cache = new();

    public static PpTargetLearningForecast? LearningPredict(PpTargetOpportunityProfile? history, PpPatternProfile? patterns,
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

// Verbatim copy of aimmod-osu-v0.3.1 PpTargetPatternModel.BuildProfile, including its JSON identity.
internal static class ReferencePpTargetPatternModel
{
    private static readonly ConditionalWeakTable<ReplayAnalysisResult, Dictionary<string, PpPatternEvidence>> measuredReplays = new();
    private const string Version = PpTargetPatternModel.Version;
    private const double normalized_radius = 50;
    private const double jump_spacing = 150;
    private const double tapping_spacing = 100;
    private const double fast_interval_ms = 200;

    public static PpPatternProfile BuildProfile(IEnumerable<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IReadOnlyDictionary<Guid, PpPatternContext>? contexts = null,
        DateTimeOffset? now = null, int recencyDays = 30,
        IEnumerable<LocalBeatmapSet>? localSets = null, PpPatternProfile? cachedProfile = null)
    {
        DateTimeOffset reference = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset referenceDay = new(reference.UtcDateTime.Date, TimeSpan.Zero);
        var difficulties = (localSets ?? []).SelectMany(s => s.Difficulties)
            .GroupBy(d => d.BeatmapId).ToDictionary(g => g.Key, g => g.First());
        var evidence = new List<PpPatternEvidence>();
        var cached = (cachedProfile?.Evidence ?? []).GroupBy(e => e.ScoreId).ToDictionary(g => g.Key, g => g.First());
        LocalReplay[] history = replays.ToArray();
        LocalReplay[] recent = history.Where(r => r.RulesetShortName.Equals("osu", StringComparison.OrdinalIgnoreCase)
                     && ScoreMods.IsManualPlay(r)
                     && r.PlayedAt <= reference && r.PlayedAt >= referenceDay.AddDays(-recencyDays))
                     .GroupBy(r => r.ScoreId).Select(g => g.OrderByDescending(r => r.PlayedAt).First())
                     .GroupBy(r => r.OnlineScoreId > 0 ? $"online:{r.OnlineScoreId}" : $"local:{r.ScoreId}")
                     .Select(g => g.OrderByDescending(r => analyses.ContainsKey(r.ScoreId))
                         .ThenByDescending(r => r.IsLocallyStored).ThenBy(r => r.ScoreId).First()).OrderBy(r => r.ScoreId).ToArray();
        var hashes = difficulties.Values.Where(d => !string.IsNullOrWhiteSpace(d.BeatmapHash))
            .GroupBy(d => d.BeatmapHash.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string mapKey(LocalReplay replay) => !string.IsNullOrWhiteSpace(replay.BeatmapHash)
            ? replay.BeatmapHash.Trim().ToLowerInvariant()
            : difficulties.TryGetValue(replay.BeatmapId, out var map) && !string.IsNullOrWhiteSpace(map.BeatmapHash)
                ? map.BeatmapHash.Trim().ToLowerInvariant()
                : replay.BeatmapId != Guid.Empty ? replay.BeatmapId.ToString("N") : $"unknown:{replay.ScoreId:N}";
        double decay(LocalReplay replay) => Math.Pow(0.5,
            (referenceDay.UtcDateTime - replay.PlayedAt.UtcDateTime.Date).TotalDays / Math.Max(1, recencyDays / 2d));
        var scores = recent.Where(r => r.Passed && !ScoreMods.HasCustomSettings(r.ModsJson) && double.IsFinite(r.StarRating) && r.StarRating > 0
                && double.IsFinite(r.Accuracy) && r.Accuracy is >= 0 and <= 1 && r.TotalScore > 0
                && !mapKey(r).StartsWith("unknown:", StringComparison.Ordinal))
            .Select(r => new PpScoreSkillEvidence(r.ScoreId, mapKey(r), modsKey(r.Mods), r.PlayedAt, r.StarRating, r.Accuracy, decay(r), r.LegacyScore || r.Origin == LocalLibraryOrigin.Stable))
            .GroupBy(e => (e.MapKey, e.ModsKey, e.LegacyScore)).SelectMany(g =>
            {
                double total = g.Sum(e => e.Weight), newest = g.Max(e => e.Weight);
                return g.Select(e => e with { Weight = e.Weight / total * newest });
            }).OrderBy(e => e.ScoreId).ToArray();

        foreach (LocalReplay replay in recent)
        {
            if (!analyses.TryGetValue(replay.ScoreId, out ReplayAnalysisResult? analysis))
            {
                if (cached.TryGetValue(replay.ScoreId, out var saved) && saved.MapKey == mapKey(replay)
                    && saved.LegacyScore == (replay.LegacyScore || replay.Origin == LocalLibraryOrigin.Stable)
                    && saved.ModsKey == modsKey(replay.Mods) && saved.PlayedAt == replay.PlayedAt
                    && (saved.SetupKey is null || saved.SetupKey == ScoreMods.Configuration(replay.Mods, replay.ModsJson, PpTargetMods.NormaliseForSkill)))
                    evidence.Add(saved with { Weight = decay(replay) });
                continue;
            }
            LocalBeatmapDifficulty? sourceDifficulty = !string.IsNullOrWhiteSpace(replay.BeatmapHash)
                ? hashes.GetValueOrDefault(replay.BeatmapHash.Trim()) : difficulties.GetValueOrDefault(replay.BeatmapId);
            string measurementKey = JsonSerializer.Serialize(new { Version, replay.ScoreId, Map = mapKey(replay),
                replay.PlayedAt, replay.LegacyScore, replay.Origin, Mods = modsKey(replay.Mods), replay.ModsJson,
                Context = contexts?.GetValueOrDefault(replay.ScoreId), sourceDifficulty,
                Fallback = difficulties.GetValueOrDefault(replay.BeatmapId) });
            var measurements = measuredReplays.GetOrCreateValue(analysis);
            lock (measurements)
                if (measurements.TryGetValue(measurementKey, out var measurement))
                {
                    evidence.Add(measurement with { Weight = decay(replay) });
                    continue;
                }
            ReplayObjectJudgement[] heads = analysis.Judgements.Where(isHead)
                .GroupBy(j => j.ObjectIndex!.Value)
                .Select(g => g.OrderByDescending(j => j.ObjectType == "SliderHeadCircle")
                    .ThenByDescending(j => j.JudgementTimeMs).First())
                .OrderBy(j => j.StartTimeMs).ThenBy(j => j.ObjectIndex).ToArray();
            if (heads.Length < 3) continue;

            double? rate = contextRate(contexts, replay.ScoreId, heads);
            double? radius = contexts is not null && contexts.TryGetValue(replay.ScoreId, out var supplied) ? positive(supplied.HitRadius) : null;
            string mods = modsKey(replay.Mods);
            LocalBeatmapDifficulty? difficulty = !string.IsNullOrWhiteSpace(replay.BeatmapHash)
                ? hashes.GetValueOrDefault(replay.BeatmapHash.Trim()) : difficulties.GetValueOrDefault(replay.BeatmapId);
            if (difficulty is null && difficulties.TryGetValue(replay.BeatmapId, out var byId)
                && string.IsNullOrWhiteSpace(byId.BeatmapHash)) difficulty = byId;
            if (radius is null && difficulty is not null)
                radius = localRadius(difficulty.CircleSize, mods, replay.ModsJson);

            PpPatternPoint[] points = heads.Select((j, i) => new PpPatternPoint(j.StartTimeMs, j.ObjectPosition!.X, j.ObjectPosition.Y,
                i > 0 && j.ObjectIndex != heads[i - 1].ObjectIndex + 1)).ToArray();
            var measured = measure(points, radius, rate);
            var outcomes = new Dictionary<string, PpPatternOutcome>();
            foreach (var (pattern, indices) in measured.Patterns)
            {
                var judged = indices.Select(i => heads[i]).Where(j => judgementAccuracy(j) is not null).ToArray();
                if (judged.Length == 0) continue;
                var reasons = judged.Where(j => j.Result == "Miss")
                    .GroupBy(j => j.MissAnalysis?.Reason ?? ReplayMissReason.Unknown)
                    .OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
                outcomes[pattern] = new PpPatternOutcome(judged.Length, judged.Average(j => judgementAccuracy(j)!.Value),
                    judged.Count(j => j.Result == "Miss") / (double)judged.Length, reasons);
            }
            if (!outcomes.TryGetValue("Overall", out var overall) || overall.ObjectCount < 3) continue;
            var result = new PpPatternEvidence(replay.ScoreId, mapKey(replay), mods, replay.PlayedAt, measured.Features, decay(replay), outcomes,
                ScoreMods.Configuration(replay.Mods, replay.ModsJson, PpTargetMods.NormaliseForSkill),
                replay.LegacyScore || replay.Origin == LocalLibraryOrigin.Stable);
            lock (measurements)
            {
                if (measurements.Count >= 8) measurements.Clear();
                measurements[measurementKey] = result;
            }
            evidence.Add(result);
        }

        var suppliedIds = history.Select(r => r.ScoreId).ToHashSet();
        evidence.AddRange(cached.Values.Where(e => !suppliedIds.Contains(e.ScoreId)
                && !e.ModsKey.Split('+').Any(m => m is "RX" or "AP" or "AT" or "CN")
                && e.PlayedAt <= reference && e.PlayedAt >= referenceDay.AddDays(-recencyDays))
            .Select(e => e with { Weight = Math.Pow(.5,
                (referenceDay.UtcDateTime - e.PlayedAt.UtcDateTime.Date).TotalDays / Math.Max(1, recencyDays / 2d)) }));

        var balanced = evidence.GroupBy(e => (e.MapKey, e.ModsKey, e.SetupKey, e.LegacyScore)).SelectMany(g =>
        {
            double total = g.Sum(e => e.Weight), newest = g.Max(e => e.Weight);
            return g.Select(e => e with { Weight = e.Weight / total * newest });
        }).OrderBy(e => e.ScoreId).ToArray();
        var form = PpTargetSessionFormModel.Build(balanced, reference);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version, recencyDays, Evidence = balanced, Scores = scores, Form = form.Patterns })))).ToLowerInvariant();
        return new PpPatternProfile(identity, reference, recencyDays, balanced, scores, form);
    }

    private static (PpPatternFeatures Features, Dictionary<string, HashSet<int>> Patterns) measure(PpPatternPoint[] input, double? radius, double? rate)
    {
        radius = positive(radius); rate = positive(rate);
        int invalid = input.Count(p => !double.IsFinite(p.TimeMs) || !double.IsFinite(p.X) || !double.IsFinite(p.Y));
        var patterns = new Dictionary<string, HashSet<int>> { ["Overall"] = [], ["Jumps"] = [], ["Bursts"] = [], ["Streams"] = [], ["Speed"] = [], ["Direction changes"] = [] };
        if (invalid > 0) return (new() { InvalidPointCount = invalid, HitRadius = radius, ClockRate = rate }, patterns);
        PpPatternPoint[] points = input.OrderBy(p => p.TimeMs).ToArray();
        for (int i = 0; i < points.Length; i++) patterns["Overall"].Add(i);
        var spacing = new List<double>(); var intervals = new List<double>(); var speeds = new List<double>(); var angles = new List<double>();
        var fast = new bool[Math.Max(0, points.Length - 1)];
        int transitions = 0;
        for (int i = 1; i < points.Length; i++)
        {
            double rawDelta = points[i].TimeMs - points[i - 1].TimeMs;
            if (rawDelta <= 0 || rawDelta > 2000 || points[i].BreakBefore) continue;
            transitions++;
            double distance = Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
            double? normalized = radius is { } r ? distance * normalized_radius / r : null;
            double? delta = rate is { } speed ? rawDelta / speed : null;
            if (normalized is { } d)
            {
                spacing.Add(d);
                if (d >= jump_spacing) patterns["Jumps"].Add(i);
            }
            if (delta is { } ms)
            {
                intervals.Add(ms);
                if (ms <= fast_interval_ms) patterns["Speed"].Add(i);
                if (normalized is { } norm) speeds.Add(norm / Math.Max(25, ms) * 1000);
            }
            fast[i - 1] = delta <= fast_interval_ms && normalized <= tapping_spacing;
            if (i >= 2 && !points[i - 1].BreakBefore
                && points[i - 1].TimeMs - points[i - 2].TimeMs is > 0 and <= 2000)
            {
                double ax = points[i - 1].X - points[i - 2].X, ay = points[i - 1].Y - points[i - 2].Y;
                double bx = points[i].X - points[i - 1].X, by = points[i].Y - points[i - 1].Y;
                double magnitude = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
                if (magnitude <= 0) continue;
                double angle = Math.Acos(Math.Clamp((ax * bx + ay * by) / magnitude, -1, 1)) * 180 / Math.PI;
                angles.Add(angle);
                if (angle >= 60) patterns["Direction changes"].Add(i);
            }
        }
        for (int start = 0; start < fast.Length;)
        {
            if (!fast[start]) { start++; continue; }
            int end = start;
            while (end < fast.Length && fast[end]) end++;
            int length = end - start + 1;
            if (length >= 3)
                for (int index = start; index <= end; index++) patterns[length >= 8 ? "Streams" : "Bursts"].Add(index);
            start = end;
        }
        string[] exposurePriority = ["Streams", "Bursts", "Jumps", "Direction changes", "Speed", "Overall"];
        var exposureCounts = Enumerable.Range(0, points.Length)
            .GroupBy(index => exposurePriority.First(pattern => patterns[pattern].Contains(index)))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var features = new PpPatternFeatures
        {
            PointCount = points.Length, TransitionCount = transitions, HitRadius = radius, ClockRate = rate,
            MeanSpacing = mean(spacing), JumpDistance = spacing.Count == 0 ? null : spacing.Where(d => d >= jump_spacing).DefaultIfEmpty(0).Average(),
            PeakSpacing = spacing.Count == 0 ? null : spacing.Order().ElementAt((int)Math.Ceiling((spacing.Count - 1) * 0.9)),
            JumpFraction = spacing.Count == 0 ? null : patterns["Jumps"].Count / (double)spacing.Count,
            NotesPerSecond = intervals.Count == 0 ? null : 1000 / Math.Max(25, median(intervals)),
            PeakNotesPerSecond = intervals.Count == 0 ? null : 1000 / Math.Max(25, intervals.Order().ElementAt((int)((intervals.Count - 1) * 0.1))),
            NormalizedSpeed = mean(speeds),
            BurstFraction = radius is null || rate is null || transitions == 0 ? null : patterns["Bursts"].Count / (double)points.Length,
            StreamFraction = radius is null || rate is null || transitions == 0 ? null : patterns["Streams"].Count / (double)points.Length,
            MeanDirectionChangeDegrees = mean(angles), SharpTurnFraction = angles.Count == 0 ? null : patterns["Direction changes"].Count / (double)angles.Count,
            DurationSeconds = points.Length < 2 || rate is null ? null : (points[^1].TimeMs - points[0].TimeMs) / rate / 1000,
            PatternObjectCounts = exposureCounts,
        };
        return (features, patterns);
    }

    private static bool isHead(ReplayObjectJudgement j) => j.ObjectIndex is >= 0 && j.ObjectPosition is { } p
        && double.IsFinite(j.StartTimeMs) && float.IsFinite(p.X) && float.IsFinite(p.Y)
        && ((j.ObjectType == "HitCircle" && string.IsNullOrEmpty(j.NestedPath)) || j.ObjectType == "SliderHeadCircle");

    private static double? judgementAccuracy(ReplayObjectJudgement j) => j.MaximumResult != "Great" ? null : j.Result switch
    { "Great" => 1, "Ok" => 1d / 3, "Meh" => 1d / 6, "Miss" => 0, _ => null };

    private static double? contextRate(IReadOnlyDictionary<Guid, PpPatternContext>? contexts, Guid id, ReplayObjectJudgement[] heads)
    {
        if (contexts is not null && contexts.TryGetValue(id, out var context) && positive(context.ClockRate) is { } supplied) return supplied;
        double[] rates = heads.Select(j => positive(j.GameplayRate)).Where(r => r is not null).Select(r => r!.Value).ToArray();
        return rates.Length == heads.Length && rates.Max() - rates.Min() < 0.00001 ? rates[0] : null;
    }

    private static double? localRadius(float cs, string mods, string modsJson)
    {
        if (!float.IsFinite(cs) || cs < 0 || cs > 10) return null;
        if (hasCustomCircleSize(modsJson)) return null;
        string[] setup = mods.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (setup.Any(m => m is not ("HD" or "HR" or "EZ" or "DT" or "HT" or "FL" or "NF" or "SO" or "CL"))) return null;
        if (setup.Contains("HR")) cs = Math.Min(10, cs * 1.3f);
        if (setup.Contains("EZ")) cs *= 0.5f;
        return OsuHitObject.OBJECT_RADIUS * LegacyRulesetExtensions.CalculateScaleFromCircleSize(cs, true);
    }

    internal static string modsKey(IEnumerable<string> mods) => string.Join('+', mods.Select(m => m.Trim().Replace(" ", "").ToUpperInvariant() switch
    {
        "NOMOD" or "NM" or "" => "", "HIDDEN" => "HD", "HARDROCK" => "HR", "EASY" => "EZ",
        "DOUBLETIME" or "NIGHTCORE" or "NC" => "DT", "HALFTIME" or "DAYCORE" or "DC" => "HT",
        "FLASHLIGHT" => "FL", "NOFAIL" => "NF", "SPUNOUT" => "SO", "CLASSIC" => "CL", var acronym => acronym,
    }).Where(m => m.Length > 0).Distinct().Order(StringComparer.Ordinal));

    private static double? positive(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static bool hasCustomCircleSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return inspect(document.RootElement);
        }
        catch (JsonException) { return true; }

        static bool inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(inspect);
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                if (name is "circlesize" or "cs" or "circlesizemultiplier"
                    && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && !(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "")) return true;
                if (inspect(property.Value)) return true;
            }
            return false;
        }
    }
    private static double? mean(List<double> values) => values.Count == 0 ? null : values.Average();
    private static double median(List<double> values) { double[] sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
}
