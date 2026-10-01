using System.Text.Json;
using AimMod.InGame.Multiplayer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Developer;

// Developer mode: off by default, saved in developer-settings.json next to the
// other service settings. Only the local AimMod UI can change it (capability URL
// plus the X-AimMod-UI header); lobby data and peers never reach it.
sealed class DeveloperMode
{
    readonly string? path;
    readonly object gate = new();
    bool enabled;

    public DeveloperMode(string? output)
    {
        path = output is null ? null : Path.Combine(output, "developer-settings.json");
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length < 1024)
                enabled = JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public bool Enabled { get { lock (gate) return enabled; } }

    public bool Set(bool on)
    {
        lock (gate)
        {
            enabled = on;
            if (path is null) return true;
            try { AtomicFile.WriteText(path, JsonSerializer.Serialize(new { enabled = on })); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}

// GET /developer: whether developer mode is on, and what the tools can see.
// POST /developer {action, ...}: enable, lobby, sim, notice, leave. Everything else
// is refused while developer mode is off.
static class DeveloperEndpoints
{
    public static void Map(IEndpointRouteBuilder routes, string prefix, DeveloperMode mode, MultiplayerService multiplayer, DeveloperTools? tools = null)
    {
        if (mode.Enabled) multiplayer.SetSimulation(true);
        routes.MapGet(prefix + "/developer", () => Results.Json(View(mode, multiplayer, tools), Protocol.Json));
        routes.MapPost(prefix + "/developer", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > 4096) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                var result = Act(mode, multiplayer, doc.RootElement, tools);
                return result.Ok ? Results.Json(View(mode, multiplayer, tools), Protocol.Json) : Results.Json(new { error = result.Message, code = result.Code }, Protocol.Json, statusCode: 409);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete request." }); }
        });
    }

    internal static object View(DeveloperMode mode, MultiplayerService multiplayer, DeveloperTools? tools = null) => new
    {
        enabled = mode.Enabled,
        notices = MultiplayerService.DevNotices,
        status = mode.Enabled ? multiplayer.DevStatus() : null,
        tools = mode.Enabled ? tools?.View() : null,
        camera = mode.Enabled ? tools?.Camera() : null,
        workshop = mode.Enabled ? multiplayer.DevWorkshopItems() : null,
        tournament = mode.Enabled ? multiplayer.Tournaments?.DevView() : null,
    };

    internal static LobbyResult Act(DeveloperMode mode, MultiplayerService multiplayer, JsonElement root, DeveloperTools? tools = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String) return LobbyResult.Fail("invalid", "Missing action.");
        string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var action = a.GetString();
        if (action == "enable")
        {
            var on = root.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
            if (!mode.Set(on)) return LobbyResult.Fail("save", "Couldn’t save developer mode.");
            multiplayer.SetSimulation(on);
            return LobbyResult.Success;
        }
        if (!mode.Enabled) return LobbyResult.Fail("dev-off", "Turn on developer mode in Settings first.");
        switch (action)
        {
            case "lobby":
                var members = root.TryGetProperty("members", out var m) && m.TryGetInt32(out var n) ? n : 3;
                return multiplayer.DevLobby(members, Text("mode"), root.TryGetProperty("simulatedHost", out var h) && h.ValueKind == JsonValueKind.True);
            case "sim":
                return multiplayer.Act("sim", JsonSerializer.SerializeToElement(new { op = Text("op"), member = Text("member") }));
            case "notice":
                return multiplayer.DevNotice(Text("kind") ?? "");
            case "leave":
                return multiplayer.Act("leave", default);
            case "avatar":
                return multiplayer.DevAvatar(root.TryGetProperty("on", out var av) && av.ValueKind == JsonValueKind.True, Text("mode"));
            case "workshop":
                return multiplayer.DevWorkshop(Text("text"));
            case "tournament":
                // Simulated Hub: a whole event against simulated players (op: simulate, sim-advance, sim-opponent-reports, sim-stop).
                if (multiplayer.Tournaments is not { } tournaments) return LobbyResult.Fail("unavailable", "Tournaments aren’t available here.");
                var op = Text("op") ?? "simulate";
                if (op is not ("simulate" or "sim-advance" or "sim-opponent-reports" or "sim-stop")) return LobbyResult.Fail("invalid", "Unknown tournament simulation.");
                return tournaments.Act(JsonSerializer.SerializeToElement(new { action = op, op = Text("variant") }), CancellationToken.None).GetAwaiter().GetResult();
            case "avatar-path" or "loopback" or "content" or "import" when tools is null:
                return LobbyResult.Fail("unavailable", "This tool needs the AimMod output folder.");
            case "avatar-path":
                return tools!.AvatarPath(Text("replay"));
            case "loopback":
                return tools!.Loopback(Text("source"), Text("replay"), root.TryGetProperty("delay", out var d) && d.TryGetDouble(out var delay) ? delay : 2);
            case "content":
                return tools!.ContentLoop(Text("scenario"), Text("map"), root.TryGetProperty("fail", out var f) && f.ValueKind == JsonValueKind.True);
            case "import":
                return tools!.Import(Text("path"));
            default:
                return LobbyResult.Fail("invalid", "Unknown developer action.");
        }
    }
}
