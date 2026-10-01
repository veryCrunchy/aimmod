using System.Text;
using AimMod.InGame;

namespace AimMod.Setup;

sealed class GameRunningException() : Exception(SetupDecision.GameRunningMessage);
sealed record SetupProgress(string Text, double? Fraction);
// Bytes: the size of the installed files, for the Apps & features entry.
sealed record SetupOutcome(string? Version, string Message, long Bytes = 0);

// Install, update, repair and uninstall for the installer. Everything that
// touches the game folder goes through the service's shared code
// (in-game/install-core): Updater.DownloadPackage verifies feed -> zip ->
// manifest -> every file, InstallOperations.Apply runs the PackageApplier
// transaction (backup, atomic replace, rollback on any failure).
sealed class SetupEngine
{
    readonly Updater updater;
    readonly Func<string, bool> gameRunning;
    public string Output { get; }
    public string UpdatesRoot => InstallState.UpdatesRoot(Output);

    public SetupEngine(string? output = null, HttpMessageHandler? handler = null, Func<string, bool>? gameRunning = null)
    {
        Output = output ?? InstallState.DefaultOutput;
        this.gameRunning = gameRunning ?? InstallLayout.GameRunning;
        updater = new Updater(UpdatesRoot, handler);
    }

    public bool GameRunning(string win64) => gameRunning(win64);
    void RequireClosed(string win64) { if (gameRunning(win64)) throw new GameRunningException(); }

    // ---- channel and feed ----
    UpdateSettings Settings() => new(Output, InstallState.CachedRelease(Output)?.Channel);
    // The channel AimMod uses now: saved in update-settings.json, else that of the installed package.
    public string SavedChannel() => Settings().Current.Channel;
    // Whether the channel was chosen (saved setting or an installed release) rather than the default.
    public bool HasChannelPreference => File.Exists(Path.Combine(Output, "update-settings.json")) || InstallState.CachedRelease(Output) is not null;
    public string FeedUrl(string channel) { var settings = Settings(); return settings.FeedUrl(settings.Current with { Channel = channel }); }
    public async Task<FeedStatus> CheckFeed(string channel, SemanticVersion installer, CancellationToken token)
    {
        try { return FeedCheck.Read(await updater.FetchFeed(FeedUrl(channel), token), installer, channel); }
        catch (ReleaseFormatException ex) { return new(FeedState.Unreachable, Message: ex.Message); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return new(FeedState.NotPublished, Message: $"No {SetupDecision.Name(channel)} release has been published yet."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !token.IsCancellationRequested)
        { return new(FeedState.Unreachable, Message: "Could not reach GitHub to get the latest AimMod. Check your connection and try again."); }
    }
    void SaveChannel(string channel)
    {
        Directory.CreateDirectory(Output);
        // Written even when it matches the installed package, so the service keeps it after a later switch.
        Settings().ApplyJson(Encoding.UTF8.GetBytes($"{{\"channel\":\"{channel}\"}}"));
    }

    // ---- what is installed ----
    public bool DataFolderExists => Directory.Exists(Output);
    public bool RepairCached => File.Exists(Path.Combine(InstallState.PackageCache(Output), InstallLayout.PackageManifest));
    public InstalledInfo? ReadInstalled(string win64)
    {
        InstallManifest? manifest;
        try { manifest = InstallManifest.Read(win64); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return new(null, null, false, true, ["The AimMod install record is damaged."]); }
        if (manifest is null) return null;
        var report = InstallHealth.Inspect(win64, manifest, InstallState.CachedRelease(Output));
        return new(manifest.Version, manifest.Channel, manifest.Managed, report.NeedsRepair,
            report.Problems.Where(p => p.Code != "not-installed").Select(p => p.Message).ToList());
    }

    // Writable without elevation? Only access denied counts; other errors surface during the install.
    public static bool CanWrite(string win64)
    {
        var folders = new List<string> { win64 };
        if (Directory.Exists(Path.Combine(win64, "ue4ss"))) folders.Add(Path.Combine(win64, "ue4ss"));
        if (Directory.Exists(InstallLayout.ContentPaks(win64))) folders.Add(InstallLayout.ContentPaks(win64));
        foreach (var folder in folders)
        {
            try { using (File.Create(Path.Combine(folder, ".aimmod-setup-" + Guid.NewGuid().ToString("N")), 1, FileOptions.DeleteOnClose)) { } }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { }
        }
        return true;
    }

    // ---- actions ----
    // install: first install or a channel switch; update: newer version; repair: same version again.
    public async Task<SetupOutcome> InstallFromFeed(string win64, UpdateFeed feed, string kind, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        RequireClosed(win64);
        var work = Path.Combine(UpdatesRoot, "setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var size = (double)feed.Package.Size;
            progress?.Report(new($"Downloading AimMod {feed.Version}…", 0));
            var bytes = new Forward<long>(done => progress?.Report(new($"Downloading AimMod {feed.Version}… {done / 1048576.0:0.0} of {size / 1048576.0:0.0} MB", 0.85 * done / size)));
            var package = await updater.DownloadPackage(feed, Path.Combine(work, "package"), Path.Combine(work, "package.zip.part"), bytes, token);
            progress?.Report(new($"Verified all {package.Manifest.Files.Length} files. Installing…", 0.9));
            var result = await Task.Run(() => Locked(() => { RequireClosed(win64); return InstallOperations.Apply(win64, Output, package, kind, gameRunning); }), token);
            SaveChannel(feed.Channel);
            var message = kind switch
            {
                "update" => $"AimMod was updated to {feed.Version}" + (result.PreviousVersion is { } p ? $" (from {p})." : "."),
                "repair" => "AimMod was repaired.",
                _ => $"AimMod {feed.Version} was installed.",
            };
            Finish(kind, feed.Version, message);
            progress?.Report(new(message, 1));
            return new(feed.Version, message, package.Manifest.Files.Sum(f => f.Size));
        }
        finally { try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // From the copy saved at install time (offline); from the feed only when
    // it offers the installed version.
    public async Task<SetupOutcome> Repair(string win64, UpdateFeed? feed, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        RequireClosed(win64);
        var installed = InstallManifest.Read(win64)?.Version;
        VerifiedPackage? cached = null;
        if (RepairCached)
            try { cached = VerifiedPackage.Open(InstallState.PackageCache(Output)); }
            catch (ReleaseFormatException) { }
        if (cached is not null && (installed is null || cached.Manifest.Version == installed))
        {
            progress?.Report(new($"Verified the saved copy of AimMod {cached.Manifest.Version}. Repairing…", 0.5));
            await Task.Run(() => Locked(() => { RequireClosed(win64); return InstallOperations.Apply(win64, Output, cached, "repair", gameRunning); }), token);
            const string message = "AimMod was repaired.";
            Finish("repair", cached.Manifest.Version, message);
            progress?.Report(new(message, 1));
            return new(cached.Manifest.Version, message, cached.Manifest.Files.Sum(f => f.Size));
        }
        if (feed is not null && feed.Version == installed) return await InstallFromFeed(win64, feed, "repair", progress, token);
        throw new InstallException("There is no saved copy of the installed AimMod to repair from. Update or install AimMod instead.");
    }

    // Removes what AimMod placed in the game. With removeData the data folder
    // (history, replays, settings) goes too.
    public SetupOutcome Uninstall(string win64, bool removeData)
    {
        RequireClosed(win64);
        var kept = 0;
        var installed = false;
        try { installed = InstallManifest.Read(win64) is not null; } catch (InvalidDataException) { installed = true; }
        if (installed) kept = Locked(() => { RequireClosed(win64); return InstallOperations.Uninstall(win64, Output, false, gameRunning); });
        if (removeData)
        {
            if (Directory.Exists(Output)) Directory.Delete(Output, true);
            var parent = Path.GetDirectoryName(Output.TrimEnd('\\'));
            try { if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); }
            catch (IOException) { }
        }
        else
        {
            Log($"uninstall: removed from {win64}" + (kept > 0 ? $", kept {kept} changed file(s)" : ""));
            InstallState.Record(UpdatesRoot, new("uninstall", true, null, "AimMod was removed.", InstallState.Now()));
        }
        var message = "AimMod was removed from KovaaK's." + (kept > 0 ? $" {kept} file(s) you changed were kept." : "")
            + (removeData ? " Your AimMod data was deleted." : " Your history, replays and settings were kept.");
        return new(null, message);
    }

    void Finish(string kind, string version, string message)
    {
        InstallState.UpdateRequests(UpdatesRoot, _ => new());
        InstallState.Record(UpdatesRoot, new(kind, true, version, message, InstallState.Now()));
        Log($"{kind}: {message}");
    }

    // Same lock as the service's command line and its post-exit applier.
    static T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, InstallState.MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new InstallException("Another AimMod install is running. Try again in a moment.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    // updates\install.log, shared with the service's install commands.
    public void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(UpdatesRoot);
            File.AppendAllText(Path.Combine(UpdatesRoot, "install.log"), $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} setup {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    sealed class Forward<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
