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

sealed record DiscordPresenceInput(LiveOverlaySnapshot Live, bool Replay, DiscordSession Session, string? HubHandle, DateTimeOffset Now);

sealed record DiscordButton(string Label, string Url);

sealed record DiscordActivity(string Phase, string Details, string State, long? Start, long? End, IReadOnlyList<DiscordButton> Buttons, string? Scenario)
{
    public const string LargeImage = "https://s.crun.zip/aimmod.png";
    public const string SmallImage = "https://cdn.discordapp.com/app-icons/1162428887066742904/798981b85db0ce80a8168c1184ef92a2.png?size=1280";
    public const string SteamAppId = "824270";
    public JsonObject ToJson(bool scenarioButton = true)
    {
        var activity = new JsonObject
        {
            ["details"] = Details,
            ["state"] = State,
            ["assets"] = new JsonObject { ["large_image"] = LargeImage, ["large_text"] = "AimMod for KovaaK's", ["small_image"] = SmallImage, ["small_text"] = "KovaaK's" },
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
    public bool SameContent(DiscordActivity? previous) => previous is not null && !Structural(previous) && previous.State == State;
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

    public static DiscordActivity Build(DiscordPresenceInput input, DiscordSettingsValue settings)
    {
        var live = input.Live; var session = input.Session;
        var now = input.Now.ToUnixTimeSeconds();
        var buttons = new List<DiscordButton>();
        string phase, details, state; long? start = null, end = null; string? scenario = null;
        if (input.Replay)
        {
            phase = "replay"; details = "Watching a replay";
            state = session.Runs > 0 ? Runs(session.Runs) : "Reviewing a run in AimMod";
        }
        else if (live.Active && !string.IsNullOrWhiteSpace(live.Scenario))
        {
            scenario = live.Scenario;
            details = Clean(live.Scenario, "In a challenge");
            var parts = new List<string>();
            if (live.Paused) parts.Add("Paused");
            if (settings.ShowScore && live.Score is double score) parts.Add("Score " + Number(score));
            if (settings.ShowScore && !live.Paused && live.Accuracy is double accuracy) parts.Add(accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "% acc");
            if (settings.ShowPersonalBest && live.PersonalBest is double best && best > 0)
                parts.Add(!live.Paused && live.ProjectedDelta is double delta && live.OpponentSource == "personal-best" ? "Pace " + Signed(delta) + " vs PB" : "PB " + Number(best));
            if (parts.Count == 0) parts.Add(live.Paused ? "Paused" : "In a challenge");
            state = string.Join(" · ", parts);
            if (live.Paused) phase = "paused";
            else
            {
                phase = "playing";
                // Remaining time counts down; without it the elapsed time counts up.
                if (live.RemainingSeconds is double remaining && remaining > 0) end = now + (long)Math.Round(remaining);
                else if (live.Seconds is double elapsed && elapsed >= 0) start = now - (long)Math.Round(elapsed);
            }
        }
        else if (session.Last is Run last)
        {
            phase = "results"; scenario = last.Scenario;
            details = Clean(last.Scenario, "Between runs");
            var parts = new List<string>();
            if (settings.ShowScore) parts.Add("Last " + Number(last.Score));
            if (settings.ShowPersonalBest && session.LastIsPersonalBest) parts.Add("New PB!");
            else if (settings.ShowPersonalBest && settings.ShowScore && session.PersonalBest is double best && best > 0) parts.Add("PB " + Number(best));
            parts.Add(Runs(session.Runs));
            state = string.Join(" · ", parts);
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
        return new(phase, Clean(details, "KovaaK's"), Clean(state, "Training"), start, end, buttons, scenario);
    }
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
    readonly Func<string?> hubHandle;
    readonly Func<DateTimeOffset> clock;
    readonly Func<int> pid;
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
    volatile string status = "starting";
    CancellationTokenSource? stop;
    Task? loop;
    public DiscordPresenceHost(string output, DiscordSettings settings, Func<LiveOverlaySnapshot> live, Func<bool> replay, Func<string?> hubHandle,
        IDiscordPipe? pipe = null, Func<DateTimeOffset>? clock = null, Func<int>? pid = null, TimeSpan? tick = null)
    {
        this.output = output; this.settings = settings; this.live = live; this.replay = replay; this.hubHandle = hubHandle;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.pid = pid ?? GamePid; this.tick = tick ?? TimeSpan.FromSeconds(1);
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { Console.Error.WriteLine($"Discord presence step failed ({ex.GetType().Name})."); }
                try { await Task.Delay(tick, cancel); } catch (OperationCanceledException) { break; }
            }
        });
    }
    // Closing the connection makes Discord drop this application's activity.
    async Task Release(bool withdraw)
    {
        if (client.Connected) await client.Disconnect();
        sent = null;
        if (withdraw && requested) { DiscordHandoff.Withdraw(output); requested = false; requestedAt = DateTimeOffset.MinValue; }
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
        if (!preferences.Enabled) { await Release(withdraw: true); status = "off"; return; }
        if (!requested || now - requestedAt >= RequestRefresh) { DiscordHandoff.Request(output, now); requested = true; requestedAt = now; }
        var game = DiscordHandoff.ReadGame(output, now);
        if (game == DiscordHandoff.GameState.Released) releasedAt = now;
        // An unreadable acknowledgement (the mod replacing the file) keeps a
        // shown presence for up to three seconds instead of flickering it off.
        // An explicit game/off/unavailable answer releases at once.
        var held = game == DiscordHandoff.GameState.Released || (game == DiscordHandoff.GameState.Unknown && client.Connected && now - releasedAt <= TimeSpan.FromSeconds(3));
        if (!held)
        {
            await Release(withdraw: false);
            status = game switch { DiscordHandoff.GameState.Off => "game-off", DiscordHandoff.GameState.Unavailable => "unsupported", _ => "waiting" };
            return;
        }
        if (!client.Connected)
        {
            if (now < nextConnect) { status = "discord-unavailable"; return; }
            if (!await client.Connect(token))
            {
                failures++; nextConnect = now + DiscordBackoff.Delay(failures); status = "discord-unavailable"; return;
            }
            failures = 0; sent = null; rate.Reset();
        }
        var activity = DiscordActivityBuilder.Build(new(live(), replay(), DiscordSession.Summarize(Volatile.Read(ref runs), started), hubHandle(), now), preferences);
        status = "showing";
        if (activity.SameContent(sent)) return;
        var due = activity.Structural(sent) ? now - sentAt >= MinimumSpacing : now - sentAt >= StateRefresh;
        if (!due || !rate.TryTake(now.UtcDateTime)) return;
        var result = await client.SetActivity(pid(), activity.ToJson(scenarioButton), token);
        if (result == DiscordSendResult.Ok) { sent = activity; sentAt = now; return; }
        if (result == DiscordSendResult.Rejected && scenarioButton && activity.Buttons.Any(b => b.Url.StartsWith("steam:", StringComparison.Ordinal)))
        { scenarioButton = false; return; } // Retried next step without the game link.
        if (result == DiscordSendResult.Rejected) { sent = activity; sentAt = now; return; } // Do not resend rejected content.
        failures++; nextConnect = now + DiscordBackoff.Delay(failures); status = "discord-unavailable";
    }
    public object StatusInfo => new { state = status };
    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        await stepGate.WaitAsync();
        try { await Release(withdraw: true); await client.DisposeAsync(); }
        finally { stepGate.Release(); }
        stop?.Dispose();
    }
}
