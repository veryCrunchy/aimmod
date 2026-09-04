using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AimMod.Desktop.Skins.Online;

public sealed record OnlineSkinImportResult(bool Success, string? Message = null);

public interface IOnlineSkinArchiveDestination
{
    Task<OnlineSkinImportResult> ImportAsync(string validatedOskPath, CancellationToken cancellationToken = default);
}

public sealed class OnlineSkinPreview : IAsyncDisposable
{
    private readonly Func<ValueTask> cleanup;
    private int disposed;

    internal OnlineSkinPreview(OnlineSkinCatalogEntry skin, string archivePath, string cacheKey, Func<ValueTask> cleanup)
    {
        Skin = skin;
        ArchivePath = archivePath;
        CacheKey = cacheKey;
        this.cleanup = cleanup;
    }

    public OnlineSkinCatalogEntry Skin { get; }
    public string ArchivePath { get; }
    public string CacheKey { get; }
    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && File.Exists(ArchivePath);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            await cleanup().ConfigureAwait(false);
    }
}

public sealed record OnlineSkinPreviewResult(
    OnlineSkinDownloadStatus Status,
    OnlineSkinPreview? Preview = null,
    Uri? ExternalUri = null,
    string? Message = null);

public sealed class OnlineSkinPreviewService
{
    private static readonly TimeSpan preview_maximum_age = TimeSpan.FromHours(8);

    private readonly string previewRoot;
    private readonly OnlineSkinCatalogCache cache;
    private readonly OnlineSkinDownloadResolverPipeline downloads;
    private readonly OnlineSkinArchiveValidator validator;
    private readonly SemaphoreSlim cleanupGate = new(1, 1);

    public OnlineSkinPreviewService(
        string previewRoot,
        OnlineSkinCatalogCache cache,
        OnlineSkinDownloadResolverPipeline downloads,
        OnlineSkinArchiveValidator validator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewRoot);
        if (!Path.IsPathFullyQualified(previewRoot))
            throw new ArgumentException("The preview path must be absolute.", nameof(previewRoot));
        this.previewRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(previewRoot));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<OnlineSkinPreviewResult> PrepareAsync(
        OnlineSkinCatalogEntry skin,
        bool allowSensitive = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skin);
        if (skin.Download is null)
            return new OnlineSkinPreviewResult(OnlineSkinDownloadStatus.ExternalBrowserRequired, ExternalUri: skin.DetailsUri, Message: "The provider did not expose a safe public download link.");
        if (skin.IsSensitive && !allowSensitive)
            return new OnlineSkinPreviewResult(OnlineSkinDownloadStatus.Rejected, ExternalUri: skin.DetailsUri, Message: "Sensitive skin previews require explicit UI confirmation.");

        await CleanupExpiredAsync(cancellationToken).ConfigureAwait(false);
        string cacheKey = downloadCacheKey(skin.Download.Uri);
        string directory = Path.Combine(previewRoot, $"preview-{Guid.NewGuid():N}");
        string previewPath = Path.Combine(directory, "skin.osk");
        Directory.CreateDirectory(directory);
        try
        {
            if (!await cache.TryCopyToAsync(cacheKey, previewPath, TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false))
            {
                string downloadPath = Path.Combine(directory, "skin.download");
                OnlineSkinResolvedDownload resolved = await downloads.ResolveAsync(skin.Download, downloadPath, cancellationToken).ConfigureAwait(false);
                if (resolved.Status != OnlineSkinDownloadStatus.Success || resolved.ArchivePath is null)
                {
                    deleteDirectory(directory);
                    return new OnlineSkinPreviewResult(resolved.Status, ExternalUri: resolved.ExternalUri ?? skin.DetailsUri, Message: resolved.Message);
                }
                File.Move(resolved.ArchivePath, previewPath, overwrite: false);
                await cache.PutFileAsync(cacheKey, previewPath, "osk", cancellationToken).ConfigureAwait(false);
            }

            OnlineSkinArchiveValidation validation = await validator.ValidateAsync(previewPath, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                deleteDirectory(directory);
                return new OnlineSkinPreviewResult(OnlineSkinDownloadStatus.InvalidArchive, ExternalUri: skin.DetailsUri, Message: validation.Message);
            }

            var preview = new OnlineSkinPreview(skin, previewPath, cacheKey, () =>
            {
                deleteDirectory(directory);
                return ValueTask.CompletedTask;
            });
            return new OnlineSkinPreviewResult(OnlineSkinDownloadStatus.Success, preview);
        }
        catch
        {
            deleteDirectory(directory);
            throw;
        }
    }

    public async Task<string> SaveAsync(
        OnlineSkinPreview preview,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ensureAvailable(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("The skin save directory must be absolute.", nameof(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        string fileName = sanitizeFileName(preview.Skin.Name) + ".osk";
        string destination = uniquePath(Path.Combine(Path.GetFullPath(destinationDirectory), fileName));
        await using FileStream source = File.OpenRead(preview.ArchivePath);
        await using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task<OnlineSkinImportResult> ImportAsync(
        OnlineSkinPreview preview,
        IOnlineSkinArchiveDestination destination,
        CancellationToken cancellationToken = default)
    {
        ensureAvailable(preview);
        ArgumentNullException.ThrowIfNull(destination);
        OnlineSkinArchiveValidation validation = await validator.ValidateAsync(preview.ArchivePath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
            return new OnlineSkinImportResult(false, validation.Message);
        return await destination.ImportAsync(preview.ArchivePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        await cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(previewRoot))
                return;
            DateTime cutoff = DateTime.UtcNow - preview_maximum_age;
            foreach (string directory in Directory.EnumerateDirectories(previewRoot, "preview-*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    deleteDirectory(directory);
            }
        }
        finally
        {
            cleanupGate.Release();
        }
    }

    private static string downloadCacheKey(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.Uri.AbsoluteUri));
        return "download:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ensureAvailable(OnlineSkinPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.IsAvailable)
            throw new InvalidOperationException("The temporary skin preview has expired.");
    }

    private static string sanitizeFileName(string value)
    {
        string cleaned = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().Trim('.');
        if (cleaned.Length == 0)
            cleaned = "osu-skin";
        return cleaned[..Math.Min(cleaned.Length, 100)];
    }

    private static string uniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = Path.Combine(directory, $"{name} ({suffix.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException("No available filename could be allocated for the skin.");
    }

    private static void deleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
