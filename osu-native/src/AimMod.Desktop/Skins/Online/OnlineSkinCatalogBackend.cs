namespace AimMod.Desktop.Skins.Online;

public sealed class OnlineSkinCatalogBackend : IDisposable
{
    private readonly SecureSkinHttpClient http;

    public OnlineSkinCatalogBackend(string cacheRoot, string previewRoot)
    {
        http = new SecureSkinHttpClient();
        Cache = new OnlineSkinCatalogCache(cacheRoot);
        Screenshots = new SkinScreenshotCache(http, Path.Combine(cacheRoot, "screenshots"));
        var validator = new OnlineSkinArchiveValidator();
        IOnlineSkinCatalogProvider[] providers =
        [
            new CachedOnlineSkinCatalogProvider(new CreatorReleaseSkinCatalogProvider(http), Cache),
            new CachedOnlineSkinCatalogProvider(new OsuSkinsNetCatalogProvider(http), Cache),
            new CachedOnlineSkinCatalogProvider(new OsuckNetSkinCatalogProvider(http), Cache),
        ];
        Catalog = new OnlineSkinCatalogService(providers);
        var resolvers = new OnlineSkinDownloadResolverPipeline(
            new MediaFireSkinDownloadResolver(http, validator),
            new GoogleDriveSkinDownloadResolver(http, validator),
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
        Previews = new OnlineSkinPreviewService(previewRoot, Cache, resolvers, validator);
    }

    public OnlineSkinCatalogCache Cache { get; }
    public OnlineSkinCatalogService Catalog { get; }
    public OnlineSkinPreviewService Previews { get; }
    public SkinScreenshotCache Screenshots { get; }

    public bool CanDownload(OnlineSkinDownloadTarget? target) => target is not null &&
        (target.Kind is OnlineSkinDownloadKind.DirectHttps or OnlineSkinDownloadKind.GoogleDrive
         || MediaFireSkinDownloadResolver.IsPublicPage(target.Uri));

    public void Dispose() => http.Dispose();
}
