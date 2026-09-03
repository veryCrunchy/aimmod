using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.Osu.Runtime.Contracts;

public static class RuntimeProtocol
{
    public const int CurrentVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    public static RuntimeRequest CreateRequest(string command, object? payload = null) =>
        new(Guid.NewGuid(), CurrentVersion, command, payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions));
}

public static class RuntimeProtocolFraming
{
    public const int MaximumRequestLineCharacters = 1024 * 1024;
    public const int MaximumResponseLineCharacters = 64 * 1024 * 1024;
    public const int LineReadBufferCharacters = 4 * 1024;
}

public static class RuntimeCommands
{
    public const string Hello = "hello";
    public const string ProbeLibrary = "library.probe";
    public const string SearchBeatmapSets = "beatmaps.search";
    public const string ReadReplay = "replays.read";
    public const string AnalyseReplay = "replays.analyse";
    public const string CalculatePp = "pp.whatif.calculate";
    public const string SearchExternalLazerCatalog = "library.catalog.search";
    public const string SearchExternalLazerSkins = "skins.installed.search";
    public const string ResolveExternalLazerAssets = "library.resolve-assets";
    public const string ResolveSkin = "skins.resolve";
    public const string ResolveAudio = "audio.resolve";
    public const string Shutdown = "shutdown";
}

public static class RuntimeCapabilities
{
    public const string LibraryRead = "library.read";
    public const string BeatmapSearch = "beatmaps.search";
    public const string ReplayDecode = "replays.decode";
    public const string ReplayAnalysis = "replays.analyse";
    public const string PerformanceCalculation = "pp.whatif.calculate";
    public const string ExternalLibraryCatalog = "library.catalog.read";
    public const string ExternalLibraryAssets = "library.resolve-assets";
    public const string SkinRead = "skins.read";
    public const string AudioResolve = "audio.resolve";
}

public sealed record RuntimeRequest(Guid Id, int ProtocolVersion, string Command, JsonElement? Payload);

public sealed record RuntimeResponse(
    Guid Id,
    int ProtocolVersion,
    bool Success,
    JsonElement? Payload = null,
    RuntimeError? Error = null);

public sealed record RuntimeError(string Code, string Message);

public sealed record RuntimeHello(string RuntimeName, string RuntimeVersion, IReadOnlyList<string> Capabilities);

/// <summary>
/// A replay analysis job over files copied into an isolated staging directory by AimMod.
/// The worker never discovers or opens the user's live osu! storage.
/// </summary>
public sealed record ReplayAnalysisRequest(string StagingDirectory, string BeatmapPath, string ReplayPath);

public sealed record ReplayAnalysisResult(
    string EngineVersion,
    string TimeBasis,
    bool HeadlessAudioMuted,
    int WallClockTimeoutMs,
    IReadOnlyList<int> Pauses,
    IReadOnlyList<ReplayObjectJudgement> Judgements,
    ReplayJudgementSummary Summary,
    ReplayAnalysisContentIdentity? ContentIdentity = null);

public sealed record ReplayAnalysisContentIdentity(string BeatmapSha256, string ReplaySha256);

public sealed record ReplayObjectJudgement(
    int? ObjectIndex,
    string? NestedPath,
    string ObjectType,
    double StartTimeMs,
    double EndTimeMs,
    string Result,
    string MaximumResult,
    double JudgementTimeMs,
    double TimeOffsetMs,
    double? GameplayRate,
    ReplayPoint? ObjectPosition,
    ReplayPoint? CursorPosition,
    int ComboBefore,
    int ComboAfter);

public sealed record ReplayPoint(float X, float Y);

public sealed record ReplayJudgementSummary(int Great, int Ok, int Meh, int Miss, int SliderBreaks, int Other)
{
    public static ReplayJudgementSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public static class ReplayAnalysisProtocol
{
    public const string EngineVersion = "ppy.osu.Game/2026.730.0";
    public const int WallClockTimeoutMs = 120_000;
    public const long MaximumBeatmapBytes = 16 * 1024 * 1024;
    public const long MaximumReplayBytes = 64 * 1024 * 1024;
    public const int MaximumJudgements = 50_000;
    public const int MaximumPauses = 10_000;
}

public sealed record PpWhatIfRequest(
    string StagingDirectory,
    string BeatmapPath,
    IReadOnlyList<string> Mods,
    double Accuracy,
    int MissCount = 0,
    int? MaxCombo = null,
    PpScoreStatistics? Statistics = null,
    string? ModsJson = null);

public sealed record PpScoreStatistics(
    int Great,
    int Ok,
    int Meh,
    int Miss,
    int SliderTailHit,
    int LargeTickMiss);

public sealed record PpWhatIfResult(
    string EngineVersion,
    int DifficultyVersion,
    double StarRating,
    int MaxCombo,
    int ObjectCount,
    int Great,
    int Ok,
    int Meh,
    int Miss,
    double Accuracy,
    double PerformancePoints,
    double? Aim,
    double? Speed,
    double? AccuracyValue,
    double? Flashlight,
    double? Reading,
    double? EffectiveMissCount);

public static class PpCalculationProtocol
{
    public const string EngineVersion = ReplayAnalysisProtocol.EngineVersion;
    public const long MaximumBeatmapBytes = ReplayAnalysisProtocol.MaximumBeatmapBytes;
    public const int MaximumMods = ExternalLazerCatalogProtocol.MaximumMods;
    public const int MaximumModAcronymLength = ExternalLazerCatalogProtocol.MaximumModAcronymLength;
}

public sealed record ExternalLazerAssetResolveRequest(
    string LibraryRoot,
    string StagingDirectory,
    IReadOnlyList<string> BeatmapHashes,
    IReadOnlyList<Guid> ScoreIds,
    IReadOnlyList<Guid>? SkinIds = null);

public sealed record ExternalLazerResolvedAsset(
    string Kind,
    string OwnerId,
    string LogicalName,
    string Sha256Hash,
    string StagedPath,
    long Length);

public sealed record ExternalLazerMissingAsset(
    string Kind,
    string OwnerId,
    string LogicalName,
    string Sha256Hash,
    string Code);

public sealed record ExternalLazerAssetResolveResult(
    IReadOnlyList<ExternalLazerResolvedAsset> Files,
    IReadOnlyList<ExternalLazerMissingAsset> MissingFiles,
    IReadOnlyList<string> MissingBeatmaps,
    IReadOnlyList<Guid> MissingScores,
    IReadOnlyList<Guid>? MissingSkins = null);

public static class ExternalLazerAssetProtocol
{
    public const int MaximumBeatmapSelections = 512;
    public const int MaximumScoreSelections = 512;
    public const int MaximumSkinSelections = 1;
    public const int MaximumFiles = 8_192;
    public const int MaximumLogicalNameCharacters = 1_024;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
}

public sealed record ExternalLazerSkinCatalogSearchRequest(
    string LibraryRoot,
    string SearchText = "",
    int Offset = 0,
    int Limit = 60,
    Guid? SkinId = null);

public sealed record ExternalLazerSkinCatalogSearchResult(
    IReadOnlyList<ExternalLazerSkinSummary> Skins,
    int Total,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + Skins.Count < Total;
}

public sealed record ExternalLazerSkinSummary(
    Guid SkinId,
    string Name,
    string Creator,
    string ContentHash,
    bool IsBuiltIn,
    int FileCount,
    string PreviewHash = "",
    string PreviewLogicalName = "");

public static class ExternalLazerSkinProtocol
{
    public const int MaximumSearchTextLength = 256;
    public const int MaximumPageSize = 100;
    public const int MaximumOffset = 10_000;
    public const int MaximumSkins = 10_000;
    public const int MaximumFilesPerSkin = 8_192;
    public const int MaximumTextFieldLength = 4_096;
}

public enum ExternalLazerCatalogEntryKind
{
    BeatmapSets,
    Replays,
}

public enum ExternalLazerCatalogSort
{
    RecentlyAdded,
    RecentlyPlayed,
    Title,
    StarRating,
    Score,
    Accuracy,
}

public sealed record ExternalLazerCatalogSearchRequest(
    string LibraryRoot,
    ExternalLazerCatalogEntryKind Kind,
    string SearchText = "",
    string RulesetShortName = "osu",
    double? MinimumStars = null,
    double? MaximumStars = null,
    ExternalLazerCatalogSort Sort = ExternalLazerCatalogSort.RecentlyAdded,
    int Offset = 0,
    int Limit = 60);

public sealed record ExternalLazerCatalogSearchResult(
    ExternalLazerCatalogEntryKind Kind,
    IReadOnlyList<ExternalLazerBeatmapSet> BeatmapSets,
    IReadOnlyList<ExternalLazerReplaySummary> Replays,
    int Total,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + (Kind == ExternalLazerCatalogEntryKind.BeatmapSets ? BeatmapSets.Count : Replays.Count) < Total;
}

public sealed record ExternalLazerBeatmapSet(
    Guid SetId,
    int OnlineId,
    string Title,
    string Artist,
    string Creator,
    string Source,
    DateTimeOffset DateAdded,
    DateTimeOffset? LastPlayed,
    IReadOnlyList<ExternalLazerBeatmapDifficulty> Difficulties,
    int LocalReplayCount,
    string BackgroundHash = "");

public sealed record ExternalLazerBeatmapDifficulty(
    Guid BeatmapId,
    int OnlineId,
    string BeatmapHash,
    string Md5Hash,
    string Name,
    string RulesetShortName,
    double StarRating,
    double Bpm,
    double LengthMilliseconds,
    float CircleSize,
    float ApproachRate,
    float OverallDifficulty,
    float DrainRate,
    int LocalScoreCount);

public sealed record ExternalLazerReplaySummary(
    Guid ScoreId,
    Guid SetId,
    Guid BeatmapId,
    string BeatmapHash,
    string Title,
    string Artist,
    string Difficulty,
    string RulesetShortName,
    string Player,
    DateTimeOffset PlayedAt,
    double StarRating,
    double Accuracy,
    long TotalScore,
    int MaxCombo,
    int MissCount,
    double? PerformancePoints,
    IReadOnlyList<string> Mods,
    bool HasReplayFile,
    string BackgroundHash = "",
    PpScoreStatistics? HitStatistics = null,
    string ModsJson = "",
    long OnlineScoreId = 0);

public static class ExternalLazerCatalogProtocol
{
    public const int MaximumSearchTextLength = 256;
    public const int MaximumRulesetShortNameLength = 64;
    public const int MaximumPageSize = 200;
    public const int MaximumOffset = 100_000;
    public const int MaximumSnapshotRows = 250_000;
    public const int MaximumTextFieldLength = 4_096;
    public const int MaximumDifficultiesPerSet = 128;
    public const int MaximumMods = 64;
    public const int MaximumModAcronymLength = 32;
    public const int MaximumFilesPerScore = 64;
    public const double MaximumStarRating = 100;
}
