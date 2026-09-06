using Microsoft.Playwright;

namespace AimMod.Desktop.Skins.Online;

public sealed class SkinDownloadBrowser : ISkinDownloadBrowser
{
    private const long maximumBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly OnlineSkinArchiveValidator validator;
    private readonly Func<IPlaywright, string, Task<IBrowserContext?>> launcher;

    public SkinDownloadBrowser(OnlineSkinArchiveValidator validator) : this(validator, launch) { }

    internal SkinDownloadBrowser(OnlineSkinArchiveValidator validator, Func<IPlaywright, string, Task<IBrowserContext?>> launcher)
    {
        this.validator = validator;
        this.launcher = launcher;
    }

    public bool CanOpen(Uri page) => SkinDownloadBrowserPolicy.CanNavigate(page);

    public async Task<OnlineSkinResolvedDownload> DownloadAsync(Uri page, string destinationPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!CanOpen(page))
            return new(OnlineSkinDownloadStatus.Rejected, Message: "This download page is not supported.");
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Download destination must be absolute.", nameof(destinationPath));
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!, "browser-" + Guid.NewGuid().ToString("N"));
        IBrowserContext? context = null;
        IPlaywright? playwright = null;
        Task? captureTask = null;
        bool success = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var completion = new TaskCompletionSource<OnlineSkinResolvedDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Directory.CreateDirectory(root);
            progress?.Report("Opening the skin download window");
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            context = await launcher(playwright, root).ConfigureAwait(false);
            if (context is null)
                return new(OnlineSkinDownloadStatus.Unsupported, Message: "The download window requires Microsoft Edge, Google Chrome, or Chromium to be installed.");
            using var cancelled = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            context.Close += (_, _) => completion.TrySetResult(new(OnlineSkinDownloadStatus.Cancelled, Message: "Skin download cancelled."));
            int captured = 0;
            int phase = 0;
            context.Download += (_, download) =>
            {
                if (Interlocked.CompareExchange(ref captured, 1, 0) != 0)
                {
                    _ = cancelDownload(download);
                    return;
                }
                captureTask = capture(download);
            };
            await context.RouteAsync("**/*", async route =>
            {
                try
                {
                    var request = route.Request;
                    if (request.IsNavigationRequest && request.Frame == request.Frame.Page.MainFrame
                        && (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !CanOpen(uri)))
                    {
                        await route.AbortAsync().ConfigureAwait(false);
                        return;
                    }
                    await route.ContinueAsync().ConfigureAwait(false);
                }
                catch (PlaywrightException) { }
            }).ConfigureAwait(false);
            IPage browserPage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            progress?.Report("Complete the provider's verification and download in the opened window");
            try
            {
                Task navigation = browserPage.GotoAsync(page.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });
                _ = observeNavigation(navigation);
                await navigation.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (PlaywrightException) when (Volatile.Read(ref captured) != 0 || completion.Task.IsCompleted) { }

            // A browser download is quarantined until completion; bound partial files too.
            long lastReportedMegabytes = -1;
            while (!completion.Task.IsCompleted)
            {
                await Task.WhenAny(completion.Task, Task.Delay(200, timeout.Token)).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (completion.Task.IsCompleted) break;
                long downloaded = 0;
                foreach (var file in new DirectoryInfo(Path.Combine(root, "downloads")).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { downloaded = checked(downloaded + file.Length); }
                    catch (FileNotFoundException) { }
                }
                if (downloaded > maximumBytes)
                    completion.TrySetResult(new(OnlineSkinDownloadStatus.TooLarge, Message: "The skin download exceeds the 256 MB limit."));
                if (Volatile.Read(ref phase) == 1 && downloaded / (1024 * 1024) != lastReportedMegabytes)
                {
                    lastReportedMegabytes = downloaded / (1024 * 1024);
                    progress?.Report($"Downloading skin ({downloaded / (1024d * 1024):0.0} MB)");
                }
            }
            var result = await completion.Task.ConfigureAwait(false);
            success = result.Status == OnlineSkinDownloadStatus.Success;
            return result;

            async Task capture(IDownload download)
            {
                try
                {
                    if (!SkinDownloadBrowserPolicy.IsCandidate(download.SuggestedFilename))
                    {
                        await download.CancelAsync().ConfigureAwait(false);
                        completion.TrySetResult(new(OnlineSkinDownloadStatus.InvalidArchive, Message: "The download was not a skin archive and was discarded."));
                        return;
                    }
                    progress?.Report("Downloading the skin archive");
                    Volatile.Write(ref phase, 1);
                    await download.SaveAsAsync(destinationPath).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    Volatile.Write(ref phase, 2);
                    progress?.Report("Checking the downloaded skin");
                    var validation = await validator.ValidateAsync(destinationPath, timeout.Token).ConfigureAwait(false);
                    completion.TrySetResult(validation.IsValid
                        ? new(OnlineSkinDownloadStatus.Success, destinationPath, Validation: validation, FileName: download.SuggestedFilename)
                        : new(OnlineSkinDownloadStatus.InvalidArchive, Message: validation.Message, Validation: validation));
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(timeout.Token); }
                catch (Exception error) when (error is PlaywrightException or IOException or UnauthorizedAccessException)
                {
                    completion.TrySetResult(new(OnlineSkinDownloadStatus.NetworkError, Message: "The skin download did not finish. Please try again."));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(OnlineSkinDownloadStatus.Cancelled, Message: "The skin download window timed out. Please try again.");
        }
        catch (Exception error) when (error is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            return new(OnlineSkinDownloadStatus.NetworkError, Message: "The skin download window could not complete the download. Please try again.");
        }
        finally
        {
            // Closing the owned context closes every popup and removes browser downloads.
            if (context is not null)
            {
                try { await context.CloseAsync().ConfigureAwait(false); }
                catch (PlaywrightException) { }
            }
            if (captureTask is not null) await captureTask.ConfigureAwait(false);
            playwright?.Dispose();
            if (!success) deleteFile(destinationPath);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            sessionGate.Release();
        }
    }

    private static async Task<IBrowserContext?> launch(IPlaywright playwright, string root)
    {
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false, ChromiumSandbox = true, AcceptDownloads = true, DownloadsPath = downloads,
            ViewportSize = ViewportSize.NoViewport, Timeout = 20_000,
            Args = ["--app=about:blank", "--window-size=1050,800"],
        };
        foreach (string channel in OperatingSystem.IsWindows() ? new[] { "msedge", "chrome" } : new[] { "chrome", "msedge" })
        {
            try
            {
                options.Channel = channel;
                return await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(root, "profile"), options).ConfigureAwait(false);
            }
            catch (PlaywrightException) { }
        }
        options.Channel = null;
        foreach (string executable in new[] { playwright.Chromium.ExecutablePath, "/usr/bin/chromium", "/usr/bin/chromium-browser" }.Where(File.Exists))
        {
            try
            {
                options.ExecutablePath = executable;
                return await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(root, "profile"), options).ConfigureAwait(false);
            }
            catch (PlaywrightException) { }
        }
        return null;
    }

    private static async Task cancelDownload(IDownload download)
    {
        try { await download.CancelAsync().ConfigureAwait(false); }
        catch (PlaywrightException) { }
    }

    private static async Task observeNavigation(Task navigation)
    {
        try { await navigation.ConfigureAwait(false); }
        catch (PlaywrightException) { }
    }

    private static void deleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
