using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame;

// AimMod Hub live activity ("Live" page and the live card on the profile),
// sent as the linked account while KovaaK's runs. The Hub keeps a heartbeat for
// 90 s; DELETE removes it at once. Payload fields match the companion app's
// (api/internal/store/live_activity.go in aimmod-hub) plus the optional client,
// activity and session fields newer Hubs read.
//
// Health mapping for the in-game mod:
//   runtimeLoaded   = AimModCore is loaded in the game (core-scene.json is fresh)
//   bridgeConnected = its live game data reaches this service (live-overlay.json is fresh)
//   steamConnected  = the AimModSteam bridge used by multiplayer; omitted when there is none
sealed record HubLivePayload(
    int GameStateCode, string GameState, bool Paused,
    string? ScenarioName, string? ScenarioType, string? ScenarioSubtype,
    double? Score, double? ScorePerMinute, double? AccuracyPct, uint? Kills,
    double? ElapsedSecs, double? TimeRemainingSecs, double? QueueTimeRemainingSecs,
    bool RuntimeLoaded, bool BridgeConnected,
    string Client, string ClientVersion, string Activity,
    uint? SessionRunCount, double? SessionElapsedSecs, bool? SteamConnected)
{
    public const string ClientName = "in-game";
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    // A change here is sent promptly; other value changes wait for the next refresh.
    public string Structure => string.Join('|', Activity, GameState, Paused, ScenarioName, RuntimeLoaded, BridgeConnected, SteamConnected);
}

/// <summary>What the game, the AimMod service and multiplayer show right now.</summary>
sealed record HubLiveSnapshot(bool GameRunning, GameScene? Scene, LiveOverlaySnapshot Live, bool Replay, string? ReplayScenario, DiscordLobbyInfo? Lobby, bool? SteamConnected);

static class HubLiveActivityBuilder
{
    internal static readonly string Version = ClientVersionOf(typeof(HubLiveActivityBuilder).Assembly);
    static string ClientVersionOf(Assembly assembly)
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "";
        var plus = value.IndexOf('+'); if (plus >= 0) value = value[..plus];
        return value.Length is > 0 and <= 40 ? value : "";
    }
    static string? Name(string? scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario)) return null;
        var value = DiscordActivityBuilder.Clean(scenario, "");
        // Generated multiplayer arenas are never shown under their file name.
        if (value.Length == 0 || value.StartsWith(Multiplayer.MatchScenario.Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return value.Length <= 160 ? value : value[..160];
    }
    static double? Finite(double? value) => value is double v && double.IsFinite(v) ? Math.Round(v, 3) : null;
    static uint? Count(double? value) => value is double v && double.IsFinite(v) && v >= 0 && v <= uint.MaxValue ? (uint)Math.Round(v) : null;

    /// <summary>The heartbeat to publish, or null when KovaaK's is not running (the heartbeat is removed).</summary>
    public static HubLivePayload? Build(HubLiveSnapshot input, DiscordSession session, DateTimeOffset now, string? version = null)
    {
        var scene = input.Scene;
        var runtime = scene is not null;
        if (!input.GameRunning && !runtime) return null;
        var live = input.Live;
        var replay = input.Replay || live.Replay;
        var bridge = runtime && (live.Available || replay);
        string activity, state; string? scenario = null; var paused = false;
        double? score = null, spm = null, accuracy = null, elapsed = null, remaining = null; uint? kills = null;
        void Run()
        {
            if (!live.Active) return;
            score = Finite(live.Score); spm = Finite(live.ScorePerMinute); accuracy = Finite(live.Accuracy);
            kills = Count(live.Kills); elapsed = Finite(live.Seconds); remaining = Finite(live.RemainingSeconds);
        }
        if (!runtime) { activity = "menu"; state = "Starting AimMod"; }
        else if (replay) { activity = "replay"; state = "Watching a replay"; scenario = Name(input.ReplayScenario); }
        else if (input.Lobby is { } lobby)
        {
            scenario = Name(lobby.Scenario) ?? Name(live.Scenario);
            if (lobby.State == "match")
            {
                activity = "match";
                var round = lobby.Round is int r ? lobby.TotalRounds is int total ? $" · Round {r}/{total}" : $" · Round {r}" : "";
                state = lobby.Mode + round;
                paused = live.Active && live.Paused;
                Run();
            }
            else if (lobby.State == "results") { activity = "results"; state = lobby.Mode + " · Match over"; }
            else { activity = "lobby"; state = $"In a lobby · {Math.Max(1, lobby.Players)}/{Math.Max(1, lobby.MaxPlayers)} · {lobby.Mode}"; }
        }
        else if (live.Active)
        {
            activity = "challenge"; scenario = Name(live.Scenario) ?? Name(scene!.Scenario); paused = live.Paused;
            state = paused ? "Paused" : "In a challenge";
            Run();
        }
        else if (scene!.InChallenge) { activity = "challenge"; scenario = Name(scene.Scenario); paused = scene.Paused; state = paused ? "Paused" : "Starting a challenge"; }
        else if (Name(scene.Scenario) is { } loaded) { activity = "scenario"; scenario = loaded; state = scene.Loading ? "Loading a scenario" : "In a scenario"; }
        else { activity = "menu"; state = scene.Loading ? "Loading" : "In the menus"; }
        if (runtime && !bridge) state = "Reconnecting";
        var sessionSecs = (now - session.Started).TotalSeconds;
        return new(0, state, paused, scenario, null, null, score, spm, accuracy, kills, elapsed, remaining, null,
            runtime, bridge, HubLivePayload.ClientName, version ?? Version, activity,
            (uint)Math.Max(0, session.Runs), sessionSecs >= 0 ? Math.Round(sessionSecs) : null, input.SteamConnected);
    }
}

/// <summary>
/// Publishes the heartbeat: promptly on a state change (at most every 2 s),
/// on value changes at most every 5 s, and otherwise every 30 s. Failures back
/// off from 5 s to 5 min; Retry-After is honored; a refused upload token stops
/// publishing until a different account is linked.
/// </summary>
sealed class HubLivePublisher : IAsyncDisposable
{
    internal static readonly TimeSpan StructuralSpacing = TimeSpan.FromSeconds(2), ValueSpacing = TimeSpan.FromSeconds(5), Heartbeat = TimeSpan.FromSeconds(30);
    const string Path = "/activity/live";
    readonly Hub hub;
    readonly HubSharingSettings settings;
    readonly Func<HubLiveSnapshot> read;
    readonly Func<DateTimeOffset> clock;
    readonly TimeSpan tick;
    readonly DateTimeOffset started;
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<Run> runs = [];
    HubLivePayload? sent; string? sentJson; DateTimeOffset sentAt, nextAttempt;
    string publishedFor = "", rejected = "";
    int failures, clearAttempts;
    volatile string status = "starting";
    CancellationTokenSource? stop; Task? loop;

    public HubLivePublisher(Hub hub, HubSharingSettings settings, Func<HubLiveSnapshot> read, Func<DateTimeOffset>? clock = null, TimeSpan? tick = null)
    {
        this.hub = hub; this.settings = settings; this.read = read;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.tick = tick ?? TimeSpan.FromSeconds(1);
        started = this.clock();
    }
    /// <summary>off, not-linked, waiting (game closed), live, unavailable, rejected or starting.</summary>
    public string Status => status;
    public HubLivePayload? Shown => sent;
    public void UpdateHistory(IReadOnlyList<Run> history) => Volatile.Write(ref runs, history);
    internal static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(300, 5 << Math.Clamp(failures - 1, 0, 6)));

    public void Start(CancellationToken token)
    {
        if (loop is not null) return;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancel = stop.Token;
        loop = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try { await Step(cancel); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                { Console.Error.WriteLine("Hub live activity step failed (" + ex.GetType().Name + ")."); }
                try { await Task.Delay(tick, cancel); } catch (OperationCanceledException) { break; }
            }
        });
    }

    internal async Task Step(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await StepCore(token); }
        finally { gate.Release(); }
    }

    void Failed(HubSendResult result, DateTimeOffset now)
    {
        failures = Math.Min(failures + 1, 16);
        var delay = result.Status is 429 or 503 && result.RetryAfter is TimeSpan wait
            ? TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 5, 3600)) : Backoff(failures);
        nextAttempt = now + delay;
        status = "unavailable";
    }

    // Removes this account's heartbeat. A failed removal is retried a few
    // times; after that the Hub's 90 s expiry removes it anyway.
    async Task Clear(DateTimeOffset now, CancellationToken token)
    {
        if (sent is null || now < nextAttempt) return;
        if (publishedFor != hub.AccountFingerprint) { Forget(); return; } // Unlinked or another account: the old token is gone.
        var result = await hub.SendAsAccount(HttpMethod.Delete, Path, null, token);
        if (result is { Ok: true } || result is null || result.Value.Unauthorized || ++clearAttempts >= 3) { Forget(); failures = 0; nextAttempt = default; return; }
        Failed(result.Value, now);
    }
    void Forget() { sent = null; sentJson = null; publishedFor = ""; clearAttempts = 0; }

    async Task StepCore(CancellationToken token)
    {
        var now = clock();
        var account = hub.AccountFingerprint;
        if (account.Length == 0) { Forget(); status = "not-linked"; return; }
        if (!settings.Current.LiveActivity) { await Clear(now, token); status = "off"; return; }
        if (account == rejected) { status = "rejected"; return; }
        if (sent is not null && publishedFor != account) Forget();
        var payload = HubLiveActivityBuilder.Build(read(), DiscordSession.Summarize(Volatile.Read(ref runs), started), now);
        if (payload is null) { await Clear(now, token); if (sent is null) status = "waiting"; return; }
        if (now < nextAttempt) return;
        var json = payload.ToJson();
        // The session clock alone never makes a heartbeat due; the Hub page counts it up.
        var values = (payload with { SessionElapsedSecs = null }).ToJson();
        var elapsed = now - sentAt;
        var due = sent is null || elapsed >= Heartbeat
            || payload.Structure != sent.Structure && elapsed >= StructuralSpacing
            || values != sentJson && elapsed >= ValueSpacing;
        if (!due) return;
        var result = await hub.SendAsAccount(HttpMethod.Post, Path, json, token);
        if (result is not { } r) { Forget(); status = "not-linked"; return; }
        if (r.Ok) { sent = payload; sentJson = values; sentAt = now; publishedFor = account; failures = 0; status = "live"; return; }
        if (r.Unauthorized) { rejected = account; Forget(); status = "rejected"; Console.Error.WriteLine("Hub live activity: the linked account was refused. Link the account again."); return; }
        Failed(r, now);
    }

    public object StatusInfo => new { state = status };

    /// <summary>Removes the heartbeat now (before an unlink); publishing resumes on the next step if still wanted.</summary>
    public async Task ClearNow()
    {
        await gate.WaitAsync();
        try
        {
            if (sent is null || publishedFor != hub.AccountFingerprint) { Forget(); return; }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await hub.SendAsAccount(HttpMethod.Delete, Path, null, limit.Token); } catch (OperationCanceledException) { }
            Forget();
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        await gate.WaitAsync();
        try
        {
            // Leave the Live page at once instead of after the 90 s expiry.
            if (sent is not null && publishedFor == hub.AccountFingerprint)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await hub.SendAsAccount(HttpMethod.Delete, Path, null, limit.Token); } catch (OperationCanceledException) { }
            }
            Forget();
        }
        finally { gate.Release(); }
        stop?.Dispose();
    }
}

/// <summary>Whether KovaaK's is running, checked at most every 5 s.</summary>
sealed class CachedGameProcess(Func<bool>? probe = null)
{
    readonly Func<bool> probe = probe ?? (() =>
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("FPSAimTrainer-Win64-Shipping");
        try { return processes.Length > 0; } finally { foreach (var p in processes) p.Dispose(); }
    });
    long next; bool value;
    public bool Running()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now >= Interlocked.Read(ref next)) { value = probe(); Interlocked.Exchange(ref next, now + 5 * System.Diagnostics.Stopwatch.Frequency); }
        return value;
    }
}
