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
    IReadOnlyList<PpTargetPerformanceSample> PerformanceSamples)
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
    string Method);

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
    IReadOnlyList<string> SuggestedMods);

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
