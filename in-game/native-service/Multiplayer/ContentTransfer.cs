using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Lobby content download: a player missing the scenario, map or profiles gets
// them from the Steam Workshop when the lobby names a Workshop item, otherwise
// from the host over the P2P link. The host serves only files that are part of
// the current lobby content. Transfers are Brotli-packed, chunked, resumable
// and rate-limited; the receiver verifies sizes and SHA-256 before installing,
// and never replaces a file it already has with different content.

// Kind decides the install folder: scenario, map, ability, weapon or character.
sealed record ContentFile(string Kind, string Name, long Size, string Hash, long Packed);
sealed record ContentManifest(string Key, IReadOnlyList<ContentFile> Files, string? Workshop)
{
    public long Size => Files.Sum(f => f.Size);
}

static class ContentRules
{
    public const long MaxFile = 200L << 20, MaxTotal = 200L << 20;
    public const int MaxFiles = 16, ChunkBytes = 8192;
    static readonly Dictionary<string, string[]> Extensions = new()
    {
        ["scenario"] = [".sce"],
        ["map"] = [".json", ".map"],
        ["ability"] = [".abilmov", ".abilwep", ".abilmelee", ".abilrecall", ".abilsprint"],
        ["weapon"] = [".wep"],
        ["character"] = [".chr"],
    };
    public static IEnumerable<string> AbilityExtensions => Extensions["ability"];
    // A bare file name in an allowed folder with an allowed extension: no separators, no traversal.
    public static bool SafeName(string kind, string? name)
    {
        if (!Extensions.TryGetValue(kind, out var allowed) || string.IsNullOrWhiteSpace(name) || name.Length > 160) return false;
        if (name != name.Trim() || name.StartsWith('.') || name.Contains("..") || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl)) return false;
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) return false;
        return allowed.Contains(Path.GetExtension(name).ToLowerInvariant());
    }
    public static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    public static bool Valid(ContentManifest m) =>
        m.Files.Count is > 0 and <= MaxFiles && m.Size <= MaxTotal && (m.Workshop is null || m.Workshop is { Length: > 0 and <= 20 } && m.Workshop.All(char.IsAsciiDigit))
        && m.Files.All(f => SafeName(f.Kind, f.Name) && Hash(f.Hash) && f.Size is >= 0 and <= MaxFile && f.Packed is >= 0 and <= MaxFile + 65536)
        && m.Files.Select(f => f.Kind + "/" + f.Name.ToLowerInvariant()).Distinct().Count() == m.Files.Count;
    public static string Folder(string root, string kind) => kind switch
    {
        "scenario" => Path.Combine(root, "Saved", "SaveGames", "Scenarios"),
        "map" => Path.Combine(root, "maps"),
        "ability" => Path.Combine(root, "Saved", "SaveGames", "Abilities"),
        "weapon" => Path.Combine(root, "Saved", "SaveGames", "Weapons"),
        "character" => Path.Combine(root, "Saved", "SaveGames", "Characters"),
        _ => throw new ArgumentException("Unknown content kind."),
    };
    public static byte[] Pack(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) brotli.Write(raw);
        return output.ToArray();
    }
    public static byte[]? Unpack(Stream packed, long expected)
    {
        using var brotli = new BrotliStream(packed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = brotli.Read(buffer)) > 0) { raw.Write(buffer, 0, read); if (raw.Length > expected) return null; }
        return raw.Length == expected ? raw.ToArray() : null;
    }
}

// Host side: answers manifest and chunk requests from lobby members.
sealed class ContentServer(ContentLibrary library, Func<long> clock)
{
    sealed record Pending(string Peer, string Hash, long Offset, long End);
    readonly Dictionary<string, (string Path, ContentFile File, byte[]? Packed)> files = new();
    readonly List<Pending> queue = [];
    string? key;
    ContentManifest? manifest;
    double budget; long last;
    public long RateLive { get; set; } = 256 * 1024;
    public long RateIdle { get; set; } = 4L << 20;

    public ContentManifest? Manifest(LobbySettings settings)
    {
        var k = string.Join('|', settings.Scenario?.Hash, settings.MapOverride?.Hash, settings.WeaponProfile.Hash, settings.CharacterProfile.Hash);
        if (k == key) return manifest;
        key = k; files.Clear(); queue.Clear();
        var list = new List<ContentFile>();
        foreach (var (kind, path) in library.ContentFiles(settings))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > ContentRules.MaxFile) continue;
                var name = info.Name;
                if (!ContentRules.SafeName(kind, name)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (files.ContainsKey(hash)) continue;
                var file = new ContentFile(kind, name, info.Length, hash, -1);
                files[hash] = (path, file, null);
                list.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Packed sizes are known once each file has been packed, so pack them now (bounded by MaxTotal).
        list = list.Select(f => f with { Packed = PackedOf(f.Hash)?.Length ?? -1 }).Where(f => f.Packed >= 0).ToList();
        manifest = list.Count == 0 || list.Sum(f => f.Size) > ContentRules.MaxTotal ? null
            : new ContentManifest(k, list.Take(ContentRules.MaxFiles).ToArray(), library.Scenarios.FirstOrDefault(s => s.Hash == settings.Scenario?.Hash)?.WorkshopId);
        return manifest;
    }

    byte[]? PackedOf(string hash)
    {
        if (!files.TryGetValue(hash, out var entry)) return null;
        if (entry.Packed is not null) return entry.Packed;
        try
        {
            var raw = File.ReadAllBytes(entry.Path);
            if (Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() != hash) return null;
            var packed = ContentRules.Pack(raw);
            files[hash] = entry with { Packed = packed };
            return packed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // A member asks for part of one file; only current lobby content is served.
    public string? Request(string peer, LobbySettings settings, string hash, long offset, long length)
    {
        if (Manifest(settings) is not { } m || m.Files.All(f => f.Hash != hash)) return "not-offered";
        var packed = PackedOf(hash);
        if (packed is null) return "unavailable";
        if (offset < 0 || offset > packed.Length || length <= 0) return "invalid";
        // A newer request for the same file replaces the older one (the receiver resumed).
        queue.RemoveAll(p => p.Peer == peer && p.Hash == hash);
        queue.Add(new Pending(peer, hash, offset, Math.Min(packed.Length, offset + Math.Min(length, 512 * 1024))));
        return null;
    }
    public void Forget(string peer) => queue.RemoveAll(p => p.Peer == peer);

    // Sends chunks within the rate budget; live matches get a small share so score frames keep flowing.
    public void Pump(bool matchLive, Action<string, object> send)
    {
        var now = clock();
        var rate = matchLive ? RateLive : RateIdle;
        budget = Math.Min(rate / 2.0, budget + (last == 0 ? rate / 10.0 : (now - last) * rate / 1000.0));
        last = now;
        while (budget >= ContentRules.ChunkBytes && queue.Count > 0)
        {
            var p = queue[0]; queue.RemoveAt(0);
            var packed = PackedOf(p.Hash);
            if (packed is null || p.Offset >= p.End) continue;
            var length = (int)Math.Min(ContentRules.ChunkBytes, p.End - p.Offset);
            send(p.Peer, new { hash = p.Hash, offset = p.Offset, total = packed.Length, data = Convert.ToBase64String(packed, (int)p.Offset, length) });
            budget -= length;
            if (p.Offset + length < p.End) queue.Insert(0, p with { Offset = p.Offset + length });
        }
    }
}

// Receiver side: one download at a time for the current lobby content.
sealed class ContentDownload(string root, string temp, Func<long> clock)
{
    public const long Window = 256 * 1024;
    public sealed record FileProgress(string Kind, string Name, long Size, long Done, string State);
    public string State { get; private set; } = "idle";        // idle, manifest, ready, downloading, verifying, installing, done, error, cancelled
    public string Source { get; set; } = "host";                // host or workshop
    public string? Error { get; private set; }
    public string? Code { get; private set; }
    public ContentManifest? Manifest { get; private set; }
    readonly Dictionary<string, long> received = new();
    readonly Queue<(long At, long Bytes)> samples = new();
    string? current; long requestedTo, lastChunkAt, requestAt;
    public Func<long>? FreeSpace { get; set; }

    public long Done => Manifest?.Files.Sum(f => Math.Min(f.Packed, received.GetValueOrDefault(f.Hash))) ?? 0;
    public long Total => Manifest?.Files.Sum(f => f.Packed) ?? 0;
    public double Speed { get { var now = clock(); while (samples.Count > 0 && now - samples.Peek().At > 3000) samples.Dequeue(); return samples.Sum(s => s.Bytes) / 3.0; } }
    public IReadOnlyList<FileProgress> Files => Manifest?.Files.Select(f => new FileProgress(f.Kind, f.Name, f.Size, Math.Min(f.Packed, received.GetValueOrDefault(f.Hash)),
        received.GetValueOrDefault(f.Hash) >= f.Packed ? "done" : f.Hash == current ? "downloading" : "waiting")).ToArray() ?? [];

    void Fail(string code, string message) { State = "error"; Code = code; Error = message; current = null; }
    string Part(string hash) => Path.Combine(temp, hash + ".part");

    public void Reset() { State = "idle"; Manifest = null; Error = null; Code = null; received.Clear(); current = null; samples.Clear(); }
    public void Asked() { if (State is "idle" or "error" or "cancelled") { State = "manifest"; requestAt = clock(); Error = null; Code = null; } }

    // A manifest from the host: validate it and check what is already here or would conflict.
    public void Offer(ContentManifest manifest)
    {
        if (!ContentRules.Valid(manifest)) { Fail("invalid", "The host offered files AimMod won’t accept."); return; }
        if (Manifest?.Key != manifest.Key) { received.Clear(); current = null; }
        Manifest = manifest;
        Directory.CreateDirectory(temp);
        foreach (var f in manifest.Files)
        {
            var part = Part(f.Hash);
            received[f.Hash] = File.Exists(part) ? Math.Min(new FileInfo(part).Length, f.Packed) : 0;
        }
        if (State is "manifest" or "idle") State = "ready";
    }

    // Files whose names are taken by different content are never replaced.
    public IReadOnlyList<ContentFile> Conflicts()
    {
        var list = new List<ContentFile>();
        if (Manifest is null) return list;
        foreach (var f in Manifest.Files)
        {
            var target = Path.Combine(ContentRules.Folder(root, f.Kind), f.Name);
            if (!File.Exists(target)) continue;
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != f.Hash) list.Add(f);
        }
        return list;
    }

    public bool Start()
    {
        if (Manifest is null) return false;
        var conflicts = Conflicts();
        if (conflicts.Count > 0) { Fail("conflict", "You already have a different “" + conflicts[0].Name + "”. AimMod won’t replace it. Rename or move your copy, then try again."); return false; }
        var need = Manifest.Files.Sum(f => f.Size + f.Packed) + (16L << 20);
        var free = FreeSpace?.Invoke() ?? FreeBytes(root);
        if (free >= 0 && free < need) { Fail("disk", "Not enough disk space: the download needs " + Megabytes(need) + " and " + Megabytes(free) + " is free."); return false; }
        State = "downloading"; Error = null; Code = null; current = null; lastChunkAt = clock();
        return true;
    }
    public void Cancel() { if (State is "downloading" or "manifest" or "ready") { State = "cancelled"; current = null; } }
    public void HostGone() { if (State is "downloading" or "manifest") Fail("host-left", "The host left during the download. Retry to continue from the new host, if they have the files."); }
    static long FreeBytes(string path) { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return -1; } }
    public static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 << 20 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    // Next window request, or null. Re-requests from the current offset after a stall (resume).
    public (string Hash, long Offset, long Length)? Next()
    {
        if (State != "downloading" || Manifest is null) return null;
        var now = clock();
        var file = Manifest.Files.FirstOrDefault(f => received.GetValueOrDefault(f.Hash) < f.Packed);
        if (file is null) { Finish(); return null; }
        var have = received.GetValueOrDefault(file.Hash);
        if (current != file.Hash) { current = file.Hash; requestedTo = have; }
        var stalled = now - lastChunkAt > 8000;
        if (stalled) { requestedTo = have; lastChunkAt = now; }
        if (requestedTo - have > Window / 2 && !stalled) return null;
        var from = Math.Max(have, requestedTo);
        if (from >= file.Packed) return null;
        requestedTo = Math.Min(file.Packed, from + Window);
        return (file.Hash, from, requestedTo - from);
    }

    // A chunk from the host: only the expected file at the expected offset is written.
    public void Chunk(string hash, long offset, long total, byte[] data)
    {
        if (State != "downloading" || Manifest?.Files.FirstOrDefault(f => f.Hash == hash) is not { } file) return;
        if (total != file.Packed || data.Length is 0 or > ContentRules.ChunkBytes || offset < 0 || offset + data.Length > file.Packed) { Fail("invalid", "The host sent a bad chunk."); return; }
        var have = received.GetValueOrDefault(hash);
        if (offset != have) return;
        using (var stream = new FileStream(Part(hash), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) { stream.Seek(offset, SeekOrigin.Begin); stream.Write(data); stream.SetLength(offset + data.Length); }
        received[hash] = offset + data.Length;
        lastChunkAt = clock(); samples.Enqueue((lastChunkAt, data.Length));
        if (Manifest.Files.All(f => received.GetValueOrDefault(f.Hash) >= f.Packed)) Finish();
    }
    public void Refused(string hash, string code)
    {
        if (State != "downloading") return;
        Fail(code == "not-offered" ? "changed" : code, code == "not-offered" ? "The lobby’s content changed. Download again." : "The host couldn’t send “" + (Manifest?.Files.FirstOrDefault(f => f.Hash == hash)?.Name ?? "a file") + "”.");
    }

    // Verify every file against the manifest, then move them into the game folders.
    void Finish()
    {
        if (Manifest is null) return;
        State = "verifying";
        var ready = new List<(ContentFile File, byte[] Raw)>();
        foreach (var f in Manifest.Files)
        {
            byte[]? raw;
            try { using var part = File.OpenRead(Part(f.Hash)); raw = ContentRules.Unpack(part, f.Size); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { raw = null; }
            if (raw is null || Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() != f.Hash)
            {
                try { File.Delete(Part(f.Hash)); } catch (IOException) { }
                received[f.Hash] = 0;
                Fail("hash", "“" + f.Name + "” didn’t match the lobby’s copy, so it was discarded. Retry to download it again.");
                return;
            }
            ready.Add((f, raw));
        }
        State = "installing";
        try
        {
            foreach (var (f, raw) in ready)
            {
                var folder = ContentRules.Folder(root, f.Kind);
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, f.Name);
                if (Path.GetDirectoryName(Path.GetFullPath(target)) != Path.GetFullPath(folder)) { Fail("invalid", "Refused an unsafe file name."); return; }
                if (File.Exists(target))
                {
                    using (var existing = File.OpenRead(target)) if (Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() == f.Hash) continue;
                    Fail("conflict", "You already have a different “" + f.Name + "”. AimMod won’t replace it."); return;
                }
                var staging = Path.Combine(temp, f.Hash + ".raw");
                File.WriteAllBytes(staging, raw);
                File.Move(staging, target, overwrite: false);
            }
            foreach (var f in Manifest.Files) try { File.Delete(Part(f.Hash)); } catch (IOException) { }
            State = "done"; current = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail("install", "Couldn’t install the files (" + ex.GetType().Name + ")."); }
    }

    public object View() => new
    {
        state = State, source = Source, error = Error, code = Code,
        total = Manifest?.Size ?? 0, packed = Total, done = Done, speed = Math.Round(Speed),
        files = Files.Select(f => new { f.Kind, f.Name, f.Size, f.Done, f.State }),
    };

    public static ContentManifest? ReadManifest(JsonElement body)
    {
        try
        {
            var files = body.GetProperty("files").EnumerateArray().Take(ContentRules.MaxFiles + 1).Select(f => new ContentFile(f.GetProperty("kind").GetString() ?? "", f.GetProperty("name").GetString() ?? "",
                f.GetProperty("size").GetInt64(), f.GetProperty("hash").GetString() ?? "", f.GetProperty("packed").GetInt64())).ToArray();
            var workshop = body.TryGetProperty("workshop", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            return new ContentManifest(body.GetProperty("key").GetString() ?? "", files, workshop);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}
