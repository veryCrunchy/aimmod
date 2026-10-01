using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Multiplayer;

// Wires multiplayer into the workspace host with the real local sources:
// AimModCore's live feed and run journal, the KovaaK's library and game commands.
static class MultiplayerHosting
{
    public static MultiplayerService Create(Hub hub, string output, string[] args, Func<LiveOverlaySnapshot> live, Func<IReadOnlyList<Run>> runs)
    {
        string? gameRoot = null;
        for (var i = 0; i + 1 < args.Length; i++) if (args[i] == "--game") gameRoot = args[i + 1];
        var library = new ContentLibrary(ContentLibrary.Locate(gameRoot));
        // Until the AimModSteam bridge transport lands, lobbies stay on this machine.
        IMultiplayerTransport transport = new OfflineTransport();
        return new MultiplayerService(transport, library, new CoreGameControl(output), () => FromLive(live()), runs, () => AccountLabel(hub), output,
            MultiplayerService.SimulationRequested(args, output));
    }

    internal static LocalRun FromLive(LiveOverlaySnapshot s)
    {
        static int Count(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? (int)Math.Min(v, int.MaxValue) : 0;
        return new LocalRun(s.Available && s.Active && !s.Paused && !s.Replay, s.Scenario, s.Score, s.Seconds, s.RemainingSeconds, Count(s.Shots), Count(s.Hits), Count(s.Kills), s.AttemptId);
    }

    static string? AccountLabel(Hub hub)
    {
        try
        {
            var info = JsonSerializer.SerializeToElement(hub.AccountInfo);
            return info.TryGetProperty("linked", out var linked) && linked.ValueKind == JsonValueKind.True && info.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    public static void MapAssets(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/multiplayer.js", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.MultiplayerScript")!, "application/javascript"));
        routes.MapGet(prefix + "/multiplayer.css", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.MultiplayerStyle")!, "text/css"));
    }
}
