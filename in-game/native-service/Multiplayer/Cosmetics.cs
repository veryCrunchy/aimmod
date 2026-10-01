using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// A catalog item a player picked, as lobbies share it: id and version only.
sealed record CosmeticRef(string Id, int Version);

// One curated catalog item (in-game/docs/cosmetics.md, CosmeticsCatalog.lua).
// Color is the item's main colour (linear 0..1 RGB) for the 2D preview, when it has one.
sealed record CosmeticItem(string Id, int Version, string Kind, string Name, IReadOnlyList<string> Models, IReadOnlyList<string> Parts, double[]? Color, string? Pak, bool Draft,
    IReadOnlyDictionary<string, double[]>? Vectors = null, IReadOnlyDictionary<string, double>? Scalars = null)
{
    // Card swatch, main colour first: body paint, panels and bare metal, or a
    // weapon's accent and glow. Material colours are linear; the page gets sRGB hex.
    static readonly string[][] SwatchParams = [["MetalPaint", "AccentColor", "PrimaryColor", "Color"], ["TriangularPaint", "Emissive"], ["RawMetal"]];
    public IReadOnlyList<string> Swatch => SwatchParams
        .Select(names => names.Select(n => Vectors?.GetValueOrDefault(n)).FirstOrDefault(v => v is { Length: >= 3 }))
        .OfType<double[]>().Select(Hex).ToArray();
    // How metallic the finish is (0..1), for the swatch's highlight.
    public double Shine => Math.Clamp(Scalars?.GetValueOrDefault("Metallic") ?? 0, 0, 1);
    static string Hex(double[] linear) => "#" + string.Concat(linear.Take(3).Select(c =>
    {
        c = Math.Clamp(c, 0, 1);
        var s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return ((int)Math.Round(s * 255)).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
    }));
}

// The curated cosmetics catalog AimMod ships (catalog.json), used only when it
// matches its manifest (catalog-manifest.json: file sizes and SHA-256). Players
// never add files; lobbies share ids. Validation follows CosmeticsCatalog.validate.
sealed partial class CosmeticsCatalog
{
    public const string CatalogFile = "catalog.json", ManifestFile = "catalog-manifest.json";
    public const int MaxEquipped = 8;
    static readonly Dictionary<string, (string[] Parts, bool NeedsPak)> Kinds = new()
    {
        ["avatar_tint"] = (["body"], false), ["avatar_pattern"] = (["body"], true),
        ["weapon_finish"] = (["weapon", "arms"], false), ["weapon_pattern"] = (["weapon", "arms"], true),
        ["accessory"] = (["body"], true), ["weapon_model"] = (["weapon"], true),
        ["reload_animation"] = (["arms"], true), ["player_model"] = (["body"], true),
    };
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")] private static partial Regex IdPattern();
    public static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public int Version { get; private init; }
    public IReadOnlyList<CosmeticItem> Items { get; private init; } = [];
    public string? Problem { get; private init; }
    readonly HashSet<string> verifiedPaks = new(StringComparer.Ordinal);
    public bool Available => Problem is null;

    public static readonly CosmeticsCatalog Empty = new() { Problem = "missing" };

    // folder holds catalog.json and its manifest; paks live in the game's Content/Paks/~AimMod.
    public static CosmeticsCatalog Load(string? folder, string? paksFolder)
    {
        if (folder is null) return Empty;
        try
        {
            var manifestPath = Path.Combine(folder, ManifestFile);
            var catalogPath = Path.Combine(folder, CatalogFile);
            if (!File.Exists(manifestPath) || !File.Exists(catalogPath)) return Empty;
            if (new FileInfo(manifestPath).Length > 1 << 20 || new FileInfo(catalogPath).Length > 4 << 20) return new() { Problem = "too-large" };
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var files = manifest.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToArray() : [];
            bool Matches(string path, string name)
            {
                var entry = files.FirstOrDefault(x => x.TryGetProperty("name", out var n) && n.GetString() == name);
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("size", out var size) || !entry.TryGetProperty("sha256", out var sha)) return false;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != size.GetInt64()) return false;
                using var stream = File.OpenRead(path);
                return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), sha.GetString(), StringComparison.OrdinalIgnoreCase);
            }
            if (!Matches(catalogPath, CatalogFile)) return new() { Problem = "manifest-mismatch" };
            var catalog = new CosmeticsCatalog { Version = manifest.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver) ? ver : 0, Items = Parse(File.ReadAllText(catalogPath)) };
            // A pak counts only when its size and SHA-256 match the manifest.
            if (paksFolder is not null)
                foreach (var pak in catalog.Items.Where(i => i.Pak is not null).Select(i => i.Pak!).Distinct())
                    if (Path.GetFileName(pak) == pak && Matches(Path.Combine(paksFolder, pak), pak)) catalog.verifiedPaks.Add(pak);
            return catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return new() { Problem = "unreadable" }; }
    }

    static List<CosmeticItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<CosmeticItem>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in items.EnumerateArray().Take(500))
        {
            if (Validate(e) is not { } item) continue;
            seen[item.Id] = seen.GetValueOrDefault(item.Id) + 1;
            list.Add(item);
        }
        // Duplicate ids invalidate every entry with that id.
        return list.Where(i => seen[i.Id] == 1).ToList();
    }

    static string[] Strings(JsonElement e, string key) => e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(16).ToArray() : [];
    static bool InRange(JsonElement v, double lo, double hi) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= lo && d <= hi;

    // The same rules as CosmeticsCatalog.validate; anything else is dropped.
    static CosmeticItem? Validate(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? Text(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = Text("id");
        if (!ValidId(id) || !e.TryGetProperty("version", out var ver) || !ver.TryGetInt32(out var version) || version is < 1 or > 100000) return null;
        if (Text("kind") is not { } kind || !Kinds.TryGetValue(kind, out var rules)) return null;
        var parts = Strings(e, "parts");
        if (parts.Length == 0 || parts.Any(p => !rules.Parts.Contains(p))) return null;
        var models = Strings(e, "models");
        if (rules.Parts.Contains("body") && !rules.Parts.Contains("weapon") && models.Length == 0) return null;
        var hasParameters = false; double[]? color = null;
        var vectors = new Dictionary<string, double[]>(StringComparer.Ordinal); var scalars = new Dictionary<string, double>(StringComparer.Ordinal);
        if (e.TryGetProperty("vector", out var vector) && vector.ValueKind == JsonValueKind.Object)
            foreach (var p in vector.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) return null;
                var c = new double[4];
                for (var i = 0; i < 4; i++) { if (!p.Value.TryGetProperty("RGBA"[i].ToString(), out var x) || !InRange(x, 0, 1)) return null; c[i] = x.GetDouble(); }
                hasParameters = true; vectors[p.Name] = c;
            }
        if (e.TryGetProperty("scalar", out var scalar) && scalar.ValueKind == JsonValueKind.Object)
            foreach (var p in scalar.EnumerateObject()) { if (!InRange(p.Value, -10, 10)) return null; hasParameters = true; scalars[p.Name] = p.Value.GetDouble(); }
        foreach (var main in new[] { "MetalPaint", "AccentColor", "PrimaryColor", "Color" })
            if (color is null && vectors.TryGetValue(main, out var mc)) color = mc[..3];
        string? pak = e.TryGetProperty("pak", out var pk) && pk.ValueKind == JsonValueKind.Object && pk.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String ? file.GetString() : null;
        if (rules.NeedsPak && pak is null) return null;
        if (!rules.NeedsPak && !hasParameters) return null;
        var name = Text("name") is { Length: > 0 and <= 40 } n && !n.Any(char.IsControl) ? n : id!;
        return new CosmeticItem(id!, version, kind, name, models, parts, color, pak, e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True, vectors, scalars);
    }

    // Items players can pick: valid, not drafts, and (for pak items) with a matching pak.
    public IReadOnlyList<CosmeticItem> Pickable => Items.Where(i => !i.Draft && (i.Pak is null || verifiedPaks.Contains(i.Pak))).ToArray();

    // A shared item resolves only to the same id and version in this viewer's own catalog.
    public CosmeticRef? Resolve(CosmeticRef r) => Pickable.Any(i => i.Id == r.Id && i.Version == r.Version) ? r : null;
    public IReadOnlyList<CosmeticRef> Filter(IEnumerable<CosmeticRef>? items) => (items ?? []).Select(Resolve).OfType<CosmeticRef>().DistinctBy(r => r.Id).Take(MaxEquipped).ToArray();
}

// cosmetic-looks.txt for AimModCore (CosmeticLooks): v=1, one peer line per lobby
// member (Steam ids only), one self line. Written with the session marker, deleted with it.
static class CosmeticLooks
{
    public const string FileName = "cosmetic-looks.txt";
    static string Items(IEnumerable<CosmeticRef> items) => string.Join(',', items.Select(i => i.Id + "@" + i.Version));
    public static string Format(IEnumerable<CosmeticRef> self, IEnumerable<(string Peer, IReadOnlyList<CosmeticRef> Items)> peers)
    {
        var text = new StringBuilder("v=1\n");
        foreach (var (peer, items) in peers)
            if (peer.Length is > 0 and <= 20 && peer.All(char.IsAsciiDigit) && items.Count > 0) text.Append("peer=").Append(peer).Append(" items=").Append(Items(items)).Append('\n');
        text.Append("self=").Append(Items(self)).Append('\n');
        return text.ToString();
    }
}
