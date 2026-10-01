using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;

// What the linked AimMod Hub account shares. Both default to on, as in the
// desktop companion app, where linking an account turned on uploads and live
// activity together. Nothing is shared while no account is linked.
sealed record HubSharingValue(
    [property: JsonPropertyName("hubLiveActivityEnabled")] bool LiveActivity = true,
    [property: JsonPropertyName("hubRunUploadsEnabled")] bool RunUploads = true);

// Own file, same conventions as discord-settings.tsv: native-settings.tsv keeps
// its exact format (AimModCore and the Lua mod parse it). Atomic replace,
// strict decoding, and a damaged file turns sharing off instead of on.
sealed class HubSharingSettings
{
    const int Limit = 1024;
    const string Header = "AIMMOD_HUB_SHARING_1";
    static readonly string[] Keys = ["hubLiveActivityEnabled", "hubRunUploadsEnabled"];
    readonly object gate = new();
    readonly string path;
    HubSharingValue current;
    public bool ReadFailed { get; private set; }
    public HubSharingValue Current { get { lock (gate) return current; } }
    public HubSharingSettings(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "hub-sharing.tsv");
        current = new();
        if (!File.Exists(path)) { Write(current); return; }
        try
        {
            if (new FileInfo(path).Length > Limit) throw new InvalidDataException();
            current = Decode(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        { current = new(false, false); ReadFailed = true; }
    }
    static bool[] Flags(HubSharingValue v) => [v.LiveActivity, v.RunUploads];
    internal static string Encode(HubSharingValue value)
    {
        var flags = Flags(value);
        var text = new StringBuilder(Header).Append('\n');
        for (var i = 0; i < Keys.Length; i++) text.Append(Keys[i]).Append('\t').Append(flags[i] ? '1' : '0').Append('\n');
        return text.ToString();
    }
    internal static HubSharingValue Decode(string text)
    {
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (rows.Length != Keys.Length + 2 || rows[0] != Header || rows[^1] != "") throw new InvalidDataException("Invalid Hub sharing settings format.");
        var flags = new bool[Keys.Length];
        for (var i = 0; i < Keys.Length; i++)
            flags[i] = rows[i + 1] == Keys[i] + "\t1" ? true : rows[i + 1] == Keys[i] + "\t0" ? false : throw new InvalidDataException("Invalid Hub sharing settings value.");
        return new(flags[0], flags[1]);
    }
    public HubSharingValue ApplyJson(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > Limit) throw new JsonException("Invalid settings size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be an object.");
        var patch = new bool?[Keys.Length];
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Settings must be boolean.");
            var index = Array.IndexOf(Keys, property.Name);
            if (index < 0 || patch[index] is not null) throw new JsonException("Unknown or duplicate setting.");
            patch[index] = property.Value.GetBoolean();
        }
        if (patch.All(p => p is null)) throw new JsonException("No settings supplied.");
        lock (gate)
        {
            var flags = Flags(current);
            for (var i = 0; i < flags.Length; i++) flags[i] = patch[i] ?? flags[i];
            var next = new HubSharingValue(flags[0], flags[1]);
            if (next != current || ReadFailed) Write(next);
            current = next; ReadFailed = false;
            return next;
        }
    }
    void Write(HubSharingValue value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encoding.UTF8.GetBytes(Encode(value)); stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix, Func<object>? status = null)
    {
        routes.MapGet(prefix + "/hub-sharing", () => Results.Json(new { settings = Current, status = status?.Invoke() }));
        routes.MapPost(prefix + "/hub-sharing", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > Limit) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                return Results.Json(new { settings = ApplyJson(bytes), status = status?.Invoke() });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid settings." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete settings." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "Settings could not be saved." }, statusCode: 500); }
        });
    }
}
