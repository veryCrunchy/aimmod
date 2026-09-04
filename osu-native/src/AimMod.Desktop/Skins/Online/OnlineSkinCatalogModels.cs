namespace AimMod.Desktop.Skins.Online;

public enum OnlineSkinRuleset
{
    Any,
    Standard,
    Mania,
    Taiko,
    Catch,
}

public enum OnlineSkinSort
{
    Newest,
    Downloads,
    Views,
    Name,
    Random,
}

public enum OnlineSkinCatalogStatus
{
    Success,
    Unavailable,
    InvalidResponse,
}

public enum OnlineSkinDownloadKind
{
    DirectHttps,
    GoogleDrive,
    Mega,
    FormPost,
    External,
}

public sealed record OnlineSkinCatalogQuery(
    string SearchText = "",
    OnlineSkinRuleset Ruleset = OnlineSkinRuleset.Standard,
    OnlineSkinSort Sort = OnlineSkinSort.Newest,
    bool Descending = true,
    bool IncludeSensitive = false,
    int Page = 1,
    int PageSize = 30)
{
    public OnlineSkinCatalogQuery Normalize() => this with
    {
        SearchText = (SearchText ?? string.Empty).Trim(),
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, 60),
    };
}

public sealed record OnlineSkinSourceAttribution(string ProviderId, string ProviderName, Uri SourceUri, string Notice);

public sealed record OnlineSkinDownloadTarget(
    Uri Uri,
    OnlineSkinDownloadKind Kind,
    IReadOnlyList<string> AllowedHosts,
    string? FileName = null,
    Uri? BrowserHandoffUri = null);

public sealed record OnlineSkinCatalogEntry(
    string ProviderId,
    string Id,
    string Name,
    string Creator,
    Uri DetailsUri,
    IReadOnlyList<Uri> PreviewUris,
    OnlineSkinSourceAttribution Attribution,
    OnlineSkinDownloadTarget? Download = null,
    IReadOnlyList<OnlineSkinRuleset>? Rulesets = null,
    bool IsSensitive = false,
    long? DownloadCount = null,
    long? ViewCount = null,
    long? FileSizeBytes = null,
    DateTimeOffset? PublishedAt = null,
    string? Variant = null)
{
    public IReadOnlyList<OnlineSkinRuleset> SupportedRulesets { get; init; } = Rulesets ?? [];
}

public sealed record OnlineSkinCatalogPage(
    OnlineSkinCatalogStatus Status,
    IReadOnlyList<OnlineSkinCatalogEntry> Items,
    int Page,
    int PageSize,
    bool HasMore,
    string? Message = null)
{
    public static OnlineSkinCatalogPage Unavailable(OnlineSkinCatalogQuery query, string message) =>
        new(OnlineSkinCatalogStatus.Unavailable, [], query.Page, query.PageSize, false, message);
}

public interface IOnlineSkinCatalogProvider
{
    string Id { get; }
    string DisplayName { get; }
    Uri HomePage { get; }

    Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default);
    Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default);
}
