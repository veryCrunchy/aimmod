namespace AimMod.Desktop.Skins.Online;

public sealed class OnlineSkinCatalogBackend : IDisposable
{
    private readonly SecureSkinHttpClient http;

    public OnlineSkinCatalogBackend(string cacheRoot, string previewRoot)
    {
        http = new SecureSkinHttpClient();
        Cache = new OnlineSkinCatalogCache(cacheRoot);
        var validator = new OnlineSkinArchiveValidator();
        IOnlineSkinCatalogProvider[] providers =
        [
            new CachedOnlineSkinCatalogProvider(new OsuSkinsNetCatalogProvider(http), Cache),
            new CachedOnlineSkinCatalogProvider(new OsuckNetSkinCatalogProvider(http), Cache),
        ];
        Catalog = new OnlineSkinCatalogService(providers);
        var resolvers = new OnlineSkinDownloadResolverPipeline(
            new GoogleDriveSkinDownloadResolver(http, validator),
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
        Previews = new OnlineSkinPreviewService(previewRoot, Cache, resolvers, validator);
    }

    public OnlineSkinCatalogCache Cache { get; }
    public OnlineSkinCatalogService Catalog { get; }
    public OnlineSkinPreviewService Previews { get; }

    public void Dispose() => http.Dispose();
}
