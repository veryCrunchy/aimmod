using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimMod.InGame.Multiplayer;

// IMultiplayerTransport over the AimModSteam bridge (in-game/steam-bridge,
// contract v1 in in-game/docs/multiplayer.md section 6). The bridge owns the
// Steam lobby, invites, friends and relay-only P2P; this class only speaks its
// named pipe. Frames are a uint32 little-endian length plus UTF-8 JSON.
// Peer and lobby ids are SteamID64 strings and never reach the UI as text.
sealed class SteamTransport : IMultiplayerTransport
{
    public const string DefaultPipe = "aimmod-steam-v1";
    const int MaxFrame = 65536;
    readonly string pipeName;
    readonly Func<long> clock;
    readonly object gate = new(), writeGate = new();
    readonly Queue<TransportEvent> events = new();
    readonly Dictionary<string, (bool Connected, int? Rtt)> members = new();
    readonly Dictionary<string, int> rtt = new();
    readonly CancellationTokenSource stop = new();
    readonly Thread worker;
    readonly string fallbackPeer = "local-" + Guid.NewGuid().ToString("N")[..10];
    NamedPipeClientStream? pipe;
    bool ready, creating;
    string? self, selfName, lobby, owner;
    bool isHost;
    int nextId = 1, createId = -1, joinId = -1;
    readonly Dictionary<int, string> workshopIds = new();
    readonly Dictionary<(string Peer, int Transfer), int> outstanding = new();
    bool ugc, ugcQuery; IReadOnlyList<WorkshopItem> workshopItems = []; int bulkBytes, bulkWindow = 4;
    string? bridgeVersion; RejoinPoint? lastLobby;
    HashSet<string> lastCharKeys = new();
    readonly Dictionary<string, string> spectators = new();
    IReadOnlyList<FriendEntry> friends = [];
    long friendsAt;
    string? lastData, lastStatus; bool? lastJoinable;

    public SteamTransport(string pipeName = DefaultPipe, Func<long>? clock = null)
    {
        this.pipeName = pipeName;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        worker = new Thread(Run) { IsBackground = true, Name = "AimMod Steam bridge" };
        worker.Start();
    }

    public string Kind => "steam";
    public bool Available { get { lock (gate) return ready; } }
    public string LocalPeer { get { lock (gate) return self ?? fallbackPeer; } }
    public string? LocalName { get { lock (gate) return selfName; } }
    public string? HostHint { get { lock (gate) return lobby is null ? null : owner; } }
    public string? LobbyToken { get { lock (gate) return lobby; } }

    // ---- pipe ------------------------------------------------------------

    void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(1000);
                lock (writeGate) pipe = client;
                Command("hello", null, withId: true);
                var length = new byte[4];
                while (!stop.IsCancellationRequested)
                {
                    client.ReadExactly(length);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(length);
                    if (size is 0 or > MaxFrame) throw new InvalidDataException("Bad bridge frame size.");
                    var payload = new byte[size];
                    client.ReadExactly(payload);
                    Handle(payload);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException or OperationCanceledException) { }
            Dropped();
            if (stop.Token.WaitHandle.WaitOne(2000)) break;
        }
    }

    // The pipe dropped (game closed or bridge restarted): every connection is gone.
    void Dropped()
    {
        lock (writeGate) pipe = null;
        lock (gate)
        {
            if (lobby is not null)
            {
                foreach (var peer in members.Where(m => m.Value.Connected && m.Key != self).Select(m => m.Key)) events.Enqueue(new TransportEvent(peer, TransportEvent.Disconnected, Reason: "bridge"));
                if (!isHost && owner is not null && owner != self) events.Enqueue(new TransportEvent(owner, TransportEvent.Disconnected, Reason: "shutdown"));
            }
            ready = false; creating = false; lobby = null; owner = null; isHost = false; members.Clear(); rtt.Clear(); outstanding.Clear();
            lastData = null; lastStatus = null; lastJoinable = null; lastCharKeys.Clear();
        }
    }

    // Returns the command id (or 0 without one), or -1 when the pipe is down.
    int Command(string name, JsonObject? fields, bool withId = false)
    {
        var message = new JsonObject { ["v"] = 1, ["cmd"] = name };
        int id;
        lock (gate) id = withId ? nextId++ : -1;
        if (withId) message["id"] = id;
        if (fields is not null) foreach (var (key, value) in fields.ToArray()) { fields.Remove(key); message[key] = value; }
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (bytes.Length > MaxFrame) return -1;
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        lock (writeGate)
        {
            if (pipe is null) return -1;
            try { pipe.Write(header); pipe.Write(bytes); pipe.Flush(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return -1; }
        }
        return withId ? id : 0;
    }

    // ---- bridge events -------------------------------------------------

    void Handle(byte[] payload)
    {
        JsonElement e;
        try { using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 }); e = doc.RootElement.Clone(); }
        catch (JsonException) { return; }
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("ev", out var evProperty) || evProperty.ValueKind != JsonValueKind.String) return;
        string? Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        int? Int(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        lock (gate)
        {
            switch (evProperty.GetString())
            {
                case "ready":
                    if (Int(e, "contract") != 1) { events.Enqueue(new TransportEvent("", TransportEvent.Error, Reason: "The Steam bridge speaks a different version. Update AimMod.")); return; }
                    if (e.TryGetProperty("self", out var me)) { self = Str(me, "peer"); selfName = Str(me, "name"); }
                    ready = Bool(e, "steam") && self is not null;
                    // Contract additions: feature list, bulk chunk size and send window.
                    var features = e.TryGetProperty("features", out var fl) && fl.ValueKind == JsonValueKind.Array ? fl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet() : [];
                    ugc = features.Contains("ugc");
                    ugcQuery = ugc && features.Contains("ugc-query");
                    bridgeVersion = Str(e, "bridge");
                    lastLobby = e.TryGetProperty("lastLobby", out var ll) && ll.ValueKind == JsonValueKind.Object && Str(ll, "lobby") is { } lastId
                        ? new RejoinPoint(lastId, Str(ll, "hostName") ?? "your host", ll.TryGetProperty("ageSeconds", out var age) && age.TryGetInt64(out var ag) ? ag : 0) : null;
                    bulkBytes = features.Contains("xfer") ? Math.Clamp(Int(e, "maxChunk") ?? 0, 0, 32768) : 0;
                    bulkWindow = Math.Clamp(Int(e, "xferWindow") ?? 4, 1, 64);
                    break;
                case "result":
                    var id = Int(e, "id");
                    if (e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                    {
                        if (id == createId) creating = false;
                        var text = Str(e, "message") ?? Str(e, "code") ?? "The Steam bridge refused that.";
                        if (id == createId || id == joinId) events.Enqueue(new TransportEvent("", TransportEvent.Error, Reason: id == joinId ? "Couldn’t join the Steam lobby: " + text : "Couldn’t create the Steam lobby: " + text));
                        if (id is { } wid && workshopIds.Remove(wid, out var failed)) events.Enqueue(new TransportEvent("", TransportEvent.WorkshopUpdate, Workshop: new WorkshopProgress(failed, "unavailable", 0, 0)));
                    }
                    break;
                case "lobby.updated":
                    lobby = Str(e, "lobby"); owner = Str(e, "owner"); isHost = Bool(e, "isHost"); creating = false; lastLobby = null;
                    members.Clear();
                    if (e.TryGetProperty("members", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var m in list.EnumerateArray()) if (Str(m, "peer") is { } peer) members[peer] = (Bool(m, "connected"), Int(m, "rtt"));
                    break;
                case "member.joined":
                    if (e.TryGetProperty("member", out var joined) && Str(joined, "peer") is { } joiner) members[joiner] = (Bool(joined, "connected"), Int(joined, "rtt"));
                    break;
                case "member.left":
                    if (Str(e, "peer") is { } leaver) { members.Remove(leaver); rtt.Remove(leaver); events.Enqueue(new TransportEvent(leaver, TransportEvent.Left)); }
                    break;
                case "lobby.left":
                    var reason = Str(e, "reason") ?? "left";
                    if (!isHost && owner is not null) events.Enqueue(new TransportEvent(owner, TransportEvent.Disconnected, Reason: reason));
                    lobby = null; owner = null; isHost = false; members.Clear(); rtt.Clear(); lastData = null; lastJoinable = null;
                    break;
                case "invite.received":
                    // Proposed bridge event (LobbyInvite_t, 503): an invite the player hasn't accepted yet.
                    if (Str(e, "lobby") is not { } invited) break;
                    events.Enqueue(new TransportEvent(Str(e, "from") ?? "", TransportEvent.InviteReceived, Invite: new IncomingInvite("inv-" + invited[^Math.Min(6, invited.Length)..],
                        Str(e, "fromName") ?? "A friend", "incoming", invited, null, clock(), !e.TryGetProperty("compatible", out var cv) || cv.ValueKind != JsonValueKind.False)));
                    break;
                case "join.requested":
                    if (Str(e, "lobby") is not { } target) break;
                    var source = Str(e, "source") ?? "steam-invite";
                    var from = Str(e, "fromName");
                    events.Enqueue(new TransportEvent(Str(e, "from") ?? "", TransportEvent.InviteReceived, Invite: new IncomingInvite("join-" + target[^Math.Min(6, target.Length)..],
                        from ?? "A friend", source.StartsWith("launch", StringComparison.Ordinal) ? "launch" : "invite", target, null, clock(), Bool(e, "compatible"))));
                    break;
                case "p2p.connected":
                    if (Str(e, "peer") is { } connected)
                    {
                        if (members.TryGetValue(connected, out var state)) members[connected] = (true, state.Rtt);
                        events.Enqueue(new TransportEvent(connected, TransportEvent.Connected, Host: Bool(e, "host")));
                    }
                    break;
                case "p2p.disconnected":
                    if (Str(e, "peer") is { } gone)
                    {
                        if (members.TryGetValue(gone, out var state)) members[gone] = (false, state.Rtt);
                        events.Enqueue(new TransportEvent(gone, TransportEvent.Disconnected, Reason: Str(e, "reason")));
                    }
                    break;
                case "p2p.message":
                    if (Str(e, "peer") is { } sender && Str(e, "data") is { } data)
                    {
                        try { events.Enqueue(new TransportEvent(sender, TransportEvent.Message, Convert.FromBase64String(data))); }
                        catch (FormatException) { }
                    }
                    break;
                case "p2p.ping":
                    if (Str(e, "peer") is { } pinged && Int(e, "rtt") is { } ms) rtt[pinged] = ms;
                    break;
                case "friends":
                    var items = new List<FriendEntry>();
                    if (e.TryGetProperty("friends", out var all) && all.ValueKind == JsonValueKind.Array)
                        foreach (var f in all.EnumerateArray())
                        {
                            var peer = Str(f, "peer"); var name = Str(f, "name"); var persona = Str(f, "state") ?? "offline";
                            if (peer is null || string.IsNullOrWhiteSpace(name) || persona == "offline") continue;
                            var aimmod = Bool(f, "aimmod"); var playing = Bool(f, "playing"); var joinLobby = Str(f, "lobby");
                            // aimmodState: lobby, playing or idle (rich presence from every AimModSteam).
                            var state = Str(f, "aimmodState"); var scenario = Str(f, "scenario");
                            int? size = Int(f, "lobbySize"), maxSize = Int(f, "lobbyMax");
                            var joinable = joinLobby is not null && aimmod && (!f.TryGetProperty("lobbyJoinable", out var lj) || lj.ValueKind != JsonValueKind.False);
                            var status = joinLobby is not null && aimmod || state == "lobby" ? "aimmod-lobby" : aimmod ? "aimmod" : playing ? "kovaaks" : persona == "online" ? "online" : "away";
                            var shown = scenario is { Length: > 0 } ? LobbyRules.CleanName(scenario, "a scenario") : null;
                            var detail = status switch
                            {
                                "aimmod-lobby" => "In AimMod lobby" + (size is { } s && maxSize is { } mx ? " (" + s + "/" + mx + ")" : ""),
                                "aimmod" => state == "playing" && shown is not null ? "Playing " + shown : state == "idle" ? "Idle" : "Playing KovaaK’s with AimMod",
                                "kovaaks" => "Playing KovaaK’s, no AimMod",
                                "away" => "Away",
                                _ => "Online",
                            };
                            // Provisional (lobby-less spectating): spectatable and spectators.
                            var spectatable = Bool(f, "spectatable") || Bool(f, "spectateAsks");
                            var watchers = Int(f, "spectators") ?? 0;
                            if (watchers > 0) detail += " · " + watchers + " watching";
                            // aimmod_workshop presence: the Workshop item of the scenario they play, if any.
                            var workshopItem = Str(f, "workshop") ?? Str(f, "aimmodWorkshop");
                            if (workshopItem is not { Length: > 0 and <= 20 } || !workshopItem.All(char.IsAsciiDigit)) workshopItem = null;
                            items.Add(new FriendEntry(peer, LobbyRules.CleanName(name, "Friend"), status, detail, joinLobby, joinable, spectatable, watchers, shown, workshopItem));
                        }
                    // AimMod players first, then KovaaK's players, then everyone else online.
                    friends = items.OrderBy(f => f.Status switch { "aimmod-lobby" => 0, "aimmod" => 1, "kovaaks" => 2, "online" => 3, _ => 4 }).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
                    break;
                case "ugc.progress" or "ugc.state" or "ugc.installed" or "ugc.error":
                    if (Str(e, "item") is { } item)
                    {
                        var ev = evProperty.GetString();
                        var ugcState = ev == "ugc.installed" ? "installed" : ev == "ugc.error" ? "failed" : ev == "ugc.progress" ? "downloading" : Str(e, "state") ?? "queued";
                        events.Enqueue(new TransportEvent("", TransportEvent.WorkshopUpdate, Reason: Str(e, "message"), Workshop: new WorkshopProgress(item, ugcState,
                            e.TryGetProperty("downloaded", out var d) && d.TryGetInt64(out var dn) ? dn : 0, e.TryGetProperty("total", out var t) && t.TryGetInt64(out var tn) ? tn : 0)));
                    }
                    break;
                // Contract addition: ugc.items {tag, items: [{item, title, bytes, updated, subscribed, installed, needsUpdate}]}.
                case "ugc.items":
                    if (e.TryGetProperty("items", out var ugcList) && ugcList.ValueKind == JsonValueKind.Array)
                        workshopItems = ugcList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Str(x, "item") is { } i && Steam(i) && Str(x, "title") is { Length: > 0 and <= 128 })
                            .Take(500).Select(x => new WorkshopItem(Str(x, "item")!, Str(x, "title")!, x.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bn) ? Math.Max(0, bn) : 0,
                                x.TryGetProperty("updated", out var u) && u.TryGetInt64(out var un) ? un : 0, Bool(x, "subscribed"), Bool(x, "installed"), Bool(x, "needsUpdate"))).ToArray();
                    break;
                // Spectating a friend without a lobby (contract §6, "spectating a friend without a lobby").
                case "spectate.asked":
                    if (Str(e, "from") is { } asker) events.Enqueue(new TransportEvent(asker, TransportEvent.SpectateAsked, Reason: Str(e, "fromName")));
                    break;
                case "spectate.started":
                    if (Str(e, "peer") is { } watched) events.Enqueue(new TransportEvent(watched, TransportEvent.SpectateStarted, Reason: Str(e, "name"), Host: Bool(e, "direct")));
                    break;
                case "spectate.ended" when Str(e, "peer") is { } endedPeer:
                    events.Enqueue(new TransportEvent(endedPeer, TransportEvent.SpectateEnded, Reason: Str(e, "reason")));
                    break;
                case "spectate.score":
                    // The watched player's live stats, every 250 ms while fresh.
                    events.Enqueue(new TransportEvent(Str(e, "peer") ?? "", TransportEvent.SpectateScore, Frame: Encoding.UTF8.GetBytes(e.GetRawText())));
                    break;
                case "spectator.joined":
                    if (Str(e, "peer") is { } viewer) { spectators[viewer] = Str(e, "name") ?? "A friend"; events.Enqueue(new TransportEvent(viewer, TransportEvent.SpectatorJoined, Reason: spectators[viewer])); }
                    break;
                case "spectator.left":
                    if (Str(e, "peer") is { } gonePeer) { spectators.Remove(gonePeer); events.Enqueue(new TransportEvent(gonePeer, TransportEvent.SpectatorLeft, Reason: Str(e, "reason"))); }
                    break;
                case "spectators":
                    // Full list: sync quietly (no "is watching you" toasts for people already there).
                    var now = new Dictionary<string, string>();
                    if (e.TryGetProperty("list", out var sl) && sl.ValueKind == JsonValueKind.Array)
                        foreach (var s in sl.EnumerateArray()) if (Str(s, "peer") is { } sp) now[sp] = Str(s, "name") ?? "A friend";
                    foreach (var old in spectators.Keys.Where(k => !now.ContainsKey(k)).ToArray()) events.Enqueue(new TransportEvent(old, TransportEvent.SpectatorLeft, Reason: "sync"));
                    foreach (var (sp, sn) in now.Where(kv => !spectators.ContainsKey(kv.Key))) events.Enqueue(new TransportEvent(sp, TransportEvent.SpectatorJoined, Reason: sn, Host: true));
                    spectators.Clear(); foreach (var kv in now) spectators[kv.Key] = kv.Value;
                    break;
                case "xfer.chunk":
                    if (Str(e, "peer") is { } xpeer && Int(e, "transfer") is { } xid && Int(e, "index") is { } xindex && Str(e, "data") is { } xdata)
                    {
                        try { events.Enqueue(new TransportEvent(xpeer, TransportEvent.BulkData, Convert.FromBase64String(xdata), Transfer: xid, Index: xindex)); }
                        catch (FormatException) { }
                    }
                    break;
                case "xfer.ack":
                    if (Str(e, "peer") is { } apeer && Int(e, "transfer") is { } aid)
                    {
                        var k = (apeer, aid);
                        var credit = Int(e, "credit");
                        outstanding[k] = credit is { } c ? Math.Max(0, bulkWindow - c) : Math.Max(0, outstanding.GetValueOrDefault(k) - 1);
                        events.Enqueue(new TransportEvent(apeer, TransportEvent.BulkAck, Transfer: aid, Index: Int(e, "index") ?? 0));
                    }
                    break;
                case "xfer.end":
                    if (Str(e, "peer") is { } epeer && Int(e, "transfer") is { } eid)
                    {
                        outstanding.Remove((epeer, eid));
                        events.Enqueue(new TransportEvent(epeer, TransportEvent.BulkEnd, Reason: Str(e, "reason"), Transfer: eid));
                    }
                    break;
                case "error":
                    var code = Str(e, "code");
                    var message = Str(e, "message") ?? code ?? "Steam bridge error.";
                    events.Enqueue(new TransportEvent("", TransportEvent.Error, Reason: code == "rejected" ? "The host refused the connection: " + message : message));
                    break;
            }
        }
    }

    // ---- IMultiplayerTransport ----------------------------------------------

    public IReadOnlyList<TransportEvent> Drain()
    {
        var now = clock();
        bool refresh;
        lock (gate) { refresh = ready && now - friendsAt > 15_000; if (refresh) friendsAt = now; }
        if (refresh) Command("friends.list", null);
        lock (gate) { var list = events.ToArray(); events.Clear(); return list; }
    }

    public void Advertise(LobbySnapshot s)
    {
        bool create, host;
        lock (gate) { if (!ready) return; create = lobby is null && !creating; host = isHost && lobby is not null; if (create) creating = true; }
        var players = s.Members.Count(m => m.Role == MemberRoles.Player);
        var data = new JsonObject
        {
            ["aimmod.code"] = s.Code,
            ["aimmod.mode"] = s.Settings.Mode,
            ["aimmod.scenario"] = Truncate(s.Settings.Scenario?.Name ?? "", 256),
            ["aimmod.scenario_hash"] = (s.Settings.Scenario?.Hash ?? "")[..Math.Min(16, s.Settings.Scenario?.Hash.Length ?? 0)],
            ["aimmod.players"] = players + "/" + s.Settings.MaxPlayers,
            ["aimmod.state"] = s.Match is null ? "lobby" : s.Match.Phase == MatchPhases.Final ? "results" : "match",
        };
        // How each player looks in the others' games: aimmod.char.<SteamID> = character profile.
        foreach (var m in s.Members.Where(m => Steam(m.Id)).Take(16))
            data["aimmod.char." + m.Id] = (AvatarProfiles.Find(m.Avatar) ?? AvatarProfiles.All[0]).ProfileName;
        // Keys of members who left are removed (null deletes a lobby key).
        string[] gone;
        lock (gate) { gone = lastCharKeys.Where(k => !data.ContainsKey(k)).ToArray(); lastCharKeys = data.Select(kv => kv.Key).Where(k => k.StartsWith("aimmod.char.", StringComparison.Ordinal)).ToHashSet(); }
        foreach (var key in gone) data[key] = null;
        if (create)
        {
            var max = Math.Clamp(s.Settings.MaxPlayers + (s.Settings.Spectators ? LobbySettings.MaxSpectators : 0), 2, 16);
            foreach (var empty in data.Where(kv => kv.Value is null).Select(kv => kv.Key).ToArray()) data.Remove(empty);
            var id = Command("lobby.create", new JsonObject { ["privacy"] = s.Settings.Privacy == LobbyPrivacy.Invite ? "invite" : "friends", ["maxMembers"] = max, ["data"] = data }, withId: true);
            lock (gate) { createId = id; if (id < 0) creating = false; }
            return;
        }
        if (!host) return;
        var dataJson = data.ToJsonString();
        var joinable = (s.Match is null || s.Match.Phase == MatchPhases.Final || s.Settings.LateJoin) && players < s.Settings.MaxPlayers;
        var status = s.Match is { Phase: not MatchPhases.Final } ? "In an AimMod match" : "In an AimMod lobby (" + players + "/" + s.Settings.MaxPlayers + ")";
        bool sendData, sendJoinable, sendStatus;
        lock (gate)
        {
            sendData = dataJson != lastData; lastData = dataJson;
            sendJoinable = joinable != lastJoinable; lastJoinable = joinable;
            sendStatus = status != lastStatus; lastStatus = status;
        }
        if (sendData) Command("lobby.setData", new JsonObject { ["data"] = JsonNode.Parse(dataJson) });
        if (sendJoinable) Command("lobby.setJoinable", new JsonObject { ["joinable"] = joinable });
        if (sendStatus) Command("presence.set", new JsonObject { ["status"] = status });
    }
    static string Truncate(string text, int bytes)
    {
        while (Encoding.UTF8.GetByteCount(text) > bytes) text = text[..^1];
        return text;
    }

    public void Withdraw()
    {
        bool leave;
        lock (gate) { leave = lobby is not null || creating; lobby = null; owner = null; isHost = false; creating = false; members.Clear(); rtt.Clear(); lastData = null; lastJoinable = null; lastStatus = null; lastCharKeys.Clear(); }
        if (leave) Command("lobby.leave", null);
    }

    // Room codes resolve through the Hub, not Steam.
    public string? Resolve(string code) => null;

    public bool BeginJoin(string token)
    {
        if (!Available || token.Length is < 1 or > 20 || !token.All(char.IsAsciiDigit)) return false;
        var id = Command("lobby.join", new JsonObject { ["lobby"] = token }, withId: true);
        lock (gate) joinId = id;
        return id >= 0;
    }
    public void DismissJoin() { if (Available) Command("join.dismiss", null); }
    public string? BridgeVersion { get { lock (gate) return ready ? bridgeVersion : null; } }
    public RejoinPoint? LastLobby { get { lock (gate) return ready && lobby is null ? lastLobby : null; } }
    public void SetPresencePrivacy(bool hideScenario) { if (Available) Command("presence.privacy", new JsonObject { ["hideScenario"] = hideScenario }); }
    public bool StartSpectate(string peer, int rate)
    {
        if (!Available || !Steam(peer)) return false;
        return Command("spectate.start", new JsonObject { ["peer"] = peer, ["rate"] = Math.Clamp(rate, 1, 60) }) >= 0;
    }
    public void StopSpectate() { if (Available) Command("spectate.stop", null); }
    public bool RequestSpectate(string peer) => Available && Steam(peer) && Command("spectate.request", new JsonObject { ["peer"] = peer, ["rate"] = 60 }, withId: true) >= 0;
    public void RemoveSpectator(string peer) { if (Available && Steam(peer)) Command("spectate.remove", new JsonObject { ["peer"] = peer }); }
    public void AnswerSpectate(string peer, bool allow) { if (Available && Steam(peer)) Command("spectate.answer", new JsonObject { ["peer"] = peer, ["allow"] = allow }); }
    public void SetSpectatePrivacy(string mode) { if (Available && mode is "friends" or "ask" or "off") Command("spectate.privacy", new JsonObject { ["mode"] = mode }); }
    // Contract additions: ugc.download {item, highPriority}, answered by ugc.progress,
    // ugc.installed or ugc.error. A failed result falls back to the host transfer.
    public bool WorkshopDownload(string item)
    {
        bool can; lock (gate) can = ready && ugc;
        if (!can || item.Length is < 1 or > 20 || !item.All(char.IsAsciiDigit)) return false;
        var id = Command("ugc.download", new JsonObject { ["item"] = item, ["highPriority"] = true }, withId: true);
        lock (gate) { if (id >= 0) workshopIds[id] = item; }
        return id >= 0;
    }
    // Contract addition: ugc.query {text?, tag?} (at least one), answered by ugc.items then a
    // result; a query while one runs gets busy. KovaaK's uploader sets no tags, so ports are
    // found by title text and filtered by name.
    public bool QueryWorkshop(string? text, string? tag = null)
    {
        bool can; lock (gate) can = ready && ugcQuery;
        if (!can || (text is not { Length: > 0 and <= 64 } && tag is not { Length: > 0 and <= 32 })) return false;
        var fields = new JsonObject();
        if (text is { Length: > 0 and <= 64 }) fields["text"] = text;
        if (tag is { Length: > 0 and <= 32 }) fields["tag"] = tag;
        return Command("ugc.query", fields, withId: true) >= 0;
    }
    public IReadOnlyList<WorkshopItem> WorkshopItems { get { lock (gate) return workshopItems; } }
    public void Kick(string peer) { if (Steam(peer)) Command("lobby.kick", new JsonObject { ["peer"] = peer }); }
    public void Transfer(string peer) { if (Steam(peer)) Command("lobby.transfer", new JsonObject { ["peer"] = peer }); }
    static bool Steam(string peer) => peer.Length is > 0 and <= 20 && peer.All(char.IsAsciiDigit);

    public void Send(string peer, byte[] frame, bool reliable)
    {
        if (!Steam(peer) || frame.Length is 0 or > Protocol.MaxBytes) return;
        Command("p2p.send", new JsonObject { ["peer"] = peer, ["reliable"] = reliable, ["data"] = Convert.ToBase64String(frame) });
    }
    public void Close(string peer) { if (Steam(peer)) Command("p2p.close", new JsonObject { ["peer"] = peer }); }

    // Bulk lane (xfer.*): the bridge allows bulkWindow unacknowledged chunks per transfer.
    public int BulkChunkBytes { get { lock (gate) return ready ? bulkBytes : 0; } }
    public BulkSend BulkChunk(string peer, int transfer, int index, byte[] data)
    {
        lock (gate)
        {
            if (!ready || bulkBytes == 0 || !Steam(peer) || transfer < 1 || data.Length is 0 || data.Length > bulkBytes) return BulkSend.Unavailable;
            if (outstanding.GetValueOrDefault((peer, transfer)) >= bulkWindow) return BulkSend.WindowFull;
            outstanding[(peer, transfer)] = outstanding.GetValueOrDefault((peer, transfer)) + 1;
        }
        return Command("xfer.chunk", new JsonObject { ["peer"] = peer, ["transfer"] = transfer, ["index"] = index, ["data"] = Convert.ToBase64String(data) }) >= 0 ? BulkSend.Sent : BulkSend.Unavailable;
    }
    public void BulkCancel(string peer, int transfer, string reason)
    {
        lock (gate) outstanding.Remove((peer, transfer));
        if (Steam(peer) && transfer > 0) Command("xfer.cancel", new JsonObject { ["peer"] = peer, ["transfer"] = transfer, ["reason"] = reason is "complete" or "error" ? reason : "cancel" });
    }

    public bool InviteOverlay(LobbySnapshot lobbySnapshot)
    {
        lock (gate) if (!ready || lobby is null) return false;
        return Command("lobby.invite", null) >= 0;
    }
    public bool InviteFriend(string friendId, LobbySnapshot lobbySnapshot)
    {
        lock (gate) if (!ready || lobby is null || friends.All(f => f.Id != friendId)) return false;
        Command("lobby.invite", new JsonObject { ["friend"] = friendId });
        return true;
    }
    // Steam lobby membership already gates who can connect, for invite-only lobbies too.
    public bool Invited(string peer) { lock (gate) return members.ContainsKey(peer); }
    public IReadOnlyList<FriendEntry> Friends() { lock (gate) return friends; }
    public PeerLink? Link(string peer)
    {
        lock (gate)
        {
            if (!members.TryGetValue(peer, out var m)) return null;
            int? ping = rtt.TryGetValue(peer, out var r) ? r : m.Rtt;
            return new PeerLink(m.Connected ? "connected" : "connecting", "relay", ping);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        Withdraw();
        lock (writeGate) { try { pipe?.Dispose(); } catch (IOException) { } pipe = null; }
        worker.Join(3000);
    }
}
