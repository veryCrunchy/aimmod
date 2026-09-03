using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public interface IExternalLazerCatalogClient
{
    Task<ExternalLazerCatalogSearchResult> SearchAsync(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalLazerCatalogClient(IRuntimeRequestClient runtimeClient) : IExternalLazerCatalogClient
{
    public async Task<ExternalLazerCatalogSearchResult> SearchAsync(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RuntimeRequest runtimeRequest = RuntimeProtocol.CreateRequest(RuntimeCommands.SearchExternalLazerCatalog, request);
        RuntimeResponse response = await runtimeClient.SendAsync(runtimeRequest, cancellationToken).ConfigureAwait(false);

        if (response.Id != runtimeRequest.Id || response.ProtocolVersion != RuntimeProtocol.CurrentVersion)
            throw invalidResponse();

        if (!response.Success)
        {
            if (response.Payload is not null || response.Error is null)
                throw invalidResponse();

            throw new ExternalLazerCatalogClientException(response.Error.Code, response.Error.Message);
        }

        if (response.Error is not null || response.Payload is null)
            throw invalidResponse();

        try
        {
            ExternalLazerCatalogSearchResult result = response.Payload.Value.Deserialize<ExternalLazerCatalogSearchResult>(RuntimeProtocol.JsonOptions)
                                                      ?? throw invalidResponse();
            validateResult(request, result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw invalidResponse();
        }
    }

    private static void validateResult(ExternalLazerCatalogSearchRequest request, ExternalLazerCatalogSearchResult result)
    {
        int itemCount = result.Kind == ExternalLazerCatalogEntryKind.BeatmapSets
            ? result.BeatmapSets?.Count ?? -1
            : result.Replays?.Count ?? -1;
        bool listsValid = result.Kind == ExternalLazerCatalogEntryKind.BeatmapSets
            ? result.Replays is { Count: 0 }
            : result.BeatmapSets is { Count: 0 };

        if (!Enum.IsDefined(result.Kind)
            || result.Kind != request.Kind
            || result.BeatmapSets is null
            || result.Replays is null
            || !listsValid
            || result.Offset != request.Offset
            || result.Limit != request.Limit
            || result.Limit is < 1 or > ExternalLazerCatalogProtocol.MaximumPageSize
            || result.Offset is < 0 or > ExternalLazerCatalogProtocol.MaximumOffset
            || itemCount < 0
            || itemCount > result.Limit
            || result.Total < itemCount
            || result.Total > ExternalLazerCatalogProtocol.MaximumSnapshotRows)
        {
            throw invalidResponse();
        }

        if (result.BeatmapSets.Any(set => set is null || !validSet(set))
            || result.Replays.Any(replay => replay is null || !validReplay(replay)))
        {
            throw invalidResponse();
        }
    }

    private static bool validSet(ExternalLazerBeatmapSet set) =>
        set.SetId != Guid.Empty
        && validText(set.Title)
        && validText(set.Artist)
        && validText(set.Creator)
        && validText(set.Source)
        && set.Difficulties is { Count: > 0 and <= ExternalLazerCatalogProtocol.MaximumDifficultiesPerSet }
        && set.LocalReplayCount >= 0
        && set.Difficulties.All(difficulty => difficulty is not null && validDifficulty(difficulty));

    private static bool validDifficulty(ExternalLazerBeatmapDifficulty difficulty) =>
        difficulty.BeatmapId != Guid.Empty
        && validHash(difficulty.BeatmapHash, 64)
        && validHash(difficulty.Md5Hash, 32)
        && validText(difficulty.Name)
        && validText(difficulty.RulesetShortName)
        && finite(difficulty.StarRating)
        && finite(difficulty.Bpm)
        && finite(difficulty.LengthMilliseconds)
        && float.IsFinite(difficulty.CircleSize)
        && float.IsFinite(difficulty.ApproachRate)
        && float.IsFinite(difficulty.OverallDifficulty)
        && float.IsFinite(difficulty.DrainRate)
        && difficulty.LocalScoreCount >= 0;

    private static bool validReplay(ExternalLazerReplaySummary replay) =>
        replay.ScoreId != Guid.Empty
        && replay.SetId != Guid.Empty
        && replay.BeatmapId != Guid.Empty
        && validHash(replay.BeatmapHash, 64)
        && validText(replay.Title)
        && validText(replay.Artist)
        && validText(replay.Difficulty)
        && validText(replay.RulesetShortName)
        && validText(replay.Player)
        && finite(replay.StarRating)
        && finite(replay.Accuracy)
        && replay.MaxCombo >= 0
        && replay.MissCount >= 0
        && (replay.PerformancePoints is null || finite(replay.PerformancePoints.Value))
        && replay.Mods is { Count: <= ExternalLazerCatalogProtocol.MaximumMods }
        && replay.Mods.All(mod => !string.IsNullOrWhiteSpace(mod) && mod.Length <= ExternalLazerCatalogProtocol.MaximumModAcronymLength);

    private static bool validText(string value) =>
        value is not null && value.Length <= ExternalLazerCatalogProtocol.MaximumTextFieldLength;

    private static bool validHash(string value, int length) =>
        value is { } && (value.Length == 0 || value.Length == length && value.All(Uri.IsHexDigit));

    private static bool finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static ExternalLazerCatalogClientException invalidResponse() =>
        new("invalid_worker_response", "The osu runtime worker returned an invalid external-library catalog result.");
}

public sealed class ExternalLazerCatalogClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
