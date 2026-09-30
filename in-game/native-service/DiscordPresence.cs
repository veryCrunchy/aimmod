using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AimMod.InGame;

// What this game session has produced so far, from the native run journal.
sealed record DiscordSession(DateTimeOffset Started, int Runs, Run? Last, bool LastIsPersonalBest, double? PersonalBest)
{
    static bool Earlier(Run run, DateTimeOffset than) =>
        !DateTimeOffset.TryParse(run.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at < than.AddSeconds(-5);
    // runs: the merged history. Only journal runs completed after the session
    // started count as this session; every source counts toward the PB.
    public static DiscordSession Summarize(IEnumerable<Run> runs, DateTimeOffset started)
    {
        var all = runs.Where(r => !string.IsNullOrEmpty(r.Scenario) && double.IsFinite(r.Score)).ToArray();
        var session = new List<(Run Run, DateTimeOffset At)>();
        foreach (var run in all)
            if (run.Id.StartsWith("native:", StringComparison.Ordinal)
                && DateTimeOffset.TryParse(run.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at >= started)
                session.Add((run, at));
        if (session.Count == 0) return new(started, 0, null, false, null);
        var (last, lastAt) = session.MaxBy(s => s.At);
        var same = all.Where(r => r.Scenario.Equals(last.Scenario, StringComparison.OrdinalIgnoreCase)).ToArray();
        var previous = same.Where(r => r.Id != last.Id && Earlier(r, lastAt)).Select(r => (double?)r.Score).Max();
        return new(started, session.Count, last, previous is double p && last.Score > p, same.Max(r => r.Score));
    }
}

// Page: the AimMod workspace page while the panel is open (null when closed).
// ReplayScenario: scenario of the replay being watched, when known.
sealed record DiscordPresenceInput(LiveOverlaySnapshot Live, bool Replay, DiscordSession Session, string? HubHandle, DateTimeOffset Now, string? ReplayScenario = null, string? Page = null);

sealed record DiscordButton(string Label, string Url);

sealed record DiscordActivity(string Phase, string Details, string State, long? Start, long? End, IReadOnlyList<DiscordButton> Buttons, string? Scenario, string LargeText = DiscordActivity.DefaultLargeText)
{
    public const string DefaultLargeText = "AimMod for KovaaK's";
    // The AimMod application has no uploaded art assets, so images are direct
    // URLs. No small image: the previous app-icon URL no longer resolves and
    // Discord draws a missing small image as a "?" badge.
    public const string LargeImage = "https://s.crun.zip/aimmod.png";
    public const string SteamAppId = "824270";
    public JsonObject ToJson(bool scenarioButton = true)
    {
        var activity = new JsonObject
        {
            ["details"] = Details,
            ["state"] = State,
            ["assets"] = new JsonObject { ["large_image"] = LargeImage, ["large_text"] = LargeText },
        };
        if (Start is not null || End is not null)
        {
            var timestamps = new JsonObject();
            if (Start is long start) timestamps["start"] = start;
            if (End is long end) timestamps["end"] = end;
            activity["timestamps"] = timestamps;
        }
        var buttons = new JsonArray();
        foreach (var button in Buttons.Where(b => scenarioButton || !b.Url.StartsWith("steam:", StringComparison.Ordinal)).Take(2))
            buttons.Add(new JsonObject { ["label"] = button.Label, ["url"] = button.Url });
        if (buttons.Count > 0) activity["buttons"] = buttons;
        return activity;
    }
    // A structural change (phase, scenario, headline, buttons, a moved timer)
    // is sent promptly; a changed state line alone waits for the refresh interval.
    public bool Structural(DiscordActivity? previous) =>
        previous is null || previous.Phase != Phase || previous.Details != Details || !previous.Buttons.SequenceEqual(Buttons)
        || Moved(previous.Start, Start) || Moved(previous.End, End);
    static bool Moved(long? a, long? b) => a.HasValue != b.HasValue || (a is long x && b is long y && Math.Abs(x - y) > 3);
    public bool SameContent(DiscordActivity? previous) => previous is not null && !Structural(previous) && previous.State == State && previous.LargeText == LargeText;
}

static class DiscordActivityBuilder
{
    const int TextLimit = 128;
    internal static string Clean(string? text, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var ch in (text ?? "").Normalize(NormalizationForm.FormC)) builder.Append(char.IsControl(ch) ? ' ' : ch);
        var value = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        if (value.Length > TextLimit)
        {
            var cut = TextLimit - 1;
            if (char.IsHighSurrogate(value[cut - 1])) cut--;
            value = value[..cut].TrimEnd() + "…";
        }
        // Discord rejects details/state shorter than two characters.
        return value.Length >= 2 ? value : fallback;
    }
    internal static string Number(double value) =>
        Math.Abs(value) >= 1000 ? value.ToString("#,0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
    static string Signed(double value) => (value >= 0 ? "+" : "−") + Number(Math.Abs(value));
    static string Runs(int count) => count == 1 ? "1 run this session" : $"{count} runs this session";
    internal static string HubProfile(string handle) => "https://aimmod.app/profiles/" + Uri.EscapeDataString(handle);
    internal static string PlayScenario(string scenario) =>
        $"steam://run/{DiscordActivity.SteamAppId}//?action=jump-to-scenario&name={Uri.EscapeDataString(scenario)}&mode=challenge";
    // Workspace page keys as used by the in-game UI (index.html titles).
    internal static string PageLabel(string page) => page switch
    {
        "overview" => "In AimMod · Overview",
        "trends" or "statistics" => "In AimMod · Statistics",
        "history" => "In AimMod · History",
        "run-details" => "Reviewing a run",
        "coaching" => "In AimMod · Coaching",
        "mechanics" => "In AimMod · Mechanics",
        "replays" => "Browsing replays",
        "benchmarks" => "In AimMod · Benchmarks",
        "leaderboard" => "In AimMod · Leaderboard",
        "account" => "In AimMod · Account",
        "overlays" => "In AimMod · Overlays",
        "settings" => "In AimMod · Settings",
        _ => "In AimMod",
    };
    // Hover text on the large image while a run is open: counters that do not
    // fit the state line.
    static string RunDetail(LiveOverlaySnapshot live, DiscordSettingsValue settings)
    {
        var parts = new List<string>();
        if (live.Kills is double kills) parts.Add(Number(kills) + (kills == 1 ? " kill" : " kills"));
        if (settings.ShowScore && live.ScorePerMinute is double spm) parts.Add(Number(spm) + " SPM");
        if (settings.ShowScore && live.Hits is double hits && live.Shots is double shots && shots > 0) parts.Add($"{Number(hits)}/{Number(shots)} hits");
        if (settings.ShowPersonalBest && live.ProjectedScore is double projected && live.OpponentSource == "personal-best") parts.Add("On pace for " + Number(projected));
        return parts.Count == 0 ? DiscordActivity.DefaultLargeText : Clean(string.Join(" · ", parts), DiscordActivity.DefaultLargeText);
    }
    static string? RunLine(LiveOverlaySnapshot live, DiscordSettingsValue settings, bool paused)
    {
        var parts = new List<string>();
        if (settings.ShowScore && live.Score is double score) parts.Add("Score " + Number(score));
        if (settings.ShowScore && live.Accuracy is double accuracy) parts.Add(accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "% acc");
        if (settings.ShowPersonalBest && live.PersonalBest is double best && best > 0)
            parts.Add(!paused && live.ProjectedDelta is double delta && live.OpponentSource == "personal-best" ? "Pace " + Signed(delta) + " vs PB" : "PB " + Number(best));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
    static string SessionLine(DiscordSession session, DiscordSettingsValue settings)
    {
        if (session.Last is not Run last) return "Ready to train";
        var parts = new List<string>();
        if (settings.ShowScore) parts.Add("Last " + Number(last.Score));
        if (settings.ShowPersonalBest && session.LastIsPersonalBest) parts.Add("New PB!");
        else if (settings.ShowPersonalBest && settings.ShowScore && session.PersonalBest is double best && best > 0) parts.Add("PB " + Number(best));
        parts.Add(Runs(session.Runs));
        return string.Join(" · ", parts);
    }

    public static DiscordActivity Build(DiscordPresenceInput input, DiscordSettingsValue settings)
    {
        var live = input.Live; var session = input.Session;
        var now = input.Now.ToUnixTimeSeconds();
        var buttons = new List<DiscordButton>();
        string phase, details, state, largeText = DiscordActivity.DefaultLargeText; long? start = null, end = null; string? scenario = null;
        var inRun = live.Active && !string.IsNullOrWhiteSpace(live.Scenario);
        if (input.Replay)
        {
            phase = "replay";
            var replayScenario = string.IsNullOrWhiteSpace(input.ReplayScenario) ? null : input.ReplayScenario;
            details = replayScenario is null ? "Watching a replay" : Clean(replayScenario, "Watching a replay");
            state = replayScenario is null ? "In AimMod" : "Watching a replay";
            if (session.Runs > 0) state += " · " + Runs(session.Runs);
        }
        else if (inRun && !live.Paused)
        {
            phase = "playing"; scenario = live.Scenario;
            details = Clean(live.Scenario, "In a challenge");
            state = RunLine(live, settings, paused: false) ?? "In a challenge";
            largeText = RunDetail(live, settings);
            // Remaining time counts down; without it the elapsed time counts up.
            if (live.RemainingSeconds is double remaining && remaining > 0) end = now + (long)Math.Round(remaining);
            else if (live.Seconds is double elapsed && elapsed >= 0) start = now - (long)Math.Round(elapsed);
        }
        else if (input.Page is string page)
        {
            // The AimMod panel lives in the pause menu, so a paused run is
            // common here: name the page and keep the run's standing.
            phase = "workspace:" + page;
            details = PageLabel(page);
            start = session.Started.ToUnixTimeSeconds();
            if (inRun)
            {
                scenario = live.Scenario;
                var run = RunLine(live, settings, paused: true);
                state = "Paused · " + Clean(live.Scenario, "a challenge") + (run is null ? "" : " · " + run);
                largeText = RunDetail(live, settings);
            }
            else state = SessionLine(session, settings);
        }
        else if (inRun)
        {
            phase = "paused"; scenario = live.Scenario;
            details = Clean(live.Scenario, "In a challenge");
            var run = RunLine(live, settings, paused: true);
            state = run is null ? "Paused" : "Paused · " + run;
            largeText = RunDetail(live, settings);
        }
        else if (session.Last is Run last)
        {
            phase = "results"; scenario = last.Scenario;
            details = Clean(last.Scenario, "Between runs");
            state = SessionLine(session, settings);
            start = session.Started.ToUnixTimeSeconds();
        }
        else
        {
            phase = "menu"; details = "In the menus"; state = "Choosing a scenario";
            start = session.Started.ToUnixTimeSeconds();
        }
        if (scenario is not null && scenario.Length <= 256) buttons.Add(new("Play this scenario", PlayScenario(scenario)));
        if (settings.ShowHubButton && !string.IsNullOrWhiteSpace(input.HubHandle) && input.HubHandle.Length <= 64)
            buttons.Add(new("AimMod Hub profile", HubProfile(input.HubHandle)));
        return new(phase, Clean(details, "KovaaK's"), Clean(state, "Training"), start, end, buttons, scenario, largeText);
    }
}

// The workspace page the in-game UI reports while the AimMod panel is shown.
// The UI re-reports every few seconds; a report older than 10 s means closed.
sealed class DiscordWorkspaceView
{
    static readonly System.Text.RegularExpressions.Regex Key = new("^[a-z][a-z-]{0,31}$");
    readonly object gate = new();
    string? page; DateTimeOffset at;
    public static bool ValidPage(string? value) => value is not null && Key.IsMatch(value);
    public void Report(string page, bool visible, DateTimeOffset now) { lock (gate) { this.page = visible ? page : null; at = now; } }
    public string? Current(DateTimeOffset now) { lock (gate) return page is not null && now - at <= TimeSpan.FromSeconds(10) && at - now <= TimeSpan.FromSeconds(2) ? page : null; }
}

// File handoff with the in-game mod (DiscordPresence.lua), which owns KovaaK's
// own presence toggle. The worker publishes only while the game has
// acknowledged that its own presence is off, so two presences never show.
static class DiscordHandoff
{
    public const string RequestFile = "discord-takeover.tsv", GameFile = "discord-game.tsv";
    const string RequestHeader = "AIMMOD_DISCORD_TAKEOVER_1", GameHeader = "AIMMOD_DISCORD_GAME_1";
    public enum GameState { Unknown, Released, Game, Off, Unavailable }
    public static string RequestText(DateTimeOffset now) => $"{RequestHeader}\t{now.ToUnixTimeSeconds()}\n";
    public static void Request(string output, DateTimeOffset now) => AtomicFile.WriteText(Path.Combine(output, RequestFile), RequestText(now));
    public static void Withdraw(string output)
    {
        try { File.Delete(Path.Combine(output, RequestFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // The acknowledgement is refreshed every second; three seconds without it
    // (game closed, mod unloaded) means the handoff is not held.
    public static GameState ParseGame(string text, DateTimeOffset now)
    {
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!line.EndsWith('\n')) return GameState.Unknown;
        var fields = line[..^1].Split('\t');
        if (fields.Length != 3 || fields[0] != GameHeader || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)) return GameState.Unknown;
        var age = now.ToUnixTimeSeconds() - stamp;
        if (age is > 3 or < -2) return GameState.Unknown;
        return fields[1] switch { "released" => GameState.Released, "game" => GameState.Game, "off" => GameState.Off, "unavailable" => GameState.Unavailable, _ => GameState.Unknown };
    }
    public static GameState ReadGame(string output, DateTimeOffset now)
    {
        var path = Path.Combine(output, GameFile);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256) return GameState.Unknown;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return ParseGame(reader.ReadToEnd(), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return GameState.Unknown; }
    }
}

sealed class DiscordPresenceHost : IAsyncDisposable
{
    public const string ClientId = "1162428887066742904";
    static readonly TimeSpan RequestRefresh = TimeSpan.FromSeconds(2), StateRefresh = TimeSpan.FromSeconds(15), MinimumSpacing = TimeSpan.FromSeconds(1);
    readonly string output;
    readonly DiscordSettings settings;
    readonly Func<LiveOverlaySnapshot> live;
    readonly Func<bool> replay;
    readonly Func<string?> replayScenario, page;
    readonly Func<string?> hubHandle;
    readonly Func<DateTimeOffset> clock;
    readonly Func<int> pid;
    readonly Action<string> log;
    readonly TimeSpan tick;
    readonly DiscordIpcClient client;
    readonly DiscordRateLimit rate = new();
    readonly DateTimeOffset started;
    readonly SemaphoreSlim stepGate = new(1, 1);
    IReadOnlyList<Run> runs = [];
    DiscordActivity? sent;
    DateTimeOffset sentAt, requestedAt = DateTimeOffset.MinValue, nextConnect = DateTimeOffset.MinValue, releasedAt = DateTimeOffset.MinValue;
    int failures;
    // requested starts true so a disabled first step also clears a request
    // file left behind by a previous worker that did not shut down cleanly.
    bool requested = true, scenarioButton = true;
    string? loggedStatus;
    volatile string status = "starting";
    CancellationTokenSource? stop;
    Task? loop;
    public DiscordPresenceHost(string output, DiscordSettings settings, Func<LiveOverlaySnapshot> live, Func<bool> replay, Func<string?> hubHandle,
        IDiscordPipe? pipe = null, Func<DateTimeOffset>? clock = null, Func<int>? pid = null, TimeSpan? tick = null,
        Func<string?>? replayScenario = null, Func<string?>? page = null, Action<string>? log = null)
    {
        this.output = output; this.settings = settings; this.live = live; this.replay = replay; this.hubHandle = hubHandle;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.pid = pid ?? GamePid; this.tick = tick ?? TimeSpan.FromSeconds(1);
        this.replayScenario = replayScenario ?? (() => null); this.page = page ?? (() => null);
        this.log = log ?? (line => Console.WriteLine("[Discord] " + line));
        client = new DiscordIpcClient(ClientId, pipe ?? new DiscordNamedPipe());
        started = this.clock();
    }
    public string Status => status;
    public DiscordActivity? Shown => sent;
    public void UpdateHistory(IReadOnlyList<Run> history) => Volatile.Write(ref runs, history);
    // Discord ties the activity to this process id; the game's pid lets it
    // merge the activity with KovaaK's detected-game entry.
    static int GamePid()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("FPSAimTrainer-Win64-Shipping");
        try { return processes.Length > 0 ? processes[0].Id : Environment.ProcessId; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    // One log line per status change, so a steady state does not fill the log.
    void SetStatus(string value, string? detail = null)
    {
        status = value;
        var line = detail is null ? value : value + ": " + detail;
        if (line != loggedStatus) { loggedStatus = line; log("status " + line); }
    }
    static string Quote(string text) => "\"" + text.Replace("\"", "'") + "\"";
    public void Start(CancellationToken token)
    {
        if (loop is not null) return;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancel = stop.Token;
        log($"presence host started (application {ClientId})");
        loop = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try { await Step(cancel); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { log($"step failed ({ex.GetType().Name})"); }
                try { await Task.Delay(tick, cancel); } catch (OperationCanceledException) { break; }
            }
        });
    }
    // Closing the connection makes Discord drop this application's activity.
    async Task Release(bool withdraw, string reason)
    {
        if (client.Connected) { await client.Disconnect(); log("disconnected from Discord (" + reason + ")"); }
        sent = null;
        if (withdraw && requested) { DiscordHandoff.Withdraw(output); requested = false; requestedAt = DateTimeOffset.MinValue; log("handoff request withdrawn"); }
    }
    internal async Task Step(CancellationToken token)
    {
        await stepGate.WaitAsync(token);
        try { await StepCore(token); }
        finally { stepGate.Release(); }
    }
    async Task StepCore(CancellationToken token)
    {
        var now = clock();
        var preferences = settings.Current;
        if (!preferences.Enabled) { await Release(withdraw: true, "presence turned off"); SetStatus("off"); return; }
        if (!requested || now - requestedAt >= RequestRefresh) { DiscordHandoff.Request(output, now); requested = true; requestedAt = now; }
        var game = DiscordHandoff.ReadGame(output, now);
        if (game == DiscordHandoff.GameState.Released) releasedAt = now;
        // An unreadable acknowledgement (the mod replacing the file) keeps a
        // shown presence for up to three seconds instead of flickering it off.
        // An explicit game/off/unavailable answer releases at once.
        var held = game == DiscordHandoff.GameState.Released || (game == DiscordHandoff.GameState.Unknown && client.Connected && now - releasedAt <= TimeSpan.FromSeconds(3));
        if (!held)
        {
            await Release(withdraw: false, "game reports " + game);
            SetStatus(game switch { DiscordHandoff.GameState.Off => "game-off", DiscordHandoff.GameState.Unavailable => "unsupported", _ => "waiting" }, "game handoff " + game);
            return;
        }
        if (!client.Connected)
        {
            if (now < nextConnect) return;
            log("connecting to Discord");
            if (!await client.Connect(token))
            {
                failures++; var delay = DiscordBackoff.Delay(failures); nextConnect = now + delay;
                log($"connect failed: {client.LastError ?? "unknown"}; retry in {delay.TotalSeconds:0} s");
                SetStatus("discord-unavailable", client.LastError); return;
            }
            log("connected: handshake READY (user redacted)");
            failures = 0; sent = null; rate.Reset();
        }
        var activity = DiscordActivityBuilder.Build(new(live(), replay(), DiscordSession.Summarize(Volatile.Read(ref runs), started), hubHandle(), now, replayScenario(), page()), preferences);
        SetStatus("showing");
        if (activity.SameContent(sent)) return;
        var due = activity.Structural(sent) ? now - sentAt >= MinimumSpacing : now - sentAt >= StateRefresh;
        if (!due || !rate.TryTake(now.UtcDateTime)) return;
        var result = await client.SetActivity(pid(), activity.ToJson(scenarioButton), token);
        var summary = $"SET_ACTIVITY {activity.Phase} details={Quote(activity.Details)} state={Quote(activity.State)} buttons={activity.Buttons.Count(b => scenarioButton || !b.Url.StartsWith("steam:", StringComparison.Ordinal))}";
        if (result == DiscordSendResult.Ok) { log(summary + ": ok"); sent = activity; sentAt = now; return; }
        if (result == DiscordSendResult.Rejected && scenarioButton && activity.Buttons.Any(b => b.Url.StartsWith("steam:", StringComparison.Ordinal)))
        {
            log(summary + ": rejected (" + client.LastError + "); dropping the play-this-scenario button");
            scenarioButton = false; return; // Retried next step without the game link.
        }
        if (result == DiscordSendResult.Rejected) { log(summary + ": rejected (" + client.LastError + ")"); sent = activity; sentAt = now; return; } // Do not resend rejected content.
        failures++; var retry = DiscordBackoff.Delay(failures); nextConnect = now + retry;
        log(summary + ": failed (" + client.LastError + "); reconnect in " + retry.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        SetStatus("discord-unavailable", client.LastError);
    }
    public object StatusInfo => new { state = status };
    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        await stepGate.WaitAsync();
        try { await Release(withdraw: true, "worker stopping"); await client.DisposeAsync(); }
        finally { stepGate.Release(); }
        stop?.Dispose();
    }
}
