using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame;

sealed record NativeSettingsValue(
    [property: JsonPropertyName("replayRecordingEnabled")] bool ReplayRecordingEnabled = true,
    [property: JsonPropertyName("hubHistoryEnabled")] bool HubHistoryEnabled = true);

// One canonical file is both persistence and the game-thread control channel.
// There is no second JSON mirror that could disagree after a failed write.
sealed class NativeSettings
{
    const int Limit = 1024;
    readonly object gate = new();
    readonly string path;
    NativeSettingsValue current;
    public bool ReadFailed { get; private set; }
    public NativeSettingsValue Current { get { lock (gate) return current; } }
    public NativeSettings(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "native-settings.tsv");
        current = new();
        if (!File.Exists(path)) { Write(current); return; }
        try
        {
            if (new FileInfo(path).Length > Limit) throw new InvalidDataException();
            current = Decode(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // Never turn capture or remote history back on because a saved
            // preference could not be read. Keep the file for user recovery.
            current = new(false, false); ReadFailed = true;
        }
    }
    internal static string Encode(NativeSettingsValue value) =>
        $"AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t{(value.ReplayRecordingEnabled ? 1 : 0)}\nhubHistoryEnabled\t{(value.HubHistoryEnabled ? 1 : 0)}\n";
    internal static NativeSettingsValue Decode(string text)
    {
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (rows.Length != 4 || rows[0] != "AIMMOD_SETTINGS_1" || rows[3] != "") throw new InvalidDataException("Invalid settings format.");
        bool Read(string row, string key) => row == key + "\t1" ? true : row == key + "\t0" ? false : throw new InvalidDataException("Invalid settings value.");
        return new(Read(rows[1], "replayRecordingEnabled"), Read(rows[2], "hubHistoryEnabled"));
    }
    public NativeSettingsValue ApplyJson(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > Limit) throw new JsonException("Invalid settings size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be an object.");
        bool? capture = null, history = null;
        var count = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Settings must be boolean.");
            switch (property.Name)
            {
                case "replayRecordingEnabled" when capture is null: capture = property.Value.GetBoolean(); break;
                case "hubHistoryEnabled" when history is null: history = property.Value.GetBoolean(); break;
                default: throw new JsonException("Unknown or duplicate setting.");
            }
            count++;
        }
        if (count == 0) throw new JsonException("No settings supplied.");
        lock (gate)
        {
            var next = new NativeSettingsValue(capture ?? current.ReplayRecordingEnabled, history ?? current.HubHistoryEnabled);
            if (next != current || ReadFailed) Write(next);
            current = next; ReadFailed = false;
            return next;
        }
    }
    void Write(NativeSettingsValue value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(Encode(value)); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/settings", () => Results.Json(Current));
        routes.MapPost(prefix + "/settings", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > Limit) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                return Results.Json(ApplyJson(bytes));
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid settings." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete settings." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "Settings could not be saved." }, statusCode: 500); }
        });
    }
}
