namespace AimMod.Desktop.Skins.Online;

public interface ISkinDownloadBrowser
{
    bool CanOpen(Uri page);
    Task<OnlineSkinResolvedDownload> DownloadAsync(Uri page, string destinationPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

internal static class SkinDownloadBrowserPolicy
{
    private static readonly string[] hosts =
    [
        "osuskins.net", "www.osuskins.net", "cdn.osuskins.net", "skins.osuck.net",
        "drive.google.com", "drive.usercontent.google.com", "docs.google.com",
        "mega.nz", "www.mega.nz", "mega.co.nz", "www.mediafire.com", "mediafire.com",
        "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
    ];

    public static bool CanNavigate(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && uri.UserInfo.Length == 0
        && (hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".osuck.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".osuskins.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(uri.Host, @"^download[0-9]+\.mediafire\.com$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));

    public static bool IsCandidate(string fileName) => !string.IsNullOrWhiteSpace(fileName)
        && (fileName.EndsWith(".osk", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static bool ShouldTryBrowser(OnlineSkinDownloadStatus status) => status is
        OnlineSkinDownloadStatus.ExternalBrowserRequired or OnlineSkinDownloadStatus.Unsupported
        or OnlineSkinDownloadStatus.NetworkError;
}
