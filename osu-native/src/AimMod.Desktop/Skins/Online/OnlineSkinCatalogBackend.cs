namespace AimMod.Desktop.Skins.Online;

public sealed class OnlineSkinCatalogBackend : IDisposable
{
    private readonly IDisposable? ownedHttp;
    private readonly ISkinDownloadBrowser browser;

    public OnlineSkinCatalogBackend(string cacheRoot, string previewRoot)
    {
        var http = new SecureSkinHttpClient();
        ownedHttp = http;
        Cache = new OnlineSkinCatalogCache(cacheRoot);
        Screenshots = new SkinScreenshotCache(http, Path.Combine(cacheRoot, "screenshots"));
        var validator = new OnlineSkinArchiveValidator();
        browser = new SkinDownloadBrowser(validator);
        IOnlineSkinCatalogProvider[] providers =
        [
            new CreatorReleaseSkinCatalogProvider(),
            new CachedOnlineSkinCatalogProvider(new OsuSkinsNetCatalogProvider(http), Cache),
            new CachedOnlineSkinCatalogProvider(new OsuckNetSkinCatalogProvider(http), Cache),
        ];
        Catalog = new OnlineSkinCatalogService(providers);
        var resolvers = new OnlineSkinDownloadResolverPipeline(
            new MediaFireSkinDownloadResolver(http, validator),
            new GoogleDriveSkinDownloadResolver(http, validator),
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
        Previews = new OnlineSkinPreviewService(previewRoot, Cache, resolvers, validator, browser);
    }

    /// <summary>Offline composition for tests and visual captures; no network client is created.</summary>
    internal OnlineSkinCatalogBackend(
        string cacheRoot,
        string previewRoot,
        ISecureSkinHttpClient http,
        IEnumerable<IOnlineSkinCatalogProvider> providers,
        ISkinDownloadBrowser browser)
    {
        this.browser = browser;
        Cache = new OnlineSkinCatalogCache(cacheRoot);
        Screenshots = new SkinScreenshotCache(http, Path.Combine(cacheRoot, "screenshots"));
        var validator = new OnlineSkinArchiveValidator();
        Catalog = new OnlineSkinCatalogService(providers);
        var resolvers = new OnlineSkinDownloadResolverPipeline(
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
        Previews = new OnlineSkinPreviewService(previewRoot, Cache, resolvers, validator, browser);
    }

    public OnlineSkinCatalogCache Cache { get; }
    public OnlineSkinCatalogService Catalog { get; }
    public OnlineSkinPreviewService Previews { get; }
    public SkinScreenshotCache Screenshots { get; }

    public bool CanDownload(OnlineSkinDownloadTarget? target) => target is not null &&
        (target.Kind is OnlineSkinDownloadKind.DirectHttps or OnlineSkinDownloadKind.GoogleDrive
         || MediaFireSkinDownloadResolver.IsPublicPage(target.Uri));

    public bool CanPrepare(OnlineSkinCatalogEntry? skin) => skin is not null &&
        (CanDownload(skin.Download) || browser.CanOpen(skin.Download?.BrowserHandoffUri ?? skin.Download?.Uri ?? skin.DetailsUri));

    public void Dispose() => ownedHttp?.Dispose();
}
