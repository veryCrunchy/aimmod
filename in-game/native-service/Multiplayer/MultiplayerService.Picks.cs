using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Favourite and recent scenarios for the pickers, saved on this PC
// (multiplayer-picks.json next to the other multiplayer files).
sealed partial class MultiplayerService
{
    const int MaxFavourites = 100, MaxRecent = 12;
    List<string>? favourites, recentPicks;

    string? PicksPath => outputFolder is null ? null : Path.Combine(outputFolder, "multiplayer-picks.json");

    void LoadPicks()
    {
        if (favourites is not null) return;
        favourites = []; recentPicks = [];
        try
        {
            if (PicksPath is not { } path || !File.Exists(path) || new FileInfo(path).Length > 65536) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            List<string> Names(string key, int max) => doc.RootElement.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 and <= 128 }).Select(x => x.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList() : [];
            favourites = Names("favourites", MaxFavourites); recentPicks = Names("recent", MaxRecent);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void SavePicks()
    {
        if (PicksPath is not { } path) return;
        try { AtomicFile.WriteText(path, JsonSerializer.Serialize(new { favourites, recent = recentPicks }, Protocol.Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // A scenario picked or played moves to the front of the recent list.
    void RecordRecent(string? scenario)
    {
        if (scenario is not { Length: > 0 and <= 128 } || scenario.StartsWith(MatchScenario.Prefix, StringComparison.OrdinalIgnoreCase)) return;
        LoadPicks();
        if (recentPicks!.Count > 0 && recentPicks[0].Equals(scenario, StringComparison.OrdinalIgnoreCase)) return;
        recentPicks.RemoveAll(s => s.Equals(scenario, StringComparison.OrdinalIgnoreCase));
        recentPicks.Insert(0, scenario);
        if (recentPicks.Count > MaxRecent) recentPicks.RemoveRange(MaxRecent, recentPicks.Count - MaxRecent);
        SavePicks();
    }

    LobbyResult Favourite(string? scenario, bool on)
    {
        if (scenario is not { Length: > 0 and <= 128 } || library.Scenario(scenario) is not { } known) return LobbyResult.Fail("invalid", "That scenario isn’t in your library.");
        LoadPicks();
        favourites!.RemoveAll(s => s.Equals(known.Name, StringComparison.OrdinalIgnoreCase));
        if (on)
        {
            if (favourites.Count >= MaxFavourites) return LobbyResult.Fail("full", "You can keep " + MaxFavourites + " favourites. Remove one first.");
            favourites.Insert(0, known.Name);
        }
        SavePicks();
        return LobbyResult.Success;
    }

    // Only names still in the library, so the pickers never offer missing content.
    object PicksView()
    {
        LoadPicks();
        var have = library.Available ? library.Scenarios.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        return new { favourites = favourites!.Where(have.Contains), recent = recentPicks!.Where(have.Contains) };
    }
}
