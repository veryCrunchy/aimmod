using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// Live character preview for the Cosmetics page (in-game/docs/cosmetics.md,
// "Character preview"). While the page is open and visible the UI posts a
// heartbeat (and the drag rotation); the service writes cosmetics-preview.txt
// for AimModCore, which renders the game's own preview stage into a render
// target and exports a PNG named in cosmetics-preview-frame.txt. The request
// expires within seconds, so a closed page or a stopped service ends it.
static partial class CosmeticPreviewFormat
{
    public const string RequestFile = "cosmetics-preview.txt", FrameFile = "cosmetics-preview-frame.txt", Folder = "cosmetics-preview";
    public const int Lifetime = 5, MaxParams = 8;
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex ParamName();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$")] private static partial Regex LookName();
    [GeneratedRegex("^preview-[01]\\.png$")] private static partial Regex FrameName();

    static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // The request body without the expiry (that is what "changed" compares).
    public static string? Body(string model, string? skin, double yaw, IEnumerable<CosmeticItem> items)
    {
        if (!LookName().IsMatch(model) || (skin is { Length: > 0 } && !LookName().IsMatch(skin)) || !double.IsFinite(yaw)) return null;
        yaw = Math.Clamp(yaw, -180, 180);
        var vectors = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var scalars = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var (name, v) in item.Vectors ?? new Dictionary<string, double[]>())
                if (ParamName().IsMatch(name) && v.Length == 4 && v.All(x => double.IsFinite(x) && x is >= 0 and <= 1)) vectors[name] = v;
            foreach (var (name, v) in item.Scalars ?? new Dictionary<string, double>())
                if (ParamName().IsMatch(name) && double.IsFinite(v) && v is >= -10 and <= 10) scalars[name] = v;
        }
        var text = new StringBuilder();
        text.Append("model=").Append(model).Append('\n');
        if (skin is { Length: > 0 } && skin != "Default") text.Append("skin=").Append(skin).Append('\n');
        text.Append("yaw=").Append(N(yaw)).Append('\n');
        foreach (var (name, v) in vectors.Take(MaxParams)) text.Append("vector=").Append(name).Append(':').Append(string.Join(',', v.Select(N))).Append('\n');
        foreach (var (name, v) in scalars.Take(MaxParams)) text.Append("scalar=").Append(name).Append(':').Append(N(v)).Append('\n');
        return text.ToString();
    }
    public static string Request(string body, long seq, long now) => $"v=1\nexpires={now + Lifetime}\nseq={seq}\n" + body;

    // The newest exported frame: (seq, file name) from cosmetics-preview-frame.txt.
    public static (long Seq, string File)? Frame(string? text)
    {
        if (text is null || text.Length > 512) return null;
        long seq = -1; string? file = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("seq=", StringComparison.Ordinal) && long.TryParse(line[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) seq = s;
            else if (line.StartsWith("file=", StringComparison.Ordinal)) file = line[5..].TrimEnd('\r');
        }
        return seq >= 0 && file is not null && FrameName().IsMatch(file) ? (seq, file) : null;
    }
}

sealed partial class MultiplayerService
{
    string? previewBody;
    long previewSeq;

    string? PreviewPath(string name) => outputFolder is null ? null : Path.Combine(outputFolder, name);

    // Body: {open: bool, yaw: number, item?: id to try on before equipping}.
    object CosmeticPreview(JsonElement args)
    {
        lock (gate)
        {
            var open = args.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True;
            if (!open) { DeletePreviewRequest(); return new { frame = 0L }; }
            var yaw = args.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetDouble(out var yv) ? yv : 0;
            var look = AvatarProfiles.Find(myAvatar) ?? AvatarProfiles.Find(AvatarProfiles.Default)!;
            // Equipped body items plus an optional item to try on; parameter items only
            // (pak items need AimModCore's pak check). Every id goes through the catalog.
            var catalog = CosmeticCatalog;
            var ids = OwnLook().Select(r => r.Id).ToList();
            if (args.TryGetProperty("item", out var t) && t.ValueKind == JsonValueKind.String && CosmeticsCatalog.ValidId(t.GetString())) ids.Add(t.GetString()!);
            var items = ids.Select(id => catalog.Pickable.FirstOrDefault(i => i.Id == id))
                .OfType<CosmeticItem>().Where(i => i.Pak is null && i.Parts.Contains("body") && i.Models.Contains(look.Model)).ToList();
            var body = CosmeticPreviewFormat.Body(look.Model, look.Skin, yaw, items);
            if (body is null || PreviewPath(CosmeticPreviewFormat.RequestFile) is not { } path) return new { frame = 0L };
            if (body != previewBody) { previewBody = body; previewSeq++; }
            try { AtomicFile.WriteText(path, CosmeticPreviewFormat.Request(body, previewSeq, DateTimeOffset.UtcNow.ToUnixTimeSeconds())); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new { frame = PreviewFrame()?.Seq ?? 0L };
        }
    }

    (long Seq, string File)? PreviewFrame()
    {
        try
        {
            var path = PreviewPath(CosmeticPreviewFormat.FrameFile);
            return path is not null && File.Exists(path) ? CosmeticPreviewFormat.Frame(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    string? PreviewImage()
    {
        if (PreviewFrame() is not { } frame || outputFolder is null) return null;
        var path = Path.Combine(outputFolder, CosmeticPreviewFormat.Folder, frame.File);
        return File.Exists(path) ? path : null;
    }

    void DeletePreviewRequest()
    {
        previewBody = null;
        try { if (PreviewPath(CosmeticPreviewFormat.RequestFile) is { } path && File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void MapPreviewEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/cosmetic-preview.png", () => PreviewImage() is { } image ? Results.File(image, "image/png") : Results.NotFound());
        routes.MapPost(prefix + "/cosmetic-preview", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > 1024) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
                return Results.Json(CosmeticPreview(doc.RootElement.Clone()), Protocol.Json);
            }
            catch (JsonException) { return Results.BadRequest(); }
            catch (EndOfStreamException) { return Results.BadRequest(); }
        });
    }
}
