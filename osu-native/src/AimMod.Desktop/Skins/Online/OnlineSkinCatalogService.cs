namespace AimMod.Desktop.Skins.Online;

public sealed record OnlineSkinProviderResult(
    string ProviderId,
    string ProviderName,
    Uri HomePage,
    OnlineSkinCatalogPage Page);

public sealed record OnlineSkinCatalogSearchResult(IReadOnlyList<OnlineSkinProviderResult> Providers)
{
    public IReadOnlyList<OnlineSkinCatalogEntry> Items => Providers
        .Where(provider => provider.Page.Status == OnlineSkinCatalogStatus.Success)
        .SelectMany(provider => provider.Page.Items)
        .ToArray();
}

public sealed class OnlineSkinCatalogService
{
    private readonly IReadOnlyDictionary<string, IOnlineSkinCatalogProvider> providers;

    public OnlineSkinCatalogService(IEnumerable<IOnlineSkinCatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers
            .Where(provider => provider is not null)
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (this.providers.Count == 0)
            throw new ArgumentException("At least one online skin provider is required.", nameof(providers));
    }

    public IReadOnlyList<IOnlineSkinCatalogProvider> Providers => providers.Values.ToArray();

    public async Task<OnlineSkinCatalogSearchResult> SearchAsync(
        OnlineSkinCatalogQuery query,
        IReadOnlyCollection<string>? providerIds = null,
        CancellationToken cancellationToken = default)
    {
        query = query.Normalize();
        IOnlineSkinCatalogProvider[] selected = providerIds is null || providerIds.Count == 0
            ? providers.Values.ToArray()
            : providerIds.Select(id => providers.GetValueOrDefault(id))
                .Where(provider => provider is not null)
                .Cast<IOnlineSkinCatalogProvider>()
                .Distinct()
                .ToArray();
        OnlineSkinProviderResult[] results = await Task.WhenAll(selected.Select(async provider =>
            new OnlineSkinProviderResult(
                provider.Id,
                provider.DisplayName,
                provider.HomePage,
                await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        return new OnlineSkinCatalogSearchResult(results);
    }

    public Task<OnlineSkinCatalogEntry?> GetDetailsAsync(
        string providerId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return providers.TryGetValue(providerId, out IOnlineSkinCatalogProvider? provider)
            ? provider.GetDetailsAsync(id, cancellationToken)
            : Task.FromResult<OnlineSkinCatalogEntry?>(null);
    }
}
