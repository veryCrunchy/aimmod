using System.Text;
using System.Text.RegularExpressions;

namespace AimMod.Desktop.Skins.Online;

public sealed class MediaFireSkinDownloadResolver(ISecureSkinHttpClient http, OnlineSkinArchiveValidator validator) : IOnlineSkinDownloadResolver
{
    public bool CanResolve(OnlineSkinDownloadTarget target) => IsPublicPage(target.Uri);
    internal static bool IsPublicPage(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Host is "mediafire.com" or "www.mediafire.com" && uri.AbsolutePath.StartsWith("/file/", StringComparison.Ordinal);

    internal static Uri? DownloadLink(string html)
    {
        foreach (Match match in Regex.Matches(html, "<a\\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150)))
        {
            string attributes = match.Groups["attributes"].Value;
            if (SkinHtml.ReadAttribute(attributes, "id") != "downloadButton") continue;
            if (Uri.TryCreate(System.Net.WebUtility.HtmlDecode(SkinHtml.ReadAttribute(attributes, "href")), UriKind.Absolute, out var uri)
                && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0
                && Regex.IsMatch(uri.Host, @"^download[0-9]+\.mediafire\.com$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) return uri;
        }
        return null;
    }

    public async Task<OnlineSkinResolvedDownload> ResolveAsync(OnlineSkinDownloadTarget target, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!CanResolve(target)) return new(OnlineSkinDownloadStatus.Unsupported);
        try
        {
            var page = await http.GetBytesAsync(target.Uri, new(["mediafire.com", "www.mediafire.com"], ["text/html"], 2 * 1024 * 1024, TimeSpan.FromSeconds(20)), cancellationToken).ConfigureAwait(false);
            var link = DownloadLink(Encoding.UTF8.GetString(page.Bytes));
            if (link is null) return new(OnlineSkinDownloadStatus.Unsupported, Message: "This MediaFire file has no unattended download available.");
            var resolver = new DirectHttpsSkinDownloadResolver(http, validator, approvedHosts: [link.Host]);
            return await resolver.ResolveAsync(new(link, OnlineSkinDownloadKind.DirectHttps, [link.Host]), destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (SkinHttpException error) { return new(OnlineSkinDownloadStatus.NetworkError, Message: error.Message); }
    }
}
