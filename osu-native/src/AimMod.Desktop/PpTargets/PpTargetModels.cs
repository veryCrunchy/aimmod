using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public enum PpTargetConfidence
{
    Insufficient,
    Low,
    Medium,
    High,
}

public sealed record PpTargetRange(double Minimum, double Maximum)
{
    public bool Contains(double value) => value >= Minimum && value <= Maximum;
}

public sealed record PpTargetPreference(string Value, int SampleCount, double Weight);

public sealed record PpTargetPerformanceSample(double StarRating, double PerformancePoints, double Accuracy);

public sealed record PpTargetPreferenceProfile(
    int ValidRunCount,
    int DistinctSetupCount,
    int PpSampleCount,
    PpTargetRange? PreferredStarRange,
    PpTargetRange? PreferredBpmRange,
    PpTargetRange? PreferredLengthSecondsRange,
    double? TypicalAccuracy,
    double? CompetitivePpFloor,
    double? HistoricalBestPp,
    PpTargetConfidence Confidence,
    IReadOnlyList<PpTargetPreference> CommonMods,
    IReadOnlyList<PpTargetPreference> PreferredCreators,
    IReadOnlyList<PpTargetPreference> PreferredSources,
    IReadOnlyList<PpTargetPreference> PreferredArtists,
    IReadOnlyList<PpTargetPreference> PreferredTitleSignals,
    IReadOnlyList<PpTargetPerformanceSample> PerformanceSamples,
    PpPatternProfile? PatternProfile = null,
    IReadOnlyList<string>? PreferredModSetup = null)
{
    public static PpTargetPreferenceProfile Empty { get; } = new(
        0, 0, 0, null, null, null, null, null, null, PpTargetConfidence.Insufficient,
        [], [], [], [], [], []);
}

public sealed record PpTargetEstimate(
    double ExpectedPp,
    double RealisticMaximumPp,
    PpTargetRange ExpectedPpRange,
    int SampleCount,
    PpTargetConfidence Confidence,
    string Method,
    int? BeatmapId = null,
    IReadOnlyList<string>? Mods = null,
    double? ExpectedAccuracy = null,
    double? Attainability = null,
    PpPatternPrediction? PatternPrediction = null,
    string? PatternProfileIdentity = null);

public sealed record PpTargetFilters(
    string SearchText = "",
    double? MinimumStars = null,
    double? MaximumStars = null,
    double? MinimumExpectedPp = null,
    double? MaximumExpectedPp = null,
    double? MinimumRealisticMaximumPp = null,
    double? MaximumRealisticMaximumPp = null,
    int? MinimumLengthSeconds = null,
    int? MaximumLengthSeconds = null,
    double? MinimumBpm = null,
    double? MaximumBpm = null,
    IReadOnlyCollection<string>? Statuses = null,
    int Limit = 100);

public sealed record PpTargetCandidate(
    int BeatmapSetId,
    int BeatmapId,
    string Title,
    string Artist,
    string Creator,
    string Source,
    string Status,
    string Difficulty,
    double StarRating,
    double Bpm,
    int TotalLengthSeconds,
    int? MaximumCombo,
    Uri? CoverUrl,
    double PreferenceFit,
    double Attainability,
    double RankScore,
    double? GainBaselinePp,
    double? EstimatedAttainableGainPp,
    PpTargetEstimate? Estimate,
    IReadOnlyList<string> SuggestedMods,
    double ScoreEvidence,
    double ModCompatibility,
    PpTargetConfidence RecommendationConfidence);

public sealed record PpTargetRankingResult(
    PpTargetPreferenceProfile Profile,
    IReadOnlyList<PpTargetCandidate> Candidates,
    int FlattenedDifficultyCount,
    int MatchingDifficultyCount);

public static class PpTargetStatus
{
    public static string FromCategory(OfficialBeatmapCategory category) => category switch
    {
        OfficialBeatmapCategory.Leaderboard => "leaderboard",
        OfficialBeatmapCategory.Ranked => "ranked",
        OfficialBeatmapCategory.Qualified => "qualified",
        OfficialBeatmapCategory.Loved => "loved",
        OfficialBeatmapCategory.Pending => "pending",
        OfficialBeatmapCategory.Wip => "wip",
        OfficialBeatmapCategory.Graveyard => "graveyard",
        _ => string.Empty,
    };
}

internal static class PpTargetMods
{
    public static IReadOnlyList<string> SelectCompatible(IReadOnlyList<PpTargetPreference> preferences, int limit = 3)
    {
        var selected = new List<string>();
        foreach (PpTargetPreference preference in preferences.Where(item => item.Weight >= 0.15))
        {
            string mod = NormaliseOne(preference.Value);
            if (mod.Length == 0 || selected.Contains(mod, StringComparer.Ordinal) || conflicts(selected, mod))
                continue;

            selected.Add(mod);
            if (selected.Count >= limit)
                break;
        }

        return selected;
    }

    public static IReadOnlyList<string> Normalise(IEnumerable<string>? mods)
    {
        HashSet<string> values = (mods ?? []).Select(NormaliseOne)
                                              .Where(mod => mod.Length > 0)
                                              .ToHashSet(StringComparer.Ordinal);
        if (values.Contains("NC"))
            values.Remove("DT");
        if (values.Contains("DT") || values.Contains("NC"))
            values.Remove("HT");
        if (values.Contains("HR"))
            values.Remove("EZ");
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    public static string NormaliseOne(string? mod) => (mod ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "NM" or "NOMOD" => string.Empty,
        "HIDDEN" => "HD",
        "HARDROCK" => "HR",
        "DOUBLETIME" => "DT",
        "NIGHTCORE" => "NC",
        "FLASHLIGHT" => "FL",
        "HALFTIME" => "HT",
        "DAYCORE" or "DC" => "HT",
        "EASY" => "EZ",
        "NOFAIL" => "NF",
        "SPUNOUT" => "SO",
        "CLASSIC" => "CL",
        var value => value,
    };

    private static bool conflicts(IEnumerable<string> selected, string candidate) => selected.Any(existing =>
        (candidate is "DT" or "NC") && (existing is "DT" or "NC" or "HT")
        || candidate == "HT" && (existing is "DT" or "NC")
        || (candidate is "HR" or "EZ") && (existing is "HR" or "EZ"));
}
