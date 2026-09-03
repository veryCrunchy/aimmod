using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public sealed class ExternalLazerSkinCatalogClient(IRuntimeRequestClient runtimeClient)
{
    public async Task<ExternalLazerSkinCatalogSearchResult> SearchAsync(
        ExternalLazerSkinCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RuntimeRequest runtimeRequest = RuntimeProtocol.CreateRequest(RuntimeCommands.SearchExternalLazerSkins, request);
        RuntimeResponse response = await runtimeClient.SendAsync(runtimeRequest, cancellationToken).ConfigureAwait(false);

        if (response.Id != runtimeRequest.Id || response.ProtocolVersion != RuntimeProtocol.CurrentVersion)
            throw invalidResponse();
        if (!response.Success)
        {
            if (response.Payload is not null || response.Error is null)
                throw invalidResponse();
            throw new ExternalLazerSkinClientException(response.Error.Code, response.Error.Message);
        }
        if (response.Error is not null || response.Payload is null)
            throw invalidResponse();

        try
        {
            ExternalLazerSkinCatalogSearchResult result = response.Payload.Value.Deserialize<ExternalLazerSkinCatalogSearchResult>(RuntimeProtocol.JsonOptions)
                                                          ?? throw invalidResponse();
            validateResult(request, result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw invalidResponse();
        }
    }

    private static void validateResult(ExternalLazerSkinCatalogSearchRequest request, ExternalLazerSkinCatalogSearchResult result)
    {
        if (result.Skins is null
            || result.Offset != request.Offset
            || result.Limit != request.Limit
            || result.Offset is < 0 or > ExternalLazerSkinProtocol.MaximumOffset
            || result.Limit is < 1 or > ExternalLazerSkinProtocol.MaximumPageSize
            || result.Skins.Count > result.Limit
            || result.Total < result.Skins.Count
            || result.Total > ExternalLazerSkinProtocol.MaximumSkins
            || request.SkinId is { } requestedSkinId && result.Skins.Any(skin => skin.SkinId != requestedSkinId)
            || result.Skins.Any(skin => skin is null || !validSkin(skin)))
        {
            throw invalidResponse();
        }
    }

    private static bool validSkin(ExternalLazerSkinSummary skin) =>
        skin.SkinId != Guid.Empty
        && validText(skin.Name)
        && validText(skin.Creator)
        && validOptionalHash(skin.ContentHash)
        && skin.FileCount is >= 0 and <= ExternalLazerSkinProtocol.MaximumFilesPerSkin
        && validOptionalHash(skin.PreviewHash)
        && validText(skin.PreviewLogicalName)
        && (skin.PreviewHash.Length == 0) == (skin.PreviewLogicalName.Length == 0);

    private static bool validText(string value) =>
        value is not null && value.Length <= ExternalLazerSkinProtocol.MaximumTextFieldLength;

    private static bool validOptionalHash(string value) =>
        value is not null && (value.Length == 0 || value.Length == 64 && value.All(Uri.IsHexDigit));

    private static ExternalLazerSkinClientException invalidResponse() =>
        new("invalid_worker_response", "The osu runtime worker returned an invalid installed-skin result.");
}

public sealed class ExternalLazerSkinClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
