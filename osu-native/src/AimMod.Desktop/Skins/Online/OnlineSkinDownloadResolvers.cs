using System.Globalization;

namespace AimMod.Desktop.Skins.Online;

public enum OnlineSkinDownloadStatus
{
    Success,
    ExternalBrowserRequired,
    Unsupported,
    Rejected,
    TooLarge,
    InvalidArchive,
    NetworkError,
}

public sealed record OnlineSkinResolvedDownload(
    OnlineSkinDownloadStatus Status,
    string? ArchivePath = null,
    Uri? ExternalUri = null,
    string? Message = null,
    string? CacheKey = null,
    OnlineSkinArchiveValidation? Validation = null,
    Uri? RedirectUri = null);

public interface IOnlineSkinDownloadResolver
{
    bool CanResolve(OnlineSkinDownloadTarget target);
    Task<OnlineSkinResolvedDownload> ResolveAsync(OnlineSkinDownloadTarget target, string destinationPath, CancellationToken cancellationToken = default);
}

public sealed class DirectHttpsSkinDownloadResolver : IOnlineSkinDownloadResolver
{
    private static readonly string[] default_approved_hosts = ["osuskins.net", "www.osuskins.net", "cdn.osuskins.net", "skins.osuck.net"];
    private static readonly string[] archive_types =
    [
        "application/octet-stream",
        "application/zip",
        "application/x-zip-compressed",
        "application/x-osu-skin",
    ];

    private readonly ISecureSkinHttpClient http;
    private readonly OnlineSkinArchiveValidator validator;
    private readonly long maximumBytes;
    private readonly IReadOnlySet<string> approvedHosts;

    public DirectHttpsSkinDownloadResolver(
        ISecureSkinHttpClient http,
        OnlineSkinArchiveValidator validator,
        long maximumBytes = 256L * 1024 * 1024,
        IReadOnlyCollection<string>? approvedHosts = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.maximumBytes = maximumBytes;
        this.approvedHosts = new HashSet<string>(
            approvedHosts ?? default_approved_hosts,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool CanResolve(OnlineSkinDownloadTarget target) => target.Kind == OnlineSkinDownloadKind.DirectHttps;

    public async Task<OnlineSkinResolvedDownload> ResolveAsync(
        OnlineSkinDownloadTarget target,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanResolve(target))
            return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Unsupported, ExternalUri: target.Uri);
        string[] allowedHosts = target.AllowedHosts.Where(approvedHosts.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!allowedHosts.Contains(target.Uri.Host, StringComparer.OrdinalIgnoreCase))
            return new OnlineSkinResolvedDownload(
                OnlineSkinDownloadStatus.Rejected,
                ExternalUri: target.Uri,
                Message: $"The host '{target.Uri.Host}' is not approved for direct skin downloads.");
        try
        {
            await http.DownloadAsync(
                target.Uri,
                destinationPath,
                new SkinHttpFetchOptions(allowedHosts, archive_types, maximumBytes, TimeSpan.FromSeconds(45)),
                cancellationToken).ConfigureAwait(false);
            return await validate(destinationPath, target.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (SkinHttpException error)
        {
            deleteQuietly(destinationPath);
            if (error.RedirectUri is not null)
                return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Unsupported, RedirectUri: error.RedirectUri, Message: error.Message);
            return error.Code switch
            {
                "payload_too_large" => new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.TooLarge, ExternalUri: target.Uri, Message: error.Message),
                "unexpected_content_type" => new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.ExternalBrowserRequired, ExternalUri: target.Uri, Message: "The link opened a web page instead of a skin archive."),
                "url_rejected" or "response_host_rejected" => new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Rejected, ExternalUri: target.Uri, Message: error.Message),
                _ => new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.NetworkError, ExternalUri: target.Uri, Message: error.Message),
            };
        }
    }

    private async Task<OnlineSkinResolvedDownload> validate(string path, Uri source, CancellationToken cancellationToken)
    {
        OnlineSkinArchiveValidation validation = await validator.ValidateAsync(path, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            deleteQuietly(path);
            return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.InvalidArchive, ExternalUri: source, Message: validation.Message, Validation: validation);
        }
        return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Success, path, CacheKey: "osk:" + validation.Sha256, Validation: validation);
    }

    private static void deleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class GoogleDriveSkinDownloadResolver : IOnlineSkinDownloadResolver
{
    public static readonly string[] AllowedHosts = ["drive.google.com", "drive.usercontent.google.com", "docs.google.com"];

    private readonly DirectHttpsSkinDownloadResolver direct;

    public GoogleDriveSkinDownloadResolver(ISecureSkinHttpClient http, OnlineSkinArchiveValidator validator, long maximumBytes = 256L * 1024 * 1024)
    {
        direct = new DirectHttpsSkinDownloadResolver(http, validator, maximumBytes, AllowedHosts);
    }

    public bool CanResolve(OnlineSkinDownloadTarget target) => target.Kind == OnlineSkinDownloadKind.GoogleDrive;

    public Task<OnlineSkinResolvedDownload> ResolveAsync(
        OnlineSkinDownloadTarget target,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanResolve(target))
            return Task.FromResult(new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Unsupported, ExternalUri: target.Uri));
        string? fileId = extractFileId(target.Uri);
        if (fileId is null)
            return Task.FromResult(new OnlineSkinResolvedDownload(
                OnlineSkinDownloadStatus.ExternalBrowserRequired,
                ExternalUri: target.Uri,
                Message: "This Google Drive link does not expose a safe public file id."));

        var download = new Uri($"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t");
        var directTarget = new OnlineSkinDownloadTarget(download, OnlineSkinDownloadKind.DirectHttps, AllowedHosts, target.FileName);
        return direct.ResolveAsync(directTarget, destinationPath, cancellationToken);
    }

    private static string? extractFileId(Uri uri)
    {
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int file = Array.FindIndex(parts, part => string.Equals(part, "d", StringComparison.OrdinalIgnoreCase));
        string? candidate = file >= 0 && file + 1 < parts.Length ? parts[file + 1] : readQuery(uri.Query, "id");
        return candidate is { Length: >= 10 and <= 128 }
               && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? candidate
            : null;
    }

    private static string? readQuery(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }
}

public sealed class ExternalSkinDownloadResolver : IOnlineSkinDownloadResolver
{
    public bool CanResolve(OnlineSkinDownloadTarget target) => target.Kind is
        OnlineSkinDownloadKind.Mega or OnlineSkinDownloadKind.FormPost or OnlineSkinDownloadKind.External;

    public Task<OnlineSkinResolvedDownload> ResolveAsync(
        OnlineSkinDownloadTarget target,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string message = target.Kind switch
        {
            OnlineSkinDownloadKind.Mega => "MEGA downloads require the official browser or app and cannot be previewed safely by AimMod.",
            OnlineSkinDownloadKind.FormPost => "This provider requires its public download page. Open it in your browser to continue.",
            _ => "This host is not supported for safe in-app downloads.",
        };
        return Task.FromResult(new OnlineSkinResolvedDownload(
            OnlineSkinDownloadStatus.ExternalBrowserRequired,
            ExternalUri: target.BrowserHandoffUri ?? target.Uri,
            Message: message));
    }
}

public sealed class OnlineSkinDownloadResolverPipeline
{
    private readonly IReadOnlyList<IOnlineSkinDownloadResolver> resolvers;

    public OnlineSkinDownloadResolverPipeline(params IOnlineSkinDownloadResolver[] resolvers)
    {
        this.resolvers = resolvers.Where(resolver => resolver is not null).ToArray();
    }

    public async Task<OnlineSkinResolvedDownload> ResolveAsync(
        OnlineSkinDownloadTarget target,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (int redirect = 0; redirect < 6; redirect++)
        {
            IOnlineSkinDownloadResolver? resolver = resolvers.FirstOrDefault(candidate => candidate.CanResolve(target));
            if (resolver is null)
                return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Unsupported, ExternalUri: target.Uri, Message: "No resolver supports this skin link.");
            OnlineSkinResolvedDownload result = await resolver.ResolveAsync(target, destinationPath, cancellationToken).ConfigureAwait(false);
            if (result.RedirectUri is null)
                return result;
            OnlineSkinDownloadTarget redirected = SkinDownloadTargetClassifier.Classify(result.RedirectUri);
            if (redirected.Kind == OnlineSkinDownloadKind.External)
                return new OnlineSkinResolvedDownload(
                    OnlineSkinDownloadStatus.Rejected,
                    ExternalUri: target.Uri,
                    Message: $"The skin download redirected to an unapproved host: {redirected.Uri.Host}.");
            target = redirected;
        }
        return new OnlineSkinResolvedDownload(OnlineSkinDownloadStatus.Rejected, ExternalUri: target.Uri, Message: "The skin download redirected too many times.");
    }
}
