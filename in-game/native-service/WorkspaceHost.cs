using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

sealed class WorkspaceHost : IAsyncDisposable
{
    readonly WebApplication app;
    readonly NativeReplayPlayback playback;
    readonly ReplayStartGate startGate = new();
    readonly ReplayAutoLoad autoLoad;
    readonly GameCommands gameCommands;
    readonly CancellationTokenSource startLoop = new();
    ReplayCatalog? replayCatalog;
    readonly ReplayKeyboard keyboard;
    readonly RendererAcknowledgement renderer;
    readonly OverlaySettings overlaySettings;
    readonly OpponentData opponents;
    readonly ObsOverlayHost obs;
    Tournaments.TournamentService? tournaments;
    public Tournaments.TournamentService? TournamentsService => tournaments;
    readonly string outputFolder;
    readonly LiveOverlayFeed liveFeed = new();
    Run[] overlayRuns = [];
    IReadOnlyDictionary<string, Run> personalBests = new Dictionary<string, Run>();
    string? overlayUrl;
    // Overlay sources poll several times a second; resolve personal bests once
    // per history revision instead of scanning the complete history per poll.
    public void UpdateHistory(Run[] runs)
    {
        Volatile.Write(ref personalBests, LiveOverlayState.PersonalBests(runs));
        Volatile.Write(ref overlayRuns, runs);
    }
    Run? PersonalBest(string scenario) => Volatile.Read(ref personalBests).GetValueOrDefault(scenario);
    // Discord presence reads the same live snapshot without sharing the overlay
    // feed's transient-gap memory.
    public LiveOverlaySnapshot ReadLive() => LiveOverlayState.Read(outputFolder, PersonalBest, playback.Visible);
    public bool ReplayVisible => playback.Visible;
    public string? ReplayScenario => playback.VisibleScenario;
    // Page of the AimMod panel while it is shown, as reported by the UI.
    public readonly DiscordWorkspaceView View = new();
    object OverlayState() => new { live = opponents.Apply(liveFeed.Accept(LiveOverlayState.Read(outputFolder, PersonalBest, playback.Visible), DateTime.UtcNow)), settings = overlaySettings.Current with { Layouts = [] } };
    object ObsState() => overlaySettings.Current.ObsEnabled ? OverlayState() : new { live = new { available = false, active = false }, settings = overlaySettings.Current with { Layouts = [] } };
    internal static int ReadRendererProtocol(string path) => new RendererAcknowledgement(path).Read().Protocol;
    bool RendererReady => renderer.Read().Ready;
    object PlaybackStatus() {
        var acknowledgement = renderer.Read();
        return new { playback = playback.Status, rendererReady = acknowledgement.Ready, rendererReason = acknowledgement.Reason, start = startGate.Status };
    }
    string data = "{}";
    public string Url { get; private set; } = "";
    public void Update(string json) => Volatile.Write(ref data, json);
    readonly Multiplayer.MultiplayerService multiplayer;
    public Multiplayer.MultiplayerService MultiplayerLobby => multiplayer;
    public WorkspaceHost(Hub hub, string output, string? historyPath = null, NativeSettings? settings = null, CsvHistory? csvHistory = null, DiscordSettings? discordSettings = null, Func<object>? discordStatus = null, string[]? args = null, Lifecycle? lifecycle = null, HubSharingSettings? hubSharing = null, Func<object>? hubSharingStatus = null)
    {
        outputFolder = output;
        gameCommands = new GameCommands(output);
        overlaySettings = new OverlaySettings(output);
        opponents = new OpponentData(output);
        obs = new ObsOverlayHost(output, ObsState, () => tournaments?.ObsView() ?? new { v = 1 }, () => multiplayer?.BoardView());
        renderer = new RendererAcknowledgement(Path.Combine(output, "native-replay-renderer.json"));
        playback = new NativeReplayPlayback(output, () => RendererReady, () => renderer.Read().Protocol);
        keyboard = new ReplayKeyboard(playback, output);
        app = LoopbackServer.Build(0);
        // 192 random bits; compared in constant time before routing.
        var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var prefix = "/" + capability;
        LoopbackServer.UseGuards(app, capability);
        (settings ?? new NativeSettings(output)).MapEndpoints(app, prefix);
        overlaySettings.MapEndpoints(app, prefix);
        lifecycle?.MapEndpoints(app, prefix);
        discordSettings?.MapEndpoints(app, prefix, discordStatus);
        hubSharing?.MapEndpoints(app, prefix, hubSharingStatus);
        app.MapGet(prefix + "/hub-sharing.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.HubSharingScript")!, "application/javascript"));
        app.MapPost(prefix + "/workspace-view", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 256) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("page", out var page) || page.ValueKind != System.Text.Json.JsonValueKind.String
                    || !root.TryGetProperty("visible", out var shown) || shown.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return Results.BadRequest();
                var key = page.GetString();
                if (!DiscordWorkspaceView.ValidPage(key)) return Results.BadRequest();
                View.Report(key!, shown.GetBoolean(), DateTimeOffset.UtcNow);
                return Results.Json(new { ok = true });
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
        });
        app.MapGet(prefix + "/discord-settings.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.DiscordSettingsScript")!, "application/javascript"));
        new CoachingFeedback(output).MapEndpoints(app, prefix);
        multiplayer = Multiplayer.MultiplayerHosting.Create(hub, output, args, () => liveFeed.Read(outputFolder, Volatile.Read(ref overlayRuns)), () => Volatile.Read(ref overlayRuns));
        multiplayer.MapEndpoints(app, prefix);
        // A pending replay's scenario loads by itself, through the lobby's load path (ScenarioLoader).
        var lobby = multiplayer;
        autoLoad = new ReplayAutoLoad(startGate, new Multiplayer.CoreGameControl(output), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lobby.Library, lobby.SourceOf);
        Multiplayer.MultiplayerHosting.MapAssets(app, prefix);
        // Tournaments come from AimMod Hub as the linked account; developer mode can simulate one.
        var live = multiplayer;
        tournaments = new Tournaments.TournamentService(new HubTournamentSource(hub), new Tournaments.MultiplayerTournamentLobby(live), () => live.SimulationOn,
            live.LibraryScenarioNames, () => live.SelfName, args is null ? null : output, autoTick: args is not null);
        multiplayer.Tournaments = tournaments;
        tournaments.MapEndpoints(app, prefix);
        app.MapGet(prefix + "/tournaments.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.TournamentsScript")!, "application/javascript"));
        app.MapGet(prefix + "/tournaments.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.TournamentsStyle")!, "text/css"));
        var importedHistory = csvHistory ?? new CsvHistory(output);
        app.MapGet(prefix + "/history-import.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.HistoryImport")!, "application/javascript"));
        app.MapPost(prefix + "/history-import", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 4096) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (!doc.RootElement.TryGetProperty("directory", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return Results.BadRequest();
                var directory = value.GetString();
                if (!CsvHistory.AcceptableDirectory(directory)) return Results.BadRequest();
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
        app.MapGet(prefix + "/overlay-setup", () => Results.Json(new { obsAvailable = obs.Available, obsUrl = obs.Url, boardUrl = obs.BoardUrl, width = 1920, height = 1080 }));
        app.MapGet(prefix + "/overlay-editor.js", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.OverlayEditor")!, "application/javascript"));
        app.MapGet(prefix + "/overlay-editor.css", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.OverlayEditorStyle")!, "text/css"));
        using var stream = typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.UI")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = reader.ReadToEnd();
        var replays = new ReplayCatalog(output);
        replayCatalog = replays;
        var library = new ReplayLibrary(replays, output);
        var inbox = new ReplayInbox(output, replays);
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
            catch (Exception ex) when (HubUnavailable(ex, token)) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/benchmark", async (uint id, CancellationToken token) => {
            if (id == 0) return Results.BadRequest();
            try { var detail = await hub.BenchmarkPage(id, token); return detail is null ? Results.NotFound() : Results.Json(detail); }
            catch (Exception ex) when (HubUnavailable(ex, token)) { return Results.StatusCode(503); }
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
                // Completed runs and their replays share an id: native:<replayId> and replays/<replayId>.amreplay.
                if (inspection.Run is not null) inspection = inspection with { ReplayId = ReplayFor(replays, inspection.Run.Id) };
                return inspection.Run is null ? Results.NotFound() : Results.Content(System.Text.Json.JsonSerializer.Serialize(inspection), "application/json", Encoding.UTF8);
            } catch (IOException) { return Results.StatusCode(503); }
        });
        app.MapGet(prefix + "/logo.png", () => Results.Stream(typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.Logo")!, "image/png"));
        // Mode card icons, embedded from ui/art/modes/png; only the generated names resolve.
        app.MapGet(prefix + "/art/modes/{file}", (string file) =>
            System.Text.RegularExpressions.Regex.IsMatch(file, @"^[a-z-]{2,24}(-on)?@[23]x\.png$")
            && typeof(WorkspaceHost).Assembly.GetManifestResourceStream("AimMod.ModeIcon." + file) is { } icon
                ? Results.Stream(icon, "image/png") : Results.NotFound());
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
        // Replays received from other players (format 2 only), validated
        // completely before they enter the library.
        app.MapPost(prefix + "/replays/import", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or <= 24 or > 8 * 1024 * 1024) return Results.StatusCode(403);
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var result = ReplayImport.Import(outputFolder, body.ToArray());
            return result.Id is null ? Results.Json(new { error = result.Error }, statusCode: 422) : Results.Json(new { id = result.Id, scenario = result.Scenario });
        });
        // Gameface has no file picker or file drop, so the Replays page offers the
        // .amreplay files in Downloads and Documents\AimMod\Replays instead.
        app.MapGet(prefix + "/replays/importable", () => {
            try { return Results.Json(inbox.List()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        });
        app.MapPost(prefix + "/replays/import-file", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                var pick = await context.Request.ReadFromJsonAsync<ImportPick>(context.RequestAborted);
                var result = inbox.Import(pick?.Source, pick?.Name);
                return result.Id is null ? Results.Json(new { error = result.Error }, statusCode: result.Error is "unknown-source" or "invalid-name" ? 400 : result.Error == "missing" ? 404 : 422) : Results.Json(new { id = result.Id, scenario = result.Scenario });
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
              catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        });
        // Game control (multiplayer lobby, replay scenario load). AimModCore
        // validates again and refuses while a challenge runs.
        app.MapGet(prefix + "/game-command", () => Results.Json(new { capabilities = GameCommands.Capabilities(outputFolder), result = gameCommands.Result(), results = gameCommands.Results() }));
        app.MapPost(prefix + "/game-command", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 2048) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                var request = await context.Request.ReadFromJsonAsync<GameCommandRequest>(context.RequestAborted);
                if (request is null) return Results.BadRequest();
                var capabilities = GameCommands.Capabilities(outputFolder);
                var needed = request.Action switch { "load-scenario" or "refresh-scenarios" or "end-run" => "load", "capture-thumbnail" => "capture", "quit-run" => "quit", _ => "start" };
                if (!capabilities.Contains(needed)) return Results.Json(new { error = "unsupported" }, statusCode: 409);
                var (sequence, error) = gameCommands.Send(request);
                return sequence is null ? Results.Json(new { error }, statusCode: 400) : Results.Json(new { sequence });
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return Results.BadRequest(); }
              catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.StatusCode(503); }
        });
        app.MapPost(prefix + "/native-replay", async (HttpContext context) => {
            if (context.Request.Headers["X-AimMod-UI"] != "1" || context.Request.ContentLength is null or > 1024)
                return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            try {
                var command = await context.Request.ReadFromJsonAsync<PlaybackCommand>(context.RequestAborted);
                if (command is null) return Results.BadRequest();
                if (command.Action == "spectate") {
                    // Follow a live view (multiplayer bridge -> spectate-pose.tsv) in this world.
                    if (string.IsNullOrWhiteSpace(command.Scenario) || command.Scenario.Length > 512 || string.IsNullOrWhiteSpace(command.MapName) || command.MapScale is not > 0)
                        return Results.BadRequest();
                    if (command.Stream is not null && !LivePoseFrame.IsStreamId(command.Stream)) return Results.BadRequest();
                    // Already watching this world: switch the followed player in place.
                    if (playback.FollowStream(command.Scenario, command.MapName, command.Stream)) return Results.Json(PlaybackStatus());
                    var feed = new LivePoseFeed(Path.Combine(outputFolder, "spectate-pose.tsv"), command.Stream);
                    if (!feed.Update()) return Results.Json(new { error = "stream-unavailable", message = "No live view is being received." }, statusCode: 409);
                    var placeholder = new NativeReplay(2, "live", command.Scenario, "", "live", 0, [], [], command.MapName, command.MapScale);
                    var ack = renderer.Read();
                    var blockedLive = ReplayStartGate.Evaluate(placeholder, GameScene.Read(outputFolder), ack.Ready, ack.Reason);
                    if (blockedLive is not null) return Results.Json(new { error = blockedLive.Reason, message = blockedLive.Message }, statusCode: 409);
                    var label = new string((command.Label ?? "peer").Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
                    playback.Spectate(feed, command.Scenario, command.MapName, command.MapScale.Value, label.Length > 0 ? label : "peer");
                    return Results.Json(PlaybackStatus());
                }
                if (command.Action == "load") {
                    var replay = replays.Read(command.Id ?? "");
                    if (replay is null || replay.Frames.Count < 2) return Results.NotFound();
                    if (string.IsNullOrWhiteSpace(replay.MapName) || replay.MapScale is null) return Results.UnprocessableEntity();
                    // Run vs run: a second replay of the same scenario as a ghost.
                    NativeReplay? compareWith = null;
                    if (command.CompareId is { Length: > 0 } compareId) {
                        compareWith = replays.Read(compareId);
                        if (compareWith is null || compareWith.Frames.Count < 2) return Results.NotFound();
                        if (compareWith.Scenario != replay.Scenario) return Results.Json(new { error = "scenario-mismatch", message = "Both runs must be of the same scenario." }, statusCode: 422);
                    }
                    // Start now if the game shows the replay's world in the pause
                    // menu; otherwise wait (with the reason) and start by itself.
                    var acknowledgement = renderer.Read();
                    var blocked = ReplayStartGate.Evaluate(replay, GameScene.Read(outputFolder), acknowledgement.Ready, acknowledgement.Reason);
                    if (blocked is not null) { startGate.Wait(replay.Id, replay.Scenario, blocked, compareWith?.Id, replay.MapName, replay.MapScale); return Results.Json(PlaybackStatus(), statusCode: 202); }
                    startGate.Clear();
                    playback.Load(replay, compareWith);
                } else if (command.Action == "cancel") startGate.Clear();
                // The pending replay's scenario from the Steam Workshop (the Map Library's install).
                else if (command.Action == "download") {
                    if (startGate.PendingScenario is not { } missing) return Results.Json(new { error = "none", message = "No replay is waiting for a scenario." }, statusCode: 409);
                    var download = multiplayer.DownloadScenario(missing);
                    if (!download.Ok) return Results.Json(new { error = download.Code, message = download.Message }, statusCode: 409);
                }
                else {
                    if (command.Action == "close") startGate.Clear();
                    if (!playback.Command(command.Action ?? "", command.Value, command.Area)) return Results.BadRequest();
                }
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
        _ = Task.Run(async () => {
            // Pending replay starts: re-evaluated four times a second.
            try {
                while (!startLoop.IsCancellationRequested) {
                    await Task.Delay(250, startLoop.Token);
                    try {
                        var ready = startGate.Poll(id => replayCatalog?.Read(id), () => GameScene.Read(outputFolder), () => { var a = renderer.Read(); return (a.Ready, a.Reason); });
                        if (ready is not null) {
                            var compareId = startGate.TakeCompare();
                            var compareWith = compareId is null ? null : replayCatalog?.Read(compareId);
                            playback.Load(ready, compareWith is not null && compareWith.Scenario == ready.Scenario ? compareWith : null);
                        }
                        else autoLoad.Tick();
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            } catch (OperationCanceledException) { }
        });
        // Recover the registered UI route without exposing a general file server.
        var endpoint = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().First(e => e.RoutePattern.RawText!.EndsWith("/ui"));
        Url = LoopbackServer.VerifiedAddress(app) + endpoint.RoutePattern.RawText;
        overlayUrl = Url[..^3] + "/overlay?surface=game";
        AtomicFile.WriteText(Path.Combine(outputFolder, "live-overlay-url.txt"), overlayUrl);
        try { await obs.Start(token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            await obs.DisposeAsync(); Console.Error.WriteLine("OBS browser source could not start (" + ex.GetType().Name + ").");
        }
    }
    public async ValueTask DisposeAsync()
    {
        // Stop accepting requests first, then publish a closed replay frame and
        // retract this process's overlay URL so the game never loads a dead port.
        startLoop.Cancel();
        tournaments?.Dispose();
        multiplayer.Dispose();
        await keyboard.DisposeAsync();
        try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
        await playback.DisposeAsync(); await obs.DisposeAsync(); await app.DisposeAsync();
        if (overlayUrl is not null) AtomicFile.DeleteIfContent(Path.Combine(outputFolder, "live-overlay-url.txt"), overlayUrl);
    }
    // An HttpClient timeout surfaces as a cancellation that the caller did not request.
    internal static string? ReplayFor(ReplayCatalog catalog, string runId) =>
        runId.StartsWith("native:", StringComparison.Ordinal) && runId.Length > 7 && catalog.Resolve(runId[7..]) is not null ? runId[7..] : null;
    static bool HubUnavailable(Exception ex, CancellationToken token) => ex is IOException or HttpRequestException or System.Text.Json.JsonException
        || ex is OperationCanceledException && !token.IsCancellationRequested;
    sealed record PlaybackCommand(string? Action, string? Id, double? Value, double[]? Area, string? CompareId = null, string? Scenario = null, string? MapName = null, double? MapScale = null, string? Label = null, string? Stream = null);
    sealed record LibraryCommand(string? Action, string? Id, bool? Favorite);
    sealed record ImportPick(string? Source, string? Name);
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
                // Share read, write and delete: the publisher (Lua os.remove + os.rename, or an
                // atomic move) must never be blocked by this 30 Hz reader.
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("state", out var state) || state.GetString() != "ready"
                    || !root.TryGetProperty("mode", out var mode) || mode.GetString() != "main") {
                    Invalidate();
                    var reason = root.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
                    return new(false, 2, reason is "challenge-active" or "scenario-active" or "map-mismatch" or "scenario-mismatch" ? reason : "unavailable");
                }
                var protocol = root.TryGetProperty("protocol", out var capability) && capability.TryGetInt32(out var version) && version >= 3 ? Math.Min(version, 6) : 2;
                cached = new(true, protocol, "unavailable");
                validUntil = stamp.AddSeconds(3);
                return cached;
            }
            // Opening a file that Lua is deleting/replacing (os.remove + os.rename)
            // reports access denied on Windows: that is a publication gap, not a
            // renderer failure, so it is transient exactly like a sharing violation.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Transient(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return Invalidate(); }
        }
    }
}
