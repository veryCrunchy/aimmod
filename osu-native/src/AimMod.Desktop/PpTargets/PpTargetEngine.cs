using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public static class PpTargetPreferenceProfiler
{
    private const int maximum_history = 10_000;

    public static PpTargetPreferenceProfile Build(
        IEnumerable<LocalReplay> history,
        IEnumerable<LocalBeatmapSet>? localBeatmapSets = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        LocalReplay[] runs = history.Where(validRun)
                                    .GroupBy(run => run.ScoreId)
                                    .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                    .OrderByDescending(run => run.PlayedAt)
                                    .ThenBy(run => run.ScoreId)
                                    .Take(maximum_history)
                                    .ToArray();
        if (runs.Length == 0)
            return PpTargetPreferenceProfile.Empty;

        Dictionary<Guid, LocalBeatmapSet> sets = (localBeatmapSets ?? [])
            .GroupBy(set => set.SetId)
            .ToDictionary(group => group.Key, group => group.First());
        Setup[] setups = runs.GroupBy(setupKey, StringComparer.Ordinal)
                            .Select(group => buildSetup(group, sets))
                            .OrderByDescending(setup => setup.PlayedAt)
                            .ThenBy(setup => setup.Key, StringComparer.Ordinal)
                            .ToArray();
        Setup[] ppSetups = setups.Where(setup => validPp(setup.Pp)).ToArray();
        double[] bpms = setups.Where(setup => validPositive(setup.Bpm)).Select(setup => setup.Bpm!.Value).Order().ToArray();
        double[] lengths = setups.Where(setup => validPositive(setup.LengthSeconds)).Select(setup => setup.LengthSeconds!.Value).Order().ToArray();
        double[] accuracies = setups.Select(setup => setup.Accuracy).Order().ToArray();
        double[] pp = ppSetups.Select(setup => setup.Pp!.Value).Order().ToArray();

        DateTimeOffset newest = runs[0].PlayedAt;
        double recency(DateTimeOffset played, double halfLife) => Math.Pow(0.5, Math.Max(0, (newest - played).TotalDays) / halfLife);
        double legacyWeight = runs.Where(r => r.LegacyScore || r.Origin == LocalLibraryOrigin.Stable).Sum(r => recency(r.PlayedAt, 14));
        return new PpTargetPreferenceProfile(
            runs.Length,
            setups.Length,
            pp.Length,
            preferredStarRange(setups, newest),
            preferredRange(bpms, 15),
            preferredRange(lengths, 30),
            percentile(accuracies, 0.5),
            pp.Length == 0 ? null : percentile(pp, pp.Length >= 20 ? 0.75 : pp.Length >= 8 ? 0.65 : 0.5),
            pp.Length == 0 ? null : pp[^1],
            confidence(setups.Length),
            preferences(setups.SelectMany(setup => setup.Mods), setups.Length, 8),
            preferences(setups.Select(setup => setup.Creator), setups.Length, 8),
            preferences(setups.Select(setup => setup.Source), setups.Length, 8),
            preferences(setups.Select(setup => setup.Artist), setups.Length, 8),
            preferences(setups.SelectMany(setup => titleSignals(setup.Title)), setups.Length, 12),
            ppSetups.Select(setup => new PpTargetPerformanceSample(setup.Stars, setup.Pp!.Value, setup.Accuracy)).ToArray(),
            PreferredModSetup: setups.GroupBy(setup => string.Join(',', PpTargetMods.Normalise(setup.Mods)), StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => PpTargetMods.Normalise(group.First().Mods)).First(),
            // The client played recently decides the scoring system, not all-time volume.
            LegacyScore: legacyWeight > runs.Sum(r => recency(r.PlayedAt, 14)) / 2);
    }

    // Recency-weighted percentiles, stretched upwards to include the stars of recent high-PP passes.
    private static PpTargetRange? preferredStarRange(Setup[] setups, DateTimeOffset newest)
    {
        if (setups.Length == 0)
            return null;
        double weight(Setup s) => Math.Pow(0.5, Math.Max(0, (newest - s.PlayedAt).TotalDays) / 30);
        var stars = setups.Select(s => (Value: s.Stars, Weight: weight(s))).ToArray();
        double centre = weightedPercentile(stars, .5);
        double minimum = weightedPercentile(stars, setups.Length < 5 ? 0 : .2);
        double maximum = weightedPercentile(stars, setups.Length < 5 ? 1 : .8);
        var recent = setups.Where(s => validPp(s.Pp) && (newest - s.PlayedAt).TotalDays <= 30).ToArray();
        if (recent.Length >= 3)
        {
            double threshold = weightedPercentile(recent.Select(s => (s.Pp!.Value, weight(s))).ToArray(), .75);
            maximum = Math.Max(maximum, recent.Where(s => s.Pp >= threshold).Max(s => s.Stars));
        }
        return new PpTargetRange(Math.Max(0, Math.Min(minimum, centre - 0.25)), Math.Max(maximum, centre + 0.25));
    }

    private static double weightedPercentile(IReadOnlyList<(double Value, double Weight)> items, double fraction)
    {
        var ordered = items.OrderBy(i => i.Value).ToArray();
        double total = ordered.Sum(i => i.Weight), threshold = Math.Clamp(fraction, 0, 1) * total, cumulative = 0;
        foreach (var item in ordered)
            if ((cumulative += item.Weight) >= threshold - 1e-12) return item.Value;
        return ordered[^1].Value;
    }

    private static Setup buildSetup(IEnumerable<LocalReplay> values, IReadOnlyDictionary<Guid, LocalBeatmapSet> sets)
    {
        LocalReplay[] runs = values.OrderByDescending(run => run.PlayedAt).ToArray();
        LocalReplay representative = runs.MaxBy(run => validPp(run.PerformancePoints) ? run.PerformancePoints : double.MinValue)!;
        sets.TryGetValue(representative.SetId, out LocalBeatmapSet? set);
        LocalBeatmapDifficulty? difficulty = set?.Difficulties.FirstOrDefault(item => item.BeatmapId == representative.BeatmapId);
        return new Setup(
            setupKey(representative), representative.PlayedAt, representative.StarRating,
            representative.Accuracy,
            runs.Where(run => validPp(run.PerformancePoints)).Select(run => run.PerformancePoints!.Value).DefaultIfEmpty(double.NaN).Max() is var best && double.IsFinite(best) ? best : null,
            normaliseMods(representative.Mods), clean(set?.Creator), clean(set?.Source), clean(representative.Artist), clean(representative.Title),
            validPositive(difficulty?.Bpm) ? difficulty!.Bpm : null,
            validPositive(difficulty?.LengthMilliseconds) ? difficulty!.LengthMilliseconds / 1_000 : null);
    }

    private static IReadOnlyList<PpTargetPreference> preferences(IEnumerable<string> values, int total, int limit) =>
        values.Select(clean)
              .Where(value => value.Length > 0)
              .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
              .Select(group => new PpTargetPreference(group.Order(StringComparer.Ordinal).First(), group.Count(), Math.Clamp((double)group.Count() / Math.Max(1, total), 0, 1)))
              .OrderByDescending(item => item.Weight)
              .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
              .Take(limit)
              .ToArray();

    private static IEnumerable<string> titleSignals(string title) => tokenise(title).Where(token => token.Length >= 3);

    internal static IEnumerable<string> tokenise(string value) =>
        (value ?? string.Empty).Split([' ', '-', '_', '(', ')', '[', ']', '/', '\\', '.', ',', ':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(token => token.ToLowerInvariant())
                               .Where(token => token.Any(char.IsLetterOrDigit));

    internal static double? percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return null;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static PpTargetRange? preferredRange(double[] sorted, double minimumWidth)
    {
        if (sorted.Length == 0)
            return null;
        double centre = percentile(sorted, 0.5)!.Value;
        double minimum = percentile(sorted, sorted.Length < 5 ? 0 : 0.2)!.Value;
        double maximum = percentile(sorted, sorted.Length < 5 ? 1 : 0.8)!.Value;
        double halfWidth = Math.Max(minimumWidth / 2, (maximum - minimum) / 2);
        return new PpTargetRange(Math.Max(0, centre - halfWidth), centre + halfWidth);
    }

    private static string setupKey(LocalReplay run) =>
        ScoreMods.SetupKey(run);

    private static string[] normaliseMods(IReadOnlyList<string>? mods) =>
        (mods ?? []).Select(clean).Where(mod => mod.Length > 0 && !string.Equals(mod, "NoMod", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool validRun(LocalReplay run) =>
        string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase)
        && run.Passed && ScoreMods.IsManualPlay(run)
        && run.StarRating > 0 && double.IsFinite(run.StarRating)
        && validAccuracy(run.Accuracy);

    private static bool validAccuracy(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool validPp(double? value) => value is > 0 && double.IsFinite(value.Value);
    private static bool validPositive(double? value) => value is > 0 && double.IsFinite(value.Value);
    private static string clean(string? value) => (value ?? string.Empty).Trim();

    private static PpTargetConfidence confidence(int count) => count switch
    {
        >= 30 => PpTargetConfidence.High,
        >= 12 => PpTargetConfidence.Medium,
        >= 5 => PpTargetConfidence.Low,
        _ => PpTargetConfidence.Insufficient,
    };

    private sealed record Setup(
        string Key, DateTimeOffset PlayedAt, double Stars, double Accuracy, double? Pp,
        IReadOnlyList<string> Mods, string Creator, string Source, string Artist, string Title,
        double? Bpm, double? LengthSeconds);
}

public static class PpTargetRanker
{
    public static PpTargetRankingResult Rank(
        PpTargetPreferenceProfile profile,
        IEnumerable<OfficialBeatmapSet> beatmapSets,
        PpTargetFilters? filters = null,
        IReadOnlyDictionary<int, PpTargetEstimate>? exactEstimates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(beatmapSets);
        NormalisedFilters query = normalise(filters ?? new PpTargetFilters());

        cancellationToken.ThrowIfCancellationRequested();
        FlatCandidate[] flattened = beatmapSets.Where(set => { cancellationToken.ThrowIfCancellationRequested(); return set is not null; })
            .OrderBy(set => set.BeatmapSetId)
            .SelectMany(set => (set.Difficulties ?? []).Select(difficulty => new FlatCandidate(set, difficulty)))
            .Where(candidate => validDifficulty(candidate.Difficulty))
            .GroupBy(candidate => candidate.Difficulty.BeatmapId)
            .Select(group => group.OrderBy(candidate => candidate.Set.BeatmapSetId).ThenBy(candidate => candidate.Difficulty.Name, StringComparer.Ordinal).First())
            .ToArray();

        var context = new RankContext(profile);
        PpTargetCandidate[] matching = flattened.Where(candidate => matchesMetadata(candidate, query)).Select(candidate => { cancellationToken.ThrowIfCancellationRequested(); return score(profile, candidate, exactEstimates, context); })
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
        IReadOnlyDictionary<int, PpTargetEstimate>? exactEstimates,
        RankContext context)
    {
        OfficialBeatmapSet set = candidate.Set;
        OfficialBeatmapDifficulty difficulty = candidate.Difficulty;
        IReadOnlyList<string> mods = context.Mods;
        double preference = preferenceFit(profile, set, difficulty);
        (double attainability, double scoreEvidence, int nearbySampleCount) = context.PerformanceFit(difficulty.StarRating);
        PpTargetPassEstimate? passEstimate = context.Pass?.Estimate(difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds, difficulty.BeatmapId);
        double? expectedAccuracy = passEstimate?.ConditionalAccuracy ?? profile.TypicalAccuracy;
        PpTargetEstimate? estimate = matchingEstimate(
            exactEstimates?.GetValueOrDefault(difficulty.BeatmapId) is {} exact && exact.LegacyScore == profile.LegacyScore && (exact.ModsJson ?? "") == (profile.PreferredModsJson ?? "") ? exact : null,
            difficulty.BeatmapId,
            context.NormalisedMods,
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
            // Unknown coverage is neutral for ordering, not evidence of proficiency.
            // A known bottleneck still counts when a different pattern is unmeasured.
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
        double? baseline = context.Baseline(difficulty.StarRating);
        bool supportedScore = passEstimate is not null && (passEstimate.ConditionalAccuracy is not null
            || estimate?.PatternPrediction is { Fit: not null, ExpectedAccuracy: not null });
        bool awardsPp = string.Equals(set.Status, "ranked", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(set.Status, "approved", StringComparison.OrdinalIgnoreCase);
        double? gain = estimate is null || baseline is null || !awardsPp || !supportedScore
            ? null
            : Math.Max(0, estimate.ExpectedPp - baseline.Value) * passEstimate!.Probability;
        double? accountGain = awardsPp && estimate is not null && supportedScore
            ? context.AccountGain.Gain(difficulty.BeatmapId, estimate.ExpectedPp) * passEstimate!.Probability : null;
        double? gainPerMinute = accountGain is { } account && difficulty.TotalLengthSeconds > 0
            ? account / (difficulty.TotalLengthSeconds / 60d) : null;
        double gainScore = accountGain is null
            ? 0
            : Math.Clamp(accountGain.Value / 20, 0, 1);
        double modCompatibility = context.ModCompatibility;
        double confidenceScore = (int)recommendation / 3d;
        // Preferences and reward break ties between playable targets; neither should
        // outweigh a demonstrated mechanical weakness on a lucrative map.
        double rank = 100 * (0.60 * attainability + 0.15 * (passEstimate?.Lower ?? attainability) + 0.10 * preference
                            + 0.08 * gainScore * attainability
                            + 0.04 * modCompatibility + 0.03 * confidenceScore);
        // A reliable play that cannot improve any known best score is a warm-up,
        // not a leading PP target. Keep mechanical fit dominant within useful gains.
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
            awardsPp ? PpTargetForecastModel.Forecast(estimate, passEstimate,
                context.Learning.Predict(estimate, difficulty.BeatmapId, difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds)) : null);
    }

    private static PpTargetEstimate? matchingEstimate(
        PpTargetEstimate? estimate,
        int beatmapId,
        IReadOnlyList<string> normalisedMods,
        double? expectedAccuracy,
        double attainability)
    {
        if (estimate is null || estimate.BeatmapId is { } estimateBeatmapId && estimateBeatmapId != beatmapId)
            return null;
        if (estimate.Mods is not null && !PpTargetMods.Normalise(estimate.Mods).SequenceEqual(normalisedMods))
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
        double starRating,
        bool customSettings,
        PpTargetPatternModel.ScoreFitIndex? scoreFit,
        NearestSamples samples)
    {
        if (customSettings) return (.5, 0, 0);
        if (scoreFit is not null)
        {
            var support = scoreFit.Fit(starRating);
            return (support.Fit, support.Confidence, support.Maps);
        }
        PpTargetPerformanceSample[] nearby = samples.ByAccuracy(starRating);
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

    private static double? difficultyBaseline(NearestSamples samples, double starRating)
    {
        double[] nearbyPp = samples.PerformancePoints(starRating);
        return nearbyPp.Length == 0
            ? null
            : PpTargetPreferenceProfiler.percentile(nearbyPp, nearbyPp.Length >= 8 ? 0.65 : 0.5);
    }

    // Everything derived from the profile alone is computed once per ranking; star-dependent fits
    // are memoised per exact star rating.
    private sealed class RankContext
    {
        private readonly PpTargetPreferenceProfile profile;
        private readonly bool customSettings;
        private readonly PpTargetPatternModel.ScoreFitIndex? scoreFit;
        private readonly NearestSamples samples;
        private readonly Dictionary<double, (double Attainability, double Evidence, int SampleCount)> fits = new();
        private readonly Dictionary<double, double?> baselines = new();

        public RankContext(PpTargetPreferenceProfile profile)
        {
            this.profile = profile;
            Mods = profile.PreferredModSetup ?? PpTargetMods.SelectCompatible(profile.CommonMods);
            NormalisedMods = PpTargetMods.Normalise(Mods);
            ModCompatibility = modFit(profile.CommonMods, Mods);
            customSettings = ScoreMods.HasCustomSettings(profile.PreferredModsJson);
            scoreFit = !customSettings && profile.PatternProfile is { ScoreEvidence: not null } recent
                ? PpTargetPatternModel.ScoreFitIndex.Create(recent, Mods, profile.LegacyScore)
                : null;
            samples = new NearestSamples(profile.PerformanceSamples);
            Pass = profile.Opportunities is { } opportunities
                ? new PpTargetOpportunityModel.PassEstimator(opportunities, Mods, profile.PreferredModsJson, profile.LegacyScore)
                : null;
            AccountGain = new PpTargetOpportunityModel.AccountGainIndex(profile.Opportunities);
            Learning = new PpTargetLearningModel.Predictor(profile.Opportunities, profile.PatternProfile, Mods, profile.PreferredModsJson, profile.LegacyScore);
        }

        public IReadOnlyList<string> Mods { get; }
        public IReadOnlyList<string> NormalisedMods { get; }
        public double ModCompatibility { get; }
        public PpTargetOpportunityModel.PassEstimator? Pass { get; }
        public PpTargetOpportunityModel.AccountGainIndex AccountGain { get; }
        public PpTargetLearningModel.Predictor Learning { get; }

        public (double Attainability, double Evidence, int SampleCount) PerformanceFit(double stars)
        {
            if (!fits.TryGetValue(stars, out var fit))
                fits[stars] = fit = performanceFit(profile, stars, customSettings, scoreFit, samples);
            return fit;
        }

        public double? Baseline(double stars)
        {
            if (!baselines.TryGetValue(stars, out double? baseline))
                baselines[stars] = baseline = difficultyBaseline(samples, stars);
            return baseline;
        }
    }

    // Samples sorted by star rating so the nearest 24 come from a binary search and an outward walk.
    // Ties resolve exactly like the stable OrderBy chains over the original sample order.
    private sealed class NearestSamples
    {
        private const int limit = 24;
        private readonly (PpTargetPerformanceSample Sample, int Order)[] accuracySamples;
        private readonly double[] accuracyStars;
        private readonly (PpTargetPerformanceSample Sample, int Order)[] performanceSamples;
        private readonly double[] performanceStars;

        public NearestSamples(IReadOnlyList<PpTargetPerformanceSample> samples)
        {
            (accuracySamples, accuracyStars) = index(samples, sample => double.IsFinite(sample.StarRating) && sample.StarRating > 0
                && double.IsFinite(sample.Accuracy) && sample.Accuracy is >= 0 and <= 1);
            (performanceSamples, performanceStars) = index(samples, sample => double.IsFinite(sample.StarRating)
                && double.IsFinite(sample.PerformancePoints) && sample.PerformancePoints > 0);
        }

        public PpTargetPerformanceSample[] ByAccuracy(double stars) =>
            nearest(accuracySamples, accuracyStars, stars, double.PositiveInfinity)
                .OrderBy(item => Math.Abs(item.Sample.StarRating - stars))
                .ThenByDescending(item => item.Sample.Accuracy)
                .ThenBy(item => item.Order)
                .Take(limit)
                .Select(item => item.Sample)
                .ToArray();

        public double[] PerformancePoints(double stars) =>
            nearest(performanceSamples, performanceStars, stars, 1.25)
                .OrderBy(item => Math.Abs(item.Sample.StarRating - stars))
                .ThenBy(item => item.Order)
                .Take(limit)
                .Select(item => item.Sample.PerformancePoints)
                .Order()
                .ToArray();

        private static ((PpTargetPerformanceSample, int)[], double[]) index(
            IReadOnlyList<PpTargetPerformanceSample> samples, Func<PpTargetPerformanceSample, bool> valid)
        {
            var sorted = samples.Select((sample, order) => (Sample: sample, Order: order))
                .Where(item => valid(item.Sample))
                .OrderBy(item => item.Sample.StarRating)
                .ThenBy(item => item.Order)
                .ToArray();
            return (sorted, sorted.Select(item => item.Sample.StarRating).ToArray());
        }

        private static List<(PpTargetPerformanceSample Sample, int Order)> nearest(
            (PpTargetPerformanceSample Sample, int Order)[] sorted, double[] stars, double target, double maximumDistance)
        {
            var chosen = new List<(PpTargetPerformanceSample, int)>(limit + 4);
            if (sorted.Length == 0 || !double.IsFinite(target))
                return chosen;
            int right = Array.BinarySearch(stars, target);
            if (right < 0)
                right = ~right;
            else
                while (right > 0 && stars[right - 1] >= target)
                    right--;
            int left = right - 1;
            double boundary = double.NaN;
            while (true)
            {
                double leftDistance = left >= 0 ? Math.Abs(stars[left] - target) : double.PositiveInfinity;
                double rightDistance = right < sorted.Length ? Math.Abs(stars[right] - target) : double.PositiveInfinity;
                double distance = Math.Min(leftDistance, rightDistance);
                if (double.IsPositiveInfinity(distance) || distance > maximumDistance)
                    break;
                if (chosen.Count >= limit && distance > boundary)
                    break;
                if (leftDistance <= rightDistance)
                    chosen.Add(sorted[left--]);
                else
                    chosen.Add(sorted[right++]);
                if (chosen.Count == limit)
                    boundary = distance;
            }
            return chosen;
        }
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

    private static double confidenceScoreFor(int nearbySamples, PpTargetConfidence profileConfidence) =>
        Math.Min((int)recommendationConfidence(nearbySamples, profileConfidence), (int)PpTargetConfidence.High) / 3d;

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
}
