using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Where a scenario this machine may lack comes from: Installed (in the library now),
// Downloadable (a Workshop map port provides it), and that download's state and percent.
sealed record ScenarioSource(bool Installed, bool Downloadable, string? Download, int? Percent);

// Map Library: AimMod map ports this machine has, plus the ones the Steam
// Workshop lists, with install and update through the bridge (ugc.download).
// Nothing here writes to the game folder; Steam installs Workshop items itself.
sealed partial class MultiplayerService
{
    readonly Dictionary<string, WorkshopProgress> mapDownloads = new();
    readonly HashSet<string> simulatedInstalls = new();
    long mapsQueriedAt = long.MinValue;

    // Simulation only: Workshop ports to try install and update without Steam.
    static readonly WorkshopItem[] SimulatedCatalog =
    [
        new("sim-port-1", "AimMod - Mirage (CSGO) - CS Movement", 61_400_000, 0, false, false, false),
        new("sim-port-2", "AimMod - Inferno (CSGO) - CS Movement", 74_900_000, 0, false, false, false),
        new("sim-port-3", "AimMod - aim_map (CSS) - CS Movement", 2_300_000, 0, false, false, false),
        new("sim-port-4", "AimMod - Office (CSS) - Sprint", 38_200_000, 0, false, false, false),
    ];

    IReadOnlyList<WorkshopItem> Catalog() => transport.Available ? transport.WorkshopItems : Simulation is not null ? SimulatedCatalog : [];
    IReadOnlyList<MapPort> Ports() => library.Available || Catalog().Count > 0 ? MapPorts.List(library, Catalog()) : [];

    // Search by title (KovaaK's uploads carry no tags); retry sooner while nothing came back.
    void QueryPorts()
    {
        var now = clock();
        if (transport.Available && now - mapsQueriedAt > (transport.WorkshopItems.Count == 0 ? 30_000 : 300_000) && transport.QueryWorkshop(MapPorts.TitlePrefix)) mapsQueriedAt = now;
    }

    public object MapsView()
    {
        lock (gate)
        {
            QueryPorts();
            var lobby = Current;
            return new
            {
                available = library.Available,
                source = transport.Available ? "steam" : Simulation is not null ? "simulation" : "none",
                canInstall = transport.Available || Simulation is not null,
                canLoad = game.Capabilities.Contains("load"),
                lobby = lobby is null ? null : new { isHost = lobby.HostId == SelfId, scenario = lobby.Settings.Scenario?.Name },
                ports = Ports().Select(p => new
                {
                    p.Key, p.Scenario, p.Display, p.Game, p.Variant, p.MapFile, p.Bytes, p.Shift, p.MapScale,
                    workshop = p.WorkshopId is not null, installed = p.Installed || (p.WorkshopId is { } s && simulatedInstalls.Contains(s)),
                    simulated = p.WorkshopId is { } sim && simulatedInstalls.Contains(sim),
                    p.NeedsUpdate, preview = p.Preview is not null,
                    download = p.WorkshopId is { } w && mapDownloads.TryGetValue(w, out var d) ? new { d.State, d.Done, d.Total } : null,
                }),
            };
        }
    }

    // The preview image of a port, by its key. Only files the library found are served.
    public string? MapPreview(string? key)
    {
        if (key is not { Length: 12 }) return null;
        lock (gate) return Ports().FirstOrDefault(p => p.Key == key)?.Preview;
    }

    LobbyResult MapAction(string action, string? key)
    {
        var port = key is null ? null : Ports().FirstOrDefault(p => p.Key == key);
        if (port is null) return LobbyResult.Fail("invalid", "That map isn’t in the library any more.");
        switch (action)
        {
            case "map-install":
                if (port.WorkshopId is not { } item) return LobbyResult.Fail("invalid", "This map isn’t on the Steam Workshop, so it can’t be installed from here.");
                if (port.Installed && !port.NeedsUpdate && !simulatedInstalls.Contains(item)) return LobbyResult.Fail("installed", "You already have the latest version.");
                if (mapDownloads.TryGetValue(item, out var busy) && busy.State is "queued" or "downloading") return LobbyResult.Success;
                if (transport.Available)
                {
                    if (!transport.WorkshopDownload(item)) return LobbyResult.Fail("workshop", "The Steam bridge can’t download Workshop items yet.");
                }
                else if (Simulation is null) return LobbyResult.Fail("workshop", "Installing maps needs Steam.");
                simulatedInstalls.Remove(item);
                mapDownloads[item] = new WorkshopProgress(item, "queued", 0, port.Bytes);
                return LobbyResult.Success;
            case "map-load":
                if (port.WorkshopId is { } simulated && simulatedInstalls.Contains(simulated)) return LobbyResult.Fail("simulated", "That install was simulated, so there’s nothing to load.");
                if (!port.Installed) return LobbyResult.Fail("missing", "Install the map first.");
                return game.Load(port.Scenario) is null ? LobbyResult.Fail("game", "AimMod can’t load scenarios in this game build. Open “" + port.Scenario + "” from KovaaK’s scenario list.") : LobbyResult.Success;
            default:
                return LobbyResult.Fail("invalid", "Unknown map action.");
        }
    }

    // A scenario someone else played (a replay's): whether this machine has it, and the
    // Workshop map port that provides it, with its download, when the Workshop lists one.
    public ScenarioSource SourceOf(string scenario)
    {
        lock (gate)
        {
            QueryPorts();
            var installed = library.PathOf("scenario", scenario) is not null;
            var port = Ports().FirstOrDefault(p => p.WorkshopId is not null && string.Equals(p.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
            var download = port?.WorkshopId is { } item && mapDownloads.TryGetValue(item, out var d) ? d : null;
            return new(installed, port is not null && (!port.Installed || port.NeedsUpdate), download?.State, download is { Total: > 0 } p ? (int)Math.Min(100, p.Done * 100 / p.Total) : null);
        }
    }

    // Download that scenario's map port from the Workshop: the Map Library's own install.
    public LobbyResult DownloadScenario(string scenario)
    {
        lock (gate)
        {
            var port = Ports().FirstOrDefault(p => p.WorkshopId is not null && string.Equals(p.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
            return port is null ? LobbyResult.Fail("invalid", "“" + scenario + "” isn’t on the Steam Workshop as an AimMod map.") : MapAction("map-install", port.Key);
        }
    }

    // Workshop progress for a map install. True when the event belonged to one.
    bool MapWorkshop(WorkshopProgress? progress)
    {
        if (progress is null || !mapDownloads.ContainsKey(progress.Item)) return false;
        var name = Ports().FirstOrDefault(p => p.WorkshopId == progress.Item)?.Display ?? "the map";
        if (progress.State == "installed")
        {
            mapDownloads.Remove(progress.Item);
            library.Refresh(force: true); game.Refresh();
            mapsQueriedAt = long.MinValue;
            notice = ("info", "Installed " + name + ".", clock());
        }
        else if (progress.State is "failed" or "unavailable")
        {
            mapDownloads.Remove(progress.Item);
            notice = ("error", "Couldn’t install " + name + " from the Steam Workshop.", clock());
        }
        else mapDownloads[progress.Item] = progress;
        // Lobby content that waits on this item keeps its own Workshop state too.
        return progress.Item != workshopItem && progress.Item != watchWorkshop;
    }

    // Simulated installs move at a visible pace and never touch the disk.
    void MapTick()
    {
        if (transport.Available || Simulation is null) return;
        foreach (var (item, p) in mapDownloads.ToArray())
        {
            var total = Math.Max(p.Total, 1);
            var done = Math.Min(total, p.Done + Math.Max(total / 40, 1));
            if (done >= total) { simulatedInstalls.Add(item); MapWorkshop(new WorkshopProgress(item, "installed", total, total)); }
            else mapDownloads[item] = new WorkshopProgress(item, "downloading", done, total);
        }
    }

    static string? KeyArg(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("key", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
