using System.Text.Json;

namespace AimMod.InGame;

// Metadata and explicit local file operations only; never writes score history.
sealed class ReplayLibrary
{
    readonly ReplayCatalog catalog;
    readonly string metadata, exports;
    readonly object gate = new();
    public ReplayLibrary(ReplayCatalog catalog, string output, string? exportDirectory = null)
    {
        this.catalog = catalog;
        metadata = Path.Combine(output, "replay-favorites.json");
        exports = exportDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AimMod", "Replays");
    }
    HashSet<string> Favorites()
    {
        if (!File.Exists(metadata)) return new(StringComparer.Ordinal);
        if (new FileInfo(metadata).Length > 256 * 1024) throw new IOException("Replay favorites exceed size limit.");
        // A damaged favorites file must not block listing, export or deletion.
        // It is replaced on the next explicit favorite change.
        try { return new((JsonSerializer.Deserialize<string[]>(File.ReadAllText(metadata)) ?? []).Where(id => id is { Length: > 0 and <= 100 }), StringComparer.Ordinal); }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }
    void Save(HashSet<string> favorites) => AtomicFile.WriteText(metadata, JsonSerializer.Serialize(favorites));
    public object List()
    {
        lock (gate)
        {
            var favorites = Favorites();
            return catalog.List().Select(r => new { r.Id, r.Scenario, r.RecordedAt, r.Reason, r.Frames, r.InputEvents, favorite = favorites.Contains(r.Id) }).ToArray();
        }
    }
    public bool Favorite(string id, bool favorite)
    {
        lock (gate)
        {
            if (catalog.Resolve(id) is null) return false;
            var values = Favorites();
            if (favorite) { if (values.Count >= 2000 && !values.Contains(id)) throw new IOException("Replay favorite limit reached."); values.Add(id); }
            else values.Remove(id);
            Save(values); return true;
        }
    }
    public bool Delete(string id)
    {
        lock (gate)
        {
            var path = catalog.Resolve(id);
            if (path is null) return false;
            // Only finalized .amreplay files resolve; the active .partial is never touched.
            var favorites = Favorites();
            favorites.Remove(id); Save(favorites);
            File.Delete(path); return true;
        }
    }
    public bool Export(string id)
    {
        lock (gate)
        {
            var replay = catalog.Read(id);
            if (replay is null || replay.Reason != "completed") return false;
            var source = catalog.Resolve(id);
            if (source is null) return false;
            Directory.CreateDirectory(exports);
            var destination = Path.Combine(exports, id + ".amreplay");
            // A second export keeps the existing copy, rather than overwriting user files.
            if (File.Exists(destination)) destination = Path.Combine(exports, id + "-" + Guid.NewGuid().ToString("N")[..8] + ".amreplay");
            File.Copy(source, destination, false); return true;
        }
    }
}
