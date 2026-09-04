using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Hub;

public enum OsuHubVisibility
{
    Private,
    Unlisted,
    Public,
}

public sealed record OsuHubProfile(
    long OsuUserId,
    string Username,
    string CountryCode = "",
    string AvatarUrl = "",
    long? GlobalRank = null,
    double? PerformancePoints = null,
    long PlayCount = 0,
    long PlayTimeSeconds = 0);

public sealed record OsuHubBeatmapSet(
    string SetKey,
    long OnlineId,
    string Title,
    string Artist,
    string Creator,
    string Source,
    string CoverUrl);

public sealed record OsuHubBeatmapDifficulty(
    string DifficultyKey,
    string SetKey,
    long OnlineId,
    string Checksum,
    string Version,
    string Ruleset,
    double StarRating,
    double Bpm,
    long LengthMs,
    double CircleSize,
    double ApproachRate,
    double OverallDifficulty,
    double DrainRate,
    int MaxCombo);

public sealed record OsuHubScore(
    string ClientScoreId,
    long OnlineScoreId,
    DateTimeOffset PlayedAt,
    long TotalScore,
    double? PerformancePoints,
    double Accuracy,
    int MaxCombo,
    int Count300,
    int Count100,
    int Count50,
    int CountMiss,
    IReadOnlyList<string> Mods,
    bool Passed);

public sealed record OsuHubReplay(
    string Sha256,
    string ClientFilename,
    bool UploadFile);

public sealed record OsuHubReplayAnalysisPayload(
    string TimeBasis,
    bool HeadlessAudioMuted,
    IReadOnlyList<int> Pauses,
    ReplayJudgementSummary Summary,
    ReplayAnalysisContentIdentity? ContentIdentity,
    IReadOnlyList<ReplayObjectJudgement> Judgements);

public sealed record OsuHubAnalysis(
    int SchemaVersion,
    string EngineVersion,
    OsuHubReplayAnalysisPayload Payload);

public sealed record OsuHubSyncRequest(
    int SchemaVersion,
    string ClientUploadId,
    string ContentHash,
    string Visibility,
    OsuHubProfile Profile,
    OsuHubBeatmapSet BeatmapSet,
    OsuHubBeatmapDifficulty Difficulty,
    OsuHubScore Score,
    OsuHubReplay? Replay,
    OsuHubAnalysis? Analysis);

public sealed record OsuHubSyncResponse(
    string ShareId,
    string Visibility,
    bool Created,
    bool ReplayUploadRequired);

public sealed record OsuHubSyncInput(
    LocalReplay Replay,
    LocalBeatmapSet BeatmapSet,
    LocalBeatmapDifficulty Difficulty,
    OsuHubProfile Profile,
    ReplayAnalysisResult? Analysis,
    OsuHubVisibility Visibility = OsuHubVisibility.Private,
    bool UploadReplayFile = false,
    bool UploadAnalysis = false);

public static class OsuHubContractFactory
{
    public const int SchemaVersion = 1;
    public const int AnalysisSchemaVersion = 1;

    public static async Task<OsuHubSyncRequest> CreateAsync(
        OsuHubSyncInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Profile.OsuUserId <= 0 || string.IsNullOrWhiteSpace(input.Profile.Username))
            throw new ArgumentException("A linked osu! profile is required.", nameof(input));
        if (input.Replay.ScoreId == Guid.Empty)
            throw new ArgumentException("The replay score id is required.", nameof(input));
        if (input.Difficulty.BeatmapId != input.Replay.BeatmapId)
            throw new ArgumentException("The selected difficulty does not match the replay.", nameof(input));

        string setKey = input.BeatmapSet.OnlineId > 0
            ? $"online:{input.BeatmapSet.OnlineId}"
            : $"local:{input.BeatmapSet.SetId:N}";
        string difficultyKey = input.Difficulty.OnlineId > 0
            ? $"online:{input.Difficulty.OnlineId}"
            : !string.IsNullOrWhiteSpace(input.Difficulty.BeatmapHash)
                ? $"checksum:{input.Difficulty.BeatmapHash.Trim().ToLowerInvariant()}"
                : $"local:{input.Difficulty.BeatmapId:N}";
        string clientScoreId = input.Replay.Origin switch
        {
            LocalLibraryOrigin.Stable => $"stable:{input.Replay.ScoreId:N}",
            LocalLibraryOrigin.Online when input.Replay.OnlineScoreId > 0 => $"online:{input.Replay.OnlineScoreId}",
            _ => $"lazer:{input.Replay.ScoreId:N}",
        };

        string replaySha256 = "";
        string replayFilename = "";
        if (input.UploadReplayFile && !input.Replay.HasReplayFile)
            throw new FileNotFoundException("The selected score does not contain a replay file.", input.Replay.ReplayPath);
        bool uploadFile = input.UploadReplayFile;
        if (input.Replay.HasReplayFile && !string.IsNullOrWhiteSpace(input.Replay.ReplayPath) && File.Exists(input.Replay.ReplayPath))
        {
            replaySha256 = await hashFileAsync(input.Replay.ReplayPath, cancellationToken).ConfigureAwait(false);
            replayFilename = Path.GetFileName(input.Replay.ReplayPath);
        }
        else if (uploadFile)
        {
            throw new FileNotFoundException("The replay file selected for sharing is unavailable.", input.Replay.ReplayPath);
        }

        PpScoreStatistics statistics = input.Replay.HitStatistics
            ?? new PpScoreStatistics(0, 0, 0, input.Replay.MissCount, 0, 0);
        string contentHash = calculateContentHash(
            clientScoreId,
            difficultyKey,
            input.Replay.PlayedAt,
            input.Replay.TotalScore,
            input.Replay.Accuracy,
            input.Replay.MaxCombo,
            statistics,
            input.Replay.Mods);

        OsuHubAnalysis? analysis = input.Analysis is null || !input.UploadAnalysis
            ? null
            : new OsuHubAnalysis(
                AnalysisSchemaVersion,
                input.Analysis.EngineVersion,
                new OsuHubReplayAnalysisPayload(
                    input.Analysis.TimeBasis,
                    input.Analysis.HeadlessAudioMuted,
                    input.Analysis.Pauses,
                    input.Analysis.Summary,
                    input.Analysis.ContentIdentity,
                    input.Analysis.Judgements));

        return new OsuHubSyncRequest(
            SchemaVersion,
            clientScoreId,
            contentHash,
            input.Visibility.ToString().ToLowerInvariant(),
            input.Profile,
            new OsuHubBeatmapSet(
                setKey,
                input.BeatmapSet.OnlineId,
                input.BeatmapSet.Title,
                input.BeatmapSet.Artist,
                input.BeatmapSet.Creator,
                input.BeatmapSet.Source,
                input.BeatmapSet.OnlineId > 0
                    ? $"https://assets.ppy.sh/beatmaps/{input.BeatmapSet.OnlineId}/covers/cover.jpg"
                    : ""),
            new OsuHubBeatmapDifficulty(
                difficultyKey,
                setKey,
                input.Difficulty.OnlineId,
                input.Difficulty.BeatmapHash,
                input.Difficulty.Name,
                input.Difficulty.RulesetShortName,
                input.Difficulty.StarRating,
                input.Difficulty.Bpm,
                (long)Math.Max(0, input.Difficulty.LengthMilliseconds),
                input.Difficulty.CircleSize,
                input.Difficulty.ApproachRate,
                input.Difficulty.OverallDifficulty,
                input.Difficulty.DrainRate,
                0),
            new OsuHubScore(
                clientScoreId,
                input.Replay.OnlineScoreId,
                input.Replay.PlayedAt,
                input.Replay.TotalScore,
                input.Replay.PerformancePoints,
                Math.Clamp(input.Replay.Accuracy, 0, 1),
                input.Replay.MaxCombo,
                statistics.Great,
                statistics.Ok,
                statistics.Meh,
                statistics.Miss,
                input.Replay.Mods,
                true),
            string.IsNullOrWhiteSpace(replaySha256) ? null : new OsuHubReplay(replaySha256, replayFilename, uploadFile),
            analysis);
    }

    private static string calculateContentHash(
        string clientScoreId,
        string difficultyKey,
        DateTimeOffset playedAt,
        long totalScore,
        double accuracy,
        int maxCombo,
        PpScoreStatistics statistics,
        IReadOnlyList<string> mods)
    {
        string canonical = string.Join('\n',
            clientScoreId,
            difficultyKey,
            playedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            totalScore.ToString(CultureInfo.InvariantCulture),
            accuracy.ToString("R", CultureInfo.InvariantCulture),
            maxCombo.ToString(CultureInfo.InvariantCulture),
            statistics.Great.ToString(CultureInfo.InvariantCulture),
            statistics.Ok.ToString(CultureInfo.InvariantCulture),
            statistics.Meh.ToString(CultureInfo.InvariantCulture),
            statistics.Miss.ToString(CultureInfo.InvariantCulture),
            string.Join(',', mods.Select(mod => mod.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<string> hashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
