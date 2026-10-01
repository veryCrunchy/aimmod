using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Steam pictures (SteamAvatars.cs): PNG checks, the cache, the disk copy, the
// route, the bridge contract and the view. Synthetic ids and generated pictures only.
static partial class MultiplayerChecks
{
    const string PersonA = "76561190000000101", PersonB = "76561190000000102", PersonC = "76561190000000103";

    // A small RGBA PNG with a deterministic pattern.
    static byte[] SyntheticPng(int width, int height, byte seed, int colorType = 6)
    {
        var channels = colorType == 6 ? 4 : 3;
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                raw.WriteByte((byte)(x * 4 + seed)); raw.WriteByte((byte)(y * 4)); raw.WriteByte((byte)(seed * 3 + x * y));
                if (channels == 4) raw.WriteByte(255);
            }
        }
        var z = new MemoryStream();
        using (var deflate = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(deflate);
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> be = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length); png.Write(be);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(body);
            var crc = 0xFFFFFFFFu;
            foreach (var b in body) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            BinaryPrimitives.WriteUInt32BigEndian(be, ~crc); png.Write(be);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = (byte)colorType;
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", []);
        return png.ToArray();
    }
    static string HashOf(byte seed) => seed.ToString("x2") + "0123456789abcd";

    static void AvatarChecks()
    {
        // PNG validation: what the bridge sends, nothing else.
        var good = SyntheticPng(64, 64, 7);
        Check(SteamAvatars.ValidPng(good, out var w, out var h) && w == 64 && h == 64 && SteamAvatars.ValidPng(SyntheticPng(32, 32, 1, colorType: 2), out _, out _), "Avatar PNGs up to 64x64, RGBA or RGB, are accepted");
        var badCrc = (byte[])good.Clone(); badCrc[40] ^= 1;
        var truncated = good[..(good.Length - 20)];
        Check(!SteamAvatars.ValidPng(badCrc, out _, out _) && !SteamAvatars.ValidPng(truncated, out _, out _) && !SteamAvatars.ValidPng(SyntheticPng(65, 64, 1), out _, out _)
            && !SteamAvatars.ValidPng(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), out _, out _) && !SteamAvatars.ValidPng(new byte[SteamAvatars.MaxPng + 1], out _, out _),
            "Damaged, truncated, oversized and non-PNG pictures are refused");
        var lying = SyntheticPng(16, 16, 3); BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(16), 20); // IHDR width without fixing the CRC
        Check(!SteamAvatars.ValidPng(lying, out _, out _), "A header that disagrees with its CRC is refused");

        // The cache: seen ids only, one request at a time, refresh, retry and change.
        long now = 10_000_000;
        var cache = new SteamAvatars(null, () => now);
        Check(!cache.Seen("dev-sim-1") && !cache.Seen("p1") && !cache.Seen(null) && cache.Seen(PersonA), "Only SteamID64s can have a Steam picture (simulated players keep initials)");
        Check(cache.Url(PersonA) is null && cache.Due().SequenceEqual([(PersonA, (string?)null)]) && cache.Due().Count == 0, "A newly seen id is asked for once, without a hash");
        Check(!cache.Received(PersonB, HashOf(1), good, false) && cache.Url(PersonB) is null, "Pictures nobody asked for are dropped");
        Check(!cache.Received(PersonA, HashOf(2), badCrc, false) && cache.Url(PersonA) is null, "A damaged picture is not stored");
        now += 31_000;
        Check(cache.Due().Count == 0, "After a refused picture the id waits before asking again");
        now += 10 * 60_000;
        Check(cache.Due().Single().Id == PersonA, "and asks again later");
        Check(cache.Received(PersonA, HashOf(2), good, false) && cache.Url(PersonA) == "/avatar/" + PersonA + ".png?v=" + HashOf(2)[..8], "A picture gives a versioned link");
        Check(cache.Get(PersonA)?.Png.SequenceEqual(good) == true && cache.File(PersonA + ".png")?.Hash == HashOf(2) && cache.File(PersonA) is null && cache.File("../" + PersonA + ".png") is null, "The route finds pictures by <id>.png only");
        now += 6 * 3_600_000L;
        cache.Seen(PersonA);
        var refresh = cache.Due();
        Check(refresh.Count == 1 && refresh[0].Have == HashOf(2), "Old pictures are re-checked with the hash held");
        Check(cache.Received(PersonA, HashOf(2), null, true) && cache.Url(PersonA) is not null && cache.Due().Count == 0, "An unchanged answer keeps the picture");
        cache.Changed(PersonA);
        Check(cache.Due().Single().Have == HashOf(2), "A persona change asks again right away");
        Check(cache.Received(PersonA, HashOf(3), SyntheticPng(64, 64, 9), false) && cache.Url(PersonA)!.EndsWith("?v=" + HashOf(3)[..8], StringComparison.Ordinal), "A new picture replaces the old and changes the link");
        cache.Seen(PersonC); cache.Due();
        Check(!cache.Received(PersonC, null, null, false) && cache.Url(PersonC) is null, "Someone without a picture stays on initials");
        now += 30 * 60_000L;
        Check(cache.Url(PersonA) is null && cache.Get(PersonA) is null, "Ids not seen for 30 minutes are no longer served");
        // At most six requests wait for the bridge at once.
        var busy = new SteamAvatars(null, () => now);
        for (var i = 0; i < 10; i++) busy.Seen("765611900000002" + i.ToString("00"));
        Check(busy.Due(10).Count == 6 && busy.Due(10).Count == 0, "Requests to the bridge are capped");

        // On disk: <hash>.png plus index.json, reloaded but only served once seen again; pruned.
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-avatar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new SteamAvatars(folder, () => now);
            disk.Seen(PersonA); disk.Seen(PersonB); disk.Due();
            disk.Received(PersonA, HashOf(4), good, false);
            disk.Received(PersonB, HashOf(5), SyntheticPng(64, 64, 5), false);
            Check(File.Exists(Path.Combine(folder, HashOf(4) + ".png")) && File.Exists(Path.Combine(folder, "index.json")), "Pictures are kept on disk by hash");
            disk.Changed(PersonB); disk.Due();
            disk.Received(PersonB, HashOf(6), SyntheticPng(64, 64, 6), false);
            Check(!File.Exists(Path.Combine(folder, HashOf(5) + ".png")) && File.Exists(Path.Combine(folder, HashOf(6) + ".png")), "A replaced picture's file is pruned");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "kept");
            var reload = new SteamAvatars(folder, () => now);
            Check(reload.Url(PersonA) is null, "After a restart nothing is served until the id is seen again");
            reload.Seen(PersonA);
            Check(reload.Url(PersonA)!.EndsWith(HashOf(4)[..8], StringComparison.Ordinal) && reload.Due().Count == 0, "A seen id is served from disk without asking Steam");
            Check(File.Exists(Path.Combine(folder, "notes.txt")), "Pruning touches only picture files");
            now += 31L * 24 * 3_600_000L;
            var stale = new SteamAvatars(folder, () => now);
            stale.Seen(PersonA);
            Check(stale.Url(PersonA) is null && stale.Due().Single().Have is null, "Pictures older than 30 days are fetched again");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        AvatarRoute(good);
        AvatarPipe(good);
        AvatarService(good);
    }

    static void AvatarRoute(byte[] good)
    {
        long now = 20_000_000;
        var cache = new SteamAvatars(null, () => now);
        cache.Seen(PersonA); cache.Seen(PersonB); cache.Due();
        cache.Received(PersonA, HashOf(8), good, false);
        var app = LoopbackServer.Build(0);
        const string prefix = "/synthetic0capability0token";
        SteamAvatars.Map(app, prefix, cache);
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            var address = LoopbackServer.VerifiedAddress(app);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            HttpResponseMessage Get(string path, string? etag = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, address + prefix + path);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return client.Send(request);
            }
            using var ok = Get("/avatar/" + PersonA + ".png?v=" + HashOf(8)[..8]);
            var body = ok.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Check(ok.StatusCode == HttpStatusCode.OK && ok.Content.Headers.ContentType?.MediaType == "image/png" && body.SequenceEqual(good), "The route serves the picture as image/png");
            Check(ok.Headers.CacheControl is { Private: true, MaxAge: { } age } && age >= TimeSpan.FromHours(1) && ok.Headers.ETag?.Tag == "\"" + HashOf(8) + "\""
                && ok.Headers.TryGetValues("X-Content-Type-Options", out var sniff) && sniff.Single() == "nosniff", "with private caching, an ETag and nosniff");
            using var again = Get("/avatar/" + PersonA + ".png", "\"" + HashOf(8) + "\"");
            Check(again.StatusCode == HttpStatusCode.NotModified, "A matching ETag gets 304");
            using var other = Get("/avatar/" + PersonA + ".png", "\"" + HashOf(9) + "\"");
            Check(other.StatusCode == HttpStatusCode.OK, "A stale ETag gets the picture");
            foreach (var path in new[] { "/avatar/" + PersonB + ".png", "/avatar/" + PersonC + ".png", "/avatar/dev-sim-1.png", "/avatar/" + PersonA, "/avatar/..%2F" + PersonA + ".png" })
            {
                using var miss = Get(path);
                Check(miss.StatusCode == HttpStatusCode.NotFound && (path.Contains("..") || miss.Headers.CacheControl?.NoStore == true), "No picture for " + path.Replace(PersonA, "<id>").Replace(PersonB, "<id>").Replace(PersonC, "<id>"));
            }
        }
        finally { app.StopAsync().GetAwaiter().GetResult(); ((IAsyncDisposable)app).DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    // The bridge contract: avatar.get {peer, format:"png", have?} and the avatar / persona events.
    static void AvatarPipe(byte[] good)
    {
        var name = "aimmod-steam-avatar-test-" + Guid.NewGuid().ToString("N")[..10];
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 1 << 17, 1 << 17);
        using var steam = new SteamTransport(name);
        Check(server.WaitForConnectionAsync().Wait(5000), "The transport connects to the bridge pipe (avatars)");
        JsonElement Read()
        {
            var header = new byte[4];
            if (!server.ReadExactlyAsync(header).AsTask().Wait(5000)) throw new Exception("Multiplayer check failed: bridge read timed out");
            var body = new byte[BitConverter.ToUInt32(header)];
            server.ReadExactlyAsync(body).AsTask().Wait(5000);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        JsonElement Expect(string cmd) { for (var i = 0; i < 6; i++) { var c = Read(); if (c.GetProperty("cmd").GetString() == cmd) return c; } throw new Exception("Multiplayer check failed: expected " + cmd); }
        void Write(object ev) { var bytes = JsonSerializer.SerializeToUtf8Bytes(ev); server.Write(BitConverter.GetBytes((uint)bytes.Length)); server.Write(bytes); server.Flush(); }
        bool Until(Func<bool> condition) { for (var i = 0; i < 100; i++) { if (condition()) return true; Thread.Sleep(20); } return false; }
        Expect("hello");
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = PersonA, name = "Synthetic Self", initials = "SS" }, relay = "Current", features = new[] { "lobby", "p2p" } });
        Check(Until(() => steam.Available) && !steam.RequestAvatar(PersonB, null), "Without the avatar feature no pictures are asked for");
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = PersonA, name = "Synthetic Self", initials = "SS" }, relay = "Current", features = new[] { "lobby", "p2p", "avatar" } });
        Check(Until(() => steam.RequestAvatar(PersonB, HashOf(1))), "With it the transport asks");
        var ask = Expect("avatar.get");
        Check(ask.GetProperty("peer").GetString() == PersonB && ask.GetProperty("format").GetString() == "png" && ask.GetProperty("have").GetString() == HashOf(1) && ask.TryGetProperty("id", out _), "avatar.get carries peer, format png and the held hash");
        Check(!steam.RequestAvatar("dev-sim-1", null) && steam.RequestAvatar(PersonC, "NOT-HEX") && !Expect("avatar.get").TryGetProperty("have", out _), "Malformed ids are never sent, and a malformed hash is left out");
        Write(new { v = 1, ev = "friends", friends = new object[] { new { peer = PersonC, name = "\u0001", initials = "?", state = "online", playing = true, aimmod = false } } });
        Write(new { v = 1, ev = "avatar", peer = PersonB, format = "png", w = 64, h = 64, hash = HashOf(2), png = Convert.ToBase64String(good) });
        Write(new { v = 1, ev = "avatar", peer = PersonB, format = "png", hash = HashOf(2), unchanged = true });
        Write(new { v = 1, ev = "avatar", peer = PersonC, format = "png", missing = true, reason = "none" });
        Write(new { v = 1, ev = "avatar", peer = PersonC, format = "png", hash = HashOf(3), png = "%%%not-base64%%%" });
        Write(new { v = 1, ev = "avatar", peer = PersonB, w = 32, h = 32, rgba = "AAAA" }); // the old 32x32 RGBA answer is ignored here
        Write(new { v = 1, ev = "persona", peer = PersonC, name = "Synthetic Renamed", initials = "SR", avatar = true });
        var events = new List<TransportEvent>();
        Check(Until(() => { events.AddRange(steam.Drain()); return events.Count(e => e.Kind is TransportEvent.Avatar or TransportEvent.Persona) >= 5; }), "Avatar and persona events arrive");
        var pictures = events.Where(e => e.Kind == TransportEvent.Avatar).ToArray();
        Check(pictures.Length == 4 && pictures[0].Peer == PersonB && pictures[0].Picture!.Png!.SequenceEqual(good) && pictures[0].Picture!.Hash == HashOf(2)
            && pictures[1].Picture is { Unchanged: true, Png: null } && pictures[2].Picture is null && pictures[3].Picture is null, "PNG, unchanged and missing answers map to events; bad base64 counts as missing");
        var persona = events.Single(e => e.Kind == TransportEvent.Persona);
        Check(persona.Peer == PersonC && persona.Host && Until(() => steam.Friends().Any(f => f.Id == PersonC && f.Name == "Synthetic Renamed")), "A persona event fixes a friend's placeholder name and flags a new picture");
    }

    // Two machines in one lobby each ask their own Steam for the other's picture; the view and
    // the notice layer carry the links; simulated players keep initials.
    static void AvatarService(byte[] good)
    {
        long now = 30_000_000;
        var net = new MemoryNetwork();
        var all = new List<(MultiplayerService Service, MemoryTransport Transport)>();
        (MultiplayerService, MemoryTransport) Make(string id)
        {
            var t = new MemoryTransport(net, id) { FriendList = [new(PersonC, "Synthetic Friend", "aimmod", null, null, false)] }; net.Peers[id] = t;
            var service = new MultiplayerService(t, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, () => now, autoTick: false);
            all.Add((service, t)); return (service, t);
        }
        var (a, ta) = Make(PersonA); var (b, tb) = Make(PersonB);
        void Pump(int n = 6) { for (var i = 0; i < n; i++) { now += 200; foreach (var (s, _) in all) s.Tick(); } }
        Check(a.Act("create", J(new { mode = "practice" })).Ok && b.Act("join", J(new { code = net.Codes.Single().Key })).Ok, "Two machines share a lobby (avatars)");
        Pump(); now += 1100; Pump();
        Check(ta.AvatarAsks.Any(x => x.Peer == PersonB) && tb.AvatarAsks.Any(x => x.Peer == PersonA) && ta.AvatarAsks.Any(x => x.Peer == PersonC) && ta.AvatarAsks.Any(x => x.Peer == PersonA),
            "Each machine asks its own Steam for every member's and friend's picture");
        Check(ta.AvatarAsks.Count(x => x.Peer == PersonB) == 1, "and asks once while it waits");
        JsonElement View(MultiplayerService s) => JsonSerializer.SerializeToElement(s.View(), Protocol.Json);
        Check(View(a).GetProperty("lobby").GetProperty("avatars").EnumerateObject().Count() == 0 && View(a).GetProperty("friends").GetProperty("items")[0].GetProperty("avatar").ValueKind == JsonValueKind.Null, "Initials until a picture arrives");
        ta.Inbox.Enqueue(new TransportEvent(PersonB, TransportEvent.Avatar, Picture: new AvatarPicture(HashOf(1), good, false)));
        ta.Inbox.Enqueue(new TransportEvent(PersonC, TransportEvent.Avatar, Picture: new AvatarPicture(HashOf(2), SyntheticPng(64, 64, 2), false)));
        Pump(1);
        var lobby = View(a).GetProperty("lobby");
        Check(lobby.GetProperty("avatars").GetProperty(PersonB).GetString() == "/avatar/" + PersonB + ".png?v=" + HashOf(1)[..8] && !lobby.GetProperty("avatars").TryGetProperty(PersonA, out _), "Members with a picture get a link in the lobby view");
        Check(View(a).GetProperty("friends").GetProperty("items")[0].GetProperty("avatar").GetString()!.StartsWith("/avatar/" + PersonC, StringComparison.Ordinal), "Friends get a link too");
        Check(lobby.GetProperty("members").EnumerateArray().All(m => m.GetProperty("avatar").GetString() is { } look && !look.Contains('/')), "Lobby members (also sent to peers) keep only their look, never a picture link");
        // Invites and watch requests: the notice layer shows the sender's picture.
        ta.Inbox.Enqueue(new TransportEvent(PersonC, TransportEvent.InviteReceived, Invite: new IncomingInvite("i-av", "Synthetic Friend", "incoming", "token", null, now, From: PersonC)));
        Pump(1);
        var notice = JsonDocument.Parse(a.NoticeText()).RootElement;
        Check(notice.GetProperty("kind").GetString() == "invite" && notice.GetProperty("person").GetProperty("avatar").GetString()!.StartsWith("/avatar/" + PersonC, StringComparison.Ordinal)
            && notice.GetProperty("person").GetProperty("name").GetString() == "Synthetic Friend", "An invite notice carries the sender's name and picture");
        Check(View(a).GetProperty("invites")[0].GetProperty("avatar").GetString()!.StartsWith("/avatar/" + PersonC, StringComparison.Ordinal) && !notice.GetRawText().Contains("\"peer\""), "So does the invite in the panel, without the raw id in the notice");
        // A friend without a picture yet still gets the person block, for the initials.
        ta.Inbox.Enqueue(new TransportEvent(PersonB, TransportEvent.InviteReceived, Invite: new IncomingInvite("i-av2", "Second Friend", "incoming", "token2", null, now + 1, From: "76561190000000104")));
        a.Act("decline-invite", J(new { id = "i-av" }));
        Pump(1);
        var initialsOnly = JsonDocument.Parse(a.NoticeText()).RootElement.GetProperty("person");
        Check(initialsOnly.GetProperty("name").GetString() == "Second Friend" && initialsOnly.GetProperty("avatar").ValueKind == JsonValueKind.Null, "Until the picture arrives the notice has the name only");
        // Simulated members never ask Steam.
        var simTransport = new MemoryTransport(net, "76561190000000199") { FriendList = [] };
        var sim = new MultiplayerService(simTransport, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, true, () => now, autoTick: false, seed: 3);
        Check(sim.DevLobby(3, LobbyModes.Race, simulatedHost: false).Ok, "A developer lobby with simulated players (avatars)");
        for (var i = 0; i < 12; i++) { now += 200; sim.Tick(); }
        var simLobby = JsonSerializer.SerializeToElement(sim.View(), Protocol.Json).GetProperty("lobby");
        var simulated = simLobby.GetProperty("members").EnumerateArray().Where(m => m.GetProperty("simulated").GetBoolean()).Select(m => m.GetProperty("id").GetString()!).ToArray();
        Check(simulated.Length == 3 && simulated.All(id => simTransport.AvatarAsks.All(x => x.Peer != id)) && simLobby.GetProperty("avatars").EnumerateObject().Count() == 0, "Simulated players keep initials and are never asked for");
    }
}
