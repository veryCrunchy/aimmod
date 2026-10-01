using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Multiplayer;

// Status: aimmod-lobby (in an AimMod lobby), aimmod (AimMod running), kovaaks (game without AimMod), online.
sealed record FriendEntry(string Id, string Name, string Status, string? Detail, string? Code, bool Joinable);
sealed record RecentMatch(string Id, long EndedAt, string Mode, string Scenario, int? Place, int Players, string? Winner, bool Won, bool Simulated, IReadOnlyList<RecentPlayer> Standings);
sealed record RecentPlayer(string Name, int Place, double? Best, int Wins, int Points, bool Self);
sealed record LocalRun(bool Active, string? Scenario, double? Score, double? Seconds, double? Remaining, int Shots, int Hits, int Kills, string? Attempt);
// How this machine starts its run for the current round.
sealed record RoundPlan(string Key, string Scenario, string Mode, bool Generated, string State, string Message, long? LoadSequence = null, long? StartSequence = null);

// Owns this machine's view of multiplayer. When this machine is the host (or
// every other member is simulated) it runs the LobbyCore authority; otherwise
// it mirrors the host's snapshots and sends commands over the transport.
sealed class MultiplayerService : IDisposable
{
    public const int HistoryLimit = 20;
    readonly object gate = new();
    readonly Func<long> clock;
    readonly IMultiplayerTransport transport;
    readonly ContentLibrary library;
    readonly IGameControl game;
    readonly Func<LocalRun> liveRun;
    readonly Func<IReadOnlyList<Run>> completedRuns;
    readonly Func<string?> accountName;
    readonly string? historyPath, outputFolder;
    readonly MatchScenarioStore? scenarios;
    readonly Dictionary<string, ClockSync> clocks = new();
    readonly List<RecentMatch> recent = [];
    readonly List<IncomingInvite> invites = [];
    readonly Timer? timer;
    LobbyCore? core;                 // authority, when it lives on this machine
    LobbySnapshot? mirror;           // last snapshot from a remote host
    string? hostPeer;                // remote host we are connected or connecting to
    long mirrorAt, helloAt, connectAt, lastBroadcast = -1, broadcastAt, lastPing, seq;
    long? joinPendingSince;          // waiting for the transport to join a Steam lobby
    long? reconnectSince;            // the relay to a still-present host dropped
    string? pendingHeir;
    string selfName = "You";
    (string Kind, string Text, long At)? notice;
    RoundPlan? plan;
    // Content download: the host serves lobby files; this machine downloads what it lacks.
    readonly ContentServer server;
    readonly ContentDownload? download;
    string? downloadKey, workshopItem;
    bool autoDownload, simulatedMissing;
    long manifestAskedAt;
    string? trackedRound, lastContentKey; HashSet<string> knownRuns = new(StringComparer.Ordinal); double lastFrameSeconds = -1; long lastFrameAt; ScoreFrame? lastFrame;
    public MultiplayerSimulation? Simulation { get; }
    public string SelfId => transport.LocalPeer;

    public MultiplayerService(IMultiplayerTransport transport, ContentLibrary library, IGameControl game, Func<LocalRun> liveRun, Func<IReadOnlyList<Run>> completedRuns,
        Func<string?> accountName, string? output, bool simulation, Func<long>? clock = null, bool autoTick = true, int seed = 0)
    {
        this.transport = transport; this.library = library; this.game = game; this.liveRun = liveRun; this.completedRuns = completedRuns; this.accountName = accountName;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        outputFolder = output;
        if (output is not null) { prefsPath = Path.Combine(output, "multiplayer-settings.json"); sessionPath = Path.Combine(output, "multiplayer-session.json"); }
        prefs = MultiplayerPrefs.Load(prefsPath); HotkeyName = prefs.Hotkey;
        LoadSession();
        historyPath = output is null ? null : Path.Combine(output, "multiplayer-matches.json");
        if (output is not null && library.ScenarioFolder is { } folder) scenarios = new MatchScenarioStore(folder, Path.Combine(output, "multiplayer-scenarios.json"));
        LoadHistory();
        server = new ContentServer(library, this.clock);
        swap = new ReplaySwap(output, this.clock);
        if (output is not null && library.Root is { } root) download = new ContentDownload(root, Path.Combine(output, "downloads"), this.clock);
        if (simulation) Simulation = new MultiplayerSimulation(this.clock, library, completedRuns, seed);
        // A failure in one tick must never take the whole service down: log it and keep going.
        if (autoTick) timer = new Timer(_ => { try { Tick(); } catch (Exception ex) { Console.Error.WriteLine("Multiplayer tick failed: " + ex.GetType().Name + ": " + ex.Message); } }, null, 100, 100);
    }

    // The simulation is a developer tool: on in Debug builds, and in Release only with
    // --multiplayer-sim, AIMMOD_MULTIPLAYER_SIM=1 or {"simulation":true} in multiplayer-dev.json.
    public static bool SimulationRequested(string[] args, string output)
    {
#if DEBUG
        const bool debug = true;
#else
        const bool debug = false;
#endif
        if (debug || args.Contains("--multiplayer-sim") || Environment.GetEnvironmentVariable("AIMMOD_MULTIPLAYER_SIM") == "1") return true;
        try
        {
            var path = Path.Combine(output, "multiplayer-dev.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("simulation", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    LobbySnapshot? Current => core is not null ? core.Snapshot() : mirror;
    string LocalName() => LobbyRules.CleanName(transport.LocalName ?? accountName(), "You");

    // ---- UI actions -------------------------------------------------------

    public LobbyResult Act(string action, JsonElement args)
    {
        lock (gate)
        {
            string? Text(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            switch (action)
            {
                case "create":
                    if (Current is not null || hostPeer is not null || joinPendingSince is not null) return LobbyResult.Fail("in-lobby", "Leave your current lobby first.");
                    selfName = LocalName();
                    var mode = Text("mode");
                    var settings = new LobbySettings(Mode: mode is not null && LobbyModes.All.Contains(mode) ? mode : LobbyModes.Race);
                    if (Text("scenario") is { } wanted && library.Scenario(wanted) is { } picked) settings = settings with { Scenario = picked };
                    else if (library.Scenarios.FirstOrDefault() is { } first) settings = settings with { Scenario = library.Scenario(first.Name) };
                    core = new LobbyCore(SelfId, selfName, settings, clock);
                    Reset();
                    ReportContent(force: true);
                    transport.Advertise(core.Snapshot());
                    notice = null;
                    return LobbyResult.Success;
                case "join":
                    if (Current is not null || hostPeer is not null || joinPendingSince is not null) return LobbyResult.Fail("in-lobby", "Leave your current lobby first.");
                    var code = (Text("code") ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
                    if (!LobbyCore.ValidCode(code)) return LobbyResult.Fail("code", "Room codes are six letters and numbers.");
                    return JoinBy(code);
                case "leave":
                    Leave("left");
                    return LobbyResult.Success;
                case "prefs":
                    return SetPrefs(args);
                case "avatar":
                    // Your look is a personal preference, applied to whichever lobby you're in.
                    if (AvatarProfiles.Find(Text("avatar")) is not { } look) return LobbyResult.Fail("invalid", "Unknown look.");
                    SetPrefs(JsonSerializer.SerializeToElement(new { prefs = new { avatar = look.Id } }));
                    return Current is null ? LobbyResult.Success : Command("avatar", args);
                case "rejoin":
                    if (transport.LastLobby is not { } last) return LobbyResult.Fail("none", "There’s no lobby to rejoin.");
                    if (Current is not null || hostPeer is not null || joinPendingSince is not null) return LobbyResult.Fail("in-lobby", "Leave your current lobby first.");
                    return JoinBy(last.Token, invite: true);
                case "spectate":
                    return Spectate(args);
                case "share-clip":
                    return ShareClip(Text("id"), Text("label"));
                case "spectate-stop":
                    if (spectating is not null) { transport.StopSpectate(); spectating = null; }
                    return LobbyResult.Success;
                case "dismiss":
                    notice = null; return LobbyResult.Success;
                case "invite":
                    if (Current is not { } lobby) return LobbyResult.Fail("no-lobby", "Create a lobby first.");
                    return transport.InviteOverlay(lobby) ? LobbyResult.Success : LobbyResult.Fail("invite-unavailable", "Steam invites need the Steam bridge. Share the room code " + lobby.Code + " for now.");
                case "invite-friend":
                    if (Current is not { } inviting) return LobbyResult.Fail("no-lobby", "Create a lobby first.");
                    var friend = Text("friend");
                    if (friend is null || Friends().All(f => f.Id != friend)) return LobbyResult.Fail("invalid", "Choose a friend from the list.");
                    if (Simulation is not null && friend.StartsWith("sim-", StringComparison.Ordinal) && core is not null)
                    { notice = ("info", "Invite sent to " + Friends().First(f => f.Id == friend).Name + ".", clock()); Simulation.InvitedFriend(core, friend); return LobbyResult.Success; }
                    return transport.InviteFriend(friend, inviting) ? LobbyResult.Success : LobbyResult.Fail("invite-unavailable", "Couldn’t send the invite. Try the Steam overlay instead.");
                case "accept-invite" or "decline-invite":
                    var invite = invites.FirstOrDefault(i => i.Id == Text("id"));
                    if (invite is null) return LobbyResult.Fail("invalid", "That invite has expired.");
                    invites.Remove(invite);
                    if (action == "decline-invite") { transport.DismissJoin(); return LobbyResult.Success; }
                    if (!invite.Compatible) { transport.DismissJoin(); return LobbyResult.Fail("version", invite.FromName + " is on a different AimMod version. Both of you need the latest AimMod."); }
                    if (invite.Kind == "request")
                    {
                        if (core is null) return LobbyResult.Fail("no-lobby", "You’re not hosting a lobby.");
                        if (Simulation is not null && invite.Token.StartsWith("sim", StringComparison.Ordinal)) { Simulation.Add(core); return LobbyResult.Success; }
                        return transport.InviteFriend(invite.Token, core.Snapshot()) ? LobbyResult.Success : LobbyResult.Fail("invite-unavailable", "Couldn’t let them in.");
                    }
                    if (Current is not null || hostPeer is not null || joinPendingSince is not null) Leave("left");
                    var accepted = JoinBy(invite.Token, invite: true);
                    if (accepted.Ok) RequestPanel();
                    return accepted;
                case "join-friend":
                    var target = Friends().FirstOrDefault(f => f.Id == Text("friend"));
                    if (target is null || !target.Joinable || target.Code is null) return LobbyResult.Fail("invalid", "That friend isn’t in a lobby you can join.");
                    if (Current is not null || hostPeer is not null || joinPendingSince is not null) return LobbyResult.Fail("in-lobby", "Leave your current lobby first.");
                    return JoinBy(target.Code, invite: true);
                case "cancel-join":
                    Leave("left"); return LobbyResult.Success;
                case "copy-code":
                    if (Current is not { } room) return LobbyResult.Fail("no-lobby", "Create a lobby first.");
                    return WindowsClipboard.SetText(room.Code) ? LobbyResult.Success : LobbyResult.Fail("clipboard", "Couldn’t copy. The room code is " + room.Code + ".");
                case "download" or "download-retry":
                    return StartDownload();
                case "download-cancel":
                    download?.Cancel(); return LobbyResult.Success;
                case "sim":
                    if (Simulation is null) return LobbyResult.Fail("sim-off", "The simulation is off in this build.");
                    var op = Text("op") ?? "";
                    if (op is "invite" or "request" or "launch") { var made = Simulation.Control(null, op, null); TakeSimulatedInvites(); return made; }
                    if (core is null) return LobbyResult.Fail("no-lobby", "Create or join a lobby first.");
                    if (op == "self-missing")
                    {
                        if (core.HostId == SelfId) return LobbyResult.Fail("host", "Join a simulated room first; the host always has the content.");
                        simulatedMissing = true; download?.Reset(); ReportContent(force: true); return LobbyResult.Success;
                    }
                    return Simulation.Control(core, op, Text("member"));
                case "score" or "finish" or "content":
                    return LobbyResult.Fail("invalid", "That comes from your runs.");
                default:
                    return Command(action, args);
            }
        }
    }

    LobbyResult JoinBy(string codeOrToken, bool invite = false)
    {
        selfName = LocalName();
        // Steam invites and friends' lobbies join asynchronously; the host arrives as a Connected event.
        if (invite && transport.BeginJoin(codeOrToken)) { joinPendingSince = clock(); notice = null; return LobbyResult.Success; }
        if (!invite && transport.Resolve(codeOrToken) is { } peer) { Connect(peer); notice = null; return LobbyResult.Success; }
        if (Simulation is not null)
        {
            core = Simulation.HostedLobby(LobbyCore.ValidCode(codeOrToken) ? codeOrToken : LobbyCore.NewCode(), SelfId, selfName);
            Reset();
            ReportContent(force: true);
            notice = null;
            return LobbyResult.Success;
        }
        if (invite) return LobbyResult.Fail("not-found", "Couldn’t join that lobby. The Steam bridge isn’t connected.");
        return LobbyResult.Fail("not-found", transport.Available ? "Room codes work once AimMod Hub rooms are live. Join through a Steam invite or a friend’s lobby for now." : "Joining needs AimMod’s Steam bridge, which isn’t connected yet.");
    }

    IReadOnlyList<FriendEntry> Friends() => Simulation is not null && !transport.Available ? Simulation.Friends(clock()) : transport.Friends();

    // ---- replay swap and clips ---------------------------------------------

    ReplaySwap swap = null!;
    readonly List<(string Match, int Round, string Id, long Since)> pendingReplays = [];
    sealed record ReplayChunkBody(string Match, int Round, string Owner, string Id, string Kind, string? Label, int Size, string Hash, int Offset, byte[] Data);
    static ReplayChunkBody? ReadReplayChunk(JsonElement b)
    {
        try
        {
            return new ReplayChunkBody(b.GetProperty("match").GetString() ?? "", b.GetProperty("round").GetInt32(), b.GetProperty("owner").GetString() ?? "", b.GetProperty("id").GetString() ?? "",
                b.GetProperty("kind").GetString() ?? "", b.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                b.GetProperty("size").GetInt32(), b.GetProperty("hash").GetString() ?? "", b.GetProperty("offset").GetInt32(), Convert.FromBase64String(b.GetProperty("data").GetString() ?? ""));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    // Send this player's round replays once AimModCore has saved them (never a .partial).
    void PumpReplays()
    {
        var now = clock();
        foreach (var p in pendingReplays.ToArray())
        {
            var bytes = swap.ReadOwn(p.Id);
            if (bytes is null) { if (now - p.Since > 60_000) pendingReplays.Remove(p); continue; }
            pendingReplays.Remove(p);
            Distribute(p.Match, p.Round, p.Id, "round", bytes, null);
        }
        swap.Pump(Current?.Match is { Phase: MatchPhases.Live or MatchPhases.Countdown }, (peer, body) => Send(peer, "replay.chunk", body));
    }
    void Distribute(string match, int round, string id, string kind, byte[] bytes, string? label)
    {
        if (core is not null) foreach (var peer in RemotePeers(core.Snapshot())) swap.Offer(peer, match, round, SelfId, id, kind, bytes, label);
        else if (hostPeer is not null) swap.Offer(hostPeer, match, round, SelfId, id, kind, bytes, label);
    }

    LobbyResult ShareClip(string? id, string? label)
    {
        if (Current is not { } lobby) return LobbyResult.Fail("no-lobby", "Join a lobby to share clips.");
        if (!ReplaySwap.ValidId(id) || !System.Text.RegularExpressions.Regex.IsMatch(id!, "-clip[0-9]{1,3}$")) return LobbyResult.Fail("invalid", "Choose one of your clips.");
        if (swap.ReadOwn(id!) is not { } bytes) return LobbyResult.Fail("missing", "That clip isn’t in your replays yet.");
        Distribute(lobby.Match?.Id ?? lobby.Id, lobby.Match?.Round ?? 0, id!, "clip", bytes, label);
        // The host announces its own clip right away; members' clips are announced when they arrive.
        if (core is not null) core.ShareClip(SelfId, id!, label);
        notice = ("info", "Sharing your clip with the lobby…", clock());
        return LobbyResult.Success;
    }

    // Round replays this machine has for a match: its own and the others', for run vs run.
    object? ReplaysView(MatchSnapshot? match)
    {
        if (match is null) return null;
        var rounds = Enumerable.Range(1, match.Round).Select(r => new
        {
            round = r, mine = swap.MineFor(match.Id, r),
            others = swap.Received.Where(x => x.Match == match.Id && x.Round == r && x.Kind == "round" && x.Owner != SelfId)
                .Select(x => new { owner = x.Owner, name = match.Standings.FirstOrDefault(s => s.MemberId == x.Owner)?.Name ?? "Player", id = x.Id }).ToArray(),
        }).Where(r => r.mine is not null || r.others.Length > 0).ToArray();
        return rounds.Length == 0 ? null : rounds;
    }

    // ---- preferences, auto-ready and rejoin -------------------------------

    MultiplayerPrefs prefs = new();
    string? prefsPath, sessionPath;
    string? autoLobby; bool autoContentOk, autoInMatch, autoJoinedDone;
    (string Token, long At)? rejoin;
    bool rejoinTried;
    string? lastSessionJson;

    // A ranked (challenge) run of the player's own, outside any match round.
    bool OwnRankedRun()
    {
        if (!prefs.QuietDuringRanked) return false;
        var live = liveRun();
        return live.Active && Current?.Match is not { Phase: MatchPhases.Countdown or MatchPhases.Live };
    }

    // Ready up on the player's chosen moments only; after they unready, nothing re-readies until the next moment.
    void AutoReady()
    {
        if (Current is not { } lobby || lobby.HostId == SelfId) { autoLobby = null; return; }
        var me = lobby.Members.FirstOrDefault(m => m.Id == SelfId);
        if (me is null || me.Role != MemberRoles.Player) return;
        var (scenario, map, profiles) = LocalContent(lobby.Settings);
        var contentOk = lobby.Settings.Scenario is not null && scenario == ContentStates.Ok && map == ContentStates.Ok && profiles is not (ContentStates.Missing or ContentStates.Mismatch);
        var inMatch = lobby.Match is not null;
        var fresh = autoLobby != lobby.Id;
        if (fresh) { autoLobby = lobby.Id; autoJoinedDone = false; autoContentOk = contentOk; autoInMatch = inMatch; }
        var want = false;
        if (!autoJoinedDone && contentOk && !inMatch) { autoJoinedDone = true; want |= prefs.ReadyOnJoin; }
        if (!fresh && contentOk && !autoContentOk) want |= prefs.ReadyOnContent;
        if (!fresh && autoInMatch && !inMatch && contentOk) want |= prefs.ReadyAfterMatch;
        autoContentOk = contentOk; autoInMatch = inMatch;
        if (want && !me.Ready && !inMatch) Command("ready", JsonSerializer.SerializeToElement(new { ready = true }));
    }

    // Where this machine is, so a crash or restart can rejoin the same lobby and round.
    void SaveSession()
    {
        if (sessionPath is null) return;
        try
        {
            if (Current is { } lobby && transport.LobbyToken is { } token && lobby.HostId != SelfId)
            {
                // Refreshed about once a minute so a stale file from long ago is never rejoined.
                var key = token + "|" + lobby.Id + "|" + clock() / 60_000;
                if (key == lastSessionJson) return;
                lastSessionJson = key;
                AtomicFile.WriteText(sessionPath, JsonSerializer.Serialize(new { token, lobby = lobby.Id, at = clock() }, Protocol.Json));
            }
            else if (Current is null && hostPeer is null && joinPendingSince is null && rejoin is null && File.Exists(sessionPath)) File.Delete(sessionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    void LoadSession()
    {
        if (sessionPath is null || !File.Exists(sessionPath)) return;
        try
        {
            if (new FileInfo(sessionPath).Length > 1024) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 and <= 64 } token
                && root.TryGetProperty("at", out var a) && a.TryGetInt64(out var at) && clock() - at is >= 0 and < 15 * 60_000)
                rejoin = (token, at);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void TryRejoin()
    {
        if (rejoin is null && !rejoinTried && transport.LastLobby is { AgeSeconds: <= 900 } recent) rejoin = (recent.Token, clock());
        if (rejoin is not { } r || rejoinTried || !transport.Available || Current is not null || hostPeer is not null || joinPendingSince is not null) return;
        rejoinTried = true; rejoin = null;
        if (JoinBy(r.Token, invite: true).Ok) notice = ("info", "Rejoining your lobby after the restart…", clock());
    }

    string myAvatar => prefs.Avatar;
    bool? sentHideScenario; string? sentVersionTo;

    // Keep this machine's look, build and presence privacy in step with its preferences.
    void SyncSelf()
    {
        if (transport.Available && sentHideScenario != prefs.HideScenario) { transport.SetPresencePrivacy(prefs.HideScenario); sentHideScenario = prefs.HideScenario; }
        if (Current is not { } lobby || lobby.Members.FirstOrDefault(m => m.Id == SelfId) is not { } me) return;
        if (core is not null) core.SetVersion(SelfId, transport.BridgeVersion);
        if (me.Avatar != myAvatar && sentVersionTo != lobby.Id + "|" + myAvatar)
        {
            sentVersionTo = lobby.Id + "|" + myAvatar;
            Command("avatar", JsonSerializer.SerializeToElement(new { avatar = myAvatar }));
        }
    }

    // Steam remembers the lobby a crash or shutdown left behind (up to 3 h). Recent ones
    // are rejoined automatically; older ones are offered on the home screen.
    object? RejoinOffer() => Current is null && hostPeer is null && joinPendingSince is null && transport.LastLobby is { } last
        ? new { hostName = LobbyRules.CleanName(last.HostName, "your host"), minutes = last.AgeSeconds / 60 } : null;

    string? spectating;
    LobbyResult Spectate(JsonElement args)
    {
        if (Current is not { } lobby) return LobbyResult.Fail("no-lobby", "Join a lobby first.");
        var target = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("member", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var member = lobby.Members.FirstOrDefault(m => m.Id == target && m.Id != SelfId);
        if (member is null) return LobbyResult.Fail("invalid", "Choose another player.");
        if (member.Simulated) return LobbyResult.Fail("simulated", "Simulated players have no camera to follow.");
        if (!transport.StartSpectate(member.Id, 60)) return LobbyResult.Fail("unavailable", "Spectating needs the Steam bridge.");
        spectating = member.Id;
        return LobbyResult.Success;
    }
    // What AimModCore's spectator view needs, for the UI to start it (native-replay spectate).
    object? SpectateView(LobbySnapshot lobby)
    {
        if (spectating is null || lobby.Members.FirstOrDefault(m => m.Id == spectating) is not { } target) { spectating = null; return null; }
        var s = lobby.Settings;
        var scenario = MatchScenario.Needed(s) ? MatchScenario.Name(s) : s.Scenario?.Name;
        var info = s.Scenario is null ? null : library.Scenarios.FirstOrDefault(x => x.Hash == s.Scenario.Hash);
        return new { member = target.Id, name = target.Name, scenario, mapName = s.MapOverride?.Name ?? s.Scenario?.Map, mapScale = info?.MapScale ?? 1, label = target.Name };
    }

    public object PrefsView() => prefs;
    LobbyResult SetPrefs(JsonElement args)
    {
        if (!args.TryGetProperty("prefs", out var patch) || MultiplayerPrefs.Apply(prefs, patch) is not { } next) return LobbyResult.Fail("invalid", "Unknown preferences.");
        prefs = next; HotkeyName = next.Hotkey;
        try { if (prefsPath is not null) prefs.Save(prefsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LobbyResult.Fail("save", "Couldn’t save your preferences."); }
        return LobbyResult.Success;
    }

    // ---- notifications outside the AimMod panel ----------------------------

    public string HotkeyName { get; set; } = "F7";
    (GameNotice Notice, long Until)? flash;
    string? lastNoticeJson;
    public bool HotkeyArmed { get { lock (gate) return (Current is not null || invites.Count > 0) && !OwnRankedRun(); } }
    public GameNotice? Notice() { lock (gate) return ComputeNotice(clock()); }

    GameNotice? ComputeNotice(long now)
    {
        var key = HotkeyName;
        // Quiet while the player runs a ranked scenario of their own: no invite or ready popups.
        var quiet = OwnRankedRun();
        if (!quiet && invites.FirstOrDefault(i => now - i.At < 120_000) is { } invite)
        {
            var from = LobbyRules.CleanName(invite.FromName, "A friend");
            if (invite.Kind == "request")
                return new GameNotice("inv-" + invite.Id, "invite", from + " wants to join", "Press " + key + " to open AimMod and let them in.", key, null, "popup");
            var what = invite.Summary is { } s ? " to " + (LobbyModes.All.Contains(s.Mode) ? ModeLabel(s.Mode) : "a match") + (s.Scenario is { Length: > 0 } sc ? " on " + LobbyRules.CleanName(sc, "a scenario") : "") : "";
            return new GameNotice("inv-" + invite.Id, "invite", from + " invited you" + what, "Join, or press " + key + " to join.", key, null, "popup") { Invite = invite.Id };
        }
        if (Current is not { } lobby) return flash is { } f && now < f.Until ? f.Notice : null;
        var me = lobby.Members.FirstOrDefault(m => m.Id == SelfId);
        if (lobby.Match is { } match && match.Players.Contains(SelfId) && match.Phase is MatchPhases.Countdown && match.StartsAt is { } at)
        {
            var seconds = (int)Math.Max(0, Math.Ceiling((at - now) / 1000.0));
            var round = match.Round > 1 ? "Round " + match.Round + " starting" : "Match starting";
            var how = plan is { } p && p.Key == match.Id + "#" + match.Round ? p.Message : "Get ready.";
            return new GameNotice("cd-" + match.Id + "-" + match.Round, "countdown", round + " in " + seconds, LobbyRules.CleanName(match.Scenario, "Scenario") + " · " + how, null, seconds, "countdown");
        }
        if (!quiet && lobby.ReadyCheck is { } asked && now - asked < LobbyCore.ReadyCheckMs && me is { Role: MemberRoles.Player, Ready: false } && lobby.HostId != SelfId)
        {
            var host = lobby.Members.FirstOrDefault(m => m.Id == lobby.HostId)?.Name ?? "The host";
            var (scenario, map, profiles) = LocalContent(lobby.Settings);
            var ok = scenario is ContentStates.Ok or ContentStates.None && map is ContentStates.Ok or ContentStates.None && profiles is not (ContentStates.Missing or ContentStates.Mismatch);
            return new GameNotice("rc-" + asked, "ready", host + " is starting", ok ? "Press " + key + " to ready up." : "You’re missing the content. Press " + key + " to open the lobby.", key, null, "popup");
        }
        return flash is { } fl && now < fl.Until ? fl.Notice : null;
    }

    // F7: ready up when the host is waiting on you, otherwise open the lobby in AimMod.
    public void Hotkey()
    {
        lock (gate)
        {
            var now = clock();
            var notice = ComputeNotice(now);
            if (notice?.Kind == "ready" && notice.Body.Contains("ready up", StringComparison.Ordinal) && Command("ready", JsonSerializer.SerializeToElement(new { ready = true })).Ok)
            {
                flash = (new GameNotice("ok-" + now, "info", "You’re ready", "The host can start now.", null, null, "click"), now + 3000);
                return;
            }
            if (notice?.Invite is { } id && invites.Any(i => i.Id == id))
            {
                var accept = Act("accept-invite", JsonSerializer.SerializeToElement(new { id }));
                if (!accept.Ok) flash = (new GameNotice("err-" + now, "info", "Couldn’t join", accept.Message ?? "Try the invite again.", null, null, "click"), now + 4000);
                return;
            }
            RequestPanel();
        }
    }

    static string ModeLabel(string mode) => mode switch { LobbyModes.Race => "a score race", LobbyModes.Duel => "a duel", LobbyModes.Rounds => "free-for-all", _ => "practice" };

    // Ask AimModNativeUI to open the AimMod panel on the Multiplayer page. It waits for
    // the main menu, and never interrupts a running scenario: the request stays until then.
    void RequestPanel()
    {
        if (outputFolder is null) return;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, "open-workspace.request"), "multiplayer"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        flash = (new GameNotice("open-" + clock(), "info", "Opening the lobby", "AimMod opens when you’re in the menu. Press Esc to get there.", null, null, "click"), clock() + 4000);
    }

    // The notice file AimModNativeUI polls; rewritten only when it changes.
    void PublishNotice()
    {
        if (outputFolder is null) return;
        var notice = ComputeNotice(clock());
        var json = notice is null ? "{\"version\":1,\"active\":false}" : JsonSerializer.Serialize(new { version = 1, active = true, notice.Id, notice.Kind, notice.Title, notice.Body, notice.Key, notice.Countdown, notice.Sound, notice.Invite, interactive = notice.Invite is not null, volume = prefs.Sounds ? prefs.Volume : 0 }, Protocol.Json);
        if (json == lastNoticeJson) return;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, "multiplayer-notify.json"), json); lastNoticeJson = json; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- content download ------------------------------------------------

    (string Scenario, string Map, string Profiles) LocalContent(LobbySettings s)
    {
        var have = library.Check(s);
        // Developer simulation: pretend this machine lacks the content until it downloads it.
        return simulatedMissing && have.Scenario != ContentStates.None ? (ContentStates.Ok, ContentStates.Missing, have.Profiles) : have;
    }

    LobbyResult StartDownload()
    {
        if (Current is not { } lobby || download is null) return LobbyResult.Fail("unavailable", "Downloads need your KovaaK’s folder.");
        if (lobby.HostId == SelfId) return LobbyResult.Fail("host", "You’re the host; you already have the content.");
        autoDownload = true;
        // Workshop first when the lobby names an item and Steam can fetch it.
        var item = download.Manifest?.Workshop ?? lobby.Settings.Scenario?.WorkshopId;
        if (item is not null && download.Source != "host-fallback" && transport.WorkshopDownload(item))
        {
            workshopItem = item; download.Source = "workshop";
            if (download.Manifest is null) download.Asked();
            return LobbyResult.Success;
        }
        download.Source = "host";
        if (download.Manifest is null || download.State is "error" && download.Code is "changed" or "invalid")
        {
            download.Reset(); download.Asked(); manifestAskedAt = clock();
            AskManifest();
            return LobbyResult.Success;
        }
        if (!download.Start()) return LobbyResult.Fail(download.Code ?? "download", download.Error ?? "Couldn’t start the download.");
        return LobbyResult.Success;
    }

    void AskManifest()
    {
        if (core is not null && simulatedMissing && server.Manifest(core.Settings) is { } local) download?.Offer(local);
        else if (hostPeer is not null) Send(hostPeer, "content.request", new { });
    }

    void WorkshopUpdate(WorkshopProgress? progress)
    {
        if (progress is null || download is null || progress.Item != workshopItem) return;
        if (progress.State is "installed")
        {
            workshopItem = null; library.Refresh(force: true); ReportContent(force: true);
            notice = ("info", "Downloaded from the Steam Workshop.", clock());
        }
        else if (progress.State is "failed" or "unavailable")
        {
            // Fall back to the host transfer.
            workshopItem = null; download.Source = "host-fallback";
            StartDownload();
            if (download.Source == "host") notice = ("info", "The Workshop download didn’t work, so the host is sending the files.", clock());
        }
        workshopState = progress;
    }
    WorkshopProgress? workshopState;

    void PumpContent()
    {
        if (download is null || Current is not { } lobby) return;
        var key = lobby.Id + "|" + lobby.Settings.Scenario?.Hash + "|" + lobby.Settings.MapOverride?.Hash + "|" + lobby.Settings.WeaponProfile.Hash + "|" + lobby.Settings.CharacterProfile.Hash;
        if (key != downloadKey) { downloadKey = key; download.Reset(); autoDownload = false; workshopItem = null; workshopState = null; }
        var now = clock();
        // Host side: serve queued chunks; a running match keeps most of the bandwidth.
        if (core is not null && !simulatedMissing)
            server.Pump(lobby.Match is { Phase: MatchPhases.Live or MatchPhases.Countdown }, (peer, body) => Send(peer, "content.chunk", body), transport,
                (peer, hash, code) => Send(peer, "content.error", new { hash, code }));
        if (lobby.HostId == SelfId && !simulatedMissing) return;
        var (scenario, map, profiles) = LocalContent(lobby.Settings);
        var missing = scenario is ContentStates.Missing or ContentStates.Mismatch || map is ContentStates.Missing or ContentStates.Mismatch || profiles is ContentStates.Missing or ContentStates.Mismatch;
        // Ask for the manifest as soon as something is missing, so the size can be shown.
        if (missing && download.State == "idle") { download.Asked(); manifestAskedAt = now; AskManifest(); }
        if (download.State == "manifest" && now - manifestAskedAt > 10_000) { manifestAskedAt = now; AskManifest(); }
        download.BulkBytes = core is null && hostPeer is not null ? transport.BulkChunkBytes : 0;
        if (hostPeer is not null) foreach (var finished in download.TakeCompleted()) Send(hostPeer, "content.done", new { transfer = finished });
        if (download.State == "downloading" && download.Next() is { } want)
        {
            if (core is not null && simulatedMissing)
            {
                // Simulated host: serve from this machine's own library at a visible pace.
                server.RateIdle = 600 * 1024;
                server.Request(SelfId, lobby.Settings, want.Hash, want.Offset, want.Length);
            }
            else if (hostPeer is not null && want.Transfer > 0) Send(hostPeer, "content.get", new { hash = want.Hash, offset = want.Offset, length = want.Length, transfer = want.Transfer, chunk = want.Chunk });
            else if (hostPeer is not null) Send(hostPeer, "content.get", new { hash = want.Hash, offset = want.Offset, length = want.Length });
        }
        if (core is not null && simulatedMissing)
            server.Pump(false, (_, body) =>
            {
                var b = JsonSerializer.SerializeToElement(body, Protocol.Json);
                download.Chunk(b.GetProperty("hash").GetString()!, b.GetProperty("offset").GetInt64(), b.GetProperty("total").GetInt64(), Convert.FromBase64String(b.GetProperty("data").GetString()!));
            });
        if (download.State == "done" && (missing || simulatedMissing))
        {
            simulatedMissing = false; library.Refresh(force: true); ReportContent(force: true);
            notice = ("info", "Content downloaded and verified. You can ready up.", now);
        }
    }

    object? DownloadView(LobbySnapshot lobby)
    {
        if (download is null || lobby.HostId == SelfId && !simulatedMissing) return null;
        if (download.State == "idle" && workshopItem is null) return null;
        var conflicts = download.Manifest is not null && download.State is "ready" ? download.Conflicts().Select(c => c.Name).ToArray() : [];
        return new
        {
            view = download.View(),
            workshop = lobby.Settings.Scenario?.WorkshopId ?? download.Manifest?.Workshop,
            workshopProgress = workshopState is { } w ? new { w.State, w.Done, w.Total } : null,
            conflicts,
        };
    }

    void Reset() { trackedRound = null; lastContentKey = null; lastBroadcast = -1; plan = null; lastFrame = null; }

    LobbyResult Command(string action, JsonElement args)
    {
        if (core is not null)
        {
            var target = action is "kick" or "transfer" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var remote = target is not null && core.Members.Any(x => x.Id == target && !x.Simulated);
            var result = core.Apply(SelfId, action, args, library);
            // Keep Steam lobby membership and ownership in step with the lobby.
            if (result.Ok && remote && action == "kick") { Send(target!, "bye", new { reason = "kicked" }); transport.Kick(target!); transport.Close(target!); }
            if (result.Ok && remote && action == "transfer") transport.Transfer(target!);
            return result;
        }
        if (mirror is null || hostPeer is null) return LobbyResult.Fail("no-lobby", "You’re not in a lobby.");
        Send(hostPeer, "command", new { id = ++seq, action, args });
        return LobbyResult.Success;
    }

    void Leave(string reason)
    {
        if (core is not null)
            foreach (var peer in RemotePeers(core.Snapshot())) { Send(peer, "bye", new { reason }); transport.Close(peer); }
        else if (hostPeer is not null) { Send(hostPeer, "bye", new { reason }); transport.Close(hostPeer); }
        if (spectating is not null) { transport.StopSpectate(); spectating = null; }
        transport.Withdraw();
        core = null; mirror = null; hostPeer = null; pendingHeir = null; joinPendingSince = null; clocks.Clear();
        // Leaving on purpose (not a crash) forgets the rejoin point.
        if (reason is "left" or "kicked" or "closed") { rejoin = null; lastSessionJson = null; try { if (sessionPath is not null && File.Exists(sessionPath)) File.Delete(sessionPath); } catch (IOException) { } }
        Reset();
    }

    IEnumerable<string> RemotePeers(LobbySnapshot s) => s.Members.Where(m => m.Id != SelfId && !m.Simulated).Select(m => m.Id);

    void Connect(string peer)
    {
        if (hostPeer != peer) connectAt = clock();
        hostPeer = peer; mirrorAt = clock(); joinPendingSince = null;
        Hello();
    }
    void Hello() { if (hostPeer is null) return; helloAt = clock(); Send(hostPeer, "hello", new { name = selfName, proto = Protocol.Version, app = "aimmod-kovaaks", bridge = transport.BridgeVersion, avatar = myAvatar }); }

    void Send(string peer, string type, object body)
    {
        var lobby = Current?.Id ?? "";
        try { transport.Send(peer, Protocol.Encode(Protocol.Create(type, lobby, SelfId, ++seq, clock(), body)), Protocol.Reliable(type)); }
        catch (InvalidOperationException) { }
    }

    void TakeSimulatedInvites()
    {
        if (Simulation is null) return;
        foreach (var invite in Simulation.TakeInvites()) AddInvite(invite);
    }
    // A join the player chose in Steam ("Join Game", an accepted invite, or a launch
    // from one) happens right away; only invites that arrive unasked wait in a popup.
    void AddInvite(IncomingInvite invite)
    {
        if (invite.Kind is "invite" or "launch")
        {
            transport.DismissJoin();
            if (!invite.Compatible) { notice = ("error", LobbyRules.CleanName(invite.FromName, "Your friend") + " is on a different AimMod version. Both of you need the latest AimMod.", clock()); return; }
            if (Current is not null || hostPeer is not null || joinPendingSince is not null) Leave("left");
            var joined = JoinBy(invite.Token, invite: true);
            if (joined.Ok) RequestPanel();
            else notice = ("error", joined.Message ?? "Couldn’t join that lobby.", clock());
            return;
        }
        invites.RemoveAll(i => i.Id == invite.Id || clock() - i.At > 120_000);
        invites.Add(invite);
        if (invites.Count > 5) invites.RemoveAt(0);
    }

    // ---- periodic work ----------------------------------------------------

    public void Tick()
    {
        lock (gate)
        {
            foreach (var e in transport.Drain()) Handle(e);
            TakeSimulatedInvites();
            var now = clock();
            if (core is not null)
            {
                Simulation?.Step(core, SelfId);
                core.Tick();
                if (core.Closed) { core = null; Reset(); return; }
                var snapshot = core.Snapshot();
                // Transfer to a real remote member moves the authority to that machine.
                if (snapshot.HostId != SelfId && snapshot.Members.FirstOrDefault(m => m.Id == snapshot.HostId) is { Simulated: false } heir)
                {
                    Broadcast(snapshot, force: true);
                    core = null; mirror = snapshot; Connect(heir.Id);
                    return;
                }
                Broadcast(snapshot, force: false);
                if (now - lastPing > 2000)
                {
                    lastPing = now;
                    foreach (var peer in RemotePeers(snapshot))
                    {
                        Send(peer, "ping", new { t0 = now });
                        if (transport.Link(peer) is { } link) core.SetLink(peer, link.Route, link.Ping ?? clocks.GetValueOrDefault(peer)?.Rtt);
                    }
                }
            }
            else if (mirror is not null && hostPeer is not null)
            {
                if (now - lastPing > 2000) { lastPing = now; Send(hostPeer, "ping", new { t0 = now }); }
                // No word from the host for 10 s: treat it as gone and migrate.
                if (now - mirrorAt > 10_000) HostLost();
            }
            else if (hostPeer is not null)
            {
                // Waiting for welcome: the relay connection can take a few seconds.
                if (now - connectAt > 20_000) { Leave("timeout"); notice = ("error", "Couldn’t reach the host. Ask them to invite you again.", now); }
                else if (now - helloAt > 2000) Hello();
            }
            else if (joinPendingSince is { } since && now - since > 30_000)
            {
                Leave("timeout"); notice = ("error", "Joining the Steam lobby took too long. Try the invite again.", now);
            }
            ReportContent(force: false);
            PumpContent();
            PumpReplays();
            PrepareMatchScenario();
            PublishNotice();
            AutoReady();
            SaveSession();
            TryRejoin();
            SyncSelf();
            PlanRound();
            TrackLocalRun();
            Remember();
        }
    }

    void Broadcast(LobbySnapshot snapshot, bool force)
    {
        var now = clock();
        if (!force && (snapshot.Revision == lastBroadcast || now - broadcastAt < 250)) return;
        lastBroadcast = snapshot.Revision; broadcastAt = now;
        foreach (var peer in RemotePeers(snapshot)) Send(peer, "snapshot", new { snapshot });
        transport.Advertise(snapshot);
    }

    void Handle(TransportEvent e)
    {
        if (e.Kind == TransportEvent.InviteReceived) { if (e.Invite is not null) AddInvite(e.Invite); return; }
        if (e.Kind == TransportEvent.WorkshopUpdate) { WorkshopUpdate(e.Workshop); return; }
        if (e.Kind == TransportEvent.BulkData) { if (core is null && e.Peer == hostPeer && e.Frame is not null) download?.Bulk(e.Transfer, e.Index, e.Frame); return; }
        if (e.Kind == TransportEvent.BulkAck) return;
        if (e.Kind == TransportEvent.BulkEnd)
        {
            if (core is not null) server.Ended(e.Peer, e.Transfer);
            else if (e.Reason != "complete") download?.Ended(e.Transfer);
            return;
        }
        if (e.Kind == TransportEvent.Error)
        {
            if (joinPendingSince is not null) joinPendingSince = null;
            notice = ("error", e.Reason ?? "Steam reported a problem.", clock());
            return;
        }
        if (e.Kind == TransportEvent.Connected)
        {
            // The relay connection to our host (after joining, or to a new host) is up: say hello.
            if (core is null && e.Host && (joinPendingSince is not null || hostPeer == e.Peer || pendingHeir == e.Peer || mirror is not null)) Connect(e.Peer);
            return;
        }
        if (e.Kind == TransportEvent.Left)
        {
            // Left the Steam lobby for good.
            if (core is not null) { if (core.Members.Any(m => m.Id == e.Peer)) core.Leave(e.Peer); }
            else if (e.Peer == hostPeer) HostLost();
            return;
        }
        if (e.Kind == TransportEvent.Disconnected)
        {
            if (e.Reason is "kicked" && core is null) { Leave("kicked"); notice = ("error", "The host removed you from the lobby.", clock()); return; }
            if (e.Reason is "closed" && core is null) { Leave("closed"); notice = ("error", "The lobby closed.", clock()); return; }
            if (core is not null) core.Disconnected(e.Peer);
            else if (e.Peer == hostPeer) HostLost();
            return;
        }
        if (e.Kind != TransportEvent.Message || e.Frame is null) return;
        var m = Protocol.Decode(e.Frame);
        // The transport authenticates peers; a frame claiming another sender is dropped.
        if (m is null || m.From != e.Peer) return;
        var now = clock();
        if (m.T == "ping") { Send(e.Peer, "pong", new { t0 = m.Body.TryGetProperty("t0", out var t0) && t0.TryGetInt64(out var v) ? v : 0, t1 = now }); return; }
        if (m.T == "pong")
        {
            if (!m.Body.TryGetProperty("t0", out var a) || !a.TryGetInt64(out var sent) || !m.Body.TryGetProperty("t1", out var b) || !b.TryGetInt64(out var remote)) return;
            if (!clocks.TryGetValue(e.Peer, out var sync)) clocks[e.Peer] = sync = new ClockSync();
            sync.Add(sent, remote, now);
            if (core is not null) core.SetLink(e.Peer, transport.Link(e.Peer)?.Route, sync.Rtt);
            else mirrorAt = Math.Max(mirrorAt, now);
            return;
        }
        if (core is not null) HandleAsHost(e.Peer, m);
        else HandleAsClient(e.Peer, m);
    }

    void HandleAsHost(string peer, Envelope m)
    {
        switch (m.T)
        {
            case "hello":
                var name = m.Body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var proto = m.Body.TryGetProperty("proto", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                if (proto != Protocol.Version) { Send(peer, "reject", new { code = "version", message = "Update AimMod to join this lobby." }); return; }
                if (core!.Settings.Privacy == LobbyPrivacy.Invite && !transport.Invited(peer) && core.Members.All(x => x.Id != peer)) { Send(peer, "reject", new { code = "invite", message = "This lobby is invite only." }); return; }
                var version = m.Body.TryGetProperty("bridge", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString() : null;
                var joined = core.Join(peer, name ?? "Player", version: version is { Length: <= 40 } ? version : null);
                if (joined.Ok && m.Body.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String) core.Apply(peer, "avatar", JsonSerializer.SerializeToElement(new { avatar = av.GetString() }), library);
                if (!joined.Ok) { Send(peer, "reject", new { code = joined.Code, message = joined.Message }); transport.Close(peer); return; }
                core.SetLink(peer, transport.Link(peer)?.Route ?? "relay", transport.Link(peer)?.Ping);
                Send(peer, "welcome", new { member = peer, snapshot = core.Snapshot() });
                break;
            case "command":
                var id = m.Body.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                var action = m.Body.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                var args = m.Body.TryGetProperty("args", out var g) ? g : default;
                var result = core!.Apply(peer, action, args, library);
                Send(peer, "result", new { id, ok = result.Ok, code = result.Code, message = result.Message });
                break;
            case "score":
                if (ReadFrame(m.Body) is { } frame) core!.Score(peer, frame);
                break;
            case "finish":
                if (ReadFinish(m.Body) is { } run) core!.Finish(peer, run);
                break;
            case "content.request":
                if (server.Manifest(core!.Settings) is { } manifest) Send(peer, "content.manifest", new { key = manifest.Key, files = manifest.Files, workshop = manifest.Workshop });
                else Send(peer, "content.error", new { hash = "", code = "none" });
                break;
            case "content.get":
                try
                {
                    var hash = m.Body.GetProperty("hash").GetString() ?? "";
                    var transfer = m.Body.TryGetProperty("transfer", out var tr) && tr.TryGetInt32(out var tv) && transport.BulkChunkBytes > 0 ? tv : 0;
                    var chunk = m.Body.TryGetProperty("chunk", out var ch) && ch.TryGetInt32(out var cv) ? Math.Min(cv, transport.BulkChunkBytes) : 0;
                    var problem = server.Request(peer, core!.Settings, hash, m.Body.GetProperty("offset").GetInt64(), m.Body.GetProperty("length").GetInt64(), transfer, chunk);
                    if (problem is not null) Send(peer, "content.error", new { hash, code = problem });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
                break;
            case "replay.chunk":
                // A member's own replay or clip: import it here, then pass it on to everyone else.
                if (ReadReplayChunk(m.Body) is { } c && c.Owner == peer && swap.Chunk(c.Match, c.Round, c.Owner, c.Id, c.Kind, c.Label, c.Size, c.Hash, c.Offset, c.Data) is { } whole && swap.Import(whole.Bytes, whole.Info) is { } stored)
                {
                    foreach (var other in RemotePeers(core!.Snapshot()).Where(p => p != peer)) swap.Offer(other, c.Match, c.Round, c.Owner, stored, c.Kind, whole.Bytes, c.Label);
                    if (c.Kind == "clip") core!.ShareClip(peer, stored, c.Label);
                }
                break;
            case "content.done":
                if (m.Body.TryGetProperty("transfer", out var dt) && dt.TryGetInt32(out var done)) { server.Ended(peer, done); transport.BulkCancel(peer, done, "complete"); }
                break;
            case "bye":
                server.Forget(peer);
                core!.Leave(peer); transport.Close(peer);
                break;
        }
    }

    void HandleAsClient(string peer, Envelope m)
    {
        if (peer != hostPeer) return;
        switch (m.T)
        {
            case "welcome" or "snapshot":
                if (!m.Body.TryGetProperty("snapshot", out var body)) return;
                LobbySnapshot? snapshot;
                try { snapshot = body.Deserialize<LobbySnapshot>(Protocol.Json); } catch (JsonException) { return; }
                if (snapshot is null || !LobbyRules.Plausible(snapshot.Settings) || snapshot.Members.Count > LobbySettings.MaxPlayerLimit + LobbySettings.MaxSpectators) return;
                if (snapshot.Members.All(x => x.Id != SelfId)) { Leave("removed"); notice = ("error", "The host removed you from the lobby.", clock()); return; }
                mirror = snapshot; mirrorAt = clock(); pendingHeir = null; reconnectSince = null;
                if (snapshot.HostId == SelfId)
                {
                    // The host handed the lobby to us.
                    core = LobbyCore.Restore(snapshot, SelfId, clock); hostPeer = null; mirror = null;
                    transport.Advertise(core.Snapshot());
                }
                else if (snapshot.HostId != peer && snapshot.Members.Any(x => x.Id == snapshot.HostId)) { transport.Close(peer); Connect(snapshot.HostId); }
                break;
            case "reject":
                var message = m.Body.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
                transport.Close(peer);
                hostPeer = null; mirror = null; notice = ("error", message ?? "The host couldn’t let you in.", clock());
                break;
            case "result":
                if (m.Body.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False && m.Body.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    notice = ("error", text.GetString() ?? "The host refused that.", clock());
                break;
            case "replay.chunk":
                if (ReadReplayChunk(m.Body) is { } fromHost && swap.Chunk(fromHost.Match, fromHost.Round, fromHost.Owner, fromHost.Id, fromHost.Kind, fromHost.Label, fromHost.Size, fromHost.Hash, fromHost.Offset, fromHost.Data) is { } got) swap.Import(got.Bytes, got.Info);
                break;
            case "content.manifest":
                if (download is not null && ContentDownload.ReadManifest(m.Body) is { } offered) { download.Offer(offered); if (download.State == "ready" && autoDownload) StartDownload(); }
                break;
            case "content.chunk":
                try
                {
                    if (download is not null && m.Body.GetProperty("hash").GetString() is { } chunkHash)
                        download.Chunk(chunkHash, m.Body.GetProperty("offset").GetInt64(), m.Body.GetProperty("total").GetInt64(), Convert.FromBase64String(m.Body.GetProperty("data").GetString() ?? ""));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
                break;
            case "content.error":
                var refusedHash = m.Body.TryGetProperty("hash", out var rh) && rh.ValueKind == JsonValueKind.String ? rh.GetString() ?? "" : "";
                var refusedCode = m.Body.TryGetProperty("code", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() ?? "" : "";
                if (download is not null && refusedCode == "none") { download.Reset(); notice = ("error", "The host can’t send this content. Get it from the Workshop or ask the host.", clock()); }
                else download?.Refused(refusedHash, refusedCode);
                break;
            case "bye":
                var why = m.Body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                if (why == "kicked") { Leave("kicked"); notice = ("error", "The host removed you from the lobby.", clock()); }
                else HostLost();
                break;
        }
    }

    // Host migration on a client: everyone applies the same rule to the last
    // snapshot, so the heir restores the authority and the rest connect to it.
    void HostLost()
    {
        download?.HostGone();
        if (mirror is not { } last) { hostPeer = null; return; }
        // Steam picks the new lobby owner and every bridge follows it, so its choice wins.
        var hint = transport.HostHint;
        if (hint is not null && hint == last.HostId && hostPeer == last.HostId)
        {
            // Steam still lists the host as owner: the relay dropped, so reconnect instead of migrating.
            reconnectSince ??= clock();
            if (clock() - reconnectSince > 30_000) { Leave("timeout"); notice = ("error", "Lost the connection to the host.", clock()); return; }
            mirrorAt = clock(); notice = ("info", "Connection to the host dropped. Reconnecting…", clock());
            Hello();
            return;
        }
        var heir = (hint is not null && hint != last.HostId ? last.Members.FirstOrDefault(m => m.Id == hint) : null)
            ?? last.Members.Where(m => m.Id != last.HostId && m.Connection == Connections.Connected)
                .OrderBy(m => m.Role == MemberRoles.Player ? 0 : 1).ThenBy(m => m.JoinedAt).FirstOrDefault();
        if (hostPeer is not null) transport.Close(hostPeer);
        var oldName = last.Members.FirstOrDefault(m => m.Id == last.HostId)?.Name ?? "The host";
        if (heir is null) { Leave("closed"); notice = ("error", oldName + " left and the lobby closed.", clock()); return; }
        if (heir.Id == SelfId)
        {
            core = LobbyCore.Restore(last, SelfId, clock); mirror = null; hostPeer = null;
            notice = ("info", oldName + " left. You’re the host now.", clock());
            transport.Advertise(core.Snapshot());
            return;
        }
        if (pendingHeir == heir.Id) { Leave("closed"); notice = ("error", "Lost the lobby after the host left.", clock()); return; }
        pendingHeir = heir.Id;
        mirror = last with { HostId = heir.Id, Members = last.Members.Where(m => m.Id != last.HostId).ToArray() };
        notice = ("info", oldName + " left. " + heir.Name + " is the host now.", clock());
        Connect(heir.Id);
    }

    static ScoreFrame? ReadFrame(JsonElement b)
    {
        try
        {
            return new ScoreFrame(b.GetProperty("match").GetString() ?? "", b.GetProperty("round").GetInt32(), b.GetProperty("t").GetDouble(), b.GetProperty("score").GetDouble(),
                b.GetProperty("shots").GetInt32(), b.GetProperty("hits").GetInt32(), b.GetProperty("kills").GetInt32(),
                b.TryGetProperty("remaining", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
    static RunFinish? ReadFinish(JsonElement b)
    {
        try
        {
            return new RunFinish(b.GetProperty("match").GetString() ?? "", b.GetProperty("round").GetInt32(), b.GetProperty("score").GetDouble(), b.GetProperty("t").GetDouble(),
                b.GetProperty("shots").GetInt32(), b.GetProperty("hits").GetInt32(), b.GetProperty("kills").GetInt32(),
                b.TryGetProperty("replay", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    // Tell the host whether this machine has the lobby's scenario, map and profiles.
    void ReportContent(bool force)
    {
        if (Current is not { } lobby) return;
        var s = lobby.Settings;
        var key = string.Join('|', lobby.Id, s.Scenario?.Hash, s.MapOverride?.Hash, s.WeaponProfile.Hash, s.CharacterProfile.Hash);
        var self = lobby.Members.FirstOrDefault(m => m.Id == SelfId);
        if (self is null) return;
        if (!force && key == lastContentKey && self.Scenario != ContentStates.Unknown) return;
        lastContentKey = key;
        var (scenario, map, profiles) = LocalContent(s);
        if (scenario == ContentStates.None) return;
        var args = JsonSerializer.SerializeToElement(new { scenario, map, profiles });
        if (core is not null) core.Apply(SelfId, "content", args, library);
        else if (hostPeer is not null) Send(hostPeer, "command", new { id = ++seq, action = "content", args });
    }

    // A match scenario is written while the lobby is still being set up, as soon
    // as the settings call for one, so KovaaK's has time to index it before the
    // countdown. After writing, AimModCore is asked to refresh its scenario list.
    string? preparedKey, preparedName, preparedProblem;
    long? refreshSequence;
    void PrepareMatchScenario()
    {
        if (Current is not { } lobby || lobby.Match is { Phase: not MatchPhases.Final } || lobby.Settings.Scenario is null) return;
        var s = lobby.Settings;
        if (!MatchScenario.Needed(s)) { preparedKey = null; preparedName = null; preparedProblem = null; return; }
        var key = MatchScenario.Key(s);
        if (key == preparedKey) return;
        var (scenario, map, profiles) = LocalContent(s);
        if (scenario != ContentStates.Ok || map != ContentStates.Ok || profiles is ContentStates.Missing or ContentStates.Mismatch) return;
        preparedKey = key; preparedName = MatchScenario.Name(s);
        preparedProblem = BuildMatchScenario(s, preparedName);
        if (preparedProblem is null) refreshSequence = game.Refresh();
    }

    static string FindIt(string scenario, bool generated) =>
        "In KovaaK’s, open Play > Scenarios, search for “" + scenario + "”" + (generated ? " (it’s one of your local scenarios) and play it in Freeplay." : " and start it.") +
        (generated ? " If it’s not listed yet, restart KovaaK’s once; AimMod saved it to your Scenarios folder." : "");

    // Load the round's scenario during the countdown and start it at zero
    // through AimModCore. Unmodified scenarios run as normal challenges (the
    // player's own ranked-eligible run); anything with lobby overrides runs a
    // generated match scenario in freeplay, scored by AimMod only.
    void PlanRound()
    {
        if (Current is not { Match: { } match } lobby || !match.Players.Contains(SelfId) || match.Phase is MatchPhases.Final) { plan = null; return; }
        var key = match.Id + "#" + match.Round;
        var caps = game.Capabilities;
        if (plan?.Key != key && match.Phase == MatchPhases.Countdown)
        {
            var s = lobby.Settings;
            var generated = MatchScenario.Needed(s);
            var scenario = s.Scenario?.Name ?? "";
            var mode = generated ? "freeplay" : "challenge";
            string? problem = null;
            if (generated)
            {
                scenario = MatchScenario.Name(s);
                // Usually written already while the lobby was set up; this only fills gaps.
                problem = preparedName == scenario && preparedProblem is null ? null : BuildMatchScenario(s, scenario);
            }
            if (problem is not null) plan = new RoundPlan(key, scenario, mode, generated, "error", problem + " " + FindIt(s.Scenario?.Name ?? scenario, false));
            else if (caps.Contains("load") && game.Load(scenario) is long load)
                plan = new RoundPlan(key, scenario, mode, generated, "loading", "Loading “" + scenario + "” in KovaaK’s…", LoadSequence: load);
            else plan = new RoundPlan(key, scenario, mode, generated, "manual", FindIt(scenario, generated) + " Start when the countdown ends.");
        }
        if (plan is null || plan.Key != key) return;
        if (match.Phase == MatchPhases.Live && plan.StartSequence is null && plan.State is "loading" or "ready" or "manual")
        {
            if (caps.Contains("start") && game.Start(plan.Scenario, plan.Mode) is long start) plan = plan with { State = "starting", Message = "Starting your run…", StartSequence = start };
            else plan = plan with { State = "manual", Message = "Go! " + FindIt(plan.Scenario, plan.Generated) };
        }
        if (game.Result is { } result && (result.Sequence == plan.LoadSequence || result.Sequence == plan.StartSequence))
        {
            var state = result.State == "error" ? "error" : result.Code is "loaded" or "already-loaded" ? "ready" : result.Code == "started" ? "started" : plan.State;
            var text = result.State == "error"
                ? (result.Code == "unknown-scenario" ? "KovaaK’s hasn’t picked up “" + plan.Scenario + "” yet. " : "KovaaK’s couldn’t " + (result.Sequence == plan.StartSequence ? "start" : "load") + " the scenario (" + result.Code + "). ") + FindIt(plan.Scenario, plan.Generated)
                : state == "ready" ? "Loaded. Your run starts when the countdown ends." : state == "started" ? "Your run has started." : plan.Message;
            plan = plan with { State = state, Message = text };
        }
    }

    string? BuildMatchScenario(LobbySettings s, string name)
    {
        if (scenarios is null || s.Scenario is null) return "AimMod can’t find your KovaaK’s folder to build the match scenario.";
        try
        {
            var basePath = library.PathOf("scenario", s.Scenario.Name);
            if (basePath is null) return "You don’t have the base scenario.";
            string? mapFile = null, mapText = null, weaponText = null, characterText = null;
            if (s.MapOverride is { } map)
            {
                var path = library.PathOf("map", map.Name);
                if (path is null) return "You don’t have the map " + map.Name + ".";
                mapFile = Path.GetFileName(path); mapText = File.ReadAllText(path);
            }
            if (s.WeaponProfile is { Preset: ProfilePresets.Custom, Custom: { } weapon }) weaponText = library.PathOf("weapon", weapon) is { } w ? File.ReadAllText(w) : null;
            if (s.CharacterProfile is { Preset: ProfilePresets.Custom, Custom: { } character }) characterText = library.PathOf("character", character) is { } c ? File.ReadAllText(c) : null;
            var text = MatchScenario.Generate(new MatchScenario.Inputs(File.ReadAllText(basePath), s, mapFile, mapText, weaponText, characterText));
            var (ok, error) = scenarios.Write(name, text, clock());
            return ok ? null : error == "name-taken" ? "A scenario of yours already uses the match name. Rename it to play." : "Couldn’t save the match scenario.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Couldn’t build the match scenario (" + ex.GetType().Name + ")."; }
    }

    // The local player's score comes from AimModCore's live feed, and the final
    // result from the run journal, exactly as the rest of AimMod records runs.
    void TrackLocalRun()
    {
        if (Current is not { Match: { } match } || !match.Players.Contains(SelfId)) { trackedRound = null; return; }
        var roundKey = match.Id + "#" + match.Round;
        if (trackedRound != roundKey)
        {
            trackedRound = roundKey; lastFrameSeconds = -1; lastFrame = null;
            knownRuns = completedRuns().Take(50).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            Simulation?.ResetSelf();
        }
        var line = match.Live.FirstOrDefault(l => l.MemberId == SelfId);
        if (line is null || line.Status is LineStates.Finished or LineStates.Left or LineStates.Dnf || match.Phase != MatchPhases.Live) return;
        var now = clock();
        var expected = plan?.Key == roundKey ? plan.Scenario : match.Scenario;
        var live = liveRun();
        var playing = live.Active && (string.Equals(live.Scenario, expected, StringComparison.OrdinalIgnoreCase) || string.Equals(live.Scenario, match.Scenario, StringComparison.OrdinalIgnoreCase));
        var simulated = false;
        if (!playing && Simulation is not null) { live = Simulation.SelfRun(match, now); playing = live.Active; simulated = true; }
        // Challenge runs finish through the run journal.
        var finished = completedRuns().Take(10).FirstOrDefault(r => !knownRuns.Contains(r.Id) && (r.Scenario.Equals(match.Scenario, StringComparison.OrdinalIgnoreCase) || r.Scenario.Equals(expected, StringComparison.OrdinalIgnoreCase)));
        if (finished is not null)
        {
            knownRuns.Add(finished.Id);
            var shots = Math.Max(line.Shots, 0); var hits = finished.Accuracy is { } acc && shots > 0 ? (int)Math.Round(shots * acc / 100) : line.Hits;
            SendFinish(new RunFinish(match.Id, match.Round, finished.Score, finished.Duration, shots, Math.Min(hits, shots), (int)Math.Max(0, finished.Kills), null));
            swap.Mine(match.Id, match.Round, finished.Id); pendingReplays.Add((match.Id, match.Round, finished.Id, clock()));
            return;
        }
        // Freeplay (generated) and simulated runs end at the lobby's time limit, scored by AimMod.
        var freeplay = simulated || plan is { Mode: "freeplay" };
        if (freeplay && lastFrame is { } last && (live.Remaining is <= 0 || last.Seconds >= match.TimeLimit - 0.05 || (!playing && now > (match.StartsAt ?? now) + match.TimeLimit * 1000 + 2000)))
        {
            var final = playing && live.Score is { } sc && live.Seconds is { } sec ? new RunFinish(match.Id, match.Round, sc, Math.Min(sec, match.TimeLimit), live.Shots, live.Hits, live.Kills, null)
                : new RunFinish(match.Id, match.Round, last.Score, last.Seconds, last.Shots, last.Hits, last.Kills, null);
            SendFinish(final);
            return;
        }
        if (!playing || live.Score is null || live.Seconds is null) return;
        if (live.Seconds.Value <= lastFrameSeconds || now - lastFrameAt < 100) return;
        lastFrameSeconds = live.Seconds.Value; lastFrameAt = now;
        var frame = new ScoreFrame(match.Id, match.Round, live.Seconds.Value, live.Score.Value, live.Shots, live.Hits, live.Kills, live.Remaining);
        lastFrame = frame;
        if (core is not null) core.Score(SelfId, frame);
        else if (hostPeer is not null) Send(hostPeer, "score", new { match = frame.MatchId, round = frame.Round, t = frame.Seconds, score = frame.Score, shots = frame.Shots, hits = frame.Hits, kills = frame.Kills, remaining = frame.Remaining });
    }
    void SendFinish(RunFinish run)
    {
        if (core is not null) core.Finish(SelfId, run);
        else if (hostPeer is not null) Send(hostPeer, "finish", new { match = run.MatchId, round = run.Round, t = run.Seconds, score = run.Score, shots = run.Shots, hits = run.Hits, kills = run.Kills, replay = run.ReplayHash });
    }

    // ---- recent matches (local only, separate from KovaaK's leaderboards) --

    void Remember()
    {
        if (Current is not { Match: { Phase: MatchPhases.Final } match } lobby) return;
        if (recent.Any(r => r.Id == match.Id)) return;
        var self = match.Standings.FirstOrDefault(s => s.MemberId == SelfId);
        var winner = match.WinnerId is { } w ? match.Standings.FirstOrDefault(s => s.MemberId == w)?.Name : null;
        recent.Insert(0, new RecentMatch(match.Id, clock(), match.Mode, match.Scenario, LobbyModes.Scored(match.Mode) ? self?.Place : null, match.Players.Count, winner,
            match.WinnerId == SelfId, lobby.Members.Any(m => m.Simulated), match.Standings.Select(s => new RecentPlayer(s.Name, s.Place, s.Best, s.Wins, s.Points, s.MemberId == SelfId)).ToArray()));
        if (recent.Count > HistoryLimit) recent.RemoveRange(HistoryLimit, recent.Count - HistoryLimit);
        SaveHistory();
    }
    void LoadHistory()
    {
        if (historyPath is null) return;
        try
        {
            if (!File.Exists(historyPath) || new FileInfo(historyPath).Length > 512 * 1024) return;
            var items = JsonSerializer.Deserialize<RecentMatch[]>(File.ReadAllText(historyPath), Protocol.Json) ?? [];
            recent.AddRange(items.Where(r => r.Id is { Length: > 0 and <= 40 } && r.Standings is not null).Take(HistoryLimit));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void SaveHistory()
    {
        if (historyPath is null) return;
        try { AtomicFile.WriteText(historyPath, JsonSerializer.Serialize(recent, Protocol.Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- view for the UI -----------------------------------------------

    public object View()
    {
        lock (gate)
        {
            var now = clock();
            var lobby = Current;
            var offset = core is null && hostPeer is not null && clocks.TryGetValue(hostPeer, out var sync) ? sync.Offset : 0;
            var caps = game.Capabilities;
            object? lobbyView = null;
            if (lobby is not null)
            {
                var (scenario, map, profiles) = LocalContent(lobby.Settings);
                var generated = MatchScenario.Needed(lobby.Settings) && lobby.Settings.Scenario is not null;
                lobbyView = new
                {
                    lobby.Id, lobby.Code, lobby.Revision, lobby.HostId, lobby.Settings, lobby.Members, lobby.Match, lobby.Chat, lobby.ReadyCheck,
                    self = SelfId, isHost = lobby.HostId == SelfId, authority = core is not null ? "local" : "remote",
                    blockers = LobbyRules.StartBlockers(lobby), content = new { scenario, map, profiles },
                    simulated = lobby.Members.Any(m => m.Simulated),
                    generated = generated ? new { name = MatchScenario.Name(lobby.Settings), key = MatchScenario.Key(lobby.Settings)[..12], mode = "freeplay", saved = preparedName == MatchScenario.Name(lobby.Settings) && preparedProblem is null, problem = preparedName == MatchScenario.Name(lobby.Settings) ? preparedProblem : null } : null,
                    round = plan is { } p && lobby.Match is { } mt && p.Key == mt.Id + "#" + mt.Round ? new { p.Scenario, p.Mode, p.Generated, p.State, p.Message } : null,
                    download = DownloadView(lobby),
                    spectate = SpectateView(lobby),
                    replays = ReplaysView(lobby.Match),
                };
            }
            var friendsSource = Simulation is not null && !transport.Available ? "simulation" : transport.Available ? "steam" : "unavailable";
            return new
            {
                v = Protocol.Version,
                now = now + offset,
                transport = new { kind = transport.Kind, online = transport.Available },
                simulation = Simulation is not null,
                hotkey = HotkeyName,
                prefs,
                rejoin = RejoinOffer(),
                avatars = AvatarProfiles.All.Select(a => new { a.Id, a.Label }),
                capabilities = new { invite = transport.Available, friends = friendsSource != "unavailable", gameLoad = caps.Contains("load"), gameStart = caps.Contains("start") },
                self = new { id = SelfId, name = LocalName() },
                joining = (hostPeer is not null && mirror is null) || joinPendingSince is not null ? new { since = joinPendingSince ?? connectAt, stage = hostPeer is null ? "lobby" : "host" } : null,
                // Steam ids and lobby tokens stay in the service; the UI acts on opaque ids only.
                friends = new { source = friendsSource, items = Friends().Select(f => new { f.Id, f.Name, f.Status, f.Detail, f.Joinable }) },
                invites = invites.Where(i => now - i.At < 120_000).Select(i => new { i.Id, i.FromName, i.Kind, i.Summary, i.At, i.Compatible }),
                recent,
                library = new { available = library.Available, scenarios = library.Available ? library.Scenarios.Count : 0 },
                notice = notice is { } n && now - n.At < 15_000 ? new { kind = n.Kind, text = n.Text } : null,
                lobby = lobbyView,
            };
        }
    }

    public object LibraryView() => new
    {
        available = library.Available,
        scenarios = library.Scenarios.Select(s => new { s.Name, s.Map, s.MapSource, s.TimeLimit, hash = s.Hash[..12], s.DefaultWeapon, s.DefaultCharacter, s.Ported }),
        maps = library.Maps.Select(m => new { m.Name, m.Source, hash = m.Hash[..12] }),
        weapons = library.Weapons.Select(w => w.Name),
        characters = library.Characters.Select(c => c.Name),
        presets = ProfilePresets.All.Select(p => new { id = p.Id, label = p.Label, weapon = p.Weapon, movement = p.Movement }),
    };

    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/multiplayer", (string? part) =>
            part == "library" ? Results.Json(LibraryView(), Protocol.Json) : Results.Json(View(), Protocol.Json));
        // Read-only notice for the always-on in-game layer (notify.html).
        routes.MapGet(prefix + "/multiplayer-notify", () => Notice() is { } n
            ? Results.Json(new { active = true, n.Id, n.Kind, n.Title, n.Body, n.Key, n.Countdown, n.Sound, n.Invite }, Protocol.Json)
            : Results.Json(new { active = false }, Protocol.Json));
        routes.MapPost(prefix + "/multiplayer", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > 8192) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String) return Results.BadRequest(new { error = "Missing action." });
                var result = Act(action.GetString()!, root.Clone());
                return result.Ok ? Results.Json(View(), Protocol.Json) : Results.Json(new { error = result.Message, code = result.Code }, Protocol.Json, statusCode: 409);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete request." }); }
        });
    }

    // Disposed with the service (the hotkey reader).
    public IDisposable? Companion { get; set; }
    public void Dispose() { timer?.Dispose(); Companion?.Dispose(); lock (gate) Leave("closed"); transport.Dispose(); }
}

static class WindowsClipboard
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    public static bool SetText(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(0)) { Thread.Sleep(20); continue; }
            try
            {
                EmptyClipboard();
                var memory = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2));
                if (memory == 0) return false;
                var target = GlobalLock(memory);
                if (target == 0) { GlobalFree(memory); return false; }
                System.Runtime.InteropServices.Marshal.Copy((text + "\0").ToCharArray(), 0, target, text.Length + 1);
                GlobalUnlock(memory);
                if (SetClipboardData(13, memory) == 0) { GlobalFree(memory); return false; }
                return true;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }
}
