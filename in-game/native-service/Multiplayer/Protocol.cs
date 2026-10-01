using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame.Multiplayer;

// Versioned P2P message schema. Every frame is one UTF-8 JSON envelope:
//   {"p":"aimmod.mp","v":1,"t":"<type>","lobby":"<id>","from":"<member>","seq":n,"at":unixMs,"body":{...}}
// The same frames travel over Steam SDR sockets and the Hub relay fallback.
// A receiver drops any frame with another protocol name, a different major
// version, an unknown type, an oversized body or a sender that doesn't match
// the transport's authenticated peer.
static class Protocol
{
    public const string Name = "aimmod.mp";
    public const int Version = 1;
    public const int MaxBytes = 16 * 1024;
    public sealed record Spec(string Type, bool Reliable, string Direction, string Body);
    public static readonly Spec[] Types =
    [
        new("hello", true, "client>host", "{name, proto, app}: join request"),
        new("welcome", true, "host>client", "{member, snapshot}: accepted, with the full lobby"),
        new("reject", true, "host>client", "{code, message}: full, kicked, in-match, closed or version"),
        new("snapshot", true, "host>all", "{snapshot}: full lobby after a change, at most 4 per second"),
        new("command", true, "client>host", "{id, action, args}: ready, content, settings, kick, transfer, chat, role, start, next, end, rematch"),
        new("result", true, "host>client", "{id, ok, code, message}: outcome of a command"),
        new("score", false, "client>host", "{match, round, t, score, shots, hits, kills, remaining}: live frame, 10 Hz"),
        new("finish", true, "client>host", "{match, round, t, score, shots, hits, kills, replay}: final run result"),
        new("ping", false, "any", "{t0}: clock sync request"),
        new("pong", false, "any", "{t0, t1}: clock sync reply"),
        new("bye", true, "any", "{reason}: leaving, or the host closing the lobby"),
    ];
    public static bool Reliable(string type) => Types.First(t => t.Type == type).Reliable;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, MaxDepth = 16 };

    public static byte[] Encode(Envelope message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Frame exceeds " + MaxBytes + " bytes.");
        return bytes;
    }
    public static Envelope Create(string type, string lobby, string from, long seq, long at, object body) =>
        new(Name, Version, type, lobby, from, seq, at, JsonSerializer.SerializeToElement(body, Json));

    public static Envelope? Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length is 0 or > MaxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(frame.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Str(string key, int max) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Length <= max ? s : null;
            long? Int(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            if (Str("p", 16) != Name || Int("v") != Version) return null;
            var type = Str("t", 16);
            if (type is null || Types.All(t => t.Type != type)) return null;
            var lobby = Str("lobby", 40); var from = Str("from", 64);
            if (lobby is null || string.IsNullOrEmpty(from) || Int("seq") is not { } seq || seq < 0 || Int("at") is not { } at) return null;
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return null;
            return new Envelope(Name, Version, type, lobby, from, seq, at, body.Clone());
        }
        catch (JsonException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }
}

sealed record Envelope(string P, int V, string T, string Lobby, string From, long Seq, long At, JsonElement Body);

// The transport moves opaque frames between authenticated peers. Steam (SDR
// P2P through the AimModSteam bridge) and the Hub WebSocket relay both
// implement it; OfflineTransport is used when neither is available, and the
// simulation then fills lobbies with local members.
interface IMultiplayerTransport : IDisposable
{
    string Kind { get; }
    bool Available { get; }
    // This machine's peer id; remote members use their peer id as member id.
    // It is opaque to the UI and never shown or typed in.
    string LocalPeer { get; }
    // The Steam persona name, when the bridge knows it.
    string? LocalName { get; }
    // Host: make the lobby joinable (friends lobby with aimmod.* keys, rich
    // presence connect string, Hub room code). Called again when it changes.
    void Advertise(LobbySnapshot lobby);
    void Withdraw();
    // Client: find the host peer for a room code or an accepted invite token.
    string? Resolve(string code);
    // Client: start joining from an invite, a launch join or a friend's lobby. The
    // host peer then arrives as a Connected event with Host = true.
    bool BeginJoin(string token);
    // Drop a pending invite or launch join that the player declined.
    void DismissJoin();
    // Host: mirror LobbyCore's kick and host transfer so Steam membership agrees.
    void Kick(string peer);
    void Transfer(string peer);
    // Who the transport considers the host (the Steam lobby owner), when it knows.
    string? HostHint { get; }
    void Send(string peer, byte[] frame, bool reliable);
    void Close(string peer);
    // Events since the last call: connected, disconnected, a frame, an incoming
    // invite or join request, or the join the game was launched with.
    IReadOnlyList<TransportEvent> Drain();
    // Steam overlay invite dialog with our connect string. False when unavailable.
    bool InviteOverlay(LobbySnapshot lobby);
    // Invite one friend from the friends panel.
    bool InviteFriend(string friendId, LobbySnapshot lobby);
    // Invite-only lobbies accept peers this machine invited, or whose join request was accepted.
    bool Invited(string peer);
    IReadOnlyList<FriendEntry> Friends();
    // Route and ping for a peer, as the relay network reports them.
    PeerLink? Link(string peer);
}
// Host is true on Connected when that peer is our host. Reason explains a
// Disconnected (for the lobby itself: left, kicked, closed or shutdown) or an Error.
sealed record TransportEvent(string Peer, string Kind, byte[]? Frame = null, IncomingInvite? Invite = null, bool Host = false, string? Reason = null)
{
    public const string Connected = "connected", Disconnected = "disconnected", Left = "left", Message = "message", InviteReceived = "invite", Error = "error";
}
// An invite or join request from Steam. Token is opaque (the connect string
// payload); Summary is what the host advertised, shown before accepting.
// Kind is invite (they invite you), request (they ask to join yours) or launch.
sealed record IncomingInvite(string Id, string FromName, string Kind, string Token, LobbySummary? Summary, long At, bool Compatible = true);
sealed record LobbySummary(string Mode, string? Scenario, int Players, int MaxPlayers);
// State is connecting or connected; Route is relay, direct or local.
sealed record PeerLink(string State, string Route, int? Ping);

// No network: lobbies stay on this machine.
sealed class OfflineTransport : IMultiplayerTransport
{
    public string Kind => "offline";
    public bool Available => false;
    public string LocalPeer { get; } = "local-" + Guid.NewGuid().ToString("N")[..10];
    public string? LocalName => null;
    public void Advertise(LobbySnapshot lobby) { }
    public void Withdraw() { }
    public string? Resolve(string code) => null;
    public bool BeginJoin(string token) => false;
    public void DismissJoin() { }
    public void Kick(string peer) { }
    public void Transfer(string peer) { }
    public string? HostHint => null;
    public void Send(string peer, byte[] frame, bool reliable) { }
    public void Close(string peer) { }
    public IReadOnlyList<TransportEvent> Drain() => [];
    public bool InviteOverlay(LobbySnapshot lobby) => false;
    public bool InviteFriend(string friendId, LobbySnapshot lobby) => false;
    public bool Invited(string peer) => false;
    public IReadOnlyList<FriendEntry> Friends() => [];
    public PeerLink? Link(string peer) => null;
    public void Dispose() { }
}

// NTP-style offset from ping/pong: keep the minimum-RTT sample of the last eight.
sealed class ClockSync
{
    readonly Queue<(long Rtt, long Offset)> samples = new();
    public void Add(long t0, long t1, long t2)
    {
        var rtt = t2 - t0;
        if (rtt < 0 || rtt > 10_000) return;
        samples.Enqueue((rtt, t1 - (t0 + t2) / 2));
        while (samples.Count > 8) samples.Dequeue();
    }
    public long Offset => samples.Count == 0 ? 0 : samples.MinBy(s => s.Rtt).Offset;
    public int? Rtt => samples.Count == 0 ? null : (int)samples.MinBy(s => s.Rtt).Rtt;
    public int Samples => samples.Count;
}
