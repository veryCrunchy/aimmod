using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// A Workshop item the Steam bridge listed (ugc.query -> ugc.items). NeedsUpdate is
// Steam's own item state, so AimMod never guesses whether a port is current.
sealed record WorkshopItem(string Item, string Title, long Bytes, long Updated, bool Subscribed, bool Installed, bool NeedsUpdate);

// One map port: installed in the library, listed on the Workshop, or both.
// Shift is walk, sprint or none (the held Ability 1 profile the port embeds).
sealed record MapPort(string Key, string Scenario, string Display, string Game, string Variant, string? MapFile, long Bytes,
    string Shift, double MapScale, string? WorkshopId, bool Installed, bool NeedsUpdate, string? Preview, string? Thumb = null);

// Map ports from tools/map-port. Their names are fixed by mapport/naming.py
// ("AimMod - <Map> (<Game>) - <Variant>", map file aimmod_<mapid>_<game>.json),
// so the library recognises them by name as well as by the scenario description.
static partial class MapPorts
{
    public const string WorkshopTag = "aimmod-port";
    // Every port title starts with this (naming.py); the Workshop search uses it.
    public const string TitlePrefix = "AimMod - ";
    const long MaxPreviewBytes = 4L << 20;
    [GeneratedRegex(@"^AimMod - (.{1,64}) \((CSGO|CSS|CS2|GMod)\) - (.{1,64})$")] private static partial Regex PortName();

    public static (string Display, string Game, string Variant)? Parse(string scenario) =>
        PortName().Match(scenario) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : null;
    public static string KeyOf(string scenario) => ContentLibrary.TextHash("port:" + scenario.ToLowerInvariant())[..12];

    // Installed ports first (by map name), then Workshop ports this machine doesn't have.
    public static IReadOnlyList<MapPort> List(ContentLibrary library, IReadOnlyList<WorkshopItem> catalog)
    {
        var list = new List<MapPort>();
        var byItem = catalog.GroupBy(c => c.Item).ToDictionary(g => g.Key, g => g.First());
        foreach (var s in library.Scenarios)
        {
            var parsed = Parse(s.Name);
            if (parsed is null && !s.Ported) continue;
            var (display, game, variant) = parsed ?? (s.Map, "", "");
            var scenarioPath = library.PathOf("scenario", s.Name);
            var mapPath = s.MapSource == "game" ? null : library.PathOf("map", s.Map);
            long bytes = Size(scenarioPath) + Size(mapPath);
            var item = s.WorkshopId is { } w && byItem.TryGetValue(w, out var listed) ? listed : null;
            list.Add(new MapPort(KeyOf(s.Name), s.Name, display, game, variant, mapPath is null ? null : Path.GetFileName(mapPath), bytes,
                scenarioPath is null ? "none" : Shift(ContentLibrary.AbilityNames(scenarioPath)), s.MapScale, s.WorkshopId, true, item?.NeedsUpdate == true,
                PreviewFor(scenarioPath, mapPath, s.WorkshopId is not null), ThumbFor(scenarioPath, mapPath)));
        }
        var have = list.Select(p => p.Scenario).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var haveItems = list.Where(p => p.WorkshopId is not null).Select(p => p.WorkshopId!).ToHashSet();
        foreach (var c in catalog)
        {
            if (Parse(c.Title) is not { } p || have.Contains(c.Title) || haveItems.Contains(c.Item)) continue;
            have.Add(c.Title);
            list.Add(new MapPort(KeyOf(c.Title), c.Title, p.Display, p.Game, p.Variant, null, c.Bytes, ShiftOf(p.Variant), 0, c.Item, false, false, null));
        }
        return list.OrderBy(p => p.Installed ? 0 : 1).ThenBy(p => p.Display, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Game).ThenBy(p => p.Variant).ToArray();
    }

    // The port embeds "CS Walk" (Shift walks) or "Sprint" (Shift sprints) as its ability profile.
    static string Shift(IEnumerable<string> abilities)
    {
        foreach (var a in abilities)
        {
            if (a.Contains("walk", StringComparison.OrdinalIgnoreCase)) return "walk";
            if (a.Contains("sprint", StringComparison.OrdinalIgnoreCase)) return "sprint";
        }
        return "none";
    }
    static string ShiftOf(string variant) => variant.Contains("sprint", StringComparison.OrdinalIgnoreCase) ? "sprint" : variant.Contains("CS", StringComparison.Ordinal) ? "walk" : "none";

    static long Size(string? file)
    {
        try { return file is not null && File.Exists(file) ? new FileInfo(file).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // A preview next to the map (<map>.preview.png, as map-port writes it) or the
    // first image in the scenario's Workshop folder. Read-only; small images only.
    static string? PreviewFor(string? scenarioPath, string? mapPath, bool workshop)
    {
        try
        {
            if (mapPath is not null)
            {
                var stem = Path.Combine(Path.GetDirectoryName(mapPath)!, Path.GetFileNameWithoutExtension(mapPath));
                foreach (var candidate in new[] { stem + ".preview.png", stem + ".png", stem + ".jpg" })
                    if (Image(candidate)) return candidate;
            }
            if (workshop && scenarioPath is not null && Path.GetDirectoryName(scenarioPath) is { } folder)
                foreach (var file in Directory.EnumerateFiles(folder).Take(64))
                    if (Image(file)) return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    // The map port's 16:9 Workshop thumbnail (<map>.workshop-thumb-16x9.jpg or .png): next to the
    // installed map, or in the scenario's folder (a Workshop item ships it with the scenario).
    // The map select shows it on the map's card; the top-down preview is the fallback.
    const string ThumbSuffix = ".workshop-thumb-16x9";
    static string? ThumbFor(string? scenarioPath, string? mapPath)
    {
        try
        {
            if (mapPath is not null)
            {
                var stem = Path.Combine(Path.GetDirectoryName(mapPath)!, Path.GetFileNameWithoutExtension(mapPath));
                foreach (var candidate in new[] { stem + ThumbSuffix + ".jpg", stem + ThumbSuffix + ".png" })
                    if (Image(candidate)) return candidate;
            }
            if (scenarioPath is not null && Path.GetDirectoryName(scenarioPath) is { } folder)
                foreach (var file in Directory.EnumerateFiles(folder).Take(64))
                    if (Path.GetFileNameWithoutExtension(file).EndsWith(ThumbSuffix, StringComparison.OrdinalIgnoreCase) && Image(file))
                        return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    static bool Image(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" && File.Exists(file) && new FileInfo(file).Length is > 0 and <= MaxPreviewBytes;
    }
    public static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
}
