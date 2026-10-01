using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Multiplayer;

// Wires multiplayer into the workspace host with the real local sources:
// AimModCore's live feed and run journal, the KovaaK's library and game commands.
static class MultiplayerHosting
{
    // args is null for hosts built by self-tests: those never touch Steam or the game folder.
    public static MultiplayerService Create(Hub hub, string output, string[]? args, Func<LiveOverlaySnapshot> live, Func<IReadOnlyList<Run>> runs)
    {
        string? gameRoot = null;
        var list = args ?? [];
        for (var i = 0; i + 1 < list.Length; i++) if (list[i] == "--game") gameRoot = list[i + 1];
        var library = new ContentLibrary(args is null ? null : ContentLibrary.Locate(gameRoot));
        // The bodies other players appear as, for any scenario (AimMod's own profiles only).
        if (library.Root is { } root) AvatarFiles.Install(ContentRules.Folder(root, "character"));
        // The AimModSteam bridge is used as soon as it answers on its pipe; until then
        // (or without the bridge) lobbies stay on this machine.
        IMultiplayerTransport transport = args is null || list.Contains("--no-steam") ? new OfflineTransport() : new SteamTransport();
        var service = new MultiplayerService(transport, library, args is null ? new NoGameControl() : new CoreGameControl(output), () => FromLive(live()), runs, () => AccountLabel(hub), args is null ? null : output,
            MultiplayerService.SimulationRequested(list, output));
        if (args is not null && !list.Contains("--no-hotkey"))
        {
            var hotkey = new MultiplayerHotkey(output, () => service.HotkeyArmed, service.Hotkey);
            service.HotkeyName = hotkey.KeyName; service.Companion = hotkey;
            hotkey.BoardArmed = () => service.BoardArmed; hotkey.BoardHeld = service.ScoreboardHeld;
            hotkey.Start();
        }
        return service;
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
        routes.MapGet(prefix + "/developer.js", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.DeveloperScript")!, "application/javascript"));
        routes.MapGet(prefix + "/developer.css", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.DeveloperStyle")!, "text/css"));
        routes.MapGet(prefix + "/cshud.js", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.CsHudScript")!, "application/javascript"));
        routes.MapGet(prefix + "/cshud.css", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.CsHudStyle")!, "text/css"));
        routes.MapGet(prefix + "/standings.js", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.StandingsScript")!, "application/javascript"));
        routes.MapGet(prefix + "/standings.css", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.StandingsStyle")!, "text/css"));
        // The always-on notice layer AimModNativeUI shows outside the AimMod panel.
        routes.MapGet(prefix + "/notify", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.NotifyPage")!, "text/html"));
        routes.MapGet(prefix + "/notify.js", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.NotifyScript")!, "application/javascript"));
        routes.MapGet(prefix + "/notify.css", () => Results.Stream(typeof(MultiplayerHosting).Assembly.GetManifestResourceStream("AimMod.NotifyStyle")!, "text/css"));
    }
}
