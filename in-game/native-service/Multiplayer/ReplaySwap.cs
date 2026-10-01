using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// After each round every player's replay (format 2, a few hundred KB at most)
// goes to the host, which relays it to everyone; each machine imports what it
// receives through ReplayImport (decoded completely, never replacing a replay
// with different bytes). Clips shared in the chat travel the same way.
// Message: replay.chunk {match, round, owner, id, kind, size, hash, offset, data}.
sealed partial class ReplaySwap(string? output, Func<long> clock)
{
    public const int MaxBytes = 8 * 1024 * 1024, ChunkBytes = 8192;
    public sealed record Shared(string Match, int Round, string Owner, string Id, string Kind, string? Label);
    sealed record Outgoing(string Peer, string Match, int Round, string Owner, string Id, string Kind, byte[] Bytes, string Hash, string? Label) { public int Offset; }
    sealed class Incoming { public required byte[] Bytes; public int Received; public required string Hash; public required string Kind; public string? Label; }
    readonly List<Outgoing> queue = [];
    readonly Dictionary<string, Incoming> assembling = new();
    readonly List<Shared> received = [];
    readonly Dictionary<string, string> mine = new(); // match#round -> my replay id
    public IReadOnlyList<Shared> Received => received;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,100}\z")] private static partial Regex IdPattern();
    public static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // A completed run's replay on this machine (never a .partial recording).
    public byte[]? ReadOwn(string id)
    {
        if (output is null || !ValidId(id)) return null;
        var path = Path.Combine(output, "replays", id + ".amreplay");
        try { var info = new FileInfo(path); return info.Exists && info.Length is > 24 and <= MaxBytes ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public void Mine(string match, int round, string id) => mine[match + "#" + round] = id;
    public string? MineFor(string match, int round) => mine.GetValueOrDefault(match + "#" + round);

    public void Offer(string peer, string match, int round, string owner, string id, string kind, byte[] bytes, string? label = null)
    {
        if (bytes.Length is 0 or > MaxBytes || queue.Any(q => q.Peer == peer && q.Id == id && q.Owner == owner)) return;
        queue.Add(new Outgoing(peer, match, round, owner, id, kind, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), label));
    }

    // Sends a bounded number of chunks per tick; fewer while a round is live.
    public void Pump(bool live, Action<string, object> send)
    {
        var budget = live ? 4 : 32;
        while (budget-- > 0 && queue.Count > 0)
        {
            var q = queue[0];
            var length = Math.Min(ChunkBytes, q.Bytes.Length - q.Offset);
            send(q.Peer, new { match = q.Match, round = q.Round, owner = q.Owner, id = q.Id, kind = q.Kind, label = q.Label, size = q.Bytes.Length, hash = q.Hash, offset = q.Offset, data = Convert.ToBase64String(q.Bytes, q.Offset, length) });
            q.Offset += length;
            if (q.Offset >= q.Bytes.Length) queue.RemoveAt(0);
        }
    }

    // One chunk; returns the complete, hash-checked file when the last one arrives.
    public (byte[] Bytes, Shared Info)? Chunk(string match, int round, string owner, string id, string kind, string? label, int size, string hash, int offset, byte[] data)
    {
        if (!ValidId(id) || kind is not ("round" or "clip") || size is <= 24 or > MaxBytes || !ContentRules.Hash(hash) || data.Length is 0 or > ChunkBytes || offset < 0 || offset + data.Length > size) return null;
        var key = owner + "|" + id;
        if (!assembling.TryGetValue(key, out var a) || a.Hash != hash || a.Bytes.Length != size)
        {
            if (offset != 0) return null;
            assembling[key] = a = new Incoming { Bytes = new byte[size], Hash = hash, Kind = kind, Label = label };
        }
        if (offset != a.Received) return null;
        Buffer.BlockCopy(data, 0, a.Bytes, offset, data.Length);
        a.Received += data.Length;
        if (a.Received < size) return null;
        assembling.Remove(key);
        if (Convert.ToHexString(SHA256.HashData(a.Bytes)).ToLowerInvariant() != hash) return null;
        return (a.Bytes, new Shared(match, round, owner, id, kind, label is { Length: <= 80 } ? label : null));
    }

    // Imports into this machine's replay library and remembers it for the round.
    public string? Import(byte[] bytes, Shared info)
    {
        if (output is null) return null;
        var (id, _, error) = ReplayImport.Import(output, bytes);
        if (id is null && error != "replay-exists") return null;
        var stored = id ?? info.Id;
        if (!received.Any(r => r.Owner == info.Owner && r.Id == stored)) received.Add(info with { Id = stored });
        if (received.Count > 200) received.RemoveAt(0);
        return stored;
    }
}
