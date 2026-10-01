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
            ready = false; creating = false; lobby = null; owner = null; isHost = false; members.Clear(); rtt.Clear();
            lastData = null; lastStatus = null; lastJoinable = null;
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
                    lobby = Str(e, "lobby"); owner = Str(e, "owner"); isHost = Bool(e, "isHost"); creating = false;
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
                            var status = joinLobby is not null && aimmod ? "aimmod-lobby" : aimmod ? "aimmod" : playing ? "kovaaks" : persona == "online" ? "online" : "away";
                            var detail = status switch { "aimmod-lobby" => "In an AimMod lobby", "aimmod" => "Playing KovaaK’s with AimMod", "kovaaks" => "Playing KovaaK’s, no AimMod", "away" => "Away", _ => "Online" };
                            items.Add(new FriendEntry(peer, LobbyRules.CleanName(name, "Friend"), status, detail, joinLobby, joinLobby is not null && aimmod));
                        }
                    // AimMod players first, then KovaaK's players, then everyone else online.
                    friends = items.OrderBy(f => f.Status switch { "aimmod-lobby" => 0, "aimmod" => 1, "kovaaks" => 2, "online" => 3, _ => 4 }).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
                    break;
                case "ugc.progress" or "ugc.state":
                    if (Str(e, "item") is { } item)
                        events.Enqueue(new TransportEvent("", TransportEvent.WorkshopUpdate, Workshop: new WorkshopProgress(item, Str(e, "state") ?? "downloading",
                            e.TryGetProperty("downloaded", out var d) && d.TryGetInt64(out var dn) ? dn : 0, e.TryGetProperty("total", out var t) && t.TryGetInt64(out var tn) ? tn : 0)));
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
        if (create)
        {
            var max = Math.Clamp(s.Settings.MaxPlayers + (s.Settings.Spectators ? LobbySettings.MaxSpectators : 0), 2, 16);
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
        lock (gate) { leave = lobby is not null || creating; lobby = null; owner = null; isHost = false; creating = false; members.Clear(); rtt.Clear(); lastData = null; lastJoinable = null; lastStatus = null; }
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
    // Proposed bridge commands (not in contract v1 yet): ugc.download {item} answered by
    // ugc.progress {item, state, downloaded, total}. An unknown-command result falls back to the host.
    public bool WorkshopDownload(string item)
    {
        if (!Available || item.Length is < 1 or > 20 || !item.All(char.IsAsciiDigit)) return false;
        var id = Command("ugc.download", new JsonObject { ["item"] = item }, withId: true);
        lock (gate) { if (id >= 0) workshopIds[id] = item; }
        return id >= 0;
    }
    public void Kick(string peer) { if (Steam(peer)) Command("lobby.kick", new JsonObject { ["peer"] = peer }); }
    public void Transfer(string peer) { if (Steam(peer)) Command("lobby.transfer", new JsonObject { ["peer"] = peer }); }
    static bool Steam(string peer) => peer.Length is > 0 and <= 20 && peer.All(char.IsAsciiDigit);

    public void Send(string peer, byte[] frame, bool reliable)
    {
        if (!Steam(peer) || frame.Length is 0 or > Protocol.MaxBytes) return;
        Command("p2p.send", new JsonObject { ["peer"] = peer, ["reliable"] = reliable, ["data"] = Convert.ToBase64String(frame) });
    }
    public void Close(string peer) { if (Steam(peer)) Command("p2p.close", new JsonObject { ["peer"] = peer }); }

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
