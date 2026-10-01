using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Multiplayer;

// Steam profile pictures for the UI (bridge feature "avatar", multiplayer.md).
// Each machine asks its own Steam for the people it sees: lobby members (Steam
// sends pictures of non-friends who share a lobby), friends, spectators and
// whoever invites us. Pictures are 64x64 PNGs the bridge encodes, kept in
// memory and in <output>/steam-avatars as <hash>.png, and served at
// <prefix>/avatar/<id>.png only for ids seen in this session.
sealed class SteamAvatars
{
    public const int MaxPng = 40 * 1024, MaxEdge = 64;
    const long Refresh = 6 * 3_600_000L;      // re-ask Steam for a picture this old (the bridge answers "unchanged")
    const long RetryMissing = 10 * 60_000L;   // a user without a picture, or one Steam didn't send
    const long AnswerWait = 30_000;           // no answer from the bridge: ask again
    const long SeenFor = 30 * 60_000L;        // served for this long after the id was last seen
    const long KeepOnDisk = 30L * 24 * 3_600_000L;
    const int MaxEntries = 512, MaxOnDisk = 300, MaxAsking = 6;

    sealed class Entry
    {
        public string? Hash;
        public byte[]? Png;
        public long FetchedAt, SeenAt = long.MinValue / 2, AskedAt = long.MinValue / 2;
        public bool Asking, Missing;
    }

    readonly string? folder;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    bool loaded;

    public SteamAvatars(string? folder, Func<long>? clock = null)
    {
        this.folder = folder;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // A SteamID64 as the bridge writes it; simulated and local ids never match.
    public static bool ValidId(string? id) => id is { Length: >= 16 and <= 20 } && id.All(char.IsAsciiDigit);
    static bool ValidHash(string? hash) => hash is { Length: 16 } && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    // Someone the UI shows right now. Returns false for ids that can't have a Steam picture.
    public bool Seen(string? id)
    {
        if (!ValidId(id)) return false;
        lock (gate)
        {
            Load();
            if (!entries.TryGetValue(id!, out var e))
            {
                if (entries.Count >= MaxEntries) Evict();
                entries[id!] = e = new Entry();
            }
            e.SeenAt = clock();
            return true;
        }
    }

    // UI link for a seen id with a picture, versioned by its hash so the browser cache is safe.
    public string? Url(string? id)
    {
        if (!ValidId(id)) return null;
        lock (gate)
            return entries.TryGetValue(id!, out var e) && e.Png is not null && e.Hash is not null && clock() - e.SeenAt < SeenFor ? "/avatar/" + id + ".png?v=" + e.Hash[..8] : null;
    }

    // Ids to ask the bridge for now, with the hash already held. Marks them asked.
    public IReadOnlyList<(string Id, string? Have)> Due(int max = 4)
    {
        var now = clock();
        var due = new List<(string, string?)>();
        lock (gate)
        {
            var asking = entries.Values.Count(e => e.Asking && now - e.AskedAt < AnswerWait);
            foreach (var (id, e) in entries.OrderByDescending(kv => kv.Value.SeenAt))
            {
                if (due.Count >= max || asking >= MaxAsking) break;
                if (now - e.SeenAt >= SeenFor || (e.Asking && now - e.AskedAt < AnswerWait)) continue;
                var wanted = e.Png is null ? now - e.AskedAt >= (e.Missing ? RetryMissing : AnswerWait) : now - e.FetchedAt >= Refresh && now - e.AskedAt >= AnswerWait;
                if (!wanted) continue;
                e.Asking = true; e.AskedAt = now; asking++;
                due.Add((id, e.Png is null ? null : e.Hash));
            }
        }
        return due;
    }

    // The bridge's answer. png null with unchanged: the held picture is still current;
    // png null otherwise: no picture (or Steam didn't send one in time).
    public bool Received(string id, string? hash, byte[]? png, bool unchanged)
    {
        if (!ValidId(id)) return false;
        lock (gate)
        {
            Load();
            if (!entries.TryGetValue(id, out var e)) return false; // never asked for
            e.Asking = false;
            var now = clock();
            if (unchanged)
            {
                if (e.Png is null || e.Hash != hash) { e.AskedAt = long.MinValue / 2; return false; } // ask again, without a hash
                e.FetchedAt = now; e.Missing = false;
                SaveIndex();
                return true;
            }
            if (png is null || !ValidHash(hash) || !ValidPng(png, out _, out _)) { e.Missing = true; e.AskedAt = now; return false; }
            var changed = e.Hash != hash;
            e.Hash = hash; e.Png = png; e.FetchedAt = now; e.Missing = false;
            if (changed) Persist(hash!, png);
            return true;
        }
    }

    // Persona event with a new picture: fetch it again soon.
    public void Changed(string id)
    {
        lock (gate) if (entries.TryGetValue(id, out var e)) { e.FetchedAt = long.MinValue / 2; e.AskedAt = long.MinValue / 2; e.Missing = false; e.Asking = false; }
    }

    public (byte[] Png, string Hash)? Get(string? id)
    {
        if (!ValidId(id)) return null;
        lock (gate)
            return entries.TryGetValue(id!, out var e) && e.Png is not null && e.Hash is not null && clock() - e.SeenAt < SeenFor ? (e.Png, e.Hash) : null;
    }

    // "<id>.png" from the route.
    public (byte[] Png, string Hash)? File(string? name) =>
        name is { Length: > 4 } && name.EndsWith(".png", StringComparison.Ordinal) ? Get(name[..^4]) : null;

    public static void Map(IEndpointRouteBuilder routes, string prefix, SteamAvatars avatars)
    {
        routes.MapGet(prefix + "/avatar/{name}", (string name, HttpContext http) =>
        {
            var headers = http.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            if (avatars.File(name) is not { } hit) { headers.CacheControl = "no-store"; return Results.NotFound(); }
            var etag = "\"" + hit.Hash + "\"";
            headers.CacheControl = "private, max-age=86400";
            headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag || v == "*")) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(hit.Png, "image/png");
        });
    }

    // Signature, an RGBA or RGB IHDR of at most 64x64, intact chunk CRCs and an IDAT that
    // inflates to exactly the scanlines the header describes.
    public static bool ValidPng(byte[] png, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length is < 45 or > MaxPng || !png.AsSpan(0, 8).SequenceEqual(signature)) return false;
        var idat = new MemoryStream();
        int channels = 0; var ended = false;
        for (var i = 8; i + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(i));
            if (length > (uint)(png.Length - i - 12)) return false;
            var len = (int)length;
            var type = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
            if (BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(i + 8 + len)) != Crc32(png.AsSpan(i + 4, len + 4))) return false;
            var data = png.AsSpan(i + 8, len);
            if (type == "IHDR")
            {
                if (i != 8 || len != 13) return false;
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data); height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                channels = data[9] switch { 6 => 4, 2 => 3, _ => 0 };
                if (data[8] != 8 || channels == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0 || width is < 1 or > MaxEdge || height is < 1 or > MaxEdge) return false;
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") { ended = true; break; }
            i += 12 + len;
        }
        if (!ended || channels == 0 || idat.Length == 0) return false;
        var expected = height * (1 + width * channels);
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new byte[expected + 1];
            var read = 0;
            while (read < raw.Length) { var n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
            if (read != expected) return false;
            for (var y = 0; y < height; y++) if (raw[y * (1 + width * channels)] > 4) return false;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => { var c = (uint)n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    // ---- disk -----------------------------------------------------------

    string? IndexPath => folder is null ? null : Path.Combine(folder, "index.json");

    void Load()
    {
        if (loaded) return;
        loaded = true;
        if (IndexPath is not { } path || !System.IO.File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) return;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var now = clock();
            foreach (var p in doc.RootElement.EnumerateObject().Take(MaxOnDisk))
            {
                if (!ValidId(p.Name) || p.Value.ValueKind != JsonValueKind.Object) continue;
                var hash = p.Value.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                var at = p.Value.TryGetProperty("at", out var a) && a.TryGetInt64(out var t) ? t : 0;
                if (!ValidHash(hash) || now - at > KeepOnDisk) continue;
                var file = Path.Combine(folder!, hash + ".png");
                if (!System.IO.File.Exists(file) || new FileInfo(file).Length > MaxPng) continue;
                var png = System.IO.File.ReadAllBytes(file);
                if (!ValidPng(png, out _, out _)) continue;
                entries[p.Name] = new Entry { Hash = hash, Png = png, FetchedAt = at };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    void Persist(string hash, byte[] png)
    {
        if (folder is null) return;
        try
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, hash + ".png");
            if (!System.IO.File.Exists(file)) AtomicFile.WriteBytes(file, png);
            SaveIndex();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // index.json maps id -> {hash, at}; the newest MaxOnDisk are kept, and pictures
    // nothing points at any more are deleted.
    void SaveIndex()
    {
        if (folder is null || IndexPath is not { } path) return;
        try
        {
            var now = clock();
            var keep = entries.Where(kv => kv.Value.Png is not null && ValidHash(kv.Value.Hash) && now - kv.Value.FetchedAt <= KeepOnDisk)
                .OrderByDescending(kv => kv.Value.FetchedAt).Take(MaxOnDisk).ToDictionary(kv => kv.Key, kv => new { hash = kv.Value.Hash, at = kv.Value.FetchedAt });
            Directory.CreateDirectory(folder);
            AtomicFile.WriteText(path, JsonSerializer.Serialize(keep));
            var used = keep.Values.Select(v => v.hash + ".png").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
            {
                var name = Path.GetFileName(file);
                if (name.Length == 20 && ValidHash(name[..16]) && !used.Contains(name)) System.IO.File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Too many ids: drop the ones seen longest ago (their files stay until the next prune).
    void Evict()
    {
        foreach (var id in entries.OrderBy(kv => kv.Value.SeenAt).Take(entries.Count - MaxEntries + 1).Select(kv => kv.Key).ToArray()) entries.Remove(id);
    }

    internal int Count { get { lock (gate) return entries.Count; } }
}
