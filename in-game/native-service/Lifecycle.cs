using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;

sealed record LifecycleRequests(
    [property: JsonPropertyName("repair")] bool Repair = false,
    [property: JsonPropertyName("install")] bool Install = false,
    [property: JsonPropertyName("skipVersion")] string? SkipVersion = null);
sealed record LifecycleResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("seen")] bool Seen = false);

// Install lifecycle inside the service: periodic update checks,
// staging, install-health checks, and the hand-off that applies a staged
// update or a repair once KovaaK's has closed. See install-lifecycle.md.
sealed class Lifecycle : IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    readonly string output, updatesRoot;
    readonly string? win64;
    readonly Func<string, bool> gameRunning;
    readonly UpdateSettings settings;
    readonly Updater updater;
    readonly PackageApplier applier;
    readonly object gate = new();
    readonly SemaphoreSlim wake = new(0, 1);
    readonly CancellationTokenSource stop = new();
    Task? loop;
    UpdateCheck check = new(UpdateState.Idle);
    DateTime? checkedAt;
    HealthReport? health;

    public Lifecycle(string output, string? win64, HttpMessageHandler? handler = null, Func<string, bool>? gameRunning = null)
    {
        this.output = output; this.win64 = win64;
        this.gameRunning = gameRunning ?? InstallLayout.GameRunning;
        updatesRoot = Path.Combine(output, "updates");
        Directory.CreateDirectory(updatesRoot);
        settings = new UpdateSettings(output);
        updater = new Updater(updatesRoot, handler);
        applier = new PackageApplier(updatesRoot, this.gameRunning);
    }
    public static string PackageCache(string output) => Path.Combine(output, "package", "current");
    public static string PreviousPackageCache(string output) => Path.Combine(output, "package", "previous");
    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ---- persisted request/result files ----
    static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    static void WriteJson<T>(string path, T value) => AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value), durable: true);
    static string RequestsPath(string root) => Path.Combine(root, "requests.json");
    static string ResultPath(string root) => Path.Combine(root, "last-result.json");
    static string ConfirmPath(string root) => Path.Combine(root, "pending-confirmation.json");
    LifecycleRequests Requests => ReadJson<LifecycleRequests>(RequestsPath(updatesRoot)) ?? new();
    void UpdateRequests(Func<LifecycleRequests, LifecycleRequests> change) { lock (gate) WriteJson(RequestsPath(updatesRoot), change(Requests)); }

    InstallManifest? Installed()
    {
        if (win64 is null) return null;
        try { return InstallManifest.Read(win64); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
    ReleaseManifest? CachedRelease()
    {
        try
        {
            var path = Path.Combine(PackageCache(output), InstallLayout.PackageManifest);
            return File.Exists(path) ? ReleaseManifest.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ReleaseFormatException) { return null; }
    }

    // ---- service side ----
    public void Start()
    {
        // The new version runs: the update that installed it is confirmed.
        if (ReadJson<LifecycleResult>(ConfirmPath(updatesRoot)) is { } pending && Installed()?.Version == pending.Version)
            try { File.Delete(ConfirmPath(updatesRoot)); } catch (IOException) { }
        CleanApplierCopies();
        loop = Task.Run(() => Run(stop.Token));
    }
    void CleanApplierCopies()
    {
        var root = Path.Combine(updatesRoot, "applier");
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    async Task Run(CancellationToken token)
    {
        try
        {
            Inspect();
            // Let the game finish loading before using the network.
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            var failures = 0;
            var manual = false;
            while (!token.IsCancellationRequested)
            {
                if (manual || settings.Current.AutoUpdate)
                    failures = await CheckOnce(token) ? 0 : Math.Min(failures + 1, 4);
                else lock (gate) check = new(UpdateState.Disabled);
                Inspect();
                // Retry failures sooner (15, 30, 60, 120 minutes), otherwise every 4 hours.
                var delay = failures > 0 ? TimeSpan.FromMinutes(15 << (failures - 1)) : CheckInterval + TimeSpan.FromMinutes(Random.Shared.Next(0, 20));
                manual = await wake.WaitAsync(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Inspect()
    {
        if (win64 is null) return;
        try
        {
            var report = InstallHealth.Inspect(win64, Installed(), CachedRelease(), Environment.ProcessPath);
            lock (gate) health = report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
    async Task<bool> CheckOnce(CancellationToken token)
    {
        lock (gate) check = check with { State = check.State == UpdateState.Ready ? UpdateState.Ready : UpdateState.Checking };
        try
        {
            var preferences = settings.Current;
            var installed = Installed();
            var result = await updater.Check(preferences, settings.FeedUrl(preferences), installed, win64 is null ? null : InstallLayout.SteamBuildId(win64), token);
            if (result.State == UpdateState.Ready && Requests.SkipVersion == result.Version && !Requests.Install)
                result = result with { Message = "The last attempt to install this update failed. Check again to retry." };
            lock (gate) { check = result; checkedAt = DateTime.UtcNow; }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReleaseFormatException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TaskCanceledException && !token.IsCancellationRequested)
        {
            var message = ex is ReleaseFormatException ? ex.Message : "Could not reach the update server. AimMod will try again later.";
            lock (gate) { check = new(updater.Staged() is { } s ? UpdateState.Ready : UpdateState.Failed, updater.Staged()?.Version, updater.Staged()?.Notes, message); checkedAt = DateTime.UtcNow; }
            return false;
        }
    }
    public void CheckNow()
    {
        UpdateRequests(r => r with { SkipVersion = null });
        try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { }
    }

    public object Snapshot()
    {
        var installed = Installed();
        var staged = updater.Staged();
        var requests = Requests;
        var prefs = settings.Current;
        UpdateCheck current; HealthReport? report; DateTime? at;
        lock (gate) { current = check; report = health; at = checkedAt; }
        if (staged is not null && current.State is not UpdateState.Ready) current = new(UpdateState.Ready, staged.Version, staged.Notes, current.Message);
        if (installed is not null && !installed.Managed) current = new(UpdateState.Unmanaged, Message: "This install was made with the developer installer; updates are off.");
        var applyOnClose = staged is not null && (prefs.AutoUpdate || requests.Install) && requests.SkipVersion != staged.Version;
        var result = ReadJson<LifecycleResult>(ResultPath(updatesRoot));
        var rollback = applier.RollbackTarget;
        return new
        {
            installed = new { found = installed is not null, managed = installed?.Managed ?? false, version = installed?.Version, channel = installed?.Channel },
            settings = new { autoUpdate = prefs.AutoUpdate, channel = prefs.Channel },
            update = new { state = State(current.State), version = current.Version, notes = current.Notes, message = current.Message, checkedAt = at?.ToString("yyyy-MM-ddTHH:mm:ssZ"), applyOnClose },
            repair = new
            {
                needed = report?.NeedsRepair == true && report.Installed,
                problems = (report?.Problems ?? []).Where(p => p.Code != "not-installed").Take(8).Select(p => p.Message).ToArray(),
                requested = requests.Repair,
                available = Directory.Exists(PackageCache(output)),
                interrupted = applier.HasInterrupted,
            },
            game = new { steamBuildId = report?.Game.SteamBuildId, tested = report?.Game.Tested ?? false, testedVersion = report?.Game.TestedVersion, warning = report?.Game.Warning },
            rollback = new { available = rollback is not null, version = rollback },
            last = result is { Seen: false } ? result : null,
        };
    }
    static string State(UpdateState state) => state switch
    {
        UpdateState.Disabled => "disabled", UpdateState.Unmanaged => "unmanaged",
        UpdateState.Checking => "checking", UpdateState.UpToDate => "up-to-date", UpdateState.Downloading => "downloading",
        UpdateState.Ready => "ready", UpdateState.NeedsNewerGame => "needs-newer-game", UpdateState.Failed => "failed", _ => "idle",
    };

    public void Act(string action)
    {
        switch (action)
        {
            case "check": CheckNow(); break;
            case "repair": UpdateRequests(r => r with { Repair = true }); break;
            case "cancel-repair": UpdateRequests(r => r with { Repair = false }); break;
            case "install": UpdateRequests(r => r with { Install = true, SkipVersion = null }); break;
            case "dismiss":
                if (ReadJson<LifecycleResult>(ResultPath(updatesRoot)) is { } result) WriteJson(ResultPath(updatesRoot), result with { Seen = true });
                break;
            default: throw new JsonException("Unknown action.");
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/lifecycle", () => Results.Json(Snapshot()));
        routes.MapGet(prefix + "/lifecycle.js", () => Results.Stream(typeof(Lifecycle).Assembly.GetManifestResourceStream("AimMod.LifecycleScript")!, "application/javascript"));
        async Task<IResult> Post(HttpRequest request, Func<byte[], object> apply, CancellationToken token)
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > 1024) return Results.StatusCode(413);
            try { var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token); return Results.Json(apply(bytes)); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete request." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Json(new { error = "Could not save." }, statusCode: 500); }
        }
        routes.MapPost(prefix + "/lifecycle/settings", (HttpRequest request, CancellationToken token) => Post(request, bytes =>
        {
            var before = settings.Current;
            var next = settings.ApplyJson(bytes);
            if (next.Channel != before.Channel || (next.AutoUpdate && !before.AutoUpdate)) CheckNow();
            return Snapshot();
        }, token));
        routes.MapPost(prefix + "/lifecycle/action", (HttpRequest request, CancellationToken token) => Post(request, bytes =>
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 1
                || !document.RootElement.TryGetProperty("action", out var value) || value.ValueKind != JsonValueKind.String) throw new JsonException("Invalid action.");
            Act(value.GetString()!);
            return Snapshot();
        }, token));
    }

    // Called once KovaaK's has closed (the service runs with --exit-with-game).
    // The service cannot replace its own running files, so a copy of this
    // installed, known-good exe applies the change after this process exits.
    public bool HandOffAfterGameExit()
    {
        if (win64 is null) return false;
        var staged = updater.Staged();
        var requests = Requests;
        var update = staged is not null && (settings.Current.AutoUpdate || requests.Install) && requests.SkipVersion != staged.Version;
        if (!update && !requests.Repair && !applier.HasInterrupted) return false;
        var source = AppContext.BaseDirectory;
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return false;
        var copy = Path.Combine(updatesRoot, "applier", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            var info = new ProcessStartInfo(Path.Combine(copy, Path.GetFileName(exe))) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = copy };
            foreach (var arg in new[] { "--apply-pending", "--wait-pid", Environment.ProcessId.ToString(), "--game-dir", win64, "--output", output }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            Console.WriteLine(update ? $"Installing AimMod {staged!.Version} now that KovaaK's has closed." : "Repairing the AimMod install now that KovaaK's has closed.");
            return process is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Could not start the AimMod updater ({ex.GetType().Name}).");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) try { await loop; } catch (OperationCanceledException) { }
        stop.Dispose();
    }

    // ---- command line (applier, Install/Repair/Uninstall-AimMod.cmd) ----
    sealed class Log(string path) : IDisposable
    {
        readonly StreamWriter? file = Open(path);
        static StreamWriter? Open(string path) { try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return new StreamWriter(path, true) { AutoFlush = true }; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
        public void Line(string text) { Console.WriteLine(text); try { file?.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {text}"); } catch (IOException) { } }
        public void Dispose() => file?.Dispose();
    }
    static string? Arg(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    public static readonly string[] Commands = ["--install", "--repair", "--apply-pending", "--rollback", "--uninstall", "--install-status"];

    public static int RunCommand(string[] args, Func<string, bool>? gameRunningOverride = null)
    {
        var output = Arg(args, "--output") is { } o ? Path.GetFullPath(o) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "KovaaksNative");
        var updatesRoot = Path.Combine(output, "updates");
        Directory.CreateDirectory(updatesRoot);
        var running = gameRunningOverride ?? InstallLayout.GameRunning;
        using var log = new Log(Path.Combine(updatesRoot, "install.log"));
        using var mutex = new Mutex(false, "Local\\AimMod.KovaaksNative.Installer");
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromMinutes(2)); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { log.Line("Another AimMod install is running."); return 1; }
        try
        {
            if (args.Contains("--apply-pending")) return ApplyPending(args, output, running, log);
            var win64 = Arg(args, "--game-dir") is { } g ? ResolveGameDir(g) : InstallLayout.FindWin64FromService(AppContext.BaseDirectory) ?? InstallLayout.FindWin64FromSteam();
            if (win64 is null) { log.Line("KovaaK's was not found. Pass --game-dir <FPSAimTrainer folder>."); return 1; }
            var applier = new PackageApplier(updatesRoot, running);
            if (args.Contains("--install-status"))
            {
                var report = InstallHealth.Inspect(win64, InstallManifest.Read(win64), CachedRelease(output));
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return report.NeedsRepair ? 3 : 0;
            }
            if (running(win64)) { log.Line("KovaaK's is running. Close the game, then try again."); return 2; }
            if (args.Contains("--uninstall"))
            {
                var kept = applier.Uninstall(win64, args.Contains("--force"));
                foreach (var dir in new[] { Path.Combine(output, "package"), Path.Combine(output, "updates", "staged") })
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
                try { File.Delete(Path.Combine(output, "updates", "staged.json")); File.Delete(RepairShortcut(output)); } catch (IOException) { }
                log.Line($"AimMod was removed from {win64}." + (kept > 0 ? $" {kept} changed file(s) were kept." : "") + " Your history and replays were kept.");
                return 0;
            }
            if (args.Contains("--rollback"))
            {
                var version = applier.RollbackLast();
                SwapCache(output, toPrevious: true);
                Record(updatesRoot, new("rollback", true, version, $"AimMod was rolled back to {version}.", Now()));
                log.Line($"AimMod was rolled back to {version}.");
                return 0;
            }
            if (applier.RecoverInterrupted()) log.Line("An interrupted install was undone.");
            string? root;
            if (Arg(args, "--package") is { } p) root = Path.GetFullPath(p);
            else if (args.Contains("--install")) root = InstallLayout.FindPackageRoot(AppContext.BaseDirectory);
            else root = Directory.Exists(PackageCache(output)) ? PackageCache(output) : InstallLayout.FindPackageRoot(AppContext.BaseDirectory);
            if (root is null) { log.Line("No AimMod package was found. Download AimMod again and run Install-AimMod.cmd."); return 1; }
            var package = VerifiedPackage.Open(root);
            log.Line($"AimMod {package.Manifest.Version}: all {package.Manifest.Files.Length} files match the release manifest.");
            var kind = args.Contains("--install") ? "install" : "repair";
            var result = applier.Apply(win64, package, kind);
            CachePackage(output, package);
            InstallRepairShortcut(output, package);
            UpdateRequestsFile(updatesRoot, r => r with { Repair = false });
            Record(updatesRoot, new(kind, true, package.Manifest.Version, kind == "install" ? $"AimMod {package.Manifest.Version} was installed." : "AimMod was repaired.", Now()));
            log.Line($"AimMod {package.Manifest.Version} {(kind == "install" ? "installed" : "repaired")} in {win64} ({result.Changed} file(s) placed).");
            var game = InstallHealth.CheckBuild(InstallLayout.SteamBuildId(win64), package.Manifest.Game);
            if (game.Warning is not null) log.Line(game.Warning);
            return 0;
        }
        catch (Exception ex) when (ex is InstallException or ReleaseFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Line(ex.Message);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
    // --verify-release <feed.json> --zip <package.zip>: checks a built release
    // the way the updater will (feed format, zip size and SHA-256, manifest
    // SHA-256, every file). Used by the release workflow before uploading.
    public static int VerifyRelease(string[] args)
    {
        var feedPath = Arg(args, "--verify-release");
        var zip = Arg(args, "--zip");
        if (feedPath is null || zip is null || !File.Exists(feedPath) || !File.Exists(zip)) { Console.Error.WriteLine("Usage: --verify-release <feed.json> --zip <package.zip>"); return 1; }
        var temp = Path.Combine(Path.GetTempPath(), "aimmod-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var feed = UpdateFeed.Parse(File.ReadAllBytes(feedPath));
            if (new FileInfo(zip).Length != feed.Package.Size || !Sha256Hex.Same(Sha256Hex.OfFile(zip), feed.Package.Sha256)) throw new ReleaseFormatException("The zip does not match the feed's size and SHA-256.");
            Updater.ExtractVerified(zip, temp, feed.ManifestSha256);
            var package = VerifiedPackage.Open(temp, feed.ManifestSha256);
            if (package.Manifest.Version != feed.Version) throw new ReleaseFormatException("The manifest version does not match the feed.");
            Console.WriteLine($"Valid {feed.Channel} release {feed.Version}: {package.Manifest.Files.Length} files, mods {string.Join(", ", package.Manifest.Mods)}.");
            return 0;
        }
        catch (Exception ex) when (ex is ReleaseFormatException or IOException or InvalidDataException or InvalidOperationException)
        { Console.Error.WriteLine("Invalid release: " + ex.Message); return 1; }
        finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { } }
    }
    static string ResolveGameDir(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var candidate in new[] { full, Path.Combine(full, "FPSAimTrainer", "Binaries", "Win64"), Path.Combine(full, "Binaries", "Win64") })
            if (InstallLayout.IsWin64(candidate)) return candidate;
        return full;
    }
    static ReleaseManifest? CachedRelease(string output)
    {
        try { var path = Path.Combine(PackageCache(output), InstallLayout.PackageManifest); return File.Exists(path) ? ReleaseManifest.Parse(File.ReadAllBytes(path)) : null; }
        catch (ReleaseFormatException) { return null; }
    }
    static void Record(string root, LifecycleResult result) => WriteJson(ResultPath(root), result);
    static void UpdateRequestsFile(string root, Func<LifecycleRequests, LifecycleRequests> change) =>
        WriteJson(RequestsPath(root), change(ReadJson<LifecycleRequests>(RequestsPath(root)) ?? new()));

    // package\current is the verified copy used for repairs; package\previous
    // is kept for a rollback.
    static void CachePackage(string output, VerifiedPackage package)
    {
        var current = PackageCache(output);
        if (string.Equals(Path.GetFullPath(package.Root).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        var previous = PreviousPackageCache(output);
        var incoming = current + ".incoming";
        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        CopyTree(package.Root, incoming);
        if (Directory.Exists(current))
        {
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            Directory.Move(current, previous);
        }
        Directory.Move(incoming, current);
    }
    static void SwapCache(string output, bool toPrevious)
    {
        var current = PackageCache(output); var previous = PreviousPackageCache(output);
        if (!toPrevious || !Directory.Exists(previous)) return;
        if (Directory.Exists(current)) Directory.Delete(current, true);
        Directory.Move(previous, current);
    }
    static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }
    // Next to the data folder: %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd by default.
    static string RepairShortcut(string output) => Path.Combine(Path.GetDirectoryName(output.TrimEnd('\\'))!, "Repair-AimMod.cmd");
    static void InstallRepairShortcut(string output, VerifiedPackage package)
    {
        // %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd: works after a game update
        // even when the mod no longer loads (it runs the cached package).
        var source = Path.Combine(package.Root, "Repair-AimMod.cmd");
        try { if (File.Exists(source)) File.Copy(source, RepairShortcut(output), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static int ApplyPending(string[] args, string output, Func<string, bool> running, Log log)
    {
        var updatesRoot = Path.Combine(output, "updates");
        if (int.TryParse(Arg(args, "--wait-pid"), out var pid))
            try { using var parent = Process.GetProcessById(pid); parent.WaitForExit(60_000); } catch (ArgumentException) { }
        var win64 = Arg(args, "--game-dir") is { } g ? ResolveGameDir(g) : null;
        if (win64 is null || !InstallLayout.IsWin64(win64)) { log.Line("KovaaK's was not found."); return 1; }
        // Give a quick restart of the game a moment, but never touch files while it runs.
        for (int i = 0; i < 24 && running(win64); i++) Thread.Sleep(5000);
        if (running(win64)) { log.Line("KovaaK's is running again; the update waits for the next time it closes."); return 2; }
        var applier = new PackageApplier(updatesRoot, running);
        if (applier.RecoverInterrupted()) log.Line("An interrupted install was undone.");
        var updater = new Updater(updatesRoot);
        var prefs = new UpdateSettings(output).Current;
        var requests = ReadJson<LifecycleRequests>(RequestsPath(updatesRoot)) ?? new();
        var staged = updater.Staged();
        if (staged is not null && (prefs.AutoUpdate || requests.Install) && requests.SkipVersion != staged.Version)
        {
            try
            {
                var package = updater.OpenStaged(staged);
                var result = applier.Apply(win64, package, "update");
                CachePackage(output, package);
                InstallRepairShortcut(output, package);
                updater.ClearStaged();
                UpdateRequestsFile(updatesRoot, r => new());
                WriteJson(ConfirmPath(updatesRoot), new LifecycleResult("update", true, package.Manifest.Version, "", Now()));
                Record(updatesRoot, new("update", true, package.Manifest.Version, $"AimMod was updated to {package.Manifest.Version}" + (result.PreviousVersion is { } p ? $" (from {p})." : "."), Now()));
                log.Line($"AimMod updated to {package.Manifest.Version}.");
                return 0;
            }
            catch (Exception ex) when (ex is InstallException or ReleaseFormatException or IOException or UnauthorizedAccessException)
            {
                // Rolled back by the applier. Do not retry this version on every close.
                UpdateRequestsFile(updatesRoot, r => r with { Install = false, SkipVersion = staged.Version });
                if (ex is ReleaseFormatException) updater.ClearStaged();
                Record(updatesRoot, new("update", false, staged.Version, $"AimMod {staged.Version} could not be installed: {ex.Message}", Now()));
                log.Line($"Update to {staged.Version} failed: {ex.Message}");
                if (!requests.Repair) return 1;
            }
        }
        if (requests.Repair && Directory.Exists(PackageCache(output)))
        {
            try
            {
                var package = VerifiedPackage.Open(PackageCache(output));
                applier.Apply(win64, package, "repair");
                UpdateRequestsFile(updatesRoot, r => r with { Repair = false });
                Record(updatesRoot, new("repair", true, package.Manifest.Version, "AimMod was repaired.", Now()));
                log.Line("AimMod was repaired.");
                return 0;
            }
            catch (Exception ex) when (ex is InstallException or ReleaseFormatException or IOException or UnauthorizedAccessException)
            {
                UpdateRequestsFile(updatesRoot, r => r with { Repair = false });
                Record(updatesRoot, new("repair", false, null, $"The repair failed: {ex.Message} Run Repair-AimMod.cmd with KovaaK's closed.", Now()));
                log.Line($"Repair failed: {ex.Message}");
                return 1;
            }
        }
        return 0;
    }
}
