using System.Security.Cryptography;
using System.Text.Json;
using Realms;

namespace AimMod.Desktop.Practice;

public sealed record PracticeCollectionResult(IReadOnlyList<string> InstalledHashes, int CollectionCount);

/// <summary>Updates only the named coaching collection; other collections and all scores are preserved.</summary>
public static class PracticeCollectionSync
{
    public const string CollectionName = "AimMod coaching";
    private const ulong schemaVersion = 51;

    public static PracticeCollectionResult Lazer(string root, string journalDirectory, IEnumerable<string> active, IEnumerable<string> retired)
    {
        string path = Path.Combine(Path.GetFullPath(root), "client.realm");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The osu!lazer library is unavailable.");
        string[] add = validHashes(active), remove = validHashes(retired).Except(add, StringComparer.OrdinalIgnoreCase).ToArray();
        // Preflight through the existing schema without allowing migration or a new database.
        using (var read = Realm.GetInstance(new RealmConfiguration(path) { IsDynamic = true, IsReadOnly = true, SchemaVersion = schemaVersion }))
        {
            // osu! maps BeatmapInfo to the persisted table "Beatmap" via [MapTo].
            if (!read.Schema.Any(s => s.Name == "BeatmapCollection") || !read.Schema.Any(s => s.Name == "Beatmap"))
                throw new IOException("This osu!lazer library version does not support coaching collections yet.");
        }
        using var realm = Realm.GetInstance(new RealmConfiguration(path)
        {
            IsDynamic = true, SchemaVersion = schemaVersion,
            MigrationCallback = (_, _) => throw new IOException("AimMod will not migrate the osu! database."),
        });
        var wanted = add.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = realm.DynamicApi.All("Beatmap").AsEnumerable().Select(m => m.DynamicApi.Get<string>("MD5Hash"))
            .Where(wanted.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int count = 0;
        realm.Write(() =>
        {
            IRealmObjectBase? collection = realm.DynamicApi.All("BeatmapCollection").AsEnumerable().FirstOrDefault(c => c.DynamicApi.Get<string>("Name") == CollectionName);
            var before = collection?.DynamicApi.GetList<string>("BeatmapMD5Hashes").ToArray() ?? [];
            var next = before.Where(h => !remove.Contains(h, StringComparer.OrdinalIgnoreCase)).Concat(add).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            count = next.Length;
            if (before.SequenceEqual(next) && collection is not null) return;
            // A small rollback journal contains only this collection, never another table.
            Directory.CreateDirectory(journalDirectory);
            File.WriteAllText(Path.Combine(journalDirectory, $"collection-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"),
                JsonSerializer.Serialize(new { Name = CollectionName, Id = collection?.DynamicApi.Get<Guid>("ID"), Hashes = before }));
            collection ??= realm.DynamicApi.CreateObject("BeatmapCollection", Guid.NewGuid());
            collection.DynamicApi.Set("Name", CollectionName);
            var hashes = collection.DynamicApi.GetList<string>("BeatmapMD5Hashes");
            hashes.Clear(); foreach (string hash in next) hashes.Add(hash);
            collection.DynamicApi.Set("LastModified", DateTimeOffset.UtcNow);
        });
        return new(installed, count);
    }

    internal static string[] validHashes(IEnumerable<string> values)
    {
        var hashes = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hashes.Length > 10000 || hashes.Any(h => h.Length != 32 || !h.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid coaching map identities.");
        return hashes.Select(h => h.ToLowerInvariant()).ToArray();
    }

    public static void Stable(string root, string journalDirectory, IEnumerable<string> active, IEnumerable<string> retired, Func<bool> isRunning)
    {
        if (isRunning()) throw new IOException("Close osu!stable once to update the AimMod coaching collection.");
        string path = Path.Combine(Path.GetFullPath(root), "collection.db");
        byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (original.Length > 32 * 1024 * 1024) throw new IOException("The collections file is too large to update.");
        var collections = new List<(string Name, List<string> Hashes)>(); int version = 20260907;
        if (original.Length > 0)
        {
            using var reader = new BinaryReader(new MemoryStream(original));
            version = reader.ReadInt32(); int length = reader.ReadInt32();
            if (length is < 0 or > 100000) throw new InvalidDataException();
            for (int c = 0; c < length; c++)
            {
                string name = readString(reader); int size = reader.ReadInt32();
                if (size is < 0 or > 1000000) throw new InvalidDataException();
                var hashes = new List<string>();
                for (int h = 0; h < size; h++) hashes.Add(readString(reader));
                collections.Add((name, hashes));
            }
            if (reader.BaseStream.Position != original.Length) throw new InvalidDataException();
        }
        string[] add = validHashes(active), remove = validHashes(retired).Except(add).ToArray();
        int index = collections.FindIndex(c => c.Name == CollectionName);
        if (index < 0) { collections.Add((CollectionName, [])); index = collections.Count - 1; }
        var owned = collections[index].Hashes;
        owned.RemoveAll(h => remove.Contains(h, StringComparer.OrdinalIgnoreCase));
        foreach (var hash in add) if (!owned.Contains(hash, StringComparer.OrdinalIgnoreCase)) owned.Add(hash);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(version); writer.Write(collections.Count);
            foreach (var item in collections)
            { writeString(writer, item.Name); writer.Write(item.Hashes.Count); foreach (string h in item.Hashes) writeString(writer, h); }
        }
        byte[] next = output.ToArray(); if (original.SequenceEqual(next)) return;
        if (isRunning() || (File.Exists(path) ? !File.ReadAllBytes(path).SequenceEqual(original) : original.Length != 0))
            throw new IOException("osu! collections changed. AimMod will retry shortly.");
        Directory.CreateDirectory(journalDirectory);
        string backup = Path.Combine(journalDirectory, $"collection-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(backup, original);
        string temporary = path + $".aimmod-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, next);
            if (isRunning()) throw new IOException("Close osu!stable once to update the AimMod coaching collection.");
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string readString(BinaryReader reader) => reader.ReadByte() switch
    { 0 => "", 11 => reader.ReadString(), _ => throw new InvalidDataException() };
    private static void writeString(BinaryWriter writer, string value) { writer.Write((byte)11); writer.Write(value); }
}
