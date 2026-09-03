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
        Setup[] ppSetups = setups.Where(setup => setup.Pp is > 0 and <= 2_000).ToArray();
        double[] stars = setups.Select(setup => setup.Stars).Order().ToArray();
        double[] bpms = setups.Where(setup => validPositive(setup.Bpm)).Select(setup => setup.Bpm!.Value).Order().ToArray();
        double[] lengths = setups.Where(setup => validPositive(setup.LengthSeconds)).Select(setup => setup.LengthSeconds!.Value).Order().ToArray();
        double[] accuracies = setups.Select(setup => setup.Accuracy).Order().ToArray();
        double[] pp = ppSetups.Select(setup => setup.Pp!.Value).Order().ToArray();

        return new PpTargetPreferenceProfile(
            runs.Length,
            setups.Length,
            pp.Length,
            preferredRange(stars, 0.5),
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
            ppSetups.Select(setup => new PpTargetPerformanceSample(setup.Stars, setup.Pp!.Value, setup.Accuracy)).ToArray());
    }

    private static Setup buildSetup(IEnumerable<LocalReplay> values, IReadOnlyDictionary<Guid, LocalBeatmapSet> sets)
    {
        LocalReplay[] runs = values.OrderByDescending(run => run.PlayedAt).ToArray();
        LocalReplay representative = runs.MaxBy(run => validPp(run.PerformancePoints) ? run.PerformancePoints : double.MinValue)!;
        sets.TryGetValue(representative.SetId, out LocalBeatmapSet? set);
        LocalBeatmapDifficulty? difficulty = set?.Difficulties.FirstOrDefault(item => item.BeatmapId == representative.BeatmapId);
        return new Setup(
            setupKey(representative), representative.PlayedAt, representative.StarRating,
            runs.Where(run => validAccuracy(run.Accuracy)).Select(run => run.Accuracy).DefaultIfEmpty(representative.Accuracy).Max(),
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
        $"{run.BeatmapId:N}|{string.Join(',', normaliseMods(run.Mods).Select(mod => mod.ToUpperInvariant()))}";

    private static string[] normaliseMods(IReadOnlyList<string>? mods) =>
        (mods ?? []).Select(clean).Where(mod => mod.Length > 0 && !string.Equals(mod, "NoMod", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool validRun(LocalReplay run) =>
        string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase)
        && run.StarRating is > 0 and <= 20 && double.IsFinite(run.StarRating)
        && validAccuracy(run.Accuracy);

    private static bool validAccuracy(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool validPp(double? value) => value is > 0 and <= 2_000 && double.IsFinite(value.Value);
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
        IReadOnlyDictionary<int, PpTargetEstimate>? exactEstimates = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(beatmapSets);
        NormalisedFilters query = normalise(filters ?? new PpTargetFilters());

        FlatCandidate[] flattened = beatmapSets.Where(set => set is not null)
            .OrderBy(set => set.BeatmapSetId)
            .SelectMany(set => (set.Difficulties ?? []).Select(difficulty => new FlatCandidate(set, difficulty)))
            .Where(candidate => validDifficulty(candidate.Difficulty))
            .GroupBy(candidate => candidate.Difficulty.BeatmapId)
            .Select(group => group.OrderBy(candidate => candidate.Set.BeatmapSetId).ThenBy(candidate => candidate.Difficulty.Name, StringComparer.Ordinal).First())
            .ToArray();

        PpTargetCandidate[] matching = flattened.Select(candidate => score(profile, candidate, exactEstimates))
            .Where(candidate => matches(candidate, query))
            .OrderByDescending(candidate => candidate.RankScore)
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
        PpTargetEstimate? estimate = exactEstimates?.GetValueOrDefault(difficulty.BeatmapId);
        double preference = preferenceFit(profile, set, difficulty);
        double attainability = rangeFit(profile.PreferredStarRange, difficulty.StarRating, 1.5);
        double? baseline = profile.CompetitivePpFloor;
        double? gain = estimate is null || baseline is null ? null : Math.Max(0, estimate.ExpectedPp - baseline.Value);
        double gainScore = gain is null || profile.HistoricalBestPp is null ? 0 : Math.Clamp(gain.Value / Math.Max(50, profile.HistoricalBestPp.Value * 0.35), 0, 1);
        double rank = 100 * (0.48 * preference + 0.37 * attainability + 0.15 * gainScore);
        string[] mods = profile.CommonMods.Where(item => item.Weight >= 0.15).Take(3).Select(item => item.Value).ToArray();

        return new PpTargetCandidate(
            set.BeatmapSetId, difficulty.BeatmapId, set.Title, set.Artist, set.Creator, set.Source, set.Status,
            difficulty.Name, difficulty.StarRating, difficulty.Bpm, difficulty.TotalLengthSeconds, difficulty.MaximumCombo,
            set.CoverUrl, preference, attainability, rank, baseline, gain, estimate, mods);
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

        if (filters.HasExpectedFilter && (candidate.Estimate is null || !between(candidate.Estimate.ExpectedPp, filters.MinimumExpectedPp, filters.MaximumExpectedPp)))
            return false;
        return !filters.HasMaximumFilter
               || candidate.Estimate is not null && between(candidate.Estimate.RealisticMaximumPp, filters.MinimumRealisticMaximumPp, filters.MaximumRealisticMaximumPp);
    }

    private static bool between(double value, double? minimum, double? maximum) =>
        (minimum is null || value >= minimum) && (maximum is null || value <= maximum);

    private static NormalisedFilters normalise(PpTargetFilters filters)
    {
        (double? minStars, double? maxStars) = range(filters.MinimumStars, filters.MaximumStars, 0, 20);
        (double? minExpected, double? maxExpected) = range(filters.MinimumExpectedPp, filters.MaximumExpectedPp, 0, 2_000);
        (double? minMaximum, double? maxMaximum) = range(filters.MinimumRealisticMaximumPp, filters.MaximumRealisticMaximumPp, 0, 2_000);
        (double? minBpm, double? maxBpm) = range(filters.MinimumBpm, filters.MaximumBpm, 0, 1_000);
        (double? minLength, double? maxLength) = range(filters.MinimumLengthSeconds, filters.MaximumLengthSeconds, 0, 86_400);
        HashSet<string> statuses = (filters.Statuses ?? []).Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] search = PpTargetPreferenceProfiler.tokenise((filters.SearchText ?? string.Empty)[..Math.Min(filters.SearchText?.Length ?? 0, 256)]).ToArray();
        return new NormalisedFilters(search, minStars, maxStars, minExpected, maxExpected, minMaximum, maxMaximum,
            minLength, maxLength, minBpm, maxBpm, statuses, Math.Clamp(filters.Limit, 1, 500));
    }

    private static (double? Minimum, double? Maximum) range(double? minimum, double? maximum, double floor, double ceiling)
    {
        minimum = validBound(minimum, floor, ceiling) ? minimum : null;
        maximum = validBound(maximum, floor, ceiling) ? maximum : null;
        return minimum > maximum ? (maximum, minimum) : (minimum, maximum);
    }

    private static bool validBound(double? value, double minimum, double maximum) =>
        value is not null && double.IsFinite(value.Value) && value >= minimum && value <= maximum;

    private static bool validDifficulty(OfficialBeatmapDifficulty difficulty) =>
        difficulty.BeatmapId > 0 && string.Equals(difficulty.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase)
        && difficulty.StarRating is > 0 and <= 20 && double.IsFinite(difficulty.StarRating)
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
