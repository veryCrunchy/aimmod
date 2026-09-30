using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;

sealed record OverlayPlacement(bool Visible, double X, double Y, double Width);
sealed record OverlayLayout(double Opacity, OverlayPlacement Stats, OverlayPlacement Versus);
sealed record SavedOverlayLayout(string Name, OverlayLayout Layout);
sealed record OverlaySettingsValue(bool GameEnabled, bool ObsEnabled, double Opacity, OverlayPlacement Stats, OverlayPlacement Versus)
{
    public OverlayLayout? Obs { get; init; }
    public SavedOverlayLayout[] Layouts { get; init; } = [];
    public static OverlaySettingsValue Default => new(false, false, 1, new(true, 1, 100, 560), new(true, 100, 100, 300));
}
sealed class OverlaySettings
{
    const int Limit = 4096;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly object gate = new();
    readonly string path;
    readonly string layoutsPath;
    sealed record LayoutStore(int Version, OverlayLayout Obs, SavedOverlayLayout[] Layouts);
    OverlaySettingsValue current = OverlaySettingsValue.Default;
    public OverlaySettingsValue Current { get { lock (gate) return current; } }
    public OverlaySettings(string directory)
    {
        Directory.CreateDirectory(directory); path = Path.Combine(directory, "overlay-settings.json");
        layoutsPath = Path.Combine(directory, "overlay-layouts.json");
        if (!File.Exists(path)) Write(current);
        try
        {
            if (new FileInfo(path).Length > Limit) throw new InvalidDataException();
            var bytes = File.ReadAllBytes(path);
            // Saved snapshots require every field; missing values never enable a surface.
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Count() != 5) throw new JsonException();
            var decoded = Decode(bytes, OverlaySettingsValue.Default);
            if (!bytes.AsSpan().SequenceEqual(CoreBytes(decoded))) throw new JsonException();
            current = decoded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { current = OverlaySettingsValue.Default; }
        current = current with { Obs = GameLayout(current) };
        if (!File.Exists(layoutsPath)) WriteLayouts(current);
        else try
        {
            if (new FileInfo(layoutsPath).Length > 32768) throw new JsonException();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(layoutsPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 || root.GetProperty("version").GetInt32() != 1) throw new JsonException();
            var obs = Layout(root.GetProperty("obs"), GameLayout(current));
            var layouts = new List<SavedOverlayLayout>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in root.GetProperty("layouts").EnumerateArray())
            {
                if (layouts.Count >= 20 || entry.EnumerateObject().Count() != 2) throw new JsonException();
                var name = Name(entry.GetProperty("name"));
                if (!names.Add(name)) throw new JsonException();
                layouts.Add(new(name, Layout(entry.GetProperty("layout"), GameLayout(current))));
            }
            var loaded = current with { Obs = obs, Layouts = layouts.ToArray() };
            if (!File.ReadAllBytes(layoutsPath).AsSpan().SequenceEqual(LayoutBytes(loaded))) throw new JsonException();
            current = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { current = current with { ObsEnabled = false }; }
        // Upgrade only the complete original placement pair. User placement,
        // visibility, opacity, enable flags and named layouts remain intact.
        var game = UpgradeOriginal(GameLayout(current));
        if (game != GameLayout(current)) { current = WithGame(current, game); Write(current); }
        var upgradedObs = UpgradeOriginal(current.Obs!);
        if (upgradedObs != current.Obs) { current = current with { Obs = upgradedObs }; WriteLayouts(current); }
    }
    static OverlayLayout UpgradeOriginal(OverlayLayout value) =>
        value.Stats.X == 2 && value.Stats.Y == 12 && value.Stats.Width == 240 && value.Versus.X == 75 && value.Versus.Y == 12 && value.Versus.Width == 300
            ? value with { Stats = value.Stats with { X = 1, Y = 100, Width = 560 }, Versus = value.Versus with { X = 100, Y = 100 } } : value;
    static OverlayLayout GameLayout(OverlaySettingsValue value) => new(value.Opacity, value.Stats, value.Versus);
    static OverlaySettingsValue WithGame(OverlaySettingsValue value, OverlayLayout layout) => value with { Opacity = layout.Opacity, Stats = layout.Stats, Versus = layout.Versus };
    static string Name(JsonElement e)
    {
        var name = e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : "";
        if (name.Length is < 1 or > 48 || name.Any(char.IsControl)) throw new JsonException();
        return name;
    }
    static OverlayLayout Layout(JsonElement e, OverlayLayout prior)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>();
        foreach (var p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new JsonException();
            prior = p.Name switch {
                "opacity" => prior with { Opacity = Number(p.Value, 0, 1) },
                "stats" => prior with { Stats = Placement(p.Value, prior.Stats) },
                "versus" => prior with { Versus = Placement(p.Value, prior.Versus) },
                _ => throw new JsonException()
            };
        }
        if (seen.Count == 0) throw new JsonException();
        return prior;
    }
    static bool Boolean(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new JsonException();
    static double Number(JsonElement e, double min, double max) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) && n >= min && n <= max ? n : throw new JsonException();
    static OverlayPlacement Placement(JsonElement e, OverlayPlacement prior)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>();
        foreach (var p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new JsonException();
            prior = p.Name switch {
                "visible" => prior with { Visible = Boolean(p.Value) },
                "x" => prior with { X = Number(p.Value, 0, 100) },
                "y" => prior with { Y = Number(p.Value, 0, 100) },
                "width" => prior with { Width = Number(p.Value, 140, 600) },
                _ => throw new JsonException()
            };
        }
        if (seen.Count == 0) throw new JsonException();
        return prior;
    }
    static OverlaySettingsValue Decode(ReadOnlyMemory<byte> bytes, OverlaySettingsValue prior)
    {
        if (bytes.Length is <= 0 or > Limit) throw new JsonException();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new JsonException();
            prior = p.Name switch {
                "gameEnabled" => prior with { GameEnabled = Boolean(p.Value) },
                "obsEnabled" => prior with { ObsEnabled = Boolean(p.Value) },
                "opacity" => prior with { Opacity = Number(p.Value, 0, 1) },
                "stats" => prior with { Stats = Placement(p.Value, prior.Stats) },
                "versus" => prior with { Versus = Placement(p.Value, prior.Versus) },
                _ => throw new JsonException()
            };
        }
        if (seen.Count == 0) throw new JsonException();
        return prior;
    }
    public OverlaySettingsValue ApplyJson(ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (bytes.Length is <= 0 or > Limit) throw new JsonException();
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || bytes.Length > Limit) throw new JsonException();
            if (root.TryGetProperty("obs", out var obs))
            {
                if (root.EnumerateObject().Count() != 1) throw new JsonException();
                var changed = current with { Obs = Layout(obs, current.Obs!) };
                WriteLayouts(changed); current = changed; return current;
            }
            if (root.TryGetProperty("layoutAction", out var action)) return LayoutAction(root, action);
            var next = Decode(bytes, current); Write(next); current = next; return next;
        }
    }
    OverlaySettingsValue LayoutAction(JsonElement root, JsonElement action)
    {
        var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != 3 || keys.Distinct().Count() != 3 || keys.Any(k => k is not ("layoutAction" or "name" or "surface"))) throw new JsonException();
        if (action.ValueKind != JsonValueKind.String || root.GetProperty("surface").ValueKind != JsonValueKind.String) throw new JsonException();
        var surface = root.GetProperty("surface").GetString();
        if (surface is not ("game" or "obs")) throw new JsonException();
        var name = Name(root.GetProperty("name"));
        var found = current.Layouts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var next = current;
        switch (action.GetString())
        {
            case "save":
                if (found is null && current.Layouts.Length >= 20) throw new JsonException();
                var layout = surface == "game" ? GameLayout(current) : current.Obs!;
                next = current with { Layouts = current.Layouts.Where(x => x != found).Append(new SavedOverlayLayout(name, layout)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
                break;
            case "delete":
                if (found is null) throw new JsonException();
                next = current with { Layouts = current.Layouts.Where(x => x != found).ToArray() };
                break;
            case "apply":
                if (found is null) throw new JsonException();
                next = surface == "game" ? WithGame(current, found.Layout) : current with { Obs = found.Layout };
                if (surface == "game") { Write(next); current = next; return next; }
                break;
            default: throw new JsonException();
        }
        WriteLayouts(next); current = next; return next;
    }
    static byte[] CoreBytes(OverlaySettingsValue value) => JsonSerializer.SerializeToUtf8Bytes(new { value.GameEnabled, value.ObsEnabled, value.Opacity, value.Stats, value.Versus }, Json);
    static byte[] LayoutBytes(OverlaySettingsValue value) => JsonSerializer.SerializeToUtf8Bytes(new LayoutStore(1, value.Obs!, value.Layouts), Json);
    void WriteLayouts(OverlaySettingsValue value) => AtomicWrite(layoutsPath, LayoutBytes(value));
    void Write(OverlaySettingsValue value)
        => AtomicWrite(path, CoreBytes(value));
    static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/overlay-settings", () => Results.Json(Current));
        routes.MapPost(prefix + "/overlay-settings", async (HttpRequest request, CancellationToken token) => {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > Limit) return Results.StatusCode(413);
            try { var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token); return Results.Json(ApplyJson(bytes)); }
            catch (Exception ex) when (ex is JsonException or EndOfStreamException) { return Results.BadRequest(new { error = "Invalid overlay settings." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Json(new { error = "Overlay settings could not be saved." }, statusCode: 500); }
        });
    }
}
