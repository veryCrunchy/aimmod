using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AimMod.InGame;

sealed class WorkspaceHost : IAsyncDisposable
{
    readonly WebApplication app;
    readonly NativeReplayPlayback playback;
    readonly ReplayKeyboard keyboard;
    readonly RendererAcknowledgement renderer;
    readonly OverlaySettings overlaySettings;
    readonly OpponentData opponents;
    readonly ObsOverlayHost obs;
    readonly string outputFolder;
    readonly LiveOverlayFeed liveFeed = new();
    Run[] overlayRuns = [];
    public void UpdateHistory(Run[] runs) => Volatile.Write(ref overlayRuns, runs);
    object OverlayState() => new { live = opponents.Apply(liveFeed.Read(outputFolder, Volatile.Read(ref overlayRuns))), settings = overlaySettings.Current with { Layouts = [] } };
    object ObsState() => overlaySettings.Current.ObsEnabled ? OverlayState() : new { live = new { available = false, active = false }, settings = overlaySettings.Current with { Layouts = [] } };
    internal static int ReadRendererProtocol(string path) => new RendererAcknowledgement(path).Read().Protocol;
    bool RendererReady => renderer.Read().Ready;
    object PlaybackStatus() {
        var acknowledgement = renderer.Read();
        return new { playback = playback.Status, rendererReady = acknowledgement.Ready, rendererReason = acknowledgement.Reason };
    }
    string data = "{}";
    public string Url { get; private set; } = "";
    public void Update(string json) => Volatile.Write(ref data, json);
    readonly Multiplayer.MultiplayerService multiplayer;
    public WorkspaceHost(Hub hub, string output, string? historyPath = null, NativeSettings? settings = null, CsvHistory? csvHistory = null, string[]? args = null)
    {
        outputFolder = output;
        overlaySettings = new OverlaySettings(output);
        opponents = new OpponentData(output);
        obs = new ObsOverlayHost(output, ObsState);
        renderer = new RendererAcknowledgement(Path.Combine(output, "native-replay-renderer.json"));
        playback = new NativeReplayPlayback(output, () => RendererReady, () => renderer.Read().Protocol);
        keyboard = new ReplayKeyboard(playback, output);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var prefix = "/" + capability;
        (settings ?? new NativeSettings(output)).MapEndpoints(app, prefix);
        overlaySettings.MapEndpoints(app, prefix);
        new CoachingFeedback(output).MapEndpoints(app, prefix);
        multiplayer = Multiplayer.MultiplayerHosting.Create(hub, output, args ?? [], () => liveFeed.Read(outputFolder, Volatile.Read(ref overlayRuns)), () => Volatile.Read(ref overlayRuns));
        multiplayer.MapEndpoints(app, prefix);
        Multiplayer.MultiplayerHosting.MapAssets(app, prefix);
        var importedHistory = csvHistory ?? new CsvHistory(output);
        app.MapGet(prefix + "/history-import.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.HistoryImport")!, "application/javascript"));
        app.MapPost(prefix + "/history-import", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 4096) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (!doc.RootElement.TryGetProperty("directory", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return Results.BadRequest();
                var directory = value.GetString();
                if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || !Path.IsPathFullyQualified(directory)) return Results.BadRequest();
                var result = await Task.Run(() => importedHistory.Import(directory, Volatile.Read(ref overlayRuns)));
                return Results.Json(result);
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return Results.BadRequest(); }
              catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(422); }
        });
        app.MapGet(prefix + "/overlay-opponents", () => Results.Json(opponents.Read()));
        app.MapPost(prefix + "/overlay-opponents", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (!doc.RootElement.TryGetProperty("key", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return Results.BadRequest();
                var key = value.GetString();
                if (key is null || key.Length > 64) return Results.BadRequest();
                return opponents.Select(key) ? Results.Json(opponents.Read()) : Results.Conflict();
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException) { return Results.BadRequest(); }
        });
        ObsOverlayHost.MapAssets(app, prefix);
        app.MapGet(prefix + "/overlay-state", () => Results.Json(OverlayState()));
        app.MapGet(prefix + "/overlay-setup", () => Results.Json(new { obsAvailable = obs.Available, obsUrl = obs.Url, width = 1920, height = 1080 }));
        app.MapGet(prefix + "/overlay-editor.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.OverlayEditor")!, "application/javascript"));
        app.MapGet(prefix + "/overlay-editor.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.OverlayEditorStyle")!, "text/css"));
        using var stream = typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.UI")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = reader.ReadToEnd();
        var replays = new ReplayCatalog(output);
        var library = new ReplayLibrary(replays, output);
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        app.MapGet(prefix + "/ui", () => Results.Content(html, "text/html", Encoding.UTF8));
        app.MapGet(prefix + "/settings.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.SettingsScript")!, "application/javascript"));
        app.MapGet(prefix + "/coaching.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.CoachingScript")!, "application/javascript"));
        app.MapGet(prefix + "/coaching.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.CoachingStyle")!, "text/css"));
        app.MapGet(prefix + "/workspace-theme.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.WorkspaceTheme")!, "text/css"));
        app.MapGet(prefix + "/mechanics.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.MechanicsScript")!, "application/javascript"));
        app.MapGet(prefix + "/benchmarks.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.BenchmarksScript")!, "application/javascript"));
        app.MapGet(prefix + "/benchmarks.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.BenchmarksStyle")!, "text/css"));
        app.MapGet(prefix + "/benchmarks", () => Results.Json(hub.BenchmarkList()));
        app.MapGet(prefix + "/leaderboard.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.LeaderboardScript")!, "application/javascript"));
        app.MapGet(prefix + "/leaderboard.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.LeaderboardStyle")!, "text/css"));
        app.MapGet(prefix + "/leaderboard", async (string? scenarioType, CancellationToken token) => {
            try { var page = await hub.Leaderboard(scenarioType ?? "", token); return page is null ? Results.BadRequest() : Results.Json(page); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/benchmark", async (uint id, CancellationToken token) => {
            if (id == 0) return Results.BadRequest();
            try { var detail = await hub.BenchmarkPage(id, token); return detail is null ? Results.NotFound() : Results.Json(detail); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/statistics.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.StatisticsScript")!, "application/javascript"));
        app.MapGet(prefix + "/statistics.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.StatisticsStyle")!, "text/css"));
        app.MapGet(prefix + "/run-details.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.RunDetailsScript")!, "application/javascript"));
        app.MapGet(prefix + "/run-details.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.RunDetailsStyle")!, "text/css"));
        app.MapGet(prefix + "/run-details/{id}", (string id, int? shotPage) => {
            if (id.Length is 0 or > 512 || shotPage is < 0 or > 100000) return Results.BadRequest();
            if (historyPath is null) return Results.NotFound();
            try {
                var inspection = RunInspection.Read(historyPath, id, shotPage ?? 0);
                return inspection.Run is null ? Results.NotFound() : Results.Content(System.Text.Json.JsonSerializer.Serialize(inspection), "application/json", Encoding.UTF8);
            } catch (IOException) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/logo.png", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.Logo")!, "image/png"));
        app.MapGet(prefix + "/browser.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.ReplayBrowser")!, "application/javascript"));
        app.MapGet(prefix + "/native-browser.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.NativeReplayBrowser")!, "application/javascript"));
        app.MapGet(prefix + "/replays", () => {
            try { return Results.Json(library.List()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return Results.StatusCode(503); }
        });
        app.MapPost(prefix + "/replay-library", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                var command = await context.Request.ReadFromJsonAsync<LibraryCommand>(context.RequestAborted);
                if (command is null || command.Id is null) return Results.BadRequest();
                bool ok;
                switch (command.Action) {
                    case "favorite" when command.Favorite.HasValue: ok = library.Favorite(command.Id, command.Favorite.Value); break;
                    case "delete": ok = library.Delete(command.Id); break;
                    case "export": ok = library.Export(command.Id); break;
                    default: return Results.BadRequest();
                }
                return ok ? Results.Json(new { ok = true }) : Results.NotFound();
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
              catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/native-replay", () => Results.Json(PlaybackStatus()));
        app.MapPost(prefix + "/native-replay", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024)
                return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                var command = await context.Request.ReadFromJsonAsync<PlaybackCommand>(context.RequestAborted);
                if (command is null) return Results.BadRequest();
                if (command.Action == "load") {
                    if (!RendererReady) return Results.StatusCode(409);
                    var replay = replays.Read(command.Id ?? "");
                    if (replay is null || replay.Frames.Count < 2) return Results.NotFound();
                    if (string.IsNullOrWhiteSpace(replay.MapName) || replay.MapScale is null) return Results.UnprocessableEntity();
                    playback.Load(replay);
                } else if (!playback.Command(command.Action ?? "", command.Value, command.Area)) return Results.BadRequest();
                return Results.Json(PlaybackStatus());
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
        });
        app.MapGet(prefix + "/replay-status", () => {
            try {
                var path = Path.Combine(output, "replay-status.json");
                if (File.Exists(path) && new FileInfo(path).Length <= 4096) {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    return Results.Json(document.RootElement.Clone());
                }
            } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            return Results.Json(new { state = "unavailable" });
        });
        app.MapGet(prefix + "/replays/{id}", (string id) => {
            var replay = replays.Read(id);
            return replay is null ? Results.NotFound() : Results.Json(replay);
        });
        app.MapGet(prefix + "/data", () => Results.Content(Volatile.Read(ref data), "application/json", Encoding.UTF8));
        app.MapPost(prefix + "/command", async (HttpContext context) =>
        {
            // Capability URL + non-simple header and no CORS prevent arbitrary web
            // pages from issuing account or UI commands to the local worker.
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024)
                return Results.StatusCode(403);
            using var input = new StreamReader(context.Request.Body);
            var command = await input.ReadToEndAsync(context.RequestAborted);
            if (command == "close") { playback.Command("close"); File.WriteAllText(Path.Combine(output, "close.request"), "1"); }
            else if (!hub.Enqueue(command)) return Results.BadRequest();
            return Results.NoContent();
        });
    }
    public async Task Start(CancellationToken token)
    {
        await app.StartAsync(token);
        playback.Start();
        keyboard.Start();
        // Recover the registered UI route without exposing a general file server.
        var endpoint = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().First(e => e.RoutePattern.RawText!.EndsWith("/ui"));
        Url = app.Urls.Single() + endpoint.RoutePattern.RawText;
        File.WriteAllText(Path.Combine(outputFolder, "live-overlay-url.txt"), Url[..^3] + "/overlay?surface=game");
        try { await obs.Start(token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            await obs.DisposeAsync(); Console.Error.WriteLine("OBS browser source could not start (" + ex.GetType().Name + ").");
        }
    }
    public async ValueTask DisposeAsync() { multiplayer.Dispose(); await keyboard.DisposeAsync(); await app.StopAsync(); await playback.DisposeAsync(); await obs.DisposeAsync(); await app.DisposeAsync(); }
    sealed record PlaybackCommand(string? Action, string? Id, double? Value, double[]? Area);
    sealed record LibraryCommand(string? Action, string? Id, bool? Favorite);
}

// Lua replaces this acknowledgement once a second. A missing file or sharing
// violation during publication must not terminate playback. Cache only a valid
// acknowledgement, and never extend its original three-second freshness bound.
sealed class RendererAcknowledgement(string path, Func<DateTime>? clock = null)
{
    internal readonly record struct State(bool Ready, int Protocol, string Reason);
    readonly object gate = new();
    static readonly State Unavailable = new(false, 2, "unavailable");
    State cached = Unavailable;
    DateTime validUntil;
    internal State Read() {
        lock (gate) {
            var now = clock?.Invoke() ?? DateTime.UtcNow;
            State Invalidate() { cached = Unavailable; validUntil = default; return cached; }
            State Transient() => now <= validUntil ? cached : Invalidate();
            try {
                var file = new FileInfo(path);
                if (!file.Exists) return Transient();
                var stamp = file.LastWriteTimeUtc;
                if (file.Length > 4096 || now - stamp > TimeSpan.FromSeconds(3) || stamp - now > TimeSpan.FromSeconds(1)) return Invalidate();
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (!root.TryGetProperty("state", out var state) || state.GetString() != "ready"
                    || !root.TryGetProperty("mode", out var mode) || mode.GetString() != "main") {
                    Invalidate();
                    var reason = root.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
                    return new(false, 2, reason is "challenge-active" or "scenario-active" or "map-mismatch" or "scenario-mismatch" ? reason : "unavailable");
                }
                var protocol = root.TryGetProperty("protocol", out var capability) && capability.TryGetInt32(out var version) && version >= 3 ? Math.Min(version, 5) : 2;
                cached = new(true, protocol, "unavailable");
                validUntil = stamp.AddSeconds(3);
                return cached;
            } catch (IOException) { return Transient(); }
              catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { return Invalidate(); }
        }
    }
}
